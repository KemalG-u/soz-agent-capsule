using System.Text.RegularExpressions;
using Soz.Core.Run;
using Soz.Core.Sessions;

namespace Soz.Core.Agents;

/// <summary>
/// Ajan komutunu çözer (araçtan bağımsız). Sıra: ayar (yol) → PATH üzerinde ad. .exe doğrudan çalışır.
/// npm/pip .cmd/.bat kısayolu cmd.exe İLE ÇALIŞTIRILMAZ (kabuk enjeksiyonu yolu, model adı enjeksiyonu kararı korunur):
/// kısayolun içinden gerçek .exe ya da node + betik çözülür. Kısayolun kendi ayarladığı ortam değişkenleri (set "A=B")
/// KAYBOLMAZ: değeri düz metin olan satırlar okunur ve LaunchCommand.Env olarak taşınır (ör. Hermes: HERMES_HOME).
/// Çözülemezse sade hata verilir.
/// </summary>
public static class AgentLocator
{
    public static AgentLocation Resolve(string? configured, string defaultName, string notFoundText, string shimText, string? home = null, string? localAppData = null, bool? includeMachinePaths = null)
    {
        var hasExplicit = !string.IsNullOrWhiteSpace(configured);
        var cmd = hasExplicit ? configured!.Trim() : defaultName;
        var resolved = PlainExplainer.Resolve(cmd);
        if (!File.Exists(resolved) && !hasExplicit && OperatingSystem.IsWindows())
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            // Birim testler PATH="" vererek eksik aracı simüle ettiğinde gerçek makine yollarına düşülmez (açık ev/localAppData verilmediyse).
            var allowFallback = home is not null || localAppData is not null || !string.IsNullOrEmpty(pathEnv);
            if (allowFallback)
            {
                var allowMachine = includeMachinePaths ?? (home is null && localAppData is null);
                foreach (var candidate in Soz.Core.Assistants.AssistantCatalog.WindowsCandidatePaths(defaultName, home, localAppData, allowMachine))
                {
                    if (File.Exists(candidate)) { resolved = candidate; break; }
                }
            }
        }
        if (!File.Exists(resolved)) return new AgentLocation(null, notFoundText);
        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        switch (ext)
        {
            case ".exe":
            case ".com":
                return new AgentLocation(new LaunchCommand(resolved, Array.Empty<string>(), resolved), null);
            case ".mjs":
            case ".js":
            case ".cjs":
            {
                var node = PlainExplainer.Resolve("node");
                return new AgentLocation(new LaunchCommand(node, new[] { resolved }, $"node {resolved}"), null);
            }
            case ".cmd":
            case ".bat":
            {
                var target = ShimTarget(resolved);
                if (target is null) return new AgentLocation(null, shimText);
                var env = ShimEnv(resolved);
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return new AgentLocation(new LaunchCommand(target, Array.Empty<string>(), target, env), null);
                var node = ShimNode(resolved) ?? PlainExplainer.Resolve("node");
                if (!File.Exists(node) && node.Equals("node", StringComparison.OrdinalIgnoreCase)) return new AgentLocation(null, shimText);
                return new AgentLocation(new LaunchCommand(node, new[] { target }, $"node {target}", env), null);
            }
            case ".ps1":
                return new AgentLocation(new LaunchCommand("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", resolved }, resolved), null);
            default:
                // Windows'ta uzantısı tanınmayan dosya (örn. claude.txt) çalıştırılamaz: "hazır" denmesin, kişi sade bir "bulunamadı" görsün (C9).
                if (OperatingSystem.IsWindows()) return new AgentLocation(null, notFoundText);
                return new AgentLocation(new LaunchCommand(resolved, Array.Empty<string>(), resolved), null);
        }
    }

    private static readonly Regex TargetInShim = new(@"""(?<p>[^""]+?\.(?:exe|js|mjs|cjs))""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Kısayol (claude.cmd, hermes.cmd) içindeki gerçek hedef: "%dp0%\...\x.exe" ya da "%dp0%\node_modules\...\cli.js" ya da mutlak yol.
    /// Bulunamazsa null. Hedef gerçekten var olan bir dosya olmalıdır.
    /// </summary>
    public static string? ShimTarget(string cmdFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cmdFile)) ?? "";
            foreach (var line in File.ReadLines(cmdFile).Take(60))
            {
                if (IsSetLine(line)) continue; // set "X=...exe" bir değişkendir, hedef değil
                foreach (Match m in TargetInShim.Matches(line))
                {
                    var raw = m.Groups["p"].Value;
                    if (raw.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase)) continue; // "%dp0%\node.exe" yorumlayıcıdır, hedef değil
                    var p = raw.Replace("%dp0%", dir + Path.DirectorySeparatorChar).Replace("%~dp0", dir + Path.DirectorySeparatorChar);
                    p = Path.GetFullPath(p);
                    if (File.Exists(p)) return p;
                }
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Kısayolun yanındaki node.exe ("%dp0%\node.exe") varsa onu kullanır.</summary>
    private static string? ShimNode(string cmdFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cmdFile)) ?? "";
            var local = Path.Combine(dir, "node.exe");
            if (File.Exists(local)) return local;
        }
        catch (Exception) { }
        return null;
    }

    // set "AD=değer"  |  set AD=değer   (set /a ve set /p değişken ayarı değildir)
    private static readonly Regex SetLine = new(@"^\s*@?set\s+(?:""(?<q>[A-Za-z_][A-Za-z0-9_]*)=(?<qv>[^""]*)""|(?<k>[A-Za-z_][A-Za-z0-9_]*)=(?<v>.*?))\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Percent = new(@"%(?:~dp0|(?<n>[A-Za-z_][A-Za-z0-9_]*))%?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsSetLine(string line) => SetLine.IsMatch(line);

    /// <summary>Kısayolun yok sayılan değişkenleri: kabuk işleyişine ait (dp0, _prog, PATH, PATHEXT...). Ajanın davranışını belirleyen değişkenler alınır.</summary>
    private static bool IsShimInternal(string name)
        => name.StartsWith('_') || name.Equals("dp0", StringComparison.OrdinalIgnoreCase)
           || name.Equals("PATH", StringComparison.OrdinalIgnoreCase) || name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)
           || name.Equals("COMSPEC", StringComparison.OrdinalIgnoreCase) || name.Equals("ERRORLEVEL", StringComparison.OrdinalIgnoreCase)
           || name.Equals("PROMPT", StringComparison.OrdinalIgnoreCase) || name.Equals("TITLE", StringComparison.OrdinalIgnoreCase)
           || name.Equals("CD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Kısayol dosyasının, hedef programı çağırmadan ÖNCE yaptığı düz değer atamaları (set "AD=değer").
    /// Alınır: değeri düz metin olan, ya da yalnız %dp0% ile var olan ortam değişkenlerine dayanan satırlar.
    /// Alınmaz: kabuk işleyişi değişkenleri, `%x%` çözülemeyen, gecikmeli genişleme (!x!), kaçış (^) ve kabuk karakteri (&amp; | &lt; &gt;) içeren satırlar.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ShimEnv(string cmdFile)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cmdFile)) ?? "";
            foreach (var line in File.ReadLines(cmdFile).Take(60))
            {
                if (line.Contains("%*")) break; // hedefin çağrıldığı satır: sonrası ilgilendirmez
                var m = SetLine.Match(line);
                if (!m.Success) continue;
                var name = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["k"].Value;
                var value = m.Groups["q"].Success ? m.Groups["qv"].Value : m.Groups["v"].Value;
                if (IsShimInternal(name)) continue;
                if (value.IndexOfAny(new[] { '&', '|', '<', '>', '^', '!', '\r', '\n' }) >= 0) continue;
                var ok = true;
                var expanded = Percent.Replace(value, mm =>
                {
                    if (mm.Value.StartsWith("%~dp0", StringComparison.OrdinalIgnoreCase)) return dir + Path.DirectorySeparatorChar;
                    var n = mm.Groups["n"].Value;
                    if (n.Equals("dp0", StringComparison.OrdinalIgnoreCase)) return dir + Path.DirectorySeparatorChar;
                    var fromEnv = n.Length > 0 ? Environment.GetEnvironmentVariable(n) : null;
                    if (fromEnv is null) { ok = false; return ""; }
                    return fromEnv;
                });
                if (!ok) continue;
                result[name] = expanded;
            }
        }
        catch (Exception) { }
        return result;
    }
}
