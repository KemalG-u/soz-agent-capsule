using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Text;

namespace Soz.Core.Run;

public static class RunStatus
{
    public const string Running = "running", WaitingApproval = "waiting_approval", Stopped = "stopped", Done = "done", Failed = "failed";
    public static bool IsActive(string s) => s is Running;
}

public sealed record ApprovalDto(long EventId, long JobId, long RunId, string Tool, string Summary, string Status, string? CreatedAt, string? DecidedAt, string? Detail = null, string? Uid = null);
/// <summary>Agent: koşuyu yapan ajan (claude | codex). Harcama alanları (token sayıları, aracın bildirdiği dolar) turun sonunda yazılır; araç bildirmiyorsa null.</summary>
public sealed record RunDto(long Id, long JobId, long? ProjectId, string? SessionId, int? Pid, string Status, string? Started, string? Ended, int? ExitCode, string? LastLine, string? FailReason, string Kind,
    string Agent = "claude", long? InputTokens = null, long? OutputTokens = null, long? CachedTokens = null, long? ReasoningTokens = null, double? CostUsd = null, string? Uid = null,
    string? BeforeSnapshotUid = null, string? AfterSnapshotUid = null, string? Protection = null, string? ProtectionNote = null);
/// <summary>İş kartındaki kısa yürütücü özeti (en son süreç).</summary>
public sealed record JobRunInfo(long RunId, string Status, string? LastLine, string? FailReason, string? Started, string? Ended, ApprovalDto? PendingApproval);
/// <summary>Zaman çizelgesi satırı. Kind: user | progress | agent | proof | approval | state | error. Detail yalnız istenirse (detail=1) dolar.</summary>
/// <summary>
/// Id kalıcıdır ve SSE ile aynı kimliği verir: "u" (kullanıcının ilk sözü), "l{lineId}" (run_lines, SSE job.progress lineId), "a{eventId}" (onay), "p{proofRunId}" (kanıt).
/// detail=1 iken biten kanıt satırında Attempt ve Results gömülüdür (satır başına ek istek gerekmez).
/// </summary>
public sealed record TimelineItem(string At, string Kind, string Text, string? Detail, long? RunId, long? EventId, string? Status, string? Verdict, long? ProofRunId,
    string? Id = null, long? LineId = null, int? Attempt = null, List<ProofResultDto>? Results = null,
    string? ReasonCode = null, string? ReasonText = null, List<string>? ChangedFiles = null, List<string>? AddedTests = null, bool CountUnknown = false, string? VerdictLabel = null, string? BaselineText = null,
    string? SnapshotId = null, List<Soz.Core.Proof.EvidenceBase.FileDiff>? Diffs = null, List<string>? Notes = null);

/// <summary>runs, run_lines ve run_approvals tabloları. Canlı olaylar (job_progress, job_state) events tablosuna yazılır; sunucu SSE'ye çevirir.</summary>
public static class RunStore
{
    public const string ProgressKind = "job_progress", StateKind = "job_state";

    private const string Cols = "id, job_id, project_id, session_id, pid, status, started, ended, exit_code, last_line, fail_reason, kind, COALESCE(agent,'claude'), input_tokens, output_tokens, cached_tokens, reasoning_tokens, cost_usd, uid, before_snapshot_uid, after_snapshot_uid, protection, protection_note";
    private static RunDto Map(SqliteDataReader r) => new(r.GetInt64(0), r.Long(1), r.LongN(2), r.Str(3), r.LongN(4) is { } p ? (int)p : null, r.Str(5) ?? RunStatus.Failed,
        r.Str(6), r.Str(7), r.LongN(8) is { } e ? (int)e : null, r.Str(9), r.Str(10), r.Str(11) ?? "start",
        r.Str(12) ?? "claude", r.LongN(13), r.LongN(14), r.LongN(15), r.LongN(16), r.IsDBNull(17) ? null : r.GetDouble(17), r.Str(18), r.Str(19), r.Str(20), r.Str(21), r.Str(22));

    /// <summary>Koşu kaydı. Ajan verilmezse işin ajanı yazılır (işte de yoksa claude).</summary>
    public static long Insert(SqliteConnection c, long jobId, long projectId, string kind, string? agent = null)
    {
        c.Exec("INSERT INTO runs(job_id, project_id, status, started, kind, agent, uid) VALUES(@j,@p,'running',@n,@k, COALESCE(@a,(SELECT agent FROM jobs WHERE id=@j),'claude'),@u)", null,
            ("@u", Uid.New()), ("@j", jobId), ("@p", projectId), ("@n", Time.NowIso()), ("@k", kind), ("@a", agent));
        return c.ScalarLong("SELECT last_insert_rowid()");
    }

