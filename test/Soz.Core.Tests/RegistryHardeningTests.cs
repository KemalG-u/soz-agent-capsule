using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Cli;
using Soz.Cli.Commands;
using Soz.Core.Jobs;
using Soz.Core.Registry;
using Soz.Server;
using Xunit;
using static Soz.Core.Tests.RegistryTestKit;

namespace Soz.Core.Tests;

/// <summary>Denetim 5 (E2 ve önemli bulgular) için kalıcı testler ve ayarın kayda bağlanması (d1c).</summary>
public class RegistryIdRuleTests
{
    [Theory]
    [InlineData("setting:ornek.a\n")]                       // sondaki satır sonu ($ eşleşirdi, \z eşleşmez)
    [InlineData("setting:ornek.a\r\n")]
    [InlineData("setting:ornek.a ")]
    [InlineData("setting:\u0430.b")]                         // Kiril "а"
    [InlineData("setting:ui.th\u0435me")]                    // Kiril "е"
    [InlineData("setting:ui.th\u0131me")]                    // noktasız ı
    [InlineData("setting:hotkey.pac\u212Aet")]               // Kelvin işareti
    [InlineData("\uFF53etting:a.b")]                         // tam genişlikli s
    [InlineData("Setting:a.b")]
    [InlineData("setting:UI.theme")]
    [InlineData("setting:ui.theme\u200B")]                   // sıfır genişlikli boşluk
    [InlineData("setting:a\u0000b")]
    [InlineData("setting:../../etc.x")]
    [InlineData("setting:a\\b")]
    public void Kimlik_YalnizAsciiKucukHarfDeseninden_BaskasiYuklenmez(string id)
    {
        var res = LoadUser(SettingFile(id));
        Assert.Empty(res.Entries);
        Assert.NotEmpty(res.Rejected);
        Assert.False(RegistryLoader.IsValidId(id));
        // fixed kimliği taklit etmeye de yaramaz: gömülü kayıt dokunulmadan kalır
        var withEmbedded = RegistryLoader.Load(RegistryLoader.EmbeddedSources(), new[] { Source("t.soz.json", SettingFile(id)) });
        Assert.Equal(RegistryLoader.LoadEmbedded().Entries.Count, withEmbedded.Entries.Count);       // d2m: sabit 39 yerine gömülü kayıt sayısı
    }

    [Fact]
    public void FixedKimlik_KarsilastirmasiOrdinal_BenzerKimlikFarkliSayilir()
    {
        Assert.True(RegistryLoader.IsValidId("setting:ui.theme"));
        // büyük-küçük harf farkı ya da benzer harf, fixed kimlikle aynı sayılmaz; çünkü zaten geçersiz kimliktir
        foreach (var fake in new[] { "setting:UI.theme", "setting:ui.th\u0435me", "setting:ui.theme\n" })
            Assert.DoesNotContain(RegistryLoader.Load(RegistryLoader.EmbeddedSources(), new[] { Source("t.soz.json", SettingFile(fake)) }).Entries, e => e.Owner == RegistryOwners.User);
        // gerçeği ise "fixed-id" ile reddedilir
        var real = RegistryLoader.Load(RegistryLoader.EmbeddedSources(), new[] { Source("t.soz.json", SettingFile("setting:ui.theme")) });
        Assert.Contains(real.Rejected, p => p.Code == "fixed-id");
    }

    [Fact]
    public void KimlikDeseni_SemadaBackslashZIleBiter()
    {
        var pattern = JsonNode.Parse(RegistryLoader.SchemaText)!["$defs"]!["EntryId"]!["pattern"]!.GetValue<string>();
        Assert.EndsWith("{0,63}\\z", pattern);
        Assert.StartsWith("^[a-z][a-z0-9-]*:[a-z]", pattern);
    }

    [Theory]
    [InlineData("connector:evil.x", "setting")]
    [InlineData("setting:evil.x", "connector")]
    [InlineData("theme:x", "setting")]
    public void KimlikOnekiKindIleAyniOlmali(string id, string kind)
    {
        var text = kind == "setting"
            ? SettingFile(id)
            : $$$$"""{"schemaVersion":1,"entries":[{"id":"{{{{id}}}}","kind":"connector","owner":"user","title":"x","body":{"depth":["run"],"detect":{"commands":["x"]},"launch":{"protocol":"acp"}}}]}""";
        var res = LoadUser(text);
        Assert.Empty(res.Entries);
        var p = Assert.Single(res.Rejected, x => x.Code == "id-kind");
        Assert.Contains("aynı olmalı", p.Message);
        Assert.Contains(kind, p.Message);
    }

