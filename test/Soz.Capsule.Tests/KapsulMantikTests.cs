using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>P1 durum makinesi ve P2 girdi sözleşmesi (saf mantık: pencere, saat, fare yok; saat milisaniye olarak verilir).</summary>
public class KapsulMantikTests
{
    private static KapsulMantik Yeni(Action<Ayar>? ayarla = null)
    {
        var a = new Ayar(); ayarla?.Invoke(a);
        var m = new KapsulMantik(a, s => s.Length * 7.0);
        m.BostaSifirla(0);
        return m;
    }
    private static Is Kaldi(string id = "j:1") => new(id, IsTuru.Kaldi, "Ödeme testi", "ödeme testi");
    private static Is Karar(string id = "j:2") => new(id, IsTuru.Karar, "Veritabanı", "veritabanı kararı");
    private static Is Soru(string id = "j:3") => new(id, IsTuru.Soru, "Sürüm", "sürüm sorusu");
    private static Is Bitti(string id = "j:4") => new(id, IsTuru.Bitti, "Giriş", "giriş düzeltmesi");
    private static Is Calisan(string id = "j:5") => new(id, IsTuru.Calisiyor, "Derleme", "derleme");
    private static bool EylemVar(KapsulMantik m, EylemTuru t, PanelKipi? kip = null) => m.Eylemler.Any(e => e.Tur == t && (kip == null || e.Kip == kip));

    // ------------------------------------------------------------ yaklaşma formülü
    [Theory]
    [InlineData(0.10, 0, 40, 1.0)]
    [InlineData(0.10, 40, 40, 0.10)]
    [InlineData(0.10, 60, 40, 0.10)]
    [InlineData(0.10, 20, 40, 0.325)]
    [InlineData(0.60, 10, 40, 0.60 + 0.40 * 0.5625)]
    public void Yaklasma_formulu_P1(double taban, double d, double r, double beklenen)
        => Assert.Equal(beklenen, Yaklasma.Opaklik(taban, d, r), 6);

    [Fact]
    public void Yaklasma_yaricap_sifirsa_kapali_ve_negatif_uzaklik_tam_opak()
    {
        Assert.Equal(0.3, Yaklasma.Opaklik(0.3, 5, 0), 6);
        Assert.Equal(1.0, Yaklasma.Opaklik(0.3, -3, 40), 6);
    }

    [Fact]
    public void Yaklasma_uzaklik_pisagor() => Assert.Equal(5, Yaklasma.Uzaklik(3, 4, 0, 0), 9);

    [Fact]
    public void Uyku_opakligi_zemine_gore_karisir_ve_tabanin_altina_inmez()
    {
        var a = new Ayar();
        Assert.Equal(0.42, Yaklasma.UykuOpaklik(a, 0), 6);
        Assert.Equal(0.50, Yaklasma.UykuOpaklik(a, 1), 6);
        Assert.Equal(0.46, Yaklasma.UykuOpaklik(a, 0.5), 6);
        a.SleepD = 0.1; a.SleepL = 0.1; a.SleepFloor = 0.2;
        Assert.Equal(0.2, Yaklasma.UykuOpaklik(a, 0.5), 6);
    }