    /// <summary>Geri alma kancası: koşunun önce görüntüsü (kimlik, koruma düzeyi ve nedeni). Görüntü alınamadıysa uid null, düzey "yok" ve neden yazılır.</summary>
    public static void SetBeforeSnapshot(SqliteConnection c, long runId, string? snapshotUid, string protection, string? note)
        => c.Exec("UPDATE runs SET before_snapshot_uid=@u, protection=@p, protection_note=@n WHERE id=@i", null, ("@u", snapshotUid), ("@p", protection), ("@n", note), ("@i", runId));

    /// <summary>
    /// Geri alma kancası: koşunun sonraki görüntüsü (ajanın son hâli). Uid null ise görüntü alınamadı: koruma en çok "sınırlı" olur (geri alma çalışır ama ajanın son hâli bilinmez).
    /// Koruma düzeyi iki görüntüden kötü olanıdır; önce görüntü hiç alınamamışsa ("yok") öyle kalır.
    /// </summary>
    public static void SetAfterSnapshot(SqliteConnection c, long runId, string? snapshotUid, string afterProtection, string? note)
        => c.Exec("UPDATE runs SET after_snapshot_uid=@u, protection=CASE WHEN protection='yok' THEN 'yok' WHEN @p IN ('yok','sinirli') THEN 'sinirli' ELSE COALESCE(protection,@p) END, protection_note=COALESCE(@n, protection_note) WHERE id=@i",
            null, ("@u", snapshotUid), ("@p", afterProtection), ("@n", note), ("@i", runId));

    /// <summary>Turun harcaması (12. işlem): aracın bildirdiği sayılar koşuya yazılır. Boşsa hiçbir şey yazılmaz.</summary>
    public static void SetUsage(SqliteConnection c, long runId, Soz.Core.Agents.AgentUsage? u)
    {
        if (u is null || u.IsEmpty) return;
        c.Exec("UPDATE runs SET input_tokens=@i, cached_tokens=@c, output_tokens=@o, reasoning_tokens=@r, cost_usd=@d WHERE id=@id", null,
            ("@i", u.InputTokens), ("@c", u.CachedInputTokens), ("@o", u.OutputTokens), ("@r", u.ReasoningTokens), ("@d", u.CostUsd), ("@id", runId));
    }

    /// <summary>İşin toplam harcaması (tüm koşuları). Hiçbir koşu harcama bildirmediyse null.</summary>
    public static JobSpend? SpendFor(SqliteConnection c, long jobId)
    {
        var row = c.Query("SELECT COUNT(*), SUM(input_tokens), SUM(cached_tokens), SUM(output_tokens), SUM(reasoning_tokens), SUM(cost_usd) FROM runs WHERE job_id=@j AND (input_tokens IS NOT NULL OR output_tokens IS NOT NULL OR cost_usd IS NOT NULL)",
            r => (n: r.GetInt64(0), i: r.LongN(1), ca: r.LongN(2), o: r.LongN(3), re: r.LongN(4), d: r.IsDBNull(5) ? (double?)null : r.GetDouble(5)), null, ("@j", jobId)).FirstOrDefault();
        return row.n == 0 ? null : new JobSpend((int)row.n, row.i, row.ca, row.o, row.re, row.d);
    }

    public static RunDto? Get(SqliteConnection c, long id) => c.Query($"SELECT {Cols} FROM runs WHERE id=@i", Map, null, ("@i", id)).FirstOrDefault();

    public static RunDto? Latest(SqliteConnection c, long jobId)
        => c.Query($"SELECT {Cols} FROM runs WHERE job_id=@j ORDER BY id DESC LIMIT 1", Map, null, ("@j", jobId)).FirstOrDefault();

    public static List<RunDto> ListForJob(SqliteConnection c, long jobId, int limit = 50)
        => c.Query($"SELECT {Cols} FROM runs WHERE job_id=@j ORDER BY id DESC LIMIT @l", Map, null, ("@j", jobId), ("@l", limit));

    public static List<RunDto> WithStatus(SqliteConnection c, string status)
        => c.Query($"SELECT {Cols} FROM runs WHERE status=@s ORDER BY id", Map, null, ("@s", status));

