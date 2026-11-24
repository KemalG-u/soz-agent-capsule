using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Guard;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Yürütücü (terminal yok turu): sahte claude (test\sahte-claude\sahte-claude.mjs) ile birim ve API sınamaları.</summary>
public class YurutucuTests
{
    private static string FakePath => Path.Combine(TestEnv.RepoRoot, "test", "sahte-claude", "sahte-claude.mjs");
    private static string SozExeStandIn => typeof(RunManager).Assembly.Location; // var olan bir dosya (kanca ayarı yalnız yolu yazar; kancalar çalıştırılmaz)

    private sealed class Rig : IDisposable
    {
        public TestEnv Env = new(copySamples: false);
        public WorkService W;
        public RunManager Rm;
        public string ProjectDir;
        public long ProjectId;
        public string LogFile;
        public string TorunFile;

        public Rig(int maxParallel = 3)
        {
            ProjectDir = Path.Combine(Env.Root, "proje klasoru"); // boşluklu yol
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
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test-gizli-anahtar-0000"); // çocuk sürece GEÇMEMELİ
        }

        public JobDto Job(string text) => W.CreateJob(ProjectId, text);

        public async Task<RunDto> Finish(RunDto run)
        {
            await Rm.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(60));
            using var c = Env.Svc.Db.Open();
            return RunStore.Get(c, run.Id)!;
        }

        public List<TimelineItem> Timeline(long jobId) { using var c = Env.Svc.Db.Open(); return RunStore.Timeline(c, jobId, includeDetail: false); }

        public void Dispose()
        {
            try { Rm.Dispose(); } catch (Exception) { }
            Environment.SetEnvironmentVariable("SAHTE_LOG", null);
            Environment.SetEnvironmentVariable("SAHTE_STATE_DIR", null);
            Environment.SetEnvironmentVariable("SAHTE_TORUN", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("SAHTE_KANCALI", null);
            Env.Dispose();
        }
    }

    // ------------------------------------------------------------------ çevirici
    private static string Assistant(object block) => JsonSerializer.Serialize(new { type = "assistant", message = new { role = "assistant", content = new[] { block } }, session_id = "s1" });
    private static string ToolUse(string name, object input, string id = "t1") => Assistant(new { type = "tool_use", id, name, input });

    [Fact]
    public void Translator_Plain_Turkish_Lines_No_Tool_Names()
    {
        var t = new RunEventTranslator();
        var all = new List<RunLine>();
        void F(string l) => all.AddRange(t.Feed(l));
        F("""{"type":"system","subtype":"init","session_id":"abc-1","cwd":"x"}""");
        Assert.Equal("abc-1", t.SessionId);
        F("""{"type":"system","subtype":"hook_started","hook_id":"1"}""");          // atlanır
        F("""{"type":"rate_limit_event","rate_limit_info":{}}""");                  // atlanır
        F("not json at all");                                                        // atlanır
        F(Assistant(new { type = "thinking", thinking = "", signature = "x" }));
        F(Assistant(new { type = "thinking", thinking = "", signature = "y" }));      // art arda tekrar yok
        F(ToolUse("Read", new { file_path = @"E:\Sample Work\proje\hesap.js" }));
        F(ToolUse("Glob", new { pattern = "**/*.js" }, "t2"));
        F(ToolUse("Bash", new { command = "npm test" }, "t3"));
        F(ToolUse("Bash", new { command = "node --test test/" }, "t4"));
        F(ToolUse("Bash", new { command = "ls -la src" }, "t5"));
        F(ToolUse("Edit", new { file_path = "a/hesap.js" }, "t6"));
        F(ToolUse("Write", new { file_path = "a/notlar.txt" }, "t7"));
        F(ToolUse("Edit", new { file_path = "a/hesap.js" }, "t8"));
        F(ToolUse("Read", new { file_path = "b.txt" }, "t9"));                         // bekleyen değişiklik satırını bırakır
        F(ToolUse("mcp__bilinmeyen__arac", new { x = 1 }, "t10"));                    // bilinmeyen araç: sessiz
        F(Assistant(new { type = "text", text = "Bitti. Testler geçti: 2/2\n\nİkinci paragraf." }));
        all.AddRange(t.Flush());

        var texts = all.Select(l => l.Text).ToList();
        Assert.Equal(new[]
        {
            "düşünüyor",
            "hesap.js dosyasını okuyor",
            "dosyaları tarıyor",
            "bir komut çalıştırıyor: testler",
            "bir komut çalıştırıyor: testler",
            "bir komut çalıştırıyor: dosya listeleme",
            "2 dosyayı değiştirdi",
            "b.txt dosyasını okuyor",
            "Bitti. Testler geçti: 2/2",
        }, texts);
        Assert.DoesNotContain(all.Where(l => l.Kind != "agent"), l => l.Text.Contains("Read") || l.Text.Contains("Bash") || l.Text.Contains("tool") || l.Text.Contains('{'));
        var rd = all.First(l => l.Text.StartsWith("hesap.js dosyasını okuyor")).Detail!; // ayrıntı sade cümledir, ham akış JSON'u değil (denetim 3, Y8)
        Assert.StartsWith("Dosya: ", rd);
        Assert.DoesNotContain("tool_use", rd);
        Assert.DoesNotContain("{", rd);
        Assert.Contains("İkinci paragraf", all.Last().Detail);
    }

    [Fact]
    public void Translator_Single_Edit_Result_And_Secret_Masking()
    {
        var t = new RunEventTranslator();
        var l1 = t.Feed(ToolUse("Edit", new { file_path = "hesap.js" })).Concat(t.Flush()).Single();
        Assert.Equal("hesap.js dosyasını değiştirdi", l1.Text);

        var sec = t.Feed(ToolUse("Bash", new { command = "curl -H 'Authorization: Bearer s" + "k-ant-api03-ABCDEFGHIJKLMNOPQRSTUV' https://x.example/a" }, "ts")).Single();
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUV", sec.Text);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUV", sec.Detail);

        t.Feed("""{"type":"result","subtype":"success","is_error":false,"result":"Tamam","session_id":"zz","num_turns":3}""");
        Assert.True(t.ResultSeen);
        Assert.False(t.ResultIsError);
        Assert.Equal("Tamam", t.ResultText);
        Assert.Equal(3, t.NumTurns);

