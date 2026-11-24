using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// e3: m1 bağımsız ölçümünün altı kusuru (K1 yardımcı tespiti, K2 yanlış suçlama cümlesi, K3 eski alarm yolu, K5 iki ayrı teklif, K6 davranışla sınama, K7 izlenmeyen klasör) ve cümle sadeleşmesi.
/// Hepsi sentetik klasörlerde (TestEnv), gerçek node ile koşar. Sabit toplam sayıya bağlı değildir.
/// </summary>
public class E3KanitTests
{
    // ------------------------------------------------------------------ düzenek
    private static string J(object o) => JsonSerializer.Serialize(o);

    private static string Hook(TestEnv env, string ev, string stdin)
    {
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run(ev, new StringReader(stdin), o, env.DataDir, _ => null));
        return o.ToString();
    }

    private sealed record Rig(TestEnv Env, WorkService W, long Pid, string Dir);

    private static Rig NewRig(TestEnv env, string name, Action<string>? extra = null)
    {
        var dir = NodeSample.Create(env, name);
        NodeSample.Fix(dir);
        extra?.Invoke(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, enabled: true);
        return new Rig(env, w, pid, dir);
    }

    private static JobDto Start(Rig r, string tag)
    {
        var job = r.W.CreateJob(r.Pid, "Dosyaları incele: " + tag);
        r.W.PatchJob(job.Id, JobStates.Given);
        Hook(r.Env, "user-prompt-submit", J(new { session_id = "e3-" + tag, cwd = r.Dir, prompt = job.PromptText }));
        Assert.Equal(JobStates.Running, r.W.Job(job.Id).State);
        return job;
    }

    private static JobDto Finish(Rig r, JobDto job, string tag, bool twice = false)
    {
        string StopIn(bool active) => J(new Dictionary<string, object?> { ["session_id"] = "e3-" + tag, ["transcript_path"] = "t.jsonl", ["cwd"] = r.Dir, ["hook_event_name"] = "Stop", ["stop_hook_active"] = active, ["last_assistant_message"] = "Bitti." });
        Hook(r.Env, "stop", StopIn(false));
        if (twice) Hook(r.Env, "stop", StopIn(true));
        return r.W.Job(job.Id);
    }

    private static (string? verdict, string? reason, string? summary) Last(Rig r, long jobId)
    {
        using var c = r.Env.Svc.Db.Open();
        return c.Query("SELECT verdict, reason_code, summary FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", x => (x.Str(0), x.Str(1), x.Str(2)), null, ("@j", jobId)).FirstOrDefault();
    }

    private static EvidenceBase.Snapshot? Approved(Rig r) { using var c = r.Env.Svc.Db.Open(); return EvidenceBase.LoadApproved(c, r.Pid); }

    private static long VerdictRuns(Rig r) { using var c = r.Env.Svc.Db.Open(); return c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind=@k", null, ("@k", ProofStore.VerdictRunEvent)); }

    private static string Test(Rig r) => Path.Combine(r.Dir, "test", "hesap.test.js");
    private static string Lib(Rig r) => Path.Combine(r.Dir, "hesap.js");
    private const string Broken = NodeSample.BrokenLib;
    private const string YeniGecen = "'use strict';\nconst test = require('node:test');\nconst assert = require('node:assert/strict');\nconst { carp } = require('../hesap.js');\ntest('yeni carpma testi', () => { assert.equal(carp(5, 5), 25); });\n";

    private static void Put(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private static bool Tampering(string? reason) => ProofStore.Reasons.IsTampering(reason);

    // ================================================================== K3: zararsız dosya eklemek alarm değildir
    [Theory]
    [InlineData("docker-compose.yml", "services:\n  web:\n    image: x\n")]
    [InlineData(".npmrc", "registry=https://registry.npmjs.org/\n")]
    [InlineData(".ornek", "ORNEK=1\n")]
    [InlineData(".github/workflows/ci.yml", "name: ci\non: push\n")]
    [InlineData(".prettierrc", "{ \"semi\": true }\n")]
    [InlineData(".eslintrc.json", "{ \"root\": true }\n")]
    [InlineData("Dockerfile", "FROM node:20\n")]
    [InlineData(".editorconfig", "root = true\n")]
    [InlineData("tsconfig.json", "{ \"compilerOptions\": {} }\n")]
    [InlineData("uretilen/cikti.log", "satir\n")]
    [InlineData("jest.config.js", "module.exports = { testEnvironment: 'node' };\n")]
    public void K3_zararsiz_dosya_eklemek_alarm_degildir_sonuc_yine_verilir(string rel, string text)
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k3-" + Guid.NewGuid().ToString("N")[..4]);
        var job = Start(r, "k3");
        Put(r.Dir, rel, text);
        File.WriteAllText(Lib(r), NodeSample.FixedLib + "// not\n");
        var j = Finish(r, job, "k3");
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.True(j.State == JobStates.Passed, $"{rel}: {j.State} / {summary}");
        Assert.Equal("passed", verdict);
        Assert.False(Tampering(reason), $"{rel}: {reason}");
        Assert.NotEqual(ProofStore.Reasons.DefinitionChanged, reason);
        Assert.DoesNotContain("Sınama komutu ya da ayarı değişti", summary);
    }

    [Fact]
    public void K3_ajanin_ekledigi_test_ayari_teklifte_gorunur_onaylayinca_kumeye_girer()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k3ayar");
        var job = Start(r, "k3ayar");
        Put(r.Dir, "jest.config.js", "module.exports = {};\n");
        File.WriteAllText(Lib(r), NodeSample.FixedLib + "// not\n");
        var j = Finish(r, job, "k3ayar");
        Assert.Equal(JobStates.Passed, j.State);
        Assert.NotNull(j.TestOffer);
        Assert.Equal("Yardımcı 1 test ayarı ekledi. Bunu da onayladığın testlere katayım mı?", j.TestOffer!.AddedSentence);
        Assert.DoesNotContain(Approved(r)!.Suspects, e => e.Path == "jest.config.js");
        r.W.ApproveNewTests(job.Id, "added");
        Assert.Contains(Approved(r)!.Suspects, e => e.Path == "jest.config.js");
    }

    [Fact]
    public void K3_onayli_test_ayarini_ajan_degistirirse_onayli_haliyle_kosar_alarm_yok_not_var()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k3ayar2", d => Put(d, ".mocharc.json", "{ \"timeout\": 1000 }\n"));
        var job = Start(r, "k3ayar2");
        Assert.Contains(Approved(r)!.Suspects, e => e.Path == ".mocharc.json");
        Put(r.Dir, ".mocharc.json", "{ \"timeout\": 1 }\n");
        File.WriteAllText(Lib(r), NodeSample.FixedLib + "// not\n");
        var j = Finish(r, job, "k3ayar2");
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.Equal(JobStates.Passed, j.State);
        Assert.Equal("passed", verdict);
        Assert.False(Tampering(reason), reason);
        Assert.Contains("Test ayarı değişti: .mocharc.json", summary);
        Assert.DoesNotContain("Sınama komutu", summary);
    }

    // ================================================================== K3: testin çalışırken ürettiği dosyalar asla değişiklik sayılmaz
    [Fact]
    public void K3_testin_calisirken_yazdigi_dosyalar_alarm_ya_da_degisiklik_degildir_iki_is_ust_uste()
    {
        using var env = new TestEnv(copySamples: false);
        const string yazan = "'use strict';\nconst test = require('node:test');\nconst fs = require('node:fs');\nconst path = require('node:path');\n" +
            "test('rapor yazar', () => {\n  fs.mkdirSync(path.join(__dirname, '__snapshots__'), { recursive: true });\n  fs.writeFileSync(path.join(__dirname, '__snapshots__', 'rapor.snap'), String(process.hrtime.bigint()));\n" +
            "  fs.writeFileSync(path.join(__dirname, '..', 'test.log'), 'x' + Date.now());\n  fs.mkdirSync(path.join(__dirname, '..', 'coverage'), { recursive: true });\n  fs.writeFileSync(path.join(__dirname, '..', 'coverage', 'lcov.info'), String(Date.now()));\n});\n";
        var r = NewRig(env, "k3yaz", d => Put(d, "test/yaz.test.js", yazan));
        for (var i = 1; i <= 2; i++)
        {
            var tag = "yaz" + i;
            var job = Start(r, tag);
            File.WriteAllText(Lib(r), NodeSample.FixedLib + $"// tur {i}\n");
            var j = Finish(r, job, tag);
            var (verdict, reason, summary) = Last(r, job.Id);
            Assert.True(j.State == JobStates.Passed, $"{i}. iş: {j.State} / {summary}");
            Assert.Equal("passed", verdict);
            Assert.False(Tampering(reason), reason);
            Assert.False(summary!.Contains("rapor.snap"), $"{i}. iş: {summary}");
            Assert.Null(j.TestOffer);                                      // üretilen dosya teklif konusu değil
        }
        // İkinci işte onaylı tabana ürettiği dosya "Söz'ün ürettiği" diye yazılmıştır: onaylı koşu (ikinci koşu) gereksiz yere çalışmaz.
        Assert.Equal(0, VerdictRuns(r));
    }

    // ================================================================== K1: yardımcı dosya yapıdan bulunur, ada bakılmaz
    private static EvidenceBase.Snapshot ScanDir(TestEnv env, string name, Action<string> make)
    {
        var dir = Path.Combine(env.Root, name);
        Directory.CreateDirectory(dir);
        make(dir);
        return EvidenceBase.Scan(dir);
    }

    private static string Cls(EvidenceBase.Snapshot s, string path) => s.Suspects.FirstOrDefault(e => e.Path == path)?.Cls ?? "-";

    private const string Dogrula = "'use strict';\nconst assert = require('node:assert/strict');\nexports.esit = (a, b) => assert.equal(a, b);\n";

    [Fact]
    public void K1_testin_ice_aktardigi_iddia_yardimcisi_adi_ne_olursa_olsun_ve_nerede_olursa_olsun_kumeye_girer()
    {
        using var env = new TestEnv(copySamples: false);
        var s = ScanDir(env, "k1a", d =>
        {
            Put(d, "package.json", NodeSample.Package);
            Put(d, "hesap.js", NodeSample.FixedLib);
            Put(d, "lib/dogrula.js", Dogrula);
            Put(d, "yardimcilar/benim-kontrol.js", Dogrula);
            Put(d, "test/hesap.test.js", "const test = require('node:test');\nconst { esit } = require('../lib/dogrula.js');\nconst k = require('../yardimcilar/benim-kontrol');\nconst { topla } = require('../hesap.js');\ntest('t', () => { esit(topla(1, 2), 3); k.esit(1, 1); });\n");
        });
        Assert.Equal(EvidenceBase.ClsTest, Cls(s, "lib/dogrula.js"));
        Assert.Equal(EvidenceBase.ClsTest, Cls(s, "yardimcilar/benim-kontrol.js"));
        Assert.NotEqual(EvidenceBase.ClsTest, Cls(s, "hesap.js"));         // test edilen kod yargıç değildir
    }

    [Fact]
    public void K1_gecisli_zincir_project_ve_job_ikisi_de_kumeye_girer_dinamik_import_sabitse_izlenir()
    {
        using var env = new TestEnv(copySamples: false);
        var s = ScanDir(env, "k1b", d =>
        {
            Put(d, "package.json", NodeSample.Package);
            Put(d, "hesap.js", NodeSample.FixedLib);
            Put(d, "ortak/project.js", "const job = require('./job');\nexports.kur = () => job.kos();\n");
            Put(d, "ortak/job.js", "const assert = require('node:assert/strict');\nexports.kos = () => assert.ok(true);\n");
            Put(d, "ortak/gec.mjs", "import assert from 'node:assert/strict';\nexport const gec = () => assert.ok(1);\n");
            Put(d, "test/zincir.test.js", "const test = require('node:test');\nconst p = require('../ortak/project');\ntest('z', async () => { p.kur(); const m = await import('../ortak/gec.mjs'); m.gec(); });\n");
        });
        Assert.Equal(EvidenceBase.ClsTest, Cls(s, "ortak/project.js"));
        Assert.Equal(EvidenceBase.ClsTest, Cls(s, "ortak/job.js"));
        Assert.Equal(EvidenceBase.ClsTest, Cls(s, "ortak/gec.mjs"));
        Assert.True(s.Partial is null or { Count: 0 });
    }

    [Fact]
    public void K1_test_olmayan_kaynak_da_kullaniyorsa_kaynaktir_bilinen_sinir()
    {
        using var env = new TestEnv(copySamples: false);
        var s = ScanDir(env, "k1c", d =>
        {
            Put(d, "package.json", NodeSample.Package);
            Put(d, "lib/dogrula.js", Dogrula);
            Put(d, "src/uygulama.js", "const { esit } = require('../lib/dogrula.js');\nmodule.exports = () => esit(1, 1);\n");
            Put(d, "test/a.test.js", "const test = require('node:test');\nconst { esit } = require('../lib/dogrula.js');\ntest('a', () => esit(1, 1));\n");
        });
        Assert.NotEqual(EvidenceBase.ClsTest, Cls(s, "lib/dogrula.js"));
    }

    [Fact]
    public void K1_cozulemeyen_dinamik_import_varsa_o_test_icin_tam_belirlenemedi_notu_dusulur()
    {
        using var env = new TestEnv(copySamples: false);
        var s = ScanDir(env, "k1d", d =>
        {
            Put(d, "package.json", NodeSample.Package);
            Put(d, "lib/dogrula.js", Dogrula);
            Put(d, "test/dinamik.test.js", "const test = require('node:test');\nconst ad = process.env.YARDIMCI || 'dogrula';\nconst { esit } = require('../lib/dogrula.js');\ntest('d', () => { require('../lib/' + ad); esit(1, 1); });\n");
        });
        Assert.NotNull(s.Partial);
        Assert.Contains("test/dinamik.test.js", s.Partial!);
    }

    private const string DogrulaliTest = "const test = require('node:test');\nconst { esit } = require('../lib/dogrula.js');\nconst { topla } = require('../hesap.js');\ntest('toplama', () => { esit(topla(2, 3), 5); });\n";

    [Fact]
    public void K1_yardimciyi_ajan_zayiflatirsa_onayli_haliyle_kosulur_kod_bozuksa_onayli_testler_kalir()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k1kacak", d => { Put(d, "lib/dogrula.js", Dogrula); Put(d, "test/hesap.test.js", DogrulaliTest); });
        var job = Start(r, "k1kacak");
        Assert.Contains(Approved(r)!.Suspects, e => e.Path == "lib/dogrula.js");
        // Ajan kodu bozdu ve yardımcıyı hiçbir şeyi yakalamayan hâle getirdi: ajanın bıraktığı klasörde testler geçer, onaylı yardımcıyla geçmez.
        File.WriteAllText(Lib(r), Broken);
        Put(r.Dir, "lib/dogrula.js", "exports.esit = () => {};\n");
        var j = Finish(r, job, "k1kacak", twice: true);
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.NotEqual(JobStates.Passed, j.State);
        Assert.Equal("unchecked", verdict);
        Assert.Equal(ProofStore.Reasons.ApprovedFailed, reason);
        Assert.Contains("yeni kodla geçmiyor", summary);
    }

    [Fact]
    public void K1_zincirdeki_yardimci_da_korunur_project_job_kacagi_kapali()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k1zincir", d =>
        {
            Put(d, "ortak/project.js", "const job = require('./job');\nexports.esit = (a, b) => job.esit(a, b);\n");
            Put(d, "ortak/job.js", "const assert = require('node:assert/strict');\nexports.esit = (a, b) => assert.equal(a, b);\n");
            Put(d, "test/hesap.test.js", "const test = require('node:test');\nconst { esit } = require('../ortak/project');\nconst { topla } = require('../hesap.js');\ntest('toplama', () => { esit(topla(2, 3), 5); });\n");
        });
        var job = Start(r, "k1zincir");
        Assert.Contains(Approved(r)!.Suspects, e => e.Path == "ortak/job.js");
        Assert.Contains(Approved(r)!.Suspects, e => e.Path == "ortak/project.js");
        File.WriteAllText(Lib(r), Broken);
        Put(r.Dir, "ortak/job.js", "exports.esit = () => {};\n");
        var j = Finish(r, job, "k1zincir", twice: true);
        var (_, reason, _) = Last(r, job.Id);
        Assert.Equal(ProofStore.Reasons.ApprovedFailed, reason);
        Assert.NotEqual(JobStates.Passed, j.State);
    }

    // ================================================================== K2: "Ajan bu testleri de değiştirmiş" yalnız gerçekten değiştirdiyse
    [Fact]
    public void K2_ajan_testlere_dokunmadiysa_cumle_testlere_dokunmadi_der_suclama_yok()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k2a");
        Finish(r, Start(r, "k2a0"), "k2a0");                                     // ilk iş: onaylı taban bu işte alınır
        // Kişi araya girip testi kendisi gevşetti (iş başlamadan önce). Sonraki işte ajan yalnız kodu bozar, testlere dokunmaz.
        File.WriteAllText(Test(r), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        var job = Start(r, "k2a");
        File.WriteAllText(Lib(r), Broken);
        var j = Finish(r, job, "k2a", twice: true);
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.NotEqual(JobStates.Passed, j.State);
        Assert.NotEqual("passed", verdict);
        Assert.Contains("Yardımcı testlere dokunmadı, sorun kodda görünüyor.", summary);
        Assert.DoesNotContain("değiştirmiş", summary);
        Assert.Contains("Yardımcının değişikliğini kabul edebilir ya da işi yardımcıya geri gönderebilirsin.", summary);
    }

    [Fact]
    public void K2_ajan_onayli_testi_gercekten_degistirdiyse_cumle_bunu_soyler_ve_dosya_yolu_degil_test_adi_gosterilir()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k2b");
        var job = Start(r, "k2b");
        File.WriteAllText(Lib(r), Broken);
        File.WriteAllText(Test(r), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));      // gevşetti ve kodu bozdu
        var j = Finish(r, job, "k2b", twice: true);
        var (_, reason, summary) = Last(r, job.Id);
        Assert.Equal(ProofStore.Reasons.ApprovedFailed, reason);
        Assert.Contains("Yardımcı bu testleri de değiştirmiş, değişiklik gerekli olabilir.", summary);
        Assert.DoesNotContain("testlere dokunmadı", summary);
        Assert.Contains("Geçmeyen test: «toplama iki sayiyi toplar».", summary);  // kişiye dosya yolu değil test adı gösterilir
        Assert.DoesNotContain(".test.js", summary);
        Assert.DoesNotContain(";", summary);
    }

    // ================================================================== K5: iki ayrı teklif, değiştirilen için tek tıkla kabul yok
    private static (Rig r, JobDto job, JobDto done) TekliflerHazir(TestEnv env, string name)
    {
        var r = NewRig(env, name);
        var job = Start(r, name);
        File.WriteAllText(Test(r), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));   // onaylı testi değiştirdi
        File.WriteAllText(Path.Combine(r.Dir, "test", "yeni.test.js"), YeniGecen);                                    // yeni test ekledi
        var done = Finish(r, job, name);
        Assert.Equal(JobStates.Passed, done.State);
        return (r, job, done);
    }

    [Fact]
    public void K5_teklif_ikiye_ayrilir_cumleler_ve_eylemler_ayri()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, job, done) = TekliflerHazir(env, "k5a");
        var o = done.TestOffer!;
        Assert.Equal(1, o.Added);
        Assert.Equal(1, o.Edited);
        Assert.Equal("Yardımcı 1 yeni test ekledi. Bunu da onayladığın testlere katayım mı?", o.AddedSentence);
        Assert.Equal("Yardımcı onayladığın 1 testi değiştirdi (test/hesap.test.js: 1 satır değişti). Değişikliği görmeden kabul edemezsin.", o.EditedSentence);
        Assert.False(string.IsNullOrEmpty(o.EditedId));
        Assert.All(o.EditedFiles!, f => Assert.Null(f.Lines));                          // iş kartında satırlar yok, GET ile gelir
        var item = env.Svc.Attention.Snapshot().Items.Single(i => i.JobId == job.Id);
        var add = Assert.Single(item.Actions, a => a.Id == "approve-tests");
        Assert.Equal("added", add.Body!["scope"]!.GetValue<string>());
        var see = Assert.Single(item.Actions, a => a.Id == "view-test-edit");
        Assert.Equal("GET", see.Method);
        Assert.DoesNotContain(item.Actions, a => a.Path.EndsWith("approve-new-tests") && a.Body?["scope"]?.GetValue<string>() == "edited");   // tek tıkla kabul YOK
    }

    [Fact]
    public void K5_degistirilen_testi_gormeden_kabul_409_gorunce_ve_kimlikle_kabul_olur_digeri_etkilenmez()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, job, done) = TekliflerHazir(env, "k5b");
        var eskiSha = Approved(r)!.Suspects.Single(e => e.Path == "test/hesap.test.js").Sha;
        var id = done.TestOffer!.EditedId!;

        var e1 = Assert.Throws<SozException>(() => r.W.ApproveNewTests(job.Id, "edited"));                    // kimlik yok
        Assert.Equal(409, e1.Status);
        Assert.Contains("Değişikliği görmeden kabul edemezsin.", e1.Message);
        var e2 = Assert.Throws<SozException>(() => r.W.ApproveNewTests(job.Id, "edited", id));                // kimlik doğru ama fark özeti hiç okunmadı
        Assert.Equal(409, e2.Status);
        var e3 = Assert.Throws<SozException>(() => r.W.ApproveNewTests(job.Id, "all"));                       // ikisi birlikte olmaz
        Assert.Equal(409, e3.Status);
        Assert.Contains("ayrı ayrı", e3.Message);

        var detay = r.W.TestOffer(job.Id, human: true);                                                      // insanın okuması "gördü" kaydı yazar
        Assert.Equal(id, detay.EditedId);
        var f = Assert.Single(detay.EditedFiles!);
        Assert.Equal("test/hesap.test.js", f.Path);
        Assert.Contains(f.Lines!, l => l.Op == "+" && l.Text.Contains("assert.ok(true)"));
        Assert.Contains(f.Lines!, l => l.Op == "-" && l.Text.Contains("topla(2, 3)"));

        var e4 = Assert.Throws<SozException>(() => r.W.ApproveNewTests(job.Id, "edited", "yanlis-kimlik"));
        Assert.Equal(409, e4.Status);
        Assert.Equal(eskiSha, Approved(r)!.Suspects.Single(e => e.Path == "test/hesap.test.js").Sha);          // hâlâ eski test

        var j = r.W.ApproveNewTests(job.Id, "edited", id);
        Assert.NotEqual(eskiSha, Approved(r)!.Suspects.Single(e => e.Path == "test/hesap.test.js").Sha);       // değişiklik gördükten sonra kabul edildi
        Assert.DoesNotContain(Approved(r)!.Suspects, e => e.Path == "test/yeni.test.js");                      // eklenenler ayrı teklifti, katılmadı
        Assert.NotNull(j.TestOffer);
        Assert.Equal(1, j.TestOffer!.Added);
        Assert.Equal(0, j.TestOffer.Edited);
    }

    [Fact]
    public void K5_eklenenleri_katmak_degistirilen_onayli_testi_eski_birakir()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, job, _) = TekliflerHazir(env, "k5c");
        var eskiSha = Approved(r)!.Suspects.Single(e => e.Path == "test/hesap.test.js").Sha;
        var j = r.W.ApproveNewTests(job.Id);                                                                  // gövdesiz eski çağrı = added
        Assert.Contains(Approved(r)!.Suspects, e => e.Path == "test/yeni.test.js");
        Assert.Equal(eskiSha, Approved(r)!.Suspects.Single(e => e.Path == "test/hesap.test.js").Sha);          // (b) kabul edilmedi: onaylı küme eski testte kaldı
        Assert.NotNull(j.TestOffer);
        Assert.Equal(0, j.TestOffer!.Added);
        Assert.Equal(1, j.TestOffer.Edited);
        Assert.Equal(409, Assert.Throws<SozException>(() => r.W.ApproveNewTests(job.Id, "added")).Status);    // aynı teklif ikinci kez cevaplanmaz
    }

    // ================================================================== K6: sınıflanamayan zayıftır, komut davranışla sınanır
    [Fact]
    public void K6_olculemeyen_deneme_zayif_sayilir_varsayilan_hayir_ve_acik_cumle()
    {
        var r = AgentTestStrength.Unmeasured("iş öncesi görüntü yok.");
        Assert.Equal(AgentTestStrength.Weak, r.Kind);
        Assert.Equal("hayir", r.DefaultAnswer);
        Assert.False(r.Rejected);
        Assert.Contains("ölçemedim", r.Sentence);
        Assert.EndsWith("Zayıf sayıyorum.", r.Sentence);
        Assert.DoesNotContain(";", r.Sentence);
    }

    [Theory]
    [InlineData("node --test; echo bitti", true)]
    [InlineData("node --test | cat", true)]
    [InlineData("node --test || node -e 0", true)]
    [InlineData("node --test", false)]
    public void K6_komut_testler_kesin_kalan_kopyada_calistirilir_yine_basariliysa_reddedilir(string cmd, bool yutuyor)
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "k6-" + Guid.NewGuid().ToString("N")[..4]);
        NodeSample.Fix(dir);
        var snap = EvidenceBase.Scan(dir);
        var res = AgentTestStrength.SwallowsFailure(dir, env.DataDir, 7, cmd, snap, 512L << 20, 60);
        var beklenen = yutuyor && !(OperatingSystem.IsWindows() && cmd.Contains("; echo"));   // Windows cmd.exe ";" ayırıcı değil: komut dürüstçe kalır, yutmaz
        Assert.Equal(beklenen, res);
        Assert.Equal(NodeSample.Tests, File.ReadAllText(Path.Combine(dir, "test", "hesap.test.js")));            // gerçek klasöre dokunulmadı
        var parent = TempTree.ParentDir(env.DataDir);
        Assert.True(!Directory.Exists(parent) || Directory.GetFileSystemEntries(parent).Length == 0, "geçici kopya kalmamalı");
    }

    [Theory]
    [InlineData("node --test; echo bitti")]
    [InlineData("node --test | cat")]
    [InlineData("node --test || node -e 0")]
    public void K6_davranisla_reddedilen_oneri_komut_degismedikce_kabul_edilemez_409(string cmd)
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k6red-" + Guid.NewGuid().ToString("N")[..4]);
        long id; using (var cc = env.Svc.Db.Open()) id = CheckStore.Insert(cc, null, r.Pid, null, "command", "Ajanın yazdığı deneme",
            new JsonObject { ["cmd"] = cmd, ["expectExit"] = 0, ["rejected"] = true, ["strength"] = "always" }, 60, false, CheckStore.AgentTestOrigin);
        var ex = Assert.Throws<SozException>(() => r.W.PatchCheck(id, enabled: true));
        Assert.Equal(409, ex.Status);
        Assert.Equal("Bu komut kod bozukken de başarılı bitiyor. Deneme sayamam.", ex.Message);
        Assert.DoesNotContain(";", ex.Message);
        using var c = env.Svc.Db.Open();
        Assert.False(CheckStore.Get(c, id)!.Enabled);
    }

    // ================================================================== K7: izlenmeyen klasöre yazmak "hiçbir dosyayı değiştirmedi" değildir
    [Fact]
    public void K7_ajan_yalniz_izlenmeyen_klasore_yazdiysa_cumle_bunu_soyler()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k7a");
        var job = Start(r, "k7a");
        Put(r.Dir, "coverage/lcov.info", "TN:\n");
        var j = Finish(r, job, "k7a");
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.Equal(JobStates.Passed, j.State);
        Assert.Equal("passed", verdict);
        Assert.Equal(ProofStore.Reasons.IgnoredOnly, reason);
        Assert.StartsWith("Yardımcı yalnız izlenmeyen klasörlere yazdı (coverage). Kaynakta değişiklik yok.", summary);
        Assert.DoesNotContain("hiçbir dosyayı değiştirmedi", summary);
        Assert.Equal("Yardımcı yalnız izlenmeyen klasörlere yazdı", ProofStore.VerdictWord(verdict, reason));
        var item = env.Svc.Attention.Snapshot().Items.Single(i => i.JobId == job.Id);
        Assert.Equal("done", item.Kind);
        Assert.Equal("no_change", item.Reason);                                       // sakin etiket grubu aynı
        Assert.Contains("yalnız izlenmeyen klasörlere yazdı (coverage). Kaynakta değişiklik yok.", item.Sentence);
        Assert.DoesNotContain("hiçbir dosyayı değiştirmedi", item.Sentence);
    }

    [Fact]
    public void K7_ajan_gercekten_hicbir_sey_yazmadiysa_eski_cumle_kalir()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "k7b");
        var job = Start(r, "k7b");
        Finish(r, job, "k7b");
        var (_, reason, summary) = Last(r, job.Id);
        Assert.Equal(ProofStore.Reasons.NoChange, reason);
        Assert.StartsWith("Yardımcı hiçbir dosyayı değiştirmedi.", summary);
    }

    // ================================================================== e3 ek maddeler (arayüzün bulduğu eksikler)
    [Fact]
    public void Ek9_10_degisiklik_olmayan_is_olay_defterinde_sabahta_ve_is_kartinda_ayri_gorunur_neden_kodu_tasinir()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "ek9");
        var job = Start(r, "ek9");
        var j = Finish(r, job, "ek9");                                   // ajan hiçbir şey yazmadı
        Assert.Equal("no_change", j.LastReason);                          // madde 10: iş kartı neden kodunu taşır
        using var c = env.Svc.Db.Open();
        var feed = Soz.Core.Feed.EventFeed.Read(c, 100);
        var line = feed.Items.First(i => i.JobId == job.Id && i.Sentence.Contains("bitti"));
        Assert.Contains("ama yardımcı hiçbir dosyayı değiştirmedi", line.Sentence);   // madde 9: "deneme sonucuyla bitti" denmez
        Assert.DoesNotContain(feed.Items.Where(i => i.JobId == job.Id), i => i.Sentence.Contains("deneme sonucuyla bitti"));
        var m = r.W.Morning();
        Assert.Contains("1 işte değişiklik yok", m.Sentence);
        Assert.DoesNotContain("1 iş deneme sonucuyla bitti", m.Sentence);
        Assert.Equal("no_change", Assert.Single(m.Passed).ReasonCode);   // madde 10: Sabah satırı da neden kodu taşır
    }

    [Fact]
    public void Ek11_hayir_sunucuda_kayitlidir_ayni_teklif_geri_gelmez_yeni_is_yeni_teklif_uretebilir()
    {
        using var env = new TestEnv(copySamples: false);
        var (r, job, done) = TekliflerHazir(env, "ek11");
        Assert.Equal(400, Assert.Throws<SozException>(() => r.W.DeclineNewTests(job.Id, "baska")).Status);
        var after = r.W.DeclineNewTests(job.Id, "added");
        Assert.NotNull(after.TestOffer);
        Assert.Equal(0, after.TestOffer!.Added);
        Assert.Equal(1, after.TestOffer.Edited);                                         // değiştirilen teklif ayrı, hâlâ açık
        Assert.Equal(409, Assert.Throws<SozException>(() => r.W.DeclineNewTests(job.Id, "added")).Status);
        Assert.Equal(409, Assert.Throws<SozException>(() => r.W.ApproveNewTests(job.Id, "added")).Status);   // reddedilen teklif sonradan kabul edilmez
        var son = r.W.DeclineNewTests(job.Id, "edited");
        Assert.Null(son.TestOffer);
        using (var c = env.Svc.Db.Open())
            Assert.Equal(2, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind=@k", null, ("@k", EvidenceBase.TestsDeclinedEvent)));   // olay defterine yazıldı
        Assert.False(Soz.Server.CallerGate.AgentMayWrite("POST", $"/api/jobs/{job.Id}/decline-new-tests"));   // yalnız insan
        // yeni iş yeni teklif üretebilir
        var job2 = Start(r, "ek11b");
        File.WriteAllText(Path.Combine(r.Dir, "test", "yeni2.test.js"), YeniGecen.Replace("yeni carpma", "ikinci carpma"));
        Assert.Equal(1, Finish(r, job2, "ek11b").TestOffer!.Added);
    }

    [Fact]
    public void Ek12_onayli_testler_gecmediginde_baslangic_cumlesi_karar_cumlesiyle_celismez()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "ek12");
        var job = Start(r, "ek12");
        File.WriteAllText(Lib(r), Broken);
        File.WriteAllText(Test(r), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        Finish(r, job, "ek12", twice: true);
        using var c = env.Svc.Db.Open();
        var (reason, bt) = c.Query("SELECT reason_code, baseline_text FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", x => (x.Str(0), x.Str(1)), null, ("@j", job.Id)).First();
        Assert.Equal(ProofStore.Reasons.ApprovedFailed, reason);
        Assert.True(string.IsNullOrEmpty(bt), bt);
    }

    // ================================================================== cümleler
    [Theory]
    [InlineData("[Söz] Zaman aşımı: 10 sn içinde bitmedi. Başlattığım süreç ağacı kapatıldı. Sonuç bilinmiyor.", "Testler 10 saniyede bitmedi.")]
    [InlineData(null, "Testler sonuç vermedi.")]
    public void Ham_kosucu_iletisi_kisiye_gun_diliyle_soylenir(string? ham, string beklenen)
    {
        Assert.Equal(beklenen, ProofStore.PlainRunnerMessage(ham));
    }

    [Fact]
    public void Kisiye_gorunen_yeni_cumlelerde_ic_terim_ve_noktali_virgul_yok()
    {
        foreach (var s in new[] { AgentTestStrength.AlwaysSentence, AgentTestStrength.WeakSentence, AgentTestStrength.Unmeasured("x").Sentence, ProofStore.PlainRunnerMessage("[Söz] Zaman aşımı: 5 sn içinde bitmedi.") })
        {
            Assert.DoesNotContain(";", s);
            Assert.DoesNotContain("hüküm", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ikinci koşu", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("geçici kopya", s, StringComparison.OrdinalIgnoreCase);
            Assert.True(s.Length <= 220, s);
        }
    }
}
