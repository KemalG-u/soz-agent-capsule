using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

public class JobsTests
{
    [Fact]
    public void Prompt_Contains_Raw_Text_Checks_Rules_Restate_And_Tag()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        env.Svc.CreateDecision(pid, "ban", "Proje dışına dosya yazma.");
        var c = w.AddCheck(pid, null, "command", "Testler geçsin", new JsonObject { ["cmd"] = "npm test" }, 120, enabled: true);
        var raw = "Toplama   hatasını düzelt;\nsonra testleri çalıştır.";
        var j = w.CreateJob(pid, raw, "dikte", restate: true, night: false);
        Assert.StartsWith(raw, j.PromptText);                         // ham söz AYNEN
        Assert.Contains("Bitti kontrolleri (Söz bu kontrolleri kendisi çalıştıracak", j.PromptText);
        Assert.Contains("- Testler geçsin (komut: npm test)", j.PromptText);
        Assert.Contains("Proje dışına dosya yazma.", j.PromptText);
        Assert.Contains("Başlamadan önce ne anladığını en çok 5 maddeyle yaz ve onayımı bekle.", j.PromptText);
        Assert.EndsWith($"(Söz iş #{j.Id})", j.PromptText);
        Assert.Equal("dikte", j.Source);
        Assert.Equal(JobStates.Draft, j.State);
        Assert.Equal(c.Id, Assert.Single(j.Checks).Id);

