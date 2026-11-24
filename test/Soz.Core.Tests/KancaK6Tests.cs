using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Hooks;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// k6: kanca kullanıcının yardımcılarını yavaşlatmaz ve ayar dosyasını bozmaz.
///  - Ayar dosyası gidiş-dönüş: kurulum yorumları, sırayı, girintiyi, satır sonunu ve BOM'u korur; kaldırınca dosya BAYT BAYT kurulumdan önceki hâline döner.
///  - Yedekler zaman damgalıdır ve hiçbirinin üstüne yazılmaz.
///  - http kancası (süreç açmaz) yalnız uygun sürümde, yalnız Söz açıkken ve yalnız SessionStart dışındaki kancalarda kurulur.
///  - Sunucu kanca ucu jetonsuz isteği reddeder.
/// </summary>
public class KancaK6Tests
{
    private const string Exe = @"C:\x\soz-kanca.exe";
    private const string Data = @"C:\veri";
    private static readonly HookHttpTarget Http = new("http://127.0.0.1:49300", "jeton-jeton-jeton-jeton-1234567890");

    private static readonly UTF8Encoding Utf8 = new(false);

    public static IEnumerable<object[]> Fixtures()
    {
        const string nl = "\n";
        var pretty = "{" + nl + "  \"model\": \"opus\"," + nl + "  \"permissions\": {" + nl + "    \"allow\": [\"Bash(ls)\"]" + nl + "  }" + nl + "}" + nl;
        yield return new object[] { "guzel", pretty, false };
        yield return new object[] { "crlf", pretty.Replace("\n", "\r\n"), false };
        yield return new object[] { "bom", pretty, true };
        yield return new object[] { "sonda-satir-yok", pretty.TrimEnd('\n'), false };
        yield return new object[] { "sekme", "{\n\t\"model\": \"opus\",\n\t\"env\": {\n\t\t\"A\": \"1\"\n\t}\n}\n", false };
        yield return new object[] { "tek-satir", "{\"model\":\"opus\",\"permissions\":{\"allow\":[\"Bash(ls)\"]}}", false };
        yield return new object[] { "yorumlu", "{\n  // üst yorum\n  \"model\": \"opus\", // model notu\n  /* blok\n     yorum */\n  \"permissions\": {\n    \"allow\": [\"Bash(ls)\"] /* satır sonu */\n  },\n}\n", false };
        yield return new object[] { "yorumlu-sonda-virgulsuz", "{\n  \"model\": \"opus\", // not\n  \"env\": { \"A\": \"1\" } // son\n}\n", false };
        yield return new object[]
        {
            "kullanici-kancalari",
            "{\n  \"hooks\": {\n    \"SessionStart\": [ { \"matcher\": \"startup\", \"hooks\": [ { \"type\": \"command\", \"command\": \"echo merhaba\" } ] } ],\n"
            + "    \"PreToolUse\": [\n      {\n        \"matcher\": \"Bash\",\n        \"hooks\": [\n          { \"type\": \"command\", \"command\": \"echo benim\" }\n        ]\n      }\n    ],\n"
            + "    \"PostToolUse\": [\n      { \"hooks\": [ { \"type\": \"http\", \"url\": \"http://127.0.0.1:8080/benim\" } ] }\n    ]\n  },\n  \"model\": \"opus\"\n}\n",
            false,
        };
        yield return new object[] { "bos-nesne", "{}", false };
        yield return new object[] { "bos-nesne-satirli", "{\n}\n", false };
        yield return new object[] { "bos-nesne-bosluklu", "{ }", false };
        yield return new object[] { "bos-dosya", "", false };
        yield return new object[] { "bos-hooks", "{\n  \"hooks\": {}\n}\n", false };
    }

