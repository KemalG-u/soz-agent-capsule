using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Run;
using Soz.Core.Security;
using Soz.Server;
using Soz.Server.Endpoints;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>s2 / N8: tarayıcıda klasör seçici için güvenli klasör gezme ucu, "iş listesi devamı var" bilgisi ve ajana açık uçların jeton davranışı.</summary>
[Collection("phone")] // PhoneServer.AddressOverride süreç geneli: telefon testleriyle aynı koleksiyon
public class S2KlasorTests
{
    static S2KlasorTests() => PhoneServer.AddressOverride = new[] { IPAddress.Loopback };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string[] Names(JsonNode n) => n["folders"]!.AsArray().Select(x => (string)x!["name"]!).ToArray();

    [Fact]
    public void Gezme_yalniz_klasor_adi_verir_gizli_ve_sistem_klasorlerini_saklar_ve_siralar()
    {
        using var env = new TestEnv(copySamples: false);
        var root = Path.Combine(env.Root, "gezme");
        Directory.CreateDirectory(Path.Combine(root, "zeta"));
        Directory.CreateDirectory(Path.Combine(root, "Alfa"));
        Directory.CreateDirectory(Path.Combine(root, "beta klasörü"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "$RECYCLE.BIN"));
        var gizli = Directory.CreateDirectory(Path.Combine(root, "gizli-ozellikli"));
        gizli.Attributes |= FileAttributes.Hidden;
        var sistem = Directory.CreateDirectory(Path.Combine(root, "sistem-ozellikli"));
        sistem.Attributes |= FileAttributes.System;
        File.WriteAllText(Path.Combine(root, "SIRDOSYA.txt"), "x");

        var json = JsonSerializer.Serialize(FolderEndpoints.Browse(root), Web);
        var n = JsonNode.Parse(json)!;
        Assert.Equal(new[] { "Alfa", "beta klasörü", "zeta" }, Names(n));
        Assert.DoesNotContain("SIRDOSYA", json);
        Assert.DoesNotContain(".git", json);
        Assert.DoesNotContain("RECYCLE", json);
        Assert.Equal(Path.GetFullPath(root), (string)n["path"]!);
        Assert.Equal(Path.GetFullPath(env.Root).TrimEnd('\\'), ((string)n["parent"]!).TrimEnd('\\'));
        Assert.True((bool)n["selectable"]!);
        Assert.False((bool)n["truncated"]!);
        // alt klasör yolu doğrudan gezilebilir
        var alfa = JsonNode.Parse(JsonSerializer.Serialize(FolderEndpoints.Browse(Path.Combine(root, "Alfa")), Web))!;
        Assert.Empty(alfa["folders"]!.AsArray());
    }

