using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soz.Core.Sessions.Ozet;

/// <summary>
/// Deneme (test) komutu tanıma. Kurallar TEK dosyadadır: <c>Sessions/Ozet/deneme-komutlari.json</c> (gömülü kaynak). Yeni araç eklemek o dosyaya bir satır eklemektir.
/// Dönen değer yalnız kuralın sabit adıdır ("npm test"); komutun kendisi, argümanları ve olası parolalar özete hiç çıkmaz.
/// </summary>
public static class DenemeKurallari
{
    public sealed record Kural(string Ad, Regex Desen, Regex? Degil);

    private static readonly Lazy<(IReadOnlyList<Kural> kurallar, HashSet<string> okuyucular)> Yuklu = new(Yukle);

    public static IReadOnlyList<Kural> Kurallar => Yuklu.Value.kurallar;

    private static readonly Regex Ayirici = new(@"&&|\|\||[;|\n\r]", RegexOptions.CultureInvariant);
    private static readonly Regex OrtamAtamasi = new(@"^(?:[A-Za-z_][A-Za-z0-9_]*=(?:""[^""]*""|'[^']*'|\S*)\s+)+", RegexOptions.CultureInvariant);

    /// <summary>Komut bir deneme komutuysa kuralın adı, değilse null. Komut parçalara bölünür; okuyucu komutla (grep, cat, ...) başlayan parça sayılmaz.</summary>
    public static string? Tani(string? komut)
    {
        if (string.IsNullOrWhiteSpace(komut)) return null;
        var (kurallar, okuyucular) = Yuklu.Value;
        foreach (var hamParca in Ayirici.Split(komut))
        {
            var parca = OrtamAtamasi.Replace(hamParca.Trim().TrimStart('(', '{', '&', ' '), "");
            if (parca.Length == 0) continue;
            var ilk = IlkSozcuk(parca);
            if (okuyucular.Contains(ilk)) continue;
            foreach (var k in kurallar)
            {
                if (!k.Desen.IsMatch(parca)) continue;
                if (k.Degil is not null && k.Degil.IsMatch(parca)) continue;
                return k.Ad;
            }
        }
        return null;
    }

    /// <summary>Parçanın ilk sözcüğü: tırnaksız, yolsuz, uzantısız, küçük harf ("C:\x\Get-Content.exe" → "get-content").</summary>
    private static string IlkSozcuk(string parca)
    {
        var s = parca.TrimStart('"', '\'');
        var son = s.IndexOfAny(new[] { ' ', '\t', '"', '\'' });
        var w = son < 0 ? s : s[..son];
        var i = Math.Max(w.LastIndexOf('/'), w.LastIndexOf('\\'));
        if (i >= 0) w = w[(i + 1)..];
        foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            if (w.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) w = w[..^ext.Length];
        return w.ToLowerInvariant();
    }

    private static (IReadOnlyList<Kural>, HashSet<string>) Yukle()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Soz.Core.deneme-komutlari.json")
                      ?? throw new InvalidOperationException("deneme-komutlari.json gömülü kaynağı yok.");
        using var doc = JsonDocument.Parse(s);
        var list = new List<Kural>();
        const RegexOptions o = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        foreach (var k in doc.RootElement.GetProperty("kurallar").EnumerateArray())
        {
            var ad = k.GetProperty("ad").GetString() ?? throw new InvalidDataException("kural adı boş");
            var desen = new Regex(k.GetProperty("desen").GetString()!, o | RegexSpeed.Compiled, TimeSpan.FromMilliseconds(250));
            Regex? degil = k.TryGetProperty("degil", out var d) && d.GetString() is { Length: > 0 } ds ? new Regex(ds, o, TimeSpan.FromMilliseconds(250)) : null;
            list.Add(new Kural(ad, desen, degil));
        }
        var okuyucular = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("okuyucuKomutlar", out var ok))
            foreach (var x in ok.EnumerateArray()) if (x.GetString() is { Length: > 0 } w) okuyucular.Add(w);
        return (list, okuyucular);
    }
}
