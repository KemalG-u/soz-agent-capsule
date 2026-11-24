using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Text;

namespace Soz.Core.Proof;

/// <summary>
/// KANIT TABANI (denetim 3, İlke 1 "varsayılan kuşku"; denetim 4, A/C/D; e3'te sınıflama hüküm vermez, yalnız ONAYLI KÜMEYİ ve kartın "ne değişti" özetini besler). Proje klasörünün dosyaları taranır; her dosya sınıflanır:
///  zararsız ya da tanınmayan (null): kaynak kod, resim, yazı tipi, stil, belge, yml/toml/sh/log/csv gibi öteki dosyalar. İzlenmez; onaylı koşu bunları yargıç yapmaz.
///  "c" ayar: package.json, koşucu ayarları (RunnerDefs), derleme tanımları, gizli klasör ayarları, ölçütün adını andığı dosya. Koşucu ayarları ve çağrılan dosyalar onaylı kümenin üyesidir.
///  "t" test dosyası ve yardımcıları (test klasörü, ad kalıpları, içe aktarma grafiğinden bulunan destek dosyaları): onaylı kümenin üyesidir.
///  "d" bağımlılık kilidi, "n" testi etkilemeyen yapılandırma (lint, biçim, workflow): yalnız bilgi notu.
/// package.json alan alan karşılaştırılır. node_modules, .venv, venv, vendor (ve ölçüt oradan yüklüyorsa dist/build) için ucuz parmak izi tutulur:
/// üst düzey paket başına dosya sayısı, toplam boyut, en son değişme zamanı. Hiçbir komut çalıştırılmaz; yalnız dosya okunur.
/// İzlenen bütün dosyaların içerik özeti yeniden hesaplanır. Onaylı küme yalnız test ve ayarlardan oluşur, diğer içerik özetleri değişiklik yok kararını besler.
/// </summary>
public static class EvidenceBase
{
    /// <summary>Bu sayıdan fazla dosya varsa klasör İZLENEMEDİ sayılır (hüküm "geçti" olamaz).</summary>
    public const int MaxFiles = 50_000;
    public const long MaxHashBytes = 32L * 1024 * 1024;
    /// <summary>Başlangıçta saklanan küçük metin dosyasının üst sınırı ve bir görüntüdeki toplam sınır.</summary>
    public const int MaxContentBytes = 64 * 1024;
    public const int MaxTotalContent = 1_500_000;
    public const int MaxDiffLines = 12;

    public const string ClsConfig = "c", ClsTest = "t", ClsDeps = "d", ClsNote = "n";

    /// <summary>Kuşkulu bir dosyanın görüntüsü. Extra: package.json için ham metin. Content: küçük metin dosyasının (sır maskeli) içeriği, fark göstermek için.</summary>
    public sealed record Entry(string Path, long Size, long MTime, string Sha, string Cls, string? Extra = null, string? Content = null);

    /// <summary>Bağımlılık klasöründeki bir üst düzey paketin parmak izi.</summary>
    public sealed record DepPrint(string Path, int Files, long Bytes, long MTime, string? Ver = null);

    /// <summary>
    /// Referenced: ölçütün çağırdığı proje dosyaları (onaylı sınama kümesine girer, ApprovedSet). TreeId: bütün izlenen dosyaların yol ve SHA-256 içerik özetlerinden kısa parmak izi:
    /// iki görüntüde aynıysa ajan hiçbir dosyayı değiştirmemiştir (N6). Eski kayıtlarda yoktur (null: bilinmiyor, "değişmedi" denmez).
    /// Partial: destek dosyaları tam belirlenemeyen testler ("*": klasör çok büyük). Ignored: izlenmeyen klasörlerin (coverage, bin, .git...) "yol|sayı:boyut:zaman" parmak izleri (K7).
    /// Produced: Söz'ün kendi koşularının ürettiği dosyalar (ajan değişikliği sayılmaz). Graph: yardımcı dosyalar içe aktarma grafiğinden bulunmuş (eski kayıtlarda false).
    /// </summary>
    public sealed record Snapshot(List<Entry> Suspects, int FileCount, bool NotTracked, string? NotTrackedWhy = null, List<DepPrint>? Deps = null, List<string>? UsedScripts = null, long ScanMs = 0, long DepsMs = 0,
        List<string>? Referenced = null, string? TreeId = null, List<string>? Partial = null, List<string>? Ignored = null, List<string>? Produced = null, bool Graph = false)
    {
        public bool IsEmpty => Suspects.Count == 0;
        public bool TooMany => FileCount > MaxFiles;
    }

    public sealed record DiffLine(string Op, string Text);
    public sealed record FileDiff(string Path, string Kind, string Sentence, List<DiffLine> Lines);

    public sealed record Diff(List<string> ChangedConfig, List<string> ChangedTests, List<string> AddedTests, List<string> DepsInfo, bool NotTracked,
        List<string>? ChangedDeps = null, List<string>? Notes = null, List<FileDiff>? Diffs = null)
    {
        public List<string> DepsChanged { get; init; } = ChangedDeps ?? new();
        public List<string> NoteList { get; init; } = Notes ?? new();
        public List<FileDiff> FileDiffs { get; init; } = Diffs ?? new();
        /// <summary>
        /// e3: yalnız bağımlılık klasörünün elle değiştirilmesi alarmdır (onaylı koşu da aynı bağımlılık klasörünü kullanır). ChangedConfig ve ChangedTests yalnız bilgidir:
        /// onaylı koşu onaylı testler, onaylı koşucu ayarı ve onaylı komutla yapıldığı için ajanın eklediği ya da değiştirdiği başka dosya yargıcı etkileyemez.
        /// </summary>
        public bool Any => DepsChanged.Count > 0;
        public static Diff Empty => new(new(), new(), new(), new(), false);
    }

    // ---------------------------------------------------------------- sınıflama tabloları
    // Dışlama listesi TEK kaynaktır: Restore\RestoreOptions.cs içindeki Exclusions. Kanıt motoru ve geri alma aynı kümeleri okur, kopya yoktur.
    /// <summary>Bağımlılık klasörleri: ad -> tür (nm, py, vendor). Bu klasörler taranmaz, parmak izi alınır.</summary>
    private static readonly IReadOnlyDictionary<string, string> DepRootNames = Soz.Core.Restore.Exclusions.DepRootKinds;

    /// <summary>Klasör taramalarının atladığı ad mı (araç önbellekleri, derleme çıktısı, bağımlılık klasörleri)? Fark özeti de aynı listeyi kullanır.</summary>
    internal static bool IsIgnoredDir(string name) => SkipDirs.Contains(name) || DepRootNames.ContainsKey(name);

    private static readonly IReadOnlySet<string> BuildNames = Soz.Core.Restore.Exclusions.Build;

    private static readonly IReadOnlySet<string> SkipDirs = Soz.Core.Restore.Exclusions.Skip;

    private static readonly HashSet<string> HarmlessExt = new(StringComparer.OrdinalIgnoreCase)
    {
        // kaynak kod
        ".js", ".mjs", ".cjs", ".mts", ".cts", ".ts", ".tsx", ".jsx", ".cs", ".py", ".go", ".rs", ".java", ".kt", ".scala", ".rb", ".php", ".c", ".h", ".cpp", ".hpp", ".swift", ".dart", ".lua", ".svelte", ".vue", ".html", ".htm", ".sql",
        // stil
        ".css", ".scss", ".sass", ".less", ".styl",
        // belge
        ".md", ".mdx", ".txt", ".rst", ".adoc", ".pdf", ".docx", ".odt",
        // resim
        ".png", ".jpg", ".jpeg", ".svg", ".gif", ".webp", ".ico", ".bmp", ".avif", ".tiff",
        // yazı tipi
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        // ikili veri, ses, video, arşiv (çalıştırılmaz; bir ölçüt ya da betik adını anarsa Referenced ile yine kuşkulu olur)
        ".bin", ".dat", ".zip", ".gz", ".tar", ".7z", ".mp3", ".mp4", ".wav", ".mov", ".webm", ".ogg", ".mkv", ".avi",
    };

