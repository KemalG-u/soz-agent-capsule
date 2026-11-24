using System.Runtime.InteropServices;
using System.Text;

namespace Soz.Capsule;

/// <summary>
/// Masaüstü programıyla konuşma yolu: komutlar standart çıktıya, olaylar standart girdiden (satır başına bir JSON, bkz. <see cref="KabukProtokol"/>).
/// Süreç alt süreç olduğu için ek paket ya da kanal yoktur. Standart girdi kapanırsa (masaüstü programı kapandı ya da çöktü) kapsül yetim kalmaz,
/// <see cref="OlayTuru.Kapat"/> olayıyla kendiliğinden kapanır. Okuyucu tek bir engelleyen iş parçacığıdır: boşta işlemci harcamaz.
/// </summary>
internal sealed class KabukKanali : IDisposable
{
    private readonly Action<KabukOlayi> _olay;
    private readonly Stream _cikis;
    private readonly Stream _girdi;
    private readonly object _kilit = new();
    private volatile bool _kopuk;
    private Thread? _okuyucu;

    public KabukKanali(Action<KabukOlayi> olay)
    {
        _olay = olay;
        _cikis = Console.OpenStandardOutput();
        _girdi = Console.OpenStandardInput();
    }

    public bool Kopuk => _kopuk;

    /// <summary>Okumaya başlar. Pencere hazır olduktan sonra çağrılır (olaylar pencere iş parçacığına yollanır).</summary>
    public void OkumayaBasla()
    {
        _okuyucu = new Thread(Oku) { IsBackground = true, Name = "kabuk-girdi" };
        _okuyucu.Start();
    }

    private void Oku()
    {
        try
        {
            using var r = new StreamReader(_girdi, new UTF8Encoding(false), false, 4096);
            string? satir;
            while ((satir = r.ReadLine()) != null)
            {
                var o = KabukProtokol.OlayOku(satir);
                if (o is { } ev) _olay(ev);
                else Gunluk.Yaz("kabuk: anlaşılmayan satır yok sayıldı");
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        _kopuk = true;
        _olay(new KabukOlayi(OlayTuru.Kapat, "girdi kapandı"));
    }

    /// <summary>Komutu kabuğa yollar. Yazılamazsa (kabuk gitmiş) sessizce kopuk sayılır.</summary>
    public bool Gonder(KapsulKomutu k)
    {
        if (_kopuk) return false;
        var b = new UTF8Encoding(false).GetBytes(KabukProtokol.Yaz(k) + "\n");
        try { lock (_kilit) { _cikis.Write(b, 0, b.Length); _cikis.Flush(); } return true; }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { _kopuk = true; return false; }
    }

    /// <summary>
    /// Bu süreç az önce kullanıcı girdisi aldı (tık), ön plan hakkı onda. Masaüstü programı hemen ardından paneli öne getirecek:
    /// hakkı ona geçirmezsek Windows panelin odak almasını reddeder. Hak YALNIZ masaüstü programına (bu sürecin üst süreci) verilir:
    /// eskiden herkese verilirdi (ASFW_ANY), bu da o sırada başka bir programın (ör. dikte) öne atlamasına kapı açıyordu.
    /// Üst süreç bulunamazsa hak verilmez (panel yine açılır, yalnız odak almayabilir).
    /// </summary>
    public static void OnPlanHakkiVer()
    {
        try { if (UstSurecNo() is { } pid && pid != 0) AllowSetForegroundWindow(pid); } catch { }
    }

    private static uint? s_ust; private static bool s_ustBakildi;
    private static uint? UstSurecNo()
    {
        if (s_ustBakildi) return s_ust;
        s_ustBakildi = true;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(GetCurrentProcess(), 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0) s_ust = (uint)(long)pbi.InheritedFromUniqueProcessId;
        }
        catch { }
        return s_ust;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int sinif, ref PROCESS_BASIC_INFORMATION bilgi, int uzunluk, out int donen);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);

    public void Dispose() { _kopuk = true; }
}
