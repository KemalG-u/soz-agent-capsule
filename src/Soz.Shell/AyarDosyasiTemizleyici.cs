using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Hooks;

namespace Soz.Shell;

/// <summary>
/// Claude Code ayar dosyasından (<c>settings.json</c>, <c>settings.local.json</c>) YALNIZ Söz'ün kanca kayıtlarını çıkarır.
/// Dosya yeniden yazılmaz: kaldırılacak kayıtların bayt aralıkları bulunur ve kesilir, geri kalan her bayt (yorumlar, girinti,
/// anahtar sırası, satır sonları, BOM) aynen kalır. Değişiklikten önce zaman damgalı yedek alınır; yedeğin üstüne yazılmaz.
/// Sonuç doğrulanamazsa (geçerli JSON değil ya da Söz kaydı kaldı) dosyaya hiç dokunulmaz.
/// </summary>
internal static class AyarDosyasiTemizleyici
{
    internal sealed record Sonuc(int Kaldirilan, string? Yedek, string? Hata)
    {
        public bool Degisti => Kaldirilan > 0 && Hata == null;
    }

    private sealed class N
    {
        public int Start, End;
        public char Tur; // o: nesne, a: dizi, d: değer
        public List<M> Uyeler = new();
    }

    private sealed class M
    {
        public int Start; // nesnede anahtarın, dizide değerin başlangıcı
        public string? Anahtar;
        public N Deger = null!;
        public bool Silinecek;
    }

    private static readonly JsonReaderOptions Okuma = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static Sonuc Temizle(string dosya, Func<string>? zaman = null)
    {
        byte[] bayt;
        try { bayt = File.ReadAllBytes(dosya); }
        catch (Exception ex) { return new Sonuc(0, null, "okunamadı: " + ex.Message); }

        var (yeni, sayi, hata) = Kes(bayt);
        if (hata != null) return new Sonuc(0, null, hata);
        if (sayi == 0 || yeni == null) return new Sonuc(0, null, null);

        string yedek;
        try
        {
            var damga = (zaman ?? (() => DateTime.Now.ToString("yyyyMMdd-HHmmss")))();
            yedek = dosya + ".soz-yedek-" + damga;
            for (var i = 2; File.Exists(yedek); i++) yedek = dosya + ".soz-yedek-" + damga + "-" + i;
            File.WriteAllBytes(yedek, bayt);
            if (!File.ReadAllBytes(yedek).AsSpan().SequenceEqual(bayt)) return new Sonuc(0, null, "yedek doğrulanamadı, dosyaya dokunulmadı");
            var gecici = dosya + ".soz-gecici";
            File.WriteAllBytes(gecici, yeni);
            File.Move(gecici, dosya, overwrite: true);
        }
        catch (Exception ex) { return new Sonuc(0, null, "yazılamadı: " + ex.Message); }
        return new Sonuc(sayi, yedek, null);
    }

