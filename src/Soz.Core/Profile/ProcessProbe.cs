using System.Diagnostics;
using System.Text.RegularExpressions;
using Soz.Core.Assistants;

namespace Soz.Core.Profile;

public sealed record ProcessObservation(
    string Name,
    int Id,
    DateTime? StartedAt,
    int? ParentId = null,
    string? CurrentDirectory = null,
    string? CommandLine = null,
    long? WindowHandle = null,
    string? ParentName = null,
    bool IsHelperProcess = false,
    string? WindowTitle = null);

public sealed record ToolProcessCount(string Tool, int? Count, string Status, string Explanation);
public sealed record ProcessInventory(IReadOnlyList<ProcessObservation> Processes, IReadOnlyList<ToolProcessCount> Tools,
    DateTime ObservedAt, string Kind = "gozlem");
public interface IProcessProvider { IReadOnlyList<ProcessObservation> Read(); }

/// <summary>
/// Süreç yoklayıcısı: merkezi AssistantCatalog ile süreç adlarını eşler, gerçek Windows ortamında ata süreç,
/// PEB çalışma klasörü ve görünür pencere (HWND) bilgilerini toplar. Electron alt süreçlerini ve ChatGPT yardımcı süreçlerini ayıklar.
/// </summary>
public sealed class ProcessProbe : IProcessProvider
{
    public static readonly IReadOnlyDictionary<string, string> ToolNames = BuildToolNames();
    public static readonly IReadOnlyList<string> AllTools = AssistantCatalog.ProfileAssistants.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static readonly IReadOnlySet<string> SharedRuntimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "node", "nodejs", "python", "python3", "pythonw", "py" };

    private static readonly Regex WorkspaceArgRegex = new(@"--workspace_id(?:=|\s+)(?:""(?<q>[^""]+)""|(?<v>\S+))", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HexEscapeRegex = new(@"_(?<hex>[0-9a-fA-F]{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ClaudeNodeRegex = new(@"(?:@anthropic-ai[\\/]claude-code|[\\/]claude-code[\\/](?:cli|bin))", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CodexNodeRegex = new(@"(?:@openai[\\/]codex|[\\/]codex[\\/]bin[\\/]codex)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenCodeNodeRegex = new(@"(?:opencode-ai|[\\/]opencode[\\/]bin)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AgyNodeRegex = new(@"antigravity-cli", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HermesPyRegex = new(@"(?:hermes[_-]cli|[\\/]hermes[\\/])", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyDictionary<string, string> BuildToolNames()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in AssistantCatalog.ProfileAssistants)
            foreach (var p in a.EffectiveProcessNames)
                d[p] = a.Id;
        return d;
    }

    public IReadOnlyList<ProcessObservation> Read()
    {
        var tree = Win32ProcessInspector.SnapshotProcessTree();
        var allWindows = Win32ProcessInspector.SnapshotAllVisibleWindows();
        var windows = new Dictionary<int, long>();
        foreach (var w in allWindows)
        {
            if (!windows.ContainsKey(w.Pid)) windows[w.Pid] = w.Hwnd;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool IsUnusableCwd(string path)
        {
            var clean = ProfileSources.CleanPath(path);
            if (string.IsNullOrWhiteSpace(clean)) return true;
            if (ProfileSources.IsDriveRoot(clean)) return true;
            if (!string.IsNullOrEmpty(home) && clean.Equals(home.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return true;
            var parts = clean.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(s => s.Equals("System32", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || s.Equals("AppData", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Programs", StringComparison.OrdinalIgnoreCase))) return true;
            if (!string.IsNullOrEmpty(home) && new[] { "Desktop", "Masaüstü", "Documents", "Belgeler" }
                .Any(n => clean.Equals(Path.Combine(home, n), StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }

        var rawList = new List<ProcessObservation>();
        var agWorkspaceServerRoots = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    var isAgLangServer = name.StartsWith("language_server_windows", StringComparison.OrdinalIgnoreCase);
                    var isTool = ToolNames.TryGetValue(name, out var toolId);
                    var isShared = SharedRuntimes.Contains(name);
                    if (!isTool && !isShared && !isAgLangServer) continue;

                    DateTime? start = null;
                    try { start = process.StartTime.ToUniversalTime(); }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }

                    int? parentId = null;
                    string? parentName = null;
                    if (tree.TryGetValue(process.Id, out var node) && node.ParentPid > 0)
                    {
                        parentId = node.ParentPid;
                        if (tree.TryGetValue(node.ParentPid, out var pNode)) parentName = pNode.Name;
                    }

                    string? cwd = null;
                    string? cmdLine = null;
                    long? hwnd = null;
                    string? winTitle = null;
                    var isHelper = false;

                    if (isAgLangServer)
                    {
                        (_, cmdLine) = Win32ProcessInspector.ReadPebStrings(process.Id);
                        if (!Win32ProcessInspector.HasAncestorNamed(process.Id, "Antigravity", tree)
                            && (cmdLine is null || !cmdLine.Contains("antigravity", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var wsDir = ExtractWorkspaceFromCommandLine(cmdLine);
                        if (string.IsNullOrWhiteSpace(wsDir) || IsAssistantInternalPath(wsDir)) continue; // Antigravity'nin kendi iç yardımcıları (ör. .gemini altındaki yan sunucu) kullanıcı çalışma alanı değildir
                        name = "antigravity";
                        toolId = "antigravity";
                        isTool = true;
                        cwd = wsDir;
                        var folderLeaf = Path.GetFileName(wsDir.TrimEnd('\\', '/'));
                        var matchedWin = allWindows.FirstOrDefault(w =>
                            w.Title.EndsWith("Antigravity", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(folderLeaf)
                            && w.Title.Contains(folderLeaf, StringComparison.OrdinalIgnoreCase))
                            ?? allWindows.FirstOrDefault(w => w.Title.EndsWith("Antigravity", StringComparison.OrdinalIgnoreCase));
                        hwnd = matchedWin is not null ? matchedWin.Hwnd : Win32ProcessInspector.ResolveWindowForProcess(process.Id, tree, windows);
                        winTitle = matchedWin?.Title ?? Win32ProcessInspector.GetWindowTitle(hwnd);
                        agWorkspaceServerRoots.Add(-1); // En az bir Antigravity çalışma alanı sunucusu bulundu
                        rawList.Add(new(name, process.Id, start, parentId, cwd, cmdLine, hwnd, parentName, false, winTitle));
                        continue;
                    }

                    if (isShared)
                    {
                        (cwd, cmdLine) = Win32ProcessInspector.ReadPebStrings(process.Id);
                        if (cmdLine is not null)
                        {
                            var reclassified = ClassifySharedRuntime(name, cmdLine);
                            if (reclassified is null) continue; // PEB okundu ve yapay zekâ yardımcısı olmadığı kesinleşti
                            name = reclassified;
                            toolId = ToolNames[name];
                            isTool = true;
                            isShared = false;
                        }
                    }

                    if (isTool)
                    {
                        if (cmdLine is null && cwd is null)
                            (cwd, cmdLine) = Win32ProcessInspector.ReadPebStrings(process.Id);
                        var ownsDirectWindow = windows.TryGetValue(process.Id, out var directHwnd);
                        var isCliTool = toolId is "claude" or "codex" or "opencode";
                        hwnd = ownsDirectWindow ? directHwnd : Win32ProcessInspector.ResolveWindowForProcess(process.Id, tree, windows, allowTerminalFallback: isCliTool);
                        winTitle = Win32ProcessInspector.GetWindowTitle(hwnd);
                        isHelper = !ownsDirectWindow && DetectHelperProcess(toolId!, name, process.Id, parentName, cmdLine, tree);

                        if (!isHelper && toolId == "claude" && (string.IsNullOrWhiteSpace(cwd) || IsUnusableCwd(cwd)))
                        {
                            cwd = Win32ProcessInspector.InheritChildProjectDirectory(process.Id, tree, IsUnusableCwd) ?? cwd;
                        }
                        if (!isHelper && toolId == "antigravity" && (string.IsNullOrWhiteSpace(cwd) || IsUnusableCwd(cwd)))
                        {
                            cwd = ExtractWorkspaceFromCommandLine(cmdLine) ?? cwd;
                        }
                    }

                    rawList.Add(new(name, process.Id, start, parentId, cwd, cmdLine, hwnd, parentName, isHelper, winTitle));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }

        // GUI araçlarında (Antigravity, VS Code, Cursor, Hermes) penceresi olan süreç varsa penceresiz başlatıcı kabuk süreçlerini yardımcı işaretle
        var hasAgWorkspaceServers = agWorkspaceServerRoots.Count > 0;
        var guiToolsWithWindow = rawList
            .Where(p => !p.IsHelperProcess && p.WindowHandle is > 0 && ToolNames.TryGetValue(p.Name, out var tid) && tid is "antigravity" or "vscode" or "cursor" or "hermes")
            .Select(p => ToolNames[p.Name])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = rawList.Select(p =>
        {
            if (p.IsHelperProcess) return p;
            if (hasAgWorkspaceServers && p.Name.Equals("Antigravity", StringComparison.OrdinalIgnoreCase))
                return p with { IsHelperProcess = true };
            if (ToolNames.TryGetValue(p.Name, out var tid) && guiToolsWithWindow.Contains(tid) && (p.WindowHandle is null or <= 0))
                return p with { IsHelperProcess = true };
            return p;
        }).ToList();
        return result;
    }

    /// <summary>Yapay zekâ yardımcısının kendi ev klasörü altındaki yol (ör. ".gemini"): kullanıcının proje klasörü değildir.</summary>
    public static bool IsAssistantInternalPath(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(p => p.Equals(".gemini", StringComparison.OrdinalIgnoreCase) || p.Equals(".agents", StringComparison.OrdinalIgnoreCase));

    public static string? ExtractWorkspaceFromCommandLine(string? cmdLine)
    {
        if (string.IsNullOrWhiteSpace(cmdLine)) return null;
        var m = WorkspaceArgRegex.Match(cmdLine);
        if (m.Success)
        {
            var raw = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["v"].Value;
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && ProfileSources.DecodeFileUri(raw) is { } uriPath)
                return uriPath;
            // file_d_3a_antigravity_proje gibi kodlanmış workspace_id biçimi
            var decoded = DecodeAntigravityWorkspaceId(raw);
            if (decoded is not null) return decoded;
        }
        return null;
    }

    public static string? ClassifySharedRuntime(string runtimeName, string? cmdLine)
    {
        if (string.IsNullOrWhiteSpace(cmdLine)) return null;
        if (runtimeName.Equals("node", StringComparison.OrdinalIgnoreCase) || runtimeName.Equals("nodejs", StringComparison.OrdinalIgnoreCase))
        {
            if (ClaudeNodeRegex.IsMatch(cmdLine)) return "claude";
            if (CodexNodeRegex.IsMatch(cmdLine)) return "codex";
            if (OpenCodeNodeRegex.IsMatch(cmdLine)) return "opencode";
            if (AgyNodeRegex.IsMatch(cmdLine)) return "agy";
            return null;
        }
        if (HermesPyRegex.IsMatch(cmdLine)) return "hermes";
        return null;
    }

    public static string? DecodeAntigravityWorkspaceId(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return null;
        var s = workspaceId.Trim();
        if (s.StartsWith("file_", StringComparison.OrdinalIgnoreCase) && s.Length >= 8)
        {
            var rest = s[5..]; // ör. e_3a_AI_projeler_ajan_2dkutusu_2ddeney veya e_3a
            var unescaped = HexEscapeRegex.Replace(rest, m =>
            {
                var b = Convert.ToByte(m.Groups["hex"].Value, 16);
                // Yalnız ASCII noktalama/boşluk kaçışlarını çöz (_3a=':', _2d='-', _20=' ', _2e='.', vb.)
                return b is >= 0x20 and <= 0x7E && !char.IsAsciiLetterOrDigit((char)b) && (char)b != '_'
                    ? ((char)b).ToString()
                    : m.Value;
            });
            if (unescaped.Length >= 2 && char.IsAsciiLetter(unescaped[0]) && unescaped[1] == ':')
            {
                var drive = char.ToUpperInvariant(unescaped[0]) + @":\";
                if (unescaped.Length == 2 || unescaped[2..].Trim('_').Length == 0)
                    return Directory.Exists(drive) ? drive : null;
                var tail = unescaped.Length > 3 && unescaped[2] == '_' ? unescaped[3..] : unescaped[2..];
                var tokens = tail.Split('_', StringSplitOptions.RemoveEmptyEntries);
                var resolved = ResolveGreedySegments(drive, tokens, 0);
                if (resolved is not null) return resolved;
                var simple = drive + tail.Replace('_', '\\');
                if (Directory.Exists(simple)) return simple;
            }
        }
        var clean = ProfileSources.CleanPath(s);
        return clean is not null && Path.IsPathFullyQualified(clean) && Directory.Exists(clean) ? clean : null;
    }

    private static string? ResolveGreedySegments(string currentDir, string[] tokens, int index)
    {
        if (index >= tokens.Length) return Directory.Exists(currentDir) ? currentDir : null;
        for (var end = tokens.Length - 1; end >= index; end--)
        {
            var seg = string.Join("_", tokens[index..(end + 1)]);
            var next = Path.Combine(currentDir, seg);
            if (!Directory.Exists(next)) continue;
            var hit = ResolveGreedySegments(next, tokens, end + 1);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static bool DetectHelperProcess(string toolId, string procName, int pid, string? parentName, string? cmdLine, IReadOnlyDictionary<int, Win32ProcessInspector.RawProcessNode> tree)
    {
        if (!string.IsNullOrEmpty(cmdLine) && cmdLine.Contains("--type=", StringComparison.OrdinalIgnoreCase))
            return true; // Electron GPU / renderer / utility alt süreci
        if (toolId == "claude" && (string.Equals(parentName, "claude", StringComparison.OrdinalIgnoreCase) || Win32ProcessInspector.HasAncestorNamed(pid, "claude", tree)))
            return true; // Claude Code'un kendi başlattığı alt node/araç süreci
        if (toolId == "hermes" && (string.Equals(parentName, "hermes", StringComparison.OrdinalIgnoreCase) || Win32ProcessInspector.HasAncestorNamed(pid, "hermes", tree)))
            return true; // Hermes penceresinin alt süreci tek pencere sayılır
        if (toolId == "codex" && (string.Equals(parentName, "ChatGPT", StringComparison.OrdinalIgnoreCase) || Win32ProcessInspector.HasAncestorNamed(pid, "ChatGPT", tree) || string.Equals(parentName, "codex", StringComparison.OrdinalIgnoreCase)))
            return true; // ChatGPT masaüstü arka plan yardımcı codex.exe süreci
        if (toolId is "vscode" or "cursor" && string.Equals(parentName, procName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (toolId == "antigravity" && procName.Equals("antigravity", StringComparison.OrdinalIgnoreCase) && string.Equals(parentName, "antigravity", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(cmdLine) || !cmdLine.Contains("--workspace_id", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    public static ProcessInventory Inspect(IProcessProvider provider, DateTime now)
    {
        IReadOnlyList<ProcessObservation> rows;
        try { rows = provider.Read(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return new([], AllTools.Select(t => new ToolProcessCount(t, null, "sayilamadi", "Süreç üst bilgisi okunamadı.")).ToArray(), now);
        }

        rows = rows.Where(p => ToolNames.ContainsKey(p.Name) || SharedRuntimes.Contains(p.Name)).DistinctBy(p => p.Id).ToArray();
        var byPid = rows.ToDictionary(p => p.Id);
        // Enjekte edilen sahte sağlayıcılarda da --type= / Hermes alt süreci / ChatGPT yardımcı codex ayıklaması uygulanır.
        rows = rows.Select(p =>
        {
            if (!ToolNames.TryGetValue(p.Name, out var t)) return p;
            var pName = p.ParentName ?? (p.ParentId is { } ppid && byPid.TryGetValue(ppid, out var par) ? par.Name : null);
            var helper = p.IsHelperProcess
                || (!string.IsNullOrEmpty(p.CommandLine) && p.CommandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase))
                || (t == "hermes" && string.Equals(pName, "hermes", StringComparison.OrdinalIgnoreCase))
                || (t == "codex" && string.Equals(pName, "ChatGPT", StringComparison.OrdinalIgnoreCase));
            return helper == p.IsHelperProcess && pName == p.ParentName ? p : p with { ParentName = pName, IsHelperProcess = helper };
        }).ToArray();

        // Başlatıcı sarmalayıcı: aynı CLI aracının asıl sürecinin doğrudan ebeveyniyse (node sarmalayıcısı, altında asıl claude/codex süreci) ayrı pencere sayılmaz.
        var toolOf = rows.Where(p => ToolNames.ContainsKey(p.Name)).ToDictionary(p => p.Id, p => ToolNames[p.Name]);
        var wrapperIds = rows.Where(c => !c.IsHelperProcess && c.ParentId is { } parentPid && toolOf.TryGetValue(c.Id, out var childTool)
                && toolOf.TryGetValue(parentPid, out var parentTool) && parentTool == childTool && childTool is "claude" or "codex" or "opencode" or "hermes")
            .Select(c => c.ParentId!.Value).ToHashSet();
        if (wrapperIds.Count > 0) rows = rows.Select(p => wrapperIds.Contains(p.Id) && !p.IsHelperProcess ? p with { IsHelperProcess = true } : p).ToArray();

        var shared = rows.Any(p => SharedRuntimes.Contains(p.Name));
        return new(rows, AllTools.Select(t =>
        {
            var count = rows.Count(p => !p.IsHelperProcess && ToolNames.TryGetValue(p.Name, out var tool) && tool == t);
            return new ToolProcessCount(t, count == 0 && shared ? null : count, shared ? (count == 0 ? "sayilamadi" : "kismi") : "sayildi",
                shared ? "Ortak node/python süreçlerinin aracı güvenle ayırt edilemiyor. Yalnız adı belli süreçler sayıldı." : "Yalnız araç adıyla çalışan süreçler sayıldı.");
        }).ToArray(), now);
    }
}
