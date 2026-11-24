using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Run;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// e2 (C7, C9, C13): yardımcı "iş yaptım, testler geçti" deyip klasörde hiçbir şey değişmediyse sonuç "geçti" OLAMAZ (kanıtsız, neden no_work);
/// yalnız okuma ya da soru türü işte aynı klasör doğrudur (geçti, no_change). Ayrıca başlatma hatası sade cümle, panel düzeni kaydı.
/// </summary>
public class E2IsYapilmadiTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TestEnv Env = new(copySamples: false);
        public readonly string Dir;
        public readonly WorkService Work;
        public readonly long ProjectId;
        public Rig(bool withTests = true)
        {
            if (withTests) Dir = NodeSample.Create(Env, "e2-proje");
            else
            {
                Dir = Path.Combine(Env.Root, "e2-bos");
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path.Combine(Dir, "notlar.txt"), "merhaba\n");
            }
            Work = new WorkService(Env.Svc.Db);
            ProjectId = Work.EnsureProjectByPath(Dir);
        }
        public void Dispose() => Env.Dispose();
    }

    private static string StopInput(string session, string cwd)
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["session_id"] = session, ["transcript_path"] = "t.jsonl", ["cwd"] = cwd, ["hook_event_name"] = "Stop", ["stop_hook_active"] = false, ["last_assistant_message"] = "Bitti." });

    private static void Line(Rig r, long jobId, string text, string? detail = null)
    {
        using var c = r.Env.Svc.Db.Open();
        c.Exec("INSERT INTO run_lines(run_id, job_id, at, kind, text, detail) VALUES(NULL,@j,@a,'progress',@t,@d)", null, ("@j", jobId), ("@a", Time.NowIso()), ("@t", text), ("@d", detail));
    }

    /// <summary>İş metni verilir, yardımcı (varsa) n dosya değiştirdim der, klasörde change çalışır, Stop kancası kapıyı koşar.</summary>
    private static (string verdict, string reason, string summary, string state) Judge(Rig r, string tag, string jobText, int? claimedFiles, Action? change, bool tests = true)
    {
        if (tests)
        {
            NodeSample.Fix(r.Dir);
            r.Work.AddCheck(r.ProjectId, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, enabled: true);
        }
        var job = r.Work.CreateJob(r.ProjectId, jobText);
        r.Work.PatchJob(job.Id, JobStates.Given);
        HookHandler.Run("user-prompt-submit", new StringReader(JsonSerializer.Serialize(new { session_id = "e2-" + tag, cwd = r.Dir, prompt = job.PromptText })), new StringWriter(), r.Env.DataDir, _ => null);
        if (claimedFiles is { } n && n > 0)
            Line(r, job.Id, n == 1 ? "hesap.js dosyasını değiştirdi" : $"{n} dosyayı değiştirdi", n >= 2 ? "değişen: hesap.js, notlar.txt" : null);
        else if (claimedFiles == 0) Line(r, job.Id, "düşünüyor");
        change?.Invoke();
        HookHandler.Run("stop", new StringReader(StopInput("e2-" + tag, r.Dir)), new StringWriter(), r.Env.DataDir, _ => null);
        using var c = r.Env.Svc.Db.Open();
        var row = c.Query("SELECT verdict, COALESCE(reason_code,''), summary FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", x => (x.GetString(0), x.GetString(1), x.GetString(2)), null, ("@j", job.Id)).First();
        var state = c.Scalar("SELECT state FROM jobs WHERE id=@j", null, ("@j", job.Id)) as string ?? "";
        return (row.Item1, row.Item2, row.Item3, state);
    }

    private const string Fix = "Giriş sayfasındaki yazım hatasını düzelt";

    // ---- C7: altı durum ----

    [Fact]
    public void C7_1_yardimci_iddia_etti_klasorde_degisiklik_yok_gecti_degil_is_yapilmamis()
    {
        using var r = new Rig();
        var (verdict, reason, summary, state) = Judge(r, "a", Fix, 2, null);
        Assert.Equal("unchecked", verdict);
        Assert.Equal(ProofStore.Reasons.NoWork, reason);
        Assert.NotEqual(JobStates.Passed, state);
        Assert.StartsWith("Yardımcı iş yaptığını söyledi ama klasörde hiçbir değişiklik yok. Bu iş yapılmamış görünüyor.", summary);
        Assert.Contains("Bu bir \"geçti\" değildir.", summary);
        Assert.Equal("iş yapılmamış görünüyor", ProofStore.VerdictWord(verdict, reason));
    }

    [Fact]
    public void C7_2_yardimci_bir_sey_iddia_etmedi_klasorde_degisiklik_yok_gecti_degil()
    {
        using var r = new Rig();
        var (verdict, reason, summary, state) = Judge(r, "b", Fix, 0, null);
        Assert.Equal("unchecked", verdict);
        Assert.Equal(ProofStore.Reasons.NoWork, reason);
        Assert.NotEqual(JobStates.Passed, state);
        Assert.StartsWith("Klasörde hiçbir değişiklik yok. İş yapılmamış görünüyor.", summary);
        Assert.DoesNotContain("söyledi ama", summary);
    }

    [Fact]
    public void C7_3_degisiklik_var_testler_geciyor_gecti()
    {
        using var r = new Rig();
        var (verdict, reason, summary, state) = Judge(r, "c", Fix, 2, () => File.WriteAllText(Path.Combine(r.Dir, "hesap.js"), NodeSample.FixedLib + "// düzeltildi\n"));
        Assert.Equal("passed", verdict);
        Assert.NotEqual(ProofStore.Reasons.NoWork, reason);
        Assert.NotEqual(ProofStore.Reasons.NoChange, reason);
        Assert.Equal(JobStates.Passed, state);
        Assert.DoesNotContain("iş yapılmamış", summary);
    }

    [Fact]
    public void C7_4_degisiklik_var_testler_kaliyor_kaldi()
    {
        using var r = new Rig();
        var (verdict, _, summary, _) = Judge(r, "d", Fix, 1, () => File.WriteAllText(Path.Combine(r.Dir, "hesap.js"), NodeSample.BrokenLib + "// bozuldu\n"));
        Assert.Equal("failed", verdict);
        Assert.DoesNotContain("iş yapılmamış", summary);
    }

    [Theory]
    [InlineData("hesap.js dosyasını incele ve ne yaptığını açıkla")]
    [InlineData("Bu projede kaç test var?")]
    [InlineData("Testleri çalıştır ve sonucu söyle")]
    public void C7_5_salt_okuma_isinde_degisiklik_olmamasi_dogru_gecti_no_change(string text)
    {
        using var r = new Rig();
        var (verdict, reason, summary, state) = Judge(r, "e" + Math.Abs(text.GetHashCode()), text, 0, null);
        Assert.Equal("passed", verdict);
        Assert.Equal(ProofStore.Reasons.NoChange, reason);
        Assert.Equal(JobStates.Passed, state);
        Assert.StartsWith("Yardımcı hiçbir dosyayı değiştirmedi.", summary);
        Assert.DoesNotContain("iş yapılmamış", summary);
    }

    [Fact]
    public void C7_5b_arastirma_turu_is_metni_ne_olursa_olsun_salt_okuma()
    {
        Assert.Equal(ChangeExpectation.Kind.ReadOnly, ChangeExpectation.Classify("Rakipleri düzelt", "arastirma"));
    }

    [Fact]
    public void C7_6_testi_olmayan_projede_hukum_kanitsiz_kalir_ve_degisiklik_yok_cumlesi_eklenir()
    {
        using var r = new Rig(withTests: false);
        var (verdict, reason, summary, state) = Judge(r, "f", Fix, 2, null, tests: false);
        Assert.Equal("unchecked", verdict);
        Assert.Equal(ProofStore.Reasons.NoChecks, reason);
        Assert.NotEqual(JobStates.Passed, state);
        Assert.StartsWith("Yardımcı iş yaptığını söyledi ama klasörde hiçbir değişiklik yok. Bu iş yapılmamış görünüyor.", summary);
        Assert.Contains("denenecek açık bir kontrol yok", summary);
    }

    [Fact]
    public void C7_7_isin_degisiklik_isteyip_istemedigi_anlasilamazsa_gecti_denmez()
    {
        using var r2 = new Rig();
        var (v2, rs2, s2, st2) = Judge(r2, "h", "Müşteri listesi işi", 0, null);
        Assert.Equal("unchecked", v2);
        Assert.Equal(ProofStore.Reasons.NoWork, rs2);
        Assert.NotEqual(JobStates.Passed, st2);
        Assert.Contains("bilemedim", s2);
        Assert.StartsWith("Klasörde hiçbir değişiklik yok.", s2);
    }

    [Theory]
    [InlineData("Giriş sayfasındaki yazım hatasını düzelt", ChangeExpectation.Kind.Expected)]
    [InlineData("Add a dark mode toggle", ChangeExpectation.Kind.Expected)]
    [InlineData("Önce incele sonra düzelt", ChangeExpectation.Kind.Expected)]
    [InlineData("Menü renklerini değiştir", ChangeExpectation.Kind.Expected)]
    [InlineData("Bana bir test yaz", ChangeExpectation.Kind.Expected)]
    [InlineData("Bu klasörde ne var, özetle", ChangeExpectation.Kind.ReadOnly)]
    [InlineData("Explain how the login works", ChangeExpectation.Kind.ReadOnly)]
    [InlineData("Neden test kalıyor?", ChangeExpectation.Kind.ReadOnly)]
    [InlineData("Raporu oku, açıkla", ChangeExpectation.Kind.ReadOnly)]
    [InlineData("Müşteri listesi işi", ChangeExpectation.Kind.Unknown)]
    [InlineData("Rapor yaz ve özetle", ChangeExpectation.Kind.Unknown)]
    [InlineData("", ChangeExpectation.Kind.Unknown)]
    public void C7_siniflandirici_bilinen_ornekler(string text, ChangeExpectation.Kind expected)
        => Assert.Equal(expected, ChangeExpectation.Classify(text));

    // ---- C9: başlatma hatası ----

    [Fact]
    public void C9_baslatma_hatasi_sade_turkce_tek_cumle_ham_hata_cumlede_yok()
    {
        var notFound = new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified");
        var s = StartFailure.Sentence("Claude Code", notFound);
        Assert.Equal("Claude Code bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et.", s);
        var bad = new System.ComponentModel.Win32Exception(193, "An error occurred trying to start process 'x' with working directory 'y'. The specified executable is not a valid application");
        var s2 = StartFailure.Sentence("Claude Code", bad);
        Assert.Equal("Claude Code açılamadı. Ayarlar'dan yolunu kontrol et.", s2);
        Assert.DoesNotContain("process", s2);
    }

    [Fact]
    public void C9_uzantisi_tanınmayan_dosya_bulundu_sayilmaz_hazir_denmez()
    {
        using var env = new TestEnv(copySamples: false);
        var txt = Path.Combine(env.Root, "claude.txt");
        File.WriteAllText(txt, "ben yardımcı değilim");
        var res = ClaudeLocator.ResolveDetailed(txt);
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(res.Cmd);
            Assert.Equal("Claude Code bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et.", res.Error);
        }
        var none = ClaudeLocator.ResolveDetailed(Path.Combine(env.Root, "yok", "claude.exe"));
        Assert.Null(none.Cmd);
        Assert.Equal("Claude Code bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et.", none.Error);
    }

    [Fact]
    public void C9_yanlis_yol_isi_baslatilamadi_yapar_ve_cumle_yardimcinin_sozu_alanina_girmez()
    {
        using var env = new TestEnv(copySamples: false);
        var work = new WorkService(env.Svc.Db);
        var dir = Path.Combine(env.Root, "c9-proje"); Directory.CreateDirectory(dir);
        var pid = work.EnsureProjectByPath(dir);
        var job = work.CreateJob(pid, "Bir şeyi düzelt");
        using (var c = env.Svc.Db.Open())
            c.Exec("UPDATE jobs SET state='baslatilamadi', start_error=@e, finished_at=@n WHERE id=@j", null,
                ("@e", "Claude Code bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et."), ("@n", Time.NowIso()), ("@j", job.Id));
        var item = env.Svc.Attention.Snapshot().Items.Single(i => i.JobId == job.Id);
        Assert.Equal("start_failed", item.Reason);
        Assert.Null(item.Detail);
        Assert.Equal("Claude Code bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et.", item.SozNotu);
    }

    // ---- C13: panel düzeni ----

    [Fact]
    public void C13_navTabs_dizi_olarak_gelirse_metne_cevrilip_kaydolur_gecersiz_girdi_anlasilir_hata_verir()
    {
        using var env = new TestEnv(copySamples: false);
        var back = env.Svc.PutSettings(new JsonObject { ["navTabs"] = new JsonArray("suan", "projeler", "bekci", "ayarlar") });
        Assert.Equal("suan,projeler,bekci,ayarlar", (string?)back["navTabs"]);
        var back2 = env.Svc.PutSettings(new JsonObject { ["navTabs"] = "suan,ayarlar" });
        Assert.Equal("suan,ayarlar", (string?)back2["navTabs"]);
        var e = Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["navTabs"] = new JsonObject { ["x"] = 1 } }));
        Assert.Equal(400, e.Status);
        Assert.Contains("sekme adlarının listesi", e.Message);
        var e2 = Assert.Throws<SozException>(() => env.Svc.PutSettings(new JsonObject { ["navTabs"] = new JsonArray("a,b", 3) }));
        Assert.Equal(400, e2.Status);
    }
}
