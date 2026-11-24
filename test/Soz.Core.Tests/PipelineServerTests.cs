using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Server;
using Xunit;
using Xunit.Abstractions;

namespace Soz.Core.Tests;

public class PipelineTests
{
    private readonly ITestOutputHelper _out;
    public PipelineTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Import_Proposes_Clusters_Repeats_And_General_Parent()
    {
        using var env = new TestEnv();
        var sum = env.Import();
        _out.WriteLine($"mesaj {sum.NewUtterances}, aday {sum.NewCandidates}, öneri {sum.NewProposals}, tekrar {sum.NewRepeatClusters}, üst {sum.ParentClusters}");
        var proposed = env.Svc.Decisions(status: "proposed");
        foreach (var d in proposed) _out.WriteLine($"#{d.Id} {d.ProjectName} {d.Type} {d.RepeatCount}/{d.SessionCount} {d.Text}");
        using (var c = env.Svc.Db.Open())
            foreach (var row in c.Query("SELECT c.id, c.cluster_id, cl.parent_id, c.type, c.score, c.text FROM candidates c LEFT JOIN clusters cl ON cl.id=c.cluster_id ORDER BY c.cluster_id",
                         r => $"aday {r.GetInt64(0)} küme {r.LongN(1)} üst {r.LongN(2)} {r.GetString(3)} {r.GetDouble(4):0.00} {r.GetString(5)}"))
                _out.WriteLine(row);
        Assert.Equal(12, sum.NewUtterances); // 10 Claude + 2 Codex insan mesajı
        Assert.True(sum.NewProposals > 0);
        Assert.True(sum.ParentClusters >= 1);
        // "Her zaman Türkçe yaz" iki projede söylendi → genel (*) öneri, tekrar sayısıyla
        var general = proposed.Single(d => d.ProjectName == "Genel" && d.Text.Contains("Türkçe"));
        Assert.True(general.RepeatCount >= 4, $"tekrar {general.RepeatCount}");
        Assert.True(general.SessionCount >= 3);
        Assert.Equal("rule", general.Type);
        Assert.NotNull(general.Origin);
        // KarincaAI yasağı (Claude + Codex, 3 oturum) proje içinde tek öneri
        var ban = proposed.Single(d => d.Text.Contains("KarincaAI"));
        Assert.Equal("ban", ban.Type);
        Assert.Equal("ornek-proje", ban.ProjectName);
        Assert.True(ban.SessionCount >= 2);
        // Alt ajan / otomasyon / sistem enjeksiyonu kaynaklı hiçbir şey önerilmedi
        Assert.DoesNotContain(proposed, d => d.Text.Contains("Japonca") || d.Text.Contains("İngilizce yaz") || d.Text.Contains("Fransızca") || d.Text.Contains("Almanca"));

        var repeats = env.Svc.Repeats();
        Assert.Contains(repeats, r => r.ProjectName == "Genel" && r.Members.Count >= 4);
    }

    [Fact]
    public void Approve_Packet_Reject_Is_Not_Reproposed()
    {
        using var env = new TestEnv();
        env.Import();
        var proposed = env.Svc.Decisions(status: "proposed");
        var ban = proposed.Single(d => d.Text.Contains("KarincaAI"));
        env.Svc.PatchDecision(ban.Id, status: "approved");
        var pid = env.Svc.ResolveProject(@"E:\ornek-proje")!.Value;
        Assert.Contains(ban.Text, env.Svc.Packet(pid).Text);

        var other = proposed.First(d => d.Id != ban.Id);
        env.Svc.PatchDecision(other.Id, status: "rejected");
        // Aynı şey yeniden söylenir; yeniden içe alma onu tekrar önermez.
        var file = Path.Combine(env.ClaudeRoot, "E--ornek-proje", "aaaaaaaa-0000-4000-8000-000000000002.jsonl");
        var line = new JsonObject
        {
            ["parentUuid"] = "b2", ["isSidechain"] = false, ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Bunu tekrar söylüyorum: " + other.Text },
            ["uuid"] = "v9", ["timestamp"] = "2026-11-10T11:00:00.000Z", ["entrypoint"] = "cli", ["cwd"] = @"E:\ornek-proje",
            ["sessionId"] = "aaaaaaaa-0000-4000-8000-000000000002", ["origin"] = new JsonObject { ["kind"] = "human" }, ["promptSource"] = "typed",
        };
        File.AppendAllText(file, line.ToJsonString() + "\n");
        env.Import();
        Assert.Equal("rejected", env.Svc.Decision(other.Id).Status);
        Assert.DoesNotContain(env.Svc.Decisions(status: "proposed"), d => d.Text == other.Text);
    }

