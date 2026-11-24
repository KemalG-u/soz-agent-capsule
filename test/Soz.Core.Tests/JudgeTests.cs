using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Judge;
using Xunit;

namespace Soz.Core.Tests;

public class JudgeTests
{
    private static readonly string[] YesNo = { "evet", "hayır" };

    [Theory]
    [InlineData("İş bitti. Testler geçti: 12 passed, dosya src/a.cs güncellendi.", "evet")]
    [InlineData("Üzerinde çalışıyorum, birazdan devam edeceğim.", "hayır")]
    public void Kurali_hakem_claims_done(string text, string expected)
    {
        var r = new RulesJudge().Decide(JudgeQuestions.ClaimsDone, YesNo, text);
        Assert.Equal(expected, r.Answer);
        Assert.Equal("rules", r.Engine);
        Assert.True(r.Confidence > 0.5);
        Assert.True(r.Ms >= 0);
    }

    [Fact]
    public void Belirsiz_bitti_ve_soru_birlikte_dusuk_guven()
    {
        var r = new RulesJudge().Decide(JudgeQuestions.ClaimsDone, YesNo, "Bitti sayılır ama devam edeyim mi?");
        Assert.True(r.Confidence < 0.5);
    }

    [Fact]
    public void Kurali_hakem_diger_sorular_ve_bilinmeyen_soru()
    {
        var j = new RulesJudge();
        Assert.Equal("evet", j.Decide(JudgeQuestions.AsksQuestion, YesNo, "Hangisini seçelim?").Answer);
        Assert.Equal("hayır", j.Decide(JudgeQuestions.AsksQuestion, YesNo, "Yaptım.").Answer);
        Assert.Equal("evet", j.Decide(JudgeQuestions.ShowsEvidence, YesNo, "Kontrol: dotnet test → 81 passed").Answer);
        Assert.Equal("evet", j.Decide(JudgeQuestions.IsRule, YesNo, "Bundan sonra her zaman Türkçe yaz, raporları da Türkçe ver.").Answer);
        Assert.Equal("hayır", j.Decide(JudgeQuestions.IsRule, YesNo, "Bu hata neden çıkıyor?").Answer);
        var unk = j.Decide("tamamen_baska_soru", YesNo, "x");
        Assert.Null(unk.Answer);
        Assert.Equal(0, unk.Confidence);
    }

    private static string Fake(TestEnv env, string body)
    {
        var p = Path.Combine(env.Root, "sahte-claude.cmd");
        File.WriteAllText(p, "@echo off\r\n" + body + "\r\n");
        return p;
    }

    [Fact]
    public void ClaudeCli_sahte_komutla_calisir_ve_anahtar_degiskenleri_silinir()
    {
        using var env = new TestEnv(copySamples: false);
        var envFile = Path.Combine(env.Root, "env.txt");
        var cmd = Fake(env, $"if defined ANTHROPIC_API_KEY (echo KEY>\"{envFile}\") else (echo NOKEY>\"{envFile}\")\r\nif defined ANTHROPIC_AUTH_TOKEN (echo TOKEN>>\"{envFile}\")\r\necho Cevap: Evet.");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-degil-gercek");
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "tok-degil-gercek");
        try
        {
            var r = new ClaudeCliJudge(cmd, new RulesJudge()).Decide(JudgeQuestions.ClaimsDone, YesNo, "x");
            Assert.Equal("claude-cli", r.Engine);
            Assert.Equal("evet", r.Answer);
            var seen = File.ReadAllText(envFile);
            Assert.Contains("NOKEY", seen);
            Assert.DoesNotContain("TOKEN", seen);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
        }
    }

    [Fact]
    public void ClaudeCli_basarisizsa_kurali_hakeme_duser()
    {
        using var env = new TestEnv(copySamples: false);
        var bad = Fake(env, "echo hata 1>&2\r\nexit /b 1");
        var r = new ClaudeCliJudge(bad, new RulesJudge()).Decide(JudgeQuestions.ClaimsDone, YesNo, "İş bitti, 3 test geçti.");
        Assert.Equal("rules(yedek)", r.Engine);
        Assert.Equal("evet", r.Answer);
        // anlaşılmaz çıktı
        var junk = Fake(env, "echo belki");
        Assert.Equal("rules(yedek)", new ClaudeCliJudge(junk, new RulesJudge()).Decide(JudgeQuestions.ClaimsDone, YesNo, "Çalışıyorum").Engine);
        // olmayan komut
        Assert.Equal("rules(yedek)", new ClaudeCliJudge(Path.Combine(env.Root, "yok.cmd"), new RulesJudge()).Decide(JudgeQuestions.ClaimsDone, YesNo, "x").Engine);
    }

    [Fact]
    public void ClaudeCli_zaman_asimi_kurali_hakeme_duser()
    {
        using var env = new TestEnv(copySamples: false);
        var slow = Fake(env, "ping -n 8 127.0.0.1 >nul\r\necho evet");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new ClaudeCliJudge(slow, new RulesJudge()) { Timeout = TimeSpan.FromSeconds(1) }.Decide(JudgeQuestions.ClaimsDone, YesNo, "İş bitti.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), "zaman aşımı işlemedi: " + sw.Elapsed);
        Assert.Equal("rules(yedek)", r.Engine);
    }

    [Fact]
    public void Fabrika_varsayilan_kapali_acilinca_cli_ve_her_karar_events_e_yazilir_baglamsiz()
    {
        using var env = new TestEnv(copySamples: false);
        var j = JudgeFactory.Create(env.Svc.Db);
        var r = j.Decide(JudgeQuestions.ClaimsDone, YesNo, "GIZLI-BAGLAM-METNI bitti");
        Assert.Equal("rules", r.Engine);
        using (var c = env.Svc.Db.Open())
        {
            var detail = (string)c.Scalar("SELECT detail FROM events WHERE kind='judge' ORDER BY id DESC LIMIT 1")!;
            var d = JsonNode.Parse(detail)!;
            Assert.Equal("rules", (string?)d["engine"]);
            Assert.NotNull(d["ms"]);
            Assert.DoesNotContain("GIZLI-BAGLAM", detail);
        }
        var cmd = Fake(env, "echo hayir");
        env.Svc.PutSettings(JsonNode.Parse("{\"judgeClaudeEnabled\":true,\"judgeCommand\":" + System.Text.Json.JsonSerializer.Serialize(cmd) + "}")!.AsObject());
        var r2 = JudgeFactory.Create(env.Svc.Db).Decide(JudgeQuestions.ClaimsDone, YesNo, "İş bitti");
        Assert.Equal("claude-cli", r2.Engine);
        Assert.Equal("hayır", r2.Answer);
    }

    [Fact]
    public void Pick_turkce_i_ve_sozcuk_siniri()
    {
        Assert.Equal("hayır", ClaudeCliJudge.Pick("HAYIR.", YesNo));
        Assert.Equal("evet", ClaudeCliJudge.Pick("evet", YesNo));
        Assert.Null(ClaudeCliJudge.Pick("evetçe belki", YesNo));
    }
}