    [Fact]
    public void Gezme_surucu_listesi_ve_hatalar()
    {
        var drives = JsonNode.Parse(JsonSerializer.Serialize(FolderEndpoints.Browse(null), Web))!;
        Assert.Null(drives["path"]);
        var list = drives["folders"]!.AsArray();
        Assert.NotEmpty(list);
        foreach (var d in list) Assert.Equal(Path.GetPathRoot((string)d!["path"]!), (string)d!["path"]!);

        // sürücünün kendisi gezilir ama proje olarak seçilemez ve nedeni söylenir
        var rootPath = (string)list[0]!["path"]!;
        var root = JsonNode.Parse(JsonSerializer.Serialize(FolderEndpoints.Browse(rootPath), Web))!;
        Assert.False((bool)root["selectable"]!);
        Assert.False(string.IsNullOrWhiteSpace((string?)root["reason"]));
        Assert.Null(root["parent"]);

        Assert.Equal(404, Assert.Throws<SozException>(() => FolderEndpoints.Browse(Path.Combine(rootPath, "yok-boyle-bir-klasor-s2"))).Status);
        // var olan bir DOSYANIN yolu yazılırsa "bulunamadı" denmez: sade cümle (G-DÜŞÜK-3)
        var dosyaYolu = typeof(FolderEndpoints).Assembly.Location;
        Assert.True(File.Exists(dosyaYolu));
        var dosya = Assert.Throws<SozException>(() => FolderEndpoints.Browse(dosyaYolu));
        Assert.Equal(400, dosya.Status);
        Assert.Equal("Bu bir dosya, klasör değil. Klasörün yolunu yaz.", dosya.Message);
        Assert.Equal(400, Assert.Throws<SozException>(() => FolderEndpoints.Browse("goreli\\yol")).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => FolderEndpoints.Browse(@"\\sunucu\paylasim")).Status);
        Assert.Equal(400, Assert.Throws<SozException>(() => FolderEndpoints.Browse("//sunucu/paylasim")).Status);
    }

    [Fact]
    public void Gezme_cok_klasorde_keser_ve_bildirir()
    {
        using var env = new TestEnv(copySamples: false);
        var root = Path.Combine(env.Root, "kalabalik");
        for (var i = 0; i < FolderEndpoints.MaxFolders + 5; i++) Directory.CreateDirectory(Path.Combine(root, "k" + i.ToString("D4")));
        var n = JsonNode.Parse(JsonSerializer.Serialize(FolderEndpoints.Browse(root), Web))!;
        Assert.Equal(FolderEndpoints.MaxFolders, n["folders"]!.AsArray().Count);
        Assert.True((bool)n["truncated"]!);
    }

    /// <summary>Gerçek süreçler: ajan işinin içindeki süreç klasör gezme ucunda 403 alır (token ucu gibi); insan 200 alır.</summary>
    [Fact]
    public async Task Gezme_ucu_ajana_kapali_insana_aciktir()
    {
        using var env = new TestEnv(copySamples: false);
        await using var h = await SozServer.StartAsync(env.DataDir, 49882);
        var script = Path.Combine(env.Root, "istek.mjs");
        File.WriteAllText(script, """
            const [,, base, path] = process.argv
            try { const r = await fetch(base + path); console.log('KOD ' + r.status + ' ' + (await r.text()).slice(0, 160)) } catch (e) { console.log('HATA ' + e.message) }
            """);

        async Task<string> RunAgent()
        {
            var lines = new List<string>();
            using var t = ProcessTree.StartStreaming("node", new[] { script, h.Url, "/api/fs/browse" }, env.Root, null, null, l => { lock (lines) lines.Add(l); }, null, null, env.DataDir);
            await t.WaitExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(100);
            lock (lines) return string.Join("\n", lines);
        }
        async Task<string> RunHuman()
        {
            var psi = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { script, h.Url, "/api/fs/browse" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await p.WaitForExitAsync();
            return o;
        }

        var agent = await RunAgent();
        Assert.Contains("KOD 403", agent);
        Assert.Contains(CallerIdentity.HumanOnlyMessage, agent);
        Assert.Contains("KOD 200", await RunHuman());
    }

    /// <summary>Telefon/LAN yüzeyi ayrı bir uygulamadır: klasör gezme ucu orada hiç yoktur (eşleşmiş çerez bile fark etmez).</summary>
    [Fact]
    public async Task Gezme_ucu_telefon_yuzeyinde_kapali()
    {
        using var env = new TestEnv(copySamples: false);
        await using var h = await SozServer.StartAsync(env.DataDir, 49883);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = new StringContent("{\"lanEnabled\":true}", System.Text.Encoding.UTF8, "application/json") };
        put.Headers.Add("X-Soz-Token", h.Token);
        Assert.True((await http.SendAsync(put)).IsSuccessStatusCode);
        Assert.True(h.Phone.Listening);
        var pairing = JsonNode.Parse(await http.GetStringAsync("/api/phone/pairing"))!;
        var url = (string)pairing["url"]!;
        var port = new Uri(url).Port;
        using var phone = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync("/api/fs/browse")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync("/api/fs/browse?path=C%3A%5C")).StatusCode);
        var kod = url[^32..];
        var eslesme = await phone.GetAsync("/t/?k=" + kod);
        Assert.DoesNotContain("folders", await (await phone.GetAsync("/api/fs/browse")).Content.ReadAsStringAsync());
        _ = eslesme;
    }

    // ------------------------------------------------------------------ B19: iş listesi toplam ve devamı var
    [Fact]
    public async Task Is_listesi_toplam_ve_devami_var_bilgisi_verir()
    {
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(NodeSample.Create(env));
        for (var i = 0; i < 7; i++) w.CreateJob(pid, "iş " + i);
        await using var h = await SozServer.StartAsync(env.DataDir, 49884);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };

        var r = await http.GetAsync("/api/jobs?limit=3");
        Assert.Equal("7", r.Headers.GetValues("X-Total-Count").Single());
        Assert.Equal("true", r.Headers.GetValues("X-Has-More").Single());
        Assert.Equal(3, JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsArray().Count); // gövde eskisi gibi dizi

        var env1 = JsonNode.Parse(await http.GetStringAsync("/api/jobs?limit=3&envelope=1"))!;
        Assert.Equal(7, (int)env1["total"]!);
        Assert.True((bool)env1["hasMore"]!);
        Assert.Equal(3, env1["items"]!.AsArray().Count);

        var son = JsonNode.Parse(await http.GetStringAsync("/api/jobs?limit=3&offset=6&envelope=1"))!;
        Assert.Single(son["items"]!.AsArray());
        Assert.False((bool)son["hasMore"]!);
        var ids = new[] { env1, JsonNode.Parse(await http.GetStringAsync("/api/jobs?limit=3&offset=3&envelope=1"))!, son }.SelectMany(x => x["items"]!.AsArray().Select(j => (long)j!["id"]!)).ToList();
        Assert.Equal(7, ids.Distinct().Count()); // sayfalar çakışmaz, hepsi gelir

        var hepsi = await http.GetAsync("/api/jobs");
        Assert.Equal("false", hepsi.Headers.GetValues("X-Has-More").Single());
        Assert.Equal(400, (int)(await http.GetAsync("/api/jobs?state=yok-boyle-durum")).StatusCode);
    }

    // ------------------------------------------------------------------ B18: ajana açık denen uçlar
    [Fact]
    public async Task Ajana_acik_uclar_insan_denetiminden_muaf_ama_jeton_ister()
    {
        Assert.True(CallerGate.AgentMayWrite("POST", "/api/queue"));
        Assert.True(CallerGate.AgentMayWrite("POST", "/api/guard/test"));
        Assert.True(CallerGate.AgentMayWrite("POST", "/api/jobs/5/reply"));
        // jeton ucu ve klasör gezme ucu insana ait
        using var env = new TestEnv(copySamples: false);
        await using var h = await SozServer.StartAsync(env.DataDir, 49885);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var noTok = await http.PostAsync("/api/queue", new StringContent("{\"text\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, noTok.StatusCode); // ajan jeton alamadığı için HTTP yolu ona kapalıdır; yolu komut satırıdır
        var noTok2 = await http.PostAsync("/api/guard/test", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, noTok2.StatusCode);
    }
}
