using System.Text.Json;
using Soz.Shell;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>
/// k8: ön bakış kutusunda tıklanabilir görünen her satır bir şey yapar; oturum satırı kendi sayfasına gider; satırda sade neden ve süre görünür;
/// kullanıcı eylemi olmadan hiçbir pencere odak almaz (dikte yazısı kaybolmasın).
/// </summary>
public class KapsulK8Tests
{
    private static readonly DateTimeOffset Simdi = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Once(int dk) => Simdi.AddMinutes(-dk);

    private static Is Soru(int dk = 3, string id = "j:3") => new(id, IsTuru.Soru, "Sürüm", "sürüm", Neden: "question", Baslangic: Once(dk));
    private static Is Izin(int dk = 12, string id = "ask:job:a:1") => new(id, IsTuru.Soru, "Ödeme", "ödeme", Neden: "approval", IsNo: 5, Baslangic: Once(dk));
    private static Is Bitti(int dk = 60, string id = "done:job:d:1") => new(id, IsTuru.Bitti, "Giriş", "giriş", Neden: "passed", IsNo: 9, Baslangic: Once(dk));
    private static Is Calisan(string id = "j:7") => new(id, IsTuru.Calisiyor, "Derleme", "derleme");
    private static Is Belirsiz(string id = "uncertain:ses:9:1") => new(id, IsTuru.Belirsiz, "Oturum", "oturum", false, Neden: "session_quiet", OturumNo: 9, Baslangic: Once(90));

    private static List<OnBakisSatiri> Kur(IReadOnlyList<Is>? b = null, IReadOnlyList<Is>? c = null, IReadOnlyList<Is>? u = null, string? proje = null)
        => OnBakisIcerigi.Kur(b ?? new List<Is>(), c ?? new List<Is>(), u ?? new List<Is>(), proje, Simdi);

    // ------------------------------------------------------------ 1. hangi satır tıklanır
    [Fact]
    public void Dolu_listede_baslik_tiklanmaz_is_satiri_proje_ve_daha_satiri_tiklanir()
    {
        var bekleyen = new[] { Izin(), Soru(), Bitti(), Soru(1, "j:31"), Soru(2, "j:32"), Soru(4, "j:33"), Soru(5, "j:34") };
        var l = Kur(bekleyen, new[] { Calisan() }, new[] { Belirsiz() }, "Hesap makinesi");

        Assert.All(l.Where(s => s.Baslik), s => Assert.False(s.Tiklanir));
        Assert.Equal(3, l.Count(s => s.Baslik));
        Assert.True(l.Single(s => s.Kimlik == "ask:job:a:1").Tiklanir);          // iş satırı
        Assert.True(l.Single(s => s.Kimlik == "j:7").Tiklanir);                  // çalışan iş
        Assert.True(l.Single(s => s.Kimlik == "uncertain:ses:9:1").Tiklanir);    // oturum satırı
        var daha = l.Single(s => s.Metin == "+3 daha");
        Assert.True(daha.Tiklanir); Assert.Equal("sayfa:suan", daha.Kimlik);
        var pr = l.Single(s => s.Metin == "Proje: Hesap makinesi");
        Assert.True(pr.Tiklanir); Assert.Equal("sayfa:is", pr.Kimlik);
        Assert.DoesNotContain(l, s => s.Metin.Contains("Bir işe tıkla"));
    }

    [Fact]
    public void Tiklanmayan_satir_yalniz_baslik_ya_da_bos_cumledir_hicbiri_soluk_ipucu_degil()
    {
        var l = Kur(new[] { Izin() }, new[] { Calisan() }, new[] { Belirsiz() }, "P");
        Assert.All(l.Where(s => !s.Tiklanir), s => Assert.True(s.Baslik, s.Metin));
        Assert.All(l.Where(s => s.Tiklanir), s => Assert.False(s.Soluk, s.Metin));   // tıklanır satır soluk (pasif) görünmez
    }

