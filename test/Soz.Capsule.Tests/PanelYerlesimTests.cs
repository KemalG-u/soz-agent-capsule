using System.Drawing;
using Soz.Shell;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>
/// Panelin yeri, boyutu, klasör seçicinin başlangıç klasörü ve boşta bırakma süresi: hepsi pencereden bağımsız saf kararlar (k4).
/// Gözlem 6 (panel sol alta gitti), 8 (yer ve boyut saklanmıyor), 3 (seçici Belgeler'de açılıyor), 4 (bırakma süresi).
/// </summary>
public class PanelYerlesimTests
{
    // Çalışma alanları (görev çubuğu hariç, fiziksel piksel). A birincil, B sağda, L solda (eksi koordinat, daha kısa).
    private static readonly Rectangle A = new(0, 0, 1920, 1040);
    private static readonly Rectangle B = new(1920, 0, 1920, 1080);
    private static readonly Rectangle L = new(-1600, 0, 1600, 860);

    private static bool Icinde(Rectangle r, params Rectangle[] alanlar) => alanlar.Any(a => a.Contains(r));

    [Fact]
    public void Gorunur_ekranda_saklı_yer_aynen_gelir()
    {
        var r = PanelYerlesim.Hesapla(new PanelSakli(100, 100, 440, 680), [A], new Rectangle(1850, 960, 24, 24));
        Assert.Equal(new Rectangle(100, 100, 440, 680), r);
    }

    [Fact]
    public void Ikinci_ekrandaki_saklı_yer_aynen_gelir()
    {
        var r = PanelYerlesim.Hesapla(new PanelSakli(2500, 200, 440, 680), [A, B], new Rectangle(1850, 960, 24, 24));
        Assert.Equal(new Rectangle(2500, 200, 440, 680), r);
    }

    [Fact]
    public void Eksi_koordinatli_sol_ekranda_saklı_yer_aynen_gelir()
    {
        var r = PanelYerlesim.Hesapla(new PanelSakli(-1500, 50, 440, 680), [A, L], new Rectangle(1850, 960, 24, 24));
        Assert.Equal(new Rectangle(-1500, 50, 440, 680), r);
    }

    [Fact]
    public void Iki_ekrani_birden_kaplayan_saklı_yer_oynamaz()
    {
        var r = PanelYerlesim.Hesapla(new PanelSakli(1700, 100, 440, 680), [A, B], new Rectangle(1850, 960, 24, 24));
        Assert.Equal(new Rectangle(1700, 100, 440, 680), r);
    }

    [Fact]
    public void Kopmus_ekranda_saklı_yer_kapsulun_yanina_gelir_ve_boyutu_korunur()
    {
        // B ekranı çıkarıldı: saklı yer artık hiçbir çalışma alanında değil
        var kapsul = new Rectangle(1850, 960, 24, 24);
        var r = PanelYerlesim.Hesapla(new PanelSakli(2500, 200, 400, 600), [A], kapsul);
        Assert.True(Icinde(r, A), r.ToString());
        Assert.Equal(new Size(400, 600), r.Size);
        Assert.Equal(kapsul.Right - 400, r.X);      // sağ kenar kapsülle hizalı
        Assert.True(r.Bottom <= kapsul.Top, "kapsülün üstünde olmalı");
    }

    [Fact]
    public void Kopmus_eksi_koordinatli_ekran_kapsulun_yanina_gelir()
    {
        var r = PanelYerlesim.Hesapla(new PanelSakli(-1500, 50, 440, 680), [A], new Rectangle(1850, 960, 24, 24));
        Assert.True(Icinde(r, A), r.ToString());
    }

