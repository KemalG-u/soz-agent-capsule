using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Restore;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Geri alma, uçtan uca (Dalga 2, şerit B): iş ver, sahte ajan dosyaları bozsun (test\sahte-geri-ajani\sahte-claude.mjs), kartta koruma düzeyi,
/// önizle, geri al, "geri almayı geri al", olaylar, ömür ve API. Gerçek claude/codex çalışmaz; hepsi urun\_gecici\testler\ altında sentetik klasörlerde.
/// </summary>
public class GeriAlmaEntegrasyonTests
{
    private static string FakePath => Path.Combine(TestEnv.RepoRoot, "test", "sahte-geri-ajani", "sahte-claude.mjs");
    private static string SozExeStandIn => typeof(RunManager).Assembly.Location;

    private sealed class Rig : IDisposable
    {
        public TestEnv Env = new(copySamples: false);
        public WorkService W;
        public RunManager Rm;
        public string ProjectDir;
        public long ProjectId;

        public Rig(Action<SqliteConnectionLike>? once = null)
        {
            ProjectDir = Path.Combine(Env.Root, "proje klasoru");
            Directory.CreateDirectory(Path.Combine(ProjectDir, "src", "derin"));
            File.WriteAllText(Path.Combine(ProjectDir, "hesap.js"), "function topla(a, b) { return a + b }\n");
            File.WriteAllText(Path.Combine(ProjectDir, "notlar.txt"), "önemli notlar\n");
            File.WriteAllText(Path.Combine(ProjectDir, "src", "a.js"), "export const a = 1\n");
            File.WriteAllText(Path.Combine(ProjectDir, "src", "derin", "b.js"), "export const b = 2\n");
            File.WriteAllBytes(Path.Combine(ProjectDir, "buyuk.bin"), new byte[200_000]);
            Directory.CreateDirectory(Path.Combine(ProjectDir, "bos-klasor"));
            W = new WorkService(Env.Svc.Db);
            ProjectId = W.EnsureProjectByPath(ProjectDir);
            Env.Svc.PutSettings(new JsonObject { ["claudePath"] = FakePath, ["maxParallelRuns"] = 3 });
            once?.Invoke(new SqliteConnectionLike(Env));
            Rm = new RunManager(Env.Svc.Db) { SozExePath = SozExeStandIn };
        }

        public JobDto Job(string text) => W.CreateJob(ProjectId, text);

        public async Task<RunDto> Finish(RunDto? run)
        {
            Assert.NotNull(run);
            await Rm.WaitAsync(run!.Id).WaitAsync(TimeSpan.FromSeconds(60));
            using var c = Env.Svc.Db.Open();
            return RunStore.Get(c, run.Id)!;
        }

        public string Ozet() => GeriAlmaMotorTests.GeriRig.Ozetle(ProjectDir);

        public List<string> Olaylar(string onEk)
        {
            using var c = Env.Svc.Db.Open();
            return c.Query("SELECT kind FROM event_log WHERE kind LIKE @k ORDER BY seq", r => r.GetString(0), null, ("@k", onEk + "%"));
        }

        public void Dispose()
        {
            try { Rm.Dispose(); } catch (Exception) { }
            Env.Dispose();
        }
    }

    /// <summary>Ayar yazma kolaylığı (RunManager kurulmadan önce, geri alma ayarları okunacağı için).</summary>
    private sealed class SqliteConnectionLike
    {
        private readonly TestEnv _env;
        public SqliteConnectionLike(TestEnv env) { _env = env; }
        public void Ayar(string key, string value)
        {
            using var c = _env.Svc.Db.Open();
            SozSettings.Save(c, new System.Text.Json.Nodes.JsonObject { [key] = System.Text.Json.Nodes.JsonNode.Parse(value) });
        }
    }

    // ================================================================== uçtan uca

