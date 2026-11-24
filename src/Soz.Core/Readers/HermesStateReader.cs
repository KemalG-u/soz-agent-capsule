using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Readers;

/// <summary>
/// Hermes Agent oturumları (YALNIZ GÖZLE): &lt;HERMES_HOME&gt;\state.db, SQLite. Dosya salt okunur açılır, hiçbir şey yazılmaz, kanca kurulmaz, ajan başlatılmaz.
/// Şema kaynağı: hermes-agent `hermes_state_common.py` (SCHEMA_VERSION 33, tablolar schema_version, sessions, messages). Koruma: `schema_version` bu okuyucunun bildiği
/// en yüksek sürümden büyükse ya da gereken sütunlar yoksa "tanımadığım sürüm" denir ve HİÇBİR satır okunmaz (tahminle okuma yok).
/// Durum: son mesajın rolü ve finish_reason'ından kayıt sonu çıkarılır; `runtime\active_sessions.json` canlılığı varsa süreci ölmüş açık tur "durmuş" sayılır.
/// Alt oturum (parent_session_id): ana oturumun yerine geçmez, ayrı satırdır ve Kind=subagent olur; tek istisna sıkıştırma devamıdır (ana oturum end_reason=compression ile
/// bitmiş, çocuk aynı konuşmanın devamıdır): o insan oturumu olarak kalır.
/// </summary>
public sealed class HermesStateReader : ITranscriptReader
{
    /// <summary>Bu okuyucunun bildiği en yüksek şema sürümü (hermes_state_common.py SCHEMA_VERSION).</summary>
    public const int KnownMaxSchema = 33;
    public const string DbName = "state.db";
    private const int MaxMessagesPerSession = 400;
    private const int ChunkSessions = 100;

    private static readonly string[] SessionCols = { "id", "source", "parent_session_id", "started_at", "ended_at", "end_reason", "message_count", "cwd", "title", "last_activity_at" };
    private static readonly string[] MessageCols = { "id", "session_id", "role", "content", "tool_calls", "timestamp", "finish_reason" };

    /// <summary>Testler için: süreç başlangıç zamanı okuyucusu ve "şimdi".</summary>
    public Func<int, DateTime?> ProcessStart { get; init; } = HermesLiveness.DefaultProcessStart;

    public string Tool => "hermes";

    public ReadReport Read(IngestStore store, string root, ReadOptions options)
    {
        var sw = Stopwatch.StartNew();
        var report = new ReadReport { Tool = Tool, Root = root };
        var db = Path.Combine(root, DbName);
        report.Found = File.Exists(db);
        if (!report.Found) return report;
        report.Files = 1;
        try
        {
            using var c = ReadOnlyDb.Open(db);
            if (!Probe(c, report, out var hasActive)) { report.ElapsedMs = sw.ElapsedMilliseconds; return report; }
            var live = HermesLiveness.Read(root, ProcessStart);
            var states = store.LoadFileStates(Tool);
            var sessions = ReadOnlyDb.Retry(() => ListSessions(c, options));
            var pending = new List<FileBatch>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            // Ana oturumun sıkıştırma ile bitip bitmediği (çocuğun türü için).
            var endReason = sessions.ToDictionary(s => s.Id, s => s.EndReason, StringComparer.Ordinal);
            foreach (var s in sessions)
            {
                var fp = s.Fingerprint;
                var key = db + "#" + s.Id;
                // Parmak izi: mesaj sayısı + canlılık kodu (açık oturum: 1 süreç yaşıyor, 2 süreç yok, 0 bilinmiyor). Kayıt değişmese de süreç giderse oturum yeniden değerlendirilir.
                var size = s.MessageCount * 4 + (s.Ended is null && live is not null ? (live.Contains(s.Id) ? 1 : 2) : 0);
                if (states.TryGetValue(key, out var st) && st.Size == size && st.Mtime == fp) { report.FilesSkippedUnchanged++; continue; }
                try
                {
                    var batch = ReadOnlyDb.Retry(() => BuildBatch(c, s, key, fp, hasActive, live, endReason, options));
                    batch.Size = size;
                    pending.Add(batch);
                    report.FilesRead++;
                }
                catch (SqliteException) { report.FileErrors++; }
            }
            for (var i = 0; i < pending.Count; i += ChunkSessions)
            {
                var (u, comp) = store.Apply(pending.Skip(i).Take(ChunkSessions).ToList(), options.SinceUtc);
                report.NewUtterances += u; report.NewCompactions += comp;
            }
        }
        catch (SqliteException e)
        {
            report.FileErrors++;
            report.Note = e.SqliteErrorCode is 5 or 6 ? "Hermes veritabanı o anda kilitli, okuyamadım. Sonra yeniden denenecek." : "Hermes veritabanı okunamadı (bozuk ya da yarım yazılmış olabilir).";
        }
        report.ElapsedMs = sw.ElapsedMilliseconds;
        return report;
    }