    [Fact]
    public void Gorunum_uyku_noktasinda_imlec_yaklasinca_opaklik_artar()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku; m.ZeminParlaklik = 0;
        var uzak = m.GorunumHesapla(0);
        Assert.Equal(0.42, uzak.Op, 6);
        m.ImlecUzaklik = 20;
        var yakin = m.GorunumHesapla(0);
        Assert.Equal(0.42 + 0.58 * 0.25, yakin.Op, 6);
        m.ImlecUzaklik = 0;
        Assert.Equal(1.0, m.GorunumHesapla(0).Op, 6);
    }

    // ------------------------------------------------------------ süreler: belirgin, soluk, uyku
    [Fact]
    public void Bosta_belirgin_4sn_soluk_sonra_30sn_uyku()
    {
        var m = Yeni();
        Assert.Equal(Seviye.Belirgin, m.Sev);
        Assert.Equal(4000, m.SonrakiMs());
        Assert.False(m.Tik(3999)); Assert.Equal(Seviye.Belirgin, m.Sev);
        Assert.True(m.Tik(4000)); Assert.Equal(Seviye.Soluk, m.Sev);
        Assert.Equal(34000, m.SonrakiMs());
        m.Tik(34000);
        Assert.Equal(Seviye.Uyku, m.Sev);
        Assert.Null(m.SonrakiMs()); // uykuda hiçbir zamanlayıcı kalmaz
    }

    [Fact]
    public void Dinlenme_durumlu_ise_uykuya_inmez_soluk_kalir()
    {
        var m = Yeni(a => a.Rest = "durumlu");
        m.Tik(4000);
        Assert.Equal(Seviye.Soluk, m.Sev);
        Assert.Null(m.SonrakiMs());
        var g = m.GorunumHesapla(4000);
        Assert.True(g.Hap); Assert.Equal("hazır", g.Icerik!.Yazi);
    }

    [Fact]
    public void Dinlenme_orb_ise_uyku_yerine_soluk_orb()
    {
        var m = Yeni(a => a.Rest = "orb");
        m.Tik(4000);
        Assert.Equal(Seviye.Soluk, m.Sev);
        Assert.Equal(0.6, m.GorunumHesapla(4000).Op, 6);
        Assert.False(m.GorunumHesapla(4000).Hap);
    }

    [Fact]
    public void Boyutlar_seviyeye_gore_14_34_44()
    {
        var m = Yeni();
        Assert.Equal(44, m.GorunumHesapla(0).H);
        m.Sev = Seviye.Soluk; Assert.Equal(34, m.GorunumHesapla(0).H);
        m.Sev = Seviye.Uyku; Assert.Equal(14, m.GorunumHesapla(0).H);
        Assert.Equal(14, m.GorunumHesapla(0).W);
    }

    // ------------------------------------------------------------ dikkat olayı
    [Fact]
    public void Dikkat_uykudan_cikarir_6sn_belirgin_sonra_soluk_durumlu_sonra_uyku_rozet_kalir()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { Bitti() }, 100000);
        Assert.Equal(Seviye.Belirgin, m.Sev);
        Assert.Equal(106000, m.SonrakiMs());
        var g = m.GorunumHesapla(100000);
        Assert.True(g.Hap); Assert.Equal("bitti", g.Icerik!.Yazi);
        m.Tik(106000);
        Assert.Equal(Seviye.Soluk, m.Sev);
        Assert.True(m.GorunumHesapla(106000).Hap);
        m.Tik(136000);
        var uyku = m.GorunumHesapla(136000);
        Assert.Equal(Seviye.Uyku, uyku.Sev); Assert.False(uyku.Hap); Assert.True(uyku.Rozet);
    }

    [Fact]
    public void Dikkat_ses_ayari_kapaliysa_ses_eylemi_yok_acikse_var()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.IsleriGuncelle(new[] { Bitti() }, 0);
        Assert.False(EylemVar(m, EylemTuru.Ses));
        var m2 = Yeni(a => a.Sound = 1); m2.Sev = Seviye.Uyku;
        m2.IsleriGuncelle(new[] { Bitti() }, 0);
        Assert.True(EylemVar(m2, EylemTuru.Ses));
    }

    [Fact]
    public void Ayni_liste_tekrar_gelince_dikkat_olayi_olmaz()
    {
        var m = Yeni(); m.Sev = Seviye.Soluk;
        m.IsleriGuncelle(new[] { Bitti() }, 0);
        m.Tik(6000); Assert.Equal(Seviye.Soluk, m.Sev);
        m.IsleriGuncelle(new[] { Bitti() }, 7000);
        Assert.Equal(Seviye.Soluk, m.Sev);
    }

    [Fact]
    public void Icerik_sirasi_dikte_bekleyen_calisan_hazir()
    {
        var m = Yeni(a => a.Rest = "durumlu");
        Assert.Equal("hazır", m.IcerikHesapla(0)!.Yazi);
        m.IsleriGuncelle(new[] { Calisan("a"), Calisan("b") }, 0);
        Assert.Equal("2 çalışıyor", m.IcerikHesapla(0)!.Yazi);
        m.IsleriGuncelle(new[] { Calisan("a"), Karar() }, 0);
        Assert.Equal("karar", m.IcerikHesapla(0)!.Yazi);
        m.IsleriGuncelle(new[] { Kaldi(), Karar() }, 0);
        Assert.Equal("1 olmadı, 1 karar", m.IcerikHesapla(0)!.Yazi);     // k3: karışık türde yalnız sayı değil, ne beklediği yazılır
        m.DikteBaslat(0);
        Assert.StartsWith("dinliyor", m.IcerikHesapla(1000)!.Yazi);
    }

    [Fact]
    public void Bekleyenler_oncelik_sirasi_kaldi_karar_soru_bitti()
    {
        var m = Yeni();
        m.IsleriGuncelle(new[] { Bitti(), Soru(), Karar(), Kaldi() }, 0);
        // d2m: sunucu tablosu (AttentionKinds): soru 1, karar ve kaldı 2 (aynı önemde, kimliğe göre), bitti 4. Kaldı artık sorudan hemen sonra, bitti en sonda.
        Assert.Equal(new[] { "j:3", "j:1", "j:2", "j:4" }, m.Bekleyenler.Select(j => j.Id).ToArray());
    }

    [Fact]
    public void Sunucudan_gelen_priority_yedek_siranin_ustune_gecer()
    {
        var m = Yeni();
        var soru = Soru() with { Sira = 9 };            // sunucu bu soruyu en sona koyduysa kapsül kendi tablosunu kullanmaz
        var bitti = Bitti() with { Sira = 1 };
        m.IsleriGuncelle(new[] { soru, bitti, Karar() }, 0);
        Assert.Equal(new[] { "j:4", "j:2", "j:3" }, m.Bekleyenler.Select(j => j.Id).ToArray());
    }

    // d2m: "emin değilim" (uncertain) kapsülde ayrı sınıf: /api/now cevabından okunur, seni bekleyen sayılmaz, uyandırmaz ve sıranın sonunda durur.
    [Fact]
    public void Belirsiz_oturum_now_cevabindan_okunur_bekleyen_sayilmaz_uyandirmaz_en_sonda()
    {
        using var d = System.Text.Json.JsonDocument.Parse("""
            {"needsYou":[{"sessionId":7,"title":"Fatura düzeni","state":"waiting","question":"Hangisi?"}],
             "uncertain":[{"sessionId":9,"title":"Rapor","state":"uncertain","endClass":"tool_call_pending","stateConfidence":"unknown"}],
             "waitingJobs":[],"runningJobs":[],"counts":{"jobsPassed":1}}
            """);
        var liste = MotorCevabi.NowAyristir(d.RootElement);
        var u = Assert.Single(liste, x => x.Tur == IsTuru.Belirsiz);
        Assert.Equal("o:9", u.Id);
        Assert.False(u.Sinyal);                       // kapsülü uyandırmaz
        Assert.False(u.Bekliyor);                     // seni bekleyen sayılmaz
        Assert.Equal(5, u.Oncelik);
        Assert.Equal("belirsiz", u.Soz);
        var m = Yeni();
        m.IsleriGuncelle(liste.ToArray(), 0);
        var bekleyen = m.Bekleyenler.Select(x => x.Id).ToArray();
        Assert.DoesNotContain("o:9", bekleyen);       // bekleyenler listesine girmez
        Assert.Contains("o:7", bekleyen);
    }

    // d2m: /api/attention öğesindeki priority aynen kullanılır; kapsül sırayı kendi icat etmez, kanıtı kalan iş (blocked) kabul bekleyenle (accept) aynı önemde gelir.
    [Fact]
    public void Siradaki_priority_alani_attention_cevabindan_aynen_okunur()
    {
        using var d = System.Text.Json.JsonDocument.Parse("""
            {"items":[
              {"id":"a1","kind":"done","title":"Bitti işi","priority":4,"signal":true},
              {"id":"a2","kind":"blocked","title":"Kalan iş","priority":2,"signal":true},
              {"id":"a3","kind":"ask","title":"Soru","priority":1,"signal":true},
              {"id":"a4","kind":"accept","title":"Kabul","priority":2,"signal":true},
              {"id":"a5","kind":"uncertain","title":"Emin değilim","priority":5,"signal":false}],
             "running":[]}
            """);
        var c = SiraBendeCevabi.Ayristir(d.RootElement)!;
        Assert.Equal(new[] { 4, 2, 1, 2, 5 }, c.Isler.Select(x => x.Oncelik).ToArray());
        Assert.Equal(IsTuru.Belirsiz, SiraBendeCevabi.Tur("uncertain"));
        var m = Yeni();
        m.IsleriGuncelle(c.Isler.ToArray(), 0);
        Assert.Equal(new[] { "a3", "a2", "a4", "a1" }, m.Bekleyenler.Select(x => x.Id).ToArray());   // soru, sonra aynı önemde (kimliğe göre) kaldı ve kabul, en sonda bitti; belirsiz bekleyen değil
    }

    [Fact]
    public void Hap_genisligi_en_cok_292_ve_yaziyla_buyur()
    {
        var m = Yeni(); m.Sev = Seviye.Soluk;
        m.IsleriGuncelle(new[] { Bitti() }, 0); m.Tik(6000);
        var g = m.GorunumHesapla(6000);
        Assert.Equal(34 + 18 + 5 * 7, g.W, 6); // "bitti" 5 harf
        var m2 = new KapsulMantik(new Ayar(), s => 1000);
        m2.IsleriGuncelle(new[] { Bitti() }, 0);
        Assert.Equal(292, m2.GorunumHesapla(0).W);
    }

    // ------------------------------------------------------------ imleç, üstünde durma, ön bakış
    [Fact]
    public void Hover_uykuyu_uyandirir_600ms_durunca_on_bakis_acilir()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.HoverGirdi(1000, 10, 10);
        Assert.Equal(Seviye.Belirgin, m.Sev);
        m.Tik(1599); Assert.Equal(PanelKipi.Yok, m.Panel);
        m.Tik(1600);
        Assert.Equal(PanelKipi.OnBakis, m.Panel);
        Assert.True(EylemVar(m, EylemTuru.PanelAc, PanelKipi.OnBakis));
    }

    [Fact]
    public void Hover_4dp_icinde_oynamak_durmayi_bozmaz_disinda_bozar()
    {
        var m = Yeni();
        m.HoverGirdi(1000, 10, 10);
        m.HoverHareket(1300, 13, 10);             // 3 dp: bozmaz
        m.Tik(1600); Assert.Equal(PanelKipi.OnBakis, m.Panel);
        var n = Yeni();
        n.HoverGirdi(1000, 10, 10);
        n.HoverHareket(1300, 20, 10);             // 10 dp: sayaç yeniden başlar
        n.Tik(1600); Assert.Equal(PanelKipi.Yok, n.Panel);
        n.Tik(1900); Assert.Equal(PanelKipi.OnBakis, n.Panel);
    }

    [Fact]
    public void On_bakis_imlec_ayrilinca_300ms_sonra_kapanir_panelin_ustundeyse_kapanmaz()
    {
        var m = Yeni();
        m.HoverGirdi(0, 0, 0); m.Tik(600);
        m.HoverCikti(1000);
        m.Tik(1299); Assert.Equal(PanelKipi.OnBakis, m.Panel);
        m.Tik(1300); Assert.Equal(PanelKipi.Yok, m.Panel);
        Assert.True(EylemVar(m, EylemTuru.PanelKapat, PanelKipi.OnBakis));

        var n = Yeni();
        n.HoverGirdi(0, 0, 0); n.Tik(600);
        n.OnBakisUstunde(true, 900); n.HoverCikti(1000);
        n.Tik(1300); Assert.Equal(PanelKipi.OnBakis, n.Panel);
        n.OnBakisUstunde(false, 1400);
        n.Tik(1700); Assert.Equal(PanelKipi.Yok, n.Panel);
    }

    [Fact]
    public void Surukleme_sonrasi_ayni_yerde_on_bakis_acilmaz_ama_kapsul_hareket_ederse_acilir()
    {
        var m = Yeni();
        m.SurukleBasla(0); m.SurukleBitir(100, 50, 50);
        Assert.True(m.NoDwell);
        m.HoverGirdi(200, 50, 50); m.Tik(1000);
        Assert.Equal(PanelKipi.Yok, m.Panel);
        m.HoverHareket(1100, 60, 50);              // 10 dp: serbest
        m.Tik(1700); Assert.Equal(PanelKipi.OnBakis, m.Panel);
    }

    [Fact]
    public void Ust_uste_giris_menu_panelde_on_bakis_acilmaz()
    {
        var m = Yeni(); m.MenuAc(0);
        m.HoverGirdi(0, 0, 0); m.Tik(700);
        Assert.Equal(PanelKipi.Yok, m.Panel);
    }

    // ------------------------------------------------------------ tıklar
    [Fact]
    public void Sol_tik_belirginde_tam_panel_acar()
    {
        var m = Yeni(); m.IsleriGuncelle(new[] { Karar() }, 0);
        m.SolTik(10, false);
        Assert.Equal(PanelKipi.Tam, m.Panel);
        var e = m.Eylemler.Single(x => x.Tur == EylemTuru.PanelAc);
        Assert.Equal(PanelKipi.Tam, e.Kip); Assert.Equal("j:2", e.IsId);
    }

    [Fact]
    public void Sol_tik_uykuda_hem_uyandirir_hem_paneli_acar()
    {
        // k3 (gözlem 5a): eskiden ilk tık yalnız uyandırıyor, panel için ikinci tık gerekiyordu
        var m = Yeni(); m.Sev = Seviye.Uyku;
        Assert.True(m.TikUykulu(0));
        m.SolTik(0, true);
        Assert.Equal(Seviye.Belirgin, m.Sev);
        Assert.Equal(PanelKipi.Tam, m.Panel);
        Assert.Contains(m.Eylemler, e => e.Tur == EylemTuru.PanelAc && e.Kip == PanelKipi.Tam);
    }

    [Fact]
    public void Uykudan_yeni_uyanmis_250ms_icindeki_tik_de_uykulu_sayilir()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        m.Uyandir(1000);
        Assert.True(m.TikUykulu(1200));
        Assert.False(m.TikUykulu(1300));
    }

    [Fact]
    public void Sag_tik_uykuda_gecirgen_soluk_uyutur_belirgin_menu_acar()
    {
        var m = Yeni(); m.Sev = Seviye.Uyku;
        Assert.True(m.SagTikGecirgen);
        m.SagTik(0, true);
        Assert.Equal(Seviye.Uyku, m.Sev); Assert.False(m.Menu);

        var s = Yeni(); s.Sev = Seviye.Soluk;
        Assert.False(s.SagTikGecirgen);
        s.SagTik(0, true);
        Assert.Equal(Seviye.Uyku, s.Sev); Assert.True(EylemVar(s, EylemTuru.Uyut));

        var b = Yeni();
        b.SagTik(0, true);
        Assert.True(b.Menu); Assert.True(EylemVar(b, EylemTuru.MenuAc));
        b.SagTik(1, true);
        Assert.False(b.Menu);
    }

    [Fact]
    public void Sag_tik_bir_geri_menu_panel_giris_sirasiyla()
    {
        var m = Yeni(); m.TamPanelAc(0, null);
        m.SagTik(1, true);
        Assert.Equal(PanelKipi.Yok, m.Panel);
        m.GirisAc(2);
        m.SagTik(3, true);
        Assert.False(m.Giris);
    }

    [Fact]
    public void Sag_tik_menusu_acikken_uyku_gecirgenligi_kapali()
    {
        var m = Yeni(); m.MenuAc(0); m.Sev = Seviye.Uyku;
        Assert.False(m.SagTikGecirgen);
    }

    [Fact]
    public void Cift_tik_girisi_acar_ve_giris_acikken_belirgin_kalir()
    {
        var m = Yeni(); m.Sev = Seviye.Soluk;
        m.CiftTik(0);
        Assert.True(m.Giris); Assert.Equal(Seviye.Belirgin, m.Sev);
        Assert.True(EylemVar(m, EylemTuru.GirisAc));
        Assert.Null(m.SonrakiMs());
        var g = m.GorunumHesapla(0);
        Assert.True(g.Giris); Assert.Equal(420, g.W); Assert.Equal(44, g.H); Assert.Equal("GİRİŞ", g.Ad);
    }

    [Fact]
    public void Giris_ekran_darsa_ekrandan_tasmaz()
    {
        var m = Yeni(); m.EkranGenislikDp = 300; m.GirisAc(0);
        Assert.Equal(288, m.GorunumHesapla(0).W);
    }

    [Fact]
    public void Esc_sirayla_menu_giris_panel_kapatir()
    {
        var m = Yeni(); m.MenuAc(0);
        Assert.True(m.Esc(1)); Assert.False(m.Menu);
        m.GirisAc(2);
        Assert.True(m.Esc(3)); Assert.False(m.Giris);
        m.TamPanelAc(4, null);
        Assert.True(m.Esc(5)); Assert.Equal(PanelKipi.Yok, m.Panel);
        Assert.False(m.Esc(6));
    }

    [Fact]
    public void Metin_gonder_giris_kapatir_ve_eylem_uretir_bos_metin_yok_sayilir()
    {
        var m = Yeni(); m.GirisAc(0); m.Eylemler.Clear();
        m.MetinGonder("   ", 1);
        Assert.True(m.Giris); Assert.Empty(m.Eylemler);
        m.MetinGonder("  testleri koş ", 2);
        Assert.False(m.Giris);
        Assert.Equal("testleri koş", m.Eylemler.Single(e => e.Tur == EylemTuru.MetinGonder).Metin);
    }

    [Fact]
    public void Hedef_cipi_proje_yoksa_Proje_sec_der_ve_tiklaninca_giris_kapanip_panel_acilir()
    {
        var m = Yeni();
        Assert.Equal("Proje seç", m.HedefEtiketi);
        m.GirisAc(0);
        m.Eylemler.Clear();
        m.HedefCipiTikla(1);
        Assert.False(m.Giris);
        Assert.Equal(PanelKipi.Tam, m.Panel);
        Assert.Contains(m.Eylemler, e => e.Tur == EylemTuru.PanelAc && e.Kip == PanelKipi.Tam);

        m.ProjeAdi = "Söz";
        Assert.Equal("Söz", m.HedefEtiketi);
    }

    [Fact]
    public void Kisayol_girisi_acar_gizliyse_once_geri_getirir()
    {
        var m = Yeni(); m.Gizle(Gizlilik.Kullanici, 0);
        Assert.True(m.GorunumHesapla(0).Gizli); Assert.Equal(0, m.GorunumHesapla(0).Op);
        m.Kisayol(10);
        Assert.Equal(Gizlilik.Yok, m.Gizli); Assert.True(m.Giris);
    }

    // ------------------------------------------------------------ tekerlek
    [Fact]
    public void Tekerlek_bekleyenler_arasinda_gezinir_basligi_gosterir_sonra_sayiya_doner()
    {
        var m = Yeni(a => a.Rest = "durumlu");
        m.IsleriGuncelle(new[] { Kaldi(), Karar(), Soru() }, 0);
        Assert.Equal("j:3", m.SeciliIs!.Id);        // d2m: sıra soru, kaldı, karar (j:3, j:1, j:2)
        m.Tekerlek(1, 1000);
        Assert.Equal("j:1", m.SeciliId);
        Assert.Equal("ödeme testi", m.IcerikHesapla(1500)!.Yazi);
        m.Tik(1000 + 2600);
        Assert.Equal("1 soru, 1 olmadı, 1 karar", m.IcerikHesapla(3700)!.Yazi);
        m.Tekerlek(1, 5000); m.Tekerlek(1, 5001);
        Assert.Equal("j:3", m.SeciliId);          // sondan başa döner
        m.Tekerlek(-1, 5002);
        Assert.Equal("j:2", m.SeciliId);          // başa geri: sona sarar
    }

    [Fact]
    public void Tekerlek_bekleyen_yoksa_hicbir_sey_yapmaz()
    {
        var m = Yeni(); m.Tekerlek(1, 0);
        Assert.Null(m.SeciliId);
    }

    // ------------------------------------------------------------ uzatma durakları
    [Theory]
    [InlineData(50, 1, false)]
    [InlineData(110, 2, false)]
    [InlineData(400, 0, true)]
    public void Uzatma_en_yakin_duraga_oturur(double genislik, int durakKodu, bool giris)
    {
        var m = Yeni();
        m.UzatmaBasla(); m.UzatmaGuncelle(genislik);
        var beklenen = (Durak)durakKodu; var d = m.UzatmaBitir(0, 44);
        Assert.Equal(beklenen, d); Assert.Equal(giris, m.Giris);
        Assert.False(m.Uzatma);
        if (!giris) Assert.Equal(beklenen, m.Durak);
    }

    [Fact]
    public void Orb_durağında_hap_yok_rozet_var_dikkat_gelince_durak_kalkar()
    {
        var m = Yeni();
        m.Durak = Durak.Orb; m.IsleriGuncelle(new[] { Bitti("j:9") }, 0);
        Assert.Equal(Durak.Yok, m.Durak);        // yeni bekleyen iş dikkat olayı: orb kısaltması kalkar
        m.Durak = Durak.Orb;
        var g = m.GorunumHesapla(0);
        Assert.False(g.Hap); Assert.True(g.Rozet);
    }

    // ------------------------------------------------------------ gizleme, tam ekran
    [Fact]
    public void Tam_ekran_uygulamada_gizlenir_bitince_geri_gelir()
    {
        var m = Yeni();
        m.TamEkran(true, 0);
        Assert.Equal(Gizlilik.TamEkran, m.Gizli); Assert.Null(m.SonrakiMs());
        Assert.True(m.GorunumHesapla(0).Gizli);
        m.TamEkran(false, 5000);
        Assert.Equal(Gizlilik.Yok, m.Gizli); Assert.Equal(Seviye.Belirgin, m.Sev);
        Assert.Equal(9000, m.SonrakiMs());
    }

    [Fact]
    public void Tam_ekranda_gizle_ayari_kapaliysa_gorunur_kalir()
    {
        var m = Yeni(a => a.FsHide = 0);
        m.TamEkran(true, 0);
        Assert.Equal(Gizlilik.Yok, m.Gizli);
    }

    [Fact]
    public void Kullanici_gizlemesi_1_saat_sonra_kendiliginden_biter()
    {
        var m = Yeni(); m.Gizle(Gizlilik.Kullanici, 0);
        Assert.Equal(3600000, m.SonrakiMs());
        m.Tik(3600000);
        Assert.Equal(Gizlilik.Yok, m.Gizli);
    }

    [Fact]
    public void Gizliyken_dikkat_olayi_uyandirmaz_tam_ekranda_gizliyken_de_ses_vermez_ama_liste_guncellenir()
    {
        var m = Yeni(); m.Gizle(Gizlilik.Kullanici, 0);
        m.IsleriGuncelle(new[] { Bitti() }, 10);
        Assert.Equal(Gizlilik.Kullanici, m.Gizli);
        Assert.Single(m.Bekleyenler);
    }

    [Fact]
    public void Gizle_acik_her_yuzeyi_kapatir()
    {
        var m = Yeni(); m.TamPanelAc(0, null); m.Eylemler.Clear();
        m.Gizle(Gizlilik.Kullanici, 1);
        Assert.Equal(PanelKipi.Yok, m.Panel);
        Assert.True(EylemVar(m, EylemTuru.PanelKapat));
    }

    // ------------------------------------------------------------ uzağa tıklama
    [Fact]
    public void Uzaga_tiklayinca_belirgin_soluklasir_ayar_kapaliysa_olmaz()
    {
        var m = Yeni(); m.DisariTik(0, 200);
        Assert.Equal(Seviye.Soluk, m.Sev);
        var n = Yeni(a => a.FarFade = 0); n.DisariTik(0, 200);
        Assert.Equal(Seviye.Belirgin, n.Sev);
    }

    [Fact]
    public void Disari_tik_once_menuyu_kapatir_soluklasmaz()
    {
        var m = Yeni(); m.MenuAc(0);
        m.DisariTik(1, 200);
        Assert.False(m.Menu); Assert.Equal(Seviye.Belirgin, m.Sev);
    }

    // ------------------------------------------------------------ dikte (görünüm ve kabuk protokolü)
    [Fact]
    public void Dikte_metni_dinliyor_ceviriyor_yazdi_panoda()
    {
        var m = Yeni();
        m.DikteBaslat(0);
        Assert.Equal("dinliyor 0:00", m.DikteMetni(500));
        Assert.Equal("dinliyor 1:05", m.DikteMetni(65500));
        m.DikteFaz(DikteFazi.Ceviriyor, 1); Assert.Equal("çeviriyor", m.DikteMetni(2));
        m.DikteFaz(DikteFazi.Yazdi, 3); Assert.Equal("yazdı", m.DikteMetni(4));
        m.DikteHedefVar = false; Assert.Equal("panoda", m.DikteMetni(4));
        Assert.True(m.Mesgul);
        m.DikteBitir(10); Assert.False(m.Mesgul);
    }

    [Fact]
    public void Dikte_durum_olayi_bes_fazi_kapsul_gorunumune_baglar()
    {
        var m = Yeni();
        m.Sev = Seviye.Uyku;
        Assert.True(m.DikteDurumUygula("dinliyor", "", 1000));
        Assert.Equal(Seviye.Belirgin, m.Sev);
        Assert.Equal("dinliyor 0:03", m.GorunumHesapla(4000).Icerik!.Yazi);

        Assert.True(m.DikteDurumUygula("ceviriyor", "", 5000));
        Assert.Equal("çeviriyor", m.GorunumHesapla(5100).Icerik!.Yazi);

        Assert.True(m.DikteDurumUygula("yazdi", "", 6000));
        Assert.Equal("yazdı", m.GorunumHesapla(6100).Icerik!.Yazi);

        Assert.True(m.DikteDurumUygula("panoda", "", 7000));
        Assert.Equal("panoda", m.GorunumHesapla(7100).Icerik!.Yazi);

        Assert.True(m.DikteDurumUygula("kapali", "", 8000));
        Assert.Null(m.Dikte);
        Assert.False(m.Mesgul);
        Assert.False(m.DikteDurumUygula("bilinmeyen", "", 9000));
    }

    // ------------------------------------------------------------ uyarlanır ayar ve hareketi azalt
    [Fact]
    public void Hareketi_azalt_ayari_sistemden_okur_ya_da_zorlar()
    {
        var a = new Ayar();
        Assert.True(a.HareketAzalt(true)); Assert.False(a.HareketAzalt(false));
        a.ReduceMotion = 1; Assert.True(a.HareketAzalt(false));
        a.ReduceMotion = 0; Assert.False(a.HareketAzalt(true));
    }

    [Fact]
    public void Konum_sifirla_eylemi_uretir()
    {
        var m = Yeni(); m.KonumSifirla();
        Assert.True(EylemVar(m, EylemTuru.KonumSifirla));
    }

    [Fact]
    public void Uyut_her_seyi_kapatir_ve_uykuya_alir()
    {
        var m = Yeni(); m.MenuAc(0); m.Uyut(1);
        Assert.False(m.Menu); Assert.Equal(Seviye.Uyku, m.Sev);
    }
}