    [Fact]
    public void Kismen_disarida_kalan_saklı_yer_calisma_alanina_itilir()
    {
        // sağdan taşıyor (görünen %73): yalnız x düzelir
        var sag = PanelYerlesim.Hesapla(new PanelSakli(1600, 300, 440, 680), [A], new Rectangle(1850, 960, 24, 24));
        Assert.Equal(new Rectangle(1480, 300, 440, 680), sag);
        // alttan taşıyor (görünen %50): yalnız y düzelir
        var alt = PanelYerlesim.Hesapla(new PanelSakli(100, 700, 440, 680), [A], new Rectangle(1850, 960, 24, 24));
        Assert.Equal(new Rectangle(100, 360, 440, 680), alt);
    }

    [Fact]
    public void Cogu_disarida_kalan_saklı_yer_kapsulun_yanina_gelir()
    {
        var r = PanelYerlesim.Hesapla(new PanelSakli(1800, 300, 500, 720), [A], new Rectangle(1850, 960, 24, 24));
        Assert.True(Icinde(r, A), r.ToString());
        Assert.Equal(new Rectangle(1374, 232, 500, 720), r);
    }

    [Fact]
    public void Saklı_yer_yoksa_kapsulun_GUNCEL_konumuna_gore_yerlesir()
    {
        var kapsul = new Rectangle(1850, 960, 24, 24);
        Assert.Equal(new Rectangle(1374, 232, 500, 720), PanelYerlesim.Hesapla(null, [A], kapsul));
        // kapsül başka bir yere gidince panel de oraya gider (bayat değer kullanılmaz)
        var kapsul2 = new Rectangle(40, 500, 24, 24);
        var r2 = PanelYerlesim.Hesapla(null, [A], kapsul2);
        Assert.Equal(new Rectangle(8, 312, 500, 720), r2);
    }

    [Fact]
    public void Kapsul_ikinci_ekrandaysa_panel_o_ekranin_icinde_kalir()
    {
        var r = PanelYerlesim.Hesapla(null, [A, B], new Rectangle(3000, 500, 24, 24));
        Assert.Equal(new Rectangle(2524, 352, 500, 720), r);
        Assert.True(Icinde(r, B));
    }

    [Fact]
    public void Kapsul_konumu_bilinmiyorsa_birincil_ekranin_sag_altina_yerlesir()
    {
        var r = PanelYerlesim.Hesapla(null, [A, B], Rectangle.Empty);
        Assert.Equal(new Rectangle(1404, 272, 500, 720), r);
    }

    [Fact]
    public void Bayat_ve_ekran_disi_kapsul_konumu_paneli_disari_atmaz()
    {
        var r = PanelYerlesim.Hesapla(null, [A], new Rectangle(5000, 5000, 24, 24));
        Assert.True(Icinde(r, A), r.ToString());
    }

    [Fact]
    public void En_kucuk_boyutun_altindaki_saklı_boyut_yukselir()
    {
        var r = PanelYerlesim.Hesapla(new PanelSakli(100, 100, 200, 100), [A], new Rectangle(1850, 960, 24, 24));
        Assert.Equal(new Size(380, 480), r.Size);
        Assert.Equal(new Point(100, 100), r.Location);
    }

    [Fact]
    public void Calisma_alanindan_buyuk_saklı_boyut_alana_sigdirilir()
    {
        var r = PanelYerlesim.Hesapla(new PanelSakli(0, 0, 5000, 5000), [A], new Rectangle(1850, 960, 24, 24));
        Assert.Equal(new Size(1904, 1024), r.Size);
        Assert.True(Icinde(r, A));
    }

    [Fact]
    public void Olcek_iki_iken_dp_boyutu_piksele_cevrilir_ve_sigdirilir()
    {
        var sakli = PanelYerlesim.Hesapla(new PanelSakli(100, 100, 400, 490), [A], new Rectangle(1850, 960, 24, 24), 2.0);
        Assert.Equal(new Size(800, 980), sakli.Size);
        var varsayilan = PanelYerlesim.Hesapla(null, [A], new Rectangle(1850, 960, 24, 24), 2.0);
        Assert.Equal(new Size(1000, 1024), varsayilan.Size);   // 1440 yüksekliği alana (1040-16) sığdırılır
        Assert.True(Icinde(varsayilan, A));
    }