    /// <summary>Şema koruması. Tanınmıyorsa rapora neden yazılır ve false döner (hiçbir oturum okunmaz).</summary>
    private static bool Probe(SqliteConnection c, ReadReport report, out bool hasActive)
    {
        hasActive = false;
        if (!ReadOnlyDb.HasTable(c, "schema_version") || !ReadOnlyDb.HasTable(c, "sessions") || !ReadOnlyDb.HasTable(c, "messages"))
        { report.Unrecognized++; report.Note = "Hermes veritabanının düzenini tanımıyorum (beklenen tablolar yok). Okumadım."; return false; }
        var ver = c.Scalar("SELECT MAX(version) FROM schema_version");
        if (ver is null or DBNull) { report.Unrecognized++; report.Note = "Hermes veritabanının sürümü yazılı değil. Tanımadığım sürüm, okumadım."; return false; }
        var v = Convert.ToInt64(ver);
        if (v > KnownMaxSchema) { report.Unrecognized++; report.Note = $"Hermes veritabanı sürüm {v}, ben en çok {KnownMaxSchema} sürümünü biliyorum: tanımadığım sürüm, okumadım."; return false; }
        var sc = ReadOnlyDb.Columns(c, "sessions"); var mc = ReadOnlyDb.Columns(c, "messages");
        var miss = SessionCols.Where(x => !sc.Contains(x)).Select(x => "sessions." + x).Concat(MessageCols.Where(x => !mc.Contains(x)).Select(x => "messages." + x)).ToList();
        if (miss.Count > 0) { report.Unrecognized++; report.Note = $"Hermes veritabanı sürüm {v} ama beklenen alanlar yok ({string.Join(", ", miss.Take(4))}): tanımadığım sürüm, okumadım."; return false; }
        hasActive = mc.Contains("active");
        return true;
    }

    private sealed record SessionRow(string Id, string Source, string? Parent, double Started, double? Ended, string? EndReason, long MessageCount, string? Cwd, string? Title, double? LastActivity)
    {
        public double Latest => Math.Max(Started, Math.Max(Ended ?? 0, LastActivity ?? 0));
        public string Fingerprint => ReadOnlyDb.IsoFromSeconds(Latest) ?? "";
    }

