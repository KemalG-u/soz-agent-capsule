using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soz.Shell;

/// <summary>
/// Kabuğun kendi küçük tercih dosyası: <c>veri\shell-tercihleri.json</c>. Sunucu ayarlarından ayrıdır, çünkü kaldırıcı ve kurucu kanca
/// sunucu açık olmadan bunu okuyabilmelidir. Bilinmeyen alanlar korunur (elle eklenen bir adres yazarken silinmez).
/// Alanlar:
/// <c>acilistaBasla</c> (true/false; yoksa karar verilmemiş), <c>kaldirirkenVeriyiSil</c> (yalnız kullanıcı açıkça seçerse true),
/// <c>guncellemeAdresi</c> (güncelleme beslemesinin adresi ya da klasörü; yoksa Söz'ün kendi beslemesi, boş dize ise denetim kapalı),
/// <c>guncellemeDenetimi</c> (false ise güncelleme denetimi kapalı; yoksa açık).
/// </summary>
internal sealed class KabukTercihleri
{
    internal const string DosyaAdi = "shell-tercihleri.json";

    public bool? AcilistaBasla { get; set; }
    public bool KaldirirkenVeriyiSil { get; set; }
    public string? GuncellemeAdresi { get; set; }
    /// <summary>Dosyada <c>guncellemeAdresi</c> açıkça boş dize yazılmış: kişi denetimi kapattı.</summary>
    public bool GuncellemeAdresiBos { get; set; }
    /// <summary><c>guncellemeDenetimi</c> alanı (yoksa null, yani açık).</summary>
    public bool? GuncellemeDenetimi { get; set; }
    /// <summary>Seni bekleyen oturum çıkınca Windows bildirimi göster. Varsayılan açık; yalnız kullanıcı kapatırsa false.</summary>
    public bool Bildirimler { get; set; } = true;

    internal static string YolOlustur(string veriKlasoru) => Path.Combine(veriKlasoru, DosyaAdi);

    /// <summary>Dosya yoksa ya da bozuksa boş (hiçbir şey seçilmemiş) tercih döner; hiçbir zaman istisna atmaz.</summary>
    public static KabukTercihleri Oku(string veriKlasoru)
    {
        var t = new KabukTercihleri();
        try
        {
            var yol = YolOlustur(veriKlasoru);
            if (!File.Exists(yol)) return t;
            if (JsonNode.Parse(File.ReadAllText(yol)) is not JsonObject o) return t;
            if (o["acilistaBasla"] is JsonValue a && a.TryGetValue<bool>(out var ab)) t.AcilistaBasla = ab;
            if (o["kaldirirkenVeriyiSil"] is JsonValue k && k.TryGetValue<bool>(out var kb)) t.KaldirirkenVeriyiSil = kb;
            if (o["bildirimler"] is JsonValue bi && bi.TryGetValue<bool>(out var bb)) t.Bildirimler = bb;
            if (o["guncellemeAdresi"] is JsonValue g && g.TryGetValue<string>(out var gs))
            {
                if (string.IsNullOrWhiteSpace(gs)) t.GuncellemeAdresiBos = true; else t.GuncellemeAdresi = gs.Trim();
            }
            if (o["guncellemeDenetimi"] is JsonValue gd && gd.TryGetValue<bool>(out var gdb)) t.GuncellemeDenetimi = gdb;
        }
        catch { /* bozuk dosya: hiçbir şey seçilmemiş say */ }
        return t;
    }

    /// <summary>Yalnız bilinen üç alanı günceller, dosyadaki öteki alanlara dokunmaz. Yazılamazsa false.</summary>
    public bool Yaz(string veriKlasoru)
    {
        try
        {
            Directory.CreateDirectory(veriKlasoru);
            var yol = YolOlustur(veriKlasoru);
            JsonObject o;
            try { o = File.Exists(yol) ? (JsonNode.Parse(File.ReadAllText(yol)) as JsonObject ?? new JsonObject()) : new JsonObject(); }
            catch { o = new JsonObject(); }
            if (AcilistaBasla is { } ab) o["acilistaBasla"] = ab; else o.Remove("acilistaBasla");
            o["kaldirirkenVeriyiSil"] = KaldirirkenVeriyiSil;
            o["bildirimler"] = Bildirimler;
            if (!string.IsNullOrWhiteSpace(GuncellemeAdresi)) o["guncellemeAdresi"] = GuncellemeAdresi;
            else if (GuncellemeAdresiBos) o["guncellemeAdresi"] = "";
            else o.Remove("guncellemeAdresi");
            if (GuncellemeDenetimi is { } gd) o["guncellemeDenetimi"] = gd;
            var gecici = yol + ".tmp";
            File.WriteAllText(gecici, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(gecici, yol, overwrite: true);
            return true;
        }
        catch (Exception ex) { Log.Write("tercih yazılamadı: " + ex.Message); return false; }
    }
}
