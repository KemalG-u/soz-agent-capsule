using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Soz.Core.Sessions;
using Soz.Core.Text;

namespace Soz.Core.Morning;

/// <summary>Sabah ekranında iş satırı. State/Night/FinishedAt/Question (k1) satırı arayüzde ayrı çizmek için: bekleyen işin ne beklediği, gece işi mi, ne zaman bitti.</summary>
public sealed record JobRef(long Id, string Title, string? ProjectName, long ProjectId, string? State = null, bool Night = false, string? FinishedAt = null, string? Question = null);
public sealed record MorningPassed(JobRef Job, long? RunId, int Attempts, string? Summary, string? ReasonCode = null);
public sealed record MorningFailed(JobRef Job, long? RunId, int Attempts, List<string> FailedChecks, string? ReasonCode = null);
public sealed record MorningUnverified(JobRef Job, long? RunId, string? Verdict, string? Summary, string? ReasonCode = null);
public sealed record NeedsYouDto(long SessionId, string Tool, long? ProjectId, string? ProjectName, string? Title, string State, string? Since, string? Reason, string? Question, int PendingQueue,
    string? StateSource = null, string? StateConfidence = null, string? EndClass = null);
public sealed record UsageSum(long Messages, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);
/// <summary>Bekçinin durdurduğu ya da sana sorduğu tek işlem (Action: deny = durdurdu, ask = sana sordu). Özet sır maskelidir.</summary>
public sealed record MorningGuardStop(string At, string? ProjectName, string Tool, string Summary, string Action, string? Reason);
/// <summary>Pencerede Söz'ün başlattığı ajan koşularının harcaması. CostUsd yalnız aracın bildirdiği dolar toplamı (Codex dolar vermez: null); token sayıları bildirilenlerdir.</summary>
public sealed record MorningSpend(int Runs, long InputTokens, long CachedTokens, long OutputTokens, long ReasoningTokens, double? CostUsd, string Note);
/// <summary>Uzun iş (eski adıyla gece işi) olarak işaretlenen işlerin pencere özeti. Note: bayrağın gerçekte ne yaptığı (saat yok, bilgisayar açık kalmalı).</summary>
public sealed record MorningNight(int Passed, int Failed, int Waiting, int Open, List<JobRef> Jobs, string Note);
public sealed record MorningDto(string Since, string Until, string Sentence, List<MorningPassed> Passed, List<MorningFailed> Failed, List<MorningUnverified> Unverified,
    List<NeedsYouDto> WaitingForYou, List<NeedsYouDto> Stuck, long GuardBlocked, UsageSum Usage, List<JobRef>? WaitingJobs = null, List<MorningPassed>? AcceptedByYou = null,
    List<NeedsYouDto>? Uncertain = null, double Hours = 0, List<MorningGuardStop>? GuardStops = null, MorningSpend? Spend = null, MorningNight? Night = null, int JobsInWindow = 0);
/// <summary>
/// JobsRunning: yalnız canlı süreci olan koşu + son 10 dk içinde etkinliği olan terminal oturumuna bağlı çalışan iş (denetim 2, Ö-5).
/// Waiting: seni bekleyen iş (cevap, karar, onay, görülmemiş kaldı) + Notification ile bekleyen terminal oturumu; bitmiş ya da Söz'ün yürüttüğü oturum sayılmaz.
/// Queued: sırada bekleyen iş sayısı (K-6). Uncertain: durumundan Söz'ün emin olmadığı oturum sayısı (seni bekleyen sayılmaz, uyandırmaz).
/// </summary>
public sealed record NowCounts(int JobsPassed, int JobsFailed, int JobsRunning, int Waiting, int Working, int Stalled, int QueuePending, long GuardBlocked, int Queued = 0, int JobsAccepted = 0, int Uncertain = 0);
public sealed record NowDto(string Sentence, List<NeedsYouDto> NeedsYou, List<JobDto> RunningJobs, List<ProofRunDto> RecentVerdicts, NowCounts Counts, List<JobDto>? WaitingJobs = null,
    List<NeedsYouDto>? Uncertain = null);

/// <summary>Sabah raporu (M6) ve Bugün sayfası (tek çağrı). Modelsiz; yalnız veritabanı.</summary>
public static class MorningBuilder
{
    public const string NoGateSummary = "Söz bu işi hiç denemedi (yardımcının kendi penceresi için Söz desteği kurulu değil ya da çalışmıyor). İş denenmedi. Bu bir \"geçti\" değildir.";

