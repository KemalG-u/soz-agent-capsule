using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Readers;

/// <summary>
/// OpenCode oturumları (YALNIZ GÖZLE): &lt;OPENCODE_DATA_DIR&gt;\opencode.db ve opencode-*.db, SQLite, salt okunur açılır; hiçbir şey yazılmaz.
/// İki düzen tanınır (kaynak: opencode packages/core/src/session/sql.ts ve migration dosyaları):
///   2.x: tablolar session_v2 (+ session_message, isteğe bağlı session_part); durum sütunları time_busy / time_idle / idle_outcome.
///   1.x: tablolar session, message (data JSON: role, time.completed), part (data JSON: type, text).
/// İkisi de yoksa ya da gereken sütun eksikse "tanımadığım sürüm" denir ve o dosya okunmaz. Sürüm yoklaması = tablo ve sütun yoklaması (süreç çalıştırılmaz).
/// Alt oturum (parent_id) ana oturumun yerine geçmez: ayrı satır, Kind=subagent, insan sözü sayılmaz.
/// </summary>
public sealed class OpenCodeDbReader : ITranscriptReader
{
    public string Tool => "opencode";
    private const int MaxUserTexts = 300;
    private const int ChunkSessions = 100;

    public ReadReport Read(IngestStore store, string root, ReadOptions options)
    {
        var sw = Stopwatch.StartNew();
        var report = new ReadReport { Tool = Tool, Root = root, Found = Directory.Exists(root) };
        if (!report.Found) return report;
        string[] dbs;
        try { dbs = Directory.GetFiles(root, "opencode*.db", SearchOption.TopDirectoryOnly); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.FileErrors++; return report; }
        Array.Sort(dbs, StringComparer.OrdinalIgnoreCase);
        var states = store.LoadFileStates(Tool);
        var notes = new List<string>();
        foreach (var db in dbs)
        {
            report.Files++;
            try
            {
                using var c = ReadOnlyDb.Open(db);
                var fam = Family(c, out var why);
                if (fam == 0) { report.Unrecognized++; notes.Add($"{Path.GetFileName(db)}: {why}"); continue; }
                var rows = ReadOnlyDb.Retry(() => ListSessions(c, fam, options));
                var pending = new List<FileBatch>();
                foreach (var s in rows)
                {
                    var key = db + "#" + s.Id;
                    if (states.TryGetValue(key, out var st) && st.Size == s.Fingerprint && st.Mtime == s.FingerprintIso) { report.FilesSkippedUnchanged++; continue; }
                    try { pending.Add(ReadOnlyDb.Retry(() => Build(c, fam, s, key, options))); report.FilesRead++; }
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
                notes.Add(e.SqliteErrorCode is 5 or 6 ? $"{Path.GetFileName(db)}: o anda kilitli, okuyamadım." : $"{Path.GetFileName(db)}: okunamadı (bozuk ya da yarım yazılmış olabilir).");
            }
        }
        if (notes.Count > 0) report.Note = string.Join(" ", notes);
        report.ElapsedMs = sw.ElapsedMilliseconds;
        return report;
    }

    /// <summary>2 = 2.x düzeni, 1 = 1.x düzeni, 0 = tanınmıyor (neden why'de).</summary>
    private static int Family(SqliteConnection c, out string why)
    {
        why = "";
        if (ReadOnlyDb.HasTable(c, "session_v2") && ReadOnlyDb.HasTable(c, "session_message"))
        {
            var sc = ReadOnlyDb.Columns(c, "session_v2"); var mc = ReadOnlyDb.Columns(c, "session_message");
            var miss = new[] { "id", "directory", "time_created", "time_updated" }.Where(x => !sc.Contains(x)).Select(x => "session_v2." + x)
                .Concat(new[] { "id", "session_id", "type", "seq", "time_created", "data" }.Where(x => !mc.Contains(x)).Select(x => "session_message." + x)).ToList();
            if (miss.Count == 0) return 2;
            why = $"2.x düzenine benziyor ama alanlar eksik ({string.Join(", ", miss.Take(4))}): tanımadığım sürüm, okumadım.";
            return 0;
        }
        if (ReadOnlyDb.HasTable(c, "session") && ReadOnlyDb.HasTable(c, "message") && ReadOnlyDb.HasTable(c, "part"))
        {
            var sc = ReadOnlyDb.Columns(c, "session"); var mc = ReadOnlyDb.Columns(c, "message"); var pc = ReadOnlyDb.Columns(c, "part");
            var miss = new[] { "id", "directory", "time_created", "time_updated" }.Where(x => !sc.Contains(x)).Select(x => "session." + x)
                .Concat(new[] { "id", "session_id", "time_created", "data" }.Where(x => !mc.Contains(x)).Select(x => "message." + x))
                .Concat(new[] { "message_id", "session_id", "time_created", "data" }.Where(x => !pc.Contains(x)).Select(x => "part." + x)).ToList();
            if (miss.Count == 0) return 1;
            why = $"1.x düzenine benziyor ama alanlar eksik ({string.Join(", ", miss.Take(4))}): tanımadığım sürüm, okumadım.";
            return 0;
        }
        why = "Ne 1.x ne 2.x oturum tabloları var: tanımadığım sürüm, okumadım.";
        return 0;
    }

    private sealed record Row(string Id, string? Parent, string? Directory, string? Title, long Created, long Updated, long? Busy, long? Idle, long? Archived)
    {
        /// <summary>Değişti mi kararı: 2.x'te time_updated busy/idle olaylarında DEĞİŞMEZ, o yüzden üçü birlikte.</summary>
        public long Fingerprint => Updated + 3 * (Busy ?? 0) + 7 * (Idle ?? 0);
        public string FingerprintIso => ReadOnlyDb.IsoFromMillis(Math.Max(Updated, Math.Max(Busy ?? 0, Idle ?? 0))) ?? "";
        public bool Running => Busy is not null && (Idle is null || Idle < Busy);
    }

    private static List<Row> ListSessions(SqliteConnection c, int fam, ReadOptions o)
    {
        var table = fam == 2 ? "session_v2" : "session";
        var cols = ReadOnlyDb.Columns(c, table);
        string Col(string n) => cols.Contains(n) ? n : "NULL";
        var sql = $"SELECT id, {Col("parent_id")}, {Col("directory")}, {Col("title")}, time_created, time_updated, {(fam == 2 ? Col("time_busy") : "NULL")}, {(fam == 2 ? Col("time_idle") : "NULL")}, {Col("time_archived")} FROM {table} ORDER BY time_created";
        var rows = c.Query(sql, r => new Row(r.GetString(0), r.Str(1), r.Str(2), r.Str(3), r.GetInt64(4), r.GetInt64(5), r.LongN(6), r.LongN(7), r.LongN(8)));
        if (o.SinceUtc is { } since)
        {
            var ms = (long)(since - DateTime.UnixEpoch).TotalMilliseconds;
            rows = rows.Where(r => Math.Max(r.Updated, Math.Max(r.Busy ?? 0, r.Idle ?? 0)) >= ms).ToList();
        }
        return rows;
    }

    private static (string Text, long Ts)? First(List<(string, long)> l) => l.Count > 0 ? (l[0].Item1, l[0].Item2) : null;

    private static FileBatch Build(SqliteConnection c, int fam, Row s, string key, ReadOptions o)
    {
        var max = Math.Max(200, o.MaxAssistantChars);
        var upd = new SessionUpdate { Tool = "opencode", ExternalId = s.Id, Cwd = s.Directory, Title = string.IsNullOrWhiteSpace(s.Title) ? null : s.Title };
        upd.StartedAt = ReadOnlyDb.IsoFromMillis(s.Created);
        if (s.Parent is not null) { upd.ParentExternalId = s.Parent; upd.Kind = SessionKinds.Subagent; }
        var batch = new FileBatch { Path = key, Tool = "opencode", Session = upd, Size = s.Fingerprint, Mtime = s.FingerprintIso, NewOffset = 0 };
        upd.Touch(upd.StartedAt);
        upd.Touch(s.FingerprintIso.Length > 0 ? s.FingerprintIso : null);
        var human = upd.Kind is null or SessionKinds.Human;

        // Kullanıcı metinleri (en yeni MaxUserTexts) ve son asistan metni. Araç çıktıları ve dosya ekleri hiç okunmaz.
        List<(string Id, string Text, long Ts)> users;
        (string Text, long Ts)? lastAsst;
        string? lastRole = null; bool lastCompleted = false;
        if (fam == 2)
        {
            users = c.Query("SELECT id, substr(json_extract(data,'$.text'),1,@max), time_created FROM session_message WHERE session_id=@s AND type='user' ORDER BY seq DESC LIMIT " + MaxUserTexts,
                r => (r.GetString(0), r.Str(1) ?? "", r.GetInt64(2)), null, ("@max", max), ("@s", s.Id));
            lastAsst = ReadOnlyDb.HasTable(c, "session_part")
                ? First(c.Query("SELECT substr(json_extract(data,'$.text'),1,@max), time_created FROM session_part WHERE session_id=@s AND type='text' ORDER BY time_created DESC, ordinal DESC LIMIT 1",
                    r => (r.Str(0) ?? "", r.GetInt64(1)), null, ("@max", max), ("@s", s.Id)))
                : null;
        }
        else
        {
            users = c.Query("""
                SELECT p.id, substr(json_extract(p.data,'$.text'),1,@max), p.time_created FROM part p JOIN message m ON m.id=p.message_id
                WHERE p.session_id=@s AND json_extract(m.data,'$.role')='user' AND json_extract(p.data,'$.type')='text' ORDER BY p.time_created DESC LIMIT
                """ + " " + MaxUserTexts, r => (r.GetString(0), r.Str(1) ?? "", r.GetInt64(2)), null, ("@max", max), ("@s", s.Id));
            lastAsst = First(c.Query("""
                SELECT substr(json_extract(p.data,'$.text'),1,@max), p.time_created FROM part p JOIN message m ON m.id=p.message_id
                WHERE p.session_id=@s AND json_extract(m.data,'$.role')='assistant' AND json_extract(p.data,'$.type')='text' ORDER BY p.time_created DESC LIMIT 1
                """, r => (r.Str(0) ?? "", r.GetInt64(1)), null, ("@max", max), ("@s", s.Id)));
            var last = c.Query("SELECT json_extract(data,'$.role'), json_extract(data,'$.time.completed') IS NOT NULL FROM message WHERE session_id=@s ORDER BY time_created DESC, id DESC LIMIT 1",
                r => (r.Str(0), r.GetInt64(1) == 1), null, ("@s", s.Id));
            if (last.Count > 0) { lastRole = last[0].Item1; lastCompleted = last[0].Item2; }
        }
        users.Reverse();
        foreach (var u in users)
        {
            if (string.IsNullOrWhiteSpace(u.Text)) continue;
            var ts = ReadOnlyDb.IsoFromMillis(u.Ts);
            upd.LastUserText = u.Text; upd.LastUserTs = ts;
            // Alt iş oturumlarının "kullanıcı" metni insan sözü değildir (ayıklamaya girmez).
            if (human) { batch.Utterances.Add(new ParsedUtterance(ts ?? s.FingerprintIso, u.Text, 0)); upd.SawHumanInput = true; }
        }
        if (lastAsst is { } la && !string.IsNullOrWhiteSpace(la.Text)) { upd.LastAssistantText = la.Text; upd.LastAssistantTs = ReadOnlyDb.IsoFromMillis(la.Ts); }

        var userTs = Time.Parse(upd.LastUserTs); var asstTs = Time.Parse(upd.LastAssistantTs);
        var asstLast = asstTs.HasValue && (!userTs.HasValue || asstTs >= userTs);
        var lastTs = s.FingerprintIso.Length > 0 ? s.FingerprintIso : null;
        if (fam == 2)
        {
            if (s.Archived is not null) upd.Event(EventKinds.Other, lastTs, true);
            else if (s.Running) upd.Event(EventKinds.Other, lastTs, false);               // yürütme başladı, bitmedi
            else if (asstLast) upd.Event(EventKinds.AssistantText, lastTs, true);         // bitti, son söz asistanın
            else if (upd.LastUserText is not null && s.Busy is null) upd.Event(EventKinds.User, lastTs, false); // hiç yürütülmemiş
            else upd.Event(EventKinds.Other, lastTs, true);
        }
        else
        {
            if (lastRole == "assistant" && lastCompleted) upd.Event(EventKinds.AssistantText, lastTs, true);
            else if (lastRole == "assistant") upd.Event(EventKinds.Other, lastTs, false);
            else if (lastRole == "user") upd.Event(EventKinds.User, lastTs, false);
            else if (lastRole is not null) upd.Event(EventKinds.Other, lastTs, true);
        }
        return batch;
    }
}