    [Fact]
    public void TumOnUcTurde_OnekKindIleAyniOlunca_Yuklenir()
    {
        var entries = RegistryKinds.All.Where(k => !RegistryKinds.IsDefined(k)).Select(k => (JsonNode)new JsonObject { ["id"] = k + ":ornek", ["kind"] = k, ["owner"] = "user", ["title"] = "x", ["body"] = new JsonObject() }).ToArray();
        var res = LoadUser(new JsonObject { ["schemaVersion"] = 1, ["entries"] = new JsonArray(entries) }.ToJsonString());
        Assert.Empty(res.Rejected);
        Assert.Equal(RegistryKinds.All.Count(k => !RegistryKinds.IsDefined(k)), res.Entries.Count);   // d2m: sabit 10 yerine tanımsız tür sayısı (reader tanımlı türlere girdi)
    }
}

public class RegistryDirTests
{
    private static string CmdJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15000);
        return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0 ? "" : "bağlantı kurulamadı";
    }

    [Theory]
    [InlineData("", "boş")]
    [InlineData("   ", "boş")]
    [InlineData("a|b", "geçersiz karakter")]
    [InlineData(@"\\localhost\C$\x", "ağ yolları")]
    [InlineData(@"\\sunucu\paylasim", "ağ yolları")]
    [InlineData("//sunucu/paylasim", "ağ yolları")]
    [InlineData(@"\\?\C:\Windows", "ağ yolları")]
    [InlineData(@"\\.\NUL", "ağ yolları")]
    [InlineData("NUL", "")]
    [InlineData("COM1", "")]
    public void CheckDir_UncAygitVeGecersizYollariReddeder(string dir, string why)
    {
        var err = RegistryLoader.CheckDir(dir, out var full);
        Assert.NotNull(err);
        Assert.Null(full);
        if (why.Length > 0) Assert.Contains(why, err);
    }

    [Fact]
    public void CheckDir_OlmayanKlasor_Dosya_Baglanti_ve_Uzun()
    {
        using var dir = new RegistryUserDir();
        Assert.Contains("klasör bulunamadı", RegistryLoader.CheckDir(Path.Combine(dir.Path, "yok"), out _));
        var file = dir.Write("bir-dosya.txt", "x");
        Assert.Contains("bir dosya", RegistryLoader.CheckDir(file, out _));
        Assert.Contains("çok uzun", RegistryLoader.CheckDir(new string('a', 2000), out _));
        Assert.Null(RegistryLoader.CheckDir(dir.Path, out var full));
        Assert.Equal(Path.GetFullPath(dir.Path), full);
    }

    [Fact]
    public void Baglanti_Junction_Izlenmez_Ne_Kok_Ne_AltKlasor()
    {
        using var outside = new RegistryUserDir();
        outside.Write("disari.soz.json", SettingFile("setting:disari.x"));
        using var dir = new RegistryUserDir();
        dir.Write("icerde.soz.json", SettingFile("setting:icerde.x"));
        var link = Path.Combine(dir.Path, "bag");
        var failed = CmdJunction(link, outside.Path);
        Assert.True(failed == "", failed);
        try
        {
            var read = RegistryLoader.ReadUserDir(dir.Path);
            Assert.Equal(new[] { "icerde.soz.json" }, read.Sources.Select(s => s.Name).ToArray());   // bağlantının ardındaki dosya okunmadı
            Assert.Empty(read.Problems);
            // kök klasörün kendisi bağlantıysa reddedilir
            Assert.Contains("bağlantı", RegistryLoader.CheckDir(link, out _));
            var viaLink = RegistryLoader.LoadEmbedded(link);
            Assert.Contains(viaLink.Rejected, p => p.Code == "dir");
            Assert.Null(viaLink.Get("setting:disari.x"));
        }
        finally { Directory.Delete(link, false); } // yalnız bağlantıyı kaldırır, hedefe dokunmaz
        Assert.True(File.Exists(Path.Combine(outside.Path, "disari.soz.json")));
    }

    [Fact]
    public void DosyaBoyutSiniri_BirMegabayt_Ustu_Okunmaz_Altinda_Okunur()
    {
        using var dir = new RegistryUserDir();
        var ok = SettingFile("setting:buyuk.a");
        var pad = RegistryLoader.MaxFileBytes - System.Text.Encoding.UTF8.GetByteCount(ok);
        dir.Write("tamam.soz.json", ok + new string(' ', pad));          // tam 1 MB (bayt olarak)
        dir.Write("fazla.soz.json", ok + new string(' ', pad + 1));       // 1 MB + 1
        var read = RegistryLoader.ReadUserDir(dir.Path);
        Assert.Equal(new[] { "tamam.soz.json" }, read.Sources.Select(s => s.Name).ToArray());
        var p = Assert.Single(read.Problems);
        Assert.Equal("fazla.soz.json", p.Source);
        Assert.Equal("size", p.Code);
        Assert.Contains("çok büyük", p.Message);
        // doğrudan kaynak verilse de (klasör dışında) aynı sınır
        var huge = Source("dev.soz.json", ok + new string(' ', RegistryLoader.MaxFileBytes + 10));
        var res = RegistryLoader.Load(Array.Empty<RegistrySource>(), new[] { huge });
        Assert.Empty(res.Entries);
        Assert.Contains(res.Rejected, x => x.Code == "size");
    }

    [Fact]
    public void KlasordeEnCokYuzDosya_Fazlasi_AtlanirVeUyariOlur()
    {
        using var dir = new RegistryUserDir();
        for (var i = 0; i < RegistryLoader.MaxFilesInDir + 5; i++) dir.Write($"d{i:D4}.soz.json", """{"schemaVersion":1,"entries":[]}""");
        var read = RegistryLoader.ReadUserDir(dir.Path);
        Assert.Equal(RegistryLoader.MaxFilesInDir, read.Sources.Count);
        Assert.Equal("d0000.soz.json", read.Sources[0].Name);        // ada göre ilk 500
        Assert.Equal("d0499.soz.json", read.Sources[^1].Name);
        var w = Assert.Single(read.Warnings);
        Assert.Equal("dir-limit", w.Code);
        Assert.Contains("500", w.Message);
        // CLI: uyarı var, hata yok → çıkış 0
        var (code, text) = RegistryCliTests.Run("check", "--dir", dir.Path);
        Assert.Equal(0, code);
        Assert.Contains("UYARI", text);
        Assert.Contains("Fazlası atlandı", text);
    }

    [Fact]
    public void HataSatirlarinda_DosyadakiDegerAynenBasilmaz_YalnizAlanAdiVeNeden()
    {
        const string gizliDeger = "SIFRE-ABC123-" + "GIZLI-DEGER-ASDFGH";
        var bad = new Dictionary<string, string>
        {
            // enum alanlarına gizli değer
            ["owner.soz.json"] = $$$"""{"schemaVersion":1,"entries":[{"id":"setting:a.b","kind":"setting","owner":"{{{gizliDeger}}}","title":"T","body":{}}]}""",
            ["state.soz.json"] = SettingFile("setting:a.c", entryPatchJson: $$"""{"state":"{{gizliDeger}}","stability":"{{gizliDeger}}","surfaces":["{{gizliDeger}}"],"since":"{{gizliDeger}}"}"""),
            ["izin.soz.json"] = SettingFile("setting:a.d", entryPatchJson: $$"""{"permissions":[{"cap":"{{gizliDeger}}","why":"x"}]}"""),
            ["kimlik.soz.json"] = SettingFile(gizliDeger + ":../x"),
            ["tur.soz.json"] = $$"""{"schemaVersion":1,"entries":[{"id":"{{gizliDeger}}:x","kind":"{{gizliDeger}}","owner":"user","title":"T"}]}""",
            ["kalip.soz.json"] = SettingFile("setting:a.e", $$"""{"schema":{"type":"string","pattern":"^x$"},"default":"{{gizliDeger}}"}"""),
            ["surum.soz.json"] = $$"""{"schemaVersion":"{{gizliDeger}}","entries":[]}""",
            ["json.soz.json"] = SettingFile("setting:a.f") + "  " + gizliDeger,
            ["alan.soz.json"] = SettingFile("setting:a.g", """{"fazlaAlan":1}"""),
            ["yinelenen.soz.json"] = """{"schemaVersion":1,"entries":[{"ornekAlan":1,"ornekAlan":2}]}""",
        };
        var res = RegistryLoader.Load(RegistryLoader.EmbeddedSources(), bad.Select(kv => Source(kv.Key, kv.Value)));
        var report = RegistryChecker.Check(res);
        var everything = string.Join("\n", res.Rejected.Concat(res.Warnings).Select(p => p.ToString())) + "\n" + report.Format();
        Assert.True(res.Rejected.Count >= bad.Count - 1, "beklenen reddedilme sayısı: " + res.Rejected.Count);
        Assert.DoesNotContain(gizliDeger, everything);
        Assert.DoesNotContain(gizliDeger[..14], everything);   // kısaltılmış parçası da yok
        // alan adı ve neden yine görünür
        Assert.Contains(res.Rejected, p => p.Source == "owner.soz.json" && p.Message.Contains("$.owner") && p.Message.Contains("izinli değerler listesinde yok"));
        Assert.Contains(res.Rejected, p => p.Source == "alan.soz.json" && p.Message.Contains("bilinmeyen alan: fazlaAlan"));
        Assert.Contains(res.Rejected, p => p.Source == "yinelenen.soz.json" && p.Message.Contains("ornekAlan"));
        Assert.Contains(res.Warnings, p => p.Source == "tur.soz.json" && p.Code == "unknown-kind" && p.Message.StartsWith("bilinmeyen tür."));
    }
}