    [Fact]
    public void Promote_And_Search_And_Report()
    {
        using var env = new TestEnv();
        env.Import();
        var rep = env.Svc.Repeats().First();
        var d = env.Svc.Promote(rep.ClusterId);
        Assert.Equal("approved", d.Status);
        var page = env.Svc.Utterances("karıncaai", null, 10, 0); // Türkçe büyük/küçük harf ve şapka duyarsız
        Assert.Equal(3, page.Total);
        var r = env.Svc.Report(30, new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, r.Utterances);
        Assert.Equal(2, r.Projects);
        Assert.Equal(4, r.Sessions);
        Assert.Equal(2, r.Compactions); // 1 Claude + 1 Codex insan oturumu (alt ajan sıkıştırması sayılmaz)
        Assert.True(r.RepeatClusters >= 2);
        Assert.True(r.TopRepeats.Count >= 1);
    }
}

public class ServerTests
{
    [Fact]
    public async Task Api_Security_And_Shapes()
    {
        using var env = new TestEnv();
        // API'den içe alma gerçek kayıtlara gitmesin: kökler örnek veriye sabitlenir.
        env.Svc.PutSettings(new JsonObject { ["claudeRoot"] = env.ClaudeRoot, ["codexRoot"] = env.CodexRoot });
        env.Import();
        await using var h = await SozServer.StartAsync(env.DataDir, 49300);
        Assert.StartsWith("http://127.0.0.1:", h.Url);
        Assert.True(File.Exists(Path.Combine(env.DataDir, "sunucu.json")));
        Assert.DoesNotContain(h.Token, File.ReadAllText(Path.Combine(env.DataDir, "sunucu.json")));
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };

        var session = JsonNode.Parse(await http.GetStringAsync("/api/session"))!;
        Assert.Equal(h.Token, (string?)session["token"]);
        Assert.Equal("tr", (string?)session["language"]);