    [Fact]
    public void Bos_durumda_tek_cumle_ve_tiklanir_tek_satir_var()
    {
        var l = Kur(proje: "Hesap makinesi");
        Assert.Equal(2, l.Count);
        Assert.Equal("Şu an seni bekleyen iş yok.", l[0].Metin); Assert.False(l[0].Tiklanir); Assert.False(l[0].Baslik);
        Assert.Equal("Söz'ü aç", l[1].Metin); Assert.True(l[1].Tiklanir); Assert.Equal("sayfa:ac", l[1].Kimlik);
        Assert.DoesNotContain(l, s => s.Metin.Contains("Bir işe tıkla") || s.Metin.Contains("Seni bekleyenler"));
    }

    [Fact]
    public void Bekleyen_yok_ama_calisan_varsa_cumle_ve_calisanlar_gelir_ipucu_cikmaz()
    {
        var l = Kur(c: new[] { Calisan() });
        Assert.Equal("Şu an seni bekleyen iş yok.", l[0].Metin);
        Assert.Contains(l, s => s.Metin == "Çalışanlar  1" && s.Baslik);
        Assert.DoesNotContain(l, s => s.Metin.Contains("Bir işe tıkla"));
        Assert.DoesNotContain(l, s => s.Kimlik == "sayfa:ac");
    }

    // ------------------------------------------------------------ 2. oturum satırı kendi sayfasına gider
    [Theory]
    [InlineData("o:12", "#/suan?oturum=12")]
    [InlineData("s:Bitti:0", "#/suan")]
    [InlineData("ask:ses:9:2026", "#/suan?oturum=9")]
    [InlineData("done:ext:codex:abc:1", "#/suan")]
    [InlineData("m:baglanti", "#/suan")]
    [InlineData("j:7", "#/is/7")]
    [InlineData("ask:job:abc", "#/is")]
    [InlineData("sayfa:suan", "#/suan")]
    [InlineData("sayfa:is", "#/is")]
    public void Panel_adresi_oturum_ve_sayfa_kimlikleri(string id, string beklenen)
    {
        Assert.Equal(beklenen, KabukProtokol.PanelAdresi(id));
        Assert.True(KabukProtokol.AdresIzinli(KabukProtokol.PanelAdresi(id)));
    }

    [Fact]
    public void Sayfa_ac_ve_kimliksiz_panel_oldugu_yerde_acilir()
    {
        Assert.Null(KabukProtokol.PanelAdresi("sayfa:ac"));
        Assert.Null(KabukProtokol.PanelAdresi(null));
    }

    private const string Attention = """
        { "rev": 5, "sentence": "x", "budget": { "dailyLimit": 3, "usedToday": 0, "suppressedToday": 0 },
          "items": [
            { "id": "ask:job:u1:t", "kind": "ask", "reason": "approval", "title": "Ödeme", "jobId": 5, "signal": true, "at": "2026-11-23T11:48:00Z", "ageSeconds": 720 },
            { "id": "done:ses:9:t", "kind": "done", "reason": "session_done", "title": "Oturum", "sessionId": 9, "signal": true, "ageSeconds": 3700 },
            { "id": "ask:ext:codex:x:t", "kind": "ask", "reason": "session_waiting", "title": "Dış", "signal": true }
          ],
          "running": [ { "jobId": 7, "title": "Derleme" } ] }
        """;

    private static List<Is> Ayristir()
    {
        using var d = JsonDocument.Parse(Attention);
        return SiraBendeCevabi.Ayristir(d.RootElement)!.Isler.ToList();
    }

