using System.Globalization;

namespace Soz.Capsule;

/// <summary>Komut satırı. Bilinmeyen bayrak hata sayılır (sessiz yutulmaz).</summary>
internal sealed class Secenekler
{
    public bool Demo, DemoGercekSure, CokNusha, Yardim;
    public double? CikisSn;
    public string? Kisayol, Tema, Mod, AyarDosyasi, DurumKlasoru, PanelKomut, VeriKlasoru, GunlukDosyasi, GoruntuKlasoru, OlcSonuc, AyarYaz;
    public bool Olc, OlcHizli, MotorYok;
    /// <summary>Masaüstü programı kapsülü kendi alt süreci olarak başlattı: komutlar standart çıktıya, olaylar standart girdiden gider; genel kısayolu masaüstü programı tutar.</summary>
    public bool Kabuk;
    public bool Hata;
    public string? HataMetni;

    public static Secenekler Ayristir(string[] a)
    {
        var o = new Secenekler();
        for (var i = 0; i < a.Length; i++)
        {
            string? Sonraki() => i + 1 < a.Length ? a[++i] : null;
            switch (a[i])
            {
                case "--demo": o.Demo = true; break;
                case "--demo-gercek-sure": o.DemoGercekSure = true; break;
                case "--exit-after": { var v = Sonraki(); if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0) o.CikisSn = s; else o.Hatali("--exit-after pozitif sayı ister"); break; }
                case "--tema": o.Tema = Sonraki(); break;
                case "--mod": o.Mod = Sonraki(); break;
                case "--ayar": o.AyarDosyasi = Sonraki(); break;
                case "--ayar-yaz": o.AyarYaz = Sonraki(); break;
                case "--durum": o.DurumKlasoru = Sonraki(); break;
                case "--panel-komut": o.PanelKomut = Sonraki(); break;
                case "--data": o.VeriKlasoru = Sonraki(); break;
                case "--gunluk": o.GunlukDosyasi = Sonraki(); break;
                case "--goruntu": o.GoruntuKlasoru = Sonraki(); break;
                case "--olc": o.Olc = true; o.OlcSonuc = Sonraki(); break;
                case "--olc-hizli": o.OlcHizli = true; break;
                case "--motor-yok": o.MotorYok = true; break;
                case "--cok": o.CokNusha = true; break;
                case "--kabuk": o.Kabuk = true; break;
                case "--kisayol": o.Kisayol = Sonraki(); break;
                case "--help": case "-h": case "/?": o.Yardim = true; break;
                default: o.Hatali("bilinmeyen bayrak: " + a[i]); break;
            }
        }
        return o;
    }

    private void Hatali(string m) { Hata = true; HataMetni ??= m; }

    public const string Yardimi = """
        Soz-Kapsul.exe: Söz'ün yerel kapsülü (ayrı süreç, WinForms ve WebView2 yok).
          --demo                 motor olmadan bütün durumları sırayla gösterir
          --exit-after <sn>      belirtilen süre sonra kendiliğinden kapanır (denemelerde her zaman kullanın)
          --tema cam|terminal|mat|ton|duz|sicak   görsel tema ailesi (çalışırken sağ tık panosundan da değişir)
          --mod sistem|koyu|acik zemin kipi
          --ayar <dosya.json>    sayıların hepsi bu dosyada; çalışırken değişirse yeniden okunur
          --ayar-yaz <dosya>     bütün sayıları varsayılanlarıyla dosyaya yazar ve çıkar
          --durum <klasör>       konum ve günlük klasörü (varsayılan %LOCALAPPDATA%\Soz\Kapsul)
          --data <klasör>        Söz veri klasörü (sunucu.json buradan okunur)
          --panel-komut <exe>    "panel aç" eyleminde başlatılacak program
          --gunluk <dosya>       eylem ve durum günlüğü
          --goruntu <klasör>     demo sırasında kapsül çevresinin ekran görüntülerini PNG olarak yazar
          --olc <sonuc.json>     ölçüm kipi (bellek, işlemci, ilk kare, sürükleme, tıkanma)
          --kisayol <metin>      (yalnız --kabuk ile) masaüstü programının tuttuğu "yeni iş" kısayolu, ekran okuyucu yardımında söylenir ("yok": kısayol alınamadı)
          --kabuk                masaüstü programının alt süreci olarak çalışır (komutlar standart çıktıya, olaylar standart girdiden gider, kısayolu masaüstü programı tutar)
          --cok                  ikinci kopyaya izin ver
        """;
}
