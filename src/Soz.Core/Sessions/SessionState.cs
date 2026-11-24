using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Readers;
using Soz.Core.Text;

namespace Soz.Core.Sessions;

public static class SessionStates
{
    public const string Working = "working", Waiting = "waiting", Stalled = "stalled", Idle = "idle", Stale = "stale";
    /// <summary>"Belirsiz": kayıt "araç çağrısı sonuçsuz" diyor ama araç mı çalışıyor, izin mi bekliyor, ajan mı çöktü ayırt edilemiyor. Tahminle "bekliyor" ya da "çalışıyor" denmez.</summary>
    public const string Uncertain = "uncertain";
    public static readonly string[] DigestOrder = { Waiting, Stalled, Uncertain, Working, Idle };
}

/// <summary>Durum kanıtının kaynağı (Resolve girdisi). Kayıt dosyası/veritabanı en zayıf, protokol olayı en güçlüdür.</summary>
public static class StateSources
{
    public const string Log = "log", Hook = "hook", Protocol = "protocol", Process = "process";
}

/// <summary>Kayıt sonu sınıfı: kaydın SON hâli ne diyor (agentsview termination.go fikri). Bu bir kanıt türüdür, "bitti" hükmü değildir.</summary>
public static class EndClasses
{
    public const string AwaitingUser = "awaiting_user", ToolCallPending = "tool_call_pending", Truncated = "truncated";
}

public sealed class SessionCard
{
    public long Id { get; set; }
    public string Tool { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public long? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public string? Title { get; set; }
    public string Kind { get; set; } = SessionKinds.Human;
    public string State { get; set; } = SessionStates.Idle;
    public string? Since { get; set; }
    public string? LastUserText { get; set; }
    public string? LastUserTs { get; set; }
    public string? LastAssistantText { get; set; }
    public string? LastAssistantTs { get; set; }
    public bool AsksQuestion { get; set; }
    public bool ClaimsDone { get; set; }
    public bool ShowsEvidence { get; set; }
    public int SubCount { get; set; }
    /// <summary>Durumun kanıt kaynağı (log | hook | protocol | process), güveni (firm | probable | unknown) ve kayıt sonu sınıfı (awaiting_user | tool_call_pending | truncated | null).</summary>
    public string StateSource { get; set; } = StateSources.Log;
    public string StateConfidence { get; set; } = SessionStateResolver.Probable;
    public string? EndClass { get; set; }
}

public sealed record StateThresholds(double WorkingSeconds = 90, double StalledMinutes = 5, double IdleMinutes = 30, double StaleHours = 24,
    double HookFreshSeconds = 120, double ProtocolWorkingFreshSeconds = 20, double ProtocolWaitingFreshSeconds = 5, double ProcessFreshSeconds = 60)
{
    // Tazelik pencereleri (agent-deck sessionstatus.go: kanca 2 dk, protokol çalışıyor 20 sn, bekliyor 5 sn) tasarım seçimidir, bu makinede ölçülmedi.
    /// <summary>Bir kaynağın bildirdiği durumun ne kadar süre geçerli sayılacağı. Süresi geçen kaynak yerini kayda bırakır.</summary>
    public TimeSpan Fresh(string? source, string? hint) => source switch
    {
        StateSources.Hook => TimeSpan.FromSeconds(HookFreshSeconds),
        StateSources.Protocol => TimeSpan.FromSeconds(hint == SessionStates.Working ? ProtocolWorkingFreshSeconds : ProtocolWaitingFreshSeconds),
        StateSources.Process => TimeSpan.FromSeconds(ProcessFreshSeconds),
        _ => TimeSpan.Zero,
    };

    public static StateThresholds From(SozSettings s) => new(s.WorkingSeconds, s.StalledMinutes, s.IdleMinutes, s.WaitingStaleHours);
}

/// <summary>Durum türetme (sözleşme 10 + 11.1). Sezgisel bir işarettir, hüküm değildir.</summary>
public static class SessionStateResolver
{
    /// <summary>
    /// Source/ObservedUtc/Hint: durum kanıtının kaynağı, ne zaman görüldüğü ve söylediği durum (protokol/kanca/süreç gibi güçlü kaynaklar).
    /// Verilmezse (null) eski davranış aynen sürer: yalnız kayıt (log) ve sezgi. Güçlü kaynak tazeyse kazanır; bayatlarsa kayıt karar verir.
    /// "Belirsiz" yalnız Source verildiğinde üretilir (eski çağrıcılar etkilenmez).
    /// </summary>
    public sealed record Input(string? LastEventKind, bool TurnEnded, string? LastEventTs, DateTime? FileMtimeUtc, string? LastUserTs, string? LastAssistantTs, string? LastAt,
        string? Source = null, DateTime? ObservedUtc = null, string? Hint = null);

