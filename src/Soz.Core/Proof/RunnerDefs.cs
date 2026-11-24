using System.Text.RegularExpressions;
using Soz.Core.Text;

namespace Soz.Core.Proof;

/// <summary>Bir kaynak dosyadan okunan içe aktarmalar: Relative (göreli yol belirtimleri), UsesFramework (test ya da iddia kütüphanesi çağırıyor), Unresolved (sabit olmayan ya da takma adlı içe aktarma var).</summary>
public sealed record ImportInfo(List<string> Relative, bool UsesFramework, bool Unresolved);

/// <summary>
/// Dile bağlı içe aktarma çözümleyicisi (küçük arayüz). Yeni bir dil eklemek bir sınıf eklemektir: kanıt motoru değişmez.
/// Bu turda yalnız JS/TS var; öteki dillerde yardımcı dosya bulma yoktur (kümeye yalnız test klasörü ve ad ipucu girer).
/// </summary>
public interface IImportResolver
{
    bool Handles(string rel);
    ImportInfo Scan(string rel, string text);
    /// <summary>Belirtimi (ör. "../lib/dogrula") proje içi bir dosyaya çevirir; çözülemezse null. exists: köke göre yolun dosya olup olmadığı.</summary>
    string? Resolve(string fromRel, string spec, Func<string, bool> exists);
}

/// <summary>
/// KOŞUCU/DİL TANIMLARI (kayıt). Bir koşucunun kendiliğinden okuyabileceği ayar dosyası adları, dilin içe aktarma çözümleyicisi ve "kesin kalan" ek (ajan denemesini davranışla sınamak için).
/// Kanıt motoru ad listesi uzatarak değil, buradaki tanımdan öğrenir: yeni koşucu eklemek bu kayda bir satır eklemektir.
/// </summary>
public static class RunnerDefs
{
    public sealed record Def(string Name, IReadOnlyList<string> ConfigNames, IReadOnlyList<string> ConfigPrefixes, IReadOnlyList<string> ConfigSuffixes, IImportResolver? Imports, IReadOnlyDictionary<string, string> FailAppend);

