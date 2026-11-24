using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Run;
using Soz.Core.Text;

namespace Soz.Core.Guard;

/// <summary>
/// `soz hook pre-tool-use` mantığı. Girdi (Claude Code, code.claude.com/docs/en/hooks, 2026-11-10 okundu): session_id, cwd, tool_name, tool_input.
/// Çıktı: {"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny|ask","permissionDecisionReason":"…"}}; eşleşme yoksa çıktı yok.
/// Hata yolları HookHandler.Run tarafından yutulur (çıkış 0, çıktı yok).
/// </summary>
public static class GuardHook
{
    public static JsonObject? Handle(SqliteConnection c, JsonObject input, Func<string, string?> env, bool skipEnsureTables = false)
    {
        var tool = Str(input, "tool_name");
        if (string.IsNullOrEmpty(tool)) return null;
        if (!GuardEngine.IsFileTool(tool) && !GuardEngine.IsShellTool(tool)) return null; // hızlı çıkış: MCP vb. değerlendirilmez
        if (!skipEnsureTables) GuardStore.EnsureTables(c);
        var cwd = Str(input, "cwd");
        var dir = env("CLAUDE_PROJECT_DIR") ?? cwd;
        var (pid, found) = HookHandler.FindProject(c, dir);
        var dataDir = string.IsNullOrEmpty(c.DataSource) ? null : Path.GetDirectoryName(c.DataSource);
        // Söz koşusunda proje klasörünün dışına Write/Edit ile yazılırsa iş için kaydedilir (kanıt kartı not düşer, denetim 5, B11). Kural olmasa da çalışır.
        if (GuardEngine.IsFileTool(tool) && long.TryParse(env("SOZ_JOB_ID"), out var outJob)) RecordOutsideWrite(c, input, tool, pid, found ?? dir, outJob, env);
        // Söz koşusunda (SOZ_RUN_ID) şablondan bağımsız: ajan Söz'ün kendisini kullanamaz (denetim 3, ilke 2).
        if (long.TryParse(env("SOZ_RUN_ID"), out _) && SelfBlock(c, input, tool, pid, dataDir) is { } blocked) return blocked;
        var rules = GuardStore.RulesFor(c, pid);
        if (rules.Count == 0) return null;

        var home = env("USERPROFILE") ?? env("HOME") ?? NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var temp = env("TEMP") ?? env("TMP") ?? NullIfEmpty(Path.GetTempPath());
        var gi = new GuardInput(tool, input["tool_input"], cwd ?? dir, pid is null ? dir : found ?? dir, home, temp, dataDir);
        var d = GuardEngine.Evaluate(rules, gi);
        if (d.Verdict == GuardActions.Allow) return null;

        // Söz yürütücüsü (başsız kip): "sor" soru kutusu açamaz. Onay isteği kaydedilir, işlem reddedilir; kullanıcı arayüzden Evet derse
        // aynı işleme tek seferlik izin yazılır (SOZ_RUN_ID ve SOZ_JOB_ID yürütücünün çocuk sürecine verdiği ortam değişkenleridir).
        if (d.Verdict == GuardActions.Ask && long.TryParse(env("SOZ_RUN_ID"), out var runId) && long.TryParse(env("SOZ_JOB_ID"), out var jobId))
            return HeadlessAsk(c, input, tool, d, pid, runId, jobId);

        try { GuardStore.AddEvent(c, pid, Str(input, "session_id"), tool, d.TargetSummary, d.Rule, d.Verdict, d.Reason); }
        catch (SqliteException) { /* olay yazılamasa da karar verilir */ }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = d.Verdict,
                ["permissionDecisionReason"] = d.Reason,
            },
        };
    }

    /// <summary>Kanca kaydı: bir Söz işinde ajanın proje klasörü DIŞINA yazdığı dosya (yalnız Write, Edit, MultiEdit, NotebookEdit; kabuk komutuyla yazılan yollar görülmez). Geçici klasör ve Claude'un kendi dosyaları sayılmaz.</summary>
    public const string OutsideWriteEvent = "job_outside_write";

    private static void RecordOutsideWrite(SqliteConnection c, JsonObject input, string tool, long? pid, string? root, long jobId, Func<string, string?> env)
    {
        try
        {
            if (pid is null || string.IsNullOrWhiteSpace(root)) return;
            var ti = input["tool_input"];
            var p = GuardEngine.Str(ti, "file_path") ?? GuardEngine.Str(ti, "notebook_path") ?? GuardEngine.Str(ti, "path");
            if (string.IsNullOrWhiteSpace(p)) return;
            var home = env("USERPROFILE") ?? env("HOME") ?? NullIfEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var temp = env("TEMP") ?? env("TMP") ?? NullIfEmpty(Path.GetTempPath());
            var n = GuardPaths.Normalize(p, Str(input, "cwd") ?? root, home, temp);
            var rn = GuardPaths.Normalize(root, null, home, temp);
            if (n is null || rn is null || GuardPaths.IsUnder(n.Key, rn.Key)) return;
            if (temp is not null && GuardPaths.Normalize(temp, null, null, null) is { } tn && GuardPaths.IsUnder(n.Key, tn.Key)) return;
            if (!string.IsNullOrEmpty(home) && GuardEngine.ClaudeOwnGlobs.Any(g => GuardPaths.GlobMatch(g, n, null, home, temp))) return;
            var shown = SecretFilter.MaskSecrets(n.Display);
            var seen = c.Query("SELECT detail FROM events WHERE kind=@k AND json_extract(detail,'$.jobId')=@j AND json_extract(detail,'$.path')=@p LIMIT 1", r => r.Str(0), null, ("@k", OutsideWriteEvent), ("@j", jobId), ("@p", shown));
            if (seen.Count > 0) return;
            Events.Log(c, OutsideWriteEvent, pid, System.Text.Json.JsonSerializer.Serialize(new { jobId, path = shown, tool }));
        }
        catch (Exception e) when (e is SqliteException or ArgumentException or IOException) { /* kayıt yazılamazsa ajan engellenmez */ }
    }

    private static JsonObject? SelfBlock(SqliteConnection c, JsonObject input, string tool, long? pid, string? dataDir)
    {
        string? why = null; var escape = false;
        string summary = "";
        if (GuardEngine.IsShellTool(tool))
        {
            summary = GuardEngine.Str(input["tool_input"], "command") ?? "";
            why = SozSelfGuard.HitAny(ShellParser.Parse(summary, tool.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)), dataDir);
            if (why is null && EscapeGuard.HitCommand(summary) is { } esc) { escape = true; why = esc; }
        }
        else if (GuardEngine.IsFileTool(tool))
        {
            var p = GuardEngine.Str(input["tool_input"], "file_path") ?? GuardEngine.Str(input["tool_input"], "notebook_path") ?? GuardEngine.Str(input["tool_input"], "path");
            summary = p ?? "";
            if (!string.IsNullOrWhiteSpace(dataDir) && p is not null && GuardPaths.Normalize(p, GuardEngine.Str(input, "cwd"), null, null) is { } n && GuardPaths.Normalize(dataDir, null, null, null) is { } dn && GuardPaths.IsUnder(n.Key, dn.Key))
                why = "Söz'ün veri klasörüne yazılıyor";
            else if (p is not null && EscapeGuard.HitPath(p, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is { } escp) { escape = true; why = escp; }
        }
        if (why is null) return null;
        var reason = escape
            ? $"Söz bekçisi durdurdu: {why}. Söz'ün yürüttüğü işte süreç kaçıran ya da kalıcı ayar değiştiren komutlar kapalıdır. Başka bir yol deneme. Durumu tek cümleyle yaz."
            : $"Söz bekçisi durdurdu: {why}. Bu işlem yalnız insanın elindedir. Yardımcılar Söz'ü kendi eliyle kullanamaz. Başka bir yol deneme. Durumu tek cümleyle yaz.";
        try { GuardStore.AddEvent(c, pid, Str(input, "session_id"), tool, summary, null, GuardActions.Deny, reason); }
        catch (SqliteException) { }
        return Decision(GuardActions.Deny, reason);
    }

    private static JsonObject Decision(string verdict, string reason) => new()
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = verdict,
            ["permissionDecisionReason"] = reason,
        },
    };

    private static JsonObject HeadlessAsk(SqliteConnection c, JsonObject input, string tool, GuardDecision d, long? pid, long runId, long jobId)
    {
        var hash = RunStore.ApprovalHash(tool, input["tool_input"]);
        var latest = RunStore.FindApproval(c, jobId, hash, "allowed", "denied", "pending");
        if (latest is not null)
        {
            if (latest.Status == "allowed" && RunStore.ConsumeAllowance(c, RunStore.ApprovalRowId(c, jobId, hash, "allowed")))
            {
                try { Events.Log(c, "approval_used", pid, System.Text.Json.JsonSerializer.Serialize(new { jobId, runId, eventId = latest.EventId })); } catch (SqliteException) { }
                return Decision(GuardActions.Allow, "Kullanıcı bu işlemi Söz arayüzünden tek seferlik onayladı.");
            }
            if (latest.Status == "denied")
                return Decision(GuardActions.Deny, "Kullanıcı bu işlemi istemedi. Başka bir yol dene ya da bunun yapılamadığını açıkla.");
            if (latest.Status == "pending")
                return Decision(GuardActions.Deny, "Bu işlem için kullanıcının onayı bekleniyor. Başka bir şey deneme. Onay gelene kadar bekle ve durumu tek cümleyle yaz.");
        }
        var summary = TextNormalizer.Truncate(SecretFilter.MaskSecrets(d.TargetSummary ?? tool).ReplaceLineEndings(" "), 160);
        var reason = $"Söz bekçisi: bu işlem kullanıcıdan onay istiyor ({d.Rule?.Label ?? "kural"}). Onay istendi. Başka bir yol deneme. Durumu tek cümleyle yaz ve dur.";
        long evId = 0;
        try { evId = GuardStore.AddEvent(c, pid, Str(input, "session_id"), tool, d.TargetSummary ?? tool, d.Rule, GuardActions.Ask, d.Reason); }
        catch (SqliteException) { }
        RunStore.AddApproval(c, runId, jobId, evId, tool, summary, hash, TextNormalizer.Truncate(SecretFilter.MaskSecrets(d.TargetSummary ?? tool).ReplaceLineEndings(" "), 600));
        try { RunStore.AddLine(c, runId, jobId, pid, "approval", $"senden onay bekliyor: {summary}", null); RunStore.EmitState(c, runId); }
        catch (SqliteException) { }
        return Decision(GuardActions.Deny, reason);
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Kuru deneme ("dene" kutusu): ne karar verirdi; olay yazılmaz.</summary>
public static class GuardService
{
    public sealed record TestResult(string Verdict, long? RuleId, string? RuleLabel, string? RuleKind, string? Reason, bool Unknown, IReadOnlyList<string> Targets, string Note);

    public static TestResult Test(SozDb db, long? projectId, string toolName, JsonNode? toolInput, string? cwd = null)
    {
        using var c = db.Open();
        GuardStore.EnsureTables(c);
        string? root = null;
        if (projectId is { } p) root = c.Scalar("SELECT path FROM projects WHERE id=@i", null, ("@i", p)) as string;
        if (toolInput is JsonValue v && v.TryGetValue<string>(out var s))
            toolInput = GuardEngine.IsShellTool(toolName) ? new JsonObject { ["command"] = s } : new JsonObject { ["file_path"] = s };
        var rules = GuardStore.RulesFor(c, projectId);
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var temp = Environment.GetEnvironmentVariable("TEMP") ?? Environment.GetEnvironmentVariable("TMP") ?? Path.GetTempPath();
        var d = GuardEngine.Evaluate(rules, new GuardInput(toolName, toolInput, cwd ?? root, root, home, temp, db.DataDir));
        var note = rules.Count == 0 ? "Bu proje için etkin kural yok."
            : d.Verdict == GuardActions.Allow ? (d.Unknown ? "Hedef yol çıkarılamadı (bilinmiyor): engellenmez." : "Hiçbir kurala uymadı.") : "";
        return new TestResult(d.Verdict, d.Rule?.Id, d.Rule?.Label, d.Rule?.Kind, d.Reason, d.Unknown, d.Targets, note);
    }
}
