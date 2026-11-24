using System.Text.Json;

namespace Soz.Capsule;

/// <summary>Kapsülün masaüstü programına (kabuk) söylediği şeyler.</summary>
/// <remarks>Gizli: kişi küçük pencereyi gizledi ("1" ya da açılınca "0", Metin alanında). Kabuk tepside "Küçük pencereyi göster" satırını buna göre gösterir.</remarks>
internal enum KomutTuru { Bilinmiyor, Panel, PanelKapat, YeniIs, Dikte, Paket, IceAl, Ayar, Metin, Cikis, Konum, Gizli }

/// <summary>Kapsülden kabuğa tek komut. Dik: kapsülün ekran konumu (piksel), panel kapsülün yanında açılsın diye. Hedef: yazı satırı açılmadan önce öndeki pencere.</summary>
internal readonly record struct KapsulKomutu(KomutTuru Tur, string? IsId = null, string? Metin = null, long Hedef = 0, KabukDik? Dik = null);

/// <summary>Ekran dikdörtgeni (piksel). Protokol kapsülün iç türlerine bağlı kalmasın diye ayrı.</summary>
internal readonly record struct KabukDik(double L, double T, double R, double B);

/// <summary>Kabuktan kapsüle tek olay.</summary>
/// <remarks>Goster: kişi tepsiden "Küçük pencereyi göster" dedi (gizliyse geri gelir, yazı satırı açılmaz).</remarks>
internal enum OlayTuru { Bilinmiyor, Flash, Kisayol, PanelKapandi, Proje, Kapat, Tema, DikteDurum, Goster }
internal readonly record struct KabukOlayi(OlayTuru Tur, string? Metin = null);

/// <summary>
/// Kapsül ile masaüstü programı arasındaki satır protokolü: kabuk kapsülü kendi alt süreci olarak başlatır, komutlar kapsülün standart
/// çıktısına, olaylar standart girdisine JSON satırı olarak gider (ek paket, soket ve adlandırılmış kanal yok). Saf ve ayrıdır: iki taraf da bunu kullanır.
/// Satır başına tek JSON nesnesi, UTF-8, "k" alanı türü söyler. Bozuk ya da bilinmeyen satır yok sayılır, çökertmez.
/// </summary>
internal static class KabukProtokol
{
    /// <summary>Bir satırın en çok uzunluğu (karakter). Yazı satırından gelen metin için üst sınır.</summary>
    public const int EnUzunSatir = 70000;

    public static readonly string[] DikteFazlari = { "dinliyor", "ceviriyor", "yazdi", "panoda", "kapali" };

    private static string Ad(KomutTuru t) => t switch
    {
        KomutTuru.Panel => "panel", KomutTuru.PanelKapat => "panel-kapat", KomutTuru.YeniIs => "yeni-is", KomutTuru.Dikte => "dikte",
        KomutTuru.Paket => "paket", KomutTuru.IceAl => "ice-al", KomutTuru.Ayar => "ayar", KomutTuru.Metin => "metin", KomutTuru.Cikis => "cikis", KomutTuru.Konum => "konum", KomutTuru.Gizli => "gizli",
        _ => "",
    };

    private static KomutTuru KomutAdi(string? s) => s switch
    {
        "panel" => KomutTuru.Panel, "panel-kapat" => KomutTuru.PanelKapat, "yeni-is" => KomutTuru.YeniIs, "dikte" => KomutTuru.Dikte,
        "paket" => KomutTuru.Paket, "ice-al" => KomutTuru.IceAl, "ayar" => KomutTuru.Ayar, "metin" => KomutTuru.Metin, "cikis" => KomutTuru.Cikis, "konum" => KomutTuru.Konum, "gizli" => KomutTuru.Gizli,
        _ => KomutTuru.Bilinmiyor,
    };

    private static string OlayAdi(OlayTuru t) => t switch
    {
        OlayTuru.Flash => "flash", OlayTuru.Kisayol => "kisayol", OlayTuru.PanelKapandi => "panel-kapandi",
        OlayTuru.Proje => "proje", OlayTuru.Kapat => "kapat", OlayTuru.Tema => "tema", OlayTuru.DikteDurum => "dikte", OlayTuru.Goster => "goster",
        _ => "",
    };

