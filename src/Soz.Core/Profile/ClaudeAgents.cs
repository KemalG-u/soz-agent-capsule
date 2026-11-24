using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Soz.Core.Run;

namespace Soz.Core.Profile;

/// <summary><c>claude agents --json</c> komutunun ham çıktısını verir. Gerçek uygulama komutu çalıştırır; testler sahte JSON verir.</summary>
public interface IClaudeAgentsCommand
{
    /// <summary>JSON metni; çalıştırılamadıysa null ve <paramref name="failure"/> içinde kısa neden.</summary>
    string? Run(out string? failure);
}

/// <summary>Claude'un açık oturumları ve nereden öğrenildiği.</summary>
/// <param name="Source"><see cref="LiveRules.SourceCommand"/>, <see cref="LiveRules.SourceRegistry"/> ya da <see cref="LiveRules.SourceProcess"/>.</param>
/// <param name="Note">Komut yerine yedek yola düşüldüyse kişiye söylenecek sade cümle (komut adı, ham neden ve süreç sözcüğü yok); yoksa null.</param>
/// <param name="Technical">Aynı durumun teknik nedeni (yalnız teknik ayrıntı ve günlük için, ekranda cümle olarak gösterilmez); yoksa null.</param>
public sealed record ClaudeAgentsResult(IReadOnlyList<ClaudeRegistryEntry> Entries, string Source, string? Note, string? Technical = null);

public static class ClaudeAgents
{
    /// <summary>Komut yerine yedek yola düşüldüğünde kişiye giden tek sade cümle.</summary>
    public const string SoftNote = "Claude'un açık pencerelerinin hepsini şu an göremiyorum.";

    /// <summary>
    /// Birincil kaynak <c>claude agents --json</c>. Komut yok, eski, zaman aşımı ya da bozuk çıktı verirse Claude'un süreç kayıt dosyalarına
    /// (<c>sessions\&lt;pid&gt;.json</c>) düşülür ve bu <see cref="ClaudeAgentsResult.Note"/> içinde söylenir.
    /// <paramref name="command"/> null ise (sentetik kökler) komut hiç denenmez ve not yazılmaz.
    /// </summary>
    public static ClaudeAgentsResult Read(IClaudeAgentsCommand? command, string? claudeHome)
    {
        string? note = null, technical = null;
        if (command is not null)
        {
            string? failure;
            string? json;
            try { json = command.Run(out failure); }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { json = null; failure = "komut-hatasi"; }
            if (json is not null)
            {
                var entries = Parse(json);
                if (entries is not null) return new(entries, LiveRules.SourceCommand, null);
                failure = "cikti-taninmadi";
            }
            note = SoftNote;
            technical = "claude agents komutundan bilgi alınamadı (" + FailureText(failure) + "); süreç kayıt dosyalarına dönüldü.";
        }
        var fromFiles = ClaudeSessionRegistry.Read(claudeHome);
        return new(fromFiles, fromFiles.Count > 0 ? LiveRules.SourceRegistry : LiveRules.SourceProcess, note, technical);
    }

    private static string FailureText(string? failure) => failure switch
    {
        "komut-yok" => "claude komutu bulunamadı",
        "zaman-asimi" => "komut süresinde bitmedi",
        "cikti-taninmadi" => "çıktısı tanınmadı",
        "eski-surum" => "bu Claude sürümü komutu bilmiyor",
        _ => "komut çalışmadı",
    };

    /// <summary>
    /// <c>[ { pid, cwd, kind, startedAt, sessionId, name, status, waitingFor }, ... ]</c>. Dizi değilse null (tanınmadı); boş dizi geçerlidir.
    /// pid ya da oturum kimliği olmayan öğe atlanır.
    /// </summary>
    public static IReadOnlyList<ClaudeRegistryEntry>? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<ClaudeRegistryEntry>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var pid = ClaudeSessionRegistry.Num(item, "pid");
                if (pid is null or <= 0 or > int.MaxValue) continue;
                var sessionId = ClaudeSessionRegistry.Text(item, "sessionId", 128);
                if (sessionId is null) continue;
                DateTime? started = ClaudeSessionRegistry.Num(item, "startedAt") is { } ms and > 0 ? ProfileSources.FromEpoch(ms) : null;
                list.Add(new((int)pid.Value, sessionId, null, null, ClaudeSessionRegistry.Text(item, "cwd", 1024),
                    ClaudeSessionRegistry.Text(item, "kind", 32) ?? "interactive", started,
                    ClaudeSessionRegistry.Text(item, "name", 120), ClaudeSessionRegistry.Text(item, "status", 32),
                    ClaudeSessionRegistry.Text(item, "waitingFor", 160), FromCommand: true));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Gerçek <c>claude agents --json</c> çağrısı: sınırlı süre, girdi kapalı, çıktı sınırlı. Model çağırmaz, kota harcamaz.</summary>
public sealed class ClaudeAgentsProcessCommand(string? configuredClaudePath, TimeSpan? timeout = null) : IClaudeAgentsCommand
{
    private const int MaxOutputChars = 512 * 1024;

    public string? Run(out string? failure)
    {
        failure = null;
        var cmd = ClaudeLocator.Resolve(configuredClaudePath);
        if (cmd is null) { failure = "komut-yok"; return null; }
        var limit = timeout ?? LiveRules.AgentsCommandTimeout;
        var psi = new ProcessStartInfo(cmd.FileName)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home && Directory.Exists(home) ? home : Path.GetTempPath(),
        };
        foreach (var a in cmd.PrefixArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("agents");
        psi.ArgumentList.Add("--json");
        if (cmd.Env is not null)
            foreach (var (k, v) in cmd.Env) psi.Environment[k] = v;
        using var p = Process.Start(psi);
        if (p is null) { failure = "komut-hatasi"; return null; }
        try { p.StandardInput.Close(); } catch (IOException) { }
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)limit.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            failure = "zaman-asimi";
            return null;
        }
        Task.WaitAll([outTask, errTask], 2000);
        if (p.ExitCode != 0) { failure = "eski-surum"; return null; }
        if (!outTask.IsCompletedSuccessfully) { failure = "zaman-asimi"; return null; }
        var text = outTask.Result;
        return text.Length > MaxOutputChars ? text[..MaxOutputChars] : text;
    }
}
