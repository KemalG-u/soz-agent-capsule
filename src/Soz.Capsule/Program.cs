using System.Runtime.InteropServices;

namespace Soz.Capsule;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var o = Secenekler.Ayristir(args);
        if (o.Yardim) { Yaz(Secenekler.Yardimi); return 0; }
        if (o.Hata) { Yaz("Hata: " + o.HataMetni + "\n\n" + Secenekler.Yardimi); return 2; }

        if (o.AyarYaz != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.AyarYaz))!);
                File.WriteAllText(o.AyarYaz, new Ayar().JsonYaz());
                Yaz("Ayar dosyası yazıldı: " + o.AyarYaz);
                return 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Yaz("Ayar dosyası yazılamadı: " + e.Message); return 2; }
        }

        // tek örnek: ikinci kopya sessizce çıkar (deneme ve ölçüm kipleri ya da --cok ile serbest)
        Mutex? mutex = null;
        if (!o.Demo && !o.Olc && !o.CokNusha)
        {
            mutex = new Mutex(true, KabukProtokol.TekOrnekAdi(o.DurumKlasoru), out var yeni);
            if (!yeni) return 0;
        }
        try { return new Kapsul(o).Calistir(); }
        catch (Exception e)
        {
            Gunluk.Yaz("ölümcül hata: " + e);
            Yaz("Kapsül hatayla durdu: " + e.Message);
            return 1;
        }
        finally { mutex?.ReleaseMutex(); }
    }

    // WinExe konsol açmaz; çıktı bağlı bir konsola (ya da yönlendirmeye) yazılır.
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    private static void Yaz(string s)
    {
        AttachConsole(-1);
        try { Console.Out.WriteLine(s); } catch (IOException) { }
    }
}
