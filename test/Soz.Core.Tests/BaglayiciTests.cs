using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Agents;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Ajan bağlayıcıları (P7): arayüz, Claude'un taşınması, Codex bağlayıcısı (sahte codex ile), dış kapı döngüsü, .cmd sarmalayıcı ortamı, şema v10, API.
/// GERÇEK codex ve GERÇEK claude çalıştırılmaz.
/// </summary>
public class BaglayiciTests
{
    private static string FakeCodex => Path.Combine(TestEnv.RepoRoot, "test", "sahte-codex", "sahte-codex.mjs");
    private static string FakeClaude => Path.Combine(TestEnv.RepoRoot, "test", "sahte-claude", "sahte-claude.mjs");

    private sealed class Rig : IDisposable
    {
        public TestEnv Env = new(copySamples: false);
        public WorkService W;
        public RunManager Rm;
        public string ProjectDir;
        public long ProjectId;
        public string CodexLog, TorunFile, CodexHome;

        public Rig(int maxParallel = 3, bool node = true, string? codexPath = null, int gateMax = 3)
        {
            ProjectDir = node ? NodeSample.Create(Env, "dugum proje") : Path.Combine(Env.Root, "proje klasoru");
            Directory.CreateDirectory(ProjectDir);
            W = new WorkService(Env.Svc.Db);
            ProjectId = W.EnsureProjectByPath(ProjectDir);
            Env.Svc.PutSettings(new JsonObject { ["claudePath"] = FakeClaude, ["codexPath"] = codexPath ?? FakeCodex, ["maxParallelRuns"] = maxParallel, ["gateMaxAttempts"] = gateMax });
            Rm = new RunManager(Env.Svc.Db); // soz.exe YOK: Codex yolu Söz'ün kanca ayarına ihtiyaç duymamalı
            CodexLog = Path.Combine(Env.Root, "sahte-codex-log.jsonl");
            TorunFile = Path.Combine(Env.Root, "torun.pid");
            CodexHome = Path.Combine(Env.Root, "codex-home-kullanici");
            Directory.CreateDirectory(CodexHome);
            Environment.SetEnvironmentVariable("SAHTE_CODEX_LOG", CodexLog);
            Environment.SetEnvironmentVariable("SAHTE_ELEVATED", "1"); // gerçek makinenin [windows] sandbox = "elevated" ayarı: bayrak yoksa her komut düşer
            Environment.SetEnvironmentVariable("SAHTE_STATE_DIR", Env.Root);
            Environment.SetEnvironmentVariable("SAHTE_TORUN", TorunFile);
            Environment.SetEnvironmentVariable("CODEX_HOME", CodexHome);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-gizli-openai-0000"); // çocuk sürece GEÇMEMELİ
            Environment.SetEnvironmentVariable("CODEX_API_KEY", "sk-test-gizli-codex-0000");   // çocuk sürece GEÇMEMELİ
        }

        public JobDto Job(string text, string? agent = "codex") => W.CreateJob(ProjectId, text, agent: agent);

        public async Task Finish(RunDto run)
        {
            await Rm.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(90));
        }