    private static JobRef Ref(SqliteDataReader r) => new(r.GetInt64(0), r.Str(1) ?? "", r.Str(2), r.Long(3));

    private const string JobSel = "SELECT j.id, j.title, p.name, j.project_id, (SELECT COUNT(*) FROM proof_runs r WHERE r.job_id=j.id AND r.trigger IN ('stop_hook','outer_gate')), " +
                                  "(SELECT r.id FROM proof_runs r WHERE r.job_id=j.id ORDER BY r.id DESC LIMIT 1), (SELECT reason_code FROM proof_runs WHERE job_id=j.id AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1) FROM jobs j LEFT JOIN projects p ON p.id=j.project_id ";

    public static MorningDto Morning(SqliteConnection c, DateTime sinceUtc, DateTime nowUtc, StateThresholds th)
    {
        var since = Time.Iso(sinceUtc);
        var passed = c.Query(JobSel + "WHERE j.state='gecti' AND COALESCE(j.accepted_by_user,0)=0 AND j.finished_at>=@s ORDER BY j.finished_at DESC",
            r => (job: Ref(r), attempts: (int)r.Long(4), run: r.LongN(5), reason: r.Str(6)), null, ("@s", since))
            .Select(x => new MorningPassed(x.job, x.run, x.attempts, x.run is null ? null : c.Scalar("SELECT summary FROM proof_runs WHERE id=@id", null, ("@id", x.run)) as string, x.reason)).ToList();
        // Denetim 3, Y7: kullanıcının kendi kabul ettiği iş "kanıtıyla bitti" sayılmaz; ayrı sayılır ve ayrı yazılır.
        var accepted = c.Query(JobSel + "WHERE j.state='gecti' AND COALESCE(j.accepted_by_user,0)=1 AND j.finished_at>=@s ORDER BY j.finished_at DESC",
            r => (job: Ref(r), attempts: (int)r.Long(4), run: r.LongN(5), reason: r.Str(6)), null, ("@s", since))
            .Select(x => new MorningPassed(x.job, x.run, x.attempts, x.run is null ? null : c.Scalar("SELECT summary FROM proof_runs WHERE id=@id", null, ("@id", x.run)) as string, x.reason)).ToList();
        var failed = c.Query(JobSel + "WHERE j.state='kaldi' AND j.finished_at>=@s ORDER BY j.finished_at DESC",
            r => (job: Ref(r), attempts: (int)r.Long(4), run: r.LongN(5), reason: r.Str(6)), null, ("@s", since))
            .Select(x => new MorningFailed(x.job, x.run, x.attempts, x.run is null ? new() :
                c.Query("SELECT label FROM proof_results WHERE run_id=@r AND status<>'passed' ORDER BY id", r => r.Str(0) ?? "", null, ("@r", x.run)), x.reason)).ToList();
        var unverified = c.Query("""
            SELECT j.id, j.title, p.name, j.project_id, r.id, r.verdict, r.summary, r.reason_code FROM jobs j
            LEFT JOIN projects p ON p.id=j.project_id
            JOIN proof_runs r ON r.id=(SELECT MAX(id) FROM proof_runs x WHERE x.job_id=j.id)
            WHERE j.state IN ('verildi','calisiyor','karar_bekliyor') AND r.verdict IN ('unchecked','error') AND r.started_at>=@s ORDER BY r.id DESC
            """, r => new MorningUnverified(Ref(r), r.LongN(4), r.Str(5), r.Str(6), r.Str(7)), null, ("@s", since));
        // EK B: kapı hiç çalışmadıysa (kanca yok ya da ölü) oturumu biten iş sessizce "sürüyor" kalmaz, "kanıtlanmadı" görünür.
        unverified.AddRange(c.Query("""
            SELECT j.id, j.title, p.name, j.project_id FROM jobs j
            LEFT JOIN projects p ON p.id=j.project_id
            JOIN sessions s ON s.id=j.session_id
            WHERE j.state IN ('verildi','calisiyor') AND NOT EXISTS(SELECT 1 FROM proof_runs x WHERE x.job_id=j.id)
              AND s.turn_ended=1 AND s.last_event_ts>=COALESCE(j.given_at, j.created_at) AND s.last_event_ts>=@s
            ORDER BY j.id DESC
            """, r => new MorningUnverified(Ref(r), null, Verdicts.Unchecked, NoGateSummary, "no_gate"), null, ("@s", since)));

        var (needs, stuck, _, _, unsure) = Attention(c, nowUtc, th, Math.Max(1, (nowUtc - sinceUtc).TotalHours));
        var waitJobs = WaitingJobIds(c, since).Select(id => JobStore.Get(c, id)).Where(j => j is not null)
            .Select(j => new JobRef(j!.Id, j.Title, j.ProjectName, j.ProjectId)).ToList();
        var guard = GuardCount(c, since);
        var usage = Usage(c, sinceUtc);
        var parts = new List<string>();
        var quietN = passed.Count(x => x.ReasonCode is "no_change" or "ignored_only");
        if (passed.Count - quietN > 0) parts.Add($"{passed.Count - quietN} iş deneme sonucuyla bitti");
        if (quietN > 0) parts.Add($"{quietN} işte değişiklik yok");
        if (accepted.Count > 0) parts.Add($"sen onayladın: {accepted.Count}");
        if (failed.Count > 0) parts.Add($"{failed.Count} iş geçemedi");
        if (unverified.Count > 0) parts.Add($"{unverified.Count} işin deneme sonucu belirsiz");
        if (waitJobs.Count > 0) parts.Add($"{waitJobs.Count} iş seni bekliyor");
        if (needs.Count > 0) parts.Add($"{needs.Count} yardımcı seni bekliyor");
        if (stuck.Count > 0) parts.Add($"{stuck.Count} oturum takılmış olabilir");
        if (unsure.Count > 0) parts.Add($"{unsure.Count} oturumdan emin değilim");
        if (guard > 0) parts.Add($"bekçi {guard} kez durdurdu");
        var sentence = parts.Count == 0 ? "Bu aralıkta biten iş ya da seni bekleyen yardımcı yok." : Cap(string.Join(", ", parts)) + ".";
        // k1: Sabah ekranı için ek alanlar. Eski alanlar aynen kalır; iş satırları durum/gece/bitiş bilgisiyle zenginleşir.
        var info = JobInfo(c);
        JobRef E(JobRef r) => info.TryGetValue(r.Id, out var i) ? r with { State = i.state, Night = i.night, FinishedAt = i.finished, Question = i.question } : r;
        passed = passed.Select(x => x with { Job = E(x.Job) }).ToList();
        accepted = accepted.Select(x => x with { Job = E(x.Job) }).ToList();
        failed = failed.Select(x => x with { Job = E(x.Job) }).ToList();
        unverified = unverified.Select(x => x with { Job = E(x.Job) }).ToList();
        waitJobs = waitJobs.Select(E).ToList();
        var night = NightSummary(c, since, info);
        var inWindow = (int)c.ScalarLong("SELECT COUNT(*) FROM jobs WHERE finished_at>=@s OR state IN ('verildi','calisiyor','cevap_bekliyor','onay_bekliyor','karar_bekliyor','sirada')", null, ("@s", since));
        return new MorningDto(since, Time.Iso(nowUtc), sentence, passed, failed, unverified, needs, stuck, guard, usage, waitJobs, accepted, unsure,
            Math.Round((nowUtc - sinceUtc).TotalHours, 1), GuardStops(c, since), RunSpend(c, since), night, inWindow);
    }

