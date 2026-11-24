using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Cli;
using Soz.Core.Data;
using Soz.Core.Guard;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Morning;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Core.Security;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Denetim 3 düzeltmeleri. İlke 1 (varsayılan kuşku: bütün dosya anlık görüntüsü, başlangıç ölçümü), İlke 2 (kabul ve ayar yalnız insandan: çağıran kimliği, CLI reddi, bekçi),
/// Y4 (zaman çizgisi alanları), Y6 (bağlantı hedefi), Y7 (sen onayladın ayrı sayılır), Y8 (sade ayrıntı, gördüm yalnız açık çağrıyla).
/// </summary>
public class DenetimUcTests
{
    private static string Hook(TestEnv env, string ev, string stdin, Func<string, string?>? envFn = null)
    {
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run(ev, new StringReader(stdin), o, env.DataDir, envFn ?? (_ => null)));
        return o.ToString();
    }

    private static string J(object o) => JsonSerializer.Serialize(o);

    private static string StopInput(string session, string cwd, bool active = false)
        => J(new Dictionary<string, object?> { ["session_id"] = session, ["transcript_path"] = "t.jsonl", ["cwd"] = cwd, ["hook_event_name"] = "Stop", ["stop_hook_active"] = active, ["last_assistant_message"] = "Bitti, testler geçti." });

    private static void SetTestScript(string dir, string script)
        => File.WriteAllText(Path.Combine(dir, "package.json"), "{ \"name\": \"ornek\", \"version\": \"1.0.0\", \"private\": true, \"scripts\": { \"test\": \"" + script + "\" } }");

    /// <summary>Düzeltilmiş örnek proje; extra() iş başlamadan ÖNCE klasöre dosya ekler (taban bunları içerir).</summary>
    private static (WorkService w, long pid, string dir, JobDto job) Setup(TestEnv env, string name, Action<string>? extra = null)
    {
        var dir = NodeSample.Create(env, name);
        NodeSample.Fix(dir);
        extra?.Invoke(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, enabled: true);
        var job = w.CreateJob(pid, "Toplamayı incele: " + name);
        w.PatchJob(job.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = "u-" + name, cwd = dir, prompt = job.PromptText }));
        Assert.Equal(JobStates.Running, w.Job(job.Id).State);
        return (w, pid, dir, job);
    }

    /// <summary>Sınama ayarını (üye olmayan ayar dosyası) değiştirip iki kez durdurur: iş karar bekliyor durumuna gelir.</summary>
    private static (WorkService w, long pid, string dir, JobDto job) AwaitingDecision(TestEnv env, string name)
    {
        var s = Setup(env, name);
        // e3: ayar dosyası eklemek artık alarm değil. Karar bekletmek için gerçek hile: testi gevşet ve kodu boz (ajanın bıraktığı hâl yeşil, onaylı testler kalır).
        File.WriteAllText(Path.Combine(s.dir, "hesap.js"), NodeSample.BrokenLib);
        File.WriteAllText(Path.Combine(s.dir, "test", "hesap.test.js"), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        Hook(env, "stop", StopInput("u-" + name, s.dir));
        Hook(env, "stop", StopInput("u-" + name, s.dir, active: true));
        Assert.Equal(JobStates.AwaitingDecision, s.w.Job(s.job.Id).State);
        return s;
    }

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction açılamadı");
    }

    // =================================================================== İLKE 2: çağıran kimliği
    [Fact]
    public void Accept_Is_Refused_While_Agent_Process_Is_Alive_Even_For_Human_Caller()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, _, job) = AwaitingDecision(env, "canli");
        using (var c = env.Svc.Db.Open())
        {
            var runId = RunStore.Insert(c, job.Id, job.ProjectId, "start");
            using var me = Process.GetCurrentProcess();
            RunStore.SetProcess(c, runId, me.Id, me.StartTime.ToUniversalTime()); // canlı süreç: bu test süreci
        }
        var ex = Assert.Throws<SozException>(() => w.Accept(job.Id, true));
        Assert.Equal(409, ex.Status);
        Assert.Contains("Yardımcı hâlâ çalışıyor", ex.Message);
        Assert.Equal(JobStates.AwaitingDecision, w.Job(job.Id).State);
        var ex2 = Assert.Throws<SozException>(() => w.Accept(job.Id, false));
        Assert.Equal(409, ex2.Status);
    }

    [Fact]
    public void Accept_Is_Recorded_With_Channel_And_Unrecorded_Acceptance_Is_Marked_Unverified()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, _, job) = AwaitingDecision(env, "kayit");
        var j = w.Accept(job.Id, true, new CallerInfo(CallerKind.Human, 4242, "soz", "deneme"));
        Assert.Equal(JobStates.Passed, j.State);
        Assert.True(j.AcceptedByUser);
        Assert.False(j.AcceptedUnverified);
        Assert.NotNull(j.AcceptedVia);
        using (var c = env.Svc.Db.Open())
        {
            var d = JsonNode.Parse(c.Scalar("SELECT detail FROM events WHERE kind='job_accepted' ORDER BY id DESC LIMIT 1") as string ?? "{}")!;
            Assert.Equal(4242, (int?)d["pid"]);
            Assert.Equal("soz", (string?)d["process"]);
            // kabul olayı yok edilirse (doğrudan veritabanı değişikliği) iş "kanıtsız kabul" diye işaretlenir
            c.Exec("DELETE FROM events WHERE kind='job_accepted'");
        }
        Assert.True(w.Job(job.Id).AcceptedUnverified);
    }

    [Fact]
    public void CliGuard_Refuses_Writing_Commands_For_Agents_And_Allows_Reading_Hooks_And_Humans()
    {
        using var env = new TestEnv(copySamples: false);
        Func<string, string?> agentEnv = k => k == "SOZ_RUN_ID" ? "7" : null;
        Func<string, string?> claudeEnv = k => k == "CLAUDECODE" ? "1" : null;
        Func<string, string?> humanEnv = _ => null;
        var empty = new HashSet<string>();
        foreach (var (cmd, args) in new[] { ("job", new[] { "accept", "3" }), ("job", new[] { "reject", "3" }), ("gate", new[] { "off" }), ("checks", new[] { "disable", "2" }),
                     ("guard", new[] { "apply", "temkinli" }), ("hooks", new[] { "install" }), ("serve", Array.Empty<string>()), ("import", Array.Empty<string>()), ("bilinmeyen", Array.Empty<string>()) })
        {
            var a = Args.Parse(args, empty);
            Assert.Equal(CallerIdentity.HumanOnlyMessage, CliGuard.Check(cmd, a, env.DataDir, agentEnv));
            Assert.Equal(CallerIdentity.HumanOnlyMessage, CliGuard.Check(cmd, a, env.DataDir, claudeEnv));
            Assert.Null(CliGuard.Check(cmd, a, env.DataDir, humanEnv)); // insan: çalışır
        }
        foreach (var (cmd, args) in new[] { ("hook", new[] { "stop" }), ("status", Array.Empty<string>()), ("job", new[] { "show", "3" }), ("morning", Array.Empty<string>()), ("checks", new[] { "list" }), ("guard", new[] { "test" }) })
            Assert.Null(CliGuard.Check(cmd, Args.Parse(args, empty), env.DataDir, agentEnv));
    }

    [Fact]
    public void CallerIdentity_Self_Agent_Environment_And_Plain_Process()
    {
        using var env = new TestEnv(copySamples: false);
        Assert.Equal(CallerKind.Agent, CallerIdentity.ClassifySelf(env.DataDir, k => k == "SOZ_JOB_ID" ? "3" : null).Kind);
        Assert.Equal(CallerKind.Agent, CallerIdentity.ClassifySelf(env.DataDir, k => k == "CLAUDE_CODE_ENTRYPOINT" ? "cli" : null).Kind);
        Assert.Equal(CallerKind.Human, CallerIdentity.ClassifySelf(env.DataDir, _ => null).Kind); // testlerde ata zinciri kapalı (TestInit)
        Assert.NotEqual(CallerIdentity.AgentJobName(env.DataDir), CallerIdentity.AgentJobName(Path.Combine(env.Root, "baska-veri")));
    }

    /// <summary>Gerçek süreçler: ajan işinin içinde başlatılan node, sunucuya yazma ve oturum isteğinde 403 alır; iş dışından başlatılan node geçer.</summary>
    [Fact]
    public async Task Server_Refuses_Real_Agent_Job_Processes_And_Serves_Human_Processes()
    {
        using var env = new TestEnv(copySamples: false);
        await using var h = await SozServer.StartAsync(env.DataDir, 49350);
        var script = Path.Combine(env.Root, "istek.mjs");
        File.WriteAllText(script, """
            const [,, base, path, method] = process.argv
            try {
              const r = await fetch(base + path, { method, headers: { 'content-type': 'application/json' }, body: method === 'POST' ? '{"accept":true}' : undefined })
              console.log('KOD ' + r.status + ' ' + (await r.text()).slice(0, 120))
            } catch (e) { console.log('HATA ' + e.message) }
            """);

        async Task<string> RunAgent(string path, string method)
        {
            var lines = new List<string>();
            using var t = ProcessTree.StartStreaming("node", new[] { script, h.Url, path, method }, env.Root, null, null, l => { lock (lines) lines.Add(l); }, null, null, env.DataDir);
            await t.WaitExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(100);
            lock (lines) return string.Join("\n", lines);
        }

        async Task<string> RunHuman(string path, string method)
        {
            var psi = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { script, h.Url, path, method }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await p.WaitForExitAsync();
            return o;
        }

        // ajan: kabul ucu, oturum ucu (belirteç) ve ayar ucu 403
        var a1 = await RunAgent("/api/jobs/1/accept", "POST");
        Assert.Contains("KOD 403", a1);
        Assert.Contains(CallerIdentity.HumanOnlyMessage, a1);
        Assert.Contains("KOD 403", await RunAgent("/api/session", "GET"));
        Assert.Contains("KOD 403", await RunAgent("/api/settings", "PUT"));
        // insan: kapıyı geçer (iş yok: 404/400), oturum ucu 200
        var h1 = await RunHuman("/api/session", "GET");
        Assert.Contains("KOD 200", h1);
        var h2 = await RunHuman("/api/jobs/999/accept", "POST");
        Assert.DoesNotContain("KOD 403", h2);
        // karşı ucu bulunamayan bağlantı da reddedilir değil, insan sayılmaz kuralı: kimlik "Unknown" ise yazma 403 olur (burada gerçek süreç bulundu)
        using (var c = env.Svc.Db.Open())
            Assert.True(c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='caller_denied'") >= 3);
    }

    [Fact]
    public void CallerIdentity_Classifies_A_Process_In_The_Agent_Job_And_Not_This_One()
    {
        using var env = new TestEnv(copySamples: false);
        using var t = ProcessTree.StartStreaming("node", new[] { "-e", "setTimeout(()=>{},4000)" }, env.Root, null, null, _ => { }, null, null, env.DataDir);
        try
        {
            Assert.Equal(CallerKind.Agent, CallerIdentity.ClassifyPid(t.Pid, env.DataDir).Kind);
            Assert.Equal(CallerKind.Human, CallerIdentity.ClassifyPid(Environment.ProcessId, env.DataDir).Kind);
            Assert.True(CallerIdentity.IsInAgentJob(t.Pid, env.DataDir));
            Assert.False(CallerIdentity.IsInAgentJob(t.Pid, Path.Combine(env.Root, "baska-veri")));
        }
        finally { t.KillTree(); }
    }

    // ---------- bekçi: Söz'ün kendisi
    private static string PreTool(string cmd, string cwd) => J(new { session_id = "g", cwd, tool_name = "Bash", tool_input = new { command = cmd }, hook_event_name = "PreToolUse" });

    [Fact]
    public void Guard_Blocks_Soz_Self_Use_In_Soz_Runs_Even_Without_Rules_And_Allows_Hook_And_Other_Local_Servers()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "bekci");
        var w = new WorkService(env.Svc.Db);
        w.EnsureProjectByPath(dir); // hiç bekçi kuralı yok
        Func<string, string?> run = k => k == "SOZ_RUN_ID" ? "5" : k == "SOZ_JOB_ID" ? "9" : null;
        foreach (var cmd in new[] { "soz job accept 3", "E:/x/yayin/soz.exe checks disable 1", "curl -s http://127.1:49300/api/session", "curl http://[::1]:49300/api/jobs/1/accept -X POST",
                     "node -e \"fetch('http://2130706433:49300/api/session')\"", "sqlite3 " + env.DataDir.Replace('\\', '/') + "/soz.db .tables", "Soz-Masaustu.exe --selftest" })
        {
            var o = Hook(env, "pre-tool-use", PreTool(cmd, dir), run);
            Assert.Contains("\"deny\"", o);
        }
        Assert.Equal("", Hook(env, "pre-tool-use", PreTool("soz hook stop", dir), run));
        Assert.Equal("", Hook(env, "pre-tool-use", PreTool("curl http://localhost:3000/api/users", dir), run));
        Assert.Equal("", Hook(env, "pre-tool-use", PreTool("npm test", dir), run));
        // Söz koşusu dışında (terminal) kural yoksa bekçi susar; kural varsa şablon engeller
        Assert.Equal("", Hook(env, "pre-tool-use", PreTool("soz job accept 3", dir)));
    }

    [Fact]
    public void Guard_Template_Carries_Soz_Self_Rule_And_Blocks_It_In_Terminal_Too()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "sablon");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        using (var c = env.Svc.Db.Open()) { GuardStore.EnsureTables(c); GuardStore.ApplyTemplate(c, pid, "temkinli"); }
        Assert.Contains("\"deny\"", Hook(env, "pre-tool-use", PreTool("soz job accept 3", dir)));
        Assert.Contains("\"deny\"", Hook(env, "pre-tool-use", PreTool("curl http://127.0.0.1:49300/api/session", dir)));
        Assert.Equal("", Hook(env, "pre-tool-use", PreTool("soz hook stop", dir)));
    }

    // =================================================================== İLKE 1: varsayılan kuşku
    [Fact]
    public void Scan_Sees_Root_Test_File_Npmrc_And_Counts_Harmless_Files_Without_Hashing_Them()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "tara");
        File.WriteAllText(Path.Combine(dir, ".npmrc"), "foo=1\n");
        File.WriteAllText(Path.Combine(dir, "hesap-test.js"), "// kök test\n");
        File.WriteAllText(Path.Combine(dir, "notlar.txt"), "zararsız");
        File.WriteAllText(Path.Combine(dir, "resim.png"), "x");
        var s = EvidenceBase.Scan(dir);
        Assert.Contains(s.Suspects, f => f.Path == ".npmrc");
        var rootTest = Assert.Single(s.Suspects, f => f.Path == "hesap-test.js");
        Assert.Equal(EvidenceBase.ClsTest, rootTest.Cls);
        Assert.DoesNotContain(s.Suspects, f => f.Path is "notlar.txt" or "resim.png");
        Assert.True(s.FileCount >= 7);
        Assert.False(s.NotTracked);
    }

    [Fact]
    public void Scan_5001_Files_Is_Fully_Tracked_And_A_Late_File_Is_Seen()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "genis");
        var many = Path.Combine(dir, "veri");
        Directory.CreateDirectory(many);
        for (var i = 0; i < 5001; i++) File.WriteAllText(Path.Combine(many, $"d{i:D5}.txt"), "x");
        var before = EvidenceBase.Scan(dir);
        Assert.False(before.NotTracked);
        Assert.True(before.FileCount >= 5004);
        File.WriteAllText(Path.Combine(many, "zzz-son.test.js"), "// geç eklenen test\n");
        File.WriteAllText(Path.Combine(dir, ".npmrc"), "x=1\n");
        var d = EvidenceBase.Compare(before, EvidenceBase.Scan(dir));
        Assert.Contains(".npmrc", d.ChangedConfig.Concat(d.AddedTests).Concat(d.ChangedTests));
        Assert.Contains("veri/zzz-son.test.js", d.AddedTests.Concat(d.ChangedTests));
    }

    [Fact]
    public void Npmrc_Edit_By_Agent_Is_Never_Passed_And_Reports_The_File()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "npmrc");
        File.WriteAllText(Path.Combine(dir, ".npmrc"), "ignore-scripts=true\n");
        Hook(env, "stop", StopInput("u-npmrc", dir));
        var r = w.Runs(null, job.Id)[0];
        // e3: ayar dosyası eklemek alarm değildir. .npmrc koşucu tanımında okunabilen ayardır: onaylı koşuda kopyadan çıkarılır, sonuç onaylı ayarlarla alınır, kişiye not düşülür.
        Assert.Equal(Verdicts.Passed, r.Verdict);
        Assert.Null(r.ReasonCode);
        Assert.Contains(".npmrc", r.Summary);
        Assert.Contains(r.Diffs!, d => d.Path == ".npmrc" && d.Kind == "config-info");
        Assert.Equal(JobStates.Passed, w.Job(job.Id).State);
    }

    [Fact]
    public void Root_Test_File_Edited_By_Agent_Is_Judged_By_The_Approved_Version_Not_Tests_Changed()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "koktest", d => File.WriteAllText(Path.Combine(d, "hesap-test.js"), "// mevcut kök test\n"));
        File.WriteAllText(Path.Combine(dir, "hesap-test.js"), "// ajan boşalttı\n");
        Hook(env, "stop", StopInput("u-koktest", dir));
        var r = w.Runs(null, job.Id)[0];
        // e2: ajanın düzenlediği test yargıç değildir; onaylı hâli geçici kopyada koşar, kod doğru olduğu için geçer ve not düşülür.
        Assert.Equal(Verdicts.Passed, r.Verdict);
        Assert.Null(r.ReasonCode);
        Assert.Contains("Yardımcı onayladığın testlere de dokundu. Sonuç senin onayladığın testlerle alındı.", r.Summary);
        Assert.Contains(r.Diffs!, d => d.Kind == "test" && d.Path == "hesap-test.js");
    }

    [Fact]
    public void Node_Test_Flag_File_Change_Is_Caught_And_Tamper_Is_Not_Inherited_By_The_Next_Job()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, pid, dir, job) = Setup(env, "miras");
        File.WriteAllText(Path.Combine(dir, "hesap.js"), NodeSample.BrokenLib);
        File.WriteAllText(Path.Combine(dir, "test", "hesap.test.js"), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));   // e3: gevşetilmiş test + bozuk kod
        Hook(env, "stop", StopInput("u-miras", dir));
        Hook(env, "stop", StopInput("u-miras", dir, active: true));
        Assert.Equal(JobStates.AwaitingDecision, w.Job(job.Id).State);
        w.Accept(job.Id, false); // kullanıcı reddetti, bozuk tanım klasörde kaldı
        var j2 = w.CreateJob(pid, "İkinci iş");
        w.PatchJob(j2.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = "u-miras-2", cwd = dir, prompt = j2.PromptText }));
        Hook(env, "stop", StopInput("u-miras-2", dir));
        var r = w.Runs(null, j2.Id)[0];
        Assert.Equal(Verdicts.Unchecked, r.Verdict); // miras alınan bozuk taban "geçti" üretmez
        Assert.Equal("approved_failed", r.ReasonCode);
        // e3 (K2): ikinci işte ajan testlere dokunmadı, gevşetme önceki işten kalmış: cümle ajanı suçlamaz.
        Assert.Contains("Yardımcı testlere dokunmadı, sorun kodda görünüyor.", r.Summary);
    }

    [Fact]
    public void Harmless_File_Change_Does_Not_Raise_False_Alarm()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "zararsiz");
        File.WriteAllText(Path.Combine(dir, "notlar.md"), "ajan not aldı");
        File.WriteAllText(Path.Combine(dir, "hesap.js"), NodeSample.FixedLib + "// yorum\n");
        Hook(env, "stop", StopInput("u-zararsiz", dir));
        var r = w.Runs(null, job.Id)[0];
        Assert.Equal(Verdicts.Passed, r.Verdict);
        Assert.Equal(JobStates.Passed, w.Job(job.Id).State);
    }

    [Fact]
    public void Installing_Terminal_Hooks_Does_Not_Make_The_Next_Job_Suspicious()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, pid, dir, job) = Setup(env, "kancakur");
        Hook(env, "stop", StopInput("u-kancakur", dir)); // geçer: proje tabanı onaylanır
        Assert.Equal(JobStates.Passed, w.Job(job.Id).State);
        w.InstallTerminalHooks(pid, typeof(RunManager).Assembly.Location); // Söz kendi .claude/settings.json dosyasını yazar
        Assert.True(File.Exists(Path.Combine(dir, ".claude", "settings.json")));
        var j2 = w.CreateJob(pid, "İkinci işi incele: kanca kurulduktan sonra");
        w.PatchJob(j2.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = "u-kancakur-2", cwd = dir, prompt = j2.PromptText }));
        Assert.Null(w.Job(j2.Id).BaseNote);
        Hook(env, "stop", StopInput("u-kancakur-2", dir));
        var r = w.Runs(null, j2.Id)[0];
        Assert.Equal(Verdicts.Passed, r.Verdict);
        // ama ajan o dosyayı sonradan değiştirirse not düşer (e3: üye olmayan ayar dosyası alarm değil)
        var j3 = w.CreateJob(pid, "Üçüncü işi incele");
        w.PatchJob(j3.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = "u-kancakur-3", cwd = dir, prompt = j3.PromptText }));
        File.AppendAllText(Path.Combine(dir, ".claude", "settings.json"), "\n");
        Hook(env, "stop", StopInput("u-kancakur-3", dir));
        var r3 = w.Runs(null, j3.Id)[0];
        Assert.Equal(Verdicts.Passed, r3.Verdict);
        Assert.Contains(r3.Diffs!, d => d.Path == ".claude/settings.json" && d.Kind == "config-info");
    }

    [Fact]
    public void Unreadable_Folder_Is_Not_Tracked_And_Never_Passed()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "izlenmez");
        using (var c = env.Svc.Db.Open()) c.Exec("DELETE FROM job_base WHERE job_id=@j", null, ("@j", job.Id)); // iş başlarken görüntü alınamamış gibi
        Hook(env, "stop", StopInput("u-izlenmez", dir));
        var r = w.Runs(null, job.Id)[0];
        Assert.Equal(Verdicts.Unchecked, r.Verdict);
        Assert.Equal("no_base", r.ReasonCode);
        Assert.Contains("başlangıç görüntüsü yok", r.VerdictLabel);
        Assert.NotEqual(JobStates.Passed, w.Job(job.Id).State);
    }

    [Fact]
    public void Baseline_Text_Composes_Plain_Turkish_Sentences()
    {
        Assert.Equal("1'i", BaselineText.Poss(1));
        Assert.Equal("2'si", BaselineText.Poss(2));
        Assert.Equal("3'ü", BaselineText.Poss(3));
        Assert.Equal("5'i", BaselineText.Poss(5));
        Assert.Equal("10'u", BaselineText.Poss(10));
        Assert.Equal("20'si", BaselineText.Poss(20));
        var b = new BaselineText.Baseline("2026-11-10T12:00:00Z", Verdicts.Failed, new() { new BaselineText.Check("k", "Testler", ResultStatus.Failed, 2, 1, 0) });
        var outcomes = new List<CheckOutcome> { new(1, "Testler", ResultStatus.Passed, 0, null, null, 10, 0, null, 2, 2) };
        var text = BaselineText.Compose(b, outcomes, Verdicts.Passed);
        Assert.Contains("Başlamadan önce 2 testten 1'i geçmiyordu", text);
        Assert.Contains("Şimdi 2'si de geçiyor", text);
        var round = BaselineText.Parse(BaselineText.Serialize(b))!;
        Assert.Equal(2, round.Total);
    }

    [Fact]
    public void Tap_Zero_Skip_Is_A_No_Test_Marker()
    {
        Assert.Equal("tap", TestSummary.NoTestMarker("TAP version 13\n1..0 # SKIP no tests\n"));
        Assert.Equal("tap", TestSummary.NoTestMarker("1..0\n"));
        Assert.Null(TestSummary.NoTestMarker("1..3\nok 1\nok 2\nok 3\n"));
    }

    // =================================================================== Y4: zaman çizgisi alanları
    [Fact]
    public void Timeline_Proof_Item_Carries_Reason_Code_Text_And_Changed_Files()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, _, job) = AwaitingDecision(env, "cizgi");
        var items = w.Timeline(job.Id, detail: false);
        var proof = items.Last(i => i.Kind == "proof");
        // e3: karar bekleten hâl artık ayar dosyası değil, onaylı testlerin yeni kodla geçmemesi (neden kodu approved_failed).
        Assert.Equal("approved_failed", proof.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(proof.ReasonText));
        Assert.False(string.IsNullOrWhiteSpace(proof.VerdictLabel));
        // arayüzün okuduğu JSON anahtarları
        var json = JsonSerializer.SerializeToNode(proof, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("approved_failed", (string?)json["reasonCode"]);
        Assert.NotNull(json["reasonText"]);
    }

    // =================================================================== Y7: sen onayladın
    [Fact]
    public void Morning_And_Now_Count_User_Accepted_Jobs_Separately()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, _, job) = AwaitingDecision(env, "onay");
        w.Accept(job.Id, true);
        var m = w.Morning(null);
        Assert.Empty(m.Passed);
        Assert.Single(m.AcceptedByYou!);
        Assert.Contains("sen onayladın: 1", m.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deneme sonucuyla bitti", m.Sentence);
        var n = w.Now();
        Assert.Equal(0, n.Counts.JobsPassed);
        Assert.Equal(1, n.Counts.JobsAccepted);
        Assert.Contains("sen onayladın: 1", n.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    // =================================================================== Y8
    [Fact]
    public void Translator_Detail_Is_Plain_Text_Not_Raw_Json_And_Errors_Are_Plain()
    {
        var t = new RunEventTranslator();
        string Tool(string name, object input, string id) => JsonSerializer.Serialize(new { type = "assistant", message = new { role = "assistant", content = new[] { new { type = "tool_use", id, name, input } } } });
        var lines = new List<RunLine>();
        lines.AddRange(t.Feed(Tool("Grep", new { pattern = "topla", path = "src" }, "g1")));
        lines.AddRange(t.Feed(Tool("Read", new { file_path = @"E:\proje\hesap.js" }, "r1")));
        lines.AddRange(t.Feed(Tool("Bash", new { command = "npm test" }, "b1")));
        lines.AddRange(t.Feed("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"b1","is_error":true,"content":"npm ERR! test failed"}]}}"""));
        Assert.Equal("Arama: topla (src)", lines[0].Detail);
        Assert.Equal(@"Dosya: E:\proje\hesap.js", lines[1].Detail);
        Assert.Equal("Komut: npm test", lines[2].Detail);
        Assert.Equal("Hata: npm ERR! test failed", lines[3].Detail);
        Assert.All(lines, l => { Assert.DoesNotContain("tool_use", l.Detail); Assert.DoesNotContain("{", l.Detail); });
    }

    [Fact]
    public void Reading_Or_Opening_A_Job_Does_Not_Mark_It_Seen_Only_The_Ack_Does()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, _, job) = AwaitingDecision(env, "gordum");
        w.Accept(job.Id, false); // "kaldı": görülmemiş
        Assert.Contains(w.Now().WaitingJobs!, j => j.Id == job.Id);
        _ = w.Job(job.Id); _ = w.Jobs(); _ = w.Timeline(job.Id, true); // okumak işareti silmez
        Assert.Contains(w.Now().WaitingJobs!, j => j.Id == job.Id);
        w.MarkSeen(job.Id); // eski "işi açtım" çağrısı (k1): bakmak kapatmak değildir
        Assert.Contains(w.Now().WaitingJobs!, j => j.Id == job.Id);
        var att = new Soz.Core.Attention.AttentionService(env.Svc.Db);
        att.Ack(att.Snapshot().Items.First(i => i.JobId == job.Id).Id); // gerçek "Gördüm"
        Assert.DoesNotContain(w.Now().WaitingJobs!, j => j.Id == job.Id);
    }

    [Fact]
    public void Job_Title_Is_Secret_Filtered()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "baslik");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var j = w.CreateJob(pid, "Anahtarı s" + "k-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdef kullanarak bağlan");
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ", j.Title);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ", w.Jobs().First(x => x.Id == j.Id).Title);
    }

    // =================================================================== Y6: bağlantı hedefi
    [Fact]
    public void Project_Path_Check_Follows_Junctions_And_Refuses_AppData_And_Soz_Data()
    {
        using var env = new TestEnv(copySamples: false);
        var home = Path.Combine(env.Root, "ev");
        var roaming = Path.Combine(home, "AppData", "Roaming", "uygulama");
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        Directory.CreateDirectory(Path.Combine(home, "projem"));
        // AppData ve Söz veri klasörü
        Assert.Contains("AppData", WorkService.CheckProjectPath(roaming, home)!);
        Assert.Contains("veri klasörü", WorkService.CheckProjectPath(Path.Combine(env.DataDir, "alt"), home, env.DataDir)!);
        Assert.Contains("veri klasörü", WorkService.CheckProjectPath(env.DataDir, home, env.DataDir)!);
        Assert.Null(WorkService.CheckProjectPath(Path.Combine(home, "projem"), home, env.DataDir));

        var tmp = new List<string>();
        try
        {
            // junction -> ev kökü, junction -> AppData, junction -> veri klasörü, junction -> .claude
            var j1 = Path.Combine(env.Root, "bag-ev"); Junction(j1, home); tmp.Add(j1);
            var j2 = Path.Combine(env.Root, "bag-roaming"); Junction(j2, roaming); tmp.Add(j2);
            var j3 = Path.Combine(env.Root, "bag-veri"); Junction(j3, env.DataDir); tmp.Add(j3);
            var j4 = Path.Combine(env.Root, "bag-claude"); Junction(j4, Path.Combine(home, ".claude")); tmp.Add(j4);
            var j5 = Path.Combine(env.Root, "bag-projem"); Junction(j5, Path.Combine(home, "projem")); tmp.Add(j5);
            Assert.NotNull(WorkService.CheckProjectPath(j1, home, env.DataDir));
            Assert.Contains("Kullanıcı klasörünün kendisi", WorkService.CheckProjectPath(j1, home, env.DataDir));
            Assert.Contains("AppData", WorkService.CheckProjectPath(j2, home, env.DataDir)!);
            Assert.Contains("veri klasörü", WorkService.CheckProjectPath(j3, home, env.DataDir)!);
            Assert.NotNull(WorkService.CheckProjectPath(j4, home, env.DataDir));
            Assert.Null(WorkService.CheckProjectPath(j5, home, env.DataDir)); // zararsız bağlantı serbest
            // gerçek yol çözümü ve kullanıcı ayar dosyası
            Assert.Equal(Path.GetFullPath(home).TrimEnd('\\'), WorkService.RealPath(j1).TrimEnd('\\'), ignoreCase: true);
            Assert.True(WorkService.IsUserClaudeSettings(WorkService.RealPath(Path.Combine(j4, "settings.json")), home));
            Assert.False(WorkService.IsUserClaudeSettings(Path.Combine(j4, "settings.json"), home)); // yazılan yol tek başına yetmezdi
        }
        finally { foreach (var j in tmp) { try { Directory.Delete(j, false); } catch (Exception) { } } }
    }

    [Fact]
    public void Hooks_Install_Refuses_A_Project_Whose_Claude_Folder_Is_The_Users_Claude_Folder()
    {
        using var env = new TestEnv(copySamples: false);
        // projenin .claude klasörü başka yere bağlantıysa (ör. kullanıcının kendi .claude klasörü) kanca oraya yazılmaz
        var dir = NodeSample.Create(env, "kanca-bag");
        var real = Path.Combine(env.Root, "disarida", ".claude");
        Directory.CreateDirectory(real);
        var link = Path.Combine(dir, ".claude");
        Junction(link, real);
        try
        {
            var w = new WorkService(env.Svc.Db);
            var pid = w.EnsureProjectByPath(dir);
            var exe = typeof(RunManager).Assembly.Location;
            var ex = Assert.Throws<SozException>(() => w.InstallTerminalHooks(pid, exe));
            Assert.Equal(400, ex.Status);
            Assert.Contains("başka bir yere bağlı", ex.Message);
            Assert.False(File.Exists(Path.Combine(real, "settings.json")));
        }
        finally { try { Directory.Delete(link, false); } catch (Exception) { } }
    }
}
