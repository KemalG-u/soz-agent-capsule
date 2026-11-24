using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Jobs;
using Soz.Core.Text;
using Soz.Server;
using Xunit;
using Xunit.Abstractions;

namespace Soz.Core.Tests;

/// <summary>
/// s2 / denetim 5 tur 2 (N3, N4): sır maskesi iki yönlü sınanır. Sır görünen yerde maskelenmeli, meşru uzun değer (proje adı, sürüm, özet) bozulmamalı.
/// Yalnız sentetik, uydurma değerler kullanılır.
/// </summary>
public class S2MaskeTests
{
    private readonly ITestOutputHelper _out;
    public S2MaskeTests(ITestOutputHelper o) => _out = o;

    // ------------------------------------------------------------------ maskelenmeli
    public static IEnumerable<object[]> Maskelenmeli() => new (string Metin, string Gizli)[]
    {
        ("deger=Zk3Fq9LmN2xV8pRt5Yw7Bc4Dh6Jg1Ks0Xa8Qw", "Zk3Fq9LmN2xV8pRt5Yw7"),
        ("anahtar sk-ant-api03-S2SAHTEANAHTAR9876543210zyxwvutsrqpon bitti", "S2SAHTEANAHTAR"),
        ("token gh" + "p_S2FAKE1234567890abcdefghijkl", "S2FAKE1234567890"),
        ("AWS AK" + "IAS2FAKEFAKE1234 kullan", "AK" + "IAS2FAKEFAKE1234"),
        ("Authorization: Bearer abc123.def456.ghi789", "abc123.def456"),
        ("Bearer s2tokenDegeri99xyz ile çağır", "s2tokenDegeri99xyz"),
        ("DATABASE_URL=postgres://" + "admin:s2p4ssw0rd9@db.example.com:5432/app", "s2p4ssw0rd9"),
        ("git clone https://" + "kullanici:s2gizliparola@github.com/x/y.git", "s2gizliparola"),
        ("password: s2hunter2xyz", "s2hunter2xyz"),
        ("şifre: S2Kalem2026x", "S2Kalem2026x"),
        ("token=" + "s2abcd1234efgh", "s2abcd1234efgh"),
        ("api_key = " + "7f3a9b2c4d5e6f708192a3b4c5d6e7f8", "7f3a9b2c4d5e6f70"),
        ("DB_PASSWORD=s2hunter2hunter2 ile bağlan", "s2hunter2hunter2"),
        ("-----BEGIN " + "PRIVATE KEY-----\nS2MIIEvQIBADANBgkqhkiG9w0BAQEFAASC\nabcdefg\n-----END PRIVATE KEY-----", "S2MIIEvQ"),
        ("{\"k\":\"-----BEGIN " + "RSA PRIVATE KEY-----\\nS2MIIEowIBAAKCAQEA\\n-----END RSA PRIVATE KEY-----\"}", "S2MIIEow"),
        ("http://localhost:3999/?key=S2AdresAnahtari0123456789abcdef", "S2AdresAnahtari"),
        ("https://api.example.com/v1/x?lang=tr&token=S2KisaTokenDegeri", "S2KisaTokenDegeri"),
        ("https://api.example.com/v1/x?access_token=s2erisim8472", "s2erisim8472"),
        ("https://api.example.com/v1/x?sig=s2imza9981ab&ok=1", "s2imza9981ab"),
        ("sk_" + "live_S2StripeAnahtar0123456789", "S2StripeAnahtar"),
        ("np" + "m_S2NpmAnahtari0123456789abcdefghijklmnop", "S2NpmAnahtari"),
        ("gl" + "pat-S2GitlabAnahtari0123456789", "S2GitlabAnahtari"),
        ("eyJhbGciOi" + "JIUzI1NiJ9.eyJzdWIi" + "OiJzMmZha2UifQ.czJzaWduYXR1cmU", "eyJzdWIi" + "OiJzMmZha2UifQ"),
        ("xo" + "xb-S2123456789-S2abcdefghij", "S2123456789"),
        ("AI" + "zaS2FakeGoogleKey0123456789abcdefg", "S2FakeGoogleKey"),
        ("curl -u " + "admin:S2Gizli123 https://x.example/a", "S2Gizli123"),
        ("deploy --password s2hunter22x --env prod", "s2hunter22x"),
        ("mysql -u root -pS2SuperSecret1 uygulama", "S2SuperSecret1"),
        ("https://hooks.slack.com/" + "services/T00000000/B00000000/S2XXXXXXXXXXXXXXXXXXXXXX", "S2XXXXXXXX"),
        ("Bunu yapıştır Zk3pQ8vN2xR7mT1wY5uB9cD4eF6gH sonra devam et", "Zk3pQ8vN2xR7mT1wY5uB9cD4eF6gH"),
        ("bağlamsız anahtar Zq81LmNpX2d9TrWb4HsEoVa7YcKfJu3DgRt6Nx5B", "Zq81LmNpX2d9TrWb4HsEoVa7YcKfJu3DgRt6Nx5B"),
        ("base64 vYr3Qn+T8k2Lw/Xz0PdM5aGhB7sUeJcV9oIyNtF4RqA= bitti", "vYr3Qn+T8k2Lw/Xz0PdM5aGhB7sUeJcV9oIyNtF4RqA"),
        ("küçük harf ve rakam k3xpq9wzmnb2v7lrt4hcj8dfs6gyu5ae1o", "k3xpq9wzmnb2v7lrt4hcj8dfs6gyu5ae1o"),
    }.Select(x => new object[] { x.Metin, x.Gizli });