    /// <summary>Kararın kendisi ve gerekçesi: durum, ne zamandır, hangi kaynaktan, ne kadar emin, kayıt sonu sınıfı.</summary>
    public sealed record Decision(string State, DateTime? Since, string Source, string Confidence, DateTime? ObservedUtc, string? EndClass);

    public const string Firm = "firm", Probable = "probable", Unknown = "unknown";

    public static (string state, DateTime? since) Resolve(Input s, DateTime nowUtc, StateThresholds th)
    {
        var d = Decide(s, nowUtc, th);
        return (d.State, d.Since);
    }

    /// <summary>Kayıt sonu sınıfı (kayıttan): açık araç çağrısı, açık ve yarım tur, ya da asistanın söyleyip beklediği tur. Sınıflanamıyorsa null.</summary>
    public static string? ClassifyEnd(Input s)
    {
        if (s.LastEventKind == EventKinds.Tool && !s.TurnEnded) return EndClasses.ToolCallPending;
        if (!s.TurnEnded && s.LastEventKind is EventKinds.AssistantText or EventKinds.Other) return EndClasses.Truncated;
        if (s.TurnEnded && s.LastEventKind == EventKinds.AssistantText) return EndClasses.AwaitingUser;
        return null;
    }

    public static Decision Decide(Input s, DateTime nowUtc, StateThresholds th)
    {
        // Güçlü kaynak (protokol, kanca, süreç) tazeyse kayda bakmadan onun dediğini söyler.
        if (s.Source is StateSources.Hook or StateSources.Protocol or StateSources.Process && s.Hint is not null && s.ObservedUtc is { } seen && nowUtc - seen <= th.Fresh(s.Source, s.Hint))
            return new Decision(s.Hint, seen, s.Source, Firm, seen, ClassifyEnd(s));
        var (state, since) = ResolveFromLog(s, nowUtc, th, out var end, out var confidence);
        return new Decision(state, since, StateSources.Log, confidence, since, end);
    }

    private static (string state, DateTime? since) ResolveFromLog(Input s, DateTime nowUtc, StateThresholds th, out string? end, out string confidence)
    {
        end = ClassifyEnd(s);
        confidence = Probable;
        var lastEvent = Time.Parse(s.LastEventTs) ?? Time.Parse(s.LastAt);
        var lastChange = Max(lastEvent, s.FileMtimeUtc) ?? Time.Parse(s.LastAt);
        if (lastChange is null) { confidence = Unknown; return (SessionStates.Stale, null); }
        var age = nowUtc - lastChange.Value;
        if (age.TotalHours > th.StaleHours) return (SessionStates.Stale, lastChange);

        // "Belirsiz" (tahminle bekliyor/çalışıyor denmez): kayıt açık bir araç çağrısında bitiyor, süre 90 sn geçti, takıldı eşiği (5 dk) dolmadı.
        if (s.Source is not null && end == EndClasses.ToolCallPending && age.TotalSeconds > th.WorkingSeconds && age.TotalMinutes <= th.StalledMinutes)
        { confidence = Unknown; return (SessionStates.Uncertain, lastChange); }

        var userTs = Time.Parse(s.LastUserTs);
        var asstTs = Time.Parse(s.LastAssistantTs);
        var assistantSpokeLast = asstTs.HasValue && (!userTs.HasValue || asstTs >= userTs);

        if (s.LastEventKind == EventKinds.User)
            return age.TotalMinutes > th.StalledMinutes ? (SessionStates.Stalled, lastChange) : (SessionStates.Working, lastChange);

        if (!s.TurnEnded)
        {
            if (s.LastEventKind is null) return age.TotalMinutes > th.IdleMinutes ? (SessionStates.Idle, lastChange) : (SessionStates.Working, lastChange);
            return age.TotalMinutes > th.StalledMinutes ? (SessionStates.Stalled, lastChange) : (SessionStates.Working, lastChange);
        }

        // Tur bitti.
        if (s.LastEventKind == EventKinds.AssistantText || (assistantSpokeLast && s.LastEventKind != EventKinds.Other))
            return (SessionStates.Waiting, asstTs ?? lastChange);
        return (SessionStates.Idle, lastChange);
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a > b ? a : b);

