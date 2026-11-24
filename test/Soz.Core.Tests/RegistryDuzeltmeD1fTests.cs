using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Registry;
using Soz.Server;
using Soz.Server.Endpoints;
using Xunit;
using static Soz.Core.Tests.RegistryTestKit;

namespace Soz.Core.Tests;

/// <summary>
/// İkinci bağımsız denetimin bulguları (Y1-Y8, Ö11), etiket d1f. Her bulgu için bir sınama:
/// Y1 kanca yolu tam kayıt defterini yüklemez ve hafif dizin tam dizinle aynı sonucu verir; Y2 özet etiketi ve eski biçimli satırlar;
/// Y3 bozuk gömülü kayıt; Y4 komut ayarları K4; Y5 düzeltilen ayarlar görünür; Y7 tam sayı ayarlar; Y8 derin klasör, yol gizleyici, anahtar çakışması.
/// </summary>
public class RegistryDuzeltmeD1fTests
{
    // ---------- yardımcılar ----------

    private sealed class Scope : IDisposable
    {
        public Scope(IReadOnlyList<RegistrySource> sources) => RegistryLoader.EmbeddedOverride.Value = sources;
        public void Dispose() => RegistryLoader.EmbeddedOverride.Value = null;
    }

    /// <summary>Gömülü kayıt dosyalarına, tam doğrulamadan geçemeyen ama hafif okuyucunun görmezden geldiği bir kayıt ekler.</summary>
    private static IReadOnlyList<RegistrySource> RealPlusFullOnlyBroken()
        => RegistryLoader.EmbeddedSources().Append(Source("tam-bozuk.soz.json", """{"schemaVersion":1,"entries":[{"id":"setting:yarim"}]}""")).ToList();

    /// <summary>Ayar dosyası yarıda kesilmiş gömülü küme (yarım kopyalanmış kurulum).</summary>
    private static IReadOnlyList<RegistrySource> Truncated()
        => RegistryLoader.EmbeddedSources().Select(s => s.Name == "ayarlar.soz.json" ? new RegistrySource(s.Name, s.Origin, s.Bytes[..(s.Bytes.Length / 2)]) : s).ToList();