    [Theory, MemberData(nameof(Maskelenmeli))]
    public void Sir_maskelenir(string metin, string gizli)
    {
        Assert.True(SecretFilter.ContainsSecret(metin), metin);
        var m = SecretFilter.MaskSecrets(metin);
        Assert.DoesNotContain(gizli, m);
        Assert.Contains(SecretFilter.Mask, m);
    }

    // ------------------------------------------------------------------ maskelenmemeli
    public static IEnumerable<object[]> Maskelenmemeli() => new[]
    {
        // denetçinin ad.txt ve mesru.txt örnekleri
        "KisiselWebSitesiProjesiV2Final",
        "ders-notlari-2026-bahar-donemi",
        "kisisel-web-sitesi-projem",
        "musteri-yonetim-paneli-frontend",
        "e-ticaret-sitesi-yeni-tasarim",
        "benim_cok_uzun_proje_klasorum_final",
        "Muvaffakiyetsiz proje adi cok uzun ama bosluklu",
        "v10.2.3-beta.4+build.20261112",
        "2026-11-12T05:14:43.123Z-hata-kodu-E_CONN_RESET_12345",
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk",
        "3f2a9c1b7d4e5f60718293a4b5c6d7e8f9a0b1c2",
        "sha40=3f2a9c1b7d4e5f60718293a4b5c6d7e8f9a0b1c2",
        "uuid=0f8fad5b-d9cb-469f-a165-70867728950e",
        "hash64=a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4",
        "0f8fad5b-d9cb-469f-a165-70867728950e",
        "2026-11-12T05:14:43.123Z",
        "src/bilesenler/cok/derin/klasor/yapisi/dosya-adi-uzun.tsx",
        "https://example.com/a/very/long/path/segment/that/goes/on/and/on",
        "#aabbccddeeff00112233",
        "Muvaffakiyetsizleştiricileştiriveremeyebileceklerimizdenmişsinizcesine",
        "a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4a1b2c3d4",
        "bilesen-kullanici-profil-karti.test.tsx",
        // başka meşru uzun değerler
        "d41d8cd98f00b204e9800998ecf8427e",
        "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e",
        @"D:\tools\dev\dotnet10\dotnet.exe",
        "Giriş formuna şifre alanını ekle",
        "API anahtarı yoksa kullanıcıya uyarı göster",
        "Token süresi dolunca yeniden giriş iste",
        "Bearer yetkilendirme başlığını belgele",
        "SessionStateResolverThresholdsFactory sınıfını kullan",
        "com.example.myapp.feature.settings.ProfileScreenViewModel",
        "feature/kullanici-profil-sayfasi-yeniden-tasarim-2026",
        "release-2026.11.12-rc.1-hotfix-build.4471",
        "ProjeYonetimPaneliKullaniciYetkiYoneticisi2026",
        "2026-11-12-gunluk-yedek-veritabani-kopyasi.sqlite",
        "ErisilebilirlikDenetimiSonuclariRaporu_v2_final.pdf",
        "https://example.com/a/b?lang=tr&sayfa=2&siralama=tarih-azalan",
        "node_modules/@types/react-dom/index.d.ts",
        "Soz.Core.Tests.S2MaskeTests.Maskeleme_Tablosu_Calisir",
        "01a1cd5a-b528-770e-b785-58b331f8ad19",
        "Türkçe uzun ad: IsYonetimiVeTakipUygulamasiArayuzuSurum3",
    }.Select(s => new object[] { s });

