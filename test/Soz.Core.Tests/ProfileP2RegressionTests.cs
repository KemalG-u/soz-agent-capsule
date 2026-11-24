using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Profile;
using Xunit;

namespace Soz.Core.Tests;

public sealed class ProfileP2RegressionTests
{
    private static readonly DateTime Now=new(2026,11,12,12,0,0,DateTimeKind.Utc);
    internal static SessionMetadata S(string id,string? cwd,int minutes=0,string? parent=null,string tool="claude") =>
        new(tool,id,cwd,null,Now.AddMinutes(-minutes-60),Now.AddMinutes(-minutes),parent,"synthetic-"+id,Now,Now,"fixture");
    private static ProjectResolver Resolver(ProfileInventoryTests.FakeFs? fs=null)=>new(fs??new(),new(@"C:\Users\synthetic",@"E:\SozData"));
    private static JsonNode Json(object value)=>JsonSerializer.SerializeToNode(value,ProfileService.JsonOptions)!;

    [Fact]
    public void Subagents_DoNotInflateScore_OrRecentRootCounts()
    {
        var rows=new[]{S("root",@"E:\Work\P",60*24*40)}.Concat(Enumerable.Range(0,20).Select(i=>S("child"+i,@"E:\Work\P",0,"root"))).ToArray();
        var project=Assert.Single(Resolver().Build(rows,Now).Projects);var json=Json(project);
        Assert.Equal(0,project.Sessions7Days);Assert.Equal(0,project.Sessions30Days);Assert.Equal(.25,project.Score);
        Assert.Equal(1,(int?)json["rootSessions"]);Assert.Equal(20,(int?)json["subagentSessions"]);
    }

