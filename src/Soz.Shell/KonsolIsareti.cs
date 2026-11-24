using System.Runtime.InteropServices;
using System.Text;

namespace Soz.Shell;

/// <summary>
/// Pencere paylaşan sekmeler arasında belirli bir sürecin sekmesini bulma yolu: sürecin konsol başlığına kısa süre benzersiz bir işaret yazılır,
/// sekme başlıklarında o işaret aranır, bulunca başlık HEMEN eski haline döndürülür. Yalnız proje adı ve oturum başlığı yetmediğinde ve süreç numarası
/// kesin bilindiğinde son çare olarak kullanılır. Eski başlık okunamıyorsa hiçbir şey yazılmaz (kullanıcının sekmesi işaretli kalmasın).
/// Not: ajan kendi başlığını sürekli yeniliyorsa (dönen simge) işaret hemen silinebilir; bu durumda null döner ve çağıran pencere ipucuna düşer.
/// </summary>
internal static class KonsolIsareti
{
    private static readonly object Kilit = new();
    /// <summary>Gerçek Windows Terminal sekmesinde uçtan uca DENENMEDİ (deneme izni verilmedi), bu yüzden varsayılan KAPALI. SOZ_SEKME_ISARETI=1 ile açılır.</summary>
    internal static bool Acik => Environment.GetEnvironmentVariable("SOZ_SEKME_ISARETI") == "1";
    private const int AtachParent = -1;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetConsoleTitleW(StringBuilder sb, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetConsoleTitleW(string title);

    internal sealed record Bulgu(IntPtr Pencere, UiaSekme.Sekme Sekme);

    internal static Bulgu? Bul(int pid, IReadOnlyList<IntPtr> pencereler)
    {
        if (!Acik || pid <= 0 || pencereler.Count == 0) return null;
        lock (Kilit)
        {
            var konsoluVardi = GetConsoleWindow() != IntPtr.Zero;
            string? eski = null;
            var isaret = "SOZ-" + Guid.NewGuid().ToString("N")[..6];
            var yazildi = false;
            try
            {
                FreeConsole();
                if (!AttachConsole(pid)) return null;
                try
                {
                    var sb = new StringBuilder(1024);
                    var n = GetConsoleTitleW(sb, (uint)sb.Capacity);
                    if (n == 0) return null; // eski başlık bilinmeden işaret yazılmaz
                    eski = sb.ToString();
                    yazildi = SetConsoleTitleW(isaret);
                }
                finally { FreeConsole(); }
                if (!yazildi) return null;

                Bulgu? bulunan = null;
                for (var i = 0; i < 12 && bulunan == null; i++)
                {
                    foreach (var p in pencereler)
                    {
                        var s = UiaSekme.Sekmeler(p).FirstOrDefault(x => x.Ad.Contains(isaret, StringComparison.Ordinal));
                        if (s != null) { bulunan = new Bulgu(p, s); break; }
                    }
                    if (bulunan == null) Thread.Sleep(50);
                }
                if (bulunan != null) UiaSekme.Sec(bulunan.Sekme); // seçim işaret hâlâ yerindeyken yapılır
                return bulunan;
            }
            finally
            {
                if (yazildi && eski != null)
                {
                    try { FreeConsole(); if (AttachConsole(pid)) { SetConsoleTitleW(eski); } }
                    catch { }
                    finally { FreeConsole(); }
                }
                if (konsoluVardi) AttachConsole(AtachParent); // deneme kipinde (konsoldan çalıştırılınca) çıktı yolu geri gelsin
            }
        }
    }
}