    [Theory, MemberData(nameof(Maskelenmemeli))]
    public void Mesru_deger_bozulmaz(string metin)
    {
        Assert.False(SecretFilter.ContainsSecret(metin), metin);
        Assert.Equal(metin, SecretFilter.MaskSecrets(metin));
    }

    [Fact]
    public void Tablolar_en_az_25_ornek_icerir()
    {
        Assert.True(Maskelenmeli().Count() >= 25);
        Assert.True(Maskelenmemeli().Count() >= 25);
    }

    // ------------------------------------------------------------------ rastgele anahtar taraması
    [Fact]
    public void Baglamsiz_rastgele_anahtarlar_yakalanir()
    {
        var rnd = new Random(20261112);
        const string b62 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        const string b36 = "abcdefghijklmnopqrstuvwxyz0123456789";
        const string b64 = b62 + "+/";
        const string url = b62 + "-_";
        var sets = new (string Ad, string Abc, int[] Uzunluk)[]
        {
            ("base62", b62, new[] { 32, 40, 48, 64 }),
            ("base36", b36, new[] { 36, 40, 48 }),
            ("base64", b64, new[] { 40, 44, 64 }),
            ("base64url", url, new[] { 40, 43, 64 }),
        };
        var kacan = new List<string>();
        var toplam = 0;
        foreach (var (ad, abc, lens) in sets)
            foreach (var len in lens)
                for (var n = 0; n < 150; n++)
                {
                    var k = new string(Enumerable.Range(0, len).Select(_ => abc[rnd.Next(abc.Length)]).ToArray());
                    toplam++;
                    if (!SecretFilter.ContainsSecret("anahtar degeri " + k)) kacan.Add($"{ad}/{len}: {k}");
                }
        _out.WriteLine($"{toplam} rastgele anahtardan kaçan: {kacan.Count}");
        foreach (var k in kacan.Take(15)) _out.WriteLine(k);
        // Bağlamsız tahmin kesin değildir (yanlış alarm değil, kaçırma yönü): çok küçük bir pay kabul edilir.
        Assert.True(kacan.Count <= toplam * 0.02, $"{kacan.Count}/{toplam} rastgele anahtar maskelenmedi");
    }

    // ------------------------------------------------------------------ N3: kayıtlı rotalardan tarama
    private const string Key = "sk-ant-api03-S2SAHTEANAHTAR9876543210zyxwvutsrqpon";
    private const string QueryKey = "S2AdresAnahtari0123456789abcdef";
    private const string Pass = "s2parola99xyz";
    private const string CmdPass = "S2KomutParolasi77";
    private const string TextKey = "S2Dosya" + "Sirri8888abcd";

    [Fact]
    public async Task Olcut_alanlarindaki_sir_butun_okuma_uclarinda_maskelidir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        await using var h = await SozServer.StartAsync(env.DataDir, 49881);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        HttpRequestMessage Req(HttpMethod m, string url, object? body = null)
        {
            var r = new HttpRequestMessage(m, url);
            if (body is not null) r.Content = JsonContent.Create(body);
            if (m != HttpMethod.Get) r.Headers.Add("X-Soz-Token", h.Token);
            return r;
        }

