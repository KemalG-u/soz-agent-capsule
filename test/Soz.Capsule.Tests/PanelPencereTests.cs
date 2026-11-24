using Soz.Shell;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>
/// m1: ana pencere (panel) normal bir Windows penceresi gibi küçültülür ve büyütülür, görev çubuğunda görünür.
/// Küçültülmüş pencere WinForms'ta hâlâ "görünür" sayılır; tepsi tıklaması, kısayol ve ikinci açılış onu gizlemez, geri getirir.
/// Büyütülmüş durum saklanır, küçültülmüş durum saklanmaz. Karar saf işlevlerdedir (pencere gerekmez).
/// </summary>
public class PanelPencereTests
{
    private static string Kok()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Soz.Shell"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("urun klasörü bulunamadı");
    }
    private static string Oku(params string[] yol) => File.ReadAllText(Path.Combine(new[] { Kok() }.Concat(yol).ToArray()));

    // ------------------------------------------------------------ tepsi / kapsül tıklaması
    // Not: kural türleri internal olduğu için kuramlarda ad (string) kullanılır.
    private static PencereDurumu D(string ad) => Enum.Parse<PencereDurumu>(ad);
    private static PanelOdak.TikKarari T(string ad) => Enum.Parse<PanelOdak.TikKarari>(ad);

    [Theory]
    // gizliyken (durum ne olursa olsun) göster
    [InlineData(false, "Normal", "Gizle", "Goster")]
    [InlineData(false, "Normal", "OneGetir", "Goster")]
    [InlineData(false, "Buyutulmus", "Gizle", "Goster")]
    // KÜÇÜLTÜLMÜŞ pencere "görünür" sayılır ama gizlenmez: her zaman geri getirilir
    [InlineData(true, "Kucultulmus", "Gizle", "GeriGetir")]
    [InlineData(true, "Kucultulmus", "OneGetir", "GeriGetir")]
    // normal ve büyütülmüş pencerede eski aç-kapa mantığı aynen: etkinse gizle, arkadaysa öne getir
    [InlineData(true, "Normal", "Gizle", "Gizle")]
    [InlineData(true, "Normal", "OneGetir", "OneGetir")]
    [InlineData(true, "Buyutulmus", "Gizle", "Gizle")]
    [InlineData(true, "Buyutulmus", "OneGetir", "OneGetir")]
    public void Tepsi_tiklamasi_karari(bool gorunur, string durum, string tik, string beklenen)
        => Assert.Equal(beklenen, PanelPencereKurali.TepsiTiklandi(gorunur, D(durum), T(tik)).ToString());

    [Fact]
    public void Kucultulmus_panel_ayni_tikla_iki_kez_gizlenmez()
    {
        // etkin kaybı çok yeni olsa bile (tıklamanın kendisi odağı almıştır) küçültülmüş pencere gizlenmez
        var tik = PanelOdak.AcKapa(etkin: false, sonEtkinKaybindanBeriMs: 10);
        Assert.Equal(PanelOdak.TikKarari.Gizle, tik);
        Assert.Equal("GeriGetir", PanelPencereKurali.TepsiTiklandi(true, PencereDurumu.Kucultulmus, tik).ToString());
    }

    // ------------------------------------------------------------ kısayol, menü, ikinci açılış: "aç" asla gizlemez
    [Theory]
    [InlineData(false, "Normal", "Goster")]
    [InlineData(false, "Kucultulmus", "Goster")]   // küçültülüp sonra gizlenmiş: yine gösterilir (açılış normal ya da büyütülmüş olur)
    [InlineData(true, "Kucultulmus", "GeriGetir")]
    [InlineData(true, "Normal", "OneGetir")]
    [InlineData(true, "Buyutulmus", "OneGetir")]
    public void Ac_istegi_hic_gizlemez(bool gorunur, string durum, string beklenen)
    {
        var e = PanelPencereKurali.AcIstendi(gorunur, D(durum)).ToString();
        Assert.Equal(beklenen, e);
        Assert.NotEqual("Gizle", e);
    }

    // ------------------------------------------------------------ hangi duruma dönülür
    [Fact]
    public void Geri_getirme_ve_acilis_durumu_kucultulmus_olmaz()
    {
        Assert.Equal("Normal", PanelPencereKurali.HedefDurum(false).ToString());
        Assert.Equal("Buyutulmus", PanelPencereKurali.HedefDurum(true).ToString());
    }

    [Theory]
    [InlineData("Buyutulmus", false, true)]    // kişi büyüttü
    [InlineData("Normal", true, false)]        // kişi eski boyuta döndürdü
    [InlineData("Kucultulmus", true, true)]    // büyütülmüşken küçültüldü: büyütülmüş akılda kalır
    [InlineData("Kucultulmus", false, false)]  // normalken küçültüldü: normal akılda kalır
    public void Kucultme_son_buyutulmus_bilgisini_degistirmez(string simdi, bool onceki, bool beklenen)
        => Assert.Equal(beklenen, PanelPencereKurali.BuyutulmusMu(D(simdi), onceki));

    [Fact]
    public void Buyutulup_kucultulup_geri_getirilince_buyutulmus_doner()
    {
        var son = PanelPencereKurali.BuyutulmusMu(PencereDurumu.Buyutulmus, false);
        son = PanelPencereKurali.BuyutulmusMu(PencereDurumu.Kucultulmus, son);
        Assert.Equal("Buyutulmus", PanelPencereKurali.HedefDurum(son).ToString());
    }

    // ------------------------------------------------------------ yer ve boyut yalnız normal durumda saklanır
    [Theory]
    [InlineData("Normal", true)]
    [InlineData("Buyutulmus", false)]
    [InlineData("Kucultulmus", false)]
    public void Yer_yalniz_normal_durumda_saklanir(string durum, bool beklenen)
        => Assert.Equal(beklenen, PanelPencereKurali.YerSaklanabilir(D(durum)));

    // ------------------------------------------------------------ durum dosyası
    [Fact]
    public void Buyutulmus_durum_yazilir_okunur_kucultulmus_hic_yazilmaz()
    {
        var d = new PanelDurum(new PanelSakli(100, 100, 440, 680), null, null, null, true);
        Assert.Contains("\"buyutulmus\":true", d.Json());
        Assert.Equal(d, PanelDurum.Oku(d.Json()));
        // büyütülmemiş durum dosyaya hiç yazılmaz, eski dosyalar aynen okunur
        var n = d with { Buyutulmus = null };
        Assert.DoesNotContain("buyutulmus", n.Json());
        Assert.Null(PanelDurum.Oku("{\"x\":1,\"y\":2,\"w\":400,\"h\":500}").Buyutulmus);
        // "küçültülmüş" diye bir alan yoktur
        Assert.DoesNotContain("kucult", d.Json(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("minimi", d.Json(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"buyutulmus\":false}")]
    [InlineData("{\"buyutulmus\":\"evet\"}")]
    [InlineData("{\"buyutulmus\":1}")]
    [InlineData("{}")]
    public void Bozuk_ya_da_kapali_buyutulmus_alani_yok_sayilir(string json)
        => Assert.Null(PanelDurum.Oku(json).Buyutulmus);

    [Fact]
    public void Buyutulmus_durum_diger_alanlari_ve_yer_siklasmasini_bozmaz()
    {
        var d = PanelDurum.Oku("{\"x\":10,\"y\":20,\"w\":420,\"h\":600,\"sonKlasor\":\"E:\\\\a\",\"bostaDk\":5,\"buyutulmus\":true}");
        Assert.True(d.Buyutulmus);
        Assert.Equal(new PanelSakli(10, 20, 420, 600), d.Sakli);
        Assert.Equal(5, d.BostaDk);
        // büyütülmüş açılışta bile yer hesabı normal (saklı) yerden yapılır: büyütme kapsülün yerini bozmaz
        var r = PanelYerlesim.Hesapla(d.Sakli, [new System.Drawing.Rectangle(0, 0, 1920, 1040)], new System.Drawing.Rectangle(1850, 960, 24, 24));
        Assert.Equal(new System.Drawing.Rectangle(10, 20, 420, 600), r);
    }

    // ------------------------------------------------------------ kaynak sözleşmesi (pencere açmadan)
    [Fact]
    public void Panel_formu_kucultulur_buyutulur_ve_gorev_cubugunda_gorunur()
    {
        var f = Oku("src", "Soz.Shell", "PanelForm.cs");
        Assert.Contains("ShowInTaskbar = true;", f);
        Assert.Contains("MinimizeBox = true;", f);
        Assert.Contains("MaximizeBox = true;", f);
        Assert.DoesNotContain("ShowInTaskbar = false", f);
        Assert.DoesNotContain("MinimizeBox = false", f);
        Assert.DoesNotContain("MaximizeBox = false", f);
        Assert.Contains("Text = \"Söz\";", f);   // görev çubuğundaki ad
        Assert.Contains("Icon = TrayIconFactory.Create(32, palette)", f);   // görev çubuğundaki simge (tepsiyle aynı)
    }

    [Fact]
    public void X_dugmesi_hala_gizler_programi_kapatmaz()
    {
        var f = Oku("src", "Soz.Shell", "PanelForm.cs");
        Assert.Contains("if (ShouldCancelClose(e.CloseReason, AllowClose)) { e.Cancel = true; Hide(); return; }", f);
        Assert.Contains("=> !allowClose && reason is not (CloseReason.ApplicationExitCall or CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing);", f);
    }

    [Fact]
    public void Panel_formu_kararlari_PanelPencereKurali_uzerinden_verir_ve_kucultulmusu_gizlemez()
    {
        var f = Oku("src", "Soz.Shell", "PanelForm.cs");
        Assert.Contains("PanelPencereKurali.TepsiTiklandi(", f);
        Assert.Contains("PanelPencereKurali.AcIstendi(", f);
        Assert.Contains("PanelPencereKurali.YerSaklanabilir(", f);
        Assert.Contains("PanelPencereKurali.BuyutulmusMu(", f);
        Assert.Contains("RestoreBounds", f);
        // IsOpen küçültülmüşü açık saymaz (küçültülmüşken bildirim balonu bastırılmasın)
        Assert.Contains("public bool IsOpen => Visible && WindowState != FormWindowState.Minimized;", f);
    }

    [Fact]
    public void Kabuk_gorunur_paneli_Activate_ile_degil_GorunurkenGetir_ile_one_alir()
    {
        var s = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.DoesNotContain("else p.Activate();", s);
        Assert.DoesNotContain("else if (odakAl) p.Activate();", s);
        Assert.Contains("p.GorunurkenGetir(", s);
        var f = Oku("src", "Soz.Shell", "PanelForm.cs");
        Assert.DoesNotContain("else if (odakAl) { Activate(); }", f);
    }
}
