using System.Text;
using System.Text.RegularExpressions;
using Soz.Core.Text;

namespace Soz.Core.Proof;

/// <summary>
/// E2: BU SINIF ARTIK HÜKÜM VERMEZ. Metne bakarak niyet okunmaz (bağımsız denetim: noktalı virgül, tırnak, yorum, describe sarması dürüst işte yanlış alarm;
/// if (false), ortam değişkeniyle atlama, sahteleme gibi yalnız ekleyen hileler kaçak). Hüküm, onaylı testlerin yeni kodla koşusudur (<see cref="VerdictRun"/>).
/// Burada kalan iş yalnız karttaki "ajan testlerde ne değiştirdi" özetidir: <c>Result.Describe</c> ve <c>Result.Lines</c>. Clean alanı da yalnız bu özet
/// ve "silinen test başka yerde yeniden ortaya çıktı mı" (yeniden adlandırma notu) içindir, hiçbir yerde alarm kapısı değildir.
///
/// Eski açıklama (denetim 5, B1): var olan bir test dosyasının değişikliğini SINAMANIN GÜCÜ açısından okur. "sınama değişti, kanıt sayamam" yalnız sınamanın gücü azalabilecekse çıkardı.
/// Alarm (Clean=false): var olan bir satır silindi ya da değişti (beklenen değer, iddia, çağrı), ya da testi atlayan / erken çıkaran bir satır eklendi (skip, only, process.exit, boş catch).
/// Alarm değil (Clean=true, yalnız not): satır EKLENDİ (silinen ya da değişen yok), yalnız boşluk ve biçim değişti, satırlar yer değiştirdi,
/// testin yalnız ADI değişti (gövde aynı), yalnız göreli içe aktarma yolu güncellendi (hedef dosya gerçekten var ve eski hedef artık yok ya da aynı dosyaya çıkıyor).
/// Hiçbir komut çalıştırılmaz, yalnız iki metin karşılaştırılır.
/// </summary>
public static class TestFileDiff
{
    public sealed record Result(bool Clean, string? Why, string? Note, List<EvidenceBase.DiffLine> Lines)
    {
        /// <summary>Kartta gösterilecek kısa açıklama (ne değişti).</summary>
        public string Describe => (Clean ? Note : Why) ?? "içerik değişti";
    }

    private const int MaxCells = 4_000_000;

    private static readonly Regex Spaces = new(@"\s+", RegexSpeed.Compiled);

    /// <summary>Testi atlayan, yalnız bir kısmını çalıştıran ya da sınamadan erken çıkaran satır kalıpları (eklenen satırlarda aranır).</summary>
    private static readonly Regex Danger = new(
        @"\.(?:skip|only|todo)\b|\b(?:xit|xdescribe|xtest|fit|fdescribe|ftest)\s*\(|process\s*\.\s*exit\b|\b(?:sys|os)\s*\.\s*_?exit\s*\(|\bos\.Exit\s*\(|\bexit\s*\(\s*0?\s*\)|^\s*return\s*;?\s*$"
        + @"|pytest\.mark\.skip|pytest\.skip|unittest\.skip|SkipTest|\bt\.Skip\w*\(|@Disabled|@Ignore\b|\[Ignore\]|\[Fact\s*\(\s*Skip|\bskip\s*[:=]\s*true"
        + @"|catch\s*(?:\([^)]*\))?\s*\{\s*\}|\.catch\s*\(\s*\(\s*\w*\s*\)\s*=>\s*\{\s*\}\s*\)"
        + @"|^(?!(?:const|let|var)\b)(?:assert|expect|t)(?:\s*\.\s*\w+)?\s*=(?!=)|^/\*(?!\*)(?!.*\*/)"
        + @"|\bassert\b[^\n]*\.(?:equal|strictEqual|deepEqual|deepStrictEqual|ok|notEqual|throws)\s*=(?!=)|\)\s*\.(?:equal|strictEqual|deepEqual|deepStrictEqual|ok|notEqual|throws)\s*=(?!=)",
        RegexOptions.CultureInvariant | RegexSpeed.Compiled);

    // test adı: test('ad', ...), it("ad", ...), describe.skip(`ad`, ...)
    private static readonly Regex JsName = new(@"^(?<pre>(?:await\s+)?(?:test|it|describe|specify|suite|context)(?:\.\w+)*)\s*\(\s*(?<q>['""`])(?<name>.*?)\k<q>(?<post>.*)$", RegexSpeed.Compiled);
    private static readonly Regex PyName = new(@"^(?<pre>(?:async\s+)?def\s+)test\w*(?<post>\s*\(.*)$", RegexSpeed.Compiled);
    private static readonly Regex GoName = new(@"^(?<pre>func\s+)Test\w*(?<post>\s*\(.*)$", RegexSpeed.Compiled);

