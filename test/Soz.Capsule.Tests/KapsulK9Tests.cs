using System.Text.Json;
using System.Text.RegularExpressions;
using Soz.Shell;
using Xunit;

namespace Soz.Capsule.Tests;

/// <summary>
/// k9: tepsi ve küçük pencere menüleri. Her madde ne yaptığını söyler, ölü madde yok, hata sessiz kalmaz, balon aynı olay için bir kez çıkar.
/// </summary>
public class KapsulK9Tests
{
    private static string Kok()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Soz.Shell"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("urun klasörü bulunamadı");
    }
    private static string Oku(params string[] yol) => File.ReadAllText(Path.Combine(new[] { Kok() }.Concat(yol).ToArray()));

    // ------------------------------------------------------------ 1. güncelleme balonu
    [Fact]
    public void Ayni_surum_icin_balon_bir_kez_cikar()
    {
        var b = new GuncellemeBalonu();
        Assert.True(b.Gostermeli("0.4.0", elleDenetim: false, duman: false));
        Assert.False(b.Gostermeli("0.4.0", false, false));       // sonraki her denetimde çıkmaz
        Assert.False(b.Gostermeli("0.4.0", false, false));
        Assert.True(b.Gostermeli("0.5.0", false, false));        // yeni sürüm yeni haber
        Assert.False(b.Gostermeli("0.5.0", false, false));
    }

    [Fact]
    public void Elle_denetimde_ve_dumanda_ve_surum_yokken_balon_cikmaz()
    {
        Assert.False(new GuncellemeBalonu().Gostermeli("0.4.0", elleDenetim: true, duman: false));
        Assert.False(new GuncellemeBalonu().Gostermeli("0.4.0", elleDenetim: false, duman: true));
        Assert.False(new GuncellemeBalonu().Gostermeli(null, false, false));
        Assert.False(new GuncellemeBalonu().Gostermeli("  ", false, false));
        // kişi elle bakıp sonucu gördüyse aynı sürüm için sonradan balon da çıkmaz
        var b = new GuncellemeBalonu();
        Assert.False(b.Gostermeli("0.4.0", true, false));
        Assert.False(b.Gostermeli("0.4.0", false, false));
    }

    [Fact]
    public void Kaynakta_balon_cagrisi_sartin_susulu_parantezinin_icinde()
    {
        var kod = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.DoesNotContain("ilkKez && !manual", kod);
        // şart satırından hemen sonra { gelir (eskiden eksikti, balon her denetimde çıkıyordu)
        Assert.Matches(new Regex(@"if \(_guncellemeBalonu\.Gostermeli\([^\n]*\)\)\s*\{\s*_bildirimBalonu = false;\s*Tray\.ShowBalloonTip\("), kod);
    }

    // ------------------------------------------------------------ 2. açılışta başlatma kaydı sessiz kalmaz
    [Fact]
    public void Acilis_kaydi_yazilamazsa_basarisiz_doner_ve_isaret_eski_halinde_kalir()
    {
        var s = AcilisKaydi.Degistir(false, () => false, () => { }, () => false);
        Assert.False(s.Basarili); Assert.False(s.YeniDeger);
    }

    [Fact]
    public void Acilis_kaydi_silinemezse_basarisiz_doner()
    {
        var s = AcilisKaydi.Degistir(true, () => true, () => { }, () => true);   // kapattı ama kayıt hâlâ duruyor
        Assert.False(s.Basarili); Assert.True(s.YeniDeger);
    }

    [Fact]
    public void Acilis_kaydi_basarili_acma_ve_kapama()
    {
        var acik = false;
        var a = AcilisKaydi.Degistir(acik, () => { acik = true; return true; }, () => acik = false, () => acik);
        Assert.True(a.Basarili); Assert.True(a.YeniDeger);
        var k = AcilisKaydi.Degistir(acik, () => { acik = true; return true; }, () => acik = false, () => acik);
        Assert.True(k.Basarili); Assert.False(k.YeniDeger);
    }

    [Fact]
    public void Kaynakta_acilis_kaydi_ve_veri_klasoru_hatalari_kullaniciya_soylenir()
    {
        var kod = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.Contains("Soyle(\"msg.autostart.fail\")", kod);
        Assert.Contains("Soyle(\"msg.datafolder.fail\")", kod);
        Assert.NotEqual("msg.autostart.fail", Strings.Get("msg.autostart.fail"));
        Assert.NotEqual("msg.datafolder.fail", Strings.Get("msg.datafolder.fail"));
    }

    // ------------------------------------------------------------ 3. ayarlar yalnız kabukla
    [Fact]
    public void Kabuk_yokken_ayarlar_menude_gorunmez_kabukla_gorunur()
    {
        Assert.DoesNotContain("ayar", KabukProtokol.Pano(false, "cam").Select(p => p.Kimlik));
        Assert.Contains("ayar", KabukProtokol.Pano(true, "cam").Select(p => p.Kimlik));
    }

    // ------------------------------------------------------------ 4. kapatma ve gizleme maddeleri ne yaptığını söyler
    [Fact]
    public void Cikis_maddesi_ne_kapattigini_soyler()
    {
        Assert.Equal("Söz'ü kapat", KabukProtokol.Pano(true, "cam").Single(p => p.Kimlik == "cikis").Metin);
        Assert.Equal("Küçük pencereyi kapat", KabukProtokol.Pano(false, "cam").Single(p => p.Kimlik == "cikis").Metin);
        Assert.Equal("Söz'ü kapat", Strings.Get("menu.exit"));
    }

    [Fact]
    public void Kucuk_pencereyi_gizlemek_ayri_bir_maddedir_ve_geri_gelecegini_soyler()
    {
        foreach (var kabuk in new[] { true, false })
        {
            var gizle = KabukProtokol.Pano(kabuk, "cam").Single(p => p.Kimlik == "gizle").Metin;
            Assert.StartsWith("Küçük pencereyi gizle", gizle);
            Assert.Contains("1 saat", gizle);
            Assert.NotEqual(KabukProtokol.Pano(kabuk, "cam").Single(p => p.Kimlik == "cikis").Metin, gizle);
        }
    }

    // ------------------------------------------------------------ 5. güncelleme satırı (sh1: varsayılan besleme var, satır her zaman görünür)
    [Fact]
    public void Guncelleme_satiri_her_zaman_gorunur()
    {
        var kod = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.Contains("_updateItem.Visible = true;", kod);
        Assert.DoesNotContain("_updateItem.Visible = _guncelleme != null;", kod);
    }

    // ------------------------------------------------------------ 6. bütün menü, balon ve kısa yazı metinleri
    private static readonly string[] Teknik = { "Paket", "paket", "içe al", "İçe al", "Dikte", "Uyut", "Çıkış", "tepsi", "Tepsi" };

    [Fact]
    public void Kapsul_menusunde_teknik_soz_uzun_tire_ve_noktali_virgul_yok()
    {
        foreach (var kabuk in new[] { true, false })
            foreach (var p in KabukProtokol.Pano(kabuk, "cam").Where(x => !x.Ayirici))
            {
                Assert.DoesNotContain('—', p.Metin); Assert.DoesNotContain('–', p.Metin); Assert.DoesNotContain(';', p.Metin);
                foreach (var t in Teknik) Assert.DoesNotContain(t, p.Metin);
                Assert.False(string.IsNullOrWhiteSpace(p.Metin));
            }
        Assert.Equal("Sesle yaz", KabukProtokol.Pano(true, "cam").Single(p => p.Kimlik == "dikte").Metin);
    }

    [Fact]
    public void Temalar_gorunum_basligi_altindadir_ve_baslik_tiklanmaz()
    {
        var pano = KabukProtokol.Pano(true, "cam").ToList();
        var b = pano.FindIndex(p => p.Kimlik == "gorunum");
        Assert.True(b > 0);
        Assert.True(pano[b].Baslik);
        Assert.Equal("tema:cam", pano[b + 1].Kimlik);
        Assert.DoesNotContain(pano.Where(p => p.Baslik), p => p.Kimlik.StartsWith("tema:"));
    }

    [Fact]
    public void Tepsi_menusu_ve_kisa_yazilar_sade_turkce_ve_hepsi_tabloda()
    {
        var kodlar = Directory.GetFiles(Path.Combine(Kok(), "src", "Soz.Shell"), "*.cs").Where(f => !Path.GetFileName(f).StartsWith("SelfTest")).Select(File.ReadAllText).ToList();
        var anahtarlar = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kod in kodlar)
            foreach (Match m in Regex.Matches(kod, "\"((?:menu|flash|msg|balloon|dlg|tip|tray)\\.[a-z.]+)\""))
                anahtarlar.Add(m.Groups[1].Value);
        Assert.NotEmpty(anahtarlar);
        foreach (var a in anahtarlar)
        {
            var tr = Strings.Get(a);
            Assert.True(tr != a, "metin tablosunda yok: " + a);   // eksik anahtar ekranda ham adıyla görünürdü ("menu.notify" olayı)
            Assert.DoesNotContain('—', tr); Assert.DoesNotContain('–', tr); Assert.DoesNotContain(';', tr);
            foreach (var t in Teknik) Assert.DoesNotContain(t, tr);
        }
        Assert.Contains("menu.notify", anahtarlar);
    }

    [Fact]
    public void Sessiz_kalan_islerin_sonuc_yazisi_vardir()
    {
        foreach (var a in new[] { "flash.copied", "flash.empty", "flash.copyfail", "flash.pasted", "flash.pastefail", "flash.imported", "flash.importfail", "flash.dictation", "flash.dictation.ok", "flash.update.none", "flash.update.unreachable", "flash.update.available" })
            Assert.NotEqual(a, Strings.Get(a));
        var kod = Oku("src", "Soz.Shell", "ShellContext.cs");
        Assert.Contains("_ => \"flash.copyfail\"", kod);       // kopyalama hatası "içe alma olmadı" demez
        Assert.Contains("_ => \"flash.pastefail\"", kod);
        Assert.Contains("else BalonGoster(metin, ToolTipIcon.Info, 3000);", kod);   // küçük pencere yoksa tepsiden yapılan işler balonla bildirilir
    }

    [Fact]
    public void Menu_komutu_kabuga_gitmezse_kapsul_kullaniciya_soyler()
    {
        var kod = Oku("src", "Soz.Capsule", "Pencere", "Kapsul.cs");
        Assert.Contains("if (!_kopru.Komut(KabukProtokol.PanoKomutu(id))) _m.Bildir(KabukaUlasilamadi, now);", kod);
        Assert.Contains("if (!_kopru.AyarAc(_ayarDosya)) _m.Bildir(KabukaUlasilamadi, now);", kod);
    }

    // ------------------------------------------------------------ 7. çalışan işin süresi
    private static DateTimeOffset Simdi => new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Calisan_isin_suresi_sunucu_baslangic_verirse_yazilir_vermezse_uydurulmaz()
    {
        const string json = """
        { "rev": 3, "sentence": "x", "items": [],
          "running": [ { "jobId": 4, "title": "Rapor", "startedAt": "2026-11-23T11:55:00Z" }, { "jobId": 5, "title": "Test" } ] }
        """;
        using var d = JsonDocument.Parse(json);
        var l = SiraBendeCevabi.Ayristir(d.RootElement)!.Isler.ToList();
        Assert.Equal("çalışıyor · 5 dk", l[0].SagYazi(Simdi));
        Assert.Equal("çalışıyor", l[1].SagYazi(Simdi));   // başlangıç yok: yalnız "çalışıyor", süre uydurulmaz
        var satir = OnBakisIcerigi.Kur(new List<Is>(), l, new List<Is>(), null, Simdi).Single(s => s.Kimlik == "j:4");
        Assert.Equal("çalışıyor · 5 dk", satir.Sag);
    }
}
