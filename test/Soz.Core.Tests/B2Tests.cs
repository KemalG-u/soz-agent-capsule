using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Agents;
using Soz.Core.Data;
using Soz.Core.Discovery;
using Soz.Core.Feed;
using Soz.Core.Guard;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Registry;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>b2: olay defteri okuma ucu ("Ne oldu"), ajan hazırlığında kapalı araçlar, keşif nedeni, kapalı ajan önerisinin kendiliğinden açılmaması, metin temizliği.</summary>
public class B2Tests
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    private static string FakeK1 => Path.Combine(TestEnv.RepoRoot, "test", "sahte-claude", "sahte-claude-k1.mjs");
    private static string FakeCodex => Path.Combine(TestEnv.RepoRoot, "test", "sahte-codex", "sahte-codex.mjs");
    private static string FakeHermes => Path.Combine(TestEnv.RepoRoot, "test", "sahte-hermes", "sahte-hermes.mjs");

    private static async Task<(HttpStatusCode code, JsonNode? body)> Call(HttpClient http, string token, HttpMethod m, string url, object? body = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (token.Length > 0) req.Headers.Add("X-Soz-Token", token);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    private static DateTime T0 => new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ 1. olay defteri okuma
    private static (long pid, long jobId) SeedFeed(TestEnv env)
    {
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(NodeSample.Create(env));
        var job = w.CreateJob(pid, "Giriş sayfasındaki yazım hatalarını düzelt");
        using var c = env.Svc.Db.Open();
        GuardStore.EnsureTables(c);
        var minute = 0;
        void At() => Time.UtcNow = () => T0.AddMinutes(++minute);
        At(); Live.Emit(c, Live.Job, pid, new { id = job.Id, state = "calisiyor" });
        At(); Live.Emit(c, Live.Proof, pid, new { runId = 1, projectId = pid, jobId = job.Id, phase = "check", label = "x", status = "passed" }); // ara adım: satır olmaz
        At(); GuardStore.AddEvent(c, pid, null, "Bash", "rm -rf ../baska token=s" + "k-FAKEKEY1234567890", null, "deny", "kural");
        At(); Events.Append(c, "registry.approved", "registry", null, null, null, "kisi", new JsonObject { ["id"] = "connector:hermes" }.ToJsonString());
        At(); Events.Log(c, "hook_pulse", pid, "{}");                                         // nabız: satır olmaz
        At(); Events.Log(c, "kimsenin_bilmedigi_tur", pid, "{}");                              // bilinmeyen tür: satır olmaz
        At(); Live.Emit(c, Live.Job, pid, new { id = job.Id, state = "gecti" });
        At(); Events.Append(c, "migration.applied", null, null, null, null, "soz", new JsonObject { ["from"] = 13, ["to"] = 14 }.ToJsonString());
        return (pid, job.Id);
    }

    [Fact]
    public void Olay_defteri_zaman_sirasiyla_sade_cumlelerle_okunur_siz_ve_gurultu_yok()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, jobId) = SeedFeed(env);
        using var c = env.Svc.Db.Open();
        var page = EventFeed.Read(c, 50, null, null);

        // yeniden eskiye, zaman damgaları azalan
        Assert.Equal(page.Items.Select(x => x.At).OrderByDescending(x => x, StringComparer.Ordinal).ToList(), page.Items.Select(x => x.At).ToList());
        Assert.DoesNotContain(page.Items, x => x.Kind is "hook_pulse" or "kimsenin_bilmedigi_tur");
        Assert.DoesNotContain(page.Items, x => x.Sentence.Contains("kontrol"));
        Assert.Contains(page.Items, x => x.Group == EventFeed.GSystem && x.Sentence.StartsWith("Veri dosyası yeni sürüme güncellendi"));
        var bitti = page.Items.Single(x => x.Sentence.Contains("deneme sonucuyla bitti"));
        Assert.Equal(jobId, bitti.JobId);
        Assert.Equal(EventFeed.GJob, bitti.Group);
        Assert.Equal("İş", bitti.GroupLabel);
        Assert.StartsWith("«Giriş sayfasındaki", bitti.Sentence);
        Assert.Equal(pid, bitti.ProjectId);
        Assert.False(string.IsNullOrEmpty(bitti.Folder));
        Assert.Contains(page.Items, x => x.Sentence == "«Giriş sayfasındaki yazım hatalarını düzelt» işi çalışmaya başladı.");

        // koruma: bekçi kaydı, sır maskeli
        var guard = page.Items.Single(x => x.Group == EventFeed.GGuard);
        Assert.StartsWith("Bekçi bir işlemi durdurdu", guard.Sentence);
        Assert.DoesNotContain("FAKEKEY", guard.Sentence);

        // ayar grubu: kayıt onayı
        var ayar = page.Items.Single(x => x.Group == EventFeed.GSetting);
        Assert.Equal("Hermes Agent kaydını onayladın.", ayar.Sentence);

        // kişiye görünen hiçbir cümlede noktalı virgül, uzun tire ya da jargon yok
        foreach (var l in page.Items)
        {
            Assert.DoesNotContain(";", l.Sentence);
            Assert.DoesNotContain("—", l.Sentence);
            Assert.DoesNotMatch("(?i)kanca|hakem|bağlayıcı|payload", l.Sentence);
        }
    }

    [Fact]
    public void Olay_defteri_sayfalar_tur_suzgeci_calisir_gecersiz_tur_400()
    {
        using var env = new TestEnv(copySamples: false);
        SeedFeed(env);
        using var c = env.Svc.Db.Open();
        var all = EventFeed.Read(c, 100, null, null);
        Assert.True(all.Items.Count >= 5);

        var p1 = EventFeed.Read(c, 2, null, null);
        Assert.Equal(2, p1.Items.Count);
        Assert.True(p1.HasMore);
        Assert.NotNull(p1.Next);
        var seen = p1.Items.Select(x => x.Id).ToList();
        var next = p1.Next;
        while (next is not null)
        {
            var p = EventFeed.Read(c, 2, next, null);
            seen.AddRange(p.Items.Select(x => x.Id));
            next = p.Next;
        }
        Assert.Equal(all.Items.Select(x => x.Id).ToList(), seen);   // sayfalar birleşince tam liste, tekrar ve kayıp yok

        var onlyGuard = EventFeed.Read(c, 50, null, "guard");
        Assert.All(onlyGuard.Items, x => Assert.Equal("guard", x.Group));
        Assert.Single(onlyGuard.Items);
        Assert.Equal(400, Assert.Throws<SozException>(() => EventFeed.Read(c, 10, null, "sistem-disi")).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => EventFeed.Read(c, 10, "dün", null)).Status);
    }

    [Fact]
    public void Olay_defteri_geri_alma_cumlesi_geri_gelen_ve_kaldirilan_dosyalari_ayri_soyler()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var minute = 0;
        void At() => Time.UtcNow = () => T0.AddMinutes(++minute);
        At(); Events.Append(c, "undo.applied", "undo", null, null, null, "kisi", new JsonObject { ["restored"] = 0, ["removed"] = 1 }.ToJsonString());
        At(); Events.Append(c, "undo.applied", "undo", null, null, null, "kisi", new JsonObject { ["restored"] = 2, ["removed"] = 1 }.ToJsonString());
        var cumleler = EventFeed.Read(c, 50, null, null).Items.Where(x => x.Kind == "undo.applied").Select(x => x.Sentence).ToList();
        Assert.Contains(cumleler, s => s.Contains("1 dosya kaldırıldı") && !s.Contains("0 dosya"));
        Assert.Contains(cumleler, s => s.Contains("2 dosya eski haline döndü") && s.Contains("1 dosya kaldırıldı"));
        Assert.DoesNotContain(cumleler, s => s.Contains(';'));
    }

    [Fact]
    public void Olay_defteri_bos_veride_bos_liste_verir()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var page = EventFeed.Read(c, 50, null, null);
        Assert.False(page.HasMore);
        Assert.Null(page.Next);
        Assert.All(page.Items, x => Assert.Equal(EventFeed.GSystem, x.Group)); // yalnız açılışta yazılmış sistem olayları olabilir
    }

    [Fact]
    public async Task Http_events_ucu_salt_okunur_ve_ajan_hazirligi_kapali_araclari_da_listeler()
    {
        using var env = new TestEnv(copySamples: false);
        AgentProbe.ClearCache();
        SeedFeed(env);
        var cfg = Path.Combine(env.Root, "claude-cfg"); Directory.CreateDirectory(cfg);
        var codexCfg = Path.Combine(env.Root, "codex-cfg"); Directory.CreateDirectory(codexCfg);
        var eskiC = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"); var eskiX = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", cfg); Environment.SetEnvironmentVariable("CODEX_HOME", codexCfg);
        try
        {
            await using var h = await SozServer.StartAsync(env.DataDir, FreePort());
            using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
            // sahte araç yolları (gerçek araçlar hiç çalıştırılmaz)
            Assert.Equal(HttpStatusCode.OK, (await Call(http, h.Token, HttpMethod.Put, "/api/settings", new { claudePath = FakeK1, codexPath = FakeCodex })).code);

            var ev = JsonNode.Parse(await http.GetStringAsync("/api/events?limit=3"))!;
            Assert.Equal(3, ev["items"]!.AsArray().Count);
            Assert.True((bool)ev["hasMore"]!);
            Assert.NotNull((string?)ev["next"]);
            Assert.Equal(new[] { "job", "guard", "setting", "system" }, ev["groups"]!.AsArray().Select(g => (string)g!["id"]!).ToArray());
            var next = Uri.EscapeDataString((string)ev["next"]!);
            var ev2 = JsonNode.Parse(await http.GetStringAsync($"/api/events?limit=50&before={next}&kind=job"))!;
            Assert.All(ev2["items"]!.AsArray(), x => Assert.Equal("job", (string?)x!["group"]));
            Assert.Equal(400, (int)(await http.GetAsync("/api/events?kind=olmayan")).StatusCode);
            // yazma ucu yok
            Assert.True((await Call(http, h.Token, HttpMethod.Post, "/api/events", new { })).code is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);

            // hazırlık: beş araç listelenir, kapatılanlar closed görünür
            RegistryHost.Revoke(env.Svc.Db, "connector:hermes");
            RegistryHost.Revoke(env.Svc.Db, "connector:opencode");
            var ready = JsonNode.Parse(await http.GetStringAsync("/api/agents/ready"))!;
            var agents = ready["agents"]!.AsArray();
            Assert.Equal(new[] { "claude", "codex", "hermes", "opencode", "antigravity" }, agents.Select(a => (string)a!["id"]!).ToArray());
            foreach (var id in new[] { "hermes", "opencode" })
            {
                var a = agents.Single(x => (string?)x!["id"] == id)!;
                Assert.Equal("closed", (string?)a["status"]);
                Assert.Equal("Tanışma sayfasında onaylayınca açılır.", (string?)a["sentence"]);
                Assert.Equal("unknown", (string?)a["login"]);
                Assert.False((bool)a["canStart"]!);
                Assert.Null(a["version"]);
            }
            Assert.NotEqual("closed", (string?)agents.Single(x => (string?)x!["id"] == "claude")!["status"]);

            // Hermes onaylanır ve sahte betiğe yönlendirilir: artık açık, sürüm bakışı yapılır
            RegistryHost.Approve(env.Svc.Db, "connector:hermes");
            Assert.Equal(HttpStatusCode.OK, (await Call(http, h.Token, HttpMethod.Put, "/api/settings", new { hermesPath = FakeHermes })).code);
            var ready2 = JsonNode.Parse(await http.GetStringAsync("/api/agents/ready"))!["agents"]!.AsArray();
            var herm = ready2.Single(x => (string?)x!["id"] == "hermes")!;
            Assert.NotEqual("closed", (string?)herm["status"]);
            Assert.Equal("closed", (string?)ready2.Single(x => (string?)x!["id"] == "opencode")!["status"]);
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", eskiC); Environment.SetEnvironmentVariable("CODEX_HOME", eskiX); AgentProbe.ClearCache(); }
    }

    // ------------------------------------------------------------------ 3. keşif nedeni
    [Fact]
    public void Kesif_nedeni_gercek_dayanaktan_uretilir_dayanagi_olmayan_oneri_cikmaz()
    {
        using var t = new DiscoveryDb();
        var p = DiscoveryDb.P with { ShadowMode = false, Threshold = 0.05 };
        // tek kullanım (aynı saat diliminde): eskiden "Genelde bu gün ve saatlerde" diye çıkıyordu
        UsageStore.Record(t.C, "setting:ui.theme", "pA", DiscoveryDb.M(0, 10), p);
        // proje içinde üç kez
        for (var d = 0; d < 3; d++) UsageStore.Record(t.C, "skill:fatura", "pA", DiscoveryDb.M(d, 15), p);
        // başka projede, aynı gün ve saat diliminde iki kez (proje dayanağı yok, zaman dayanağı var)
        for (var d = 0; d < 2; d++) UsageStore.Record(t.C, "skill:rapor", "pB", DiscoveryDb.M(d * 7, 10), p);

        var r = SuggestionService.Suggest(t.C, "pA", DiscoveryDb.M(14, 10), p);
        Assert.DoesNotContain(r.Items, s => s.ObjectUid == "setting:ui.theme");       // dayanağı yok: öneri çıkmaz
        var fatura = r.Items.SingleOrDefault(s => s.ObjectUid == "skill:fatura");
        var rapor = r.Items.SingleOrDefault(s => s.ObjectUid == "skill:rapor");
        if (fatura is not null) Assert.Equal("Bu projede 3 kez kullandın.", fatura.Reason);
        Assert.NotNull(rapor);
        Assert.Equal("time", rapor!.ReasonCode);
        Assert.Contains("2 kez", rapor.Reason);
        Assert.Contains("Pazartesi", rapor.Reason);                                  // 2026-03-02 pazartesi, M(14) de pazartesi
        Assert.Contains("09:00 ile 12:00", rapor.Reason);                      // üç saatlik blok
        Assert.All(r.Items, s => { Assert.DoesNotContain(";", s.Reason); Assert.NotEqual("Genelde bu gün ve saatlerde kullanıyorsun.", s.Reason); });
        Assert.True(r.Items.Select(s => s.Reason).Distinct().Count() == r.Items.Count);   // her nedenin kendi sayısı var, hepsi aynı cümle değil
    }

    [Fact]
    public void Kesif_genel_neden_toplam_sayiyi_ve_son_gunu_soyler()
    {
        using var t = new DiscoveryDb();
        var p = DiscoveryDb.P with { ShadowMode = false, Threshold = 0.01 };
        UsageStore.Record(t.C, "skill:eski", null, DiscoveryDb.M(0, 9), p);
        UsageStore.Record(t.C, "skill:eski", null, DiscoveryDb.M(1, 21), p);
        var r = SuggestionService.Suggest(t.C, null, DiscoveryDb.M(3, 16), p);
        var s = r.Items.Single(x => x.ObjectUid == "skill:eski");
        Assert.Equal("recent", s.ReasonCode);
        Assert.Equal("Toplam 2 kez kullandın, en son 2 gün önce.", s.Reason);
    }

    // ------------------------------------------------------------------ 5. ApproveSetup
    [Fact]
    public void ApproveSetup_checkIds_verilmezse_ajanin_onerdigi_kapali_olcutu_acmaz_acik_kabulle_acar()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(NodeSample.Create(env));
        long agentCheck, normalCheck;
        using (var c = env.Svc.Db.Open())
        {
            agentCheck = CheckStore.Insert(c, null, pid, null, "command", "Ajanın yazdığı deneme geçsin (npm test)", new JsonObject { ["cmd"] = "npm test" }, 60, false, CheckStore.AgentTestOrigin);
            normalCheck = CheckStore.Insert(c, null, pid, null, "command", "Başka bir komut", new JsonObject { ["cmd"] = "node -v" }, 60, false, "suggest");
        }
        var r = w.ApproveSetup(pid);                                                  // checkIds yok
        Assert.False(r.Checks.Single(x => x.Id == agentCheck).Enabled);               // yardımcı önerisi kendiliğinden açılmadı
        Assert.True(r.Checks.Single(x => x.Id == normalCheck).Enabled);               // Söz'ün kendi önerisi açıldı

        var r2 = w.ApproveSetup(pid, new[] { agentCheck });                           // kişi açıkça kabul eder
        Assert.True(r2.Checks.Single(x => x.Id == agentCheck).Enabled);
    }

    // ------------------------------------------------------------------ 4. metin temizliği
    [Fact]
    public void Kayit_metinlerinde_noktali_virgul_ve_jargon_yok()
    {
        var dir = Path.Combine(TestEnv.RepoRoot, "src", "Soz.Core", "Registry", "kayit");
        var bad = new List<string>();
        void Walk(JsonNode? n, string path)
        {
            switch (n)
            {
                case JsonValue v when v.TryGetValue<string>(out var s) && s.Contains(' '):
                    if (System.Text.RegularExpressions.Regex.IsMatch(s, @";(\s|$)")) bad.Add($"{path}: noktalı virgül");
                    if (s.Contains('—') || s.Contains('–')) bad.Add($"{path}: uzun tire");
                    if (System.Text.RegularExpressions.Regex.IsMatch(s, @"(?i)\bkanca|\bhakem\b|güvenlik kapı|bağlayıcı|arka ucundaki")) bad.Add($"{path}: jargon");
                    break;
                case JsonArray a: for (var i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]"); break;
                case JsonObject o: foreach (var (k, x) in o) if (k != "keywords") Walk(x, $"{path}.{k}"); break;
            }
        }
        foreach (var f in Directory.GetFiles(dir, "*.json")) Walk(JsonNode.Parse(File.ReadAllText(f)), Path.GetFileName(f));
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(20)));
    }
}
