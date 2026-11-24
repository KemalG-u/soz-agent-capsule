using System.Drawing;
using System.Text;
using System.Text.Json;

namespace Soz.Shell;

/// <summary>Kişinin taşıyıp boyutlandırdığı panelin saklı yeri: X ve Y ekran pikseli, boyut 96 dpi'a göre (ölçekten bağımsız, dp).</summary>
internal readonly record struct PanelSakli(int X, int Y, int GenDp, int YukDp);

/// <summary>
/// Panelin nereye ve ne boyutta açılacağı. Pencereden bağımsız saf karar: girdi saklı değer, ekranların çalışma alanları ve kapsülün GÜNCEL konumu;
/// çıktı dikdörtgen. Kural: saklı yer hâlâ görünüyorsa aynen gelir; kısmen dışarıdaysa çalışma alanına itilir; görünmüyorsa (ekran çıkarıldı,
/// çözünürlük değişti) ya da hiç saklı yer yoksa kapsülün yanına, tamamı çalışma alanı içinde kalacak biçimde yerleşir. Boyut en küçük ve en büyük sınırdan geçer.
/// </summary>
internal static class PanelYerlesim
{
    /// <summary>Hiç boyutlandırılmadıysa açılış boyutu (dp).</summary>
    public static readonly Size VarsayilanDp = new(500, 720);
    /// <summary>Panelin inebileceği en küçük boyut (dp). Arayüz bunun altında bozulur.</summary>
    public static readonly Size EnKucukDp = new(380, 480);
    /// <summary>Çalışma alanı kenarından bırakılan pay (piksel).</summary>
    public const int Pay = 8;

    private static readonly Rectangle Yedek = new(0, 0, 1280, 720);

    private static Size Olc(Size dp, double olcek) => new((int)Math.Round(dp.Width * olcek), (int)Math.Round(dp.Height * olcek));
    private static long Alan(Rectangle r) => r.Width <= 0 || r.Height <= 0 ? 0 : (long)r.Width * r.Height;

    public static Rectangle Hesapla(PanelSakli? sakli, IReadOnlyList<Rectangle> calismaAlanlari, Rectangle kapsul, double olcek = 1.0)
    {
        var alanlar = calismaAlanlari.Where(a => a.Width > 0 && a.Height > 0).ToList();
        if (alanlar.Count == 0) alanlar.Add(Yedek);
        if (olcek <= 0) olcek = 1.0;
        var enKucuk = Olc(EnKucukDp, olcek);
        var istenen = sakli is { } s ? Olc(new Size(s.GenDp, s.YukDp), olcek) : Olc(VarsayilanDp, olcek);

        if (sakli is { } sk)
        {
            var r0 = new Rectangle(sk.X, sk.Y, istenen.Width, istenen.Height);
            var en = EnCokKesisen(alanlar, r0);
            if (en is { } alan)
            {
                var boyut = Boyutla(istenen, enKucuk, alan);
                var r = new Rectangle(sk.X, sk.Y, boyut.Width, boyut.Height);
                var gorunen = alanlar.Sum(a => Alan(Rectangle.Intersect(a, r)));
                if (gorunen == Alan(r)) return r;                 // tamamı görünür (tek ekranda ya da ekranlara yayılmış)
                if (gorunen * 2 >= Alan(r)) return Sikistir(r, alan); // yarısından çoğu görünür: çalışma alanına it
            }
        }
        return KapsulYani(istenen, enKucuk, alanlar, kapsul, olcek);
    }

    /// <summary>Panelin alabileceği en büyük boyut: bütün ekranların en büyük çalışma alanı.</summary>
    public static Size EnBuyuk(IReadOnlyList<Rectangle> alanlar)
    {
        var l = alanlar.Where(a => a.Width > 0 && a.Height > 0).ToList();
        if (l.Count == 0) return Yedek.Size;
        return new Size(l.Max(a => a.Width), l.Max(a => a.Height));
    }

    /// <summary>İstenen boyut en küçüğün altına inmez, çalışma alanından 16 piksel azını geçmez (alan en küçükten de küçükse alan kazanır).</summary>
    internal static Size Boyutla(Size istenen, Size enKucuk, Rectangle alan)
    {
        var w = Math.Min(Math.Max(istenen.Width, enKucuk.Width), alan.Width - 2 * Pay);
        var h = Math.Min(Math.Max(istenen.Height, enKucuk.Height), alan.Height - 2 * Pay);
        return new Size(Math.Max(1, w), Math.Max(1, h));
    }