public class RegistryCliTests
{
    internal static (int code, string text) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = RegistryCommands.Run(Args.Parse(args.Prepend("registry").Skip(1).ToArray(), new HashSet<string> { "json" }), o, e);
        return (code, o.ToString() + e.ToString());
    }

    [Fact]
    public void Check_OlmayanKlasor_Dosya_UncVeBosYol_AcikHataVeCikis1()
    {
        using var dir = new RegistryUserDir();
        var file = dir.Write("duz.txt", "x");
        foreach (var (arg, why) in new[]
        {
            (Path.Combine(dir.Path, "yok"), "klasör bulunamadı"),
            (file, "bir dosya"),
            (@"\\localhost\C$\x", "ağ yolları"),
            (@"\\.\NUL", "ağ yolları"),
            ("a|b", "geçersiz karakter"),
            ("", "boş"),
        })
        {
            var (code, text) = Run("check", "--dir=" + arg);
            Assert.True(code == 1, $"--dir={arg}: çıkış {code}\n{text}");
            Assert.Contains("--dir kullanılamıyor", text);
            Assert.Contains(why, text);
            Assert.Contains("KALDI", text);
            Assert.DoesNotContain("GEÇTİ", text);
            var (lcode, ltext) = Run("list", "--dir=" + arg);
            Assert.Equal(1, lcode);
            Assert.Contains(why, ltext);
        }
    }

    [Fact]
    public void Check_BosAmaVarOlanKlasor_Gecer_GecerliKayitliKlasor_Gecer()
    {
        using var dir = new RegistryUserDir();
        var (c0, t0) = Run("check", "--dir", dir.Path);
        Assert.Equal(0, c0);
        Assert.Contains($"{RegistryLoader.LoadEmbedded().Entries.Count} kayıt", t0);
        dir.Write("a.soz.json", SettingFile("setting:ornek.cli"));
        var (c1, t1) = Run("check", "--dir", dir.Path);
        Assert.Equal(0, c1);
        Assert.Contains($"{RegistryLoader.LoadEmbedded().Entries.Count + 1} kayıt", t1);
    }

    [Fact]
    public void Check_YinelenenAnahtarliDosya_ExitBir_GomuluKayitlarListelenmeyeDevamEder()
    {
        // Denetim E2: bu dosya CLI'yi "İç hata" (çıkış 2) ile düşürüyor ve hiçbir şey listelemiyordu.
        using var dir = new RegistryUserDir();
        dir.Write("dup.soz.json", SettingFile("setting:a.b").Replace("\"id\":\"setting:a.b\",", "\"id\":\"setting:a.b\",\"id\":\"setting:c.d\","));
        dir.Write("saglam.soz.json", SettingFile("setting:ornek.saglam"));
        var (code, text) = Run("check", "--dir", dir.Path);
        Assert.Equal(1, code);
        Assert.Contains("iki kez yazılmış", text);
        Assert.Contains("dup.soz.json", text);
        Assert.Contains($"{RegistryLoader.LoadEmbedded().Entries.Count + 1} kayıt", text);          // gömülü + sağlam dosya
        Assert.Contains("1 hata", text);
        var (lcode, ltext) = Run("list", "--dir", dir.Path, "--kind", "setting");
        Assert.Equal(0, lcode);
        Assert.Contains("setting:ornek.saglam", ltext);
        Assert.Contains("setting:ui.theme", ltext);
        Assert.Contains("1 kayıt reddedildi", ltext);
    }

    [Fact]
    public void Check_PahaliVeBozukRegex_Cokmez_Ve_ZamanAsimiYok()
    {
        using var dir = new RegistryUserDir();
        dir.Write("redos.soz.json", SettingFile("setting:a.b", """{"schema":{"type":"string","pattern":"^(a+)+$"},"default":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!"}"""));
        dir.Write("bozuk.soz.json", SettingFile("setting:a.c", """{"schema":{"type":"string","pattern":"("},"default":"x"}"""));
        var sw = Stopwatch.StartNew();
        var (code, text) = Run("check", "--dir", dir.Path);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"süre {sw.Elapsed}");
        Assert.Equal(1, code);
        Assert.DoesNotContain("İç hata", text);
        Assert.Contains("geçerli bir düzenli ifade değil", text);
        // ReDoS kalıbı doğrusal motorda sorunsuz: varsayılan "...!" kalıba uymadığı için normal kural hatası verir, çökmez
        Assert.Contains("varsayılan değer kendi şemasına uymuyor", text);
    }

    [Theory]
    [InlineData("check", true)]
    [InlineData("list", true)]
    [InlineData("CHECK", true)]
    [InlineData("approve", false)]
    [InlineData("yok", false)]
    [InlineData("check-all", false)]
    [InlineData("", false)]
    public void CliGuard_RegistryKomutu_AltKomutaBakilarakSaltOkunurSayilir(string sub, bool allowed)
    {
        var args = sub.Length == 0 ? new[] { "--json" } : new[] { sub, "--json" };
        Assert.Equal(allowed, CliGuard.AgentMayRun("registry", Args.Parse(args, new HashSet<string> { "json" })));
        // alt komutsuz ve bilinmeyen komut ajana kapalı kalır
        Assert.False(CliGuard.AgentMayRun("bilinmeyen", Args.Parse(new[] { "check" }, new HashSet<string>())));
    }
}

