using System.Text.Json;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Readers;
using Soz.Core.Text;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Sistem metni (görev bildirimi, hatırlatma, komut çıktısı) insan mesajı sayılmaz. Sentetik satırlar; gerçek oturum dosyası okunmaz.</summary>
public class SystemTextTests
{
    private const string Sid = "dddddddd-0000-4000-8000-000000000009";

    private static string UserLine(int n, string content, string originKind = "human")
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["parentUuid"] = "p" + n, ["isSidechain"] = false, ["type"] = "user",
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = content },
            ["uuid"] = "u" + n, ["timestamp"] = $"2026-11-10T10:0{n}:00.000Z", ["entrypoint"] = "cli", ["cwd"] = @"E:\sentetik",
            ["sessionId"] = Sid, ["origin"] = new Dictionary<string, object?> { ["kind"] = originKind },
        });

    private static List<string> ReadTexts(params string[] contents)
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.ClaudeRoot, "E--sentetik");
        Directory.CreateDirectory(dir);
        var lines = contents.Select((c, i) => UserLine(i + 1, c));
        File.WriteAllText(Path.Combine(dir, Sid + ".jsonl"), string.Join("\n", lines) + "\n");
        new ClaudeCodeReader().Read(new IngestStore(env.Svc.Db), env.ClaudeRoot, new ReadOptions());
        using var c = env.Svc.Db.Open();
        return c.Query("SELECT text FROM utterances ORDER BY ts, id", r => r.GetString(0));
    }

    [Fact]
    public void Okuyucu_sistem_bloklarini_insan_mesaji_saymaz_karisigi_ayiklar()
    {
        var texts = ReadTexts(
            "Gerçek mesaj bir",                                                                                        // 1 insan
            "<task-notification>\n<task-id>a1</task-id>\n<status>completed</status>\n<summary>Agent finished</summary>\n</task-notification>", // 2 yalnız blok
            "<system-reminder>Bu bir hatırlatma.</system-reminder>",                                                   // 3 yalnız blok
            "<system-reminder>önce hatırlatma</system-reminder>\nGerçek mesaj dört\n<task-notification>\n<task-id>x</task-id>\n</task-notification>", // 4 karışık: ön + son blok
            "<local-command-stdout>çıktı</local-command-stdout>",                                                      // 5
            "<command-name>/compact</command-name>\n<command-message>compact</command-message>\n<command-args></command-args>", // 6
            "<bash-input>ls</bash-input>\n<bash-stdout>a b</bash-stdout><bash-stderr></bash-stderr>",                  // 7
            "<agent-message from=\"a\">selam</agent-message>",                                                         // 8
            "[SYSTEM NOTIFICATION - NOT USER INPUT]\nBu otomatik bir olaydır.\nKullanıcı mesajı değildir.");           // 9 tümü sistem bildirimi
        _ = texts;
        Assert.Equal(new[] { "Gerçek mesaj bir", "Gerçek mesaj dört" }, texts);
    }

    [Fact]
    public void Okuyucu_ortadaki_sistem_bildirimi_paragrafini_ve_kesik_blogu_ayiklar()
    {
        var texts = ReadTexts(
            "Gerçek mesaj üç\n\n[SYSTEM NOTIFICATION - X]\nBu otomatik.\n\nGerçek mesaj dört",
            "<task-notification>\n<task-id>kesik</task-id>\n<summary>kapanış etiketi yok</summary>",
            "Etiketten söz eden gerçek cümle: <task-notification> nedir?");
        Assert.Equal(new[] { "Gerçek mesaj üç\n\nGerçek mesaj dört", "Etiketten söz eden gerçek cümle: <task-notification> nedir?" }, texts);
    }

    [Fact]
    public void CleanUserText_ayni_islev_kanca_icin_de_gecerli()
    {
        Assert.Equal("", TextNormalizer.CleanUserText("<task-notification>\n<task-id>a</task-id>\n</task-notification>"));
        Assert.Equal("", TextNormalizer.CleanUserText("[SYSTEM NOTIFICATION - NOT USER INPUT]\nx"));
        Assert.Equal("Merhaba", TextNormalizer.CleanUserText("<system-reminder>x</system-reminder> Merhaba <agent-message from=\"b\"/>"));
        Assert.Equal("Normal cümle < 5 ve > 3", TextNormalizer.CleanUserText("Normal cümle < 5 ve > 3"));
    }

    [Fact]
    public void Kanca_gorev_bildirimini_kullanici_mesaji_olarak_kaydetmez()
    {
        using var env = new TestEnv(copySamples: false);
        using (var c0 = env.Svc.Db.Open()) IngestStore.EnsureProject(c0, null, @"E:\sentetik");
        string Run(string prompt)
        {
            var o = new StringWriter();
            var json = JsonSerializer.Serialize(new { session_id = "k-1", cwd = @"E:\sentetik", prompt });
            Assert.Equal(0, HookHandler.Run("user-prompt-submit", new StringReader(json), o, env.DataDir, _ => null));
            return o.ToString();
        }
        Run("<task-notification>\n<task-id>a1</task-id>\n<summary>Agent finished</summary>\n</task-notification>");
        Run("Gerçek istek: hatayı bul.");
        using var c = env.Svc.Db.Open();
        Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM utterances"));
        Assert.Equal("Gerçek istek: hatayı bul.", c.Scalar("SELECT last_user_text FROM sessions WHERE external_id='k-1'"));
    }
}