    private static Rectangle Sikistir(Rectangle r, Rectangle alan)
    {
        var x = Math.Clamp(r.X, alan.Left, Math.Max(alan.Left, alan.Right - r.Width));
        var y = Math.Clamp(r.Y, alan.Top, Math.Max(alan.Top, alan.Bottom - r.Height));
        return new Rectangle(x, y, r.Width, r.Height);
    }

    private static Rectangle? EnCokKesisen(List<Rectangle> alanlar, Rectangle r)
    {
        Rectangle? en = null; long enAlan = 0;
        foreach (var a in alanlar)
        {
            var k = Alan(Rectangle.Intersect(a, r));
            if (k > enAlan) { en = a; enAlan = k; }
        }
        return en;
    }

    /// <summary>Hiç kesişmiyorsa merkeze en yakın alan (bayat ya da ekran dışı kapsül konumunda bile bir ekran seçilir).</summary>
    private static Rectangle EnYakin(List<Rectangle> alanlar, Rectangle r)
    {
        if (EnCokKesisen(alanlar, r) is { } k) return k;
        var cx = r.Left + r.Width / 2.0; var cy = r.Top + r.Height / 2.0;
        return alanlar.OrderBy(a =>
        {
            var dx = Math.Max(a.Left - cx, Math.Max(0, cx - a.Right));
            var dy = Math.Max(a.Top - cy, Math.Max(0, cy - a.Bottom));
            return dx * dx + dy * dy;
        }).First();
    }

    /// <summary>Önce kapsülün üstüne, sığmazsa altına; sağ kenar kapsülle hizalı. Sonuç hep seçilen çalışma alanının içindedir.</summary>
    private static Rectangle KapsulYani(Size istenen, Size enKucuk, List<Rectangle> alanlar, Rectangle kapsul, double olcek)
    {
        Rectangle alan;
        if (kapsul.Width <= 0 || kapsul.Height <= 0)
        {
            alan = alanlar[0]; // birincil ekran (çağıran ilk sıraya koyar)
            kapsul = new Rectangle(alan.Right - 40, alan.Bottom - 40, 24, 24);
        }
        else alan = EnYakin(alanlar, kapsul);
        var b = Boyutla(istenen, enKucuk, alan);
        var gap = (int)Math.Round(8 * olcek);
        var y = kapsul.Top - b.Height - gap;
        if (y < alan.Top) y = kapsul.Bottom + gap;
        if (y + b.Height > alan.Bottom) y = Math.Max(alan.Top, alan.Bottom - b.Height - Pay);
        var x = kapsul.Right - b.Width;
        x = Math.Clamp(x, alan.Left + Pay, Math.Max(alan.Left + Pay, alan.Right - b.Width - Pay));
        return Sikistir(new Rectangle(x, y, b.Width, b.Height), alan);
    }
}

internal enum KlasorKaynagi { SonSecim, ProfilUst, Surucu, Belgeler }

/// <summary>Klasör seçicinin açılacağı klasör: ilk var olan aday kazanır. Pencere ve ağ yok, var mı denetimi dışarıdan verilir.</summary>
internal static class KlasorBaslangic
{
    /// <summary>
    /// Sıra: (1) en son bu seçiciyle seçilen klasörün üst klasörü, (2) profilin önerdiği yeni proje üst klasörü, (3) proje sürücüsünün kökü, (4) Belgeler.
    /// Boş, göreli ya da anlamsız değer aday olmaz; <paramref name="varMi"/> hata verirse aday yok sayılır. Hiçbiri yoksa <paramref name="belgeler"/> döner (denetlenmeden).
    /// </summary>
    public static string Sec(string? sonSecilen, string? profilUst, string? profilSurucu, string belgeler, Func<string, bool> varMi)
        => Karar(sonSecilen, profilUst, profilSurucu, belgeler, varMi).Yol;

