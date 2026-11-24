using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Soz.Capsule;

internal readonly record struct Dik(double L, double T, double R, double B)
{
    public double W => R - L;
    public double H => B - T;
    public double MerkezX => (L + R) / 2;
    public double MerkezY => (T + B) / 2;
    public bool Icinde(double x, double y) => x >= L && x < R && y >= T && y < B;
}

/// <summary>Bir ekran: kimlik (aygıt adı), tam alan, çalışma alanı (görev çubuğu hariç) ve ölçek (DPI / 96). Hepsi fiziksel piksel.</summary>
internal sealed record EkranBilgi(string Id, Dik Tam, Dik Calisma, double Olcek, bool Birincil = false);

internal enum Taraf { Sol, Sag }
internal enum KenarX { Yok, Sol, Sag }
internal enum KenarY { Yok, Ust, Alt }

/// <summary>
/// Kapsülün yeri: ekran kimliği ile çalışma alanına oran olarak saklanır (P2: "çoklu ekran ve DPI: konum ekran kimliği + oran").
/// Anchor (çapa) hapın büyüme yönünü belirler: sağdaki kapsülde sağ uç sabit, sola büyür.
/// </summary>
internal sealed class KonumDurumu
{
    public string EkranId = "";
    public double Fx = 1, Fy = 0.4;       // çapa x oranı, merkez y oranı (çalışma alanı içinde)
    public KenarX SnapX = KenarX.Sag;
    public KenarY SnapY = KenarY.Yok;
    public Taraf Taraf = Taraf.Sag;

    public KonumDurumu Kopya() => (KonumDurumu)MemberwiseClone();

    public static KonumDurumu Varsayilan() => new();

    public string Json() => new StringBuilder("{")
        .Append($"\"ekran\":{JsonSerializer.Serialize(EkranId)},")
        .Append(CultureInfo.InvariantCulture, $"\"fx\":{Fx:0.####},\"fy\":{Fy:0.####},")
        .Append($"\"snapX\":\"{SnapX}\",\"snapY\":\"{SnapY}\",\"taraf\":\"{Taraf}\"}}").ToString();

    public static KonumDurumu? JsonOku(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            var e = d.RootElement; if (e.ValueKind != JsonValueKind.Object) return null;
            var k = new KonumDurumu();
            if (e.TryGetProperty("ekran", out var a) && a.ValueKind == JsonValueKind.String) k.EkranId = a.GetString() ?? "";
            if (e.TryGetProperty("fx", out var fx) && fx.TryGetDouble(out var fxv)) k.Fx = Math.Clamp(fxv, 0, 1);
            if (e.TryGetProperty("fy", out var fy) && fy.TryGetDouble(out var fyv)) k.Fy = Math.Clamp(fyv, 0, 1);
            if (e.TryGetProperty("snapX", out var sx) && Enum.TryParse<KenarX>(sx.GetString(), out var sxv)) k.SnapX = sxv;
            if (e.TryGetProperty("snapY", out var sy) && Enum.TryParse<KenarY>(sy.GetString(), out var syv)) k.SnapY = syv;
            if (e.TryGetProperty("taraf", out var tf) && Enum.TryParse<Taraf>(tf.GetString(), out var tfv)) k.Taraf = tfv;
            return k;
        }
        catch (JsonException) { return null; }
    }
}

internal static class KonumHesap
{
    /// <summary>Noktaya en yakın ekran (içindeyse o). Liste boşsa null.</summary>
    public static EkranBilgi? EkranBul(double x, double y, IReadOnlyList<EkranBilgi> ekranlar)
    {
        if (ekranlar.Count == 0) return null;
        EkranBilgi? en = null; var enMesafe = double.MaxValue;
        foreach (var e in ekranlar)
        {
            if (e.Tam.Icinde(x, y)) return e;
            var dx = Math.Max(Math.Max(e.Tam.L - x, 0), x - e.Tam.R);
            var dy = Math.Max(Math.Max(e.Tam.T - y, 0), y - e.Tam.B);
            var m = dx * dx + dy * dy;
            if (m < enMesafe) { enMesafe = m; en = e; }
        }
        return en;
    }

    /// <summary>Kimliğe göre ekran; yoksa birincil; o da yoksa ilk.</summary>
    public static EkranBilgi? EkranKimlikle(string id, IReadOnlyList<EkranBilgi> ekranlar)
        => ekranlar.FirstOrDefault(e => e.Id == id) ?? ekranlar.FirstOrDefault(e => e.Birincil) ?? ekranlar.FirstOrDefault();

    /// <summary>Saklanan oranlardan çapa (ax) ve merkez y (cy), fiziksel piksel. hPx görünen yükseklik, kenar payı kenarPx.</summary>
    public static (double Ax, double Cy, EkranBilgi? Ekran) Cozumle(KonumDurumu k, IReadOnlyList<EkranBilgi> ekranlar, double hPx, double kenarPx)
    {
        var e = EkranKimlikle(k.EkranId, ekranlar);
        if (e == null) return (0, 0, null);
        var w = e.Calisma;
        var ax = k.SnapX switch { KenarX.Sol => w.L + kenarPx, KenarX.Sag => w.R - kenarPx, _ => w.L + k.Fx * w.W };
        var cy = k.SnapY switch { KenarY.Ust => w.T + kenarPx + hPx / 2, KenarY.Alt => w.B - kenarPx - hPx / 2, _ => w.T + k.Fy * w.H };
        return (ax, cy, e);
    }

