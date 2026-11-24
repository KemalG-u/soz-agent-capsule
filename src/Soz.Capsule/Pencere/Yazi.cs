using System.Runtime.InteropServices;

namespace Soz.Capsule;

/// <summary>Önceden çarpılmış BGRA bit eşlem (yazı).</summary>
internal sealed class YaziBitmap
{
    public int W, H;
    public byte[] Piksel = Array.Empty<byte>();
    public double AdvancePx;   // yazının gerçek genişliği (kenar payı hariç)
    public const int Pay = 2;  // her kenarda boş piksel
}

/// <summary>
/// En ucuz yazı yolu (kapı 2): GDI ile gri tonlamalı kenar yumuşatmalı yazıyı siyah zemine beyaz çizer, kapsama değerini alfa yapar,
/// rengi önceden çarpar. Ek aygıt ya da kütüphane yok. Yazı tipi: Segoe UI Variable Text, yoksa Segoe UI.
/// </summary>
internal sealed unsafe class YaziCizici : IDisposable
{
    private IntPtr _dc;
    private readonly Dictionary<(int px, int agirlik), IntPtr> _yazitipleri = new();
    private string? _yuzAdi;

    public YaziCizici()
    {
        _dc = N.CreateCompatibleDC(IntPtr.Zero);
        _yuzAdi = YuzSec();
    }

    private string YuzSec()
    {
        // Windows 11'de "Segoe UI Variable Text" vardır; yoksa Segoe UI. Varlığı bir deneme yazı tipiyle denetlenir (CreateFont her zaman bir şey döner).
        foreach (var ad in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            var f = N.CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, ad);
            if (f == IntPtr.Zero) continue;
            var eski = N.SelectObject(_dc, f);
            var kimlik = new char[32];
            // GetTextFace ile gerçekte seçilen ad okunur
            fixed (char* p = kimlik) { var n = GetTextFaceW(_dc, 32, p); if (n > 0 && new string(p, 0, n - 1).Equals(ad, StringComparison.OrdinalIgnoreCase)) { N.SelectObject(_dc, eski); N.DeleteObject(f); return ad; } }
            N.SelectObject(_dc, eski); N.DeleteObject(f);
        }
        return "Segoe UI";
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetTextFaceW(IntPtr dc, int n, char* buf);

    private IntPtr Font(int px, int agirlik)
    {
        if (_yazitipleri.TryGetValue((px, agirlik), out var f)) return f;
        // kalite 4 = ANTIALIASED_QUALITY (gri tonlama; ClearType alfa ile karışmaz)
        f = N.CreateFontW(-px, 0, 0, 0, agirlik, 0, 0, 0, 1, 0, 0, 4, 0, _yuzAdi!);
        _yazitipleri[(px, agirlik)] = f;
        return f;
    }

    /// <summary>Yazının px genişliği (ölçek dahil, kenar payı hariç).</summary>
    public double Olc(string yazi, int px, int agirlik)
    {
        if (yazi.Length == 0) return 0;
        var eski = N.SelectObject(_dc, Font(px, agirlik));
        N.GetTextExtentPoint32W(_dc, yazi, yazi.Length, out var s);
        N.SelectObject(_dc, eski);
        return s.cx;
    }

    /// <summary>Yazıyı verilen renkle çizer. enFazlaPx aşılırsa sonu "…" ile kısaltılır.</summary>
    public YaziBitmap Ciz(string yazi, int px, int agirlik, Renk renk, int enFazlaPx = int.MaxValue)
    {
        var b = new YaziBitmap();
        if (yazi.Length == 0) { b.W = 1; b.H = 1; b.Piksel = new byte[4]; return b; }
        var font = Font(px, agirlik);
        var eski = N.SelectObject(_dc, font);
        N.GetTextExtentPoint32W(_dc, yazi, yazi.Length, out var sz);
        if (sz.cx > enFazlaPx)
        {
            var lo = 0; var hi = yazi.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                var t = yazi[..mid].TrimEnd() + "…";
                N.GetTextExtentPoint32W(_dc, t, t.Length, out var s2);
                if (s2.cx <= enFazlaPx) lo = mid; else hi = mid - 1;
            }
            yazi = yazi[..lo].TrimEnd() + "…";
            N.GetTextExtentPoint32W(_dc, yazi, yazi.Length, out sz);
        }
        var pay = YaziBitmap.Pay;
        var w = sz.cx + 2 * pay; var h = sz.cy + 2 * pay;
        var bi = new N.BITMAPINFOHEADER { biSize = (uint)sizeof(N.BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = N.CreateDIBSection(_dc, ref bi, 0, out var bits, IntPtr.Zero, 0);
        var eskiBmp = N.SelectObject(_dc, dib);
        new Span<byte>((void*)bits, w * h * 4).Clear();
        N.SetBkMode(_dc, 1);
        N.SetTextColor(_dc, 0x00FFFFFF);
        N.TextOutW(_dc, pay, pay, yazi, yazi.Length);
        N.GdiFlush();
        var src = (byte*)bits;
        b.W = w; b.H = h; b.AdvancePx = sz.cx; b.Piksel = new byte[w * h * 4];
        var a0 = renk.A / 255.0;
        for (var i = 0; i < w * h; i++)
        {
            // kapsama: gri tonlamada üç kanal eşit; en büyüğü alınır. Hafif kontrast (gama) açık zeminde ince, koyu zeminde kalın görünmeyi dengeler.
            int cov = Math.Max(src[i * 4], Math.Max(src[i * 4 + 1], src[i * 4 + 2]));
            var al = cov / 255.0 * a0;
            b.Piksel[i * 4 + 0] = (byte)Math.Round(renk.B * al);
            b.Piksel[i * 4 + 1] = (byte)Math.Round(renk.G * al);
            b.Piksel[i * 4 + 2] = (byte)Math.Round(renk.R * al);
            b.Piksel[i * 4 + 3] = (byte)Math.Round(al * 255);
        }
        N.SelectObject(_dc, eskiBmp); N.DeleteObject(dib); N.SelectObject(_dc, eski);
        return b;
    }

    /// <summary>Metnin ilk n karakterinin genişliği (imleç konumu için).</summary>
    public double OlcOn(string yazi, int n, int px, int agirlik) => Olc(yazi[..Math.Clamp(n, 0, yazi.Length)], px, agirlik);

    public void Dispose()
    {
        foreach (var f in _yazitipleri.Values) N.DeleteObject(f);
        _yazitipleri.Clear();
        if (_dc != IntPtr.Zero) { N.DeleteDC(_dc); _dc = IntPtr.Zero; }
    }
}
