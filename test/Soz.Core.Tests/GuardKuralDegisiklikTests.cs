using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Guard;
using Soz.Core.Readers;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>A-KOR-03: kullanıcı bir kuralı "önce sor"dan "durdur"a çevirince sunucu 200 dönüp kuralı eski hâlinde bırakıyordu. İstenen ile kaydedilen hep aynı olmalı, olamıyorsa açık ret dönmeli.</summary>
public class GuardKuralDegisiklikTests
{
    private const string AskLabel = "Zorla gönderme ve sert sıfırlama önce sorulur";

    private static (long pid, string dir) Setup(TestEnv env)
    {
        var dir = Path.Combine(env.Root, "projem");
        Directory.CreateDirectory(dir);
        using var c = env.Svc.Db.Open();
        var pid = IngestStore.EnsureProject(c, null, dir);
        GuardStore.EnsureTables(c);
        GuardStore.ApplyTemplate(c, pid, "temkinli");
        return (pid, dir);
    }

    private static GuardRule Ask(SqliteConnectionHolder h, long pid) => h.Rules.First(r => r.Label == AskLabel);

    private sealed class SqliteConnectionHolder : IDisposable
    {
        public Microsoft.Data.Sqlite.SqliteConnection C { get; }
        public SqliteConnectionHolder(TestEnv env) => C = env.Svc.Db.Open();
        public List<GuardRule> Rules => GuardStore.List(C, null);
        public void Dispose() => C.Dispose();
    }

