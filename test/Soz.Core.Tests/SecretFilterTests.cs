using System.Text;
using Soz.Core.Data;
using Soz.Core.Extract;
using Soz.Core.Text;
using Xunit;
using Xunit.Abstractions;

namespace Soz.Core.Tests;

/// <summary>Sır süzgeci: yalnız sentetik, uydurma değerler kullanılır (gerçek sır yok).</summary>
public class SecretFilterTests
{
    private readonly ITestOutputHelper _out;
    public SecretFilterTests(ITestOutputHelper o) => _out = o;

    public static IEnumerable<object[]> Secrets() => new[]
    {
        "şifre: Kx9mPq2vLz7",
        "Parola: benimparolam",
        "Şifrem hunter2 olsun",
        "password=hunter22",
        "my password is Abc12345x",
        "api key s" + "k-FAKEKEY1234567890 kullan",
        "token: gh" + "p_FAKE1234567890abcdefgh",
        "AWS anahtarım AK" + "IAFAKEFAKEFAKE12 olacak",
        "Authorization: Bearer abc123.def456.ghi789",
        "Bunu yapıştır Zk3pQ8vN2xR7mT1wY5uB9cD4eF6gH sonra devam et",
        "api_key = " + "7f3a9b2c4d5e6f708192a3b4c5d6e7f8",
        "{\"password\": \"s3cr3tvalue\"}",
        "client secret: " + "abcdEFGH1234",
        "eyJhbGciOi" + "JIUzI1NiJ9.eyJzdWIiOiJmYWtlIn0.c2lnbmF0dXJl",
        "-----BEGIN " + "RSA PRIVATE KEY-----",
    }.Select(s => new object[] { s });

    public static IEnumerable<object[]> NotSecrets() => new[]
    {
        "Her zaman şifre yönetimi için ortam değişkeni kullan.",
        "API anahtarını koda yazma.",
        "Parolayı asla loga yazma.",
        "Token limitini 8000 yapma.",
        "token: 8000",
        "Always use bearer authentication for the API.",
        "Dosya: E:\\Sample Work\\urun\\_gecici\\ekran-gercek-api\\toparla.png içinde",
        "https://example.com/a/very/long/path/that/goes/on/and/on adresine bak",
        "Oturum aaaaaaaa-0000-4000-8000-000000000001 kapandı",
        "SessionStateResolverThresholdsFactory sınıfını kullan",
        "Bundan sonra her zaman Türkçe yaz.",
        "Karar verdim: veritabanı olarak SQLite ile devam edelim.",
        "Task-force kurma, sk-learn da kullanma.",
    }.Select(s => new object[] { s });

    [Theory, MemberData(nameof(Secrets))]
    public void Detects_Secret(string text)
    {
        Assert.True(SecretFilter.ContainsSecret(text), text);
        Assert.Contains(SecretFilter.Mask, SecretFilter.MaskSecrets(text));
    }

    [Theory, MemberData(nameof(NotSecrets))]
    public void Leaves_Normal_Sentences(string text)
    {
        Assert.False(SecretFilter.ContainsSecret(text), text);
        Assert.Equal(text, SecretFilter.MaskSecrets(text));
    }

    [Fact]
    public void Mask_Replaces_Only_The_Secret_Part()
    {
        var m = SecretFilter.MaskSecrets("Hatırla, şifre: Kx9mPq2vLz7 ve bunu sakla.");
        Assert.Equal("Hatırla, şifre: ••• ve bunu sakla.", m);
        var m2 = SecretFilter.MaskSecrets("Anahtar s" + "k-FAKEKEY1234567890 ve token: gh" + "p_FAKE1234567890abcdefgh bitti");
        Assert.DoesNotContain("FAKEKEY", m2);
        Assert.DoesNotContain("ghp_", m2);
        Assert.StartsWith("Anahtar ", m2);
        Assert.EndsWith(" bitti", m2);
        _out.WriteLine(m2);
    }

    [Fact]
    public void Extractor_Drops_Secret_Sentences_But_Keeps_Rules()
    {
        var ex = new RuleBasedExtractor();
        var found = ex.Extract("Her zaman Türkçe yaz. Bundan sonra şifre: Kx9mPq2vLz7 kullan. Asla s" + "k-FAKEKEY1234567890 anahtarını paylaşma.");
        Assert.Contains(found, c => c.Text.Contains("Türkçe"));
        Assert.DoesNotContain(found, c => c.Text.Contains("Kx9m") || c.Text.Contains("FAKEKEY"));
    }