    [Fact]
    public async Task E2E_Is_ver_ajan_bozar_onizle_geri_al_klasor_birebir_ayni_sonra_geri_almayi_geri_al()
    {
        using var rig = new Rig();
        var onceki = rig.Ozet();
        var job = rig.Job("Hesabı düzelt [[sahte:boz]]");
        var run = await rig.Finish(rig.Rm.Start(job.Id));
        Assert.Equal(RunStatus.Done, run.Status);

        // kart alanları dolu
        Assert.False(string.IsNullOrEmpty(run.BeforeSnapshotUid));
        Assert.False(string.IsNullOrEmpty(run.AfterSnapshotUid));
        Assert.NotEqual(run.BeforeSnapshotUid, run.AfterSnapshotUid);
        Assert.Equal(ProtectionLevel.Full, run.Protection);
        var prot = rig.Rm.Restore.ProtectionOf(job.Id);
        Assert.Equal(ProtectionLevel.Full, prot.Level);
        Assert.True(prot.CanUndo);
        Assert.Contains("geri alınabilir", prot.Text);
        Assert.Contains("paket kurulumları", prot.Limits); // geri alınamayanlar her zaman açıkça yazılı

        // ajan bozdu
        var ajanHali = rig.Ozet();
        Assert.NotEqual(onceki, ajanHali);
        Assert.Contains("AJAN BOZDU", File.ReadAllText(Path.Combine(rig.ProjectDir, "hesap.js")));
        Assert.False(File.Exists(Path.Combine(rig.ProjectDir, "notlar.txt")));

        // önizleme: hiçbir dosyaya dokunmaz
        var on = rig.Rm.Restore.Preview(job.Id, new UndoRequest());
        Assert.Equal("previewed", on.Status);
        Assert.Equal(ajanHali, rig.Ozet());
        Assert.Contains(on.Items, i => i.Path == "notlar.txt" && i.Action == "geri-yukle");
        Assert.Contains(on.Items, i => i.Path == "yeni.txt" && i.Action == "sil");
        Assert.Contains(on.Items, i => i.Path == "src/a.js");
        Assert.Contains(on.Items, i => i.Action == "klasor-sil" && i.Path == "yeni-bos");
        Assert.Contains(on.Items, i => i.Action == "klasor-olustur" && i.Path == "bos-klasor");
        Assert.Empty(on.Conflicts);

        // uygula: klasör BİREBİR eski hâlinde (yol kümesi + özet)
        var ap = rig.Rm.Restore.Apply(job.Id, new UndoRequest(PreviewUid: on.UndoUid));
        Assert.Equal("applied", ap.Status);
        Assert.Equal(onceki, rig.Ozet());
        Assert.Equal(on.UndoUid, ap.UndoUid); // önizlemenin kaydı kullanıldı
        Assert.Empty(Directory.GetFiles(rig.ProjectDir, "*.soz-geri", SearchOption.AllDirectories));

        // geri almayı geri al: ajanın son hâline döner
        var rv = rig.Rm.Restore.Revert(ap.UndoUid!);
        Assert.Equal("reverted", rv.Status);
        Assert.Equal(ajanHali, rig.Ozet());
        Assert.Throws<SozException>(() => rig.Rm.Restore.Revert(ap.UndoUid!)); // ikinci kez geri alınamaz

        // olaylar (ekle-yalnız kayıt): iki nokta + önizleme + uygulama + geri alma
        Assert.Equal(2, rig.Olaylar("snapshot.taken").Count);
        Assert.Single(rig.Olaylar("undo.previewed"));
        Assert.Single(rig.Olaylar("undo.applied"));
        Assert.Single(rig.Olaylar("undo.reverted"));
        Assert.Empty(rig.Olaylar("snapshot.failed"));

        // geçmiş
        var gecmis = rig.Rm.Restore.History(job.Id);
        Assert.Single(gecmis);
        Assert.Equal("reverted", gecmis[0].Status);
    }