    private static OlayTuru OlayTuruOku(string? s) => s switch
    {
        "flash" => OlayTuru.Flash, "kisayol" => OlayTuru.Kisayol, "panel-kapandi" => OlayTuru.PanelKapandi,
        "proje" => OlayTuru.Proje, "kapat" => OlayTuru.Kapat, "tema" => OlayTuru.Tema, "goster" => OlayTuru.Goster,
        "dikte" or "dikte-durum" => OlayTuru.DikteDurum,
        _ => OlayTuru.Bilinmiyor,
    };

    /// <summary>Kabuktan kapsüle tema olayı ("tema\t&lt;aile&gt;\t&lt;kip&gt;" yüküyle).</summary>
    public static KabukOlayi TemaOlayi(string aile, string? kip = null)
        => new(OlayTuru.Tema, string.IsNullOrEmpty(kip) ? aile : $"{aile}\t{kip}");

    /// <summary>Tema olayının yükünü (aile, kip) olarak çözer.</summary>
    public static (string Aile, string Kip) TemaCoz(string? metin)
    {
        if (string.IsNullOrEmpty(metin)) return ("", "");
        var parcalar = metin.Split('\t');
        if (parcalar.Length >= 3 && parcalar[0] == "tema") return (parcalar[1].Trim(), parcalar[2].Trim());
        if (parcalar.Length == 2 && parcalar[0] == "tema") return (parcalar[1].Trim(), "");
        var i = metin.IndexOf('\t');
        return i < 0 ? (metin.Trim(), "") : (metin[..i].Trim(), metin[(i + 1)..].Split('\t')[0].Trim());
    }

    /// <summary>Kabuktan kapsüle dikte durum olayı ("dikte\t&lt;faz&gt;\t&lt;metin&gt;" yüküyle, fazlar: dinliyor | ceviriyor | yazdi | panoda | kapali).</summary>
    public static KabukOlayi DikteOlayi(string faz, string? metin = null)
        => new(OlayTuru.DikteDurum, metin == null ? faz : $"{faz}\t{metin}");

    /// <summary>Dikte olayının yükünü (faz, metin) olarak çözer.</summary>
    public static (string Faz, string Metin) DikteCoz(string? metin)
    {
        if (string.IsNullOrEmpty(metin)) return ("", "");
        var kalan = metin.StartsWith("dikte\t", StringComparison.Ordinal) ? metin[6..] : metin;
        var i = kalan.IndexOf('\t');
        var faz = (i < 0 ? kalan : kalan[..i]).Trim().ToLowerInvariant()
            .Replace('ç', 'c').Replace('ı', 'i').Replace('ş', 's').Replace('ö', 'o').Replace('ü', 'u').Replace('ğ', 'g');
        var ek = i < 0 ? "" : kalan[(i + 1)..].Trim();
        return (faz, ek);
    }

    // ------------------------------------------------------------ kapsül -> kabuk
    public static string Yaz(KapsulKomutu k)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("k", Ad(k.Tur));
            if (k.IsId != null) w.WriteString("is", k.IsId);
            if (k.Metin != null) w.WriteString("m", k.Metin);
            if (k.Hedef != 0) w.WriteNumber("hedef", k.Hedef);
            if (k.Dik is { } d) { w.WriteStartArray("dik"); w.WriteNumberValue((int)Math.Round(d.L)); w.WriteNumberValue((int)Math.Round(d.T)); w.WriteNumberValue((int)Math.Round(d.R)); w.WriteNumberValue((int)Math.Round(d.B)); w.WriteEndArray(); }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Bir komut satırını okur. Bozuk, bilinmeyen ya da çok uzun satır null verir.</summary>
    public static KapsulKomutu? KomutOku(string? satir)
    {
        if (!Nesne(satir, out var doc)) return null;
        using (doc)
        {
            var r = doc!.RootElement;
            var tur = KomutAdi(Metin(r, "k"));
            if (tur == KomutTuru.Bilinmiyor) return null;
            long hedef = r.TryGetProperty("hedef", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt64(out var hv) ? hv : 0;
            KabukDik? dik = null;
            if (r.TryGetProperty("dik", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() == 4)
            {
                var v = new double[4]; var i = 0; var ok = true;
                foreach (var e in a.EnumerateArray()) { if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var dv)) v[i++] = dv; else { ok = false; break; } }
                if (ok) dik = new KabukDik(v[0], v[1], v[2], v[3]);
            }
            return new KapsulKomutu(tur, Metin(r, "is"), Metin(r, "m"), hedef, dik);
        }
    }

