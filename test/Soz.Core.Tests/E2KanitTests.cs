using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Restore;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// e2: YÖNTEM DEĞİŞİKLİĞİ. Yargıç, kişinin onayladığı testlerdir; ajanın düzenlediği testler değil.
/// İş bitince ajanın yeni kaynak kodu ONAYLI sınama kümesiyle geçici kopyada koşar (hüküm koşusu); ajanın bıraktığı hâlle (A) birleştirilir.
/// Dört dal, N5 (deneme gücü), N6 (hiçbir dosya değişmedi), yeni testleri katma teklifi, geçici kopya ve bağlantı (junction) güvenliği.
/// Hepsi sentetik klasörlerde (TestEnv) çalışır; gerçek veri açılmaz.
/// </summary>
public class E2KanitTests
{
    // ------------------------------------------------------------------ düzenek (terminal kancası yolu: ajan başlangıcı ve Stop kancası)
    private static string Hook(TestEnv env, string ev, string stdin)
    {
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run(ev, new StringReader(stdin), o, env.DataDir, _ => null));
        return o.ToString();
    }

    private static string J(object o) => JsonSerializer.Serialize(o);

    private static string StopInput(string session, string cwd, bool active = false)
        => J(new Dictionary<string, object?> { ["session_id"] = session, ["transcript_path"] = "t.jsonl", ["cwd"] = cwd, ["hook_event_name"] = "Stop", ["stop_hook_active"] = active, ["last_assistant_message"] = "Bitti, testler geçti." });

    private sealed record Rig(TestEnv Env, WorkService W, long Pid, string Dir);

    private static Rig NewRig(TestEnv env, string name = "e2-proje")
    {
        var dir = NodeSample.Create(env, name);
        NodeSample.Fix(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, enabled: true);
        return new Rig(env, w, pid, dir);
    }

    /// <summary>Yeni iş: Söz ajan başlangıcında klasörün görüntüsünü alır (iş başı tabanı).</summary>
    private static JobDto Start(Rig r, string tag)
    {
        var job = r.W.CreateJob(r.Pid, "Dosyaları incele: " + tag);
        r.W.PatchJob(job.Id, JobStates.Given);
        Hook(r.Env, "user-prompt-submit", J(new { session_id = "e2-" + tag, cwd = r.Dir, prompt = job.PromptText }));
        Assert.Equal(JobStates.Running, r.W.Job(job.Id).State);
        return job;
    }

    private static string Stop(Rig r, JobDto job, string tag, bool active = false) => Hook(r.Env, "stop", StopInput("e2-" + tag, r.Dir, active));

    /// <summary>İşi bitirir; kanıt çalışmasının ilk ve ikinci (ısrar) kapısını koşar. Dönen: işin son durumu.</summary>
    private static JobDto Finish(Rig r, JobDto job, string tag, bool twice = false)
    {
        Stop(r, job, tag);
        if (twice) Stop(r, job, tag, active: true);
        return r.W.Job(job.Id);
    }

    private static (string? verdict, string? reason, string? summary) Last(Rig r, long jobId)
    {
        using var c = r.Env.Svc.Db.Open();
        return c.Query("SELECT verdict, reason_code, summary FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", x => (x.Str(0), x.Str(1), x.Str(2)), null, ("@j", jobId)).FirstOrDefault();
    }

    private static string Test(Rig r) => Path.Combine(r.Dir, "test", "hesap.test.js");
    private static string Lib(Rig r) => Path.Combine(r.Dir, "hesap.js");
    private const string Broken = "'use strict';\nfunction topla(a, b) { return a - b; }\nfunction carp(a, b) { return a * b; }\nmodule.exports = { topla, carp };\n";
    private const string YeniGecen = "'use strict';\nconst test = require('node:test');\nconst assert = require('node:assert/strict');\nconst { carp } = require('../hesap.js');\ntest('yeni carpma testi', () => { assert.equal(carp(5, 5), 25); });\n";
    private const string YeniKalan = "'use strict';\nconst test = require('node:test');\nconst assert = require('node:assert/strict');\nconst { carp } = require('../hesap.js');\ntest('yeni kalan test', () => { assert.equal(carp(5, 5), 26); });\n";

    private static EvidenceBase.Snapshot? Approved(Rig r) { using var c = r.Env.Svc.Db.Open(); return EvidenceBase.LoadApproved(c, r.Pid); }

    // ================================================================== DAL 1: onaylı geçti + ajan geçti
    [Fact]
    public void Dal1_ajan_testi_zayiflatsa_da_onayli_testler_gecerse_yesil_ve_not_dusulur()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        var job = Start(r, "dal1");
        // Ajan test dosyasını zayıflattı (e1 bunu alarm sayardı): kod doğru, onaylı testler geçiyor. Hüküm onaylı testlerle verilir.
        File.WriteAllText(Test(r), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        var j = Finish(r, job, "dal1");
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.True(j.State == JobStates.Passed, $"{j.State} / {summary}");
        Assert.Equal("passed", verdict);
        Assert.Null(reason);
        Assert.Contains("Yardımcı onayladığın testlere de dokundu. Sonuç senin onayladığın testlerle alındı.", summary);
    }

    [Fact]
    public void Dal1_ajan_testlere_dokunmadiysa_not_yok_ve_ikinci_kosu_yapilmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        var job = Start(r, "dal1b");
        File.WriteAllText(Lib(r), NodeSample.FixedLib + "// küçük değişiklik\n");
        var j = Finish(r, job, "dal1b");
        var (_, reason, summary) = Last(r, job.Id);
        Assert.Equal(JobStates.Passed, j.State);
        Assert.Null(reason);
        Assert.DoesNotContain("Yardımcı onayladığın testlere de dokundu", summary);
        using var c = env.Svc.Db.Open();
        Assert.Equal(0, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind=@k", null, ("@k", ProofStore.VerdictRunEvent)));
    }

    // ================================================================== DAL 2: onaylı geçti + ajan KALDI
    [Fact]
    public void Dal2_onayli_testler_gecip_ajanin_yeni_testi_kalirsa_karar_bekler()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        var job = Start(r, "dal2");
        File.WriteAllText(Path.Combine(r.Dir, "test", "yeni.test.js"), YeniKalan);
        var j = Finish(r, job, "dal2", twice: true);
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.Equal(JobStates.AwaitingDecision, j.State);
        Assert.Equal("unchecked", verdict);
        Assert.Equal(ProofStore.Reasons.AgentTestsFail, reason);
        Assert.Contains("Onayladığın testler geçiyor ama yardımcının eklediği yeni testler geçmiyor.", summary);
        Assert.Contains("yeni kalan test", summary);                                  // geçmeyen testin adı
        var item = env.Svc.Attention.Snapshot().Items.Single(i => i.JobId == job.Id);
        Assert.Equal("accept", item.Kind);
        Assert.Equal(2, item.Priority);
    }

    // ================================================================== DAL 3: onaylı KALDI + ajan geçti
    [Fact]
    public void Dal3_ajan_testi_gevsetip_kodu_bozarsa_onayli_testler_kalir_karar_bekler_kabulle_taban_ilerler()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        var job = Start(r, "dal3");
        File.WriteAllText(Lib(r), Broken);
        File.WriteAllText(Test(r), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));      // ajanın testi artık geçiyor: A yeşil
        var j = Finish(r, job, "dal3", twice: true);
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.Equal(JobStates.AwaitingDecision, j.State);
        Assert.Equal("unchecked", verdict);
        Assert.Equal(ProofStore.Reasons.ApprovedFailed, reason);
        Assert.Contains("Onayladığın 2 testten 1'i yeni kodla geçmiyor.", summary);
        Assert.Contains("toplama iki sayiyi toplar", summary);                          // geçmeyen onaylı testin adı
        Assert.Contains("Yardımcı bu testleri de değiştirmiş, değişiklik gerekli olabilir.", summary);   // e3: yalnız yardımcı bu işte gerçekten değiştirdiyse söylenir
        var p = env.Svc.Db.Open().Let(c => ProofStore.Get(c, j.LastRunId!.Value)!);
        Assert.Contains(p.Diffs!, d => d.Kind == "approved-fail" && d.Lines.Any(l => l.Text.Contains("toplama iki sayiyi toplar")));
        Assert.Contains(p.Diffs!, d => d.Kind == "test" && d.Path == "test/hesap.test.js");                              // kısa fark kartta
        var item = env.Svc.Attention.Snapshot().Items.Single(i => i.JobId == job.Id);
        Assert.Equal(2, item.Priority);

        // Kişi kabul ederse taban ilerler: onaylı küme ajanın şimdiki testlerine döner.
        var kabul = r.W.Accept(job.Id, true);
        Assert.Equal(JobStates.Passed, kabul.State);
        var onayli = Approved(r)!.Suspects.Single(e => e.Path == "test/hesap.test.js");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Test(r)))).ToLowerInvariant(), onayli.Sha);
    }

    // ================================================================== DAL 4: ikisi de kaldı
    [Fact]
    public void Dal4_kod_bozuk_ve_onayli_testler_de_kaliyorsa_kaldi()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        var job = Start(r, "dal4");
        File.WriteAllText(Lib(r), Broken);
        File.WriteAllText(Path.Combine(r.Dir, "test", "yeni.test.js"), YeniGecen);      // ajan sınama dosyasına dokundu (ikinci koşu gerekir)
        Finish(r, job, "dal4");
        var (verdict, reason, _) = Last(r, job.Id);
        Assert.Equal("failed", verdict);
        Assert.Null(reason);                                                             // "Kaldı": karar bekleme değil
    }

    // ================================================================== DAL 5 (e1 yolu korunur): önce test koşuyordu, şimdi hiçbiri
    [Fact]
    public void Test_dosyasi_ve_betik_silinirse_onayli_testler_yeni_kodla_gecse_bile_karar_bekler_Once_N_test_cumlesi_korunur()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        var ilk = Start(r, "d5a");
        Assert.Equal(JobStates.Passed, Finish(r, ilk, "d5a").State);       // önceki kayıt: 2 test geçti
        var job = Start(r, "d5b");
        File.Delete(Test(r));
        var j = Finish(r, job, "d5b", twice: true);
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.Equal(JobStates.AwaitingDecision, j.State);
        Assert.Equal("unchecked", verdict);
        Assert.Equal(ProofStore.Reasons.AgentTestsFail, reason);
        Assert.Contains("Önce 2 test koşuyordu, şimdi hiçbiri koşmadı.", summary);
        Assert.Contains("Onayladığın testler yeni kodla geçiyor", summary);
    }

    // ================================================================== hüküm koşusu yapılamıyorsa: kanıt sayamam
    [Fact]
    public void Gecici_kopya_kurulamazsa_kanit_sayamam_denir_eski_yola_sessizce_dusulmez()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        env.Svc.PutSettings(new JsonObject { ["setting:proof.verdict-copy-max-mb"] = 16 });
        File.WriteAllBytes(Path.Combine(r.Dir, "veri.dat"), new byte[17 * 1024 * 1024]);
        var job = Start(r, "kopya");
        File.WriteAllText(Test(r), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        var j = Finish(r, job, "kopya");
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.NotEqual(JobStates.Passed, j.State);
        Assert.Equal("unchecked", verdict);
        Assert.Equal(ProofStore.Reasons.VerdictUnavailable, reason);
        Assert.Contains("Onayladığın testleri yeniden çalıştıramadım", summary);
        Assert.Contains("Sonucu doğrulayamam", summary);
        Assert.Contains("MB sınırından büyük", summary);
        Assert.False(Directory.Exists(TempTree.ParentDir(env.DataDir)) && Directory.GetDirectories(TempTree.ParentDir(env.DataDir)).Length > 0, "geçici kopya kalmamalı");
    }

    [Fact]
    public void Ikinci_kosu_sure_sinirini_asarsa_kanit_sayamam_denir()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        env.Svc.PutSettings(new JsonObject { ["setting:proof.verdict-run-seconds"] = 10 });
        // Yavaş ama geçen test: ilk koşu (A) yetişir (ayrı bütçe), ikinci koşu 10 sn sınırını aşar.
        File.WriteAllText(Test(r), NodeSample.Tests + "\ntest('yavas', async () => { await new Promise((ok) => setTimeout(ok, 14000)); });\n");
        var job0 = Start(r, "sure0");
        Assert.Equal(JobStates.Passed, Finish(r, job0, "sure0").State);
        var job = Start(r, "sure");
        File.WriteAllText(Test(r), File.ReadAllText(Test(r)) + "// ajan düzenledi\n");
        var j = Finish(r, job, "sure");
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.NotEqual(JobStates.Passed, j.State);
        Assert.Equal("unchecked", verdict);
        Assert.Equal(ProofStore.Reasons.VerdictUnavailable, reason);
        Assert.Contains("Sonucu doğrulayamam", summary);
        var kalan = Directory.Exists(TempTree.ParentDir(env.DataDir)) ? string.Join(" | ", Directory.GetFileSystemEntries(TempTree.ParentDir(env.DataDir), "*", SearchOption.AllDirectories)) : "";
        Assert.True(kalan.Length == 0, "geçici kopya kalmamalı: " + kalan);
    }

    // ================================================================== N6: ajan hiçbir dosyayı değiştirmedi
    [Fact]
    public void N6_ajan_hicbir_dosyayi_degistirmediyse_yesil_bitti_degil_ayri_sakin_etiket()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        var job = Start(r, "n6");
        var j = Finish(r, job, "n6");
        var (verdict, reason, summary) = Last(r, job.Id);
        Assert.Equal(JobStates.Passed, j.State);                              // "biten" sınıfı
        Assert.Equal("passed", verdict);
        Assert.Equal(ProofStore.Reasons.NoChange, reason);
        Assert.StartsWith("Yardımcı hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu.", summary);
        Assert.Equal("Yardımcı hiçbir dosyayı değiştirmedi", ProofStore.VerdictWord(verdict, reason));
        var item = env.Svc.Attention.Snapshot().Items.Single(i => i.JobId == job.Id);
        Assert.Equal("done", item.Kind);
        Assert.Equal("no_change", item.Reason);
        Assert.Equal(4, item.Priority);
        Assert.Contains("hiçbir dosyayı değiştirmedi", item.Sentence);
        Assert.DoesNotContain("deneme sonucuyla bitti", item.Sentence);
    }

    [Fact]
    public void N6_ajan_bir_dosyayi_degistirdiyse_kanitiyla_bitti_der()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        var job = Start(r, "n6b");
        File.WriteAllText(Lib(r), NodeSample.FixedLib + "// not\n");
        Finish(r, job, "n6b");
        var item = env.Svc.Attention.Snapshot().Items.Single(i => i.JobId == job.Id);
        Assert.Equal("passed", item.Reason);
        Assert.Contains("deneme sonucuyla bitti", item.Sentence);
    }

    // ================================================================== yeni testleri onaylı kümeye katma teklifi
    [Fact]
    public void Yeni_test_teklifi_ve_insan_onayi_ajanin_testi_kendiliginden_onayli_kumeye_girmez()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        using (var cs = env.Svc.Db.Open()) Assert.False(SozSettings.Load(cs).Bool("setting:proof.auto-approve-new-tests"));         // varsayılan KAPALI
        var job = Start(r, "teklif");
        File.WriteAllText(Path.Combine(r.Dir, "test", "yeni.test.js"), YeniGecen);
        var j = Finish(r, job, "teklif");
        Assert.Equal(JobStates.Passed, j.State);
        Assert.NotNull(j.TestOffer);
        Assert.Equal(1, j.TestOffer!.Added);
        Assert.Equal("Yardımcı 1 yeni test ekledi. Bunu da onayladığın testlere katayım mı?", j.TestOffer.Sentence);
        Assert.DoesNotContain(Approved(r)!.Suspects, e => e.Path == "test/yeni.test.js");        // kendiliğinden girmedi
        var item = env.Svc.Attention.Snapshot().Items.Single(i => i.JobId == job.Id);
        Assert.Contains(item.Actions, a => a.Id == "approve-tests" && a.Path == $"/api/jobs/{job.Id}/approve-new-tests");

        var sonra = r.W.ApproveNewTests(job.Id);                                                // yalnız insan (CallerGate)
        Assert.Null(sonra.TestOffer);
        Assert.Contains(Approved(r)!.Suspects, e => e.Path == "test/yeni.test.js");
        var ex = Assert.Throws<SozException>(() => r.W.ApproveNewTests(job.Id));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public void Ayar_acikken_yeni_eklenen_test_kendiliginden_katilir_duzenlenen_yine_sorulur()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        env.Svc.PutSettings(new JsonObject { ["setting:proof.auto-approve-new-tests"] = true });
        var job = Start(r, "oto");
        File.WriteAllText(Path.Combine(r.Dir, "test", "yeni.test.js"), YeniGecen);
        File.WriteAllText(Test(r), NodeSample.Tests + "// ek satır\n");
        var j = Finish(r, job, "oto");
        Assert.Equal(JobStates.Passed, j.State);
        Assert.Contains(Approved(r)!.Suspects, e => e.Path == "test/yeni.test.js");                // eklenen kendiliğinden
        Assert.NotNull(j.TestOffer);
        Assert.Equal(0, j.TestOffer!.Added);
        Assert.Equal(1, j.TestOffer.Edited);                                                      // düzenlenen onaylı test hâlâ sorulur
    }

    [Fact]
    public void Onayli_uye_yalniz_insan_eylemiyle_ilerler_gecen_is_onayli_testi_degistirmez()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env);
        var ilk = Approved(r);                                                                   // henüz yok: ilk iş başında alınır
        var job = Start(r, "uye");
        var once = Approved(r)!.Suspects.Single(e => e.Path == "test/hesap.test.js").Sha;
        File.WriteAllText(Test(r), NodeSample.Tests + "// ajan ekledi\n");
        Assert.Equal(JobStates.Passed, Finish(r, job, "uye").State);
        Assert.Equal(once, Approved(r)!.Suspects.Single(e => e.Path == "test/hesap.test.js").Sha);   // zincirleme zayıflatma yok
        Assert.Null(ilk);
    }

    [Fact]
    public void Yeni_test_katma_ucu_ajana_kapali()
    {
        Assert.False(CallerGate.AgentMayWrite("POST", "/api/jobs/5/approve-new-tests"));
    }

    // ================================================================== sınıflama ve hüküm kuralı yardımcıları
    [Theory]
    [InlineData("test/hesap.test.js", true)]
    [InlineData("test/fixtures/beklenen.json", true)]
    [InlineData("lib/assert.js", true)]
    [InlineData("src/matchers.js", true)]
    [InlineData("jest.config.js", true)]
    [InlineData("conftest.py", true)]
    [InlineData("pytest.ini", true)]
    [InlineData("hesap.js", false)]
    [InlineData("src/uygulama.py", false)]
    public void Onayli_kume_uyeligi_sinif_ve_ad_ile_belirlenir_dile_bagli_degildir(string yol, bool uye)
    {
        var cls = EvidenceBase.Classify(yol);
        var e = new EvidenceBase.Entry(yol, 1, 1, "00", cls ?? "n");
        var s = new EvidenceBase.Snapshot(new() { e }, 1, false);
        Assert.Equal(uye, ApprovedSet.IsMember(e, s));
    }

    [Fact]
    public void Paket_dosyasinda_test_alanlari_onayli_hale_cevrilir_bagimliliklar_ajaninki_kalir()
    {
        var onayli = """{ "name": "x", "scripts": { "test": "node --test" }, "dependencies": { "a": "1" } }""";
        var ajan = """{ "name": "x", "scripts": { "test": "node -e 0" }, "dependencies": { "a": "1", "b": "2" } }""";
        var birlesik = JsonNode.Parse(ApprovedSet.MergePackage(onayli, ajan))!;
        Assert.Equal("node --test", (string?)birlesik["scripts"]!["test"]);
        Assert.Equal("2", (string?)birlesik["dependencies"]!["b"]);
        Assert.True(ApprovedSet.PackageTestKeysDiffer(onayli, ajan));
        Assert.False(ApprovedSet.PackageTestKeysDiffer(onayli, onayli.Replace("\"name\": \"x\"", "\"name\": \"y\"")));
    }

    [Fact]
    public void TestFileDiff_artik_hukum_vermez_yalniz_karttaki_ozettir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "tfd");
        var once = EvidenceBase.Scan(dir);
        File.WriteAllText(Path.Combine(dir, "test", "hesap.test.js"), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        var d = EvidenceBase.Compare(once, EvidenceBase.Scan(dir), dir);
        Assert.False(d.Any);                                                                       // alarm yok
        Assert.Contains("test/hesap.test.js", d.ChangedTests);                                     // ama bilgi var
        Assert.Contains(d.FileDiffs, f => f.Kind == "test" && f.Path == "test/hesap.test.js" && f.Sentence.Length > 0);
    }

    [Fact]
    public void Yeni_neden_kodlari_karar_bekletir_ve_etiketleri_sadedir()
    {
        Assert.True(ProofStore.Reasons.IsTampering(ProofStore.Reasons.ApprovedFailed));
        Assert.True(ProofStore.Reasons.IsTampering(ProofStore.Reasons.AgentTestsFail));
        Assert.False(ProofStore.Reasons.IsTampering(ProofStore.Reasons.VerdictUnavailable));      // "verildi" (Kanıtsız): karar değil
        Assert.False(ProofStore.Reasons.IsTampering(ProofStore.Reasons.NoChange));
        Assert.Equal("Onayladığın testler geçmiyor", ProofStore.VerdictWord("unchecked", ProofStore.Reasons.ApprovedFailed));
        Assert.Equal("Yardımcının yeni testleri geçmiyor", ProofStore.VerdictWord("unchecked", ProofStore.Reasons.AgentTestsFail));
    }

    // ================================================================== geçici kopya ve bağlantı güvenliği
    private static string Junction(string link, string target)
    {
        Assert.True(TempTree.MakeJunction(link, target), "bağlantı kurulamadı");
        Assert.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
        return link;
    }

    [Fact]
    public void Gecici_kopya_kaldirilirken_node_modules_baglantisinin_HEDEFI_silinmez()
    {
        using var env = new TestEnv(copySamples: false);
        var hedef = Path.Combine(env.Root, "gercek-node-modules");
        Directory.CreateDirectory(Path.Combine(hedef, "paket", "derin"));
        File.WriteAllText(Path.Combine(hedef, "paket", "index.js"), "module.exports = 1;");
        File.WriteAllText(Path.Combine(hedef, "paket", "derin", "a.txt"), "a");
        File.SetAttributes(Path.Combine(hedef, "paket", "derin", "a.txt"), FileAttributes.ReadOnly);

        var tabanDizin = TempTree.NewBase(env.DataDir, "j1-r1-abc");
        var kopya = Path.Combine(tabanDizin, "p");
        Directory.CreateDirectory(kopya);
        File.WriteAllText(Path.Combine(kopya, "ornek.js"), "x");
        Junction(Path.Combine(kopya, "node_modules"), hedef);

        Assert.True(TempTree.SafeRemove(tabanDizin, TempTree.ParentDir(env.DataDir)));
        Assert.False(Directory.Exists(tabanDizin));                                                // kopya gitti
        Assert.True(File.Exists(Path.Combine(hedef, "paket", "index.js")));                         // HEDEF yerinde
        Assert.True(File.Exists(Path.Combine(hedef, "paket", "derin", "a.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(hedef, "paket", "derin", "a.txt")));
        File.SetAttributes(Path.Combine(hedef, "paket", "derin", "a.txt"), FileAttributes.Normal);
    }

    [Fact]
    public void Yol_denetimi_kopya_dizini_disini_kok_klasoru_isaretsiz_klasoru_reddeder_hicbir_sey_silinmez()
    {
        using var env = new TestEnv(copySamples: false);
        var parent = TempTree.ParentDir(env.DataDir);
        Directory.CreateDirectory(parent);
        var proje = Path.Combine(env.Root, "kullanici-projesi");
        Directory.CreateDirectory(proje);
        File.WriteAllText(Path.Combine(proje, "onemli.txt"), "dokunma");
        File.WriteAllText(Path.Combine(proje, TempTree.Marker), "işaret taklidi");                // işareti olsa bile parent altında değil
        Assert.False(TempTree.SafeRemove(proje, parent));                                           // dışarıda
        Assert.False(TempTree.SafeRemove(parent, parent));                                          // parent'ın kendisi
        Assert.False(TempTree.SafeRemove(Path.Combine(parent, ".."), parent));                      // ../ ile kaçış
        var isaretsiz = Path.Combine(parent, "baska");
        Directory.CreateDirectory(isaretsiz);
        File.WriteAllText(Path.Combine(isaretsiz, "x.txt"), "x");
        Assert.False(TempTree.SafeRemove(isaretsiz, parent));                                       // işaretsiz: Söz'ün kurduğu değil
        Assert.True(File.Exists(Path.Combine(isaretsiz, "x.txt")));
        Assert.Equal("dokunma", File.ReadAllText(Path.Combine(proje, "onemli.txt")));
        Assert.True(TempTree.SafeRemove(Path.Combine(parent, "yok-boyle-bir-klasor"), parent));    // yoksa sorun değil
    }

    private sealed class MemBlobs : IApprovedBlobs
    {
        public readonly Dictionary<string, byte[]> Map = new();
        public bool TryWrite(string sha, string targetPath)
        {
            if (!Map.TryGetValue(sha, out var b)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllBytes(targetPath, b);
            return true;
        }
        public void Dispose() { }
    }

    private static string TreeHash(string root)
    {
        var sb = new StringBuilder();
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(Path.GetRelativePath(root, f)).Append(':').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    [Fact]
    public void Hukum_kosusu_kopyada_onayli_testleri_geri_koyar_kullanicinin_klasorune_dokunmaz_ve_iz_birakmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "hk");
        NodeSample.Fix(dir);
        var nm = Path.Combine(env.Root, "nm-hedef");
        Directory.CreateDirectory(Path.Combine(nm, "p"));
        File.WriteAllText(Path.Combine(nm, "p", "i.js"), "1");
        Junction(Path.Combine(dir, "node_modules"), nm);

        var onayli = EvidenceBase.Scan(dir);
        // Ajan: testi zayıflattı, yeni test ekledi, kodu bozdu.
        File.WriteAllText(Path.Combine(dir, "test", "hesap.test.js"), NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        File.WriteAllText(Path.Combine(dir, "test", "yeni.test.js"), YeniGecen);
        File.WriteAllText(Path.Combine(dir, "hesap.js"), Broken);
        var simdi = EvidenceBase.Scan(dir);
        var onceHash = TreeHash(dir);

        var spec = new JsonObject { ["cmd"] = "node --test", ["expectExit"] = 0 };
        var res = VerdictRun.Execute(new VerdictRunRequest(dir, env.DataDir, 1, 1, onayli, simdi, new[] { new CheckToRun(1, CheckKinds.Command, "Testler", spec, 60) }, 120, 512L << 20, null, null));
        Assert.True(res.Ran, res.Why);
        Assert.True(res.Restored >= 1);
        Assert.Equal(1, res.Removed);                                                              // yardımcının yeni testi çıkarıldı
        var o = Assert.Single(res.Outcomes);
        Assert.Equal(ResultStatus.Failed, o.Status);                                               // onaylı test yeni (bozuk) kodla kalıyor
        Assert.Equal(2, o.TestsTotal);                                                             // yalnız onaylı 2 test koştu
        Assert.Equal(onceHash, TreeHash(dir));                                                     // kullanıcının klasörü bayt bayt aynı
        Assert.True(File.Exists(Path.Combine(nm, "p", "i.js")));
        var parent = TempTree.ParentDir(env.DataDir);
        Assert.True(!Directory.Exists(parent) || Directory.GetFileSystemEntries(parent).Length == 0, "geçici kopya kalmamalı");
        Assert.False(Directory.Exists(Path.Combine(env.DataDir, "kanit", "1-hukum")));
    }

    [Fact]
    public void Onayli_icerik_gecmis_gorüntü_depodan_ya_da_kayittaki_metinden_gelir_ikisi_de_yoksa_hukum_kosulamaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "hk2");
        NodeSample.Fix(dir);
        var onayli = EvidenceBase.Scan(dir);
        var testYolu = "test/hesap.test.js";
        var orijinal = File.ReadAllBytes(Path.Combine(dir, "test", "hesap.test.js"));
        File.WriteAllText(Path.Combine(dir, "test", "hesap.test.js"), "değişti");
        var simdi = EvidenceBase.Scan(dir);
        var spec = new JsonObject { ["cmd"] = "node --test", ["expectExit"] = 0 };
        var checks = new[] { new CheckToRun(1, CheckKinds.Command, "Testler", spec, 60) };

        // Kayıttaki küçük metin silinirse içerik depodan (sha ile) gelir.
        var icerikSiz = onayli with { Suspects = onayli.Suspects.Select(e => e with { Content = null }).ToList() };
        var blobs = new MemBlobs();
        blobs.Map[Convert.ToHexString(SHA256.HashData(orijinal)).ToLowerInvariant()] = orijinal;
        var ok = VerdictRun.Execute(new VerdictRunRequest(dir, env.DataDir, 2, 2, icerikSiz, simdi, checks, 120, 512L << 20, blobs, null));
        Assert.True(ok.Ran, ok.Why);
        Assert.Equal(ResultStatus.Passed, Assert.Single(ok.Outcomes).Status);

        // Ne kayıtta ne depoda varsa koşu yapılamaz: sessizce eski yola düşmez.
        var yok = VerdictRun.Execute(new VerdictRunRequest(dir, env.DataDir, 3, 3, icerikSiz, simdi, checks, 120, 512L << 20, new MemBlobs(), null));
        Assert.False(yok.Ran);
        Assert.Contains(testYolu, yok.Why);
        Assert.Contains("bulamadım", yok.Why);

        // Kayıttaki metin SADIK değilse (özeti uymuyorsa) kullanılmaz: maskeli ya da bozuk metin hükme girmez.
        var bozuk = onayli with { Suspects = onayli.Suspects.Select(e => e.Path == testYolu ? e with { Content = "başka metin" } : e).ToList() };
        var bz = VerdictRun.Execute(new VerdictRunRequest(dir, env.DataDir, 4, 4, bozuk, simdi, checks, 120, 512L << 20, new MemBlobs(), null));
        Assert.False(bz.Ran);
    }

    [Fact]
    public void Geri_alma_deposu_blobu_sha_ile_verir_depo_yoksa_hicbir_sey_olusturulmaz()
    {
        using var env = new TestEnv(copySamples: false);
        Assert.Null(DepotBlobs.TryOpen(env.DataDir));
        Assert.False(Directory.Exists(Path.Combine(env.DataDir, "geri")));                          // açmakla depo yaratılmadı

        var dir = NodeSample.Create(env, "depo");
        var depoDizin = Path.Combine(env.Root, "depo-" + Guid.NewGuid().ToString("N")[..6]);
        using var depo = new GeriDepo(depoDizin, new RestoreOptions { Fsync = false });
        using var eng = new SnapshotEngine(depo);
        var s = eng.Take(new SnapshotRequest(dir, "before_run", "p1", "j1"));
        Assert.True(s.Ok);
        var rec = eng.Resolve(s.Id)["test/hesap.test.js"];
        using var blobs = DepotBlobs.Wrap(depo);
        var hedef = Path.Combine(env.Root, "cikti", "hesap.test.js");
        Assert.True(blobs.TryWrite(rec.Blob!, hedef));
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "test", "hesap.test.js")), File.ReadAllText(hedef));
        Assert.False(blobs.TryWrite(new string('0', 64), Path.Combine(env.Root, "cikti", "yok.txt")));
    }

    // ================================================================== N5: deneme gücü
    [Theory]
    [InlineData("npm test || true", true)]
    [InlineData("node --test; exit 0", true)]
    [InlineData("node --test || echo ok", true)]
    [InlineData("node --test && exit 0", true)]
    [InlineData("node --test || exit /b 0", true)]
    [InlineData("echo tamam", true)]
    [InlineData("true", true)]
    [InlineData("node --test || (exit 0)", true)]
    [InlineData("node --test", false)]
    [InlineData("npm test", false)]
    [InlineData("pytest -q", false)]
    [InlineData("node --test || exit 1", false)]
    public void Her_zaman_basarili_komut_kalıbı_taninir(string cmd, bool beklenen)
    {
        Assert.Equal(beklenen, AgentTestStrength.AlwaysSucceeds(cmd, null));
    }

    [Fact]
    public void Komutun_cagirdigi_package_betigi_her_zaman_basariliysa_da_taninir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "hep");
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "x", "scripts": { "test": "node --test || true" } }""");
        Assert.True(AgentTestStrength.AlwaysSucceeds("npm test", dir));
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "x", "scripts": { "test": "node --test" } }""");
        Assert.False(AgentTestStrength.AlwaysSucceeds("npm test", dir));
    }

    [Fact]
    public void Her_zaman_basarili_denemeyi_kisi_kabul_edemez_sunucu_reddeder()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "n5red");
        long chkId; using (var cc = env.Svc.Db.Open()) chkId = CheckStore.Insert(cc, null, r.Pid, null, "command", "Ajanın yazdığı deneme", new JsonObject { ["cmd"] = "npm test || true", ["expectExit"] = 0 }, 60, false, CheckStore.AgentTestOrigin);
        var ex = Assert.Throws<SozException>(() => r.W.PatchCheck(chkId, enabled: true));
        Assert.Equal(409, ex.Status);
        Assert.Equal("Bu komut kod bozukken de başarılı bitiyor. Deneme sayamam.", ex.Message);
        using var c = env.Svc.Db.Open();
        Assert.False(CheckStore.Get(c, chkId)!.Enabled);
    }

    private sealed class GucRig : IDisposable
    {
        public readonly TestEnv Env; public readonly string Dir; public readonly GeriDepo Depo; public readonly SnapshotEngine Eng; public readonly long SnapId;
        public GucRig(TestEnv env, Action<string> oncekiHal)
        {
            Env = env;
            Dir = Path.Combine(env.Root, "guc-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Dir);
            oncekiHal(Dir);
            Depo = new GeriDepo(Path.Combine(env.Root, "guc-depo-" + Guid.NewGuid().ToString("N")[..6]), new RestoreOptions { Fsync = false });
            Eng = new SnapshotEngine(Depo);
            var s = Eng.Take(new SnapshotRequest(Dir, "before_run", "p1", "j1"));
            Assert.True(s.Ok);
            SnapId = s.Id;
        }
        public StrengthResult Olc(string cmd)
        {
            using var blobs = DepotBlobs.Wrap(Depo);
            return AgentTestStrength.Measure(Dir, Env.DataDir, 9, cmd, Eng.Resolve(SnapId), blobs, EvidenceBase.Scan(Dir), 512L << 20, 60);
        }
        public void Dispose() { Eng.Dispose(); }
    }

    [Fact]
    public void N5_guclu_deneme_is_oncesi_kodla_kaliyor_sonrasi_geciyor()
    {
        using var env = new TestEnv(copySamples: false);
        using var g = new GucRig(env, d =>
        {
            File.WriteAllText(Path.Combine(d, "package.json"), NodeSample.Package);
            File.WriteAllText(Path.Combine(d, "hesap.js"), NodeSample.BrokenLib);
        });
        // Ajan: kodu düzeltti ve deneme yazdı.
        Directory.CreateDirectory(Path.Combine(g.Dir, "test"));
        File.WriteAllText(Path.Combine(g.Dir, "hesap.js"), NodeSample.FixedLib);
        File.WriteAllText(Path.Combine(g.Dir, "test", "hesap.test.js"), NodeSample.Tests);
        var r = g.Olc("npm test");
        Assert.Equal(AgentTestStrength.Strong, r.Kind);
        Assert.Equal("evet", r.DefaultAnswer);
        Assert.False(r.Rejected);
        Assert.Equal(NodeSample.FixedLib, File.ReadAllText(Path.Combine(g.Dir, "hesap.js")));          // ölçüm gerçek klasörü bozmadı
        var parent = TempTree.ParentDir(env.DataDir);
        Assert.True(!Directory.Exists(parent) || Directory.GetFileSystemEntries(parent).Length == 0, "geçici kopya kalmamalı");
    }

    [Fact]
    public void N5_zayif_deneme_is_oncesi_de_geciyordu_bir_sey_kanitlamaz_varsayilan_cevap_hayir()
    {
        using var env = new TestEnv(copySamples: false);
        using var g = new GucRig(env, d =>
        {
            File.WriteAllText(Path.Combine(d, "package.json"), NodeSample.Package);
            File.WriteAllText(Path.Combine(d, "hesap.js"), NodeSample.FixedLib);                       // kod zaten doğru
        });
        Directory.CreateDirectory(Path.Combine(g.Dir, "test"));
        File.WriteAllText(Path.Combine(g.Dir, "test", "hesap.test.js"), NodeSample.Tests);             // ajanın "denemesi" zaten geçen şeyi sınıyor
        var r = g.Olc("npm test");
        Assert.Equal(AgentTestStrength.Weak, r.Kind);
        Assert.Equal("hayir", r.DefaultAnswer);
        Assert.Equal("Bu deneme iş yapılmadan önce de geçiyordu. Bir şey kanıtlamaz.", r.Sentence);
        Assert.False(r.Rejected);                                                                     // kabul edilebilir, ama varsayılan Hayır
    }

    [Fact]
    public void N5_komut_her_zaman_basariliysa_olcum_yapilmadan_reddedilir_olculemezse_uydurma_yok()
    {
        using var env = new TestEnv(copySamples: false);
        using var g = new GucRig(env, d => File.WriteAllText(Path.Combine(d, "package.json"), NodeSample.Package));
        var red = g.Olc("npm test || true");
        Assert.Equal(AgentTestStrength.Always, red.Kind);
        Assert.True(red.Rejected);
        Assert.Equal("hayir", red.DefaultAnswer);
        Assert.Equal("Bu komut kod bozukken de başarılı bitiyor. Deneme sayamam.", red.Sentence);

        // Ölçülemeyen durumda (iş öncesi görüntü dosyasının kopyası depoda yok) "bilmiyorum" der, uydurmaz.
        File.WriteAllText(Path.Combine(g.Dir, "package.json"), NodeSample.Package.Replace("1.0.0", "2.0.0"));
        File.WriteAllText(Path.Combine(g.Dir, "hesap.js"), "// başka\n");
        using var bos = new MemBlobs();
        var bilinmez = AgentTestStrength.Measure(g.Dir, env.DataDir, 10, "npm test", g.Eng.Resolve(g.SnapId), bos, EvidenceBase.Scan(g.Dir), 512L << 20, 60);
        Assert.Equal(AgentTestStrength.Weak, bilinmez.Kind);                                                  // e3: ölçülemeyen zayıf sayılır, "unknown" üretilmez
        Assert.Contains("ölçemedim", bilinmez.Sentence);
        Assert.Equal("hayir", bilinmez.DefaultAnswer);
    }

    // ================================================================== sahte ajan yolu (Söz'ün kendi başlattığı iş): öneri gücü karta yazılır
    [Fact]
    public void Onerilen_deneme_icin_guc_ve_varsayilan_cevap_is_kartinda_gorunur()
    {
        using var env = new TestEnv(copySamples: false);
        var r = NewRig(env, "n5kart");
        var job = r.W.CreateJob(r.Pid, "deneme yaz", writeCheckFirst: true);
        using var c = env.Svc.Db.Open();
        CheckStore.Insert(c, null, r.Pid, null, "command", "Ajanın yazdığı deneme", new JsonObject
        {
            ["cmd"] = "npm test", ["expectExit"] = 0, ["suggestedByJob"] = job.Id, ["strength"] = "weak", ["strengthNote"] = AgentTestStrength.WeakSentence, ["defaultAnswer"] = "hayir", ["rejected"] = false,
        }, 60, false, CheckStore.AgentTestOrigin);
        var s = Assert.Single(r.W.Job(job.Id).SuggestedChecks!);
        Assert.Equal("weak", s.Strength);
        Assert.Equal("hayir", s.DefaultAnswer);
        Assert.False(s.Rejected);
        Assert.Contains("Bu deneme iş yapılmadan önce de geçiyordu. Bir şey kanıtlamaz.", s.Sentence);
    }
}

internal static class E2Ext
{
    public static T Let<T>(this Microsoft.Data.Sqlite.SqliteConnection c, Func<Microsoft.Data.Sqlite.SqliteConnection, T> f) { using (c) return f(c); }
}
