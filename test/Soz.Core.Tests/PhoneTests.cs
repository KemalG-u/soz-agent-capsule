using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Hooks;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Telefon (M11) güvenlik yüzeyi, düşmanca. Yerel ağ uygulaması sınamada 127.0.0.1 üzerinde AYRI bir portta açılır
/// (PhoneServer.AddressOverride); ana uygulamayla aynı süreçte ama ayrı Kestrel örneğidir. Gerçek yerel ağ adresiyle sınama e2e betiğindedir.
/// </summary>
[Collection("phone")] // AddressOverride süreç genelidir: bu sınıf tek başına koşar
public class PhoneTests
{
    static PhoneTests() => PhoneServer.AddressOverride = new[] { IPAddress.Loopback };

    private sealed record Raw(int Status, string Head, string Body);

    /// <summary>Ham HTTP/1.1 (HttpClient yolu normalleştirdiği için "..", ters bölü, Host oyunları buradan).</summary>
    private static async Task<Raw> Send(int port, string method, string target, string? host = null, string? cookie = null, string? body = null, string? extra = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var s = tcp.GetStream();
        var bytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append($"{method} {target} HTTP/1.1\r\nHost: {host ?? "127.0.0.1:" + port}\r\nConnection: close\r\n");
        if (cookie is not null) sb.Append($"Cookie: {cookie}\r\n");
        if (body is not null) sb.Append($"Content-Type: application/json\r\nContent-Length: {bytes.Length}\r\n");
        if (extra is not null) sb.Append(extra);
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
        if (bytes.Length > 0) { try { await s.WriteAsync(bytes); } catch (IOException) { } }
        using var ms = new MemoryStream();
        try { await s.CopyToAsync(ms); } catch (IOException) { }
        var text = Encoding.UTF8.GetString(ms.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var status = int.Parse(head.Split(' ')[1]);
        var body2 = split < 0 ? "" : text[(split + 4)..];
        if (head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)) body2 = Dechunk(body2);
        return new Raw(status, head, body2);
    }