public class RegistrySettingsBindingTests
{
    private static JsonNode? J2(string s) => JsonNode.Parse(s);

    [Fact]
    public void Varsayilanlar_KayittanGelir_Ve_BosVeritabaniAyniVarsayilanlariVerir()
    {
        var load = FixedCatalog.Get;
        // d2m: çekirdek 33 ayar hâlâ aynı değerlerle var; kayıta eklenen her yeni ayar da varsayılanlar sözlüğüne girer (sayı sabit değil).
        Assert.True(SozSettings.Defaults.Count >= 33);
        Assert.Equal(load.Settings.Count(), SozSettings.Defaults.Count);
        foreach (var e in load.Settings)
        {
            var key = e.Setting!.LegacyKey ?? e.Id;
            Assert.True(JsonNode.DeepEquals(e.Setting.Default, SozSettings.Defaults[key]), key);
        }
        using var env = new TestEnv(copySamples: false);
        var s = SozSettings.Load(env.Svc.Db);
        Assert.Equal(300, WorkService.GateBudgetSeconds(s));
        Assert.Equal(3, WorkService.GateMaxAttempts(s));
        Assert.Equal("tr", s.Language);
        Assert.Equal(0.6, s.ExtractThreshold);
        Assert.Equal(2000, s.PacketBudget);
        Assert.Equal("Read,Edit,Write,MultiEdit,Glob,Grep,Bash", s.RunnerAllowedTools);
        Assert.Equal("claude", s.ExplainCommand);
        Assert.Equal(3, s.MaxParallelRuns);
        Assert.Equal(90, s.RunnerMaxMinutes);
    }

