namespace Soz.Capsule;

/// <summary>Alfa, kırmızı, yeşil, mavi (0-255). Düz sayı, çizim katmanına bağımlılık yok.</summary>
internal readonly record struct Renk(byte A, byte R, byte G, byte B)
{
    public static Renk Hex(uint rgb, double alfa = 1) => new((byte)Math.Round(Math.Clamp(alfa, 0, 1) * 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    public Renk Alfa(double a) => this with { A = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255) };
    /// <summary>Bir rengi diğeriyle t oranında karıştırır (alfa dahil).</summary>
    public static Renk Karistir(Renk a, Renk b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return new Renk(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
    /// <summary>Göreli parlaklık, 0 ile 1 arası (sRGB, yaklaşma ve karşıtlık denetimi için).</summary>
    public double Parlaklik => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;
}

/// <summary>Durum rengi sınıfı: iş türü ile eşleşir.</summary>
internal enum DurumSinifi { Yok, Bitti, Karar, Kaldi, Soru, Calisiyor, Dikte, Tamam, Belirsiz }

/// <summary>
/// Bir tema ailesinin bir zemin kipindeki (koyu ya da açık) bütün renk ve biçim değerleri.
/// Kaynak: calisma\tema-2\jetonlar.json içindeki 6 onaylı tema ailesi (cam, terminal, mat, ton, duz, sicak).
/// </summary>
internal sealed record TemaKip(
    string Ad, bool Koyu,
    Renk Govde, Renk ParlamaUst, Renk ParlamaAlt, Renk Kenar,
    Renk Yazi, Renk Yazi2, Renk Nokta, Renk NoktaKenar, Renk Vurgu,
    Renk Ok, Renk Dec, Renk Fail, Renk Ask, Renk Run, Renk Dic, Renk Tam,
    Renk PanelZemin, Renk PanelYazi, Renk PanelYazi2, Renk PanelCizgi, Renk PanelVurgu,
    double YaricapOrani, double YaricapEn, int YaziAgirligi, double CizgiKalinligi, double GolgeOlcegi)
{
    public Renk Durum(DurumSinifi s) => s switch
    {
        DurumSinifi.Bitti => Ok, DurumSinifi.Karar => Dec, DurumSinifi.Kaldi => Fail, DurumSinifi.Soru => Ask,
        DurumSinifi.Calisiyor => Run, DurumSinifi.Dikte => Dic, DurumSinifi.Tamam => Tam, DurumSinifi.Belirsiz => Tam, _ => Yazi,
    };

    /// <summary>Köşe yarıçapı (dp) yüksekliğe göre: cam, ton ve sıcak tam yuvarlak, mat yumuşak, terminal köşeli, düz dik.</summary>
    public double Yaricap(double yukseklik) => Math.Min(YaricapEn, yukseklik * YaricapOrani);
}

internal static class Temalar
{
    public static readonly string[] Aileler = { "cam", "terminal", "mat", "ton", "duz", "sicak" };

    private static Renk H(uint rgb, double a = 1) => Renk.Hex(rgb, a);
    private static readonly Renk Beyaz = H(0xFFFFFF);
    private static readonly Renk Yok = new(0, 0, 0, 0);

    // ---- 1. cam (serin buzlu mavi) ----
    private static readonly TemaKip CamAcik = new("cam", false,
        H(0xFFFFFF, .78), Beyaz.Alfa(.65), Beyaz.Alfa(.10), Beyaz.Alfa(.90),
        H(0x12161D), H(0x414B5E), H(0x0C5CC0), Beyaz.Alfa(.90), H(0x0B6CC4),
        H(0x076331), H(0x774D04), H(0x942C27), H(0x02598E), H(0x0B6CC4), H(0x9A1B6E), H(0x566077),
        H(0xF7F9FE, .94), H(0x12161D), H(0x414B5E), H(0x141E3C, .12), H(0x0B6CC4),
        .5, 999, 600, 1.8, 0.85);
    private static readonly TemaKip CamKoyu = new("cam", true,
        H(0x181C28, .78), Beyaz.Alfa(.14), Beyaz.Alfa(.02), Beyaz.Alfa(.22),
        H(0xF2F5FB), H(0xB4BDCF), H(0x58A6F2), H(0x000000, .75), H(0x6CC0FF),
        H(0x7FE69E), H(0xFFC781), H(0xFDC3BC), H(0xA9D6FD), H(0x6CC0FF), H(0xFF9FD8), H(0xAAB3C8),
        H(0x161A24, .92), H(0xF2F5FB), H(0xB4BDCF), Beyaz.Alfa(.13), H(0x6CC0FF),
        .5, 999, 600, 1.8, 1.0);

    // ---- 2. terminal (koyu yeşil ve kehribar net konsol) ----
    private static readonly TemaKip TerminalAcik = new("terminal", false,
        H(0xFBFBF8), Yok, Yok, H(0x131313),
        H(0x131313), H(0x58584F), H(0x1C1C1C), Beyaz.Alfa(.90), H(0x05773B),
        H(0x05773B), H(0x885703), H(0xA04038), H(0x0068A5), H(0x05773B), H(0x885703), H(0x58584F),
        H(0xFBFBF8), H(0x131313), H(0x58584F), H(0xD8D8D0), H(0x05773B),
        .15, 4, 700, 1.7, 0.5);
    private static readonly TemaKip TerminalKoyu = new("terminal", true,
        H(0x0E0F10), Yok, Yok, H(0x6D6C66),
        H(0xE9E8E1), H(0x9C9B93), H(0x58B575), H(0x000000, .80), H(0x58B575),
        H(0x58B575), H(0xD09033), H(0xE47C72), H(0x4FA6E9), H(0x58B575), H(0xD09033), H(0x9C9B93),
        H(0x0E0F10), H(0xE9E8E1), H(0x9C9B93), H(0x2C2D2F), H(0x58B575),
        .15, 4, 700, 1.7, 0.6);

    // ---- 3. mat (yumuşak taş gri) ----
    private static readonly TemaKip MatAcik = new("mat", false,
        H(0xF4F5F7), Yok, Yok, H(0xD6D9E0),
        H(0x16181D), H(0x505562), H(0x2B2E38), Beyaz.Alfa(.90), H(0x4B4FC9),
        H(0x1A763F), H(0x875808), H(0x9C443C), H(0x0F68A2), H(0x4B4FC9), H(0x7B3FA0), H(0x505562),
        H(0xF4F5F7), H(0x16181D), H(0x505562), H(0xE0E3E9), H(0x4B4FC9),
        .32, 12, 600, 1.6, 0.7);
    private static readonly TemaKip MatKoyu = new("mat", true,
        H(0x1E1F24), Yok, Yok, H(0x3A3D48),
        H(0xECECF1), H(0xA3A7B3), H(0xDFE1EA), H(0x000000, .75), H(0x9EA4F5),
        H(0x5FB479), H(0xCC9140), H(0xDF8075), H(0x58A5E4), H(0x9EA4F5), H(0xD49BF5), H(0xA3A7B3),
        H(0x16171A), H(0xECECF1), H(0xA3A7B3), H(0x2A2C33), H(0x9EA4F5),
        .32, 12, 600, 1.6, 0.9);

    // ---- 4. ton (dingin çivit ve teal katmanlar) ----
    private static readonly TemaKip TonAcik = new("ton", false,
        H(0xDCEAF6), Beyaz.Alfa(.25), Yok, H(0x1A2850, .16),
        H(0x111A2E), H(0x354668), H(0x2D46B9), Beyaz.Alfa(.90), H(0x2D46B9),
        H(0x045F2E), H(0x724901), H(0x992724), H(0x005589), H(0x2D46B9), H(0x7A2468), H(0x354668),
        H(0xEEF3FA), H(0x111A2E), H(0x354668), H(0x1A2850, .14), H(0x2D46B9),
        .5, 999, 700, 2.0, 0.9);
    private static readonly TemaKip TonKoyu = new("ton", true,
        H(0x1C2541), Beyaz.Alfa(.06), Yok, H(0xB8CCFF, .22),
        H(0xE6EEFF), H(0xAAB8DA), H(0x7B9CFF), H(0x000000, .75), H(0x7B9CFF),
        H(0x4DBF74), H(0xE59A23), H(0xFA8175), H(0x66C4FF), H(0x7B9CFF), H(0xF09CE0), H(0xAAB8DA),
        H(0x11172B), H(0xE6EEFF), H(0xAAB8DA), H(0xB8CCFF, .16), H(0x7B9CFF),
        .5, 999, 700, 2.0, 1.0);

    // ---- 5. duz (yüksek kontrast net siyah-beyaz) ----
    private static readonly TemaKip DuzAcik = new("duz", false,
        H(0xFFFFFF), Yok, Yok, H(0x0A0A0A),
        H(0x0A0A0A), H(0x46463F), H(0x0A0A0A), Beyaz, H(0x0A0A0A),
        H(0x05773B), H(0x885703), H(0xB7191C), H(0x0068A5), H(0x0A0A0A), H(0x8A1C62), H(0x46463F),
        H(0xFFFFFF), H(0x0A0A0A), H(0x46463F), H(0x0A0A0A), H(0x0A0A0A),
        0, 0, 800, 2.2, 0);
    private static readonly TemaKip DuzKoyu = new("duz", true,
        H(0x0B0B0B), Yok, Yok, Beyaz,
        Beyaz, H(0xB9B9B1), H(0xC6F432), H(0x000000), H(0xC6F432),
        H(0x0CBD62), H(0xD78C00), H(0xFF655A), H(0x23A6FE), H(0xC6F432), H(0xFF7AD9), H(0xB9B9B1),
        H(0x0B0B0B), Beyaz, H(0xB9B9B1), Beyaz, H(0xC6F432),
        0, 0, 800, 2.2, 0);

    // ---- 6. sicak (krem ve toprak sıcak ton) ----
    private static readonly TemaKip SicakAcik = new("sicak", false,
        H(0xFFFDF8), Yok, Yok, H(0xDCCFB9),
        H(0x241F1A), H(0x5F554A), H(0x8A2F63), Beyaz.Alfa(.90), H(0x8A2F63),
        H(0x0E6F39), H(0x875808), H(0x9C443C), H(0x07659F), H(0x8A2F63), H(0xA41D74), H(0x5F554A),
        H(0xF6F2EA), H(0x241F1A), H(0x5F554A), H(0xE2D9C8), H(0x8A2F63),
        .5, 999, 600, 1.7, 0.9);
    private static readonly TemaKip SicakKoyu = new("sicak", true,
        H(0x262320), Yok, Yok, H(0x4A4237),
        H(0xF3EDE2), H(0xBDB3A3), H(0xE08FBD), H(0x000000, .75), H(0xE9A3C9),
        H(0x5FB479), H(0xCC9140), H(0xDF8075), H(0x58A5E4), H(0xE9A3C9), H(0xEEB0D1), H(0xBDB3A3),
        H(0x1D1A17), H(0xF3EDE2), H(0xBDB3A3), H(0x35302A), H(0xE9A3C9),
        .5, 999, 600, 1.7, 1.1);

    /// <summary>Girdiyi 6 kanonik tema ailesinden birine eşler (eski murekkep -> terminal, kagit -> sicak). Tanınmazsa null döner.</summary>
    public static string? Esle(string? girdi)
    {
        if (string.IsNullOrWhiteSpace(girdi)) return null;
        var s = girdi.Trim().ToLowerInvariant()
            .Replace('ü', 'u').Replace('â', 'a').Replace('ı', 'i')
            .Replace('ö', 'o').Replace('ç', 'c').Replace('ş', 's').Replace('ğ', 'g');
        return s switch
        {
            "cam" => "cam",
            "terminal" or "murekkep" => "terminal",
            "mat" => "mat",
            "ton" => "ton",
            "duz" => "duz",
            "sicak" or "kagit" => "sicak",
            _ => null,
        };
    }

    /// <summary>Kanonik tema adı. Tanınmayan ya da boş değer varsayılan "cam"a düşer.</summary>
    public static string KanoniAd(string? ad) => Esle(ad) ?? "cam";

    /// <summary>Zemin kipi eşlemesi: "acik"/"light", "koyu"/"dark", "sistem"/"system". Geçersizse null.</summary>
    public static string? KipEsle(string? kip)
    {
        if (string.IsNullOrWhiteSpace(kip)) return null;
        var s = kip.Trim().ToLowerInvariant()
            .Replace('ç', 'c').Replace('ı', 'i').Replace('ş', 's');
        return s switch
        {
            "acik" or "light" => "acik",
            "koyu" or "dark" => "koyu",
            "sistem" or "system" => "sistem",
            _ => null,
        };
    }

    /// <summary>Ad ve zemin kipine göre tema. Eski adlar geriye uyumlu eşlenir, bilinmeyen ad cama düşer.</summary>
    public static TemaKip Al(string? ad, bool koyu) => (KanoniAd(ad), koyu) switch
    {
        ("terminal", true) => TerminalKoyu, ("terminal", false) => TerminalAcik,
        ("mat", true) => MatKoyu, ("mat", false) => MatAcik,
        ("ton", true) => TonKoyu, ("ton", false) => TonAcik,
        ("duz", true) => DuzKoyu, ("duz", false) => DuzAcik,
        ("sicak", true) => SicakKoyu, ("sicak", false) => SicakAcik,
        (_, true) => CamKoyu, _ => CamAcik,
    };

    /// <summary>Yüksek karşıtlık: sistem renkleriyle düz, kenarlı. Sistem renkleri çağıran tarafından verilir.</summary>
    public static TemaKip YuksekKarsitlik(Renk pencere, Renk yazi, Renk vurgu, Renk secili) => new("yuksek-karsitlik", pencere.Parlaklik < .5,
        pencere, Yok, Yok, yazi,
        yazi, yazi, yazi, pencere, vurgu,
        yazi, yazi, yazi, yazi, yazi, yazi, yazi,
        pencere, yazi, yazi, yazi, vurgu,
        .5, 999, 700, 2.4, 0);
}
