using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Xunit;

namespace Soz.Core.Tests;

public class StopHookTests
{
    private static string Hook(TestEnv env, string ev, string stdin, string? dataDir = null)
    {
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run(ev, new StringReader(stdin), o, dataDir ?? env.DataDir, _ => null));
        return o.ToString();
    }

    private static string J(object o) => JsonSerializer.Serialize(o);

    private static string StopInput(string session, string cwd, string? msg = "Bitti, testler geçti.", bool active = false)
        => J(new Dictionary<string, object?> { ["session_id"] = session, ["transcript_path"] = "t.jsonl", ["cwd"] = cwd, ["hook_event_name"] = "Stop", ["stop_hook_active"] = active, ["last_assistant_message"] = msg });

    [Fact]
    public void Queue_Message_Is_Delivered_On_Next_Stop()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "kuyruk-proje");
        Directory.CreateDirectory(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var q = w.Enqueue(pid, null, "README'ye de bir satır ekle");
        Assert.Null(q.DeliveredAt);

        var o = JsonNode.Parse(Hook(env, "stop", StopInput("s-q", dir)))!;
        Assert.Equal("block", (string?)o["decision"]);
        Assert.Equal("Kullanıcıdan mesaj: README'ye de bir satır ekle", (string?)o["reason"]);
        Assert.NotNull(w.Queue(pid, null, false)[0].DeliveredAt);
        Assert.Empty(w.Queue(pid, null, true));

        // İkinci duruş: kuyruk boş, kapı kapalı → izin (boş çıktı).
        Assert.Equal("", Hook(env, "stop", StopInput("s-q", dir)));
    }

    [Fact]
    public void Hero_Flow_Block_With_Test_Name_Then_Pass_After_Fix()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var s = w.SuggestChecks(pid);
        var check = Assert.Single(s.Checks);
        Assert.Equal("npm test", (string?)check.Spec["cmd"]);

        // İş kartı: kapalı öneri seçildi → yalnız bu iş için açık kopya.
        var job = w.CreateJob(pid, "Toplama hatasını düzelt, testler geçsin.", checkIds: new[] { check.Id });
        Assert.False(w.Checks(pid).Single().Enabled);
        Assert.Single(job.Checks);
        w.PatchJob(job.Id, JobStates.Given);

        // Kullanıcı istemi terminale yapıştırır → iş oturuma bağlanır.
        Hook(env, "user-prompt-submit", J(new { session_id = "s-hero", cwd = dir, prompt = job.PromptText }));
        var linked = w.Job(job.Id);
        Assert.Equal(JobStates.Running, linked.State);
        Assert.NotNull(linked.SessionId);

        // Ajan "bitti" der → kapı testi çalıştırır, kaldı → block + kalan testin adı.
        var o1 = JsonNode.Parse(Hook(env, "stop", StopInput("s-hero", dir)))!;
        Assert.Equal("block", (string?)o1["decision"]);
        var reason = (string)o1["reason"]!;
        Assert.Contains("toplama iki sayiyi toplar", reason);
        Assert.Contains("tur 1/3", reason);
        Assert.Contains("Testler geçsin", reason);

        // Ajan düzeltir, yeniden durur (stop_hook_active=true) → geçti, izin, iş gecti.
        NodeSample.Fix(dir);
        var out2 = Hook(env, "stop", StopInput("s-hero", dir, "Düzelttim, bitti.", active: true));
        var o2 = JsonNode.Parse(out2)!;
        Assert.Null(o2["decision"]);
        Assert.Contains("geçti", (string?)o2["systemMessage"]);
        var done = w.Job(job.Id);
        Assert.Equal(JobStates.Passed, done.State);
        Assert.Equal(2, done.Attempts);
        Assert.Equal(Verdicts.Passed, done.LastVerdict);
        Assert.Equal("bitti dedi / deneme sonucu: geçti", done.Runs![0].Claim!.Label);
        Assert.Equal("bitti dedi / deneme sonucu: olmadı", done.Runs![1].Claim!.Label);

        // K-2: iş geçti ama oturum sürüyor; sonraki duruşta kapı yine koşar (geçtiden sonra bozulma yakalanır). Burada hâlâ yeşil: yine geçti.
        var o3 = JsonNode.Parse(Hook(env, "stop", StopInput("s-hero", dir)))!;
        Assert.Null(o3["decision"]);
        Assert.Contains("geçti", (string?)o3["systemMessage"]);
        Assert.Equal(JobStates.Passed, w.Job(job.Id).State);
        Assert.Equal(3, w.Runs(pid, null).Count);

        // Sabah raporu ve Bugün işi görür.
        var m = w.Morning();
        Assert.Single(m.Passed);
        Assert.StartsWith("1 iş deneme sonucuyla bitti", m.Sentence);
        Assert.Equal(1, w.Now().Counts.JobsPassed);
    }

    [Fact]
    public void Max_Attempts_Then_Allows_And_Marks_Failed()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler geçsin", new JsonObject { ["cmd"] = "npm test" }, 120, enabled: true);
        var job = w.CreateJob(pid, "Gece işi: testleri geçir.", night: true);
        w.PatchJob(job.Id, JobStates.Given);
        // Gece işi yalnız kendi istemini alan oturuma bağlanır.
        Hook(env, "user-prompt-submit", J(new { session_id = "s-gece", cwd = dir, prompt = job.PromptText }));
        Assert.Equal("block", (string?)JsonNode.Parse(Hook(env, "stop", StopInput("s-gece", dir)))!["decision"]);
        Assert.Equal("block", (string?)JsonNode.Parse(Hook(env, "stop", StopInput("s-gece", dir, active: true)))!["decision"]);
        var third = JsonNode.Parse(Hook(env, "stop", StopInput("s-gece", dir, active: true)))!;
        Assert.Null(third["decision"]); // 3. tur: artık bırak
        Assert.Contains("olmadı", (string?)third["systemMessage"]);
        var j = w.Job(job.Id);
        Assert.Equal(JobStates.Failed, j.State);
        Assert.Equal(3, j.Attempts);
        var m = w.Morning();
        var f = Assert.Single(m.Failed);
        Assert.Equal(3, f.Attempts);
        Assert.Equal(new List<string> { "Testler geçsin" }, f.FailedChecks);
    }

    [Fact]
    public void Night_Job_Is_Not_Bound_To_Another_Session()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var s = w.SuggestChecks(pid);
        var job = w.CreateJob(pid, "Gece işi: testleri geçir.", night: true, checkIds: new[] { s.Checks.Single().Id });
        w.PatchJob(job.Id, JobStates.Given);

        // Başka bir oturum (istemi eşleşmeyen) aynı projede durur: iş ona BAĞLANMAZ, işin kendi ölçütü onda çalışmaz.
        Hook(env, "user-prompt-submit", J(new { session_id = "s-baska", cwd = dir, prompt = "README dosyasını oku." }));
        var outOther = Hook(env, "stop", StopInput("s-baska", dir));
        var j1 = w.Job(job.Id);
        Assert.Null(j1.SessionId);
        Assert.Equal(JobStates.Given, j1.State);
        Assert.Equal(0, j1.Attempts);
        Assert.DoesNotContain("block", outOther);
        // Proje kapısı gece işi bitene kadar açık sayılır: açık proje ölçütü yok → "ölçüt yok", geçti DEĞİL.
        Assert.Contains("kontrol yok", outOther);

        // Etiketi taşımayan ama metni yalnız benzeyen istem de bağlamaz.
        Hook(env, "user-prompt-submit", J(new { session_id = "s-benzer", cwd = dir, prompt = "Gece işi: testleri geçir." }));
        Assert.Null(w.Job(job.Id).SessionId);

        // Kendi istemi (etiketli) gelen oturuma bağlanır.
        Hook(env, "user-prompt-submit", J(new { session_id = "s-dogru", cwd = dir, prompt = "şunu yap:\n" + job.PromptText.Replace("\n\n", "\n") }));
        var j2 = w.Job(job.Id);
        Assert.NotNull(j2.SessionId);
        Assert.Equal(JobStates.Running, j2.State);
    }

    [Fact]
    public void Orphan_Proof_Run_Becomes_Error_On_Next_Run()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "yetim");
        Directory.CreateDirectory(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        using var c = env.Svc.Db.Open();
        // Kanca öldürülmüş: hükmü boş, 1 saat önce başlamış çalışma.
        var old = Time.UtcNow().AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        c.Exec("INSERT INTO proof_runs(project_id, trigger, attempt, started_at) VALUES(@p,'stop_hook',1,@t)", null, ("@p", pid), ("@t", old));
        var orphan = c.ScalarLong("SELECT last_insert_rowid()");
        // Yeni başlamış (sürüyor olabilecek) çalışma dokunulmadan kalmalı.
        c.Exec("INSERT INTO proof_runs(project_id, trigger, attempt, started_at) VALUES(@p,'manual',1,@t)", null, ("@p", pid), ("@t", Time.NowIso()));
        var fresh = c.ScalarLong("SELECT last_insert_rowid()");
        Assert.Null(w.Run(orphan).Verdict);

        var run = w.RunProof(pid, null);
        Assert.Equal(Verdicts.Unchecked, run.Verdict);
        var o = w.Run(orphan);
        Assert.Equal(Verdicts.Error, o.Verdict);
        Assert.NotNull(o.FinishedAt);
        Assert.Contains("yarıda kesildi", o.Summary);
        Assert.Null(w.Run(fresh).Verdict);
        Assert.Equal(0, ProofStore.ReapOrphans(c, 300));
    }

    [Fact]
    public void Max_Attempts_Setting_Is_Honored()
    {
        using var env = new TestEnv(copySamples: false);
        env.Svc.PutSettings(new JsonObject { ["gateMaxAttempts"] = 1 });
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler geçsin", new JsonObject { ["cmd"] = "npm test" }, 120, enabled: true);
        w.SetGate(pid, true);
        var o = JsonNode.Parse(Hook(env, "stop", StopInput("s-bir", dir)))!;
        Assert.Null(o["decision"]);
        Assert.Equal(Verdicts.Failed, w.Runs(pid, null)[0].Verdict);
    }

    /// <summary>Kanca ham çalıştırma: çıkış kodunu ve çıktıyı DÖNDÜRÜR (sessizce yutmaz); çağıran doğrular.</summary>
    private static (int code, string output) HookRaw(TestEnv env, string ev, string stdin, string? dataDir = null)
    {
        var o = new StringWriter();
        var code = HookHandler.Run(ev, new StringReader(stdin), o, dataDir ?? env.DataDir, _ => null);
        return (code, o.ToString());
    }

    private static void AssertNeverLocks(int code, string output, string what)
    {
        Assert.True(code == 0, $"{what}: çıkış kodu {code} (0 olmalı; sıfırdan farklı kod yardımcıyı kilitler)");
        if (output.Length == 0) return;
        var node = JsonNode.Parse(output); // geçerli JSON olmalı
        Assert.True(node is JsonObject, $"{what}: çıktı JSON nesnesi değil: {output}");
        Assert.NotEqual("block", (string?)node!["decision"]); // hata yolunda yardımcıyı tutan block üretilmez
    }

    [Fact]
    public void Hook_Errors_Never_Lock_The_Agent()
    {
        using var env = new TestEnv(copySamples: false);
        // Bozuk, boş ve beklenmedik girdiler: her kanca için çıkış 0 ve çıktı boş ya da geçerli JSON.
        foreach (var ev in new[] { "stop", "notification", "user-prompt-submit", "pre-tool-use", "session-start" })
            foreach (var input in new[] { "{bozuk", "", "[1,2,3]", "null" })
            {
                var (code, output) = HookRaw(env, ev, input);
                AssertNeverLocks(code, output, $"{ev} girdi='{input}'");
            }
        var (c0, o0) = HookRaw(env, "bilinmeyen-kanca", "{}");
        AssertNeverLocks(c0, o0, "bilinmeyen kanca");
        // Bozuk girdi günlüğe yazılır.
        var log = File.ReadAllText(Path.Combine(env.DataDir, "soz-hook.log"));
        Assert.Contains("stop Json", log); // JsonReaderException vb.

        // Veri klasörü oluşturulamıyor (yol bir dosyanın altında).
        var bad = Path.Combine(env.Root, "dosya.txt");
        File.WriteAllText(bad, "x");
        var (c1, o1) = HookRaw(env, "stop", StopInput("s", env.Root), dataDir: Path.Combine(bad, "veri"));
        AssertNeverLocks(c1, o1, "veri klasörü yok");
        Assert.Equal("", o1);

        // Kanca içinde gerçek bir istisna: oturum tablosu yok → çıkış 0, çıktı boş, günlükte SqliteException.
        using (var c = env.Svc.Db.Open()) c.Exec("DROP TABLE sessions");
        var (c2, o2) = HookRaw(env, "stop", StopInput("s-yok", env.Root));
        AssertNeverLocks(c2, o2, "tablo yok");
        Assert.Equal("", o2);
        Assert.Contains("stop SqliteException", File.ReadAllText(Path.Combine(env.DataDir, "soz-hook.log")));
    }

    [Fact]
    public void Notification_Marks_Needs_User_And_Prompt_Clears_It()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "bildirim");
        Directory.CreateDirectory(dir);
        Assert.Equal("", Hook(env, "notification", J(new { session_id = "s-n", cwd = dir, hook_event_name = "Notification", message = "Claude needs your permission to use Bash", notification_type = "permission_prompt" })));
        var w = new WorkService(env.Svc.Db);
        var now = w.Now();
        var n = Assert.Single(now.NeedsYou);
        Assert.Equal("permission_prompt", n.Reason);
        using (var c = env.Svc.Db.Open())
        {
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE needs_user_since IS NOT NULL"));
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='session_needs_user'"));
        }
        Hook(env, "user-prompt-submit", J(new { session_id = "s-n", cwd = dir, prompt = "Evet, izin veriyorum." }));
        using (var c = env.Svc.Db.Open()) Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE needs_user_since IS NOT NULL"));
    }

    [Fact]
    public void Gate_Closed_Project_Does_Nothing()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler geçsin", new JsonObject { ["cmd"] = "npm test" }, 120, enabled: true);
        Assert.Equal("", Hook(env, "stop", StopInput("s-kapali", dir)));
        Assert.Empty(w.Runs(pid, null));
    }
}