    private static string Verdict(TestEnv env, long pid, string dir, string cmd)
        => GuardService.Test(env.Svc.Db, pid, "Bash", JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new { command = cmd })), dir).Verdict;

    private static SozException Rejected(Action a) => Assert.Throws<SozException>(a);

    [Fact]
    public void Sor_durdura_cevrilir_geri_okunur_ve_gercekten_durdurur()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, dir) = Setup(env);
        using var h = new SqliteConnectionHolder(env);
        var rule = Ask(h, pid);
        Assert.Equal("ask", rule.Action);
        Assert.Equal("ask", Verdict(env, pid, dir, "git push --force"));

        var res = GuardStore.PatchFromBody(h.C, rule.Id, new JsonObject { ["action"] = "deny" });
        Assert.Equal("deny", res.Action);
        // cevap (arayüzün gördüğü) ve ayrı bir okuma aynı değeri söylemeli
        Assert.Equal("deny", (string?)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(GuardStore.Dto(res)))!["action"]);
        Assert.Equal("deny", GuardStore.Get(h.C, rule.Id)!.Action);
        // kural gerçekten durduruyor
        Assert.Equal("deny", Verdict(env, pid, dir, "git push --force"));
        Assert.Contains("engellenir", GuardText.Describe(GuardStore.Get(h.C, rule.Id)!));
    }

    [Fact]
    public void Durdurdan_sora_donus_izinli_ve_uygulanir()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, dir) = Setup(env);
        using var h = new SqliteConnectionHolder(env);
        var rule = Ask(h, pid);
        GuardStore.PatchFromBody(h.C, rule.Id, new JsonObject { ["action"] = "deny" });
        var back = GuardStore.PatchFromBody(h.C, rule.Id, new JsonObject { ["action"] = "ask" });
        Assert.Equal("ask", back.Action);
        Assert.Equal("ask", GuardStore.Get(h.C, rule.Id)!.Action);
        Assert.Equal("ask", Verdict(env, pid, dir, "git push --force"));
        // komut kuralı için de aynı yön çalışır
        var cb = GuardStore.Create(h.C, pid, "command_block", "Whoami yasak", new JsonObject { ["patterns"] = new JsonArray("whoami") }, "deny");
        Assert.Equal("ask", GuardStore.PatchFromBody(h.C, cb.Id, new JsonObject { ["action"] = "ask" }).Action);
        Assert.Equal("ask", Verdict(env, pid, dir, "whoami"));
        Assert.Equal("deny", GuardStore.PatchFromBody(h.C, cb.Id, new JsonObject { ["action"] = "deny" }).Action);
        Assert.Equal("deny", Verdict(env, pid, dir, "whoami"));
    }

    [Fact]
    public void Yerlesik_sablon_kurali_da_sikilasir_ve_cumlesi_dogru_kalir()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, _) = Setup(env);
        using var h = new SqliteConnectionHolder(env);
        var rule = Ask(h, pid);
        Assert.Equal("template", rule.Origin);
        Assert.Contains("sorulur", GuardText.Describe(rule));
        var res = GuardStore.PatchFromBody(h.C, rule.Id, new JsonObject { ["action"] = "deny" });
        // sıkılaştırılmış şablon kuralı hâlâ "önce sorulur" cümlesini taşımamalı
        Assert.DoesNotContain("önce sorulur", GuardText.Describe(res));
        Assert.Contains("engellenir", GuardText.Describe(res));
        // şablonu yeniden uygulamak kullanıcının seçimini ezmez
        var again = GuardStore.ApplyTemplate(h.C, pid, "temkinli");
        Assert.Equal("deny", GuardStore.Get(h.C, rule.Id)!.Action);
        Assert.Empty(again.Created);
    }

    [Fact]
    public void Olmayan_kural_404_dondurur()
    {
        using var env = new TestEnv(copySamples: false);
        Setup(env);
        using var h = new SqliteConnectionHolder(env);
        Assert.Equal(404, Rejected(() => GuardStore.PatchFromBody(h.C, 987654, new JsonObject { ["action"] = "deny" })).Status);
        Assert.False(GuardStore.Delete(h.C, 987654));
    }

    [Fact]
    public void Gecersiz_ya_da_yutulacak_istekler_sessiz_basari_degil_acik_ret_olur()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, _) = Setup(env);
        using var h = new SqliteConnectionHolder(env);
        var rule = Ask(h, pid);
        void Bad(JsonObject body, string parca)
        {
            var e = Rejected(() => GuardStore.PatchFromBody(h.C, rule.Id, body));
            Assert.Equal(400, e.Status);
            Assert.Contains(parca, e.Message);
            var now = GuardStore.Get(h.C, rule.Id)!;
            Assert.Equal(rule.Action, now.Action);
            Assert.Equal(rule.Label, now.Label);
            Assert.Equal(rule.Enabled, now.Enabled);
        }
        Bad(new JsonObject { ["action"] = "izin" }, "deny");
        Bad(new JsonObject { ["action"] = 5 }, "metin");
        Bad(new JsonObject { ["enabled"] = "false" }, "doğru ya da yanlış");
        Bad(new JsonObject { ["label"] = 3 }, "metin");
        Bad(new JsonObject { ["label"] = "  " }, "boş");
        Bad(new JsonObject { ["spec"] = new JsonArray("x") }, "nesne");
        Bad(new JsonObject { ["spec"] = new JsonObject() }, "ask_before");
        Bad(new JsonObject { ["actoin"] = "deny" }, "Bilinmeyen alan");
        Bad(new JsonObject(), "Değiştirilecek");
        Bad(new JsonObject { ["kind"] = "command_block" }, "türü");
        Bad(new JsonObject { ["projectId"] = pid + 1 }, "taşınamaz");
        Bad(new JsonObject { ["origin"] = "user" }, "kaynağı");
        // değişmeyen alanların geri gönderilmesi zararsız
        var ok = GuardStore.PatchFromBody(h.C, rule.Id, new JsonObject { ["kind"] = rule.Kind, ["projectId"] = pid, ["origin"] = rule.Origin, ["id"] = rule.Id, ["action"] = "deny" });
        Assert.Equal("deny", ok.Action);
    }

    [Fact]
    public void Ekleme_gecersiz_proje_kaynak_ve_tur_hatalarini_acikca_reddeder_ask_before_deny_uygulanir()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, dir) = Setup(env);
        using var h = new SqliteConnectionHolder(env);
        var spec = () => new JsonObject { ["patterns"] = new JsonArray("whoami") };
        Assert.Equal(404, Rejected(() => GuardStore.CreateFromBody(h.C, new JsonObject { ["projectId"] = 424242, ["kind"] = "command_block", ["spec"] = spec() })).Status);
        Assert.Equal(400, Rejected(() => GuardStore.CreateFromBody(h.C, new JsonObject { ["projectId"] = "x", ["kind"] = "command_block", ["spec"] = spec() })).Status);
        Assert.Equal(400, Rejected(() => GuardStore.CreateFromBody(h.C, new JsonObject { ["kind"] = "command_block", ["spec"] = spec(), ["origin"] = "baska" })).Status);
        Assert.Equal(400, Rejected(() => GuardStore.CreateFromBody(h.C, new JsonObject { ["kind"] = "command_block", ["spec"] = spec(), ["action"] = "izin" })).Status);
        Assert.Equal(400, Rejected(() => GuardStore.CreateFromBody(h.C, new JsonObject { ["kind"] = "command_block", ["spec"] = "x" })).Status);
        Assert.Equal(400, Rejected(() => GuardStore.CreateFromBody(h.C, new JsonObject { ["kind"] = "command_block", ["spec"] = spec(), ["enabled"] = "evet" })).Status);
        Assert.Equal(400, Rejected(() => GuardStore.CreateFromBody(h.C, new JsonObject { ["spec"] = spec() })).Status);
        // ask_before + deny: kaydedilen de, cevap da, davranış da deny
        var r = GuardStore.CreateFromBody(h.C, new JsonObject { ["projectId"] = pid, ["kind"] = "ask_before", ["label"] = "Kim bu", ["spec"] = spec(), ["action"] = "deny" });
        Assert.Equal("deny", r.Action);
        Assert.Equal("deny", Verdict(env, pid, dir, "whoami"));
        // action verilmezse ask_before ask olur
        var r2 = GuardStore.CreateFromBody(h.C, new JsonObject { ["projectId"] = pid, ["kind"] = "ask_before", ["spec"] = new JsonObject { ["patterns"] = new JsonArray("hostname") } });
        Assert.Equal("ask", r2.Action);
    }

    [Fact]
    public async Task Uc_PATCH_200_ile_yeni_degeri_doner_gecersizde_400_404_ve_geri_okuma_tutarli()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, _) = Setup(env);
        await using var h = await SozServer.StartAsync(env.DataDir, 0);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        HttpRequestMessage Req(HttpMethod m, string url, object? body = null)
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Add("X-Soz-Token", h.Token);
            if (body is not null) r.Content = JsonContent.Create(body);
            return r;
        }
        var rules = JsonNode.Parse(await http.GetStringAsync($"/api/guard/rules?projectId={pid}"))!.AsArray();
        var id = (long)rules.First(x => (string?)x!["label"] == AskLabel)!["id"]!;

        var ok = await http.SendAsync(Req(HttpMethod.Patch, $"/api/guard/rules/{id}", new { action = "deny" }));
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("deny", (string?)JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["action"]);
        var after = JsonNode.Parse(await http.GetStringAsync($"/api/guard/rules?projectId={pid}"))!.AsArray();
        Assert.Equal("deny", (string?)after.First(x => (long)x!["id"]! == id)!["action"]);

        var bad = await http.SendAsync(Req(HttpMethod.Patch, $"/api/guard/rules/{id}", new { action = "izin" }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((string?)JsonNode.Parse(await bad.Content.ReadAsStringAsync())!["error"]));
        var none = await http.SendAsync(Req(HttpMethod.Patch, "/api/guard/rules/987654", new { action = "deny" }));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, none.StatusCode);
        var kind = await http.SendAsync(Req(HttpMethod.Patch, $"/api/guard/rules/{id}", new { kind = "scope_only" }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, kind.StatusCode);
        var noproj = await http.SendAsync(Req(HttpMethod.Post, "/api/guard/rules", new { projectId = 424242, kind = "command_block", spec = new { patterns = new[] { "x" } } }));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, noproj.StatusCode);
        var del = await http.SendAsync(Req(HttpMethod.Delete, "/api/guard/rules/987654"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, del.StatusCode);
    }
}
