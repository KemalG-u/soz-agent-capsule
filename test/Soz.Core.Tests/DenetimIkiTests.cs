using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Morning;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Core.Text;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Denetim 2 düzeltmeleri (arka uç): K-1 kanıt tabanı, K-2 geçtikten sonra bozulma, K-3 model adı ve kısayol, Ö-1..Ö-6, K-4, K-5, K-7, v7 göçü, zaman çizgisi sırası.
/// Yürütücü ve API senaryoları YurutucuIkiTests.cs içindedir.
/// </summary>
public class DenetimIkiTests
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

    /// <summary>Düzeltilmiş örnek proje (testler geçer), proje kayıtlı, "npm test" ölçütü açık, iş verildi ve terminal oturumuna bağlandı (kanıt tabanı alındı).</summary>
    private static (WorkService w, long pid, string dir, JobDto job) Setup(TestEnv env, string name, bool fixedLib = true, string session = "s")
    {
        var dir = NodeSample.Create(env, name);
        if (fixedLib) NodeSample.Fix(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", Cmd("npm test"), 120, enabled: true);
        var job = w.CreateJob(pid, "Toplamayı incele: " + name);
        w.PatchJob(job.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = session + "-" + name, cwd = dir, prompt = job.PromptText }));
        Assert.Equal(JobStates.Running, w.Job(job.Id).State);
        return (w, pid, dir, job);
    }

    private static void SetTestScript(string dir, string script)
        => File.WriteAllText(Path.Combine(dir, "package.json"), "{ \"name\": \"ornek\", \"version\": \"1.0.0\", \"private\": true, \"scripts\": { \"test\": \"" + script + "\" } }");

    // ------------------------------------------------------------------ K-1
    [Fact]
    public void K1_Emptied_Test_Script_Is_Judged_By_Approved_Tests_Correct_Code_Passes_Broken_Code_Is_Never_Passed()
    {
        using var env = new TestEnv(copySamples: false);
        // e2: ajanın test betiğini boşaltması tek başına alarm değil; onaylı betik ve testler geçici kopyada koşar.
        // Kod doğruysa onaylı testler geçer: yeşil, ama not düşülür.
        var (w, _, dir, job) = Setup(env, "bosalt");
        SetTestScript(dir, "node -e 0");
        var o0 = JsonNode.Parse(Hook(env, "stop", StopInput("s-bosalt", dir)) is { Length: > 0 } s0 ? s0 : "{}")!;
        Assert.Null(o0["decision"]);
        var r0 = w.Runs(null, job.Id)[0];
        Assert.Equal(Verdicts.Passed, r0.Verdict);
        Assert.Contains("Yardımcı onayladığın testlere de dokundu. Sonuç senin onayladığın testlerle alındı.", r0.Summary);
        Assert.Equal(JobStates.Passed, w.Job(job.Id).State);

        // Kod bozuksa (gerçek hile: betiği boşalt, testler geçmiş görünsün) onaylı testler kalır: bir kez geri yollanır, ısrar ederse karar kullanıcıya kalır.
        var (w2, _, dir2, job2) = Setup(env, "bosalt2", fixedLib: false);
        SetTestScript(dir2, "node -e 0");
        var o1 = JsonNode.Parse(Hook(env, "stop", StopInput("s-bosalt2", dir2)))!;
        Assert.Equal("block", (string?)o1["decision"]);
        var r1 = w2.Runs(null, job2.Id)[0];
        Assert.Equal(Verdicts.Unchecked, r1.Verdict);
        Assert.Equal("approved_failed", r1.ReasonCode);
        Assert.Contains("Onayladığın", r1.Summary);
        Assert.Equal(JobStates.Running, w2.Job(job2.Id).State);
        var o2 = JsonNode.Parse(Hook(env, "stop", StopInput("s-bosalt2", dir2, active: true)))!;
        Assert.Null(o2["decision"]);
        Assert.Contains("Karar sende", (string?)o2["systemMessage"]);
        Assert.Equal(JobStates.AwaitingDecision, w2.Job(job2.Id).State);
        Assert.NotEqual(JobStates.Passed, w2.Job(job2.Id).State);
    }

    [Fact]
    public void K1_Skipped_Test_Is_Judged_By_Approved_Tests_Skipping_The_Failing_Test_Does_Not_Hide_Broken_Code()
    {
        using var env = new TestEnv(copySamples: false);
        // Kod doğru, ajan bir testi atladı: onaylı test geçer, yeşil + not.
        var (w, _, dir, job) = Setup(env, "atla");
        var f = Path.Combine(dir, "test", "hesap.test.js");
        File.WriteAllText(f, File.ReadAllText(f).Replace("test('toplama", "test.skip('toplama"));
        Hook(env, "stop", StopInput("s-atla", dir));
        var r = w.Runs(null, job.Id)[0];
        Assert.Equal(Verdicts.Passed, r.Verdict);
        Assert.Contains("Yardımcı onayladığın testlere de dokundu", r.Summary);

        // Kod bozuk ve kalan (başarısız) test atlandı: ajanın bıraktığı hâl yeşil görünür, onaylı test kalır.
        var (w2, _, dir2, job2) = Setup(env, "atla2", fixedLib: false);
        var f2 = Path.Combine(dir2, "test", "hesap.test.js");
        File.WriteAllText(f2, File.ReadAllText(f2).Replace("test('toplama", "test.skip('toplama"));
        Hook(env, "stop", StopInput("s-atla2", dir2));
        var r2 = w2.Runs(null, job2.Id)[0];
        Assert.Equal(Verdicts.Unchecked, r2.Verdict);
        Assert.Equal("approved_failed", r2.ReasonCode);
        Assert.NotEqual(JobStates.Passed, w2.Job(job2.Id).State);
    }

    [Fact]
    public void K1_Deleted_Extra_Test_File_Is_Restored_In_The_Verdict_Run_And_Judged_By_Approved_Tests()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "sil");
        File.WriteAllText(Path.Combine(dir, "test", "ikinci.test.js"), "'use strict';\nconst test = require('node:test');\ntest('bir', () => {});\n");
        // ikinci test dosyası tabana girsin: işi yeniden başlat gibi tabanı yenile
        using (var c = env.Svc.Db.Open()) EvidenceBase.TakeForJob(c, job.Id, force: true);
        File.Delete(Path.Combine(dir, "test", "ikinci.test.js"));
        Hook(env, "stop", StopInput("s-sil", dir));
        var r = w.Runs(null, job.Id)[0];
        // e2: silinen onaylı test geçici kopyaya geri konur ve koşar; hepsi geçtiği için yeşil, silinme notla söylenir.
        Assert.Equal(Verdicts.Passed, r.Verdict);
        Assert.Contains("Yardımcı onayladığın testlere de dokundu", r.Summary);
        Assert.Contains(r.Diffs!, d => d.Kind == "test" && d.Path == "test/ikinci.test.js");
    }

    [Fact]
    public void K1_New_Test_File_And_New_Dependency_Are_Not_False_Alarms()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "yeni");
        File.WriteAllText(Path.Combine(dir, "test", "yeni.test.js"), "'use strict';\nconst test = require('node:test');\ntest('yeni test', () => {});\n");
        File.WriteAllText(Path.Combine(dir, "package.json"), "{ \"name\": \"ornek\", \"version\": \"1.0.0\", \"private\": true, \"scripts\": { \"test\": \"node --test\" }, \"dependencies\": { \"x\": \"1.0.0\" } }");
        // gerçek ajan "npm install x" der: kilit dosyası da güncellenir (kilitsiz ekleme ayrı testte kuşkulu sayılır)
        File.WriteAllText(Path.Combine(dir, "package-lock.json"), "{ \"name\": \"ornek\", \"lockfileVersion\": 3, \"packages\": { \"node_modules/x\": { \"version\": \"1.0.0\" } } }");

        Hook(env, "stop", StopInput("s-yeni", dir));
        var r = w.Runs(null, job.Id)[0];
        Assert.Equal(Verdicts.Passed, r.Verdict);
        Assert.Null(r.ReasonCode);
        Assert.Contains("test/yeni.test.js", r.AddedTests!);
        Assert.Contains("Yeni test dosyası eklendi", r.Summary);
        Assert.Equal(JobStates.Passed, w.Job(job.Id).State);
        Assert.False(w.Job(job.Id).AcceptedByUser); // kendi kanıtıyla geçti
    }

    [Fact]
    public void K1_Count_Dropped_When_Readable_Test_Count_Falls_Below_Last_Trusted_Result()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "say");
        Directory.CreateDirectory(Path.Combine(dir, "test"));
        File.WriteAllText(Path.Combine(dir, "package.json"), NodeSample.Package);
        File.WriteAllText(Path.Combine(dir, "test", "sayili.test.js"),
            "'use strict';\nconst test = require('node:test');\nconst n = Number(process.env.SOZ_TEST_N || 2);\nfor (let i = 0; i < n; i++) test('t' + i, () => {});\n");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", Cmd("npm test"), 120, enabled: true);
        try
        {
            Environment.SetEnvironmentVariable("SOZ_TEST_N", "2");
            var baseline = w.RunProof(pid, null); // güvenilir ilk sonuç: 2 test
            Assert.Equal(Verdicts.Passed, baseline.Verdict);
            Assert.Equal(2, baseline.Results![0].TestsTotal);

            var job = w.CreateJob(pid, "Say.");
            w.PatchJob(job.Id, JobStates.Given);
            Hook(env, "user-prompt-submit", J(new { session_id = "s-say", cwd = dir, prompt = job.PromptText }));
            Environment.SetEnvironmentVariable("SOZ_TEST_N", "1"); // ajan testi azaltır (dosyalar aynı)
            Hook(env, "stop", StopInput("s-say", dir));
            var r = w.Runs(null, job.Id)[0];
            Assert.Equal(Verdicts.Unchecked, r.Verdict);
            Assert.Equal("count_dropped", r.ReasonCode);
            Assert.Contains("Test sayısı düştü", r.Summary);
        }
        finally { Environment.SetEnvironmentVariable("SOZ_TEST_N", null); }
    }

    [Fact]
    public void K1_Accept_Makes_It_Passed_By_User_And_Reject_Makes_It_Failed()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "kabul");
        File.WriteAllText(Path.Combine(dir, "hesap.js"), NodeSample.BrokenLib);
        File.WriteAllText(Path.Combine(dir, "test", "hesap.test.js"), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));   // e3: ayar dosyası alarm değil; karar bekletmek için gevşetilmiş test + bozuk kod
        Hook(env, "stop", StopInput("s-kabul", dir));
        Hook(env, "stop", StopInput("s-kabul", dir, active: true));
        Assert.Equal(JobStates.AwaitingDecision, w.Job(job.Id).State);
        Assert.Contains(w.Jobs(null, JobStates.AwaitingDecision), x => x.Id == job.Id);

        var accepted = w.Accept(job.Id, true);
        Assert.Equal(JobStates.Passed, accepted.State);
        Assert.True(accepted.AcceptedByUser);
        Assert.True(accepted.Runs![0].Accepted);
        Assert.Equal(409, Assert.Throws<SozException>(() => w.Accept(job.Id, true)).Status); // artık karar beklemiyor

        // kabulle taban yenilendi: aynı klasörde sonraki iş uyarısız
        var j2 = w.CreateJob(w.Job(job.Id).ProjectId, "Sonraki iş.");
        w.PatchJob(j2.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = "s-kabul-2", cwd = dir, prompt = j2.PromptText }));
        Assert.Null(w.Job(j2.Id).BaseNote);

        // reddetme
        var (w3, _, dir3, job3) = Setup(env, "red");
        File.WriteAllText(Path.Combine(dir3, "hesap.js"), NodeSample.BrokenLib);
        File.WriteAllText(Path.Combine(dir3, "test", "hesap.test.js"), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        Hook(env, "stop", StopInput("s-red", dir3));
        Hook(env, "stop", StopInput("s-red", dir3, active: true));
        Assert.Equal(JobStates.Failed, w3.Accept(job3.Id, false).State);
        Assert.False(w3.Job(job3.Id).AcceptedByUser);
    }

    [Fact]
    public void K1_Next_Job_Warns_When_Definition_Differs_From_Last_Approved_Until_User_Accepts()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, pid, dir, job) = Setup(env, "taban");
        File.WriteAllText(Path.Combine(dir, "hesap.js"), NodeSample.BrokenLib);
        File.WriteAllText(Path.Combine(dir, "test", "hesap.test.js"), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        Hook(env, "stop", StopInput("s-taban", dir));
        Hook(env, "stop", StopInput("s-taban", dir, active: true));
        w.Accept(job.Id, false); // kullanıcı reddetti; bozuk tanım klasörde kaldı

        var j2 = w.CreateJob(pid, "İkinci iş: değişmiş tanımla başlıyor.");
        w.PatchJob(j2.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", J(new { session_id = "s-taban-2", cwd = dir, prompt = j2.PromptText }));
        Assert.Equal(EvidenceBase.ProjectNote, w.Job(j2.Id).BaseNote);
        Hook(env, "stop", StopInput("s-taban-2", dir));
        var r = w.Runs(null, j2.Id)[0];
        Assert.Equal(Verdicts.Unchecked, r.Verdict);
        Assert.Equal("approved_failed", r.ReasonCode);
        Assert.Contains("Yardımcı testlere dokunmadı, sorun kodda görünüyor.", r.Summary);   // e3 (K2): gevşetme önceki işten kalmış
    }

    [Fact]
    public void K1_Base_Scan_Reads_Defs_And_Tests_And_Skips_Noise()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "tarama");
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "x"));
        File.WriteAllText(Path.Combine(dir, "node_modules", "x", "a.test.js"), "x");
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        File.WriteAllText(Path.Combine(dir, "ornek.csproj"), "<Project/>");
        var s = EvidenceBase.Scan(dir);
        Assert.Contains(s.Suspects, f => f.Path == "package.json");
        Assert.Contains(s.Suspects, f => f.Path == "ornek.csproj");
        Assert.Contains(s.Suspects, f => f.Path == "test/hesap.test.js");
        // Zararsız dosya (README) yalnız sayılır, kuşkulu listeye girmez; node_modules hiç taranmaz.
        Assert.DoesNotContain(s.Suspects, f => f.Path.Contains("node_modules") || f.Path == "README.md");
        Assert.True(s.FileCount >= 4);
        // package.json alan alan karşılaştırılır: kilitle birlikte bağımlılık eklemek kuşkulu değil, kullanılan betiği değiştirmek kuşkulu
        var ctx = Soz.Core.Proof.ScanContext.Build(dir, new[] { "npm test" });
        var b0 = EvidenceBase.Scan(dir, ctx);
        File.WriteAllText(Path.Combine(dir, "package.json"), NodeSample.Package.Replace("\"private\": true", "\"private\": true, \"dependencies\": { \"y\": \"1\" }"));
        File.WriteAllText(Path.Combine(dir, "package-lock.json"), "{ \"lockfileVersion\": 3, \"packages\": { \"node_modules/y\": { \"version\": \"1\" } } }");
        Assert.DoesNotContain("package.json", EvidenceBase.Compare(b0, EvidenceBase.Scan(dir, ctx)).ChangedConfig);
        File.WriteAllText(Path.Combine(dir, "package.json"), "{ \"name\": \"ornek\", \"version\": \"1.0.0\", \"private\": true, \"scripts\": { \"test\": \"echo ok\" } }");
        // e2: kullanılan betiğin değişmesi artık alarm değil; hüküm koşusu onaylı betikle koşar, değişiklik karta not düşer.
        var sd = EvidenceBase.Compare(b0, EvidenceBase.Scan(dir, ctx));
        Assert.DoesNotContain("package.json", sd.ChangedConfig);
        Assert.Contains(sd.NoteList, n => n.Contains("sonuç onayladığın hâliyle alınır"));
        Assert.True(ApprovedSet.Changes(b0, EvidenceBase.Scan(dir, ctx)).Edited.Contains("package.json"));
    }

    // ------------------------------------------------------------------ K-2
    [Fact]
    public void K2_Pass_Then_Break_Same_Session_Final_Verdict_Is_Failed_With_Note()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "bozulma");
        Hook(env, "stop", StopInput("s-bozulma", dir)); // geçer
        Assert.Equal(JobStates.Passed, w.Job(job.Id).State);

        File.WriteAllText(Path.Combine(dir, "hesap.js"), NodeSample.BrokenLib); // ajan çalışmaya devam edip kodu bozdu
        var o = JsonNode.Parse(Hook(env, "stop", StopInput("s-bozulma", dir)))!;
        Assert.Equal("block", (string?)o["decision"]); // kapı yine koştu ve geri yolladı
        var r = w.Runs(null, job.Id)[0];
        Assert.Equal(Verdicts.Failed, r.Verdict);
        Assert.Contains("Önceki duruşta geçmişti, sonra bozuldu.", r.Summary);
        Assert.NotEqual(JobStates.Passed, w.Job(job.Id).State);

        // ısrarla bozuk kalırsa nihai hüküm kaldi
        Hook(env, "stop", StopInput("s-bozulma", dir, active: true));
        Hook(env, "stop", StopInput("s-bozulma", dir, active: true));
        Assert.Equal(JobStates.Failed, w.Job(job.Id).State);
        Assert.Equal(Verdicts.Failed, w.Job(job.Id).LastVerdict);
    }

    [Fact]
    public void K2_Pass_Then_Gate_Command_Stops_Working_Is_Not_Passed_Anymore()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "belirsiz");
        Hook(env, "stop", StopInput("s-belirsiz", dir));
        Assert.Equal(JobStates.Passed, w.Job(job.Id).State);
        File.Delete(Path.Combine(dir, "package.json")); // ölçüt komutu artık çalışmaz: hüküm geçti olamaz
        Hook(env, "stop", StopInput("s-belirsiz", dir));
        Assert.NotEqual(JobStates.Passed, w.Job(job.Id).State);
    }

    // ------------------------------------------------------------------ K-3
    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("sonnet", true)]
    [InlineData("opus[1m]", true)]
    [InlineData("", true)]
    [InlineData("x&echo,PWNED>pwn-model.txt", false)]
    [InlineData("x y", false)]
    [InlineData("x\"y", false)]
    [InlineData("$(calc)", false)]
    [InlineData("a|b", false)]
    public void K3_Model_Name_Validation(string name, bool ok)
    {
        Assert.Equal(ok, ModelName.Check(name) is null);
        using var env = new TestEnv(copySamples: false);
        var patch = new JsonObject { ["runnerModel"] = name };
        Assert.Equal(ok, SozSettings.Validate(patch) is null);
    }

    [Fact]
    public void K3_Cmd_Shim_Is_Never_Run_Through_Cmd_Exe_Node_And_Cli_Js_Are_Resolved()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "npm-kisayol");
        var cli = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.WriteAllText(cli, "console.log(1)");
        var shim = Path.Combine(dir, "claude.cmd");
        File.WriteAllText(shim, "@ECHO off\r\nSETLOCAL\r\nSET dp0=%~dp0\r\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n");
        var res = ClaudeLocator.ResolveDetailed(shim);
        Assert.Null(res.Error);
        Assert.NotNull(res.Cmd);
        Assert.DoesNotContain("cmd", Path.GetFileName(res.Cmd!.FileName), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(cli), res.Cmd.PrefixArgs[0]);

        // kısayol çözülemezse: sade hata, cmd.exe asla
        var bad = Path.Combine(env.Root, "bozuk", "claude.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllText(bad, "@echo off\r\nrem hedef yok\r\n");
        var r2 = ClaudeLocator.ResolveDetailed(bad);
        Assert.Null(r2.Cmd);
        Assert.Equal(ClaudeLocator.ShimText, r2.Error);
    }

    // ------------------------------------------------------------------ Ö-2
    [Theory]
    [InlineData("  0 passing (3ms)\n", "mocha")]
    [InlineData("?   \texample.com/x\t[no test files]\n", "go")]
    [InlineData("running 0 tests\n\ntest result: ok. 0 passed; 0 failed\n", "cargo")]
    [InlineData("No test is available in x.dll. Make sure that test discoverers & executors are registered.\n", "dotnet")]
    [InlineData("Kullanılabilir test yok.\n", "dotnet")]
    [InlineData("No test files found, exiting with code 1\n", "vitest")]
    [InlineData("bun test v1.1\nRan 0 tests across 0 files.\n", "bun")]
    [InlineData("TAP version 13\n1..0\n", "tap")]
    public void O2_Zero_Test_Markers_Mean_No_Tests_Ran(string output, string tool)
    {
        Assert.Equal(tool, TestSummary.NoTestMarker(output));
        Assert.Equal(TestSummary.NoneRan, TestSummary.Evaluate(output).Problem);
    }

    [Fact]
    public void O2_Markers_Do_Not_Fire_When_Tests_Actually_Ran()
    {
        Assert.Null(TestSummary.NoTestMarker("  2 passing (4ms)\n  1 failing\n"));
        Assert.Null(TestSummary.NoTestMarker("ok  \texample.com/a\t0.1s\n?   \texample.com/b\t[no test files]\n"));
        Assert.Null(TestSummary.NoTestMarker("running 3 tests\n"));
    }

    [Fact]
    public void O2_Unrecognized_Output_With_Test_Command_Stays_Passed_But_Notes_Unknown_Count()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "tanimsiz");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "t.js"), "console.log('hepsi tamam');");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", Cmd("node t.js --test"), 60, enabled: true);
        var r = w.RunProof(pid, null);
        Assert.Equal(Verdicts.Passed, r.Verdict);
        Assert.True(r.CountUnknown);
        Assert.Contains("Test sayısını okuyamadım", r.Summary);
    }

    [Fact]
    public void O2_Marker_In_Real_Command_Gives_Unchecked_No_Tests()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "sifir");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "t.js"), "console.log('  0 passing (1ms)');");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", Cmd("node t.js"), 60, enabled: true);
        var r = w.RunProof(pid, null);
        Assert.Equal(Verdicts.Unchecked, r.Verdict);
        Assert.Equal("no_tests", r.ReasonCode);
    }

    // ------------------------------------------------------------------ Ö-3
    [Fact]
    public void O3_Agent_Question_Does_Not_Run_Gate_Job_Waits_For_Answer_Then_Resumes()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "soru", fixedLib: false);
        var o = Hook(env, "stop", StopInput("s-soru", dir, msg: "İki yol var: A ya da B. Hangisini seçeyim?"));
        Assert.Equal("", o); // Stop'a izin: kapı zorla sürdürmedi
        Assert.Empty(w.Runs(null, job.Id)); // kapı koşmadı
        var j = w.Job(job.Id);
        Assert.Equal(JobStates.AwaitingAnswer, j.State);
        Assert.Contains("Hangisini seçeyim", j.Question);

        // kullanıcı cevap yazınca iş yeniden çalışıyor olur
        Hook(env, "user-prompt-submit", J(new { session_id = "s-soru", cwd = dir, prompt = "A olsun." }));
        Assert.Equal(JobStates.Running, w.Job(job.Id).State);
    }

    [Fact]
    public void O3_Done_And_Polite_Question_Is_Not_A_Question_Gate_Runs()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, dir, job) = Setup(env, "nazik", fixedLib: false);
        Hook(env, "stop", StopInput("s-nazik", dir, msg: "Bitti. Başka bir şey ister misin?"));
        Assert.Single(w.Runs(null, job.Id)); // kapı koştu: "bitti" diyen yardımcı soru atlatması yapamaz
        Assert.NotEqual(JobStates.AwaitingAnswer, w.Job(job.Id).State);
    }

    [Fact]
    public void O3_Question_Without_Job_Also_Does_Not_Force_Gate()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "isiz-soru");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", Cmd("npm test"), 120, enabled: true);
        w.SetGate(pid, true);
        Assert.Equal("", Hook(env, "stop", StopInput("s-isiz", dir, msg: "Hangi dosyadan başlayayım?")));
        Assert.Empty(w.Runs(pid, null));
    }

    // ------------------------------------------------------------------ Ö-4
    [Theory]
    [InlineData(@"C:\", false)]
    [InlineData(@"D:\", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"C:\Program Files\Uygulama", false)]
    [InlineData(@"C:\Program Files (x86)\Uygulama", false)]
    [InlineData(@"C:\ProgramData", false)]
    [InlineData(@"C:\Users\deneme", false)]            // ev klasörünün kendisi
    [InlineData(@"C:\Users", false)]                    // kullanıcılar klasörü
    [InlineData(@"C:\Users\deneme\.claude", false)]
    [InlineData(@"C:\Users\deneme\.claude\projects", false)]
    [InlineData(@"C:\Users\deneme\.codex\sessions", false)]
    [InlineData(@"\\sunucu\paylasim\proje", false)]
    [InlineData(@"//sunucu/paylasim/proje", false)]
    [InlineData(@"goreli\klasor", false)]
    [InlineData(@"..\yukari", false)]
    [InlineData(@"", false)]
    [InlineData(@"C:\Users\deneme\projelerim\benim-proje", true)] // ev klasörünün ALTINDAKİ klasör serbest
    [InlineData(@"D:\projelerim\benim-proje", true)]
    public void O4_Project_Path_Guard(string path, bool allowed)
    {
        var err = WorkService.CheckProjectPath(path, homeOverride: @"C:\Users\deneme");
        Assert.Equal(allowed, err is null);
        if (!allowed) Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void O4_AddProject_Rejects_With_400_And_Hooks_Never_Target_Users_Claude_Settings()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.AddProject(Environment.GetFolderPath(Environment.SpecialFolder.Windows))).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.AddProject("goreli")).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => w.EnsureProjectByPath(Path.GetPathRoot(env.Root)!)).Status);
        // çift emniyet: hedef kullanıcının ev klasöründeki .claude ise (ev klasörü sahte verildi)
        Assert.True(WorkService.IsUserClaudeSettings(@"C:\Users\deneme\.claude\settings.json", @"C:\Users\deneme"));
        Assert.False(WorkService.IsUserClaudeSettings(@"C:\Users\deneme\projelerim\p\.claude\settings.json", @"C:\Users\deneme"));
    }

    // ------------------------------------------------------------------ Ö-6
    [Theory]
    [InlineData("curl -u " + "admin:Gizli123 https://x.example/a", "Gizli123")]
    [InlineData("curl --user " + "admin:Gizli123 https://x.example/a", "Gizli123")]
    [InlineData("curl -u " + "'admin:Çok gizli parola' https://x.example", "Çok gizli parola")]
    [InlineData("deploy --password hunter22x --env prod", "hunter22x")]
    [InlineData("deploy --password=hunter22x --env prod", "hunter22x")]
    [InlineData("ŞİFRE: Kalem2026x", "Kalem2026x")]
    [InlineData("şifre: Kalem2026x", "Kalem2026x")]
    [InlineData("Parola: Kalem2026x", "Kalem2026x")]
    [InlineData("PAROLA: Kalem2026x", "Kalem2026x")]
    [InlineData("db:\n  password:\n    Kalem2026xyz\n  host: x", "Kalem2026xyz")]
    [InlineData("db:\n  sifre:\n    Kalem2026xyz\n", "Kalem2026xyz")]
    public void O6_Secret_Filter_V3_Catches_New_Shapes(string text, string secret)
    {
        Assert.True(SecretFilter.ContainsSecret(text), text);
        Assert.DoesNotContain(secret, SecretFilter.MaskSecrets(text));
    }

    [Theory]
    [InlineData("commit 4f9c2a1b7d3e5f60718293a4b5c6d7e8f9012345 düzeltme")]
    [InlineData("sha256 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 dosya")]
    [InlineData("git push -u origin main")]
    [InlineData("jest --passWithNoTests")]
    public void O6_Secret_Filter_V3_Does_Not_Mask_Plain_Hashes_Or_Flags(string text)
    {
        Assert.False(SecretFilter.ContainsSecret(text), text);
        Assert.Equal(text, SecretFilter.MaskSecrets(text));
    }

    [Fact]
    public void O6_Secret_Filter_Version_Is_4()
    {
        Assert.Equal(4, SecretFilter.Version);
    }

    private static string Assistant(object block, string? parent = null)
    {
        var o = new Dictionary<string, object?> { ["type"] = "assistant", ["message"] = new { role = "assistant", content = new[] { block } }, ["session_id"] = "s1" };
        if (parent is not null) o["parent_tool_use_id"] = parent;
        return JsonSerializer.Serialize(o);
    }

    private static string ToolUse(string name, object input, string id = "t1", string? parent = null) => Assistant(new { type = "tool_use", id, name, input }, parent);

    [Fact]
    public void O6_Command_Lines_Are_Plain_Raw_Command_Only_In_Masked_Detail()
    {
        var t = new RunEventTranslator();
        var lines = new List<RunLine>();
        void F(string l) => lines.AddRange(t.Feed(l));
        F(ToolUse("Bash", new { command = "curl -u " + "admin:Gizli123 https://x.example/api" }, "a1"));
        F(ToolUse("Bash", new { command = "dotnet build src/Soz.sln" }, "a2"));
        F(ToolUse("Bash", new { command = "git status --short" }, "a3"));
        F(ToolUse("Bash", new { command = "ls -la" }, "a4"));
        F(ToolUse("Bash", new { command = "cat package.json" }, "a5"));
        F(ToolUse("Bash", new { command = "npm install" }, "a6"));
        F(ToolUse("Bash", new { command = "node --test" }, "a7"));
        F(ToolUse("Bash", new { command = "kendi-betigim.sh --kimlik 123" }, "a8"));
        Assert.Equal(new[]
        {
            "bir komut çalıştırıyor",
            "bir komut çalıştırıyor: derleme",
            "bir komut çalıştırıyor: git durumu",
            "bir komut çalıştırıyor: dosya listeleme",
            "bir komut çalıştırıyor: dosya okuma",
            "bir komut çalıştırıyor: paket kurulumu",
            "bir komut çalıştırıyor: testler",
            "bir komut çalıştırıyor",
        }, lines.Select(l => l.Text).ToArray());
        Assert.All(lines, l => Assert.DoesNotContain("Gizli123", l.Text + l.Detail));
        Assert.Contains("curl -u admin:", lines[0].Detail);       // ham komut yalnız ayrıntıda
        Assert.DoesNotContain("kendi-betigim", lines[7].Text);
        Assert.Contains("kendi-betigim.sh", lines[7].Detail);
    }

    [Fact]
    public void Plain_Agent_Sentences_Show_File_Names_Not_Full_Paths_Or_Tool_Names()
    {
        var t = new RunEventTranslator();
        var l = t.Feed(Assistant(new { type = "text", text = @"Şimdi E:\Sample Work\urun\src\hesap.js dosyasını ve /home/k/proje/test/a.test.js dosyasını Edit tool ile düzenledim. Bkz https://ornek.com/a/b/c" })).Single();
        Assert.Contains("hesap.js", l.Text);
        Assert.Contains("a.test.js", l.Text);
        Assert.DoesNotContain(@"E:\Sample Work", l.Text);
        Assert.DoesNotContain("/home/k/proje", l.Text);
        Assert.DoesNotContain("Edit tool", l.Text);
        Assert.Contains("https://ornek.com/a/b/c", l.Text); // adres bozulmaz
    }

    // ------------------------------------------------------------------ K-8 (çevirici)
    [Fact]
    public void K8_Sub_Agent_Events_Do_Not_Become_Main_Agent_Words_Or_Progress_Lines()
    {
        var t = new RunEventTranslator();
        var lines = new List<RunLine>();
        void F(string l) => lines.AddRange(t.Feed(l));
        F(ToolUse("Task", new { description = "a" }, "ust1"));
        F(ToolUse("Task", new { description = "b" }, "ust2"));                                  // art arda: tek satıra toplanır
        F(Assistant(new { type = "text", text = "Alt ajan: hangisini seçeyim?" }, parent: "ust1"));
        F(ToolUse("Bash", new { command = "rm -rf x" }, "alt1", parent: "ust1"));
        F(Assistant(new { type = "text", text = "Ana ajan: tamamlandı." }));
        Assert.Equal(new[] { "bir alt yardımcı çalıştırıyor", "Ana ajan: tamamlandı." }, lines.Select(l => l.Text).ToArray());
        Assert.Equal("Ana ajan: tamamlandı.", t.LastAgentText);
        Assert.Equal(2, t.SubAgentEvents);
    }

    // ------------------------------------------------------------------ K-7
    [Fact]
    public void K7_Hook_Command_Uses_Single_Quotes_And_Escapes_Apostrophe()
    {
        var cmd = HookInstaller.BuildCommand(@"C:\a b\it's$(x)\soz.exe", "stop", @"D:\ver`i", @"D:\dot net");
        Assert.Equal("DOTNET_ROOT='D:/dot net' 'C:/a b/it'\\''s$(x)/soz.exe' hook stop --data 'D:/ver`i' 2>/dev/null || true", cmd);
        Assert.Equal("DOTNET_ROOT='D:/dot net' 'C:/a b/it'\\''s$(x)/soz.exe' hook stop --data 'D:/ver`i'", HookInstaller.BuildCommand(@"C:\a b\it's$(x)\soz.exe", "stop", @"D:\ver`i", @"D:\dot net", quiet: false));
        var (exe, data) = HookHealth.ParseCommand(cmd);
        Assert.Equal("C:/a b/it's$(x)/soz.exe", exe);
        Assert.Equal("D:/ver`i", data);
    }

    [Fact]
    public void K7_Shell_Does_Not_Expand_Anything_In_Quoted_Path()
    {
        // Gerçek bash: yolda $(...) ve ters tırnak YÜRÜTÜLMEZ; olmayan dosyayı çalıştırmaya çalışır ve adı tam olarak hata metninde görünür.
        var cmd = HookInstaller.BuildCommand("/yok/x$(echo PWNED)`echo PWNED2`/soz", "stop", null, null, quiet: false); // sessiz sonek hata metnini gizler; burada kabuk hatası okunur
        var psi = new ProcessStartInfo("bash") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(cmd);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        Assert.Contains("x$(echo PWNED)`echo PWNED2`/soz", err);
        Assert.DoesNotContain("xPWNED", err);
    }

    // ------------------------------------------------------------------ K-4, K-5
    [Fact]
    public void K4_Interrupted_Proof_Rows_Become_Error_Interrupted_On_Stop_And_Startup()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "yarim");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var job = w.CreateJob(pid, "Yarım kalan kanıt.");
        long a, b;
        using (var c = env.Svc.Db.Open())
        {
            a = ProofStore.Begin(c, new ProofStore.RunRequest(pid, job.Id, null, "run_end", 1));
            b = ProofStore.Begin(c, new ProofStore.RunRequest(pid, null, null, "manual", 1));
            Assert.Equal(1, ProofStore.InterruptRunning(c, job.Id)); // yalnız bu işin satırı
        }
        Assert.Equal(Verdicts.Error, w.Run(a).Verdict);
        Assert.Equal("interrupted", w.Run(a).ReasonCode);
        Assert.Contains("yarıda kesildi", w.Run(a).Summary);
        Assert.Null(w.Run(b).Verdict);
        using (var c = env.Svc.Db.Open()) Assert.Equal(1, ProofStore.InterruptRunning(c)); // açılışta kalan da
        Assert.Equal("interrupted", w.Run(b).ReasonCode);
    }

    [Theory]
    [InlineData(null, "Kontrol yok")]
    [InlineData("no_checks", "Kontrol yok")]
    [InlineData("no_tests", "hiç test koşmadı")]
    [InlineData("definition_changed", "sınama komutu ya da ayarı değişti")]
    [InlineData("tests_changed", "test dosyası değişti")]
    [InlineData("count_dropped", "test sayısı düştü")]
    public void K5_Unchecked_Word_Depends_On_Reason(string? reason, string expected)
    {
        Assert.Equal(expected, ProofStore.VerdictWord(Verdicts.Unchecked, reason));
    }

    [Fact]
    public void K5_Interrupted_Word()
    {
        Assert.Equal("yarıda kesildi", ProofStore.VerdictWord(Verdicts.Error, "interrupted"));
    }

    // ------------------------------------------------------------------ zaman çizgisi sırası
    [Fact]
    public void Timeline_Orders_Proof_Card_After_The_Lines_Before_It_By_Time_Stably()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "siralama");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var job = w.CreateJob(pid, "Sıra.");
        using var c = env.Svc.Db.Open();
        var runId = RunStore.Insert(c, job.Id, pid, "start");
        void Line(string at, string text) => c.Exec("INSERT INTO run_lines(run_id, job_id, at, kind, text) VALUES(@r,@j,@a,'progress',@t)", null, ("@r", runId), ("@j", job.Id), ("@a", at), ("@t", text));
        Line("2026-11-10T11:00:01.000Z", "ilk satır");
        Line("2026-11-10T11:00:03.000Z", "üçüncü satır (kanıttan sonra)");
        var pr = ProofStore.Begin(c, new ProofStore.RunRequest(pid, job.Id, null, "stop_hook", 1));
        c.Exec("UPDATE proof_runs SET started_at='2026-11-10T11:00:02.000Z', finished_at='2026-11-10T11:00:02.500Z', verdict='failed', summary='kaldı' WHERE id=@i", null, ("@i", pr));
        Line("2026-11-10T11:00:00.500Z", "sıfırıncı satır (sonradan yazılmış ama daha eski)");
        var order = RunStore.Timeline(c, job.Id, includeDetail: false).Where(i => i.Text != "Sıra.").Select(i => i.Kind == "proof" ? "KANIT" : i.Text).ToList();
        Assert.Equal(new[] { "sıfırıncı satır (sonradan yazılmış ama daha eski)", "ilk satır", "KANIT", "üçüncü satır (kanıttan sonra)" }, order);
        // ikinci çağrı aynı sırayı verir (kararlı)
        Assert.Equal(order, RunStore.Timeline(c, job.Id, includeDetail: false).Where(i => i.Text != "Sıra.").Select(i => i.Kind == "proof" ? "KANIT" : i.Text).ToList());
    }

    // ------------------------------------------------------------------ v7 göçü
    [Fact]
    public void Migration_V6_To_V7_Keeps_Data_And_Adds_Denetim2_Columns()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "gocv7");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var job = w.CreateJob(pid, "v6 verisi: göçten sonra durmalı.");
        w.AddCheck(pid, job.Id, "command", "Ölçüt", Cmd("exit 0"), 60, enabled: true);
        var run = w.RunProof(pid, job.Id);
        using (var c = env.Svc.Db.Open())
        {
            c.Exec("DROP INDEX IF EXISTS ix_results_cmdkey");
            c.Exec("DROP TABLE job_base");
            foreach (var col in new[] { "accepted_by_user", "start_error", "question", "base_note", "queued_json", "queued_at", "seen_at" }) c.Exec($"ALTER TABLE jobs DROP COLUMN {col}");
            foreach (var col in new[] { "defs_json", "defs_at" }) c.Exec($"ALTER TABLE projects DROP COLUMN {col}");
            foreach (var col in new[] { "reason_code", "reason_text", "changed_files", "count_unknown", "added_tests", "accepted" }) c.Exec($"ALTER TABLE proof_runs DROP COLUMN {col}");
            foreach (var col in new[] { "tests_total", "tests_passed", "tests_skipped", "cmd_key" }) c.Exec($"ALTER TABLE proof_results DROP COLUMN {col}");
            c.Exec("ALTER TABLE run_approvals DROP COLUMN detail");
            c.Exec("PRAGMA user_version=6");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var db = SozDb.OpenAndMigrate(env.DataDir);
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion());
        using var c2 = db.Open();
        Assert.Contains("accepted_by_user", c2.Query("PRAGMA table_info(jobs)", r => r.GetString(1)));
        Assert.Contains("base_note", c2.Query("PRAGMA table_info(jobs)", r => r.GetString(1)));
        Assert.Contains("defs_json", c2.Query("PRAGMA table_info(projects)", r => r.GetString(1)));
        Assert.Contains("reason_code", c2.Query("PRAGMA table_info(proof_runs)", r => r.GetString(1)));
        Assert.Contains("cmd_key", c2.Query("PRAGMA table_info(proof_results)", r => r.GetString(1)));
        Assert.Contains("detail", c2.Query("PRAGMA table_info(run_approvals)", r => r.GetString(1)));
        Assert.Contains("job_base", c2.Query("SELECT name FROM sqlite_master WHERE type='table'", r => r.GetString(0)));
        Assert.Equal(1L, c2.ScalarLong("SELECT COUNT(*) FROM jobs WHERE id=@j AND raw_text='v6 verisi: göçten sonra durmalı.'", null, ("@j", job.Id)));
        Assert.Equal(1L, c2.ScalarLong("SELECT COUNT(*) FROM proof_runs WHERE id=@r", null, ("@r", run.Id)));
        Assert.Equal(1L, c2.ScalarLong("SELECT COUNT(*) FROM proof_results WHERE run_id=@r", null, ("@r", run.Id)));
        Assert.Equal(SozDb.CurrentVersion, SozDb.OpenAndMigrate(env.DataDir).GetVersion()); // ikinci açılış değişiklik yapmaz
    }

    // ------------------------------------------------------------------ Ö-5 durum sayıları
    private static long Session(TestEnv env, string name, string dir, string? notification = null)
    {
        Hook(env, "user-prompt-submit", J(new { session_id = name, cwd = dir, prompt = "merhaba " + name }));
        if (notification is not null) Hook(env, "notification", J(new { session_id = name, cwd = dir, notification_type = notification }));
        using var c = env.Svc.Db.Open();
        return c.ScalarLong("SELECT id FROM sessions WHERE external_id=@e", null, ("@e", name));
    }

    [Fact]
    public void O5_Running_Counts_Only_Live_Runs_And_Recently_Active_Terminal_Jobs()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "durum");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        using var c = env.Svc.Db.Open();

        var given = w.CreateJob(pid, "Yalnız verildi."); w.PatchJob(given.Id, JobStates.Given);                    // çalışıyor sayılmaz
        var live = w.CreateJob(pid, "Canlı süreç."); JobStore.SetState(c, live.Id, JobStates.Running, pid);
        RunStore.Insert(c, live.Id, pid, "start");                                                                  // runs.status=running
        var staleRun = w.CreateJob(pid, "Süreci ölmüş."); JobStore.SetState(c, staleRun.Id, JobStates.Running, pid);   // calisiyor ama canlı süreç yok, oturum da yok
        var fresh = w.CreateJob(pid, "Terminalde etkin."); JobStore.SetState(c, fresh.Id, JobStates.Running, pid);
        var old = w.CreateJob(pid, "Terminalde eski."); JobStore.SetState(c, old.Id, JobStates.Running, pid);
        var sFresh = Session(env, "s-taze", dir); var sOld = Session(env, "s-eski", dir);
        c.Exec("UPDATE jobs SET session_id=@s WHERE id=@j", null, ("@s", sFresh), ("@j", fresh.Id));
        c.Exec("UPDATE jobs SET session_id=@s WHERE id=@j", null, ("@s", sOld), ("@j", old.Id));
        c.Exec("UPDATE sessions SET last_event_ts='2026-11-10T11:30:00.000Z' WHERE id=@s", null, ("@s", sOld)); // 30 dk önce

        var now = w.Now();
        Assert.Equal(2, now.Counts.JobsRunning);
        Assert.Equal(new[] { live.Id, fresh.Id }.OrderBy(x => x), now.RunningJobs.Select(j => j.Id).OrderBy(x => x));
    }

    [Fact]
    public void O5_Waiting_Counts_Jobs_Waiting_For_You_And_Real_Notifications_Not_Finished_Or_Run_Sessions()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "bekleyen");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        using var c = env.Svc.Db.Open();
        JobDto Make(string text, string state) { var j = w.CreateJob(pid, text); JobStore.SetState(c, j.Id, state, pid); return j; }

        Make("cevap", JobStates.AwaitingAnswer);
        Make("karar", JobStates.AwaitingDecision);
        Make("onay", JobStates.AwaitingApproval);
        var unseen = Make("görülmemiş kaldı", JobStates.Failed);
        var seen = Make("görülmüş kaldı", JobStates.Failed); JobStore.MarkSeen(c, seen.Id);
        Make("geçti", JobStates.Passed);
        Make("sırada", JobStates.Queued);
        Make("durduruldu", JobStates.Stopped);

        // oturumlar: gerçekten bekleyen terminal (sayılır), işi geçmiş terminal (sayılmaz), Söz'ün yürüttüğü bitmiş oturum (sayılmaz)
        var sReal = Session(env, "s-gercek", dir, "idle_prompt");
        var doneJob = Make("oturumlu geçti", JobStates.Passed);
        var sDone = Session(env, "s-bitmis", dir, "idle_prompt");
        c.Exec("UPDATE jobs SET session_id=@s WHERE id=@j", null, ("@s", sDone), ("@j", doneJob.Id));
        var runJob = Make("Söz koşulu", JobStates.Running);
        var sRun = Session(env, "s-soz", dir, "idle_prompt");
        c.Exec("UPDATE jobs SET session_id=@s WHERE id=@j", null, ("@s", sRun), ("@j", runJob.Id));
        var rid = RunStore.Insert(c, runJob.Id, pid, "start");
        RunStore.SetStatus(c, rid, RunStatus.Done, ended: true);

        var now = w.Now();
        Assert.Equal(3, now.WaitingJobs!.Count(j => JobStates.IsWaitingUser(j.State)));
        Assert.Contains(now.WaitingJobs!, j => j.Id == unseen.Id);
        Assert.DoesNotContain(now.WaitingJobs!, j => j.Id == seen.Id);
        Assert.Equal(1, now.NeedsYou.Count);
        Assert.Equal(sReal, now.NeedsYou[0].SessionId);
        Assert.Equal(5, now.Counts.Waiting); // 3 bekleyen iş + 1 görülmemiş kaldı + 1 gerçek bildirim
        Assert.Equal(1, now.Counts.Queued);
        Assert.Contains("seni bekliyor", now.Sentence);
        Assert.Contains("1 iş sırada", now.Sentence);
    }
}
