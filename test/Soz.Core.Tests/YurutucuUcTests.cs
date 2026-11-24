using Soz.Core.Data;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Core.Security;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Denetim 3: yürütücü tarafı (sahte claude ile). Başlangıç ölçümü, ajan işi nesnesi, durdurma yanıtının güncelliği.</summary>
public class YurutucuUcTests
{
    private static string FakePath => Path.Combine(TestEnv.RepoRoot, "test", "sahte-claude", "sahte-claude.mjs");
    private static string SozExeStandIn => typeof(RunManager).Assembly.Location;

    private sealed class Rig : IDisposable
    {
        public TestEnv Env = new(copySamples: false);
        public WorkService W;
        public RunManager Rm;
        public string ProjectDir;
        public long ProjectId;
        public string TorunFile;

        public Rig(bool failingProject)
        {
            ProjectDir = NodeSample.Create(Env, "ucproje"); // topla bozuk: 2 testten 1'i kalır
            if (!failingProject) NodeSample.Fix(ProjectDir);
            W = new WorkService(Env.Svc.Db);
            ProjectId = W.EnsureProjectByPath(ProjectDir);
            W.AddCheck(ProjectId, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, enabled: true);
            Env.Svc.PutSettings(new JsonObject { ["claudePath"] = FakePath, ["maxParallelRuns"] = 3 });
            Rm = new RunManager(Env.Svc.Db) { SozExePath = SozExeStandIn };
            TorunFile = Path.Combine(Env.Root, "torun.pid");
            Environment.SetEnvironmentVariable("SAHTE_STATE_DIR", Env.Root);
            Environment.SetEnvironmentVariable("SAHTE_TORUN", TorunFile);
        }

        public async Task<RunDto> Finish(RunDto run)
        {
            await Rm.WaitAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(90));
            using var c = Env.Svc.Db.Open();
            return RunStore.Get(c, run.Id)!;
        }