    // ---- Bayraklar ----
    private static readonly Regex AskRe = new(@"(\?)|\b(onayliyor musun|onaylar misin|hangisini|hangisi|ister misin|istersen|karar ver|secer misin|devam edeyim mi|should i|do you want|would you like|shall i|which one|let me know|your call)\b", RegexSpeed.Compiled);
    private static readonly Regex DoneRe = new(@"\b(bitti|bitirdim|tamamlandi|tamamladim|tamam(dir)?\b(?! mi)|hazir(dir)?|teslim|gecti|gectiler|calisiyor|done|completed|complete|finished|shipped|all tests pass(ed)?|ready)\b", RegexSpeed.Compiled);
    private static readonly Regex EvidenceRe = new(@"(```)|(\bKontrol:)|(\b(test|tests|testler|testleri)\b[^\n]{0,60}\b(gecti|passed|basarili)\b[^\n]{0,40}\d|\d+[^\n]{0,30}\b(gecti|passed)\b)|([A-Za-z]:\\[^\s]+)|(\b[\w.-]+/[\w./-]+\.\w{1,6}\b)|(https?://\S+)", RegexSpeed.Compiled);

    public static (bool asks, bool done, bool evidence) Flags(string? assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText)) return (false, false, false);
        var paragraphs = assistantText.Trim().Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var lastPara = TextNormalizer.Fold(paragraphs.Length > 0 ? paragraphs[^1] : assistantText);
        var folded = TextNormalizer.Fold(assistantText);
        var asks = AskRe.IsMatch(lastPara);
        var done = DoneRe.IsMatch(folded);
        var evidence = EvidenceRe.IsMatch(assistantText) || EvidenceRe.IsMatch(folded);
        return (asks, done, evidence);
    }
}

/// <summary>Oturum kartlarını veritabanından üretir.</summary>
public static class SessionQueries
{
    public static List<SessionCard> Cards(SqliteConnection c, double hours, bool includeStale, StateThresholds th, DateTime nowUtc, bool humanOnly = true, long? id = null)
    {
        var since = Time.Iso(nowUtc.AddHours(-hours));
        var sql = """
            SELECT s.id, s.tool, s.external_id, s.project_id, p.name, s.title, s.kind, s.last_event_kind, s.turn_ended, s.last_event_ts,
                   f.mtime, s.last_user_text, s.last_user_ts, s.last_assistant_text, s.last_assistant_ts, s.last_at,
                   (SELECT COUNT(*) FROM sessions x WHERE x.parent_external_id = s.external_id) AS subs
            FROM sessions s
            LEFT JOIN projects p ON p.id = s.project_id
            LEFT JOIN file_state f ON f.path = s.file_path
            WHERE (@id IS NOT NULL AND s.id=@id) OR (@id IS NULL AND (@all=1 OR s.kind='human') AND COALESCE(s.last_at, s.last_event_ts, '') >= @since)
            ORDER BY s.last_at DESC
            """;
        var list = c.Query(sql, r =>
        {
            // Kaynak: oturum satırı kayıt dosyasından/veritabanından geldi (log). Güçlü kaynaklar (kanca, protokol) bu tabloya yazılmadığı için burada verilmez.
            var input = new SessionStateResolver.Input(r.Str(7), r.Long(8) == 1, r.Str(9), Time.Parse(r.Str(10)), r.Str(12), r.Str(14), r.Str(15), StateSources.Log);
            var dec = SessionStateResolver.Decide(input, nowUtc, th);
            var (state, sinceTs) = (dec.State, dec.Since);
            var (asks, done, ev) = SessionStateResolver.Flags(r.Str(13));
            return new SessionCard
            {
                Id = r.GetInt64(0), Tool = r.GetString(1), ExternalId = r.GetString(2), ProjectId = r.LongN(3), ProjectName = r.Str(4),
                Title = r.Str(5), Kind = r.Str(6) ?? SessionKinds.Human, State = state, Since = sinceTs.HasValue ? Time.Iso(sinceTs.Value) : null,
                LastUserText = r.Str(11), LastUserTs = r.Str(12), LastAssistantText = r.Str(13), LastAssistantTs = r.Str(14),
                AsksQuestion = asks, ClaimsDone = done, ShowsEvidence = ev, SubCount = (int)r.Long(16),
                StateSource = dec.Source, StateConfidence = dec.Confidence, EndClass = dec.EndClass,
            };
        }, null, ("@id", id), ("@all", humanOnly ? 0 : 1), ("@since", since));
        if (!includeStale && id is null) list = list.Where(x => x.State != SessionStates.Stale).ToList();
        return list;
    }
}

