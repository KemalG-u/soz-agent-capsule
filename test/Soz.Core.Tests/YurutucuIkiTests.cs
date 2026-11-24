using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Denetim 2: yürütücü (kuyruk, devam et, çoklu onay, durdur, kanıt tabanı süreç sonu) ve API sınamaları. Sahte claude: test\sahte-claude\sahte-claude.mjs.</summary>
public class YurutucuIkiTests
{
    private static string FakePath => Path.Combine(TestEnv.RepoRoot, "test", "sahte-claude", "sahte-claude.mjs");
    private static string SozExeStandIn => typeof(RunManager).Assembly.Location;

    private sealed class Rig : IDisposable
    {
        public TestEnv Env = new(copySamples: false);
        public WorkService W;
        public RunManager Rm;
        public string ProjectDir;
        public long ProjectId;
        public string LogFile;
        public string TorunFile;

        public Rig(int maxParallel = 3, bool node = false)
        {
            ProjectDir = node ? NodeSample.Create(Env, "dugum proje") : Path.Combine(Env.Root, "proje klasoru");
            Directory.CreateDirectory(ProjectDir);
            W = new WorkService(Env.Svc.Db);
            ProjectId = W.EnsureProjectByPath(ProjectDir);
            Env.Svc.PutSettings(new JsonObject { ["claudePath"] = FakePath, ["maxParallelRuns"] = maxParallel });
            Rm = new RunManager(Env.Svc.Db) { SozExePath = SozExeStandIn };
            LogFile = Path.Combine(Env.Root, "sahte-log.jsonl");
            TorunFile = Path.Combine(Env.Root, "torun.pid");
            Environment.SetEnvironmentVariable("SAHTE_LOG", LogFile);
            Environment.SetEnvironmentVariable("SAHTE_STATE_DIR", Env.Root);
            Environment.SetEnvironmentVariable("SAHTE_TORUN", TorunFile);
        }

        public JobDto Job(string text) => W.CreateJob(ProjectId, text);

        public async Task<RunDto> Finish(RunDto run)
        {
            await Rm.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(60));
            using var c = Env.Svc.Db.Open();
            return RunStore.Get(c, run.Id)!;
        }