    private static readonly IReadOnlyList<Def> All = new List<Def>
    {
        new("node", new[] { "ava.json", ".taprc", "karma.json", "playwright.json", "cypress.json", "vitest.json", "babel.json", "jest.json", "mocha.json", "bunfig.toml", ".npmrc", ".yarnrc", ".yarnrc.yml" },
            new[] { ".mocharc", "jest.", "vitest.", "playwright.", "karma.", "babel.config.", ".babelrc", "ava.config.", ".nycrc", ".c8rc", "cypress.config." },
            Array.Empty<string>(), new JsImportResolver(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".js"] = "\nthrow new Error('soz-kesin-kalan');\n", [".cjs"] = "\nthrow new Error('soz-kesin-kalan');\n", [".mjs"] = "\nthrow new Error('soz-kesin-kalan');\n",
                [".ts"] = "\nthrow new Error('soz-kesin-kalan');\n", [".jsx"] = "\nthrow new Error('soz-kesin-kalan');\n", [".tsx"] = "\nthrow new Error('soz-kesin-kalan');\n",
                [".mts"] = "\nthrow new Error('soz-kesin-kalan');\n", [".cts"] = "\nthrow new Error('soz-kesin-kalan');\n",
            }),
        new("python", new[] { "conftest.py", "sitecustomize.py", "usercustomize.py", "setup.py", "noxfile.py", "pytest.ini", "tox.ini", "setup.cfg", "pyproject.toml", ".coveragerc" },
            Array.Empty<string>(), Array.Empty<string>(), null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [".py"] = "\nraise RuntimeError('soz-kesin-kalan')\n" }),
        new("go", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [".go"] = "\nfunc init() { panic(\"soz-kesin-kalan\") }\n" }),
        new("dotnet-java-ruby", new[] { "xunit.runner.json", ".rspec" }, Array.Empty<string>(), new[] { ".runsettings" }, null, new Dictionary<string, string>()),
    };

    /// <summary>Dosya adı (küçük harfe çevrilir) bir koşucunun kendiliğinden okuyabileceği ayar adı mı? package.json ve tsconfig derleme ayarıdır: çağıran ayrıca karar verir.</summary>
    public static bool IsConfigName(string rel)
    {
        var name = Path.GetFileName(rel.Replace('\\', '/')).ToLowerInvariant();
        foreach (var d in All)
        {
            if (d.ConfigNames.Contains(name)) return true;
            foreach (var p in d.ConfigPrefixes) if (name.StartsWith(p, StringComparison.Ordinal)) return true;
            foreach (var s in d.ConfigSuffixes) if (name.EndsWith(s, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static IImportResolver? ResolverFor(string rel)
    {
        foreach (var d in All) if (d.Imports is { } r && r.Handles(rel)) return r;
        return null;
    }

    /// <summary>Test dosyasının sonuna eklenince dosyayı KESİN kalan yapan satır (dilin tanımından). Tanım yoksa null: bu dosya davranışla sınanamaz.</summary>
    public static string? FailAppendFor(string rel)
    {
        var ext = Path.GetExtension(rel);
        foreach (var d in All) if (d.FailAppend.TryGetValue(ext, out var s)) return s;
        return null;
    }
}

/// <summary>JS/TS içe aktarma çözümleyicisi: require, import, export from, dinamik import() (sabit dizgeyle), jest/vi mock çağrıları.</summary>
public sealed class JsImportResolver : IImportResolver
{
    private static readonly string[] Exts = { ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".mts", ".cts", ".json" };
    private static readonly string[] JsExts = { ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".mts", ".cts" };

    public static bool IsJsFile(string rel) => JsExts.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase);

    public bool Handles(string rel) => IsJsFile(rel);

    private static readonly Regex Req = new(@"(?:(?<![\w$.])|(?<=\.\.\.))require\s*\(\s*(?<q>['""`])(?<s>[^'""`\r\n]*)\k<q>\s*\)", RegexSpeed.Compiled);
    private static readonly Regex ReqResolve = new(@"(?<![\w$])require\.resolve\s*\(\s*(?<q>['""`])(?<s>[^'""`\r\n]*)\k<q>", RegexSpeed.Compiled);
    private static readonly Regex Imp = new(@"(?<![\w$.])import\s*(?:[\w$*{}\s,]+?\s*from\s*)?(?<q>['""])(?<s>[^'""\r\n]+)\k<q>", RegexSpeed.Compiled);
    private static readonly Regex Dyn = new(@"(?<![\w$.])import\s*\(\s*(?<q>['""`])(?<s>[^'""`\r\n]*)\k<q>\s*\)", RegexSpeed.Compiled);
    private static readonly Regex ExpFrom = new(@"(?<![\w$.])export\s+(?:\*(?:\s+as\s+[\w$]+)?|\{[^}]*\})\s*from\s*(?<q>['""])(?<s>[^'""\r\n]+)\k<q>", RegexSpeed.Compiled);
    private static readonly Regex Mock = new(@"(?<![\w$])(?:jest|vi)\.(?:mock|doMock|unmock|requireActual|requireMock|importActual|importMock)\s*\(\s*(?<q>['""`])(?<s>[^'""`\r\n]*)\k<q>", RegexSpeed.Compiled);
    private static readonly Regex NonLiteral = new(@"(?<![\w$.])(?:require|import)\s*\(\s*(?:(?!['""`\s)])|(?<q>['""`])[^'""`\r\n]*\k<q>\s*\+)", RegexSpeed.Compiled);

    /// <summary>Test ve iddia kütüphaneleri (koşucu tanımının parçası: bir dosya bunlardan birini çağırıyorsa "iddia kurabilen" dosyadır).</summary>
    private static readonly HashSet<string> Frameworks = new(StringComparer.Ordinal)
    {
        "test", "assert", "chai", "expect", "jest", "vitest", "mocha", "jasmine", "ava", "tap", "tape", "sinon", "should", "supertest", "uvu", "qunit", "power-assert", "unexpected",
    };
    private static readonly string[] FrameworkPrefixes = { "@jest/", "@testing-library/", "@vitest/", "@playwright/", "jest-", "chai-", "sinon-", "@sinonjs/" };

    private static bool IsFramework(string spec)
    {
        var s = spec.StartsWith("node:", StringComparison.Ordinal) ? spec[5..] : spec;
        var first = s.Split('/')[0];
        if (Frameworks.Contains(first)) return true;
        foreach (var p in FrameworkPrefixes) if (s.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    private static IEnumerable<(string spec, bool tmpl)> Specs(string text)
    {
        foreach (var rx in new[] { Req, ReqResolve, Imp, Dyn, ExpFrom, Mock })
            foreach (Match m in rx.Matches(text))
                yield return (m.Groups["s"].Value, m.Groups["q"].Value == "`");
    }

    public ImportInfo Scan(string rel, string text)
    {
        var relative = new List<string>(); var fw = false; var unresolved = NonLiteral.IsMatch(text);
        foreach (var (spec, tmpl) in Specs(text))
        {
            if (tmpl && spec.Contains("${", StringComparison.Ordinal)) { unresolved = true; continue; }
            if (spec.StartsWith('.')) { relative.Add(spec); continue; }
            if (spec.StartsWith("@/", StringComparison.Ordinal) || spec.StartsWith("~/", StringComparison.Ordinal) || spec.StartsWith('#')) { unresolved = true; continue; }
            if (IsFramework(spec)) fw = true;
        }
        return new ImportInfo(relative, fw, unresolved);
    }

    public string? Resolve(string fromRel, string spec, Func<string, bool> exists)
    {
        if (!spec.StartsWith("./", StringComparison.Ordinal) && !spec.StartsWith("../", StringComparison.Ordinal) && spec != "." && spec != "..") return null;
        var dir = fromRel.Replace('\\', '/');
        var slash = dir.LastIndexOf('/');
        dir = slash < 0 ? "" : dir[..slash];
        var parts = new List<string>(dir.Length == 0 ? Array.Empty<string>() : dir.Split('/'));
        foreach (var seg in spec.Split('/'))
        {
            if (seg == "" || seg == ".") continue;
            if (seg == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        var basePath = string.Join('/', parts);
        if (basePath.Length == 0) return null;
        if (exists(basePath)) return basePath;
        // TS usulü: ./x.js yazılır, dosya x.ts'dir.
        var ext = Path.GetExtension(basePath);
        if (ext is ".js" or ".mjs" or ".cjs" or ".jsx")
        {
            var stem = basePath[..^ext.Length];
            foreach (var e in new[] { ".ts", ".mts", ".cts", ".tsx" }) if (exists(stem + e)) return stem + e;
        }
        foreach (var e in Exts) if (exists(basePath + e)) return basePath + e;
        foreach (var e in Exts) if (exists(basePath + "/index" + e)) return basePath + "/index" + e;
        return null;
    }
}