    [Fact]
    public void Sunucu_cevabindan_is_ve_oturum_numarasi_okunur_tiklayinca_dogru_kimlik_gider()
    {
        var l = Ayristir();
        Assert.Equal("j:5", l[0].PanelKimligi);
        Assert.Equal("o:9", l[1].PanelKimligi);
        Assert.Equal("ask:ext:codex:x:t", l[2].PanelKimligi);   // numarası yok: kendi kimliği, adres "Şu An"
        Assert.Equal("#/suan?oturum=9", KabukProtokol.PanelAdresi(l[1].PanelKimligi));
        Assert.Equal("#/is/5", KabukProtokol.PanelAdresi(l[0].PanelKimligi));
        Assert.Equal("#/suan", KabukProtokol.PanelAdresi(l[2].PanelKimligi));
    }

    [Fact]
    public void Tam_panel_acilirken_kabuga_giden_kimlik_panel_kimligidir()
    {
        var m = new KapsulMantik(new Ayar(), s => s.Length * 7.0); m.BostaSifirla(0);
        m.IsleriGuncelle(Ayristir(), 1000);
        m.TamPanelAc(1100, "done:ses:9:t");
        var e = Assert.Single(m.Eylemler, x => x.Tur == EylemTuru.PanelAc && x.Kip == PanelKipi.Tam);
        Assert.Equal("o:9", e.IsId);
        Assert.Equal("#/suan?oturum=9", KabukProtokol.PanelAdresi(e.IsId));

        m.Eylemler.Clear();
        m.TamPanelAc(1200, "ask:job:u1:t");
        Assert.Equal("j:5", m.Eylemler.Single(x => x.Tur == EylemTuru.PanelAc).IsId);
    }

    [Theory]
    [InlineData("suan", "sayfa:suan")]
    [InlineData("is", "sayfa:is")]
    [InlineData("ac", "sayfa:ac")]
    public void Sayfa_satirlari_is_secmeden_tam_paneli_acar(string sayfa, string beklenenKimlik)
    {
        var m = new KapsulMantik(new Ayar(), s => s.Length * 7.0); m.BostaSifirla(0);
        m.IsleriGuncelle(new[] { Soru() }, 1000);
        m.TamPanelSayfaAc(1100, sayfa);
        var e = Assert.Single(m.Eylemler, x => x.Tur == EylemTuru.PanelAc && x.Kip == PanelKipi.Tam);
        Assert.Equal(beklenenKimlik, e.IsId);      // seçili iş (j:3) değil, sayfa
        Assert.Equal(PanelKipi.Tam, m.Panel);
    }

    // ------------------------------------------------------------ 3. satır metni biçimi
    [Fact]
    public void Satir_yazisi_neden_ve_sure_gosterir()
    {
        Assert.Equal("izin bekliyor · 12 dk", Izin(12).SagYazi(Simdi));
        Assert.Equal("soru sordu · 3 dk", Soru(3).SagYazi(Simdi));
        Assert.Equal("bitti, kontrol et · 1 saat", Bitti(60).SagYazi(Simdi));
        Assert.Equal("uzun süredir sessiz · 1 saat", Belirsiz().SagYazi(Simdi));
        Assert.Equal("çalışıyor", Calisan().SagYazi(Simdi));                       // çalışanda süre yok
        Assert.Equal("soru sordu", new Is("j:1", IsTuru.Soru, "x", "x").SagYazi(Simdi));  // başlangıç bilinmiyor: yalnız neden
    }

    [Theory]
    [InlineData(0, "az önce")]
    [InlineData(59, "az önce")]
    [InlineData(60, "1 dk")]
    [InlineData(3599, "59 dk")]
    [InlineData(3600, "1 saat")]
    [InlineData(7199, "1 saat")]
    [InlineData(86399, "23 saat")]
    [InlineData(172800, "2 gün")]
    public void Sure_metni(int saniye, string beklenen) => Assert.Equal(beklenen, Is.SureMetni(TimeSpan.FromSeconds(saniye)));

