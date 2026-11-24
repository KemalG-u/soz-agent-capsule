using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Onboarding;
using Soz.Core.Profile;
using Xunit;
using Xunit.Abstractions;

namespace Soz.Core.Tests;

public sealed class ProfileInventoryTests(ITestOutputHelper output)
{
    internal sealed class FakeMachine : IMachineProbe
    { public MachineProfile Read(DateTime now) => new(32L*1024*1024*1024,"Synthetic CPU",8,16,"Synthetic GPU",[],[new("cpuName","Synthetic CPU",new("machine","fixture",null,now),now,"gozlem")]); }
    internal sealed class FakeFs : IProjectFileSystem
    {
        public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Gits = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Markers = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Tests = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectoryExists(string p) => Dirs.Contains(p);
        public bool HasGit(string p) => Gits.Contains(p);
        public bool HasMarker(string p) => Markers.Contains(p);
        public bool HasTests(string p) => Tests.Contains(p);
    }
    internal sealed class Rig
    {
        public readonly string Root=ProfileMetadataTests.Fixture();
        public readonly SozDb Db;
        public readonly OnboardingRoots Roots;
        public readonly FakeFs Fs=new();
        public readonly DateTime Now=DateTime.UtcNow;
        public Rig()
        {
            Db=SozDb.OpenAndMigrate(Path.Combine(Root,"data"));
            Roots=OnboardingRoots.Under(Path.Combine(Root,"home"));
            foreach(var dir in new[]{Path.Combine(Roots.ClaudeHome,"projects"),Path.Combine(Roots.CodexHome,"sessions"),Roots.HermesHome,Roots.OpenCodeData}) Directory.CreateDirectory(dir);
            var fake=Path.Combine(Root,"fake-tool.cmd"); File.WriteAllText(fake,"@exit /b 0\r\n");
            using var c=Db.Open();
            foreach(var key in new[]{"claudePath","codexPath","hermesPath","opencodePath"})
                c.Exec("INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",null,("@k",key),("@v",JsonSerializer.Serialize(fake)));
            var settings=SozSettings.Load(Db);
            foreach(var key in new[]{"claudePath","codexPath","hermesPath","opencodePath"}) Assert.Equal(fake,settings.Str(key));
            var roots=ProfileSources.Resolve(settings,Roots);
            Assert.True(roots.Count >= 4);
            Assert.All(roots,r=>Assert.StartsWith(Root,r.Path)); // verified before the first scan
        }
        public ScanContext Context()=>new(){Roots=Roots,Probe=new NullSystemProbe(),UtcNow=()=>Now};
        public ProfileService Service()=>new(Db){Machine=new FakeMachine(),Processes=new ProfileP2ExtraTests.FakeProcesses(),FileSystem=Fs,ExclusionsFactory=r=>new(r.Home,Db.DataDir)};
        public string Claude(string id,string? cwd,string? parent=null)
        {
            var dir=Path.Combine(Roots.ClaudeHome,"projects","E--Se-im");
            if(parent is not null) dir=Path.Combine(dir,parent,"subagents");
            Directory.CreateDirectory(dir); var p=Path.Combine(dir,parent is null?id+".jsonl":"agent-"+id+".jsonl");
            File.WriteAllText(p,JsonSerializer.Serialize(new {sessionId=id,cwd,agentId=parent is null?null:id,timestamp=Now.ToString("O"),message=new {content="P1_DO_NOT_STORE_MESSAGE_9c81",tool_calls=new[]{"P1_DO_NOT_STORE_MESSAGE_9c81"}}})+"\n");
            return p;
        }
        public void Database(string tool,string directory,string id,string cwd,string? parent=null)
        {
            Directory.CreateDirectory(directory);
            using var c=new SqliteConnection($"Data Source={Path.Combine(directory,tool=="hermes"?"state.db":"opencode.db")};Pooling=False");c.Open();
            if(tool=="hermes")
            {
                c.Exec("CREATE TABLE IF NOT EXISTS sessions(id TEXT,cwd TEXT,git_repo_root TEXT,parent_session_id TEXT,started_at REAL,ended_at REAL,last_activity_at REAL); CREATE TABLE IF NOT EXISTS messages(content TEXT);");
                c.Exec("INSERT INTO sessions VALUES(@id,@cwd,@cwd,@parent,@t,NULL,@t)",null,("@id",id),("@cwd",cwd),("@parent",parent),("@t",(Now-DateTime.UnixEpoch).TotalSeconds));
            }
            else
            {
                c.Exec("CREATE TABLE session(id TEXT,directory TEXT,parent_id TEXT,time_created INTEGER,time_updated INTEGER);CREATE TABLE messages(content TEXT);");
                c.Exec("INSERT INTO session VALUES(@id,@cwd,@parent,@t,@t)",null,("@id",id),("@cwd",cwd),("@parent",parent),("@t",(long)(Now-DateTime.UnixEpoch).TotalMilliseconds));
            }
            c.Exec("INSERT INTO messages VALUES('P1_DO_NOT_STORE_MESSAGE_9c81')");
        }
    }

