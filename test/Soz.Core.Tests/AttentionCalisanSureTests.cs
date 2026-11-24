using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Soz.Core.Attention;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Run;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>k10: <c>/api/attention</c> cevabındaki <c>running[]</c> öğeleri başlangıç anını (startedAt) ve geçen süreyi (ageSeconds) taşır; kapsül "çalışıyor · 12 dk" yazar.</summary>
public class AttentionCalisanSureTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Rig : IDisposable
    {
        public readonly TestEnv Env = new(copySamples: false);
        public readonly WorkService Work;
        public readonly long ProjectId;
        public readonly AttentionService Svc;
        public Rig()
        {
            Time.UtcNow = () => T0;
            Work = new WorkService(Env.Svc.Db);
            ProjectId = Work.EnsureProjectByPath(NodeSample.Create(Env));
            Svc = new AttentionService(Env.Svc.Db);
        }
        public JobDto Running(string text, bool withRun)
        {
            var j = Work.CreateJob(ProjectId, text);
            using var c = Env.Svc.Db.Open();
            JobStore.SetState(c, j.Id, JobStates.Running, ProjectId);
            if (withRun) RunStore.Insert(c, j.Id, ProjectId, "start");
            return Work.Job(j.Id);
        }
        public void Dispose() => Env.Dispose();
    }

    [Fact]
    public void Calisan_is_kosunun_baslangicini_ve_gecen_sureyi_verir()
    {
        using var r = new Rig();
        var j = r.Running("Uzun iş", withRun: true);
        var s = r.Svc.Snapshot(nowUtc: T0.AddMinutes(12));
        var it = Assert.Single(s.Running);
        Assert.Equal(j.Id, it.JobId);
        Assert.Equal(T0, Time.Parse(it.StartedAt));            // koşunun başlangıcı (Time.UtcNow sabit)
        Assert.Equal(720, it.AgeSeconds);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", it.StartedAt!);   // ISO UTC
    }

    [Fact]
    public void Kosu_yoksa_islerin_verilme_ani_baslangic_sayilir()
    {
        // koşusuz ama oturuma bağlı çalışan iş: başlangıç işin verilme anıdır (saf işlev; MorningBuilder bu işi yalnız oturum bağlıysa listeler)
        using var r = new Rig();
        var j = r.Running("Koşusuz", withRun: false) with { Run = null };
        Assert.NotNull(j.GivenAt);
        var it = AttentionBuilder.RunningOf(j, T0.AddSeconds(90));
        Assert.Equal(Time.NormalizeIso(j.GivenAt), it.StartedAt);
        Assert.Equal(90, it.AgeSeconds);
    }

    [Fact]
    public void Baslangic_bilinmiyorsa_sure_alanlari_bos_kalir_uydurulmaz()
    {
        using var r = new Rig();
        var j = r.Running("Bilinmeyen", withRun: false) with { Run = null, GivenAt = null };
        var it = AttentionBuilder.RunningOf(j, T0.AddMinutes(5));
        Assert.Null(it.StartedAt); Assert.Null(it.AgeSeconds);
        Assert.Equal(j.Id, it.JobId); Assert.Equal("Bilinmeyen", it.Title);
        // bozuk zaman damgası da süre uydurmaz
        var bozuk = AttentionBuilder.RunningOf(j with { GivenAt = "dün akşam" }, T0);
        Assert.Null(bozuk.StartedAt); Assert.Null(bozuk.AgeSeconds);
    }

    [Fact]
    public void Gelecekteki_baslangic_sifir_saniye_sayilir_eksi_sure_yok()
    {
        using var r = new Rig();
        var j = r.Running("Saat kaymış", withRun: false) with { Run = new JobRunInfo(1, "running", null, null, "2099-01-01T00:00:00.000Z", null, null) };
        var it = AttentionBuilder.RunningOf(j, T0);
        Assert.Equal(0, it.AgeSeconds);
        Assert.Equal("2099-01-01T00:00:00.000Z", it.StartedAt);
    }

    [Fact]
    public void Eski_alanlar_ayni_kalir_yeni_alanlar_yalniz_eklenir()
    {
        using var r = new Rig();
        var j = r.Running("Eski tüketici", withRun: true);
        var it = Assert.Single(r.Svc.Snapshot(nowUtc: T0.AddSeconds(5)).Running);
        Assert.Equal((j.Uid, j.Id, "Eski tüketici"), (it.JobUid, it.JobId, it.Title));
        Assert.Equal(1, r.Svc.Snapshot(nowUtc: T0.AddSeconds(5)).Counts.Running);
        // eski biçimde (yeni alansız) kurulan öğe hâlâ geçerli
        var eski = new RunningItem("u", 3, "t", null);
        Assert.Null(eski.StartedAt); Assert.Null(eski.AgeSeconds);
    }

    [Fact]
    public async Task Uc_noktasi_running_ogesinde_startedAt_ve_ageSeconds_yazar()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(NodeSample.Create(env));
        var j = w.CreateJob(pid, "Uçtan gelen");
        using (var c = env.Svc.Db.Open()) { JobStore.SetState(c, j.Id, JobStates.Running, pid); { var rid = RunStore.Insert(c, j.Id, pid, "start"); RunStore.SetProcess(c, rid, Environment.ProcessId, System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()); } }
        await using var h = await SozServer.StartAsync(env.DataDir, 52800 + Random.Shared.Next(0, 90));
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var o = await http.GetFromJsonAsync<JsonObject>("/api/attention");
        var run = Assert.Single(o!["running"]!.AsArray())!.AsObject();
        Assert.Equal(j.Id, (long)run["jobId"]!);
        Assert.True(run.ContainsKey("startedAt")); Assert.True(run.ContainsKey("ageSeconds"));
        Assert.NotNull(Time.Parse((string?)run["startedAt"]));
        Assert.True((int)run["ageSeconds"]! >= 0);
    }
}