        // Sır, kişinin yazabildiği HER yere konur: iş metni, ölçüt adresi, ölçüt etiketi, ölçüt komutu, dosya metni.
        var jobRes = await http.SendAsync(Req(HttpMethod.Post, "/api/jobs", new { projectId = pid, rawText = $"Şunu kullan: {Key} ve password={Pass}" }));
        var jobId = (long)JsonNode.Parse(await jobRes.Content.ReadAsStringAsync())!["id"]!;
        var c1 = await http.SendAsync(Req(HttpMethod.Post, "/api/checks", new { projectId = pid, kind = "url", label = $"adres token={Pass}", spec = new { url = $"http://localhost:3999/?key={QueryKey}" }, enabled = false }));
        Assert.Equal(System.Net.HttpStatusCode.Created, c1.StatusCode);
        var c2 = await http.SendAsync(Req(HttpMethod.Post, "/api/checks", new { projectId = pid, jobId, kind = "command", label = "komut", spec = new { command = "curl -u " + $"admin:{CmdPass} https://x.example" }, enabled = false }));
        _ = c2; // komut ölçütü bu sürümde reddedilebilir; asıl amaç adres ve metin alanları
        var c3 = await http.SendAsync(Req(HttpMethod.Post, "/api/checks", new { projectId = pid, jobId, kind = "file-text", label = "dosya", spec = new { path = "hesap.js", text = $"api_key = {TextKey}" }, enabled = false }));
        _ = c3;

        var secrets = new[] { Key, Pass, QueryKey, CmdPass, TextKey };

        // Uç listesi uygulamanın kayıtlı rotalarından çıkar (elle liste yok): yeni uç kendiliğinden taranır.
        var skip = new[] { "/api/session", "/api/events/stream", "/api/agents", "/api/agents/ready", "/api/doctor", "/api/phone/", "/api/proof/artifact" };
        var gets = h.Routes().Where(r => r.Method == "GET").Select(r => r.Pattern).Distinct().ToList();
        var swept = 0;
        var leaks = new List<string>();
        foreach (var pattern in gets)
        {
            if (skip.Any(s => pattern.StartsWith(s, StringComparison.OrdinalIgnoreCase) && (pattern.Length == s.Length || s.EndsWith("/") || pattern[s.Length] == '/'))) continue;
            var num = (pattern.Contains("/projects/") ? pid : jobId).ToString();
            var url = Regex.Replace(pattern, @"\{(\w+)(:\w+)?\??\}", m => m.Groups[2].Success ? num : "x.txt");
            var res = await http.SendAsync(Req(HttpMethod.Get, url));
            var text = await res.Content.ReadAsStringAsync();
            foreach (var s in secrets) if (text.Contains(s)) leaks.Add($"{url} ({(int)res.StatusCode}) {s}");
            swept++;
        }
        Assert.True(leaks.Count == 0, string.Join("; ", leaks));
        Assert.True(swept >= 20, $"yalnız {swept} okuma ucu tarandı");
        _out.WriteLine($"{swept} kayıtlı okuma ucu tarandı, {gets.Count} GET rotası var");

        // Ölçüt listesi: adres hâlâ okunabilir ama anahtar kısmı maskeli.
        var checks = await http.GetStringAsync($"/api/projects/{pid}/checks");
        Assert.Contains("localhost:3999", checks);
        Assert.Contains("•••", checks);
        Assert.DoesNotContain(QueryKey, checks);
    }

    [Fact]
    public async Task Proje_adi_ve_surum_dizgesi_cevapta_bozulmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env, "KisiselWebSitesiProjesiV2Final");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var job = w.CreateJob(pid, "Sürüm v10.2.3-beta.4+build.20261112 ve ders-notlari-2026-bahar-donemi için düzelt.");
        await using var h = await SozServer.StartAsync(env.DataDir, 49881);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        foreach (var u in new[] { "/api/projects", "/api/jobs", $"/api/jobs/{job.Id}", "/api/now", "/api/morning" })
        {
            var t = await http.GetStringAsync(u);
            Assert.DoesNotContain("•••", t);
        }
        Assert.Contains("KisiselWebSitesiProjesiV2Final", await http.GetStringAsync("/api/projects"));
        Assert.Contains("v10.2.3-beta.4+build.20261112", await http.GetStringAsync($"/api/jobs/{job.Id}"));
    }
}