    [Fact]
    public void FourTools_Profiles_Incremental_Privacy_RecordsUnchanged()
    {
        var r=new Rig(); var cwd=@"E:\İşler\Seçim";r.Claude("claude-id",cwd);
        var cp=Path.Combine(r.Roots.CodexHome,"sessions","rollout-no-id.jsonl");
        File.WriteAllText(cp,JsonSerializer.Serialize(new {type="session_meta",payload=new {id="codex-id",cwd,timestamp=r.Now.ToString("O")}}));
        r.Database("hermes",r.Roots.HermesHome,"h1",cwd);
        r.Database("hermes",Path.Combine(r.Roots.HermesHome,"profiles","second"),"h2",cwd,"h1");
        r.Database("opencode",r.Roots.OpenCodeData,"o1",cwd);
        var files=ProfileSources.Resolve(SozSettings.Load(r.Db),r.Roots).SelectMany(ProfileSources.Files).ToArray();
        var hashes=files.ToDictionary(f=>f,f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var service=r.Service();var first=service.Scan(r.Context());var second=service.Scan(r.Context());
        Assert.Equal(5,first.Counts.Sessions);Assert.Single(first.Projects);Assert.Equal("Seçim",first.Projects[0].Name);
        Assert.Equal(2,first.Projects[0].SessionsByTool["hermes"]);Assert.All(second.Sources,s=>Assert.Equal(0,s.FilesRead));
        Assert.Equal(4,second.Tools.Count);Assert.All(second.Sources,s=>Assert.True(s.Ok));
        foreach(var f in files) Assert.Equal(hashes[f],Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        using(var c=r.Db.Open())
        {
            foreach(var table in c.Query("SELECT name FROM sqlite_master WHERE type='table'",x=>x.GetString(0)))
                using(var cmd=c.CreateCommand())
                { cmd.CommandText="SELECT * FROM \""+table.Replace("\"","\"\"")+"\""; using var reader=cmd.ExecuteReader();while(reader.Read())for(var i=0;i<reader.FieldCount;i++)Assert.DoesNotContain("P1_DO_NOT_STORE_MESSAGE_9c81",reader.GetValue(i).ToString()??""); }
        }
        foreach(var f in Directory.EnumerateFiles(r.Db.DataDir,"*",SearchOption.AllDirectories))
        {
            using var stream=new FileStream(f,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var text=new StreamReader(stream,Encoding.UTF8);
            Assert.DoesNotContain("P1_DO_NOT_STORE_MESSAGE_9c81",text.ReadToEnd());
        }
        File.AppendAllText(cp,"\n{}");File.SetLastWriteTimeUtc(cp,r.Now.AddSeconds(1));
        var third=service.Scan(r.Context());Assert.Equal(1,third.Sources.Sum(s=>s.FilesRead));
    }

    [Fact]
    public void Projects_Merge_Exclusions_Missing_Over20_Relations_Defaults()
    {
        var r=new Rig();var fs=r.Fs;var project=@"E:\Projeler\Türkçe";
        fs.Dirs.UnionWith(new[]{project,project+@"\src",project+@"\src\deep",project+@"\_gecici\deneme"});fs.Gits.Add(project);fs.Tests.Add(project);
        r.Claude("main",project+@"\src");r.Claude("child",project+@"\src\deep","main");r.Claude("other",project+@"\_gecici\deneme");
        r.Claude("home",r.Roots.Home);r.Claude("drive",@"E:\");r.Claude("system",@"C:\WINDOWS\system32");r.Claude("temp",@"E:\Tmp\orphan");r.Claude("unknown",null);
        r.Claude("missing",@"E:\Projeler\Yok");
        for(var i=0;i<25;i++)r.Claude("project-"+i,@"E:\Projeler\P"+i);
        r.Claude("same-name",@"D:\Projeler\Türkçe");
        var p=r.Service().Scan(r.Context());
        Assert.Equal(28,p.Projects.Count);Assert.Equal(5,p.Unassigned.Count);
        var merged=Assert.Single(p.Projects,x=>x.Path==project);Assert.Equal(3,merged.SessionsByTool["claude"]);Assert.True(merged.HasGit);Assert.True(merged.HasTests);
        Assert.True(Assert.Single(p.Projects,x=>x.Name=="Yok").MissingFolder);
        Assert.Contains(p.Live.SelectMany(x=>x.Relations),x=>x.Relation=="ana ve yardımcı"&&x.Kind=="gozlem");
        Assert.Contains(p.Live.SelectMany(x=>x.Relations),x=>x.Relation=="aynı projede"&&x.Kind=="cikarim");
        Assert.Equal(@"E:\",p.Defaults.ProjeSurucusu);Assert.Equal(@"E:\Projeler",p.Defaults.YeniProjeUstKlasoru);
        Assert.Equal(2,p.Projects.Count(x=>x.Name=="Türkçe"));Assert.All(p.Projects.Where(x=>x.Name=="Türkçe"),x=>Assert.NotEmpty(x.DisplayHint));
    }

    [Fact]
    public void CorruptAndLockedFiles_AreUnreadable_NotFatal_UnknownCwdKept()
    {
        var r=new Rig();r.Claude("known",null);var locked=r.Claude("locked",@"E:\Good");
        File.WriteAllText(Path.Combine(r.Roots.CodexHome,"sessions","rollout-broken.jsonl"),"not json");
        using(var hold=new FileStream(locked,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            var p=r.Service().Scan(r.Context());Assert.Equal(2,p.Sources.Sum(x=>x.Unreadable));Assert.Equal(1,p.Counts.Sessions);Assert.Equal(1,p.Unassigned.Count);
        }
        Assert.Equal(2,r.Service().Scan(r.Context()).Counts.Sessions); // lock release is retried even if mtime did not change
    }

    [Fact]
    public void View_ExpiresLiveWithoutRescanning_AndOwnDataIsExcluded()
    {
        var r=new Rig();r.Claude("current",@"E:\Projeler\Canlı");var service=r.Service();
        var p=service.Scan(r.Context());Assert.Single(p.Live);
        Assert.Equal("sicak",(string?)service.View(r.Now.AddMinutes(11))["live"]![0]!["sessions"]![0]!["level"]);
        Assert.Empty(service.View(r.Now.AddDays(1))["live"]!.AsArray());
        var policy=new ProjectExclusions(r.Roots.Home,r.Db.DataDir,@"E:\Soz\urun\_gecici");
        Assert.Equal("soz-verisi-ve-denemesi",policy.HardReason(r.Db.DataDir+@"\x"));
        Assert.Equal("soz-verisi-ve-denemesi",policy.HardReason(@"E:\Soz\urun\_gecici\test"));
    }

    [Theory]
    [InlineData("TMP")][InlineData("_tmp")][InlineData("TeMp")][InlineData("scratchpad")][InlineData("_gecici")][InlineData("node_modules")][InlineData("bin")][InlineData("obj")][InlineData("deneme-proje")]
    public void ExclusionSegments_AreExact_CaseInsensitive_AncestorPreserved(string segment)
    {
        var fs=new FakeFs();var project=@"E:\Projeler\P";fs.Dirs.Add(project);fs.Markers.Add(project);
        var resolver=new ProjectResolver(fs,new(@"C:\Users\synthetic",@"E:\SozData"));
        Assert.Equal(project,resolver.Resolve(project+"\\"+segment+"\\x").Root);
        Assert.Null(resolver.Resolve(@"E:\"+segment+@"\x").Root);
        Assert.NotNull(resolver.Resolve(@"E:\templates\x").Root);
    }

    [Fact]
    public void Migration14To15_PreservesSettings_AndRollsBackFailure()
    {
        var db=new SozDb(Path.Combine(ProfileMetadataTests.Fixture(),"migration"));db.MigrateTo(14);
        using(var c=db.Open())c.Exec("INSERT INTO settings(key,value) VALUES('p1-migration','42')");
        db.MigrationFault=step=>{if(step=="v15.sema")throw new InvalidOperationException("synthetic failure");};
        Assert.Throws<InvalidOperationException>(()=>db.Migrate());Assert.Equal(14,db.GetVersion());
        db.MigrationFault=null;db.MigrateTo(15);Assert.Equal(15,db.GetVersion());
        using(var c=db.Open()){Assert.Equal("42",c.Scalar("SELECT value FROM settings WHERE key='p1-migration'"));Assert.Equal(0,c.ScalarLong("SELECT count(*) FROM profile_session"));}
    }

    [Fact]
    public void Performance5000Files_FirstUnder10s_IncrementalUnder2s()
    {
        var r=new Rig();for(var i=0;i<5000;i++)r.Claude("perf-"+i,@"E:\Projeler\Hız");
        var service=r.Service();var sw=Stopwatch.StartNew();var first=service.Scan(r.Context());var cold=sw.ElapsedMilliseconds;
        sw.Restart();var second=service.Scan(r.Context());var warm=sw.ElapsedMilliseconds;
        output.WriteLine($"P1_PERF firstMs={cold} secondMs={warm} sessions={first.Counts.Sessions}");
        File.WriteAllText(Path.Combine(r.Root,"performance.json"),JsonSerializer.Serialize(new{cold,warm,sessions=first.Counts.Sessions}));
        Assert.Equal(5000,first.Counts.Sessions);Assert.Equal(5000,second.Sources.Sum(x=>x.FilesUnchanged));Assert.True(cold<10000,$"first={cold}ms");Assert.True(warm<2000,$"second={warm}ms");
    }
}
