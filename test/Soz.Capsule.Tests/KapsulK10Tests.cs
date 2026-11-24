using System.Text.Json;
using Soz.Shell;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>k10: çalışan işte süre, oturum satırı ana pencerede kendi satırını açar, dikte sürerken "Yeni iş" odak almaz.</summary>
public class KapsulK10Tests
{
    private static string Kok()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Soz.Shell"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("urun klasörü bulunamadı");
    }
    private static string Oku(params string[] yol) => File.ReadAllText(Path.Combine(new[] { Kok() }.Concat(yol).ToArray()));

    private static List<Is> Ayristir(string json)
    {
        using var d = JsonDocument.Parse(json);
        return SiraBendeCevabi.Ayristir(d.RootElement)!.Isler.ToList();
    }

    // ------------------------------------------------------------ 2. çalışan işte süre
    private static readonly DateTimeOffset Simdi = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Calisan_is_sunucunun_startedAt_degerinden_sure_yazar()
    {
        var l = Ayristir("""{ "items": [], "running": [ { "jobId": 4, "title": "Rapor", "startedAt": "2026-11-23T11:48:00.000Z", "ageSeconds": 720 } ] }""");
        Assert.Equal("çalışıyor · 12 dk", l[0].SagYazi(Simdi));
    }

    [Fact]
    public void Calisan_is_yalniz_ageSeconds_gelirse_ondan_sure_yazar()
    {
        var l = Ayristir("""{ "items": [], "running": [ { "jobId": 4, "title": "Rapor", "ageSeconds": 3700 } ] }""");
        Assert.Equal("çalışıyor · 1 saat", l[0].SagYazi(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Eski_sunucuda_alan_yoksa_sure_yazilmaz_satir_duzgun_kalir()
    {
        var l = Ayristir("""{ "items": [], "running": [ { "jobId": 4, "title": "Rapor" } ] }""");
        Assert.Equal("çalışıyor", l[0].SagYazi(Simdi));
        var satir = OnBakisIcerigi.Kur(new List<Is>(), l, new List<Is>(), null, Simdi).Single(s => s.Kimlik == "j:4");
        Assert.Equal("çalışıyor", satir.Sag);
        Assert.DoesNotContain("·", satir.Sag);
    }

    [Fact]
    public void Calisan_satir_bekleyen_satirla_ayni_bicimde_yazilir()
    {
        var l = Ayristir("""
            { "items": [ { "id": "ask:job:u:t", "kind": "ask", "reason": "approval", "title": "Ödeme", "jobId": 5, "at": "2026-11-23T11:48:00Z", "signal": true } ],
              "running": [ { "jobId": 4, "title": "Rapor", "startedAt": "2026-11-23T11:48:00Z" } ] }
            """);
        var bekleyen = l.Single(x => x.Bekliyor).SagYazi(Simdi);
        var calisan = l.Single(x => x.Tur == IsTuru.Calisiyor).SagYazi(Simdi);
        Assert.Equal("izin bekliyor · 12 dk", bekleyen);
        Assert.Equal("çalışıyor · 12 dk", calisan);
        Assert.Equal(bekleyen[(bekleyen.IndexOf('·'))..], calisan[(calisan.IndexOf('·'))..]);
    }

    [Fact]
    public void Bozuk_startedAt_ageSeconds_yoksa_sure_uydurmaz()
    {
        var l = Ayristir("""{ "items": [], "running": [ { "jobId": 4, "title": "Rapor", "startedAt": "dün" } ] }""");
        Assert.Equal("çalışıyor", l[0].SagYazi(Simdi));
    }

    // ------------------------------------------------------------ 3. oturum satırı #/suan?oturum=<kimlik>
    [Fact]
    public void Yardimci_oturumu_externalSessionId_ile_ana_pencerede_kendi_satirini_acar()
    {
        var l = Ayristir("""
            { "items": [ { "id": "ask:ses:9:t", "kind": "ask", "reason": "session_permission", "title": "Oturum", "sessionId": 9, "externalSessionId": "abc-123", "signal": true } ], "running": [] }
            """);
        Assert.Equal("x:abc-123", l[0].PanelKimligi);
        Assert.Equal("#/suan?oturum=abc-123", KabukProtokol.PanelAdresi(l[0].PanelKimligi));
    }

    [Fact]
    public void Oturum_kimligi_url_icin_kacislanir_ve_adres_izinli_kalir()
    {
        var adres = KabukProtokol.PanelAdresi("x:ab c/ü&x=1#z")!;
        Assert.Equal("#/suan?oturum=ab%20c%2F%C3%BC%26x%3D1%23z", adres);
        Assert.True(KabukProtokol.AdresIzinli(adres));
        Assert.DoesNotContain("&", adres);
        Assert.DoesNotContain(" ", adres);
    }

    [Fact]
    public void Is_kimligi_oturum_kimliginden_once_gelir_ve_oturum_numarasi_yedektir()
    {
        var isli = new Is("a", IsTuru.Soru, "t", "t", IsNo: 5, OturumNo: 9, DisOturum: "abc");
        Assert.Equal("j:5", isli.PanelKimligi);
        Assert.Equal("#/is/5", KabukProtokol.PanelAdresi(isli.PanelKimligi));
        var dis = new Is("a", IsTuru.Soru, "t", "t", OturumNo: 9, DisOturum: "abc");
        Assert.Equal("x:abc", dis.PanelKimligi);
        var no = new Is("a", IsTuru.Soru, "t", "t", OturumNo: 9);
        Assert.Equal("#/suan?oturum=9", KabukProtokol.PanelAdresi(no.PanelKimligi));
        var bos = new Is("ask:ext:codex:x:t", IsTuru.Soru, "t", "t", DisOturum: "  ");
        Assert.Equal("ask:ext:codex:x:t", bos.PanelKimligi);   // boşluk kimlik sayılmaz
        Assert.Equal("#/suan", KabukProtokol.PanelAdresi(bos.PanelKimligi));
    }

    [Theory]
    [InlineData("x:")]
    [InlineData("x:   ")]
    [InlineData("o:")]
    [InlineData("x:a\nb")]
    public void Bos_ya_da_bozuk_oturum_kimligi_duz_su_an_sayfasina_gider(string id) => Assert.Equal("#/suan", KabukProtokol.PanelAdresi(id));

    [Fact]
    public void Cok_uzun_oturum_kimligi_kesilmez_duz_su_an_sayfasina_gider()
    {
        Assert.Equal("#/suan", KabukProtokol.PanelAdresi("x:" + new string('a', 500)));
        var enUzun = KabukProtokol.PanelAdresi("x:" + new string('a', KabukProtokol.OturumKimligiEn))!;
        Assert.True(KabukProtokol.AdresIzinli(enUzun));
    }

    [Theory]
    [InlineData("#/suan?oturum=abc-123_x.y~z%20k", true)]
    [InlineData("#/suan?oturum=", false)]
    [InlineData("#/suan?oturum=a b", false)]
    [InlineData("#/suan?oturum=a';alert(1);'", false)]
    [InlineData("#/suan?oturum=a&yeni=1", false)]
    [InlineData("#/suan?oturum=<script>", false)]
    [InlineData("#/is?oturum=abc", false)]
    public void Oturum_adresi_yalniz_guvenli_karakterlerle_izinlidir(string adres, bool izinli) => Assert.Equal(izinli, KabukProtokol.AdresIzinli(adres));

    [Fact]
    public void Gezinme_betigi_oturum_adresi_zaten_aciksa_once_duz_sayfaya_gider()
    {
        var js = KabukProtokol.GezinmeBetigi("#/suan?oturum=abc")!;
        Assert.Contains("location.hash===h", js);
        Assert.Contains("\"#/suan?oturum=abc\"", js);
        Assert.Equal("location.hash=\"#/is/7\"", KabukProtokol.GezinmeBetigi("#/is/7"));
        Assert.Null(KabukProtokol.GezinmeBetigi("#/suan?oturum=a';alert(1);'"));
        Assert.Null(KabukProtokol.GezinmeBetigi(null));
    }

    [Fact]
    public void Arayuz_oturum_adresini_okuyor_ve_panel_gezinmeyi_ayni_kapidan_yapar()
    {
        // arayüz tarafı: router.svelte.ts "oturum" parametresini okur (ana ekran işçisi ekledi); doğrulanan yalnız kaynakta varlığıdır
        var ui = Path.Combine(Kok(), "ui", "src", "lib", "router.svelte.ts");
        if (File.Exists(ui)) Assert.Contains("oturum=", File.ReadAllText(ui));
        Assert.Contains("KabukProtokol.GezinmeBetigi(hash)", Oku("src", "Soz.Shell", "PanelForm.cs"));
    }

    // ------------------------------------------------------------ 4. dikte sürerken "Yeni iş" odak almaz
    [Theory]
    [InlineData((byte)DictationHostStatus.Recording, 0)]
    [InlineData((byte)DictationHostStatus.Processing, 0)]
    [InlineData((byte)DictationHostStatus.Recording, 14999)]
    public void Dikte_suruyorsa_yeni_is_odagi_ertelenir(byte durum, int ms)
        => Assert.Equal(YeniIsOdak.Ertele, OdakPolitikasi.YeniIsKarari((DictationHostStatus)durum, TimeSpan.FromMilliseconds(ms)));

    [Theory]
    [InlineData((byte)DictationHostStatus.Recording, 15000)]
    [InlineData((byte)DictationHostStatus.Processing, 60000)]
    public void Erteleme_en_cok_15_sn_surer(byte durum, int ms)
        => Assert.Equal(YeniIsOdak.Simdi, OdakPolitikasi.YeniIsKarari((DictationHostStatus)durum, TimeSpan.FromMilliseconds(ms)));

    [Theory]
    [InlineData((byte)DictationHostStatus.Unavailable)]   // durum okunamıyor: bugünkü davranış
    [InlineData((byte)DictationHostStatus.Idle)]
    [InlineData((byte)DictationHostStatus.Delivered)]
    [InlineData((byte)DictationHostStatus.ClipboardOnly)]
    public void Durum_okunamiyorsa_ya_da_dikte_suruyorsa_degilse_odak_hemen_alinir(byte durum)
        => Assert.Equal(YeniIsOdak.Simdi, OdakPolitikasi.YeniIsKarari((DictationHostStatus)durum, TimeSpan.Zero));

    [Fact]
    public void Anlamsiz_negatif_sure_odagi_ertelemez_ve_sinir_15_sn_dir()
    {
        Assert.Equal(YeniIsOdak.Simdi, OdakPolitikasi.YeniIsKarari(DictationHostStatus.Recording, TimeSpan.FromSeconds(-1)));
        Assert.Equal(TimeSpan.FromSeconds(15), OdakPolitikasi.YeniIsEnCokBekleme);
    }

    [Fact]
    public void Yeni_is_kabukta_karari_kullanir_zamanlayici_ve_dikte_bitince_odak_verir()
    {
        var kaynak = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.Contains("OdakPolitikasi.YeniIsKarari(_sonDikteDurumu, TimeSpan.Zero)", kaynak);
        Assert.Contains("p.OpenNear(PanelAnchor(), odakAl: false)", kaynak);
        Assert.Contains("_yeniIsZamanlayici.Tick += (_, _) => YeniIsOdagiVer()", kaynak);
        // dikte durumu her güncellendiğinde bekleyen odak yeniden değerlendirilir
        Assert.Matches(@"_sonDikteDurumu = status;[\s\S]{0,200}YeniIsBekleyeniDene\(\);", kaynak);
    }
}
