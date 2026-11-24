using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Text;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>s1 / B4: iş metnine yazılan anahtar hiçbir okuma cevabında düz görünmez; maske tek yerdedir ve yeni uçları kendiliğinden kapsar.</summary>
public class S1CikisMaskeTests
{
    private const string Key = "sk-ant-api03-S1SAHTEANAHTAR9876543210zyxwvutsrqpon";
    private const string Pass = "s1parola99xyz";

    [Fact]
    public async Task Butun_okuma_uclari_anahtari_maskeler_ve_yazma_cevabi_uyarir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        await using var h = await SozServer.StartAsync(env.DataDir, 49880);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        HttpRequestMessage Req(HttpMethod m, string url, object? body = null)
        {
            var r = new HttpRequestMessage(m, url);
            if (body is not null) r.Content = JsonContent.Create(body);
            if (m != HttpMethod.Get) r.Headers.Add("X-Soz-Token", h.Token);
            return r;
        }

        var created = await http.SendAsync(Req(HttpMethod.Post, "/api/jobs", new { projectId = pid, rawText = $"Şunu kullan: {Key} ve şifre: {Pass} sonra testleri koş." }));
        var body = await created.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain(Key, body);
        Assert.DoesNotContain(Pass, body);
        var job = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(SecretFilter.JobWarning, (string?)job["secretWarning"]);
        var jobId = (long)job["id"]!;

        // Gizli anahtarlar veritabanına yazılırken de maskelenir, ne diskte ne HTTP yanıtında düz kalır.
        Assert.DoesNotContain(Key, w.Job(jobId).RawText);
        Assert.DoesNotContain(Pass, w.Job(jobId).RawText);
        using (var c = env.Svc.Db.Open())
        {
            var rawOnDisk = c.Scalar("SELECT raw_text FROM jobs WHERE id=@id", null, ("@id", jobId)) as string ?? "";
            var promptOnDisk = c.Scalar("SELECT prompt_text FROM jobs WHERE id=@id", null, ("@id", jobId)) as string ?? "";
            Assert.DoesNotContain(Key, rawOnDisk);
            Assert.DoesNotContain(Pass, rawOnDisk);
            Assert.DoesNotContain(Key, promptOnDisk);
            Assert.DoesNotContain(Pass, promptOnDisk);
        }

        var warn = await (await http.SendAsync(Req(HttpMethod.Post, "/api/jobs/text-check", new { text = "Hesap makinesini düzelt" }))).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Null(warn!["secretWarning"]);
        var warn2 = await (await http.SendAsync(Req(HttpMethod.Post, "/api/jobs/text-check", new { text = "anahtar " + Key }))).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(SecretFilter.JobWarning, (string?)warn2!["secretWarning"]);

        // Gerçek araç çalıştıran uçlar taranmaz (gerçek claude/codex çalıştırılmaz).
        var skip = new[] { "/api/session", "/api/events/stream", "/api/agents", "/api/agents/ready", "/api/doctor", "/api/phone/" };
        var swept = 0;
        var gets = h.Routes().Where(r => r.Method == "GET").Select(r => r.Pattern).Distinct().ToList();
        foreach (var pattern in gets)
        {
            if (skip.Any(s => pattern.StartsWith(s, StringComparison.OrdinalIgnoreCase) && (pattern.Length == s.Length || s.EndsWith("/") || pattern[s.Length] == '/'))) continue;
            var num = (pattern.Contains("/projects/") ? pid : jobId).ToString();
            var url = Regex.Replace(pattern, @"\{(\w+)(:\w+)?\??\}", m => m.Groups[2].Success ? num : "x.txt");
            var res = await http.SendAsync(Req(HttpMethod.Get, url));
            var text = await res.Content.ReadAsStringAsync();
            Assert.False(text.Contains(Key) || text.Contains(Pass), $"{url} ({(int)res.StatusCode}) anahtarı düz gösterdi");
            swept++;
        }
        Assert.True(swept >= 20, $"yalnız {swept} okuma ucu tarandı");

        // İş uçları: maskeli ama kullanılabilir.
        var one = await http.GetStringAsync($"/api/jobs/{jobId}");
        Assert.Contains("•••", one);
        var now = await http.GetStringAsync("/api/now");
        Assert.DoesNotContain(Key, now);
        var digest = await http.GetStringAsync("/api/digest");
        Assert.DoesNotContain(Key, digest);
    }

    [Fact]
    public async Task Yapisal_alanlar_ve_yollar_maskelenmez()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        await using var h = await SozServer.StartAsync(env.DataDir, 49880);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var projects = await http.GetStringAsync("/api/projects");
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(dir, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Trim('"'), projects);
        Assert.False(OutputMask.IsStructural("rawText"));
        Assert.True(OutputMask.IsStructural("jobUid") && OutputMask.IsStructural("id") && !OutputMask.IsStructural("url") && !OutputMask.IsStructural("artifactUrl"));
        _ = pid;
    }

    [Fact]
    public void Surucu_harfli_ileri_bolulu_yol_anahtar_sayilmaz()
    {
        Assert.Equal("D:/tools/dev/dotnet10/dotnet.exe", SecretFilter.MaskSecrets("D:/tools/dev/dotnet10/dotnet.exe"));
        Assert.Contains("•••", SecretFilter.MaskSecrets("anahtar " + Key));
    }

    [Fact]
    public void Zaman_damgasi_ve_uid_anahtar_sayilmaz()
    {
        foreach (var t in new[] { "2026-11-12T04:10:27.123Z", "2026-11-12T04:10:27Z", "2026-11-12T04:10:27.1234567+03:00", "2026-11-12", "2026-11-12 04:10", "01a1cd5a-b528-770e-b785-58b331f8ad19" })
            Assert.Equal(t, SecretFilter.MaskSecrets(t));
        var dirty = System.Text.Encoding.UTF8.GetBytes("{\"at\":\"2026-11-12T04:10:27.123Z\",\"sentence\":\"x\"}");
        Assert.Same(dirty, OutputMask.MaskJson(dirty));
    }

    [Fact]
    public void MaskJson_degismeyen_cevabi_bayt_bayt_ayni_birakir()
    {
        var plain = System.Text.Encoding.UTF8.GetBytes("{\"a\":\"merhaba dünya\",\"n\":1.50}");
        Assert.Same(plain, OutputMask.MaskJson(plain));
        var dirty = System.Text.Encoding.UTF8.GetBytes("{\"rawText\":\"x " + Key + "\",\"jobUid\":\"" + Key + "\"}");
        var outp = System.Text.Encoding.UTF8.GetString(OutputMask.MaskJson(dirty));
        Assert.DoesNotContain("\"rawText\":\"x " + Key, outp);
        Assert.Contains("\"jobUid\":\"" + Key + "\"", outp); // yapısal alan dokunulmaz
    }
}
