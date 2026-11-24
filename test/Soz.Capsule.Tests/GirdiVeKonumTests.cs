using System.Text.Json;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>P2 girdi sözleşmesi (işaretçi), konum ve yapışma, ayar JSON'u, giriş metni, motor cevabı, yay, tema.</summary>
public class GirdiVeKonumTests
{
    private static (KapsulMantik M, PointerYonetici P) Yeni(Action<Ayar>? ayarla = null)
    {
        var a = new Ayar(); ayarla?.Invoke(a);
        var m = new KapsulMantik(a, s => s.Length * 7.0);
        m.BostaSifirla(0);
        return (m, new PointerYonetici(m));
    }
    // Sağ kenara yapışık 100 px genişliğinde, 44 px yüksekliğinde kapsül: (900..1000) x (400..444)
    private static readonly Dik Kapsul = new(900, 400, 1000, 444);

    // ------------------------------------------------------------ eşik, sürükleme
    [Fact]
    public void Surukleme_esigi_4dp_alti_tiktir_ustu_surukleme()
    {
        var (m, p) = Yeni();
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        p.Hareket(10, 953, 420);
        Assert.False(p.Tasindi);
        p.Hareket(20, 956, 420);
        Assert.True(p.Tasindi); Assert.True(m.Surukle); Assert.True(p.Suruklenir);
        var sonuc = p.SolBirakildi(30, 956, 420);
        Assert.Equal(PointerYonetici.Sonuc.SurukleBitti, sonuc);
        Assert.False(m.Surukle); Assert.True(m.NoDwell);
    }

    [Fact]
    public void Esik_dp_cinsinden_olcek_2de_6px_3dp_tiktir()
    {
        var (m, p) = Yeni();
        p.SolBasildi(0, 950, 420, Kapsul, 2, Taraf.Sag, Seviye.Belirgin);
        p.Hareket(10, 956, 420);
        Assert.False(p.Tasindi);
        p.Hareket(20, 960, 420);                  // 10 px = 5 dp
        Assert.True(p.Tasindi);
    }

    [Fact]
    public void Surukleme_hedefi_tutulan_noktayi_korur_sag_tarafta_sag_uc_capa()
    {
        var (_, p) = Yeni();
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        p.Hareket(5, 960, 430); p.Hareket(10, 1050, 520);
        // tutulan nokta sağ uca 50 px, merkezin 2 px altında: hedef çapa x = 1050 + 50, cy = 520 + 2
        Assert.Equal(1100, p.SurukleHedefi.Ax, 6);
        Assert.Equal(522, p.SurukleHedefi.Cy, 6);
    }

    [Fact]
    public void Surukleme_sinirlar_icinde_kalir()
    {
        var (_, p) = Yeni();
        p.Sinirlar = new Dik(0, 0, 1920, 1080);
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        p.Hareket(5, 960, 430); p.Hareket(10, 5000, 5000);
        Assert.True(p.SurukleHedefi.Ax <= 1920 && p.SurukleHedefi.Cy <= 1080);
    }

