using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Guard;
using Soz.Core.Hooks;
using Soz.Core.Readers;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

public class GuardHookTests
{
    /// <summary>"Temkinli" şablonundaki kural sayısı (şablon değişince sabit sayı tutulmaz).</summary>
    private static readonly int TemkinliRules = GuardTemplates.Find("temkinli")!.Rules.Count;

    private static (int code, string output) Hook(TestEnv env, object input, Dictionary<string, string?>? vars = null, string? rawStdin = null)
    {
        var o = new StringWriter();
        var stdin = rawStdin ?? System.Text.Json.JsonSerializer.Serialize(input);
        var code = HookHandler.Run("pre-tool-use", new StringReader(stdin), o, env.DataDir, k => vars is not null && vars.TryGetValue(k, out var v) ? v : null);
        return (code, o.ToString());
    }

    private static (long pid, string dir) SetupProject(TestEnv env, string template = "temkinli")
    {
        var dir = Path.Combine(env.Root, "projem");
        Directory.CreateDirectory(dir);
        using var c = env.Svc.Db.Open();
        var pid = IngestStore.EnsureProject(c, null, dir);
        GuardStore.EnsureTables(c);
        GuardStore.ApplyTemplate(c, pid, template);
        return (pid, dir);
    }

    [Fact]
    public void Proje_disi_yazma_deny_cikti_belgedeki_bicimde()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, dir) = SetupProject(env);
        var (code, output) = Hook(env, new { session_id = "s1", cwd = dir, hook_event_name = "PreToolUse", tool_name = "Write", tool_input = new { file_path = Path.Combine(env.Root, "disarida.txt"), content = "x" } });
        Assert.Equal(0, code);
        var j = JsonNode.Parse(output)!["hookSpecificOutput"]!;
        Assert.Equal("PreToolUse", (string?)j["hookEventName"]);
        Assert.Equal("deny", (string?)j["permissionDecision"]);
        Assert.Contains("Proje dışına yazma yasak", (string?)j["permissionDecisionReason"]);
        using var c = env.Svc.Db.Open();
        Assert.Equal(1, c.ScalarLong("SELECT COUNT(*) FROM guard_events WHERE action='deny' AND tool='Write' AND tool_session_id='s1' AND project_id=@p", null, ("@p", pid)));
    }

    [Fact]
    public void Toplu_silme_ask_cikti_ve_olay()
    {
        using var env = new TestEnv(copySamples: false);
        var (_, dir) = SetupProject(env);
        var (code, output) = Hook(env, new { session_id = "s2", cwd = dir, tool_name = "Bash", tool_input = new { command = "rm -rf build" } });
        Assert.Equal(0, code);
        Assert.Equal("ask", (string?)JsonNode.Parse(output)!["hookSpecificOutput"]!["permissionDecision"]);
    }

    [Fact]
    public void Eslesme_yoksa_cikti_bos_cikis_0()
    {
        using var env = new TestEnv(copySamples: false);
        var (_, dir) = SetupProject(env);
        var (code, output) = Hook(env, new { session_id = "s3", cwd = dir, tool_name = "Bash", tool_input = new { command = "git status" } });
        Assert.Equal(0, code);
        Assert.Equal("", output);
        using var c = env.Svc.Db.Open();
        Assert.Equal(0, c.ScalarLong("SELECT COUNT(*) FROM guard_events"));
        // izinli eşleşme olay yazmaz; ayrıca MCP gibi araçlar hiç değerlendirilmez
        var (c2, o2) = Hook(env, new { cwd = dir, tool_name = "mcp__x__y", tool_input = new { command = "rm -rf /" } });
        Assert.Equal((0, ""), (c2, o2));
    }

    [Theory]
    [InlineData("{bozuk json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"tool_name\":42,\"tool_input\":\"x\"}")]
    public void Bozuk_stdin_ajani_kilitlemez(string stdin)
    {
        using var env = new TestEnv(copySamples: false);
        var (code, output) = Hook(env, new { }, rawStdin: stdin);
        Assert.Equal(0, code);
        Assert.Equal("", output);
    }

    [Fact]
    public void Veri_klasoru_yoksa_ya_da_kural_bozuksa_cikis_0_ve_log()
    {
        using var env = new TestEnv(copySamples: false);
        var (_, dir) = SetupProject(env);
        // bozuk spec_json: motor çökmeden (boş spec) değerlendirir
        using (var c = env.Svc.Db.Open()) c.Exec("UPDATE guard_rules SET spec_json='{{{bozuk'");
        var (code, _) = Hook(env, new { cwd = dir, tool_name = "Bash", tool_input = new { command = "ls" } });
        Assert.Equal(0, code);
        // veri klasörü oluşturulamayacak bir yol (dosya adıyla çakışan)
        var blocker = Path.Combine(env.Root, "dosya.txt");
        File.WriteAllText(blocker, "x");
        var o = new StringWriter();
        var rc = HookHandler.Run("pre-tool-use", new StringReader("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"rm -rf x\"},\"cwd\":\"E:\\\\x\"}"), o, Path.Combine(blocker, "alt"));
        Assert.Equal(0, rc);
        Assert.Equal("", o.ToString());
    }

    [Fact]
    public void Olay_hedef_ozeti_sirdan_arindirilir_ve_200_karakterle_sinirlanir()
    {
        using var env = new TestEnv(copySamples: false);
        var (_, dir) = SetupProject(env);
        var secret = "sk-" + new string('a', 40);
        var cmd = "rm -rf build && echo token=" + secret + " " + new string('x', 400);
        Hook(env, new { session_id = "s4", cwd = dir, tool_name = "Bash", tool_input = new { command = cmd } });
        using var c = env.Svc.Db.Open();
        var target = (string)c.Scalar("SELECT target_summary FROM guard_events ORDER BY id DESC LIMIT 1")!;
        Assert.DoesNotContain(secret, target);
        Assert.True(target.Length <= 200, "uzunluk " + target.Length);
    }

    [Fact]
    public void Genel_kural_project_id_null_her_projede_gecerli_ve_ayarlanan_etkisiz_kural_calismaz()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, dir) = SetupProject(env, "proje-disi-yazma");
        using (var c = env.Svc.Db.Open())
        {
            GuardStore.Create(c, null, "command_block", "Genel yasak", new JsonObject { ["patterns"] = new JsonArray("taskkill") }, "deny");
            var off = GuardStore.Create(c, pid, "command_block", "Kapalı", new JsonObject { ["patterns"] = new JsonArray("whoami") }, "deny", enabled: false);
        }
        Assert.Equal("deny", (string?)JsonNode.Parse(Hook(env, new { cwd = dir, tool_name = "Bash", tool_input = new { command = "taskkill /im a.exe" } }).output)!["hookSpecificOutput"]!["permissionDecision"]);
        Assert.Equal("", Hook(env, new { cwd = dir, tool_name = "Bash", tool_input = new { command = "whoami" } }).output);
    }

    [Fact]
    public void Sablon_uygulama_tekrarinda_cogalmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, _) = SetupProject(env);
        using var c = env.Svc.Db.Open();
        var before = c.ScalarLong("SELECT COUNT(*) FROM guard_rules");
        Assert.Equal(TemkinliRules, before);
        var again = GuardStore.ApplyTemplate(c, pid, "temkinli");
        Assert.Empty(again.Created);
        Assert.Equal(TemkinliRules, again.Existing.Count);
        Assert.Equal(TemkinliRules, c.ScalarLong("SELECT COUNT(*) FROM guard_rules"));
        Assert.Throws<SozException>(() => GuardStore.ApplyTemplate(c, pid, "yok-boyle-sablon"));
    }

    [Fact]
    public void Kural_dogrulama_hatali_spec_reddedilir()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        GuardStore.EnsureTables(c);
        Assert.Throws<SozException>(() => GuardStore.Create(c, null, "command_block", "x", new JsonObject(), "deny"));
        Assert.Throws<SozException>(() => GuardStore.Create(c, null, "yok", "x", new JsonObject(), "deny"));
        Assert.Throws<SozException>(() => GuardStore.Create(c, null, "command_block", "x", new JsonObject { ["patterns"] = new JsonArray("re:(") }, "deny"));
        Assert.Throws<SozException>(() => GuardStore.Create(c, null, "scope_only", "x", new JsonObject(), "izin"));
    }

    [Fact]
    public async Task Ucler_kural_crud_sablon_dene_olaylar_ve_token()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "projem2");
        Directory.CreateDirectory(dir);
        long pid;
        using (var c = env.Svc.Db.Open()) pid = IngestStore.EnsureProject(c, null, dir);
        await using var h = await SozServer.StartAsync(env.DataDir, 49360);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        HttpRequestMessage Req(HttpMethod m, string url, object? body = null)
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Add("X-Soz-Token", h.Token);
            if (body is not null) r.Content = JsonContent.Create(body);
            return r;
        }
        // belirteçsiz değişiklik → 401
        var noToken = await http.PostAsJsonAsync("/api/guard/templates/apply", new { projectId = pid, template = "temkinli" });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, noToken.StatusCode);
        // şablon uygula
        var ap = await http.SendAsync(Req(HttpMethod.Post, "/api/guard/templates/apply", new { projectId = pid, template = "temkinli" }));
        Assert.True(ap.IsSuccessStatusCode, await ap.Content.ReadAsStringAsync());
        var apj = JsonNode.Parse(await ap.Content.ReadAsStringAsync())!;
        Assert.Equal(TemkinliRules, apj["created"]!.AsArray().Count);
        var rules = JsonNode.Parse(await http.GetStringAsync($"/api/guard/rules?projectId={pid}"))!.AsArray();
        Assert.Equal(TemkinliRules, rules.Count);
        Assert.False(string.IsNullOrEmpty((string?)rules[0]!["sentence"]));
        // dene
        var test = await http.SendAsync(Req(HttpMethod.Post, "/api/guard/test", new { projectId = pid, toolName = "Bash", toolInput = new { command = "git push --force" } }));
        var tj = JsonNode.Parse(await test.Content.ReadAsStringAsync())!;
        Assert.Equal("ask", (string?)tj["verdict"]);
        var test2 = await http.SendAsync(Req(HttpMethod.Post, "/api/guard/test", new { projectId = pid, toolName = "Bash", toolInput = "ls" }));
        Assert.Equal("allow", (string?)JsonNode.Parse(await test2.Content.ReadAsStringAsync())!["verdict"]);
        // kural ekle / değiştir / sil
        var cr = await http.SendAsync(Req(HttpMethod.Post, "/api/guard/rules", new { projectId = pid, kind = "command_block", label = "Whoami yasak", spec = new { patterns = new[] { "whoami" } }, action = "deny" }));
        Assert.Equal(System.Net.HttpStatusCode.Created, cr.StatusCode);
        var id = (long)JsonNode.Parse(await cr.Content.ReadAsStringAsync())!["id"]!;
        var pa = await http.SendAsync(Req(HttpMethod.Patch, $"/api/guard/rules/{id}", new { enabled = false }));
        Assert.False((bool)JsonNode.Parse(await pa.Content.ReadAsStringAsync())!["enabled"]!);
        var bad = await http.SendAsync(Req(HttpMethod.Post, "/api/guard/rules", new { projectId = pid, kind = "command_block", spec = new { } }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        var del = await http.SendAsync(Req(HttpMethod.Delete, $"/api/guard/rules/{id}"));
        Assert.True(del.IsSuccessStatusCode);
        // olaylar (kanca yazar, uç okur) + SSE
        var o = new StringWriter();
        HookHandler.Run("pre-tool-use", new StringReader($"{{\"session_id\":\"s9\",\"cwd\":{System.Text.Json.JsonSerializer.Serialize(dir)},\"tool_name\":\"Bash\",\"tool_input\":{{\"command\":\"rm -rf out\"}}}}"), o, env.DataDir);
        // SSE: kanca başka süreçte yazar, sunucu tabloyu izleyip "guard" olayı yayar
        using var sse = new HttpClient { BaseAddress = new Uri(h.Url), Timeout = TimeSpan.FromSeconds(20) };
        using var sseResp = await sse.GetAsync("/api/events/stream", HttpCompletionOption.ResponseHeadersRead);
        using var sseReader = new StreamReader(await sseResp.Content.ReadAsStreamAsync());
        var sseTask = Task.Run(async () => { while (true) { var line = await sseReader.ReadLineAsync(); if (line is null) return ""; if (line.StartsWith("data:") && line.Contains("git push --force")) return line; } });
        HookHandler.Run("pre-tool-use", new StringReader($"{{\"session_id\":\"s10\",\"cwd\":{System.Text.Json.JsonSerializer.Serialize(dir)},\"tool_name\":\"Bash\",\"tool_input\":{{\"command\":\"git push --force\"}}}}"), new StringWriter(), env.DataDir);
        var sseLine = await sseTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("git push --force", sseLine);
        var ev = JsonNode.Parse(await http.GetStringAsync("/api/guard/events?days=1"))!;
        Assert.Equal(2, (int)ev["total"]!);
        Assert.Equal(2, (int)ev["ask"]!);
        Assert.Equal("Zorla gönderme ve sert sıfırlama önce sorulur", (string?)ev["items"]![0]!["ruleLabel"]);
        Assert.Equal("Toplu silme önce sorulur", (string?)ev["items"]![1]!["ruleLabel"]);
    }
}