    [Fact]
    public void EskidenKalmisSinirDisiGateDegeri_OkunurkenKayitSinirinaKistirilir_Cokmez()
    {
        using var env = new TestEnv(copySamples: false);
        void Seed(string json)
        {
            using var c = env.Svc.Db.Open();
            SozSettings.Save(c, JsonNode.Parse(json)!.AsObject());
        }
        Seed("""{"gateBudgetSeconds":5,"gateMaxAttempts":3600}""");          // eski Validate bunları kabul ederdi
        var s = SozSettings.Load(env.Svc.Db);
        Assert.Equal(10, WorkService.GateBudgetSeconds(s));
        Assert.Equal(20, WorkService.GateMaxAttempts(s));
        Assert.Equal(10, (double)s.ToJson()["gateBudgetSeconds"]!);            // arayüz de kıstırılmış değeri görür
        Assert.Equal(20, (double)s.ToJson()["gateMaxAttempts"]!);
        Seed("""{"gateBudgetSeconds":99999,"gateMaxAttempts":0}""");
        s = SozSettings.Load(env.Svc.Db);
        Assert.Equal(3600, WorkService.GateBudgetSeconds(s));
        Assert.Equal(1, WorkService.GateMaxAttempts(s));
        Seed("""{"gateBudgetSeconds":45,"gateMaxAttempts":7}""");             // sınır içi dokunulmaz
        s = SozSettings.Load(env.Svc.Db);
        Assert.Equal(45, WorkService.GateBudgetSeconds(s));
        Assert.Equal(7, WorkService.GateMaxAttempts(s));
        // sayı olmayan eski değer varsayılana düşer
        Seed("""{"gateBudgetSeconds":"abc","gateMaxAttempts":null}""");
        s = SozSettings.Load(env.Svc.Db);
        Assert.Equal(300, WorkService.GateBudgetSeconds(s));
        Assert.Equal(3, WorkService.GateMaxAttempts(s));
        // kıstırma sayıları kodda yok: WorkService ve MaxParallelRuns/RunnerMaxMinutes kayıttan okur
        Seed("""{"maxParallelRuns":9,"runnerMaxMinutes":0}""");
        s = SozSettings.Load(env.Svc.Db);
        Assert.Equal(6, s.MaxParallelRuns);
        Assert.Equal(1, s.RunnerMaxMinutes);
    }

