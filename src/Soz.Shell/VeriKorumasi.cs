using System.Text;

namespace Soz.Shell;

/// <summary>
/// Kaldırmada kullanıcının verisini (iş geçmişi, kanıtlar, ayarlar) kurulum klasörünün DIŞINA taşır; kurulum klasörü Velopack tarafından
/// topluca silinir. Yeniden kurulumda ilk açılışta veri geri taşınır. Taşıma aynı disk üzerinde yeniden adlandırmadır (anında ve güvenli);
/// taşınamazsa kopyalanır. Hiçbir adımda kullanıcının verisi silinmez: silme yalnız <see cref="KabukTercihleri.KaldirirkenVeriyiSil"/> açıkça açıldıysa yapılır.
/// </summary>
internal static class VeriKorumasi
{
    internal const string KorunanAd = "Soz-veri";
    internal const string NotAdi = "SOZ-VERI-NEREDE.txt";

    /// <summary>Kurulum kökünün yanındaki korunan klasör: <c>%LOCALAPPDATA%\Soz</c> için <c>%LOCALAPPDATA%\Soz-veri</c>.</summary>
    internal static string KorunanKlasor(string kurulumKoku)
    {
        var tam = Path.TrimEndingDirectorySeparator(Path.GetFullPath(kurulumKoku));
        var ust = Path.GetDirectoryName(tam) ?? tam;
        return Path.Combine(ust, KorunanAd);
    }

    internal sealed record Sonuc(string? Klasor, bool Tasindi, bool Kopyalandi, string? Hata);

    /// <summary>Veriyi korunan klasöre taşır (yoksa hiçbir şey yapmaz). Klasör doluysa tarih damgalı yeni ad kullanılır, üstüne yazılmaz.</summary>
    internal static Sonuc Koru(string veriKlasoru, string kurulumKoku, Func<DateTime>? simdi = null)
    {
        try
        {
            if (!Directory.Exists(veriKlasoru)) return new Sonuc(null, false, false, null);
            var hedef = KorunanKlasor(kurulumKoku);
            var damga = (simdi ?? (() => DateTime.Now))().ToString("yyyyMMdd-HHmmss");
            if (Directory.Exists(hedef) || File.Exists(hedef)) hedef += "-" + damga;
            for (var i = 2; Directory.Exists(hedef) || File.Exists(hedef); i++) hedef = KorunanKlasor(kurulumKoku) + "-" + damga + "-" + i;

            Exception? son = null;
            for (var deneme = 0; deneme < 8; deneme++)
            {
                try { Directory.Move(veriKlasoru, hedef); NotYaz(hedef, kurulumKoku, simdi); return new Sonuc(hedef, true, false, null); }
                catch (Exception ex) { son = ex; Thread.Sleep(400); } // süreç dosyaları henüz bırakmamış olabilir
            }
            // Taşınamadı: kopyala. Kopyalanamayan dosya varsa hata olarak bildirilir ama kopya tutulur.
            var atlanan = KlasoruKopyala(veriKlasoru, hedef);
            NotYaz(hedef, kurulumKoku, simdi);
            return new Sonuc(hedef, false, true, atlanan == 0 ? null : $"{atlanan} dosya kopyalanamadı ({son?.Message})");
        }
        catch (Exception ex) { return new Sonuc(null, false, false, ex.Message); }
    }

    private static int KlasoruKopyala(string kaynak, string hedef)
    {
        var atlanan = 0;
        Directory.CreateDirectory(hedef);
        foreach (var f in Directory.EnumerateFiles(kaynak))
        {
            try { File.Copy(f, Path.Combine(hedef, Path.GetFileName(f)), overwrite: false); } catch { atlanan++; }
        }
        foreach (var d in Directory.EnumerateDirectories(kaynak))
        {
            try
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) { atlanan++; continue; }
                atlanan += KlasoruKopyala(d, Path.Combine(hedef, Path.GetFileName(d)));
            }
            catch { atlanan++; }
        }
        return atlanan;
    }

    private static void NotYaz(string klasor, string kurulumKoku, Func<DateTime>? simdi)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Söz kaldırılırken verilerini senin için sakladı.");
            sb.AppendLine();
            sb.AppendLine("Bu klasörde iş geçmişin, deneme sonuçların ve ayarların duruyor.");
            sb.AppendLine("Söz'ü yeniden kurarsan bunlar ilk açılışta kendiliğinden geri gelir.");
            sb.AppendLine("Söz'ü bir daha kurmayacaksan bu klasörü kendin silebilirsin. Silince geri getirilemez.");
            sb.AppendLine();
            sb.AppendLine("Klasör: " + klasor);
            sb.AppendLine("Önceki yeri: " + Path.Combine(kurulumKoku, "veri"));
            sb.AppendLine("Tarih: " + (simdi ?? (() => DateTime.Now))().ToString("yyyy-MM-dd HH:mm"));
            File.WriteAllText(Path.Combine(klasor, NotAdi), sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* not yazılamadıysa veri yine de yerinde */ }
    }

    /// <summary>
    /// Yeniden kurulumdan sonraki ilk açılış: veri klasöründe veri tabanı yok ama korunan klasörde var ise içeriği geri taşır.
    /// Var olan dosyaların üstüne yazmaz (çakışanlar korunan klasörde kalır). Geri taşınan satır sayısını döndürür.
    /// </summary>
    internal static int GeriYukle(string veriKlasoru, string kurulumKoku)
    {
        try
        {
            var korunan = KorunanKlasor(kurulumKoku);
            if (!Directory.Exists(korunan) || !File.Exists(Path.Combine(korunan, "soz.db"))) return 0;
            if (File.Exists(Path.Combine(veriKlasoru, "soz.db"))) return 0; // zaten veri var: karıştırma
            if (!Directory.Exists(veriKlasoru))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(veriKlasoru))!);
                Directory.Move(korunan, veriKlasoru);
                TryDeleteFile(Path.Combine(veriKlasoru, NotAdi));
                return 1;
            }
            var tasinan = 0;
            foreach (var e in Directory.EnumerateFileSystemEntries(korunan).ToList())
            {
                var ad = Path.GetFileName(e);
                if (ad.Equals(NotAdi, StringComparison.OrdinalIgnoreCase)) continue;
                var hedef = Path.Combine(veriKlasoru, ad);
                if (File.Exists(hedef) || Directory.Exists(hedef)) continue;
                try { if (Directory.Exists(e)) Directory.Move(e, hedef); else File.Move(e, hedef); tasinan++; } catch { }
            }
            if (tasinan > 0 && !Directory.EnumerateFileSystemEntries(korunan).Any(x => !Path.GetFileName(x).Equals(NotAdi, StringComparison.OrdinalIgnoreCase)))
            {
                TryDeleteFile(Path.Combine(korunan, NotAdi));
                try { Directory.Delete(korunan, recursive: false); } catch { }
            }
            return tasinan;
        }
        catch { return 0; }
    }

    private static void TryDeleteFile(string p) { try { File.Delete(p); } catch { } }
}
