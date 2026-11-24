using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Guard;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Text;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Bağımsız denetimin bulguları (B-1 ... B-9): her biri önce kırmızı yazıldı, sonra düzeltildi.</summary>
public class DenetimDuzeltmeTests
{
    private static string Hook(TestEnv env, string ev, string stdin)
    {
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run(ev, new StringReader(stdin), o, env.DataDir, _ => null));
        return o.ToString();
    }

    private static string J(object o) => JsonSerializer.Serialize(o);

    private static string StopInput(string session, string cwd, bool active = false, string? msg = "Bitti, testler geçti.")
        => J(new Dictionary<string, object?> { ["session_id"] = session, ["transcript_path"] = "t.jsonl", ["cwd"] = cwd, ["hook_event_name"] = "Stop", ["stop_hook_active"] = active, ["last_assistant_message"] = msg });

    private static JsonObject Cmd(string cmd) => new() { ["cmd"] = cmd, ["expectExit"] = 0 };

    private static string NewDir(TestEnv env, string name)
    {
        var d = Path.Combine(env.Root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // ------------------------------------------------------------------ B-1
    [Fact]
    public void B1_Job_Of_Another_Project_Is_Not_Bound_And_Never_Passes()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var dirX = NewDir(env, "x-proje"); var dirY = NewDir(env, "y-proje");
        var px = w.EnsureProjectByPath(dirX); var py = w.EnsureProjectByPath(dirY);
        w.AddCheck(py, null, "command", "Y kalıcı ölçüt", Cmd("exit 0"), 60, enabled: true);
        var job = w.CreateJob(px, "X projesinin işini bitir, kalan ölçüt geçmeli.");
        w.AddCheck(px, job.Id, "command", "X kalan ölçüt", Cmd("exit 1"), 60, enabled: true);
        w.PatchJob(job.Id, JobStates.Given);

        // İstem iş metniyle eşleşiyor ama oturum başka projede (Y): iş bağlanmamalı.
        Hook(env, "user-prompt-submit", J(new { session_id = "s-y", cwd = dirY, prompt = job.PromptText }));
        var j = w.Job(job.Id);
        Assert.Null(j.SessionId);
        Assert.Equal(JobStates.Given, j.State);
        Hook(env, "stop", StopInput("s-y", dirY));
        Assert.NotEqual(JobStates.Passed, w.Job(job.Id).State);
    }

    [Fact]
    public void B1_Mismatched_Project_Gives_Error_Verdict_Even_When_Forced()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var dirX = NewDir(env, "x-proje"); var dirY = NewDir(env, "y-proje");
        var px = w.EnsureProjectByPath(dirX); var py = w.EnsureProjectByPath(dirY);
        w.AddCheck(py, null, "command", "Y kalıcı ölçüt", Cmd("exit 0"), 60, enabled: true);
        var job = w.CreateJob(px, "X işi.");
        w.AddCheck(px, job.Id, "command", "X kalan ölçüt", Cmd("exit 1"), 60, enabled: true);
        w.PatchJob(job.Id, JobStates.Given);

        // Oturum Y'de açılır; işi doğrudan veritabanından bağla (eski hata yolunu zorla).
        Hook(env, "user-prompt-submit", J(new { session_id = "s-y2", cwd = dirY, prompt = "merhaba" }));
        using (var c = env.Svc.Db.Open())
        {
            var sid = c.ScalarLong("SELECT id FROM sessions WHERE external_id='s-y2'");
            Assert.True(sid > 0);
            c.Exec("UPDATE jobs SET session_id=@s, state='calisiyor' WHERE id=@j", null, ("@s", sid), ("@j", job.Id));
        }
        var o = Hook(env, "stop", StopInput("s-y2", dirY));
        Assert.Contains("proje eşleşmedi", o);
        Assert.NotEqual(JobStates.Passed, w.Job(job.Id).State);
        var run = w.Runs(null, job.Id)[0];
        Assert.Equal(Verdicts.Error, run.Verdict);
        Assert.Contains("proje eşleşmedi", run.Summary);
    }

    // ------------------------------------------------------------------ B-2
    private static ProofRunDto RunWithOutput(TestEnv env, string output, string name)
    {
        var dir = NewDir(env, name);
        File.WriteAllText(Path.Combine(dir, "out.txt"), output);
        File.WriteAllText(Path.Combine(dir, "s.js"), "console.log(require('fs').readFileSync(__dirname + '/out.txt', 'utf8'));");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", Cmd("node s.js"), 60, enabled: true);
        return w.RunProof(pid, null);
    }

    public static IEnumerable<object[]> TestOutputs() => new[]
    {
        // çıktı, beklenen hüküm, özet içinde aranan
        new object[] { "ℹ tests 0\nℹ suites 0\nℹ pass 0\nℹ fail 0\nℹ skipped 0\n", Verdicts.Unchecked, "hiç test koşmadı" },
        new object[] { "ℹ tests 3\nℹ pass 0\nℹ fail 0\nℹ skipped 3\n", Verdicts.Unchecked, "testler atlandı" },
        new object[] { "ℹ tests 5\nℹ pass 4\nℹ fail 0\nℹ skipped 1\n", Verdicts.Passed, "1 test atlandı" },
        new object[] { "ℹ tests 2\nℹ pass 2\nℹ fail 0\nℹ skipped 0\n", Verdicts.Passed, "" },
        new object[] { "Tests:       0 total\nTest Suites: 0 total\n", Verdicts.Unchecked, "hiç test koşmadı" },
        new object[] { "Tests:       2 skipped, 2 total\n", Verdicts.Unchecked, "testler atlandı" },
        new object[] { "Tests:       1 skipped, 2 passed, 3 total\n", Verdicts.Passed, "1 test atlandı" },
        new object[] { " Test Files  1 passed (1)\n      Tests  2 passed | 1 skipped (3)\n", Verdicts.Passed, "1 test atlandı" },
        new object[] { "      Tests  3 skipped (3)\n", Verdicts.Unchecked, "testler atlandı" },
        new object[] { "============ no tests ran in 0.01s ============\n", Verdicts.Unchecked, "hiç test koşmadı" },
        new object[] { "=== 3 skipped in 0.02s ===\n", Verdicts.Unchecked, "testler atlandı" },
        new object[] { "==== 4 passed, 1 skipped in 0.10s ====\n", Verdicts.Passed, "1 test atlandı" },
        new object[] { "....\n4 passed in 0.12s\n", Verdicts.Passed, "" },
        new object[] { "Başarılı!  - Başarısız:     0, Başarılı:     0, Atlanan:     0, Toplam:     0, Süre: 1 ms - x.dll (net10.0)\n", Verdicts.Unchecked, "hiç test koşmadı" },
        new object[] { "Passed!  - Failed:     0, Passed:     0, Skipped:     2, Total:     2, Duration: 1 ms - x.dll (net10.0)\n", Verdicts.Unchecked, "testler atlandı" },
        new object[] { "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 ms - x.dll (net10.0)\n", Verdicts.Passed, "" },
        new object[] { "Test Run Successful.\nTotal tests: 0\n", Verdicts.Unchecked, "hiç test koşmadı" },
        new object[] { "Test Run Successful.\nTotal tests: 4\n     Passed: 3\n    Skipped: 1\n", Verdicts.Passed, "1 test atlandı" },
        // Özeti tanınmayan komutta davranış değişmez.
        new object[] { "merhaba dünya\n", Verdicts.Passed, "" },
    };

    [Theory, MemberData(nameof(TestOutputs))]
    public void B2_Zero_Or_All_Skipped_Tests_Are_Not_Passed(string output, string verdict, string summaryHas)
    {
        using var env = new TestEnv(copySamples: false);
        var run = RunWithOutput(env, output, "b2");
        Assert.Equal(verdict, run.Verdict);
        if (summaryHas.Length > 0) Assert.Contains(summaryHas, run.Summary);
    }

    // ------------------------------------------------------------------ B-3
    [Fact]
    public void B3_Check_And_Gate_Changes_Write_Events()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(NewDir(env, "olay"));
        var ch = w.AddCheck(pid, null, "command", "Testler", Cmd("exit 0"), 60, enabled: true);
        w.PatchCheck(ch.Id, spec: Cmd("exit 0 & rem degisti"));
        w.PatchCheck(ch.Id, enabled: false);
        w.SetGate(pid, true);
        w.SetGate(pid, false);
        w.DeleteCheck(ch.Id);
        using var c = env.Svc.Db.Open();
        Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='check_added'"));
        Assert.Equal(2L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='check_changed'"));
        Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='check_removed'"));
        Assert.Equal(2L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='gate_changed'"));
        var changed = c.Query("SELECT detail FROM events WHERE kind='check_changed' ORDER BY id", r => r.GetString(0));
        Assert.Contains("\"actor\":\"cli\"", changed[0]);
        Assert.Contains("exit 0 & rem degisti", changed[0]);   // yeni tanım
        Assert.Contains("\"cmd\":\"exit 0\"", changed[0]);     // eski tanım
    }

    private static (WorkService w, long pid, string dir, JobDto job, long checkA, long checkB) TamperSetup(TestEnv env, string name)
    {
        var w = new WorkService(env.Svc.Db);
        var dir = NewDir(env, name);
        var pid = w.EnsureProjectByPath(dir);
        var job = w.CreateJob(pid, "Ölçütler iş sırasında değişmesin: " + name);
        var a = w.AddCheck(pid, job.Id, "command", "A ölçütü", Cmd("exit 1"), 60, enabled: true);
        var b = w.AddCheck(pid, job.Id, "command", "B ölçütü", Cmd("exit 0"), 60, enabled: true);
        w.PatchJob(job.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = "s-" + name, cwd = dir, prompt = job.PromptText }));
        Assert.Equal(JobStates.Running, w.Job(job.Id).State);
        return (w, pid, dir, job, a.Id, b.Id);
    }

    [Fact]
    public void B3_Untouched_Checks_Still_Give_Failed_Then_Passed()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job, a, _) = TamperSetup(env, "kontrol");
        // A ölçütü kalır; sorun yok, hüküm kaldı.
        var o = Hook(env, "stop", StopInput("s-kontrol", dir));
        Assert.Contains("block", o);
        Assert.Equal(Verdicts.Failed, w.Runs(null, job.Id)[0].Verdict);
    }

    [Theory]
    [InlineData("degistir")]
    [InlineData("kapat")]
    [InlineData("sil")]
    public void B3_Check_Changed_After_Give_Is_Unchecked_Not_Passed(string how)
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job, a, _) = TamperSetup(env, "oynama-" + how);
        if (how == "degistir") w.PatchCheck(a, spec: Cmd("exit 0"));
        else if (how == "kapat") w.PatchCheck(a, enabled: false);
        else w.DeleteCheck(a);

        var o = Hook(env, "stop", StopInput("s-oynama-" + how, dir));
        var run = w.Runs(null, job.Id)[0];
        Assert.Equal(Verdicts.Unchecked, run.Verdict);
        Assert.Contains("kontrol iş sırasında değişti", run.Summary);
        Assert.NotEqual(JobStates.Passed, w.Job(job.Id).State);
        Assert.DoesNotContain("\"decision\":\"block\"", o);
    }

    [Fact]
    public void B3_Migration_To_V5_Keeps_Existing_Data()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(NewDir(env, "gocu"));
        var job = w.CreateJob(pid, "Göç öncesi iş.");
        w.AddCheck(pid, job.Id, "command", "Ölçüt", Cmd("exit 0"), 60, enabled: true);
        using (var c = env.Svc.Db.Open())
        {
            c.Exec("ALTER TABLE jobs DROP COLUMN checks_hash");
            c.Exec("PRAGMA user_version=4");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var db = SozDb.OpenAndMigrate(env.DataDir);
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion());
        using var c2 = db.Open();
        Assert.Contains("checks_hash", c2.Query("PRAGMA table_info(jobs)", r => r.GetString(1)));
        Assert.Equal(1L, c2.ScalarLong("SELECT COUNT(*) FROM jobs WHERE id=@j AND raw_text='Göç öncesi iş.'", null, ("@j", job.Id)));
        Assert.Equal(1L, c2.ScalarLong("SELECT COUNT(*) FROM checks WHERE job_id=@j", null, ("@j", job.Id)));
    }

    // ------------------------------------------------------------------ B-4
    public static IEnumerable<object[]> NewSecrets() => new[]
    {
        new object[] { "DB_PASSWORD=hunter2hunter2 ile bağlan", "hunter2hunter2" },
        new object[] { "export PGPASSWORD=s3cretPg1 psql -h db", "s3cretPg1" },
        new object[] { "MYSQL_PWD=abc12345 mysqldump x", "abc12345" },
        new object[] { "AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "wJalrXUtnFEMI" },
        new object[] { "GITHUB_TOKEN: " + "ghx12345abcd", "ghx12345abcd" },
        new object[] { "OPENAI_API_KEY=abc123def456", "abc123def456" },
        new object[] { "DATABASE_URL=postgres://" + "admin:p4ssw0rd@db.example.com:5432/app", "p4ssw0rd" },
        new object[] { "git clone https://" + "kullanici:gizliparola@github.com/x/y.git", "gizliparola" },
        new object[] { "curl -H \"Authorization: Basic dXNlcjpwYXNzd29yZA==\" https://x", "dXNlcjpwYXNzd29yZA" },
        new object[] { "Authorization: Bearer abcdefghij", "abcdefghij" },
        new object[] { "mysql -u root -pSuperSecret1 uygulama", "SuperSecret1" },
        new object[] { "mysql -u root -p'Cok Gizli Parola' uygulama", "Gizli" },
        new object[] { "https://hooks.slack.com/" + "services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXXXXXX", "XXXXXXXXXXXX" },
        new object[] { "{\"password\": \"çok sözcüklü değer\"}", "sözcüklü" },
        new object[] { "{\"db_password\": \"bir iki üç\", \"host\": \"localhost\"}", "iki" },
        new object[] { "client_secret = 'a b c d e f'", "d e f" },
    };

    [Theory, MemberData(nameof(NewSecrets))]
    public void B4_Detects_And_Fully_Masks(string text, string mustVanish)
    {
        Assert.True(SecretFilter.ContainsSecret(text), text);
        var masked = SecretFilter.MaskSecrets(text);
        Assert.DoesNotContain(mustVanish, masked);
        Assert.Contains(SecretFilter.Mask, masked);
    }

    public static IEnumerable<object[]> NotSecretsNew() => new[]
    {
        "monkey: 3",
        "Primary key: id sütunu",
        "sort key: name olsun",
        "The keyboard layout is Turkish",
        "TOKEN_LIMIT=8000",
        "https://example.com/a/b?x=1",
        "https://github.com/user/repo.git",
        "http://localhost:3000/api/users",
        "ssh://git@github.com/x/y.git",
        "user@example.com adresine yaz",
        "Authorization header kullanma, oturum çerezi yeterli.",
        "Her anahtar için ayrı dosya aç, KEY değerini kodda tutma.",
        "mysql -u root -p uygulama",
        "mysql --port 3306 --protocol tcp",
        "docker run -p 8080:80 nginx",
        "Parola yöneticisi kullan.",
    }.Select(s => new object[] { s });

    [Theory, MemberData(nameof(NotSecretsNew))]
    public void B4_No_False_Alarm(string text)
    {
        Assert.False(SecretFilter.ContainsSecret(text), text);
        Assert.Equal(text, SecretFilter.MaskSecrets(text));
    }

    [Fact]
    public void B4_Phone_Payload_Passes_Through_The_Filter()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NewDir(env, "tel-sir");
        var msg = "Bağlantı: postgres://" + "admin:p4ssw0rd9@db.x/app ve DB_PASSWORD=hunter2hunter2 ve Authorization: Basic dXNlcjpwYXNzd29yZA== kullanayım mı?";
        Hook(env, "stop", StopInput("tel-sir-1", dir, msg: msg));
        Hook(env, "notification", J(new { session_id = "tel-sir-1", cwd = dir, notification_type = "idle_prompt", message = "bekliyor" }));
        var phone = new PhoneServer(env.Svc, 49555);
        var payload = JsonSerializer.Serialize(phone.NowPayload(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("p4ssw0rd9", payload);
        Assert.DoesNotContain("hunter2hunter2", payload);
        Assert.DoesNotContain("dXNlcjpwYXNzd29yZA", payload);
        Assert.Contains("lastAssistantText", payload);
        Assert.DoesNotContain("p4ssw0rd9", PhoneServer.Short(msg)!);
    }

    // ------------------------------------------------------------------ B-5
    [Fact]
    public void B5_Phone_Message_Is_Prefixed_With_Its_Source()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NewDir(env, "kaynak");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.Enqueue(pid, null, "hesabı kontrol et", "telefon");
        var o = JsonNode.Parse(Hook(env, "stop", StopInput("s-kaynak", dir)))!;
        Assert.Equal("block", (string?)o["decision"]);
        Assert.StartsWith("Telefondan gelen mesaj: hesabı kontrol et", (string?)o["reason"]);
        // Panel mesajı eskisi gibi.
        w.Enqueue(pid, null, "bir de bunu yap", "panel");
        var o2 = JsonNode.Parse(Hook(env, "stop", StopInput("s-kaynak", dir)))!;
        Assert.Equal("Kullanıcıdan mesaj: bir de bunu yap", (string?)o2["reason"]);
    }

    // ------------------------------------------------------------------ B-8
    [Fact]
    public void B8_Stop_Hook_Active_False_Starts_A_New_Chain_Without_Prompt_Hook()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "b8");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler geçsin", Cmd("npm test"), 120, enabled: true);
        w.SetGate(pid, true);

        // UserPromptSubmit kancası YOK: her yeni duruş (active=false) yeni zincirdir.
        for (var i = 0; i < 3; i++)
        {
            var o = JsonNode.Parse(Hook(env, "stop", StopInput("s-b8", dir, active: false)))!;
            Assert.Equal("block", (string?)o["decision"]);
            Assert.Contains("tur 1/3", (string?)o["reason"]);
        }
        // Zincirin devamı (active=true): 2, 3; üst sınırda bırakır.
        Assert.Contains("tur 2/3", (string?)JsonNode.Parse(Hook(env, "stop", StopInput("s-b8", dir, active: true)))!["reason"]);
        Assert.Null(JsonNode.Parse(Hook(env, "stop", StopInput("s-b8", dir, active: true)))!["decision"]); // 3. turda artık bırakır
        // Zincir bitti; yeni duruş (active=false) yine 1. tur.
        Assert.Contains("tur 1/3", (string?)JsonNode.Parse(Hook(env, "stop", StopInput("s-b8", dir, active: false)))!["reason"]);
    }

    // ------------------------------------------------------------------ B-9
    [Fact]
    public void B9_Queue_Message_Is_Never_Marked_Delivered_Without_Reaching_The_Agent()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NewDir(env, "b9");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.Enqueue(pid, null, "kuyruktaki önemli mesaj", "panel");
        // Yan çağrılar (olay günlüğü) patlasın.
        using (var c = env.Svc.Db.Open()) c.Exec("DROP TABLE events");

        var o = Hook(env, "stop", StopInput("s-b9", dir));
        var q = w.Queue(pid, null, false).Single();
        if (q.DeliveredAt is not null) Assert.Contains("kuyruktaki önemli mesaj", o); // iletildi → yardımcıya ulaştı
        else Assert.DoesNotContain("block", o);                                         // bekliyor → hiçbir şey gitmedi
        // Düzeltilmiş davranış: olay günlüğü olmasa da mesaj iletilir.
        Assert.NotNull(q.DeliveredAt);
        Assert.Contains("kuyruktaki önemli mesaj", o);
    }

    // ------------------------------------------------------------------ ek doğrudan testler (yeni API)
    [Fact]
    public void B3_Api_Actor_Is_Recorded_In_Events()
    {
        using var env = new TestEnv(copySamples: false);
        var api = new WorkService(env.Svc.Db) { Actor = "api" };
        var pid = api.EnsureProjectByPath(NewDir(env, "api-olay"));
        var ch = api.AddCheck(pid, null, "command", "Testler", Cmd("exit 0"), 60, enabled: true);
        api.PatchCheck(ch.Id, enabled: false);
        api.SetGate(pid, true);
        using var c = env.Svc.Db.Open();
        var rows = c.Query("SELECT kind, detail FROM events WHERE kind IN ('check_added','check_changed','gate_changed') ORDER BY id", r => (r.GetString(0), r.GetString(1)));
        Assert.Equal(3, rows.Count);
        Assert.All(rows, x => Assert.Contains("\"actor\":\"api\"", x.Item2));
        Assert.Contains("\"enabled\":true", rows[1].Item2);   // eski
        Assert.Contains("\"enabled\":false", rows[1].Item2);  // yeni
    }

    [Fact]
    public void B3_Soz_Data_Dir_Is_Protected_By_The_Cautious_Template()
    {
        var rules = new List<GuardRule>();
        long id = 1;
        foreach (var tr in GuardTemplates.Find("temkinli")!.Rules)
            rules.Add(new GuardRule(id++, 1, tr.Kind, tr.Label, tr.Spec, tr.Action, true, null, "template", null));
        GuardDecision Eval(string tool, string key, string val, string? dataDir)
            => GuardEngine.Evaluate(rules, new GuardInput(tool, new JsonObject { [key] = val }, @"E:\proj", @"E:\proj", @"C:\Users\t", @"C:\Temp", dataDir));
        Assert.Equal("deny", Eval("Write", "file_path", @"E:\proj\kayit\ayar.json", @"E:\proj\kayit").Verdict);
        Assert.Equal("deny", Eval("Bash", "command", @"echo x > E:\proj\kayit\notlar.txt", @"E:\proj\kayit").Verdict);
        Assert.Equal("allow", Eval("Write", "file_path", @"E:\proj\kayit\ayar.json", null).Verdict); // veri klasörü bilinmiyorsa yalnız soz.db kalıpları
        Assert.Equal("deny", Eval("Write", "file_path", @"E:\proj\baska\soz.db", null).Verdict);
        Assert.Equal("allow", Eval("Write", "file_path", @"E:\proj\src\a.cs", @"E:\proj\kayit").Verdict);
    }

    [Theory]
    [InlineData("ℹ tests 5\nℹ pass 4\nℹ skipped 1\n", "node:test", 5, 4, 1)]
    [InlineData("Tests:       1 skipped, 2 passed, 3 total\n", "jest", 3, 2, 1)]
    [InlineData("      Tests  2 passed | 1 skipped (3)\n", "vitest", 3, 2, 1)]
    [InlineData("==== 4 passed, 1 skipped in 0.10s ====\n", "pytest", 5, 4, 1)]
    [InlineData("Başarılı!  - Başarısız:     0, Başarılı:    10, Atlanan:     2, Toplam:    12, Süre: 3 s\nBaşarılı!  - Başarısız:     0, Başarılı:     5, Atlanan:     0, Toplam:     5, Süre: 1 s\n", "dotnet", 17, 15, 2)]
    [InlineData("Total tests: 4\n     Passed: 3\n    Skipped: 1\n", "dotnet", 4, 3, 1)]
    public void B2_Summary_Parser_Reads_Known_Tools(string output, string tool, int total, int passed, int skipped)
    {
        var f = Assert.Single(TestSummary.Parse(output));
        Assert.Equal(new TestSummary.Counts(tool, total, passed, skipped), f);
    }

    [Fact]
    public void B2_Unrecognized_Output_And_Nonzero_Expected_Exit_Do_Not_Change_Behavior()
    {
        Assert.Empty(TestSummary.Parse("hello world\nTotal files: 3\n"));
        Assert.Null(TestSummary.Evaluate("hello").Problem);
        // beklenen çıkış kodu 0 değilse (kasıtlı hata sınaması) özet bakılmaz
        using var env = new TestEnv(copySamples: false);
        var dir = NewDir(env, "b2-bekle");
        File.WriteAllText(Path.Combine(dir, "s.js"), "console.log('ℹ tests 0\nℹ pass 0'); process.exit(1);");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Hata beklenir", new JsonObject { ["cmd"] = "node s.js", ["expectExit"] = 1 }, 60, enabled: true);
        Assert.Equal(Verdicts.Passed, w.RunProof(pid, null).Verdict);
    }
}
