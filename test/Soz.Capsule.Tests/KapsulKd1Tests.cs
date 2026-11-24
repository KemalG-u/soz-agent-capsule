using System.Text.RegularExpressions;
using Soz.Shell;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>
/// kd1: küçük pencere ve masaüstü kabuğundaki kusurlar. Ekran okuyucu cümleleri, kısayol ve gizleme süresi ayardan gelir, bilgiye ulaşılamayınca
/// "hiç iş yok" denmez, tepsi ve küçük pencere aynı kaynaktan sayar, sunucu açılmazsa boş sayfa yerine açıklama ve çalışan düğme vardır.
/// </summary>
public class KapsulKd1Tests
{
    private static string Kok()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Soz.Shell"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("urun klasörü bulunamadı");
    }
    private static string Oku(params string[] yol) => File.ReadAllText(Path.Combine(new[] { Kok() }.Concat(yol).ToArray()));

    private static KapsulMantik Yeni() => new(new Ayar(), s => s.Length * 7.0);
    private static readonly string[] IcDurumAdlari = { "UYKU", "SOLUK", "BELİRGİN", "DURUMLU", "ÖN BAKIŞ", "PANEL", "GİRİŞ" };

    // ------------------------------------------------------------ 3. ekran okuyucu adı
    [Fact]
    public void Ekran_okuyucu_adi_bekleyen_isi_insan_cumlesiyle_soyler()
    {
        var m = Yeni();
        m.IsleriGuncelle(new[] { new Is("a", IsTuru.Soru, "A", "a"), new Is("b", IsTuru.Bitti, "B", "b") }, 0, kesin: true);
        var ad = m.EkranOkuyucuAdi(m.GorunumHesapla(1000), null);
        Assert.Equal("Söz küçük pencere. 2 iş seni bekliyor.", ad);
    }

    [Fact]
    public void Ekran_okuyucu_adinda_ic_durum_adi_hicbir_durumda_gecmez()
    {
        var m = Yeni();
        var durumlar = new List<string>();
        foreach (var sev in new[] { Seviye.Uyku, Seviye.Soluk, Seviye.Belirgin })
            foreach (var panel in new[] { PanelKipi.Yok, PanelKipi.OnBakis, PanelKipi.Tam })
                foreach (var giris in new[] { false, true })
                    foreach (var liste in new[] { Array.Empty<Is>(), new[] { new Is("a", IsTuru.Soru, "A", "a") }, new[] { new Is("c", IsTuru.Calisiyor, "C", "c") }, new[] { BaglantiKarar.BelirsizSatir() } })
                    {
                        m.IsleriGuncelle(liste, 0, kesin: true);
                        m.Sev = sev; m.Panel = panel; m.Giris = giris;
                        var ad = m.EkranOkuyucuAdi(m.GorunumHesapla(5000), "Söz");
                        durumlar.Add(ad);
                        foreach (var t in IcDurumAdlari) Assert.DoesNotContain(t, ad);
                        Assert.DoesNotContain("Durum:", ad);
                        Assert.StartsWith("Söz küçük pencere.", ad);
                        Assert.DoesNotContain('—', ad); Assert.DoesNotContain(';', ad);
                    }
        Assert.True(durumlar.Distinct().Count() > 6);
    }

    [Fact]
    public void Ekran_okuyucu_adi_bos_calisan_panel_ve_baglanti_durumlarini_soyler()
    {
        var m = Yeni();
        m.IsleriGuncelle(Array.Empty<Is>(), 0, kesin: true);
        Assert.Equal("Söz küçük pencere. Seni bekleyen iş yok. Etkin proje: Söz.", m.EkranOkuyucuAdi(m.GorunumHesapla(0), "Söz"));
        m.IsleriGuncelle(new[] { new Is("c", IsTuru.Calisiyor, "C", "c") }, 1, kesin: true);
        Assert.Equal("Söz küçük pencere. Bir iş çalışıyor.", m.EkranOkuyucuAdi(m.GorunumHesapla(1), null));
        m.Panel = PanelKipi.Tam;
        Assert.Contains("Panel açık.", m.EkranOkuyucuAdi(m.GorunumHesapla(1), null));
        m.Panel = PanelKipi.Yok;
        m.IsleriGuncelle(new[] { BaglantiKarar.BelirsizSatir() }, 2, kesin: false);
        var ad = m.EkranOkuyucuAdi(m.GorunumHesapla(2), null);
        Assert.Contains("Bilgiye ulaşamıyorum", ad);
        Assert.DoesNotContain("Seni bekleyen iş yok", ad);
    }

    // ------------------------------------------------------------ 5. kısayol ve gizleme süresi ayardan
    [Fact]
    public void Yardim_metni_ayardaki_gercek_kisayolu_soyler_kisayol_yoksa_hic_soz_etmez()
    {
        Assert.Contains("Kısayol: Ctrl+Alt+K.", Erisim.Yardim("Ctrl+Alt+K"));
        Assert.DoesNotContain("Ctrl+Alt+J", Erisim.Yardim("Ctrl+Alt+K"));
        Assert.DoesNotContain("Kısayol", Erisim.Yardim(null));
        Assert.DoesNotContain("Kısayol", Erisim.Yardim(""));
        Assert.DoesNotContain("Kısayol", Erisim.Yardim(Erisim.KisayolYok));
        Assert.Contains("çift tıklayınca yazı satırını açar", Erisim.Yardim(null));
        Assert.DoesNotContain("Ctrl+Alt+J", Oku("src", "Soz.Capsule", "Pencere", "Erisilebilirlik.cs"));   // sabit metin kalmadı
    }

    [Fact]
    public void Gizle_maddesi_ayardaki_sureyi_yazar()
    {
        Assert.Equal("1 saat", KabukProtokol.GizleSuresi(3600000));
        Assert.Equal("30 dakika", KabukProtokol.GizleSuresi(1800000));
        Assert.Equal("90 dakika", KabukProtokol.GizleSuresi(5400000));
        Assert.Equal("2 saat", KabukProtokol.GizleSuresi(7200000));
        Assert.Equal("1 dakika", KabukProtokol.GizleSuresi(60000));
        foreach (var kabuk in new[] { true, false })
        {
            Assert.Equal("Küçük pencereyi gizle (30 dakika)", KabukProtokol.Pano(kabuk, "cam", 1800000).Single(p => p.Kimlik == "gizle").Metin);
            Assert.Equal("Küçük pencereyi gizle (1 saat)", KabukProtokol.Pano(kabuk, "cam").Single(p => p.Kimlik == "gizle").Metin);
        }
        Assert.Contains("_ayar.HideMs", Oku("src", "Soz.Capsule", "Pencere", "Kapsul.cs"));   // pano ayardaki değeri verir
    }

    [Fact]
    public void Kabuk_kapsule_gercek_kisayolu_verir()
    {
        var kod = Oku("src", "Soz.Shell", "KapsulYoneticisi.cs");
        Assert.Contains("\"--kisayol\"", kod);
        Assert.Contains("NewJobInfo is { Registered: true } ? NewJobInfo.Display : null", Oku("src", "Soz.Shell", "ShellContext.cs"));
        Assert.Contains("case \"--kisayol\": o.Kisayol = Sonraki(); break;", Oku("src", "Soz.Capsule", "Pencere", "Secenekler.cs"));
        Assert.Contains("_o.Kabuk ? _o.Kisayol : _ayar.Kisayol", Oku("src", "Soz.Capsule", "Pencere", "Kapsul.cs"));
    }

    // ------------------------------------------------------------ 6. bilgiye ulaşılamayınca
    [Fact]
    public void Baglanti_koptuktan_sonra_haptaki_yazi_bilgiye_ulasamiyorum_der_ve_eski_isleri_gostermez()
    {
        var m = Yeni(); m.Sev = Seviye.Belirgin;
        m.IsleriGuncelle(new[] { new Is("a", IsTuru.Soru, "A", "a") }, 0, kesin: true);
        Assert.Equal("soru", m.IcerikHesapla(10)!.Yazi);
        m.IsleriGuncelle(new[] { BaglantiKarar.BelirsizSatir() }, 6000, kesin: false);   // motor ölü: eski liste gider, yerine tek satır
        var c = m.IcerikHesapla(6000)!;
        Assert.Equal(BaglantiKarar.HapYazisi, c.Yazi);
        Assert.Equal(DurumSinifi.Belirsiz, c.Sinif);
        Assert.Empty(m.Bekleyenler);
    }

    [Fact]
    public void On_bakis_kutusu_bilgi_yokken_seni_bekleyen_is_yok_demez()
    {
        var l = OnBakisIcerigi.Kur(Array.Empty<Is>(), Array.Empty<Is>(), new[] { BaglantiKarar.BelirsizSatir() }, "Söz", DateTimeOffset.UtcNow);
        Assert.DoesNotContain(l, s => s.Metin == OnBakisIcerigi.BosCumle);
        Assert.Contains(l, s => s.Metin == BaglantiKarar.KutuCumlesi);
        Assert.Contains(l, s => s.Kimlik == OnBakisIcerigi.SayfaAc && s.Tiklanir);
        // gerçekten iş yokken eski cümle yerinde
        var bos = OnBakisIcerigi.Kur(Array.Empty<Is>(), Array.Empty<Is>(), Array.Empty<Is>(), null, DateTimeOffset.UtcNow);
        Assert.Contains(bos, s => s.Metin == OnBakisIcerigi.BosCumle);
        Assert.DoesNotContain('—', BaglantiKarar.KutuCumlesi); Assert.DoesNotContain(';', BaglantiKarar.KutuCumlesi);
    }

    [Fact]
    public void Motor_kaydi_var_ama_surec_olmusse_notr_degil_belirsiz_gosterilir()
    {
        var kod = Oku("src", "Soz.Capsule", "Pencere", "Motor.cs");
        Assert.Contains("k == null ? BaglantiGorunumu.Notr : BaglantiGorunumu.Belirsiz", kod);
        Assert.Equal(BaglantiGorunumu.Belirsiz, BaglantiKarar.Karar(true, false, 31_000));
        Assert.Equal(BaglantiGorunumu.Notr, BaglantiKarar.Karar(false, false, 31_000));
    }

    // ------------------------------------------------------------ 9. tepsi ile küçük pencere aynı kaynaktan sayar
    [Fact]
    public void Tepsi_sayilari_kucuk_pencerenin_kullandigi_dikkat_listesinden_gelir()
    {
        var json = """
        { "rev": 5, "items": [
          { "id": "1", "kind": "ask" }, { "id": "2", "kind": "accept" }, { "id": "3", "kind": "blocked" }, { "id": "4", "kind": "done" },
          { "id": "5", "kind": "uncertain" }, { "id": "6", "kind": "uncertain" } ], "running": [] }
        """;
        using var d = System.Text.Json.JsonDocument.Parse(json);
        var ogeler = Json.ParseAttention(d.RootElement)!;
        var (bekleyen, belirsiz) = DikkatSayaci.Say(ogeler);
        Assert.Equal((4, 2), (bekleyen, belirsiz));
        // küçük pencerenin kendi ayrıştırıcısı aynı listeyi aynı sayar
        var kapsul = SiraBendeCevabi.Ayristir(d.RootElement)!;
        Assert.Equal(bekleyen, kapsul.Isler.Count(i => i.Bekliyor));
        Assert.Equal(belirsiz, kapsul.Isler.Count(i => i.Tur == IsTuru.Belirsiz));
        Assert.Contains("GetAttentionAsync", Oku("src", "Soz.Shell", "ShellContext.cs"));
    }

    [Fact]
    public void Tepsi_yazisi_sunucu_yokken_ve_ulasilamazken_ayri_sade_cumle_verir()
    {
        Strings.Language = "tr";
        Assert.Equal("açılamadı", Strings.Get("tip.offline"));
        Assert.Equal("bilgiye ulaşamıyorum", Strings.Get("tip.unreachable"));
        Assert.NotEqual(Strings.Get("tip.nodata"), Strings.Get("tip.unreachable"));
    }

    // ------------------------------------------------------------ 1, 2, 7, 8, 10: kabuk metinleri ve kaynak kuralları
    [Fact]
    public void Panelden_yapistirma_basarisiz_olunca_kayit_okunamadi_demez()
    {
        Strings.Language = "tr";
        var kod = Oku("src", "Soz.Shell", "ShellContext.cs");
        var govde = kod[kod.IndexOf("private async Task PasteFromPanelAsync", StringComparison.Ordinal)..];
        govde = govde[..govde.IndexOf("/// <summary>Kısa sonuç", StringComparison.Ordinal)];
        Assert.DoesNotContain("flash.importfail", govde);
        Assert.Contains("_ => \"flash.text.failed\"", govde);
        var m = Strings.Get("flash.text.failed");
        Assert.Contains("Yapıştırılamadı", m);
        Assert.Contains("kendin yapıştırabilirsin", m);
        Assert.DoesNotContain("Kayıt", m);
    }

    [Fact]
    public void Sunucu_acilmazsa_panel_aciklama_ve_yeniden_dene_dugmesi_gosterir_ham_hata_cikmaz()
    {
        Strings.Language = "tr";
        var ctx = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.Contains("Panel.HataGoster(Strings.Get(\"panel.error.title\")", ctx);
        Assert.Contains("Panel.YenidenDeneIstendi += () => _ = RetryServerAsync();", ctx);
        Assert.Contains("Panel?.SunucuyaBaglan(BaseUrl + \"/\");", ctx);
        var panel = Oku("src", "Soz.Shell", "PanelForm.cs");
        Assert.Contains("_hataDugme.Click += (_, _) => YenidenDeneIstendi?.Invoke();", panel);
        Assert.Contains("if (!_hataModu) _init ??= InitWebViewAsync(_t0, no);", panel);
        foreach (var k in new[] { "panel.error.title", "panel.error.text", "panel.error.again", "panel.error.retry", "msg.server.fail" })
        {
            var t = Strings.Get(k);
            Assert.NotEqual(k, t);
            Assert.DoesNotContain('—', t); Assert.DoesNotContain(';', t);
            Assert.DoesNotContain("sunucu", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exception", t, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal("Yeniden dene", Strings.Get("panel.error.retry"));
        // ex.Message ekrana taşınmıyor: açılış hata kutuları sade cümle kullanır
        var prog = Oku("src", "Soz.Shell", "Program.cs");
        Assert.DoesNotContain("Strings.Get(\"start.failed\", ex.Message", prog);
        Assert.DoesNotContain("\"Ayrıntı günlükte.\"", prog);
    }

    [Fact]
    public void Acilis_hata_kutusunda_ayrinti_sozu_bir_kez_gecer()
    {
        Strings.Language = "tr";
        var s = Strings.Get("start.failed", Strings.Get("start.failed.reason"), @"C:\x\soz-shell.log");
        Assert.Empty(Regex.Matches(s, "Ayrıntı"));
        Assert.Contains("soz-shell.log", s);
        Assert.DoesNotContain('—', s); Assert.DoesNotContain(';', s);
    }

    [Fact]
    public void Ikinci_kopya_ilk_kopya_yanit_vermezse_sessiz_cikmaz()
    {
        Strings.Language = "tr";
        var prog = Oku("src", "Soz.Shell", "Program.cs");
        Assert.Contains("var cevapVerdi = SingleInstanceChannel.TrySignalShowPanel(pipeName);", prog);
        Assert.Contains("Strings.Get(\"dlg.second.text\")", prog);
        var t = Strings.Get("dlg.second.text");
        Assert.Contains("yanıt vermiyor", t);
        Assert.DoesNotContain('—', t); Assert.DoesNotContain(';', t);
    }

    [Fact]
    public void Kullanici_eylemini_etkileyen_hatalar_kisiye_sade_cumleyle_soylenir()
    {
        Strings.Language = "tr";
        var ctx = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.Contains("KisiEylemi(\"küçük pencere komutu (\"", ctx);
        Assert.Contains("private bool KisiEylemi(", ctx);
        Assert.Contains("if (!_exiting) Soyle(mesajAnahtari);", ctx);
        Assert.Contains("Log.Error(ad, ex);", ctx);
        foreach (var k in new[] { "msg.panel.fail", "msg.action.fail" })
        {
            var t = Strings.Get(k);
            Assert.NotEqual(k, t); Assert.DoesNotContain('—', t); Assert.DoesNotContain(';', t);
        }
    }

    // ------------------------------------------------------------ 4. sözcük kuralı: ekrana çıkan kabuk metinlerinde ajan, kanıt, ölçüt yok
    [Fact]
    public void Kabuk_metin_tablosunda_ajan_kanit_olcut_uzun_tire_noktali_virgul_yok()
    {
        Strings.Language = "tr";
        var anahtarlar = Regex.Matches(Oku("src", "Soz.Shell", "Strings.cs"), "\\[\"([a-z.]+)\"\\] = \\(").Select(m => m.Groups[1].Value).ToList();
        Assert.True(anahtarlar.Count > 60);
        foreach (var a in anahtarlar)
        {
            var t = Strings.Get(a);
            foreach (var y in new[] { "ajan", "kanıt", "ölçüt", "kanıtsız" }) Assert.DoesNotContain(y, t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('—', t); Assert.DoesNotContain('–', t); Assert.DoesNotContain(';', t);
        }
        // kapsülün ekrana çıkan kaynak dizeleri
        foreach (var f in new[] { "Mantik/Isler.cs", "Mantik/KabukProtokol.cs", "Mantik/OnBakisIcerigi.cs", "Mantik/SiraBende.cs", "Mantik/Erisim.cs", "Pencere/Kapsul.cs" })
            foreach (var satir in Oku("src", "Soz.Capsule", f).Split('\n').Where(s => !s.TrimStart().StartsWith("//")))
                foreach (Match m in Regex.Matches(satir, "\"([^\"\\\\]*)\""))
                    Assert.DoesNotMatch(new Regex("\\bajan", RegexOptions.IgnoreCase), m.Groups[1].Value);
    }

    // ------------------------------------------------------------ 11. balon tıklaması o oturumun kartını açar
    [Fact]
    public void Balon_adresi_isi_oturumu_ve_bilinmeyeni_dogru_ayirir()
    {
        AttentionItemInfo O(long job = 0, long oturum = 0, string dis = "") => new("k", "session_permission", "P", "H", "c", "d", "t", "ask", job, oturum, dis);
        Assert.Equal("#/is/7", BildirimYoneticisi.Adres(O(job: 7)));
        Assert.Equal("#/suan?oturum=abc%2F1", BildirimYoneticisi.Adres(O(dis: "abc/1")));
        Assert.Equal("#/suan?oturum=12", BildirimYoneticisi.Adres(O(oturum: 12)));
        Assert.Equal("#/suan", BildirimYoneticisi.Adres(O()));
        // iş her zaman önce gelir
        Assert.Equal("#/is/3", BildirimYoneticisi.Adres(O(job: 3, oturum: 9, dis: "x")));
        // kimlikte denetim karakteri varsa güvenli genel sayfa
        Assert.Equal("#/suan", BildirimYoneticisi.Adres(O(dis: "a\nb")));
    }

    // ------------------------------------------------------------ 13. ana pencere odak kaybında kendini gizlemez
    [Fact]
    public void Panel_odak_kaybinda_varsayilan_olarak_gizlenmez()
    {
        var acik = PanelOdak.Coz(null, null);
        Assert.False(acik);
        Assert.False(PanelOdak.OdakKaybindaGizle(acik, gorunur: true, klasorSeciliyor: false, gosterildiktenBeriMs: 5000));
    }

    [Theory]
    [InlineData("1", null, true)]
    [InlineData("true", false, true)]
    [InlineData("0", true, false)]
    [InlineData("false", true, false)]
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    [InlineData("belki", false, false)]
    public void Panel_odak_ayari_ortam_degiskeni_dosyadan_once_gelir(string? ortam, bool? dosya, bool beklenen)
        => Assert.Equal(beklenen, PanelOdak.Coz(ortam, dosya));

    [Theory]
    [InlineData(true, true, false, 5000, true)]     // ayar açık, görünür: gizle
    [InlineData(true, true, false, 100, false)]     // gösterilir gösterilmez sahte odak oynaması
    [InlineData(true, true, true, 5000, false)]     // klasör seçici açık
    [InlineData(true, false, false, 5000, false)]   // zaten gizli
    [InlineData(false, true, false, 5000, false)]   // ayar kapalı
    public void Panel_odak_kaybi_karari(bool ayar, bool gorunur, bool secici, double ms, bool beklenen)
        => Assert.Equal(beklenen, PanelOdak.OdakKaybindaGizle(ayar, gorunur, secici, ms));

    [Theory]
    [InlineData(true, 5000, "Gizle")]        // etkin panele tıklama: kapat
    [InlineData(false, 100, "Gizle")]        // tıklamanın kendisi etkinliği aldı: kapat
    [InlineData(false, 5000, "OneGetir")]    // başka pencerenin arkasında: öne getir
    public void Tepsi_tiklamasi_etkinse_gizler_arkadaysa_one_getirir(bool etkin, double kayip, string beklenen)
        => Assert.Equal(beklenen, PanelOdak.AcKapa(etkin, kayip).ToString());

    [Fact]
    public void Panel_durum_dosyasi_odak_ayarini_yazar_okur_ve_bozugu_yok_sayar()
    {
        var d = new PanelDurum(null, null, null, true);
        Assert.True(PanelDurum.Oku(d.Json()).OdakKaybindaGizle);
        Assert.Null(PanelDurum.Oku("{}").OdakKaybindaGizle);
        Assert.Null(PanelDurum.Oku("{\"odakKaybindaGizle\":\"evet\"}").OdakKaybindaGizle);
        Assert.False(PanelDurum.Oku("{\"odakKaybindaGizle\":false}").OdakKaybindaGizle);
    }

    [Fact]
    public void Panel_formu_odak_kararini_PanelOdak_uzerinden_verir()
    {
        var f = Oku("src", "Soz.Shell", "PanelForm.cs");
        Assert.Contains("PanelOdak.OdakKaybindaGizle(", f);
        Assert.Contains("PanelOdak.AcKapa(", f);
        Assert.DoesNotContain("> 400) Hide()", f);
    }

    // ------------------------------------------------------------ 12. çalışan iş varken "Söz'ü kapat" sorar
    [Theory]
    [InlineData(true, 0, "Kapat")]          // iş yok: sormadan kapanır
    [InlineData(true, 1, "Sor")]
    [InlineData(true, 7, "Sor")]
    [InlineData(true, null, "BilinmiyorSor")]   // sunucu ayakta ama cevap yok: işi sessizce kesme
    [InlineData(false, null, "Kapat")]      // açılışı başarısız: kaybedilecek iş yok
    [InlineData(false, 3, "Kapat")]
    public void Kapatma_karari(bool sunucu, int? calisan, string beklenen)
        => Assert.Equal(beklenen, CikisKurali.Ver(sunucu, calisan).ToString());

    [Fact]
    public void Kapatma_sorusu_sayiyi_ve_gercek_sonucu_soyler_sade_dille()
    {
        Strings.Language = "tr";
        var soru = CikisKurali.Soru(CikisKurali.Karar.Sor, 2);
        Assert.Contains("Şu an 2 iş çalışıyor", soru);
        Assert.Contains("durdurulur", soru);          // işlere gerçekte olan: durdurulur, silinmez
        Assert.Contains("Aynı işe yeni bir şey ekle", soru);          // geri dönüş yolu
        Assert.EndsWith("Yine de kapatılsın mı?", soru);
        Assert.Contains("öğrenemedim", CikisKurali.Soru(CikisKurali.Karar.BilinmiyorSor, 0));
        Assert.Equal("Söz kapatılsın mı?", Strings.Get("dlg.quit.title"));
    }

    [Fact]
    public void Calisan_is_sayisi_running_dizisinden_okunur()
    {
        int? Say(string json) { using var d = System.Text.Json.JsonDocument.Parse(json); return Json.ParseRunningCount(d.RootElement); }
        Assert.Equal(2, Say("{\"items\":[],\"running\":[{\"jobId\":1},{\"jobId\":2}]}"));
        Assert.Equal(0, Say("{\"running\":[]}"));
        Assert.Null(Say("{\"items\":[]}"));      // alan yoksa 0 denmez: bilinmiyor
        Assert.Null(Say("[]"));
    }

    [Fact]
    public void Kapatma_yolu_menu_ve_kucuk_pencere_ayni_soru_kapisindan_gecer()
    {
        var ctx = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.Contains("(_, _) => _ = CikisIsteginiIsleAsync()", ctx);             // tepsi menüsü
        Assert.Contains("case KomutTuru.Cikis: _ = CikisIsteginiIsleAsync()", ctx); // küçük pencere
        Assert.DoesNotContain("case KomutTuru.Cikis: ExitApp()", ctx);
        Assert.DoesNotContain("if (k.Tur == KomutTuru.Cikis) Politika.CikisIstendi()",Oku("src", "Soz.Shell", "KapsulYoneticisi.cs"));
        // kabuk bağlıyken küçük pencere kendi kendine gizlenmez, kararı kabuğa bırakır
        Assert.Contains("_kopru.KabukVar && _kopru.CikisBildir()", Oku("src", "Soz.Capsule", "Pencere", "Kapsul.cs"));
    }
}
