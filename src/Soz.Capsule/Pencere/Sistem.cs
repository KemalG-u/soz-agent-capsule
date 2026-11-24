using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Soz.Capsule;

/// <summary>Tek yerden ms saati (Stopwatch, süreç başından). Mantık katmanı saati dışarıdan alır.</summary>
internal static class Zaman
{
    private static readonly long Baslangic = Stopwatch.GetTimestamp();
    public static long Simdi => (long)((Stopwatch.GetTimestamp() - Baslangic) * 1000.0 / Stopwatch.Frequency);
    public static double SimdiHassas => (Stopwatch.GetTimestamp() - Baslangic) * 1000.0 / Stopwatch.Frequency;
}

/// <summary>Günlük: dosya verilmişse satır ekler, yoksa hiçbir şey yapmaz (boşta disk ve bellek işi yok).</summary>
internal static class Gunluk
{
    private static string? _dosya;
    private static readonly object Kilit = new();
    public static void Ac(string? dosya) { _dosya = dosya; if (dosya != null) try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dosya))!); } catch { } }
    public static void Yaz(string satir)
    {
        if (_dosya == null) return;
        try { lock (Kilit) File.AppendAllText(_dosya, $"{DateTime.Now:HH:mm:ss.fff} {satir}\n", Encoding.UTF8); } catch { }
    }
}

internal static class Sistem
{
    /// <summary>Uygulama teması: açık mı (AppsUseLightTheme)? Okunamazsa koyu varsayılır.</summary>
    public static unsafe bool UygulamaAcik()
    {
        try
        {
            uint tip, boyut = 4, deger;
            var hr = N.RegGetValueW(N.HKEY_CURRENT_USER, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0x10 /*RRF_RT_REG_DWORD*/, out tip, out deger, ref boyut);
            return hr == 0 && deger != 0;
        }
        catch { return false; }
    }

    /// <summary>Sistem "animasyon efektleri" kapalıysa hareketi azalt gibi davran (SPI_GETCLIENTAREAANIMATION).</summary>
    public static bool SistemHareketAzalt()
    {
        try { return N.SystemParametersInfoW(0x1042 /*SPI_GETCLIENTAREAANIMATION*/, 0, out int v, 0) && v == 0; }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct HIGHCONTRAST { public uint cbSize, dwFlags; public IntPtr lpszDefaultScheme; }
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint a, uint b, ref HIGHCONTRAST h, uint c);

    public static bool YuksekKarsitlik()
    {
        var h = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
        return SystemParametersInfoW(0x42 /*SPI_GETHIGHCONTRAST*/, h.cbSize, ref h, 0) && (h.dwFlags & 1) != 0;
    }

    public static Renk SistemRengi(int idx)
    {
        var c = N.GetSysColor(idx);
        return new Renk(255, (byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
    }

    /// <summary>Kapsül penceresini Aero Peek dışında tutar (DWMWA_EXCLUDED_FROM_PEEK). Dönüş: DWM çağrısı başarılı mı (HRESULT 0).</summary>
    public static bool PeekDisiBirak(IntPtr hwnd, out int hresult)
    {
        try { var v = 1; hresult = N.DwmSetWindowAttribute(hwnd, N.DWMWA_EXCLUDED_FROM_PEEK, ref v, sizeof(int)); return hresult == 0; }
        catch (Exception) { hresult = -1; return false; }
    }

    /// <summary>Tam ekran uygulama (oyun, sunum, tam ekran video) çalışıyor mu? Kabuk sorgusu + öndeki pencerenin ekranı kaplaması.</summary>
    public static unsafe bool TamEkranVar(IntPtr kendi, IntPtr kendi2)
    {
        try
        {
            if (N.SHQueryUserNotificationState(out var durum) == 0 && durum is 2 or 3 or 4) return true; // QUNS_BUSY, D3D tam ekran, sunum
        }
        catch { }
        var h = N.GetForegroundWindow();
        if (h == IntPtr.Zero || h == kendi || h == kendi2 || h == N.GetDesktopWindow() || h == N.GetShellWindow()) return false;
        if (!N.IsWindowVisible(h) || !N.GetWindowRect(h, out var r)) return false;
        var sinif = stackalloc char[64]; var n = N.GetClassNameW(h, sinif, 64);
        var ad = new string(sinif, 0, Math.Max(0, n));
        if (PencereKurallari.TamEkranSayilmazSinif(ad)) return false;
        var mon = N.MonitorFromWindow(h, 2 /*NEAREST*/);
        var mi = new N.MONITORINFOEXW { cbSize = Marshal.SizeOf<N.MONITORINFOEXW>() };
        if (!N.GetMonitorInfoW(mon, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// <summary>Bağlı ekranlar (aygıt adı, tam alan, çalışma alanı, ölçek). Fiziksel piksel.</summary>
    public static List<EkranBilgi> Ekranlar()
    {
        var l = new List<EkranBilgi>();
        N.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref N.RECT rc, IntPtr d) =>
        {
            var mi = new N.MONITORINFOEXW { cbSize = Marshal.SizeOf<N.MONITORINFOEXW>() };
            if (!N.GetMonitorInfoW(hMon, ref mi)) return true;
            double olcek = 1;
            if (N.GetDpiForMonitor(hMon, 0, out var dx, out _) == 0 && dx > 0) olcek = dx / 96.0;
            l.Add(new EkranBilgi(mi.szDevice, new Dik(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                new Dik(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom), olcek, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return l;
    }

    /// <summary>Kapsülün arkasındaki zeminin parlaklığı: kapsül çevresinde 8 noktadan ekran pikseli okunur (0 koyu, 1 açık). 10 sn'de bir.</summary>
    public static double ZeminParlakligi(double cx, double cy, double yaricapPx)
    {
        var dc = N.GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return -1;
        try
        {
            double top = 0; var n = 0;
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                var v = N.GetPixel(dc, (int)Math.Round(cx + Math.Cos(a) * yaricapPx), (int)Math.Round(cy + Math.Sin(a) * yaricapPx));
                if (v == 0xFFFFFFFF) continue;
                top += (0.2126 * (v & 0xFF) + 0.7152 * ((v >> 8) & 0xFF) + 0.0722 * ((v >> 16) & 0xFF)) / 255.0; n++;
            }
            return n == 0 ? -1 : top / n;
        }
        finally { N.ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>Panodaki metin (Ctrl+V). Yoksa boş.</summary>
    public static string PanodanMetin(IntPtr hwnd)
    {
        if (!N.OpenClipboard(hwnd)) return "";
        try
        {
            var h = N.GetClipboardData(13 /*CF_UNICODETEXT*/);
            if (h == IntPtr.Zero) return "";
            var p = N.GlobalLock(h);
            if (p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; } finally { N.GlobalUnlock(h); }
        }
        finally { N.CloseClipboard(); }
    }
}

internal static class SurecZamani
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern void GetSystemTimeAsFileTime(out long t);

    /// <summary>Sürecin başlangıcından bu ana kadar geçen süre (ms). İlk kare ölçümü için.</summary>
    public static double BasindanMs()
    {
        if (!GetProcessTimes(N.GetCurrentProcess(), out var c, out _, out _, out _)) return -1;
        GetSystemTimeAsFileTime(out var now);
        return (now - c) / 10000.0;
    }

    /// <summary>Sürecin toplam işlemci süresi (ms): çekirdek + kullanıcı.</summary>
    public static double IslemciMs()
    {
        if (!GetProcessTimes(N.GetCurrentProcess(), out _, out _, out var k, out var u)) return -1;
        return (k + u) / 10000.0;
    }
}