    /// <summary>Aynı karar, hangi adaydan geldiği ile (günlük yolu açmadan kaynağı yazsın diye).</summary>
    public static (string Yol, KlasorKaynagi Kaynak) Karar(string? sonSecilen, string? profilUst, string? profilSurucu, string belgeler, Func<string, bool> varMi)
    {
        var adaylar = new[] { (UstKlasor(sonSecilen), KlasorKaynagi.SonSecim), (Duzgun(profilUst), KlasorKaynagi.ProfilUst), (SurucuKoku(profilSurucu), KlasorKaynagi.Surucu) };
        foreach (var (aday, kaynak) in adaylar)
            if (aday != null && Var(varMi, aday)) return (aday, kaynak);
        return (belgeler, KlasorKaynagi.Belgeler);
    }

    private static bool Var(Func<string, bool> varMi, string yol) { try { return varMi(yol); } catch { return false; } }

    /// <summary>Sürücü harfi/kökü ya da tam yol olmalı (göreli yol, "E:abc" ve çöp değil). Sondaki ayırıcı atılır, kök korunur.</summary>
    private static string? Duzgun(string? yol)
    {
        if (string.IsNullOrWhiteSpace(yol)) return null;
        var p = yol.Trim();
        try
        {
            var kok = Path.GetPathRoot(p);
            if (string.IsNullOrEmpty(kok) || kok.Length < 3 || !(kok.EndsWith('\\') || kok.EndsWith('/'))) return null;
            if (p.Length > kok.Length) p = p.TrimEnd('\\', '/');
            return p;
        }
        catch (ArgumentException) { return null; }
    }

    private static string? UstKlasor(string? sonSecilen)
    {
        var p = Duzgun(sonSecilen);
        if (p == null) return null;
        string? ust;
        try { ust = Path.GetDirectoryName(p); } catch (ArgumentException) { return null; }
        return string.IsNullOrEmpty(ust) ? p : Duzgun(ust);   // sürücü kökü seçildiyse üstü yok: kendisi
    }

    private static string? SurucuKoku(string? surucu)
    {
        if (string.IsNullOrWhiteSpace(surucu)) return null;
        var s = surucu.Trim();
        if (s.Length <= 3 && char.IsAsciiLetter(s[0]) && (s.Length == 1 || (s[1] == ':' && (s.Length == 2 || s[2] is '\\' or '/'))))
            return char.ToUpperInvariant(s[0]) + @":\";
        var d = Duzgun(s);
        if (d == null) return null;
        try { return Path.GetPathRoot(d); } catch (ArgumentException) { return null; }
    }
}

/// <summary>
/// Panelin odağı kaybedince ve tepsi ya da küçük pencere tıklanınca ne yapacağı (saf karar, pencere gerekmez).
/// Panel ana pencere gibi davranır: kişi terminalden bir şey kopyalayıp dönmek isteyebilir, bu yüzden odak kaybı paneli gizlemez (varsayılan).
/// Eski "açılır pencere" davranışı isteyen için ortam değişkeni <c>SOZ_PANEL_ODAK_GIZLE=1</c> ya da panel-durum.json içinde <c>odakKaybindaGizle: true</c>.
/// Gizlemek için X, tepsi simgesi ya da küçük pencere tıklaması vardır. Panel başka pencerenin arkasında kaldıysa aynı tıklama onu öne getirir (gizlemez).
/// Panel küçültülebilir ve görev çubuğunda düğmesi vardır; küçültülmüş panel için karar <see cref="PanelPencereKurali"/> içindedir.
/// </summary>
internal static class PanelOdak
{
    public const string OrtamAdi = "SOZ_PANEL_ODAK_GIZLE";
    /// <summary>Tıklamanın kendisi paneli etkinsizleştirir (tepsiye tıklayınca odak görev çubuğuna geçer): bu süre içinde etkin kaybı "tıklamadan önce etkindi" sayılır.</summary>
    public const double TikPayiMs = 600;
    public const double GosterimPayiMs = 400;

    public enum TikKarari { Gizle, OneGetir }

    public static bool Coz(string? ortamDegeri, bool? dosya)
    {
        var o = ortamDegeri?.Trim();
        if (o is "1" or "true") return true;
        if (o is "0" or "false") return false;
        return dosya ?? false;
    }