    [Fact]
    public void YediSikilasanAyar_EskidenKalmisUygunsuzDeger_VarsayilanaDuser_Cokmez()
    {
        using var env = new TestEnv(copySamples: false);
        using (var c = env.Svc.Db.Open())
            SozSettings.Save(c, JsonNode.Parse($$"""
                {"claudeRoot":5,"codexRoot":{"a":1},"dictationPath":true,"explainCommand":5,"judgeCommand":"{{new string('x', 2001)}}","capsuleX":"abc","capsuleY":1000000000}
                """)!.AsObject());
        var s = SozSettings.Load(env.Svc.Db);
        Assert.Null(s.ClaudeRoot);
        Assert.Null(s.CodexRoot);
        Assert.Null(s.ToJson()["dictationPath"]);
        Assert.Equal("claude", s.ExplainCommand);
        Assert.Equal("claude", s.Str("judgeCommand"));
        Assert.Null(s.ToJson()["capsuleX"]);
        Assert.Null(s.ToJson()["capsuleY"]);                 // sınır dışı sayı bu ayarlarda kıstırılmaz, varsayılana düşer
        // geçerli değerler aynen kalır
        using (var c = env.Svc.Db.Open())
            SozSettings.Save(c, JsonNode.Parse("""{"claudeRoot":"D:\\kayit","capsuleX":-120,"capsuleY":45,"explainCommand":"codex","dictationPath":null}""")!.AsObject());
        s = SozSettings.Load(env.Svc.Db);
        Assert.Equal("D:\\kayit", s.ClaudeRoot);
        Assert.Equal(-120, (double)s.ToJson()["capsuleX"]!);
        Assert.Equal(45, (double)s.ToJson()["capsuleY"]!);
        Assert.Equal("codex", s.ExplainCommand);
    }

    [Fact]
    public void BozukJsonDegeri_VeritabanindaOkunurken_VarsayilanaDuser()
    {
        using var env = new TestEnv(copySamples: false);
        using (var c = env.Svc.Db.Open())
        {
            SozSettings.SetInternal(c, "packetBudget", "{bozuk");
            SozSettings.SetInternal(c, "language", "xx");
            SozSettings.SetInternal(c, "ozelAnahtar", "{\"a\":1}");
        }
        var s = SozSettings.Load(env.Svc.Db);
        Assert.Equal(2000, s.PacketBudget);
        Assert.Equal("tr", s.Language);
        Assert.NotNull(s.ToJson()["ozelAnahtar"]);           // kayıtta olmayan anahtar olduğu gibi gelir
    }