    [Fact]
    public void Sunucu_ogesinden_neden_ve_sure_okunur()
    {
        var l = Ayristir();
        Assert.Equal("izin bekliyor", l[0].NedenMetni);
        Assert.Equal("bitti, kontrol et", l[1].NedenMetni);
        Assert.Equal("seni bekliyor", l[2].NedenMetni);
        Assert.NotNull(l[0].Baslangic); Assert.NotNull(l[1].Baslangic);   // "at" ve "ageSeconds" ikisi de okunur
        Assert.Null(l[2].Baslangic);                                      // ikisi de yoksa süre yazılmaz
    }

    [Fact]
    public void Ekrandaki_hicbir_metinde_uzun_tire_noktali_virgul_ya_da_teknik_kod_yok()
    {
        var bekleyen = new[] { Izin(), Soru(), Bitti(), new Is("k", IsTuru.Kaldi, "", "", Neden: "start_failed", Baslangic: Once(5)), new Is("q", IsTuru.Karar, "K", "k", Neden: "decision", Baslangic: Once(2)) };
        var l = Kur(bekleyen, new[] { Calisan() }, new[] { Belirsiz(), new Is("u", IsTuru.Belirsiz, "U", "u", Neden: "unproven"), BaglantiKarar.BelirsizSatir() }, "Proje");
        foreach (var s in l)
        {
            foreach (var metin in new[] { s.Metin, s.Sag ?? "" })
            {
                Assert.DoesNotContain('—', metin); Assert.DoesNotContain('–', metin); Assert.DoesNotContain(';', metin);
                Assert.DoesNotContain('_', metin);                                  // approval, session_done gibi kod sızmaz
            }
        }
        Assert.All(l.Where(s => s.Sag != null), s => Assert.True(s.Sag!.Length > 0));
        Assert.Equal("bağlantı yok", BaglantiKarar.BelirsizSatir().NedenMetni);
    }

    [Theory]
    [InlineData("approval", "izin bekliyor")] [InlineData("session_permission", "izin bekliyor")] [InlineData("question", "soru sordu")]
    [InlineData("session_question", "soru sordu")] [InlineData("decision", "karar bekliyor")] [InlineData("proof_failed", "kontrol geçmedi")]
    [InlineData("start_failed", "başlatılamadı")] [InlineData("session_failed", "hata verip durdu")] [InlineData("passed", "bitti, kontrol et")]
    [InlineData("session_done", "bitti, kontrol et")] [InlineData("run_lost", "yardımcının süreci kayıp")] [InlineData("session_turn_end", "sırayı sana verdi")] [InlineData("session_quiet", "uzun süredir sessiz")]
    public void Neden_kodu_sade_turkceye_cevrilir(string kod, string beklenen)
        => Assert.Equal(beklenen, new Is("x", IsTuru.Bitti, "t", "t", Neden: kod).NedenMetni);

    [Fact]
    public void Neden_yoksa_turden_gelir()
    {
        Assert.Equal("soru sordu", new Is("a", IsTuru.Soru, "t", "t").NedenMetni);
        Assert.Equal("karar bekliyor", new Is("a", IsTuru.Karar, "t", "t").NedenMetni);
        Assert.Equal("bitti, kontrol et", new Is("a", IsTuru.Bitti, "t", "t").NedenMetni);
        Assert.Equal("durumu belli değil", new Is("a", IsTuru.Belirsiz, "t", "t").NedenMetni);
    }

    // ------------------------------------------------------------ 4-5. odak kuralı
    [Theory]
    [InlineData("Kullanici", false, true)]
    [InlineData("Kullanici", true, true)]       // kullanıcı tıkladı: dikte sürerken de öne gelir
    [InlineData("IkinciOrnek", false, true)]
    [InlineData("IkinciOrnek", true, false)]    // dikte sürerken ikinci başlatma odak çalmaz
    [InlineData("Acilis", false, false)]        // program açılışı hiçbir zaman
    [InlineData("Acilis", true, false)]
    [InlineData("Olcum", false, false)]
    [InlineData("Olcum", true, false)]
    public void Odak_yalniz_kullanici_eyleminde_alinir(string neden, bool dikte, bool beklenen)
        => Assert.Equal(beklenen, OdakPolitikasi.OdakAlsin(Enum.Parse<PanelAcilisi>(neden), dikte));