    [Fact]
    public void NestedRoots_AndNestedGit_AreAreasOfTopRoot()
    {
        var fs=new ProfileInventoryTests.FakeFs();
        fs.Dirs.UnionWith([@"E:\Is",@"E:\Is\urun",@"E:\Is\urun\src",@"E:\Is\urun\ui"]);
        fs.Markers.UnionWith([@"E:\Is\urun",@"E:\Is\urun\ui"]);fs.Gits.Add(@"E:\Is\urun\ui");
        var result=Resolver(fs).Build([S("top",@"E:\Is"),S("sln",@"E:\Is\urun"),S("src",@"E:\Is\urun\src"),S("ui",@"E:\Is\urun\ui")],Now);
        Assert.Equal(2,result.Projects.Count);
        Assert.Contains(result.Projects,p=>p.Path==@"E:\Is\urun");
        Assert.Contains(result.Projects,p=>p.Path==@"E:\Is\urun\ui");
        Assert.Equal(1,result.Unassigned.Reasons["kap-klasor"]);
        var urun=result.Projects.Single(p=>p.Path==@"E:\Is\urun");
        var areas=Json(urun)["areas"]!.AsArray();Assert.Single(areas);Assert.EndsWith("src",(string?)areas[0]!["path"]??"",StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Containers_DoNotConsumeTheirChildren_AndMissingSmallProjectsFade()
    {
        var fs=new ProfileInventoryTests.FakeFs();fs.Dirs.Add(@"D:\Workspace");
        var rows=new List<SessionMetadata>{S("container",@"D:\Workspace"),S("faded",@"E:\Gone")};
        for(var i=0;i<3;i++){var p=@"D:\Workspace\P"+i;fs.Dirs.Add(p);fs.Markers.Add(p);rows.Add(S("p"+i,p,60*24*10));}
        var result=Resolver(fs).Build(rows,Now);Assert.Equal(4,result.Projects.Count);Assert.Equal(1,result.Unassigned.Count);
        Assert.Equal(1,result.Unassigned.Reasons["kap-klasor"]);Assert.Equal(@"E:\Gone",result.Projects[^1].Path);
        Assert.True((bool?)Json(result.Projects[^1])["soluk"]);
    }

    [Theory]
    [InlineData(@"C:\Users\synthetic\.claude\projects\a")]
    [InlineData(@"E:\Tools\hermes-home\profiles\a")]
    [InlineData(@"E:\Tools\my-profile\a")]
    [InlineData(@"E:\Tools\hermes-chrome-profile-2\a")]
    [InlineData(@"C:\Users\synthetic\Desktop")]
    [InlineData(@"C:\Users\synthetic\Documents")]
    public void ToolHomes_Profiles_AndSpecialFolders_AreNotProjects(string path)
    {Assert.Empty(Resolver().Build([S("a",path)],Now).Projects);}

    [Theory]
    [InlineData("testler")][InlineData("tests-out")][InlineData("olcum")][InlineData("worktrees")]
    public void ShortMissingMeasurementRuns_AreUnassigned(string segment)
    {
        var result=Resolver().Build([S("a",@"E:\Work\"+segment+@"\run-1",60*24*2)],Now);
        Assert.Empty(result.Projects);Assert.Equal(1,result.Unassigned.Reasons["tek-seferlik"]);
    }

    [Fact]
    public void ActivityHasThreeWindows_AndProjectsDoNotContainSessions()
    {
        var result=Resolver().Build([S("writing",@"E:\Work\P",1),S("hot",@"E:\Work\P",90),S("today",@"E:\Work\P",240)],Now);
        var live=Json(result.Live);Assert.Contains("yaziyor",live.ToJsonString());Assert.Contains("sicak",live.ToJsonString());Assert.Contains("bugun",live.ToJsonString());
        var project=Json(Assert.Single(result.Projects));Assert.Null(project["activeSessions"]);Assert.Null(project["sessions"]);
    }

    [Fact]
    public void DefaultsUseSameWeightedDriveAndParentTable()
    {
        var rows=Enumerable.Range(0,12).Select(i=>S("e"+i,@"E:\Yeni\P")).Concat(Enumerable.Range(0,5).Select(i=>S("d"+i,@"D:\Eski\P"+i,60*24*40))).ToArray();
        var d=Resolver().Build(rows,Now).Defaults;Assert.Equal(@"E:\",d.ProjeSurucusu);Assert.Equal(@"E:\Yeni",d.YeniProjeUstKlasoru);Assert.NotNull(Json(d)["explanation"]);
    }

    [Fact]
    public void UnassignedHasToolAndReasonRecentBreakdown()
    {
        var result=Resolver().Build([S("a",null,0,tool:"hermes"),S("b",null,60*24*10,tool:"codex")],Now);
        var json=Json(result.Unassigned);Assert.Equal(1,(int?)json["byTool"]?["hermes"]?["sessions7Days"]);
        Assert.Equal(2,(int?)json["byReason"]?["klasor-bilinmiyor"]?["sessions30Days"]);
    }

    [Fact]
    public void OldHermesSchema_MissingCwd_IsReadable()
    {
        var path=Path.Combine(ProfileP2TimeTests.Fixture(),"state.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False")){c.Open();c.Exec("CREATE TABLE sessions(id TEXT,git_repo_root TEXT,source TEXT);INSERT INTO sessions VALUES('a','E:\\Git','telegram')");}
        var row=Assert.Single(ProfileSources.ReadDatabase("hermes",path,Now));Assert.Null(row.Cwd);Assert.Equal(@"E:\Git",row.GitRepoRoot);
    }

    [Fact]
    public void PartialSourcesAreCounted_WithFailureReasons_NoGlobalOk()
    {
        var r=new ProfileInventoryTests.Rig();r.Database("hermes",r.Roots.HermesHome,"good",@"E:\Good");
        var bad=Path.Combine(r.Roots.HermesHome,"profiles","bad");Directory.CreateDirectory(bad);File.WriteAllText(Path.Combine(bad,"state.db"),"broken");
        var source=Json(r.Service().Scan(r.Context()).Sources.Single(s=>s.Tool=="hermes"));
        Assert.Null(source["ok"]);Assert.Equal(1,(int?)source["okunan"]);Assert.Equal(1,(int?)source["okunamayan"]);Assert.Equal(1,(int?)source["reasons"]?["bozuk"]);
    }

    [Theory]
    [InlineData("hermes")][InlineData("opencode")]
    public void SeparateDatabaseTitle_IsKept_ButMessagesAreNot(string tool)
    {
        var path=Path.Combine(ProfileP2TimeTests.Fixture(),"title.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();c.Exec(tool=="hermes"?"CREATE TABLE sessions(id TEXT,cwd TEXT,title TEXT)":"CREATE TABLE session(id TEXT,directory TEXT,title TEXT)");
            c.Exec(tool=="hermes"?"INSERT INTO sessions VALUES('a','E:\\P','laboratuvar')":"INSERT INTO session VALUES('a','E:\\P','laboratuvar')");
        }
        var json=Json(Assert.Single(ProfileSources.ReadDatabase(tool,path,Now)));Assert.Equal("laboratuvar",(string?)json["title"]);
    }

    [Theory]
    [InlineData("claude")][InlineData("codex")]
    public void SeparateJsonTitle_IsKept_WithoutDerivingFromMessage(string tool)
    {
        var path=Path.Combine(ProfileP2TimeTests.Fixture(),"title.jsonl");
        var meta=new {id="a",sessionId="a",cwd=@"E:\P",title="kurulum",timestamp=Now.ToString("O"),message=new{title="PRIVATE_MESSAGE_TITLE",content="PRIVATE_CONTENT"}};
        File.WriteAllText(path,tool=="claude"?JsonSerializer.Serialize(meta):JsonSerializer.Serialize(new {type="session_meta",payload=meta}));
        var json=Json(SessionMetadataReader.Read(tool,path,Now)!);Assert.Equal("kurulum",(string?)json["title"]);Assert.DoesNotContain("PRIVATE_",json.ToJsonString());
    }
}
