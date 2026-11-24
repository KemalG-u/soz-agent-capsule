using Soz.Core;
using Soz.Core.Data;

// Çok süreçli testlerin yardımcı süreci.
//   goc  <veri> <git-dosyası>                 veritabanını açar ve göç ettirir (SozDb.OpenAndMigrate)
//   yaz  <veri> <adet> <etiket> <git-dosyası>  <adet> olay yazar: çift sıra Events.Append, tek sıra Events.Log (her yazımda yeni bağlantı, kanca gibi)
// Başlayınca stdout'a "HAZIR" yazar ve <git-dosyası> oluşana kadar bekler; böylece bütün süreçler aynı anda koşar (sabit bekleme yok).
// Çıkış kodu 0 = hatasız. Hata olursa stdout'a "HATA <tür>: <ileti>" yazılır.
static void Bekle(string gitDosyasi)
{
    Console.Out.WriteLine("HAZIR");
    Console.Out.Flush();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (!File.Exists(gitDosyasi))
    {
        if (sw.Elapsed > TimeSpan.FromMinutes(2)) { Console.Out.WriteLine("HATA git işareti gelmedi"); Environment.Exit(3); }
        Thread.Sleep(1);
    }
}

try
{
    switch (args[0])
    {
        case "goc":
            Bekle(args[2]);
            SozDb.OpenAndMigrate(args[1]);
            Console.Out.WriteLine("TAMAM");
            return 0;
        case "yaz":
        {
            var db = SozDb.OpenAndMigrate(args[1]);
            var adet = int.Parse(args[2]);
            var etiket = args[3];
            Bekle(args[4]);
            for (var i = 0; i < adet; i++)
            {
                using var c = db.Open();
                if (i % 2 == 0) Events.Append(c, "yaris.append", null, null, null, null, "yazar-" + etiket, $"{{\"y\":\"{etiket}\",\"i\":{i}}}");
                else Events.Log(c, "yaris_log", null, $"{etiket}-{i}");
            }
            Console.Out.WriteLine("TAMAM");
            return 0;
        }
        default:
            Console.Out.WriteLine("HATA bilinmeyen komut");
            return 2;
    }
}
catch (Exception e)
{
    Console.Out.WriteLine($"HATA {e.GetType().Name}: {e.Message.Split('\n')[0]}");
    return 1;
}