    private static byte[] Bytes(string text, bool bom)
    {
        var body = Utf8.GetBytes(text);
        return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static int CountOurs(JsonNode root)
    {
        var n = 0;
        if (root["hooks"] is not JsonObject hooks) return 0;
        foreach (var (_, groups) in hooks)
            foreach (var g in groups!.AsArray())
                foreach (var h in g!["hooks"]!.AsArray())
                    if (HookInstaller.IsOurs(h)) n++;
        return n;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Gidis_Donus_Komut_Yolu_Bayt_Bayt(string ad, string text, bool bom) => GidisDonus(ad, text, bom, null);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Gidis_Donus_Http_Yolu_Bayt_Bayt(string ad, string text, bool bom) => GidisDonus(ad, text, bom, Http);

    private static void GidisDonus(string ad, string text, bool bom, HookHttpTarget? http)
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Bytes(text, bom);
        File.WriteAllBytes(path, original);

        var res = HookInstaller.InstallDetailed(path, Exe, Data, null, null, 300, http);
        Assert.True(res.Changed, ad);
        Assert.NotNull(res.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(res.BackupPath!)); // yedek = kurulumdan önceki baytlar

        var installedBytes = File.ReadAllBytes(path);
        Assert.Equal(bom, installedBytes.Length >= 3 && installedBytes[0] == 0xEF && installedBytes[1] == 0xBB && installedBytes[2] == 0xBF); // BOM korunur
        var installed = Utf8.GetString(installedBytes, bom ? 3 : 0, installedBytes.Length - (bom ? 3 : 0));
        var node = JsonNode.Parse(installed, documentOptions: Lenient)!;
        Assert.Equal(6, CountOurs(node));
        if (text.Contains("\r\n")) Assert.DoesNotMatch(new Regex("(?<!\r)\n"), installed); // satır sonu tutarlı kalır
        foreach (Match m in Regex.Matches(text, @"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)) Assert.Contains(m.Value.TrimEnd('\r'), installed); // yorumlar korunur

        // kullanıcının hooks dışındaki her ayarı aynı
        var origNode = text.Trim().Length == 0 ? new JsonObject() : JsonNode.Parse(text, documentOptions: Lenient)!.AsObject();
        foreach (var (k, v) in origNode.AsObject())
            if (k != "hooks") Assert.True(JsonNode.DeepEquals(v, node[k]), $"{ad}: {k} değişti");
        // kullanıcının kendi kancaları yerinde
        if (origNode["hooks"] is JsonObject uh)
            foreach (var (ev, groups) in uh)
                foreach (var g in groups!.AsArray())
                    Assert.Contains(node["hooks"]![ev]!.AsArray(), x => JsonNode.DeepEquals(x, g));

        if (http is not null)
        {
            var h = node["hooks"]!;
            foreach (var arg in HookInstaller.HttpArgs)
            {
                var ev = HookInstaller.Hooks.First(x => x.Arg == arg).Event;
                var hk = h[ev]!.AsArray().Select(g => g!["hooks"]![0]!).First(HookInstaller.IsOurs);
                Assert.Equal("http", (string?)hk["type"]);
                Assert.Equal("http://127.0.0.1:49300/api/hooks/" + arg, (string?)hk["url"]);
                Assert.Equal(Http.Token, (string?)hk["headers"]![HookInstaller.TokenHeader]);
            }
            Assert.Equal("command", (string?)h["SessionStart"]!.AsArray().Select(g => g!["hooks"]![0]!).First(HookInstaller.IsOurs)["type"]); // SessionStart http desteklemez
            Assert.Equal("command", (string?)h["Notification"]!.AsArray().Select(g => g!["hooks"]![0]!).First(HookInstaller.IsOurs)["type"]);
        }

        // aynı kurulumu yinelemek dosyaya da yedeğe dokunmaz
        var again = HookInstaller.InstallDetailed(path, Exe, Data, null, null, 300, http);
        Assert.False(again.Changed, ad);
        Assert.Equal(installedBytes, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.json.soz-kurulum-*"));

        Assert.True(HookInstaller.Uninstall(path));
        Assert.Equal(original, File.ReadAllBytes(path)); // BAYT BAYT
        Assert.False(HookInstaller.Uninstall(path));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Dosya_Yoksa_Kurar_Kaldirinca_Siler_Ve_Isaret_Birakmaz_Yanlislikla_Baska_Dosyayi_Silmez()
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, "yeni", ".claude", "settings.json");
        var res = HookInstaller.InstallDetailed(path, Exe, Data, null, null, 300, null);
        Assert.True(File.Exists(path));
        Assert.EndsWith(".yok", res.BackupPath!); // "dosya yoktu" işareti
        Assert.Equal(6, CountOurs(JsonNode.Parse(File.ReadAllText(path))!));
        Assert.True(HookInstaller.Uninstall(path));
        Assert.False(File.Exists(path)); // Söz'ün yarattığı dosya geri gider

        // Kullanıcı kurulumdan sonra kendi ayarını eklerse dosya silinmez
        HookInstaller.Install(path, Exe, Data);
        var t = File.ReadAllText(path);
        File.WriteAllText(path, "{\n  \"model\": \"opus\"," + t[1..]); // kullanıcı kurulumdan sonra kendi ayarını ekledi
        Assert.True(HookInstaller.Uninstall(path));
        Assert.True(File.Exists(path));
        Assert.Equal("opus", (string?)JsonNode.Parse(File.ReadAllText(path))!["model"]);
        Assert.Null(JsonNode.Parse(File.ReadAllText(path))!["hooks"]);
    }

    [Fact]
    public void Bozuk_Dosyaya_Dokunulmaz_Yedek_Yazilmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var bad in new[] { "{ \"model\": ", "[1,2]", "{ \"hooks\": 5 }", "{ \"hooks\": { \"Stop\": {} } }", "düz yazı" })
        {
            File.WriteAllText(path, bad);
            Assert.Throws<InvalidDataException>(() => HookInstaller.Install(path, Exe, Data));
            Assert.Equal(bad, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.soz-*"));
        }
        // UTF-8 olmayan dosya
        File.WriteAllBytes(path, new byte[] { 0x7B, 0xFF, 0xFE, 0x7D });
        Assert.Throws<InvalidDataException>(() => HookInstaller.Install(path, Exe, Data));
        Assert.Equal(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Yedekler_Zaman_Damgali_Ve_Hicbirinin_Ustune_Yazilmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\n  \"model\": \"opus\"\n}\n");
        for (var i = 0; i < 3; i++)
        {
            HookInstaller.Install(path, Exe, Data, only: i % 2 == 0 ? "stop" : "stop,notification"); // her seferinde farklı küme: gerçek değişiklik
            HookInstaller.Uninstall(path);
        }
        var all = Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.json.soz-*.bak");
        Assert.Equal(6, all.Length); // 3 kurulum + 3 kaldırma öncesi, hepsi ayrı dosya
        Assert.Equal(6, all.Distinct().Count());
        Assert.All(all, f => Assert.Matches(@"\.soz-(kurulum|kaldirma)-\d{8}T\d{9}Z(-\d+)?\.bak$", f));
        Assert.Equal("{\n  \"model\": \"opus\"\n}\n", File.ReadAllText(path));
    }

    [Fact]
    public void Yeniden_Kurulum_Komuttan_Http_Yoluna_Gecis_Bayt_Bayt_Doner()
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = "{\n  // yorum\n  \"model\": \"opus\"\n}\n";
        File.WriteAllText(path, original);
        HookInstaller.Install(path, Exe, Data);
        var cmdText = File.ReadAllText(path);
        var r = HookInstaller.InstallDetailed(path, Exe, Data, null, null, 300, Http);
        Assert.True(r.Changed);
        var httpText = File.ReadAllText(path);
        Assert.NotEqual(cmdText, httpText);
        Assert.Equal(6, CountOurs(JsonNode.Parse(httpText, documentOptions: Lenient)!)); // çoğalma yok
        Assert.True(HookInstaller.Uninstall(path));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Kullanicinin_Kendi_Http_Kancasi_Sozun_Sayilmaz()
    {
        Assert.False(HookInstaller.IsOurs(JsonNode.Parse("""{ "type": "http", "url": "http://127.0.0.1:8080/hooks/stop" }""")));
        Assert.False(HookInstaller.IsOurs(JsonNode.Parse("""{ "type": "http", "url": "https://example.com/api/hooks/stop" }""")));
        Assert.False(HookInstaller.IsOurs(JsonNode.Parse("""{ "type": "http", "url": "http://127.0.0.1:49300/api/hooks/baska-kanca" }""")));
        Assert.True(HookInstaller.IsOurs(JsonNode.Parse("""{ "type": "http", "url": "http://127.0.0.1:49300/api/hooks/pre-tool-use" }""")));
        Assert.True(HookInstaller.IsOurs(JsonNode.Parse("""{ "type": "command", "command": "'C:/Soz/soz-kanca.exe' hook stop --data 'C:/v'" }""")));
        Assert.False(HookInstaller.IsOurs(JsonNode.Parse("""{ "type": "command", "command": "echo hook stop" }""")));
        Assert.Equal("stop", HookInstaller.ArgOf(JsonNode.Parse("""{ "type": "command", "command": "'C:/Soz/soz-kanca.exe' hook stop --data 'C:/v' 2>/dev/null || true" }""")));
    }

    [Fact]
    public void Bekci_Yalniz_Dosya_Ve_Kabuk_Araclarinda_Calisir_Timeout_Kisa()
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, ".claude", "settings.json");
        HookInstaller.Install(path, Exe, Data, null, null, 300, Http);
        var h = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!;
        Assert.Equal(HookInstaller.PreToolUseMatcher, (string?)h["PreToolUse"]![0]!["matcher"]);
        foreach (var t in new[] { "Bash", "PowerShell", "Shell", "Write", "Edit", "MultiEdit", "NotebookEdit" }) Assert.Matches("^(" + HookInstaller.PreToolUseMatcher + ")$", t);
        foreach (var t in new[] { "Read", "Grep", "Glob", "WebFetch", "mcp__x__y", "Task" }) Assert.DoesNotMatch("^(" + HookInstaller.PreToolUseMatcher + ")$", t);
        Assert.Equal(5, (int)h["PreToolUse"]![0]!["hooks"]![0]!["timeout"]!); // sunucu takılırsa iş beklemez
        Assert.Equal(5, (int)h["UserPromptSubmit"]![0]!["hooks"]![0]!["timeout"]!);
        Assert.Equal("http", (string?)h["StopFailure"]![0]!["hooks"]![0]!["type"]); // hata durması gözlemi de süreç açmaz
        Assert.Equal(5, (int)h["StopFailure"]![0]!["hooks"]![0]!["timeout"]!);
        Assert.Equal(330, (int)h["Stop"]![0]!["hooks"]![0]!["timeout"]!); // kapı bütçesi + 30
        Assert.Null(h["Stop"]![0]!["matcher"]);
        // koruma ağı: onFailure verilmez (varsayılan "devam et"; eski sürümde de geçerli)
        Assert.DoesNotContain("onFailure", File.ReadAllText(path));
    }

