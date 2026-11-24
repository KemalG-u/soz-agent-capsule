using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>s2 / B8 ve N7: gölge kip yalnız kendiliğinden çıkan öneriyi susturur; kişinin kendi açtığı Öneriler sayfası dayanağı olan öneriyi her zaman gösterir.</summary>
public class S2OneriTests
{
    private static HttpRequestMessage Post(string url, string token, object body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        r.Headers.Add("X-Soz-Token", token);
        return r;
    }

    [Fact]
    public async Task Golge_kip_acikken_sayfa_gosterir_kendiliginden_cikan_susar_ve_bos_cumle_tektir()
    {
        using var env = new TestEnv(copySamples: false);
        await using var h = await SozServer.StartAsync(env.DataDir, 49886);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };

        // Sıfırdan kurulum: gölge kip varsayılan açık. Boş sayfa her iki yolda da AYNI cümleyi verir.
        const string bos = "Henüz öneri yok. Söz birkaç gün neyi sık kullandığına bakar, sonra burada önerir.";
        var auto0 = JsonNode.Parse(await (await http.SendAsync(Post("/api/discovery/suggestions", h.Token, new { }))).Content.ReadAsStringAsync())!;
        var page0 = JsonNode.Parse(await (await http.SendAsync(Post("/api/discovery/suggestions", h.Token, new { surface = "page" }))).Content.ReadAsStringAsync())!;
        Assert.True((bool)auto0["shadow"]!);
        Assert.True((bool)page0["shadow"]!);
        Assert.Empty(page0["items"]!.AsArray());
        Assert.Equal(bos, (string)auto0["emptyText"]!);
        Assert.Equal(bos, (string)page0["emptyText"]!);

        // Dayanak oluşur (aynı ayar altı kez kullanıldı).
        for (var i = 0; i < 6; i++)
            Assert.True((await http.SendAsync(Post("/api/discovery/usage", h.Token, new { objectUid = "setting:ui.theme", source = "s2" }))).IsSuccessStatusCode);

        var auto = JsonNode.Parse(await (await http.SendAsync(Post("/api/discovery/suggestions", h.Token, new { }))).Content.ReadAsStringAsync())!;
        var page = JsonNode.Parse(await (await http.SendAsync(Post("/api/discovery/suggestions", h.Token, new { surface = "page" }))).Content.ReadAsStringAsync())!;
        Assert.True((bool)auto["shadow"]!);
        Assert.Empty(auto["items"]!.AsArray());                  // araya girmez: bildirim, kapsül ve iş ekranı içi susar
        Assert.NotEmpty(page["items"]!.AsArray());               // sayfa dayanağı olan öneriyi her zaman gösterir
        Assert.Equal("setting:ui.theme", (string)page["items"]![0]!["objectUid"]!);
        Assert.False(string.IsNullOrWhiteSpace((string)page["items"]![0]!["reason"]!));
        Assert.Null(page["emptyText"]);
    }
}
