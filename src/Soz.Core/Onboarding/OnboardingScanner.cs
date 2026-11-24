using System.Globalization;
using Soz.Core.Data;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soz.Core.Onboarding;

/// <summary>Bir keşif koşusunun bağlamı: kök yollar, sistem okuyucusu, dosya kapısı ve saat.</summary>
public sealed class ScanContext
{
    public required OnboardingRoots Roots { get; init; }
    public required ISystemProbe Probe { get; init; }
    public SafeFs Fs { get; } = new();
    public Func<DateTime> UtcNow { get; init; } = () => Time.UtcNow();
    /// <summary>API taramasında üst bilgiden türetilen gerçek projeler. Eski bağımsız keşif çağrılarıyla uyumludur.</summary>
    public IReadOnlyList<Soz.Core.Profile.ProjectProfile>? ProfileProjects { get; set; }

    /// <summary>Bu bilgisayarın GERÇEK bağlamı. Yalnız gerçek ilk koşuda (yönetici) kullanılır; test ve deneme sentetik bağlam verir.</summary>
    public static ScanContext ForThisMachine() => new()
    {
        Roots = OnboardingRoots.FromEnvironment(),
        Probe = OperatingSystem.IsWindows() ? new WindowsSystemProbe() : new NullSystemProbe(),
    };

    public static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Salt okunur keşif (tek giriş: <see cref="Run"/>). Bulguları üretir, hiçbir yere yazmaz, ağa çıkmaz, program çalıştırmaz.
/// Dosya sistemine yalnız <see cref="SafeFs"/> üzerinden, Windows'a yalnız <see cref="ISystemProbe"/> üzerinden dokunur.
/// </summary>
public static class OnboardingScanner
{
    public static ScanResult Run(ScanContext ctx)
    {
        var res = new ScanResult();
        ToolScanner.Scan(ctx, res);
        SystemScanner.Scan(ctx, res);
        if (ctx.ProfileProjects is not null)
            foreach (var project in ctx.ProfileProjects)
            {
                var finding = new Finding { Id = "project:profile:" + project.Path.ToLowerInvariant(), Group = FindingGroups.Projects,
                    Title = project.Name, Detail = project.MissingFolder ? "Klasörü yok" : project.DisplayHint,
                    Count = project.SessionsByTool.Values.Sum(), Confidence = Confidence.Seen,
                    Facts = Soz.Core.Profile.ProfileService.ToJsonProject(project) };
                foreach (var source in project.Sources)
                    finding.Evidence.Add(new Evidence(source.Tool + " oturum üst bilgisi", ctx.Roots.Abbrev(source.Path ?? ""), ScanContext.Day(source.ObservedAt)));
                res.Findings.Add(finding);
            }
        res.Unreadable = ctx.Fs.Unreadable;
        return res;
    }
}

/// <summary>Bir ajan aracı: komut adı, (varsa) npm paketi ve kayıt klasörü. Yeni araç = tabloya bir satır.</summary>
internal sealed record ToolSpec(string Id, string Title, string[] Commands, string? NpmPackage, RecordSpec? Record);

/// <summary>Aracın kayıt yeri. Tür: "projects" (alt klasör = proje), "dates" (yıl\ay\gün ağacı), "file" (tek veritabanı dosyası), "dir" (klasör, alt klasör sayısı).</summary>
internal sealed record RecordSpec(string Kind, Func<OnboardingRoots, string> Path, string Label);

internal static class ToolScanner
{
    private const int RecentDays = 30;
    private const int MaxDayFolders = 3000;
    private static readonly string[] Extensions = { ".exe", ".cmd", ".bat", ".com" };

    internal static readonly IReadOnlyList<ToolSpec> Tools = new ToolSpec[]
    {
        new("claude", "Claude Code", new[] { "claude" }, "@anthropic-ai/claude-code", new("projects", r => Path.Combine(r.ClaudeHome, "projects"), "projects")),
        new("codex", "Codex", new[] { "codex" }, "@openai/codex", new("dates", r => Path.Combine(r.CodexHome, "sessions"), "sessions")),
        new("hermes", "Hermes", new[] { "hermes" }, null, new("file", r => Path.Combine(r.HermesHome, "state.db"), "state.db")),
        new("opencode", "OpenCode", new[] { "opencode" }, "opencode-ai", new("file", r => Path.Combine(r.OpenCodeData, "opencode.db"), "opencode.db")),
        new("antigravity", "Antigravity", new[] { "agy" }, null, new("file", r => Path.Combine(r.AntigravityHome, "conversation_summaries.db"), "conversation_summaries.db")),
        new("gemini", "Gemini CLI", new[] { "gemini" }, "@google/gemini-cli", new("dir", r => Path.Combine(r.Home, ".gemini", "tmp"), "tmp")),
        new("aider", "Aider", new[] { "aider" }, null, null),
        new("goose", "Goose", new[] { "goose" }, null, null),
        new("qwen", "Qwen Code", new[] { "qwen" }, "@qwen-code/qwen-code", null),
        new("copilot", "GitHub Copilot CLI", new[] { "copilot" }, "@github/copilot", null),
        new("cursor-agent", "Cursor CLI", new[] { "cursor-agent" }, null, null),
    };