    [Fact]
    public async Task Kismi_geri_alma_yalniz_secilen_dosyayi_doner()
    {
        using var rig = new Rig();
        var job = rig.Job("boz [[sahte:boz]]");
        await rig.Finish(rig.Rm.Start(job.Id));
        var ap = rig.Rm.Restore.Apply(job.Id, new UndoRequest(Paths: new[] { "notlar.txt", "src/" }));
        Assert.Equal("applied", ap.Status);
        Assert.True(File.Exists(Path.Combine(rig.ProjectDir, "notlar.txt")));
        Assert.True(File.Exists(Path.Combine(rig.ProjectDir, "src", "derin", "b.js")));
        Assert.Contains("AJAN BOZDU", File.ReadAllText(Path.Combine(rig.ProjectDir, "hesap.js"))); // seçilmedi
        Assert.True(File.Exists(Path.Combine(rig.ProjectDir, "yeni.txt")));
    }

    [Fact]
    public async Task Kisi_ajandan_sonra_dokunduysa_cakisma_listelenir_ve_zorla_ile_gelir()
    {
        using var rig = new Rig();
        var job = rig.Job("boz [[sahte:boz]]");
        await rig.Finish(rig.Rm.Start(job.Id));
        File.WriteAllText(Path.Combine(rig.ProjectDir, "hesap.js"), "kişi sonradan yazdı");
        var ap = rig.Rm.Restore.Apply(job.Id, new UndoRequest());
        Assert.Contains(ap.Conflicts, c => c.Path == "hesap.js");
        Assert.Contains("Sen ya da başka bir program", ap.Conflicts.First(c => c.Path == "hesap.js").Text);
        Assert.Equal("kişi sonradan yazdı", File.ReadAllText(Path.Combine(rig.ProjectDir, "hesap.js")));
        var z = rig.Rm.Restore.Apply(job.Id, new UndoRequest(Force: true));
        Assert.Empty(z.Conflicts);
        Assert.StartsWith("function topla", File.ReadAllText(Path.Combine(rig.ProjectDir, "hesap.js")));
    }

    [Fact]
    public async Task Uygulanan_geri_alma_kalan_isin_Sira_bende_satirini_dusurur_onizleme_dusurmez()
    {
        using var rig = new Rig();
        var job = rig.Job("boz [[sahte:boz]]");
        await rig.Finish(rig.Rm.Start(job.Id));
        using (var c = rig.Env.Svc.Db.Open())
        {
            JobStore.SetState(c, job.Id, JobStates.Failed, rig.ProjectId); // "kaldı": görülmemiş
            Assert.Null(c.Scalar("SELECT seen_at FROM jobs WHERE id=@i", null, ("@i", job.Id)));
        }
        rig.Rm.Restore.Preview(job.Id, new UndoRequest());
        using (var c = rig.Env.Svc.Db.Open()) Assert.Null(c.Scalar("SELECT seen_at FROM jobs WHERE id=@i", null, ("@i", job.Id)));
        var ap = rig.Rm.Restore.Apply(job.Id, new UndoRequest());
        Assert.Equal("applied", ap.Status);
        using (var c = rig.Env.Svc.Db.Open()) Assert.NotNull(c.Scalar("SELECT seen_at FROM jobs WHERE id=@i", null, ("@i", job.Id)));
        Assert.DoesNotContain(rig.W.Now().WaitingJobs!, j => j.Id == job.Id);
    }

    // ================================================================== kanca: görüntü alınamazsa koşu durmaz

    [Fact]
    public async Task Goruntu_alinamazsa_kosu_durmaz_koruma_yok_olur_kart_alani_dolar()
    {
        using var rig = new Rig(a => a.Ayar("setting:restore.enabled", "false"));
        var job = rig.Job("boz [[sahte:boz]]");
        var run = await rig.Finish(rig.Rm.Start(job.Id));
        Assert.Equal(RunStatus.Done, run.Status); // ajan çalıştı ve bitti
        Assert.Contains("AJAN BOZDU", File.ReadAllText(Path.Combine(rig.ProjectDir, "hesap.js")));
        Assert.Null(run.BeforeSnapshotUid);
        Assert.Equal(ProtectionLevel.None, run.Protection);
        Assert.False(string.IsNullOrWhiteSpace(run.ProtectionNote));

        var prot = rig.Rm.Restore.ProtectionOf(job.Id);
        Assert.Equal(ProtectionLevel.None, prot.Level);
        Assert.False(prot.CanUndo);
        Assert.Contains("korumasız", prot.Text);
        var ex = Assert.Throws<SozException>(() => rig.Rm.Restore.Preview(job.Id, new UndoRequest()));
        Assert.Equal(409, ex.Status);
        Assert.Single(rig.Olaylar("snapshot.failed"));
        Assert.Empty(rig.Olaylar("snapshot.taken"));
        // ilerleme akışında kişi bunu görür
        using var c = rig.Env.Svc.Db.Open();
        Assert.True(c.ScalarLong("SELECT COUNT(*) FROM run_lines WHERE text LIKE '%korumasız%'") >= 1);
    }