    /// <summary>Odak kaybında paneli gizle? Yalnız ayar açıksa, panel görünürken, klasör seçici açık değilken ve gösterilir gösterilmez olan sahte odak oynamasından sonra.</summary>
    public static bool OdakKaybindaGizle(bool gizleAyari, bool gorunur, bool klasorSeciliyor, double gosterildiktenBeriMs)
        => gizleAyari && gorunur && !klasorSeciliyor && gosterildiktenBeriMs > GosterimPayiMs;

    /// <summary>Panel görünürken tepsi ya da küçük pencere tıklandı: etkinse (ya da tıklamanın kendisi etkinliği aldıysa) gizle, arkada kalmışsa öne getir.</summary>
    public static TikKarari AcKapa(bool etkin, double sonEtkinKaybindanBeriMs)
        => etkin || sonEtkinKaybindanBeriMs < TikPayiMs ? TikKarari.Gizle : TikKarari.OneGetir;
}

/// <summary>Panel penceresinin durumu (WinForms'tan bağımsız; <c>FormWindowState</c> karşılığı).</summary>
internal enum PencereDurumu { Normal, Kucultulmus, Buyutulmus }

/// <summary>Bir tepsi tıklaması, kısayol ya da ikinci açılış isteğinde paneline yapılacak iş.</summary>
internal enum PanelEylemi { Goster, GeriGetir, OneGetir, Gizle }

/// <summary>
/// Panel normal bir Windows penceresi gibi küçültülüp büyütülebilir. Küçültülmüş pencere WinForms'ta hâlâ <c>Visible</c> sayılır; bu yüzden
/// "görünürse gizle" mantığı küçültülmüşü gizlemez, GERİ GETİRİR. Karar saf işlevdir (pencere gerekmez).
/// Büyütülmüş durum bir sonraki açılışta hatırlanır, küçültülmüş durum hatırlanmaz (program küçültülmüş açılmaz).
/// </summary>
internal static class PanelPencereKurali
{
    /// <summary>
    /// Tepsi ya da kapsül tıklaması. Gizliyse göster; küçültülmüşse geri getir (asla gizleme); aksi halde mevcut etkin/arkada kararı
    /// (<see cref="PanelOdak.AcKapa"/>) uygulanır: etkinse gizle, arkada kalmışsa öne getir.
    /// </summary>
    public static PanelEylemi TepsiTiklandi(bool gorunur, PencereDurumu durum, PanelOdak.TikKarari tik)
    {
        if (!gorunur) return PanelEylemi.Goster;
        if (durum == PencereDurumu.Kucultulmus) return PanelEylemi.GeriGetir;
        return tik == PanelOdak.TikKarari.Gizle ? PanelEylemi.Gizle : PanelEylemi.OneGetir;
    }

    /// <summary>"Aç" isteği (kısayol, menü, ikinci açılış, bildirim): hiçbir zaman gizlemez. Gizliyse göster, küçültülmüşse geri getir, değilse öne getir.</summary>
    public static PanelEylemi AcIstendi(bool gorunur, PencereDurumu durum)
        => !gorunur ? PanelEylemi.Goster : durum == PencereDurumu.Kucultulmus ? PanelEylemi.GeriGetir : PanelEylemi.OneGetir;

    /// <summary>Gösterilirken ya da küçültmeden dönerken verilecek durum: küçültülmüş asla hedef değildir; en son büyütülmüşse büyütülmüş, değilse normal.</summary>
    public static PencereDurumu HedefDurum(bool sonBuyutulmus)
        => sonBuyutulmus ? PencereDurumu.Buyutulmus : PencereDurumu.Normal;

    /// <summary>"En son büyütülmüş müydü" bilgisini günceller: küçültme bunu değiştirmez (küçültmeden önceki durum akılda kalır).</summary>
    public static bool BuyutulmusMu(PencereDurumu simdi, bool onceki)
        => simdi switch { PencereDurumu.Buyutulmus => true, PencereDurumu.Normal => false, _ => onceki };

    /// <summary>Yer ve boyut yalnız normal durumda saklanır. Büyütülmüş ya da küçültülmüşken pencerenin Bounds'u ekran boyudur ya da ekran dışıdır.</summary>
    public static bool YerSaklanabilir(PencereDurumu durum) => durum == PencereDurumu.Normal;
}