    [Fact]
    public void Surukleme_acik_on_bakisi_ve_menuyu_kapatir_uykuyu_uyandirir()
    {
        var (m, p) = Yeni();
        m.Sev = Seviye.Uyku; m.MenuAc(0);
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, m.Sev);
        p.Hareket(5, 970, 420);
        Assert.False(m.Menu); Assert.Equal(Seviye.Belirgin, m.Sev);
    }

    [Fact]
    public void Yakalama_kaybi_surukleme_iptal_eder()
    {
        var (m, p) = Yeni();
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        p.Hareket(5, 970, 420);
        p.Iptal(10);
        Assert.False(p.Basili); Assert.False(m.Surukle);
    }

    // ------------------------------------------------------------ tık, çift tık
    [Fact]
    public void Tek_tik_tam_panel_acar()
    {
        var (m, p) = Yeni();
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        Assert.Equal(PointerYonetici.Sonuc.Tik, p.SolBirakildi(80, 950, 420));
        Assert.Equal(PanelKipi.Tam, m.Panel);
    }

    [Fact]
    public void Uykuda_tek_tik_hem_uyandirir_hem_paneli_acar()
    {
        var (m, p) = Yeni();
        m.Sev = Seviye.Uyku;
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Uyku);
        Assert.True(p.UykuluTik);
        p.SolBirakildi(60, 950, 420);
        Assert.Equal(PanelKipi.Tam, m.Panel); Assert.Equal(Seviye.Belirgin, m.Sev);
    }

    [Fact]
    public void Cift_tik_500ms_icinde_girisi_acar_gec_kalan_iki_tik_olur()
    {
        // k3: ilk tık paneli açıyorsa ikinci tık yutulur (KapsulK3Tests). Çift tık yazı satırı, panel zaten açıkken (ilk tık boşta) çalışır.
        var (m, p) = Yeni();
        m.TamPanelAc(-5000, null);
        p.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin); p.SolBirakildi(50, 950, 420);
        p.SolBasildi(300, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        Assert.Equal(PointerYonetici.Sonuc.CiftTik, p.SolBirakildi(350, 950, 420));
        Assert.True(m.Giris);

        var (n, q) = Yeni();
        q.SolBasildi(0, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin); q.SolBirakildi(50, 950, 420);
        q.SolBasildi(900, 950, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        Assert.Equal(PointerYonetici.Sonuc.Tik, q.SolBirakildi(950, 950, 420));
        Assert.False(n.Giris);
    }

    // ------------------------------------------------------------ uzatma
    [Fact]
    public void Serbest_uctan_tutup_cekmek_uzatir_ve_en_yakin_durağa_oturur()
    {
        var (m, p) = Yeni();
        p.SolBasildi(0, 905, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);   // sol uç (serbest uç): 5 px içinde
        p.Hareket(5, 895, 420); p.Hareket(10, 880, 420);
        Assert.True(p.Uzatiyor); Assert.True(m.Uzatma);
        Assert.Equal(125, m.UzatmaGenislik, 6);
        Assert.Equal(PointerYonetici.Sonuc.UzatmaBitti, p.SolBirakildi(20, 880, 420));
        Assert.Equal(Durak.Durumlu, m.Durak);
    }

    [Fact]
    public void Uzatma_giris_genisligini_asmaz_ve_orbdan_kucuk_olmaz()
    {
        var (m, p) = Yeni();
        p.SolBasildi(0, 905, 420, Kapsul, 1, Taraf.Sag, Seviye.Belirgin);
        p.Hareket(5, 880, 420); p.Hareket(10, -3000, 420);
        Assert.Equal(420, m.UzatmaGenislik, 6);
        p.Hareket(15, 2000, 420);
        Assert.Equal(44, m.UzatmaGenislik, 6);
    }

    [Fact]
    public void Uykuda_uzatma_baslamaz_surukleme_olur()
    {
        var (m, p) = Yeni();
        m.Sev = Seviye.Uyku;
        p.SolBasildi(0, 905, 420, new Dik(960, 407, 1000, 437), 1, Taraf.Sag, Seviye.Uyku);
        p.Hareket(5, 880, 420);
        Assert.False(p.Uzatiyor); Assert.True(m.Surukle);
    }

    // ------------------------------------------------------------ konum, yapışma, çoklu ekran
    private static readonly EkranBilgi E1 = new(@"\\.\DISPLAY1", new Dik(0, 0, 1920, 1080), new Dik(0, 0, 1920, 1040), 1, true);
    private static readonly EkranBilgi E2 = new(@"\\.\DISPLAY2", new Dik(1920, 0, 3840, 1080), new Dik(1920, 0, 3840, 1080), 1.5);

    [Fact]
    public void Birak_sag_kenarin_28dp_icinde_yapistirir()
    {
        var k = KonumHesap.Birak(new Dik(1700, 300, 1894, 344), E1, 28, 6);
        Assert.Equal(KenarX.Sag, k.SnapX); Assert.Equal(Taraf.Sag, k.Taraf); Assert.Equal(KenarY.Yok, k.SnapY);
        Assert.Equal((1920 - 6) / 1920.0, k.Fx, 6);
        Assert.Equal(322 / 1040.0, k.Fy, 6);
    }

    [Fact]
    public void Birak_sol_kenar_ve_ust_kose_yapisma()
    {
        var k = KonumHesap.Birak(new Dik(10, 12, 54, 56), E1, 28, 6);
        Assert.Equal(KenarX.Sol, k.SnapX); Assert.Equal(Taraf.Sol, k.Taraf); Assert.Equal(KenarY.Ust, k.SnapY);
    }

    [Fact]
    public void Birak_ortada_serbest_kalir_yariya_gore_cipalanir()
    {
        var k = KonumHesap.Birak(new Dik(500, 500, 600, 544), E1, 28, 6);
        Assert.Equal(KenarX.Yok, k.SnapX); Assert.Equal(Taraf.Sol, k.Taraf);
        Assert.Equal(500 / 1920.0, k.Fx, 6);
        var k2 = KonumHesap.Birak(new Dik(1200, 500, 1300, 544), E1, 28, 6);
        Assert.Equal(Taraf.Sag, k2.Taraf); Assert.Equal(1300 / 1920.0, k2.Fx, 6);
    }

    [Fact]
    public void Cozumle_cozunurluk_degisince_oranlari_korur()
    {
        var k = new KonumDurumu { EkranId = E1.Id, Fx = 0.5, Fy = 0.25, SnapX = KenarX.Yok, SnapY = KenarY.Yok, Taraf = Taraf.Sag };
        var (ax, cy, e) = KonumHesap.Cozumle(k, new[] { E1 }, 44, 6);
        Assert.Equal(960, ax, 6); Assert.Equal(260, cy, 6); Assert.Same(E1, e);
        var kucuk = new EkranBilgi(E1.Id, new Dik(0, 0, 1280, 720), new Dik(0, 0, 1280, 680), 1, true);
        var (ax2, cy2, _) = KonumHesap.Cozumle(k, new[] { kucuk }, 44, 6);
        Assert.Equal(640, ax2, 6); Assert.Equal(170, cy2, 6);
    }

    [Fact]
    public void Cozumle_kenara_yapisik_kapsul_yeni_cozunurlukte_yine_kenarda()
    {
        var k = new KonumDurumu { EkranId = E1.Id, SnapX = KenarX.Sag, SnapY = KenarY.Alt };
        var kucuk = new EkranBilgi(E1.Id, new Dik(0, 0, 1280, 720), new Dik(0, 0, 1280, 680), 1, true);
        var (ax, cy, _) = KonumHesap.Cozumle(k, new[] { kucuk }, 44, 6);
        Assert.Equal(1274, ax, 6); Assert.Equal(680 - 6 - 22, cy, 6);
    }

    [Fact]
    public void Ekran_cikarilinca_birincile_duser_kimlik_ile_ekran_bulunur()
    {
        var k = new KonumDurumu { EkranId = E2.Id };
        Assert.Same(E2, KonumHesap.EkranKimlikle(k.EkranId, new[] { E1, E2 }));
        Assert.Same(E1, KonumHesap.EkranKimlikle(k.EkranId, new[] { E1 }));
        Assert.Null(KonumHesap.EkranKimlikle("x", Array.Empty<EkranBilgi>()));
        Assert.Same(E2, KonumHesap.EkranBul(2500, 100, new[] { E1, E2 }));
        Assert.Same(E1, KonumHesap.EkranBul(-50, 100, new[] { E1, E2 }));
    }

    [Fact]
    public void Konum_json_gidis_donus_ve_bozuk_json_null()
    {
        var k = new KonumDurumu { EkranId = @"\\.\DISPLAY2", Fx = 0.33, Fy = 0.71, SnapX = KenarX.Sol, SnapY = KenarY.Alt, Taraf = Taraf.Sol };
        var o = KonumDurumu.JsonOku(k.Json())!;
        Assert.Equal(k.EkranId, o.EkranId); Assert.Equal(0.33, o.Fx, 4); Assert.Equal(0.71, o.Fy, 4);
        Assert.Equal(KenarX.Sol, o.SnapX); Assert.Equal(KenarY.Alt, o.SnapY); Assert.Equal(Taraf.Sol, o.Taraf);
        Assert.Null(KonumDurumu.JsonOku("{bozuk"));
        Assert.Null(KonumDurumu.JsonOku(""));
        Assert.Equal(1, KonumDurumu.JsonOku("{\"fx\": 9}")!.Fx);
    }

    [Fact]
    public void Sikistir_kapsulu_calisma_alanina_sokar()
    {
        var r = KonumHesap.Sikistir(new Dik(1900, 1030, 1960, 1074), E1.Calisma, 0);
        Assert.True(r.R <= 1920 && r.B <= 1040 && r.L >= 0 && r.T >= 0);
        Assert.Equal(60, r.W, 6);
    }

    [Fact]
    public void Popup_kapsulun_yaninda_ve_ekrandan_tasmadan_yerlesir()
    {
        var (x, y, ox, oy) = KonumHesap.PopupYerle(new Dik(1800, 500, 1894, 544), 360, 300, E1.Calisma, 10, 8);
        Assert.Equal(1800 - 10 - 360, x, 6);
        Assert.True(y >= 8 && y + 300 <= 1040 - 8);
        Assert.InRange(ox, 0, 360); Assert.InRange(oy, 0, 300);
        var (x2, _, _, _) = KonumHesap.PopupYerle(new Dik(10, 500, 54, 544), 360, 300, E1.Calisma, 10, 8);
        Assert.Equal(54 + 10, x2, 6);
    }

    [Fact]
    public void Popup_yan_yer_yoksa_altina_ya_da_ustune()
    {
        var dar = new Dik(0, 0, 400, 800);
        var (x, y, _, _) = KonumHesap.PopupYerle(new Dik(300, 100, 394, 144), 380, 200, dar, 10, 8);
        Assert.True(x >= 8 && x + 380 <= 392);
        Assert.True(y >= 144);
    }

    // ------------------------------------------------------------ ayar JSON
    [Fact]
    public void Ayar_json_gidis_donus_hicbir_alan_kaybolmaz()
    {
        var a = new Ayar { Tema = "sicak", Idle1 = 7, Rest = "durumlu", Kisayol = "Ctrl+Shift+K", HoverMs = 750 };
        var b = new Ayar();
        var uyari = b.JsonYukle(a.JsonYaz());
        Assert.Empty(uyari);
        Assert.Equal("sicak", b.Tema); Assert.Equal(7, b.Idle1); Assert.Equal("durumlu", b.Rest);
        Assert.Equal("Ctrl+Shift+K", b.Kisayol); Assert.Equal(750, b.HoverMs);
        foreach (var t in Ayar.Tablo) Assert.Equal(t.Oku(a), t.Oku(b));
    }

    [Fact]
    public void Ayar_sinir_disi_deger_sinirlanir_bilinmeyen_alan_uyari_verir_bozuk_dosya_degeri_korur()
    {
        var a = new Ayar();
        var uyari = a.JsonYukle("{\"idle1\": -5, \"sleepFloor\": 7, \"yok\": 1, \"tema\": \"mor\", \"snap\": \"cok\"}");
        Assert.Equal(1, a.Idle1); Assert.Equal(0.5, a.SleepFloor);
        Assert.Contains(uyari, u => u.Contains("yok")); Assert.Contains(uyari, u => u.StartsWith("tema"));
        Assert.Contains(uyari, u => u.StartsWith("snap"));
        Assert.Equal("cam", a.Tema);
        var b = new Ayar { Idle1 = 9 };
        var u2 = b.JsonYukle("{bozuk");
        Assert.Single(u2); Assert.Equal(9, b.Idle1);
    }

    [Fact]
    public void Ayar_varsayilanlari_P1_sayilari()
    {
        var a = new Ayar();
        Assert.Equal(4, a.Idle1); Assert.Equal(30, a.Idle2); Assert.Equal(800, a.FadeMs); Assert.Equal(150, a.WakeMs);
        Assert.Equal(6000, a.AttentionMs); Assert.Equal(40, a.R); Assert.Equal(14, a.Dot); Assert.Equal(34, a.Orb); Assert.Equal(44, a.Big);
        Assert.Equal(292, a.PillMax); Assert.Equal(420, a.GirisW); Assert.Equal(600, a.HoverMs); Assert.Equal(4, a.DwellTol);
        Assert.Equal(4, a.DragThreshold); Assert.Equal(28, a.Snap); Assert.Equal(6, a.EdgeGap); Assert.Equal(32, a.MinHit);
        Assert.Equal(0.10, a.SleepFloor); Assert.Equal("cam", a.Tema); Assert.Equal("Ctrl+Alt+J", a.Kisayol);
    }

    [Fact]
    public void Ayar_al_nesne_kimligini_korur_degerleri_kopyalar()
    {
        var hedef = new Ayar(); var kaynak = new Ayar { Idle1 = 12, Tema = "murekkep" };
        var m = new KapsulMantik(hedef, s => 10);
        hedef.Al(kaynak);
        Assert.Same(hedef, m.Ayar); Assert.Equal(12, m.Ayar.Idle1); Assert.Equal("terminal", m.Ayar.Tema);
    }

    [Theory]
    [InlineData("Mürekkep", "terminal")] [InlineData(" KAGIT ", "sicak")] [InlineData("kâğıt", "sicak")]
    [InlineData("cam", "cam")] [InlineData("terminal", "terminal")] [InlineData("mat", "mat")]
    [InlineData("ton", "ton")] [InlineData("Düz", "duz")] [InlineData("Sıcak", "sicak")]
    [InlineData("mor", null)] [InlineData(null, null)]
    public void Secim_gecerli_turkce_karakterleri_ve_eski_adlari_esler(string? girdi, string? beklenen)
        => Assert.Equal(beklenen, Ayar.SecimGecerli(Ayar.TemaDegerleri, girdi));

    // ------------------------------------------------------------ giriş metni
    [Fact]
    public void Giris_metni_ekle_sil_imlec_kelime()
    {
        var g = new GirisMetni();
        g.Ekle("merhaba dünya");
        Assert.Equal("merhaba dünya", g.Metin); Assert.Equal(13, g.Imlec);
        g.KelimeGeriSil(); Assert.Equal("merhaba ", g.Metin);
        g.Basa(); g.IleriSil(); Assert.Equal("erhaba ", g.Metin);
        g.Sona(); g.GeriSil(); Assert.Equal("erhaba", g.Metin);
        g.Sol(true); Assert.Equal(0, g.Imlec);
        g.Sag(true); Assert.Equal(6, g.Imlec);
        g.Temizle(); Assert.True(g.Bos);
    }

    [Fact]
    public void Giris_metni_satir_sonu_ve_kontrol_karakteri_atar_en_fazla_sinirina_uyar()
    {
        var g = new GirisMetni { EnFazla = 5 };
        g.Ekle("a\r\nb\u0001c\td");
        Assert.Equal("abc d", g.Metin);
        g.Ekle("zzz"); Assert.Equal(5, g.Metin.Length);
    }

    [Fact]
    public void Giris_metni_vekil_cift_karakteri_bolmez()
    {
        var g = new GirisMetni(); g.Ekle("a😀"); g.GeriSil();
        Assert.Equal("a", g.Metin);
    }

    // ------------------------------------------------------------ motor cevabı
    private static List<Is> Ayristir(string json) { using var d = JsonDocument.Parse(json); return MotorCevabi.NowAyristir(d.RootElement); }

    [Fact]
    public void Motor_cevabi_hata_izin_ve_bilgi_nedenlerini_ayirir_eski_cevap_ayni_kalir()
    {
        var l = Ayristir("""
        {"needsYou":[
          {"sessionId":1,"title":"Hata A","reason":"failure:rate_limit"},
          {"sessionId":2,"title":"Hata B","reason":"session_failed"},
          {"sessionId":3,"title":"Izin","reason":"permission_prompt"},
          {"sessionId":4,"title":"Bilgi","reason":"elicitation_dialog"},
          {"sessionId":5,"title":"Soru","reason":"session_question"},
          {"sessionId":6,"title":"Eski","reason":"bilinmeyen_bir_neden"},
          {"sessionId":7,"title":"Takili","reason":"stalled"}]}
        """);
        Assert.Contains(l, j => j.Id == "o:1" && j.Tur == IsTuru.Kaldi && j.Oncelik == 3 && j.Neden == "failure" && j.Sinyal);
        Assert.Contains(l, j => j.Id == "o:2" && j.Tur == IsTuru.Kaldi && j.Oncelik == 3);
        Assert.Contains(l, j => j.Id == "o:3" && j.Tur == IsTuru.Karar);
        Assert.Contains(l, j => j.Id == "o:4" && j.Tur == IsTuru.Soru);
        Assert.Contains(l, j => j.Id == "o:5" && j.Tur == IsTuru.Soru);
        Assert.Contains(l, j => j.Id == "o:6" && j.Tur == IsTuru.Belirsiz && j.Durdu && !j.Sinyal);   // tanınmayan neden, soru yok: durdu (bitti değil)
        Assert.Contains(l, j => j.Id == "o:7" && j.Tur == IsTuru.Belirsiz && !j.Sinyal);
    }

    [Fact]
    public void Motor_cevabi_bekleyen_calisan_ve_sayilari_isle_cevirir()
    {
        var l = Ayristir("""
        {"counts":{"jobsPassed":2,"jobsFailed":1,"jobsRunning":1,"stalled":0},
         "needsYou":[{"sessionId":7,"title":"Fatura işi","reason":"stalled"},{"sessionId":8,"title":"Rapor","question":"Hangisi?"}],
         "waitingJobs":[{"id":3,"title":"Karar işi","state":"karar_bekliyor"},{"id":4,"title":"Bitti işi","state":"gecti"}],
         "runningJobs":[{"id":9,"title":"Derleme"}]}
        """);
        Assert.Contains(l, j => j.Id == "o:7" && j.Tur == IsTuru.Belirsiz && !j.Sinyal && j.Oncelik == 5); // takılan oturum: sunucuda session_quiet = belirsiz, 5
        Assert.Contains(l, j => j.Id == "o:8" && j.Tur == IsTuru.Soru);
        Assert.Contains(l, j => j.Id == "j:3" && j.Tur == IsTuru.Karar);
        Assert.Contains(l, j => j.Id == "j:4" && j.Tur == IsTuru.Bitti);
        Assert.Contains(l, j => j.Id == "j:9" && j.Tur == IsTuru.Calisiyor);
        Assert.Equal(2, l.Count(j => j.Tur == IsTuru.Bitti));            // listede 1, sayıda 2: biri tamamlanır
        Assert.Equal(1, l.Count(j => j.Tur == IsTuru.Kaldi));            // jobsFailed=1 ama listede kaldı yok: biri adsız tamamlanır (takılan oturum kaldı sayılmaz)
    }

    [Fact]
    public void Motor_cevabi_yedek_oncelik_tablosu_sunucuyla_ayni()
    {
        // s1 / B20: sunucu tablosu (AttentionKinds.Table): soru 1, kabul 2, kanıtı kalan 2, başlatılamayan 3, bitti 4, belirsiz (takılan oturum dahil) 5.
        var l = Ayristir("""
        {"needsYou":[{"sessionId":7,"title":"Takılan","reason":"stalled"},{"sessionId":8,"title":"Soru","question":"Hangisi?"},{"sessionId":9,"title":"Biten"}],
         "waitingJobs":[{"id":1,"title":"Kanıtı kalan","state":"kaldi"},{"id":2,"title":"Başlamayan","state":"baslatilamadi"},{"id":3,"title":"Karar","state":"karar_bekliyor"}]}
        """);
        int P(string id) => l.Single(j => j.Id == id).Oncelik;
        Assert.Equal(1, P("o:8"));
        Assert.Equal(2, P("j:3"));
        Assert.Equal(2, P("j:1"));
        Assert.Equal(3, P("j:2"));
        Assert.Equal(5, P("o:9"));   // soru işaretsiz durmuş oturum: bitti (4) değil, durdu (5)
        Assert.Equal(5, P("o:7"));
        Assert.Equal(IsTuru.Kaldi, l.Single(j => j.Id == "j:2").Tur);
    }

    [Fact]
    public void Motor_cevabi_bozuk_ya_da_bos_cevap_cokertmez()
    {
        Assert.Empty(Ayristir("[]"));
        Assert.Empty(Ayristir("{}"));
        Assert.Empty(Ayristir("""{"needsYou":"x","counts":5}"""));
    }

    [Fact]
    public void Kisalt_uzun_basligi_keser()
    {
        Assert.Equal("kısa", MotorCevabi.Kisalt("  kısa "));
        var k = MotorCevabi.Kisalt(new string('a', 40), 10);
        Assert.Equal(10, k.Length); Assert.EndsWith("…", k);
    }

    // ------------------------------------------------------------ yay
    [Fact]
    public void Yay_hedefe_oturur_ve_durgunluk_bildirir()
    {
        var y = new Yay(0, 380, 0.8) { T = 100 };
        for (var i = 0; i < 120; i++) y.Adim(1.0 / 60);
        Assert.True(y.Durgun(0.25, 4)); Assert.InRange(y.X, 99.7, 100.3);
    }

    [Fact]
    public void Yay_kritik_sonumlu_tasmaz()
    {
        var y = new Yay(0, 380, 1.0) { T = 100 };
        var enFazla = 0.0;
        for (var i = 0; i < 300; i++) { y.Adim(1.0 / 60); enFazla = Math.Max(enFazla, y.X); }
        Assert.True(enFazla <= 100.01);
    }

    [Fact]
    public void Yay_dt_buyuk_olsa_da_kararli_alt_adimlarla_ilerler()
    {
        var y = new Yay(0, 800, 0.6) { T = 50 };
        y.Adim(0.2);
        Assert.False(double.IsNaN(y.X)); Assert.InRange(y.X, -50, 120);
    }

    [Fact]
    public void Composition_periyodu_1_bolu_karekok_k()
        => Assert.Equal(1 / Math.Sqrt(380), Yay.CompositionPeriyodu(380), 9);

    [Fact]
    public void Sertlik_sureden_kritik_yay_yaklasik_o_surede_oturur()
    {
        var k = Yay.SertlikSureden(400);
        var y = new Yay(0, k, 1.0) { T = 100 };
        for (var i = 0; i < 24; i++) y.Adim(1.0 / 60);   // 400 ms
        Assert.InRange(y.X, 97, 100.01);
    }

    // ------------------------------------------------------------ tema (6 aile + ters renk yok + kabuk Palette eşlemesi)
    [Theory]
    [InlineData("cam", true)] [InlineData("cam", false)]
    [InlineData("terminal", true)] [InlineData("terminal", false)]
    [InlineData("mat", true)] [InlineData("mat", false)]
    [InlineData("ton", true)] [InlineData("ton", false)]
    [InlineData("duz", true)] [InlineData("duz", false)]
    [InlineData("sicak", true)] [InlineData("sicak", false)]
    [InlineData("murekkep", true)] [InlineData("murekkep", false)]
    [InlineData("kagit", true)] [InlineData("kagit", false)]
    public void Her_tema_iki_zeminde_dogru_yonde_ve_yazi_govdeden_ayirt_edilir(string ad, bool koyu)
    {
        var t = Temalar.Al(ad, koyu);
        Assert.Equal(koyu, t.Koyu);
        Assert.True(t.Yazi.A > 0);
        if (koyu)
        {
            Assert.True(t.Govde.Parlaklik < 0.30, $"{ad}/koyu zemin açık olmamalı: {t.Govde.Parlaklik}");
            Assert.True(t.Yazi.Parlaklik > 0.75, $"{ad}/koyu yazı koyu olmamalı: {t.Yazi.Parlaklik}");
        }
        else
        {
            Assert.True(t.Govde.Parlaklik > 0.70, $"{ad}/açık zemin koyu olmamalı: {t.Govde.Parlaklik}");
            Assert.True(t.Yazi.Parlaklik < 0.25, $"{ad}/açık yazı açık olmamalı: {t.Yazi.Parlaklik}");
        }
        Assert.True(Math.Abs(t.Yazi.Parlaklik - t.Govde.Parlaklik) > 0.45, $"{ad}/{(koyu ? "koyu" : "açık")}: yazı ile gövde ayrışmıyor");

        var pal = Soz.Shell.Palette.ForExplicit(ad, !koyu);
        Assert.Equal(!koyu, pal.Light);
        Assert.Equal(255, pal.EffectivePanelBg.A);
        var palBgLum = (0.2126 * pal.EffectivePanelBg.R + 0.7152 * pal.EffectivePanelBg.G + 0.0722 * pal.EffectivePanelBg.B) / 255.0;
        if (koyu) Assert.True(palBgLum < 0.25, $"kabuk {ad}/koyu PanelBg çok açık: {palBgLum}");
        else Assert.True(palBgLum > 0.80, $"kabuk {ad}/açık PanelBg çok koyu: {palBgLum}");
    }

    [Fact]
    public void Temalar_birbirinden_belirgin_ayrisir()
    {
        var aciklar = Temalar.Aileler.Select(a => Temalar.Al(a, false)).ToList();
        var koyular = Temalar.Aileler.Select(a => Temalar.Al(a, true)).ToList();
        Assert.Equal(6, aciklar.Select(x => x.Govde).Distinct().Count());
        Assert.Equal(6, koyular.Select(x => x.Govde).Distinct().Count());
        Assert.Equal("terminal", Temalar.Al("murekkep", false).Ad);
        Assert.Equal("sicak", Temalar.Al("kagit", true).Ad);
    }

    [Fact]
    public void Kabuk_web_iletileri_ve_metinleri_guncel_sozlesmeye_uyar()
    {
        var tc = Soz.Shell.WebMessages.Parse("{\"type\":\"theme-changed\",\"family\":\"sicak\",\"mode\":\"koyu\"}");
        Assert.Equal(Soz.Shell.WebMessageKind.ThemeChanged, tc.Kind);
        Assert.Equal("sicak", tc.Family);
        Assert.Equal("koyu", tc.Mode);

        var rp = Soz.Shell.WebMessages.Parse("{\"type\":\"reset-panel-place\"}");
        Assert.Equal(Soz.Shell.WebMessageKind.ResetPanelPlace, rp.Kind);

        Soz.Shell.Strings.Language = "tr";
        Assert.Equal("Henüz seçili proje yok", Soz.Shell.Strings.Get("tip.noproject"));
        Assert.StartsWith("Ekran bileşeni (Microsoft Edge WebView2) bu bilgisayarda eksik görünüyor.", Soz.Shell.Strings.Get("webview.missing.text"));
        Assert.DoesNotContain(";", Soz.Shell.Strings.Get("webview.missing.text"));
        Assert.DoesNotContain("—", Soz.Shell.Strings.Get("webview.missing.text"));
    }

    [Fact]
    public void Renk_karistir_ve_alfa()
    {
        var k = Renk.Karistir(Renk.Hex(0x000000), Renk.Hex(0xFFFFFF), 0.5);
        Assert.Equal(128, k.R); Assert.Equal(255, k.A);
        Assert.Equal(0, Renk.Hex(0x123456, 0).A);
    }
}