    [Fact]
    public void Kucuk_ekranda_varsayilan_boyut_sigdirilir()
    {
        var kucuk = new Rectangle(0, 0, 800, 500);
        var r = PanelYerlesim.Hesapla(null, [kucuk], new Rectangle(760, 460, 24, 24));
        Assert.Equal(new Size(500, 484), r.Size);
        Assert.True(Icinde(r, kucuk), r.ToString());
    }

    [Fact]
    public void Ekran_listesi_bos_olsa_da_gecerli_dikdortgen_doner()
    {
        var r = PanelYerlesim.Hesapla(new PanelSakli(10, 10, 500, 720), [], Rectangle.Empty);
        Assert.True(r.Width >= 380 && r.Height >= 480);
    }

    [Fact]
    public void Sonuc_her_girdide_bir_calisma_alaninin_icinde_kalir()
    {
        var ekranSetleri = new[] { new[] { A }, new[] { A, B }, new[] { A, L }, new[] { B }, new[] { L, A, B } };
        var kapsullar = new[] { Rectangle.Empty, new Rectangle(1850, 960, 24, 24), new Rectangle(-1000, 800, 24, 24), new Rectangle(4000, 4000, 24, 24), new Rectangle(0, 0, 24, 24) };
        var saklilar = new PanelSakli?[] { null, new(0, 0, 440, 680), new(-3000, -3000, 440, 680), new(1900, 1000, 500, 700), new(3800, 1000, 440, 680), new(100, 100, 100, 100), new(2000, 100, 9000, 9000) };
        foreach (var set in ekranSetleri)
            foreach (var k in kapsullar)
                foreach (var s in saklilar)
                    foreach (var olcek in new[] { 1.0, 1.5, 2.0 })
                    {
                        var r = PanelYerlesim.Hesapla(s, set, k, olcek);
                        // saklı yer iki ekranı birden kaplıyorsa tek alan içinde olması gerekmez; onun dışında hep içeride
                        var kapsananAlan = set.Sum(a => Math.Max(0, Rectangle.Intersect(a, r).Width) * (long)Math.Max(0, Rectangle.Intersect(a, r).Height));
                        Assert.True(kapsananAlan == (long)r.Width * r.Height, $"dışarıda: {r} set={set.Length} kapsül={k} saklı={s} ölçek={olcek}");
                    }
    }

    [Fact]
    public void En_buyuk_boyut_butun_ekranlarin_en_buyuk_calisma_alanidir()
    {
        Assert.Equal(new Size(1920, 1080), PanelYerlesim.EnBuyuk([A, B, L]));
        Assert.Equal(new Size(1920, 1040), PanelYerlesim.EnBuyuk([A]));
        Assert.True(PanelYerlesim.EnBuyuk([]).Width > 0);
    }

    // ------------------------------------------------------------------ saklı durum
    [Fact]
    public void Durum_yazilip_geri_okunur()
    {
        var d = new PanelDurum(new PanelSakli(-1500, 50, 440, 680), @"E:\projeler\yeni", 45);
        Assert.Equal(d, PanelDurum.Oku(d.Json()));
    }

    [Fact]
    public void Bozuk_ya_da_bos_durum_dosyasi_bos_durum_verir()
    {
        foreach (var j in new[] { null, "", "   ", "{bozuk", "[1,2]", "5", "{\"x\":\"a\"}" })
        {
            var d = PanelDurum.Oku(j);
            Assert.Null(d.Sakli); Assert.Null(d.SonKlasor); Assert.Null(d.BostaDk);
        }
    }