    [Fact]
    public void PutSettings_ApiYolu_KayitSinirinaGoreKabulVeRetEder()
    {
        using var env = new TestEnv(copySamples: false);
        env.Svc.PutSettings(new JsonObject { ["gateBudgetSeconds"] = 10, ["capsuleX"] = -3, ["claudeRoot"] = env.ClaudeRoot });
        var ex = Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["gateBudgetSeconds"] = 9 }));
        Assert.Equal(400, ex.Status);
        Assert.Contains("10 ile 3600 arası tam sayı olmalı", ex.Message);   // d2m: sayaç ayarları tam sayı (d1f Y7)
        Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["gateMaxAttempts"] = 21 }));
        Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["capsuleY"] = 1.5 }));
        Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["explainCommand"] = null }));
        // kabuğun yazdığı biçim (tam sayı konum) çalışmaya devam eder
        var back = env.Svc.PutSettings(new JsonObject { ["capsuleX"] = 123, ["capsuleY"] = 45 });
        Assert.Equal(123, (double)back["capsuleX"]!);
    }

    [Fact]
    public void WorkingSeconds_KullanimDisi_AmaAyarCalismayaDevamEder()
    {
        var e = FixedCatalog.Get.SettingByLegacyKey("workingSeconds")!;
        Assert.Equal(RegistryStates.Deprecated, e.State);
        Assert.Contains("okumuyor", e.Deprecated!.Message!.Tr);
        Assert.True(SozSettings.Defaults.ContainsKey("workingSeconds"));
        Assert.Null(SozSettings.Validate(new JsonObject { ["workingSeconds"] = 120 }));
        using var env = new TestEnv(copySamples: false);
        Assert.Equal(120, (double)env.Svc.PutSettings(new JsonObject { ["workingSeconds"] = 120 })["workingSeconds"]!);
        Assert.True(RegistryChecker.Check(FixedCatalog.Get).Ok);
    }

    [Fact]
    public void RunnerAllowedTools_GuvenlikKapisi_K4_DosyadanGelenOnayBekler()
    {
        var b = FixedCatalog.Get.SettingByLegacyKey("runnerAllowedTools")!.Setting!;
        Assert.Equal("K4", b.Class);
        Assert.Equal("intersection-wins", b.Restrictive);
        Assert.False(b.ModeSettable);
        // dosyadan gelen K4 ayar onaysız etkin olmaz; yeni seçenek şemadan geçer
        var res = LoadUser(SettingFile("setting:ornek.liste", """{"schema":{"type":"string"},"default":"Read","class":"K4","securityGate":{"restrictive":"intersection-wins"},"group":"kural-kanit","card":"Kapı"}"""));
        Assert.Empty(res.Rejected);
        Assert.Equal(RegistryStates.Suggested, Assert.Single(res.Entries).State);
        // bilinmeyen seçenek hâlâ reddedilir
        Assert.NotEmpty(LoadUser(SettingFile("setting:ornek.liste2", """{"class":"K4","securityGate":{"restrictive":"herkes-kazanir"},"group":"kural-kanit","card":"Kapı"}""")).Rejected);
    }

    [Fact]
    public void YeniAyarEklemek_ArkaUctaTekKayitDosyasi_KodaDokunmadanDogrulaVarsayilanOku()
    {
        // Ölçüm (madde 11): sahte iki ayar tek bir kayıt dosyasıyla eklenir. Arka uçta başka hiçbir dosyaya dokunulmaz:
        // Validate, varsayılan, okunurken kıstırma ve API yolu hepsi kayıttan gelir.
        const string fake = """
            {"schemaVersion":1,"entries":[
             {"id":"setting:deneme.sahte-sayi","kind":"setting","owner":"fixed","title":"Sahte sayı","description":"Ölçüm için.","stability":"scaffold","since":"0.1",
              "contract":{"discover":"d","disable":"d","onError":"d","onNewTool":"d"},
              "body":{"schema":{"type":"integer","minimum":1,"maximum":5},"default":2,"class":"K2","group":"gelismis","card":"Deneme","order":1,"control":"number","legacyKey":"sahteSayi","clampOnRead":true}},
             {"id":"setting:deneme.sahte-secim","kind":"setting","owner":"fixed","title":"Sahte seçim","description":"Ölçüm için.","stability":"scaffold","since":"0.1",
              "contract":{"discover":"d","disable":"d","onError":"d","onNewTool":"d"},
              "body":{"schema":{"type":"string","enum":["a","b","c"]},"default":"b","class":"K1","group":"gelismis","card":"Deneme","order":2,"control":"select","legacyKey":"sahteSecim"}}
            ]}
            """;
        var cat = RegistryLoader.Load(RegistryLoader.EmbeddedSources().Append(Source("deneme.soz.json", fake)), Array.Empty<RegistrySource>());
        Assert.Empty(cat.Rejected);
        Assert.Equal(RegistryLoader.LoadEmbedded().Settings.Count() + 2, cat.Settings.Count());   // d2m: gömülü ayarlar + bu testin iki sahte ayarı
        Assert.True(RegistryChecker.Check(cat).Ok);                                             // denetim de kayıtla birlikte genişler

        // 1) Validate: sınır ve enum kayıttan, mesaj sınırı kayıttan söyler
        Assert.Null(SozSettings.Validate(new JsonObject { ["sahteSayi"] = 5, ["sahteSecim"] = "c" }, cat));
        Assert.Equal("sahteSayi: 1 ile 5 arası tam sayı olmalı", SozSettings.Validate(new JsonObject { ["sahteSayi"] = 6 }, cat));
        Assert.Equal("sahteSecim: a, b ya da c olmalı", SozSettings.Validate(new JsonObject { ["sahteSecim"] = "d" }, cat));
        Assert.Equal("Bilinmeyen ayar: sahteSayi", SozSettings.Validate(new JsonObject { ["sahteSayi"] = 3 }));   // gömülü paket bilmez: kayıt eklenmemiş

        // 2) Varsayılan ve okunurken düzeltme
        using var env = new TestEnv(copySamples: false);
        using (var c = env.Svc.Db.Open())
        {
            var s0 = SozSettings.Load(c, cat);
            Assert.Equal(2, s0.Num("sahteSayi"));
            Assert.Equal("b", s0.Str("sahteSecim"));
            SozSettings.Save(c, new JsonObject { ["sahteSayi"] = 99, ["sahteSecim"] = "zzz" });
            var s1 = SozSettings.Load(c, cat);
            Assert.Equal(5, s1.ClampedInt("sahteSayi"));                                       // clampOnRead: sınıra
            Assert.Equal("b", s1.Str("sahteSecim"));                                           // uygunsuz: varsayılana
        }
        // Sayım: arka uçta dokunulan dosya = 1 (Registry\kayit\ayarlar.soz.json'a iki giriş); kod dosyası = 0.
    }
}

public class RegistryEndpointTests
{
    [Fact]
    public void Build_GomuluKayitlar_UretilenAlanlarlaGelir_Ve_YolSizdirmaz()
    {
        var all = Soz.Server.Endpoints.RegistryEndpoints.Build(FixedCatalog.Get, null);
        Assert.Equal(RegistryLoader.LoadEmbedded().Entries.Count, (int)all["count"]!);   // d2m: sabit 41 yerine gömülü kayıt sayısı
        var settings = Soz.Server.Endpoints.RegistryEndpoints.Build(FixedCatalog.Get, "setting");
        Assert.Equal(FixedCatalog.Get.Settings.Count(), settings["entries"]!.AsArray().Count);
        Assert.Empty(settings["problems"]!.AsArray());
        var first = settings["entries"]![0]!.AsObject();
        foreach (var k in new[] { "id", "kind", "owner", "state", "title", "contract", "body", "origin", "sha256", "problems", "stability" }) Assert.True(first.ContainsKey(k), k);
        Assert.All(settings["entries"]!.AsArray(), e => { Assert.Equal("core", (string?)e!["origin"]); Assert.Matches("^[0-9a-f]{64}$", (string?)e["sha256"] ?? ""); });
        var wk = settings["entries"]!.AsArray().Single(e => (string?)e!["id"] == "setting:session.working-seconds")!;
        Assert.Equal("deprecated", (string?)wk["state"]);
    }