    [Fact]
    public void Pipeline_Keeps_Secrets_Out_Of_Candidates_Packet_And_Search_Results()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.ClaudeRoot, "E--gizli-proje");
        Directory.CreateDirectory(dir);
        var lines = new[]
        {
            "Her zaman Türkçe yaz.",
            "Bundan sonra şifre: Kx9mPq2vLz7 kullan, bunu hep hatırla.",
            "Asla s" + "k-FAKEKEY1234567890 anahtarını loga yazma.",
        };
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
            sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                parentUuid = (string?)null, isSidechain = false, type = "user",
                message = new { role = "user", content = lines[i] },
                uuid = "g" + i, timestamp = $"2026-11-10T10:0{i}:00.000Z", userType = "external", entrypoint = "cli",
                cwd = "E:\\gizli-proje", sessionId = "dddddddd-0000-4000-8000-000000000001", version = "2.1.322",
                origin = new { kind = "human" }, promptSource = "typed",
            }));
        File.WriteAllText(Path.Combine(dir, "dddddddd-0000-4000-8000-000000000001.jsonl"), sb.ToString());
        env.Import();

        using (var c = env.Svc.Db.Open())
        {
            Assert.Equal(0, c.ScalarLong("SELECT COUNT(*) FROM candidates WHERE text LIKE '%Kx9m%' OR text LIKE '%FAKEKEY%'"));
            Assert.True(c.ScalarLong("SELECT COUNT(*) FROM candidates WHERE text LIKE '%Türkçe%'") >= 1);
        }

        // Onaylı karar sır içeriyorsa (elle eklenmiş olsa bile) pakete girmez.
        var pid = env.Svc.ResolveProject(@"E:\gizli-proje")!.Value;
        env.Svc.CreateDecision(pid, "rule", "Her zaman Türkçe yaz.");
        env.Svc.CreateDecision(pid, "rule", "Şifre: Kx9mPq2vLz7 kullan.");
        var packet = env.Svc.Packet(pid);
        Assert.Contains("Türkçe", packet.Text);
        Assert.DoesNotContain("Kx9m", packet.Text);
        Assert.Equal(1, packet.DecisionCount);

        // "Ne demiştim": ham mesaj bulunur ama sır parçası maskelidir.
        var hit = env.Svc.Utterances("hatırla", null, 10, 0);
        Assert.Single(hit.Items);
        Assert.Contains("•••", hit.Items[0].Text);
        Assert.DoesNotContain("Kx9m", hit.Items[0].Text);
        var hit2 = env.Svc.Utterances("loga", null, 10, 0);
        Assert.DoesNotContain("FAKEKEY", hit2.Items[0].Text);
    }

    [Fact]
    public void Purge_Removes_Old_Secret_Candidates_And_Pending_Proposals_Once()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var star = c.ScalarLong("SELECT id FROM projects WHERE key='*'");
        c.Exec("INSERT INTO candidates(utterance_id, project_id, type, text, score, cluster_id) VALUES(1,@p,'rule','Hep şifre: Kx9mPq2vLz7 yaz',0.9,NULL)", null, ("@p", star));
        c.Exec("INSERT INTO candidates(utterance_id, project_id, type, text, score, cluster_id) VALUES(1,@p,'rule','Her zaman Türkçe yaz',0.9,NULL)", null, ("@p", star));
        var bad = c.ScalarLong("SELECT id FROM candidates WHERE text LIKE '%Kx9m%'");
        c.Exec("INSERT INTO decisions(project_id,type,text,status,pinned,origin_candidate_id,repeat_count,created_at,updated_at) VALUES(@p,'rule','Hep şifre: Kx9mPq2vLz7 yaz','proposed',0,@o,1,'x','x')", null, ("@p", star), ("@o", bad));
        Assert.Equal(1, SecretFilter.PurgeExisting(c));
        Assert.Equal(0, c.ScalarLong("SELECT COUNT(*) FROM candidates WHERE text LIKE '%Kx9m%'"));
        Assert.Equal(1, c.ScalarLong("SELECT COUNT(*) FROM candidates"));
        Assert.Equal(0, c.ScalarLong("SELECT COUNT(*) FROM decisions WHERE status='proposed'"));
        Assert.Equal(0, SecretFilter.PurgeExisting(c)); // aynı sürüm yeniden taranmaz
    }
}
