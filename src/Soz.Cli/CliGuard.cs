using Soz.Core;
using Soz.Core.Security;

namespace Soz.Cli;

/// <summary>
/// "Kabul ve ayar yalnız insandan" komut satırı tarafı (denetim 3, İlke 2, madde 3). Kendi sürecim Söz ajan işindeyse, atalarımdan biri claude ise
/// ya da ajan ortam değişkeni (SOZ_JOB_ID, SOZ_RUN_ID, CLAUDECODE, CLAUDE_CODE_ENTRYPOINT) varsa YAZAN komutlar reddedilir.
/// Okuma komutları ve kanca komutları (hook ...) çalışır. Bu engel en iyi çabadır: aynı kullanıcı olarak çalışan kötü niyetli bir süreç veritabanını doğrudan değiştirebilir.
/// </summary>
public static class CliGuard
{
    /// <summary>Komut ajana açık mı (okuma ya da serbest yazma)? false = yalnız insan.</summary>
    public static bool AgentMayRun(string cmd, Args a)
    {
        var sub = a.Positional.FirstOrDefault()?.ToLowerInvariant();
        switch (cmd)
        {
            case "hook": return true;
            case "projects" or "repeats" or "search" or "packet" or "report" or "status" or "digest" or "explain" or "morning" or "now" or "doctor" or "decisions" or "agents": return true;
            case "registry": return sub is "check" or "list";
            case "usage": return !a.Has("refresh");
            case "guard": return sub is "list" or "templates" or "test" or "events";
            case "undo": return sub is "list"; // yalnız okuma; önizleme ve uygulama CLI'da yok
            case "job": return sub is "list" or "show";
            case "checks": return sub is "list";
            case "proof": return sub is "list" or "show";
            case "project": return sub is "list"; // add ve relocate yalnız insan
            case "queue": return sub is "add" or "list";
            default: return false; // approve/reject/archive/pin/add, gate, hooks, import, export, serve ve bilinmeyenler
        }
    }

    /// <summary>Reddedilecekse sade hata cümlesi, değilse null.</summary>
    public static string? Check(string cmd, Args a, string dataDir, Func<string, string?>? env = null)
    {
        if (AgentMayRun(cmd, a)) return null;
        var who = CallerIdentity.ClassifySelf(dataDir, env);
        return who.IsAgent ? CallerIdentity.HumanOnlyMessage : null;
    }
}