    private static Dictionary<long, (string state, bool night, string? finished, string? question)> JobInfo(SqliteConnection c)
        => c.Query("SELECT id, state, night, finished_at, question FROM jobs", r => (id: r.GetInt64(0), state: r.Str(1) ?? "", night: r.Long(2) == 1, finished: r.Str(3), question: r.Str(4)))
            .ToDictionary(x => x.id, x => (x.state, x.night, x.finished, x.question));

    public const string NightNote = "Uzun iş belli bir saatte başlamaz ya da durmaz. Tek farkı: o klasörün bitti denetimi iş bitene kadar açık kalır. Bilgisayar açık kalmalı. Seni kimse uyandırmaz.";

    private static MorningNight NightSummary(SqliteConnection c, string since, Dictionary<long, (string state, bool night, string? finished, string? question)> info)
    {
        var rows = c.Query("SELECT j.id, j.title, p.name, j.project_id, j.state, j.finished_at FROM jobs j LEFT JOIN projects p ON p.id=j.project_id WHERE j.night=1 AND (j.finished_at>=@s OR j.state IN ('verildi','calisiyor','cevap_bekliyor','onay_bekliyor','karar_bekliyor','sirada')) ORDER BY j.id",
            r => new JobRef(r.GetInt64(0), r.Str(1) ?? "", r.Str(2), r.Long(3), r.Str(4), true, r.Str(5), info.TryGetValue(r.GetInt64(0), out var i) ? i.question : null), null, ("@s", since));
        return new MorningNight(rows.Count(x => x.State == JobStates.Passed), rows.Count(x => x.State == JobStates.Failed),
            rows.Count(x => x.State is JobStates.AwaitingAnswer or JobStates.AwaitingApproval or JobStates.AwaitingDecision),
            rows.Count(x => x.State is JobStates.Given or JobStates.Running or JobStates.Queued), rows, NightNote);
    }