    private static List<SessionRow> ListSessions(SqliteConnection c, ReadOptions o)
    {
        var rows = c.Query("SELECT id, source, parent_session_id, started_at, ended_at, end_reason, message_count, cwd, title, last_activity_at FROM sessions ORDER BY started_at",
            r => new SessionRow(r.GetString(0), r.Str(1) ?? "", r.Str(2), r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4), r.Str(5), r.Long(6), r.Str(7), r.Str(8), r.IsDBNull(9) ? null : r.GetDouble(9)));
        if (o.SinceUtc is { } since)
        {
            var sinceSec = (since - DateTime.UnixEpoch).TotalSeconds;
            rows = rows.Where(r => r.Latest >= sinceSec).ToList();
        }
        return rows;
    }

    private static FileBatch BuildBatch(SqliteConnection c, SessionRow s, string key, string fp, bool hasActive, HashSet<string>? live, Dictionary<string, string?> endReason, ReadOptions o)
    {
        var max = Math.Max(200, o.MaxAssistantChars);
        // Yalnız insan ve asistan metni okunur; araç çıktıları (uzun ve gizli olabilir) hiç çekilmez.
        var sql = $"""
            SELECT id, role, CASE WHEN role IN ('user','assistant') THEN substr(content, 1, @max) END,
                   CASE WHEN tool_calls IS NOT NULL AND tool_calls <> '' AND tool_calls <> '[]' THEN 1 ELSE 0 END, timestamp, finish_reason
            FROM messages WHERE session_id=@s {(hasActive ? "AND (active IS NULL OR active=1)" : "")}
            ORDER BY id DESC LIMIT {MaxMessagesPerSession}
            """;
        var msgs = c.Query(sql, r => (Id: r.GetInt64(0), Role: r.Str(1) ?? "", Text: r.Str(2), HasCalls: r.GetInt64(3) == 1, Ts: r.GetDouble(4), Finish: r.Str(5)), null, ("@max", max), ("@s", s.Id));
        msgs.Reverse();

        var upd = new SessionUpdate { Tool = "hermes", ExternalId = s.Id, Cwd = s.Cwd, Title = string.IsNullOrWhiteSpace(s.Title) ? null : s.Title };
        upd.StartedAt = ReadOnlyDb.IsoFromSeconds(s.Started);
        if (s.Source is "cron" or "webhook" or "batch") upd.Kind = SessionKinds.Automation;
        if (s.Parent is not null)
        {
            upd.ParentExternalId = s.Parent;
            // Sıkıştırma devamı: aynı konuşmanın sürdüğü yer, alt iş değildir.
            upd.Kind = endReason.TryGetValue(s.Parent, out var pr) && pr == "compression" ? SessionKinds.Human : SessionKinds.Subagent;
        }
        var batch = new FileBatch { Path = key, Tool = "hermes", Session = upd, Size = s.MessageCount, Mtime = fp, NewOffset = 0 };
        upd.Touch(upd.StartedAt);
        upd.Touch(fp.Length > 0 ? fp : null);

        foreach (var m in msgs)
        {
            var ts = ReadOnlyDb.IsoFromSeconds(m.Ts);
            if (m.Role == "user")
            {
                if (!string.IsNullOrWhiteSpace(m.Text))
                {
                    upd.LastUserText = m.Text; upd.LastUserTs = ts;
                    // Alt iş ve otomasyon oturumlarının "kullanıcı" mesajı insan sözü değildir: söz arşivine (ayıklamaya) girmez.
                    if (upd.Kind is null or SessionKinds.Human) { batch.Utterances.Add(new ParsedUtterance(ts ?? fp, m.Text, m.Id)); upd.SawHumanInput = true; }
                }
                upd.Event(EventKinds.User, ts, false);
            }
            else if (m.Role == "assistant")
            {
                if (!string.IsNullOrWhiteSpace(m.Text)) { upd.LastAssistantText = m.Text; upd.LastAssistantTs = ts; }
                if (m.HasCalls) upd.Event(EventKinds.Tool, ts, false);
                else upd.Event(EventKinds.AssistantText, ts, m.Finish is null or "stop" or "end_turn" or "length" or "content_filter");
            }
            else if (m.Role == "tool") upd.Event(EventKinds.Tool, ts, false);
        }

        var open = s.Ended is null;
        if (!open)
        {
            // Oturum kapanmış: açık araç çağrısı kalmış olsa da tur bitti sayılır, "çalışıyor" denmez.
            upd.TurnEnded = true;
            if (upd.LastEventKind == EventKinds.Tool || upd.LastEventKind == EventKinds.User) upd.LastEventKind = EventKinds.Other;
            upd.Touch(ReadOnlyDb.IsoFromSeconds(s.Ended));
        }
        else if (live is not null && upd.TurnEnded != true && !live.Contains(s.Id))
        {
            // Açık tur ama canlı bir süreç kaydı yok (canlılık dosyası okundu): süreç gitmiş, durmuş say.
            upd.TurnEnded = true;
            upd.LastEventKind = EventKinds.Other;
        }
        return batch;
    }
}

/// <summary>
/// Hermes canlılık kaydı: &lt;HERMES_HOME&gt;\runtime\active_sessions.json ({"entries":[{session_id, pid, process_start_time, ...}]}), salt okunur.
/// Süreç yaşıyor sayılır: pid var VE (başlangıç zamanı yazılıysa) kayıttaki başlangıçla aynı (pid yeniden kullanımına karşı).
/// Dosya yok ya da okunamıyorsa null döner = "bilmiyorum" (varsayım yapılmaz).
/// </summary>
public static class HermesLiveness
{
    public const string RelativePath = "runtime\\active_sessions.json";

    public static DateTime? DefaultProcessStart(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited ? null : p.StartTime.ToUniversalTime(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    public static HashSet<string>? Read(string root, Func<int, DateTime?> processStart)
    {
        var path = Path.Combine(root, RelativePath);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 4 << 20) return null;
            var node = JsonNode.Parse(fs);
            var entries = node?["entries"];
            IEnumerable<JsonNode?> items = entries is JsonArray a ? a : entries is JsonObject o ? o.Select(kv => kv.Value) : Array.Empty<JsonNode?>();
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in items)
            {
                if (e is not JsonObject eo) continue;
                var sid = (eo["session_id"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
                if (string.IsNullOrEmpty(sid) || !TryInt(eo["pid"], out var pid) || pid <= 0) continue;
                var start = processStart(pid);
                if (start is null) continue;
                if (TryDouble(eo["process_start_time"], out var expected) && expected > 0
                    && Math.Abs((start.Value - DateTime.UnixEpoch).TotalSeconds - expected) > 2.0) continue;
                set.Add(sid);
            }
            return set;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    private static bool TryInt(JsonNode? n, out int v) { v = 0; return n is JsonValue j && (j.TryGetValue(out v) || (j.TryGetValue<double>(out var d) && (v = (int)d) == d)); }
    private static bool TryDouble(JsonNode? n, out double v) { v = 0; return n is JsonValue j && (j.TryGetValue(out v) || (j.TryGetValue<long>(out var l) && (v = l) == l)); }
}