    public static void SetProcess(SqliteConnection c, long runId, int pid, DateTime startUtc)
        => c.Exec("UPDATE runs SET pid=@p, proc_start=@s WHERE id=@i", null, ("@p", pid), ("@s", Time.Iso(startUtc)), ("@i", runId));

    public static (int? pid, DateTime? start) ProcessOf(SqliteConnection c, long runId)
    {
        var row = c.Query("SELECT pid, proc_start FROM runs WHERE id=@i", r => (pid: r.LongN(0), s: r.Str(1)), null, ("@i", runId)).FirstOrDefault();
        return (row.pid is { } p ? (int)p : null, Time.Parse(row.s));
    }

    public static void SetSession(SqliteConnection c, long runId, string sessionId)
        => c.Exec("UPDATE runs SET session_id=@s WHERE id=@i", null, ("@s", sessionId), ("@i", runId));

    /// <summary>Son oturum kimliği (işin en son süreçlerinden): --resume için.</summary>
    public static string? LastSessionId(SqliteConnection c, long jobId)
        => c.Scalar("SELECT session_id FROM runs WHERE job_id=@j AND session_id IS NOT NULL AND session_id<>'' ORDER BY id DESC LIMIT 1", null, ("@j", jobId)) as string;

    /// <summary>Durumu günceller ve job_state olayı yazar. Zaten bitmiş (stopped/failed/done) satırı çalışıyora geri çevirmez.</summary>
    public static bool SetStatus(SqliteConnection c, long runId, string status, string? lastLine = null, string? failReason = null, int? exitCode = null, bool ended = false, bool force = false)
    {
        var n = c.Exec("""
            UPDATE runs SET status=@s, last_line=COALESCE(@l,last_line), fail_reason=COALESCE(@f,fail_reason), exit_code=COALESCE(@e,exit_code),
              ended = CASE WHEN @end=1 THEN COALESCE(ended,@n) ELSE ended END
            WHERE id=@i AND (@force=1 OR status IN ('running','waiting_approval'))
            """, null, ("@s", status), ("@l", lastLine), ("@f", failReason), ("@e", exitCode), ("@end", ended ? 1 : 0), ("@n", Time.NowIso()), ("@i", runId), ("@force", force ? 1 : 0));
        if (n > 0) EmitState(c, runId);
        return n > 0;
    }

    public static void EmitState(SqliteConnection c, long runId)
    {
        var r = Get(c, runId);
        if (r is null) return;
        var jobState = c.Scalar("SELECT state FROM jobs WHERE id=@j", null, ("@j", r.JobId)) as string;
        var pas = PendingApprovals(c, r.JobId);
        var pa = pas.FirstOrDefault();
        Live.Emit(c, StateKind, r.ProjectId, new
        {
            jobId = r.JobId, runId = r.Id, status = r.Status, jobState, lastLine = r.LastLine, failReason = r.FailReason, exitCode = r.ExitCode,
            approval = pa is null ? null : new { eventId = pa.EventId, tool = pa.Tool, summary = pa.Summary },
            pendingApprovals = pas.Select(x => new { eventId = x.EventId, summary = x.Summary, detail = x.Detail }).ToList(),
        });
    }

    public static long AddLine(SqliteConnection c, long runId, long jobId, long? projectId, string kind, string text, string? detail, bool live = true)
    {
        var at = Time.NowIso();
        c.Exec("INSERT INTO run_lines(run_id, job_id, at, kind, text, detail) VALUES(@r,@j,@a,@k,@t,@d)", null,
            ("@r", runId), ("@j", jobId), ("@a", at), ("@k", kind), ("@t", text), ("@d", detail));
        var id = c.ScalarLong("SELECT last_insert_rowid()");
        if (kind is "progress" or "agent") c.Exec("UPDATE runs SET last_line=@l WHERE id=@r", null, ("@l", text), ("@r", runId));
        if (live) Live.Emit(c, ProgressKind, projectId, new { jobId, runId, lineId = id, at, kind, text });
        return id;
    }

    // ---------------- onaylar ----------------
    /// <summary>Aynı işlemi tanımak için: araç adı + giriş (komut boşlukları sadeleşir). Sır içeren komut da aynı özetle tanınır; özet maskelidir.</summary>
    public static string ApprovalHash(string tool, JsonNode? input)
    {
        string basis;
        if (input is JsonObject o && o["command"] is JsonValue cv && cv.TryGetValue<string>(out var cmd))
            basis = System.Text.RegularExpressions.Regex.Replace(cmd.Trim(), @"\s+", " ");
        else basis = input?.ToJsonString() ?? "";
        return TextNormalizer.Hash(tool + "\n" + basis);
    }