    private sealed class Install
    {
        public required string Dir { get; init; }
        public required string Base { get; init; }
        public List<string> Files { get; } = new();
        public bool InPath { get; init; }
        public string Kind = "other";
        public string? Version;
        public string? VersionSource;
    }

    public static void Scan(ScanContext ctx, ScanResult res)
    {
        var fs = ctx.Fs;
        var roots = ctx.Roots;

        // 1) Aranacak klasörler: PATH girdileri + bilinen yerler. Her klasör bir kez listelenir; ağ yolu hiç açılmaz.
        var pathSet = new HashSet<string>(roots.PathEntries.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Norm), StringComparer.OrdinalIgnoreCase);
        var dirs = roots.PathEntries
            .Concat(new[]
            {
                Path.Combine(roots.AppData, "npm"), Path.Combine(roots.Home, ".local", "bin"), Path.Combine(roots.Home, ".bun", "bin"),
                Path.Combine(roots.Home, ".cargo", "bin"), Path.Combine(roots.LocalAppData, "Microsoft", "WinGet", "Links"),
            })
            .Concat(Soz.Core.Assistants.AssistantCatalog.ExtraToolDirectories(roots))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(Norm).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var listing = new Dictionary<string, IReadOnlyList<FileStat>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs) listing[d] = fs.Files(d);

