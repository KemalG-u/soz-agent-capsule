using Soz.Shell;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>
/// Yeni kapsülün ürünün tek kapsülü olması: eski kapsülün (masaüstü programının içindeki) yaptığı işlerin yeni kapsüle taşınması.
/// Kapsül ile masaüstü programı arasındaki satır protokolü, sağ tık panosu, kısa bildirim, kapsül seçimi ve çökme kararı. Hepsi saf mantık.
/// </summary>
public class KabukProtokolTests
{
    public static IEnumerable<object[]> Komutlar() =>
        Enum.GetValues<KomutTuru>().Where(t => t != KomutTuru.Bilinmiyor).Select(t => new object[] { (int)t });

    [Theory]
    [MemberData(nameof(Komutlar))]
    public void Her_komut_yazilip_geri_okunur(int turNo)
    {
        var k = new KapsulKomutu((KomutTuru)turNo);
        Assert.Equal(k, KabukProtokol.KomutOku(KabukProtokol.Yaz(k)));
    }

    [Fact]
    public void Panel_komutu_is_kimligini_ve_kapsul_konumunu_tasir()
    {
        var k = new KapsulKomutu(KomutTuru.Panel, "j:7", null, 0, new KabukDik(1500.4, 900, 1560.6, 944));
        var geri = KabukProtokol.KomutOku(KabukProtokol.Yaz(k))!.Value;
        Assert.Equal(KomutTuru.Panel, geri.Tur);
        Assert.Equal("j:7", geri.IsId);
        Assert.Equal(new KabukDik(1500, 900, 1561, 944), geri.Dik);   // tam piksele yuvarlanır
    }

