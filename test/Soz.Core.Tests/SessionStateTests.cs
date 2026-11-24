using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Readers;
using Soz.Core.Sessions;
using Xunit;

namespace Soz.Core.Tests;

public class SessionStateTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static string Ago(double minutes) => Time.Iso(Now.AddMinutes(-minutes));
    private static readonly StateThresholds Th = new();

    private static string State(string? kind, bool ended, double eventMinAgo, double? userMinAgo = null, double? asstMinAgo = null, double? fileMinAgo = null)
        => SessionStateResolver.Resolve(new SessionStateResolver.Input(kind, ended, Ago(eventMinAgo), fileMinAgo is null ? null : Now.AddMinutes(-fileMinAgo.Value),
            userMinAgo is null ? null : Ago(userMinAgo.Value), asstMinAgo is null ? null : Ago(asstMinAgo.Value), Ago(eventMinAgo)), Now, Th).state;

    [Fact] public void Working_When_Tool_Running_Recently() => Assert.Equal("working", State("tool", false, 0.5, 3, 1, 0.5));
    [Fact] public void Working_Up_To_Five_Minutes_Long_Tool_Call() => Assert.Equal("working", State("tool", false, 4, 10, 6, 4));
    [Fact] public void Stalled_When_Turn_Open_Over_Five_Minutes() => Assert.Equal("stalled", State("tool", false, 12, 20, 15, 12));
    [Fact] public void Stalled_When_User_Spoke_And_No_Answer() => Assert.Equal("stalled", State("user", false, 8, 8, 30, 8));
    [Fact] public void Working_When_User_Just_Spoke() => Assert.Equal("working", State("user", false, 1, 1, 30, 1));
    [Fact] public void Waiting_When_Assistant_Ended_Turn() => Assert.Equal("waiting", State("assistant_text", true, 300, 310, 300, 300));
    [Fact] public void Idle_When_Interrupted_And_Quiet() => Assert.Equal("idle", State("other", true, 45, 50, 46, 45));
    [Fact] public void Stale_After_24_Hours() => Assert.Equal("stale", State("assistant_text", true, 25 * 60, 25 * 60 + 5, 25 * 60, 25 * 60));

    [Fact]
    public void Flags_Question_Done_Evidence()
    {
        var (a1, d1, e1) = SessionStateResolver.Flags("Bitti, her şey hazır.");
        Assert.False(a1); Assert.True(d1); Assert.False(e1);   // bitti dedi, kanıt yok
        var (a2, d2, e2) = SessionStateResolver.Flags("Tamamlandı.\n\nKontrol: dotnet test → 42 geçti.");
        Assert.False(a2); Assert.True(d2); Assert.True(e2);
        var (a3, _, _) = SessionStateResolver.Flags("İki yol var.\n\nHangisini istersin?");
        Assert.True(a3);
        var (a4, d4, e4) = SessionStateResolver.Flags("Should I continue with the migration?");
        Assert.True(a4); Assert.False(d4); Assert.False(e4);
        var (_, d5, e5) = SessionStateResolver.Flags("Done. Updated E:\\proje\\src\\a.cs and all tests passed (12/12).");
        Assert.True(d5); Assert.True(e5);
    }

    [Fact]
    public void Digest_Groups_In_Order_And_Shows_Flags()
    {
        var cards = new List<SessionCard>
        {
            new() { Tool = "claude", ProjectName = "a", State = "working", Since = Ago(1), LastUserText = "çalış", LastAssistantText = "çalışıyorum" },
            new() { Tool = "codex", ProjectName = "b", State = "waiting", Since = Ago(30), LastUserText = "raporu yaz", LastAssistantText = "Bitti, hazır.", ClaimsDone = true, SubCount = 3 },
            new() { Tool = "claude", ProjectName = "c", State = "stalled", Since = Ago(20), LastUserText = "devam" },
        };
        var (text, w, s, wk, i) = DigestBuilder.Build(cards, 24, Now);
        Assert.Equal((1, 1, 1, 0), (w, s, wk, i));
        var iw = text.IndexOf("Seni bekleyenler", StringComparison.Ordinal);
        var ist = text.IndexOf("Takılmış olabilir", StringComparison.Ordinal);
        var iwk = text.IndexOf("Çalışanlar", StringComparison.Ordinal);
        Assert.True(iw >= 0 && iw < ist && ist < iwk);
        Assert.Contains("codex · b · 30 dk · 3 alt iş", text);
        Assert.Contains("Sen en son: «raporu yaz»", text);
        Assert.Contains("Yardımcının son sözü: «Bitti, hazır.»", text);
        Assert.Contains("[bitti dedi, kanıt göstermedi]", text);
    }

    [Fact]
    public void Sessions_Api_Shows_Only_Humans_With_SubCount()
    {
        using var env = new TestEnv();
        env.Import();
        var list = env.Svc.Sessions(hours: 24 * 30, includeStale: true, nowUtc: Now);
        Assert.All(list, s => Assert.Equal("human", s.Kind));
        Assert.Equal(4, list.Count); // 3 Claude ana oturum + 1 Codex insan oturumu
        Assert.Equal(1, list.Single(s => s.Title == "Not uygulaması").SubCount);
        var codex = list.Single(s => s.Tool == "codex");
        Assert.Equal(1, codex.SubCount);
        var report = env.Svc.Report(30, Now);
        Assert.Equal(1, report.AutomationSessions);
        Assert.Equal(2, report.SubagentSessions);
    }
}