        public void Dispose()
        {
            try { Rm.Dispose(); } catch (Exception) { }
            Environment.SetEnvironmentVariable("SAHTE_STATE_DIR", null);
            Environment.SetEnvironmentVariable("SAHTE_TORUN", null);
            Env.Dispose();
        }
    }

    [Fact]
    public async Task Baseline_Is_Measured_Before_The_Agent_Starts_And_Shown_As_One_Plain_Sentence()
    {
        using var rig = new Rig(failingProject: true);
        var job = rig.W.CreateJob(rig.ProjectId, "Hesabı düzelt [[sahte:oku]]");
        var run = rig.Rm.Start(job.Id)!;
        var done = await rig.Finish(run);
        Assert.Equal(RunStatus.Done, done.Status);

        using var c = rig.Env.Svc.Db.Open();
        var baseJson = c.Scalar("SELECT baseline_json FROM jobs WHERE id=@j", null, ("@j", job.Id)) as string;
        Assert.False(string.IsNullOrWhiteSpace(baseJson));
        var b = BaselineText.Parse(baseJson)!;
        Assert.Equal(2, b.Total);
        Assert.Equal(1, b.Passed); // başlamadan önce 2 testten 1'i geçiyordu
        var items = RunStore.Timeline(c, job.Id, includeDetail: false);
        var texts = items.Where(i => i.Kind == "progress").Select(i => i.Text).ToList();
        var measuring = texts.FindIndex(t => t.StartsWith("Başlamadan önce durumu ölçüyorum"));
        var sentence = texts.FindIndex(t => t.StartsWith("Başlamadan önce 2 testten 1'i geçmiyor"));
        Assert.True(measuring >= 0, "ölçüm satırı yok");
        Assert.True(sentence > measuring, "sonuç cümlesi yok ya da sırası yanlış");
        // ölçüm ajandan ÖNCE: ajanın ilk satırı cümleden sonra gelir
        var firstAgentLine = items.FindIndex(i => i.Kind is "agent" || i.Text.Contains("dosyasını okuyor"));
        var sentenceItem = items.FindIndex(i => i.Text.StartsWith("Başlamadan önce 2 testten 1'i geçmiyor"));
        if (firstAgentLine >= 0) Assert.True(sentenceItem < firstAgentLine);
    }

    [Fact]
    public async Task Baseline_Setting_Off_Skips_The_Measurement()
    {
        using var rig = new Rig(failingProject: true);
        rig.Env.Svc.PutSettings(new JsonObject { ["baselineRun"] = false });
        var job = rig.W.CreateJob(rig.ProjectId, "Hesabı düzelt [[sahte:oku]]");
        await rig.Finish(rig.Rm.Start(job.Id)!);
        using var c = rig.Env.Svc.Db.Open();
        Assert.Null(c.Scalar("SELECT baseline_json FROM jobs WHERE id=@j", null, ("@j", job.Id)));
    }

    [Fact]
    public async Task Continue_Of_A_Job_Stopped_Before_Any_Agent_Session_Starts_It_Afresh_But_Given_Job_Is_Still_Refused()
    {
        using var rig = new Rig(failingProject: false);
        var job = rig.W.CreateJob(rig.ProjectId, "Hesabı düzelt [[sahte:oku]]");
        rig.W.PatchJob(job.Id, JobStates.Given);
        Assert.Equal(409, Assert.Throws<SozException>(() => rig.Rm.Continue(job.Id)).Status); // verildi ve oturum yok: reddedilir
        using (var c = rig.Env.Svc.Db.Open())
        {
            var runId = RunStore.Insert(c, job.Id, job.ProjectId, "start");
            c.Exec("UPDATE runs SET status='stopped', ended=@e WHERE id=@r", null, ("@e", Time.NowIso()), ("@r", runId));
            JobStore.SetState(c, job.Id, JobStates.Stopped, job.ProjectId); // başlangıç ölçümü sürerken durduruldu: oturum yok
        }
        var run = rig.Rm.Continue(job.Id);
        Assert.NotNull(run);
        var done = await rig.Finish(run!);
        Assert.Equal(RunStatus.Done, done.Status);
    }

    [Fact]
    public async Task Agent_Process_And_Its_Child_Run_Inside_The_Named_Agent_Job_And_Stop_Reply_Is_Current()
    {
        using var rig = new Rig(failingProject: false);
        var job = rig.W.CreateJob(rig.ProjectId, "uzun [[sahte:uzun]]");
        var run = rig.Rm.Start(job.Id)!;
        for (var i = 0; i < 200 && !File.Exists(rig.TorunFile); i++) await Task.Delay(100);
        Assert.True(File.Exists(rig.TorunFile), "sahte torun süreç başlamadı");
        var torun = int.Parse((await File.ReadAllTextAsync(rig.TorunFile)).Trim());
        using (var c = rig.Env.Svc.Db.Open())
        {
            var (pid, _) = RunStore.ProcessOf(c, run.Id);
            Assert.NotNull(pid);
            Assert.True(CallerIdentity.IsInAgentJob(pid!.Value, rig.Env.DataDir));
            Assert.Equal(CallerKind.Agent, CallerIdentity.ClassifyPid(torun, rig.Env.DataDir).Kind); // torun süreç de içeride
            Assert.True(WorkService.HasLiveRun(c, job.Id));
        }
        Assert.Equal(CallerKind.Human, CallerIdentity.ClassifyPid(Environment.ProcessId, rig.Env.DataDir).Kind);

        // durdurma yanıtı güncel: StopAsync dönünce iş "durduruldu", koşu "stopped"
        Assert.True(await rig.Rm.StopAsync(job.Id));
        var j = rig.W.Job(job.Id);
        Assert.Equal(JobStates.Stopped, j.State);
        Assert.Equal(RunStatus.Stopped, j.Run!.Status);
        using (var c = rig.Env.Svc.Db.Open()) Assert.False(WorkService.HasLiveRun(c, job.Id));
    }
}