    [Fact]
    public void Metin_komutu_turkce_tirnak_ve_satir_sonuyla_bozulmadan_gider()
    {
        var metin = "Ödeme \"sayfası\" testini çalıştır\nsonra özeti yaz: ğüşiöç İ\\x";
        var geri = KabukProtokol.KomutOku(KabukProtokol.Yaz(new KapsulKomutu(KomutTuru.Metin, null, metin, 0x1A2B3C)))!.Value;
        Assert.Equal(metin, geri.Metin);
        Assert.Equal(0x1A2B3C, geri.Hedef);
        Assert.DoesNotContain('\n', KabukProtokol.Yaz(new KapsulKomutu(KomutTuru.Metin, null, metin)));   // tek satır kalır
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[1,2]")]
    [InlineData("\"panel\"")]
    [InlineData("{\"k\":\"bilinmeyen\"}")]
    [InlineData("{\"k\":5}")]
    [InlineData("{}")]
    [InlineData("Soz-Kapsul hata: bilinmeyen bayrak")]
    public void Bozuk_ya_da_bilinmeyen_satir_yok_sayilir(string? satir)
    {
        Assert.Null(KabukProtokol.KomutOku(satir));
        Assert.Null(KabukProtokol.OlayOku(satir));
    }

    [Fact]
    public void Cok_uzun_satir_yok_sayilir()
    {
        var uzun = "{\"k\":\"metin\",\"m\":\"" + new string('a', KabukProtokol.EnUzunSatir) + "\"}";
        Assert.Null(KabukProtokol.KomutOku(uzun));
    }

    [Fact]
    public void Bozuk_konum_dizisi_komutu_dusurmez_yalniz_konumu_atar()
    {
        var k = KabukProtokol.KomutOku("{\"k\":\"panel\",\"is\":\"j:1\",\"dik\":[1,2,\"x\",4]}")!.Value;
        Assert.Equal(KomutTuru.Panel, k.Tur); Assert.Null(k.Dik);
        Assert.Null(KabukProtokol.KomutOku("{\"k\":\"panel\",\"dik\":[1,2,3]}")!.Value.Dik);
    }

    [Theory]
    [InlineData((int)OlayTuru.Flash, "Paket panoda")]
    [InlineData((int)OlayTuru.Proje, "Söz")]
    [InlineData((int)OlayTuru.Kisayol, null)]
    [InlineData((int)OlayTuru.PanelKapandi, null)]
    [InlineData((int)OlayTuru.Kapat, "masaüstü programı kapanıyor")]
    [InlineData((int)OlayTuru.Tema, "terminal\tkoyu")]
    [InlineData((int)OlayTuru.DikteDurum, "dinliyor\t")]
    [InlineData((int)OlayTuru.DikteDurum, "ceviriyor\t")]
    [InlineData((int)OlayTuru.DikteDurum, "yazdi\t")]
    [InlineData((int)OlayTuru.DikteDurum, "panoda\t")]
    [InlineData((int)OlayTuru.DikteDurum, "kapali\t")]
    public void Her_olay_yazilip_geri_okunur(int turNo, string? metin)
    {
        var o = new KabukOlayi((OlayTuru)turNo, metin);
        Assert.Equal(o, KabukProtokol.OlayOku(KabukProtokol.OlayYaz(o)));
    }

    [Fact]
    public void Tema_ve_dikte_olaylari_hem_json_hem_sekme_satiri_olarak_cozulur()
    {
        var tJson = KabukProtokol.OlayOku(KabukProtokol.OlayYaz(KabukProtokol.TemaOlayi("sicak", "acik")))!.Value;
        Assert.Equal(OlayTuru.Tema, tJson.Tur);
        Assert.Equal(("sicak", "acik"), KabukProtokol.TemaCoz(tJson.Metin));

        var tTab = KabukProtokol.OlayOku("tema\tduz\tkoyu")!.Value;
        Assert.Equal(OlayTuru.Tema, tTab.Tur);
        Assert.Equal(("duz", "koyu"), KabukProtokol.TemaCoz(tTab.Metin));

        foreach (var faz in KabukProtokol.DikteFazlari)
        {
            var dJson = KabukProtokol.OlayOku(KabukProtokol.OlayYaz(KabukProtokol.DikteOlayi(faz, "")))!.Value;
            Assert.Equal(OlayTuru.DikteDurum, dJson.Tur);
            Assert.Equal((faz, ""), KabukProtokol.DikteCoz(dJson.Metin));

            var dTab = KabukProtokol.OlayOku($"dikte\t{faz}\tornek")!.Value;
            Assert.Equal(OlayTuru.DikteDurum, dTab.Tur);
            Assert.Equal((faz, "ornek"), KabukProtokol.DikteCoz(dTab.Metin));
        }
    }

    // ------------------------------------------------------------ panel adresi (iş kimliği → sayfa)
    [Theory]
    [InlineData("j:7", "#/is/7")]
    [InlineData("j:12345", "#/is/12345")]
    [InlineData("o:12", "#/suan?oturum=12")]          // oturum: Şu An sayfası o oturumun satırını gösterir (k10)
    [InlineData("ask:job:abc", "#/is")]
    [InlineData("j:", "#/is")]
    [InlineData("j:7x", "#/is")]
    [InlineData("j:7;alert(1)", "#/is")]
    [InlineData("j:99999999999999", "#/is")]          // uzun sayı: izinli biçimin dışında
    public void Is_kimliginden_panel_adresi(string id, string beklenen) => Assert.Equal(beklenen, KabukProtokol.PanelAdresi(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Kimlik_yoksa_panel_oldugu_yerde_acilir(string? id) => Assert.Null(KabukProtokol.PanelAdresi(id));

    [Theory]
    [InlineData("#/is", true)]
    [InlineData("#/is/7", true)]
    [InlineData("#/ayarlar", true)]
    [InlineData("#/is/7x", false)]
    [InlineData("#/is/", false)]
    [InlineData("#/ayarlar/claude", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("#/is';alert(1);'", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Kabuk_yalniz_izinli_adresi_panele_yazar(string? adres, bool izinli) => Assert.Equal(izinli, KabukProtokol.AdresIzinli(adres));

    [Fact]
    public void Uretilen_her_adres_izinlidir()
    {
        foreach (var id in new[] { "j:1", "j:77", "o:3", "ask:x", "j:", "zz", "j:12ab" })
            Assert.True(KabukProtokol.AdresIzinli(KabukProtokol.PanelAdresi(id)), id);
    }

    // ------------------------------------------------------------ sağ tık panosu (eski kapsülün tepsi menüsü işleri)
    [Fact]
    public void Kabukla_birlikteyken_pano_yeni_is_dikte_paket_ve_ice_al_satirlarini_tasir()
    {
        var kimlikler = KabukProtokol.Pano(true, "cam").Select(p => p.Kimlik).ToList();
        Assert.Equal(new[] { "yeniis", "dikte", "paket", "icealma" }, kimlikler.Take(4));
        Assert.Equal("cikis", kimlikler[^1]);
        Assert.Contains("ayar", kimlikler);
    }

    [Fact]
    public void Tek_basina_calisirken_islemeyen_dugme_gosterilmez()
    {
        var kimlikler = KabukProtokol.Pano(false, "cam").Select(p => p.Kimlik).ToList();
        Assert.DoesNotContain("yeniis", kimlikler); Assert.DoesNotContain("dikte", kimlikler);
        Assert.DoesNotContain("paket", kimlikler); Assert.DoesNotContain("icealma", kimlikler);
        Assert.Equal(new[] { "uyut", "gizle", "sifirla" }, kimlikler.Take(3));   // "Ayarlar" kabuk yokken ölü olduğu için görünmez (k9)
        Assert.DoesNotContain("ayar", kimlikler);
    }

    [Theory]
    [InlineData("cam", "tema:cam")]
    [InlineData("terminal", "tema:terminal")]
    [InlineData("mat", "tema:mat")]
    [InlineData("ton", "tema:ton")]
    [InlineData("duz", "tema:duz")]
    [InlineData("sicak", "tema:sicak")]
    [InlineData("murekkep", "tema:terminal")]
    [InlineData("kagit", "tema:sicak")]
    public void Pano_alti_temayi_gosterir_ve_gecerli_temayi_isaretler(string tema, string beklenenKimlik)
    {
        var pano = KabukProtokol.Pano(true, tema);
        var temalar = pano.Where(p => p.Kimlik.StartsWith("tema:", StringComparison.Ordinal)).Select(p => p.Kimlik).ToList();
        Assert.Equal(new[] { "tema:cam", "tema:terminal", "tema:mat", "tema:ton", "tema:duz", "tema:sicak" }, temalar);
        var isaretli = pano.Where(p => p.Isaretli).Select(p => p.Kimlik).ToList();
        Assert.Equal(new[] { beklenenKimlik }, isaretli);
    }

    [Fact]
    public void Pano_komutlari_dogru_kabuk_komutuna_baglanir_ve_ote_satirlar_kabuk_komutu_degildir()
    {
        Assert.Equal(KomutTuru.YeniIs, KabukProtokol.PanoKomutu("yeniis"));
        Assert.Equal(KomutTuru.Dikte, KabukProtokol.PanoKomutu("dikte"));
        Assert.Equal(KomutTuru.Paket, KabukProtokol.PanoKomutu("paket"));
        Assert.Equal(KomutTuru.IceAl, KabukProtokol.PanoKomutu("icealma"));
        foreach (var id in new[] { "uyut", "gizle", "sifirla", "ayar", "cikis", "tema:cam" }) Assert.Equal(KomutTuru.Bilinmiyor, KabukProtokol.PanoKomutu(id));
        // pano ile komut tablosu birbirini tutar: kabuk satırlarının hepsi bir komuta bağlı
        foreach (var p in KabukProtokol.Pano(true, "cam").Where(x => !x.Ayirici).Take(4)) Assert.NotEqual(KomutTuru.Bilinmiyor, KabukProtokol.PanoKomutu(p.Kimlik));
    }

    // ------------------------------------------------------------ tek örnek
    [Fact]
    public void Tek_ornek_kilidi_durum_klasorune_ozeldir()
    {
        var taban = KabukProtokol.TekOrnekAdi(null);
        Assert.Equal("Soz-Kapsul-Tek-Ornek", taban);
        var a = KabukProtokol.TekOrnekAdi(@"C:\Veri\Soz\kapsul");
        Assert.NotEqual(taban, a);
        Assert.Equal(a, KabukProtokol.TekOrnekAdi(@"c:\veri\soz\KAPSUL\"));          // büyük küçük harf ve sondaki ayraç fark etmez
        Assert.NotEqual(a, KabukProtokol.TekOrnekAdi(@"C:\Baska\kapsul"));          // ayrı veri klasörü ayrı kilit
    }
}

/// <summary>Kapsülün hapında kısa bildirim: masaüstü programının işlere verdiği cevap ("Paket panoda").</summary>
public class KisaBildirimTests
{
    private static KapsulMantik Yeni()
    {
        var m = new KapsulMantik(new Ayar(), s => s.Length * 7.0);
        m.BostaSifirla(0);
        return m;
    }

    [Fact]
    public void Bildirim_uyuyan_kapsulu_uyandirir_ve_hapta_gorunur()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.Bildir("Paket panoda", 1000);
        var g = m.GorunumHesapla(1000);
        Assert.Equal(Seviye.Belirgin, g.Sev);
        Assert.True(g.Hap);
        Assert.Equal("Paket panoda", g.Icerik!.Yazi);
        Assert.False(g.Rozet);                                  // sade bilgi: rozet ve renkli durum yok
    }

    [Fact]
    public void Bildirim_suresi_dolunca_kendiliginden_kalkar_ve_tarih_sorulabilir()
    {
        var m = Yeni();
        m.Bildir("Dikte komutu gönderildi", 5000);
        Assert.Equal(5000 + KapsulMantik.BildirimMs, m.SonrakiMs());
        Assert.True(m.GeciciAktif(5000 + KapsulMantik.BildirimMs - 1));
        Assert.False(m.Tik(5000 + KapsulMantik.BildirimMs - 1));   // vakit gelmedi
        Assert.True(m.Tik(5000 + KapsulMantik.BildirimMs));
        Assert.Null(m.Gecici);
        Assert.False(m.GorunumHesapla(5000 + KapsulMantik.BildirimMs).Icerik is { Yazi: "Dikte komutu gönderildi" });
    }

    [Fact]
    public void Bildirim_orb_durakta_bile_gorunur_sonra_orba_doner()
    {
        var m = Yeni(); m.Durak = Durak.Orb;
        m.Bildir("Metin panoda", 0);
        Assert.True(m.GorunumHesapla(10).Hap);
        m.Tik(KapsulMantik.BildirimMs + 1);
        Assert.False(m.GorunumHesapla(KapsulMantik.BildirimMs + 1).Hap);
    }

    [Fact]
    public void Bekleyen_is_varken_bildirim_once_gosterilir_bitince_is_sayisi_geri_gelir()
    {
        var m = Yeni();
        m.IsleriGuncelle(new[] { new Is("j:1", IsTuru.Kaldi, "Ödeme testi", "ödeme testi"), new Is("j:2", IsTuru.Karar, "Veritabanı", "veritabanı") }, 0, sessiz: true, kesin: true);
        m.Bildir("Paket panoda", 100);
        Assert.Equal("Paket panoda", m.GorunumHesapla(200).Icerik!.Yazi);
        m.Tik(100 + KapsulMantik.BildirimMs);
        Assert.Equal("1 olmadı, 1 karar", m.GorunumHesapla(100 + KapsulMantik.BildirimMs).Icerik!.Yazi);
    }

    [Fact]
    public void Kullanici_gizlediyse_ya_da_metin_bossa_bildirim_yok_sayilir()
    {
        var m = Yeni();
        m.Bildir("", 0); m.Bildir("   ", 0); m.Bildir(null, 0);
        Assert.Null(m.Gecici);
        m.Gizle(Gizlilik.Kullanici, 0);
        m.Bildir("Paket panoda", 10);
        Assert.Null(m.Gecici);
        Assert.True(m.GorunumHesapla(10).Gizli);
    }

    [Fact]
    public void Dikte_surerken_dikte_durumu_bildirimden_onceliklidir()
    {
        var m = Yeni();
        m.DikteBaslat(0);
        m.Bildir("Dikte komutu gönderildi", 10);
        Assert.StartsWith("dinliyor", m.GorunumHesapla(20).Icerik!.Yazi);
    }

    [Fact]
    public void Kisayol_kabuktan_gelince_yazi_satirini_acar()
    {
        // kabuk kısayolu tutar ve kapsüle "kisayol" olayı yollar; kapsül aynı mantık işini yapar (Ctrl+Alt+J = GİRİŞ)
        var m = Yeni();
        m.Kisayol(10);
        Assert.True(m.Giris);
        Assert.Contains(m.Eylemler, e => e.Tur == EylemTuru.GirisAc);
    }

    [Fact]
    public void Yazi_satirindan_giden_metin_eylem_olarak_cikar_ve_satir_kapanir()
    {
        var m = Yeni(); m.GirisAc(0); m.Eylemler.Clear();
        m.MetinGonder("  testleri koş ", 5);
        var e = m.Eylemler.First(x => x.Tur == EylemTuru.MetinGonder);
        Assert.Equal("testleri koş", e.Metin);
        Assert.False(m.Giris);
    }

    [Fact]
    public void Kullanici_cikis_derse_cikis_eylemi_olusur_kabuk_buna_bakip_programi_kapatir()
    {
        var m = Yeni();
        m.Gizle(Gizlilik.Cikis, 0);
        Assert.Contains(m.Eylemler, e => e.Tur == EylemTuru.Cikis);
    }
}

/// <summary>Masaüstü programının kapsül seçimi ve yeni kapsül çökerse davranışı (saf mantık; süreç başlatmak ayrı).</summary>
public class KapsulPolitikasiTests
{
    [Theory]
    [InlineData(null, null, "Yeni")]            // varsayılan yeni
    [InlineData(null, "yeni", "Yeni")]
    [InlineData(null, "eski", "Eski")]
    [InlineData(null, "ESKI", "Eski")]
    [InlineData(null, " eski ", "Eski")]
    [InlineData(null, "", "Yeni")]
    [InlineData(null, "bilinmeyen", "Yeni")]    // tanınmayan değer sessizce varsayılana düşer
    [InlineData("eski", "yeni", "Eski")]        // komut satırı ayarın önüne geçer (deneme)
    [InlineData("yeni", "eski", "Yeni")]
    [InlineData("", "eski", "Eski")]
    public void Kapsul_turu_ayardan_secilir(string? komut, string? ayar, string beklenen) => Assert.Equal(beklenen, KapsulPolitikasi.Sec(komut, ayar).ToString());

    [Fact]
    public void Ayar_adi_kayit_defterindeki_eski_anahtardir()
    {
        Assert.Equal("capsuleKind", KapsulPolitikasi.AyarAdi);
        Assert.Equal("yeni", KapsulPolitikasi.YeniDeger); Assert.Equal("eski", KapsulPolitikasi.EskiDeger);
    }

    [Fact]
    public void Kapsul_dosyasi_kabugun_yaninda_sonra_kapsul_klasorunde_aranir()
    {
        var yan = Path.Combine("K", "Soz-Kapsul.exe"); var alt = Path.Combine("K", "kapsul", "Soz-Kapsul.exe");
        Assert.Equal(yan, KapsulPolitikasi.ExeBul("K", p => p == yan || p == alt));
        Assert.Equal(alt, KapsulPolitikasi.ExeBul("K", p => p == alt));
        Assert.Null(KapsulPolitikasi.ExeBul("K", _ => false));      // yoksa null: kabuk eski kapsüle düşer
    }

    [Theory]
    [InlineData("light", "acik")]
    [InlineData("dark", "koyu")]
    [InlineData("system", "sistem")]
    [InlineData(null, "sistem")]
    [InlineData("baska", "sistem")]
    public void Tema_tercihi_kapsulun_zemin_kipine_cevrilir(string? tema, string kip) => Assert.Equal(kip, KapsulPolitikasi.ZeminKipi(tema));

    [Fact]
    public void Ilk_cokmede_bir_kez_yeniden_baslatir_ikincide_eski_kapsule_duser()
    {
        var p = new KapsulPolitikasi();
        Assert.Equal(KapsulKarari.Yeniden, p.CikistaKarar());
        Assert.Equal(1, p.YenidenBaslatma);
        Assert.Equal(KapsulKarari.EskiyeDus, p.CikistaKarar());
        Assert.Equal(KapsulKarari.EskiyeDus, p.CikistaKarar());        // sonsuz yeniden başlatma döngüsü yok
        Assert.Equal(1, p.YenidenBaslatma);
    }

    [Fact]
    public void Kullanici_cikis_dediyse_bitis_cokme_sayilmaz()
    {
        var p = new KapsulPolitikasi();
        p.CikisIstendi();
        Assert.Equal(KapsulKarari.Yok, p.CikistaKarar());
        Assert.Equal(0, p.YenidenBaslatma);
    }

    [Fact]
    public void Gunluk_cumleleri_sade_ve_noktalama_kurallarina_uygun()
    {
        foreach (var k in new[] { KapsulKarari.Yeniden, KapsulKarari.EskiyeDus, KapsulKarari.Yok })
        {
            var c = KapsulPolitikasi.Cumle(k, 5);
            Assert.DoesNotContain('\n', c); Assert.DoesNotContain(';', c); Assert.DoesNotContain('—', c);
        }
        Assert.Contains("kod 5", KapsulPolitikasi.Cumle(KapsulKarari.Yeniden, 5));
        Assert.Contains("eski kapsül", KapsulPolitikasi.Cumle(KapsulKarari.EskiyeDus, 0));
    }
}