    [Fact]
    public async Task Sinirli_koruma_buyuk_dosya_saklanmayinca_kartta_yazilir()
    {
        using var rig = new Rig(a => a.Ayar("setting:restore.max-copy-mb", "1")); // 1 MB; buyuk.bin 200 KB olduğu için sınır içinde
        var job = rig.Job("boz [[sahte:boz]]");
        var run = await rig.Finish(rig.Rm.Start(job.Id));
        Assert.Equal(ProtectionLevel.Full, run.Protection);

        using var rig2 = new Rig(a => a.Ayar("setting:restore.max-copy-mb", "1"));
        File.WriteAllBytes(Path.Combine(rig2.ProjectDir, "dev.bin"), new byte[(1 << 20) + 5]);
        var job2 = rig2.Job("boz [[sahte:boz]]");
        var run2 = await rig2.Finish(rig2.Rm.Start(job2.Id));
        Assert.Equal(ProtectionLevel.Limited, run2.Protection);
        Assert.Contains("çok büyük", run2.ProtectionNote);
        var p = rig2.Rm.Restore.ProtectionOf(job2.Id);
        Assert.Equal(ProtectionLevel.Limited, p.Level);
        Assert.True(p.CanUndo);
    }

    // ================================================================== ömür ve çöp toplama

    [Fact]
    public async Task Omur_kabul_edilmis_eski_is_noktalari_gider_acik_isin_noktalari_ve_depo_saglam_kalir()
    {
        using var rig = new Rig(a => a.Ayar("setting:restore.keep-last-points", "1"));
        var j1 = rig.Job("bir [[sahte:boz]]");
        await rig.Finish(rig.Rm.Start(j1.Id));
        File.WriteAllText(Path.Combine(rig.ProjectDir, "hesap.js"), "ikinci tur başlangıcı");
        var j2 = rig.Job("iki [[sahte:boz]]");
        await rig.Finish(rig.Rm.Start(j2.Id));
        var j2Once = "ikinci tur başlangıcı";

        // iş 1: kabul edilmiş ve 9 gün önce bitmiş; iş 2: açık
        using (var c = rig.Env.Svc.Db.Open())
        {
            c.Exec("UPDATE jobs SET accepted_by_user=1, finished_at='2026-11-01T10:00:00Z' WHERE id=@j", null, ("@j", j1.Id));
            c.Exec("UPDATE jobs SET accepted_by_user=0 WHERE id=@j", null, ("@j", j2.Id));
        }
        var pr = rig.Rm.Restore.Prune();
        Assert.NotNull(pr);
        Assert.Equal(2, pr!.Snapshots);

        var ex = Assert.Throws<SozException>(() => rig.Rm.Restore.Preview(j1.Id, new UndoRequest()));
        Assert.Equal(409, ex.Status);
        Assert.Contains("süresi dolmuş", ex.Message);

        // açık işin noktaları ve blob'ları sağlam: geri alma çalışır
        var ap = rig.Rm.Restore.Apply(j2.Id, new UndoRequest());
        Assert.Equal("applied", ap.Status);
        Assert.Equal(j2Once, File.ReadAllText(Path.Combine(rig.ProjectDir, "hesap.js")));
        Assert.False(File.Exists(Path.Combine(rig.ProjectDir, "notlar.txt"))); // iş 2 başlarken notlar.txt zaten yoktu (iş 1 silmişti)
        Assert.True(File.Exists(Path.Combine(rig.ProjectDir, "kaynak", "a.js")));
        Assert.Single(rig.Olaylar("snapshot.expired"));
    }

