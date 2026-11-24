using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Agents;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Registry;
using Soz.Core.Restore;
using Soz.Core.Run;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Hermes ve OpenCode iş yürütücüleri (12 Kas 2026 kararı): sahte hermes ve sahte opencode ile (test\sahte-hermes, test\sahte-opencode).
/// GERÇEK hermes ve opencode çalıştırılmaz (kota harcar; Hermes ortak arka plan hizmetine iş bırakır).
/// Bağlayıcılar kapalı (state suggested) gelir; testler kişinin Tanışma'daki onayını RegistryHost.Approve ile taklit eder.
/// Sayılara bağlı değildir: yeni kayıt eklemek bu testleri kırmaz.
/// </summary>
public class HermesOpenCodeTests
{
    private sealed record Tool(string Id, string Script, string PathKey, string LogEnv, string Display);

    private static Tool T(string id) => id switch
    {
        "hermes" => new Tool("hermes", Path.Combine(TestEnv.RepoRoot, "test", "sahte-hermes", "sahte-hermes.mjs"), "hermesPath", "SAHTE_HERMES_LOG", "Hermes Agent"),
        "opencode" => new Tool("opencode", Path.Combine(TestEnv.RepoRoot, "test", "sahte-opencode", "sahte-opencode.mjs"), "opencodePath", "SAHTE_OPENCODE_LOG", "OpenCode"),
        _ => throw new ArgumentException(id),
    };

    public static IEnumerable<object[]> Tools => new[] { new object[] { "hermes" }, new object[] { "opencode" } };

    private sealed class Rig : IDisposable
    {
        public TestEnv Env = new(copySamples: false);
        public WorkService W;
        public RunManager Rm;
        public string ProjectDir;
        public long ProjectId;
        public Tool Tool;
        public string LogFile, TorunFile;

        public Rig(string id, bool node = true, bool approve = true, string? scriptPath = null)
        {
            Tool = T(id);
            ProjectDir = node ? NodeSample.Create(Env, "dugum proje") : Path.Combine(Env.Root, "proje klasoru");
            Directory.CreateDirectory(ProjectDir);
            File.WriteAllText(Path.Combine(ProjectDir, "notlar.txt"), "önemli notlar\n");
            W = new WorkService(Env.Svc.Db);
            ProjectId = W.EnsureProjectByPath(ProjectDir);
            Env.Svc.PutSettings(new JsonObject { [Tool.PathKey] = scriptPath ?? Tool.Script, ["maxParallelRuns"] = 3, ["gateMaxAttempts"] = 3 });
            if (approve) RegistryHost.Approve(Env.Svc.Db, "connector:" + Tool.Id);
            Rm = new RunManager(Env.Svc.Db);
            LogFile = Path.Combine(Env.Root, "sahte-log.jsonl");
            TorunFile = Path.Combine(Env.Root, "torun.pid");
            Environment.SetEnvironmentVariable(Tool.LogEnv, LogFile);
            Environment.SetEnvironmentVariable("SAHTE_STATE_DIR", Env.Root);
            Environment.SetEnvironmentVariable("SAHTE_TORUN", TorunFile);
            OpenCodeConnector.ClearVersionCache();
        }