        // Ölçütsüz iş dürüstçe söyler.
        var pid2 = w.EnsureProjectByPath(Directory.CreateDirectory(Path.Combine(env.Root, "bos")).FullName);
        Assert.Contains("Bitti kontrolü tanımlı değil", w.CreateJob(pid2, "Bir şey yap.").PromptText);
    }

    [Fact]
    public void State_Transitions_And_Tag_Linking()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var j = w.CreateJob(pid, "Birinci iş.");
        Assert.Equal(JobStates.Given, w.PatchJob(j.Id, JobStates.Given).State);
        Assert.NotNull(w.Job(j.Id).GivenAt);
        var ex = Assert.Throws<SozException>(() => w.PatchJob(j.Id, "gecti"));
        Assert.Equal(400, ex.Status);

        // Terminal metni biraz değiştirse bile etiketten bağlanır.
        var o = new StringWriter();
        HookHandler.Run("user-prompt-submit", new StringReader(System.Text.Json.JsonSerializer.Serialize(new { session_id = "s-tag", cwd = dir, prompt = "Birinci iş (biraz değişti)\n(Söz iş #" + j.Id + ")" })), o, env.DataDir, _ => null);
        Assert.Equal(JobStates.Running, w.Job(j.Id).State);

        Assert.Equal(JobStates.Cancelled, w.PatchJob(j.Id, JobStates.Cancelled).State);
        Assert.Equal(409, Assert.Throws<SozException>(() => w.PatchJob(j.Id, JobStates.Given)).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.CreateJob(pid, "  ")).Status);
        Assert.Equal(404, Assert.Throws<SozException>(() => w.CreateJob(999, "x")).Status);
        var other = w.EnsureProjectByPath(Directory.CreateDirectory(Path.Combine(env.Root, "baska")).FullName);
        var foreign = w.AddCheck(other, null, "file", "dosya var", new JsonObject { ["path"] = "a.txt" }, 10, false);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.CreateJob(pid, "x", checkIds: new[] { foreign.Id })).Status);
    }

    [Fact]
    public void Installer_Writes_Five_Hooks_With_Stop_Timeout_And_Only()
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, ".claude", "settings.json");
        HookInstaller.Install(path, @"C:\x\soz.exe", @"C:\veri", gateBudgetSeconds: 300);
        var h = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!.AsObject();
        Assert.Equal(new[] { "SessionStart", "UserPromptSubmit", "Stop", "StopFailure", "Notification", "PreToolUse" }, h.Select(x => x.Key).ToArray());
        Assert.Equal(330, (int)h["Stop"]![0]!["hooks"]![0]!["timeout"]!);
        Assert.Equal("'C:/x/soz.exe' hook stop --data 'C:/veri' 2>/dev/null || true", (string?)h["Stop"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal("'C:/x/soz.exe' hook pre-tool-use --data 'C:/veri' 2>/dev/null || true", (string?)h["PreToolUse"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal(HookInstaller.PreToolUseMatcher, (string?)h["PreToolUse"]![0]!["matcher"]); // bekçi yalnız dosya ve kabuk araçlarında çalışır
        Assert.Null(h["Stop"]![0]!["matcher"]); // Stop matcher desteklemez

        HookInstaller.Install(path, @"C:\x\soz.exe", null, only: "stop,notification");
        var h2 = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!.AsObject();
        Assert.Equal(new[] { "Stop", "Notification" }, h2.Select(x => x.Key).ToArray());
        Assert.Throws<ArgumentException>(() => HookInstaller.Select("stop,olmayan"));
        Assert.True(HookInstaller.Uninstall(path));
    }

    [Fact]
    public async Task Api_Jobs_Checks_Proof_Queue_Morning_Now()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        await using var h = await SozServer.StartAsync(env.DataDir, 49360);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        HttpRequestMessage Req(HttpMethod m, string url, object? body = null, bool token = true)
        {
            var r = new HttpRequestMessage(m, url);
            if (body is not null) r.Content = JsonContent.Create(body);
            if (token) r.Headers.Add("X-Soz-Token", h.Token);
            return r;
        }

        // belirteçsiz POST → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(HttpMethod.Post, "/api/jobs", new { projectId = pid, rawText = "x" }, token: false))).StatusCode);

        var sug = await (await http.SendAsync(Req(HttpMethod.Post, $"/api/projects/{pid}/checks/suggest"))).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(1, (int)sug!["added"]!);
        var checkId = (long)sug["checks"]![0]!["id"]!;
        Assert.False((bool)sug["checks"]![0]!["enabled"]!);
        Assert.Equal("npm test", (string?)sug["checks"]![0]!["spec"]!["cmd"]);

        var jr = await http.SendAsync(Req(HttpMethod.Post, "/api/jobs", new { projectId = pid, rawText = "Hatayı düzelt.", source = "yazi", restate = false, night = false, checkIds = new[] { checkId } }));
        Assert.Equal(HttpStatusCode.Created, jr.StatusCode);
        var job = (await jr.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("taslak", (string?)job["state"]);
        var jobId = (long)job["id"]!;
        var patched = await (await http.SendAsync(Req(HttpMethod.Patch, $"/api/jobs/{jobId}", new { state = "verildi" }))).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("verildi", (string?)patched!["state"]);

        // Elle kanıt (wait) → failed + çıktı + kanıt dosyası
        var run = await (await http.SendAsync(Req(HttpMethod.Post, $"/api/projects/{pid}/proof/run", new { jobId, wait = true }))).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("failed", (string?)run!["verdict"]);
        var art = (string)run["results"]![0]!["artifactUrl"]!;
        var artResp = await http.GetAsync(art);
        Assert.Equal(HttpStatusCode.OK, artResp.StatusCode);
        Assert.Contains("toplama iki sayiyi toplar", await artResp.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/proof/artifact/{(long)run["id"]!}/..%5C..%5Csoz.db")).StatusCode);
        // Arka planda çalıştırma → 202 {runId}
        var acc = await http.SendAsync(Req(HttpMethod.Post, $"/api/projects/{pid}/proof/run", new { jobId }));
        Assert.Equal(HttpStatusCode.Accepted, acc.StatusCode);
        var runId = (long)(await acc.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!;
        JsonObject? got = null;
        for (var i = 0; i < 120; i++)
        {
            got = await http.GetFromJsonAsync<JsonObject>($"/api/proof/runs/{runId}");
            if (got!["verdict"] is not null) break;
            await Task.Delay(250);
        }
        Assert.Equal("failed", (string?)got!["verdict"]);
        var runs = await http.GetFromJsonAsync<JsonArray>($"/api/proof/runs?projectId={pid}");
        Assert.Equal(2, runs!.Count);

        // Kuyruk
        var qr = await http.SendAsync(Req(HttpMethod.Post, "/api/queue", new { projectId = pid, text = "Bir de README güncelle." }));
        Assert.Equal(HttpStatusCode.Created, qr.StatusCode);
        Assert.Single((await http.GetFromJsonAsync<JsonArray>($"/api/queue?projectId={pid}&pending=true"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Req(HttpMethod.Post, "/api/queue", new { projectId = pid, text = "" }))).StatusCode);

        // Proje kapısı
        var g = await (await http.SendAsync(Req(HttpMethod.Patch, $"/api/projects/{pid}", new { gateEnabled = true }))).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True((bool)g!["gateEnabled"]!);
        Assert.True((bool)(await http.GetFromJsonAsync<JsonObject>($"/api/projects/{pid}"))!["gateEnabled"]!);

        // Ölçüt ekle/değiştir/sil
        var cr = await http.SendAsync(Req(HttpMethod.Post, "/api/checks", new { projectId = pid, kind = "file", label = "paket dosyası var", spec = new { path = "package.json" } }));
        Assert.Equal(HttpStatusCode.Created, cr.StatusCode);
        var cid = (long)(await cr.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
        Assert.True((bool)(await (await http.SendAsync(Req(HttpMethod.Patch, $"/api/checks/{cid}", new { enabled = true }))).Content.ReadFromJsonAsync<JsonObject>())!["enabled"]!);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Delete, $"/api/checks/{cid}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Req(HttpMethod.Post, "/api/checks", new { projectId = pid, kind = "command", label = "x", spec = new { } }))).StatusCode);

        // Sabah ve Bugün
        var morning = await http.GetFromJsonAsync<JsonObject>("/api/morning");
        Assert.NotNull(morning!["sentence"]);
        var now = await http.GetFromJsonAsync<JsonObject>("/api/now");
        Assert.Equal(0, (int)now!["counts"]!["jobsRunning"]!); // "verildi" iş ya da terminalde etkinliği olmayan iş çalışıyor sayılmaz (Ö-5)
        Assert.Equal(0, (int)now["counts"]!["queued"]!);
        Assert.Equal(1, (int)now["counts"]!["queuePending"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/jobs/9999")).StatusCode);

        // SSE: kancadan (ayrı yol) yazılan olay sunucudan yayınlanır.
        using var sse = new HttpClient { BaseAddress = new Uri(h.Url), Timeout = TimeSpan.FromSeconds(30) };
        using var stream = await sse.GetStreamAsync("/api/events/stream");
        using var reader = new StreamReader(stream);
        await reader.ReadLineAsync(); // ": bağlandı"
        await reader.ReadLineAsync();
        HookHandler.Run("notification", new StringReader(System.Text.Json.JsonSerializer.Serialize(new { session_id = "s-sse", cwd = dir, notification_type = "idle_prompt" })), new StringWriter(), env.DataDir, _ => null);
        var seen = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            if (line == "event: session") { seen = true; break; }
        }
        Assert.True(seen, "SSE session olayı gelmedi");
    }
}