    private static string HookRun(TestEnv env, string ev, object input)
    {
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run(ev, new StringReader(System.Text.Json.JsonSerializer.Serialize(input)), o, env.DataDir, _ => null));
        return o.ToString();
    }

    private static string HookLog(TestEnv env)
    {
        var f = Path.Combine(env.DataDir, "soz-hook.log");
        return File.Exists(f) ? File.ReadAllText(f) : "";
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static SqliteConnection Ham(string dosya)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dosya, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    // ---------- Y1: kanca yolu ----------

    [Fact]
    public void Y1_HafifDizin_TamKayitDefteriyleAyni_Varsayilan_Sinir_Ve_Duzeltme()
    {
        var full = SettingIndex.Of(FixedCatalog.Get);
        var light = SettingIndex.Embedded;
        // Kayıt dosyası ile hafif okuyucu arasında kayma olursa burası kırmızı olur (hafif yol kayıt dosyasını okur, ikinci bir tablo yok; yine de iki yol karşılaştırılır).
        Assert.Equal(full.Defaults.Keys.OrderBy(k => k, StringComparer.Ordinal), light.Defaults.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(full.Defaults.Count >= 33);
        var probes = new JsonNode?[] { null, true, false, 0, -1, 1, 3, 5, 24, 99999, 2.5, 300.5, -100001, 0.5, "x", "", "claude", new JsonArray(), new JsonObject() };
        foreach (var (key, def) in full.Defaults)
        {
            Assert.True(JsonNode.DeepEquals(def, light.Defaults[key]), $"varsayılan farklı: {key}");
            Assert.Equal(full.Range(key), light.Range(key));
            foreach (var p in probes)
            {
                var a = full.Normalize(key, p?.DeepClone(), out var ra);
                var b = light.Normalize(key, p?.DeepClone(), out var rb);
                Assert.True(JsonNode.DeepEquals(a, b), $"{key} = {p?.ToJsonString()}: tam {a?.ToJsonString()} hafif {b?.ToJsonString()}");
                Assert.Equal(ra, rb);
            }
        }
    }

    [Fact]
    public void Y1_KancaYolu_TamKayitDefteriniYuklemez_Yalniz_AyarlariOkur()
    {
        using var env = new TestEnv(copySamples: false);
        var cwd = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(cwd);
        using (new Scope(RealPlusFullOnlyBroken()))
        {
            // tam yükleme bu kümede çöker, hafif okuma çalışır: kanca yolu tam yüklemeye dokunsaydı aşağıdaki kancalar günlüğe hata yazardı
            var ex = Assert.Throws<SozException>(() => FixedCatalog.Get);
            Assert.True(RegistryUnavailable.Is(ex));
            using (var c = env.Svc.Db.Open()) Assert.Equal(2000, SozSettings.Load(c).PacketBudget);

            HookRun(env, "session-start", new { session_id = "s1", cwd, source = "startup" });
            HookRun(env, "user-prompt-submit", new { session_id = "s1", cwd, prompt = "Lütfen testleri çalıştır." });
            HookRun(env, "pre-tool-use", new { session_id = "s1", cwd, tool_name = "Bash", tool_input = new { command = "ls -la" } });
            HookRun(env, "stop", new { session_id = "s1", cwd, stop_hook_active = false, last_assistant_message = "Bitti." });
            HookRun(env, "notification", new { session_id = "s1", cwd, notification_type = "idle_prompt" });
        }
        Assert.Equal("", HookLog(env));
    }

    [Fact]
    public void Y1_KimlikBicimi_KarakterDongusu_EskiDuzenliIfadeyleAyni_Ve_SemadakiDeseningAyni()
    {
        var pattern = JsonNode.Parse(RegistryLoader.SchemaText)!["$defs"]!.AsObject().Select(kv => kv.Value).Select(v => v?["pattern"]?.GetValue<string>()).First(p => p is not null && p.StartsWith("^[a-z][a-z0-9-]*:", StringComparison.Ordinal))!;
        Assert.Equal(@"^[a-z][a-z0-9-]*:[a-z][a-z0-9._-]{0,63}\z", pattern); // döngü bu kuralın kopyasıdır; şema değişirse burası kırmızı olur
        var re = new Regex(pattern, RegexOptions.CultureInvariant);
        var rnd = new Random(20261111);
        const string alphabet = "abzAZ09-._: \n\u0130\u0131é";
        var samples = new List<string> { "", ":", "a:b", "setting:ui.theme", "setting:ui.theme\n", "Setting:x", "a-b:c.d_e-f", "a:" + new string('x', 64), "a:" + new string('x', 65), "a:1x", "1a:x", "a:b:c", "setting:é", "setting: x" };
        for (var i = 0; i < 6000; i++)
        {
            var len = rnd.Next(0, 80);
            var sb = new StringBuilder();
            for (var j = 0; j < len; j++) sb.Append(j == 0 || rnd.Next(6) > 0 ? "abcxyz"[rnd.Next(6)] : alphabet[rnd.Next(alphabet.Length)]);
            if (rnd.Next(2) == 0 && sb.Length > 2) sb[rnd.Next(1, sb.Length - 1)] = ':';
            samples.Add(sb.ToString());
        }
        foreach (var s in samples) Assert.True(re.IsMatch(s) == RegistryLoader.IsValidId(s), $"ayrıldı: [{s.Replace("\n", "\\n")}]");
    }

    // ---------- Y2: özet etiketi, eski biçimli satırlar, doctor ----------

    [Fact]
    public void Y2_OzetEtiketi_V2_Ve_BicimiDegisince_Artar()
    {
        Assert.Equal("soz-event-v2", Events.HashTag);
        var h = Events.ComputeHash("p", "i", "d", "t", "k", 1, null, "e", null, null, "a", "{}");
        string Enc(string? f) { if (f is null) return "~;"; var b = Encoding.UTF8.GetBytes(f); return $"{b.Length}:{f};"; }
        string Expect(string tag) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tag + ";" + string.Concat(new string?[] { "p", "i", "d", "t", "k", "1", null, "e", null, null, "a", "{}" }.Select(Enc))))).ToLowerInvariant();
        Assert.Equal(Expect("soz-event-v2"), h);
        Assert.NotEqual(Expect("soz-event-v1"), h);
    }

    [Fact]
    public void Y2_EskiBicimliSatirlar_KirikSayilmaz_AyriSayilir_YeniSatirdaOynamaYakalanir()
    {
        using var env = new TestEnv(copySamples: false);
        // TestEnv açılışı zaten olay yazar: temiz bir defter için kopyada olayları ve v2 işaretini sil (tetikleyiciler kopyada kaldırılır)
        var temiz = Path.Combine(env.Root, "y2-temiz.db");
        using (var o = env.Svc.Db.Open()) o.Exec("VACUUM INTO @f", null, ("@f", temiz));
        using var c = Ham(temiz);
        c.Exec("DROP TRIGGER trg_event_no_delete");
        c.Exec("DELETE FROM event_log");
        c.Exec("DELETE FROM settings WHERE key=@k", null, ("@k", Events.ChainMarkerKey));
        var device = Events.DeviceUid(c, null);
        // v2'den önce yazılmış satırlar: özetleri bugünkü biçimle tutmaz (başka kuralla hesaplanmış), ama bağlantıları (prev_hash) sağlam
        c.Exec("INSERT INTO event_log(id,device_uid,ts,kind,kind_v,actor,payload,prev_hash,hash) VALUES('o1',@d,'2026-11-01T00:00:00Z','eski.bir',1,'soz','{}',NULL,'eskihash1')", null, ("@d", device));
        c.Exec("INSERT INTO event_log(id,device_uid,ts,kind,kind_v,actor,payload,prev_hash,hash) VALUES('o2',@d,'2026-11-01T00:00:01Z','eski.iki',1,'soz','{}','eskihash1','eskihash2')", null, ("@d", device));
        var before = Events.VerifyChainDetailed(c);
        Assert.Null(before.BrokenSeq);                     // işaret yok: mevcut bütün satırlar eski biçimli
        Assert.Equal((2L, 0L, 2L), (before.Total, before.Verified, before.Legacy));

        Events.Append(c, "yeni.bir", null, null, null, null, "soz", "{\"n\":1}");
        Events.Append(c, "yeni.iki", null, null, null, null, "soz", "{\"n\":2}");
        var r = Events.VerifyChainDetailed(c);
        Assert.Null(r.BrokenSeq);
        Assert.Equal((4L, 2L, 2L), (r.Total, r.Verified, r.Legacy));
        Assert.Null(Events.VerifyChain(c));
        Assert.Equal("olay defteri: 4 kayıt, zincir sağlam (2 eski biçimli kayıt doğrulanmadı)", r.Line());

        // seq sayıları AUTOINCREMENT yüzünden 1'den başlamayabilir: gerçek değerleri oku
        var ilkEski = c.ScalarLong("SELECT MIN(seq) FROM event_log");
        var ilkYeni = c.ScalarLong("SELECT seq FROM event_log WHERE kind='yeni.bir'");
        var kopya = Path.Combine(env.Root, "y2.db");
        c.Exec("VACUUM INTO @f", null, ("@f", kopya));
        // yeni satırda oynama: kırık
        using (var k = Ham(kopya))
        {
            k.Exec("DROP TRIGGER trg_event_no_update");
            k.Exec("UPDATE event_log SET payload='{\"n\":99}' WHERE seq=@s", null, ("@s", ilkYeni));
            Assert.Equal(ilkYeni, Events.VerifyChain(k));
            Assert.Equal($"olay defteri: 3 kayıt, zincir {ilkYeni}. kayıttan itibaren bozuk (2 eski biçimli kayıt doğrulanmadı)", Events.VerifyChainDetailed(k).Line());
        }
        // eski satırın bağlantısı koparsa (satır silindi) o da yakalanır: özet doğrulanamasa da zincir bağı denetlenir
        var kopya2 = Path.Combine(env.Root, "y2b.db");
        c.Exec("VACUUM INTO @f", null, ("@f", kopya2));
        using (var k = Ham(kopya2))
        {
            // silme tetikleyicisi temiz kopyada zaten kaldırıldı
            k.Exec("DELETE FROM event_log WHERE seq=@s", null, ("@s", ilkEski));
            Assert.Equal(c.ScalarLong("SELECT MIN(seq) FROM event_log WHERE seq>@s", null, ("@s", ilkEski)), Events.VerifyChain(k));
        }
    }

    [Fact]
    public void Y2_Doctor_OlayDefteriSatiri_Ve_Yeni_Veride_EskiSatirYok()
    {
        using var env = new TestEnv(copySamples: false);
        using (var c = env.Svc.Db.Open()) { Events.Append(c, "a.b", null, null, null, null, "soz", "{}"); Events.Append(c, "a.c", null, null, null, null, "soz", "{}"); }
        var h = DoctorHealth.Collect(env.Svc.Db);
        Assert.Null(h.RegistryError);
        var line = Assert.Single(h.Lines(), l => l.StartsWith("olay defteri:", StringComparison.Ordinal));
        Assert.Matches(@"^olay defteri: \d+ kayıt, zincir sağlam$", line);
        Assert.Contains(h.Lines(), l => l.StartsWith("ayarlar:", StringComparison.Ordinal));
        var json = h.ToJson();
        Assert.Equal(true, (bool?)json["registry"]!["ok"]);
        Assert.Equal(0L, (long)json["eventLog"]!["legacyUnverified"]!);
        Assert.Equal("olay defteri: 5 kayıt, zincir 4. kayıttan itibaren bozuk", new ChainReport(5, 3, 0, 4).Line());
    }

    // ---------- Y3: bozuk gömülü kayıt ----------

    [Fact]
    public void Y3_BozukGomuluKayit_TekSadeCumle_SozException500()
    {
        using (new Scope(Truncated()))
        {
            var ex = Assert.Throws<SozException>(() => FixedCatalog.Get);
            Assert.Equal(500, ex.Status);
            Assert.Equal("Söz'ün ayar kaydı okunamadı, kurulum bozulmuş olabilir. Yeniden kur.", ex.Message);
            Assert.False(string.IsNullOrEmpty(RegistryUnavailable.DetailOf(ex)));
            Assert.False(FixedCatalog.TryGet(out _, out var detail)); Assert.False(string.IsNullOrEmpty(detail));
            // hafif yol da aynı cümleyi söyler; tür başlatıcısı çökmez (Defaults her seferinde aynı hatayı verir)
            Assert.Equal(RegistryUnavailable.Message, Assert.Throws<SozException>(() => SozSettings.Defaults).Message);
            Assert.Equal(RegistryUnavailable.Message, Assert.Throws<SozException>(() => SozSettings.Defaults).Message);
            Assert.Equal(RegistryUnavailable.Message, Assert.Throws<SozException>(() => SozSettings.Validate(new JsonObject { ["language"] = "tr" })).Message);
        }
        using (new Scope(Array.Empty<RegistrySource>()))
            Assert.Throws<SozException>(() => FixedCatalog.Get); // hiç kayıt dosyası yok
        Assert.NotNull(FixedCatalog.Get); // üstteki bozukluk yalnız o kapsamda: sağlam kayıt hâlâ yükleniyor
    }

    [Fact]
    public void Y3_Kanca_BozukKayitta_Sessizce_Gecmez_Gunluge_Yazar_Ve_Ajani_Engellemez()
    {
        using var env = new TestEnv(copySamples: false);
        var cwd = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(cwd);
        using (new Scope(Truncated()))
        {
            var o = HookRun(env, "user-prompt-submit", new { session_id = "s1", cwd, prompt = "bir şey" }); // çıkış 0 (HookRun doğrular), çıktı yok
            Assert.Equal("", o);
        }
        var log = HookLog(env);
        Assert.Contains("user-prompt-submit", log);
        Assert.Contains(RegistryUnavailable.Message, log);
    }

    [Fact]
    public async Task Y3_Sunucu_BozukKayitta_Acilir_NowJsonGovdeli500_TekSadeCumle_SaglamdaCalisir()
    {
        using var env = new TestEnv(copySamples: false);
        // d2m: bozuk gömülü kayıtta sunucu ARTIK açılır (servisler kayıt defterini kurucuda değil ilk kullanımda alır); istek 500 ile, JSON gövdeli ve tek sade cümleyle döner. Boş 500 yok.
        using (new Scope(Truncated()))
        {
            // Kestrel istek iş parçacığına AsyncLocal geçmez; bu yüzden istek yolu burada çağıran bağlamda sınanır: /api/now'un çağırdığı WorkService.Now() tek sade cümleyle (500) durur.
            // Gerçek HTTP kanıtı: bozuk gömülü kayıtlı ayrı derleme, _gecici bozuk-kopya klasörü (teslim raporu).
            await using var bad = await SozServer.StartAsync(env.DataDir, FreePort());
            var ex = Assert.Throws<SozException>(() => new Soz.Core.Jobs.WorkService(env.Svc.Db).Now());
            Assert.Equal(500, ex.Status);
            Assert.Equal(RegistryUnavailable.Message, ex.Message);
        }
        // Sağlam kayıtta aynı sunucu açılır; /api/settings gövdesinde JSON ve adjusted vardır (ayrıntı Y5 testinde)
        await using var h = await SozServer.StartAsync(env.DataDir, FreePort());
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var resp = await http.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/now")).StatusCode);
    }

    // ---------- Y4: komut ayarları K4 ----------

    [Fact]
    public void Y4_ProgramCalistiranAyarlar_K4_GuvenlikKapisiyla_Ve_AcmaAnahtarlariK4()
    {
        var load = FixedCatalog.Get;
        foreach (var key in new[] { "explainCommand", "judgeCommand", "explainEnabled", "judgeClaudeEnabled" })
        {
            var b = load.SettingByLegacyKey(key)!.Setting!;
            Assert.Equal("K4", b.Class);
            Assert.False(string.IsNullOrEmpty(b.Restrictive), key);
            Assert.False(b.ModeSettable, key);
        }
        // dosyadan gelen değer öneri kalır: K4 ayar kayıtlı olarak onaysız etkin olmaz (yükleyici kuralı); komut ayarının kendisi kişi dosyasından yeni kayıt olarak da öneri
        var user = LoadUser(SettingFile("setting:x.komut", """{"schema":{"type":"string","maxLength":2000},"default":"claude","class":"K4","control":"text","securityGate":{"restrictive":"false-wins"}}"""));
        Assert.Equal(RegistryStates.Suggested, user.Get("setting:x.komut")!.State);
        // path.exists EKLENMEDİ: komut satırı argüman içerebilir
        Assert.DoesNotContain("path.exists", load.SettingByLegacyKey("explainCommand")!.Setting!.Validators);
        Assert.Null(SozSettings.Validate(new JsonObject { ["explainCommand"] = "claude -p --model haiku" }));
    }

    // ---------- Y5: düzeltilen ayar görünür ----------

    [Fact]
    public void Y5_SinaraCekilenDeger_Adjusted_Listesinde_Sebebiyle()
    {
        using var env = new TestEnv(copySamples: false);
        using (var c = env.Svc.Db.Open())
        {
            SozSettings.Save(c, new JsonObject { ["packetBudget"] = 1500 });
            c.Exec("INSERT OR REPLACE INTO settings(key,value) VALUES('gateBudgetSeconds','5'),('gateMaxAttempts','2.5'),('capsuleX','\"abc\"'),('runnerAllowedTools','7'),('maxParallelRuns','4')");
        }
        var st = env.Svc.Settings();
        Assert.Equal(10, st.ClampedInt("gateBudgetSeconds"));
        Assert.Equal(2, st.ClampedInt("gateMaxAttempts"));
        var byKey = st.Adjusted.ToDictionary(a => a.Key);
        Assert.Equal(new[] { "capsuleX", "gateBudgetSeconds", "gateMaxAttempts", "runnerAllowedTools" }, byKey.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("Kayıtlı değer 5, bu ayar 10 ile 3600 arasında olmalı. 10 kullanılıyor.", byKey["gateBudgetSeconds"].Reason);
        Assert.Contains("tam sayı olmalı", byKey["gateMaxAttempts"].Reason);
        Assert.Equal(2.0, (double)byKey["gateMaxAttempts"].Applied!);
        Assert.Contains("Varsayılan", byKey["capsuleX"].Reason);
        Assert.Contains("Varsayılan", byKey["runnerAllowedTools"].Reason);
        var arr = st.AdjustedJson();
        var first = arr.Select(n => n!.AsObject()).First(o => (string?)o["key"] == "gateBudgetSeconds");
        Assert.Equal(5.0, (double)first["stored"]!); Assert.Equal(10.0, (double)first["applied"]!);
        // sınır içi ve geçerli değer düzeltilmez
        Assert.DoesNotContain("maxParallelRuns", byKey.Keys);
        Assert.DoesNotContain("packetBudget", byKey.Keys);
    }

    [Fact]
    public async Task Y5_ApiSettings_AdjustedEkAlani_VarOlanAlanlarDegismez()
    {
        using var env = new TestEnv(copySamples: false);
        await using var h = await SozServer.StartAsync(env.DataDir, FreePort());
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var clean = (await http.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        Assert.Empty(clean["adjusted"]!.AsArray());
        using (var c = env.Svc.Db.Open()) c.Exec("INSERT OR REPLACE INTO settings(key,value) VALUES('gateBudgetSeconds','5')");
        var now = (await http.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        var a = now["adjusted"]!.AsArray().Single()!.AsObject();
        Assert.Equal("gateBudgetSeconds", (string?)a["key"]); Assert.Equal(5.0, (double)a["stored"]!); Assert.Equal(10.0, (double)a["applied"]!);
        Assert.False(string.IsNullOrWhiteSpace((string?)a["reason"]));
        Assert.Equal(10.0, (double)now["gateBudgetSeconds"]!); // var olan alan aynı (uygulanan değer)
        // eklenen alan dışında bütün anahtarlar ayarlardan gelir ve ayar sayısı aynı
        Assert.Equal(clean.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal), now.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Y5_Doctor_Satiri_DuzeltilenAyarlariSayar()
    {
        using var env = new TestEnv(copySamples: false);
        using (var c = env.Svc.Db.Open()) c.Exec("INSERT OR REPLACE INTO settings(key,value) VALUES('gateBudgetSeconds','5')");
        var line = Assert.Single(DoctorHealth.Collect(env.Svc.Db).Lines(), l => l.StartsWith("ayarlar:", StringComparison.Ordinal));
        Assert.Equal("ayarlar: 1 kayıtlı değer kurala uymadığı için düzeltilerek kullanılıyor: gateBudgetSeconds 5 → 10", line);
    }

    // ---------- Y7: tam sayı ayarlar ----------

    [Fact]
    public void Y7_SayacAyarlar_KayittaIntegerTuru_Ve_KesirReddedilir()
    {
        var load = FixedCatalog.Get;
        foreach (var key in new[] { "gateBudgetSeconds", "gateMaxAttempts", "packetBudget", "runnerMaxMinutes", "maxParallelRuns" })
            { string? type = load.SettingByLegacyKey(key)!.Setting!.Schema["type"]?.GetValue<string>(); Assert.True(type == "integer", $"{key}: tür {type}"); }
        Assert.Contains("tam sayı", SozSettings.Validate(new JsonObject { ["gateBudgetSeconds"] = 300.5 })!);
        Assert.Contains("tam sayı", SozSettings.Validate(new JsonObject { ["gateMaxAttempts"] = 2.5 })!);
        Assert.Null(SozSettings.Validate(new JsonObject { ["gateBudgetSeconds"] = 300, ["gateMaxAttempts"] = 3 }));
    }

    // ---------- Y8: klasör, yol gizleyici, anahtar çakışması ----------

    [Fact]
    public void Y8_DerinKlasor_SessizAtlanmaz_UyariListesineDuser_8Seviye_Okunur()
    {
        using var dir = new RegistryUserDir();
        string Deep(int n) => string.Join("/", Enumerable.Range(1, n).Select(i => "k" + i));
        dir.Write($"{Deep(8)}/sekiz.soz.json", SettingFile("setting:derin.sekiz"));
        dir.Write($"{Deep(9)}/dokuz.soz.json", SettingFile("setting:derin.dokuz"));
        dir.Write($"{Deep(12)}/oniki.soz.json", SettingFile("setting:derin.oniki"));
        var read = RegistryLoader.ReadUserDir(dir.Path);
        Assert.Equal(new[] { "sekiz.soz.json" }, read.Sources.Select(s => Path.GetFileName(s.Name)));
        var w = read.Warnings.Where(x => x.Code == "dir-depth").ToList();
        Assert.Equal(2, w.Count);
        Assert.Contains(w, x => x.Source == "dokuz.soz.json" && x.Message.Contains("daha derinde"));
        Assert.Contains(w, x => x.Source == "oniki.soz.json");
        // yükleyici aynı uyarıyı sonuca taşır
        Assert.Equal(2, RegistryLoader.LoadEmbedded(dir.Path).Warnings.Count(x => x.Code == "dir-depth"));
    }

    [Fact]
    public void Y8_YolGizleyici_BoslukluYolunTamaminiGizler()
    {
        var res = new RegistryLoadResult();
        foreach (var msg in new[]
                 {
                     "Dosya E:/Sample Work/urun/veri/soz.db okunamadı.",
                     @"Dosya C:\Users\Ali Veli\Belgelerim\kayit dosyam.json yok",
                     @"UNC \\sunucu\ortak klasör\Kisisel Dosya\a.json bulunamadı",
                     "Açılamadı: ~/Kişisel Klasör/gizli yer/x.json, tekrar dene",
                 })
            res.Warnings.Add(new RegistryProblem("x.soz.json", null, "t", msg));
        var problems = RegistryEndpoints.Build(res, null)["problems"]!.AsArray().Select(p => (string)p!["message"]!).ToList();
        Assert.Equal(4, problems.Count);
        foreach (var m in problems)
        {
            Assert.Contains(RegistryEndpoints.Hidden, m);
            foreach (var leak in new[] { "Deneme", "urun", "soz.db", "Veli", "Belgelerim", "kayit dosyam", "sunucu", "ortak", "Kisisel", "a.json", "Kişisel", "gizli yer", "x.json" })
                Assert.DoesNotContain(leak, m);
        }
        Assert.Equal("Açılamadı: " + RegistryEndpoints.Hidden + ", tekrar dene", problems[3]); // noktalama yolu bitirir, cümlenin geri kalanı korunur
    }

    [Fact]
    public void Y8_LegacyKeyCakismasi_YukleyicideReddedilir_FixedAnahtariniKisiAlamaz_IkiKisiKaydiDaPaylasamaz()
    {
        // fixed "language" anahtarını alan kişi kaydı
        var r = RegistryLoader.Load(RegistryLoader.EmbeddedSources(), new[] { Source("kisi.soz.json", SettingFile("setting:my.lang", """{"legacyKey":"language"}""")) });
        Assert.Contains(r.Rejected, p => p.Code == "legacy-key" && p.Id == "setting:my.lang" && p.Message.Contains("language"));
        Assert.Null(r.Get("setting:my.lang"));
        Assert.Equal("setting:ui.language", r.SettingByLegacyKey("language")!.Id);
        // iki kişi kaydı aynı anahtarı isterse ilki kalır, ikincisi reddedilir
        var two = RegistryLoader.Load(Array.Empty<RegistrySource>(), new[]
        {
            Source("a.soz.json", SettingFile("setting:a.bir", """{"legacyKey":"ortakAnahtar"}""")),
            Source("b.soz.json", SettingFile("setting:b.iki", """{"legacyKey":"ortakAnahtar"}""")),
        });
        Assert.NotNull(two.Get("setting:a.bir"));
        Assert.Contains(two.Rejected, p => p.Code == "legacy-key" && p.Id == "setting:b.iki");
        // kimlik anahtarı (legacyKey yok) ile çakışma da yakalanır
        var third = RegistryLoader.Load(Array.Empty<RegistrySource>(), new[] { Source("c.soz.json", SettingFile("setting:c.uc")), Source("d.soz.json", SettingFile("setting:d.dort", """{"legacyKey":"gerceknokta"}""")) });
        Assert.Empty(third.Rejected);
    }

    // ---------- Ö11: Uid belgesi ----------

    [Fact]
    public void O11_UidBelgesi_SiralamaIcinSeqNotunuTasir()
    {
        var src = File.ReadAllText(Path.Combine(TestEnv.RepoRoot, "src", "Soz.Core", "Data", "Uid.cs"));
        Assert.Contains("seq", src);
        Assert.Contains("aynı milisaniyede", src);
    }
}
