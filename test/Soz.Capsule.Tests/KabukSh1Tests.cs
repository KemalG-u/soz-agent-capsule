using System.Text.RegularExpressions;
using Soz.Shell;
using Xunit;

namespace Soz.Core.Data
{
    /// <summary>
    /// Sınama için aynı tam adlı yerel tür. Bu proje Core'a bağlı değil; kabuk istisnayı tam adıyla tanır.
    /// Gerçek türün adının değişmediğini Core sınaması (<c>Sh1SemaAdiTests</c>) korur.
    /// </summary>
    public sealed class SozSchemaTooNewException(string m) : InvalidOperationException(m);
}

namespace Soz.Capsule.Tests
{
    /// <summary>
    /// sh1: kabuğun güncelleme zinciri (varsayılan besleme, kapatma anahtarı, soru metni, indirme kilidi, menü satırı), yeni şemalı veri cümlesi,
    /// panelin sağ tık süzgeci, geliştirici araçları, ekran süreci çöküşü, Esc kuralı ve "Küçük pencereyi göster".
    /// </summary>
    public class KabukSh1Tests
    {
        private static string Kok()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Soz.Shell"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("urun klasörü bulunamadı");
        }
        private static string Oku(params string[] yol) => File.ReadAllText(Path.Combine(new[] { Kok() }.Concat(yol).ToArray()));

