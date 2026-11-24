using Soz.Core.Attention;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Registry;
using Soz.Core.Run;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>k11: kullanıcıya dönük cümlelerde "ajan" yerine "yardımcı", "kanıt" yerine "deneme sonucu", "ölçüt" yerine "kontrol". Alan adları ve neden kodları değişmez.</summary>
public class AttentionK11CumleTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Yasak = { "ajan", "kanıt", "ölçüt" };

    private sealed class Rig : IDisposable
    {
        public readonly TestEnv Env = new(copySamples: false);
        public readonly WorkService Work;
        public readonly long ProjectId;
        public readonly AttentionService Svc;
        public Rig()
        {
            var n = 0; Time.UtcNow = () => T0.AddMilliseconds(n++);
            Work = new WorkService(Env.Svc.Db);
            ProjectId = Work.EnsureProjectByPath(NodeSample.Create(Env));
            Svc = new AttentionService(Env.Svc.Db);
        }
        public JobDto Job(string text, string state)
        {
            var j = Work.CreateJob(ProjectId, text);
            using var c = Env.Svc.Db.Open();
            if (state == JobStates.AwaitingAnswer) JobStore.SetAwaitingAnswer(c, j.Id, "Hangisini seçeyim?", ProjectId);
            else JobStore.SetState(c, j.Id, state, ProjectId);
            return Work.Job(j.Id);
        }
        public void Dispose() => Env.Dispose();
    }

    private static string Cumle(AttentionService s, long jobId) => s.Snapshot().Items.Single(i => i.JobId == jobId).Sentence;

    [Fact]
    public void Soru_ve_izin_cumlesi_yardimci_der_ajan_demez()
    {
        using var r = new Rig();
        var soru = r.Job("Soru işi", JobStates.AwaitingAnswer);
        var izin = r.Job("İzin işi", JobStates.AwaitingApproval);
        var s = r.Svc.Snapshot();
        Assert.Equal("«Soru işi» işinde yardımcı sana bir soru sordu.", s.Items.Single(i => i.JobId == soru.Id).Sentence);
        Assert.Equal("«İzin işi» işinde yardımcı bir işlem için senden izin bekliyor.", s.Items.Single(i => i.JobId == izin.Id).Sentence);
        Assert.Equal("question", s.Items.Single(i => i.JobId == soru.Id).Reason);   // neden kodu değişmez
        Assert.Equal("approval", s.Items.Single(i => i.JobId == izin.Id).Reason);
    }

    [Fact]
    public void Karar_ve_gecmeyen_deneme_cumlesi_deneme_sonucu_der_kanit_demez()
    {
        using var r = new Rig();
        var karar = r.Job("Karar işi", JobStates.AwaitingDecision);
        var kalan = r.Job("Kalan iş", JobStates.Failed);
        Assert.Equal("«Karar işi» işinde deneme sonucu sayılamadı. Sonucu kabul edip etmeyeceğine sen karar ver.", Cumle(r.Svc, karar.Id));
        Assert.Equal("«Kalan iş» için deneme sonucu geçmedi.", Cumle(r.Svc, kalan.Id));
        Assert.Equal("proof_failed", r.Svc.Snapshot().Items.Single(i => i.JobId == kalan.Id).Reason);
    }

    [Fact]
    public void Biten_is_cumlesi_deneme_sonucuyla_bitti_der()
    {
        using var r = new Rig();
        var bitti = r.Job("Bitti işi", JobStates.Passed);
        var it = r.Svc.Snapshot().Items.Single(i => i.JobId == bitti.Id);
        Assert.Equal("«Bitti işi» deneme sonucuyla bitti.", it.Sentence);
        Assert.Equal("done", it.Kind);
    }

    [Fact]
    public void Denenmeden_biten_is_cumlesi_ve_ayrintisi_kontrol_der_olcut_demez()
    {
        using var r = new Rig();
        var j = r.Job("Denenmemiş iş", JobStates.Given);
        using (var c = r.Env.Svc.Db.Open()) { var rid = RunStore.Insert(c, j.Id, r.ProjectId, "start"); RunStore.SetStatus(c, rid, RunStatus.Done, ended: true); }
        var it = r.Svc.Snapshot().Items.Single(i => i.JobId == j.Id);
        Assert.Equal("unproven", it.Reason);                                            // neden kodu aynı
        Assert.Equal("«Denenmemiş iş» denenmeden bitti: yardımcı bitirdi ama Söz bunu deneyemedi. Bitti mi bilmiyorum.", it.Sentence);
        Assert.Contains("çalışan bir kontrol yok", it.Detail);
        Assert.DoesNotContain("ölçüt", it.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sureci_kaybolan_kosu_cumlesi_yardimcinin_sureci_der()
    {
        using var r = new Rig();
        var lost = r.Job("Süreci ölmüş", JobStates.Running);
        using (var c = r.Env.Svc.Db.Open()) { var a = RunStore.Insert(c, lost.Id, r.ProjectId, "start"); RunStore.SetProcess(c, a, 2_000_000_000, T0); }
        var it = r.Svc.Snapshot(nowUtc: T0.AddSeconds(AttentionBuilder.DeadRunGraceSeconds + 5)).Items.Single(i => i.JobId == lost.Id);
        Assert.Equal("run_lost", it.Reason);
        Assert.Equal("«Süreci ölmüş» çalışıyor görünüyor ama yardımcının süreci bulunamadı. Durumu belirsiz.", it.Sentence);
    }

    [Fact]
    public void Hicbir_is_cumlesinde_ajan_kanit_olcut_sozu_kalmaz()
    {
        using var r = new Rig();
        r.Job("A", JobStates.AwaitingAnswer); r.Job("B", JobStates.AwaitingApproval); r.Job("C", JobStates.AwaitingDecision);
        r.Job("D", JobStates.Failed); r.Job("E", JobStates.Passed);
        var u = r.Job("F", JobStates.Given);
        using (var c = r.Env.Svc.Db.Open()) { var rid = RunStore.Insert(c, u.Id, r.ProjectId, "start"); RunStore.SetStatus(c, rid, RunStatus.Done, ended: true); }
        var items = r.Svc.Snapshot().Items;
        Assert.True(items.Count >= 6);
        foreach (var it in items.Where(i => i.JobId is not null))
            foreach (var y in Yasak)
            {
                Assert.DoesNotContain(y, it.Sentence, StringComparison.OrdinalIgnoreCase);
                if (it.Reason is "unproven") Assert.DoesNotContain(y, it.Detail ?? "", StringComparison.OrdinalIgnoreCase);
            }
    }
}
