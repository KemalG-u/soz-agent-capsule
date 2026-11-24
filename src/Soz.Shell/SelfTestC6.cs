using System.Text;
using Microsoft.Win32;
using Soz.Core.Hooks;

namespace Soz.Shell;

/// <summary>--selftest c6 bölümü: kaldırma, veri koruma, ayar dosyası temizliği, açılışta başlama, güncelleme kuralları, pencere eşleştirme. Gerçek kullanıcı verisine ve gerçek kayıt defterine dokunmaz.</summary>
internal static partial class SelfTest
{
    private static string? C(bool cond, string msg) => cond ? null : msg;

    private static string SozKomutu(string arg) => HookInstaller.BuildCommand(@"C:\Soz\Soz.exe", arg, @"C:\Soz\veri", null);

    private static void C6Testleri(Action<string, Func<string?>> T)
    {
        var utf8 = new UTF8Encoding(false);

        // ---------- ayar dosyası: yalnız Söz kayıtları çıkar, kalan her bayt aynı ----------
        T("ayar temizliği: son olay (BOM, CRLF, sekme, yorum) bayt bayt korunur", () =>
        {
            var soz = "{ \"hooks\": [ { \"type\": \"command\", \"command\": \"" + SozKomutu("session-start").Replace("\\", "\\\\") + "\" } ] }";
            var parca = ",\r\n\t\t\"SessionStart\": [\r\n\t\t\t" + soz + "\r\n\t\t]";
            var once = "\uFEFF{\r\n\t// kullanıcının notu\r\n\t\"permissions\": { \"allow\": [\"Bash(ls)\"] },\r\n\t\"hooks\": {\r\n\t\t\"Stop\": [\r\n\t\t\t{ \"hooks\": [ { \"type\": \"command\", \"command\": \"echo benim\" } ] }\r\n\t\t]" + parca + "\r\n\t},\r\n\t\"model\": \"opus\" // sonda yorum\r\n}\r\n";
            var beklenen = once.Replace(parca, "");
            var bom = new UTF8Encoding(true).GetPreamble();
            var b = bom.Concat(utf8.GetBytes(once.TrimStart('\uFEFF'))).ToArray();
            var (yeni, n, hata) = AyarDosyasiTemizleyici.Kes(b);
            var bk = bom.Concat(utf8.GetBytes(beklenen.TrimStart('\uFEFF'))).ToArray();
            return C(hata == null && n == 1 && yeni != null && yeni.AsSpan().SequenceEqual(bk), $"hata={hata} n={n} sonuç={(yeni == null ? "yok" : Encoding.UTF8.GetString(yeni))}");
        });
        T("ayar temizliği: aynı grupta ortadaki Söz kancası çıkar, yanındaki kullanıcı kancaları kalır", () =>
        {
            var soz = "{ \"type\": \"command\", \"command\": \"" + SozKomutu("session-start") + "\" }";
            var once = "{\n  \"hooks\": {\n    \"Stop\": [\n      {\n        \"hooks\": [\n          { \"type\": \"command\", \"command\": \"echo benim\" },\n          " + soz + ",\n          { \"type\": \"command\", \"command\": \"echo sonra\" }\n        ]\n      }\n    ]\n  }\n}\n";
            var beklenen = once.Replace(soz + ",\n          ", "");
            var (yeni, n, hata) = AyarDosyasiTemizleyici.Kes(utf8.GetBytes(once));
            return C(hata == null && n == 1 && yeni != null && Encoding.UTF8.GetString(yeni) == beklenen, $"hata={hata} n={n} sonuç={(yeni == null ? "yok" : Encoding.UTF8.GetString(yeni))}");
        });
        T("ayar temizliği: yalnız Söz kancası kalan 'hooks' bölümü tümden gider, sonraki anahtar korunur", () =>
        {
            var soz = "{ \"type\": \"command\", \"command\": \"" + SozKomutu("user-prompt-submit") + "\" }";
            var kume = "\"hooks\": {\n    \"UserPromptSubmit\": [ { \"hooks\": [ " + soz + " ] } ]\n  },\n  ";
            var once = "{\n  " + kume + "\"env\": { \"A\": \"1\" }\n}\n";
            var beklenen = "{\n  \"env\": { \"A\": \"1\" }\n}\n";
            var (yeni, n, hata) = AyarDosyasiTemizleyici.Kes(utf8.GetBytes(once));
            return C(hata == null && n == 1 && yeni != null && Encoding.UTF8.GetString(yeni) == beklenen, $"hata={hata} n={n} sonuç={(yeni == null ? "yok" : Encoding.UTF8.GetString(yeni))}");
        });
        T("ayar temizliği: Söz kancası yoksa, bozuk dosyada ve 'soz' geçen kullanıcı kancasında dokunmaz", () =>
        {
            var a = AyarDosyasiTemizleyici.Kes(utf8.GetBytes("{ \"hooks\": { \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"echo soz notlari\" } ] }, ] } }"));
            var b = AyarDosyasiTemizleyici.Kes(utf8.GetBytes("{ \"hooks\": { bozuk"));
            var c = AyarDosyasiTemizleyici.Kes(utf8.GetBytes("[1,2]"));
            return C(a is { Sayi: 0, Yeni: null, Hata: null } && b.Hata != null && b.Yeni == null && c.Hata != null, $"a={a.Sayi}/{a.Hata} b={b.Hata} c={c.Hata}");
        });

        // ---------- kaldırma: veri kalır ----------
        string Kur(out string baz, out string root, out string veri, out string proj)
        {
            baz = Path.Combine(Path.GetTempPath(), "soz-c6-" + Guid.NewGuid().ToString("N")[..8]);
            root = Path.Combine(baz, "Soz");
            veri = Path.Combine(root, "veri");
            proj = Path.Combine(baz, "ornek-proje");
            Directory.CreateDirectory(Path.Combine(proj, ".claude"));
            Directory.CreateDirectory(Path.Combine(veri, "kanit"));
            File.WriteAllText(Path.Combine(veri, "kanit", "a.txt"), "kanıt");
            var db = Soz.Core.Data.SozDb.OpenAndMigrate(veri);
            _ = new Soz.Core.Jobs.WorkService(db).EnsureProjectByPath(proj);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return baz;
        }

        T("kaldırma: veri silinmez, kenara taşınır, not yazılır; ayar dosyaları bayt bayt korunur; yedek alınır; ikinci çalışma değiştirmez", () =>
        {
            var baz = Kur(out _, out var root, out var veri, out var proj);
            try
            {
                var yerel = Path.Combine(proj, ".claude", "settings.local.json");
                var ortak = Path.Combine(proj, ".claude", "settings.json");
                var soz1 = "{ \"type\": \"command\", \"command\": \"" + SozKomutu("session-start") + "\", \"timeout\": 5 }";
                var soz2 = "{ \"type\": \"command\", \"command\": \"" + SozKomutu("user-prompt-submit") + "\" }";
                var kullanici = "{ \"type\": \"command\", \"command\": \"echo kullanici\" }";
                var yerelOnce = "{\r\n\t// elle yazılmış not\r\n\t\"hooks\": {\r\n\t\t\"Stop\": [ { \"hooks\": [ " + kullanici + " ] } ],\r\n\t\t\"SessionStart\": [ { \"hooks\": [ " + soz1 + " ] } ]\r\n\t}\r\n}";
                var yerelBekl = "{\r\n\t// elle yazılmış not\r\n\t\"hooks\": {\r\n\t\t\"Stop\": [ { \"hooks\": [ " + kullanici + " ] } ]\r\n\t}\r\n}";
                var ortakOnce = "{ \"model\": \"x\", \"hooks\": { \"UserPromptSubmit\": [ { \"hooks\": [ " + kullanici + ", " + soz2 + " ] } ] }, \"z\": 1 }";
                var ortakBekl = "{ \"model\": \"x\", \"hooks\": { \"UserPromptSubmit\": [ { \"hooks\": [ " + kullanici + " ] } ] }, \"z\": 1 }";
                File.WriteAllBytes(yerel, utf8.GetBytes(yerelOnce));
                File.WriteAllBytes(ortak, utf8.GetBytes(ortakOnce));

                var res = UninstallCleanup.RunInRoot(root, clearStartup: false, TimeSpan.FromSeconds(10));
                var korunan = Path.Combine(baz, "Soz-veri");
                var yerelSon = File.ReadAllBytes(yerel);
                var ortakSon = File.ReadAllBytes(ortak);
                var yedekler = Directory.GetFiles(Path.Combine(proj, ".claude"), "*.soz-yedek-*");
                var yedekDogru = yedekler.Count(y => File.ReadAllBytes(y).AsSpan().SequenceEqual(utf8.GetBytes(y.Contains("settings.local") ? yerelOnce : ortakOnce)));
                var res2 = UninstallCleanup.TryUninstallProjectHooks(proj);
                var yedekler2 = Directory.GetFiles(Path.Combine(proj, ".claude"), "*.soz-yedek-*").Length;
                return C(res.HooksRemoved == 2 && !res.VeriSilindi && res.KorunanKlasor == korunan
                         && !Directory.Exists(veri) && File.Exists(Path.Combine(korunan, "soz.db")) && File.ReadAllText(Path.Combine(korunan, "kanit", "a.txt")) == "kanıt"
                         && File.Exists(Path.Combine(korunan, VeriKorumasi.NotAdi))
                         && File.ReadAllText(Path.Combine(korunan, VeriKorumasi.NotAdi)).Contains(korunan)
                         && yerelSon.AsSpan().SequenceEqual(utf8.GetBytes(yerelBekl)) && ortakSon.AsSpan().SequenceEqual(utf8.GetBytes(ortakBekl))
                         && yedekler.Length == 2 && yedekDogru == 2 && res2 == 0 && yedekler2 == 2,
                    $"kancalar={res.HooksRemoved} silindi={res.VeriSilindi} korunan={res.KorunanKlasor} veriVar={Directory.Exists(veri)} yedek={yedekler.Length}/{yedekDogru} ikinci={res2}/{yedekler2} not={string.Join("|", res.Notlar ?? [])}");
            }
            finally { try { Directory.Delete(baz, true); } catch { } }
        });
        T("kaldırma kancası (RunDefault) yalıtılmış kökle çalışır: veri kenara alınır, gerçek açılış kaydı ve gerçek veri klasörü değişmez", () =>
        {
            var baz = Kur(out _, out var root, out var veri, out _);
            var onceki = Environment.GetEnvironmentVariable(UninstallCleanup.TestRootEnv);
            string? gercekOnce = null, gercekSon = null;
            try { using var k = Registry.CurrentUser.OpenSubKey(StartWithWindows.RunKeyPath); gercekOnce = k?.GetValue(StartWithWindows.ValueName) as string; } catch { }
            var gercekVeri = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soz", "veri");
            var gercekVeriVardi = Directory.Exists(gercekVeri);
            try
            {
                Environment.SetEnvironmentVariable(UninstallCleanup.TestRootEnv, root);
                var res = UninstallCleanup.RunDefault();
                try { using var k = Registry.CurrentUser.OpenSubKey(StartWithWindows.RunKeyPath); gercekSon = k?.GetValue(StartWithWindows.ValueName) as string; } catch { }
                return C(res.KorunanKlasor == Path.Combine(baz, "Soz-veri") && !res.StartupCleared && gercekOnce == gercekSon && Directory.Exists(gercekVeri) == gercekVeriVardi && File.Exists(Path.Combine(baz, "Soz-veri", "soz.db")),
                    $"korunan={res.KorunanKlasor} startupTemizlendi={res.StartupCleared} kayit={(gercekOnce == gercekSon)} gercekVeri={Directory.Exists(gercekVeri) == gercekVeriVardi}");
            }
            finally { Environment.SetEnvironmentVariable(UninstallCleanup.TestRootEnv, onceki); try { Directory.Delete(baz, true); } catch { } }
        });
        T("kaldırma: kullanıcı 'verimi de sil' seçtiyse (tercih dosyası) veri silinir, yoksa asla", () =>
        {
            var baz = Kur(out _, out var root, out var veri, out _);
            try
            {
                new KabukTercihleri { KaldirirkenVeriyiSil = true }.Yaz(veri);
                var res = UninstallCleanup.RunInRoot(root, clearStartup: false, TimeSpan.FromSeconds(10));
                return C(res.VeriSilindi && !Directory.Exists(veri) && !Directory.Exists(Path.Combine(baz, "Soz-veri")), $"silindi={res.VeriSilindi} veriVar={Directory.Exists(veri)}");
            }
            finally { try { Directory.Delete(baz, true); } catch { } }
        });
        T("yeniden kurulum: kenardaki veri ilk açılışta geri gelir, mevcut veri ezilmez, dolu korunan klasörün üstüne yazılmaz", () =>
        {
            var baz = Kur(out _, out var root, out var veri, out _);
            try
            {
                var an = new DateTime(2026, 11, 23, 10, 0, 0);
                var k1 = VeriKorumasi.Koru(veri, root, () => an);
                if (!k1.Tasindi || k1.Klasor != Path.Combine(baz, "Soz-veri")) return "ilk taşıma olmadı: " + k1;
                var geri = VeriKorumasi.GeriYukle(veri, root);
                var tamam1 = geri > 0 && File.Exists(Path.Combine(veri, "soz.db")) && File.Exists(Path.Combine(veri, "kanit", "a.txt")) && !Directory.Exists(k1.Klasor);
                // ikinci kaldırma: korunan ad doluysa damgalı ad
                var k2 = VeriKorumasi.Koru(veri, root, () => an);
                Directory.CreateDirectory(veri); File.WriteAllText(Path.Combine(veri, "soz.db"), "yeni");
                var k3 = VeriKorumasi.Koru(veri, root, () => an);
                var geri2 = VeriKorumasi.GeriYukle(veri, root); // veri\soz.db yok, Soz-veri yok (k2 korunan = Soz-veri) -> geri gelir
                return C(tamam1 && k2.Klasor == Path.Combine(baz, "Soz-veri") && k3.Klasor != null && k3.Klasor.Contains("20261123-100000") && geri2 > 0 && File.Exists(Path.Combine(veri, "soz.db")),
                    $"tamam1={tamam1} k2={k2.Klasor} k3={k3.Klasor} geri2={geri2}");
            }
            finally { try { Directory.Delete(baz, true); } catch { } }
        });
        T("kabuk tercihleri: yaz/oku, bilinmeyen alan korunur, bozuk dosyada boş döner", () =>
        {
            var d = Path.Combine(Path.GetTempPath(), "soz-c6t-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(d);
                File.WriteAllText(KabukTercihleri.YolOlustur(d), "{\"baskaAlan\": 7}");
                new KabukTercihleri { AcilistaBasla = false, KaldirirkenVeriyiSil = false, GuncellemeAdresi = "https://ornek.test/f" }.Yaz(d);
                var t = KabukTercihleri.Oku(d);
                var ham = File.ReadAllText(KabukTercihleri.YolOlustur(d));
                File.WriteAllText(KabukTercihleri.YolOlustur(d), "{bozuk");
                var bos = KabukTercihleri.Oku(d);
                return C(t.AcilistaBasla == false && !t.KaldirirkenVeriyiSil && t.GuncellemeAdresi == "https://ornek.test/f" && ham.Contains("baskaAlan") && bos.AcilistaBasla == null && !bos.KaldirirkenVeriyiSil,
                    $"t={t.AcilistaBasla}/{t.GuncellemeAdresi} ham={ham}");
            }
            finally { try { Directory.Delete(d, true); } catch { } }
        });

        // ---------- açılışta başlama (yalıtılmış kayıt anahtarı) ----------
        T("açılışta başlama: aç, kapat, Görev Yöneticisi kapatması, önceki 'kapalı' kararı kurulumda saygı görür", () =>
        {
            var tur = "Software\\SozDeneme-" + Guid.NewGuid().ToString("N")[..8];
            var run = tur + "\\Run"; var onay = tur + "\\Onay";
            var d = Path.Combine(Path.GetTempPath(), "soz-c6r-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var exe = Environment.ProcessPath!;
                var bos = !StartWithWindows.IsEnabled(run, onay);
                var ac = StartWithWindows.Enable(exe, run, onay) && StartWithWindows.IsEnabled(run, onay);
                using (var k = Registry.CurrentUser.CreateSubKey(onay)) k.SetValue(StartWithWindows.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                var gorevYoneticisiKapali = !StartWithWindows.IsEnabled(run, onay);
                var yenidenAc = StartWithWindows.Enable(exe, run, onay) && StartWithWindows.IsEnabled(run, onay);
                StartWithWindows.Disable(run);
                var kapali = !StartWithWindows.IsEnabled(run, onay);
                // kurulum kancası: önceki karar "kapalı" ise eklemez; karar yoksa ekler
                Directory.CreateDirectory(d);
                new KabukTercihleri { AcilistaBasla = false }.Yaz(d);
                StartWithWindows.EnableOnFreshInstall(exe, [d], run, onay);
                var saygi = !StartWithWindows.IsEnabled(run, onay);
                StartWithWindows.EnableOnFreshInstall(exe, [Path.Combine(d, "yok")], run, onay);
                var varsayilan = StartWithWindows.IsEnabled(run, onay);
                return C(bos && ac && gorevYoneticisiKapali && yenidenAc && kapali && saygi && varsayilan, $"bos={bos} ac={ac} gy={gorevYoneticisiKapali} yeniden={yenidenAc} kapali={kapali} saygi={saygi} varsayilan={varsayilan}");
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(tur, throwOnMissingSubKey: false); } catch { }
                try { Directory.Delete(d, true); } catch { }
            }
        });

        // ---------- güncelleme kuralları ----------
        T("güncelleme: adres sırası (env, tercih, varsayılan besleme), kapatma anahtarı, düz uzak http reddedilir, kurulu olmayan yerde ağa çıkmadan sessiz geçer", () =>
        {
            var d = Path.Combine(Path.GetTempPath(), "soz-c6g-" + Guid.NewGuid().ToString("N")[..8]);
            var onceki = Environment.GetEnvironmentVariable(GuncellemeDenetimi.AdresEnv);
            try
            {
                Environment.SetEnvironmentVariable(GuncellemeDenetimi.AdresEnv, null);
                Directory.CreateDirectory(d);
                var yok = GuncellemeDenetimi.AdresBul(d) == (GuncellemeAdresi.Varsayilan, false);
                new KabukTercihleri { GuncellemeAdresi = "https://tercih.test/f" }.Yaz(d);
                var tercih = GuncellemeDenetimi.AdresBul(d) == ("https://tercih.test/f", false);
                new KabukTercihleri { GuncellemeDenetimi = false }.Yaz(d);
                var ayar = GuncellemeDenetimi.AdresBul(d) == ((string?)null, true);   // kapatma anahtarı
                Environment.SetEnvironmentVariable(GuncellemeDenetimi.AdresEnv, "https://env.test/f");
                var env = GuncellemeDenetimi.AdresBul(d) == ("https://env.test/f", false);
                var guvenli = GuncellemeDenetimi.AdresGuvenli("https://a.test/f") && GuncellemeDenetimi.AdresGuvenli("http://127.0.0.1:8080/f") && GuncellemeDenetimi.AdresGuvenli(d)
                              && !GuncellemeDenetimi.AdresGuvenli("http://a.test/f") && !GuncellemeDenetimi.AdresGuvenli("ftp://a.test/f") && !GuncellemeDenetimi.AdresGuvenli("");
                var r1 = new GuncellemeDenetimi("http://a.test/f").DenetleAsync().GetAwaiter().GetResult();
                var r2 = new GuncellemeDenetimi("http://127.0.0.1:1/f").DenetleAsync().GetAwaiter().GetResult(); // kurulu değil: ağa hiç çıkmadan sessiz
                return C(yok && tercih && ayar && env && guvenli && r1.Durum == GuncellemeDenetimi.Durum.AdresGecersiz && r2.Durum == GuncellemeDenetimi.Durum.KurulumDegil,
                    $"yok={yok} tercih={tercih} ayar={ayar} env={env} guvenli={guvenli} r1={r1.Durum} r2={r2.Durum}/{r2.Ayrinti}");
            }
            finally
            {
                Environment.SetEnvironmentVariable(GuncellemeDenetimi.AdresEnv, onceki);
                try { Directory.Delete(d, true); } catch { }
            }
        });

        // ---------- pencere eşleştirme ----------
        T("pencereyi aç: sekme eşleştirme tek eşleşmede seçer, belirsizde seçmez ve anlaşılır ipucu verir", () =>
        {
            var adlar = new[] { "✳ Claude Code", "soz-urun: duzeltme", "Windows PowerShell", "api-sunucu" };
            var tek = PencereOnuneGetirici.TekSekme(adlar, "soz-urun", @"E:\Is\soz-urun");
            var yok = PencereOnuneGetirici.TekSekme(adlar, "baska-proje", null);
            var iki = PencereOnuneGetirici.TekSekme(["a: soz-urun", "b: soz-urun"], "soz-urun", null);
            var kisa = PencereOnuneGetirici.TekSekme(["ab: yapıyor"], "ab", null); // 3 harften kısa ad eşleşmez
            var oturum = PencereOnuneGetirici.TekSekme(["✳ Pencere öne getirme düzeltmesi", "api-sunucu", "ab"], "baska-proje", null, "pencere öne getirme düzeltmesi") == 0
                         && PencereOnuneGetirici.TekSekme(["kısa", "api"], "baska-proje", null, "kısa") == null; // 6 harften kısa oturum başlığı ipucu sayılmaz
            var sozcuk = PencereOnuneGetirici.TekSekme(["soz-urunler listesi"], "soz-urun", null) == null; // sözcüğün parçası değil
            var yolla = PencereOnuneGetirici.Tasiyor(@"E:\Is\soz-urun - Visual Studio Code", "soz-urun", @"E:\Is\soz-urun");
            var ad = PencereOnuneGetirici.KlasorAdi(@"E:\Is\soz-urun\") == "soz-urun" && PencereOnuneGetirici.KlasorAdi("") == null;
            var ip = PencereOnuneGetirici.Ipucu("soz-urun", 4, 0);
            var ip2 = PencereOnuneGetirici.Ipucu("soz-urun", 4, 2);
            var ip3 = PencereOnuneGetirici.Ipucu("soz-urun", 0, 0);
            var sade = new[] { ip, ip2, ip3 }.All(s => !s.Contains(';') && !s.Contains('\u2014') && !s.Contains('\u2013') && s.Contains("soz-urun"));
            return C(tek == 1 && yok == null && iki == null && kisa == null && sozcuk && yolla && ad && sade && oturum, $"oturum={oturum} tek={tek} yok={yok} iki={iki} kisa={kisa} sozcuk={sozcuk} yolla={yolla} ad={ad} sade={sade}");
        });

        // ---------- Windows bildirimi ----------
        T("bildirim: ilk yükleme sessiz, yeni öğe bir kez bildirilir, panel açıkken bildirilmez", () =>
        {
            AttentionItemInfo O(string id, string reason, string detail = "") => new(id, reason, "soz-urun", "Claude", "«soz-urun» yardımcısı sana bir soru sordu.", detail, "soz-urun");
            var an = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            var y = new BildirimYoneticisi(() => an);
            var ilk = y.Degerlendir([O("a", "session_question")], false);
            var ayni = y.Degerlendir([O("a", "session_question")], false);
            var yeni = y.Degerlendir([O("a", "session_question"), O("b", "session_permission")], false);
            var tekrar = y.Degerlendir([O("a", "session_question"), O("b", "session_permission")], false);
            var diger = y.Degerlendir([O("c", "session_done")], false); // uygun olmayan neden
            var panelde = y.Degerlendir([O("d", "session_input")], true);
            var sonra = y.Degerlendir([O("d", "session_input")], false); // panelde görüldü sayıldı
            var sade = yeni != null && yeni.Baslik == "soz-urun · Claude" && yeni.Metin == "Senden izin bekliyor" && yeni.Sayi == 1;
            return C(ilk == null && ayni == null && sade && tekrar == null && diger == null && panelde == null && sonra == null,
                $"ilk={ilk} ayni={ayni} yeni={yeni} tekrar={tekrar} diger={diger} panelde={panelde} sonra={sonra}");
        });
        T("bildirim: dakikada en çok üç, fazlası birleşir, sade Türkçe metin, hata nedeni", () =>
        {
            AttentionItemInfo O(string id, string reason, string detail = "") => new(id, reason, "api", "Codex", "", detail, "api");
            var an = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            var y = new BildirimYoneticisi(() => an);
            var liste = new List<AttentionItemInfo>();
            y.Degerlendir(liste, false);
            var gosterilen = 0;
            for (var i = 0; i < 6; i++) { liste.Add(O("x" + i, "session_question", "Hangisini seçeyim?")); if (y.Degerlendir(liste, false) != null) gosterilen++; }
            an = an.AddSeconds(61);
            var birlesik = y.Degerlendir(liste, false); // bekleyen üç öğe tek bildirim
            var hata = BildirimYoneticisi.Tek(O("f", "session_failed", "kullanım sınırı doldu"));
            var soru = BildirimYoneticisi.Tek(O("q", "session_question", "«Hangisini» seçeyim?\n"));
            var sade = new[] { hata.Metin, soru.Metin, birlesik?.Metin ?? "" }.All(s => !s.Contains(';') && !s.Contains('—') && !s.Contains('«') && !s.Contains('\n'));
            return C(gosterilen == BildirimYoneticisi.DakikadaEnCok && birlesik is { Sayi: 3 } && hata.Metin == "Hata yüzünden durdu: kullanım sınırı doldu" && soru.Metin == "Hangisini seçeyim?" && sade,
                $"gosterilen={gosterilen} birlesik={birlesik} hata={hata.Metin} soru={soru.Metin} sade={sade}");
        });
        T("bildirim: /api/attention cevabı ayrıştırılır, bozuk gövde çökertmez, tercih varsayılanı açık", () =>
        {
            using var d = System.Text.Json.JsonDocument.Parse("{\"items\":[{\"id\":\"s1\",\"reason\":\"session_failed\",\"projectName\":\"p\",\"helper\":\"Claude\",\"detail\":\"d\"},{\"reason\":\"yok\"}]}");
            var l = Json.ParseAttention(d.RootElement);
            using var b = System.Text.Json.JsonDocument.Parse("[1,2]");
            var bos = Json.ParseAttention(b.RootElement);
            var k = Path.Combine(Path.GetTempPath(), "soz-bildirim-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var varsayilan = KabukTercihleri.Oku(k).Bildirimler;
                var t = new KabukTercihleri { Bildirimler = false }; t.Yaz(k);
                var susturuldu = !KabukTercihleri.Oku(k).Bildirimler;
                return C(l is { Count: 1 } && l[0].Id == "s1" && l[0].Helper == "Claude" && bos == null && varsayilan && susturuldu, $"l={l?.Count} bos={bos} varsayilan={varsayilan} susturuldu={susturuldu}");
            }
            finally { try { Directory.Delete(k, true); } catch { } }
        });
    }
}
