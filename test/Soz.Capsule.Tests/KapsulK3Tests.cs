using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>k3: kapsül yorgunluğu düzeltmeleri. A: hap yazısı ne beklediğini söyler. B: panel bir tıkta açılır (uyku, çift tık, ön bakış). C: görev çubuğu önizlemesi.</summary>
public class KapsulK3Tests
{
    private static KapsulMantik Yeni(Action<Ayar>? ayarla = null)
    {
        var a = new Ayar(); ayarla?.Invoke(a);
        var m = new KapsulMantik(a, s => s.Length * 7.0);
        m.BostaSifirla(0);
        return m;
    }
    private static (KapsulMantik M, PointerYonetici P) YeniPtr(Action<Ayar>? ayarla = null)
    {
        var m = Yeni(ayarla); return (m, new PointerYonetici(m));
    }
    private static readonly Dik Kapsul = new(900, 400, 1000, 444);
    private static Is[] Hepsi(int soru, int karar, int kaldi, int bitti)
    {
        var l = new List<Is>();
        for (var i = 0; i < soru; i++) l.Add(new Is($"q:{i}", IsTuru.Soru, "S", "s"));
        for (var i = 0; i < karar; i++) l.Add(new Is($"d:{i}", IsTuru.Karar, "K", "k"));
        for (var i = 0; i < kaldi; i++) l.Add(new Is($"f:{i}", IsTuru.Kaldi, "F", "f"));
        for (var i = 0; i < bitti; i++) l.Add(new Is($"b:{i}", IsTuru.Bitti, "B", "b"));
        return l.ToArray();
    }
    private static string Yazi(KapsulMantik m, params Is[] isler) { m.IsleriGuncelle(isler, 0, sessiz: true, kesin: true); return m.IcerikHesapla(0)!.Yazi; }

    // ------------------------------------------------------------ A: hap yazısı
    [Fact]
    public void Tek_tur_cok_is_o_turu_soyler()
    {
        Assert.Equal("5 karar bekliyor", Yazi(Yeni(), Hepsi(0, 5, 0, 0)));
        Assert.Equal("3 soru bekliyor", Yazi(Yeni(), Hepsi(3, 0, 0, 0)));
        Assert.Equal("4 iş bitti", Yazi(Yeni(), Hepsi(0, 0, 0, 4)));
        Assert.Equal("2 iş olmadı", Yazi(Yeni(), Hepsi(0, 0, 2, 0)));
    }

    [Fact]
    public void Tek_is_eskisi_gibi_tek_sozcuk()
        => Assert.Equal("karar", Yazi(Yeni(), Hepsi(0, 1, 0, 0)));

    [Fact]
    public void Karisik_turde_oncelik_sirasiyla_turler_ve_sayilari_yazilir_bekliyor_yazmaz()
    {
        var y = Yazi(Yeni(), Hepsi(3, 2, 0, 3));
        Assert.Equal("3 soru, 2 karar, 3 bitti", y);
        Assert.DoesNotContain("bekliyor", y);
    }

    [Fact]
    public void Karisik_yazi_sigmazsa_en_onemli_tur_ve_toplama_kisalir()
    {
        var m = Yeni();
        var y = Yazi(m, Hepsi(12, 10, 10, 11));
        Assert.Equal("12 soru, toplam 43", y);
        var g = m.GorunumHesapla(0);
        Assert.True(g.W <= m.Ayar.PillMax);
    }

    [Fact]
    public void Dar_hapta_tek_tur_kisa_biciminde_ve_en_sonda_sadece_sayida_siger()
    {
        var dar = Yeni(a => a.PillMax = 150);          // yazıya 150-44-18 = 88 dp = 12 karakter
        Assert.Equal("5 karar", Yazi(dar, Hepsi(0, 5, 0, 0)));
        var cokDar = Yeni(a => a.PillMax = 120);       // 58 dp = 8 karakter
        var y = Yazi(cokDar, Hepsi(12, 10, 10, 11));
        Assert.Equal("43 iş", y);
        Assert.True(cokDar.GorunumHesapla(0).W <= 120);
    }

    // ------------------------------------------------------------ B1: uyku
    [Fact]
    public void Soluk_kapsule_tek_tik_paneli_acar()
    {
        var m = Yeni(); m.Sev = Seviye.Soluk;
        m.SolTik(0, m.TikUykulu(0));
        Assert.Equal(PanelKipi.Tam, m.Panel); Assert.Equal(Seviye.Belirgin, m.Sev);
    }

    [Fact]
    public void Uyut_dugmesinden_sonra_ilk_tik_de_paneli_acar()
    {
        var (m, p) = YeniPtr();
        m.Sev = Seviye.Soluk; m.Uyut(0);
        Assert.True(m.Sev == Seviye.Uyku);
        p.SolBasildi(100, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Uyku);
        p.SolBirakildi(150, 950, 420);
        Assert.Equal(PanelKipi.Tam, m.Panel);
    }