    private static List<MorningGuardStop> GuardStops(SqliteConnection c, string since)
    {
        try
        {
            return c.Query("SELECT e.at, p.name, e.tool, e.target_summary, e.action, e.reason FROM guard_events e LEFT JOIN projects p ON p.id=e.project_id WHERE e.at>=@s AND e.action IN ('deny','ask') ORDER BY e.id DESC LIMIT 10",
                r => new MorningGuardStop(r.Str(0) ?? "", r.Str(1), r.Str(2) ?? "", SecretFilter.MaskSecrets(r.Str(3) ?? ""), r.Str(4) ?? "", r.Str(5) is { } rs ? SecretFilter.MaskSecrets(rs) : null), null, ("@s", since));
        }
        catch (SqliteException) { return new(); }
    }

    private static MorningSpend RunSpend(SqliteConnection c, string since)
    {
        var row = c.Query("SELECT COUNT(*), COALESCE(SUM(input_tokens),0), COALESCE(SUM(cached_tokens),0), COALESCE(SUM(output_tokens),0), COALESCE(SUM(reasoning_tokens),0), SUM(cost_usd) FROM runs WHERE started>=@s",
            r => (n: r.GetInt64(0), i: r.GetInt64(1), ca: r.GetInt64(2), o: r.GetInt64(3), re: r.GetInt64(4), d: r.IsDBNull(5) ? (double?)null : r.GetDouble(5)), null, ("@s", since)).First();
        var note = row.n == 0 ? "Bu aralıkta Söz yardımcı başlatmadı."
            : row.d is null ? "Araç dolar tutarı bildirmedi. Yalnız kullanım miktarı var." : "Dolar tutarı aracın kendi bildirdiği toplamdır (tahmin değil).";
        return new MorningSpend((int)row.n, row.i, row.ca, row.o, row.re, row.d, note);
    }

    private static readonly string[] AskingStates = { JobStates.AwaitingAnswer, JobStates.AwaitingDecision, JobStates.AwaitingApproval };

    /// <summary>Seni bekleyen işler: cevap, karar ya da onay bekleyen + görülmemiş "kaldı" (sinceIso'dan beri bitenler).</summary>
    private static List<long> WaitingJobIds(SqliteConnection c, string sinceIso)
        => c.Query("SELECT id FROM jobs WHERE state IN ('cevap_bekliyor','karar_bekliyor','onay_bekliyor') OR (state='kaldi' AND seen_at IS NULL AND finished_at>=@s) ORDER BY id",
            r => r.GetInt64(0), null, ("@s", sinceIso));

    /// <summary>Canlı koşusu olan iş + son 10 dk içinde etkinliği olan terminal oturumuna bağlı (Söz koşusu olmayan) çalışan iş.</summary>
    private static List<long> RunningJobIds(SqliteConnection c, DateTime nowUtc)
    {
        var fresh = Time.Iso(nowUtc.AddMinutes(-10));
        var ids = c.Query("SELECT DISTINCT job_id FROM runs WHERE status='running'", r => r.GetInt64(0));
        ids.AddRange(c.Query("""
            SELECT j.id FROM jobs j JOIN sessions s ON s.id=j.session_id
            WHERE j.state='calisiyor' AND NOT EXISTS(SELECT 1 FROM runs r WHERE r.job_id=j.id) AND s.last_event_ts>=@t
            """, r => r.GetInt64(0), null, ("@t", fresh)));
        return ids.Distinct().OrderBy(x => x).ToList();
    }