    // göreli içe aktarma: require('./x'), import a from "../x", import('./x'), export * from './x'
    private static readonly Regex JsImport = new(@"^(?<pre>.*?(?:require\s*\(\s*|\bfrom\s+|\bimport\s*\(?\s*))(?<q>['""])(?<spec>\.{1,2}(?:/[^'""]*)?)\k<q>(?<post>.*)$", RegexSpeed.Compiled);
    private static readonly Regex PyFrom = new(@"^from\s+(?<spec>\.*[\w.]*)\s+import\s+(?<post>.+)$", RegexSpeed.Compiled);
    private static readonly Regex PyImport = new(@"^import\s+(?<spec>[\w.]+)(?<post>.*)$", RegexSpeed.Compiled);

    private static string Norm(string s) => Spaces.Replace(s.Trim(), " ");

    private static List<(string norm, string raw)> Lines(string text)
    {
        var res = new List<(string, string)>();
        foreach (var l in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var n = Norm(l);
            if (n.Length > 0) res.Add((n, l.Trim()));
        }
        return res;
    }

    private static string StripAll(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private static string Short(string s) => SecretFilter.MaskSecrets(s.Length > 70 ? s[..70] + "…" : s);

    /// <summary>
    /// Eski ve yeni metni karşılaştırır. oldPath ve newPath proje köküne göre (/ ile); root verilirse içe aktarma yolu güncellemesi dosya sisteminde doğrulanır, verilmezse sayılmaz.
    /// </summary>
    public static Result Analyze(string oldPath, string oldText, string newPath, string newText, string? root)
    {
        var a = Lines(oldText);
        var b = Lines(newText);
        var lines = new List<EvidenceBase.DiffLine>();

        if (StripAll(oldText) == StripAll(newText))
            return new Result(true, null, "yalnız boşluk ve biçim değişti", lines);

        // ortak baş ve son satırlar (LCS'yi küçültür)
        var p = 0;
        while (p < a.Count && p < b.Count && a[p].norm == b[p].norm) p++;
        var s = 0;
        while (s < a.Count - p && s < b.Count - p && a[a.Count - 1 - s].norm == b[b.Count - 1 - s].norm) s++;
        var ma = a.Skip(p).Take(a.Count - p - s).ToList();
        var mb = b.Skip(p).Take(b.Count - p - s).ToList();

        List<(string norm, string raw)> removed, added;
        if ((long)ma.Count * mb.Count > MaxCells)
        {
            // çok büyük değişiklik: ayrıntılı karşılaştırma yapılamaz, güvenli yön
            return new Result(false, $"çok sayıda satır değişti ({ma.Count} eski, {mb.Count} yeni), karşılaştırılamadı", null, EvidenceBase.LineDiff(oldText, newText));
        }
        Lcs(ma, mb, out removed, out added);

        // yer değiştirme: aynı satır iki yanda da var
        var addedLeft = new List<(string norm, string raw)>(added);
        var removedLeft = new List<(string norm, string raw)>();
        var moved = 0;
        foreach (var r in removed)
        {
            var i = addedLeft.FindIndex(x => x.norm == r.norm);
            if (i >= 0) { addedLeft.RemoveAt(i); moved++; }
            else removedLeft.Add(r);
        }

        var renamedTests = 0; var importFixes = 0;
        var unexplained = new List<(string norm, string raw)>();
        foreach (var r in removedLeft)
        {
            var nk = NameKey(r.norm);
            if (nk is not null)
            {
                var i = addedLeft.FindIndex(x => NameKey(x.norm) == nk);
                if (i >= 0) { addedLeft.RemoveAt(i); renamedTests++; continue; }
            }
            if (ImportParts(r.norm) is { } ri)
            {
                var i = addedLeft.FindIndex(x => ImportParts(x.norm) is { } ai && ai.template == ri.template && ai.spec != ri.spec && ImportMoved(oldPath, ri.spec, ri.lang, newPath, ai.spec, root));
                if (i >= 0) { addedLeft.RemoveAt(i); importFixes++; continue; }
            }
            unexplained.Add(r);
        }

        foreach (var r in unexplained.Take(MaxShown())) lines.Add(new EvidenceBase.DiffLine("-", Short(r.raw)));
        foreach (var x in addedLeft.Take(MaxShown())) lines.Add(new EvidenceBase.DiffLine("+", Short(x.raw)));

        if (unexplained.Count > 0)
        {
            var first = Short(unexplained[0].raw);
            var what = unexplained.Count == 1 ? $"1 var olan satır değişti ya da silindi: «{first}»" : $"{unexplained.Count} var olan satır değişti ya da silindi, ilki: «{first}»";
            return new Result(false, what, null, lines);
        }

        // eklenen satırlar testi zayıflatıyor mu?
        foreach (var x in b.Where(y => a.All(z => z.norm != y.norm)))
        {
            if (Danger.IsMatch(x.raw))
                return new Result(false, $"testi atlayan ya da erken çıkaran bir satır eklendi: «{Short(x.raw)}»", null, lines);
        }

        var parts = new List<string>();
        if (addedLeft.Count > 0) parts.Add($"{addedLeft.Count} yeni satır eklendi");
        if (renamedTests > 0) parts.Add("testin yalnız adı değişti");
        if (importFixes > 0) parts.Add("içe aktarma yolu güncellendi");
        if (moved > 0) parts.Add("satırlar yer değiştirdi");
        if (parts.Count == 0) parts.Add("yalnız biçim değişti");
        return new Result(true, null, string.Join(", ", parts), lines);
    }

    private static int MaxShown() => 4;

    private static void Lcs(List<(string norm, string raw)> a, List<(string norm, string raw)> b, out List<(string, string)> removed, out List<(string, string)> added)
    {
        var n = a.Count; var m = b.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = a[i].norm == b[j].norm ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        removed = new(); added = new();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x].norm == b[y].norm) { x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { removed.Add(a[x]); x++; }
            else { added.Add(b[y]); y++; }
        }
        while (x < n) removed.Add(a[x++]);
        while (y < m) added.Add(b[y++]);
    }

    /// <summary>Test bildirim satırı: ad çıkarılmış hâli. Aynı anahtar = yalnız ad değişti (skip, only gibi önekler anahtarda kalır).</summary>
    internal static string? NameKey(string line)
    {
        var m = JsName.Match(line);
        if (m.Success) return "js|" + m.Groups["pre"].Value + "|" + m.Groups["post"].Value;
        m = PyName.Match(line);
        if (m.Success) return "py|" + m.Groups["pre"].Value + "|" + m.Groups["post"].Value;
        m = GoName.Match(line);
        if (m.Success) return "go|" + m.Groups["pre"].Value + "|" + m.Groups["post"].Value;
        return null;
    }

    internal static (string template, string spec, string lang)? ImportParts(string line)
    {
        var m = JsImport.Match(line);
        if (m.Success) return (m.Groups["pre"].Value + "\u0001" + m.Groups["post"].Value, m.Groups["spec"].Value, "js");
        m = PyFrom.Match(line);
        if (m.Success) return ("from \u0001 import " + m.Groups["post"].Value, m.Groups["spec"].Value, "py");
        m = PyImport.Match(line);
        if (m.Success) return ("import \u0001" + m.Groups["post"].Value, m.Groups["spec"].Value, "py");
        return null;
    }

    private static readonly string[] JsExt = { "", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".mts", ".cts", ".json" };

    private static string? Resolve(string filePath, string spec, string lang, string root)
    {
        try
        {
            var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
            var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(rootFull, filePath.Replace('/', Path.DirectorySeparatorChar)))) ?? rootFull;
            string? Under(string full) => full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
            if (lang == "js")
            {
                var basePath = Path.GetFullPath(Path.Combine(dir, spec.Replace('/', Path.DirectorySeparatorChar)));
                foreach (var e in JsExt) if (File.Exists(basePath + e)) return Under(basePath + e);
                foreach (var e in JsExt.Skip(1)) if (File.Exists(Path.Combine(basePath, "index" + e))) return Under(Path.Combine(basePath, "index" + e));
                return null;
            }
            // python: baştaki noktalar bulunulan klasöre göre, nokta yoksa proje köküne ya da dosyanın klasörüne göre
            var dots = spec.TakeWhile(c => c == '.').Count();
            var rest = spec[dots..].Replace('.', Path.DirectorySeparatorChar);
            var bases = dots > 0 ? new[] { UpDirs(dir, dots - 1) } : new[] { rootFull, dir };
            foreach (var bse in bases)
            {
                var cand = Path.GetFullPath(Path.Combine(bse, rest));
                if (rest.Length > 0 && File.Exists(cand + ".py")) return Under(cand + ".py");
                if (File.Exists(Path.Combine(cand, "__init__.py"))) return Under(Path.Combine(cand, "__init__.py"));
            }
            return null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string UpDirs(string dir, int n)
    {
        for (var i = 0; i < n; i++) dir = Path.GetDirectoryName(dir) ?? dir;
        return dir;
    }

    /// <summary>İçe aktarma yolu güncellemesi güvenli mi? Yeni hedef gerçekten var olmalı; eski hedef artık yok (yeniden adlandırma) ya da yeni yolla aynı dosyaya çıkmalı (test taşındı).</summary>
    private static bool ImportMoved(string oldPath, string oldSpec, string lang, string newPath, string newSpec, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var newTarget = Resolve(newPath, newSpec, lang, root);
        if (newTarget is null) return false;
        var oldTarget = Resolve(oldPath, oldSpec, lang, root);
        return oldTarget is null || oldTarget.Equals(newTarget, StringComparison.OrdinalIgnoreCase);
    }
}
