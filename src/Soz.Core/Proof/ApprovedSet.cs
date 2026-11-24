using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soz.Core.Proof;

/// <summary>
/// ONAYLI SINAMA KÜMESİ (e2). Yargıç, kişinin onayladığı testlerdir; ajanın düzenlediği testler değil.
/// Küme, projenin onaylı tabanındaki (defs_json) şu dosyalardır: test dosyaları, test yardımcıları ve fixture'lar (sınıf "t"),
/// koşucu ayar dosyaları (jest.config, .mocharc, pytest.ini, conftest.py, tox.ini...), ölçüt komutunun çağırdığı dosyalar (tools/kos.js gibi)
/// ve package.json'ın test ile ilgili alanları (scripts, type, main, imports, exports, test çerçevesi blokları).
/// Dosya kümeleri dile bağlı değildir: aynı sınıflama (IsTestFile, IsRunnerConfig) Node, Python, Go, Java ve C# adlarını birlikte tanır.
/// Bu sınıf yalnız SORU cevaplar (hangi dosya kümede, ne değişti); dosya takası <see cref="VerdictRun"/>'dadır.
/// Yardımcı dosyalar (test klasörü dışında bile) ADLARINA göre değil YAPILARINA göre bulunur (SupportFinder): onaylı testlerin içe aktardığı, test olmayan hiçbir kaynağın içe aktarmadığı ve
/// iddia kütüphanesi çağıran dosyalar kümeye girer. Ad listesi yalnız yedek ipucudur (aşağıdaki SupportStems).
/// Bilinen sınırlar: hem test hem kaynak tarafından kullanılan dosya kaynaktır; iddia çağırmayan saf yardımcı (hesap, veri üreten) kaynak sayılır; bu turda yalnız JS/TS içe aktarmaları çözülür.
/// </summary>
public static class ApprovedSet
{
    /// <summary>YEDEK İPUCU: içe aktarma grafiğiyle bulunamayan dillerde, test klasörü dışında bile test yardımcısı sayılan dosya adları (uzantısız, küçük harf).</summary>
    private static readonly HashSet<string> SupportStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "assert", "assertions", "matchers", "test-utils", "testutils", "test-utility", "test-helpers", "testhelpers", "test-helper", "testhelper",
        "setuptests", "setup-tests", "test-setup", "testsetup", "jest-setup", "jest.setup", "vitest.setup", "harness", "test-harness",
    };

    /// <summary>package.json içinde sınamayı belirleyen alanlar: hüküm koşusunda onaylı hâliyle geri konur.</summary>
    public static readonly IReadOnlyList<string> PackageTestKeys = new[]
    {
        "scripts", "type", "main", "imports", "exports", "jest", "mocha", "ava", "vitest", "nyc", "c8",
    };

    /// <summary>Test klasörü dışında duran bir test yardımcısı mı (adı: assert, matchers, test-utils...)?</summary>
    public static bool IsTestSupport(string rel)
    {
        var file = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(file)) return false;
        var dot = file.LastIndexOf('.');
        var stem = dot > 0 ? file[..dot] : file;
        return SupportStems.Contains(stem) || SupportStems.Contains(file);
    }

    /// <summary>
    /// Koşucunun kendiliğinden yüklediği ayar dosyası mı? (jest.config, .mocharc, vitest.config, playwright.config, karma.conf, babel.config, conftest.py, pytest.ini, tox.ini,
    /// kökte araç adını gölgeleyen pytest.py gibi dosyalar.) tsconfig ve setup.py bir derleme ayarıdır, test koşucusu değildir: kümeye girmez.
    /// </summary>
    public static bool IsRunnerConfig(string rel)
    {
        if (!EvidenceBase.IsAutoLoaded(rel)) return false;
        var name = Path.GetFileName(rel.Replace('\\', '/')).ToLowerInvariant();
        if (name.StartsWith("tsconfig", StringComparison.Ordinal) || name == "setup.py") return false;
        return true;
    }

    private static bool IsExcluded(string rel)
    {
        var norm = rel.Replace('\\', '/');
        var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && (parts[0].Equals(".claude", StringComparison.OrdinalIgnoreCase) || parts[0].Equals(".git", StringComparison.OrdinalIgnoreCase))) return true;
        if (Soz.Core.Restore.SirKutusu.IsSecretFile(norm)) return true; // sır dosyaları geçici kopyaya hiç yazılmaz
        return false;
    }

    private static bool IsPkg(string rel) => Path.GetFileName(rel.Replace('\\', '/')).Equals("package.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bu kayıt onaylı sınama kümesinin üyesi mi? snap: kaydın ait olduğu görüntü (ölçütün çağırdığı dosyaların listesi onda durur).</summary>
    public static bool IsMember(EvidenceBase.Entry e, EvidenceBase.Snapshot snap)
    {
        if (IsExcluded(e.Path)) return false;
        if (e.Cls == EvidenceBase.ClsTest) return true;
        if (e.Cls != EvidenceBase.ClsConfig) return false;
        return IsPkg(e.Path) || IsRunnerConfig(e.Path) || (snap.Referenced?.Contains(e.Path, StringComparer.Ordinal) ?? false);
    }

    /// <summary>Ajan YENİ eklediyse geçici kopyadan çıkarılan dosya mı? (Yeni test, test yardımcısı ve koşucu ayarı; ölçütün çağırdığı yeni dosyalar kümede olamaz.)</summary>
    public static bool RemoveWhenAdded(EvidenceBase.Entry e) => !IsExcluded(e.Path) && (e.Cls == EvidenceBase.ClsTest || (e.Cls == EvidenceBase.ClsConfig && IsRunnerConfig(e.Path)));

    public static IEnumerable<EvidenceBase.Entry> Members(EvidenceBase.Snapshot s) => s.Suspects.Where(e => IsMember(e, s));

    /// <summary>Üye dosyalarda iki görüntü arasındaki fark (yollar köke göre, / ile).</summary>
    public sealed record MemberChanges(List<string> Edited, List<string> Removed, List<string> Added)
    {
        public bool Any => Edited.Count + Removed.Count + Added.Count > 0;
        public static MemberChanges None => new(new(), new(), new());
        public IEnumerable<string> All => Edited.Concat(Removed).Concat(Added);
    }

    /// <summary>
    /// "before" (onaylı ya da iş başı) ile "now" arasında kümedeki dosyaların farkı. Düzenlenen: özeti değişen (package.json için yalnız test alanları değişen).
    /// Silinen: before'da var now'da yok. Eklenen: now'da var (üye olabilecek türde), before'da yok.
    /// </summary>
    public static MemberChanges Changes(EvidenceBase.Snapshot before, EvidenceBase.Snapshot now)
    {
        var nowMap = now.Suspects.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var beforeMap = before.Suspects.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var res = MemberChanges.None;
        // Söz'ün kendi koşularının ürettiği dosyalar (log, rapor...) ajan değişikliği sayılmaz.
        var produced = new HashSet<string>((before.Produced ?? new()).Concat(now.Produced ?? new()), StringComparer.Ordinal);
        foreach (var b in before.Suspects)
        {
            if (!IsMember(b, before) || produced.Contains(b.Path)) continue;
            if (!nowMap.TryGetValue(b.Path, out var n)) { res.Removed.Add(b.Path); continue; }
            if (n.Sha == b.Sha) continue;
            if (IsPkg(b.Path) && b.Extra is not null && n.Extra is not null && !PackageTestKeysDiffer(b.Extra, n.Extra)) continue; // package.json'ın sınamayla ilgisiz alanı değişti
            res.Edited.Add(b.Path);
        }
        foreach (var n in now.Suspects)
            if (!beforeMap.ContainsKey(n.Path) && !produced.Contains(n.Path) && RemoveWhenAdded(n)) res.Added.Add(n.Path);
        return res;
    }

    private static JsonObject? ParsePkg(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>İki package.json metninin sınamayla ilgili alanları (PackageTestKeys) farklı mı? Okunamayan metin farklı sayılır (güvenli yön).</summary>
    public static bool PackageTestKeysDiffer(string beforeText, string nowText)
    {
        var a = ParsePkg(beforeText); var b = ParsePkg(nowText);
        if (a is null || b is null) return true;
        foreach (var k in PackageTestKeys.Where(k => k != "scripts"))
            if (!JsonNode.DeepEquals(a[k], b[k])) return true;
        if ((a["scripts"] is not null && a["scripts"] is not JsonObject) || (b["scripts"] is not null && b["scripts"] is not JsonObject))
            return !JsonNode.DeepEquals(a["scripts"], b["scripts"]);
        foreach (var name in TestScripts(a).Union(TestScripts(b)))
            if (!JsonNode.DeepEquals((a["scripts"] as JsonObject)?[name], (b["scripts"] as JsonObject)?[name])) return true;
        return false;
    }

    private static HashSet<string> TestScripts(JsonObject pkg)
    {
        var scripts = (pkg["scripts"] as JsonObject)?.Where(k => k.Value is JsonValue v && v.TryGetValue<string>(out _))
            .ToDictionary(k => k.Key, k => k.Value!.GetValue<string>(), StringComparer.Ordinal) ?? new Dictionary<string, string>();
        var result = new HashSet<string>(StringComparer.Ordinal) { "test" };
        var queue = new Queue<string>(); queue.Enqueue("test");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var name))
        {
            if (!seen.Add(name)) continue;
            result.Add(name);
            if (scripts.TryGetValue(name, out var cmd))
                foreach (var called in ScanContext.ScriptCalls(cmd, scripts)) queue.Enqueue(called);
            foreach (var lifecycle in new[] { "pre" + name, "post" + name })
                if (scripts.ContainsKey(lifecycle)) queue.Enqueue(lifecycle);
        }
        return result;
    }

    /// <summary>
    /// Ajanın package.json'ında test alanlarını onaylı hâline çevirir; bağımlılıklar gibi öteki alanlar ajanın yazdığı gibi kalır (yeni kod onlara ihtiyaç duyabilir).
    /// current okunamazsa onaylı metin aynen döner. Dönen metin geçici kopyaya yazılır.
    /// </summary>
    public static string MergePackage(string approvedText, string? currentText)
    {
        var a = ParsePkg(approvedText);
        var c = ParsePkg(currentText);
        if (a is null) return currentText ?? approvedText;
        if (c is null) return approvedText;
        foreach (var k in PackageTestKeys)
        {
            if (k == "scripts" && (a[k] is null or JsonObject) && (c[k] is null or JsonObject))
            {
                var dest = c[k] as JsonObject ?? new JsonObject();
                var original = a[k] as JsonObject;
                foreach (var name in TestScripts(a).Union(TestScripts(c)))
                {
                    if (original?[name] is { } value) dest[name] = value.DeepClone();
                    else dest.Remove(name);
                }
                if (dest.Count > 0 || a[k] is JsonObject || c[k] is JsonObject) c[k] = dest;
                continue;
            }
            if (a[k] is { } v) c[k] = v.DeepClone();
            else c.Remove(k);
        }
        return c.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
