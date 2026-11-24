using System.Globalization;

namespace Soz.Core.Profile;

public interface IProjectFileSystem
{
    bool DirectoryExists(string path);
    bool HasGit(string path);
    bool HasMarker(string path);
    bool HasTests(string path);
    bool HasStrongProjectMarker(string path) => HasGit(path) || HasMarker(path);
    /// <summary>Klasörün kendi proje sözleşmesi var mı (PROJECT.md, CLAUDE.md ya da AGENTS.md). Üst proje kararında kullanılır.</summary>
    bool HasProjectContract(string path) => false;
    /// <summary>Doğrudan alt klasörler (üst proje ile kap klasör ayrımı için). Okunamazsa boş.</summary>
    IEnumerable<string> ChildDirectories(string path) => [];
}

public sealed class ProjectFileSystem : IProjectFileSystem
{
    private static readonly string[] StrongMarkers = ["package.json", "pyproject.toml", "Cargo.toml", "go.mod", "PROJECT.md"];
    private static readonly string[] Markers = ["package.json", "pyproject.toml", "Cargo.toml", "go.mod", "PROJECT.md", "CLAUDE.md", "AGENTS.md"];

    private static bool IsWorktreeCheckout(string path)
    {
        if (Directory.Exists(Path.Combine(path, ".git"))) return false;
        if (!File.Exists(Path.Combine(path, ".git"))) return false;
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.SkipLast(1).Any(p => p.Equals("worktrees", StringComparison.OrdinalIgnoreCase) || p.Equals(".worktrees", StringComparison.OrdinalIgnoreCase));
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool HasGit(string path) => Directory.Exists(Path.Combine(path, ".git")) || (File.Exists(Path.Combine(path, ".git")) && !IsWorktreeCheckout(path));
    public bool HasMarker(string path) => !IsWorktreeCheckout(path) && (Markers.Any(m => File.Exists(Path.Combine(path, m))) || Directory.EnumerateFiles(path, "*.sln", SearchOption.TopDirectoryOnly).Any());
    private static readonly string[] ContractFiles = ["PROJECT.md", "CLAUDE.md", "AGENTS.md"];
    private const int MaxChildDirectories = 400;

    public bool HasProjectContract(string path) => !IsWorktreeCheckout(path) && ContractFiles.Any(m => File.Exists(Path.Combine(path, m)));

    public IEnumerable<string> ChildDirectories(string path)
    {
        var list = new List<string>();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
            foreach (var d in Directory.EnumerateDirectories(path, "*", options))
            {
                list.Add(d);
                if (list.Count >= MaxChildDirectories) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return list;
    }

    public bool HasStrongProjectMarker(string path) => HasGit(path) || (!IsWorktreeCheckout(path) && (StrongMarkers.Any(m => File.Exists(Path.Combine(path, m))) || Directory.EnumerateFiles(path, "*.sln", SearchOption.TopDirectoryOnly).Any()));
    public bool HasTests(string path) => new[] { "test", "tests", "__tests__", "pytest.ini", "vitest.config.ts", "jest.config.js" }
        .Any(m => Directory.Exists(Path.Combine(path, m)) || File.Exists(Path.Combine(path, m)))
        || Directory.EnumerateFiles(path, "*Tests.csproj", SearchOption.TopDirectoryOnly).Any();
}

public sealed record LiveHintContext(
    IReadOnlySet<string>? HermesActiveIds = null,
    IReadOnlySet<string>? AntigravityPresenceIds = null,
    IReadOnlyList<ClaudeRegistryEntry>? ClaudeRegistry = null,
    string ClaudeSource = LiveRules.SourceProcess,
    string? ClaudeNote = null);

/// <summary>All exclusions live here. A temporary child can still resolve to a legitimate ancestor project.</summary>
public sealed class ProjectExclusions(string home, string dataDir, string? ownTemporaryDir = null, string? tempDir = null,
    IReadOnlyList<ProfileRoot>? recordRoots = null)
{
    public static readonly IReadOnlySet<string> TemporarySegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "tmp", "_tmp", "temp", "scratchpad", "_gecici", "node_modules", "bin", "obj", "deneme-proje",
          "canli-proje", "veri-deneme", "sinama", "sinama-proje" };

    public static bool Under(string path, string prefix)
    {
        var p = ProfileSources.CleanPath(path) ?? path;
        var pre = (ProfileSources.CleanPath(prefix) ?? prefix).TrimEnd('\\', '/');
        return p.Equals(pre, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(pre + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public string? HardReason(string rawPath)
    {
        var path = ProfileSources.CleanPath(rawPath) ?? rawPath;
        if (Under(path, dataDir) || ownTemporaryDir is not null && Under(path, ownTemporaryDir)) return "soz-verisi-ve-denemesi";
        if (recordRoots?.Any(r => Under(path, r.Path)) == true) return "arac-kayit-klasoru";
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => p.Equals(".claude", StringComparison.OrdinalIgnoreCase) || p.Equals(".codex", StringComparison.OrdinalIgnoreCase)
            || p.Equals(".codex-home", StringComparison.OrdinalIgnoreCase) || p.Equals("hermes-home", StringComparison.OrdinalIgnoreCase)
            || p.Equals(".hermes", StringComparison.OrdinalIgnoreCase) || p.Equals(".opencode", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("-profile", StringComparison.OrdinalIgnoreCase) || p.Contains("chrome-profile", StringComparison.OrdinalIgnoreCase))) return "arac-profil-klasoru";
        if (parts.Any(p => p.Equals(".agents", StringComparison.OrdinalIgnoreCase)
            || p.Equals(".gemini", StringComparison.OrdinalIgnoreCase)
            || p.Equals("AppData", StringComparison.OrdinalIgnoreCase))) return "arac-gecici-klasoru";
        if (IsCodexScratchDocs(path, parts)) return "arac-gecici-klasoru";

        var cleanHome = (ProfileSources.CleanPath(home) ?? home).TrimEnd('\\', '/');
        if (path.Equals(cleanHome, StringComparison.OrdinalIgnoreCase)) return "kullanici-evi";
        if (new[] { "Desktop", "Documents", "Masaüstü", "Belgeler" }.Any(n => path.Equals(Path.Combine(cleanHome, n), StringComparison.OrdinalIgnoreCase))
            || new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments }.Select(Environment.GetFolderPath)
                .Any(p => !string.IsNullOrEmpty(p) && path.Equals(p, StringComparison.OrdinalIgnoreCase))) return "ozel-kap-klasoru";
        if (ProfileSources.IsDriveRoot(path) || path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) return "surucu-koku";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && Under(path, windows)) return "sistem-klasoru";
        if (parts.Any(p => p.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || p.Equals("Program Files", StringComparison.OrdinalIgnoreCase) || p.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase))) return "sistem-klasoru";
        return null;
    }