    // ================================================================== API

    private static async Task<(HttpStatusCode code, JsonNode? body)> Call(HttpClient http, string token, HttpMethod m, string url, object? body = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (token.Length > 0) req.Headers.Add("X-Soz-Token", token);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    [Fact]
    public async Task Api_koruma_onizle_uygula_geri_al_ve_guvenlik()
    {
        using var rig = new Rig();
        var onceki = rig.Ozet();
        var port = 50800 + Random.Shared.Next(0, 180); // rastgele port; dolu ise sunucu sonrakini dener
        await using var h = await SozServer.StartAsync(rig.Env.DataDir, port);
        h.Runs.SozExePath = SozExeStandIn;
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };

        var (jc, job) = await Call(http, h.Token, HttpMethod.Post, "/api/jobs", new { projectId = rig.ProjectId, rawText = "Hesabı düzelt [[sahte:boz]]" });
        Assert.Equal(HttpStatusCode.Created, jc);
        var jobId = (long)job!["id"]!;
        // iş çalışmadan koruma: koşu yok, geri alma yok
        var (pc0, p0) = await Call(http, h.Token, HttpMethod.Get, $"/api/jobs/{jobId}/protection");
        Assert.Equal(HttpStatusCode.OK, pc0);
        Assert.False((bool)p0!["canUndo"]!);

        var (sc, started) = await Call(http, h.Token, HttpMethod.Post, $"/api/jobs/{jobId}/start", new { });
        Assert.Equal(HttpStatusCode.Accepted, sc);
        await h.Runs.WaitAsync((long)started!["run"]!["id"]!);
        await Task.Delay(300);

        var (pc, prot) = await Call(http, h.Token, HttpMethod.Get, $"/api/jobs/{jobId}/protection");
        Assert.Equal(HttpStatusCode.OK, pc);
        Assert.Equal("tam", (string)prot!["level"]!);
        Assert.True((bool)prot["canUndo"]!);
        Assert.Contains("geri alınabilir", (string)prot["text"]!);

        var (vc, prev) = await Call(http, h.Token, HttpMethod.Post, $"/api/jobs/{jobId}/undo/preview", new { scope = "all" });
        Assert.Equal(HttpStatusCode.OK, vc);
        Assert.Equal("previewed", (string)prev!["status"]!);
        Assert.True(prev["items"]!.AsArray().Count >= 6);

        // güvenlik: belirteçsiz uygulama 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(http, "", HttpMethod.Post, $"/api/jobs/{jobId}/undo", new { })).code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(http, "", HttpMethod.Post, $"/api/jobs/{jobId}/undo/preview", new { })).code);

        // hatalı gövde
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(http, h.Token, HttpMethod.Post, $"/api/jobs/{jobId}/undo/preview", new { scope = "yarim" })).code);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(http, h.Token, HttpMethod.Post, "/api/jobs/99999/undo/preview", new { })).code);

        var (ac, ap) = await Call(http, h.Token, HttpMethod.Post, $"/api/jobs/{jobId}/undo", new { previewUid = (string)prev["undoUid"]! });
        Assert.Equal(HttpStatusCode.OK, ac);
        Assert.Equal("applied", (string)ap!["status"]!);
        Assert.Equal(onceki, rig.Ozet());

        var (hc, hist) = await Call(http, h.Token, HttpMethod.Get, $"/api/jobs/{jobId}/undo");
        Assert.Equal(HttpStatusCode.OK, hc);
        Assert.Single(hist!["undo"]!.AsArray());

        var (rc, rv) = await Call(http, h.Token, HttpMethod.Post, $"/api/undo/{(string)ap["undoUid"]!}/revert", new { });
        Assert.Equal(HttpStatusCode.OK, rc);
        Assert.Equal("reverted", (string)rv!["status"]!);
        Assert.NotEqual(onceki, rig.Ozet());
        Assert.Equal(HttpStatusCode.Conflict, (await Call(http, h.Token, HttpMethod.Post, $"/api/undo/{(string)ap["undoUid"]!}/revert", new { })).code);
    }
}