    /// <summary>Bitmiş işe ya da Söz koşusuna bağlı oturumlar "seni bekleyen terminal oturumu" sayılmaz (işin son hâli esas).</summary>
    internal static HashSet<long> NotWaitingSessions(SqliteConnection c)
        => c.Query("""
            SELECT j.session_id FROM jobs j
            WHERE j.session_id IS NOT NULL AND j.id=(SELECT MAX(x.id) FROM jobs x WHERE x.session_id=j.session_id)
              AND (j.state IN ('gecti','kaldi','durduruldu','baslatilamadi','iptal','sirada','cevap_bekliyor','karar_bekliyor','onay_bekliyor')
                   OR EXISTS(SELECT 1 FROM runs r WHERE r.job_id=j.id))
            """, r => r.GetInt64(0)).ToHashSet();

    public static NowDto Now(SqliteConnection c, DateTime nowUtc, StateThresholds th)
    {
        var day = Time.Iso(nowUtc.AddHours(-24));
        var (needs, stuck, working, _, unsure) = Attention(c, nowUtc, th, 24);
        var running = RunningJobIds(c, nowUtc).Select(id => JobStore.Get(c, id)).Where(j => j is not null).Select(j => j!).ToList();
        var waitingJobs = WaitingJobIds(c, day).Select(id => JobStore.Get(c, id)).Where(j => j is not null).Select(j => j!).ToList();
        var queued = (int)c.ScalarLong("SELECT COUNT(*) FROM jobs WHERE state='sirada'");
        var waiting = waitingJobs.Count + needs.Count;
        var recent = c.Query("SELECT id FROM proof_runs WHERE started_at>=@d ORDER BY id DESC LIMIT 10", r => r.GetInt64(0), null, ("@d", day))
            .Select(id => ProofStore.Get(c, id)! with { Results = null }).ToList();
        var passed = (int)c.ScalarLong("SELECT COUNT(*) FROM jobs WHERE state='gecti' AND COALESCE(accepted_by_user,0)=0 AND finished_at>=@d", null, ("@d", day));
        var acceptedN = (int)c.ScalarLong("SELECT COUNT(*) FROM jobs WHERE state='gecti' AND COALESCE(accepted_by_user,0)=1 AND finished_at>=@d", null, ("@d", day));
        var failed = (int)c.ScalarLong("SELECT COUNT(*) FROM jobs WHERE state='kaldi' AND finished_at>=@d", null, ("@d", day));
        var queue = (int)c.ScalarLong("SELECT COUNT(*) FROM queue WHERE delivered_at IS NULL");
        var guard = GuardCount(c, day);
        var all = needs.Concat(stuck).ToList();
        var parts = new List<string>();
        var quietNow = (int)c.ScalarLong("SELECT COUNT(*) FROM jobs j WHERE j.state='gecti' AND COALESCE(j.accepted_by_user,0)=0 AND j.finished_at>=@d AND " + "(SELECT reason_code FROM proof_runs WHERE job_id=j.id AND verdict='passed' ORDER BY id DESC LIMIT 1) IN ('no_change','ignored_only')", null, ("@d", day));
        if (passed - quietNow > 0) parts.Add($"{passed - quietNow} iş deneme sonucuyla bitti");
        if (quietNow > 0) parts.Add($"{quietNow} işte değişiklik yok");
        if (acceptedN > 0) parts.Add($"sen onayladın: {acceptedN}");
        if (failed > 0) parts.Add($"{failed} kaldı");
        if (running.Count > 0) parts.Add($"{running.Count} iş sürüyor");
        if (queued > 0) parts.Add($"{queued} iş sırada");
        if (waitingJobs.Count > 0) parts.Add($"{waitingJobs.Count} iş seni bekliyor");
        if (needs.Count > 0) parts.Add($"{needs.Count} yardımcı seni bekliyor");
        if (stuck.Count > 0) parts.Add($"{stuck.Count} oturum takılmış olabilir");
        if (unsure.Count > 0) parts.Add($"{unsure.Count} oturumdan emin değilim");
        var sentence = parts.Count == 0 ? "Şu an seni bekleyen bir şey yok." : Cap(string.Join(", ", parts)) + ".";
        return new NowDto(sentence, all, running, recent, new NowCounts(passed, failed, running.Count, waiting, working, stuck.Count, queue, guard, queued, acceptedN, unsure.Count), waitingJobs, unsure);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], TextNormalizer.Tr) + s[1..];

    /// <summary>(seni bekleyenler, takılanlar, çalışan sayısı, bekleyen sayısı, emin olunmayanlar)</summary>
    private static (List<NeedsYouDto> needs, List<NeedsYouDto> stuck, int working, int waiting, List<NeedsYouDto> unsure) Attention(SqliteConnection c, DateTime nowUtc, StateThresholds th, double hours)
    {
        var cards = SessionQueries.Cards(c, hours, false, th, nowUtc);
        var flagged = c.Query("SELECT id, needs_user_since, needs_user_kind FROM sessions WHERE needs_user_since IS NOT NULL", r => (id: r.GetInt64(0), since: r.Str(1), kind: r.Str(2)))
            .ToDictionary(x => x.id);
        foreach (var f in flagged.Values.Where(f => cards.All(x => x.Id != f.id)))
            cards.AddRange(SessionQueries.Cards(c, hours, true, th, nowUtc, true, f.id));
        var pending = c.Query("SELECT session_id, COUNT(*) FROM queue WHERE delivered_at IS NULL AND session_id IS NOT NULL GROUP BY session_id", r => (s: r.Long(0), n: (int)r.Long(1)))
            .ToDictionary(x => x.s, x => x.n);
        NeedsYouDto Dto(SessionCard x, string? reason, string? since) => new(x.Id, x.Tool, x.ProjectId, x.ProjectName,
            SecretFilter.MaskSecrets(TextNormalizer.Truncate(string.IsNullOrWhiteSpace(x.Title) ? DigestBuilder.Display(x.LastUserText ?? "") : x.Title, 80)),
            x.State, since ?? x.Since, reason,
            x.AsksQuestion && x.LastAssistantText is { } q ? SecretFilter.MaskSecrets(TextNormalizer.Truncate(q, 200)) : null,
            pending.GetValueOrDefault(x.Id), x.StateSource, x.StateConfidence, x.EndClass);
        var needs = new List<NeedsYouDto>();
        var stuck = new List<NeedsYouDto>();
        var unsure = new List<NeedsYouDto>();
        var skip = NotWaitingSessions(c);
        cards = cards.Where(x => !skip.Contains(x.Id)).ToList();
        foreach (var x in cards)
        {
            if (flagged.TryGetValue(x.Id, out var f))
            {
                // n6: kancadan gelen "hata verip durdu" işareti bekleyen değil durmuş oturumdur (cümle "ajan seni bekliyor" demesin).
                var dto = Dto(x, f.kind ?? "notification", f.since);
                if (f.kind is { } fk && fk.StartsWith(Soz.Core.Attention.FailureProbe.HookKindPrefix, StringComparison.Ordinal)) stuck.Add(dto); else needs.Add(dto);
            }
            else if (x.State == SessionStates.Waiting) needs.Add(Dto(x, x.AsksQuestion ? "question" : "waiting", null));
            else if (x.State == SessionStates.Stalled) stuck.Add(Dto(x, "stalled", null));
            else if (x.State == SessionStates.Uncertain) unsure.Add(Dto(x, "uncertain", null)); // tahminle "bekliyor" ya da "bitti" denmez; ayrı durur
        }
        return (needs, stuck, cards.Count(x => x.State == SessionStates.Working), cards.Count(x => x.State == SessionStates.Waiting), unsure);
    }

    private static long GuardCount(SqliteConnection c, string sinceIso)
    {
        try { return c.ScalarLong("SELECT COUNT(*) FROM guard_events WHERE at>=@s", null, ("@s", sinceIso)); }
        catch (SqliteException) { return 0; }
    }

    private static UsageSum Usage(SqliteConnection c, DateTime sinceUtc)
    {
        try
        {
            var day = sinceUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            return c.Query("""
                SELECT COALESCE(SUM(messages),0), COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0), COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_write_tokens),0)
                FROM usage_daily WHERE day>=@d
                """, r => new UsageSum(r.Long(0), r.Long(1), r.Long(2), r.Long(3), r.Long(4)), null, ("@d", day)).First();
        }
        catch (SqliteException) { return new UsageSum(0, 0, 0, 0, 0); }
    }
}