    private bool IsCodexScratchDocs(string path, string[] parts)
    {
        var cleanHome = (ProfileSources.CleanPath(home) ?? home).TrimEnd('\\', '/');
        foreach (var rel in new[]
        {
            @"Documents\Codex",
            @"Belgeler\Codex",
            @"OneDrive\Documents\Codex",
            @"OneDrive\Belgeler\Codex",
        })
        {
            if (!string.IsNullOrEmpty(cleanHome) && Under(path, Path.Combine(cleanHome, rel))) return true;
        }
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if ((parts[i].Equals("Documents", StringComparison.OrdinalIgnoreCase) || parts[i].Equals("Belgeler", StringComparison.OrdinalIgnoreCase))
                && parts[i + 1].Equals("Codex", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public bool Temporary(string rawPath)
    {
        var path = ProfileSources.CleanPath(rawPath) ?? rawPath;
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(TemporarySegments.Contains)
            || !string.IsNullOrEmpty(tempDir) && Under(path, tempDir);
    }
}

public sealed class ProjectResolver(IProjectFileSystem fs, ProjectExclusions exclusions)
{
    public Dictionary<(string Tool, string RecordPath, string Id), (string? Project, string? Reason)> Assignments { get; } = new();
    private readonly Dictionary<string, (string? Root, string? Reason, bool Git, bool Tests, bool Missing)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public (string? Root, string? Reason, bool Git, bool Tests, bool Missing) Resolve(string? rawCwd)
    {
        var cwd = ProfileSources.CleanPath(rawCwd);
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) return (null, "klasor-bilinmiyor", false, false, false);
        var path = ProfileSources.IsDriveRoot(cwd) ? Path.GetFullPath(cwd) : Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        if (_cache.TryGetValue(path, out var cached)) return cached;
        var hard = exclusions.HardReason(path);
        if (hard is not null) return _cache[path] = (null, hard, false, false, false);

        // Git worktrees alt klasörlerini (ör. E:\sample-lab\worktrees\r174-*) doğrudan ana depo köküne (E:\sample-lab) bağla
        var segs = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var wtIdx = Array.FindIndex(segs, s => s.Equals("worktrees", StringComparison.OrdinalIgnoreCase) || s.Equals(".worktrees", StringComparison.OrdinalIgnoreCase));
        var startPath = path;
        if (wtIdx >= 2)
        {
            var repoCandidate = string.Join(Path.DirectorySeparatorChar, segs[..wtIdx]);
            if (ProfileSources.IsDriveRoot(repoCandidate)) repoCandidate += Path.DirectorySeparatorChar;
            if (!ProfileSources.IsDriveRoot(repoCandidate) && exclusions.HardReason(repoCandidate) is null)
                startPath = repoCandidate;
        }

        string? marker = null, git = null;
        var exists = fs.DirectoryExists(startPath);
        try
        {
            for (var current = startPath; current is not null; current = Directory.GetParent(current)?.FullName)
            {
                if (exclusions.HardReason(current) is not null) break;
                if (!fs.DirectoryExists(current)) continue;
                if (!exclusions.Temporary(current))
                {
                    if (fs.HasGit(current)) { git = current; break; }
                    if (marker is null && fs.HasMarker(current)) marker = current;
                }
            }
            var root = git ?? marker ?? startPath;
            if (exclusions.Temporary(root)) return _cache[path] = (null, "gecici-klasor", false, false, !exists);
            var rootExists = fs.DirectoryExists(root);
            return _cache[path] = (root, null, git is not null || (rootExists && SafeGit(root)), rootExists && fs.HasTests(root), !rootExists);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (exclusions.Temporary(startPath)) return _cache[path] = (null, "gecici-klasor", false, false, !exists);
            return _cache[path] = (startPath, null, false, false, !exists);
        }
    }

    public static bool IsRoot(SessionMetadata row) => string.IsNullOrWhiteSpace(row.ParentId);

    public static string? ActivityLevel(DateTime? lastOrNull, DateTime now)
    {
        if (lastOrNull is not { } last || last > now.AddMinutes(1) || last == DateTime.MinValue) return null;
        if (last >= now.AddMinutes(-2)) return "yaziyor";
        if (last >= now.AddHours(-2)) return "sicak";
        return last.ToLocalTime().Date == now.ToLocalTime().Date ? "bugun" : null;
    }

    private static bool Recent(SessionMetadata s, DateTime now, int days) => IsRoot(s) && s.LastActivity >= now.AddDays(-days) && s.LastActivity <= now.AddMinutes(1);

    private static SessionBreakdown Breakdown(IEnumerable<SessionMetadata> items, DateTime now)
    {
        var rows = items.ToArray();
        var roots = rows.Count(IsRoot);
        return new(rows.Length, roots, rows.Length - roots, rows.Count(s => Recent(s, now, 7)), rows.Count(s => Recent(s, now, 30)));
    }

    public (List<ProjectProfile> Projects, UnassignedProfile Unassigned, List<LiveProject> Live, ProfileDefaults Defaults)
        Build(IReadOnlyList<SessionMetadata> sessions, DateTime now, ProcessInventory? processes = null, LiveHintContext? liveHints = null)
    {
        Assignments.Clear();
        var groups = new Dictionary<string, List<SessionMetadata>>(StringComparer.OrdinalIgnoreCase);
        var unassigned = new List<(SessionMetadata Row, string Reason)>();
        void Assign(SessionMetadata row, string? project, string? reason) => Assignments[(row.Tool, row.RecordPath, row.Id)] = (project, reason);
        void Reject(SessionMetadata row, string reason) { unassigned.Add((row, reason)); Assign(row, null, reason); }
        string? DirectoryOf(SessionMetadata s) => ProfileSources.CleanPath(string.IsNullOrWhiteSpace(s.Cwd) ? s.GitRepoRoot : s.Cwd);

        var oneOffs = sessions.Select(s => (Row: s, Dir: DirectoryOf(s)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Dir) && Path.IsPathFullyQualified(x.Dir!) && !ProfileSources.IsDriveRoot(x.Dir))
            .GroupBy(x => Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.Dir!)), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1)
                    .Any(p => new[] { "testler", "tests-out", "olcum", "worktrees" }.Contains(p, StringComparer.OrdinalIgnoreCase))
                && !fs.DirectoryExists(g.Key) && g.Count(x => IsRoot(x.Row)) < 5 && g.All(x => x.Row.StartedAt.HasValue && x.Row.LastActivity.HasValue)
                && (g.Max(x => x.Row.LastActivity!.Value) - g.Min(x => x.Row.StartedAt!.Value)).TotalDays < 30)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sessions)
        {
            var cwd = DirectoryOf(s);
            if (cwd is not null && Path.IsPathFullyQualified(cwd) && !ProfileSources.IsDriveRoot(cwd)
                && oneOffs.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd))))
            { Reject(s, "tek-seferlik"); continue; }
            var r = Resolve(cwd);
            if (r.Root is null)
            {
                var reason = r.Reason ?? "klasor-bilinmiyor";
                if (reason == "klasor-bilinmiyor" && s.Tool == "hermes" && !string.IsNullOrWhiteSpace(s.SourceChannel))
                    reason = "hermes-source:" + s.SourceChannel;
                Reject(s, reason);
                continue;
            }
            if (!groups.TryGetValue(r.Root, out var list)) groups[r.Root] = list = [];
            list.Add(s);
        }

        bool IsIndependentProject(string p) => fs.DirectoryExists(p) && (SafeGit(p) || SafeMarker(p));
        bool HasDirectStrongMarker(string p) => fs.DirectoryExists(p) && SafeStrongMarker(p);

        var roots = groups.Keys.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (HasDirectStrongMarker(root)) continue;
            var descendants = new List<string>();
            var independentSubProjects = 0;
            foreach (var child in roots.Where(p => !p.Equals(root, StringComparison.OrdinalIgnoreCase) && ProjectExclusions.Under(p, root)))
            {
                if (IsIndependentProject(child)) independentSubProjects++;
                if (!descendants.Any(p => ProjectExclusions.Under(child, p))) descendants.Add(child);
            }
            if (descendants.Count >= 3 && groups[root].Count(IsRoot) < 5)
                containers.Add(root);
            else if (independentSubProjects >= 2)
                containers.Add(root);
        }

        var transient = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, rows) in groups)
        {
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isRun = parts.SkipLast(1).Any(p => new[] { "testler", "tests-out", "olcum", "worktrees" }.Contains(p, StringComparer.OrdinalIgnoreCase));
            var starts = rows.Select(s => s.StartedAt ?? s.LastActivity).Where(t => t.HasValue).Select(t => t!.Value).ToArray();
            var ends = rows.Select(s => s.LastActivity).Where(t => t.HasValue).Select(t => t!.Value).ToArray();
            if (isRun && !fs.DirectoryExists(path) && rows.Count(IsRoot) < 5 && rows.All(s => s.StartedAt.HasValue) && starts.Length > 0 && ends.Length > 0
                && (ends.Max() - starts.Min()).TotalDays < 30) transient.Add(path);
        }

        // Üst proje kuralı: kendi .git ya da işareti olan klasör, üstünde proje sözleşmesi (PROJECT.md, CLAUDE.md, AGENTS.md) taşıyan
        // TEK projelik bir klasör varsa o projenin parçasıdır (ör. "E:\Sample Work" altındaki "urun"). Üst klasörde birden çok
        // bağımsız proje varsa o klasör çalışma alanıdır: çocuklar ayrı kalır (ör. "D:\tools" altındaki "wisper kemal").
        var workspaceCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool IsWorkspaceFolder(string folder)
        {
            if (workspaceCache.TryGetValue(folder, out var known)) return known;
            var independent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots)
                if (!r.Equals(folder, StringComparison.OrdinalIgnoreCase) && ProjectExclusions.Under(r, folder) && !transient.Contains(r) && IsIndependentProject(r))
                    independent.Add(r);
            if (independent.Count < 2)
            {
                foreach (var child in SafeChildren(folder))
                {
                    if (exclusions.Temporary(child)) continue;
                    if (IsIndependentProject(child)) { independent.Add(child); continue; }
                    foreach (var grand in SafeChildren(child))
                        if (!exclusions.Temporary(grand) && IsIndependentProject(grand)) independent.Add(grand);
                    if (independent.Count >= 2) break;
                }
            }
            // Bir projenin içindeki alt klasörler ayrı proje sayılmaz: yalnız en üstteki bağımsız projeler sayılır.
            var tops = independent.Count(p => !independent.Any(o => !o.Equals(p, StringComparison.OrdinalIgnoreCase) && ProjectExclusions.Under(p, o)));
            return workspaceCache[folder] = tops >= 2;
        }
        string? EligibleParent(string path)
        {
            var current = path;
            for (var depth = 0; depth < LiveRules.MaxParentProjectDepth; depth++)
            {
                current = Directory.GetParent(current)?.FullName;
                if (current is null || exclusions.HardReason(current) is not null || exclusions.Temporary(current)) return null;
                if (!fs.DirectoryExists(current)) continue;
                if (SafeContract(current))
                    return containers.Contains(current) || transient.Contains(current) || IsWorkspaceFolder(current) ? null : current;
                if (IsIndependentProject(current)) return null; // araya başka bir proje girdi
            }
            return null;
        }
        string TopProject(string path)
        {
            for (var guard = 0; guard < 8; guard++)
            {
                var up = EligibleParent(path);
                if (up is null) break;
                path = up;
            }
            return path;
        }

        var merged = new Dictionary<string, List<SessionMetadata>>(StringComparer.OrdinalIgnoreCase);
        var originalRoots = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in roots)
        {
            if (containers.Contains(path) || transient.Contains(path))
            {
                foreach (var s in groups[path]) Reject(s, containers.Contains(path) ? "kap-klasor" : "tek-seferlik");
                continue;
            }
            // Kendi .git ya da işareti olan klasör bağımsız projedir; yalnız TEK projelik bir üst proje sözleşmesi varsa ona bağlanır.
            var canonical = IsIndependentProject(path)
                ? TopProject(path)
                : roots.First(p => !containers.Contains(p) && !transient.Contains(p) && ProjectExclusions.Under(path, p));
            if (!merged.TryGetValue(canonical, out var list))
            {
                merged[canonical] = list = [];
                originalRoots[canonical] = new(StringComparer.OrdinalIgnoreCase);
            }
            list.AddRange(groups[path]);
            originalRoots[canonical].Add(path);
            foreach (var s in groups[path]) Assign(s, canonical, null);
        }

        var projects = new List<ProjectProfile>();
        foreach (var (path, rows) in merged)
        {
            var r = Resolve(path);
            var rootRows = rows.Where(IsRoot).ToArray();
            var sources = rows.GroupBy(s => (s.Tool, s.SourceType)).Select(g => new ProfileEvidence(g.Key.Tool, g.Key.SourceType, null, g.Max(s => s.ObservedAt))).ToArray();
            var score = rootRows.Sum(s =>
            {
                if (s.LastActivity is not { } lastActivity || lastActivity > now.AddMinutes(1)) return 0;
                var age = Math.Max(0, (now - lastActivity).TotalDays);
                return age < 1 ? 4.0 : age < 7 ? 2.0 : age < 30 ? .5 : .25;
            });
            var areaPaths = new HashSet<string>(originalRoots[path], StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var cwd = DirectoryOf(row);
                if (cwd is not null && !ProfileSources.IsDriveRoot(cwd) && ProjectExclusions.Under(cwd, path) && !exclusions.Temporary(cwd))
                    areaPaths.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)));
            }
            areaPaths.Remove(path);
            var areas = areaPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p =>
            {
                var inside = rows.Where(s =>
                {
                    var scwd = DirectoryOf(s) ?? "";
                    return scwd.Length > 0 && ProjectExclusions.Under(scwd, p);
                }).ToArray();
                var count = inside.Count(IsRoot);
                return new ProjectArea(p, inside.Length, count, inside.Length - count, SafeGit(p));
            }).ToArray();

            projects.Add(new(Path.GetFileName(path), path, Directory.GetParent(path)?.FullName ?? path, Path.GetPathRoot(path) ?? "", r.Missing,
                rows.GroupBy(s => s.Tool).ToDictionary(g => g.Key, g => g.Count()), rootRows.Where(s => s.StartedAt.HasValue).Select(s => s.StartedAt).DefaultIfEmpty().Min(),
                rootRows.Select(s => s.LastActivity).DefaultIfEmpty().Max(), rows.Count(s => Recent(s, now, 7)), rows.Count(s => Recent(s, now, 30)), score,
                r.Git, r.Tests, rootRows.Length, rows.Count - rootRows.Length, areas, r.Missing && rootRows.Length < 3, sources, now));
        }
        var live = BuildLive(sessions, merged, unassigned.Select(x => x.Row).ToArray(), now, processes, liveHints);

        // Klasörü artık olmayan proje "eski" sayılır ve listede en sona gider (etkin görünmesin, öne çıkmasın).
        projects = ProjectStates.Apply(projects, merged, now).Select(p => p.MissingFolder ? p with { Status = "eski" } : p)
            .OrderBy(p => p.MissingFolder).ThenBy(p => p.Soluk).ThenByDescending(p => p.Score).ThenByDescending(p => p.LastActivity).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var weights = projects.Where(p => p.Score > 0).Select(p => new DefaultWeight(p.Drive, Directory.GetParent(p.Path)?.FullName ?? p.Path, p.Score))
            .GroupBy(w => (w.Drive, w.Parent)).Select(g => new DefaultWeight(g.Key.Drive, g.Key.Parent, g.Sum(w => w.Score))).OrderByDescending(w => w.Score).ThenBy(w => w.Parent, StringComparer.OrdinalIgnoreCase).ToArray();
        var drive = weights.GroupBy(w => w.Drive, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(w => w.Score)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Key;
        var parentFolder = weights.FirstOrDefault(w => string.Equals(w.Drive, drive, StringComparison.OrdinalIgnoreCase))?.Parent;
        var reasons = unassigned.GroupBy(x => x.Reason).ToDictionary(g => g.Key, g => g.Count());
        var byTool = unassigned.GroupBy(x => x.Row.Tool).ToDictionary(g => g.Key, g => Breakdown(g.Select(x => x.Row), now));
        var byReason = unassigned.GroupBy(x => x.Reason).ToDictionary(g => g.Key, g => Breakdown(g.Select(x => x.Row), now));
        var total = Breakdown(unassigned.Select(x => x.Row), now);
        return (projects, new(unassigned.Count, reasons, now, Sources: unassigned.GroupBy(x => (x.Row.Tool, x.Row.SourceType)).Select(g => new ProfileEvidence(g.Key.Tool, g.Key.SourceType, null, now)).ToArray(),
                ByTool: byTool, ByReason: byReason, Sessions7Days: total.Sessions7Days, Sessions30Days: total.Sessions30Days), live,
            new(drive, parentFolder, projects.SelectMany(p => p.Sources).Distinct().ToArray(), now,
                drive is null ? "Yeterli kök oturum gözlemi yok." : "Sürücü kök oturumların güncellik puanı toplamıyla seçildi. Üst klasör aynı puan tablosundan, bu sürücü içinde seçildi. Kap klasör yeni proje için uygundur.", weights));
    }

    /// <summary>
    /// "Şu an açık" bölümünü hesaplar: süreç envanteri, Claude açık oturum bilgisi ve kayıtlı oturumlardan. Ağır tam tarama gerekmez;
    /// <paramref name="merged"/> proje yoluna göre oturumlar, <paramref name="unassigned"/> projesiz oturumlardır.
    /// </summary>
    public List<LiveProject> BuildLive(IReadOnlyList<SessionMetadata> sessions, Dictionary<string, List<SessionMetadata>> merged,
        IReadOnlyList<SessionMetadata> unassigned, DateTime now, ProcessInventory? processes, LiveHintContext? liveHints)
    {
        var (candidateMeta, extraOpenApps) = SelectLiveCandidates(sessions, merged, now, processes, liveHints);
        var live = new List<LiveProject>();
        void AddLive(string? path, IReadOnlyList<SessionMetadata> rows)
        {
            var active = rows.Where(s => ActivityLevel(s.LastActivity, now) != null || candidateMeta.ContainsKey((s.Tool, s.Id))).ToArray();
            var extraHere = extraOpenApps.Where(x => string.Equals(x.ProjectPath, path, StringComparison.OrdinalIgnoreCase)).Select(x => x.Session).ToArray();
            if (active.Length == 0 && extraHere.Length == 0) return;

            var relations = new List<LiveRelation>();
            var byId = active.GroupBy(s => (s.Tool, s.Id)).ToDictionary(g => g.Key, g => g.First());
            var pairs = new HashSet<(string, string, string, string)>();
            void Link(SessionMetadata a, SessionMetadata b, bool parent)
            {
                if (pairs.Contains((b.Tool, b.Id, a.Tool, a.Id)) || !pairs.Add((a.Tool, a.Id, b.Tool, b.Id))) return;
                relations.Add(new(a.Tool, a.Id, b.Tool, b.Id, parent ? "ana ve yardımcı" : "aynı projede",
                    new(a.Tool, a.SourceType, null, a.ObservedAt, parent ? "gozlem" : "cikarim"), now, parent ? "gozlem" : "cikarim"));
            }
            foreach (var s in active) if (s.ParentId is not null && byId.TryGetValue((s.Tool, s.ParentId), out var parent)) Link(parent, s, true);
            if (path is not null) foreach (var s in active.Skip(1)) Link(active[0], s, false);

            var liveRows = active.Select(s =>
            {
                var hasCand = candidateMeta.TryGetValue((s.Tool, s.Id), out var info);
                return new LiveSession(
                    s.Tool,
                    s.Id,
                    s.LastActivity,
                    ActivityLevel(s.LastActivity, now),
                    hasCand,
                    info?.Kind ?? "cikarim",
                    string.IsNullOrWhiteSpace(s.Title) ? info?.Title : s.Title,
                    info?.Pid,
                    info?.Hwnd,
                    info?.Hwnd is > 0,
                    info?.StatusLabel,
                    info?.Evidence ?? LiveRules.EvidenceForActivity(s.LastActivity, now),
                    info?.State ?? LiveRules.StateUnknown,
                    info?.WaitingFor);
            }).Concat(extraHere).ToArray();

            live.Add(new(path, liveRows, relations, now));
        }

        var overrideRows = unassigned.Where(x => candidateMeta.TryGetValue((x.Tool, x.Id), out var m) && m.ProjectOverride is not null).ToArray();
        bool Overridden(SessionMetadata row) => candidateMeta.TryGetValue((row.Tool, row.Id), out var m) && m.ProjectOverride is not null;

        foreach (var (path, rows) in merged)
            AddLive(path, overrideRows.Where(o => string.Equals(candidateMeta[(o.Tool, o.Id)].ProjectOverride, path, StringComparison.OrdinalIgnoreCase)).Concat(rows).ToArray());
        AddLive(null, unassigned.Where(x => !Overridden(x)).ToArray());
        return live;
    }

    private sealed record CandidateMeta(int? Pid, long? Hwnd, string? StatusLabel, string Kind, string Evidence = LiveRules.EvidenceProcess, string? ProjectOverride = null,
        string State = LiveRules.StateUnknown, string? WaitingFor = null, string? Title = null);

    private const string LabelOpen = "Şimdi açık";
    private const string LabelMaybe = "Açık olabilir";

    private (Dictionary<(string Tool, string Id), CandidateMeta> Candidates, List<(string? ProjectPath, LiveSession Session)> ExtraOpenApps)
        SelectLiveCandidates(
            IReadOnlyList<SessionMetadata> sessions,
            Dictionary<string, List<SessionMetadata>> merged,
            DateTime now,
            ProcessInventory? processes,
            LiveHintContext? liveHints)
    {
        var result = new Dictionary<(string Tool, string Id), CandidateMeta>();
        var extraApps = new List<(string? ProjectPath, LiveSession Session)>();

        string? ResolveProjectOf(string? cwd)
        {
            if (string.IsNullOrWhiteSpace(cwd)) return null;
            var r = Resolve(cwd);
            if (r.Root is null) return null;
            if (merged.ContainsKey(r.Root)) return r.Root;
            return merged.Keys.FirstOrDefault(k => ProjectExclusions.Under(r.Root, k));
        }

        string? ResolveProjectFromWindowTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var parts = title.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            // Son parça IDE adı ("Visual Studio Code", "Antigravity", "Cursor"), ondan önceki parça çoğunlukla açık klasör adıdır
            for (var idx = parts.Length - 2; idx >= 0; idx--)
            {
                var seg = parts[idx];
                if (seg.Equals("Insiders", StringComparison.OrdinalIgnoreCase) || seg.Equals("Visual Studio Code", StringComparison.OrdinalIgnoreCase))
                    continue;
                var hit = merged.Keys
                    .Where(k => Path.GetFileName(k).Equals(seg, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(k => merged[k].Max(s => s.LastActivity))
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
            return null;
        }

        string? ResolveProjectOfProcess(ProcessObservation p)
            => ResolveProjectOf(p.CurrentDirectory) ?? ResolveProjectFromWindowTitle(p.WindowTitle);

        // Süreç bağı kuruldu: kanıt "surec". Yalnız son hareketin yakınlığına dayanan eşleşme: kanıt "yakin-zaman".
        CandidateMeta Bound(ProcessObservation? p, string kind) => new(p?.Id, p?.WindowHandle, LabelOpen, kind, LiveRules.EvidenceProcess);
        CandidateMeta Near(ProcessObservation? p) => new(p?.Id, p?.WindowHandle, LabelMaybe, "cikarim", LiveRules.EvidenceNearTime);
        bool RecentRoot(SessionMetadata s, TimeSpan window) => s.LastActivity is { } last && last >= now - window && last <= now.AddMinutes(1);

        var activeProcsByTool = (processes?.Processes ?? [])
            .Where(p => !p.IsHelperProcess && ProcessProbe.ToolNames.TryGetValue(p.Name, out _))
            .GroupBy(p => ProcessProbe.ToolNames[p.Name], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 1) Antigravity: presence/*.lock (son 2 saat) ve not_fully_idle işareti YALNIZ çalışan bir Antigravity süreci varsa geçerlidir; açık pencereler
        var agProcs = (activeProcsByTool.GetValueOrDefault("antigravity") ?? [])
            .Where(p => string.IsNullOrWhiteSpace(p.CurrentDirectory) || !ProcessProbe.IsAssistantInternalPath(p.CurrentDirectory)).ToList(); // .gemini altındaki iç yan sunucu pencere değildir
        var defaultAgProc = agProcs.FirstOrDefault(p => p.WindowHandle is > 0) ?? agProcs.FirstOrDefault();
        var matchedAgProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (agProcs.Count > 0)
        {
            foreach (var s in sessions.Where(x => x.Tool == "antigravity"))
            {
                var inPresence = liveHints?.AntigravityPresenceIds?.Contains(s.Id) == true;
                var notIdle = s.SourceChannel == "antigravity-active" && (s.LastActivity is null || s.LastActivity >= now - LiveRules.StaleAfter);
                if (!inPresence && !notIdle) continue;
                var sProj = ResolveProjectOf(s.Cwd);
                var matchedProc = sProj is null ? null : agProcs.FirstOrDefault(p => ResolveProjectOfProcess(p) is { } pp && string.Equals(pp, sProj, StringComparison.OrdinalIgnoreCase));
                if (matchedProc is not null)
                {
                    result[("antigravity", s.Id)] = Bound(matchedProc, "gozlem");
                    matchedAgProjects.Add(sProj!);
                }
                else if (RecentRoot(s, LiveRules.NearTime))
                {
                    result[("antigravity", s.Id)] = Near(defaultAgProc) with { Kind = "gozlem" };
                    if (sProj is not null) matchedAgProjects.Add(sProj);
                }
            }
        }
        foreach (var agp in agProcs)
        {
            var proj = ResolveProjectOfProcess(agp);
            if (proj is not null && merged.TryGetValue(proj, out var projRows))
            {
                var latestAg = projRows.Where(s => s.Tool == "antigravity" && IsRoot(s) && s.LastActivity >= now - LiveRules.StaleAfter && LiveRules.Plausible(s.LastActivity, agp.StartedAt))
                    .OrderByDescending(s => s.LastActivity).FirstOrDefault();
                if (latestAg is not null)
                {
                    if (!result.ContainsKey(("antigravity", latestAg.Id)))
                        result[("antigravity", latestAg.Id)] = Bound(agp, "cikarim");
                    matchedAgProjects.Add(proj);
                    continue;
                }
            }
            if (result.Values.Any(m => m.Pid == agp.Id)) continue;
            if (agp.WindowHandle is > 0 && (proj is null || matchedAgProjects.Add(proj))
                && !extraApps.Any(x => x.Session.Tool == "antigravity" && x.Session.Hwnd == agp.WindowHandle)) // aynı pencere tek kayıt
            {
                extraApps.Add((proj, new LiveSession(
                    "antigravity",
                    "uygulama-" + agp.Id.ToString(CultureInfo.InvariantCulture),
                    agp.StartedAt ?? now,
                    "acik",
                    true,
                    "gozlem",
                    !string.IsNullOrWhiteSpace(agp.WindowTitle) ? agp.WindowTitle : "Antigravity açık",
                    agp.Id,
                    agp.WindowHandle,
                    true,
                    LabelOpen,
                    LiveRules.EvidenceProcess)));
            }
        }

        // 2) Hermes: active_sessions.json YALNIZ Hermes süreci çalışıyorsa geçerlidir; yoksa son 15 dk etkin oturum. Başka hiçbir oturum açık sayılmaz.
        //    Aynı pencereyi paylaşan süreçler tek Hermes sayılır.
        var hermesProcs = activeProcsByTool.GetValueOrDefault("hermes") ?? [];
        var hermesCount = hermesProcs.Count == 0 ? 0 : Math.Min(processes?.Tools.FirstOrDefault(t => t.Tool == "hermes")?.Count ?? 0,
            hermesProcs.Select(p => p.WindowHandle is > 0 ? "w" + p.WindowHandle.Value.ToString(CultureInfo.InvariantCulture) : "p" + p.Id.ToString(CultureInfo.InvariantCulture)).Distinct().Count());
        var defaultHermesProc = hermesProcs.FirstOrDefault(p => p.WindowHandle is > 0) ?? hermesProcs.FirstOrDefault();
        var matchedAnyHermes = false;
        if (hermesCount > 0 && liveHints?.HermesActiveIds is { Count: > 0 } activeHermesIds)
        {
            foreach (var s in sessions.Where(x => x.Tool == "hermes" && activeHermesIds.Contains(x.Id)))
            {
                result[("hermes", s.Id)] = Bound(defaultHermesProc, "gozlem");
                matchedAnyHermes = true;
            }
        }
        if (!matchedAnyHermes && hermesCount > 0)
        {
            var recentHermes = sessions
                .Where(s => s.Tool == "hermes" && IsRoot(s) && RecentRoot(s, LiveRules.RecentBinding))
                .GroupBy(s => s.Id)
                .Select(g => g.OrderByDescending(s => s.LastActivity).First())
                .OrderByDescending(s => s.LastActivity)
                .Take(hermesCount)
                .ToArray();
            foreach (var s in recentHermes)
            {
                result[("hermes", s.Id)] = Near(defaultHermesProc);
                matchedAnyHermes = true;
            }
            if (!matchedAnyHermes && defaultHermesProc is not null)
            {
                var proj = ResolveProjectOf(defaultHermesProc.CurrentDirectory);
                extraApps.Add((proj, new LiveSession(
                    "hermes",
                    "uygulama-" + defaultHermesProc.Id.ToString(CultureInfo.InvariantCulture),
                    defaultHermesProc.StartedAt ?? now,
                    "sicak",
                    true,
                    "gozlem",
                    "Hermes uygulaması açık",
                    defaultHermesProc.Id,
                    defaultHermesProc.WindowHandle,
                    defaultHermesProc.WindowHandle is > 0,
                    LabelOpen,
                    LiveRules.EvidenceProcess)));
            }
        }

        // 3) Codex: ChatGPT altındaki yardımcı codex süreçleri zaten elendi. Süreç klasörüyle (ve süreç başlangıcıyla) eşleşen oturum "surec";
        //    eşleşmeyen süreç için yalnız son 15 dk etkin oturum "yakin-zaman".
        var codexCount = processes?.Tools.FirstOrDefault(t => t.Tool == "codex")?.Count ?? 0;
        if (codexCount > 0)
        {
            var codexProcs = activeProcsByTool.GetValueOrDefault("codex") ?? [];
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cp in codexProcs.Where(p => !string.IsNullOrWhiteSpace(p.CurrentDirectory)))
            {
                var proj = ResolveProjectOf(cp.CurrentDirectory);
                var match = sessions.Where(s => s.Tool == "codex" && IsRoot(s) && !usedIds.Contains(s.Id) && LiveRules.Plausible(s.LastActivity, cp.StartedAt)
                        && ((proj is not null && string.Equals(ResolveProjectOf(s.Cwd), proj, StringComparison.OrdinalIgnoreCase))
                            || string.Equals(ProfileSources.CleanPath(s.Cwd), ProfileSources.CleanPath(cp.CurrentDirectory), StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(s => s.LastActivity)
                    .FirstOrDefault();
                if (match is not null)
                {
                    usedIds.Add(match.Id);
                    result[("codex", match.Id)] = Bound(cp, "cikarim");
                }
            }
            var remaining = codexCount - usedIds.Count;
            if (remaining > 0)
            {
                var fallbackProc = codexProcs.FirstOrDefault(p => p.WindowHandle is > 0) ?? codexProcs.FirstOrDefault();
                foreach (var s in sessions.Where(s => s.Tool == "codex" && IsRoot(s) && !usedIds.Contains(s.Id) && RecentRoot(s, LiveRules.RecentBinding))
                    .GroupBy(s => s.Id).Select(g => g.OrderByDescending(x => x.LastActivity).First())
                    .OrderByDescending(s => s.LastActivity).Take(remaining))
                {
                    result[("codex", s.Id)] = Near(fallbackProc);
                }
            }
        }

        // 4) Claude, OpenCode, VS Code, Cursor
        // 4a) Claude Code'un kendi süreç kaydı (sessions\<pid>.json): pid + süreç başlangıcı doğrulanırsa oturum kesin bellidir.
        //     Birincil kaynak `claude agents --json` (ClaudeSource = agents-komutu): liste tamdır, listede olmayan Claude süreci açık oturum sayılmaz
        //     ve süreç klasöründen ya da son harekete göre TAHMİN yapılmaz. Komut yoksa kayıt dosyaları, o da yoksa eski süreç tahmini.
        var registryPids = new HashSet<int>();
        var registrySessions = new HashSet<string>(StringComparer.Ordinal);
        var claudeStrict = liveHints?.ClaudeSource == LiveRules.SourceCommand;
        if (liveHints?.ClaudeRegistry is { Count: > 0 } registry)
        {
            var procById = (processes?.Processes ?? []).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var entry in registry.OrderByDescending(e => e.UpdatedAt ?? e.StartedAt ?? DateTime.MinValue))
            {
                procById.TryGetValue(entry.Pid, out var rp);
                if (rp is not null)
                {
                    if (!ProcessProbe.ToolNames.TryGetValue(rp.Name, out var rt) || rt != "claude") continue; // pid başka programa geçmiş
                    if (rp.StartedAt is { } actual)
                    {
                        if (entry.ProcessStartedAt is { } declared && (declared - actual).Duration() > LiveRules.ProcessStartTolerance) continue; // pid başka sürece geçmiş
                        if (entry.FromCommand && entry.StartedAt is { } reported && (reported - actual).Duration() > LiveRules.AgentsStartTolerance) continue;
                    }
                }
                else if (!entry.FromCommand) continue; // kayıt dosyası tek başına yetmez: sürecin yaşadığı doğrulanmalı
                if (registrySessions.Contains(entry.SessionId)) continue;
                var row = sessions.Where(s => s.Tool == "claude" && IsRoot(s) && string.Equals(s.Id, entry.SessionId, StringComparison.Ordinal))
                    .OrderByDescending(s => s.LastActivity).FirstOrDefault();
                var waiting = entry.WaitingFor; // okuyucu zaten denetim karakterlerini temizleyip uzunluğu sınırlar
                if (row is null)
                {
                    if (!entry.FromCommand) continue;
                    // Henüz taranmamış yeni oturum: kayıtsız da olsa komut onun açık olduğunu söylüyor
                    registrySessions.Add(entry.SessionId);
                    if (rp is not null) registryPids.Add(rp.Id);
                    var newProject = ResolveProjectOf(rp?.CurrentDirectory) ?? ResolveProjectOf(entry.Cwd);
                    extraApps.Add((newProject, new LiveSession("claude", entry.SessionId, entry.StartedAt ?? now, "acik", true, "gozlem",
                        entry.Name ?? "Claude penceresi", rp?.Id ?? entry.Pid, rp?.WindowHandle, rp?.WindowHandle is > 0, LabelOpen,
                        LiveRules.EvidenceProcess, entry.State, waiting)));
                    continue;
                }
                registrySessions.Add(row.Id);
                if (rp is not null) registryPids.Add(rp.Id);
                // Oturumun kendi klasörü projesizse (ör. sistem klasöründe başlatılmış) açık pencereyi sürecin çalıştığı projede göster
                var own = ResolveProjectOf(row.Cwd ?? row.GitRepoRoot);
                var viaProcess = own is null ? (rp is not null ? ResolveProjectOfProcess(rp) : ResolveProjectOf(entry.Cwd)) : null;
                result[("claude", row.Id)] = new CandidateMeta(rp?.Id ?? entry.Pid, rp?.WindowHandle, LabelOpen, "gozlem", LiveRules.EvidenceProcess, viaProcess,
                    entry.State, waiting, entry.Name);
            }
        }

        foreach (var toolInfo in processes?.Tools ?? [])
        {
            if (toolInfo.Count is not int count || count <= 0) continue;
            var tool = toolInfo.Tool;
            if (tool is "hermes" or "codex" or "antigravity") continue;
            if (tool == "claude" && claudeStrict) continue;

            var procs = (activeProcsByTool.GetValueOrDefault(tool) ?? []).Where(p => !registryPids.Contains(p.Id)).ToList();
            var usedSessions = tool == "claude" ? new HashSet<string>(registrySessions, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            var unmatchedProcs = new List<ProcessObservation>();

            foreach (var p in procs)
            {
                var proj = ResolveProjectOfProcess(p);
                if (proj is not null || !string.IsNullOrWhiteSpace(p.CurrentDirectory))
                {
                    var match = sessions.Where(s => s.Tool == tool && IsRoot(s) && !usedSessions.Contains(s.Id) && LiveRules.Plausible(s.LastActivity, p.StartedAt)
                            && ((proj is not null && string.Equals(ResolveProjectOf(s.Cwd), proj, StringComparison.OrdinalIgnoreCase))
                                || (!string.IsNullOrWhiteSpace(p.CurrentDirectory) && string.Equals(ProfileSources.CleanPath(s.Cwd), ProfileSources.CleanPath(p.CurrentDirectory), StringComparison.OrdinalIgnoreCase))))
                        .OrderByDescending(s => s.LastActivity)
                        .FirstOrDefault();
                    if (match is not null)
                    {
                        usedSessions.Add(match.Id);
                        result[(tool, match.Id)] = Bound(p, "cikarim");
                        continue;
                    }
                }
                unmatchedProcs.Add(p);
            }

            if (tool is "vscode" or "cursor")
            {
                // IDE pencerelerinde CWD/başlık eşleşmediyse yalnız son 15 dk etkin sohbetleri ya da görünür IDE penceresini dürüstçe göster
                var rem = count - usedSessions.Count;
                if (rem > 0)
                {
                    var ideProc = unmatchedProcs.FirstOrDefault(p => p.WindowHandle is > 0) ?? unmatchedProcs.FirstOrDefault();
                    var matchedRecent = false;
                    foreach (var s in sessions.Where(s => s.Tool == tool && IsRoot(s) && !usedSessions.Contains(s.Id) && RecentRoot(s, LiveRules.RecentBinding))
                        .GroupBy(s => s.Id).Select(g => g.OrderByDescending(x => x.LastActivity).First())
                        .OrderByDescending(s => s.LastActivity).Take(rem))
                    {
                        result[(tool, s.Id)] = Near(ideProc);
                        matchedRecent = true;
                    }
                    if (!matchedRecent)
                    {
                        foreach (var up in unmatchedProcs.Where(p => p.WindowHandle is > 0))
                        {
                            if (result.Values.Any(m => m.Pid == up.Id)) continue;
                            var proj = ResolveProjectOfProcess(up);
                            var appTitle = !string.IsNullOrWhiteSpace(up.WindowTitle)
                                ? up.WindowTitle
                                : (tool == "vscode" ? "VS Code açık" : "Cursor açık");
                            extraApps.Add((proj, new LiveSession(
                                tool,
                                "uygulama-" + up.Id.ToString(CultureInfo.InvariantCulture),
                                up.StartedAt ?? now,
                                "acik",
                                true,
                                "gozlem",
                                appTitle,
                                up.Id,
                                up.WindowHandle,
                                true,
                                LabelOpen,
                                LiveRules.EvidenceProcess)));
                        }
                    }
                }
                continue;
            }

            // Claude ve OpenCode: klasörü okunamamış süreçler için YALNIZ son 2 saatte etkin kök oturumlar ("yakin-zaman"). Eski oturum asla açık sayılmaz.
            var left = count - usedSessions.Count;
            if (left > 0)
            {
                var pool = sessions.Where(s => s.Tool == tool && IsRoot(s) && !usedSessions.Contains(s.Id) && RecentRoot(s, LiveRules.NearTime));
                var ordered = pool.GroupBy(s => s.Id).Select(g => g.OrderByDescending(s => s.LastActivity).First())
                    .OrderByDescending(s => s.LastActivity).ThenBy(s => s.Id, StringComparer.Ordinal).Take(left).ToArray();
                for (var i = 0; i < ordered.Length; i++)
                {
                    var p = i < unmatchedProcs.Count ? unmatchedProcs[i] : procs.FirstOrDefault();
                    result[(tool, ordered[i].Id)] = Near(p);
                }
            }
        }

        return (result, extraApps);
    }

    private IEnumerable<string> SafeChildren(string path)
    {
        try { return fs.ChildDirectories(path).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    private bool SafeContract(string path)
    {
        try { return fs.HasProjectContract(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private bool SafeGit(string path)
    {
        try { return fs.HasGit(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private bool SafeMarker(string path)
    {
        try { return fs.HasMarker(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private bool SafeStrongMarker(string path)
    {
        try { return fs.HasStrongProjectMarker(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