public class ExplainTests
{
    private static string FakeCommand(TestEnv env, string body)
    {
        var p = Path.Combine(env.Root, "sahte-claude.cmd");
        File.WriteAllText(p, "@echo off\r\n" + body + "\r\n");
        return p;
    }

    [Fact]
    public async Task Disabled_Returns_409()
    {
        using var env = new TestEnv();
        env.Import();
        var sid = env.Svc.Sessions(24 * 30, true, new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc)).First().Id;
        var ex = await Assert.ThrowsAsync<SozException>(() => env.Svc.ExplainAsync(sid));
        Assert.Equal(409, ex.Status);
        Assert.Contains("Ayarlardan açabilirsin", ex.Message);
    }

    [Fact]
    public async Task Fake_Command_Runs_With_Args_Stdin_And_Clean_Env()
    {
        using var env = new TestEnv();
        env.Import();
        var cmd = FakeCommand(env, "echo ARGS=%*\r\nif defined ANTHROPIC_API_KEY (echo ANAHTAR=VAR) else (echo ANAHTAR=YOK)\r\nif defined ANTHROPIC_AUTH_TOKEN (echo TOKEN=VAR) else (echo TOKEN=YOK)\r\nmore");
        env.Svc.PutSettings(new JsonObject { ["explainEnabled"] = true, ["explainCommand"] = cmd });
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sahte-anahtar");
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "sahte-token");
        try
        {
            var sid = env.Svc.Sessions(24 * 30, true, new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc)).Single(s => s.Title == "Not uygulaması").Id;
            var r = await env.Svc.ExplainAsync(sid);
            Assert.True(r.Ok, r.Text);
            Assert.Contains("ARGS=-p --model haiku", r.Text);
            Assert.Contains("ANAHTAR=YOK", r.Text);
            Assert.Contains("TOKEN=YOK", r.Text);
            Assert.Contains("Bitti. Testler", r.Text); // istem stdin'den geçti (ajanın son sözü dahil)
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
        }
    }

    [Fact]
    public async Task Timeout_Kills_Child()
    {
        using var env = new TestEnv();
        var cmd = FakeCommand(env, "ping -n 30 127.0.0.1 >nul");
        using (var c = env.Svc.Db.Open()) SozSettings.Save(c, new JsonObject { ["explainEnabled"] = true, ["explainCommand"] = cmd });
        var r = await new PlainExplainer { Timeout = TimeSpan.FromSeconds(1) }.ExplainAsync(env.Svc.Settings(), "Bir rapor.");
        Assert.False(r.Ok);
        Assert.Contains("zaman aşımı", r.Text);
    }

    [Fact]
    public void Env_Strip_List()
    {
        Assert.True(PlainExplainer.ShouldStrip("ANTHROPIC_API_KEY"));
        Assert.True(PlainExplainer.ShouldStrip("anthropic_auth_token"));
        Assert.True(PlainExplainer.ShouldStrip("CLAUDE_CODE_USE_BEDROCK"));
        Assert.False(PlainExplainer.ShouldStrip("PATH"));
    }
}