    private static readonly HashSet<string> HarmlessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitignore", ".editorconfig", ".npmignore", ".dockerignore", "license", "licence", "notice", "authors", "contributors", "changelog", "codeowners", "readme",
        ".gitattributes", ".gitkeep", ".env.example", ".env.sample", ".nvmrc", ".node-version", ".tool-versions", ".mailmap", ".browserslistrc", ".gitmodules",
    };

    /// <summary>Test ve derleme çalıştırıcılarının okuduğu bilinen json ayar adları (küçük harf). tsconfig*, jest.*, .mocharc* vb. IsAutoLoaded ve IsKnownConfigName ile ayrıca yakalanır.</summary>
    private static readonly HashSet<string> RunnerJson = new(StringComparer.OrdinalIgnoreCase)
    {
        "deno.json", "angular.json", "nx.json", "project.json", "turbo.json", "lerna.json", "rush.json", "jsconfig.json", "workspace.json", "karma.json", "ava.json", "tap.json", "playwright.json", "cypress.json", "vitest.json",
        "babel.json", "jest.json", "mocha.json", "pytest.json", "composer.json", "tox.json", "bunfig.json", "wrangler.json", "vercel.json", "firebase.json",
    };

    private static readonly HashSet<string> LockNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "bun.lock", "cargo.lock", "poetry.lock", "pipfile.lock", "composer.lock", "gemfile.lock", "go.sum",
    };

    /// <summary>
    /// package.json'da sınamayı etkilemeyen alanlar: değişirse yalnız bilgi notu (denetim 5, B1). Sınamayı etkileyenler (scripts, type, main, imports, exports, test çerçevesi blokları, overrides, workspaces...)
    /// ve tanınmayan her alan kuşkulu kalır.
    /// </summary>
    private static readonly HashSet<string> PkgInfoKeys = new(StringComparer.Ordinal)
    {
        "name", "version", "description", "keywords", "author", "contributors", "license", "homepage", "repository", "bugs", "funding", "private", "files", "engines", "os", "cpu",
        "publishConfig", "browserslist", "bin", "man", "directories", "sideEffects", "displayName", "types", "typings", "packageManager",
    };
    private static readonly string[] PkgDepKeys = { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" };

    private static readonly HashSet<string> TestDirs = new(StringComparer.OrdinalIgnoreCase) { "test", "tests", "__tests__", "__test__", "spec", "specs", "e2e", "__mocks__", "fixtures", "__fixtures__", "testdata" };

    /// <summary>Kökte araç adını gölgeleyen dosya adları (python pytest.py'yi kendi paketinin yerine yükler).</summary>
    private static readonly HashSet<string> ShadowStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "pytest", "unittest", "nose", "nose2", "coverage", "tox", "mocha", "jest", "vitest", "ava", "tap", "jasmine", "karma", "playwright", "cypress", "node",
    };
    private static readonly HashSet<string> ShadowExt = new(StringComparer.OrdinalIgnoreCase) { ".py", ".js", ".cjs", ".mjs", ".ts" };

    private static readonly Regex LintTool = new(@"^(?:\.?eslint(?:rc)?(?:\.config)?[.\w-]*|\.eslintignore|\.?prettier(?:rc)?(?:\.config)?[.\w-]*|\.prettierignore|\.?stylelint(?:rc)?(?:\.config)?[.\w-]*|\.stylelintignore)$", RegexOptions.IgnoreCase | RegexSpeed.Compiled);

    // ---------------------------------------------------------------- test adı tanıma
    /// <summary>
    /// Test dosyası mı? Yalnız: test/tests/__tests__ (ve spec, e2e, __mocks__, fixtures, testdata) klasörü altı, *.test.*, *.spec.*, *_test.*, *-test.*, test_*, test-*, test.js,
    /// Java/C# kuralı *Test(s).*. "testimonials", "setup-db", "latest" gibi adlar sıradan koddur.
    /// </summary>
    public static bool IsTestFile(string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        for (var i = 0; i < parts.Length - 1; i++) if (TestDirs.Contains(parts[i])) return true;
        var file = parts[^1];
        var dot = file.LastIndexOf('.');
        var stem = dot > 0 ? file[..dot] : file;
        var lower = stem.ToLowerInvariant();
        if (lower is "test" or "tests" or "spec" or "specs") return true;
        foreach (var suf in new[] { ".test", ".spec", ".tests", "_test", "_tests", "-test", "-tests", "_spec", "-spec" })
            if (lower.EndsWith(suf, StringComparison.Ordinal)) return true;
        if (lower.StartsWith("test_", StringComparison.Ordinal) || lower.StartsWith("test-", StringComparison.Ordinal) || lower.StartsWith("test.", StringComparison.Ordinal)) return true;
        if (stem.Length > 4 && (stem.EndsWith("Test", StringComparison.Ordinal) || stem.EndsWith("Tests", StringComparison.Ordinal))) return true;
        if (stem.Length > 4 && stem.StartsWith("Test", StringComparison.Ordinal) && char.IsUpper(stem[4])) return true;
        return false;
    }

    /// <summary>Çalıştırıcının kendiliğinden yüklediği ya da araç adını gölgeleyen adlar: YENİ eklenince de kuşkulu (sınıf c).</summary>
    public static bool IsAutoLoaded(string rel)
    {
        var norm = rel.Replace('\\', '/');
        var name = System.IO.Path.GetFileName(norm).ToLowerInvariant();
        if (RunnerDefs.IsConfigName(norm)) return true; // koşucu tanımı (RunnerDefs): jest.*, .mocharc*, conftest.py, pytest.ini, pyproject.toml, setup.cfg ...
        if (name.StartsWith("tsconfig", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)) return true;
        if (!norm.Contains('/'))
        {
            var ext = System.IO.Path.GetExtension(name);
            var stem = System.IO.Path.GetFileNameWithoutExtension(name);
            if (ShadowExt.Contains(ext) && ShadowStems.Contains(stem)) return true;
        }
        return false;
    }

    private static bool IsKnownConfigName(string n)
    {
        if (n.StartsWith('.')) return true;
        if (n.EndsWith(".csproj") || n.EndsWith(".fsproj") || n.EndsWith(".vbproj") || n.EndsWith(".sln") || n.EndsWith(".slnx")) return true;
        if (n.StartsWith("directory.build.") || n.Contains(".config.")) return true;
        return n is "package.json" or "pyproject.toml" or "setup.cfg" or "cargo.toml" or "go.mod" or "makefile" or "dockerfile" or "global.json" or "nuget.config";
    }

    private static bool IsLocaleJson(string rel, string ext)
    {
        if (!ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals("locales", StringComparison.OrdinalIgnoreCase) || parts[i].Equals("i18n", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Dosyanın sınıfı: null = zararsız; "c" sınama komutu/ayarı; "t" test; "d" bağımlılık kilidi (bilgi); "n" sınamayı etkilemeyen yapılandırma (bilgi).
    /// full verilirse içerik gerektiren kurallar da işler (*_test.go içinde TestMain).
    /// </summary>
    public static string? Classify(string rel, ScanContext? ctx = null, string? full = null)
    {
        ctx ??= ScanContext.Empty;
        rel = rel.Replace('\\', '/');
        var name = System.IO.Path.GetFileName(rel).ToLowerInvariant();
        var ext = System.IO.Path.GetExtension(name);
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (LockNames.Contains(name)) return ClsDeps;
        if (name.StartsWith("requirements", StringComparison.Ordinal) && ext == ".txt") return ClsDeps;
        if (ctx.Referenced.Contains(rel)) return ClsConfig; // ölçütün ya da kullanılan betiğin çalıştırdığı dosya
        if (ctx.Pinned.Contains(rel)) return ClsTest;       // onaylı kümenin bilinen üyesi (ör. yardımcı): ajan işi değiştirerek kümeden çıkaramaz
        if (parts.Length > 1 && parts[0].Equals(".vscode", StringComparison.OrdinalIgnoreCase)) return null;
        if (parts.Length > 1 && parts[0].Equals(".idea", StringComparison.OrdinalIgnoreCase)) return null;
        if (LintTool.IsMatch(name))
        {
            var tool = name.Contains("eslint") ? "eslint" : name.Contains("prettier") ? "prettier" : "stylelint";
            return ctx.RunsTool(tool) ? ClsConfig : ClsNote;
        }
        if (parts.Length > 2 && parts[0].Equals(".github", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("workflows", StringComparison.OrdinalIgnoreCase)) return ClsNote;
        if (IsAutoLoaded(rel)) return ClsConfig;
        if (IsTestFile(rel))
        {
            if (name.EndsWith("_test.go", StringComparison.Ordinal) && full is not null && HasText(full, "func TestMain(")) return ClsConfig;
            return ClsTest;
        }
        if (ApprovedSet.IsTestSupport(rel)) return ClsTest; // test klasörü dışındaki yardımcı (assert.js, test-utils.js): onaylı sınama kümesinin parçası
        if (HarmlessNames.Contains(name)) return null;
        if (IsKnownConfigName(name)) return ClsConfig;
        // Gizli klasörlerdeki ayarlar (.claude/settings.json, .codex, .husky...) ajanın ve kancaların davranışını belirler: kuşkulu kalır.
        if (parts.Length > 1 && parts[0].StartsWith('.')) return ClsConfig;
        // Sıradan ayar ve veri json'u (ayar.json, veri.json, data\x.json) sınamayı etkilemez; çalıştırıcıların bilinen json ayarları yukarıda ve aşağıdaki adlarla kuşkulu kalır (denetim 5, B1).
        if (ext == ".json" && !RunnerJson.Contains(name)) return null;
        if (HarmlessExt.Contains(ext)) return null;
        if (IsLocaleJson(rel, ext)) return null;
        // e3: tanınmayan tür (yml, toml, sh, log, csv, noktalı ad...) artık "kuşkulu" sayılmaz ve hüküm vermez. Onaylı koşu onaylı testlerle, onaylı koşucu ayarıyla ve onaylı komutla yapılır:
        // ajanın eklediği ya da değiştirdiği başka bir dosya yargıcı etkileyemez. Koşucunun okuyabileceği ayarlar (RunnerDefs) ve ölçütün çağırdığı dosyalar yukarıda zaten üyedir.
        return null;
    }

    private static bool HasText(string full, string needle)
    {
        try
        {
            var fi = new FileInfo(full);
            if (fi.Length > MaxContentBytes * 4) return false;
            return File.ReadAllText(full).Contains(needle, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---------------------------------------------------------------- tarama
    private readonly record struct Item(string Name, bool IsDir, bool Reparse, long Len, long MTime);

    private static bool Sensitive(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith(".env") || n.EndsWith(".pem") || n.EndsWith(".key") || n.EndsWith(".pfx") || n.EndsWith(".p12") || n.StartsWith("id_rsa") || n.StartsWith("id_ed25519") || n.StartsWith("credentials") || n.StartsWith("secrets");
    }

    /// <summary>
    /// Proje kökünü tarar. ctx: ölçüt komutlarından kurulan bağlam (yoksa boş). Dosya sayısı MaxFiles'ı aşarsa NotTracked.
    /// </summary>
    public static Snapshot Scan(string root, ScanContext? ctx = null)
    {
        ctx ??= ScanContext.Empty;
        var sw = Stopwatch.StartNew();
        var suspects = new List<Entry>();
        if (!Directory.Exists(root)) return new Snapshot(suspects, 0, true, "Klasör bulunamadı.");
        var rootFull = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
        var count = 0; ulong treeSum = 0, treeXor = 0;
        var treeComplete = true;
        var contentBudget = MaxTotalContent;
        var depRoots = new List<(string full, string rel, string kind)>();
        var buildDirs = new List<(string full, string rel)>();
        var ignoredDirs = new List<(string full, string rel)>();
        var jsFiles = new List<(string rel, string full, long len)>();
        var dataCandidates = new List<string>();
        var stack = new Stack<(string dir, string rel)>();
        stack.Push((rootFull, ""));
        while (stack.Count > 0)
        {
            var (dir, relDir) = stack.Pop();
            List<Item> items;
            try
            {
                items = new FileSystemEnumerable<Item>(dir, (ref FileSystemEntry e) => new Item(e.FileName.ToString(), e.IsDirectory,
                        (e.Attributes & FileAttributes.ReparsePoint) != 0, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcTicks), new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true })
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var it in items)
            {
                var relPath = relDir.Length == 0 ? it.Name : relDir + "/" + it.Name;
                if (it.IsDir)
                {
                    if (it.Reparse) continue; // bağlantı klasörleri izlenmez (Y6 bunları proje olarak eklemeyi ayrıca reddeder)
                    var fullDir = System.IO.Path.Combine(dir, it.Name);
                    if (DepRootNames.TryGetValue(it.Name, out var kind)) { depRoots.Add((fullDir, relPath, kind)); continue; }
                    if (BuildNames.Contains(it.Name)) { buildDirs.Add((fullDir, relPath)); continue; }
                    if (SkipDirs.Contains(it.Name)) { ignoredDirs.Add((fullDir, relPath)); continue; }
                    stack.Push((fullDir, relPath));
                    continue;
                }
                if (++count > MaxFiles) return new Snapshot(suspects, count, true, $"Klasörde {MaxFiles:N0} dosyadan fazlası var.", null, null, sw.ElapsedMilliseconds);
                var full = System.IO.Path.Combine(dir, it.Name);
                if (it.Reparse) { treeComplete = false; continue; }
                try
                {
                    var treeMix = TreeMix(relPath, ContentHash(full)); treeSum += treeMix; treeXor ^= treeMix;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { treeComplete = false; }
                if (JsImportResolver.IsJsFile(it.Name)) jsFiles.Add((relPath, full, it.Len));
                else if (dataCandidates.Count < 4096 && SupportFinder.IsDataFile(relPath)) dataCandidates.Add(relPath);
                var cls = Classify(relPath, ctx, full);
                if (cls is null) continue;
                var (sha, extra, content) = Digest(full, it.Name, it.Len, cls, ref contentBudget);
                suspects.Add(new Entry(relPath, it.Len, it.MTime, sha, cls, extra, content));
            }
        }
        // K1: yalnız testin kullandığı destek dosyaları, içe aktarma grafiğinden (yapısal) bulunur; adı ya da yeri önemli değildir.
        var partial = new List<string>();
        try { AddSupport(rootFull, suspects, jsFiles, dataCandidates, ctx, ref contentBudget, partial); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { partial.Add("*"); }
        suspects.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var ignored = ignoredDirs.Concat(buildDirs).Select(d => d.rel + "|" + IgnoredPrint(d.full)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var scanMs = sw.ElapsedMilliseconds;

        // Bağımlılık klasörleri ve (ölçüt oradan yüklüyorsa) dist/build için parmak izi.
        var sw2 = Stopwatch.StartNew();
        var deps = new List<DepPrint>();
        foreach (var (full, rel, kind) in depRoots) deps.AddRange(FingerprintDepRoot(full, rel, kind));
        if (buildDirs.Count > 0 && !ctx.Rebuilds && (ctx.MentionsBuildOutput || suspects.Any(e => e.Cls == ClsTest && LoadsBuild(e.Content))))
            foreach (var (full, rel) in buildDirs) deps.AddRange(FingerprintUnits(rel, new[] { (rel, full, false) }, null));
        deps.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var referenced = ctx.Referenced.Where(r => suspects.Any(e => e.Path == r)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new Snapshot(suspects, count, false, null, deps, ctx.UsedScripts.OrderBy(x => x, StringComparer.Ordinal).ToList(), scanMs, sw2.ElapsedMilliseconds, referenced, treeComplete ? $"sha256:{count}:{treeSum:x}:{treeXor:x}" : null,
            partial.Count == 0 ? null : partial, ignored, null, true);
    }

    /// <summary>Destek dosyalarını bulur ve izlenen kayıtlara test sınıfında ekler (zaten başka bir sınıfla izleniyorsa dokunmaz).</summary>
    private static void AddSupport(string rootFull, List<Entry> suspects, List<(string rel, string full, long len)> jsFiles, IReadOnlyList<string> dataCandidates, ScanContext ctx, ref int contentBudget, List<string> partial)
    {
        var roots = suspects.Where(e => e.Cls == ClsTest || (e.Cls == ClsConfig && !IsPkg(e.Path) && (ApprovedSet.IsRunnerConfig(e.Path) || ctx.Referenced.Contains(e.Path)))).Select(e => e.Path).ToList();
        if (roots.Count == 0 || !roots.Any(JsImportResolver.IsJsFile) || jsFiles.Count == 0) return;
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in jsFiles) known[f.rel] = f.rel;
        bool Exists(string p)
        {
            if (known.TryGetValue(p, out var actual)) return actual == p;
            if (!SupportFinder.IsDataFile(p)) return false;
            try { return File.Exists(System.IO.Path.Combine(rootFull, p.Replace('/', System.IO.Path.DirectorySeparatorChar))); } catch (ArgumentException) { return false; }
        }
        var clsOf = suspects.ToDictionary(e => e.Path, e => e.Cls, StringComparer.Ordinal);
        var res = SupportFinder.Find(jsFiles, roots, p => clsOf.TryGetValue(p, out var c) && c == ClsTest, Exists, dataCandidates);
        partial.AddRange(res.Partial);
        foreach (var path in res.Support.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (clsOf.ContainsKey(path)) continue; // zaten izleniyor (test ya da ayar)
            var full = System.IO.Path.Combine(rootFull, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            FileInfo fi;
            try { fi = new FileInfo(full); if (!fi.Exists) continue; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            var (sha, extra, content) = Digest(full, fi.Name, fi.Length, ClsTest, ref contentBudget);
            suspects.Add(new Entry(path, fi.Length, fi.LastWriteTimeUtc.Ticks, sha, ClsTest, extra, content));
        }
    }

    /// <summary>İzlenmeyen bir klasörün (coverage, bin, .git...) kısa parmak izi: "dosya sayısı:boyut:son değişme". Çok büyükse ilk 4000 dosya (ucuz, kaba).</summary>
    private static string IgnoredPrint(string dir)
    {
        long files = 0, bytes = 0, mt = 0;
        try
        {
            var en = new FileSystemEnumerable<(long len, long mt)>(dir, (ref FileSystemEntry e) => (e.Length, e.LastWriteTimeUtc.UtcTicks),
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory };
            foreach (var (len, m) in en) { files++; bytes += len; if (m > mt) mt = m; if (files >= 4000) break; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return $"{files}:{bytes}:{mt}";
    }

    /// <summary>Dosya zamanından bağımsız içerik özeti.</summary>
    private static string ContentHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Yol ve içerik özetinin FNV-1a karışımı. Ağaçta sıradan bağımsız toplanır.</summary>
    private static ulong TreeMix(string rel, string sha)
    {
        const ulong prime = 1099511628211UL;
        var h = 14695981039346656037UL;
        foreach (var ch in rel) { h ^= ch; h *= prime; }
        foreach (var ch in sha) { h ^= ch; h *= prime; }
        return h;
    }

    private static readonly Regex BuildLiteral = new(@"['""](?:[^'""\r\n]*[/\\])?(?:dist|build)(?:[/\\][^'""\r\n]*)?['""]", RegexSpeed.Compiled);
    private static bool LoadsBuild(string? content) => content is not null && BuildLiteral.IsMatch(content);

    private static (string sha, string? extra, string? content) Digest(string path, string name, long len, string cls, ref int contentBudget)
    {
        try
        {
            var small = len <= MaxContentBytes;
            if (small)
            {
                var bytes = File.ReadAllBytes(path);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var isPkg = name.Equals("package.json", StringComparison.OrdinalIgnoreCase);
                var text = LooksText(bytes) ? Encoding.UTF8.GetString(bytes) : null;
                if (isPkg) return (sha, text, null);
                if (text is not null && cls is ClsConfig or ClsTest && !Sensitive(name))
                {
                    var masked = SecretFilter.MaskSecrets(text);
                    if (masked.Length <= contentBudget) { contentBudget -= masked.Length; return (sha, null, masked); }
                }
                return (sha, null, null);
            }
            if (len > MaxHashBytes) return ("boyut:" + len, null, null);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            return (Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(), null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ("okunamadi", null, null); }
    }

    private static bool LooksText(byte[] bytes)
    {
        var n = Math.Min(bytes.Length, 4096);
        for (var i = 0; i < n; i++) if (bytes[i] == 0) return false;
        return true;
    }

    // ---------------------------------------------------------------- bağımlılık parmak izi
    private static (int files, long bytes, long mtime) SumDir(string dir)
    {
        var files = 0; long bytes = 0, mt = 0;
        var en = new FileSystemEnumerable<(long len, long mt)>(dir, (ref FileSystemEntry e) => (e.Length, e.LastWriteTimeUtc.UtcTicks),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => !e.FileName.Equals("__pycache__", StringComparison.Ordinal) && !e.FileName.Equals(".cache", StringComparison.Ordinal),
        };
        foreach (var (len, m) in en) { files++; bytes += len; if (m > mt) mt = m; }
        return (files, bytes, mt);
    }

    private static readonly Regex VersionRe = new(@"""version""\s*:\s*""([^""]+)""", RegexSpeed.Compiled);

    private static List<DepPrint> FingerprintUnits(string relRoot, IEnumerable<(string rel, string full, bool isFile)> units, string? kind)
    {
        var list = units.ToList();
        var res = new DepPrint[list.Count];
        void One(int i)
        {
            var (rel, full, isFile) = list[i];
            try
            {
                if (isFile)
                {
                    var fi = new FileInfo(full);
                    res[i] = new DepPrint(rel, 1, fi.Length, fi.LastWriteTimeUtc.Ticks);
                    return;
                }
                var (f, b, m) = SumDir(full);
                string? ver = null;
                if (kind == "nm")
                {
                    var pj = System.IO.Path.Combine(full, "package.json");
                    if (File.Exists(pj) && new FileInfo(pj).Length < 262144) { var mm = VersionRe.Match(File.ReadAllText(pj)); if (mm.Success) ver = mm.Groups[1].Value; }
                }
                res[i] = new DepPrint(rel, f, b, m, ver);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { res[i] = new DepPrint(rel, -1, -1, 0); }
        }
        if (list.Count > 48) Parallel.For(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, One);
        else for (var i = 0; i < list.Count; i++) One(i);
        return res.ToList();
    }

    private static IEnumerable<(string rel, string full, bool isFile)> Children(string relDir, string fullDir, Func<string, bool, bool>? keep = null)
    {
        List<Item> items;
        try
        {
            items = new FileSystemEnumerable<Item>(fullDir, (ref FileSystemEntry e) => new Item(e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0, e.IsDirectory ? 0 : e.Length, 0),
                new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var it in items)
        {
            if (it.Reparse && it.IsDir) continue;
            if (keep is not null && !keep(it.Name, it.IsDir)) continue;
            yield return (relDir + "/" + it.Name, System.IO.Path.Combine(fullDir, it.Name), !it.IsDir);
        }
    }

    private static List<DepPrint> FingerprintDepRoot(string full, string rel, string kind)
    {
        try
        {
            var units = new List<(string rel, string full, bool isFile)>();
            if (kind == "nm")
            {
                foreach (var u in Children(rel, full, (n, d) => !n.StartsWith('.') || n == ".bin"))
                {
                    var n = u.rel[(rel.Length + 1)..];
                    if (n.StartsWith('@') && !u.isFile) units.AddRange(Children(u.rel, u.full));
                    else units.Add(u);
                }
            }
            else if (kind == "py")
            {
                foreach (var top in Children(rel, full))
                {
                    var n = top.rel[(rel.Length + 1)..];
                    if (top.isFile) { units.Add(top); continue; }
                    if (n.Equals("Scripts", StringComparison.OrdinalIgnoreCase) || n.Equals("bin", StringComparison.OrdinalIgnoreCase)) { units.Add(top); continue; }
                    if (n.Equals("Lib", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var sp in Children(top.rel, top.full, (x, d) => d && x.Equals("site-packages", StringComparison.OrdinalIgnoreCase)))
                            units.AddRange(Children(sp.rel, sp.full, (x, d) => !x.Equals("__pycache__", StringComparison.Ordinal)));
                    }
                    else if (n.Equals("lib", StringComparison.OrdinalIgnoreCase) || n.Equals("lib64", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var py in Children(top.rel, top.full, (x, d) => d && x.StartsWith("python", StringComparison.OrdinalIgnoreCase)))
                            foreach (var sp in Children(py.rel, py.full, (x, d) => d && x.Equals("site-packages", StringComparison.OrdinalIgnoreCase)))
                                units.AddRange(Children(sp.rel, sp.full, (x, d) => !x.Equals("__pycache__", StringComparison.Ordinal)));
                    }
                }
            }
            else units.AddRange(Children(rel, full)); // vendor: ilk düzey (Go için alan adı, composer için sağlayıcı)
            return FingerprintUnits(rel, units, kind);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new List<DepPrint> { new(rel, -1, -1, 0) }; }
    }

    // ---------------------------------------------------------------- anlık görüntü kimliği (E)
    /// <summary>Görüntünün kısa kimliği: kuşkulu dosyaların özetleri ve bağımlılık parmak izleri. Aynı durum aynı kimliği verir.</summary>
    public static string SnapshotId(Snapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(s.NotTracked ? "nt" : "ok").Append('\n');
        foreach (var e in s.Suspects) sb.Append(e.Path).Append('\u001f').Append(e.Sha).Append('\u001f').Append(e.Cls).Append('\n');
        if (s.Deps is not null) foreach (var d in s.Deps) sb.Append(d.Path).Append('\u001f').Append(d.Files).Append('\u001f').Append(d.Bytes).Append('\u001f').Append(d.MTime).Append('\u001f').Append(d.Ver).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..16];
    }

    /// <summary>Projenin açık komut ölçütlerinden kurulan bağlamla taranmış görüntü.</summary>
    public static Snapshot ScanProject(SqliteConnection c, long projectId, long? jobId, string root)
    {
        var cmds = new List<string>();
        try
        {
            foreach (var ch in CheckStore.EnabledFor(c, projectId, jobId))
                if (ch.Kind == CheckKinds.Command && ch.Spec["cmd"] is JsonValue v && v.TryGetValue<string>(out var s)) cmds.Add(s);
        }
        catch (SqliteException) { }
        var ctx = ScanContext.Build(root, cmds);
        try
        {
            if (LoadApproved(c, projectId) is { } appr)
                ctx = ctx.WithPinned(appr.Suspects.Where(e => e.Cls == ClsTest).Select(e => e.Path));
        }
        catch (SqliteException) { }
        return Scan(root, ctx);
    }

    // ---------------------------------------------------------------- karşılaştırma
    private static string Q(string s) => SecretFilter.MaskSecrets(s.Length > 160 ? s[..160] + "…" : s);

    /// <summary>İki metnin satır farkı: ortak baş ve son satırlar atılır, kalan "-" ve "+" satırları (en çok 12) döner.</summary>
    public static List<DiffLine> LineDiff(string? before, string? after)
    {
        var a = (before ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        var b = (after ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        if (before is null) a.Clear();
        if (after is null) b.Clear();
        var p = 0;
        while (p < a.Count && p < b.Count && a[p] == b[p]) p++;
        var s = 0;
        while (s < a.Count - p && s < b.Count - p && a[a.Count - 1 - s] == b[b.Count - 1 - s]) s++;
        var rem = a.Skip(p).Take(a.Count - p - s).Where(x => x.Length > 0 || a.Count - p - s == 1).ToList();
        var add = b.Skip(p).Take(b.Count - p - s).Where(x => x.Length > 0 || b.Count - p - s == 1).ToList();
        var lines = rem.Select(x => new DiffLine("-", Q(x))).Concat(add.Select(x => new DiffLine("+", Q(x)))).ToList();
        if (lines.Count > MaxDiffLines)
        {
            var extra = lines.Count - (MaxDiffLines - 1);
            lines = lines.Take(MaxDiffLines - 1).Append(new DiffLine(" ", $"… {extra} satır daha")).ToList();
        }
        return lines;
    }

    private static JsonObject? ParsePkg(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string J(JsonNode? n) => n is null ? "(yok)" : n.ToJsonString();
    private static string Pretty(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : J(n);

    /// <summary>
    /// package.json alan alan karşılaştırma. Kuşkulu: ölçütün kullandığı betik, type, imports, exports, main, test çerçevesi blokları, overrides/resolutions, workspaces,
    /// var olan bağımlılığın sürümü ya da silinmesi, kilit dosyasız bağımlılık eklemesi, bilinmeyen her alan. Yalnız bilgi: bağımlılık eklemesi (kilit dosyasıyla), kullanılmayan betik, version/description/keywords/author/license.
    /// </summary>
    internal static (List<string> suspicious, List<string> notes, List<DiffLine> lines, List<string> sentences) ComparePackage(string beforeText, string nowText, HashSet<string> usedScripts, bool lockChanged, bool testKeysRestored = true)
    {
        var sus = new List<string>(); var notes = new List<string>(); var lines = new List<DiffLine>(); var sentences = new List<string>();
        var a = ParsePkg(beforeText); var b = ParsePkg(nowText);
        if (a is null || b is null)
        {
            sus.Add("package.json okunamadı ya da bozuldu");
            sentences.Add("package.json okunamadı ya da bozuldu.");
            return (sus, notes, LineDiff(beforeText, nowText), sentences);
        }
        var keys = a.Select(kv => kv.Key).Union(b.Select(kv => kv.Key)).ToList();
        foreach (var k in keys)
        {
            var x = a[k]; var y = b[k];
            if (J(x) == J(y)) continue;
            if (PkgInfoKeys.Contains(k)) { notes.Add($"package.json «{k}» alanı değişti"); continue; }
            if (PkgDepKeys.Contains(k))
            {
                var xo = x as JsonObject ?? new JsonObject(); var yo = y as JsonObject ?? new JsonObject();
                if ((x is not null && x is not JsonObject) || (y is not null && y is not JsonObject)) { notes.Add($"package.json içindeki «{k}» alanı değişti"); continue; }
                foreach (var name in xo.Select(kv => kv.Key).Union(yo.Select(kv => kv.Key)))
                {
                    var xv = xo[name]; var yv = yo[name];
                    if (J(xv) == J(yv)) continue;
                    if (xv is null)
                    {
                        // Yeni bağımlılık eklemek sınamanın ne koştuğunu değiştirmez (sınama komutu ve ayarı ayrıca izlenir): yalnız not (denetim 5, B1).
                        notes.Add(lockChanged ? $"Bağımlılık eklendi: {name} (kilit dosyası da güncellendi)" : $"Bağımlılık eklendi: {name} (kilit dosyası değişmedi)");
                    }
                    else if (yv is null) notes.Add($"«{name}» bağımlılığı silindi");
                    else notes.Add($"«{name}» bağımlılığının sürümü «{Pretty(xv)}» iken «{Pretty(yv)}» yapıldı");
                }
                continue;
            }
            if (k == "scripts")
            {
                var xo = x as JsonObject ?? new JsonObject(); var yo = y as JsonObject ?? new JsonObject();
                foreach (var name in xo.Select(kv => kv.Key).Union(yo.Select(kv => kv.Key)))
                {
                    var xv = xo[name]; var yv = yo[name];
                    if (J(xv) == J(yv)) continue;
                    var used = usedScripts.Contains(name);
                    var label = name == "test" ? "Test komutu" : $"«{name}» betiği";
                    if (!used)
                    {
                        if (xv is null && CommonAppScripts.Contains(name)) continue; // K14: olağan uygulama betikleri için gürültü notu üretme
                        notes.Add($"Kullanılmayan betik {(xv is null ? "eklendi" : yv is null ? "silindi" : "değişti")}: {name}");
                        continue;
                    }
                    if (testKeysRestored) { notes.Add($"{label} {(xv is null ? "eklendi" : yv is null ? "silindi" : "değişti")} (sonuç onayladığın hâliyle alınır)"); continue; }
                    sus.Add($"package.json scripts.{name}");
                    if (xv is null) sentences.Add($"{label} eklendi ve sınamada kullanılıyor: «{Pretty(yv)}».");
                    else if (yv is null) sentences.Add($"{label} silindi (önceki: «{Pretty(xv)}»).");
                    else sentences.Add($"{label} «{Pretty(xv)}» iken «{Pretty(yv)}» yapıldı.");
                    if (xv is not null) lines.Add(new DiffLine("-", Q($"scripts.{name}: {Pretty(xv)}")));
                    if (yv is not null) lines.Add(new DiffLine("+", Q($"scripts.{name}: {Pretty(yv)}")));
                }
                continue;
            }
            if (testKeysRestored && ApprovedSet.PackageTestKeys.Contains(k)) { notes.Add($"package.json «{k}» alanı değişti (sonuç onayladığın hâliyle alınır)"); continue; }
            sus.Add($"package.json {k}");
            sentences.Add($"package.json içindeki «{k}» alanı değişti.");
            if (x is not null) lines.Add(new DiffLine("-", Q($"{k}: {J(x)}")));
            if (y is not null) lines.Add(new DiffLine("+", Q($"{k}: {J(y)}")));
        }
        if (lines.Count > MaxDiffLines)
        {
            var extra = lines.Count - (MaxDiffLines - 1);
            lines = lines.Take(MaxDiffLines - 1).Append(new DiffLine(" ", $"… {extra} satır daha")).ToList();
        }
        return (sus, notes, lines, sentences);
    }

    private static readonly HashSet<string> CommonAppScripts = new(StringComparer.OrdinalIgnoreCase)
        { "start", "dev", "build", "lint", "check", "typecheck", "preview", "format", "serve" };

    private static bool IsPkg(string path) => System.IO.Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// İki görüntüyü karşılaştırır. root (proje klasörü) verilirse test dosyasındaki içe aktarma yolu güncellemesi dosya sisteminde doğrulanır;
    /// verilmezse bu kural işlemez (güvenli yön: değişiklik sayılır). Test dosyaları için TestFileDiff: yalnız sınamanın gücü azalabiliyorsa alarm.
    /// </summary>
    public static Diff Compare(Snapshot before, Snapshot now, string? root = null)
    {
        var nowMap = now.Suspects.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var beforeMap = before.Suspects.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var cfg = new List<string>(); var tests = new List<string>(); var added = new List<string>(); var deps = new List<string>();
        var notes = new List<string>(); var depChanged = new List<string>(); var diffs = new List<FileDiff>();

        // Silinen test dosyası, başka bir adla ya da yerde aynı içerikle (içe aktarma yolları dışında) yeniden ortaya çıktıysa "yeniden adlandırıldı ya da taşındı": alarm değil, not.
        var movedFrom = new Dictionary<string, string>(StringComparer.Ordinal); // eski yol -> yeni yol
        var movedTo = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in before.Suspects.Where(x => x.Cls == ClsTest && !nowMap.ContainsKey(x.Path)))
        {
            var cands = now.Suspects.Where(x => x.Cls == ClsTest && !beforeMap.ContainsKey(x.Path) && !movedTo.Contains(x.Path)).ToList();
            var hit = cands.FirstOrDefault(x => x.Sha == f.Sha);
            if (hit is null && f.Content is not null)
                hit = cands.FirstOrDefault(x => x.Content is not null && TestFileDiff.Analyze(f.Path, f.Content, x.Path, x.Content, root).Clean);
            if (hit is null) continue;
            movedFrom[f.Path] = hit.Path; movedTo.Add(hit.Path);
        }
        var used = new HashSet<string>(before.UsedScripts ?? new(), StringComparer.Ordinal);
        foreach (var u in now.UsedScripts ?? new()) used.Add(u);

        // Kilit dosyası değişti mi? (bağımlılık eklemeyi "kuşkulu" saymamak için)
        var lockChanged = before.Suspects.Any(f => f.Cls == ClsDeps && (!nowMap.TryGetValue(f.Path, out var n) || n.Sha != f.Sha))
                          || now.Suspects.Any(f => f.Cls == ClsDeps && !beforeMap.ContainsKey(f.Path));

        // Söz'ün kendi koşularının ürettiği dosyalar (test raporu, log, görüntü kaydı) hiçbir karşılaştırmada değişiklik sayılmaz (e3, K3).
        var produced = new HashSet<string>((before.Produced ?? new()).Concat(now.Produced ?? new()), StringComparer.Ordinal);
        foreach (var f in before.Suspects)
        {
            if (produced.Contains(f.Path)) continue;
            var present = nowMap.TryGetValue(f.Path, out var n);
            var changed = !present || n!.Sha != f.Sha;
            if (!changed) continue;
            var kindWord = present ? "değişti" : "silindi";
            switch (f.Cls)
            {
                case ClsConfig:
                    if (present && IsPkg(f.Path) && f.Extra is not null && n!.Extra is not null)
                    {
                        var (sus, nt, lines, sentences) = ComparePackage(f.Extra, n.Extra, used, lockChanged);
                        notes.AddRange(nt);
                        if (sus.Count > 0)
                        {
                            // e3: hüküm vermez. package.json'ın test alanları onaylı hâliyle geri konur; bağımlılık ve öteki alanlar yalnız bilgidir.
                            cfg.Add(f.Path);
                            foreach (var sentence in sentences) notes.Add(sentence.TrimEnd('.'));
                            diffs.Add(new FileDiff(f.Path, "config-info", string.Join(" ", sentences), lines));
                        }
                    }
                    else if (ApprovedSet.IsMember(f, before))
                    {
                        // Koşucu ayarı onaylı sınama kümesindedir: hüküm onaylı hâliyle verilir, alarm değil (e2).
                        notes.Add($"Test ayarı {kindWord}: {f.Path} (sonuç onayladığın hâliyle alınır)");
                        diffs.Add(new FileDiff(f.Path, "config-info", present ? $"Test ayarı değişti: {f.Path}." : $"Test ayarı silindi: {f.Path}.", LineDiff(f.Content, present ? n!.Content : "")));
                    }
                    else
                    {
                        // Koşucunun ayarı olmayan ayar dosyası (derleme tanımı, gizli klasör ayarı...): yalnız sakin not.
                        cfg.Add(f.Path);
                        notes.Add($"Ayar dosyası {kindWord}: {f.Path}");
                        diffs.Add(new FileDiff(f.Path, "config-info", present ? $"Ayar dosyası değişti: {f.Path}." : $"Ayar dosyası silindi: {f.Path}.", LineDiff(f.Content, present ? n!.Content : "")));
                    }
                    break;
                case ClsTest:
                    if (!present && movedFrom.TryGetValue(f.Path, out var movedPath))
                    {
                        notes.Add($"Test dosyası yeniden adlandırıldı ya da taşındı: {f.Path} → {movedPath}");
                        break;
                    }
                    // e2: test dosyasındaki değişiklik HÜKÜM VERMEZ. Hüküm, onaylı testlerle yeni kodun koşusudur (VerdictRun). Burada yalnız karttaki "ajan testlerde ne değiştirdi" özeti üretilir.
                    tests.Add(f.Path);
                    if (present && f.Content is not null && n!.Content is not null)
                    {
                        var an = TestFileDiff.Analyze(f.Path, f.Content, n.Path, n.Content, root);
                        diffs.Add(new FileDiff(f.Path, "test", $"Test dosyası değişti: {f.Path} ({an.Describe}).", an.Lines));
                    }
                    else
                        diffs.Add(new FileDiff(f.Path, "test", present ? $"Test dosyası değişti: {f.Path} (içeriği karşılaştıramadım)." : $"Test dosyası silindi: {f.Path}.", LineDiff(f.Content, present ? n!.Content : "")));
                    break;
                case ClsNote:
                    notes.Add($"Testi etkilemeyen ayar {kindWord}: {f.Path}");
                    break;
                default: deps.Add(f.Path); notes.Add($"Bağımlılık kilidi {kindWord}: {f.Path}"); break;
            }
        }
        foreach (var f in now.Suspects)
        {
            if (beforeMap.ContainsKey(f.Path) || produced.Contains(f.Path)) continue;
            switch (f.Cls)
            {
                case ClsConfig:
                    if (ApprovedSet.RemoveWhenAdded(f))
                    {
                        // Ajanın eklediği koşucu ayarı onaylı kümede yoktur: geçici kopyadan çıkarılır, alarm değil.
                        notes.Add($"Yeni test ayarı eklendi: {f.Path} (sonuç onayladığın ayarlarla alınır)");
                        diffs.Add(new FileDiff(f.Path, "config-info", $"Yeni test ayarı eklendi: {f.Path}.", LineDiff(null, f.Content)));
                        break;
                    }
                    cfg.Add(f.Path);
                    notes.Add($"Yeni ayar dosyası eklendi: {f.Path}");
                    diffs.Add(new FileDiff(f.Path, "config-info", $"Yeni ayar dosyası eklendi: {f.Path}.", LineDiff(null, f.Content)));
                    break;
                case ClsTest:
                    if (movedTo.Contains(f.Path)) break; // yeniden adlandırma ya da taşıma notu yukarıda yazıldı
                    added.Add(f.Path); notes.Add($"Yeni test dosyası eklendi: {f.Path}"); break;
                case ClsNote: notes.Add($"Testi etkilemeyen ayar eklendi: {f.Path}"); break;
                default: deps.Add(f.Path); notes.Add($"Bağımlılık kilidi eklendi: {f.Path}"); break;
            }
        }

        // Bağımlılık klasörü parmak izi: kilit dosyası değişmeden içerik değiştiyse kuşkulu; kilitle birlikte yeni paket ya da yeni sürüm bilgi notu.
        if (before.Deps is not null && now.Deps is not null)
        {
            var bd = before.Deps.ToDictionary(d => d.Path, StringComparer.Ordinal);
            var nd = now.Deps.ToDictionary(d => d.Path, StringComparer.Ordinal);
            foreach (var d in before.Deps.Concat(now.Deps).Select(x => x.Path).Distinct(StringComparer.Ordinal))
            {
                bd.TryGetValue(d, out var x); nd.TryGetValue(d, out var y);
                if (x is not null && y is not null && x.Files == y.Files && x.Bytes == y.Bytes && x.MTime == y.MTime && x.Ver == y.Ver) continue;
                var isBuild = BuildNames.Contains(d.Split('/')[^1]);
                var shortName = d.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase) ? d["node_modules/".Length..] : d;
                var versionMoved = x is null || y is null || x.Ver != y.Ver;
                if (!isBuild && lockChanged && versionMoved) { notes.Add($"Bağımlılık değişti: {shortName} (kilit dosyası da güncellendi)"); continue; }
                depChanged.Add(shortName);
                var sentence = x is null ? $"«{shortName}» klasörü sonradan eklendi" : y is null ? $"«{shortName}» klasörü silindi" : $"«{shortName}» içindeki dosyalar elle değiştirilmiş";
                sentence += isBuild ? " (sınama bu klasörden yükleniyor)." : lockChanged ? " (kilit dosyası güncellenmiş ama sürüm aynı)." : " (kilit dosyası aynı kaldı).";
                diffs.Add(new FileDiff(d, "deps", sentence, new List<DiffLine>()));
            }
        }
        return new Diff(cfg, tests, added, deps, before.NotTracked || now.NotTracked, depChanged, notes, diffs);
    }

    // ---------------------------------------------------------------- saklama
    public static string Serialize(Snapshot s)
        => JsonSerializer.Serialize(new
        {
            v = 3, n = s.FileCount, nt = s.NotTracked, why = s.NotTrackedWhy, us = s.UsedScripts ?? new List<string>(), rf = s.Referenced, tid = s.TreeId, pt = s.Partial, ig = s.Ignored, pr = s.Produced, sg = s.Graph ? 1 : 0,
            f = s.Suspects.Select(e => new object?[] { e.Path, e.Size, e.MTime, e.Sha, e.Cls, e.Extra, e.Content }),
            p = (s.Deps ?? new List<DepPrint>()).Select(d => new object?[] { d.Path, d.Files, d.Bytes, d.MTime, d.Ver }),
        });

    /// <summary>Saklanan görüntüyü okur. Biçim tanınmazsa (eski sürüm, bozuk kayıt) null döner.</summary>
    public static Snapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["v"]?.GetValue<int>() != 3) return null;
            var list = new List<Entry>();
            foreach (var a in o["f"]!.AsArray())
            {
                var r = a!.AsArray();
                list.Add(new Entry(r[0]!.GetValue<string>(), r[1]!.GetValue<long>(), r[2]!.GetValue<long>(), r[3]!.GetValue<string>(), r[4]!.GetValue<string>(), r[5]?.GetValue<string>(), r[6]?.GetValue<string>()));
            }
            var deps = new List<DepPrint>();
            if (o["p"] is JsonArray pa)
                foreach (var a in pa) { var r = a!.AsArray(); deps.Add(new DepPrint(r[0]!.GetValue<string>(), r[1]!.GetValue<int>(), r[2]!.GetValue<long>(), r[3]!.GetValue<long>(), r[4]?.GetValue<string>())); }
            var us = o["us"] is JsonArray ua ? ua.Select(x => x!.GetValue<string>()).ToList() : new List<string>();
            var rf = o["rf"] is JsonArray ra ? ra.Select(x => x!.GetValue<string>()).ToList() : null;
            List<string>? Strs(string k) => o[k] is JsonArray xa ? xa.Select(x => x!.GetValue<string>()).ToList() : null;
            return new Snapshot(list, o["n"]?.GetValue<int>() ?? 0, o["nt"]?.GetValue<bool>() ?? false, o["why"]?.GetValue<string>(), deps, us, 0, 0, rf, o["tid"]?.GetValue<string>(),
                Strs("pt"), Strs("ig"), Strs("pr"), o["sg"]?.GetValue<int>() == 1);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }

    public static Snapshot? Load(SqliteConnection c, long jobId)
    {
        var row = c.Query("SELECT json FROM job_base WHERE job_id=@j", r => r.Str(0), null, ("@j", jobId)).FirstOrDefault();
        return Parse(row);
    }

    public static Snapshot? LoadApproved(SqliteConnection c, long projectId)
        => Parse(c.Scalar("SELECT defs_json FROM projects WHERE id=@p", null, ("@p", projectId)) as string);

    public const string ProjectNote = "Bu klasörde test tanımı ya da test dosyası son onayladığından beri değişmiş.";

    /// <summary>
    /// İş başlarken (Söz koşusu: ajan başlamadan hemen önce; terminal: oturuma bağlanma anı) tabanı alır. Yalnız bir kez.
    /// force = kullanıcı kabulü: iş tabanı ve projenin ONAYLI tabanı şimdiki duruma (given verilmişse kartın görüntüsüne) çekilir. refresh = yalnız iş tabanını yeniler (başlangıç ölçümünden sonra).
    /// Projenin onaylı tabanı yoksa ilk görüntü onaylı sayılır. Onaylıdan farklıysa işe baştan uyarı notu yazılır.
    /// Hata sızmaz: taban alınamayan işte kapı "izlenemedi" der, asla "geçti" demez.
    /// </summary>
    public static void TakeForJob(SqliteConnection c, long jobId, bool force = false, bool refresh = false, Snapshot? given = null)
    {
        try
        {
            if (!force && !refresh && c.ScalarLong("SELECT COUNT(*) FROM job_base WHERE job_id=@j", null, ("@j", jobId)) > 0) return;
            var pid = c.Scalar("SELECT project_id FROM jobs WHERE id=@j", null, ("@j", jobId)) as long?;
            if (pid is null) return;
            var root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", pid)) as string;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            var snap = given ?? ScanProject(c, pid.Value, jobId, root);
            c.Exec("INSERT INTO job_base(job_id, taken_at, json, truncated) VALUES(@j,@t,@s,@x) ON CONFLICT(job_id) DO UPDATE SET taken_at=excluded.taken_at, json=excluded.json, truncated=excluded.truncated",
                null, ("@j", jobId), ("@t", Time.NowIso()), ("@s", Serialize(snap)), ("@x", snap.NotTracked ? 1 : 0));
            DiffSummaryMeasurer.TakeForJob(c, jobId, pid.Value, root, refresh);
            if (refresh) return;
            string? note = null;
            var approved = LoadApproved(c, pid.Value);
            if (approved is { Graph: false, NotTracked: false } && !force && !snap.NotTracked)
            {
                // Eski kayıt (e3 öncesi): iş başındaki yardımcı dosyalar, ajan başlamadan önce klasörde duran hâliyle onaylı kümeye katılır (bir kez). Ajanın sonradan yaptığı değişiklik bunu etkilemez.
                var have = approved.Suspects.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
                var helpers = snap.Suspects.Where(e => e.Cls == ClsTest && !have.Contains(e.Path) && !IsTestFile(e.Path) && !ApprovedSet.IsTestSupport(e.Path)).ToList();
                var merged = approved.Suspects.Concat(helpers).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
                approved = approved with { Suspects = merged, Graph = true };
                SaveApproved(c, pid.Value, approved);
            }
            if (force || approved is null || snap.NotTracked)
            {
                if (!snap.NotTracked) SaveApproved(c, pid.Value, snap);
            }
            else if (Compare(approved, snap, root).Any || ApprovedSet.Changes(approved, snap).Any) note = ProjectNote;
            c.Exec("UPDATE jobs SET base_note=@n WHERE id=@j", null, ("@n", note), ("@j", jobId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { /* taban alınamazsa kapı "izlenemedi" der */ }
    }

    public static void SaveApproved(SqliteConnection c, long projectId, Snapshot snap)
        => c.Exec("UPDATE projects SET defs_json=@d, defs_at=@t WHERE id=@p", null, ("@d", Serialize(snap)), ("@t", Time.NowIso()), ("@p", projectId));

    /// <summary>
    /// Söz'ün KENDİ yazdığı dosyalar (terminal kancası: .claude/settings.json ve yedeği) onaylı tabana işlenir; yoksa kancayı kurmak sonraki her işi "ayar dosyası değişti" diye işaretlerdi.
    /// Yalnız verilen önekin altındaki kayıtlar değişir. Onaylı taban yoksa bir şey yapılmaz.
    /// </summary>
    public static void RefreshApprovedUnder(SqliteConnection c, long projectId, string root, string relPrefix)
    {
        try
        {
            var approved = LoadApproved(c, projectId);
            if (approved is null) return;
            var now = ScanProject(c, projectId, null, root);
            if (now.NotTracked) return;
            var keep = approved.Suspects.Where(e => !e.Path.StartsWith(relPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            keep.AddRange(now.Suspects.Where(e => e.Path.StartsWith(relPrefix, StringComparison.OrdinalIgnoreCase)));
            SaveApproved(c, projectId, approved with { Suspects = keep });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { /* tazelenemezse sonraki işte uyarı çıkar; güvenli yön */ }
    }

    /// <summary>
    /// İş kanıtıyla bitince onaylı taban ilerler ama ONAYLI SINAMA KÜMESİ (ApprovedSet) yerinde kalır: ajanın düzenlediği ya da eklediği testler kişi onaylayana kadar kümeye girmez (e2).
    /// Üye olmayan kayıtlar (kilit dosyaları, ayar dosyaları...) şimdiki hâlden gelir.
    /// </summary>
    public static Snapshot AdvanceKeepingMembers(Snapshot approvedPrev, Snapshot now)
    {
        var keep = approvedPrev.Suspects.Where(e => ApprovedSet.IsMember(e, approvedPrev)).ToList();
        var keepPaths = keep.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var rest = now.Suspects.Where(e => !keepPaths.Contains(e.Path) && !ApprovedSet.IsMember(e, now)).ToList();
        var all = keep.Concat(rest).ToList();
        all.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var refs = (approvedPrev.Referenced ?? new()).Concat(now.Referenced ?? new()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var produced = (approvedPrev.Produced ?? new()).Concat(now.Produced ?? new()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return now with { Suspects = all, Referenced = refs, Produced = produced.Count == 0 ? null : produced };
    }

    /// <summary>
    /// Söz'ün kendi sonuç koşusunun (ölçütler) proje klasörüne ürettiği dosyalar (log, rapor, çıktı): ajan bitiş anındaki görüntü (before) ile koşu sonrası görüntü (after) arasındaki yeni ya da değişen kayıtlar.
    /// Onaylı tabana "üretilen" diye işlenir: sonraki işte ajan değişikliği, yeni test ya da alarm sayılmaz.
    /// </summary>
    public static void AddProduced(SqliteConnection c, long projectId, Snapshot before, Snapshot after)
    {
        try
        {
            var beforeMap = before.Suspects.ToDictionary(e => e.Path, StringComparer.Ordinal);
            var made = after.Suspects.Where(e => !beforeMap.TryGetValue(e.Path, out var b) || b.Sha != e.Sha).Select(e => e.Path).ToList();
            if (made.Count == 0) return;
            var approved = LoadApproved(c, projectId);
            if (approved is null || approved.NotTracked) return;
            var all = (approved.Produced ?? new()).Concat(made).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(500).ToList();
            SaveApproved(c, projectId, approved with { Produced = all });
        }
        catch (SqliteException) { }
    }

    // ---------------------------------------------------------------- yeni testleri onaylı kümeye katma (e2, e3'te iki ayrı teklif)
    /// <summary>
    /// İş kanıtıyla bitince iki ayrı teklif kaydedilir: (a) ajanın EKLEDİĞİ yeni test, test ayarı ve yardımcı dosyalar; (b) ajanın DEĞİŞTİRDİĞİ onaylı dosyalar.
    /// (a) tek tıkla katılabilir. (b) tek tıkla kabul EDİLEMEZ: kişi önce sunucunun verdiği fark özetine bakmalı (kimliğiyle), sonra o kimlikle kabul eder. İkisi tek istekle birlikte kabul edilemez.
    /// </summary>
    public const string TestOfferEvent = "job_test_offer", TestsApprovedEvent = "job_tests_approved", TestOfferSeenEvent = "job_test_offer_seen", TestsDeclinedEvent = "job_tests_declined";

    public const string ScopeAdded = "added", ScopeEdited = "edited";

    public sealed record OfferFile(string Path, string Sentence, List<DiffLine> Lines);

    /// <summary>Bekleyen teklif. Added*: eklenen dosya sayıları (tür başına). Edited*: değiştirilen onaylı dosya sayıları, EditedId: fark özetinin kimliği, EditedFiles: fark özeti.</summary>
    public sealed record Offer(int AddedTests, int AddedConfigs, int AddedHelpers, int EditedTests, int EditedConfigs, int EditedHelpers, string? EditedId, List<OfferFile> EditedFiles)
    {
        public int Added => AddedTests + AddedConfigs + AddedHelpers;
        public int Edited => EditedTests + EditedConfigs + EditedHelpers;
    }

    /// <summary>Dosyanın teklifteki türü: test, ayar (koşucu ayarı) ya da yardımcı dosya.</summary>
    public static string OfferKind(string path) => IsTestFile(path) ? "test" : ApprovedSet.IsRunnerConfig(path) ? "config" : "helper";

    /// <summary>İki metnin tam satır farkı sayıları (kırpmasız): (silinen, eklenen).</summary>
    public static (int removed, int added) LineChangeCounts(string? before, string? after)
    {
        var a = (before ?? "").Replace("\r\n", "\n").Split('\n');
        var b = (after ?? "").Replace("\r\n", "\n").Split('\n');
        var p = 0;
        while (p < a.Length && p < b.Length && a[p] == b[p]) p++;
        var sfx = 0;
        while (sfx < a.Length - p && sfx < b.Length - p && a[a.Length - 1 - sfx] == b[b.Length - 1 - sfx]) sfx++;
        return (a.Length - p - sfx, b.Length - p - sfx);
    }

    /// <summary>"2 satır değişti, 1 satır silindi" gibi sade özet. Boşsa içerik aynı satırlardan oluşuyor (yalnız boşluk ya da satır sonu).</summary>
    public static string LineChangeSentence(string? before, string? after)
    {
        var (r, ad) = LineChangeCounts(before, after);
        var changed = Math.Min(r, ad); var deleted = r - changed; var added = ad - changed;
        var parts = new List<string>();
        if (changed > 0) parts.Add($"{changed} satır değişti");
        if (deleted > 0) parts.Add($"{deleted} satır silindi");
        if (added > 0) parts.Add($"{added} satır eklendi");
        return parts.Count == 0 ? "yalnız boşluk ya da satır sonu değişti" : string.Join(", ", parts);
    }

    /// <summary>Teklifteki bir değiştirilmiş dosyanın fark özeti: onaylı sürüm ile şimdiki sürüm arasında.</summary>
    public static OfferFile EditedDetail(string path, Entry? approved, Entry? now)
    {
        // package.json ham metni Extra'da tutulur. Ekrana giden farkta sırlar yine maskelenir.
        static string? Text(Entry? e) => e?.Content ?? (e is not null && IsPkg(e.Path) && e.Extra is not null ? SecretFilter.MaskSecrets(e.Extra) : null);
        var before = Text(approved);
        var after = Text(now);
        if (now is null && before is not null) return new OfferFile(path, $"{path}: dosya silindi", LineDiff(before, null));
        if (before is null || after is null) return new OfferFile(path, $"{path}: içeriği karşılaştıramadım", new List<DiffLine>());
        return new OfferFile(path, $"{path}: {LineChangeSentence(before, after)}", LineDiff(before, after));
    }

    public const string UnviewableEditMessage = "Bu dosyadaki değişikliği gösteremiyorum. Göremediğin değişikliği kabul edemezsin. Dosyayı kendin açıp bakabilir ya da işi yardımcıya geri gönderebilirsin.";

    /// <summary>Fark özetinin kimliği: (yol, onaylı özet, şimdiki özet) üçlülerinden. Dosya sonradan değişirse kimlik de değişir.</summary>
    public static string EditedDiffId(IEnumerable<(string path, string approvedSha, string nowSha)> items)
    {
        var sb = new StringBuilder();
        foreach (var i in items.OrderBy(x => x.path, StringComparer.Ordinal)) sb.Append(i.path).Append('\u001f').Append(i.approvedSha).Append('\u001f').Append(i.nowSha).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..16];
    }

    public static void RecordTestOffer(SqliteConnection c, long projectId, long jobId, Snapshot now, IReadOnlyList<string> added, IReadOnlyList<string> edited, Snapshot? approvedPrev = null)
    {
        var map = now.Suspects.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var apprMap = approvedPrev?.Suspects.ToDictionary(e => e.Path, StringComparer.Ordinal) ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
        object Row(string p) => new { p, s = map.TryGetValue(p, out var e) ? e.Sha : "", k = OfferKind(p) };
        var editedRows = edited.Select(p =>
        {
            map.TryGetValue(p, out var ne); apprMap.TryGetValue(p, out var ae);
            var d = EditedDetail(p, ae, ne);
            return new { p, s = ne?.Sha ?? "", a = ae?.Sha ?? "", k = OfferKind(p), sum = d.Sentence, lines = d.Lines.Select(l => new { op = l.Op, t = l.Text }).ToList() };
        }).ToList();
        var id = editedRows.Count == 0 ? null : EditedDiffId(editedRows.Select(r => (r.p, r.a, r.s)));
        Events.Log(c, TestOfferEvent, projectId, JsonSerializer.Serialize(new { jobId, added = added.Select(Row).ToList(), edited = editedRows, editedId = id }));
    }

    private static (long id, string? detail) LastOffer(SqliteConnection c, long jobId)
    {
        var last = c.Query("SELECT id, detail FROM events WHERE kind=@k AND json_extract(detail,'$.jobId')=@j ORDER BY id DESC LIMIT 1", r => (id: r.GetInt64(0), d: r.Str(1)), null, ("@k", TestOfferEvent), ("@j", jobId)).FirstOrDefault();
        return (last.id, last.d);
    }

    /// <summary>Hangi kapsamlar (added, edited) teklifin sonrasında cevaplandı? Kapsamsız eski kayıt: addedOnly ise yalnız added, değilse ikisi.</summary>
    private static HashSet<string> AnsweredScopes(SqliteConnection c, long jobId, long afterId)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        // "Hayır" da cevaptır: aynı teklif geri gelmez (yeni bir iş yeni teklif üretebilir).
        foreach (var d in c.Query("SELECT detail FROM events WHERE kind IN (@k, @d) AND json_extract(detail,'$.jobId')=@j AND id>@i", r => r.Str(0), null, ("@k", TestsApprovedEvent), ("@d", TestsDeclinedEvent), ("@j", jobId), ("@i", afterId)))
        {
            try
            {
                if (JsonNode.Parse(d ?? "") is not JsonObject o) continue;
                var sc = (o["scope"] as JsonValue)?.GetValue<string>();
                if (sc is not null) { if (sc == "all") { res.Add(ScopeAdded); res.Add(ScopeEdited); } else res.Add(sc); continue; }
                if (o["addedOnly"] is JsonValue ao && ao.TryGetValue<bool>(out var only) && only) res.Add(ScopeAdded);
                else { res.Add(ScopeAdded); res.Add(ScopeEdited); }
            }
            catch (JsonException) { }
        }
        return res;
    }

    /// <summary>İşin son teklifi (yoksa ya da kişi iki kapsamı da cevapladıysa null). Cevaplanan kapsam sayılmaz.</summary>
    public static Offer? PendingOffer(SqliteConnection c, long jobId)
    {
        var last = LastOffer(c, jobId);
        if (last.detail is null) return null;
        var answered = AnsweredScopes(c, jobId, last.id);
        try
        {
            if (JsonNode.Parse(last.detail) is not JsonObject o) return null;
            int At = 0, Ac = 0, Ah = 0, Et = 0, Ec = 0, Eh = 0;
            if (!answered.Contains(ScopeAdded) && o["added"] is JsonArray aa)
                foreach (var t in aa.OfType<JsonObject>())
                {
                    var p = (t["p"] as JsonValue)?.GetValue<string>() ?? "";
                    switch ((t["k"] as JsonValue)?.GetValue<string>() ?? OfferKind(p)) { case "test": At++; break; case "config": Ac++; break; default: Ah++; break; }
                }
            var files = new List<OfferFile>(); string? id = null;
            if (!answered.Contains(ScopeEdited) && o["edited"] is JsonArray ea)
            {
                foreach (var t in ea.OfType<JsonObject>())
                {
                    var p = (t["p"] as JsonValue)?.GetValue<string>() ?? "";
                    switch ((t["k"] as JsonValue)?.GetValue<string>() ?? OfferKind(p)) { case "test": Et++; break; case "config": Ec++; break; default: Eh++; break; }
                    var lines = (t["lines"] as JsonArray)?.OfType<JsonObject>().Select(l => new DiffLine((l["op"] as JsonValue)?.GetValue<string>() ?? " ", (l["t"] as JsonValue)?.GetValue<string>() ?? "")).ToList() ?? new List<DiffLine>();
                    files.Add(new OfferFile(p, (t["sum"] as JsonValue)?.GetValue<string>() ?? p, lines));
                }
                id = (o["editedId"] as JsonValue)?.GetValue<string>();
            }
            var offer = new Offer(At, Ac, Ah, Et, Ec, Eh, id, files);
            return offer.Added + offer.Edited == 0 ? null : offer;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>u6: GET test-offer çağrıldığında diskteki düzenlenmiş dosya sonradan değişmişse güncel fark özetini ve editedId'yi yeniden hesaplar.</summary>
    public static void RefreshPendingOfferIfChanged(SqliteConnection c, long projectId, long jobId)
    {
        try
        {
            var last = LastOffer(c, jobId);
            if (last.detail is null) return;
            var answered = AnsweredScopes(c, jobId, last.id);
            if (answered.Contains(ScopeEdited)) return;
            if (JsonNode.Parse(last.detail) is not JsonObject o || o["edited"] is not JsonArray ea || ea.Count == 0) return;

            var approved = LoadApproved(c, projectId);
            var root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
            if (approved is null || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            var now = ScanProject(c, projectId, null, root);
            if (now.NotTracked) return;
            var nowMap = now.Suspects.ToDictionary(f => f.Path, StringComparer.Ordinal);

            var editedPaths = new List<string>();
            var stale = false;
            foreach (var t in ea.OfType<JsonObject>())
            {
                var p = (t["p"] as JsonValue)?.GetValue<string>();
                var s = (t["s"] as JsonValue)?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(p)) continue;
                editedPaths.Add(p);
                var curSha = nowMap.TryGetValue(p, out var ne) ? ne.Sha : "";
                if (curSha != s) stale = true;
            }
            if (!stale || editedPaths.Count == 0) return;

            var remainingAdded = new List<string>();
            if (!answered.Contains(ScopeAdded) && o["added"] is JsonArray aa)
                foreach (var t in aa.OfType<JsonObject>())
                    if ((t["p"] as JsonValue)?.GetValue<string>() is { Length: > 0 } ap && nowMap.ContainsKey(ap))
                        remainingAdded.Add(ap);

            RecordTestOffer(c, projectId, jobId, now, remainingAdded, editedPaths, approved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or JsonException) { }
    }

    /// <summary>Kişi (b) teklifinin fark özetine baktı: kimlik kaydedilir. Kabul bu kayıt olmadan reddedilir.</summary>
    public static void MarkOfferSeen(SqliteConnection c, long projectId, long jobId, string editedId, bool human)
    {
        if (human && PendingOffer(c, jobId)?.EditedId == editedId && !OfferSeen(c, jobId, editedId))
            Events.Log(c, TestOfferSeenEvent, projectId, JsonSerializer.Serialize(new { jobId, editedId }));
    }

    /// <summary>Kişi teklife "Hayır" dedi (yalnız insan çağırabilir). scope: added | edited | all. Olay defterine yazılır, teklif bir daha sorulmaz.</summary>
    public static void DeclineOffer(SqliteConnection c, long projectId, long jobId, string scope)
        => Events.Log(c, TestsDeclinedEvent, projectId, JsonSerializer.Serialize(new { jobId, scope }));

    public static bool OfferSeen(SqliteConnection c, long jobId, string editedId)
        => c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind=@k AND json_extract(detail,'$.jobId')=@j AND json_extract(detail,'$.editedId')=@e", null, ("@k", TestOfferSeenEvent), ("@j", jobId), ("@e", editedId)) > 0;

    /// <summary>
    /// Kişi teklifi kabul etti (yalnız insan çağırabilir, CallerGate). İşin kayıtlı dosyalarından, özeti hâlâ aynı olanlar onaylı tabana girer.
    /// scope: "added" (varsayılan) ya da "edited"; ikisi birlikte katılamaz. addedOnly eski ad, anlamı scope=added ile aynıdır. Dönen: katılan dosya sayısı.
    /// </summary>
    public static int ApproveNewTests(SqliteConnection c, long projectId, long jobId, bool addedOnly = false, string? scope = null)
    {
        try
        {
            scope ??= ScopeAdded;
            var detail = LastOffer(c, jobId).detail;
            if (detail is null || JsonNode.Parse(detail) is not JsonObject rec) return 0;
            if (scope == ScopeEdited && PendingOffer(c, jobId) is { } offer &&
                offer.EditedFiles.Any(f => !f.Lines.Any(l => l.Op is "+" or "-")))
                throw new SozException(409, UnviewableEditMessage);
            var approved = LoadApproved(c, projectId);
            var root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
            if (approved is null || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;
            var now = ScanProject(c, projectId, null, root);
            if (now.NotTracked) return 0;
            var nowMap = now.Suspects.ToDictionary(f => f.Path, StringComparer.Ordinal);
            // Görülen teklifin bir dosyası bile sonradan değiştiyse hiçbirini kısmen kabul etme.
            if (scope == ScopeEdited && rec[scope] is JsonArray edited && edited.OfType<JsonObject>().Any(t =>
                t["p"] is not JsonValue pv || !pv.TryGetValue<string>(out var p) ||
                t["s"] is not JsonValue sv || !sv.TryGetValue<string>(out var s) ||
                !nowMap.TryGetValue(p, out var e) || e.Sha != s))
                throw new SozException(409, "Değişikliği gördükten sonra dosyalar yeniden değişti. Güncel değişikliğe bakmadan kabul edemezsin.");
            var suspects = approved.Suspects.ToList();
            var put = 0;
            if (rec[scope] is JsonArray arr)
                foreach (var t in arr.OfType<JsonObject>())
                {
                    var p = (t["p"] as JsonValue)?.GetValue<string>(); var s = (t["s"] as JsonValue)?.GetValue<string>();
                    if (p is null || !nowMap.TryGetValue(p, out var ne) || ne.Sha != s || !ApprovedSet.IsMember(ne, now)) continue;
                    var i = suspects.FindIndex(x => x.Path == p);
                    if (i >= 0) suspects[i] = ne; else suspects.Add(ne);
                    put++;
                }
            if (put == 0) return 0;
            suspects.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            var refs = (approved.Referenced ?? new()).Concat(now.Referenced ?? new()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            SaveApproved(c, projectId, approved with { Suspects = suspects, Referenced = refs });
            Events.Log(c, TestsApprovedEvent, projectId, JsonSerializer.Serialize(new { jobId, count = put, addedOnly = scope == ScopeAdded, scope }));
            return put;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or JsonException or InvalidOperationException) { return 0; }
    }

    /// <summary>Kişi "klasördeki testler şimdiki hâliyle onaylı" dedi: onaylı taban klasörün şimdiki görüntüsü olur. Dönen: yapıldı mı.</summary>
    public static bool ApproveCurrent(SqliteConnection c, long projectId)
    {
        try
        {
            var root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
            var now = ScanProject(c, projectId, null, root);
            if (now.NotTracked) return false;
            SaveApproved(c, projectId, now);
            Events.Log(c, TestsApprovedEvent, projectId, JsonSerializer.Serialize(new { jobId = (long?)null, all = true }));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { return false; }
    }

    /// <summary>Projenin onaylı tabanından şimdiki duruma fark (onaylı taban yoksa boş).</summary>
    public static Diff DriftVsApproved(SqliteConnection c, long projectId, Snapshot now)
    {
        var approved = LoadApproved(c, projectId);
        if (approved is null) return Diff.Empty;
        var root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
        return Compare(approved, now, root);
    }

    // ---------------------------------------------------------------- ajanın yazdığı deneme (denetim 5, B6)
    /// <summary>Olay türü: "önce deneme yaz" işinin sonunda ajanın kendi eklediği test dosyaları ve package.json durumu (yol ve özet; içerik yok).</summary>
    public const string AgentFilesEvent = "job_agent_files";

    /// <summary>
    /// "Önce deneme yaz" işinde ajanın klasöre bıraktığı deneme dosyalarını kaydeder: YENİ test dosyaları (yol ve özet) ve iş başından beri değişen package.json'ın özeti.
    /// Kişi öneriyi kabul edince yalnız bu kayıttaki dosyalar onaylı tabana işlenir (<see cref="ApproveAgentTest"/>); kayıtta olmayan değişiklik onaylanmaz.
    /// </summary>
    public static void RecordAgentFiles(SqliteConnection c, long projectId, long jobId, Snapshot before, Snapshot now)
    {
        if (before.NotTracked || now.NotTracked) return;
        var beforeMap = before.Suspects.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var tests = now.Suspects.Where(e => e.Cls == ClsTest && !beforeMap.ContainsKey(e.Path)).Select(e => new { p = e.Path, s = e.Sha }).ToList();
        var pkg = now.Suspects.FirstOrDefault(e => e.Path == "package.json");
        string? pkgSha = pkg is not null && (!beforeMap.TryGetValue("package.json", out var bp) || bp.Sha != pkg.Sha) ? pkg.Sha : null;
        if (tests.Count == 0 && pkgSha is null) return;
        var json = JsonSerializer.Serialize(new { jobId, tests, pkg = pkgSha });
        var last = c.Query("SELECT detail FROM events WHERE kind=@k AND json_extract(detail,'$.jobId')=@j ORDER BY id DESC LIMIT 1", r => r.Str(0), null, ("@k", AgentFilesEvent), ("@j", jobId)).FirstOrDefault();
        if (last == json) return;
        Events.Log(c, AgentFilesEvent, projectId, json);
    }

    /// <summary>
    /// Kişi ajanın yazdığı deneme önerisini KABUL etti (yalnız insan çağırabilir): o işte ajanın deneme için yaptığı değişiklikler onaylanmış sayılır ve onaylı taban o anda ilerler.
    /// Ne ilerler: işin kaydındaki YENİ test dosyaları (özet hâlâ aynıysa) ve package.json (yalnız onaylanan komutun çağırdığı betik alanı değiştiyse, projenin onaylı package.json'ı iş başındakiyle aynıysa).
    /// Daha fazlası değil: kayıtta olmayan, sonradan değişen ya da başka alanı değiştiren dosya onaylanmaz ve sonraki işte yine uyarı çıkar. True = taban ilerledi.
    /// </summary>
    public static bool ApproveAgentTest(SqliteConnection c, long projectId, long jobId, string? cmd)
    {
        try
        {
            var detail = c.Query("SELECT detail FROM events WHERE kind=@k AND json_extract(detail,'$.jobId')=@j ORDER BY id DESC LIMIT 1", r => r.Str(0), null, ("@k", AgentFilesEvent), ("@j", jobId)).FirstOrDefault();
            if (detail is null || JsonNode.Parse(detail) is not JsonObject rec) return false;
            var approved = LoadApproved(c, projectId);
            var root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
            if (approved is null || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
            var now = ScanProject(c, projectId, null, root);
            if (now.NotTracked) return false;
            var nowMap = now.Suspects.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var suspects = approved.Suspects.ToList();
            var changed = false;

            void Put(Entry e)
            {
                var i = suspects.FindIndex(x => x.Path == e.Path);
                if (i >= 0) { if (suspects[i].Sha == e.Sha) return; suspects[i] = e; } else suspects.Add(e);
                changed = true;
            }

            if (rec["tests"] is JsonArray ta)
                foreach (var t in ta.OfType<JsonObject>())
                {
                    var p = (t["p"] as JsonValue)?.GetValue<string>(); var s = (t["s"] as JsonValue)?.GetValue<string>();
                    if (p is not null && nowMap.TryGetValue(p, out var ne) && ne.Cls == ClsTest && ne.Sha == s) Put(ne);
                }

            var pkgSha = (rec["pkg"] as JsonValue)?.GetValue<string>();
            var jobBase = Load(c, jobId);
            var basePkg = jobBase?.Suspects.FirstOrDefault(e => e.Path == "package.json");
            var apprPkg = suspects.FirstOrDefault(e => e.Path == "package.json");
            if (pkgSha is not null && nowMap.TryGetValue("package.json", out var nowPkg) && nowPkg.Sha == pkgSha && nowPkg.Extra is not null && basePkg?.Extra is not null && apprPkg is not null && apprPkg.Sha == basePkg.Sha)
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(cmd)) foreach (var n in ScanContext.ScriptCalls(cmd, ScanContext.ReadScripts(root))) used.Add(n);
                var (sus, _, _, _) = ComparePackage(basePkg.Extra, nowPkg.Extra, used, false, testKeysRestored: false);
                if (sus.All(x => x.StartsWith("package.json scripts.", StringComparison.Ordinal))) Put(nowPkg);
            }

            if (!changed) return false;
            suspects.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            SaveApproved(c, projectId, approved with { Suspects = suspects });
            Events.Log(c, "agent_test_approved", projectId, JsonSerializer.Serialize(new { jobId, cmd }));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or JsonException or InvalidOperationException) { return false; }
    }
}