    [Fact]
    public void Build_KisiKayitlarinda_SirVeDosyaYolulariDisariVerilmez()
    {
        const string secret = "SIRRI-TOKEN-12345";
        using var dir = new RegistryUserDir();
        dir.Write("alt/yol.soz.json", $$$"""
            {"schemaVersion":1,"entries":[
             {"id":"setting:ornek.gizli","kind":"setting","owner":"user","title":"Gizli","description":"Dosya C:\\Users\\kemal\\gizli.txt içinde ve \\\\paylasimsunucusu91\\paylasim altında.",
              "permissions":[{"cap":"fs.read","scope":"C:\\Users\\kemal\\.claude","why":"okur"},{"cap":"net","scope":"api.ornek.com","why":"x"}],
              "contract":{"discover":"~/gizli klasörü","disable":"d","onError":"d","onNewTool":"d"},
              "body":{"schema":{"type":"string"},"default":"{{{secret}}}","class":"K1","group":"genel","card":"Gizli","order":1,"sensitive":true,"control":"secret"}},
             {"id":"setting:ornek.yol","kind":"setting","owner":"user","title":"Yol","body":{"schema":{"type":"string"},"default":"D:\\Programlar\\ajan.exe","class":"K1","group":"genel","card":"Gizli","order":2}}
            ]}
            """);
        dir.Write("alt/kotu.soz.json", "{ bozuk");
        var load = RegistryLoader.LoadEmbedded(dir.Path);
        var text = Soz.Server.Endpoints.RegistryEndpoints.Build(load, null).ToJsonString();
        Assert.DoesNotContain(secret, text);                         // sensitive ayarın varsayılanı verilmez
        Assert.DoesNotContain(dir.Path, text);                       // dosya yolu yok
        Assert.DoesNotContain(@"C:\\Users", text);
        Assert.DoesNotContain("demo", text);
        Assert.DoesNotContain("D:\\\\Programlar", text);
        Assert.DoesNotContain("paylasimsunucusu91", text);               // UNC paylaşım adı sızmaz (d2m: "sunucu" gömülü kayıt metinlerinde de geçer)
        Assert.DoesNotContain("~/gizli", text);
        Assert.DoesNotContain("alt/", text);                         // yalnız dosya adı
        Assert.Contains("(yol gizlendi)", text);
        Assert.Contains("kotu.soz.json", text);                      // dosya adı ve neden kalır
        var gizli = Soz.Server.Endpoints.RegistryEndpoints.Build(load, "setting")["entries"]!.AsArray().Single(e => (string?)e!["id"] == "setting:ornek.gizli")!;
        Assert.Equal("file", (string?)gizli["origin"]);              // "file:<yol>" değil
        Assert.Equal("api.ornek.com", (string?)gizli["permissions"]![1]!["scope"]);   // yol olmayan kapsam kalır
    }

    [Fact]
    public async Task Uc_GetRegistry_KindSuzgeciVeHataKodu()
    {
        using var env = new TestEnv(copySamples: false);
        await using var h = await SozServer.StartAsync(env.DataDir, 49371);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var all = JsonNode.Parse(await http.GetStringAsync("/api/registry"))!;
        Assert.Equal(RegistryLoader.LoadEmbedded().Entries.Count, (int)all["count"]!);   // d2m: sabit 41 yerine gömülü kayıt sayısı
        var conns = JsonNode.Parse(await http.GetStringAsync("/api/registry?kind=connector"))!;
        Assert.Superset(new HashSet<string> { "connector:claude", "connector:codex", "connector:hermes", "connector:opencode" }, conns["entries"]!.AsArray().Select(e => (string)e!["id"]!).ToHashSet());   // d2m: sabit 4 yerine bilinen kimlikler
        Assert.Equal("connector", (string?)conns["kind"]);
        var bad = await http.GetAsync("/api/registry?kind=hologram");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        // salt okunur: yazma yöntemleri kabul edilmez
        var post = await http.PostAsync("/api/registry", new StringContent("{}"));
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        // Host denetimi başka uçlarla aynı ara katmandan geçer
        var evil = new HttpRequestMessage(HttpMethod.Get, "/api/registry");
        evil.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(evil)).StatusCode);
    }
}