    private static string Dechunk(string s)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var nl = s.IndexOf("\r\n", i, StringComparison.Ordinal);
            if (nl < 0) break;
            var size = Convert.ToInt32(s[i..nl].Split(';')[0].Trim(), 16);
            if (size == 0) break;
            // Boyut bayt cinsindendir; gövde ASCII dışı içerebileceği için bayt düzeyinde kes.
            var rest = Encoding.UTF8.GetBytes(s[(nl + 2)..]);
            var chunk = Encoding.UTF8.GetString(rest, 0, Math.Min(size, rest.Length));
            sb.Append(chunk);
            i = nl + 2 + chunk.Length + 2;
        }
        return sb.ToString();
    }

    private static async Task<HttpResponseMessage> Put(HttpClient http, string token, object body)
    {
        var r = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        r.Headers.Add("X-Soz-Token", token);
        return await http.SendAsync(r);
    }

    private static int PortOf(string url) => new Uri(url).Port;

    [Fact]
    public async Task Varsayilan_kapali_ve_kapaliyken_yerel_agda_dinleme_yok()
    {
        using var env = new TestEnv(copySamples: false);
        await using var h = await SozServer.StartAsync(env.DataDir, 49300);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        Assert.False(h.Phone.Listening);
        Assert.Empty(h.Phone.Urls);
        var p = JsonNode.Parse(await http.GetStringAsync("/api/phone/pairing"))!;
        Assert.False((bool)p["enabled"]!);
        Assert.Null(p["url"]);
        // Ana uygulamada telefon uçları yok (yalnız eşleştirme bilgisi).
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/phone/now")).StatusCode);
    }

    [Fact]
    public async Task Eslestirme_cerez_ve_yasak_uclar_dusmanca()
    {
        using var env = new TestEnv(copySamples: false);
        var projDir = Path.Combine(env.Root, "kuyruk-proje");
        Directory.CreateDirectory(projDir);
        HookHandler.Run("session-start", new StringReader(JsonSerializer.Serialize(new { session_id = "kq-1", cwd = projDir, source = "startup" })), new StringWriter(), env.DataDir, _ => null);
        await using var h = await SozServer.StartAsync(env.DataDir, 49300);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };

        // Belirteçsiz ayar değişikliği reddedilir (telefon açılamaz).
        var noTok = await http.PutAsync("/api/settings", new StringContent("{\"lanEnabled\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, noTok.StatusCode);
        Assert.False(h.Phone.Listening);

        Assert.True((await Put(http, h.Token, new { lanEnabled = true })).IsSuccessStatusCode);
        Assert.True(h.Phone.Listening);
        var pairing = JsonNode.Parse(await http.GetStringAsync("/api/phone/pairing"))!;
        var url = (string)pairing["url"]!;
        Assert.Matches(@"/t/\?k=[0-9a-f]{32}$", url);
        var code = url[^32..];
        var port = PortOf(url);
        Assert.NotEqual(PortOf(h.Url), port);

        // --- kodsuz: 401, yanlış çerez: 403
        Assert.Equal(401, (await Send(port, "GET", "/api/phone/now")).Status);
        Assert.Equal(403, (await Send(port, "GET", "/api/phone/now", cookie: "soz_t=" + new string('a', 64))).Status);
        Assert.Equal(403, (await Send(port, "GET", "/api/phone/now", cookie: "soz_t=" + code)).Status); // kodun kendisi çerez değildir
        Assert.Equal(401, (await Send(port, "POST", "/api/phone/queue", body: "{\"projectId\":1,\"text\":\"x\"}")).Status);

        // --- yanlış kod: 403 ve çerez YOK
        var bad = await Send(port, "GET", "/t/?k=" + new string('0', 32));
        Assert.Equal(403, bad.Status);
        Assert.DoesNotContain("Set-Cookie", bad.Head, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(403, (await Send(port, "GET", "/t/?k=" + code[..31])).Status);        // eksik
        Assert.Equal(403, (await Send(port, "GET", "/t/?k=" + code.ToUpperInvariant() + "0")).Status); // fazla

        // --- doğru kod: HttpOnly + SameSite=Strict çerez, kod adreste kalmaz
        var ok = await Send(port, "GET", "/t/?k=" + code);
        Assert.InRange(ok.Status, 301, 303);
        var setCookie = ok.Head.Split("\r\n").First(l => l.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Location: /t/\r", ok.Head + "\r");
        var cookie = setCookie.Split(':', 2)[1].Trim().Split(';')[0];
        Assert.DoesNotContain(code, cookie);

        // Telefon sayfası gerçekten sunulur (yönlendirme sırası hatası /t/'yi 404 yapıyordu).
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "t", "index.html")))
        {
            var pg = await Send(port, "GET", "/t/", cookie: cookie);
            Assert.Equal(200, pg.Status);
            Assert.Contains("/t/assets/", pg.Body);
        }

        var now = await Send(port, "GET", "/api/phone/now", cookie: cookie);
        Assert.Equal(200, now.Status);
        var nj = JsonNode.Parse(now.Body)!;
        Assert.NotNull(nj["waiting"]);
        Assert.NotNull(nj["morning"]);

        // --- yerel ağdan başka HİÇBİR uç yok (geçerli çerezle bile)
        foreach (var path in new[] { "/api/settings", "/api/session", "/api/decisions", "/api/utterances?q=a", "/api/summary", "/api/packet", "/api/sessions",
                     "/api/proof/artifact/1/cikti-1.txt", "/api/events/stream", "/api/phone/pairing", "/api/guard/rules", "/api/jobs", "/api/now", "/api/morning",
                     "/api/usage", "/api/doctor", "/", "/index.html", "/api", "/api/", "/API/SETTINGS", "/api/phone/pairing/renew" })
        {
            var r = await Send(port, "GET", path, cookie: cookie);
            Assert.True(r.Status == 404, $"{path} → {r.Status}");
        }
        foreach (var path in new[] { "/api/import", "/api/settings", "/api/phone/pairing/renew", "/api/decisions", "/api/queue" })
        {
            var r = await Send(port, "POST", path, cookie: cookie, body: "{}");
            Assert.True(r.Status == 404, $"POST {path} → {r.Status}");
        }
        Assert.Equal(404, (await Send(port, "PUT", "/api/settings", cookie: cookie, body: "{\"lanEnabled\":false}")).Status);
        Assert.Equal(405, (await Send(port, "POST", "/t/", cookie: cookie, body: "{}")).Status);

        // --- yol kaçışı
        foreach (var t in new[] { "/t/../api/settings", "/t/..%2fapi/settings", "/t/%2e%2e/api/settings", "/t/..\\api\\settings", "/t/%2E%2E/%2E%2E/soz.db",
                     "/t/../../veri/soz.db", "/t/%5c..%5capi%5csettings", "/t/index.html%00.txt", "//api/settings", "/t//../api/settings" })
        {
            var r = await Send(port, "GET", t, cookie: cookie);
            Assert.True(r.Status is 400 or 404, $"{t} → {r.Status}");
            Assert.DoesNotContain("lanEnabled", r.Body);
        }

        // --- Host başlığı oyunları
        Assert.Equal(400, (await Send(port, "GET", "/api/phone/now", host: "evil.example", cookie: cookie)).Status);
        Assert.Equal(400, (await Send(port, "GET", "/api/phone/now", host: "localhost:" + port, cookie: cookie)).Status);
        Assert.Equal(400, (await Send(port, "GET", "/api/phone/now", host: "127.0.0.1:" + PortOf(h.Url), cookie: cookie)).Status);
        Assert.Equal(400, (await Send(port, "GET", "/api/phone/now", host: "", cookie: cookie)).Status);
        // ana uygulama yerel ağ Host'unu kabul etmez
        Assert.Equal(400, (await Send(PortOf(h.Url), "GET", "/api/settings", host: "192.168.1.20:" + PortOf(h.Url))).Status);

        // --- kuyruk: boş, çok uzun metin, çok uzun gövde, bozuk JSON, başka köken
        Assert.Equal(400, (await Send(port, "POST", "/api/phone/queue", cookie: cookie, body: "{\"projectId\":1,\"text\":\"   \"}")).Status);
        Assert.Equal(400, (await Send(port, "POST", "/api/phone/queue", cookie: cookie, body: JsonSerializer.Serialize(new { projectId = 1, text = new string('x', PhoneServer.MaxQueueText + 1) }))).Status);
        var huge = await Send(port, "POST", "/api/phone/queue", cookie: cookie, body: JsonSerializer.Serialize(new { projectId = 1, text = new string('y', 40_000) }));
        Assert.Equal(413, huge.Status);
        Assert.Equal(400, (await Send(port, "POST", "/api/phone/queue", cookie: cookie, body: "{bozuk")).Status);
        Assert.Equal(403, (await Send(port, "POST", "/api/phone/queue", cookie: cookie, body: "{\"projectId\":1,\"text\":\"a\"}", extra: "Origin: http://evil.example\r\n")).Status);

        // --- geçerli kuyruk mesajı, sonra hız sınırı
        var star = JsonNode.Parse(await http.GetStringAsync("/api/projects"))!.AsArray().First()!["id"]!.GetValue<long>();
        var good = await Send(port, "POST", "/api/phone/queue", cookie: cookie, body: JsonSerializer.Serialize(new { projectId = star, text = "Telefondan: devam et." }));
        Assert.Equal(201, good.Status);
        var q = JsonNode.Parse(await http.GetStringAsync("/api/queue"))!.AsArray();
        Assert.Contains(q, x => (string?)x!["origin"] == "telefon" && (string?)x["text"] == "Telefondan: devam et.");
        var statuses = new List<int>();
        for (var i = 0; i < PhoneServer.QueuePerMinute + 2; i++)
            statuses.Add((await Send(port, "POST", "/api/phone/queue", cookie: cookie, body: JsonSerializer.Serialize(new { projectId = star, text = "m" + i }))).Status);
        Assert.Contains(429, statuses);

        // --- kod yenilenince eski çerez geçersiz
        var renew = new HttpRequestMessage(HttpMethod.Post, "/api/phone/pairing/renew");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/phone/pairing/renew"))).StatusCode);
        renew.Headers.Add("X-Soz-Token", h.Token);
        var renewed = JsonNode.Parse(await (await http.SendAsync(renew)).Content.ReadAsStringAsync())!;
        var code2 = ((string)renewed["url"]!)[^32..];
        Assert.NotEqual(code, code2);
        Assert.Equal(403, (await Send(port, "GET", "/api/phone/now", cookie: cookie)).Status);
        Assert.Equal(403, (await Send(port, "GET", "/t/?k=" + code)).Status);
        var ok2 = await Send(port, "GET", "/t/?k=" + code2);
        var cookie2 = ok2.Head.Split("\r\n").First(l => l.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim().Split(';')[0];
        Assert.Equal(200, (await Send(port, "GET", "/api/phone/now", cookie: cookie2)).Status);

        // --- kapatınca yerel ağ dinlemesi biter
        Assert.True((await Put(http, h.Token, new { lanEnabled = false })).IsSuccessStatusCode);
        Assert.False(h.Phone.Listening);
        await Assert.ThrowsAnyAsync<SocketException>(async () => { using var t = new TcpClient(); await t.ConnectAsync(IPAddress.Loopback, port); });
    }

    [Fact]
    public async Task Telefon_metinleri_maskeli_ve_200_karakter()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "tel-proje");
        Directory.CreateDirectory(dir);
        var secret = "sk-ant-api03-" + new string('Q', 40);
        var longQ = "Şu anahtarı kullanayım mı: " + secret + " " + new string('u', 400) + " devam edeyim mi?";
        var o = new StringWriter();
        HookHandler.Run("stop", new StringReader(JsonSerializer.Serialize(new Dictionary<string, object?> { ["session_id"] = "tel-1", ["cwd"] = dir, ["stop_hook_active"] = false, ["last_assistant_message"] = longQ })), o, env.DataDir, _ => null);
        HookHandler.Run("notification", new StringReader(JsonSerializer.Serialize(new { session_id = "tel-1", cwd = dir, notification_type = "idle_prompt", message = "Claude is waiting " + secret })), o, env.DataDir, _ => null);

        await using var h = await SozServer.StartAsync(env.DataDir, 49300);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        Assert.True((await Put(http, h.Token, new { lanEnabled = true })).IsSuccessStatusCode);
        var payload = JsonSerializer.Serialize(h.Phone.NowPayload(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(secret, payload);
        Assert.DoesNotContain("QQQQQQQQ", payload);
        var n = JsonNode.Parse(payload)!;
        var w = Assert.Single(n["waiting"]!.AsArray());
        var text = (string?)w!["lastAssistantText"];
        Assert.NotNull(text);
        Assert.True(text!.Length <= PhoneServer.PhoneTextChars, $"uzunluk {text.Length}");
        Assert.True(PhoneServer.Short(new string('a', 1000))!.Length <= 200);
        Assert.Null(PhoneServer.Short("  "));
    }
}

[CollectionDefinition("phone", DisableParallelization = true)]
public class PhoneCollection { }
