using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Security;
using Soz.Core.Restore;
using Soz.Server;
using Soz.Server.Endpoints;
using Xunit;

namespace Soz.Core.Tests;

public class E4KanitTests
{
    [Fact]
    public void Ek14_zayif_denemeye_hayir_kalici_kaydedilir_oneri_tekrarlanmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var dir = NodeSample.Create(env, "hayir");
        var project = w.EnsureProjectByPath(dir);
        var job = w.CreateJob(project, "Denemeyi yaz", writeCheckFirst: true);
        long id;
        using (var c = env.Svc.Db.Open())
            id = CheckStore.Insert(c, null, project, null, "command", "Ajanın yazdığı deneme",
                new JsonObject { ["cmd"] = "node --test", ["suggestedByJob"] = job.Id, ["strength"] = "weak", ["defaultAnswer"] = "hayir" },
                60, false, CheckStore.AgentTestOrigin);
        Assert.Equal(id, Assert.Single(w.Job(job.Id).SuggestedChecks!).CheckId);
        Assert.Empty(w.DeclineSuggestedCheck(id).SuggestedChecks!);
        Assert.Empty(new WorkService(env.Svc.Db).Job(job.Id).SuggestedChecks!);
        using (var c = env.Svc.Db.Open())
        {
            var saved = CheckStore.Get(c, id)!;
            Assert.False(saved.Enabled);
            Assert.True(saved.Spec["declined"]!.GetValue<bool>());
            var line = Assert.Single(Soz.Core.Feed.EventFeed.Read(c, 100).Items, e => e.Kind == "job_check_declined");
            Assert.Equal(job.Id, line.JobId);
            Assert.Contains("Hayır dedin. Deneme açılmadı.", line.Sentence);
        }
        Assert.Equal(409, Assert.Throws<SozException>(() => w.DeclineSuggestedCheck(id)).Status);
        Assert.Equal(404, Assert.Throws<SozException>(() => w.DeclineSuggestedCheck(long.MaxValue)).Status);
        Assert.False(CallerGate.AgentMayWrite("POST", $"/api/checks/{id}/decline"));
    }

    [Fact]
    public void Ek13_cumle_tekrar_ayrinti_olmaz_farkli_bilgi_korunur()
    {
        using var env = new TestEnv(copySamples: false);
        var (_, _, job) = EditedOffer(env, _ => { });
        var item = Assert.Single(env.Svc.Attention.Snapshot().Items, i => i.JobId == job.Id);
        Assert.Contains("Yardımcı hiçbir dosyayı değiştirmedi.", item.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Testler zaten geçiyordu.", item.Detail ?? "");
        Assert.Contains("kontrol geçti", item.Detail);
        using var c = env.Svc.Db.Open();
        c.Exec("UPDATE proof_runs SET summary=@s WHERE job_id=@j", null, ("@s", "Ajan hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu."), ("@j", job.Id));
        var repeated = Assert.Single(env.Svc.Attention.Snapshot().Items, i => i.JobId == job.Id);
        Assert.Null(repeated.Detail);
    }
    [Fact]
    public void Ek12_reddedilen_test_teklifi_olay_listesinde_sade_cumleyle_gorunur()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, job) = EditedOffer(env);
        w.DeclineNewTests(job.Id, "edited");
        using var c = env.Svc.Db.Open();
        var page = Soz.Core.Feed.EventFeed.Read(c, 100);
        var line = Assert.Single(page.Items, e => e.Kind == "job_tests_declined");
        Assert.Equal(job.Id, line.JobId);
        Assert.Equal("job", line.Group);
        Assert.Contains("test değişikliklerini kabul etmedin", line.Sentence);
        Assert.Contains("Onayladığın testler korundu.", line.Sentence);
    }
    [Theory]
    [InlineData(1, "Bunu")]
    [InlineData(2, "Bunları")]
    public void Cumle_teklif_tekil_cogul_ve_buyuk_harf(int count, string pronoun)
    {
        using var env = new TestEnv(copySamples: false);
        var (w, dir, job) = EditedOffer(env);
        using var c = env.Svc.Db.Open();
        var added = Enumerable.Range(1, count).Select(i => $"test/yeni{i}.test.js").ToList();
        foreach (var f in added) Put(dir, f, "const test = require('node:test'); test('yeni', () => {});");
        EvidenceBase.RecordTestOffer(c, job.ProjectId, job.Id, EvidenceBase.Scan(dir), added, []);
        Assert.Contains(pronoun + " da onayladığın testlere", w.Job(job.Id).TestOffer!.AddedSentence);
        foreach (var reason in new[] { ProofStore.Reasons.ApprovedFailed, ProofStore.Reasons.AgentTestsFail, "" })
            Assert.True(char.IsUpper(ProofStore.VerdictWord("unchecked", reason)[0]));
        Assert.True(char.IsUpper(ProofStore.PlainRunnerMessage(null)[0]));
    }
    private sealed class NoBlobs : IApprovedBlobs
    {
        public bool TryWrite(string sha, string targetPath) => false;
        public void Dispose() { }
    }

    [Theory]
    [InlineData("echo basla && node --test")]
    [InlineData("cmd /c echo basla && node --test")]
    public void K12_echo_ondeyse_davranis_karar_verir_kabul_engellenmez(string cmd)
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "echo-deneme");
        NodeSample.Fix(dir);
        Assert.False(AgentTestStrength.AlwaysSucceeds(cmd, dir));
        Assert.False(AgentTestStrength.SwallowsFailure(dir, env.DataDir, 1, cmd, EvidenceBase.Scan(dir), 16L << 20, 10));
    }

    [Fact]
    public void K12_genel_ret_cumlesi_komut_bicimi_uydurmaz()
    {
        Assert.Equal("Bu komut kod bozukken de başarılı bitiyor. Deneme sayamam.", AgentTestStrength.AlwaysSentence);
        Assert.DoesNotContain("|| true", AgentTestStrength.AlwaysSentence);
        Assert.True(AgentTestStrength.AlwaysSucceeds("echo bitti", null));
        Assert.True(AgentTestStrength.AlwaysSucceeds("node --test || true", null));
    }

    [Fact]
    public void K11_asili_komut_guc_ve_davranis_olcumunde_sureyle_kesilir_hayir_ve_kalinti_yok()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "asili-deneme");
        var snap = EvidenceBase.Scan(dir);
        using var blobs = new NoBlobs();
        const string cmd = "node -e \"setInterval(()=>{},1000)\"";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = AgentTestStrength.Measure(dir, env.DataDir, 9, cmd, new PointState(), blobs, snap, 16L << 20, 1);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), clock.Elapsed.ToString());
        Assert.Equal("weak", result.Kind);
        Assert.Equal("hayir", result.DefaultAnswer);
        Assert.Contains("ölçemedim", result.Sentence);
        clock.Restart();
        Assert.Null(AgentTestStrength.SwallowsFailure(dir, env.DataDir, 9, cmd, snap, 16L << 20, 1));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), clock.Elapsed.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(TempTree.ParentDir(env.DataDir)));
    }

    [Fact]
    public void K11_temizlik_yalniz_eski_isaretli_gecici_kopyayi_kaldirir()
    {
        using var env = new TestEnv(copySamples: false);
        var old = TempTree.NewBase(env.DataDir, "n5-eski");
        var recent = TempTree.NewBase(env.DataDir, "n5-yeni");
        var unmanaged = Path.Combine(TempTree.ParentDir(env.DataDir), "dokunma");
        Directory.CreateDirectory(unmanaged);
        File.WriteAllText(Path.Combine(unmanaged, "benim.txt"), "koru");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        Directory.SetLastWriteTimeUtc(unmanaged, DateTime.UtcNow.AddHours(-2));
        Assert.Equal(1, VerdictRun.SweepOld(env.DataDir));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(File.Exists(Path.Combine(unmanaged, "benim.txt")));
    }

    [Fact]
    public async Task K11_sunucu_acilirken_eski_kopya_temizlenir()
    {
        using var env = new TestEnv(copySamples: false);
        var fake = Path.Combine(env.Root, "sahte-ajan.cmd");
        File.WriteAllText(fake, "@echo off\r\nexit /b 0\r\n");
        var patch = new JsonObject();
        foreach (var key in new[] { "claudePath", "codexPath", "hermesPath", "opencodePath" }) patch[key] = fake;
        env.Svc.PutSettings(patch);
        using (var c = env.Svc.Db.Open())
            foreach (var key in new[] { "claudePath", "codexPath", "hermesPath", "opencodePath" }) Assert.Equal(fake, SozSettings.Load(c).Str(key));
        var old = TempTree.NewBase(env.DataDir, "n5-acilistan-kalma");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        await using var server = await SozServer.StartAsync(env.DataDir, 0);
        Assert.False(Directory.Exists(old));
    }
    private static void Put(string root, string rel, string text)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Theory]
    [InlineData("export * from './kontrol.js';")]
    [InlineData("export { esit } from './kontrol.js';")]
    [InlineData("module.exports = require('./kontrol.js');")]
    [InlineData("exports.esit = require('./kontrol.js').esit;")]
    [InlineData("Object.assign(module.exports, require('./kontrol.js'));")]
    [InlineData("module.exports = { ...require('./kontrol.js') };")]
    public void K1a_yeniden_disa_aktarilan_iddia_yardimcisi_kumeye_girer(string barrel)
    {
        using var env = new TestEnv(copySamples: false);
        var root = Path.Combine(env.Root, "reexport");
        Put(root, "test/hesap.test.js", "const test = require('node:test'); const { esit } = require('../lib/index.js'); const hesap = require('../hesap.js'); test('topla', () => esit(hesap(2, 3), 5));");
        Put(root, "hesap.js", "module.exports = (a, b) => a + b;");
        Put(root, "lib/index.js", barrel);
        Put(root, "lib/kontrol.js", "const assert = require('node:assert/strict'); exports.esit = assert.equal;");
        var before = EvidenceBase.Scan(root);
        Assert.Contains(ApprovedSet.Members(before), e => e.Path == "lib/kontrol.js");
        Assert.Contains(ApprovedSet.Members(before), e => e.Path == "lib/index.js");
        Assert.DoesNotContain(ApprovedSet.Members(before), e => e.Path == "hesap.js");
        Put(root, "lib/kontrol.js", "exports.esit = () => {};");
        Put(root, "hesap.js", "module.exports = (a, b) => a - b;");
        var now = EvidenceBase.Scan(root);
        Assert.Contains("lib/kontrol.js", ApprovedSet.Changes(before, now).All);
    }
    private static void Hook(TestEnv env, string ev, object input)
        => Assert.Equal(0, HookHandler.Run(ev, new StringReader(JsonSerializer.Serialize(input)), new StringWriter(), env.DataDir, _ => null));

    [Theory]
    [InlineData("ayni")]
    [InlineData("mtime")]
    [InlineData("geri")]
    public void K7_ayni_icerik_zaman_ve_geri_alma_no_change(string mode)
    {
        using var env = new TestEnv(copySamples: false);
        var (_, _, job) = EditedOffer(env, root =>
        {
            var file = Path.Combine(root, "hesap.js");
            var text = File.ReadAllText(file);
            if (mode == "geri") File.WriteAllText(file, "bozuk");
            if (mode != "mtime") File.WriteAllText(file, text);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(10));
        });
        Assert.Equal("no_change", job.LastReason);
    }

    [Fact]
    public void K7_ayni_boyut_ayni_zaman_farkli_icerik_no_change_degil()
    {
        using var env = new TestEnv(copySamples: false);
        var root = Path.Combine(env.Root, "icerik");
        Put(root, "kaynak.js", "a + b");
        var path = Path.Combine(root, "kaynak.js");
        var stamp = File.GetLastWriteTimeUtc(path);
        var before = EvidenceBase.Scan(root);
        Put(root, "kaynak.js", "a - b");
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.NotEqual(before.TreeId, EvidenceBase.Scan(root).TreeId);
    }

    [Fact]
    public void K7_izlenmeyen_butun_klasorler_cumlede_sayilir()
    {
        using var env = new TestEnv(copySamples: false);
        var (_, _, job) = EditedOffer(env, root =>
        {
            foreach (var dir in new[] { "coverage", "dist", "build", ".cache" }) Put(root, dir + "/cikti.txt", "çıktı");
        });
        Assert.Equal("ignored_only", job.LastReason);
        using var c = env.Svc.Db.Open();
        var summary = c.Scalar("SELECT summary FROM proof_runs WHERE job_id=@j ORDER BY id DESC LIMIT 1", null, ("@j", job.Id)) as string;
        foreach (var dir in new[] { "coverage", "dist", "build", ".cache" }) Assert.Contains(dir, summary);
        Assert.DoesNotContain("ve diğerleri", summary);
    }

    [Theory]
    [InlineData("'veri/beklenen.json'", "json")]
    [InlineData("path.join(__dirname, '..', 'veri', 'beklenen.json')", "json")]
    [InlineData("'../veri/beklenen.txt'", "txt")]
    [InlineData("'veri/beklenen.csv'", "csv")]
    [InlineData("'veri/beklenen.yml'", "yml")]
    [InlineData("'veri/beklenen.yaml'", "yaml")]
    [InlineData("'veri/beklenen.xml'", "xml")]
    [InlineData("'veri/beklenen.snap'", "snap")]
    [InlineData("'veri/beklenen.html'", "html")]
    [InlineData("'veri/beklenen.md'", "md")]
    public void K1b_testin_okudugu_veri_onayli_kumeye_girer_degisim_yakalanir(string expression, string ext)
    {
        using var env = new TestEnv(copySamples: false);
        var root = Path.Combine(env.Root, "fixture");
        var fixture = "veri/beklenen." + ext;
        Put(root, fixture, "5\n");
        Put(root, "hesap.js", "exports.topla = (a,b) => a + b;");
        Put(root, "test/hesap.test.js", "const test = require('node:test'); const assert = require('node:assert/strict'); const fs = require('node:fs'); const path = require('node:path'); const hesap = require('../hesap.js'); const veri = fs.readFileSync(" + expression + ");");
        var before = EvidenceBase.Scan(root);
        Assert.Contains(ApprovedSet.Members(before), e => e.Path == fixture);
        Assert.DoesNotContain(ApprovedSet.Members(before), e => e.Path == "hesap.js");
        Put(root, fixture, "-1\n");
        Assert.Contains(fixture, ApprovedSet.Changes(before, EvidenceBase.Scan(root)).Edited);
    }

    [Fact]
    public void K1b_yardimci_verisi_dahil_genel_belge_kilit_ve_kaynak_haric()
    {
        using var env = new TestEnv(copySamples: false);
        var root = Path.Combine(env.Root, "veri-sinir");
        Put(root, "test/a.test.js", "const test = require('node:test'); require('../lib/yardim.js'); const a = 'package.json'; const b = 'README.md'; const c = 'LICENSE.txt'; const d = 'package-lock.json'; const e = '../hesap.js'; const f = '../../dis.json';");
        Put(root, "lib/yardim.js", "const assert = require('node:assert/strict'); const f = require('node:fs').readFileSync('veri/beklenen.json');");
        Put(root, "veri/beklenen.json", "5");
        Put(root, "package.json", "{}");
        foreach (var name in new[] { "README.md", "LICENSE.txt", "package-lock.json", "hesap.js", "eklenen.md" }) Put(root, name, "zararsız");
        var snap = EvidenceBase.Scan(root);
        Assert.Contains(ApprovedSet.Members(snap), e => e.Path == "veri/beklenen.json");
        foreach (var name in new[] { "README.md", "LICENSE.txt", "package-lock.json", "hesap.js", "eklenen.md" })
            Assert.DoesNotContain(ApprovedSet.Members(snap), e => e.Path == name);
        Assert.Single(ApprovedSet.Members(snap), e => e.Path == "package.json"); // kendi ayar kuralıyla, test verisi sınıfında değil
        Assert.Equal(EvidenceBase.ClsConfig, snap.Suspects.Single(e => e.Path == "package.json").Cls);
    }

    private static (WorkService work, string dir, JobDto job) EditedOffer(TestEnv env, Action<string>? change = null)
    {
        var dir = NodeSample.Create(env, "e4-proje");
        NodeSample.Fix(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 60, enabled: true);
        var job = w.CreateJob(pid, "Test açıklamasını incele");
        w.PatchJob(job.Id, JobStates.Given);
        Hook(env, "user-prompt-submit", new { session_id = "e4", cwd = dir, prompt = job.PromptText });
        if (change is null) File.AppendAllText(Path.Combine(dir, "test", "hesap.test.js"), "\n// Yeni açıklama\n");
        else change(dir);
        Hook(env, "stop", new { session_id = "e4", cwd = dir, transcript_path = "t.jsonl", hook_event_name = "Stop", stop_hook_active = false, last_assistant_message = "Bitti." });
        job = w.Job(job.Id);
        Assert.Equal(JobStates.Passed, job.State);
        if (change is null) Assert.Equal(1, job.TestOffer!.Edited);
        return (w, dir, job);
    }

    private static DefaultHttpContext Caller(CallerKind kind)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Items["soz.caller"] = new CallerInfo(kind, 123, "sentetik", "E4 sınaması");
        return ctx;
    }

    [Theory]
    [InlineData("lint")]
    [InlineData("start")]
    [InlineData("build")]
    [InlineData("description")]
    [InlineData("coklu")]
    public void K2_durust_package_degisimleri_teklif_ve_alarm_uretmez(string field)
    {
        using var env = new TestEnv(copySamples: false);
        var (_, _, job) = EditedOffer(env, root =>
        {
            var path = Path.Combine(root, "package.json");
            var pkg = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (field == "description") pkg[field] = "Yeni açıklama";
            else if (field == "coklu") { pkg["scripts"]!["lint"] = "echo lint"; pkg["scripts"]!["build"] = "echo build"; pkg["scripts"]!["start"] = "echo start"; }
            else pkg["scripts"]![field] = "echo " + field;
            File.WriteAllText(path, pkg.ToJsonString());
        });
        Assert.Null(job.TestOffer);
        using var c = env.Svc.Db.Open();
        var summary = c.Scalar("SELECT summary FROM proof_runs WHERE job_id=@j ORDER BY id DESC LIMIT 1", null, ("@j", job.Id)) as string;
        Assert.DoesNotContain("onayladığın testlere de dokundu", summary);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("alt")]
    [InlineData("derin")]
    [InlineData("pretest")]
    [InlineData("posttest")]
    public void K2_test_betigi_ve_cagirdigi_zincir_izlenir_merge_ilgisizi_korur(string script)
    {
        const string before = "{\"scripts\":{\"test\":\"npm run alt\",\"alt\":\"npm run derin\",\"derin\":\"node --test\",\"pretest\":\"echo hazir\",\"posttest\":\"echo bitti\",\"lint\":\"echo eski\"}}";
        var after = JsonNode.Parse(before)!.AsObject();
        after["scripts"]![script] = "echo degisti";
        after["scripts"]!["lint"] = "echo yeni";
        Assert.True(ApprovedSet.PackageTestKeysDiffer(before, after.ToJsonString()));
        var merged = JsonNode.Parse(ApprovedSet.MergePackage(before, after.ToJsonString()))!;
        Assert.Equal(JsonNode.Parse(before)!["scripts"]![script]!.GetValue<string>(), merged["scripts"]![script]!.GetValue<string>());
        Assert.Equal("echo yeni", merged["scripts"]!["lint"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("dependencies")]
    [InlineData("devDependencies")]
    public void K2_bagimlilik_ekleme_silme_surumu_bilgi_notudur(string key)
    {
        using var env = new TestEnv(copySamples: false);
        var root = Path.Combine(env.Root, "deps");
        Put(root, "package.json", "{\"scripts\":{\"test\":\"node --test\"},\"" + key + "\":{\"a\":\"1\",\"b\":\"1\"}}");
        var before = EvidenceBase.Scan(root);
        Put(root, "package.json", "{\"scripts\":{\"test\":\"node --test\"},\"" + key + "\":{\"a\":\"2\",\"c\":\"1\"}}");
        var now = EvidenceBase.Scan(root);
        Assert.False(ApprovedSet.Changes(before, now).Any);
        var diff = EvidenceBase.Compare(before, now, root);
        Assert.Empty(diff.ChangedConfig);
        Assert.NotEmpty(diff.Notes!);
        Assert.Contains(diff.Notes!, note => note.Contains("bağımlılığ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void K9_cekirdekte_ajan_okumasi_kayit_yazmaz_eski_kimlik_yeni_teklifi_acmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, dir, job) = EditedOffer(env);
        var agent = w.TestOffer(job.Id, human: false);
        Assert.Equal(409, Assert.Throws<SozException>(() => w.ApproveNewTests(job.Id, "edited", agent.EditedId)).Status);
        var seen = w.TestOffer(job.Id, human: true);
        using var c = env.Svc.Db.Open();
        var approved = EvidenceBase.LoadApproved(c, job.ProjectId)!;
        File.AppendAllText(Path.Combine(dir, "test", "hesap.test.js"), "// İkinci değişiklik\n");
        var now = EvidenceBase.Scan(dir);
        EvidenceBase.RecordTestOffer(c, job.ProjectId, job.Id, now, [], ["test/hesap.test.js"], approved);
        var newer = w.TestOffer(job.Id, human: false);
        Assert.NotEqual(seen.EditedId, newer.EditedId);
        EvidenceBase.MarkOfferSeen(c, job.ProjectId, job.Id, newer.EditedId!, human: false);
        Assert.False(EvidenceBase.OfferSeen(c, job.Id, newer.EditedId!));
        Assert.Equal(409, Assert.Throws<SozException>(() => w.ApproveNewTests(job.Id, "edited", seen.EditedId)).Status);
        Assert.Equal(409, Assert.Throws<SozException>(() => w.ApproveNewTests(job.Id, "edited", newer.EditedId)).Status);
        w.TestOffer(job.Id, human: true);
        Assert.Null(w.ApproveNewTests(job.Id, "edited", newer.EditedId).TestOffer);
    }

    [Theory]
    [InlineData(CallerKind.Agent)]
    [InlineData(CallerKind.Unknown)]
    public void K9_ajan_GET_insan_POST_edited_409_insan_GET_sonrasi_200(CallerKind kind)
    {
        using var env = new TestEnv(copySamples: false);
        var (w, _, job) = EditedOffer(env);
        var offer = JobsProofEndpoints.ReadTestOffer(Caller(kind), env.Svc.Db, job.Id);
        Assert.NotEmpty(Assert.Single(offer.EditedFiles!).Lines!);
        using (var c = env.Svc.Db.Open()) Assert.False(EvidenceBase.OfferSeen(c, job.Id, offer.EditedId!));
        Assert.Equal(409, Assert.Throws<SozException>(() => w.ApproveNewTests(job.Id, "edited", offer.EditedId)).Status);
        var human = JobsProofEndpoints.ReadTestOffer(Caller(CallerKind.Human), env.Svc.Db, job.Id);
        using (var c = env.Svc.Db.Open()) Assert.True(EvidenceBase.OfferSeen(c, job.Id, human.EditedId!));
        Assert.Null(w.ApproveNewTests(job.Id, "edited", human.EditedId).TestOffer);
    }

    [Fact]
    public void K10_package_json_gercek_fark_uretir_insan_gorunce_kabul_edilir()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, dir, job) = EditedOffer(env);
        using var c = env.Svc.Db.Open();
        var approved = EvidenceBase.LoadApproved(c, job.ProjectId)!;
        var pkg = Path.Combine(dir, "package.json");
        File.WriteAllText(pkg, File.ReadAllText(pkg).Replace("node --test", "echo basla && node --test"));
        EvidenceBase.RecordTestOffer(c, job.ProjectId, job.Id, EvidenceBase.Scan(dir), [], ["package.json"], approved);
        var offer = w.TestOffer(job.Id, human: true);
        var f = Assert.Single(offer.EditedFiles!);
        Assert.Contains(f.Lines!, l => l.Op == "+" && l.Text.Contains("echo basla"));
        Assert.Contains(f.Lines!, l => l.Op == "-" && l.Text.Contains("node --test"));
        Assert.Null(w.ApproveNewTests(job.Id, "edited", offer.EditedId).TestOffer);
    }

    [Fact]
    public void K10_gosterilemeyen_fark_cekirdekte_409_ve_taban_degismez()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, dir, job) = EditedOffer(env);
        using var c = env.Svc.Db.Open();
        var approved = EvidenceBase.LoadApproved(c, job.ProjectId)!;
        File.WriteAllBytes(Path.Combine(dir, "test", "hesap.test.js"), [0, 1, 2]);
        EvidenceBase.RecordTestOffer(c, job.ProjectId, job.Id, EvidenceBase.Scan(dir), [], ["test/hesap.test.js"], approved);
        var offer = w.TestOffer(job.Id, human: true);
        Assert.Empty(Assert.Single(offer.EditedFiles!).Lines!);
        var ex = Assert.Throws<SozException>(() => w.ApproveNewTests(job.Id, "edited", offer.EditedId));
        Assert.Equal(409, ex.Status);
        Assert.Equal(EvidenceBase.UnviewableEditMessage, ex.Message);
        Assert.Equal(409, Assert.Throws<SozException>(() => EvidenceBase.ApproveNewTests(c, job.ProjectId, job.Id, scope: "edited")).Status);
        Assert.Equal(approved.Suspects, EvidenceBase.LoadApproved(c, job.ProjectId)!.Suspects);
    }

    [Fact]
    public void K9_gordukten_sonra_icerik_degisti_ayni_teklif_kimligiyle_kabul_edilemez()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, dir, job) = EditedOffer(env);
        var offer = w.TestOffer(job.Id, human: true);
        File.AppendAllText(Path.Combine(dir, "test", "hesap.test.js"), "// Sonradan değişti\n");
        Assert.Equal(409, Assert.Throws<SozException>(() => w.ApproveNewTests(job.Id, "edited", offer.EditedId)).Status);
    }
}
