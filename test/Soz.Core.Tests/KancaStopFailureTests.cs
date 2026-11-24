using System.Text.Json;
using Soz.Core.Attention;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>k6: StopFailure kancası ("hata verip durdu") ve Notification tür değişimi.</summary>
public class KancaStopFailureTests
{
    private static string J(object o) => JsonSerializer.Serialize(o);

    private static void Hook(TestEnv env, string ev, string stdin)
    {
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run(ev, new StringReader(stdin), o, env.DataDir, _ => null));
        Assert.Equal("", o.ToString()); // gözlem kancası: çıktı yok
    }

    private static (string? since, string? kind) Row(TestEnv env)
    {
        using var c = env.Svc.Db.Open();
        return c.Query("SELECT needs_user_since, needs_user_kind FROM sessions WHERE external_id='sf1'", r => (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1))).Single();
    }

    [Fact]
    public void StopFailure_isaretler_ve_yeni_istem_kaldirir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "p");
        Directory.CreateDirectory(dir);
        Hook(env, "stop-failure", J(new { session_id = "sf1", cwd = dir, hook_event_name = "StopFailure", error = "rate_limit" }));
        var (since, kind) = Row(env);
        Assert.NotNull(since);
        Assert.Equal("failure:rate_limit", kind);
        Assert.Equal(FailureProbe.Limit, FailureProbe.FromHookKind(kind, since)!.Key);

        Hook(env, "user-prompt-submit", J(new { session_id = "sf1", cwd = dir, prompt = "devam" }));
        var after = Row(env);
        Assert.Null(after.since);
        Assert.Null(after.kind);
    }

    [Theory]
    [InlineData("authentication_failed", "failure:authentication_failed")]
    [InlineData("", "failure:unknown")]
    [InlineData("Garip; DROP TABLE", "failure:unknown")]
    public void StopFailure_hata_adi_guvenli_yazilir(string error, string beklenen)
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "p");
        Directory.CreateDirectory(dir);
        Hook(env, "stop-failure", J(new { session_id = "sf1", cwd = dir, error }));
        Assert.Equal(beklenen, Row(env).kind);
    }

    [Fact]
    public void Notification_tur_degisince_bekleme_suresi_sifirlanir_ayni_turde_korunur()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "p");
        Directory.CreateDirectory(dir);
        const string Sentinel = "2000-01-01T00:00:00.000Z"; // test saati sabit olabilir: işaret elle bilinen bir değere çekilir
        Hook(env, "notification", J(new { session_id = "sf1", cwd = dir, notification_type = "idle_prompt" }));
        using (var c = env.Svc.Db.Open()) c.Exec("UPDATE sessions SET needs_user_since=@s WHERE external_id='sf1'", null, ("@s", Sentinel));
        Hook(env, "notification", J(new { session_id = "sf1", cwd = dir, notification_type = "idle_prompt" }));
        Assert.Equal(Sentinel, Row(env).since); // aynı tür: ilk bekleme anı korunur
        Hook(env, "notification", J(new { session_id = "sf1", cwd = dir, notification_type = "permission_prompt" }));
        var changed = Row(env);
        Assert.Equal("permission_prompt", changed.kind);
        Assert.NotEqual(Sentinel, changed.since); // tür değişti: yeni bekleme
    }

    [Fact]
    public void ArgOf_stop_ile_stop_failure_karismaz()
    {
        var cmd = System.Text.Json.Nodes.JsonNode.Parse(J(new { type = "command", command = HookInstaller.BuildCommand(@"C:\x\soz-kanca.exe", "stop-failure", @"C:\veri", null) }));
        Assert.Equal("stop-failure", HookInstaller.ArgOf(cmd));
        var stop = System.Text.Json.Nodes.JsonNode.Parse(J(new { type = "command", command = HookInstaller.BuildCommand(@"C:\x\soz-kanca.exe", "stop", @"C:\veri", null) }));
        Assert.Equal("stop", HookInstaller.ArgOf(stop));
        var http = System.Text.Json.Nodes.JsonNode.Parse(J(new { type = "http", url = "http://127.0.0.1:49300/api/hooks/stop-failure" }));
        Assert.Equal("stop-failure", HookInstaller.ArgOf(http));
    }
}