        private static string GeciciKlasor()
        {
            var d = Path.Combine(Path.GetTempPath(), "soz-sh1-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(d);
            return d;
        }

        // ------------------------------------------------------------ 1. adres önceliği
        [Fact]
        public void Adres_sirasi_ortam_degiskeni_sonra_tercih_sonra_varsayilan()
        {
            Assert.Equal("https://kokpit.oluk.org/guncelleme/soz", GuncellemeAdresi.Varsayilan);
            Assert.Equal((GuncellemeAdresi.Varsayilan, false), GuncellemeAdresi.Coz(null, new KabukTercihleri()));
            Assert.Equal((GuncellemeAdresi.Varsayilan, false), GuncellemeAdresi.Coz("   ", new KabukTercihleri()));
            Assert.Equal(("https://tercih.test/f", false), GuncellemeAdresi.Coz(null, new KabukTercihleri { GuncellemeAdresi = "https://tercih.test/f" }));
            Assert.Equal(("https://env.test/f", false), GuncellemeAdresi.Coz(" https://env.test/f ", new KabukTercihleri { GuncellemeAdresi = "https://tercih.test/f" }));
        }

        [Fact]
        public void Denetim_araligi_alti_saat_ilk_denetim_kisa_sure_sonra()
        {
            Assert.Equal(TimeSpan.FromHours(6), GuncellemeAdresi.Aralik);
            Assert.InRange(GuncellemeAdresi.IlkBekleme.TotalSeconds, 5, 120);
            Assert.True(GuncellemeAdresi.Aralik.TotalMilliseconds < int.MaxValue);   // WinForms zamanlayıcısı int milisaniye alır
        }

        // ------------------------------------------------------------ 2. kapatma anahtarı
        [Fact]
        public void Kapatma_anahtari_false_ya_da_bos_adres_denetimi_kapatir()
        {
            Assert.Equal(((string?)null, true), GuncellemeAdresi.Coz(null, new KabukTercihleri { GuncellemeDenetimi = false }));
            Assert.Equal(((string?)null, true), GuncellemeAdresi.Coz(null, new KabukTercihleri { GuncellemeAdresiBos = true }));
            Assert.Equal(((string?)null, true), GuncellemeAdresi.Coz(null, new KabukTercihleri { GuncellemeDenetimi = false, GuncellemeAdresi = "https://tercih.test/f" }));
            Assert.Equal((GuncellemeAdresi.Varsayilan, false), GuncellemeAdresi.Coz(null, new KabukTercihleri { GuncellemeDenetimi = true }));
            // geliştirici ve sınama yolu: ortam değişkeni kapatmanın da üstündedir
            Assert.Equal(("https://env.test/f", false), GuncellemeAdresi.Coz("https://env.test/f", new KabukTercihleri { GuncellemeDenetimi = false }));
        }

        [Fact]
        public void Kapatma_anahtari_dosyadan_okunur_ve_baska_tercih_yazilinca_silinmez()
        {
            var d = GeciciKlasor();
            try
            {
                File.WriteAllText(Path.Combine(d, KabukTercihleri.DosyaAdi), "{ \"guncellemeAdresi\": \"\", \"baskaAlan\": 3 }");
                var t = KabukTercihleri.Oku(d);
                Assert.True(t.GuncellemeAdresiBos);
                Assert.Null(t.GuncellemeAdresi);
                Assert.Equal(((string?)null, true), GuncellemeAdresi.Coz(null, t));
                t.Bildirimler = false;   // kişi tepsiden bildirimi kapattı: dosya yeniden yazılır
                Assert.True(t.Yaz(d));
                var ham = File.ReadAllText(Path.Combine(d, KabukTercihleri.DosyaAdi));
                Assert.Contains("\"guncellemeAdresi\": \"\"", ham);
                Assert.Contains("baskaAlan", ham);
                Assert.True(KabukTercihleri.Oku(d).GuncellemeAdresiBos);

                File.WriteAllText(Path.Combine(d, KabukTercihleri.DosyaAdi), "{ \"guncellemeDenetimi\": false }");
                var t2 = KabukTercihleri.Oku(d);
                Assert.False(t2.GuncellemeDenetimi);
                t2.KaldirirkenVeriyiSil = true;
                Assert.True(t2.Yaz(d));
                Assert.False(KabukTercihleri.Oku(d).GuncellemeDenetimi);
                Assert.Equal(((string?)null, true), GuncellemeAdresi.Coz(null, KabukTercihleri.Oku(d)));

                File.WriteAllText(Path.Combine(d, KabukTercihleri.DosyaAdi), "{ \"acilistaBasla\": true }");
                Assert.Equal((GuncellemeAdresi.Varsayilan, false), GuncellemeAdresi.Coz(null, KabukTercihleri.Oku(d)));   // alan yoksa açık
            }
            finally { try { Directory.Delete(d, true); } catch { } }
        }

        // ------------------------------------------------------------ 3. güncelleme sorusu
        [Fact]
        public void Guncelleme_sorusu_calisan_is_varsa_sayisini_ve_durdurulacagini_soyler()
        {
            Strings.Language = "tr";
            var s = GuncellemeSorusu.Metin("0.3.5", 2);
            Assert.StartsWith("Yeni sürüm var: 0.3.5. Şimdi kurulsun mu?", s);
            Assert.Contains("Çalışan 2 iş durdurulacak. Güncelleme bitince Söz yeniden açılır.", s);
            Assert.True(GuncellemeSorusu.HayirVarsayilan(2));
        }

        [Fact]
        public void Guncelleme_sorusu_is_yoksa_durdurma_cumlesi_cikmaz()
        {
            Strings.Language = "tr";
            var s = GuncellemeSorusu.Metin("0.3.5", 0);
            Assert.StartsWith("Yeni sürüm var: 0.3.5. Şimdi kurulsun mu?", s);
            Assert.DoesNotContain("durdurulacak", s);
            Assert.DoesNotContain("Çalışan", s);
            Assert.Contains("Güncelleme bitince Söz yeniden açılır.", s);
            Assert.False(GuncellemeSorusu.HayirVarsayilan(0));
        }

        [Fact]
        public void Guncelleme_sorusu_is_sayisi_ogrenilemezse_durust_soyler()
        {
            Strings.Language = "tr";
            var s = GuncellemeSorusu.Metin("0.3.5", null);
            Assert.Contains("öğrenemedim", s);
            Assert.Contains("durdurulacak", s);
            Assert.True(GuncellemeSorusu.HayirVarsayilan(null));
        }

        [Fact]
        public void Guncelleme_metinlerinde_islerin_kesilmedigi_sozu_yok_ve_sozcuk_kurali_tutar()
        {
            foreach (var dil in new[] { "tr", "en" })
            {
                Strings.Language = dil;
                foreach (var a in new[] { "dlg.update.text", "dlg.update.jobs", "dlg.update.nojobs", "dlg.update.unknown", "flash.update.failed", "flash.update.none", "flash.update.unreachable",
                                          "flash.update.busy", "flash.update.notinstalled", "menu.update.off", "menu.update.downloading", "menu.capsule.show", "msg.schema.new",
                                          "panel.schema.title", "panel.schema.button", "panel.crash.title", "panel.crash.text" })
                {
                    var t = Strings.Get(a);
                    Assert.NotEqual(a, t);
                    Assert.DoesNotContain("kesilmez", t);
                    Assert.DoesNotContain("not interrupted", t, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain('—', t); Assert.DoesNotContain('–', t); Assert.DoesNotContain(';', t);
                    Assert.DoesNotContain("ajan", t, StringComparison.OrdinalIgnoreCase);
                }
            }
            Strings.Language = "tr";
            Assert.Equal("Söz güncel (0.3.4).", Strings.Get("flash.update.none", "0.3.4"));
            Assert.Equal("Güncelleme indirilemedi. İnternet bağlantını kontrol et, sonra yeniden dene.", Strings.Get("flash.update.failed"));
            Assert.Equal("Küçük pencereyi göster", Strings.Get("menu.capsule.show"));
        }

        // ------------------------------------------------------------ 4. indirme kilidi ve menü satırı
        [Fact]
        public void Indirme_kilidi_ikinci_tiklamada_yeni_indirme_baslatmaz()
        {
            var k = new GuncellemeKilidi();
            Assert.False(k.Suruyor);
            Assert.True(k.Basla());
            Assert.True(k.Suruyor);
            Assert.False(k.Basla());   // indirme sürerken ikinci tık
            Assert.False(k.Basla());
            k.Bitir();
            Assert.False(k.Suruyor);
            Assert.True(k.Basla());     // hata sonrası yeniden denenebilir
        }

        [Fact]
        public void Indirme_kilidi_ayni_anda_gelen_tiklamalardan_yalniz_birini_gecirir()
        {
            var k = new GuncellemeKilidi();
            var gecen = 0;
            Parallel.For(0, 200, _ => { if (k.Basla()) Interlocked.Increment(ref gecen); });
            Assert.Equal(1, gecen);
        }

        [Fact]
        public void Menu_satiri_durumu_sade_soyler()
        {
            Strings.Language = "tr";
            Assert.Equal(("Güncelleme denetimi kapalı", false), GuncellemeMenusu.Satir(kapali: true, indiriliyor: false, denetleniyor: false, yeniSurum: null));
            Assert.Equal(("Güncelleme indiriliyor...", false), GuncellemeMenusu.Satir(false, indiriliyor: true, false, "0.3.5"));
            Assert.Equal(("Yeni sürüm var (0.3.5), şimdi kur", true), GuncellemeMenusu.Satir(false, false, false, "0.3.5"));
            Assert.Equal(("Güncelleme denetleniyor...", false), GuncellemeMenusu.Satir(false, false, denetleniyor: true, null));
            Assert.Equal(("Güncellemeleri denetle", true), GuncellemeMenusu.Satir(false, false, false, null));
        }

        [Fact]
        public void Kaynakta_indirme_kilidi_soru_ve_duzgun_kapanis_bagli()
        {
            var ctx = Oku("src", "Soz.Shell", "ShellContext.cs");
            Assert.Contains("if (_indirmeKilidi.Suruyor) { Flash(\"flash.update.busy\"); return; }", ctx);
            Assert.Contains("if (!_indirmeKilidi.Basla())", ctx);
            Assert.Contains("GuncellemeSorusu.Metin(surum, calisan)", ctx);
            Assert.Contains("_guncellemeZamanlayici = new System.Windows.Forms.Timer { Interval = (int)GuncellemeAdresi.Aralik.TotalMilliseconds };", ctx);
            Assert.Contains("ExitApp();", ctx);
            var gd = Oku("src", "Soz.Shell", "GuncellemeDenetimi.cs");
            Assert.Contains("WaitExitThenApplyUpdates", gd);           // Söz düzgünce kapanır (işler durdurulur, kancalar geri çevrilir)
            Assert.DoesNotContain("ApplyUpdatesAndRestart(", gd);     // anında süreç sonlandırma yok
            Assert.DoesNotContain("AllowVersionDowngrade = true", gd);  // sürüm düşürme kodda açılmadı (geri dönüş elle)
            Assert.DoesNotContain("updateUrl", gd);                    // ölü sunucu ayarı dalı kalktı
            Assert.Contains("if (Program.KurulumKoku() == null) return new Sonuc(Durum.KurulumDegil);", gd);   // sınama ve geliştirme kopyası ağa çıkmaz
        }

        // ------------------------------------------------------------ 5. yeni şemalı veri
        [Fact]
        public void Yeni_semali_veri_zincirin_icinde_de_taninir()
        {
            var sema = new Soz.Core.Data.SozSchemaTooNewException("v17");
            Assert.True(AcilisHatasi.SemaYeni(sema));
            Assert.True(AcilisHatasi.SemaYeni(new InvalidOperationException("dış", sema)));
            Assert.True(AcilisHatasi.SemaYeni(new AggregateException(new IOException("x"), new InvalidOperationException("y", sema))));
            Assert.False(AcilisHatasi.SemaYeni(new InvalidOperationException("başka")));
            Assert.False(AcilisHatasi.SemaYeni(new IOException("kilitli", new UnauthorizedAccessException())));
            Assert.False(AcilisHatasi.SemaYeni(null));
            Assert.Equal(AcilisHatasi.SemaYeniTuru, typeof(Soz.Core.Data.SozSchemaTooNewException).FullName);
        }

        [Fact]
        public void Yeni_semali_veride_dogru_cumle_gosterilir_yaniltici_cumle_gosterilmez()
        {
            Strings.Language = "tr";
            Assert.Equal("Bu kayıtlar Söz'ün daha yeni bir sürümüyle yazılmış. Lütfen Söz'ün yeni sürümünü kur.", Strings.Get("msg.schema.new"));
            Assert.DoesNotContain("kapatıp yeniden aç", Strings.Get("msg.schema.new"));
            var ctx = Oku("src", "Soz.Shell", "ShellContext.cs");
            Assert.Contains("SemaYeni = AcilisHatasi.SemaYeni(ex);", ctx);
            Assert.Contains("Panel.HataGoster(Strings.Get(\"panel.schema.title\"), Strings.Get(\"msg.schema.new\"), Strings.Get(\"panel.schema.button\"));", ctx);
            Assert.Contains("Strings.Get(SemaYeni ? \"msg.schema.new\" : \"msg.server.fail\")", ctx);
        }

        // ------------------------------------------------------------ 6. sağ tık süzgeci ve geliştirici araçları
        [Fact]
        public void Sag_tik_menusunde_yalniz_metin_isleri_kalir()
        {
            var edge = new (string?, bool)[]
            {
                ("back", false), ("forward", false), ("reload", false), (null, true),
                ("cut", false), ("copy", false), ("paste", false), (null, true),
                ("selectAll", false), (null, true),
                ("saveAs", false), ("print", false), ("createQrCode", false), ("share", false), (null, true), ("inspectElement", false),
            };
            var kalan = BaglamMenusu.Suz(edge, duzenlenebilir: true).Select(i => edge[i].Item1 ?? "|").ToList();
            Assert.Equal(new[] { "cut", "copy", "paste", "|", "selectAll" }, kalan);
            // salt okunur metinde yalnız kopyala ve tümünü seç gelir, ayraç başta ya da sonda kalmaz
            var okuma = new (string?, bool)[] { ("back", false), (null, true), ("copy", false), (null, true), (null, true), ("selectAll", false), (null, true), ("inspectElement", false) };
            Assert.Equal(new[] { "copy", "|", "selectAll" }, BaglamMenusu.Suz(okuma, false).Select(i => okuma[i].Item1 ?? "|").ToList());
            // boş alanda hiç öğe kalmaz (menü açılmaz)
            Assert.Empty(BaglamMenusu.Suz(new (string?, bool)[] { ("back", false), ("reload", false), (null, true), ("print", false), ("inspectElement", false) }, false));
        }

        [Fact]
        public void Yazim_onerileri_yalniz_yazi_kutusunda_kalir()
        {
            Assert.True(BaglamMenusu.Kalsin("spellCheck", duzenlenebilir: true));
            Assert.False(BaglamMenusu.Kalsin("spellCheck", duzenlenebilir: false));
            Assert.False(BaglamMenusu.Kalsin("inspectElement", true));
            Assert.False(BaglamMenusu.Kalsin("other", true));
            Assert.False(BaglamMenusu.Kalsin(null, true));
            Assert.True(BaglamMenusu.Kalsin("SelectAll", false));
        }

        [Fact]
        public void Gelistirici_araclari_yalniz_SOZ_DEVTOOLS_1_ile_acilir()
        {
            Assert.Equal("SOZ_DEVTOOLS", GelistiriciAraclari.OrtamAdi);
            Assert.True(GelistiriciAraclari.Acik("1"));
            Assert.True(GelistiriciAraclari.Acik(" 1 "));
            Assert.False(GelistiriciAraclari.Acik(null));
            Assert.False(GelistiriciAraclari.Acik(""));
            Assert.False(GelistiriciAraclari.Acik("0"));
            Assert.False(GelistiriciAraclari.Acik("true"));
        }

        [Fact]
        public void Kaynakta_panel_ayarlari_bagli_yakinlastirma_ve_F5_kapatilmadi()
        {
            var p = Oku("src", "Soz.Shell", "PanelForm.cs");
            Assert.Contains("core.ContextMenuRequested += (_, e) => BaglamMenusunuSuz(e);", p);
            Assert.Contains("core.Settings.AreDevToolsEnabled = GelistiriciAraclari.Acik(Environment.GetEnvironmentVariable(GelistiriciAraclari.OrtamAdi));", p);
            Assert.Contains("core.ProcessFailed += (_, e) => EkranSureciOldu(e);", p);
            Assert.DoesNotContain("IsZoomControlEnabled = false", p);
            Assert.DoesNotContain("AreBrowserAcceleratorKeysEnabled = false", p);
        }

        // ------------------------------------------------------------ 7. ekran süreci çöküşü
        [Fact]
        public void Ekran_cokunce_bir_kez_kendiliginden_toparlanir_yine_cokerse_mesaj()
        {
            var k = new WebViewKurtarma();
            var t0 = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
            Assert.Equal(WebViewKurtarma.Karar.Yenile, k.Ver(WebViewKurtarma.Tur.SayfaKapandi, t0));
            Assert.Equal(WebViewKurtarma.Karar.MesajGoster, k.Ver(WebViewKurtarma.Tur.SayfaKapandi, t0.AddSeconds(30)));
            Assert.Equal(WebViewKurtarma.Karar.MesajGoster, k.Ver(WebViewKurtarma.Tur.TarayiciKapandi, t0.AddSeconds(40)));
            // uzun süre sonra gelen çöküş yine bir kez kendiliğinden toparlanır
            Assert.Equal(WebViewKurtarma.Karar.YenidenKur, k.Ver(WebViewKurtarma.Tur.TarayiciKapandi, t0.AddMinutes(10)));
            // kişi "Yeniden dene" dedi: sayaç sıfırlanır
            Assert.Equal(WebViewKurtarma.Karar.MesajGoster, k.Ver(WebViewKurtarma.Tur.SayfaYanitVermiyor, t0.AddMinutes(10.5)));
            k.Sifirla();
            Assert.Equal(WebViewKurtarma.Karar.Yenile, k.Ver(WebViewKurtarma.Tur.SayfaYanitVermiyor, t0.AddMinutes(10.6)));
        }

        [Fact]
        public void Yan_surec_cokmesi_yok_sayilir_ve_sayaci_tuketmez()
        {
            var k = new WebViewKurtarma();
            var t0 = DateTime.UtcNow;
            Assert.Equal(WebViewKurtarma.Karar.Yoksay, k.Ver(WebViewKurtarma.Tur.Diger, t0));
            Assert.Equal(WebViewKurtarma.Karar.YenidenKur, k.Ver(WebViewKurtarma.Tur.TarayiciKapandi, t0.AddSeconds(1)));
        }

        // ------------------------------------------------------------ 8. Esc kuralı
        [Fact]
        public void Kabuk_Esc_ile_paneli_gizlemez()
        {
            var p = Oku("src", "Soz.Shell", "PanelForm.cs");
            Assert.DoesNotContain("postMessage('soz-esc')", p);
            Assert.DoesNotContain("keyData == Keys.Escape", p);
            Assert.DoesNotContain("case WebMessageKind.Esc: Hide();", p);
        }

        // ------------------------------------------------------------ 9. küçük pencereyi tepsiden geri getirme
        [Fact]
        public void Kisinin_gizledigi_kucuk_pencere_tepsiden_geri_gelir_yazi_satiri_acilmaz()
        {
            var m = new KapsulMantik(new Ayar(), s => s.Length * 7.0);
            m.BostaSifirla(0);
            Assert.False(m.Goster(1));                        // gizli değilken bir şey yapmaz
            m.Gizle(Gizlilik.Kullanici, 10);
            Assert.Equal(Gizlilik.Kullanici, m.Gizli);
            Assert.True(m.Goster(20));
            Assert.Equal(Gizlilik.Yok, m.Gizli);
            Assert.False(m.Giris);
            m.Gizle(Gizlilik.TamEkran, 30);
            Assert.False(m.Goster(40));                       // tam ekran gizlemesine dokunmaz
        }

        [Fact]
        public void Gizlilik_bilgisi_ve_goster_olayi_protokolde_gidip_gelir()
        {
            foreach (var deger in new[] { "1", "0" })
            {
                var k = new KapsulKomutu(KomutTuru.Gizli, null, deger);
                Assert.Equal(k, KabukProtokol.KomutOku(KabukProtokol.Yaz(k)));
            }
            var o = new KabukOlayi(OlayTuru.Goster);
            Assert.Equal(o, KabukProtokol.OlayOku(KabukProtokol.OlayYaz(o)));
            var ctx = Oku("src", "Soz.Shell", "ShellContext.cs");
            Assert.Contains("case KomutTuru.Gizli: _kapsulGizli = k.Metin == \"1\";", ctx);
            Assert.Contains("_showCapsuleItem.Visible = _kapsulGizli && Kapsul is { Calisiyor: true };", ctx);
            var kapsul = Oku("src", "Soz.Capsule", "Pencere", "Kapsul.cs");
            Assert.Contains("case OlayTuru.Goster: _m.Goster(now); Isle(); break;", kapsul);
        }

        // ------------------------------------------------------------ 10. sürüm
        [Fact]
        public void Surum_0_3_6_tek_kaynakta()
        {
            Assert.Matches(new Regex(@"<Version>0\.3\.6</Version>"), Oku("Directory.Build.props"));
        }
    }
}