        var e = new RunEventTranslator();
        e.Feed("""{"type":"result","subtype":"error_max_turns","is_error":true,"result":"çok tur"}""");
        Assert.True(e.ResultIsError);
    }

    [Fact]
    public void Translator_Real_Claude_Capture_Has_No_Tool_Names()
    {
        // Gerçek claude 2.1.322 çıktısından alınmış örnek satırlar (Glob + Read + metin).
        var lines = new[]
        {
            """{"type":"system","subtype":"init","cwd":"E:\\x","session_id":"1b5b9298-0016-4062-b186-dadaec94484f","tools":["Task","Bash"],"model":"claude-haiku-5-5","permissionMode":"acceptEdits"}""",
            """{"type":"assistant","message":{"model":"claude-haiku-5-5","id":"msg_1","type":"message","role":"assistant","content":[{"type":"thinking","thinking":"","signature":"CAQ"}]},"session_id":"1b5b9298"}""",
            """{"type":"assistant","message":{"model":"claude-haiku-5-5","id":"msg_1","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Glob","input":{"pattern":"**/hesap.js","path":"E:\\x"},"caller":{"type":"direct"}}]},"session_id":"1b5b9298"}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"hesap.js"}]},"parent_tool_use_id":null,"session_id":"1b5b9298"}""",
            """{"type":"assistant","message":{"model":"claude-haiku-5-5","id":"msg_2","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_2","name":"Read","input":{"file_path":"E:\\x\\hesap.js"},"caller":{"type":"direct"}}]},"session_id":"1b5b9298"}""",
            """{"type":"system","subtype":"thinking_tokens","estimated_tokens":50}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"`hesap.js` iki küçük fonksiyon dışa aktarıyor."}]},"session_id":"1b5b9298"}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":19000,"num_turns":3,"result":"`hesap.js` iki küçük fonksiyon dışa aktarıyor.","stop_reason":"end_turn","session_id":"1b5b9298-0016-4062-b186-dadaec94484f","total_cost_usd":0.008}""",
        };
        var t = new RunEventTranslator();
        var got = lines.SelectMany(l => t.Feed(l)).Concat(t.Flush()).Select(l => l.Text).ToList();
        Assert.Equal(new[] { "düşünüyor", "dosyaları tarıyor", "hesap.js dosyasını okuyor", "`hesap.js` iki küçük fonksiyon dışa aktarıyor." }, got);
        Assert.Equal("1b5b9298-0016-4062-b186-dadaec94484f", t.SessionId);
        Assert.True(t.ResultSeen);
    }

    // ------------------------------------------------------------------ şema v6
    [Fact]
    public void Migration_V6_To_Current_Keeps_Existing_Data_And_Adds_Tables()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var dir = Path.Combine(env.Root, "gocmen");
        Directory.CreateDirectory(dir);
        var pid = w.EnsureProjectByPath(dir);
        var job = w.CreateJob(pid, "v6 öncesi iş");
        using (var c = env.Svc.Db.Open())
        {
            c.Exec("DROP TABLE runs"); c.Exec("DROP TABLE run_lines"); c.Exec("DROP TABLE run_approvals");
            c.Exec("ALTER TABLE projects DROP COLUMN added"); c.Exec("ALTER TABLE jobs DROP COLUMN agent_state");
            c.Exec("PRAGMA user_version=5");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var db = SozDb.OpenAndMigrate(env.DataDir);
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion());
        Assert.True(SozDb.CurrentVersion >= 12); // geri alma tabloları (v12) bu sürümde var; sayı zincire her adım eklendiğinde yükselir
        using var c2 = db.Open();
        var tables = c2.Query("SELECT name FROM sqlite_master WHERE type='table'", r => r.GetString(0));
        Assert.Contains("runs", tables); Assert.Contains("run_lines", tables); Assert.Contains("run_approvals", tables);
        Assert.Contains("added", c2.Query("PRAGMA table_info(projects)", r => r.GetString(1)));
        Assert.Contains("agent_state", c2.Query("PRAGMA table_info(jobs)", r => r.GetString(1)));
        Assert.Equal(1L, c2.ScalarLong("SELECT COUNT(*) FROM jobs WHERE id=@j AND raw_text='v6 öncesi iş'", null, ("@j", job.Id)));
        Assert.Equal(1L, c2.ScalarLong("SELECT COUNT(*) FROM projects WHERE id=@p", null, ("@p", pid)));
        // ikinci açılış değişiklik yapmaz
        Assert.Equal(SozDb.CurrentVersion, SozDb.OpenAndMigrate(env.DataDir).GetVersion());
    }

    // ------------------------------------------------------------------ ayarlar
    [Fact]
    public void Settings_Show_Runner_And_Judge_Keys_With_Validation()
    {
        using var env = new TestEnv(copySamples: false);
        var s = env.Svc.GetSettings();
        Assert.False((bool)s["judgeClaudeEnabled"]!);
        Assert.Equal(3, (int)s["maxParallelRuns"]!);
        Assert.Equal("", (string)s["runnerModel"]!);
        Assert.Null(s["claudePath"]);
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["maxParallelRuns"] = 0 }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["claudePath"] = 5 }));
        Assert.Null(SozSettings.Validate(new JsonObject { ["runnerModel"] = "haiku", ["maxParallelRuns"] = 5, ["judgeClaudeEnabled"] = true }));
    }

    // ------------------------------------------------------------------ proje ekleme
    [Fact]
    public void AddProject_Validates_Suggests_Closed_And_Single_Approval_Opens_All()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var ex = Assert.Throws<SozException>(() => w.AddProject(Path.Combine(env.Root, "yok-boyle-klasor")));
        Assert.Equal(404, ex.Status);

        var dir = NodeSample.Create(env, "yeni proje");
        var r = w.AddProject(dir);
        Assert.True(r.Created);
        var s = Assert.Single(r.Suggested);
        Assert.False(s.Enabled);
        Assert.Equal("npm test", (string?)s.Spec["cmd"]);
        Assert.Contains("`npm test`", r.Card);
        Assert.Contains("Olur mu?", r.Card);
        Assert.Contains(env.Svc.Projects(), p => p.Id == r.Id); // proje hemen listede (hiç oturum/karar yokken bile)
        Assert.False(w.ProjectGate(r.Id).GateEnabled);

        Assert.False(w.AddProject(dir).Created); // ikinci kez: yeni kayıt yok
        Assert.Single(w.Checks(r.Id));

        var ap = w.ApproveSetup(r.Id);
        Assert.True(ap.Gate.GateEnabled);
        Assert.All(ap.Checks, c => Assert.True(c.Enabled));
        Assert.Equal(1, ap.Gate.EnabledChecks);
        // olaylar yazıldı
        using var c = env.Svc.Db.Open();
        Assert.True(c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind IN ('check_changed','gate_changed')") >= 2);
    }

    [Fact]
    public void AddProject_Without_Tests_Gives_Unverified_Card_And_Job_New_Project_Is_Listed()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var dir = Path.Combine(env.Root, "bos proje");
        Directory.CreateDirectory(dir);
        var r = w.AddProject(dir);
        Assert.Empty(r.Suggested);
        Assert.Contains("denenmeden", r.Card);

        // `job new --project <klasör>` yolu (EnsureProjectByPath) da listede görünür (hata düzeltmesi)
        var dir2 = Path.Combine(env.Root, "cli ile gelen");
        Directory.CreateDirectory(dir2);
        var pid2 = w.EnsureProjectByPath(dir2);
        w.CreateJob(pid2, "bir iş");
        Assert.Contains(env.Svc.Projects(), p => p.Id == pid2);
    }

    [Fact]
    public void Terminal_Hooks_Install_Only_In_Selected_Project_Folder_And_Keep_Existing()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var dir = Path.Combine(env.Root, "terminal proje");
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(Path.Combine(dir, ".claude", "settings.json"), """{ "permissions": { "allow": ["Bash(ls)"] } }""");
        var other = Path.Combine(env.Root, "baska proje");
        Directory.CreateDirectory(other);
        var pid = w.EnsureProjectByPath(dir);
        w.EnsureProjectByPath(other);

        var res = w.InstallTerminalHooks(pid, SozExeStandIn);
        Assert.Equal(Path.Combine(dir, ".claude", "settings.json"), res.SettingsPath);
        var json = JsonNode.Parse(File.ReadAllText(res.SettingsPath))!;
        Assert.Equal("Bash(ls)", (string?)json["permissions"]!["allow"]![0]);
        foreach (var ev in new[] { "SessionStart", "UserPromptSubmit", "Stop", "Notification", "PreToolUse" }) Assert.NotNull(json["hooks"]![ev]);
        Assert.False(Directory.Exists(Path.Combine(other, ".claude"))); // yalnız seçilen klasör
        Assert.NotNull(res.Backup);
        Assert.True(File.Exists(res.Backup)); // zaman damgalı yedek, hiçbir yedeğin üstüne yazılmaz
        Assert.Equal("""{ "permissions": { "allow": ["Bash(ls)"] } }""", File.ReadAllText(res.Backup!));
        Assert.True(w.UninstallTerminalHooks(pid));
        Assert.Null(JsonNode.Parse(File.ReadAllText(res.SettingsPath))!["hooks"]);
        Assert.Equal("""{ "permissions": { "allow": ["Bash(ls)"] } }""", File.ReadAllText(res.SettingsPath)); // kurulumdan önceki içerik bayt bayt
    }

    // ------------------------------------------------------------------ yürütücü: sahte claude
    [Fact]
    public async Task Start_Runs_Windowless_Fake_Claude_Writes_Plain_Progress_And_Links_Job()
    {
        using var rig = new Rig();
        var job = rig.Job("Hesabı düzelt [[sahte:oku]]");
        var run = rig.Rm.Start(job.Id);
        Assert.NotNull(run.Pid);
        var done = await rig.Finish(run);
        Assert.Equal(RunStatus.Done, done.Status);
        Assert.Equal(0, done.ExitCode);
        Assert.False(string.IsNullOrEmpty(done.SessionId));

        var tl = rig.Timeline(job.Id);
        var texts = tl.Select(i => i.Text).ToList();
        Assert.Contains("hesap.js dosyasını okuyor", texts);
        Assert.Contains("2 dosyayı değiştirdi", texts);
        Assert.Contains("bir komut çalıştırıyor: testler", texts);
        Assert.Contains("düşünüyor", texts);
        Assert.Contains("Yardımcı işi bitirdi.", texts);
        Assert.Contains(tl, i => i.Kind == "agent" && i.Text.StartsWith("Bitti. Testler geçti"));
        Assert.Equal("user", tl[0].Kind); // ilk satır kullanıcının sözü
        Assert.DoesNotContain(tl.Where(i => i.Kind == "progress"), i => i.Text.Contains("Read") || i.Text.Contains("Bash") || i.Text.Contains("{"));

        var j = rig.W.Job(job.Id);
        Assert.NotNull(j.SessionId); // iş oturuma bağlandı
        // Süreç bitti, hüküm yoktu: iş "calisiyor" kalmaz; Söz kapıyı kendisi çalıştırır, ölçüt yok → "unchecked", iş "verildi" (asla "geçti" değil)
        Assert.Equal(JobStates.Given, j.State);
        Assert.Equal(Verdicts.Unchecked, j.LastVerdict);
        using (var cc = rig.Env.Svc.Db.Open()) Assert.Equal("run_end", cc.Scalar("SELECT trigger FROM proof_runs WHERE job_id=@j", null, ("@j", job.Id)) as string);
        Assert.Equal("kanit", j.AgentState); // Hakem: bitti diyor + kanıt gösteriyor
        Assert.Equal(RunStatus.Done, j.Run!.Status);

        // argümanlar ve ortam (gerçek süreç; ArgumentList)
        var log = JsonNode.Parse(File.ReadAllLines(rig.LogFile)[0])!;
        var argv = log["argv"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.Contains("-p", argv);
        Assert.Equal("stream-json", argv[argv.IndexOf("--output-format") + 1]);
        Assert.Contains("--verbose", argv);
        Assert.Equal("acceptEdits", argv[argv.IndexOf("--permission-mode") + 1]);
        Assert.Contains("Bash", argv[argv.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("--resume", argv);
        var settingsArg = argv[argv.IndexOf("--settings") + 1];
        Assert.Equal(Path.Combine(rig.Env.DataDir, "yurutucu", "claude-ayar.json"), settingsArg); // proje dosyasına yazılmadı
        Assert.False(File.Exists(Path.Combine(rig.ProjectDir, ".claude", "settings.json")));
        Assert.Equal(rig.ProjectDir, (string)log["cwd"]!); // boşluklu proje klasöründe çalıştı
        Assert.Contains("(Söz iş #", (string)log["prompt"]!); // istem stdin'den geldi
        Assert.DoesNotContain((string)log["prompt"]!, string.Join(" ", argv));
        Assert.Equal(done.Id.ToString(), (string)log["env"]!["SOZ_RUN_ID"]!);
        Assert.Equal(job.Id.ToString(), (string)log["env"]!["SOZ_JOB_ID"]!);
        Assert.Null(log["env"]!["ANTHROPIC_API_KEY"]); // ücretli API anahtarı çocuk sürece geçmez

        // kanca ayar dosyası: beş kanca, soz.exe mutlak yolu ve --data
        var hooks = JsonNode.Parse(File.ReadAllText(settingsArg))!["hooks"]!.AsObject();
        Assert.Equal(new[] { "Notification", "PreToolUse", "SessionStart", "Stop", "StopFailure", "UserPromptSubmit" }, hooks.Select(h => h.Key).OrderBy(x => x).ToArray());
        var cmd = (string)hooks["Stop"]![0]!["hooks"]![0]!["command"]!;
        Assert.Contains(SozExeStandIn.Replace('\\', '/'), cmd);
        Assert.Contains(" hook stop --data ", cmd);
        Assert.Contains(rig.Env.DataDir.Replace('\\', '/'), cmd);
    }

    [Fact]
    public async Task Reply_Resumes_Same_Session_And_Approve_Needs_Pending()
    {
        using var rig = new Rig();
        var job = rig.Job("İş [[sahte:oku]]");
        var first = await rig.Finish(rig.Rm.Start(job.Id));
        var second = await rig.Finish(rig.Rm.Reply(job.Id, "Bir de README'ye yaz"));
        Assert.Equal(RunStatus.Done, second.Status);
        var lines = File.ReadAllLines(rig.LogFile).Select(l => JsonNode.Parse(l)!).ToList();
        var argv2 = lines[1]["argv"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.Equal(first.SessionId, argv2[argv2.IndexOf("--resume") + 1]);
        Assert.Equal("Bir de README'ye yaz", (string)lines[1]["prompt"]!);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Contains(rig.Timeline(job.Id), i => i.Kind == "user" && i.Text == "Bir de README'ye yaz");
        // onay yokken approve 404
        Assert.Equal(404, Assert.Throws<SozException>(() => rig.Rm.Approve(job.Id, 0, true)).Status);
        // henüz konuşmamış işe cevap: 409
        var fresh = rig.Job("yeni");
        Assert.Equal(409, Assert.Throws<SozException>(() => rig.Rm.Reply(fresh.Id, "x")).Status);
    }

    [Fact]
    public async Task Hakem_Classifies_Question()
    {
        using var rig = new Rig();
        var job = rig.Job("İş [[sahte:soru]]");
        await rig.Finish(rig.Rm.Start(job.Id));
        Assert.Equal("soru", rig.W.Job(job.Id).AgentState);
        using var c = rig.Env.Svc.Db.Open();
        var judge = c.Query("SELECT detail FROM events WHERE kind='judge'", r => JsonNode.Parse(r.GetString(0))!);
        Assert.NotEmpty(judge);
        Assert.All(judge, d => { Assert.Equal("rules", (string)d["engine"]!); Assert.Equal(job.Id, (long)d["jobId"]!); });
    }

    [Fact]
    public async Task Claude_Missing_Exit_Error_And_Login_Fail_Plainly_Without_Crash()
    {
        using var rig = new Rig();
        // claude yok
        // (arayüz bu yolu doğrulayıp reddeder; burada ayar doğrudan yazılır: dosya sonradan silinmiş durumu)
        using (var cs = rig.Env.Svc.Db.Open()) SozSettings.Save(cs, new JsonObject { ["claudePath"] = Path.Combine(rig.Env.Root, "yok", "claude.exe") });
        var j1 = rig.Job("iş");
        var r1 = rig.Rm.Start(j1.Id)!;
        Assert.Equal(RunStatus.Failed, r1.Status);
        Assert.Contains("Claude Code bu bilgisayarda bulunamadı", r1.FailReason);
        Assert.Equal(JobStates.StartFailed, rig.W.Job(j1.Id).State);
        Assert.Contains("Claude Code bu bilgisayarda bulunamadı", rig.W.Job(j1.Id).StartError);
        Assert.Contains(rig.Timeline(j1.Id), i => i.Kind == "error");

        // hata çıkış kodu
        rig.Env.Svc.PutSettings(new JsonObject { ["claudePath"] = FakePath });
        var j2 = rig.Job("iş [[sahte:hata]]");
        var r2 = await rig.Finish(rig.Rm.Start(j2.Id)!);
        Assert.Equal(RunStatus.Failed, r2.Status);
        Assert.Equal(JobStates.StartFailed, rig.W.Job(j2.Id).State);
        Assert.Equal(1, r2.ExitCode);
        Assert.Equal("Yardımcı işi yarıda bırakıp durdu.", r2.FailReason); // ham metin ("boom") cümlede değil, hata satırının teknik ayrıntısında
        using (var cx = rig.Env.Svc.Db.Open()) Assert.Contains(RunStore.Timeline(cx, j2.Id, includeDetail: true), i => i.Kind == "error" && (i.Detail ?? "").Contains("boom") && (i.Detail ?? "").Contains("çıkış kodu 1"));

        // giriş yok
        var j3 = rig.Job("iş [[sahte:giris]]");
        var r3 = await rig.Finish(rig.Rm.Start(j3.Id)!);
        Assert.Equal(RunStatus.Failed, r3.Status);
        Assert.Contains("giriş yapılmamış", r3.FailReason);
        Assert.Equal(JobStates.StartFailed, rig.W.Job(j3.Id).State);
        Assert.Contains("giriş yapılmamış", rig.W.Job(j3.Id).StartError);
        // bir işe ikinci başlatma yeniden mümkün (kilitlenme yok)
        var again = await rig.Finish(rig.Rm.Start(j3.Id)!);
        Assert.Equal(RunStatus.Failed, again.Status);
    }

    [Fact]
    public async Task Stop_Kills_Process_Tree_No_Orphans_And_Parallel_Limit()
    {
        using var rig = new Rig(maxParallel: 1);
        var j1 = rig.Job("uzun [[sahte:uzun]]");
        var r1 = rig.Rm.Start(j1.Id)!;
        // torun süreç başlayana kadar bekle
        for (var i = 0; i < 100 && !File.Exists(rig.TorunFile); i++) await Task.Delay(100);
        Assert.True(File.Exists(rig.TorunFile), "sahte torun süreç başlamadı");
        var torunPid = int.Parse((await File.ReadAllTextAsync(rig.TorunFile)).Trim());
        Assert.True(IsAlive(torunPid));

        // K-6: aynı anda en çok N; fazlası hata değil, sıraya girer
        var j2 = rig.Job("ikinci");
        Assert.Null(rig.Rm.Start(j2.Id));
        Assert.Equal(JobStates.Queued, rig.W.Job(j2.Id).State);
        // aynı işi ikinci kez başlatma
        Assert.Equal(409, Assert.Throws<SozException>(() => rig.Rm.Start(j1.Id)).Status);

        Assert.True(rig.Rm.Stop(j1.Id));
        var done = await rig.Finish(r1);
        Assert.Equal(RunStatus.Stopped, done.Status);
        Assert.Equal(JobStates.Stopped, rig.W.Job(j1.Id).State);
        // yer açıldı: sıradaki iş kendiliğinden başlar ve biter
        for (var i = 0; i < 200 && rig.W.Job(j2.Id).State == JobStates.Queued; i++) await Task.Delay(100);
        Assert.NotEqual(JobStates.Queued, rig.W.Job(j2.Id).State);
        var j2run = rig.W.Job(j2.Id).Run;
        Assert.NotNull(j2run);
        await rig.Rm.WaitAsync(j2run!.RunId).WaitAsync(TimeSpan.FromSeconds(60));
        for (var i = 0; i < 50 && IsAlive(torunPid); i++) await Task.Delay(100);
        Assert.False(IsAlive(torunPid), "durdurmadan sonra yetim torun süreç kaldı");
        Assert.False(IsAlive(done.Pid!.Value));
        Assert.Contains(rig.Timeline(j1.Id), i => i.Text == "İş durduruldu.");
        Assert.False(rig.Rm.Stop(j1.Id)); // çalışan kalmadı
    }

    [Fact]
    public async Task Shutdown_Stops_Running_Processes_And_Startup_Marks_Stale_Runs()
    {
        using var rig = new Rig();
        var j1 = rig.Job("uzun [[sahte:uzun]]");
        var r1 = rig.Rm.Start(j1.Id)!;
        for (var i = 0; i < 100 && !File.Exists(rig.TorunFile); i++) await Task.Delay(100);
        var torunPid = int.Parse((await File.ReadAllTextAsync(rig.TorunFile)).Trim());
        await rig.Rm.StopAllAsync();
        var done = await rig.Finish(r1);
        Assert.Equal(RunStatus.Stopped, done.Status);
        Assert.Contains("kapan", done.LastLine);
        for (var i = 0; i < 50 && IsAlive(torunPid); i++) await Task.Delay(100);
        Assert.False(IsAlive(torunPid));

        // açılışta "running" kalmış (süreci olmayan) kayıt
        long staleId;
        var j2 = rig.Job("yarım kalan");
        using (var c = rig.Env.Svc.Db.Open())
        {
            staleId = RunStore.Insert(c, j2.Id, rig.ProjectId, "start");
            RunStore.SetProcess(c, staleId, 2_000_000_000, DateTime.UtcNow);
            JobStore.SetState(c, j2.Id, JobStates.Running, rig.ProjectId);
        }
        var fresh = new RunManager(rig.Env.Svc.Db);
        Assert.Equal(1, fresh.RecoverOnStartup());
        using var c2 = rig.Env.Svc.Db.Open();
        var st = RunStore.Get(c2, staleId)!;
        Assert.Equal(RunStatus.Stopped, st.Status);
        Assert.Equal("yarım kaldı", st.LastLine);
        Assert.Equal(JobStates.Stopped, rig.W.Job(j2.Id).State);
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // ------------------------------------------------------------------ bekçi "sor" başsız kipte
    private static string Hook(TestEnv env, string ev, object input, long? runId = null, long? jobId = null, string? dir = null)
    {
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run(ev, new StringReader(JsonSerializer.Serialize(input)), o, env.DataDir, k => k switch
        {
            "SOZ_RUN_ID" => runId?.ToString(),
            "SOZ_JOB_ID" => jobId?.ToString(),
            "CLAUDE_PROJECT_DIR" => dir,
            _ => null,
        }));
        return o.ToString();
    }

    private static (string decision, string reason) Decision(string output)
    {
        var o = JsonNode.Parse(output)!["hookSpecificOutput"]!;
        return ((string)o["permissionDecision"]!, (string)o["permissionDecisionReason"]!);
    }

    [Fact]
    public async Task Headless_Ask_Becomes_Waiting_Approval_One_Shot_Allow_And_Deny_Path()
    {
        using var rig = new Rig();
        using (var c = rig.Env.Svc.Db.Open()) { GuardStore.EnsureTables(c); GuardStore.ApplyTemplate(c, rig.ProjectId, "temkinli"); }
        var job = rig.Job("temizlik [[sahte:oku]]");
        long runId; long sid;
        using (var c = rig.Env.Svc.Db.Open())
        {
            runId = RunStore.Insert(c, job.Id, rig.ProjectId, "start");
            RunStore.SetSession(c, runId, "sess-onay-1");
            (sid, _) = Soz.Core.Readers.IngestStore.EnsureSession(c, null, "claude", "sess-onay-1", rig.ProjectDir, null, "human");
            c.Exec("UPDATE jobs SET session_id=@s WHERE id=@j", null, ("@s", sid), ("@j", job.Id));
            JobStore.SetState(c, job.Id, JobStates.Running, rig.ProjectId);
        }
        var input = new { session_id = "sess-onay-1", cwd = rig.ProjectDir, tool_name = "Bash", tool_input = new { command = "rm -rf eski-klasor" } };

        // 1) bekçi "sor" → başsız kipte reddedilir + onay isteği kaydedilir
        var (d1, why1) = Decision(Hook(rig.Env, "pre-tool-use", input, runId, job.Id, rig.ProjectDir));
        Assert.Equal("deny", d1);
        Assert.Contains("onay", why1);
        using (var c = rig.Env.Svc.Db.Open())
        {
            var pend = RunStore.PendingApproval(c, job.Id)!;
            Assert.Contains("rm -rf eski-klasor", pend.Summary);
            Assert.True(pend.EventId > 0);
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM guard_events WHERE action='ask' AND id=@e", null, ("@e", pend.EventId)));
            RunStore.SetStatus(c, runId, RunStatus.WaitingApproval, ended: true);
        }
        // aynı istek yinelenirse yeni onay açılmaz
        var (d1b, why1b) = Decision(Hook(rig.Env, "pre-tool-use", input, runId, job.Id, rig.ProjectDir));
        Assert.Equal("deny", d1b);
        Assert.Contains("bekleniyor", why1b);
        using (var c = rig.Env.Svc.Db.Open()) Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM run_approvals WHERE job_id=@j", null, ("@j", job.Id)));
        // onay beklerken Stop kapısı çalışmaz
        var stop = Hook(rig.Env, "stop", new { session_id = "sess-onay-1", cwd = rig.ProjectDir, stop_hook_active = false, last_assistant_message = "Engellendi, onay bekliyorum." }, runId, job.Id, rig.ProjectDir);
        Assert.Equal("", stop);
        using (var c = rig.Env.Svc.Db.Open()) Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM proof_runs WHERE job_id=@j", null, ("@j", job.Id)));
        // iş kartında onay isteği görünür
        Assert.NotNull(rig.W.Job(job.Id).Run!.PendingApproval);
        Assert.Contains(rig.Timeline(job.Id), i => i.Kind == "approval" && i.Text.StartsWith("senden onay bekliyor"));

        // 2) Evet → tek seferlik izin + --resume
        int evId;
        using (var c = rig.Env.Svc.Db.Open()) evId = (int)RunStore.PendingApproval(c, job.Id)!.EventId;
        var resumed = await rig.Finish(rig.Rm.Approve(job.Id, evId, allow: true));
        Assert.Equal(RunStatus.Done, resumed.Status);
        var log = File.ReadAllLines(rig.LogFile).Select(l => JsonNode.Parse(l)!).Last();
        Assert.Equal("sess-onay-1", (string)log["argv"]![(log["argv"]!.AsArray().Select(x => (string)x!).ToList().IndexOf("--resume")) + 1]!);
        Assert.Contains("onayladı", (string)log["prompt"]!);
        var (d2, _) = Decision(Hook(rig.Env, "pre-tool-use", input, resumed.Id, job.Id, rig.ProjectDir));
        Assert.Equal("allow", d2);
        // izin kullanıldı: bir daha geçmez, yeni onay istenir
        var (d3, why3) = Decision(Hook(rig.Env, "pre-tool-use", input, resumed.Id, job.Id, rig.ProjectDir));
        Assert.Equal("deny", d3);
        Assert.Contains("Onay istendi", why3);
        using (var c = rig.Env.Svc.Db.Open())
            Assert.Equal(new[] { "used", "pending" }, c.Query("SELECT status FROM run_approvals WHERE job_id=@j ORDER BY id", r => r.GetString(0), null, ("@j", job.Id)).ToArray());
        // başka komuta izin geçmez
        var other = new { session_id = "sess-onay-1", cwd = rig.ProjectDir, tool_name = "Bash", tool_input = new { command = "rm -rf baska-klasor" } };
        Assert.Equal("deny", Decision(Hook(rig.Env, "pre-tool-use", other, resumed.Id, job.Id, rig.ProjectDir)).decision);

        // 3) Hayır → "kullanıcı bu işlemi istemedi"
        using (var c = rig.Env.Svc.Db.Open()) { foreach (var p in c.Query("SELECT guard_event_id FROM run_approvals WHERE status='pending'", r => r.GetInt64(0))) c.Exec("UPDATE run_approvals SET status='used' WHERE guard_event_id=@e", null, ("@e", p)); }
        var (d4, _) = Decision(Hook(rig.Env, "pre-tool-use", input, resumed.Id, job.Id, rig.ProjectDir));
        Assert.Equal("deny", d4);
        long ev2;
        using (var c = rig.Env.Svc.Db.Open()) ev2 = RunStore.PendingApproval(c, job.Id)!.EventId;
        var denied = await rig.Finish(rig.Rm.Approve(job.Id, ev2, allow: false));
        Assert.Equal(RunStatus.Done, denied.Status);
        var (d5, why5) = Decision(Hook(rig.Env, "pre-tool-use", input, denied.Id, job.Id, rig.ProjectDir));
        Assert.Equal("deny", d5);
        Assert.Contains("istemedi", why5);
        Assert.Contains("istemedi", (string)JsonNode.Parse(File.ReadAllLines(rig.LogFile).Last())!["prompt"]!);
        // cevap verilmiş isteğe tekrar cevap: 409
        Assert.Equal(409, Assert.Throws<SozException>(() => rig.Rm.Approve(job.Id, ev2, true)).Status);
    }

    // ------------------------------------------------------------------ kapı varsayılan açık, Hakem iki yerde
    [Fact]
    public void Gate_Opens_For_Soz_Run_Job_Even_When_Project_Gate_Is_Off_And_Unchecked_Without_Criteria()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "kapi proje");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        Assert.False(w.ProjectGate(pid).GateEnabled);
        var chk = w.SuggestChecks(pid).Checks.Single();
        var jobWith = w.CreateJob(pid, "Düzelt", checkIds: new[] { chk.Id }); // proje kapısı kapalı; ölçüt yalnız bu işe açık
        var jobNo = w.CreateJob(pid, "Ölçütsüz");

        void Link(string session, long jobId)
        {
            using var c = env.Svc.Db.Open();
            var (sid, _) = Soz.Core.Readers.IngestStore.EnsureSession(c, null, "claude", session, dir, null, "human");
            c.Exec("UPDATE jobs SET session_id=@s WHERE id=@j", null, ("@s", sid), ("@j", jobId));
            JobStore.SetState(c, jobId, JobStates.Running, pid);
        }
        Link("kapi-1", jobWith.Id);
        var o1 = Hook(env, "stop", new { session_id = "kapi-1", cwd = dir, stop_hook_active = false, last_assistant_message = "Bitti." });
        Assert.Contains("\"decision\":\"block\"", o1); // bozuk test: geri yollandı (kapı çalıştı)

        Link("kapi-2", jobNo.Id);
        var msg = "Bitti. Testler geçti: 5/5 (test/hesap.test.js)\nKontrol: node --test → 5 geçti";
        var o2 = Hook(env, "stop", new { session_id = "kapi-2", cwd = dir, stop_hook_active = false, last_assistant_message = msg });
        Assert.DoesNotContain("block", o2);
        var run = w.Runs(null, jobNo.Id)[0];
        Assert.Equal(Verdicts.Unchecked, run.Verdict); // kontrol yok: hüküm unchecked, "geçti" değil
        Assert.Contains("Yardımcı şunu gösterdi (Söz doğrulamadı)", run.Summary);
        Assert.NotEqual(JobStates.Passed, w.Job(jobNo.Id).State);
        Assert.Equal("kanit", w.Job(jobNo.Id).AgentState);
        using var c2 = env.Svc.Db.Open();
        var judge = c2.Query("SELECT detail FROM events WHERE kind='judge'", r => JsonNode.Parse(r.GetString(0))!);
        Assert.Contains(judge, d => (long?)d["jobId"] == jobNo.Id && (string?)d["source"] == "stop_hook");
        Assert.True(judge.Count >= 4);
    }

    [Fact]
    public async Task Judge_Setting_Switches_To_Model_Arm_And_Falls_Back_To_Rules()
    {
        using var env = new TestEnv(copySamples: false);
        env.Svc.PutSettings(new JsonObject { ["judgeClaudeEnabled"] = true, ["judgeCommand"] = Path.Combine(env.Root, "yok", "claude.exe") });
        var judge = Soz.Core.Judge.JudgeFactory.Create(env.Svc.Db, null, 7, "deneme");
        var r = judge.Decide(Soz.Core.Judge.JudgeQuestions.AsksQuestion, Soz.Core.Judge.JudgeQuestions.YesNo, "Hangisini seçeyim?");
        Assert.Equal("evet", r.Answer);
        Assert.Equal("rules(yedek)", r.Engine); // model kolu açık ama claude yok: kurallı motora düştü
        using var c = env.Svc.Db.Open();
        Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='judge' AND detail LIKE '%\"jobId\":7%'"));
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ API
    private static async Task<(HttpStatusCode code, JsonNode? body)> Call(HttpClient http, string token, HttpMethod m, string url, object? body = null, string? origin = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (token.Length > 0) req.Headers.Add("X-Soz-Token", token);
        if (origin is not null) req.Headers.Add("Origin", origin);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    [Fact]
    public async Task Api_Project_Add_Approve_Start_Timeline_Sse_Stop_And_Security()
    {
        using var rig = new Rig();
        var dir = NodeSample.Create(rig.Env, "api proje");
        await using var h = await SozServer.StartAsync(rig.Env.DataDir, 49360);
        h.Runs.SozExePath = SozExeStandIn;
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };

        // güvenlik: belirteçsiz 401, yabancı köken 403
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(http, "", HttpMethod.Post, "/api/projects", new { path = dir })).code);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(http, h.Token, HttpMethod.Post, "/api/projects", new { path = dir }, origin: "https://evil.example")).code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(http, "", HttpMethod.Post, "/api/jobs/1/start", new { })).code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(http, "", HttpMethod.Post, "/api/projects/1/hooks/install", new { })).code);
        // klasör yok → 200 + {ok:false, code:klasor_yok} (kullanıcı girdisi hatası konsolu kirletmez)
        var noDir = await Call(http, h.Token, HttpMethod.Post, "/api/projects", new { path = Path.Combine(rig.Env.Root, "yok") });
        Assert.Equal(HttpStatusCode.OK, noDir.code);
        Assert.False((bool)noDir.body!["ok"]!);
        Assert.Equal("klasor_yok", (string?)noDir.body["code"]);

        var (code, add) = await Call(http, h.Token, HttpMethod.Post, "/api/projects", new { path = dir });
        Assert.Equal(HttpStatusCode.Created, code);
        var pid = (long)add!["id"]!;
        Assert.Equal("npm test", (string)add["suggested"]![0]!["spec"]!["cmd"]!);
        Assert.False((bool)add["suggested"]![0]!["enabled"]!);
        Assert.Contains("Olur mu?", (string)add["card"]!);
        var list = JsonNode.Parse(await http.GetStringAsync("/api/projects"))!.AsArray();
        Assert.Contains(list, p => (long)p!["id"]! == pid);

        var (c2, ap) = await Call(http, h.Token, HttpMethod.Post, $"/api/projects/{pid}/approve-setup", new { guardTemplate = true });
        Assert.Equal(HttpStatusCode.OK, c2);
        Assert.True((bool)ap!["gate"]!["gateEnabled"]!);
        Assert.True((bool)ap["checks"]![0]!["enabled"]!);
        Assert.Equal("temkinli", (string)ap["guardTemplate"]!);

        // SSE: job.progress ve job.state
        var sse = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var stream = await http.GetStreamAsync("/api/events/stream", cts.Token);
        var reader = new StreamReader(stream);
        var pump = Task.Run(async () =>
        {
            try { string? line; while ((line = await reader.ReadLineAsync(cts.Token)) is not null) lock (sse) sse.Add(line); }
            catch (OperationCanceledException) { }
        });

        var (jc, job) = await Call(http, h.Token, HttpMethod.Post, "/api/jobs", new { projectId = pid, rawText = "Hesabı düzelt [[sahte:oku]]" });
        Assert.Equal(HttpStatusCode.Created, jc);
        var jobId = (long)job!["id"]!;
        var (sc, started) = await Call(http, h.Token, HttpMethod.Post, $"/api/jobs/{jobId}/start", new { });
        Assert.Equal(HttpStatusCode.Accepted, sc);
        Assert.Equal("running", (string)started!["run"]!["status"]!);
        await h.Runs.WaitAsync((long)started["run"]!["id"]!);
        await Task.Delay(900); // olay yoklaması 300 ms

        var (tc, tl) = await Call(http, h.Token, HttpMethod.Get, $"/api/jobs/{jobId}/timeline");
        Assert.Equal(HttpStatusCode.OK, tc);
        var texts = tl!["items"]!.AsArray().Select(i => (string)i!["text"]!).ToList();
        Assert.Contains("hesap.js dosyasını okuyor", texts);
        Assert.Equal("done", (string)tl["run"]!["status"]!);

        string all; lock (sse) all = string.Join("\n", sse);
        Assert.Contains("event: job.progress", all);
        Assert.Contains("event: job.state", all);
        var progressTexts = all.Split('\n').Where(l => l.StartsWith("data: ") && l.Contains("lineId")).Select(l => (string?)JsonNode.Parse(l[6..])!["text"]).ToList(); // SSE JSON'u \u biçiminde kaçırır
        Assert.Contains("hesap.js dosyasını okuyor", progressTexts);
        cts.Cancel();
        await pump;

        // durdur: çalışan yok
        var (st, stopped) = await Call(http, h.Token, HttpMethod.Post, $"/api/jobs/{jobId}/stop", new { });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.False((bool)stopped!["stopped"]!);
        // allow olmadan approve: 400
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(http, h.Token, HttpMethod.Post, $"/api/jobs/{jobId}/approve", new { eventId = 1 })).code);
        // boş cevap: 400
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(http, h.Token, HttpMethod.Post, $"/api/jobs/{jobId}/reply", new { text = "  " })).code);

        // kanca kurma (yalnız bu proje klasörü)
        var (hc, hk) = await Call(http, h.Token, HttpMethod.Post, $"/api/projects/{pid}/hooks/install", new { });
        Assert.Equal(HttpStatusCode.OK, hc);
        Assert.Equal(Path.Combine(dir, ".claude", "settings.json"), (string)hk!["settingsPath"]!);
        Assert.True(File.Exists(Path.Combine(dir, ".claude", "settings.json")));

        // ayarlarda hakem ayarı görünür
        var settings = JsonNode.Parse(await http.GetStringAsync("/api/settings"))!;
        Assert.False((bool)settings["judgeClaudeEnabled"]!);

        // #8 Hakem anahtarı açılıp kapanır (ayar yazılır ve okunur; bu sırada hakem kararı istenmez, gerçek claude çağrılmaz)
        var (pc, on) = await Call(http, h.Token, HttpMethod.Put, "/api/settings", new { judgeClaudeEnabled = true });
        Assert.Equal(HttpStatusCode.OK, pc);
        Assert.True((bool)on!["judgeClaudeEnabled"]!);
        Assert.True((bool)JsonNode.Parse(await http.GetStringAsync("/api/settings"))!["judgeClaudeEnabled"]!);
        var (_, off) = await Call(http, h.Token, HttpMethod.Put, "/api/settings", new { judgeClaudeEnabled = false });
        Assert.False((bool)off!["judgeClaudeEnabled"]!);
        Assert.False((bool)JsonNode.Parse(await http.GetStringAsync("/api/settings"))!["judgeClaudeEnabled"]!);

        // süreç bitince kapı (npm test, bozuk hesap.js) çalıştı: iş kaldı; sonuç zaman çizelgesinde gömülü, komut çıktısı "text"
        var (_, tl2) = await Call(http, h.Token, HttpMethod.Get, $"/api/jobs/{jobId}/timeline?detail=1");
        Assert.Equal("kaldi", (string)tl2!["state"]!);
        var items = tl2["items"]!.AsArray();
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty((string?)i!["id"])));
        var proofItem = items.Single(i => (string)i!["kind"]! == "proof")!;
        Assert.Equal("failed", (string)proofItem["verdict"]!);
        Assert.Equal("text", (string)proofItem["results"]![0]!["artifactKind"]!);
    }

    // ------------------------------------------------------------------ bütünleştirme 3: süreç bitince hüküm, kimlikler, gömülü kanıt
    [Fact]
    public async Task RunEnd_Without_Verdict_Runs_Gate_Job_Is_Never_Left_Running()
    {
        using var rig = new Rig();
        // ölçüt var ve geçer: kapıyı Söz kendisi çalıştırır, iş geçti
        rig.W.AddCheck(rig.ProjectId, null, "command", "Her zaman geçer", new JsonObject { ["cmd"] = "exit 0", ["expectExit"] = 0 }, 60, true);
        var j1 = rig.Job("iş bir [[sahte:oku]]");
        await rig.Finish(rig.Rm.Start(j1.Id));
        var a = rig.W.Job(j1.Id);
        Assert.Equal(JobStates.Passed, a.State);
        Assert.Equal(Verdicts.Passed, a.LastVerdict);
        Assert.Single(rig.W.Run(a.LastRunId!.Value).Results!);

        // ikinci ölçüt kalır: iş kaldı
        rig.W.AddCheck(rig.ProjectId, null, "command", "Hep kalır", new JsonObject { ["cmd"] = "exit 1", ["expectExit"] = 0 }, 60, true);
        var j2 = rig.Job("iş iki [[sahte:oku]]");
        await rig.Finish(rig.Rm.Start(j2.Id));
        var b = rig.W.Job(j2.Id);
        Assert.Equal(JobStates.Failed, b.State);
        Assert.Equal(Verdicts.Failed, b.LastVerdict);
    }

    [Fact]
    public async Task RunEnd_Agent_Question_Waits_For_Reply_Without_Gate()
    {
        using var rig = new Rig();
        rig.W.AddCheck(rig.ProjectId, null, "command", "Hep kalır", new JsonObject { ["cmd"] = "exit 1", ["expectExit"] = 0 }, 60, true);
        var job = rig.Job("iş [[sahte:soru]]");
        await rig.Finish(rig.Rm.Start(job.Id));
        var j = rig.W.Job(job.Id);
        Assert.Equal("soru", j.AgentState);
        Assert.Equal(JobStates.AwaitingAnswer, j.State); // calisiyor değil: cevap bekliyor
        Assert.False(string.IsNullOrWhiteSpace(j.Question)); // soru metni karta gider
        using var c = rig.Env.Svc.Db.Open();
        Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM proof_runs WHERE job_id=@j", null, ("@j", job.Id))); // soruya kapı uygulanmaz
    }

    [Fact]
    public async Task RunEnd_Crash_Is_Failed_With_Plain_Reason_And_Job_Not_Running()
    {
        using var rig = new Rig();
        var job = rig.Job("iş [[sahte:hata]]");
        var run = await rig.Finish(rig.Rm.Start(job.Id));
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.False(string.IsNullOrWhiteSpace(run.FailReason));
        Assert.NotEqual(JobStates.Running, rig.W.Job(job.Id).State);
    }

    [Fact]
    public async Task Stopped_Job_Is_Stopped_State_And_Resumable()
    {
        // Denetim 2: durdurulan iş "durduruldu" durumuna girer; "Bu işe devam et" ile sürdürülebilir.
        using var rig = new Rig();
        var job = rig.Job("uzun [[sahte:uzun]]");
        var run = rig.Rm.Start(job.Id)!;
        for (var i = 0; i < 100 && !File.Exists(rig.TorunFile); i++) await Task.Delay(100);
        Assert.True(rig.Rm.Stop(job.Id));
        await rig.Finish(run);
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.Stopped, j.State);
        Assert.Equal(RunStatus.Stopped, j.Run!.Status);
        Assert.Null(j.LastVerdict);
    }

    [Fact]
    public void Timeline_Has_Stable_Ids_One_Approval_Row_And_Embeds_Proof_Results()
    {
        using var rig = new Rig();
        var job = rig.Job("iş");
        long lineId;
        using (var c = rig.Env.Svc.Db.Open())
        {
            var runId = RunStore.Insert(c, job.Id, rig.ProjectId, "start");
            lineId = RunStore.AddLine(c, runId, job.Id, rig.ProjectId, "progress", "hesap.js dosyasını okuyor", null, live: false);
            RunStore.AddLine(c, runId, job.Id, rig.ProjectId, "approval", "senden onay bekliyor: rm -rf x", null, live: false); // run_lines kopyası
            RunStore.AddApproval(c, runId, job.Id, 77, "Bash", "rm -rf x", "h1");
            var pr = ProofStore.Begin(c, new ProofStore.RunRequest(rig.ProjectId, job.Id, null, "run_end", 2));
            c.Exec("INSERT INTO proof_results(run_id, check_id, label, status, exit_code, output_tail, artifact_path, duration_ms) VALUES(@r,1,'Testler','failed',1,'x','cikti-1.txt',5)", null, ("@r", pr));
            c.Exec("INSERT INTO proof_results(run_id, check_id, label, status, exit_code, output_tail, artifact_path, duration_ms) VALUES(@r,2,'Ekran','passed',NULL,NULL,'ekran-2.png',7)", null, ("@r", pr));
            c.Exec("UPDATE proof_runs SET finished_at=@n, verdict='failed', summary='1 ölçüt kaldı' WHERE id=@i", null, ("@n", Time.NowIso()), ("@i", pr));
        }
        using var c2 = rig.Env.Svc.Db.Open();
        var tl = RunStore.Timeline(c2, job.Id, includeDetail: true);
        Assert.Single(tl, i => i.Kind == "approval");                                   // 1: aynı onay iki satır değil
        Assert.All(tl, i => Assert.False(string.IsNullOrEmpty(i.Id)));                  // 2: her satırın kalıcı kimliği var
        Assert.Equal(tl.Count, tl.Select(i => i.Id).Distinct().Count());
        Assert.Equal("l" + lineId, tl.Single(i => i.Kind == "progress").Id);
        Assert.Equal(lineId, tl.Single(i => i.Kind == "progress").LineId);
        Assert.Equal("a77", tl.Single(i => i.Kind == "approval").Id);
        var proof = tl.Single(i => i.Kind == "proof");                                  // 3: sonuçlar gömülü
        Assert.Equal(2, proof.Attempt);
        Assert.Equal(new[] { "text", "image" }, proof.Results!.Select(r => r.ArtifactKind).ToArray()); // 5: tür işaretli
        Assert.Equal("1 ölçüt kaldı", proof.Detail);
        Assert.Null(RunStore.Timeline(c2, job.Id, includeDetail: false).Single(i => i.Kind == "proof").Results); // ayrıntısız çağrıda gömülmez
    }

    [Fact]
    public void ArtifactKind_Marks_Command_Output_As_Text_And_Screenshot_As_Image()
    {
        Assert.Equal("text", ProofStore.ArtifactKindOf("cikti-3.txt"));
        Assert.Equal("image", ProofStore.ArtifactKindOf("ekran-4.png"));
        Assert.Equal("file", ProofStore.ArtifactKindOf("x.bin"));
        Assert.Null(ProofStore.ArtifactKindOf(null));
    }
}