    private static ApprovalDto MapA(SqliteDataReader r) => new(r.LongN(3) ?? r.GetInt64(0), r.Long(2), r.Long(1), r.Str(4) ?? "", r.Str(5) ?? "", r.Str(7) ?? "pending", r.Str(8), r.Str(9), r.Str(10), r.Str(11));
    private const string ACols = "id, run_id, job_id, guard_event_id, tool, summary, input_hash, status, created_at, decided_at, detail, uid";

    public static long AddApproval(SqliteConnection c, long runId, long jobId, long guardEventId, string tool, string summary, string hash, string? detail = null)
    {
        c.Exec("INSERT INTO run_approvals(run_id, job_id, guard_event_id, tool, summary, input_hash, status, created_at, detail, uid) VALUES(@r,@j,@g,@t,@s,@h,'pending',@n,@d,@u)", null,
            ("@u", Uid.New()), ("@r", runId), ("@j", jobId), ("@g", guardEventId), ("@t", tool), ("@s", summary), ("@h", hash), ("@n", Time.NowIso()), ("@d", detail));
        return c.ScalarLong("SELECT last_insert_rowid()");
    }

    public static ApprovalDto? FindApproval(SqliteConnection c, long jobId, string hash, params string[] statuses)
    {
        var list = string.Join(",", statuses.Select(s => "'" + s.Replace("'", "") + "'"));
        return c.Query($"SELECT {ACols} FROM run_approvals WHERE job_id=@j AND input_hash=@h AND status IN ({list}) ORDER BY id DESC LIMIT 1", MapA, null, ("@j", jobId), ("@h", hash)).FirstOrDefault();
    }

    public static ApprovalDto? ApprovalByEvent(SqliteConnection c, long jobId, long eventId)
        => c.Query($"SELECT {ACols} FROM run_approvals WHERE job_id=@j AND guard_event_id=@e ORDER BY id DESC LIMIT 1", MapA, null, ("@j", jobId), ("@e", eventId)).FirstOrDefault();

    /// <summary>Bekleyen tüm onaylar, en eski önce (kullanıcı sırayla cevaplar; hepsi cevaplanınca tek süreç başlar).</summary>
    public static List<ApprovalDto> PendingApprovals(SqliteConnection c, long jobId)
        => c.Query($"SELECT {ACols} FROM run_approvals WHERE job_id=@j AND status='pending' ORDER BY id ASC", MapA, null, ("@j", jobId));

    public static ApprovalDto? PendingApproval(SqliteConnection c, long jobId)
        => c.Query($"SELECT {ACols} FROM run_approvals WHERE job_id=@j AND status='pending' ORDER BY id DESC LIMIT 1", MapA, null, ("@j", jobId)).FirstOrDefault();

    public static bool SetApprovalStatus(SqliteConnection c, long jobId, long eventId, string from, string to)
        => c.Exec("UPDATE run_approvals SET status=@t, decided_at=@n WHERE job_id=@j AND guard_event_id=@e AND status=@f", null,
            ("@t", to), ("@n", Time.NowIso()), ("@j", jobId), ("@e", eventId), ("@f", from)) > 0;

    /// <summary>Tek seferlik izni kullanır: allowed → used. Başka süreçle yarış olursa yalnız biri kazanır.</summary>
    public static bool ConsumeAllowance(SqliteConnection c, long approvalRowId)
        => c.Exec("UPDATE run_approvals SET status='used', decided_at=COALESCE(decided_at,@n) WHERE id=@i AND status='allowed'", null, ("@n", Time.NowIso()), ("@i", approvalRowId)) > 0;

    public static long ApprovalRowId(SqliteConnection c, long jobId, string hash, string status)
        => c.ScalarLong("SELECT COALESCE((SELECT id FROM run_approvals WHERE job_id=@j AND input_hash=@h AND status=@s ORDER BY id DESC LIMIT 1),0)", null, ("@j", jobId), ("@h", hash), ("@s", status));