    /// <summary>Kapsül dikdörtgeni çalışma alanı içinde kalsın (deneme sayfasındaki capPos kenar sıkıştırması).</summary>
    public static Dik Sikistir(Dik r, Dik calisma, double pay = 2)
    {
        var l = Math.Clamp(r.L, calisma.L + pay, Math.Max(calisma.L + pay, calisma.R - r.W - pay));
        var t = Math.Clamp(r.T, calisma.T + pay, Math.Max(calisma.T + pay, calisma.B - r.H - pay));
        return new Dik(l, t, l + r.W, t + r.H);
    }

    /// <summary>
    /// Bırakınca: kenara 28 dp içindeyse kenara yapışır (kenar payı 6 dp), yoksa serbest kalır. Hangi yarıdaysa o tarafa çapalanır
    /// (deneme sayfasındaki endDrag). Girdi fiziksel piksel.
    /// </summary>
    public static KonumDurumu Birak(Dik r, EkranBilgi ekran, double yapismaPx, double kenarPx)
    {
        var w = ekran.Calisma;
        var dl = r.L - w.L; var dr = w.R - r.R; var dt = r.T - w.T; var db = w.B - r.B;
        var k = new KonumDurumu { EkranId = ekran.Id };
        double ax, cy;
        if (dl <= yapismaPx && dl <= dr) { k.SnapX = KenarX.Sol; k.Taraf = Taraf.Sol; ax = w.L + kenarPx; }
        else if (dr <= yapismaPx) { k.SnapX = KenarX.Sag; k.Taraf = Taraf.Sag; ax = w.R - kenarPx; }
        else
        {
            k.SnapX = KenarX.Yok;
            if (r.MerkezX < w.L + w.W / 2) { k.Taraf = Taraf.Sol; ax = r.L; } else { k.Taraf = Taraf.Sag; ax = r.R; }
        }
        if (dt <= yapismaPx && dt <= db) { k.SnapY = KenarY.Ust; cy = w.T + kenarPx + r.H / 2; }
        else if (db <= yapismaPx) { k.SnapY = KenarY.Alt; cy = w.B - kenarPx - r.H / 2; }
        else { k.SnapY = KenarY.Yok; cy = r.MerkezY; }
        k.Fx = w.W <= 0 ? 1 : Math.Clamp((ax - w.L) / w.W, 0, 1);
        k.Fy = w.H <= 0 ? .4 : Math.Clamp((cy - w.T) / w.H, 0, 1);
        return k;
    }

    /// <summary>Çapa ve boyuta göre kapsül dikdörtgeni (sağ taraf: sağ uç çapa, sola büyür).</summary>
    public static Dik Dikdortgen(double ax, double cy, double wPx, double hPx, Taraf taraf)
        => taraf == Taraf.Sol ? new Dik(ax, cy - hPx / 2, ax + wPx, cy + hPx / 2) : new Dik(ax - wPx, cy - hPx / 2, ax, cy + hPx / 2);

    /// <summary>
    /// Açılır yüzeyin (ön bakış, pano) yeri: kapsülün yanında yatay yer varsa yanında, yoksa altında ya da üstünde; ekrandan taşmaz.
    /// Dönüş: sol-üst ve büyüme başlangıç noktası (kapsül merkezi, panel içinde). Deneme sayfasındaki placePanel.
    /// </summary>
    public static (double X, double Y, double OrijinX, double OrijinY) PopupYerle(Dik kapsul, double pw, double ph, Dik calisma, double aralik, double pay)
    {
        double x, y;
        var ccx = kapsul.MerkezX; var ccy = kapsul.MerkezY;
        var spL = kapsul.L - calisma.L - aralik - pay;
        var spR = calisma.R - kapsul.R - aralik - pay;
        if (pw <= Math.Max(spL, spR))
        {
            var solTercih = ccx > calisma.L + calisma.W / 2;
            if (solTercih && pw <= spL) x = kapsul.L - aralik - pw;
            else if (!solTercih && pw <= spR) x = kapsul.R + aralik;
            else x = pw <= spL ? kapsul.L - aralik - pw : kapsul.R + aralik;
            y = ccy < calisma.T + calisma.H / 2 ? kapsul.T : kapsul.B - ph;
        }
        else
        {
            x = ccx > calisma.L + calisma.W / 2 ? kapsul.R - pw : kapsul.L;
            y = calisma.B - kapsul.B - aralik - pay >= kapsul.T - calisma.T - aralik - pay ? kapsul.B + aralik : kapsul.T - aralik - ph;
        }
        x = Math.Clamp(x, calisma.L + pay, Math.Max(calisma.L + pay, calisma.R - pw - pay));
        y = Math.Clamp(y, calisma.T + pay, Math.Max(calisma.T + pay, calisma.B - ph - pay));
        return (x, y, Math.Clamp(ccx - x, 0, pw), Math.Clamp(ccy - y, 0, ph));
    }
}