/// <summary>Özet metni (sözleşme 10, model kullanmaz).</summary>
public static class DigestBuilder
{
    public static (string text, int waiting, int stalled, int working, int idle) Build(IReadOnlyList<SessionCard> cards, double hours, DateTime nowUtc, string language = "tr")
    {
        var sb = new StringBuilder();
        sb.Append(Strings.Get(language, "digest.title").Replace("{hours}", hours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))).Append('\n');
        var groups = SessionStates.DigestOrder.ToDictionary(s => s, s => cards.Where(c => c.State == s).ToList());
        if (groups.Values.All(g => g.Count == 0)) sb.Append('\n').Append(Strings.Get(language, "digest.empty")).Append('\n');
        foreach (var st in SessionStates.DigestOrder)
        {
            var g = groups[st];
            if (g.Count == 0) continue;
            sb.Append('\n').Append(Strings.Get(language, "digest." + st)).Append($" ({g.Count})\n");
            foreach (var c in g)
            {
                var ago = Ago(c.Since, nowUtc, language);
                sb.Append("• ").Append(c.Tool).Append(" · ").Append(c.ProjectName ?? "?");
                if (!string.IsNullOrWhiteSpace(c.Title)) sb.Append(" · ").Append(TextNormalizer.Truncate(c.Title, 60));
                sb.Append(" · ").Append(ago);
                if (c.SubCount > 0) sb.Append(" · ").Append(Strings.Get(language, "digest.sub").Replace("{n}", c.SubCount.ToString()));
                sb.Append('\n');
                if (!string.IsNullOrWhiteSpace(c.LastUserText))
                    sb.Append("  ").Append(Strings.Get(language, "digest.youLast")).Append(": «").Append(TextNormalizer.Truncate(Display(c.LastUserText), 200)).Append("»\n");
                if (!string.IsNullOrWhiteSpace(c.LastAssistantText))
                    sb.Append("  ").Append(Strings.Get(language, "digest.agentLast")).Append(": «").Append(TextNormalizer.Truncate(c.LastAssistantText, 400)).Append("»\n");
                var flags = new List<string>();
                if (c.AsksQuestion) flags.Add(Strings.Get(language, "flag.asksQuestion"));
                if (c.ClaimsDone && !c.ShowsEvidence) flags.Add(Strings.Get(language, "flag.noEvidence"));
                else if (c.ClaimsDone) flags.Add(Strings.Get(language, "flag.claimsDone"));
                if (flags.Count > 0) sb.Append("  [").Append(string.Join(", ", flags)).Append("]\n");
            }
        }
        return (sb.ToString().TrimEnd('\n'), groups[SessionStates.Waiting].Count, groups[SessionStates.Stalled].Count, groups[SessionStates.Working].Count, groups[SessionStates.Idle].Count);
    }

    /// <summary>Görüntü için: yapıştırma blokları kısa işarete iner.</summary>
    public static string Display(string text) => Regex.Replace(text, @"<pasted_content\b[^>]*>[\s\S]*?</pasted_content[^>]*>", "[yapıştırma]");

    public static string Ago(string? iso, DateTime nowUtc, string language)
    {
        var t = Time.Parse(iso);
        if (t is null) return "?";
        var d = nowUtc - t.Value;
        if (d.TotalMinutes < 1) return Strings.Get(language, "ago.sec");
        if (d.TotalHours < 1) return Strings.Get(language, "ago.min").Replace("{n}", ((int)d.TotalMinutes).ToString());
        if (d.TotalDays < 1) return Strings.Get(language, "ago.hour").Replace("{n}", ((int)d.TotalHours).ToString());
        return Strings.Get(language, "ago.day").Replace("{n}", ((int)d.TotalDays).ToString());
    }
}
