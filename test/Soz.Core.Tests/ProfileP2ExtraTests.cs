using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Profile;
using Xunit;

namespace Soz.Core.Tests;

public sealed class ProfileP2ExtraTests
{
    internal sealed class FakeProcesses(params ProcessObservation[] processes):IProcessProvider
    { public IReadOnlyList<ProcessObservation> Read()=>processes; }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void GpuFallback_UsesSecondSource_WhenRegistryEmptyOrDenied(bool denied)
    {
        var probe=new MachineProbe {GpuSources=[()=>denied?throw new UnauthorizedAccessException():[],()=>["Synthetic GPU"]]};
        Assert.Equal("Synthetic GPU",probe.ReadGpuName());
        Assert.Null(new MachineProbe{GpuSources=[()=>[],()=>[]]}.ReadGpuName());
    }

    [Fact]
    public void ProcessCandidates_OnlyLatestDistinctRootSessions_NeverSubagentsOrNodeGuess()
    {
        var now=new DateTime(2026,11,12,12,0,0,DateTimeKind.Utc);
        var inventory=ProcessProbe.Inspect(new FakeProcesses(new("claude",1,now),new("claude",2,now),new("node",3,now),new("unrelated",4,now)),now);
        var fs=new ProfileInventoryTests.FakeFs();
        var resolver=new ProjectResolver(fs,new(@"C:\Users\synthetic",@"E:\SozData"));
        var rows=new[]{ProfileP2RegressionTests.S("old",@"E:\P",300),ProfileP2RegressionTests.S("first",@"E:\P",90),
            ProfileP2RegressionTests.S("second",@"E:\P",100),ProfileP2RegressionTests.S("child",@"E:\P",0,"first"),
            ProfileP2RegressionTests.S("node-tool",@"E:\P",0,tool:"codex")};
        var result=resolver.Build(rows,now,inventory);
        var candidates=result.Live.SelectMany(p=>p.Sessions).Where(s=>s.AcikAdayi).ToArray();
        Assert.Equal(new[]{"first","second"},candidates.Select(s=>s.Id).Order().ToArray());Assert.All(candidates,s=>Assert.Equal("cikarim",s.Kind));
        Assert.Null(inventory.Tools.Single(t=>t.Tool=="codex").Count);Assert.Equal("sayilamadi",inventory.Tools.Single(t=>t.Tool=="codex").Status);
        Assert.DoesNotContain(inventory.Processes,p=>p.Name=="unrelated");
        Assert.Equal(2,inventory.Tools.Single(t=>t.Tool=="claude").Count);
    }

    [Fact]
    public void FailureReasons_AllFour_AndSuccessfulOldProfile_AreCounted_ThenRetried()
    {
        var r=new ProfileInventoryTests.Rig();r.Database("hermes",r.Roots.HermesHome,"good",@"E:\P");
        string FileAt(string name){var dir=Path.Combine(r.Roots.HermesHome,"profiles",name);Directory.CreateDirectory(dir);return Path.Combine(dir,"state.db");}
        var corrupt=FileAt("bad");File.WriteAllText(corrupt,"broken");
        var schema=FileAt("schema");using(var c=new SqliteConnection($"Data Source={schema};Pooling=False")){c.Open();c.Exec("CREATE TABLE sessions(old_column TEXT)");}
        var wal=FileAt("wal");File.WriteAllText(wal,"not read");File.WriteAllText(wal+"-wal","fixture");
        var locked=FileAt("locked");File.WriteAllText(locked,"locked");
        using(var hold=new FileStream(locked,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            var report=r.Service().Scan(r.Context()).Sources.Single(s=>s.Tool=="hermes");
            Assert.Equal(1,report.Okunan);Assert.Equal(4,report.Okunamayan);
            foreach(var reason in new[]{"kilitli","wal-var-shm-yok","sema-farkli","bozuk"})Assert.Equal(1,report.Reasons[reason]);
        }
        Assert.False(File.Exists(wal+"-shm"));
        var retry=r.Service().Scan(r.Context()).Sources.Single(s=>s.Tool=="hermes");Assert.Equal(4,retry.FilesRead);Assert.Equal(1,retry.FilesUnchanged);
    }

    [Fact]
    public void HermesBlankCwd_FallsBackToGit_ThenChannelReasons_PersistedAcrossIncrementalScan()
    {
        var r=new ProfileInventoryTests.Rig();var path=Path.Combine(r.Roots.HermesHome,"state.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();c.Exec("CREATE TABLE sessions(id TEXT,cwd TEXT,git_repo_root TEXT,source TEXT,started_at,ended_at);"+
                "INSERT INTO sessions VALUES('git','','E:\\Git','cli',@t,@t),('chat',NULL,NULL,'telegram',@t,@t),('job','',NULL,'cron',@t,@t)",null,("@t",r.Now.ToString("O")));
        }
        var service=r.Service();var first=service.Scan(r.Context());var second=service.Scan(r.Context());
        Assert.Single(first.Projects);Assert.Equal(@"E:\Git",first.Projects[0].Path);
        Assert.Equal(1,second.Unassigned.ByReason!["hermes-source:telegram"].Sessions7Days);
        Assert.Equal(1,second.Unassigned.ByReason["hermes-source:cron"].Sessions30Days);
        Assert.Equal(2,second.Unassigned.ByTool!["hermes"].Count);Assert.Equal(1,second.Sources.Single(s=>s.Tool=="hermes").FilesUnchanged);
    }