        // Host denetimi
        var bad = new HttpRequestMessage(HttpMethod.Get, "/api/summary");
        bad.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(bad)).StatusCode);
        // Yabancı köken
        var cross = new HttpRequestMessage(HttpMethod.Get, "/api/summary");
        cross.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(cross)).StatusCode);

        var summary = JsonNode.Parse(await http.GetStringAsync("/api/summary"))!;
        foreach (var k in new[] { "projects", "sessions", "utterances", "proposed", "approved", "repeatClusters", "lastImportAt", "sources", "waiting", "working", "stalled" })
            Assert.True(summary.AsObject().ContainsKey(k), k);

        var decisions = JsonNode.Parse(await http.GetStringAsync("/api/decisions?status=proposed"))!.AsArray();
        Assert.NotEmpty(decisions);
        var first = decisions[0]!.AsObject();
        foreach (var k in new[] { "id", "projectId", "projectName", "type", "text", "status", "pinned", "repeatCount", "sessionCount", "createdAt", "updatedAt", "origin" })
            Assert.True(first.ContainsKey(k), k);
        var id = (long)first["id"]!;

        // Belirteçsiz değişiklik reddedilir
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/decisions/{id}") { Content = new StringContent("{\"status\":\"approved\"}", Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(patch)).StatusCode);
        var patch2 = new HttpRequestMessage(HttpMethod.Patch, $"/api/decisions/{id}") { Content = new StringContent("{\"status\":\"approved\",\"pinned\":true}", Encoding.UTF8, "application/json") };
        patch2.Headers.Add("X-Soz-Token", h.Token);
        var pr = await http.SendAsync(patch2);
        Assert.Equal(HttpStatusCode.OK, pr.StatusCode);
        var patched = JsonNode.Parse(await pr.Content.ReadAsStringAsync())!;
        Assert.Equal("approved", (string?)patched["status"]);
        Assert.True((bool)patched["pinned"]!);

        var pkt = JsonNode.Parse(await http.GetStringAsync($"/api/packet?projectId={(long)first["projectId"]!}"))!;
        Assert.Contains((string)first["text"]!, (string)pkt["text"]!);
        Assert.True((int)pkt["decisionCount"]! >= 1);

        var post = new HttpRequestMessage(HttpMethod.Post, "/api/decisions") { Content = new StringContent($"{{\"projectId\":{(long)first["projectId"]!},\"type\":\"preference\",\"text\":\"Kısa cevap isterim.\"}}", Encoding.UTF8, "application/json") };
        post.Headers.Add("X-Soz-Token", h.Token);
        var created = await http.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("approved", (string?)JsonNode.Parse(await created.Content.ReadAsStringAsync())!["status"]);

        var repeats = JsonNode.Parse(await http.GetStringAsync("/api/repeats?min=2"))!.AsArray();
        Assert.NotEmpty(repeats);
        Assert.True(repeats[0]!.AsObject().ContainsKey("members"));
        var utt = JsonNode.Parse(await http.GetStringAsync("/api/utterances?q=t%C3%BCrk%C3%A7e&limit=5"))!;
        Assert.True((long)utt["total"]! >= 5);
        Assert.Equal(5, utt["items"]!.AsArray().Count);
        var sessions = JsonNode.Parse(await http.GetStringAsync("/api/sessions?hours=1000&includeStale=true"))!.AsArray();
        Assert.Equal(4, sessions.Count);
        foreach (var k in new[] { "id", "tool", "projectId", "projectName", "title", "kind", "state", "since", "lastUserText", "lastUserTs", "lastAssistantText", "lastAssistantTs", "asksQuestion", "claimsDone", "showsEvidence", "subCount" })
            Assert.True(sessions[0]!.AsObject().ContainsKey(k), k);
        var digest = JsonNode.Parse(await http.GetStringAsync("/api/digest?hours=1000"))!;
        Assert.True(digest.AsObject().ContainsKey("stalled"));
        var report = JsonNode.Parse(await http.GetStringAsync("/api/report?days=30"))!;
        Assert.True(report.AsObject().ContainsKey("topRepeats"));

        // Sade anlat kapalı → 409
        var sid = (long)sessions[0]!["id"]!;
        var ex = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sid}/explain");
        ex.Headers.Add("X-Soz-Token", h.Token);
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(ex)).StatusCode);

        // Ayarlar
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = new StringContent("{\"theme\":\"dark\",\"packetBudget\":1500}", Encoding.UTF8, "application/json") };
        put.Headers.Add("X-Soz-Token", h.Token);
        var st = JsonNode.Parse(await (await http.SendAsync(put)).Content.ReadAsStringAsync())!;
        Assert.Equal("dark", (string?)st["theme"]);
        Assert.Equal(1500, (int)st["packetBudget"]!);
        var badPut = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = new StringContent("{\"theme\":\"mor\"}", Encoding.UTF8, "application/json") };
        badPut.Headers.Add("X-Soz-Token", h.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(badPut)).StatusCode);

        // Dışa aktarma: gerçek proje yolu → 409
        var exp = new HttpRequestMessage(HttpMethod.Post, "/api/export") { Content = new StringContent($"{{\"projectId\":{(long)first["projectId"]!},\"agents\":true}}", Encoding.UTF8, "application/json") };
        exp.Headers.Add("X-Soz-Token", h.Token);
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(exp)).StatusCode);

        // SSE: içe alma olayı akışa düşer
        using var sseClient = new HttpClient { BaseAddress = new Uri(h.Url), Timeout = TimeSpan.FromSeconds(30) };
        using var sse = await sseClient.GetAsync("/api/events/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", sse.Content.Headers.ContentType?.MediaType);
        var stream = await sse.Content.ReadAsStreamAsync();
        var imp = new HttpRequestMessage(HttpMethod.Post, "/api/import") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        imp.Headers.Add("X-Soz-Token", h.Token);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(imp)).StatusCode);
        using var reader = new StreamReader(stream);
        var got = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !got.ToString().Contains("event: import"))
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            got.AppendLine(line);
        }
        Assert.Contains("event: import", got.ToString());

        // SPA geri dönüşü ve bilinmeyen API
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/yok")).StatusCode);
    }

    [Fact]
    public async Task Port_Fallback_When_Busy()
    {
        using var env = new TestEnv(copySamples: false);
        await using var a = await SozServer.StartAsync(env.DataDir, 49340);
        await using var b = await SozServer.StartAsync(Path.Combine(env.Root, "veri2"), 49340);
        Assert.NotEqual(a.Url, b.Url);
    }
}