    [Fact]
    public void Saçma_degerler_yok_sayilir()
    {
        var d = PanelDurum.Oku("{\"x\":10,\"y\":20,\"w\":5,\"h\":99999,\"bostaDk\":-3,\"sonKlasor\":\"\"}");
        Assert.Null(d.Sakli);      // boyut dp olarak anlamsız (5 ve 99999)
        Assert.Null(d.BostaDk);
        Assert.Null(d.SonKlasor);
    }

    // ------------------------------------------------------------------ klasör seçici
    private static Func<string, bool> Var(params string[] yollar)
    {
        var set = new HashSet<string>(yollar, StringComparer.OrdinalIgnoreCase);
        return p => set.Contains(p);
    }

    private const string Belge = @"C:\Users\x\Documents";

    [Fact]
    public void Klasor_en_son_secilenin_ustunden_acilir()
    {
        var s = KlasorBaslangic.Sec(@"E:\projeler\a", @"D:\diger", @"D:\", Belge, Var(@"E:\projeler", @"D:\diger", @"D:\", Belge));
        Assert.Equal(@"E:\projeler", s);
    }

    [Fact]
    public void Sondaki_ters_bolu_son_secimde_de_ust_klasor_bulunur()
    {
        var s = KlasorBaslangic.Sec(@"E:\projeler\a\", null, null, Belge, Var(@"E:\projeler", Belge));
        Assert.Equal(@"E:\projeler", s);
    }

    [Fact]
    public void Son_secimin_ustu_yoksa_profilin_onerdigi_ust_klasore_gecilir()
    {
        var s = KlasorBaslangic.Sec(@"E:\silinmis\a", @"D:\projelerim", @"D:\", Belge, Var(@"D:\projelerim", @"D:\", Belge));
        Assert.Equal(@"D:\projelerim", s);
    }

    [Fact]
    public void Profil_ust_klasoru_yoksa_proje_surucusunun_kokune_gecilir()
    {
        Assert.Equal(@"E:\", KlasorBaslangic.Sec(null, @"E:\yok", @"E:\", Belge, Var(@"E:\", Belge)));
        // sürücü "E:" ya da "e" biçiminde gelse de kök yoluna çevrilir
        Assert.Equal(@"E:\", KlasorBaslangic.Sec(null, null, "E:", Belge, Var(@"E:\", Belge)));
        Assert.Equal(@"E:\", KlasorBaslangic.Sec(null, null, "e", Belge, Var(@"E:\", Belge)));
    }

    [Fact]
    public void Hicbiri_yoksa_en_son_Belgeler_gelir()
    {
        Assert.Equal(Belge, KlasorBaslangic.Sec(@"E:\x\a", @"E:\yok", @"Z:\", Belge, Var()));
    }

    [Fact]
    public void Profil_henuz_yoksa_bos_degerler_sirayi_bozmaz()
    {
        Assert.Equal(@"E:\projeler", KlasorBaslangic.Sec(@"E:\projeler\a", null, null, Belge, Var(@"E:\projeler")));
        Assert.Equal(Belge, KlasorBaslangic.Sec(null, null, null, Belge, Var()));
        Assert.Equal(Belge, KlasorBaslangic.Sec("  ", "", " ", Belge, Var()));
    }

    [Fact]
    public void Surucu_koku_secildiyse_kendisi_acilir()
    {
        // E:\ seçildi: üst klasörü yok, kendisi başlangıç olur
        Assert.Equal(@"E:\", KlasorBaslangic.Sec(@"E:\", null, null, Belge, Var(@"E:\", Belge)));
    }

    [Fact]
    public void Yol_denetimi_hata_verirse_sonraki_adaya_gecilir()
    {
        var s = KlasorBaslangic.Sec(@"E:\projeler\a", @"D:\diger", null, Belge, p => p == @"E:\projeler" ? throw new IOException() : p == @"D:\diger");
        Assert.Equal(@"D:\diger", s);
    }

    [Fact]
    public void Goreli_ya_da_anlamsiz_yol_aday_olmaz()
    {
        Assert.Equal(Belge, KlasorBaslangic.Sec("proje", "altklasor", "??", Belge, _ => true));
    }

    // ------------------------------------------------------------------ boşta bırakma süresi
    [Fact]
    public void Bosta_birakma_varsayilani_uc_dakikadir()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), PanelBosta.Varsayilan);
        Assert.Equal(TimeSpan.FromMinutes(3), PanelBosta.Coz(null, null));
    }

    [Fact]
    public void Bosta_birakma_ortam_degiskeni_dosyadan_once_gelir()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), PanelBosta.Coz("10", 45));
        Assert.Equal(TimeSpan.FromMinutes(45), PanelBosta.Coz(null, 45));
        Assert.Equal(TimeSpan.FromMinutes(45), PanelBosta.Coz("abc", 45));
        Assert.Equal(TimeSpan.FromMinutes(3), PanelBosta.Coz("abc", 0));
    }

    [Fact]
    public void Bosta_birakma_sinirlari_disindaki_deger_kabul_edilmez()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), PanelBosta.Coz("0", null));
        Assert.Equal(TimeSpan.FromMinutes(3), PanelBosta.Coz("-5", null));
        Assert.Equal(TimeSpan.FromMinutes(3), PanelBosta.Coz("99999", null));
        Assert.Equal(TimeSpan.FromMinutes(1440), PanelBosta.Coz("1440", null));
        Assert.Equal(TimeSpan.FromMinutes(1), PanelBosta.Coz(" 1 ", null));
    }
}