    [Fact]
    public void Uykudan_yeni_uyanmis_kapsulde_250ms_icindeki_tik_paneli_acar()
    {
        var (m, p) = YeniPtr(); m.Sev = Seviye.Uyku; m.Uyandir(1000);
        p.SolBasildi(1100, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        Assert.True(p.UykuluTik);
        p.SolBirakildi(1150, 950, 420);
        Assert.Equal(PanelKipi.Tam, m.Panel);
    }

    // ------------------------------------------------------------ B2: çift tık
    [Fact]
    public void Panel_acan_tikin_ardindan_gelen_sabirsiz_ikinci_tik_paneli_kapatmaz_giris_acmaz()
    {
        var (m, p) = YeniPtr();
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        Assert.Equal(PointerYonetici.Sonuc.Tik, p.SolBirakildi(50, 950, 420));
        Assert.Equal(PanelKipi.Tam, m.Panel);
        m.Eylemler.Clear();
        p.SolBasildi(300, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin); p.SolBirakildi(350, 950, 420);
        Assert.Equal(PanelKipi.Tam, m.Panel);
        Assert.False(m.Giris);
        Assert.Empty(m.Eylemler);
    }

    [Fact]
    public void Uc_sabirsiz_tik_panel_acilirken_hepsi_yutulur()
    {
        var (m, p) = YeniPtr();
        foreach (var t in new long[] { 0, 600, 900, 1200 })
        { p.SolBasildi(t, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin); p.SolBirakildi(t + 40, 950, 420); }
        Assert.Equal(PanelKipi.Tam, m.Panel); Assert.False(m.Giris);
    }

    [Fact]
    public void Uyku_tikiyla_acilan_panelin_ikinci_tikida_da_yutulur()
    {
        var (m, p) = YeniPtr(); m.Sev = Seviye.Uyku;
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Uyku); p.SolBirakildi(50, 950, 420);
        p.SolBasildi(300, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin); p.SolBirakildi(350, 950, 420);
        Assert.Equal(PanelKipi.Tam, m.Panel); Assert.False(m.Giris);
    }

    [Fact]
    public void Panel_uzun_suredir_aciksa_cift_tik_eskisi_gibi_paneli_kapatip_yazi_satirini_acar()
    {
        var m = Yeni();
        m.TamPanelAc(0, null); m.Eylemler.Clear();
        m.CiftTik(KapsulMantik.PanelAcilisPayiMs + 100);
        Assert.True(m.Giris); Assert.Equal(PanelKipi.Yok, m.Panel);
        Assert.Contains(m.Eylemler, e => e.Tur == EylemTuru.GirisAc);
    }

    [Fact]
    public void Panel_yokken_cift_tik_yazi_satirini_acar()
    {
        var m = Yeni(); m.CiftTik(10);
        Assert.True(m.Giris);
    }

    [Fact]
    public void Tek_tik_panel_acmayi_geciktirmez_eylem_hemen_siraya_girer()
    {
        var (m, p) = YeniPtr();
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        p.SolBirakildi(40, 950, 420);
        Assert.Contains(m.Eylemler, e => e.Tur == EylemTuru.PanelAc && e.Kip == PanelKipi.Tam);
    }

    // ------------------------------------------------------------ B3: ön bakış
    [Fact]
    public void On_bakis_acikken_tam_panel_acilinca_on_bakis_kapatma_eylemi_de_gelir()
    {
        var m = Yeni(); m.IsleriGuncelle(new[] { new Is("j:1", IsTuru.Karar, "K", "k") }, 0, sessiz: true, kesin: true);
        m.HoverGirdi(0, 10, 10); m.Tik(700);
        Assert.Equal(PanelKipi.OnBakis, m.Panel); m.Eylemler.Clear();
        m.SolTik(800, false);
        Assert.Equal(PanelKipi.Tam, m.Panel);
        var l = m.Eylemler.ToList();
        var kapat = l.FindIndex(e => e.Tur == EylemTuru.PanelKapat && e.Kip == PanelKipi.OnBakis);
        var ac = l.FindIndex(e => e.Tur == EylemTuru.PanelAc && e.Kip == PanelKipi.Tam);
        Assert.True(kapat >= 0, "ön bakışı kapatma eylemi yok");
        Assert.True(ac > kapat);
    }

    [Fact]
    public void Panel_yokken_tam_panel_acmak_gereksiz_on_bakis_kapatma_eylemi_uretmez()
    {
        var m = Yeni(); m.SolTik(0, false);
        Assert.DoesNotContain(m.Eylemler, e => e.Tur == EylemTuru.PanelKapat);
    }

    // ------------------------------------------------------------ C: görev çubuğu önizlemesi
    [Theory]
    [InlineData("Progman")] [InlineData("WorkerW")] [InlineData("Shell_TrayWnd")] [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("MultitaskingViewFrame")] [InlineData("TaskSwitcherWnd")] [InlineData("ForegroundStaging")] [InlineData("XamlExplorerHostIslandWindow")]
    public void Kabuk_ve_gorev_gorunumu_pencereleri_tam_ekran_uygulama_sayilmaz(string sinif)
        => Assert.True(PencereKurallari.TamEkranSayilmazSinif(sinif));

    [Theory]
    [InlineData("Chrome_WidgetWin_1")] [InlineData("UnityWndClass")] [InlineData("SDL_app")] [InlineData("PotPlayer64")] [InlineData("")]
    public void Gercek_uygulama_pencereleri_tam_ekran_sayilmaya_devam_eder(string sinif)
        => Assert.False(PencereKurallari.TamEkranSayilmazSinif(sinif));

    [Fact]
    public void Ustte_kalma_yalniz_aralik_dolunca_ve_kapsulun_kendi_yuzeyi_kapaliyken_yenilenir()
    {
        Assert.False(PencereKurallari.UstteKalmaYenilensin(4999, 0, 5000, false));
        Assert.True(PencereKurallari.UstteKalmaYenilensin(5000, 0, 5000, false));
        Assert.False(PencereKurallari.UstteKalmaYenilensin(60000, 0, 5000, true));    // panel, ön bakış ya da menü açıkken onları altta bırakma
    }
}