        // 2) Araç araç kurulumlar. Aynı yerdeki .cmd + .exe tek kurulumdur; bağlantı (junction) gerçek yola çözülür.
        foreach (var tool in Tools)
        {
            var installs = new Dictionary<string, Install>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, files) in listing)
                foreach (var f in files)
                {
                    var ext = Path.GetExtension(f.Name);
                    if (!Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                    var b = Path.GetFileNameWithoutExtension(f.Name);
                    if (!tool.Commands.Contains(b, StringComparer.OrdinalIgnoreCase)) continue;
                    var key = fs.Resolve(dir).ToLowerInvariant() + "|" + b.ToLowerInvariant();
                    if (!installs.TryGetValue(key, out var inst)) installs[key] = inst = new Install { Dir = dir, Base = b, InPath = pathSet.Contains(dir) };
                    inst.Files.Add(f.Path);
                }
            foreach (var inst in installs.Values) ReadVersion(fs, tool, inst);

            var recordPath = tool.Record?.Path(roots);
            var hasRecord = recordPath is not null && (tool.Record!.Kind == "file" ? fs.FileExists(recordPath) : fs.DirExists(recordPath));
            if (installs.Count > 0) res.Findings.Add(ToolFinding(roots, tool, installs.Values.OrderByDescending(i => i.InPath).ThenBy(i => i.Dir, StringComparer.OrdinalIgnoreCase).ToList(), Confidence.Installed, fs));
            else if (hasRecord) res.Findings.Add(ToolFinding(roots, tool, new List<Install>(), Confidence.Trace, fs));
            if (hasRecord) AddRecords(ctx, res, tool, recordPath!);
        }
    }

    private static string Norm(string p) => p.Trim().Trim('"').TrimEnd('\\', '/');

    private static void ReadVersion(SafeFs fs, ToolSpec tool, Install inst)
    {
        var hasShim = inst.Files.Any(f => Path.GetExtension(f).Equals(".cmd", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f).Equals(".bat", StringComparison.OrdinalIgnoreCase));
        var exe = inst.Files.FirstOrDefault(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase));
        if (tool.NpmPackage is not null && hasShim)
        {
            var pkg = Path.Combine(new[] { inst.Dir, "node_modules" }.Concat(tool.NpmPackage.Split('/')).Append("package.json").ToArray());
            if (fs.FileExists(pkg))
            {
                inst.Kind = "npm";
                inst.Version = fs.PackageVersion(pkg);
                if (inst.Version is not null) inst.VersionSource = "paketin sürüm bilgisi";
            }
        }
        if (inst.Kind == "other") inst.Kind = exe is not null ? "exe" : hasShim ? "kısayol" : "other";
        if (inst.Version is null && exe is not null)
        {
            inst.Version = fs.ExeVersion(exe);
            if (inst.Version is not null) inst.VersionSource = "programın dosya özellikleri";
        }
    }

    private static Finding ToolFinding(OnboardingRoots roots, ToolSpec tool, List<Install> installs, string confidence, SafeFs fs)
    {
        var facts = new JsonObject { ["tool"] = tool.Id };
        var arr = new JsonArray();
        foreach (var i in installs)
            arr.Add(new JsonObject { ["path"] = roots.Abbrev(Path.Combine(i.Dir, i.Base)), ["kind"] = i.Kind, ["inPath"] = i.InPath, ["version"] = i.Version });
        facts["installs"] = arr;
        var version = installs.Select(i => i.Version).FirstOrDefault(v => v is not null);
        facts["version"] = version;

        string title, detail;
        if (confidence == Confidence.Installed)
        {
            title = $"{tool.Title} kurulu";
            detail = version is not null ? $"Sürüm {version}" : "Sürümünü okuyamadım";
            if (installs.Count > 1) detail += $", {installs.Count} ayrı kurulum bulundu";
        }
        else
        {
            title = $"{tool.Title} için iz var";
            detail = "Kayıt klasörü duruyor ama çalıştırma dosyasını görmedim: kurulu olduğunu doğrulayamadım";
        }
        var f = new Finding { Id = "tool:" + tool.Id, Group = FindingGroups.Tools, Title = title, Detail = detail, Count = installs.Count, Confidence = confidence, Facts = facts };
        foreach (var i in installs)
        {
            foreach (var file in i.Files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                f.Evidence.Add(new Evidence("Çalıştırma dosyası var", roots.Abbrev(file), fs.LastWrite(file) is { } w ? ScanContext.Day(w) : null));
            if (i.Version is not null) f.Evidence.Add(new Evidence($"Sürüm {i.Version}: {i.VersionSource} okundu (program çalıştırılmadı)", roots.Abbrev(i.Dir)));
            if (i.InPath) f.Evidence.Add(new Evidence("Bu klasör komut yolunda (PATH) kayıtlı", roots.Abbrev(i.Dir)));
        }
        if (confidence == Confidence.Trace && tool.Record is { } r) f.Evidence.Add(new Evidence("Kayıt klasörü ya da dosyası var, çalıştırma dosyası yok", roots.Abbrev(r.Path(roots))));
        return f;
    }

    // ---- Kayıt klasörleri ve proje etkinliği: yalnız ad, sayı, tarih ----

    private static void AddRecords(ScanContext ctx, ScanResult res, ToolSpec tool, string path)
    {
        var fs = ctx.Fs;
        var roots = ctx.Roots;
        var rec = tool.Record!;
        var last = fs.LastWrite(path);
        // "root": aracın kayıt kökü (dosya ise bulunduğu klasör), ev klasörü "~" ile kısaltılmış. Kişi bu satırı kabul ederse YALNIZ bu yoldan okunur (bkz. OnboardingService.AcceptedObserveRoots).
        var facts = new JsonObject { ["tool"] = tool.Id, ["kind"] = rec.Kind, ["root"] = roots.Abbrev(rec.Kind == "file" ? (Path.GetDirectoryName(path) ?? path) : path) };
        string detail;
        int? count = null;
        switch (rec.Kind)
        {
            case "projects":
            {
                var projects = ProjectActivity(fs, path);
                count = fs.SubDirs(path).Count;
                facts["projectFolders"] = count;
                detail = $"{count} proje klasörü" + (last is { } l ? $", en son değişiklik {ScanContext.Day(l)}" : "");
                if (ctx.ProfileProjects is null)
                    foreach (var p in projects) res.Findings.Add(ProjectFinding(roots, tool, p, path));
                break;
            }
            case "dates":
            {
                var (total, recent, lastDay) = DateTree(ctx, path);
                count = total;
                facts["sessionFiles"] = total;
                facts["recentSessionFiles"] = recent;
                facts["lastDay"] = lastDay;
                detail = $"Son {RecentDays} günde {recent} oturum kaydı, toplam {total}" + (lastDay is not null ? $", en son gün {lastDay}" : "");
                break;
            }
            case "file":
                facts["exists"] = true;
                detail = "Kayıt dosyası var" + (last is { } lf ? $", son değişiklik {ScanContext.Day(lf)}" : "");
                if (tool.Id == "hermes" && fs.FileExists(Path.Combine(roots.HermesHome, "runtime", "active_sessions.json"))) { facts["liveFile"] = true; detail += ". Çalışan oturum listesi dosyası da var"; }
                break;
            default:
                count = fs.SubDirs(path).Count;
                facts["folders"] = count;
                detail = $"{count} klasör" + (last is { } ld ? $", en son değişiklik {ScanContext.Day(ld)}" : "");
                break;
        }
        var f = new Finding { Id = "records:" + tool.Id, Group = FindingGroups.Records, Title = $"{tool.Title} kayıt klasörü var", Detail = detail, Count = count, Confidence = Confidence.Seen, Facts = facts };
        f.Evidence.Add(new Evidence(rec.Kind == "file" ? "Kayıt dosyası var" : "Kayıt klasörü var", roots.Abbrev(path), last is { } w ? ScanContext.Day(w) : null));
        f.Evidence.Add(new Evidence("Yalnız adlara, sayılara ve tarihlere baktım. Kayıtların içine hiç bakmadım", "—"));
        res.Findings.Add(f);
    }

    private sealed record ProjectStat(string Folder, int Sessions, DateTime Last);

    private static List<ProjectStat> ProjectActivity(SafeFs fs, string projectsDir)
    {
        var list = new List<ProjectStat>();
        foreach (var name in fs.SubDirs(projectsDir))
        {
            var files = fs.Files(Path.Combine(projectsDir, name), "*.jsonl");
            if (files.Count == 0) continue;
            list.Add(new ProjectStat(name, files.Count, files.Max(f => f.LastWriteUtc)));
        }
        return list.OrderByDescending(p => p.Last).ThenBy(p => p.Folder, StringComparer.Ordinal).ToList();
    }

    private static Finding ProjectFinding(OnboardingRoots roots, ToolSpec tool, ProjectStat p, string recordFolder)
    {
        var display = DisplayFolder(roots.Home, p.Folder);
        var f = new Finding
        {
            Id = $"project:{tool.Id}:{display}", // kimlikte de ev klasörü "~": kullanıcı adı dışarı çıkmaz
            Group = FindingGroups.Projects,
            Title = display,
            Detail = $"{tool.Title}: {p.Sessions} oturum kaydı, en son {ScanContext.Day(p.Last)}",
            Count = p.Sessions,
            Confidence = Confidence.Seen,
            Facts = new JsonObject { ["tool"] = tool.Id, ["folderName"] = display, ["sessions"] = p.Sessions, ["lastDay"] = ScanContext.Day(p.Last) },
        };
        f.Evidence.Add(new Evidence($"{tool.Title} kayıt klasöründe bu adla bir alt klasör var", roots.Abbrev(recordFolder) + "\\" + display, ScanContext.Day(p.Last)));
        f.Evidence.Add(new Evidence($"{p.Sessions} oturum dosyası saydım (içlerine bakmadım)", "—"));
        f.Evidence.Add(new Evidence("Klasör adı kayıt klasöründeki hâliyle gösteriliyor: yolun harf ve rakam dışı her karakteri \"-\" olmuş, bu yüzden gerçek yol tahmin edilmedi", "—"));
        return f;
    }

    /// <summary>Claude klasör adlarında ev klasörü "~" olur (kullanıcı adı dışarı çıkmaz). Kodlama: harf ve rakam dışı her karakter "-".</summary>
    internal static string DisplayFolder(string home, string folder)
    {
        var enc = Regex.Replace(home.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");
        return enc.Length > 0 && folder.StartsWith(enc + "-", StringComparison.OrdinalIgnoreCase) ? "~" + folder[enc.Length..] : folder;
    }

    private static (int Total, int Recent, string? LastDay) DateTree(ScanContext ctx, string sessions)
    {
        var fs = ctx.Fs;
        var cutoff = ctx.UtcNow().Date.AddDays(-RecentDays);
        int total = 0, recent = 0, visited = 0;
        DateTime? last = null;
        foreach (var y in fs.SubDirs(sessions).Where(n => Regex.IsMatch(n, @"^\d{4}$")))
            foreach (var m in fs.SubDirs(Path.Combine(sessions, y)).Where(n => Regex.IsMatch(n, @"^\d{2}$")))
                foreach (var d in fs.SubDirs(Path.Combine(sessions, y, m)).Where(n => Regex.IsMatch(n, @"^\d{2}$")))
                {
                    if (++visited > MaxDayFolders) goto done;
                    if (!DateTime.TryParseExact($"{y}-{m}-{d}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)) continue;
                    var n = fs.Files(Path.Combine(sessions, y, m, d), "rollout-*").Count;
                    if (n == 0) continue;
                    total += n;
                    if (day >= cutoff) recent += n;
                    if (last is null || day > last) last = day;
                }
        done:
        return (total, recent, last is { } l ? ScanContext.Day(l) : null);
    }
}