    /// <summary>Saf işlev (dosyaya dokunmaz): Söz kayıtları kesilmiş bayt dizisi ve kaldırılan kanca sayısı. Hata varsa metni.</summary>
    internal static (byte[]? Yeni, int Sayi, string? Hata) Kes(byte[] bayt)
    {
        N kok;
        try
        {
            var okuyucu = new Utf8JsonReader(bayt.AsSpan(BomUzunlugu(bayt)), Okuma);
            if (!okuyucu.Read() || okuyucu.TokenType != JsonTokenType.StartObject) return (null, 0, "dosya bir JSON nesnesi değil");
            kok = DegerOku(ref okuyucu, BomUzunlugu(bayt));
        }
        catch (JsonException ex) { return (null, 0, "geçerli JSON değil: " + ex.Message); }

        var sayi = 0;
        var hooks = kok.Uyeler.FirstOrDefault(m => m.Anahtar == "hooks");
        if (hooks?.Deger.Tur == 'o')
        {
            foreach (var olay in hooks.Deger.Uyeler)
            {
                if (olay.Deger.Tur != 'a') continue;
                foreach (var grup in olay.Deger.Uyeler)
                {
                    if (grup.Deger.Tur != 'o') continue;
                    var ic = grup.Deger.Uyeler.FirstOrDefault(m => m.Anahtar == "hooks");
                    if (ic?.Deger.Tur != 'a' || ic.Deger.Uyeler.Count == 0) continue;
                    foreach (var k in ic.Deger.Uyeler)
                    {
                        if (k.Deger.Tur != 'o') continue;
                        JsonNode? dugum;
                        try { dugum = JsonNode.Parse(bayt.AsMemory(k.Start, k.Deger.End - k.Start).Span, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
                        catch { continue; }
                        if (HookInstaller.IsOurs(dugum)) { k.Silinecek = true; sayi++; }
                    }
                    if (ic.Deger.Uyeler.All(m => m.Silinecek)) grup.Silinecek = true;
                }
                if (olay.Deger.Uyeler.Count > 0 && olay.Deger.Uyeler.All(m => m.Silinecek)) olay.Silinecek = true;
            }
            if (hooks.Deger.Uyeler.Count > 0 && hooks.Deger.Uyeler.All(m => m.Silinecek)) hooks.Silinecek = true;
        }
        if (sayi == 0) return (null, 0, null);

        var kesikler = new List<(int Bas, int Son)>();
        KesikleriTopla(kok, kesikler);
        kesikler.Sort((a, b) => a.Bas.CompareTo(b.Bas));
        var cikti = new List<byte>(bayt.Length);
        var konum = 0;
        foreach (var (bas, son) in kesikler)
        {
            if (bas < konum) return (null, 0, "kesik aralıkları çakıştı, dosyaya dokunulmadı");
            cikti.AddRange(bayt.AsSpan(konum, bas - konum).ToArray());
            konum = son;
        }
        cikti.AddRange(bayt.AsSpan(konum).ToArray());
        var yeni = cikti.ToArray();

        // Doğrulama: geçerli JSON, Söz kancası kalmadı, kesilenler dışında her bayt aynı.
        try
        {
            var okuyucu = new Utf8JsonReader(yeni.AsSpan(BomUzunlugu(yeni)), Okuma);
            if (!okuyucu.Read() || okuyucu.TokenType != JsonTokenType.StartObject) return (null, 0, "sonuç geçerli bir nesne değil");
            var kok2 = DegerOku(ref okuyucu, BomUzunlugu(yeni));
            var kalan = 0;
            foreach (var m in kok2.Uyeler.Where(m => m.Anahtar == "hooks" && m.Deger.Tur == 'o').SelectMany(m => m.Deger.Uyeler)
                         .Where(m => m.Deger.Tur == 'a').SelectMany(m => m.Deger.Uyeler).Where(m => m.Deger.Tur == 'o')
                         .SelectMany(m => m.Deger.Uyeler).Where(m => m.Anahtar == "hooks" && m.Deger.Tur == 'a').SelectMany(m => m.Deger.Uyeler))
            {
                var d = JsonNode.Parse(yeni.AsMemory(m.Start, m.Deger.End - m.Start).Span, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (HookInstaller.IsOurs(d)) kalan++;
            }
            if (kalan != 0) return (null, 0, "Söz kaydı kaldırılamadı, dosyaya dokunulmadı");
        }
        catch (JsonException ex) { return (null, 0, "sonuç geçerli JSON değil (" + ex.Message + "), dosyaya dokunulmadı"); }
        return (yeni, sayi, null);
    }

    private static void KesikleriTopla(N kutu, List<(int, int)> kesikler)
    {
        var u = kutu.Uyeler;
        var i = 0;
        while (i < u.Count)
        {
            if (!u[i].Silinecek)
            {
                KesikleriTopla(u[i].Deger, kesikler);
                i++;
                continue;
            }
            var a = i;
            while (i < u.Count && u[i].Silinecek) i++;
            var b = i - 1;
            if (b < u.Count - 1) kesikler.Add((u[a].Start, u[b + 1].Start));
            else if (a > 0) kesikler.Add((u[a - 1].Deger.End, u[b].Deger.End));
            else kesikler.Add((u[a].Start, u[b].Deger.End));
        }
    }

    // reader şu an bir değerin ilk belirtecinde.
    private static int BomUzunlugu(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;

    private static N DegerOku(ref Utf8JsonReader r, int o)
    {
        var n = new N { Start = o + (int)r.TokenStartIndex };
        switch (r.TokenType)
        {
            case JsonTokenType.StartObject:
                n.Tur = 'o';
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    var m = new M { Start = o + (int)r.TokenStartIndex, Anahtar = r.GetString() };
                    if (!r.Read()) throw new JsonException("beklenmedik son");
                    m.Deger = DegerOku(ref r, o);
                    n.Uyeler.Add(m);
                }
                n.End = o + (int)r.BytesConsumed;
                break;
            case JsonTokenType.StartArray:
                n.Tur = 'a';
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    var d = DegerOku(ref r, o);
                    n.Uyeler.Add(new M { Start = d.Start, Deger = d });
                }
                n.End = o + (int)r.BytesConsumed;
                break;
            default:
                n.Tur = 'd';
                n.End = o + (int)r.BytesConsumed;
                break;
        }
        return n;
    }
}