    // ---------------- zaman çizelgesi ----------------
    public static List<TimelineItem> Timeline(SqliteConnection c, long jobId, bool includeDetail)
    {
        var items = new List<(string at, int ord, TimelineItem it)>();
        var job = c.Query("SELECT raw_text, created_at, project_id FROM jobs WHERE id=@j", r => (raw: r.Str(0) ?? "", at: r.Str(1) ?? "", p: r.Long(2)), null, ("@j", jobId)).FirstOrDefault();
        if (job == default) return new();
        items.Add((job.at, 0, new TimelineItem(job.at, "user", SecretFilter.MaskSecrets(job.raw), null, null, null, null, null, null, "u")));

        foreach (var l in c.Query("SELECT id, run_id, at, kind, text, detail FROM run_lines WHERE job_id=@j ORDER BY id", r => (id: r.GetInt64(0), run: r.Long(1), at: r.Str(2) ?? "", kind: r.Str(3) ?? "progress", text: r.Str(4) ?? "", detail: r.Str(5)), null, ("@j", jobId)))
        {
            // Onay isteği run_lines'a da yazılır (canlı akış için), ama zaman çizelgesinde onayın tek kaynağı run_approvals'tır: aynı onay iki satır olmasın.
            if (l.kind == "approval") continue;
            var kind = l.kind switch { "progress" or "agent" or "user" or "state" or "error" => l.kind, _ => "progress" };
            items.Add((l.at, 1, new TimelineItem(l.at, kind, l.text, includeDetail ? l.detail : null, l.run, null, null, null, null, "l" + l.id, l.id)));
        }
        foreach (var p in c.Query("SELECT id, started_at, finished_at, trigger, attempt, verdict, summary, reason_code FROM proof_runs WHERE job_id=@j ORDER BY id",
                     r => (id: r.GetInt64(0), st: r.Str(1) ?? "", fin: r.Str(2), trig: r.Str(3) ?? "", att: r.Long(4), v: r.Str(5), s: r.Str(6), rc: r.Str(7)), null, ("@j", jobId)))
        {
            // Kanıt kartı, BAŞLADIĞI andan sıralanır: kendisinden önce gelen ilerleme satırlarının arkasında kalır (süreç çıktısı satırları kanıt bitiminden sonra yazılabilir).
            var at = p.st;
            // Karar kartı nedeni gösterebilsin: neden kodu/metni, değişen dosyalar ve etiket her zaman gelir (denetim 3, Y4).
            var full = p.v is null ? null : Proof.ProofStore.Get(c, p.id);
            var label = full?.VerdictLabel ?? Proof.ProofStore.VerdictWord(p.v, p.rc);
            var text = p.v is null ? $"deneme sürüyor (tur {p.att})" : $"deneme: {label} (tur {p.att})";
            var results = includeDetail ? full?.Results : null;
            items.Add((at, 2, new TimelineItem(at, "proof", text, includeDetail ? p.s : null, null, null, null, p.v, p.id, "p" + p.id, null, (int)p.att, results,
                full?.ReasonCode, full?.ReasonText, full?.ChangedFiles, full?.AddedTests, full?.CountUnknown ?? false, full?.VerdictLabel, full?.BaselineText, full?.SnapshotId, full?.Diffs, full?.Notes)));
        }
        foreach (var a in c.Query($"SELECT {ACols} FROM run_approvals WHERE job_id=@j ORDER BY id", MapA, null, ("@j", jobId)))
        {
            var text = a.Status switch
            {
                "pending" => $"senden onay bekliyor: {a.Summary}",
                "allowed" or "used" => $"onayladın: {a.Summary}",
                "denied" => $"reddettin: {a.Summary}",
                _ => a.Summary,
            };
            items.Add((a.CreatedAt ?? "", 1, new TimelineItem(a.CreatedAt ?? "", "approval", text, includeDetail ? a.Tool : null, a.RunId, a.EventId, a.Status, null, null, "a" + a.EventId)));
        }
        // Sıra: zaman damgası, eşitse kullanıcı sözü < satırlar/onaylar < kanıt; eşitlikte kayıt sırası (kararlı sıralama).
        return items.Select((x, i) => (x, i)).OrderBy(t => t.x.at, StringComparer.Ordinal).ThenBy(t => t.x.ord).ThenBy(t => t.i).Select(t => t.x.it).ToList();
    }

    public static JobRunInfo? InfoFor(SqliteConnection c, long jobId)
    {
        var r = Latest(c, jobId);
        if (r is null) return null;
        return new JobRunInfo(r.Id, r.Status, r.LastLine, r.FailReason, r.Started, r.Ended, PendingApprovals(c, jobId).FirstOrDefault());
    }
}
