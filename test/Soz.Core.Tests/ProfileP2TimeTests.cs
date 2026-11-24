using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Profile;
using Xunit;

namespace Soz.Core.Tests;

public sealed class ProfileP2TimeTests
{
    internal static string Fixture()
    {
        var p=Path.Combine(TestEnv.RepoRoot,"_gecici","p2","sentetik",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p); return p;
    }

    [Theory]
    [InlineData("hermes","seconds")][InlineData("hermes","milliseconds")][InlineData("hermes","text")]
    [InlineData("opencode","seconds")][InlineData("opencode","milliseconds")][InlineData("opencode","text")]
    public void Database_UsesSessionTimes_NotSharedFileTime(string tool,string format)
    {
        var path=Path.Combine(Fixture(),"state.db");
        var start=new DateTime(2026,1,1,10,0,0,DateTimeKind.Utc);var end=start.AddHours(1);
        object Stamp(DateTime t)=>format switch {"seconds"=>(t-DateTime.UnixEpoch).TotalSeconds,"milliseconds"=>(t-DateTime.UnixEpoch).TotalMilliseconds,_=>t.ToString("O")};
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            c.Exec(tool=="hermes"?"CREATE TABLE sessions(id TEXT,cwd TEXT,started_at,ended_at)":"CREATE TABLE session(id TEXT,directory TEXT,time_created,time_updated)");
            c.Exec(tool=="hermes"?"INSERT INTO sessions VALUES('old','E:\\Old',@s,@e)":"INSERT INTO session VALUES('old','E:\\Old',@s,@e)",null,("@s",Stamp(start)),("@e",Stamp(end)));
        }
        File.SetLastWriteTimeUtc(path,end.AddMonths(9));
        var row=Assert.Single(ProfileSources.ReadDatabase(tool,path,end.AddMonths(9)));
        Assert.Equal(start,row.StartedAt);Assert.Equal(end,row.LastActivity);
        Assert.NotEqual(row.RecordModifiedAt,row.LastActivity);
    }

    [Fact]
    public void Hermes_MissingEnd_UsesOnlyMaxMessageTimestamp_UnknownNeverUsesMtime()
    {
        var path=Path.Combine(Fixture(),"state.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();c.Exec("CREATE TABLE sessions(id TEXT,cwd TEXT,started_at,ended_at);CREATE TABLE messages(session_id TEXT,timestamp,content TEXT);");
            c.Exec("INSERT INTO sessions VALUES('active','E:\\Old',1767261600,NULL),('unknown',NULL,NULL,NULL);");
            c.Exec("INSERT INTO messages VALUES('active',1767265200000,'P2_PRIVATE_MESSAGE'),('active',1767261600000,'P2_PRIVATE_MESSAGE');");
        }
        var rows=ProfileSources.ReadDatabase("hermes",path,DateTime.UtcNow);
        Assert.Equal(new DateTime(2026,1,1,11,0,0,DateTimeKind.Utc),rows.Single(r=>r.Id=="active").LastActivity);
        Assert.Null(rows.Single(r=>r.Id=="unknown").LastActivity);   // p3: bilinmeyen zaman null, 0001 değil
        Assert.DoesNotContain("P2_PRIVATE_MESSAGE",System.Text.Json.JsonSerializer.Serialize(rows));
    }

    [Fact]
    public void Hermes_544OldSessionsInRecentlyWrittenDatabase_DoNotBecomeRecent()
    {
        var path=Path.Combine(Fixture(),"state.db");var now=new DateTime(2026,11,12,12,0,0,DateTimeKind.Utc);
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();c.Exec("CREATE TABLE sessions(id TEXT,cwd TEXT,started_at,ended_at)");
            using var tx=c.BeginTransaction();
            for(var i=0;i<544;i++)c.Exec("INSERT INTO sessions VALUES(@id,'E:\\P',@s,@e)",tx,("@id","old"+i),("@s",now.AddDays(-50).ToString("O")),("@e",now.AddDays(-40).ToString("O")));
            tx.Commit();
        }
        File.SetLastWriteTimeUtc(path,now);
        var rows=ProfileSources.ReadDatabase("hermes",path,now);
        var resolver=new ProjectResolver(new ProfileInventoryTests.FakeFs(),new(@"C:\Users\synthetic",@"E:\Data"));
        var project=Assert.Single(resolver.Build(rows,now).Projects);
        Assert.Equal(544,project.RootSessions);Assert.Equal(0,project.Sessions7Days);Assert.Equal(0,project.Sessions30Days);
    }
}