/// <summary>Sunucunun /api/profile cevabından klasör seçicinin ihtiyacı olan kısım ve kapsülün konum bildirimi (k4).</summary>
public class ProfilVeKonumTests
{
    [Fact]
    public void Profil_cevabindan_surucu_ve_ust_klasor_okunur()
    {
        using var d = System.Text.Json.JsonDocument.Parse("""{"projects":[],"defaults":{"projeSurucusu":"E:\\","yeniProjeUstKlasoru":"E:\\projeler","sources":[],"explanation":"x","weights":[],"kind":"cikarim"},"counts":{}}""");
        Assert.Equal(new ProfilVarsayilan(@"E:\", @"E:\projeler"), Json.ParseProfileDefaults(d.RootElement));
    }

    [Theory]
    [InlineData("{\"defaults\":{\"projeSurucusu\":null,\"yeniProjeUstKlasoru\":null,\"explanation\":\"Yeterli proje gözlemi yok.\"}}")]   // profil henüz yok
    [InlineData("{\"defaults\":{}}")]
    [InlineData("{\"projects\":[]}")]
    [InlineData("[1]")]
    [InlineData("{\"defaults\":5}")]
    public void Profil_yok_ya_da_bos_ise_null_doner(string json)
    {
        using var d = System.Text.Json.JsonDocument.Parse(json);
        Assert.Null(Json.ParseProfileDefaults(d.RootElement));
    }

    [Fact]
    public void Yalniz_surucu_varsa_ust_klasor_null_kalir()
    {
        using var d = System.Text.Json.JsonDocument.Parse("{\"defaults\":{\"projeSurucusu\":\"D:\\\\\"}}");
        Assert.Equal(new ProfilVarsayilan(@"D:\", null), Json.ParseProfileDefaults(d.RootElement));
    }

    [Fact]
    public void Konum_komutu_kapsulun_guncel_dikdortgenini_tasir()
    {
        var k = new KapsulKomutu(KomutTuru.Konum, null, null, 0, new KabukDik(40.2, 500, 64, 524));
        var geri = KabukProtokol.KomutOku(KabukProtokol.Yaz(k))!.Value;
        Assert.Equal(KomutTuru.Konum, geri.Tur);
        Assert.Equal(new KabukDik(40, 500, 64, 524), geri.Dik);
        Assert.Contains("\"k\":\"konum\"", KabukProtokol.Yaz(k));
    }
}
