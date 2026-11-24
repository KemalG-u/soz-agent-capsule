using System.Text.Json;
using Soz.Core.Profile;
using Xunit;

namespace Soz.Core.Tests;

public sealed class ProfileP2StateTests
{
    [Fact]
    public void ProjectStates_AreDerivedFromRootTimeline_WithActiveDaysAndQuietDays()
    {
        var now=new DateTime(2026,11,12,12,0,0,DateTimeKind.Utc);var rows=new List<SessionMetadata>();var id=0;
        void Add(string project,int age,int count=1){for(var i=0;i<count;i++)rows.Add(new("claude",(++id).ToString(),@"E:\State\"+project,null,now.AddDays(-age).AddHours(-1),now.AddDays(-age),null,"fixture",now,now,"fixture"));}
        Add("Yeni",2);Add("Etkin",40);Add("Etkin",1);
        for(var age=50;age>=20;age-=2)Add("Duraklamis",age);
        Add("Yasayan",100,8);Add("Yasayan",22);Add("Yasayan",15);Add("Yasayan",8);Add("Yasayan",1);
        Add("Eski",90);Add("Aralik",10);
        // Klasörleri olan projeler: klasörü olmayan proje artık "eski" sayılır (a6, D6-08), bu test zaman çizgisini sınar.
        var fs=new ProfileInventoryTests.FakeFs();
        foreach(var name in new[]{"Yeni","Etkin","Duraklamis","Yasayan","Eski","Aralik"})fs.Dirs.Add(@"E:\State\"+name);
        var resolver=new ProjectResolver(fs,new(@"C:\Users\synthetic",@"E:\Data"));
        var result=resolver.Build(rows,now);
        string? State(string name)=>JsonSerializer.SerializeToNode(result.Projects.Single(p=>p.Name==name),ProfileService.JsonOptions)?["status"]?.GetValue<string>();
        Assert.Equal("yeni",State("Yeni"));Assert.Equal("etkin",State("Etkin"));Assert.Equal("duraklamis",State("Duraklamis"));
        Assert.Equal("yasayan-sistem",State("Yasayan"));Assert.Equal("eski",State("Eski"));Assert.Equal("belirsiz",State("Aralik"));
        var quiet=JsonSerializer.SerializeToNode(result.Projects.Single(p=>p.Name=="Duraklamis"),ProfileService.JsonOptions)!;
        Assert.Equal(20,(int?)quiet["lastQuietDays"]);Assert.Equal(16,(int?)quiet["activeDays"]);
        Assert.All(result.Projects,p=>Assert.Equal("cikarim",p.Kind));
    }
}