/// <summary>Panel gizli kalınca WebView2'nin ne kadar sonra bırakılacağı (RAM). Ortam değişkeni, sonra durum dosyası, sonra varsayılan.</summary>
internal static class PanelBosta
{
    public static readonly TimeSpan Varsayilan = TimeSpan.FromMinutes(3);
    public const string OrtamAdi = "SOZ_PANEL_BOSTA_DK";
    private const int EnAz = 1, EnCok = 1440;

    public static TimeSpan Coz(string? ortamDegeri, int? dosyaDk)
    {
        if (int.TryParse(ortamDegeri?.Trim(), out var o) && o is >= EnAz and <= EnCok) return TimeSpan.FromMinutes(o);
        if (dosyaDk is { } d && d is >= EnAz and <= EnCok) return TimeSpan.FromMinutes(d);
        return Varsayilan;
    }
}

/// <summary>
/// Panelin kişiye ait durumu (veri\panel-durum.json): saklı yer ve boyut, klasör seçicinin son klasörü, boşta bırakma süresi.
/// Sunucu ayar sözlüğü bilinmeyen anahtarı reddettiği için kabuğun yerel dosyasında durur. İçinde jeton ya da kişisel içerik yoktur (yalnız sayılar ve son seçilen klasör yolu).
/// </summary>
internal sealed record PanelDurum(PanelSakli? Sakli, string? SonKlasor, int? BostaDk, bool? OdakKaybindaGizle = null, bool? Buyutulmus = null)
{
    public const string DosyaAdi = "panel-durum.json";

    public static readonly PanelDurum Bos = new(null, null, null);

    public string Json()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            if (Sakli is { } s) { w.WriteNumber("x", s.X); w.WriteNumber("y", s.Y); w.WriteNumber("w", s.GenDp); w.WriteNumber("h", s.YukDp); }
            if (!string.IsNullOrEmpty(SonKlasor)) w.WriteString("sonKlasor", SonKlasor);
            if (BostaDk is { } b) w.WriteNumber("bostaDk", b);
            if (OdakKaybindaGizle is { } og) w.WriteBoolean("odakKaybindaGizle", og);
            if (Buyutulmus == true) w.WriteBoolean("buyutulmus", true);   // yalnız "büyütülmüş" yazılır; küçültülmüş durum hiç saklanmaz
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Bozuk, boş ya da saçma değerli dosya çökertmez: geçersiz alanlar yok sayılır.</summary>
    public static PanelDurum Oku(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Bos;
        try
        {
            using var d = JsonDocument.Parse(json);
            var e = d.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return Bos;
            int? Sayi(string ad, int en, int cok) => e.TryGetProperty(ad, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= en && i <= cok ? i : null;
            var x = Sayi("x", -100000, 100000); var y = Sayi("y", -100000, 100000);
            var w = Sayi("w", 100, 8000); var h = Sayi("h", 100, 8000);
            PanelSakli? sakli = x is { } xv && y is { } yv && w is { } wv && h is { } hv ? new PanelSakli(xv, yv, wv, hv) : null;
            string? klasor = e.TryGetProperty("sonKlasor", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 and <= 1000 } ks ? ks : null;
            bool? odakGizle = e.TryGetProperty("odakKaybindaGizle", out var og) && og.ValueKind is JsonValueKind.True or JsonValueKind.False ? og.ValueKind == JsonValueKind.True : null;
            bool? buyuk = e.TryGetProperty("buyutulmus", out var bu) && bu.ValueKind == JsonValueKind.True ? true : null;
            return new PanelDurum(sakli, klasor, Sayi("bostaDk", 1, 1440), odakGizle, buyuk);
        }
        catch (JsonException) { return Bos; }
    }

    public static PanelDurum Yukle(string? dosya)
    {
        if (string.IsNullOrEmpty(dosya)) return Bos;
        try { return File.Exists(dosya) ? Oku(File.ReadAllText(dosya)) : Bos; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Bos; }
    }

    /// <summary>Yazar; başarısızsa false (günlüğe yazmak çağıranın işi). Önce geçici dosyaya yazıp değiştirir: yarım dosya kalmaz.</summary>
    public static bool Kaydet(string? dosya, PanelDurum d)
    {
        if (string.IsNullOrEmpty(dosya)) return false;
        try
        {
            var gecici = dosya + ".tmp";
            File.WriteAllText(gecici, d.Json());
            File.Move(gecici, dosya, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