    // ------------------------------------------------------------ kabuk -> kapsül
    public static string OlayYaz(KabukOlayi o)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("k", OlayAdi(o.Tur));
            if (o.Metin != null) w.WriteString("m", o.Metin);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public static KabukOlayi? OlayOku(string? satir)
    {
        satir = satir?.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(satir) || satir.Length > EnUzunSatir) return null;
        if (satir.StartsWith("tema\t", StringComparison.Ordinal))
            return new KabukOlayi(OlayTuru.Tema, satir[5..]);
        if (satir.StartsWith("dikte\t", StringComparison.Ordinal))
            return new KabukOlayi(OlayTuru.DikteDurum, satir[6..]);

        if (!Nesne(satir, out var doc)) return null;
        using (doc)
        {
            var r = doc!.RootElement;
            var tur = OlayTuruOku(Metin(r, "k"));
            if (tur == OlayTuru.Bilinmiyor) return null;
            var m = Metin(r, "m");
            if (m == null && tur == OlayTuru.Tema)
            {
                var aile = Metin(r, "aile"); var kip = Metin(r, "kip");
                if (aile != null) m = kip == null ? aile : $"{aile}\t{kip}";
            }
            else if (m == null && tur == OlayTuru.DikteDurum)
            {
                var faz = Metin(r, "faz"); var ek = Metin(r, "metin");
                if (faz != null) m = ek == null ? faz : $"{faz}\t{ek}";
            }
            return new KabukOlayi(tur, m);
        }
    }

    private static bool Nesne(string? satir, out JsonDocument? doc)
    {
        doc = null;
        satir = satir?.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(satir) || satir.Length > EnUzunSatir) return false;
        try
        {
            doc = JsonDocument.Parse(satir);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return true;
            doc.Dispose(); doc = null; return false;
        }
        catch (JsonException) { return false; }
    }

    private static string? Metin(JsonElement e, string ad) => e.TryGetProperty(ad, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ------------------------------------------------------------ kabuğun panelde açacağı adres
    /// <summary>
    /// Bir kimlikten panel adresi: "j:7" (sayılı iş) İşler sayfasında o işi açar ("#/is/7"). Oturum satırları "Şu An" sayfasını o oturumun satırını gösterecek biçimde açar:
    /// "x:&lt;yardımcıdaki kimlik&gt;" ve "o:12" için "#/suan?oturum=&lt;kimlik&gt;" (kimlik URL için kaçışlanır; arayüz bu adresi okuyup satırı görünür kılar ve vurgular),
    /// sunucunun "ask:ses:12:..." kimliği de aynı biçime (12) çevrilir. Kimlik yoksa, bozuksa ya da çok uzunsa düz "#/suan". Bağlantı satırı ("m:") ve "ext" kimliği düz "#/suan".
    /// Sayfa kimlikleri ("sayfa:suan", "sayfa:is") doğrudan o sayfayı, "sayfa:ac" ve kimlik yoksa null verir (panel olduğu yerde açılır). Öteki kimlikler İşler sayfasına gider.
    /// Dönüş yalnız izinli biçimlerdir (kabuk bunu adres çubuğuna yazar).
    /// </summary>
    public static string? PanelAdresi(string? isId)
    {
        if (string.IsNullOrEmpty(isId)) return null;
        if (isId.Length is > 2 and <= 12 && isId.StartsWith("j:", StringComparison.Ordinal) && isId.Skip(2).All(char.IsAsciiDigit)) return "#/is/" + isId[2..];
        switch (isId)
        {
            case "sayfa:suan": return "#/suan";
            case "sayfa:is": return "#/is";
            case "sayfa:ac": return null;
        }
        if (isId.StartsWith("x:", StringComparison.Ordinal)) return OturumAdresi(isId[2..]);
        if (isId.StartsWith("o:", StringComparison.Ordinal)) return OturumAdresi(isId[2..]);
        if (isId.Contains(":ses:", StringComparison.Ordinal))
        {
            var parca = isId.Split(':');
            var i = Array.IndexOf(parca, "ses");
            return OturumAdresi(i >= 0 && i + 1 < parca.Length ? parca[i + 1] : null);
        }
        if (isId.StartsWith("s:", StringComparison.Ordinal) || isId.StartsWith("m:", StringComparison.Ordinal) || isId.Contains(":ext:", StringComparison.Ordinal)) return "#/suan";
        return "#/is";
    }

    /// <summary>Kaçışlı oturum kimliğinin en çok uzunluğu (adres izinli biçimin sınırı). Daha uzun kimlik kesilmez, sade "#/suan" olur.</summary>
    public const int OturumKimligiEn = 200;
    private const string OturumOn = "#/suan?oturum=";

    /// <summary>"#/suan?oturum=&lt;kaçışlı kimlik&gt;". Kimlik boşsa, yalnız boşluktan oluşuyorsa, denetim karakteri taşıyorsa ya da kaçışlı hâli çok uzunsa "#/suan".</summary>
    public static string OturumAdresi(string? kimlik)
    {
        if (string.IsNullOrWhiteSpace(kimlik)) return "#/suan";
        var k = kimlik.Trim();
        if (k.Any(char.IsControl)) return "#/suan";
        var kacisli = Uri.EscapeDataString(k);
        return kacisli.Length > OturumKimligiEn ? "#/suan" : OturumOn + kacisli;
    }

    /// <summary>Kabuğun adres çubuğuna yazmaya razı olduğu biçimler. Kapsülden gelen adres yine de bu listeden geçer.</summary>
    public static bool AdresIzinli(string? a)
    {
        if (a is "#/suan" or "#/suan?yeni=1" or "#/is" or "#/is?yeni=1" or "#/ayarlar") return true;
        if (a != null && a.Length is > 5 and <= 16 && a.StartsWith("#/is/", StringComparison.Ordinal) && a.Skip(5).All(char.IsAsciiDigit)) return true;
        // "#/suan?oturum=<kaçışlı kimlik>": yalnız URL için güvenli karakterler ve %XX (betiğe ya da adrese başka bir şey sızmaz).
        return a != null && a.StartsWith(OturumOn, StringComparison.Ordinal) && a.Length > OturumOn.Length && a.Length - OturumOn.Length <= OturumKimligiEn
            && a.Skip(OturumOn.Length).All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '%');
    }

    /// <summary>
    /// Arayüze adresi yazan betik. Oturum adresi zaten açıksa tarayıcı yeni bir hashchange olayı üretmez ve satır yeniden vurgulanmaz:
    /// bu durumda önce düz sayfaya, sonra hedefe gidilir. Adres izinli değilse null.
    /// </summary>
    public static string? GezinmeBetigi(string? hash)
    {
        if (!AdresIzinli(hash)) return null;
        var js = System.Text.Json.JsonSerializer.Serialize(hash);
        return hash!.StartsWith(OturumOn, StringComparison.Ordinal) ? "(function(h){if(location.hash===h){location.hash='#/suan'}location.hash=h})(" + js + ")" : "location.hash=" + js;
    }

    // ------------------------------------------------------------ tek örnek
    /// <summary>
    /// Tek örnek kilidinin adı. Durum klasörü verilmişse (masaüstü programı kendi veri klasörünün altındakini verir) kilit o klasöre özeldir:
    /// kullanıcının kendi başına açtığı kapsül ile farklı veri klasörüyle çalışan bir başka masaüstü programı birbirini susturmaz.
    /// </summary>
    public static string TekOrnekAdi(string? durumKlasoru)
    {
        const string Taban = "Soz-Kapsul-Tek-Ornek";
        if (string.IsNullOrWhiteSpace(durumKlasoru)) return Taban;
        ulong h = 14695981039346656037UL;
        foreach (var c in Path.GetFullPath(durumKlasoru).TrimEnd('\\', '/').ToLowerInvariant()) { h ^= c; h *= 1099511628211UL; }
        return Taban + "-" + h.ToString("x16");
    }

    // ------------------------------------------------------------ sağ tık panosu
    /// <summary>Pano satırı: kimlik, metin, ayırıcı mı, işaretli mi.</summary>
    internal readonly record struct PanoOgesi(string Kimlik, string Metin, bool Ayirici = false, bool Isaretli = false, bool Baslik = false);

    private static string KanoniTemaAdi(string? tema)
    {
        if (string.IsNullOrWhiteSpace(tema)) return "cam";
        var s = tema.Trim().ToLowerInvariant()
            .Replace('ü', 'u').Replace('â', 'a').Replace('ı', 'i')
            .Replace('ö', 'o').Replace('ç', 'c').Replace('ş', 's').Replace('ğ', 'g');
        return s switch
        {
            "terminal" or "murekkep" => "terminal",
            "mat" => "mat",
            "ton" => "ton",
            "duz" => "duz",
            "sicak" or "kagit" => "sicak",
            _ => "cam",
        };
    }

    /// <summary>
    /// Sağ tık panosunun satırları. kabuk: kapsül masaüstü programıyla birlikte çalışıyor: yeni iş, dikte, paketi kopyala, şimdi içe al.
    /// Kabuk yoksa bu satırlar hiç görünmez (işe yaramayan düğme olmaz); "Ayarlar" da kabuk yokken görünmez, çünkü paneli açan kabuktur.
    /// Her madde ne yaptığını söyler: "Söz'ü kapat" bütün Söz'ü, "Küçük pencereyi kapat" yalnız tek başına çalışan küçük pencereyi kapatır.
    /// "Küçük pencereyi gizle" bir saat sonra kendiliğinden geri gelir. Altı tema ailesi "Görünüm" başlığı altında listelenir.
    /// </summary>
    /// <summary>"Küçük pencereyi gizle (1 saat)" etiketindeki süre: ayardaki gerçek değerden ("30 dakika", "1 saat", "90 dakika", "2 saat").</summary>
    public static string GizleSuresi(double ms)
    {
        var dk = (long)Math.Max(1, Math.Round(ms / 60000.0));
        return dk % 60 == 0 && dk >= 60 ? (dk / 60) + " saat" : dk + " dakika";
    }

    public static IReadOnlyList<PanoOgesi> Pano(bool kabuk, string tema, double gizleMs = 3600000)
    {
        var secili = KanoniTemaAdi(tema);
        var l = new List<PanoOgesi>();
        if (kabuk)
        {
            l.Add(new("yeniis", "Yeni iş")); l.Add(new("dikte", "Sesle yaz")); l.Add(new("paket", "Kurallarımı kopyala")); l.Add(new("icealma", "Kayıtları şimdi oku"));
            l.Add(new("a0", "", true));
        }
        l.Add(new("uyut", "Soluklaştır")); l.Add(new("gizle", $"Küçük pencereyi gizle ({GizleSuresi(gizleMs)})")); l.Add(new("sifirla", "Konumu sıfırla"));
        if (kabuk) l.Add(new("ayar", "Ayarlar"));
        l.Add(new("a1", "", true));
        l.Add(new("gorunum", "Görünüm", Baslik: true));
        l.Add(new("tema:cam", "Cam", false, secili == "cam"));
        l.Add(new("tema:terminal", "Terminal", false, secili == "terminal"));
        l.Add(new("tema:mat", "Mat", false, secili == "mat"));
        l.Add(new("tema:ton", "Ton", false, secili == "ton"));
        l.Add(new("tema:duz", "Düz", false, secili == "duz"));
        l.Add(new("tema:sicak", "Sıcak", false, secili == "sicak"));
        l.Add(new("a2", "", true)); l.Add(new("cikis", kabuk ? "Söz'ü kapat" : "Küçük pencereyi kapat"));
        return l;
    }

    /// <summary>Pano kimliğinin kabuğa giden komutu (kabuk komutu değilse Bilinmiyor).</summary>
    public static KomutTuru PanoKomutu(string kimlik) => kimlik switch
    {
        "yeniis" => KomutTuru.YeniIs, "dikte" => KomutTuru.Dikte, "paket" => KomutTuru.Paket, "icealma" => KomutTuru.IceAl, _ => KomutTuru.Bilinmiyor,
    };
}