    [Fact]
    public void SessionsEndpointData_IsBounded_Filtered_Parameterized_ProjectsAreCompact()
    {
        var r=new ProfileInventoryTests.Rig();
        for(var i=0;i<30;i++)r.Claude("a"+i,@"E:\P");r.Claude("b",@"E:\Other");
        var service=r.Service();var snapshot=service.Scan(r.Context());
        var data=service.Sessions(@"e:\p",2);Assert.Equal(30,(long)data["total"]!);Assert.Equal(2,data["sessions"]!.AsArray().Count);
        Assert.Empty(service.Sessions("' OR 1=1 --")["sessions"]!.AsArray());
        var project=ProfileService.ToJsonProject(snapshot.Projects.Single(p=>p.Path==@"E:\P"));
        Assert.Null(project["sessions"]);Assert.Null(project["activeSessions"]);Assert.True(project.ToJsonString().Length<1600);
    }

    [Fact]
    public void Migration15To16_IsAtomic_PreservesData_AndInvalidatesOldTimeIndex()
    {
        var db=new SozDb(Path.Combine(ProfileP2TimeTests.Fixture(),"migration"));db.MigrateTo(15);
        using(var c=db.Open())c.Exec("INSERT INTO profile_file VALUES('hermes','synthetic','old',1);INSERT INTO settings VALUES('p2-migration','42')");
        db.MigrationFault=s=>{if(s=="v16.sema")throw new InvalidOperationException("synthetic");};
        Assert.Throws<InvalidOperationException>(()=>db.Migrate());Assert.Equal(15,db.GetVersion());
        using(var c=db.Open())Assert.Equal(0,c.ScalarLong("SELECT count(*) FROM pragma_table_info('profile_session') WHERE name='source_channel'"));
        db.MigrationFault=null;db.Migrate();Assert.Equal(16,db.GetVersion());
        using(var c=db.Open()){Assert.Equal(0,c.ScalarLong("SELECT ok FROM profile_file"));Assert.Equal("42",c.Scalar("SELECT value FROM settings WHERE key='p2-migration'"));}
        using(var c=db.Open()){using var tx=c.BeginTransaction();ProfileSchema.ApplyV16(c,tx);tx.Commit();}
        using(var c=db.Open())Assert.Equal(3,c.ScalarLong("SELECT count(*) FROM pragma_table_info('profile_session') WHERE name IN ('source_channel','project_path','unassigned_reason')"));
    }

    [Fact]
    public void MissingShortRunUnderMarkedAncestor_IsStillExcluded_LongOrExistingRunIsKept()
    {
        var now=new DateTime(2026,11,12,12,0,0,DateTimeKind.Utc);var fs=new ProfileInventoryTests.FakeFs();
        fs.Dirs.UnionWith([@"E:\P",@"E:\P\worktrees\active"]);fs.Markers.Add(@"E:\P");
        var resolver=new ProjectResolver(fs,new(@"C:\Users\synthetic",@"E:\Data"));
        var result=resolver.Build([ProfileP2RegressionTests.S("short",@"E:\P\worktrees\gone"),
            ProfileP2RegressionTests.S("existing",@"E:\P\worktrees\active"),
            ProfileP2RegressionTests.S("long",@"E:\P\worktrees\old") with {StartedAt=now.AddDays(-40)}],now);
        Assert.Equal(1,result.Unassigned.Reasons["tek-seferlik"]);Assert.Equal(2,Assert.Single(result.Projects).RootSessions);
    }

    [Fact]
    public void ConfiguredRecordRoots_AndDescendants_AreAlwaysExcluded_DesktopChildCanBeProject()
    {
        var fs=new ProfileInventoryTests.FakeFs();
        var exclusions=new ProjectExclusions(@"C:\Users\synthetic",@"E:\Data",recordRoots:[new("hermes",@"E:\CustomHistory")]);
        var resolver=new ProjectResolver(fs,exclusions);
        Assert.Null(resolver.Resolve(@"E:\CustomHistory").Root);Assert.Null(resolver.Resolve(@"E:\CustomHistory\profiles\x").Root);
        Assert.NotNull(resolver.Resolve(@"C:\Users\synthetic\Desktop\Project").Root);
    }
}