    [Fact]
    public void Komut_Sessiz_Sonekle_Program_Yoksa_Basarili_Biter()
    {
        var cmd = HookInstaller.BuildCommand(@"C:\yok\soz-kanca.exe", "pre-tool-use", @"C:\veri", null);
        Assert.EndsWith(" 2>/dev/null || true", cmd);
        var psi = new System.Diagnostics.ProcessStartInfo(TestEnv.BashYolu()) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(cmd);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(15000));
        Assert.Equal(0, p.ExitCode);
        Assert.Equal("", err);
    }

    private static HookHttpTarget? Fast(string dataDir, Func<Version?> version, Func<string, ServerFileInfo?> server) => HookTransportPlanner.Plan(dataDir, null, version, server, fastMode: true);

    [Fact]
    public void Planlayici_Yalniz_Uygun_Surum_Ve_Acik_Sunucuda_Http_Verir()
    {
        using var env = new TestEnv(copySamples: false);
        ServerFileInfo? Live(string _) => new("http://127.0.0.1:49321", 1234, "x", true);
        var ok = Fast(env.DataDir, () => new Version(2, 1, 330), Live);
        Assert.NotNull(ok);
        Assert.Equal("http://127.0.0.1:49321", ok!.BaseUrl);
        Assert.Equal(HookToken.GetOrCreate(env.DataDir), ok.Token);
        Assert.NotNull(Fast(env.DataDir, () => HookInstaller.HttpHooksMinVersion, Live)); // sınır dahil
        Assert.Null(Fast(env.DataDir, () => new Version(2, 1, 294), Live));   // eski sürüm: komut yolu
        Assert.Null(Fast(env.DataDir, () => null, Live));                      // sürüm okunamadı: komut yolu
        Assert.Null(Fast(env.DataDir, () => new Version(2, 1, 330), _ => null)); // Söz kapalı
        Assert.Null(Fast(env.DataDir, () => new Version(2, 1, 330), _ => new ServerFileInfo("http://127.0.0.1:49321", 1, "x", false))); // bayat işaret
        Assert.Null(Fast(env.DataDir, () => new Version(2, 1, 330), _ => new ServerFileInfo("http://baska.example:49321", 1, "x", true))); // geri döngü değil
        var local = Fast(env.DataDir, () => new Version(2, 1, 330), _ => new ServerFileInfo("http://localhost:49321", 1, "x", true));
        Assert.Equal("http://127.0.0.1:49321", local!.BaseUrl); // localhost → 127.0.0.1 (IPv6 gecikmesi yok)
        // VARSAYILAN komut yolu: ayar kapalıyken (ya da hiç verilmediyse) her koşulda null
        Assert.Null(HookTransportPlanner.Plan(env.DataDir, null, () => new Version(2, 1, 330), Live));
        Assert.Null(HookTransportPlanner.Plan(env.DataDir, null, () => new Version(2, 1, 330), Live, fastMode: false));
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, "http", true)]
    [InlineData(true, "command", false)]
    [InlineData(true, "HTTP", true)]
    [InlineData(false, "saçma", false)]
    public void Hizli_Kip_Ayar_Ve_Ortam_Degiskeni_Onceligi(bool ayar, string? ortam, bool beklenen)
    {
        Assert.Equal(beklenen, HookTransportPlanner.FastModeWanted(ayar, ortam));
    }

    [Fact]
    public void Surum_Metni_Okunur()
    {
        Assert.Equal(new Version(2, 1, 330), ClaudeVersion.Parse("2.1.330 (Claude Code)"));
        Assert.Equal(new Version(2, 1, 5), ClaudeVersion.Parse("claude 2.1.5"));
        Assert.Null(ClaudeVersion.Parse(""));
        Assert.Null(ClaudeVersion.Parse("sürüm yok"));
    }

    [Fact]
    public void Jeton_Kalici_Ve_Karsilastirma_Sabit_Zamanli()
    {
        using var env = new TestEnv(copySamples: false);
        var a = HookToken.GetOrCreate(env.DataDir);
        Assert.True(a.Length >= 32);
        Assert.Equal(a, HookToken.GetOrCreate(env.DataDir)); // yeniden başlatmada değişmez: ayar dosyaları bayatlamaz
        Assert.True(HookToken.Equal(a, a));
        Assert.False(HookToken.Equal(a + "x", a));
        Assert.False(HookToken.Equal("", a));
        Assert.False(HookToken.Equal(null, a));
    }

    [Fact]
    public void Saglik_Http_Kancayi_Tanir_Ve_Kapi_Uyumsuzlugunu_Bildirir()
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, "p", ".claude", "settings.json");
        HookInstaller.Install(path, Exe, Data, null, null, 300, Http);
        var f = HookHealth.Read("proje", null, null, path, env.DataDir);
        Assert.Equal(6, f.Hooks.Count);
        Assert.Contains("pre-tool-use", f.Hooks);

        // Yalnız http kancaları (komutsuz) kurulu: adres yazılı
        var only = Path.Combine(env.Root, "p2", ".claude", "settings.json");
        HookInstaller.Install(only, Exe, Data, null, "stop,pre-tool-use", 300, Http);
        var f2 = HookHealth.Read("proje", null, null, only, env.DataDir);
        Assert.Equal(2, f2.Hooks.Count);
        Assert.Equal("http://127.0.0.1:49300/api/hooks/stop", f2.DataDir);
        Assert.Null(f2.ExePath);
    }

    [Fact]
    public async Task Sunucu_Kanca_Ucu_Jetonsuz_Istegi_Reddeder_Jetonluyu_Isler()
    {
        using var env = new TestEnv(copySamples: false);
        await using var server = await SozServer.StartAsync(env.DataDir, 0);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        var body = JsonSerializer.Serialize(new { session_id = "k6-1", cwd = env.Root, tool_name = "Read", tool_input = new { file_path = "a.txt" } });

        var none = await http.PostAsync("/api/hooks/pre-tool-use", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/hooks/pre-tool-use") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        wrong.Headers.Add(HookInstaller.TokenHeader, "yanlis-jeton");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

        var token = HookToken.Read(env.DataDir);
        Assert.NotNull(token); // sunucu açılışta üretir
        // Claude Code'un http kancası gibi: ham olay gövdesi + başlıkta jeton; boş gövde = başarı, çıktı yok
        using var good = new HttpRequestMessage(HttpMethod.Post, "/api/hooks/pre-tool-use") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        good.Headers.Add(HookInstaller.TokenHeader, token);
        var ok = await http.SendAsync(good);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("", await ok.Content.ReadAsStringAsync());

        // soz-kanca / soz.exe rölesi sarmalı gövdeyi (stdin + env) aynı jetonla yollar
        var wrapped = JsonSerializer.Serialize(new { stdin = body, env = new Dictionary<string, string> { ["CLAUDE_PROJECT_DIR"] = env.Root } });
        using var relay = new HttpRequestMessage(HttpMethod.Post, "/api/hooks/notification") { Content = new StringContent(wrapped, Encoding.UTF8, "application/json") };
        relay.Headers.Add(HookInstaller.TokenHeader, token);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(relay)).StatusCode);

        // jetonsuz GET sağlık ucu açık kalır (okuma)
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/hooks/health")).StatusCode);
    }
}