        public JobDto Job(string text) => W.CreateJob(ProjectId, text, agent: Tool.Id);
        public async Task Finish(RunDto run) => await Rm.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(90));
        public void AddNpmTest() => W.AddCheck(ProjectId, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, true);
        public List<JsonNode> Log() => File.Exists(LogFile) ? File.ReadAllLines(LogFile).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).ToList() : new();
        public static List<string> Argv(JsonNode entry) => entry["argv"]!.AsArray().Select(x => (string)x!).ToList();

        public List<(string trigger, int attempt, string? verdict, string? reason)> Proofs(long jobId)
        {
            using var c = Env.Svc.Db.Open();
            return c.Query("SELECT trigger, attempt, verdict, reason_code FROM proof_runs WHERE job_id=@j ORDER BY id", r => (r.GetString(0), (int)r.GetInt64(1), r.Str(2), r.Str(3)), null, ("@j", jobId));
        }

        public List<RunDto> Runs(long jobId) { using var c = Env.Svc.Db.Open(); return RunStore.ListForJob(c, jobId).OrderBy(r => r.Id).ToList(); }

        public List<string> Lines(long jobId, params string[] kinds)
        {
            using var c = Env.Svc.Db.Open();
            return RunStore.Timeline(c, jobId, includeDetail: false).Where(i => kinds.Contains(i.Kind)).Select(i => i.Text).ToList();
        }

        public void Dispose()
        {
            try { Rm.Dispose(); } catch (Exception) { }
            foreach (var k in new[] { "SAHTE_HERMES_LOG", "SAHTE_OPENCODE_LOG", "SAHTE_STATE_DIR", "SAHTE_TORUN", "SAHTE_OPENCODE_SURUM" })
                Environment.SetEnvironmentVariable(k, null);
            OpenCodeConnector.ClearVersionCache();
            Env.Dispose();
        }
    }

    // ------------------------------------------------------------------ açık gelir, kapatılınca durur, onaylanınca yeniden açılır
    [Theory, MemberData(nameof(Tools))]
    public void Baglayici_Kapali_Gelir_Onaylaninca_Acilir(string id)
    {
        using var rig = new Rig(id, node: false, approve: false);
        var initial = RegistryHost.Load(rig.Env.Svc.Db);
        Assert.Equal(RegistryStates.Enabled, initial.Get("connector:" + id)!.State);
        Assert.NotNull(AgentRegistry.Find(id));
        Assert.Contains(id, AgentRegistry.Ids);

        // Ayarlar'dan kapatılınca çalışmaz
        RegistryHost.Revoke(rig.Env.Svc.Db, "connector:" + id);
        var load = RegistryHost.Load(rig.Env.Svc.Db);
        Assert.Equal(RegistryStates.Disabled, load.Get("connector:" + id)!.State);
        Assert.DoesNotContain(AgentRegistry.OpenFrom(load), c => c.Id == id);
        Assert.Equal(id, AgentRegistry.Get(id).Id);                            // iş yazılırken ad çözülür ("bilinmeyen yardımcı" denmez)
        var st = SozSettings.Load(rig.Env.Svc.Db);
        Assert.DoesNotContain(AgentProbe.All(st, false, load), a => a.Id == id);

        var job = rig.Job("iş [[sahte:oku]]");
        Assert.Equal(id, job.Agent);
        var ex = Assert.Throws<SozException>(() => rig.Rm.Start(job.Id));
        Assert.Equal(409, ex.Status);
        Assert.Contains("henüz açık değil", ex.Message);
        Assert.Empty(rig.Log());                                               // hiçbir süreç başlamadı

        // kişi yeniden açınca etkinleşir
        RegistryHost.Approve(rig.Env.Svc.Db, "connector:" + id);
        var open = RegistryHost.Load(rig.Env.Svc.Db);
        Assert.Contains(AgentRegistry.OpenFrom(open), c => c.Id == id);
        var info = AgentProbe.All(st, false, open).Single(a => a.Id == id);
        Assert.True(info.Capabilities.Headless);
    }

    // ------------------------------------------------------------------ dürüst yetenekler
    [Fact]
    public void Yetenekler_Durust_Bekci_Yok_Hermes_Durdurmayi_Garanti_Etmez()
    {
        var h = AgentRegistry.Known.Single(c => c.Id == "hermes");
        var o = AgentRegistry.Known.Single(c => c.Id == "opencode");
        foreach (var c in new[] { h, o })
        {
            Assert.Equal(GuardTier.None, c.Capabilities.BeforeTool);           // araç başına araya girilemiyor: "sınırlı" ya da "kanca" denmez
            Assert.Equal("yok", c.GuardLabel);
            Assert.Equal("none", AgentProbe.Describe(c.Capabilities).GuardTier);
            Assert.Equal(TurnEndMode.OuterLoop, c.Capabilities.TurnEnd);       // kanıt kapısı Söz'ün dış döngüsünde
            Assert.True(c.Capabilities.Headless && c.Capabilities.EventStream && c.Capabilities.Resume);
        }
        Assert.Equal(CostMode.Tokens, h.Capabilities.Cost);                    // Hermes dolar vermez
        Assert.Equal(CostMode.Dollars, o.Capabilities.Cost);
        Assert.False(h.Capabilities.Stop);
        Assert.NotNull(h.StopNote);                                            // kişiye söylenir
        Assert.Contains("garanti edemez", h.StopNote);
        Assert.True(o.Capabilities.Stop);
        Assert.Null(o.StopNote);
        Assert.Null(AgentRegistry.Find("claude")!.StopNote);                   // eski yardımcıların davranışı değişmedi
        Assert.Null(AgentRegistry.Find("codex")!.StopNote);
    }

    // ------------------------------------------------------------------ komut satırı
    [Fact]
    public void Hermes_Komut_Satiri_Baslatma_Ve_Surdurme()
    {
        using var env = new TestEnv(copySamples: false);
        var st = SozSettings.Load(env.Svc.Db);
        var cmd = new LaunchCommand("C:\\x\\node.exe", new[] { "C:\\x\\hermes.js" }, "node hermes.js");
        var ctx = new AgentRunContext("E:\\Sample Work\\p", "iş: -tehlikeli \"tırnaklı\" metin", "", AgentPermissionMode.Careful, 5, 7, st, null);
        var h = new HermesConnector();
        var start = h.BuildStart(cmd, ctx);
        Assert.Equal(new[] { "C:\\x\\hermes.js", "chat", "--format", "stream-json", "--query-file", "-", "--in", "E:\\Sample Work\\p" }, start.Args.ToArray());
        Assert.Equal("iş: -tehlikeli \"tırnaklı\" metin", start.Stdin);       // istem komut satırında değil, standart girdide
        var withModel = h.BuildStart(cmd, ctx with { Model = "gpt-test" });
        Assert.Equal(new[] { "-m", "gpt-test" }, withModel.Args.TakeLast(2).ToArray());
        var resume = h.BuildResume(cmd, ctx with { Model = "gpt-test" }, "S-1");
        Assert.Equal(start.Args.Concat(new[] { "--resume", "S-1" }).ToArray(), resume.Args.ToArray()); // devam turunda model bayrağı yok
        Assert.DoesNotContain(start.Args.Concat(resume.Args), a => a.Contains("yolo") || a.Contains("accept-hooks") || a.Contains("--source"));
        Assert.Equal("1", start.AddEnv["PYTHONUTF8"]);
        Assert.False(h.ShouldStripEnv("OPENAI_API_KEY"));                      // Hermes kendi anahtarlarını ortamdan okur: silinmez
        var ex = Assert.Throws<AgentLaunchException>(() => h.BuildStart(cmd, ctx with { Permission = AgentPermissionMode.ReadOnly }));
        Assert.Equal(HermesConnector.ReadOnlyText, ex.Message);                // salt okunur sessizce serbest bırakılmaz
        Assert.Equal("", h.DefaultModel(st));
    }

    [Fact]
    public void OpenCode_Komut_Satiri_Surume_Gore_Standalone()
    {
        using var rig = new Rig("opencode", node: false);
        var st = SozSettings.Load(rig.Env.Svc.Db);
        var node = AgentLocator.Resolve(rig.Tool.Script, "opencode", "yok", "kısayol").Cmd!;
        var ctx = new AgentRunContext("E:\\p", "istem", "", AgentPermissionMode.Careful, 5, 7, st, null);
        var o = new OpenCodeConnector();

        Environment.SetEnvironmentVariable("SAHTE_OPENCODE_SURUM", "2.0.3");
        OpenCodeConnector.ClearVersionCache();
        var v2 = o.BuildStart(node, ctx);
        Assert.Equal(new[] { node.PrefixArgs[0], "run", "--standalone", "--format", "json" }, v2.Args.ToArray());
        Assert.Equal("istem", v2.Stdin);
        var resume = o.BuildResume(node, ctx with { Model = "x/y" }, "ses_1");
        Assert.Equal(new[] { node.PrefixArgs[0], "run", "--standalone", "--format", "json", "--session", "ses_1", "--model", "x/y" }, resume.Args.ToArray());

        Environment.SetEnvironmentVariable("SAHTE_OPENCODE_SURUM", "1.14.2");
        OpenCodeConnector.ClearVersionCache();
        Assert.DoesNotContain("--standalone", o.BuildStart(node, ctx).Args); // 1.x'te bayrak yok: verilirse araç reddeder

        var ro = o.BuildStart(node, ctx with { Permission = AgentPermissionMode.ReadOnly });
        Assert.Contains("plan", ro.Args);                                      // salt okunur: düzenlemeyi reddeden plan kipi
        Assert.DoesNotContain(v2.Args.Concat(resume.Args), a => a.Contains("dangerously") || a.Contains("--auto"));
        Assert.False(o.ShouldStripEnv("ANTHROPIC_API_KEY"));
        Assert.Equal(2, OpenCodeConnector.ParseMajor("opencode 2.0.3"));
        Assert.Equal(1, OpenCodeConnector.ParseMajor("1.14.2"));
        Assert.Null(OpenCodeConnector.ParseMajor("sürüm yok"));
    }

    // ------------------------------------------------------------------ uçtan uca: başlat, satırlar, bitti, kanıt kapısı
    [Theory, MemberData(nameof(Tools))]
    public async Task Is_Baslar_Satirlar_Akar_Kanit_Kapisi_Gecer(string id)
    {
        using var rig = new Rig(id);
        rig.AddNpmTest();
        var job = rig.Job("Düzelt [[sahte:gecer]]");
        var run = rig.Rm.Start(job.Id)!;
        Assert.Equal(id, run.Agent);
        await rig.Finish(run);

        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.Passed, j.State);
        Assert.Equal(Verdicts.Passed, j.LastVerdict);
        Assert.Contains(rig.Proofs(job.Id), p => p.trigger == "outer_gate" && p.attempt == 1 && p.verdict == Verdicts.Passed);
        using (var c = rig.Env.Svc.Db.Open())
        {
            Assert.NotNull(c.Scalar("SELECT baseline_json FROM jobs WHERE id=@j", null, ("@j", job.Id)));  // başlangıç ölçümü Söz tarafında, her yardımcıda
            Assert.Equal(id, c.Scalar("SELECT tool FROM sessions WHERE id=(SELECT session_id FROM jobs WHERE id=@j)", null, ("@j", job.Id)));
        }

        var log = rig.Log().Single();
        var argv = Rig.Argv(log);
        Assert.Contains("--format", argv);
        Assert.DoesNotContain(argv, a => a.Contains("yolo") || a.Contains("dangerously") || a.Contains("accept-hooks"));
        Assert.Contains("Düzelt", (string)log["prompt"]!);                     // istem standart girdiden geldi
        Assert.Equal(rig.ProjectDir, (string)log["cwd"]!);                     // süreç proje klasöründe başladı
        if (id == "hermes")
        {
            Assert.Equal(rig.ProjectDir, argv[argv.IndexOf("--in") + 1]);
            Assert.Equal("1", (string)log["env"]!["PYTHONUTF8"]!);
        }
        else Assert.Equal("--standalone", argv[argv.IndexOf("run") + 1]);     // sahte opencode varsayılan 2.x

        var lines = rig.Lines(job.Id, "progress", "agent");
        Assert.Contains("bir komut çalıştırıyor: testler", lines);
        Assert.Contains("bir komut hata verdi", lines);                        // ilk npm test kaldı
        Assert.Contains(lines, l => l.EndsWith("dosyasını değiştirdi"));
        Assert.Contains(lines, l => l.StartsWith("Düzelttim. Testler geçti"));
        Assert.DoesNotContain(lines, l => l.Contains("tool_use") || l.Contains("terminal") || l.Contains("\"type\""));  // araç adı ve ham olay satıra çıkmaz

        var r = rig.Runs(job.Id).Single();
        Assert.Equal(id, r.Agent);
        Assert.False(string.IsNullOrEmpty(r.SessionId));
        Assert.Null(r.CostUsd);                                                // ikisi de bu senaryoda dolar bildirmiyor (OpenCode: fiyat 0 = bilinmiyor)
        if (id == "hermes") { Assert.Equal(1000, r.InputTokens); Assert.Equal(500, r.CachedTokens); Assert.Equal(100, r.OutputTokens); }
        else { Assert.Equal(2000, r.InputTokens); Assert.Equal(1000, r.CachedTokens); Assert.Equal(200, r.OutputTokens); Assert.Equal(40, r.ReasoningTokens); }
        Assert.Equal(r.InputTokens, j.Spend!.InputTokens);
    }

    [Fact]
    public async Task OpenCode_Fiyat_Bildirirse_Dolar_Yazilir()
    {
        using var rig = new Rig("opencode", node: false);
        var job = rig.Job("iş [[sahte:ucret]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        Assert.Equal(0.0123, rig.Runs(job.Id).Single().CostUsd!.Value, 6);
    }

    [Theory]
    [InlineData("1.14.2", false)]
    [InlineData("2.0.3", true)]
    public async Task OpenCode_Eski_Surumde_Standalone_Verilmez_Is_Yine_Calisir(string surum, bool beklenenStandalone)
    {
        using var rig = new Rig("opencode", node: false);
        Environment.SetEnvironmentVariable("SAHTE_OPENCODE_SURUM", surum);
        OpenCodeConnector.ClearVersionCache();
        var job = rig.Job("iş [[sahte:oku]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        Assert.Equal(beklenenStandalone, Rig.Argv(rig.Log().Single()).Contains("--standalone"));
        Assert.NotEqual(JobStates.StartFailed, rig.W.Job(job.Id).State); // 1.x'te bayrak verilseydi sahte araç "Unknown argument" ile düşerdi
    }

    [Theory, MemberData(nameof(Tools))]
    public async Task Ajan_Isi_Bozarsa_Kaldi_Sonra_Geri_Alinir(string id)
    {
        using var rig = new Rig(id);
        rig.AddNpmTest();
        var onceki = GeriAlmaMotorTests.GeriRig.Ozetle(rig.ProjectDir);
        var job = rig.Job("Düzelt [[sahte:boz]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);

        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.Failed, j.State);                               // "kaldı": yardımcı "Bitti" dedi ama kontrol geçmedi
        Assert.All(rig.Proofs(job.Id).Where(p => p.trigger == "outer_gate"), p => Assert.Equal(Verdicts.Failed, p.verdict));
        Assert.Contains("AJAN BOZDU", File.ReadAllText(Path.Combine(rig.ProjectDir, "hesap.js")));
        Assert.False(File.Exists(Path.Combine(rig.ProjectDir, "notlar.txt")));
        Assert.NotEqual(onceki, GeriAlmaMotorTests.GeriRig.Ozetle(rig.ProjectDir));

        // geri alma noktası Söz tarafında, hangi ajanla çalışılırsa çalışılsın
        var prot = rig.Rm.Restore.ProtectionOf(job.Id);
        Assert.True(prot.CanUndo);
        var on = rig.Rm.Restore.Preview(job.Id, new UndoRequest());
        Assert.Contains(on.Items, i => i.Path == "notlar.txt" && i.Action == "geri-yukle");
        Assert.Contains(on.Items, i => i.Path == "yeni.txt" && i.Action == "sil");
        var ap = rig.Rm.Restore.Apply(job.Id, new UndoRequest(PreviewUid: on.UndoUid));
        Assert.Equal("applied", ap.Status);
        Assert.Equal(onceki, GeriAlmaMotorTests.GeriRig.Ozetle(rig.ProjectDir));  // klasör birebir eski hâlinde
    }

    [Theory, MemberData(nameof(Tools))]
    public async Task Kapi_Geri_Yollar_Ayni_Oturumda_Surdurur_Ve_Gecer(string id)
    {
        using var rig = new Rig(id);
        rig.AddNpmTest();
        var job = rig.Job("Düzelt [[sahte:duzelt]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);

        Assert.Equal(JobStates.Passed, rig.W.Job(job.Id).State);
        Assert.Equal(new (int, string?)[] { (1, Verdicts.Failed), (2, Verdicts.Passed) }, rig.Proofs(job.Id).Where(p => p.trigger == "outer_gate").Select(p => (p.attempt, p.verdict)).ToArray());
        var log = rig.Log();
        Assert.Equal(2, log.Count);
        var sid = rig.Runs(job.Id)[0].SessionId;
        Assert.False(string.IsNullOrEmpty(sid));
        var flag = id == "hermes" ? "--resume" : "--session";
        var a2 = Rig.Argv(log[1]);
        Assert.Equal(sid, a2[a2.IndexOf(flag) + 1]);                           // aynı oturum
        Assert.DoesNotContain(flag, Rig.Argv(log[0]));
        Assert.Contains("Söz kanıt kapısı", (string)log[1]["prompt"]!);
        Assert.Contains("Testler", (string)log[1]["prompt"]!);
        Assert.Single(rig.Runs(job.Id).Select(r => r.SessionId).Distinct());
        Assert.Equal(new[] { "start", "gate" }, rig.Runs(job.Id).Select(r => r.Kind).ToArray());
        if (id == "hermes") Assert.DoesNotContain("-m", a2);                   // devam turunda model bayrağı yok
        Assert.Contains(rig.Lines(job.Id, "state"), n => n.Contains("yardımcıya geri yollandı") && n.Contains("2/3"));
    }

    [Theory, MemberData(nameof(Tools))]
    public async Task Kapi_Uc_Turda_Kalir_Sonsuz_Dongu_Yok(string id)
    {
        using var rig = new Rig(id);
        rig.AddNpmTest();
        var job = rig.Job("Düzelt [[sahte:bozuk]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        Assert.Equal(JobStates.Failed, rig.W.Job(job.Id).State);
        Assert.Equal(3, rig.Log().Count);
    }

    [Theory, MemberData(nameof(Tools))]
    public async Task Test_Dosyasini_Bozan_Ajan_Yesil_Alamaz(string id)
    {
        using var rig = new Rig(id);
        rig.AddNpmTest();
        File.WriteAllText(Path.Combine(rig.ProjectDir, "hesap.js"), NodeSample.FixedLib.Replace("return a * b;", "return a + b;")); // e2: çarpma bozuk: ilk test (çarpma) atlanınca ajanın hâli yeşil görünür, onaylı test kalır
        var job = rig.Job("Düzelt [[sahte:hile]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.NotEqual(JobStates.Passed, j.State);
        Assert.Equal(JobStates.AwaitingDecision, j.State);
        Assert.All(rig.Proofs(job.Id).Where(p => p.trigger == "outer_gate"), p => Assert.True(ProofStore.Reasons.IsTampering(p.reason), p.reason));
    }

    [Theory, MemberData(nameof(Tools))]
    public async Task Soru_Cevap_Bekler_Cevap_Ayni_Oturumu_Surdurur(string id)
    {
        using var rig = new Rig(id, node: false);
        rig.W.AddCheck(rig.ProjectId, null, "command", "Hep kalır", new JsonObject { ["cmd"] = "exit 1", ["expectExit"] = 0 }, 60, true);
        var job = rig.Job("iş [[sahte:soru]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        Assert.Equal(JobStates.AwaitingAnswer, rig.W.Job(job.Id).State);
        Assert.Empty(rig.Proofs(job.Id));                                      // soruya kapı uygulanmaz
        var sid = rig.Runs(job.Id).Single().SessionId;
        Assert.False(string.IsNullOrEmpty(sid));

        await rig.Finish(rig.Rm.Reply(job.Id, "Birinciyi seç.")!);
        var log = rig.Log();
        Assert.True(log.Count >= 2);
        var a2 = Rig.Argv(log[1]);
        Assert.Equal(sid, a2[a2.IndexOf(id == "hermes" ? "--resume" : "--session") + 1]);
        Assert.Equal("Birinciyi seç.", ((string)log[1]["prompt"]!).Trim());
        Assert.Equal(id, rig.Runs(job.Id).Last().Agent);
    }

    [Theory, MemberData(nameof(Tools))]
    public async Task Durdur_Sureci_Ve_Torunlari_Kapatir(string id)
    {
        using var rig = new Rig(id, node: false);
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
        Assert.False(ProcessTree.IsAlive(torun, null));
        var notes = rig.Lines(job.Id, "state");
        Assert.Contains("İş durduruldu.", notes);
        // Hermes işi kendi hizmetinde sürdürüyor olabilir: kişiye açıkça söylenir. OpenCode (özel sunucu) gerçekten durur: ek uyarı yok.
        if (id == "hermes") Assert.Contains(notes, n => n.Contains("garanti edemez"));
        else Assert.DoesNotContain(notes, n => n.Contains("garanti edemez"));
    }

    // ------------------------------------------------------------------ hatalar sade cümle
    [Theory]
    [InlineData("hermes", "hata", "Yardımcı işi yarıda bırakıp durdu")]
    [InlineData("hermes", "giris", "hermes setup")]
    [InlineData("hermes", "limit", "kullanım sınırına")]
    [InlineData("hermes", "girdi", "onay ya da cevap")]
    [InlineData("opencode", "hata", "Yardımcı işi yarıda bırakıp durdu")]
    [InlineData("opencode", "giris", "giriş yap")]
    [InlineData("opencode", "limit", "kullanım sınırına")]
    public async Task Hatalarin_Sade_Nedeni_Var(string id, string senaryo, string beklenen)
    {
        using var rig = new Rig(id, node: false);
        var job = rig.Job($"iş [[sahte:{senaryo}]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.StartFailed, j.State);
        Assert.Contains(beklenen, j.StartError!);
    }

    [Theory, MemberData(nameof(Tools))]
    public async Task Arac_Bulunamazsa_Sade_Cumle(string id)
    {
        // Yol ayarı var ama dosya sonradan silinmiş (kurulum kaldırıldı): "bulunamadı" der, ham hata değil.
        var geciciDir = Path.Combine(TestEnv.RepoRoot, "_gecici", "testler", "yok-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(geciciDir);
        var kopya = Path.Combine(geciciDir, Path.GetFileName(T(id).Script));
        File.Copy(T(id).Script, kopya);
        using var rig = new Rig(id, node: false, scriptPath: kopya);
        File.Delete(kopya);
        Directory.Delete(geciciDir);
        var job = rig.Job("iş [[sahte:oku]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.StartFailed, j.State);
        Assert.Equal(id == "hermes" ? HermesConnector.NotFoundText : OpenCodeConnector.NotFoundText, j.StartError);
        Assert.Contains("bulunamadı", j.StartError!);
        Assert.Empty(rig.Log());
    }

    // ------------------------------------------------------------------ olay çevirisi
    private static string J(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public void Hermes_Olay_Cevirisi_Sade_Turkce_Arac_Adi_Yok()
    {
        var t = new HermesEventStream();
        var all = new List<RunLine>();
        void F(string l) => all.AddRange(t.Feed(l));
        F(J(new { type = "system", subtype = "init", session_id = "" }));
        Assert.Null(t.SessionId);                                              // yeni oturumda kimlik henüz yok
        F("araç günlüğü, JSON değil");
        F(J(new { type = "text", text = "Başlı" }));
        F(J(new { type = "text", text = "yorum." }));                          // parçalar birleşir
        F(J(new { type = "tool_use", tool_call_id = "c1", name = "read_file", input = new { path = "E:\\p\\hesap.js" } }));
        F(J(new { type = "tool_use", tool_call_id = "c2", name = "terminal", input = new { command = "npm test" } }));
        F(J(new { type = "tool_result", tool_call_id = "c2", name = "terminal", output = "fail 1", is_error = true }));
        F(J(new { type = "tool_use", tool_call_id = "c3", name = "patch", input = new { path = "E:\\p\\hesap.js" } }));
        F(J(new { type = "tool_use", tool_call_id = "c4", name = "write_file", input = new { path = "E:\\p\\notlar.txt" } }));
        F(J(new { type = "tool_use", tool_call_id = "c5", name = "web_search", input = new { query = "node" } }));       // bekleyen değişiklik satırını bırakır
        F(J(new { type = "tool_use", tool_call_id = "c6", name = "bilinmeyen_arac", input = new { } }));                // sessiz
        F(J(new { type = "text", text = "Bitti." }));
        F(J(new { type = "result", session_id = "S1", text = "Bitti.", exit_code = 0, tokens = new { input = 1000, output = 100, cache_read = 500 } }));
        all.AddRange(t.Flush());

        var texts = all.Select(l => l.Text).ToList();
        Assert.Equal("Başlıyorum.", texts[0]);
        Assert.EndsWith("dosyasını okuyor", texts[1]);
        Assert.Equal(new[] { "bir komut çalıştırıyor: testler", "bir komut hata verdi", "2 dosyayı değiştirdi", "internette arıyor", "Bitti." }, texts.Skip(2).ToArray());
        Assert.Equal("agent", all[0].Kind);
        Assert.Equal("Komut: npm test", all[2].Detail);
        Assert.DoesNotContain(all, l => l.Text.Contains("terminal") || l.Text.Contains("read_file") || l.Text.Contains("patch"));
        Assert.Equal("S1", t.SessionId);                                       // kimlik ilk kez sonuçta geldi
        Assert.True(t.ResultSeen);
        Assert.False(t.ResultIsError);
        Assert.Equal(1, t.NumTurns);
        Assert.Equal(new AgentUsage(1000, 500, 100, null, null), t.Usage);
    }

    [Fact]
    public void Hermes_Hata_Nedenleri_Sade_Cumle()
    {
        var h = new HermesConnector();
        var giris = new HermesEventStream();
        giris.Feed(J(new { type = "result", session_id = "S", text = "", exit_code = 1, error = "No inference provider configured. Run `hermes setup`." }));
        Assert.True(giris.ResultSeen && giris.ResultIsError);
        Assert.Contains("hermes setup", h.FailureReason(1, giris, ""));
        var girdi = new HermesEventStream();
        Assert.Contains("onay ya da cevap", h.FailureReason(3, girdi, "input required"));
        var kapi = new HermesEventStream();
        Assert.Contains("arka plan hizmetine", h.FailureReason(1, kapi, "gateway connection refused; no local fallback"));
        Assert.Equal("Yardımcı sonuç vermeden kapandı.", h.FailureReason(0, new HermesEventStream(), ""));
        // sırlar hata cümlesine sızmaz
        var sirli = new HermesEventStream();
        Assert.DoesNotContain("s" + "k-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", h.FailureReason(1, sirli, "rate limit: s" + "k-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"));
    }

    [Fact]
    public void OpenCode_Olay_Cevirisi_Sade_Turkce_Arac_Adi_Yok()
    {
        var t = new OpenCodeEventStream();
        var all = new List<RunLine>();
        void F(string l) => all.AddRange(t.Feed(l));
        object Tool(string tool, object input, string status = "completed", object? meta = null) => new { type = "tool_use", sessionID = "ses_1", part = new { type = "tool", tool, state = new { status, input, output = "x", metadata = meta ?? new { exit = 0 } } } };
        F(J(new { type = "step_start", sessionID = "ses_1", part = new { type = "step-start" } }));
        F("günlük satırı, JSON değil");
        F(J(new { type = "text", sessionID = "ses_1", part = new { type = "text", text = "Başlıyorum." } }));
        F(J(Tool("read", new { filePath = "E:\\p\\hesap.js" })));
        F(J(Tool("bash", new { command = "npm test" }, meta: new { exit = 1 })));
        F(J(Tool("edit", new { filePath = "E:\\p\\hesap.js" })));
        F(J(Tool("write", new { filePath = "E:\\p\\notlar.txt" })));
        F(J(new { type = "reasoning", sessionID = "ses_1", part = new { type = "reasoning", text = "x" } }));            // bekleyen değişiklik satırını bırakır
        F(J(Tool("bash", new { command = "npm test" })));
        F(J(Tool("bilinmeyen_arac", new { })));                                                                           // sessiz
        F(J(new { type = "step_finish", sessionID = "ses_1", part = new { reason = "tool-calls", cost = 0, tokens = new { input = 1000, output = 100, reasoning = 20, cache = new { read = 500, write = 0 } } } }));
        Assert.False(t.ResultSeen);                                            // araç çağrısıyla biten adım sonuç değildir
        F(J(new { type = "text", sessionID = "ses_1", part = new { type = "text", text = "Bitti." } }));
        F(J(new { type = "step_finish", sessionID = "ses_1", part = new { reason = "stop", cost = 0.0123, tokens = new { input = 2000, output = 200, reasoning = 40, cache = new { read = 1000, write = 0 } } } }));
        all.AddRange(t.Flush());

        var texts = all.Select(l => l.Text).ToList();
        Assert.Equal("Başlıyorum.", texts[0]);
        Assert.EndsWith("dosyasını okuyor", texts[1]);
        Assert.Equal(new[] { "bir komut çalıştırıyor: testler", "bir komut hata verdi", "2 dosyayı değiştirdi", "düşünüyor", "bir komut çalıştırıyor: testler", "Bitti." }, texts.Skip(2).ToArray());
        Assert.DoesNotContain(all, l => l.Text.Contains("bash") || l.Text.Contains("read") && !l.Text.Contains("okuyor") || l.Text.Contains("edit"));
        Assert.Equal("ses_1", t.SessionId);
        Assert.True(t.ResultSeen);
        Assert.False(t.ResultIsError);
        Assert.Equal(1, t.NumTurns);
        Assert.Equal(new AgentUsage(3000, 1500, 300, 60, 0.0123), t.Usage);
        // fiyat 0 ise araç fiyat bilmiyor: dolar yok
        var z = new OpenCodeEventStream();
        z.Feed(J(new { type = "step_finish", sessionID = "s", part = new { reason = "stop", cost = 0, tokens = new { input = 10, output = 5, reasoning = 0, cache = new { read = 0, write = 0 } } } }));
        Assert.Null(z.Usage!.CostUsd);
        Assert.Equal(10, z.Usage.InputTokens);
    }

    [Fact]
    public void OpenCode_Hata_Olaylari_Iki_Surum_Ve_Sade_Nedenler()
    {
        var o = new OpenCodeConnector();
        var v2 = new OpenCodeEventStream();
        v2.Feed(J(new { type = "error", sessionID = "s", error = new { name = "APIError", message = "Not logged in. Run `opencode auth login`." } }));
        Assert.True(v2.ResultSeen && v2.ResultIsError);
        Assert.Contains("giriş yap", o.FailureReason(1, v2, ""));
        var v1 = new OpenCodeEventStream();
        v1.Feed(J(new { type = "error", sessionID = "s", error = new { name = "APIError", data = new { message = "You've hit your usage limit." } } }));
        Assert.True(v1.ResultIsError);
        Assert.Contains("kullanım sınırına", o.FailureReason(1, v1, ""));
        // tek adım başladı, hata ve sonuç yok: akış bitince hüküm çıkış koduna kalır (hata değil)
        var yarim = new OpenCodeEventStream();
        yarim.Feed(J(new { type = "step_start", sessionID = "s", part = new { type = "step-start" } }));
        Assert.False(yarim.ResultSeen);
        yarim.Flush();
        Assert.True(yarim.ResultSeen);
        Assert.False(yarim.ResultIsError);
        Assert.Equal("Yardımcı sonuç vermeden kapandı.", o.FailureReason(0, new OpenCodeEventStream(), ""));
        Assert.Contains("Yardımcı işi yarıda bırakıp durdu", o.FailureReason(1, new OpenCodeEventStream(), "boom: bir sey ters gitti"));
    }

    [Fact]
    public void Kayit_Iki_Baglayiciyi_Kapali_Ve_Yol_Ayarini_Guvenlik_Kapisi_Olarak_Tasir()
    {
        var load = RegistryLoader.LoadEmbedded();
        Assert.Empty(load.Rejected);
        Assert.True(RegistryChecker.Check(load).Ok);
        foreach (var id in new[] { "hermes", "opencode", "antigravity" })
        {
            var spec = AgentRegistry.Specs.Single(s => s.Id == id);
            Assert.True(spec.CanRun && spec.CanObserve);
            Assert.True(spec.IsActive);                                        // kurulu araçlar engele takılmasın diye açık gelir
            Assert.Equal(RegistryStates.Enabled, load.Get("connector:" + id)!.State);
            Assert.NotNull(spec.PathSetting);
            var setting = load.Get(spec.PathSetting!)!.Setting!;
            Assert.Equal("K4", setting.Class);                                 // program yolu: güvenlik kapısı sınıfı
            Assert.Empty(load.Get("connector:" + id)!.Contract!.Missing());
        }
    }
}