        public List<JsonNode> Log() => File.Exists(CodexLog) ? File.ReadAllLines(CodexLog).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).ToList() : new();

        public List<(string trigger, int attempt, string? verdict, string? reason)> Proofs(long jobId)
        {
            using var c = Env.Svc.Db.Open();
            return c.Query("SELECT trigger, attempt, verdict, reason_code FROM proof_runs WHERE job_id=@j ORDER BY id", r => (r.GetString(0), (int)r.GetInt64(1), r.Str(2), r.Str(3)), null, ("@j", jobId));
        }

        public List<RunDto> Runs(long jobId) { using var c = Env.Svc.Db.Open(); return RunStore.ListForJob(c, jobId).OrderBy(r => r.Id).ToList(); }

        public void Dispose()
        {
            try { Rm.Dispose(); } catch (Exception) { }
            foreach (var k in new[] { "SAHTE_CODEX_LOG", "SAHTE_STATE_DIR", "SAHTE_TORUN", "CODEX_HOME", "OPENAI_API_KEY", "CODEX_API_KEY", "SAHTE_SARMAL", "SAHTE_ELEVATED" })
                Environment.SetEnvironmentVariable(k, null);
            Env.Dispose();
        }
    }

    private void AddNpmTestCheck(Rig rig) => rig.W.AddCheck(rig.ProjectId, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, true);

    // ------------------------------------------------------------------ arayüz ve yetenekler
    [Fact]
    public void Registry_Has_Claude_And_Codex_With_Honest_Capabilities()
    {
        var claude = AgentRegistry.Get("claude");
        var codex = AgentRegistry.Get("CODEX");
        Assert.Equal(new[] { "claude", "codex", "hermes", "opencode", "antigravity" }, AgentRegistry.Ids.ToArray());
        Assert.Equal(GuardTier.NativeHook, claude.Capabilities.BeforeTool);
        Assert.Equal(TurnEndMode.NativeHook, claude.Capabilities.TurnEnd);
        Assert.Equal(CostMode.Dollars, claude.Capabilities.Cost);
        // Codex: kanca YOK (kullanıcı ayarına yazmak gerekirdi): işletim sistemi sınırı + dış kapı döngüsü
        Assert.Equal(GuardTier.OsSandbox, codex.Capabilities.BeforeTool);
        Assert.Equal(TurnEndMode.OuterLoop, codex.Capabilities.TurnEnd);
        Assert.Equal(CostMode.Tokens, codex.Capabilities.Cost);
        Assert.True(codex.Capabilities.Resume && codex.Capabilities.Headless && codex.Capabilities.EventStream && codex.Capabilities.Stop);
        Assert.Equal("sınırlı", codex.GuardLabel);
        Assert.Equal("komut komut", claude.GuardLabel);
        Assert.Equal("os_sandbox", AgentProbe.Describe(codex.Capabilities).GuardTier);
        Assert.Equal("outer_loop", AgentProbe.Describe(codex.Capabilities).TurnEnd);
        var ex = Assert.Throws<SozException>(() => AgentRegistry.Get("gemini"));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void Claude_Launch_Is_Identical_To_Pre_Connector_Arguments()
    {
        using var env = new TestEnv(copySamples: false);
        var st = SozSettings.Load(env.Svc.Db);
        var cmd = new LaunchCommand("C:\\x\\claude.exe", new[] { "--on" }, "claude");
        var ctx = new AgentRunContext("E:\\p", "istem metni", "sonnet", AgentPermissionMode.Careful, 5, 7, st, () => "E:\\veri\\yurutucu\\claude-ayar.json");
        var start = new ClaudeConnector().BuildStart(cmd, ctx);
        Assert.Equal("C:\\x\\claude.exe", start.FileName);
        Assert.Equal(new[] { "--on", "-p", "--output-format", "stream-json", "--verbose", "--settings", "E:\\veri\\yurutucu\\claude-ayar.json", "--permission-mode", "acceptEdits", "--allowedTools", st.RunnerAllowedTools, "--model", "sonnet" }, start.Args.ToArray());
        Assert.Equal("istem metni", start.Stdin);
        var resume = new ClaudeConnector().BuildResume(cmd, ctx, "abc-1");
        Assert.Equal(start.Args.Concat(new[] { "--resume", "abc-1" }).ToArray(), resume.Args.ToArray());
        // kanca ayarı yazılamazsa sade neden
        var bad = ctx with { HookSettings = () => throw new IOException("disk dolu") };
        Assert.Contains("terminal desteği ayarı hazırlanamadı", Assert.Throws<AgentLaunchException>(() => new ClaudeConnector().BuildStart(cmd, bad)).Message);
    }

    [Fact]
    public void Claude_Result_Cost_And_Usage_Are_Read()
    {
        var t = new RunEventTranslator();
        t.Feed("""{"type":"result","subtype":"success","is_error":false,"num_turns":3,"result":"tamam","session_id":"s","total_cost_usd":0.0123,"usage":{"input_tokens":1500,"cache_read_input_tokens":900,"output_tokens":210}}""");
        Assert.NotNull(t.Usage);
        Assert.Equal(0.0123, t.Usage!.CostUsd!.Value, 6);
        Assert.Equal(1500, t.Usage.InputTokens);
        Assert.Equal(900, t.Usage.CachedInputTokens);
        Assert.Equal(210, t.Usage.OutputTokens);
        var none = new RunEventTranslator();
        none.Feed("""{"type":"result","subtype":"success","is_error":false,"result":"x","session_id":"s"}""");
        Assert.Null(none.Usage);
    }

    [Fact]
    public async Task Claude_Run_Writes_Cost_To_Run_And_Job_Spend()
    {
        using var rig = new Rig();
        Environment.SetEnvironmentVariable("SAHTE_LOG", Path.Combine(rig.Env.Root, "claude-log.jsonl"));
        try
        {
            rig.Rm.SozExePath = typeof(RunManager).Assembly.Location;
            var job = rig.Job("iş [[sahte:oku]]", agent: "claude");
            Assert.Equal("claude", job.Agent);
            var run = rig.Rm.Start(job.Id)!;
            await rig.Finish(run);
            var r = rig.Runs(job.Id).Single();
            Assert.Equal("claude", r.Agent);
            Assert.Equal(0.0, r.CostUsd); // sahte claude total_cost_usd:0 bildirir; alan okundu ve yazıldı
            var j = rig.W.Job(job.Id);
            Assert.NotNull(j.Spend);
            Assert.Equal(1, j.Spend!.Runs);
        }
        finally { Environment.SetEnvironmentVariable("SAHTE_LOG", null); }
    }

    // ------------------------------------------------------------------ Codex bağlayıcısı: komut satırı
    [Fact]
    public void Codex_Start_And_Resume_Command_Lines()
    {
        using var env = new TestEnv(copySamples: false);
        var st = SozSettings.Load(env.Svc.Db);
        var cmd = new LaunchCommand("C:\\x\\node.exe", new[] { "C:\\x\\codex.js" }, "node codex.js");
        var ctx = new AgentRunContext("E:\\Sample Work\\p", "iş: -tehlikeli \"tırnaklı\" metin", "", AgentPermissionMode.Careful, 5, 7, st, null);
        var codex = new CodexConnector();

        var start = codex.BuildStart(cmd, ctx);
        Assert.Equal("C:\\x\\node.exe", start.FileName);
        Assert.Equal(new[] { "C:\\x\\codex.js", "exec", "--json", "--skip-git-repo-check", "-s", "workspace-write", "-C", "E:\\Sample Work\\p", "-c", "approval_policy=\"never\"", "-c", "windows.sandbox=\"unelevated\"", "-" }, start.Args.ToArray());
        Assert.Equal("iş: -tehlikeli \"tırnaklı\" metin", start.Stdin); // istem komut satırında değil, standart girdide

        var resume = codex.BuildResume(cmd, ctx with { Model = "gpt-test" }, "019a-uuid");
        Assert.Equal(new[] { "C:\\x\\codex.js", "exec", "resume", "--json", "--skip-git-repo-check", "-c", "sandbox_mode=\"workspace-write\"", "-c", "approval_policy=\"never\"", "-c", "windows.sandbox=\"unelevated\"", "-m", "gpt-test", "019a-uuid", "-" }, resume.Args.ToArray());
        Assert.DoesNotContain("-s", resume.Args); // `exec resume` -s ve -C kabul etmez (codex 0.167.0 --help)
        Assert.DoesNotContain("-C", resume.Args);

        // izin kipi eşlemesi: temkinli → workspace-write, salt okunur → read-only; danger-full-access hiçbir kipe eşlenmez
        Assert.Contains("workspace-write", codex.DescribePermission(AgentPermissionMode.Careful));
        Assert.Contains("read-only", codex.DescribePermission(AgentPermissionMode.ReadOnly));
        Assert.Contains("read-only", codex.BuildStart(cmd, ctx with { Permission = AgentPermissionMode.ReadOnly }).Args);
        Assert.DoesNotContain(start.Args.Concat(resume.Args), a => a.Contains("danger") || a.Contains("bypass"));
        // kullanıcının ~/.codex klasörüne dokunacak hiçbir bayrak ya da ortam yok
        Assert.DoesNotContain(start.AddEnv.Keys, k => k.StartsWith("CODEX_HOME", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(start.Args.Concat(resume.Args), a => a.Contains(".codex", StringComparison.OrdinalIgnoreCase) || a.Contains("hooks", StringComparison.OrdinalIgnoreCase));
        // Claude'un çalıştırıcı modeli Codex'e verilmez
        Assert.Equal("", codex.DefaultModel(SozSettings.Load(env.Svc.Db)));
    }

    [Fact]
    public void Codex_Strips_Billing_Keys_Per_Tool()
    {
        var codex = new CodexConnector();
        Assert.True(codex.ShouldStripEnv("OPENAI_API_KEY"));
        Assert.True(codex.ShouldStripEnv("openai_base_url"));
        Assert.True(codex.ShouldStripEnv("CODEX_API_KEY"));
        Assert.False(codex.ShouldStripEnv("CODEX_HOME")); // kullanıcının kendi ayar klasörü, Söz değiştirmez ve silmez
        Assert.False(codex.ShouldStripEnv("ANTHROPIC_API_KEY")); // her araç yalnız kendi listesi
        var claude = new ClaudeConnector();
        Assert.True(claude.ShouldStripEnv("ANTHROPIC_API_KEY"));
        Assert.False(claude.ShouldStripEnv("OPENAI_API_KEY"));
    }

    // ------------------------------------------------------------------ Codex olay çevirisi
    private static string J(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public void Codex_Event_Translator_Plain_Turkish_Lines_No_Tool_Names()
    {
        var t = new CodexEventStream();
        var all = new List<RunLine>();
        void F(string l) => all.AddRange(t.Feed(l));
        F(J(new { type = "thread.started", thread_id = "019a-abc" }));
        Assert.Equal("019a-abc", t.SessionId);
        F(J(new { type = "turn.started" }));
        F("not json at all");
        F(J(new { type = "item.completed", item = new { id = "i0", type = "reasoning", text = "x" } }));
        F(J(new { type = "item.completed", item = new { id = "i1", type = "reasoning", text = "y" } })); // art arda tekrar yok
        F(J(new { type = "item.started", item = new { id = "i2", type = "command_execution", command = "\"C:\\\\Windows\\\\System32\\\\WindowsPowerShell\\\\v1.0\\\\powershell.exe\" -Command \"npm test\"", status = "in_progress" } }));
        F(J(new { type = "item.completed", item = new { id = "i2", type = "command_execution", command = "\"C:\\\\Windows\\\\System32\\\\WindowsPowerShell\\\\v1.0\\\\powershell.exe\" -Command \"npm test\"", aggregated_output = "fail 1", exit_code = 1, status = "failed" } }));
        F(J(new { type = "item.completed", item = new { id = "i3", type = "command_execution", command = "bash -lc \"ls -la src\"", aggregated_output = "x", exit_code = 0, status = "completed" } })); // yalnız tamamlanmış gelirse de bir kez
        F(J(new { type = "item.completed", item = new { id = "i4", type = "file_change", changes = new[] { new { path = "E:\\p\\hesap.js", kind = "update" } }, status = "completed" } }));
        F(J(new { type = "item.completed", item = new { id = "i5", type = "file_change", changes = new[] { new { path = "E:\\p\\notlar.txt", kind = "add" } }, status = "completed" } }));
        F(J(new { type = "item.started", item = new { id = "i6", type = "web_search", query = "node test" } }));              // bekleyen değişiklik satırını bırakır
        F(J(new { type = "item.started", item = new { id = "i7", type = "mcp_tool_call", server = "x", tool = "y" } }));
        F(J(new { type = "item.completed", item = new { id = "i8", type = "bilinmeyen_tur", text = "z" } }));                  // sessiz
        F(J(new { type = "item.completed", item = new { id = "i9", type = "agent_message", text = "Bitti. Testler geçti: 2/2\n\nİkinci paragraf." } }));
        F(J(new { type = "turn.completed", usage = new { input_tokens = 1200, cached_input_tokens = 800, output_tokens = 150, reasoning_output_tokens = 40 } }));
        all.AddRange(t.Flush());

        Assert.Equal(new[]
        {
            "düşünüyor",
            "bir komut çalıştırıyor: testler",
            "bir komut hata verdi",
            "bir komut çalıştırıyor: dosya listeleme",
            "2 dosyayı değiştirdi",
            "internette arıyor",
            "bir yardımcı araç çalıştırıyor",
            "Bitti. Testler geçti: 2/2",
        }, all.Select(l => l.Text).ToArray());
        // araç adı ve ham kabuk sarmalayıcısı satırda yok
        Assert.DoesNotContain(all, l => l.Text.Contains("powershell", StringComparison.OrdinalIgnoreCase) || l.Text.Contains("bash"));
        Assert.Equal("Komut: npm test", all[1].Detail); // sarmalayıcı atıldı, asıl komut ayrıntıda
        Assert.Equal("agent", all[^1].Kind);
        Assert.True(t.ResultSeen);
        Assert.False(t.ResultIsError);
        Assert.Equal("Bitti. Testler geçti: 2/2\n\nİkinci paragraf.", t.LastAgentText);
        Assert.Equal(new AgentUsage(1200, 800, 150, 40, null), t.Usage);
        Assert.Equal(1, t.NumTurns);
    }

    [Theory]
    [InlineData("\"C:\\\\Windows\\\\System32\\\\WindowsPowerShell\\\\v1.0\\\\powershell.exe\" -Command \"npm test\"", "npm test")]
    [InlineData("powershell.exe -NoProfile -Command 'Get-Content a.txt'", "Get-Content a.txt")]
    [InlineData("bash -lc \"git status\"", "git status")]
    [InlineData("cmd.exe /c dir", "dir")]
    [InlineData("npm test", "npm test")]
    [InlineData("node -e \"console.log(1)\"", "node -e \"console.log(1)\"")]
    public void Codex_Unwraps_Shell_Wrappers(string raw, string expected)
        => Assert.Equal(expected, CodexEventStream.UnwrapShell(raw.Replace("\\\\", "\\")));

    [Fact]
    public void Codex_Failure_And_Error_Events()
    {
        var t = new CodexEventStream();
        t.Feed(J(new { type = "thread.started", thread_id = "t1" }));
        t.Feed(J(new { type = "error", message = "Reconnecting... 1/5" })); // geçici hata: tek başına başarısızlık değil
        Assert.False(t.ResultSeen);
        t.Feed(J(new { type = "turn.failed", error = new { message = "Not logged in. Please run `codex login`." } }));
        Assert.True(t.ResultSeen);
        Assert.True(t.ResultIsError);
        var codex = new CodexConnector();
        Assert.Contains("giriş yap", codex.FailureReason(1, t, ""));
        var lim = new CodexEventStream();
        lim.Feed(J(new { type = "turn.failed", error = new { message = "You've hit your usage limit." } }));
        Assert.Contains("kullanım sınırına", codex.FailureReason(1, lim, ""));
        Assert.Equal("Yardımcı sonuç vermeden kapandı.", codex.FailureReason(0, new CodexEventStream(), ""));
    }

    // ------------------------------------------------------------------ .cmd sarmalayıcısı ortamı (Hermes bulgusu)
    [Fact]
    public void Cmd_Shim_Env_Is_Preserved_Generic()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "hermes-kisayol");
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        var exe = Path.Combine(dir, "bin", "hermes.exe");
        File.WriteAllText(exe, "MZ");
        var shim = Path.Combine(dir, "hermes.cmd");
        // gerçek hermes.cmd'nin biçimi (set "AD=değer" satırları, sonra hedef çağrısı) + dikkat gerektiren satırlar
        File.WriteAllText(shim, string.Join("\r\n", new[]
        {
            "@echo off",
            "set \"HERMES_HOME=D:\\tools\\hermes-home\"",
            "set \"HERMES_CA_BUNDLE=%dp0%certs\\ca.pem\"",
            "set SSL_CERT_FILE=D:\\tools\\hermes-home\\certs\\hermes-runtime-ca.pem",
            "set \"_prog=node\"",                       // kabuk içi: alınmaz
            "set PATHEXT=%PATHEXT:;.JS;=;%",            // kabuk içi: alınmaz
            "set \"TEHLIKELI=a & calc\"",               // kabuk karakteri: alınmaz
            "set \"COZULEMEZ=%BU_DEGISKEN_YOK_123%\"",  // çözülemez: alınmaz
            "set \"ANTHROPIC_API_KEY=sk-ant-sarmal\"",  // alınır (kısayolun işi) ama Claude başlatmasında silinir
            "\"" + exe + "\" %*",
            "set \"SONRADAN=hayir\"",                   // hedef çağrısından sonrası ilgilendirmez
        }) + "\r\n");

        var loc = AgentLocator.Resolve(shim, "hermes", "yok", "kısayol çözülemedi");
        Assert.Null(loc.Error);
        Assert.Equal(exe, loc.Cmd!.FileName);
        Assert.DoesNotContain("cmd", Path.GetFileName(loc.Cmd.FileName), StringComparison.OrdinalIgnoreCase); // cmd.exe üzerinden ÇALIŞTIRILMAZ
        var e = loc.Cmd.Env!;
        Assert.Equal("D:\\tools\\hermes-home", e["HERMES_HOME"]);
        Assert.Equal(dir + Path.DirectorySeparatorChar + "certs\\ca.pem", e["HERMES_CA_BUNDLE"]);
        Assert.Equal("D:\\tools\\hermes-home\\certs\\hermes-runtime-ca.pem", e["SSL_CERT_FILE"]);
        Assert.False(e.ContainsKey("_prog"));
        Assert.False(e.ContainsKey("PATHEXT"));
        Assert.False(e.ContainsKey("TEHLIKELI"));
        Assert.False(e.ContainsKey("COZULEMEZ"));
        Assert.False(e.ContainsKey("SONRADAN"));

        // faturalı anahtar sarmalayıcıdan da geçmez: Claude başlatması ANTHROPIC_* sildirir
        var st = SozSettings.Load(env.Svc.Db);
        var launch = new ClaudeConnector().BuildStart(loc.Cmd, new AgentRunContext("E:\\p", "x", "", AgentPermissionMode.Careful, 1, 1, st, () => "s.json"));
        Assert.Equal("D:\\tools\\hermes-home", launch.AddEnv["HERMES_HOME"]);
        Assert.False(launch.AddEnv.ContainsKey("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void Real_Npm_Shims_Still_Resolve_And_Add_No_Env()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "npm-kisayol");
        var js = Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js");
        Directory.CreateDirectory(Path.GetDirectoryName(js)!);
        File.WriteAllText(js, "console.log(1)");
        var shim = Path.Combine(dir, "codex.cmd");
        // gerçek npm codex.cmd (bu makineden, 11 Kas 2026)
        File.WriteAllText(shim, string.Join("\r\n", new[]
        {
            "@ECHO off", "GOTO start", ":find_dp0", "SET dp0=%~dp0", "EXIT /b", ":start", "SETLOCAL", "CALL :find_dp0", "",
            "IF EXIST \"%dp0%\\node.exe\" (", "  SET \"_prog=%dp0%\\node.exe\"", ") ELSE (", "  SET \"_prog=node\"", "  SET PATHEXT=%PATHEXT:;.JS;=;%", ")", "",
            "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@openai\\codex\\bin\\codex.js\" %*",
        }) + "\r\n");
        var loc = AgentLocator.Resolve(shim, "codex", CodexConnector.NotFoundText, CodexConnector.ShimText);
        Assert.Null(loc.Error);
        Assert.Equal(Path.GetFullPath(js), loc.Cmd!.PrefixArgs[0]);
        Assert.True(loc.Cmd.Env is null || loc.Cmd.Env.Count == 0);
    }

    [Fact]
    public async Task Shim_Env_Reaches_The_Child_Process()
    {
        using var rig = new Rig();
        var dir = Path.Combine(rig.Env.Root, "sarmal");
        Directory.CreateDirectory(dir);
        File.Copy(FakeCodex, Path.Combine(dir, "sahte-codex.mjs"));
        var shim = Path.Combine(dir, "codex.cmd");
        File.WriteAllText(shim, "@echo off\r\nset \"SAHTE_SARMAL=evet-sarmal\"\r\n\"%~dp0sahte-codex.mjs\" %*\r\n");
        rig.Env.Svc.PutSettings(new JsonObject { ["codexPath"] = shim });
        var job = rig.Job("iş [[sahte:oku]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var log = rig.Log().Single();
        Assert.Equal("evet-sarmal", (string?)log["env"]!["SAHTE_SARMAL"]); // sarmalayıcının ayarı kaybolmadı
    }

    // ------------------------------------------------------------------ Codex koşusu: sahte codex ile uçtan uca
    [Fact]
    public async Task Codex_Run_Passes_Gate_Without_Hooks_Or_User_Config_Writes()
    {
        using var rig = new Rig();
        AddNpmTestCheck(rig);
        var job = rig.Job("Düzelt [[sahte:gecer]]");
        Assert.Equal("codex", job.Agent);
        var run = rig.Rm.Start(job.Id)!;
        Assert.Equal("codex", run.Agent);
        await rig.Finish(run);

        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.Passed, j.State);
        Assert.Equal(Verdicts.Passed, j.LastVerdict);
        var proofs = rig.Proofs(job.Id);
        Assert.Contains(proofs, p => p.trigger == "outer_gate" && p.attempt == 1 && p.verdict == Verdicts.Passed);
        Assert.Equal(1, j.Attempts); // dış kapı turu iş kartında tur olarak sayılır
        // başlangıç ölçümü (ajan başlamadan önce) ve kanıt tabanı Codex işinde de çalıştı
        using (var c = rig.Env.Svc.Db.Open())
        {
            Assert.NotNull(c.Scalar("SELECT baseline_json FROM jobs WHERE id=@j", null, ("@j", job.Id)));
            Assert.True(c.ScalarLong("SELECT COUNT(*) FROM job_base WHERE job_id=@j", null, ("@j", job.Id)) > 0);
            Assert.Equal("codex", c.Scalar("SELECT tool FROM sessions WHERE id=(SELECT session_id FROM jobs WHERE id=@j)", null, ("@j", job.Id)));
        }

        var log = rig.Log().Single();
        var argv = log["argv"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.Equal(new[] { "exec", "--json", "--skip-git-repo-check", "-s", "workspace-write", "-C", rig.ProjectDir, "-c", "approval_policy=\"never\"", "-c", "windows.sandbox=\"unelevated\"", "-" }, argv);
        Assert.True((bool)log["unelevated"]!); // süreç testi: değer çift tırnaklı TOML dizgisi olarak çocuğa ulaştı
        Assert.False((bool)log["sandboxBroken"]!); // bayrak sayesinde komutlar kum havuzunda koştu
        Assert.Contains("Düzelt", (string)log["prompt"]!); // istem standart girdiden geldi
        Assert.Equal(rig.ProjectDir, (string)log["cwd"]!);
        Assert.Null((string?)log["env"]!["OPENAI_API_KEY"]);  // faturalı anahtar geçmedi
        Assert.Null((string?)log["env"]!["CODEX_API_KEY"]);
        Assert.Equal(rig.CodexHome, (string)log["env"]!["CODEX_HOME"]!); // kullanıcının klasörü olduğu gibi, değiştirilmedi
        Assert.Equal(rig.Env.Root, Path.GetDirectoryName(rig.Log().Count > 0 ? rig.CodexLog : rig.Env.Root)); // günlük beklenen yerde

        // Söz kullanıcının Codex ayar klasörüne HİÇBİR ŞEY yazmadı; Söz'ün Claude kanca ayarı da hiç oluşturulmadı
        Assert.Empty(Directory.GetFileSystemEntries(rig.CodexHome));
        Assert.False(File.Exists(Path.Combine(rig.Env.DataDir, "yurutucu", "claude-ayar.json")));
        Assert.False(Directory.Exists(Path.Combine(rig.ProjectDir, ".codex")));
        Assert.False(File.Exists(Path.Combine(rig.ProjectDir, ".claude", "settings.json")));

        // koşu kaydı: ajan, token harcaması (Codex dolar vermez)
        var r = rig.Runs(job.Id).Single();
        Assert.Equal("codex", r.Agent);
        Assert.Equal(1200, r.InputTokens);
        Assert.Equal(800, r.CachedTokens);
        Assert.Equal(150, r.OutputTokens);
        Assert.Equal(40, r.ReasoningTokens);
        Assert.Null(r.CostUsd);
        Assert.Equal(1200, j.Spend!.InputTokens);

        // zaman çizelgesi sade Türkçe, araç adı yok
        using var c2 = rig.Env.Svc.Db.Open();
        var lines = RunStore.Timeline(c2, job.Id, includeDetail: false).Where(i => i.Kind is "progress" or "agent").Select(i => i.Text).ToList();
        Assert.Contains("düşünüyor", lines);
        Assert.Contains("bir komut çalıştırıyor: testler", lines);
        Assert.Contains("bir komut hata verdi", lines); // ilk npm test kaldı
        Assert.DoesNotContain(lines, l => l.Contains("komut çalıştırılamadı")); // bayrak sayesinde kum havuzu çalıştı
        Assert.DoesNotContain(lines, l => l.Contains("powershell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Codex_Failed_Gate_Sends_Back_With_Resume_Then_Passes()
    {
        using var rig = new Rig();
        AddNpmTestCheck(rig);
        var job = rig.Job("Düzelt [[sahte:duzelt]]");
        var run = rig.Rm.Start(job.Id)!;
        await rig.Finish(run); // zincirin tamamını bekler (geri yollama turu dahil)

        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.Passed, j.State);
        var proofs = rig.Proofs(job.Id).Where(p => p.trigger == "outer_gate").ToList();
        Assert.Equal(new[] { (1, Verdicts.Failed), (2, Verdicts.Passed) }, proofs.Select(p => (p.attempt, p.verdict)).ToArray());

        var log = rig.Log();
        Assert.Equal(2, log.Count);
        Assert.False((bool)log[0]["isResume"]!);
        Assert.True((bool)log[1]["isResume"]!);
        var sid = (string)log[1]["resume"]!;
        Assert.False(string.IsNullOrEmpty(sid));
        Assert.Equal(sid, rig.Runs(job.Id)[0].SessionId); // aynı oturum
        Assert.Contains("Söz kanıt kapısı", (string)log[1]["prompt"]!);   // geri yollama metni: kalan kontrol
        Assert.Contains("Testler", (string)log[1]["prompt"]!);
        Assert.Contains("tur 1/3", (string)log[1]["prompt"]!);
        var argv2 = log[1]["argv"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.Equal(new[] { "exec", "resume", "--json", "--skip-git-repo-check", "-c", "sandbox_mode=\"workspace-write\"", "-c", "approval_policy=\"never\"", "-c", "windows.sandbox=\"unelevated\"", sid, "-" }, argv2);
        Assert.True((bool)log[1]["unelevated"]!);

        var runs = rig.Runs(job.Id);
        Assert.Equal(1, runs.Select(r => r.SessionId).Distinct().Count()); // resume aynı kimlikle thread.started'ı yeniden basar: yeni oturum açılmaz
        using (var cs = rig.Env.Svc.Db.Open()) Assert.Equal(1L, cs.ScalarLong("SELECT COUNT(*) FROM sessions WHERE tool='codex'"));
        Assert.Equal(new[] { "start", "gate" }, runs.Select(r => r.Kind).ToArray());
        Assert.All(runs, r => Assert.Equal("codex", r.Agent));
        Assert.Equal(3600, j.Spend!.InputTokens); // iki turun toplamı: 1200 + 2400

        using var c = rig.Env.Svc.Db.Open();
        var notes = RunStore.Timeline(c, job.Id, false).Where(i => i.Kind == "state").Select(i => i.Text).ToList();
        Assert.Contains(notes, n => n.Contains("yardımcıya geri yollandı") && n.Contains("2/3"));
    }

    [Fact]
    public async Task Codex_Gate_Stops_After_Three_Rounds_And_Job_Fails()
    {
        using var rig = new Rig();
        AddNpmTestCheck(rig);
        var job = rig.Job("Düzelt [[sahte:bozuk]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.Failed, j.State);
        Assert.Equal(new[] { 1, 2, 3 }, rig.Proofs(job.Id).Where(p => p.trigger == "outer_gate").Select(p => p.attempt).ToArray());
        Assert.Equal(3, rig.Log().Count); // 1 başlatma + 2 geri yollama; sonsuz döngü yok
        Assert.Equal(3, j.Attempts);
    }

    [Fact]
    public async Task Codex_Test_File_Tampering_Goes_To_User_Decision()
    {
        using var rig = new Rig();
        AddNpmTestCheck(rig);
        File.WriteAllText(Path.Combine(rig.ProjectDir, "hesap.js"), NodeSample.FixedLib.Replace("return a * b;", "return a + b;")); // e2: çarpma bozuk: ilk test (çarpma) atlanınca ajanın hâli yeşil görünür, onaylı test kalır
        var job = rig.Job("Düzelt [[sahte:hile]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.True(j.State == JobStates.AwaitingDecision, $"durum={j.State} kanıtlar=" + string.Join(" | ", rig.Proofs(job.Id).Select(p => $"{p.trigger}/{p.attempt}/{p.verdict}/{p.reason}")) + " günlük=" + rig.Log().Count); // "geçti" DEĞİL
        var proofs = rig.Proofs(job.Id).Where(p => p.trigger == "outer_gate").ToList();
        Assert.Equal(2, proofs.Count); // bir kez geri yollandı, ısrar edince karar kullanıcıya kaldı
        Assert.All(proofs, p => Assert.Equal(Verdicts.Unchecked, p.verdict));
        Assert.All(proofs, p => Assert.True(ProofStore.Reasons.IsTampering(p.reason), p.reason));
        Assert.Equal(2, rig.Log().Count);
        Assert.Contains("kişinin onayladığı testler senin yeni kodunla geçmiyor", (string)rig.Log()[1]["prompt"]!);
        Assert.NotNull(rig.W.Run(j.LastRunId!.Value).SnapshotId); // karar kartı görüntü kimliğini taşır: kabul akışı Codex işinde de aynı
    }

    [Fact]
    public async Task Codex_Question_Waits_For_Reply_And_Reply_Resumes_Same_Session()
    {
        using var rig = new Rig(node: false);
        rig.W.AddCheck(rig.ProjectId, null, "command", "Hep kalır", new JsonObject { ["cmd"] = "exit 1", ["expectExit"] = 0 }, 60, true);
        var job = rig.Job("iş [[sahte:soru]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.AwaitingAnswer, j.State);
        Assert.Empty(rig.Proofs(job.Id)); // soruya kapı uygulanmaz
        var sid = rig.Runs(job.Id).Single().SessionId;
        Assert.False(string.IsNullOrEmpty(sid));

        var reply = rig.Rm.Reply(job.Id, "Birinciyi seç.")!;
        await rig.Finish(reply);
        var log = rig.Log();
        Assert.True(log.Count >= 2); // cevaptan sonra kontrol hep kalır: kapı geri yollamaları da ayrı turlardır
        Assert.True((bool)log[1]["isResume"]!);
        Assert.Equal(sid, (string)log[1]["resume"]!);
        Assert.Equal("Birinciyi seç.", ((string)log[1]["prompt"]!).Trim());
        Assert.Equal("codex", rig.Runs(job.Id).Last().Agent);
    }

    [Fact]
    public async Task Codex_Stop_Kills_The_Process_Tree()
    {
        using var rig = new Rig(node: false);
        var job = rig.Job("uzun [[sahte:uzun]]");
        var run = rig.Rm.Start(job.Id)!;
        for (var i = 0; i < 100 && !File.Exists(rig.TorunFile); i++) await Task.Delay(100);
        Assert.True(File.Exists(rig.TorunFile));
        var torun = int.Parse(File.ReadAllText(rig.TorunFile));
        await Task.Delay(500);
        Assert.True(await rig.Rm.StopAsync(job.Id));
        await rig.Finish(run);
        Assert.Equal(JobStates.Stopped, rig.W.Job(job.Id).State);
        Assert.Equal(RunStatus.Stopped, rig.Runs(job.Id).Single().Status);
        for (var i = 0; i < 50 && ProcessTree.IsAlive(torun, null); i++) await Task.Delay(100);
        Assert.False(ProcessTree.IsAlive(torun, null)); // torun süreç de kapandı (iş nesnesi)
    }

    [Theory]
    [InlineData("hata", "Yardımcı işi yarıda bırakıp durdu")]
    [InlineData("giris", "giriş yap")]
    [InlineData("limit", "kullanım sınırına")]
    [InlineData("erken", "Yardımcı işi yarıda bırakıp durdu")] // ham metin (code-mode host ...) cümleye girmez, teknik ayrıntıya yazılır. turn.completed gelmeden süreç 0 ile bitti: asılı kalmaz, hata olur
    [InlineData("erken1", "Yardımcı işi yarıda bırakıp durdu")]
    public async Task Codex_Failures_Have_Plain_Reasons(string scenario, string expected)
    {
        using var rig = new Rig(node: false);
        var job = rig.Job($"iş [[sahte:{scenario}]]");
        var run = rig.Rm.Start(job.Id)!;
        await rig.Finish(run);
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.StartFailed, j.State);
        Assert.Contains(expected, j.StartError!);
    }

    // ---- "yazdım dedi ama yazmadı": ürünün asıl vaadi, dış kapı yeşil VERMEMELİ
    private static void AddFileCheck(Rig rig) => rig.W.AddCheck(rig.ProjectId, null, "file", "not.txt oluşsun", new JsonObject { ["path"] = "not.txt", ["exists"] = true }, 60, true);

    [Fact]
    public async Task Codex_Says_Written_But_Sandbox_Could_Not_Run_Commands_Is_Not_Green_And_Says_Why()
    {
        using var rig = new Rig(node: false);
        AddFileCheck(rig);
        var job = rig.Job("not.txt yaz [[sahte:yazdim]]"); // komut kum havuzu kurulum hatasıyla düşer, ajan yine de "yazdım" der, turn.completed gelir
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.False(File.Exists(Path.Combine(rig.ProjectDir, "not.txt")));
        Assert.NotEqual(JobStates.Passed, j.State);
        Assert.Equal(JobStates.StartFailed, j.State);
        Assert.Equal(CodexConnector.SandboxBrokenText, j.StartError);
        Assert.Contains("Windows kum havuzu kurulumu eksik", j.StartError!);
        using var c = rig.Env.Svc.Db.Open();
        var lines = RunStore.Timeline(c, job.Id, includeDetail: false).Where(i => i.Kind is "progress" or "agent" or "error").Select(i => i.Text).ToList();
        Assert.Contains("komut çalıştırılamadı", lines);
        Assert.Single(rig.Log()); // sonsuz geri yollama yok
    }

    [Fact]
    public async Task Codex_Says_Written_But_File_Missing_Gate_Fails_Three_Rounds_Never_Green()
    {
        using var rig = new Rig(node: false);
        AddFileCheck(rig);
        var job = rig.Job("not.txt yaz [[sahte:yazdim2]]"); // komut sıradan hatayla düşer, ajan "yazdım" der, dosya yok
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.False(File.Exists(Path.Combine(rig.ProjectDir, "not.txt")));
        Assert.Equal(JobStates.Failed, j.State);
        var proofs = rig.Proofs(job.Id).Where(p => p.trigger == "outer_gate").ToList();
        Assert.Equal(new[] { 1, 2, 3 }, proofs.Select(p => p.attempt).ToArray());
        Assert.All(proofs, p => Assert.Equal(Verdicts.Failed, p.verdict));
        Assert.Equal(3, rig.Log().Count);
    }

    [Fact]
    public void Codex_Real_Event_Shapes_Duplicate_Thread_Started_Noise_And_Failed_Command()
    {
        var t = new CodexEventStream();
        var all = new List<RunLine>();
        void F(string l) => all.AddRange(t.Feed(l));
        F(J(new { type = "thread.started", thread_id = "019a-abc" }));
        F(J(new { type = "turn.started" }));
        F("2026-11-11T12:32:10.701972Z ERROR codex_core::tools::router: error=Failed to create unified exec process"); // JSON değil: yutulur
        F("2026-11-11T12:32:10.9Z  WARN something"); // ERROR bile değil
        F("123"); // geçerli JSON ama nesne değil
        F("{bozuk json");
        Assert.Contains("ERROR codex_core", t.LastNoise);
        Assert.False(t.ResultSeen);
        // exec resume aynı kimlikle yeniden basar: ilk kimlik kalır
        F(J(new { type = "thread.started", thread_id = "019a-abc" }));
        F(J(new { type = "thread.started", thread_id = "baska-kimlik" }));
        Assert.Equal("019a-abc", t.SessionId);
        // item.started + item.completed çifti: satır bir kez, kum havuzu kurulum hatası "komut çalıştırılamadı"
        var cmd = "\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -Command \"Set-Content not.txt x\"";
        F(J(new { type = "item.started", item = new { id = "i1", type = "command_execution", command = cmd, aggregated_output = "", exit_code = (int?)null, status = "in_progress" } }));
        F(J(new { type = "item.completed", item = new { id = "i1", type = "command_execution", command = cmd, aggregated_output = "Failed to create unified exec process: orchestrator_helper_launch_canceled: ShellExecuteExW failed to launch setup helper: 1223", exit_code = -1, status = "failed" } }));
        F(J(new { type = "item.completed", item = new { id = "i2", type = "agent_message", text = "not.txt dosyasına yazdım." } }));
        Assert.True(t.SandboxBroken);
        F(J(new { type = "turn.completed", usage = new { input_tokens = 10, cached_input_tokens = 5, cache_write_input_tokens = 0, output_tokens = 3, reasoning_output_tokens = 0 } }));
        Assert.Equal(new[] { "bir komut çalıştırıyor", "komut çalıştırılamadı", "not.txt dosyasına yazdım." }, all.Select(l => l.Text).ToArray());
        Assert.True(t.ResultSeen);
        Assert.True(t.ResultIsError); // turn.completed geldi ama hiçbir komut koşmadı ve dosya değişikliği yok: sonuç hata sayılır
        Assert.Equal(CodexConnector.SandboxBrokenText, new CodexConnector().FailureReason(0, t, ""));
        Assert.Equal(new AgentUsage(10, 5, 3, 0, null), t.Usage);

        // aynı tur: bir komut gerçekten koştu (exit 1 bile olsa) ya da dosya değişti ise kum havuzu bozuk sayılmaz
        var ok = new CodexEventStream();
        ok.Feed(J(new { type = "item.completed", item = new { id = "a", type = "command_execution", command = "npm test", aggregated_output = "fail", exit_code = 1, status = "failed" } }));
        ok.Feed(J(new { type = "item.completed", item = new { id = "b", type = "command_execution", command = "x", aggregated_output = "orchestrator_helper_launch_canceled", exit_code = -1, status = "failed" } }));
        ok.Feed(J(new { type = "turn.completed", usage = new { input_tokens = 1 } }));
        Assert.False(ok.SandboxBroken);
        Assert.False(ok.ResultIsError);
        var patched = new CodexEventStream();
        patched.Feed(J(new { type = "item.completed", item = new { id = "b", type = "command_execution", command = "x", aggregated_output = "setup helper: 1223", exit_code = -1, status = "failed" } }));
        patched.Feed(J(new { type = "item.completed", item = new { id = "p", type = "file_change", changes = new[] { new { path = "a.js", kind = "update" } }, status = "completed" } }));
        patched.Feed(J(new { type = "turn.completed", usage = new { input_tokens = 1 } }));
        Assert.True(patched.SandboxBroken);
        Assert.False(patched.ResultIsError); // dosya değişti: kapı gerçeği ölçer
        // sıradan komut hatası (exit 1, kurulum izi yok) "bir komut hata verdi" kalır
        var plain = new CodexEventStream();
        var l2 = plain.Feed(J(new { type = "item.completed", item = new { id = "z", type = "command_execution", command = "npm test", aggregated_output = "fail", exit_code = 1, status = "failed" } }));
        Assert.Contains(l2, l => l.Text == "bir komut hata verdi");
        Assert.False(plain.SandboxBroken);
    }

    [Fact]
    public void Codex_Missing_Tool_Gives_Plain_Message_And_Switching_Agent_Works()
    {
        using var rig = new Rig(node: false);
        rig.Env.Svc.PutSettings(new JsonObject { ["codexPath"] = null });
        var saved = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "");
        try
        {
            var job = rig.Job("iş");
            var run = rig.Rm.Start(job.Id)!;
            Assert.Equal(RunStatus.Failed, run.Status);
            Assert.Equal(CodexConnector.NotFoundText, run.FailReason);
            // başlatılamayan işin ajanı değiştirilebilir (oturum yok)
            Environment.SetEnvironmentVariable("PATH", saved);
            rig.Env.Svc.PutSettings(new JsonObject { ["claudePath"] = FakeClaude });
            rig.Rm.SozExePath = typeof(RunManager).Assembly.Location;
            Environment.SetEnvironmentVariable("SAHTE_LOG", Path.Combine(rig.Env.Root, "c.jsonl"));
            var run2 = rig.Rm.Start(job.Id, null, "claude")!;
            Assert.Equal("claude", rig.W.Job(job.Id).Agent);
            Assert.Equal("claude", run2.Agent);
            rig.Rm.WaitAsync(run2.Id).Wait(TimeSpan.FromSeconds(60));
        }
        finally { Environment.SetEnvironmentVariable("PATH", saved); Environment.SetEnvironmentVariable("SAHTE_LOG", null); }
    }

    [Fact]
    public async Task Agent_Cannot_Change_After_The_Job_Talked_To_An_Agent()
    {
        using var rig = new Rig(node: false);
        var job = rig.Job("iş [[sahte:oku]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var ex = Assert.Throws<SozException>(() => rig.Rm.Start(job.Id, null, "claude"));
        Assert.Equal(409, ex.Status);
        Assert.Throws<SozException>(() => rig.Rm.Start(job.Id, null, "gemini"));
    }

    // ------------------------------------------------------------------ şema v10
    [Fact]
    public void Schema_V10_Is_Additive_And_Old_Rows_Read_As_Claude()
    {
        using var env = new TestEnv(copySamples: false);
        var db = env.Svc.Db;
        Assert.True(SozDb.CurrentVersion >= 12); // geri alma tabloları (v12) bu sürümde var; sayı zincire her adım eklendiğinde yükselir
        long jobId;
        using (var c = db.Open())
        {
            foreach (var col in new[] { "agent" })
            {
                Assert.Contains(col, c.Query("PRAGMA table_info(jobs)", r => r.GetString(1)));
                Assert.Contains(col, c.Query("PRAGMA table_info(runs)", r => r.GetString(1)));
            }
            foreach (var col in new[] { "input_tokens", "cached_tokens", "output_tokens", "reasoning_tokens", "cost_usd" })
                Assert.Contains(col, c.Query("PRAGMA table_info(runs)", r => r.GetString(1)));
            // v9'a geri sar: sütunları kaldır, eski biçimde bir iş ve koşu yaz
            foreach (var col in new[] { "agent", "input_tokens", "cached_tokens", "output_tokens", "reasoning_tokens", "cost_usd" }) c.Exec($"ALTER TABLE runs DROP COLUMN {col}");
            c.Exec("ALTER TABLE jobs DROP COLUMN agent");
            c.Exec("PRAGMA user_version=9");
            var pid = c.ScalarLong("SELECT id FROM projects LIMIT 1");
            c.Exec("INSERT INTO jobs(project_id, title, raw_text, prompt_text, prompt_hash, source, state, created_at) VALUES(@p,'eski','eski','','', 'yazi','taslak','2026-11-01T00:00:00.000Z')", null, ("@p", pid));
            jobId = c.ScalarLong("SELECT last_insert_rowid()");
            c.Exec("INSERT INTO runs(job_id, project_id, status, started, kind) VALUES(@j,@p,'done','2026-11-01T00:00:00.000Z','start')", null, ("@j", jobId), ("@p", pid));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var db2 = SozDb.OpenAndMigrate(env.DataDir);
        Assert.Equal(SozDb.CurrentVersion, db2.GetVersion());
        using var c2 = db2.Open();
        var job = JobStore.Get(c2, jobId)!;
        Assert.Equal("claude", job.Agent); // eski işler claude
        Assert.Equal("claude", RunStore.Latest(c2, jobId)!.Agent);
        Assert.Null(job.Spend);
        Assert.Equal("eski", job.Title); // mevcut veri korundu
        Assert.Equal(SozDb.CurrentVersion, SozDb.OpenAndMigrate(env.DataDir).GetVersion()); // ikinci açılış değişiklik yapmaz
    }

    // ------------------------------------------------------------------ ayar ve API
    [Fact]
    public void Settings_Validate_Agent_Keys()
    {
        using var env = new TestEnv(copySamples: false);
        Assert.Null(SozSettings.Validate(new JsonObject { ["defaultAgent"] = "codex" }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["defaultAgent"] = "gemini" }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["defaultAgent"] = 3 }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["codexPath"] = Path.Combine(env.Root, "yok.exe") }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["codexPath"] = "a|b" }));
        Assert.Null(SozSettings.Validate(new JsonObject { ["codexPath"] = FakeCodex }));
        Assert.Equal("claude", SozSettings.Load(env.Svc.Db).DefaultAgent);
        env.Svc.PutSettings(new JsonObject { ["defaultAgent"] = "codex" });
        Assert.Equal("codex", SozSettings.Load(env.Svc.Db).DefaultAgent);
        // varsayılan ajan yeni işe yazılır
        var w = new WorkService(env.Svc.Db);
        var dir = Path.Combine(env.Root, "p"); Directory.CreateDirectory(dir);
        var pid = w.EnsureProjectByPath(dir);
        Assert.Equal("codex", w.CreateJob(pid, "iş").Agent);
        Assert.Equal("claude", w.CreateJob(pid, "iş", agent: "claude").Agent);
        Assert.Throws<SozException>(() => w.CreateJob(pid, "iş", agent: "gemini"));
    }

    private static async Task<(HttpStatusCode code, JsonNode? body)> Call(HttpClient http, string token, HttpMethod m, string url, object? body = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (token.Length > 0) req.Headers.Add("X-Soz-Token", token);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    [Fact]
    public async Task Api_Agents_Jobs_And_Start_Carry_Agent()
    {
        using var rig = new Rig();
        AddNpmTestCheck(rig);
        AgentProbe.ClearCache();
        await using var h = await SozServer.StartAsync(rig.Env.DataDir, 49601);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        async Task<(HttpStatusCode code, JsonNode? body)> Api(HttpMethod m, string url, object? body = null) => await Call(http, h.Token, m, url, body);

        // ayar: claudePath ve codexPath'i sahteye çevir ve GERİ OKUYARAK doğrula
        var put = await Api(HttpMethod.Put, "/api/settings", new { claudePath = FakeClaude, codexPath = FakeCodex });
        Assert.Equal(HttpStatusCode.OK, put.code);
        var settings = JsonNode.Parse(await http.GetStringAsync("/api/settings"))!;
        Assert.Equal(FakeClaude, (string)settings["claudePath"]!);
        Assert.Equal(FakeCodex, (string)settings["codexPath"]!);
        Assert.Equal("claude", (string)settings["defaultAgent"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Put, "/api/settings", new { defaultAgent = "gemini" })).code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Put, "/api/settings", new { codexPath = Path.Combine(rig.Env.Root, "yok.exe") })).code);

        // GET /api/agents
        var ag = (await Api(HttpMethod.Get, "/api/agents")).body!;
        Assert.Equal("claude", (string)ag["defaultAgent"]!);
        var list = ag["agents"]!.AsArray();
        Assert.Equal(new[] { "claude", "codex", "hermes", "opencode", "antigravity" }, list.Select(x => (string)x!["id"]!).ToArray());
        var codex = list.Single(x => (string)x!["id"]! == "codex")!;
        Assert.True((bool)codex["found"]!);
        Assert.True((bool)codex["ready"]!);
        Assert.Contains("sahte", (string)codex["version"]!);
        Assert.Equal("os_sandbox", (string)codex["capabilities"]!["guardTier"]!);
        Assert.Equal("outer_loop", (string)codex["capabilities"]!["turnEnd"]!);
        Assert.Equal("tokens", (string)codex["capabilities"]!["cost"]!);
        Assert.Equal("sınırlı", (string)codex["guardLabel"]!);
        var claude = list.Single(x => (string)x!["id"]! == "claude")!;
        Assert.Equal("native_hook", (string)claude["capabilities"]!["guardTier"]!);
        Assert.Equal("dollars", (string)claude["capabilities"]!["cost"]!);
        Assert.True((bool)claude["isDefault"]!);

        // bulunamayan araç: found=false, sade neden
        await Api(HttpMethod.Put, "/api/settings", new { codexPath = "" });
        var saved = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "");
        try
        {
            AgentProbe.ClearCache();
            var ag2 = (await Api(HttpMethod.Get, "/api/agents")).body!["agents"]!.AsArray().Single(x => (string)x!["id"]! == "codex")!;
            Assert.False((bool)ag2["found"]!);
            Assert.False((bool)ag2["ready"]!);
            Assert.Equal(CodexConnector.NotFoundText, (string)ag2["error"]!);
        }
        finally { Environment.SetEnvironmentVariable("PATH", saved); }
        await Api(HttpMethod.Put, "/api/settings", new { codexPath = FakeCodex });

        // POST /api/jobs: agent
        var created = await Api(HttpMethod.Post, "/api/jobs", new { projectId = rig.ProjectId, rawText = "Düzelt [[sahte:gecer]]", agent = "codex" });
        Assert.Equal(HttpStatusCode.Created, created.code);
        Assert.Equal("codex", (string)created.body!["agent"]!);
        var jobId = (long)created.body["id"]!;
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Post, "/api/jobs", new { projectId = rig.ProjectId, rawText = "x", agent = "gemini" })).code);
        var dflt = await Api(HttpMethod.Post, "/api/jobs", new { projectId = rig.ProjectId, rawText = "varsayılan" });
        Assert.Equal("claude", (string)dflt.body!["agent"]!);

        // POST /start {agent}: bilinmeyen 400; doğru ajanla başlar, koşuda agent görünür
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Post, $"/api/jobs/{jobId}/start", new { agent = "gemini" })).code);
        var st = await Api(HttpMethod.Post, $"/api/jobs/{jobId}/start", new { agent = "codex" });
        Assert.Equal(HttpStatusCode.Accepted, st.code);
        Assert.Equal("codex", (string)st.body!["run"]!["agent"]!);
        for (var i = 0; i < 600 && rig.W.Job(jobId).State is JobStates.Running or JobStates.Given; i++) await Task.Delay(100);
        var jj = (await Api(HttpMethod.Get, $"/api/jobs/{jobId}")).body!;
        Assert.Equal("gecti", (string)jj["state"]!);
        Assert.Equal("codex", (string)jj["agent"]!);
        Assert.True((long)jj["spend"]!["inputTokens"]! > 0);
        var list2 = (await Api(HttpMethod.Get, "/api/jobs")).body!.AsArray();
        Assert.Contains(list2, x => (long)x!["id"]! == jobId && (string)x["agent"]! == "codex");
    }
}
