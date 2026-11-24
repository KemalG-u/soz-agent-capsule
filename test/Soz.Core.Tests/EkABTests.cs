using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Morning;
using Soz.Core.Proof;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>EK A (düz cümleyle ölçüt) ve EK B (sessiz ölü kanca olmasın).</summary>
public class EkABTests
{
    private static string J(object o) => JsonSerializer.Serialize(o);

    private static string Hook(TestEnv env, string ev, string stdin)
    {
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run(ev, new StringReader(stdin), o, env.DataDir, _ => null));
        return o.ToString();
    }

    // ------------------------------------------------------------------ EK A
    [Fact]
    public void EkA_cumleler_test_varsa_acik_yoksa_gri_ve_neden_yazili()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var node = w.EnsureProjectByPath(NodeSample.Create(env));
        var bosDir = Path.Combine(env.Root, "testsiz");
        Directory.CreateDirectory(bosDir);
        var bos = w.EnsureProjectByPath(bosDir);

        var a = w.CheckTemplateCatalog(node);
        Assert.True(a.HasTests);
        Assert.Null(a.Hint);
        var t = a.Templates.Single(x => x.Id == "tests");
        Assert.True(t.Available);
        Assert.Equal("npm test", t.DetectedCmd);
        Assert.Equal(new[] { "tests", "build", "url-opens", "page-text", "file-exists", "file-text", "screenshot" }, a.Templates.Select(x => x.Id).Take(7)); // eski yedi cümle aynı sırada; yenileri (d2a) kayıt defterinden gelir ve ardından sıralanır

        var b = w.CheckTemplateCatalog(bos);
        Assert.False(b.HasTests);
        Assert.Equal(CheckTemplates.NoTestsHint, b.Hint);
        var tb = b.Templates.Single(x => x.Id == "tests");
        Assert.False(tb.Available);
        Assert.Contains("test komutu bulamadım", tb.Reason);
        Assert.False(b.Templates.Single(x => x.Id == "build").Available);
        Assert.All(b.Templates.Where(x => x.Id is not ("tests" or "build")), x => Assert.True(x.Available));
        Assert.Equal("http://localhost:3000", b.Templates.Single(x => x.Id == "url-opens").Fields.Single().Default);

        // Gri cümle seçilemez; boş/yanlış değerler reddedilir.
        Assert.Equal(409, Assert.Throws<SozException>(() => w.AddCheckFromTemplate(bos, null, "tests", null, null, true)).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.AddCheckFromTemplate(bos, null, "page-text", new JsonObject { ["url"] = "http://localhost:3000" }, null, true)).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.AddCheckFromTemplate(bos, null, "url-opens", new JsonObject { ["url"] = "ftp://x" }, null, true)).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.AddCheckFromTemplate(bos, null, "file-exists", new JsonObject { ["path"] = "../disari.txt" }, null, true)).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.AddCheckFromTemplate(bos, null, "yok-boyle", null, null, true)).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.AddCheckFromTemplate(bos, null, "file-text", new JsonObject { ["path"] = "a.txt", ["text"] = new string('x', 201) }, null, true)).Status);

        // Cümle → doğru tür ve spec, etiket kullanıcının cümlesi, varsayılan açık.
        var c1 = w.AddCheckFromTemplate(node, null, "tests", null, null, true);
        Assert.Equal(CheckKinds.Command, c1.Kind);
        Assert.Equal("npm test", (string?)c1.Spec["cmd"]);
        Assert.Equal("Testler geçsin (npm test)", c1.Label);
        Assert.True(c1.Enabled);
        var c2 = w.AddCheckFromTemplate(bos, null, "url-opens", null, null, true); // boş bırakılan adres → varsayılan
        Assert.Equal((CheckKinds.Url, "http://localhost:3000", "Şu adres açılsın: http://localhost:3000"), (c2.Kind, (string?)c2.Spec["url"], c2.Label));
        var c3 = w.AddCheckFromTemplate(bos, null, "page-text", new JsonObject { ["url"] = "http://127.0.0.1:5173/", ["text"] = "Merhaba" }, null, true);
        Assert.Equal(("Merhaba", 200L), ((string?)c3.Spec["contains"], (long?)c3.Spec["expectStatus"]));
        var c4 = w.AddCheckFromTemplate(bos, null, "screenshot", new JsonObject { ["url"] = "http://127.0.0.1:5173/" }, null, true);
        Assert.Equal(CheckKinds.Screenshot, c4.Kind);
        var c5 = w.AddCheckFromTemplate(bos, null, "file-exists", new JsonObject { ["path"] = "dist/index.html" }, null, true);
        Assert.Equal((CheckKinds.File, "Şu dosya oluşsun: dist/index.html"), (c5.Kind, c5.Label));
    }

    [Fact]
    public void EkA_dosya_cumlesi_gercekten_calisir_ve_kalir_sonra_gecer()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "dosya-proje");
        Directory.CreateDirectory(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheckFromTemplate(pid, null, "file-text", new JsonObject { ["path"] = "OKU.md", ["text"] = "Kurulum" }, null, true);
        var r1 = w.CompleteRun(w.BeginManualRun(pid, null));
        Assert.Equal(Verdicts.Failed, r1.Verdict);
        File.WriteAllText(Path.Combine(dir, "OKU.md"), "# Proje\n\n## Kurulum\n");
        var r2 = w.CompleteRun(w.BeginManualRun(pid, null));
        Assert.Equal(Verdicts.Passed, r2.Verdict);
    }

    [Fact]
    public async Task EkA_sunucu_template_alani_ve_sablon_listesi()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "web-proje");
        Directory.CreateDirectory(dir);
        var pid = new WorkService(env.Svc.Db).EnsureProjectByPath(dir);
        await using var h = await SozServer.StartAsync(env.DataDir, 49300);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var cat = JsonNode.Parse(await http.GetStringAsync($"/api/projects/{pid}/checks/templates"))!;
        Assert.Equal(CheckTemplates.NoTestsHint, (string?)cat["hint"]);
        Assert.False((bool)cat["templates"]![0]!["available"]!);

        async Task<HttpResponseMessage> Post(object body)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/api/checks") { Content = new StringContent(J(body), Encoding.UTF8, "application/json") };
            r.Headers.Add("X-Soz-Token", h.Token);
            return await http.SendAsync(r);
        }
        var ok = await Post(new { projectId = pid, template = "page-text", values = new { url = "http://localhost:3000", text = "Giriş" } });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var c = JsonNode.Parse(await ok.Content.ReadAsStringAsync())!;
        Assert.Equal("url", (string?)c["kind"]);
        Assert.Equal("Sayfada şu yazı görünsün: \"Giriş\" (http://localhost:3000)", (string?)c["label"]);
        Assert.True((bool)c["enabled"]!);
        Assert.Equal("Giriş", (string?)c["spec"]!["contains"]);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(new { projectId = pid, template = "tests" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { projectId = pid, template = "url-opens", values = new { url = "javascript:alert(1)" } })).StatusCode);
        // Eski biçim (kind + spec) hâlâ çalışır.
        Assert.Equal(HttpStatusCode.Created, (await Post(new { projectId = pid, kind = "command", label = "Kendi komutum", spec = new { cmd = "echo ok" } })).StatusCode);
    }

    // ------------------------------------------------------------------ EK B
    private static List<(string name, string result, string? error, long? pid)> Pulses(TestEnv env)
    {
        using var c = env.Svc.Db.Open();
        return c.Query("SELECT detail, project_id FROM events WHERE kind='hook_pulse' ORDER BY id", r => (d: r.Str(0), p: r.LongN(1)))
            .Select(x => { var o = JsonNode.Parse(x.d!)!; return ((string)o["name"]!, (string)o["result"]!, (string?)o["error"], x.p); }).ToList();
    }

    [Fact]
    public void EkB_her_kanca_nabiz_yazar_hata_ve_sir_maskeli()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "nabiz");
        Directory.CreateDirectory(dir);
        var pid = new WorkService(env.Svc.Db).EnsureProjectByPath(dir);
        Hook(env, "session-start", J(new { session_id = "n1", cwd = dir, source = "startup" }));
        Hook(env, "user-prompt-submit", J(new { session_id = "n1", cwd = dir, prompt = "merhaba" }));
        Hook(env, "pre-tool-use", J(new { session_id = "n1", cwd = dir, tool_name = "Bash", tool_input = new { command = "ls" } }));
        Hook(env, "notification", J(new { session_id = "n1", cwd = dir, message = "x" }));
        Hook(env, "stop", J(new Dictionary<string, object?> { ["session_id"] = "n1", ["cwd"] = dir, ["stop_hook_active"] = false }));
        Hook(env, "stop", "{bozuk json sk-ant-api03-" + new string('Z', 40));
        var p = Pulses(env);
        Assert.Equal(new[] { "session-start", "user-prompt-submit", "pre-tool-use", "notification", "stop", "stop" }, p.Select(x => x.name));
        Assert.All(p.Take(5), x => { Assert.Equal("ok", x.result); Assert.Equal(pid, x.pid); });
        Assert.Equal("hata", p[5].result);
        Assert.NotNull(p[5].error);
        Assert.DoesNotContain("ZZZZZZZZ", p[5].error);

        Assert.Equal("zaman aşımı", HookHandler.ResultOf(new Microsoft.Data.Sqlite.SqliteException("busy", 5)));
        Assert.Equal("zaman aşımı", HookHandler.ResultOf(new TimeoutException()));
        Assert.Equal("hata", HookHandler.ResultOf(new InvalidOperationException()));
    }

    [Fact]
    public void EkB_nabiz_yazilamazsa_gunluge_duser()
    {
        using var env = new TestEnv(copySamples: false);
        var bad = Path.Combine(env.Root, "bozuk-veri");
        // Veritabanı yerine klasör: açılamaz → nabız soz-hook.log'a düşer, kanca yine 0 döner.
        Directory.CreateDirectory(Path.Combine(bad, "soz.db"));
        Assert.Equal(0, HookHandler.Run("stop", new StringReader("{}"), new StringWriter(), bad, _ => null));
        var log = File.ReadAllText(Path.Combine(bad, "soz-hook.log"));
        Assert.Contains("nabız yazılamadı: stop", log);
    }

    [Fact]
    public void EkB_saglik_kurulu_ama_sessiz_exe_yok_baska_veri_ve_kapi_sorunu()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var exe = Path.Combine(env.Root, "bin", "soz.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "sahte");

        var dir = Path.Combine(env.Root, "kapi-proje");
        Directory.CreateDirectory(dir);
        var pid = w.EnsureProjectByPath(dir);
        w.SetGate(pid, true);
        HookHealthDto H() { using var c = env.Svc.Db.Open(); return HookHealth.Build(c, env.DataDir, null); }

        // 1) Hiç kurulu değil, kapı açık → kapı sorunu "kurulu değil".
        var h0 = H();
        Assert.Contains("kurulu değil", h0.GateProblem);
        Assert.False(h0.Ok);

        // 2) Beşi kurulu, exe var, veri aynı; ama oturum var ve hiç nabız yok → sessiz uyarı + kapı sorunu.
        HookInstaller.Install(Path.Combine(dir, ".claude", "settings.json"), exe, env.DataDir);
        using (var c = env.Svc.Db.Open())
            c.Exec("INSERT INTO sessions(tool, external_id, project_id, last_at) VALUES('claude','imp-1',@p,@t)", null, ("@p", pid), ("@t", Time.NowIso()));
        var h1 = H();
        var f = Assert.Single(h1.Files);
        Assert.Equal(6, f.Hooks.Count);
        Assert.True(f.ExeExists);
        Assert.True(f.DataMatches);
        Assert.Contains(h1.Warnings, x => x.Contains(HookHealth.SilentWarning));
        Assert.Contains("hiç çalışmadı", h1.GateProblem);

        // 3) Stop kancası gerçekten çalışınca kapı sorunu ve sessiz uyarı kalkar.
        Hook(env, "stop", J(new Dictionary<string, object?> { ["session_id"] = "imp-1", ["cwd"] = dir, ["stop_hook_active"] = false, ["last_assistant_message"] = "Bitti." }));
        var h2 = H();
        Assert.Null(h2.GateProblem);
        Assert.DoesNotContain(h2.Warnings, x => x.Contains(HookHealth.SilentWarning));
        Assert.Equal(1, h2.Stats.Single(s => s.Name == "stop").Runs24h);
        Assert.True(h2.Ok);

        // 4) Son stop çalışması hata verdi → kapı sorunu, hata metni maskeli.
        Hook(env, "stop", "{bozuk sk-ant-api03-" + new string('Y', 40));
        var h3 = H();
        Assert.Contains("hata verdi", h3.GateProblem);
        Assert.DoesNotContain("YYYYYYYY", h3.GateProblem);
        Assert.Contains(h3.Warnings, x => x.StartsWith("Terminal desteğinin “kanıt denetimi” parçası son 24 saatte 1 kez hata verdi"));

        // 5) Exe silinmiş ya da başka veri klasörü → ayrı uyarılar.
        HookInstaller.Install(Path.Combine(dir, ".claude", "settings.json"), Path.Combine(env.Root, "yok", "soz.exe"), env.DataDir);
        var h4 = H();
        Assert.False(h4.Files.Single().ExeExists);
        Assert.Contains("program bulunamadı", h4.GateProblem);
        HookInstaller.Install(Path.Combine(dir, ".claude", "settings.json"), exe, Path.Combine(env.Root, "baska-veri"));
        var h5 = H();
        Assert.False(h5.Files.Single().DataMatches);
        Assert.Contains("başka bir veri klasörüne", h5.GateProblem);
    }

    [Fact]
    public void EkB_komut_ayristirma()
    {
        var (exe, data) = HookHealth.ParseCommand("DOTNET_ROOT=\"D:/x y\" \"E:/a b/soz.exe\" hook stop --data \"E:/v d\"");
        Assert.Equal(("E:/a b/soz.exe", "E:/v d"), (exe, data));
        Assert.Equal(("soz", (string?)null), HookHealth.ParseCommand("soz hook stop"));
    }

    [Fact]
    public async Task EkB_saglik_ucu_ve_doktor()
    {
        using var env = new TestEnv(copySamples: false);
        var home = Path.Combine(env.Root, "ev");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        Environment.SetEnvironmentVariable("SOZ_USER_HOME", home);
        try
        {
            await using var h = await SozServer.StartAsync(env.DataDir, 49300);
            using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
            var j = JsonNode.Parse(await http.GetStringAsync("/api/hooks/health"))!;
            Assert.Equal(6, j["stats"]!.AsArray().Count);
            Assert.True((bool)j["ok"]!);
            Assert.NotNull(env.Svc.Doctor()["hookHealth"]);
        }
        finally { Environment.SetEnvironmentVariable("SOZ_USER_HOME", null); }
    }

    // ------------------------------------------------------------------ kapı çalışmazsa "geçti" yok
    [Fact]
    public void Kapi_kapali_ya_da_kanca_olu_biten_is_unchecked_kalir_asla_gecti_olmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir); // kapı kapalı, açık ölçüt yok

        // (a) Kanca çalışıyor ama açık ölçüt yok: ajan "bitti, testler geçti" der → unchecked, iş geçti olmaz.
        var j1 = w.CreateJob(pid, "Toplama hatasını düzelt.");
        w.PatchJob(j1.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = "g-a", cwd = dir, prompt = w.Job(j1.Id).PromptText }));
        Hook(env, "stop", J(new Dictionary<string, object?> { ["session_id"] = "g-a", ["cwd"] = dir, ["stop_hook_active"] = false, ["last_assistant_message"] = "Bitti, tüm testler geçti." }));
        Assert.NotEqual(JobStates.Passed, w.Job(j1.Id).State);
        Assert.Equal(Verdicts.Unchecked, w.Runs(pid, j1.Id, 5).First().Verdict);

        // (b) Kanca ölü: iş oturuma bağlandı, oturum bitti (içe almadan), stop kancası HİÇ çalışmadı.
        var j2 = w.CreateJob(pid, "README'yi güncelle.");
        w.PatchJob(j2.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = "g-b", cwd = dir, prompt = w.Job(j2.Id).PromptText }));
        using (var c = env.Svc.Db.Open())
            c.Exec("UPDATE sessions SET turn_ended=1, last_event_ts=@t WHERE external_id='g-b'", null, ("@t", Time.Iso(Time.UtcNow().AddMinutes(1))));
        Assert.NotEqual(JobStates.Passed, w.Job(j2.Id).State);

        var m = w.Morning(Time.UtcNow().AddHours(-1));
        Assert.Empty(m.Passed);
        var u1 = Assert.Single(m.Unverified, x => x.Job.Id == j1.Id);
        Assert.Equal(Verdicts.Unchecked, u1.Verdict);
        var u2 = Assert.Single(m.Unverified, x => x.Job.Id == j2.Id);
        Assert.Equal(Verdicts.Unchecked, u2.Verdict);
        Assert.Equal(MorningBuilder.NoGateSummary, u2.Summary);
    }
}
