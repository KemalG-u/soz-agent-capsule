using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Soz.Core.Profile;
using Soz.Core.Security;
using Soz.Server;
using Soz.Server.Endpoints;
using Xunit;

namespace Soz.Core.Tests;

public sealed class ProfileApiTests
{
    [Fact]
    public async Task Api_SyntheticRoots_TokenAndOrigin_ExistingShape_ProfilePersisted()
    {
        var r=new ProfileInventoryTests.Rig();r.Claude("api-session",@"E:\Projeler\Türkçe");
        var saved=OnboardingEndpoints.ContextFactory;var ancestor=CallerIdentity.AncestorCheck;
        OnboardingEndpoints.ContextFactory=r.Context;CallerIdentity.AncestorCheck=false;
        try
        {
            await using var server=await SozServer.StartAsync(r.Db.DataDir,49534);
            using var client=new HttpClient{BaseAddress=new Uri(server.Url)};
            var before=JsonNode.Parse(await client.GetStringAsync("/api/profile"))!;
            Assert.Equal(0,(int)before["counts"]!["sessions"]!);
            Assert.True(before.AsObject().ContainsKey("scannedAt"));Assert.Null(before["scannedAt"]);   // hiç tarama yok: alan var, boş
            var denied=await client.PostAsync("/api/onboarding/scan",new StringContent("{}",Encoding.UTF8,"application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
            client.DefaultRequestHeaders.Add("X-Soz-Token",server.Token);
            using(var request=new HttpRequestMessage(HttpMethod.Post,"/api/onboarding/scan"){Content=new StringContent("{}",Encoding.UTF8,"application/json")})
            { request.Headers.Add("Origin","http://foreign.invalid");Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(request)).StatusCode); }
            var scan=await client.PostAsync("/api/onboarding/scan",new StringContent("{}",Encoding.UTF8,"application/json"));
            var text=await scan.Content.ReadAsStringAsync();Assert.Equal(HttpStatusCode.OK,scan.StatusCode);
            var body=JsonNode.Parse(text)!;Assert.NotNull(body["outcome"]);Assert.NotNull(body["view"]);Assert.NotNull(body["profile"]);
            var profile=JsonNode.Parse(await client.GetStringAsync("/api/profile"))!;
            Assert.Equal(1,(int)profile["counts"]!["sessions"]!);
            Assert.Equal(r.Now,DateTime.Parse((string)profile["scannedAt"]!,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind));
            Assert.Equal("Türkçe",(string?)profile["projects"]![0]!["name"]);
            foreach(var key in new[]{"machine","tools","projects","unassigned","live","defaults","counts","scanMs","sources"}) Assert.NotNull(profile[key]);
            var onboarding=JsonNode.Parse(await client.GetStringAsync("/api/onboarding"))!;
            Assert.Equal(6,onboarding["groups"]!.AsArray().Count);
            var projectItems=onboarding["groups"]!.AsArray().SelectMany(g=>g!["items"]!.AsArray())
                .Where(item=>(string?)item!["group"]=="projects").ToArray();
            Assert.NotEmpty(projectItems);
            Assert.All(projectItems,item=>Assert.Equal("Türkçe",(string?)item!["title"]));
            Assert.DoesNotContain("P1_DO_NOT_STORE_MESSAGE_9c81",text);
            var sessions=JsonNode.Parse(await client.GetStringAsync("/api/profile/sessions?project="+Uri.EscapeDataString(@"E:\Projeler\Türkçe")+"&limit=1"))!;
            Assert.Equal(1,(int)sessions["total"]!);Assert.Single(sessions["sessions"]!.AsArray());
            Assert.Null(profile["projects"]![0]!["activeSessions"]);
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/profile/sessions?limit=0")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/profile/sessions?project=x&limit=501")).StatusCode);
            var p2dir=Path.Combine(TestEnv.RepoRoot,"_gecici","p2");Directory.CreateDirectory(p2dir);var example=Path.Combine(p2dir,"api-ornek.json");
            File.WriteAllText(example,profile.ToJsonString(new(){WriteIndented=true}));
        }
        finally{OnboardingEndpoints.ContextFactory=saved;CallerIdentity.AncestorCheck=ancestor;}
    }

    [Fact]
    public void MachineProbe_Under2Seconds_UnknownIsExplicit_NoProcessQuery()
    {
        var sw=Stopwatch.StartNew();var machine=new MachineProbe().Read(DateTime.UtcNow);
        Assert.True(sw.ElapsedMilliseconds<2000,$"machineMs={sw.ElapsedMilliseconds}");
        Assert.Equal(5,machine.Facts.Count);Assert.All(machine.Facts,f=>Assert.Equal("gozlem",f.Kind));
        var p2dir=Path.Combine(TestEnv.RepoRoot,"_gecici","p2");Directory.CreateDirectory(p2dir);File.WriteAllText(Path.Combine(p2dir,"machine-ms.txt"),sw.ElapsedMilliseconds.ToString());
    }
}