        public List<JsonNode> SahteLog() => File.Exists(LogFile) ? File.ReadAllLines(LogFile).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).ToList() : new();

        public void Dispose()
        {
            try { Rm.Dispose(); } catch (Exception) { }
            Environment.SetEnvironmentVariable("SAHTE_LOG", null);
            Environment.SetEnvironmentVariable("SAHTE_STATE_DIR", null);
            Environment.SetEnvironmentVariable("SAHTE_TORUN", null);
            Environment.SetEnvironmentVariable("SAHTE_KANCALI", null);
            Env.Dispose();
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task WaitState(Rig rig, long jobId, Func<string, bool> ok, int seconds = 60)
    {
        for (var i = 0; i < seconds * 10 && !ok(rig.W.Job(jobId).State); i++) await Task.Delay(100);
    }

    // ------------------------------------------------------------------ Ö-8 onay akışı
    private static (long runId, long projectId) PendingSetup(Rig rig, JobDto job, params (long ev, string summary, string detail)[] approvals)
    {
        using var c = rig.Env.Svc.Db.Open();
        var run = RunStore.Insert(c, job.Id, rig.ProjectId, "start");
        RunStore.SetSession(c, run, "sahte-oturum-1");
        foreach (var a in approvals) RunStore.AddApproval(c, run, job.Id, a.ev, "Bash", a.summary, "h" + a.ev, a.detail);
        RunStore.SetStatus(c, run, RunStatus.WaitingApproval, lastLine: "onay bekliyor", ended: true);
        JobStore.SetState(c, job.Id, JobStates.AwaitingApproval, rig.ProjectId);
        return (run, rig.ProjectId);
    }

    [Fact]
    public async Task O8_Several_Approvals_Are_Answered_One_By_One_Oldest_First_Then_One_Single_Process_Starts()
    {
        using var rig = new Rig();
        var job = rig.Job("iş [[sahte:oku]]");
        PendingSetup(rig, job, (101, "rm -rf eski-a", "tam komut A"), (102, "rm -rf eski-b", "tam komut B"), (103, "git push", "tam komut C"));

        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.AwaitingApproval, j.State);
        Assert.Equal(new long[] { 101, 102, 103 }, j.PendingApprovals!.Select(p => p.EventId).ToArray()); // en eski önce
        Assert.Equal("tam komut A", j.PendingApprovals![0].Detail);

        Assert.Null(rig.Rm.Approve(job.Id, 101, true)); // yalnız kaydedilir
        Assert.Null(rig.Rm.Approve(job.Id, 102, false));
        Assert.Empty(rig.SahteLog());                    // hiç süreç başlamadı
        Assert.Equal(1, rig.W.Job(job.Id).PendingApprovals!.Count);
        Assert.Equal(JobStates.AwaitingApproval, rig.W.Job(job.Id).State);
        Assert.Equal(409, Assert.Throws<SozException>(() => rig.Rm.Approve(job.Id, 101, true)).Status); // zaten cevaplanmış

        var run = rig.Rm.Approve(job.Id, 103, true);
        Assert.NotNull(run);
        await rig.Finish(run!);
        var log = rig.SahteLog();
        Assert.Single(log); // TEK süreç
        var prompt = (string)log[0]["prompt"]!;
        Assert.Contains("rm -rf eski-a", prompt);
        Assert.Contains("rm -rf eski-b", prompt);
        Assert.Contains("git push", prompt);
        Assert.Contains("--resume", log[0]["argv"]!.AsArray().Select(x => (string)x!));
    }

    [Fact]
    public async Task O8_Stop_While_Waiting_For_Approval_Denies_All_And_Job_Becomes_Stopped()
    {
        using var rig = new Rig();
        var job = rig.Job("iş");
        var (runId, _) = PendingSetup(rig, job, (201, "rm -rf a", ""), (202, "rm -rf b", ""));
        Assert.True(rig.Rm.Stop(job.Id));
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.Stopped, j.State);
        Assert.Empty(j.PendingApprovals ?? new());
        Assert.Equal(RunStatus.Stopped, j.Run!.Status);
        using var c = rig.Env.Svc.Db.Open();
        Assert.Equal(2L, c.ScalarLong("SELECT COUNT(*) FROM run_approvals WHERE job_id=@j AND status='denied'", null, ("@j", job.Id)));
        Assert.False(rig.Rm.Stop(job.Id)); // artık yapılacak bir şey yok
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ K-6 sıra ve devam et
    [Fact]
    public async Task K6_Continue_Reopens_Same_Session_With_Resume_And_Job_Goes_Back_To_Running()
    {
        using var rig = new Rig();
        var job = rig.Job("uzun [[sahte:uzun]]");
        var r1 = rig.Rm.Start(job.Id)!;
        for (var i = 0; i < 100 && !File.Exists(rig.TorunFile); i++) await Task.Delay(100);
        Assert.True(rig.Rm.Stop(job.Id));
        await rig.Finish(r1);
        Assert.Equal(JobStates.Stopped, rig.W.Job(job.Id).State);

        var r2 = rig.Rm.Continue(job.Id);
        Assert.NotNull(r2);
        Assert.Equal(JobStates.Running, rig.W.Job(job.Id).State);
        var log = rig.SahteLog();
        for (var i = 0; i < 100 && log.Count < 2; i++) { await Task.Delay(100); log = rig.SahteLog(); }
        Assert.Equal(2, log.Count);
        Assert.Contains("--resume", log[1]["argv"]!.AsArray().Select(x => (string)x!));
        Assert.Equal((string)log[0]["scenario"]!, (string)log[1]["scenario"]!); // aynı oturum, aynı iş
        Assert.Contains("Kaldığın yerden devam et", (string)log[1]["prompt"]!);
        Assert.Contains(rig.W.Timeline(job.Id, false), i => i.Text == "Bu işe devam et.");
        Assert.True(rig.Rm.Stop(job.Id));
        await rig.Finish(r2!);
    }

    [Fact]
    public void K6_Continue_Refuses_Draft_Cancelled_And_Sessionless_Jobs()
    {
        using var rig = new Rig();
        var draft = rig.Job("taslak");
        Assert.Equal(409, Assert.Throws<SozException>(() => rig.Rm.Continue(draft.Id)).Status);
        var cancelled = rig.Job("iptal");
        rig.W.PatchJob(cancelled.Id, JobStates.Cancelled);
        Assert.Equal(409, Assert.Throws<SozException>(() => rig.Rm.Continue(cancelled.Id)).Status);
        var given = rig.Job("verildi"); rig.W.PatchJob(given.Id, JobStates.Given);
        Assert.Equal(409, Assert.Throws<SozException>(() => rig.Rm.Continue(given.Id)).Status); // oturum yok
        Assert.Equal(404, Assert.Throws<SozException>(() => rig.Rm.Continue(99999)).Status);
    }

    [Fact]
    public async Task K6_Queued_Job_Starts_By_Itself_When_Capacity_Opens_And_Can_Be_Taken_Out()
    {
        using var rig = new Rig(maxParallel: 1);
        var a = rig.Job("uzun [[sahte:uzun]]");
        var ra = rig.Rm.Start(a.Id)!;
        var b = rig.Job("ikinci [[sahte:oku]]");
        var c = rig.Job("üçüncü [[sahte:oku]]");
        Assert.Null(rig.Rm.Start(b.Id));
        Assert.Null(rig.Rm.Start(c.Id));
        Assert.Equal(JobStates.Queued, rig.W.Job(b.Id).State);
        Assert.Equal(2, rig.W.Now().Counts.Queued); // iki iş sırada
        Assert.Equal(409, Assert.Throws<SozException>(() => rig.Rm.Start(b.Id)).Status);  // zaten sırada

        Assert.True(rig.Rm.Stop(c.Id)); // sıradan çıkar
        Assert.Equal(JobStates.Stopped, rig.W.Job(c.Id).State);

        Assert.True(rig.Rm.Stop(a.Id));
        await rig.Finish(ra);
        await WaitState(rig, b.Id, s => s != JobStates.Queued);
        Assert.NotEqual(JobStates.Queued, rig.W.Job(b.Id).State);
        var rb = rig.W.Job(b.Id).Run;
        Assert.NotNull(rb);
        await rig.Rm.WaitAsync(rb!.RunId).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(JobStates.Stopped, rig.W.Job(c.Id).State); // çıkarılan iş yeniden başlamadı
    }

    [Fact]
    public void K6_Queued_Order_Is_First_In_First_Out_And_Survives_Restart()
    {
        using var rig = new Rig(maxParallel: 1);
        var a = rig.Job("uzun [[sahte:uzun]]");
        rig.Rm.Start(a.Id);
        var b = rig.Job("b"); var c = rig.Job("c");
        rig.Rm.Start(b.Id); rig.Rm.Start(c.Id);
        using var db = rig.Env.Svc.Db.Open();
        var order = db.Query("SELECT id FROM jobs WHERE state='sirada' ORDER BY queued_at, id", r => r.GetInt64(0));
        Assert.Equal(new[] { b.Id, c.Id }, order.ToArray());
        Assert.NotNull(db.Scalar("SELECT queued_json FROM jobs WHERE id=@j", null, ("@j", b.Id))); // kuyruk kaydı veritabanında: yeniden açılışta kaybolmaz
        rig.Rm.Stop(a.Id);
    }

    // ------------------------------------------------------------------ Ö-5 / K-3 başlatılamadı
    [Fact]
    public async Task O5_Start_Failed_Gets_Its_Own_State_And_Plain_Reason()
    {
        using var rig = new Rig();
        var j = rig.Job("iş [[sahte:hata]]");
        var r = await rig.Finish(rig.Rm.Start(j.Id)!);
        Assert.Equal(RunStatus.Failed, r.Status);
        var job = rig.W.Job(j.Id);
        Assert.Equal(JobStates.StartFailed, job.State);
        Assert.False(string.IsNullOrWhiteSpace(job.StartError));
        Assert.DoesNotContain("sk-", job.StartError!);
        // yeniden başlatınca hata notu temizlenir
        using var c = rig.Env.Svc.Db.Open();
        JobStore.SetState(c, j.Id, JobStates.Running, rig.ProjectId);
        Assert.Null(JobStore.Get(c, j.Id)!.StartError);
    }

    [Fact]
    public void K3_Start_Refuses_Bad_Model_Names_Everywhere()
    {
        using var rig = new Rig();
        var j = rig.Job("iş");
        Assert.Equal(400, Assert.Throws<SozException>(() => rig.Rm.Start(j.Id, "x&echo,PWNED>pwn-model.txt")).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => rig.Rm.Reply(j.Id, "cevap", "a b")).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => rig.Rm.Continue(j.Id, null, "$(calc)")).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => rig.Rm.Approve(j.Id, 1, true, "a|b")).Status);
        Assert.False(File.Exists(Path.Combine(rig.ProjectDir, "pwn-model.txt")));
        // ayarda kayıtlı kötü ad (ayar doğrulamasını atlayan eski kayıt): başlatılamadı, kabuk çalışmaz
        using (var c = rig.Env.Svc.Db.Open()) SozSettings.Save(c, new JsonObject { ["runnerModel"] = "x&echo,PWNED>pwn-model.txt" });
        var r = rig.Rm.Start(j.Id)!;
        Assert.Equal(RunStatus.Failed, r.Status);
        Assert.Contains("Model adı geçersiz", r.FailReason);
        Assert.Equal(JobStates.StartFailed, rig.W.Job(j.Id).State);
        Assert.False(File.Exists(Path.Combine(rig.ProjectDir, "pwn-model.txt")));
    }

    [Fact]
    public void O8_Settings_Keys_ClaudePath_And_MaxParallelRuns_Are_Validated()
    {
        using var env = new TestEnv(copySamples: false);
        Assert.Equal(400, Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["maxParallelRuns"] = 7 })).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["maxParallelRuns"] = 0 })).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["maxParallelRuns"] = 2.5 })).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["claudePath"] = Path.Combine(env.Root, "yok", "claude.exe") })).Status);
        env.Svc.PutSettings(new JsonObject { ["maxParallelRuns"] = 6, ["claudePath"] = FakePath });
        Assert.Equal(6, SozSettings.Load(env.Svc.Db).MaxParallelRuns);
        env.Svc.PutSettings(new JsonObject { ["claudePath"] = "claude" }); // yalın ad PATH'te aranır
        env.Svc.PutSettings(new JsonObject { ["claudePath"] = null });
        Assert.Contains("maxParallelRuns", env.Svc.GetSettings().ToJsonString());
    }

    // ------------------------------------------------------------------ süreç sonu: kanıt tabanı, soru, alt ajan
    [Theory]
    [InlineData("paket", "approved_failed")]
    [InlineData("atla", "approved_failed")]
    public async Task K1_Run_End_Gate_Detects_Tampering_Job_Waits_For_Decision(string scenario, string reason)
    {
        using var rig = new Rig(node: true);
        rig.W.AddCheck(rig.ProjectId, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, true);
        // e2: gerçek hile = ajan sınama düzenini bozar ve kodu bozuk bırakır. "paket": topla bozuk kalır (betik boşaltılınca ajanın hâli yeşil görünür).
        // "atla": topla düzeltilir, çarpma bozulur; ilk test (çarpma) atlanınca ajanın hâli yeşil görünür. İkisinde de onaylı test yeni kodla kalır.
        if (scenario == "atla") File.WriteAllText(Path.Combine(rig.ProjectDir, "hesap.js"), NodeSample.FixedLib.Replace("return a * b;", "return a + b;"));
        var job = rig.Job($"Düzelt [[sahte:{scenario}]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.AwaitingDecision, j.State);
        Assert.NotEqual(JobStates.Passed, j.State);
        var run = rig.W.Runs(null, job.Id)[0];
        Assert.Equal(Verdicts.Unchecked, run.Verdict);
        Assert.Equal(reason, run.ReasonCode);
        Assert.Contains(rig.W.Timeline(job.Id, false), i => i.Kind == "proof" && i.Verdict == Verdicts.Unchecked);
    }

    [Fact]
    public async Task K2_Run_End_Judges_Final_State_Break_After_Passing_Is_Failed()
    {
        using var rig = new Rig(node: true);
        rig.W.AddCheck(rig.ProjectId, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, true);
        var job = rig.Job("Düzelt [[sahte:sonrabozdu]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        Assert.Equal(JobStates.Failed, rig.W.Job(job.Id).State); // ajan düzeltti, sonra bozdu: nihai hüküm son hâl

        var job2 = rig.Job("Düzelt ve sür [[sahte:suren]]");
        NodeSample.Fix(rig.ProjectDir);
        File.WriteAllText(Path.Combine(rig.ProjectDir, "hesap.js"), NodeSample.BrokenLib);
        await rig.Finish(rig.Rm.Start(job2.Id)!);
        Assert.Equal(JobStates.Passed, rig.W.Job(job2.Id).State); // düzeltip sürdürdü, bozmadı
    }

    [Fact]
    public async Task O3_Run_End_Question_Waits_For_Answer_And_Answer_Resumes()
    {
        using var rig = new Rig();
        rig.W.AddCheck(rig.ProjectId, null, "command", "Hep kalır", new JsonObject { ["cmd"] = "exit 1", ["expectExit"] = 0 }, 60, true);
        var job = rig.Job("iş [[sahte:soru]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.AwaitingAnswer, j.State);
        Assert.Contains("Hangisini seçeyim", j.Question);
        Assert.Empty(rig.W.Runs(null, job.Id));
        var r2 = rig.Rm.Reply(job.Id, "Birincisi olsun.");
        Assert.NotNull(r2);
        await rig.Finish(r2!);
        Assert.Contains("Birincisi olsun.", (string)rig.SahteLog()[1]["prompt"]!);
    }

    [Fact]
    public async Task K8_Sub_Agent_Question_Does_Not_Make_Main_Job_Wait_And_Is_Not_In_Timeline()
    {
        using var rig = new Rig(node: true);
        rig.W.AddCheck(rig.ProjectId, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, true);
        var job = rig.Job("Yardımcı kullan [[sahte:altajan]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.NotEqual(JobStates.AwaitingAnswer, j.State);
        Assert.Equal(JobStates.Failed, j.State); // ana ajan düzeltmedi, kapı kaldı dedi
        var texts = rig.W.Timeline(job.Id, false).Select(i => i.Text).ToList();
        Assert.DoesNotContain(texts, t => t.Contains("Alt ajan"));
        Assert.Single(texts, t => t == "bir alt yardımcı çalıştırıyor");
    }

    // ------------------------------------------------------------------ K-8 sahte claude her araçtan önce PreToolUse çağırır
    private static (List<JsonNode> pre, string stdout) RunFake(string scenario, string hookBody, string workDir, string stateDir)
    {
        Directory.CreateDirectory(workDir);
        var hookLog = Path.Combine(stateDir, "pre.log");
        var settings = Path.Combine(stateDir, "ayar.json");
        var cmd = hookBody.Replace("{LOG}", hookLog.Replace('\\', '/'));
        File.WriteAllText(settings, new JsonObject { ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = cmd }) }) } }.ToJsonString());
        var psi = new ProcessStartInfo("node") { WorkingDirectory = workDir, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { FakePath, "-p", "--settings", settings }) psi.ArgumentList.Add(a);
        psi.Environment["SAHTE_KANCALI"] = "1";
        psi.Environment["SAHTE_STATE_DIR"] = stateDir;
        using var p = Process.Start(psi)!;
        p.StandardInput.Write($"iş [[sahte:{scenario}]]");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30000);
        var pre = File.Exists(hookLog) ? File.ReadAllLines(hookLog).Where(l => l.Trim().StartsWith('{')).Select(l => JsonNode.Parse(l)!).ToList() : new();
        return (pre, stdout);
    }

    [Fact]
    public void K8_Fake_Claude_Calls_PreToolUse_Before_Every_Tool()
    {
        using var env = new TestEnv(copySamples: false);
        var (pre, stdout) = RunFake("oku", "cat >> '{LOG}'; echo >> '{LOG}'", Path.Combine(env.Root, "calisma"), env.Root);
        var names = pre.Select(p => (string)p["tool_name"]!).ToList();
        Assert.Equal(new[] { "Read", "Edit", "Write", "Bash" }, names);
        // araç olaylarının sayısı kanca çağrılarına eşit: hiçbir araç kancasız çalışmadı
        var toolEvents = stdout.Split('\n').Count(l => l.Contains("\"tool_use\"") && !l.Contains("\"tool_result\""));
        Assert.Equal(names.Count, toolEvents);
    }

    [Fact]
    public void K8_Fake_Claude_Denied_Tool_Never_Runs()
    {
        using var env = new TestEnv(copySamples: false);
        var deny = "cat >> '{LOG}'; echo >> '{LOG}'; echo '{\"hookSpecificOutput\":{\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"yasak\"}}'";
        var (pre, stdout) = RunFake("oku", deny, Path.Combine(env.Root, "calisma2"), env.Root);
        Assert.Equal(4, pre.Count);                         // dördü de soruldu
        Assert.DoesNotContain("\"tool_use\"", stdout);       // hiçbiri çalıştırılmadı
    }

    [Fact]
    public void K8_Fake_Claude_Sub_Agent_Scenario_Emits_Task_And_Parented_Events()
    {
        using var env = new TestEnv(copySamples: false);
        var (pre, stdout) = RunFake("altajan", "cat >> '{LOG}'; echo >> '{LOG}'", Path.Combine(env.Root, "calisma3"), env.Root);
        Assert.Equal(2, pre.Count(p => (string)p["tool_name"]! == "Task"));
        Assert.Contains("parent_tool_use_id", stdout);
    }

    // ------------------------------------------------------------------ API
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
    public async Task Api_Denetim2_Accept_Continue_Queue_Now_Settings_And_Project_Guards()
    {
        using var rig = new Rig(maxParallel: 1, node: true);
        rig.W.AddCheck(rig.ProjectId, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, true);
        NodeSample.Fix(rig.ProjectDir);
        await using var h = await SozServer.StartAsync(rig.Env.DataDir, 49371);
        h.Runs.SozExePath = SozExeStandIn;
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        async Task<(HttpStatusCode code, JsonNode? body)> Api(HttpMethod m, string url, object? body = null) => await Call(http, h.Token, m, url, body);

        // ayar ve proje koruması: 400 + JSON hata
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Put, "/api/settings", new { maxParallelRuns = 9 })).code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Put, "/api/settings", new { claudePath = Path.Combine(rig.Env.Root, "yok.exe") })).code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Put, "/api/settings", new { runnerModel = "x & calc" })).code);
        var win = await Api(HttpMethod.Post, "/api/projects", new { path = Environment.GetFolderPath(Environment.SpecialFolder.Windows) });
        Assert.Equal(HttpStatusCode.OK, win.code); // kullanıcı girdisi hatası: 200 + ok:false
        Assert.False((bool)win.body!["ok"]!);
        Assert.Equal("yol_reddedildi", (string?)win.body["code"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)win.body["error"]));
        var rel = await Api(HttpMethod.Post, "/api/projects", new { path = "goreli/yol" });
        Assert.Equal(HttpStatusCode.OK, rel.code);
        Assert.Equal("yol_reddedildi", (string?)rel.body!["code"]);
        var missing = await Api(HttpMethod.Post, "/api/projects", new { path = Path.Combine(rig.Env.Root, "yok-klasor") });
        Assert.Equal(HttpStatusCode.OK, missing.code);
        Assert.Equal("klasor_yok", (string?)missing.body!["code"]);
        // var olan bir DOSYANIN yolu: "klasör bulunamadı" değil, sade "bu bir dosya" cümlesi (G-DÜŞÜK-3)
        var dosyaYolu = Path.Combine(rig.Env.Root, "notlar.md");
        File.WriteAllText(dosyaYolu, "x");
        var dosya = await Api(HttpMethod.Post, "/api/projects", new { path = dosyaYolu });
        Assert.Equal(HttpStatusCode.OK, dosya.code);
        Assert.False((bool)dosya.body!["ok"]!);
        Assert.Equal("bu_dosya", (string?)dosya.body["code"]);
        Assert.Equal("Bu bir dosya, klasör değil. Klasörün yolunu yaz.", (string?)dosya.body["error"]);
        Assert.Equal("yol_gerekli", (string?)(await Api(HttpMethod.Post, "/api/projects", new { })).body!["code"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(http, "", HttpMethod.Post, "/api/projects", new { path = rig.ProjectDir })).code); // yetki hatası aynen
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Post, "/api/jobs/1/start", new { model = "x&calc" })).code);

        // karar: paket senaryosu → karar_bekliyor → accept=true → gecti, acceptedByUser
        File.WriteAllText(Path.Combine(rig.ProjectDir, "hesap.js"), File.ReadAllText(Path.Combine(rig.ProjectDir, "hesap.js")).Replace("a + b", "a - b")); // e2: gerçek hile = kod bozuk kalır, betik boşaltılır
        var job = rig.Job("Düzelt [[sahte:paket]]");
        var st = await Api(HttpMethod.Post, $"/api/jobs/{job.Id}/start", new { });
        Assert.Equal(HttpStatusCode.Accepted, st.code);
        await WaitState(rig, job.Id, s => s != JobStates.Running && s != JobStates.Given);
        var jj = (await Api(HttpMethod.Get, $"/api/jobs/{job.Id}")).body!;
        Assert.Equal("karar_bekliyor", (string?)jj["state"]);
        Assert.Equal("approved_failed", (string?)jj["runs"]![0]!["reasonCode"]);   // e2: betik boşaltıldı, kod bozuk: onaylı testler kalır
        Assert.False((bool)jj["acceptedByUser"]!);
        var snap = (string?)jj["runs"]![0]!["snapshotId"];
        Assert.False(string.IsNullOrEmpty(snap)); // karar kartı görüntü kimliğini taşır (denetim 4, E)
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Post, $"/api/jobs/{job.Id}/accept", new { accept = true })).code); // kimliksiz kabul olmaz
        var ac = await Api(HttpMethod.Post, $"/api/jobs/{job.Id}/accept", new { accept = true, snapshotId = snap });
        Assert.Equal(HttpStatusCode.OK, ac.code);
        Assert.Equal("gecti", (string?)ac.body!["job"]!["state"]);
        Assert.True((bool)ac.body["job"]!["acceptedByUser"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Api(HttpMethod.Post, $"/api/jobs/{job.Id}/accept", new { accept = true, snapshotId = snap })).code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api(HttpMethod.Post, $"/api/jobs/{job.Id}/accept", new { })).code);

        // sıra: tek yuva dolu; ikinci iş sirada (run null), /api/now.queued 1
        var a = rig.Job("uzun [[sahte:uzun]]");
        var b = rig.Job("ikinci [[sahte:oku]]");
        Assert.Equal(HttpStatusCode.Accepted, (await Api(HttpMethod.Post, $"/api/jobs/{a.Id}/start", new { })).code);
        var sb = await Api(HttpMethod.Post, $"/api/jobs/{b.Id}/start", new { });
        Assert.Equal(HttpStatusCode.Accepted, sb.code);
        Assert.Null(sb.body!["run"]);
        Assert.Equal("sirada", (string?)sb.body["job"]!["state"]);
        var now = (await Api(HttpMethod.Get, "/api/now")).body!;
        Assert.Equal(1, (int)now["counts"]!["jobsRunning"]!);
        Assert.Equal(1, (int)now["counts"]!["queued"]!);

        // durdur → sıradaki otomatik başlar; "Bu işe devam et" durdurulan işte çalışır
        for (var i = 0; i < 100 && !File.Exists(rig.TorunFile); i++) await Task.Delay(100);
        await Task.Delay(700); // ajanın oturum kimliği kaydedilsin
        Assert.True((bool)(await Api(HttpMethod.Post, $"/api/jobs/{a.Id}/stop", new { })).body!["stopped"]!);
        await WaitState(rig, b.Id, s => s != JobStates.Queued);
        Assert.NotEqual(JobStates.Queued, rig.W.Job(b.Id).State);
        await WaitState(rig, b.Id, s => s is not (JobStates.Running or JobStates.Given));
        await WaitState(rig, a.Id, s => s == JobStates.Stopped);
        var ct = await Api(HttpMethod.Post, $"/api/jobs/{a.Id}/continue", new { });
        Assert.True(ct.code == HttpStatusCode.Accepted, $"continue: {ct.code} {ct.body}");
        Assert.Equal("running", (string?)ct.body!["run"]!["status"]);
        Assert.Equal(HttpStatusCode.Conflict, (await Api(HttpMethod.Post, $"/api/jobs/{a.Id}/start", new { })).code);
        await Api(HttpMethod.Post, $"/api/jobs/{a.Id}/stop", new { });
        await WaitState(rig, a.Id, s => s == JobStates.Stopped);
    }
}