    [Theory]
    [InlineData("Recording", true)]
    [InlineData("Processing", true)]
    [InlineData("Idle", false)]
    [InlineData("Delivered", false)]
    [InlineData("ClipboardOnly", false)]
    [InlineData("Unavailable", false)]   // eski KarıncaAI durum vermez: koruma etkisiz, asıl çözüm odak kuralıdır
    public void Dikte_durumu_dinliyor_ya_da_ceviriyorsa_suruyor_sayilir(string durum, bool beklenen)
        => Assert.Equal(beklenen, OdakPolitikasi.DikteSuruyor(Enum.Parse<DictationHostStatus>(durum)));

    // ------------------------------------------------------------ odak almayan gösterim bayrakları (kaynak taraması)
    private static string Kok()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Soz.Shell"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("urun klasörü bulunamadı");
    }
    private static string Oku(params string[] yol) => File.ReadAllText(Path.Combine(new[] { Kok() }.Concat(yol).ToArray()));

    [Fact]
    public void Kapsul_ve_onbakis_penceresi_odak_almayan_bayrakla_olusur_ve_tiklamayla_aktiflenmez()
    {
        var kapsul = Oku("src", "Soz.Capsule", "Pencere", "Kapsul.cs");
        var acilir = Oku("src", "Soz.Capsule", "Pencere", "Acilir.cs");
        Assert.Contains("N.WS_EX_NOACTIVATE | N.WS_EX_TOOLWINDOW", kapsul);
        Assert.Contains("N.WS_EX_NOACTIVATE | N.WS_EX_TOOLWINDOW", acilir);
        Assert.Contains("N.ShowWindow(_hwnd, 4)", acilir);                       // SW_SHOWNOACTIVATE
        Assert.Contains("case N.WM_MOUSEACTIVATE: return N.MA_NOACTIVATE;", acilir);
        Assert.Contains("N.MA_NOACTIVATE", kapsul);
        // kapsül yalnız yazı satırı açıkken (kullanıcı kısayola bastı ya da çift tıkladı) odak alır: SetForegroundWindow yalnız GirisBasla ve GirisBitir içinde
        var govde = kapsul.Split('\n').Where(s => s.Contains("N.SetForegroundWindow(") || s.Contains("N.SetFocus(")).Count();
        Assert.Equal(2, govde);   // GirisBasla (aynı satırda iki çağrı) + GirisBitir (önceki pencereye geri verir)
    }

    [Fact]
    public void Panel_kullanici_eylemi_olmadan_odaksiz_gosterilebilir()
    {
        var panel = Oku("src", "Soz.Shell", "PanelForm.cs");
        Assert.Contains("protected override bool ShowWithoutActivation => _odaksizAcilis;", panel);
        Assert.Contains("Native.SWP_NOACTIVATE", panel);
        Assert.Contains("if (odakAl) Activate();", panel);
        var ctx = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.Contains("OpenPanelAndBringToFront(neden)", ctx);
        Assert.Contains("OdakPolitikasi.OdakAlsin(neden, DikteSuruyor)", ctx);
        Assert.DoesNotContain("OpenPanelAndBringToFront()", ctx);                 // nedensiz çağrı kalmadı
        var olcum = Oku("src", "Soz.Shell", "Olcum.cs");
        Assert.Contains("OpenPanel(odakAl: false)", olcum);
    }

    [Fact]
    public void Kapsul_ustunden_gelen_on_plan_hakki_herkese_degil_yalniz_kabuga_verilir()
    {
        var kanal = Oku("src", "Soz.Capsule", "Pencere", "KabukKanali.cs");
        Assert.DoesNotContain("AllowSetForegroundWindow(unchecked((uint)-1))", kanal);   // ASFW_ANY kalktı
        Assert.Contains("UstSurecNo()", kanal);
    }
}
