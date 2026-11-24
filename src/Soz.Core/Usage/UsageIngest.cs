using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Readers;

namespace Soz.Core.Usage;

/// <summary>Bir API çağrısının (mesajın) kullanımı. Key: yinelenen satırları teke indirir.</summary>
public sealed record UsageRec(string Key, string Day, string Tool, string Model, bool IsSub, long Input, long Output, long CacheRead, long CacheWrite);

public sealed class UsageReport
{
    public int Files { get; set; }
    public int FilesRead { get; set; }
    public int FilesSkippedUnchanged { get; set; }
    public int NewMessages { get; set; }
    public int DuplicateLines { get; set; }
    public int NoIdMessages { get; set; }
    public int BadLines { get; set; }
    public int FileErrors { get; set; }
    public long BytesRead { get; set; }
    public long ElapsedMs { get; set; }
    public bool CodexTokenEvents { get; set; }
}

/// <summary>
/// Harcama içe alma (M8). Okuyucu geçişinden BAĞIMSIZ: kendi dosya durumu (file_state, yol "usage:&lt;dosya&gt;") ile artımlı.
/// Yineleme kuralı (gerçek dosyalarda ölçüldü): Claude Code aynı API çağrısını içerik bloğu başına ayrı satır yazar
/// (thinking / text / tool_use ...) ve her satırda AYNI message.id ile AYNI usage vardır (örnek dosyalarda satırların ≈%50-60'ı yineleme).
/// Anahtar = message.id (yoksa requestId); aynı anahtar bir daha gelirse alan başına en büyük değer tutulur ve yalnızca FARK usage_daily'ye eklenir.
/// Böylece aynı dosya yeniden okunsa, dosya yeniden yazılsa ya da oturum çatallanıp (resume) satırlar başka dosyaya kopyalansa şişmez.
/// Codex: event_msg/token_count.info.last_token_usage; anahtar = oturum + kümülatif total_tokens (aynı toplam ikinci kez gelirse sayılmaz).
/// Gün = zaman damgasının UTC tarihi.
/// </summary>
public static class UsageIngest
{
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS usage_daily(day TEXT, tool TEXT, model TEXT, is_subagent INTEGER, messages INTEGER DEFAULT 0, input_tokens INTEGER DEFAULT 0, output_tokens INTEGER DEFAULT 0, cache_read_tokens INTEGER DEFAULT 0, cache_write_tokens INTEGER DEFAULT 0);
        CREATE TABLE IF NOT EXISTS usage_seen(key TEXT PRIMARY KEY, day TEXT, tool TEXT, model TEXT, is_subagent INTEGER, input_tokens INTEGER, output_tokens INTEGER, cache_read_tokens INTEGER, cache_write_tokens INTEGER);
        CREATE INDEX IF NOT EXISTS ix_usage_daily_day ON usage_daily(day);
        """;

    public static void EnsureTables(SqliteConnection c) => c.Exec(Ddl);

    public static UsageReport Run(SozDb db, string? claudeRoot, string? codexRoot, DateTime? sinceUtc, bool skipClaude = false, bool skipCodex = false)
    {
        var sw = Stopwatch.StartNew();
        var rep = new UsageReport();
        using (var c0 = db.Open()) EnsureTables(c0);
        if (!skipClaude && claudeRoot is not null && Directory.Exists(claudeRoot))
            foreach (var f in Enumerate(claudeRoot)) Process(db, f, "claude", sinceUtc, rep);
        if (!skipCodex && codexRoot is not null && Directory.Exists(codexRoot))
        {
            string[] files;
            try { files = Directory.GetFiles(codexRoot, "rollout-*.jsonl", SearchOption.AllDirectories); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files = Array.Empty<string>(); rep.FileErrors++; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files) Process(db, f, "codex", sinceUtc, rep);
        }
        rep.ElapsedMs = sw.ElapsedMilliseconds;
        return rep;
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try { return Directory.EnumerateFiles(root, "*.jsonl", opt).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public static bool IsSubagentPath(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/subagents/", StringComparison.OrdinalIgnoreCase);
    }

    private static void Process(SozDb db, string file, string tool, DateTime? since, UsageReport rep)
    {
        rep.Files++;
        try
        {
            var fi = new FileInfo(file);
            if (since is { } s && fi.LastWriteTimeUtc < s) return;
            var stateKey = "usage:" + file;
            long offset = 0, lineNo = 0;
            using (var c = db.Open())
            {
                var row = c.Query("SELECT offset, line_no, mtime, size FROM file_state WHERE path=@p",
                    r => (off: r.Long(0), ln: r.Long(1), mt: r.Str(2), sz: r.Long(3)), null, ("@p", stateKey)).FirstOrDefault();
                if (row != default)
                {
                    if (row.sz == fi.Length && row.mt == Time.Iso(fi.LastWriteTimeUtc)) { rep.FilesSkippedUnchanged++; return; }
                    if (row.off <= fi.Length) { offset = row.off; lineNo = row.ln; }
                }
            }
            var batch = new Dictionary<string, UsageRec>();
            var acceptTail = (Time.UtcNow() - fi.LastWriteTimeUtc).TotalSeconds > 60;
            long newOffset, endLine, len;
            var fallbackDay = Time.Iso(fi.LastWriteTimeUtc)[..10];
            if (tool == "claude")
            {
                var isSub = IsSubagentPath(file);
                void H(ReadOnlySpan<byte> line, long off, long lno)
                {
                    if (!JsonlScanner.Contains(line, "\"usage\":{"u8)) return;
                    var res = UsageParser.ParseClaude(line, out var rec, out var noId);
                    if (res == UsageParser.Result.Bad) { rep.BadLines++; return; }
                    if (res != UsageParser.Result.Ok || rec is null) return;
                    var key = rec.Key.Length > 0 ? rec.Key : "cl:f:" + Path.GetFileName(file) + ":" + lno;
                    if (noId) rep.NoIdMessages++;
                    var day = rec.Day.Length == 10 ? rec.Day : fallbackDay;
                    var r2 = rec with { Key = key, Day = day, IsSub = isSub };
                    if (batch.TryGetValue(key, out var old)) { r2 = Merge(old, r2); rep.DuplicateLines++; }
                    batch[key] = r2;
                }
                newOffset = JsonlScanner.Scan(file, offset, lineNo, H, out endLine, out len, acceptTail);
            }
            else
            {
                var meta = ReadCodexMeta(file);
                string model;
                using (var c = db.Open()) model = c.Scalar("SELECT model FROM usage_seen WHERE key LIKE @k ORDER BY rowid DESC LIMIT 1", null, ("@k", "cx:" + meta.id + ":%")) as string ?? "codex";
                void H(ReadOnlySpan<byte> line, long off, long lno)
                {
                    if (JsonlScanner.Contains(line, "\"turn_context\""u8, 300))
                    {
                        using var d = JsonlScanner.TryParse(line);
                        if (d is not null && d.RootElement.Obj("payload").StrN("model") is { Length: > 0 } m) model = m;
                        return;
                    }
                    if (!JsonlScanner.Contains(line, "\"token_count\""u8, 400)) return;
                    using var doc = JsonlScanner.TryParse(line);
                    if (doc is null) { rep.BadLines++; return; }
                    var rec = UsageParser.ParseCodex(doc.RootElement, meta.id, model, meta.isSub, fallbackDay);
                    if (rec is null) return;
                    rep.CodexTokenEvents = true;
                    if (batch.TryGetValue(rec.Key, out var old)) { rec = Merge(old, rec); rep.DuplicateLines++; }
                    batch[rec.Key] = rec;
                }
                newOffset = JsonlScanner.Scan(file, offset, lineNo, H, out endLine, out len, acceptTail);
            }
            rep.BytesRead += Math.Max(0, newOffset - offset);
            Apply(db, batch.Values, stateKey, tool, newOffset, endLine, len, Time.Iso(fi.LastWriteTimeUtc), rep);
            rep.FilesRead++;
        }
        catch (IOException) { rep.FileErrors++; }
        catch (UnauthorizedAccessException) { rep.FileErrors++; }
    }

    private static (string id, bool isSub) ReadCodexMeta(string file)
    {
        string id = Path.GetFileNameWithoutExtension(file);
        var isSub = false;
        var first = JsonlScanner.ReadFirstLine(file, 256 * 1024);
        if (first is not null)
        {
            using var d = JsonlScanner.TryParse(first);
            if (d is not null && d.RootElement.StrN("type") == "session_meta")
            {
                var p = d.RootElement.Obj("payload");
                id = p.StrN("id") ?? p.StrN("session_id") ?? id;
                isSub = p.Obj("source").ValueKind == JsonValueKind.Object || p.StrN("thread_source") == "subagent";
            }
        }
        return (id, isSub);
    }

    private static UsageRec Merge(UsageRec a, UsageRec b) => a with
    {
        Input = Math.Max(a.Input, b.Input), Output = Math.Max(a.Output, b.Output),
        CacheRead = Math.Max(a.CacheRead, b.CacheRead), CacheWrite = Math.Max(a.CacheWrite, b.CacheWrite),
        Model = a.Model, Day = a.Day,
    };

    private static void Apply(SozDb db, IEnumerable<UsageRec> recs, string stateKey, string tool, long offset, long lineNo, long size, string mtime, UsageReport rep)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var daily = new Dictionary<(string day, string tool, string model, int sub), long[]>();
        foreach (var r in recs)
        {
            var old = c.Query("SELECT day, tool, model, is_subagent, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens FROM usage_seen WHERE key=@k",
                x => (day: x.Str(0)!, tool: x.Str(1)!, model: x.Str(2)!, sub: (int)x.Long(3), i: x.Long(4), o: x.Long(5), cr: x.Long(6), cw: x.Long(7)), tx, ("@k", r.Key)).FirstOrDefault();
            long di, dout, dcr, dcw, dm;
            string day, t, model; int sub;
            if (old == default)
            {
                c.Exec("INSERT INTO usage_seen(key, day, tool, model, is_subagent, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens) VALUES(@k,@d,@t,@m,@s,@i,@o,@cr,@cw)", tx,
                    ("@k", r.Key), ("@d", r.Day), ("@t", r.Tool), ("@m", r.Model), ("@s", r.IsSub ? 1 : 0), ("@i", r.Input), ("@o", r.Output), ("@cr", r.CacheRead), ("@cw", r.CacheWrite));
                di = r.Input; dout = r.Output; dcr = r.CacheRead; dcw = r.CacheWrite; dm = 1;
                day = r.Day; t = r.Tool; model = r.Model; sub = r.IsSub ? 1 : 0;
                rep.NewMessages++;
            }
            else
            {
                var mi = Math.Max(old.i, r.Input); var mo = Math.Max(old.o, r.Output); var mcr = Math.Max(old.cr, r.CacheRead); var mcw = Math.Max(old.cw, r.CacheWrite);
                if (mi == old.i && mo == old.o && mcr == old.cr && mcw == old.cw) { rep.DuplicateLines++; continue; }
                c.Exec("UPDATE usage_seen SET input_tokens=@i, output_tokens=@o, cache_read_tokens=@cr, cache_write_tokens=@cw WHERE key=@k", tx,
                    ("@i", mi), ("@o", mo), ("@cr", mcr), ("@cw", mcw), ("@k", r.Key));
                di = mi - old.i; dout = mo - old.o; dcr = mcr - old.cr; dcw = mcw - old.cw; dm = 0;
                day = old.day; t = old.tool; model = old.model; sub = old.sub;
                rep.DuplicateLines++;
            }
            var k = (day, t, model, sub);
            if (!daily.TryGetValue(k, out var acc)) daily[k] = acc = new long[5];
            acc[0] += dm; acc[1] += di; acc[2] += dout; acc[3] += dcr; acc[4] += dcw;
        }
        foreach (var (k, v) in daily)
        {
            var n = c.Exec("UPDATE usage_daily SET messages=messages+@m, input_tokens=input_tokens+@i, output_tokens=output_tokens+@o, cache_read_tokens=cache_read_tokens+@cr, cache_write_tokens=cache_write_tokens+@cw WHERE day=@d AND tool=@t AND model=@mo AND is_subagent=@s", tx,
                ("@m", v[0]), ("@i", v[1]), ("@o", v[2]), ("@cr", v[3]), ("@cw", v[4]), ("@d", k.day), ("@t", k.tool), ("@mo", k.model), ("@s", k.sub));
            if (n == 0)
                c.Exec("INSERT INTO usage_daily(day, tool, model, is_subagent, messages, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens) VALUES(@d,@t,@mo,@s,@m,@i,@o,@cr,@cw)", tx,
                    ("@d", k.day), ("@t", k.tool), ("@mo", k.model), ("@s", k.sub), ("@m", v[0]), ("@i", v[1]), ("@o", v[2]), ("@cr", v[3]), ("@cw", v[4]));
        }
        c.Exec("""
            INSERT INTO file_state(path, tool, offset, mtime, size, line_no) VALUES(@p,@tool,@o,@m,@sz,@ln)
            ON CONFLICT(path) DO UPDATE SET tool=excluded.tool, offset=excluded.offset, mtime=excluded.mtime, size=excluded.size, line_no=excluded.line_no
            """, tx, ("@p", stateKey), ("@tool", "usage-" + tool), ("@o", offset), ("@m", mtime), ("@sz", size), ("@ln", lineNo));
        tx.Commit();
    }
}

/// <summary>Bayt düzeyinde ayrıştırma (DOM yok): büyük araç çıktılı satırlarda hızlı.</summary>
public static class UsageParser
{
    public enum Result { Ok, NotAssistant, NoUsage, Synthetic, Bad }

    public static Result ParseClaude(ReadOnlySpan<byte> line, out UsageRec? rec, out bool noId)
    {
        rec = null; noId = false;
        string? type = null, ts = null, requestId = null, id = null, model = null;
        long inp = 0, outp = 0, cr = 0, cw = 0;
        var hasUsage = false;
        try
        {
            var r = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 256 });
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return Result.Bad;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("type"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) type = r.GetString(); else r.Skip(); }
                else if (r.ValueTextEquals("timestamp"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) ts = r.GetString(); else r.Skip(); }
                else if (r.ValueTextEquals("requestId"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) requestId = r.GetString(); else r.Skip(); }
                else if (r.ValueTextEquals("message"u8))
                {
                    r.Read();
                    if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                    {
                        if (r.ValueTextEquals("id"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) id = r.GetString(); else r.Skip(); }
                        else if (r.ValueTextEquals("model"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) model = r.GetString(); else r.Skip(); }
                        else if (r.ValueTextEquals("usage"u8))
                        {
                            r.Read();
                            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                            hasUsage = true;
                            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                            {
                                long v;
                                if (r.ValueTextEquals("input_tokens"u8)) { r.Read(); inp = r.TokenType == JsonTokenType.Number && r.TryGetInt64(out v) ? v : 0; }
                                else if (r.ValueTextEquals("output_tokens"u8)) { r.Read(); outp = r.TokenType == JsonTokenType.Number && r.TryGetInt64(out v) ? v : 0; }
                                else if (r.ValueTextEquals("cache_read_input_tokens"u8)) { r.Read(); cr = r.TokenType == JsonTokenType.Number && r.TryGetInt64(out v) ? v : 0; }
                                else if (r.ValueTextEquals("cache_creation_input_tokens"u8)) { r.Read(); cw = r.TokenType == JsonTokenType.Number && r.TryGetInt64(out v) ? v : 0; }
                                else { r.Read(); r.Skip(); }
                            }
                        }
                        else { r.Read(); r.Skip(); }
                    }
                }
                else { r.Read(); r.Skip(); }
            }
        }
        catch (JsonException) { return Result.Bad; }
        if (type != "assistant") return Result.NotAssistant;
        if (!hasUsage) return Result.NoUsage;
        if (model is null || model == "<synthetic>") return Result.Synthetic;
        if (inp + outp + cr + cw == 0) return Result.NoUsage;
        string key;
        if (!string.IsNullOrEmpty(id)) key = "cl:" + id;
        else if (!string.IsNullOrEmpty(requestId)) key = "cl:rq:" + requestId;
        else { key = ""; noId = true; }
        var day = Time.NormalizeIso(ts) is { } iso ? iso[..10] : "";
        rec = new UsageRec(key, day, "claude", model, false, inp, outp, cr, cw);
        return Result.Ok;
    }

    /// <summary>Codex token_count olayı. input_tokens önbellekli kısmı da kapsar: input = input - cached, cacheRead = cached.</summary>
    public static UsageRec? ParseCodex(JsonElement root, string sessionId, string model, bool isSub, string fallbackDay)
    {
        var pl = root.Obj("payload");
        if (pl.StrN("type") != "token_count") return null;
        var info = pl.Obj("info");
        if (info.ValueKind != JsonValueKind.Object) return null;
        var last = info.Obj("last_token_usage");
        var total = info.Obj("total_token_usage");
        if (last.ValueKind != JsonValueKind.Object) return null;
        long L(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : 0;
        var cached = L(last, "cached_input_tokens");
        var input = Math.Max(0, L(last, "input_tokens") - cached);
        var output = L(last, "output_tokens");
        var cw = L(last, "cache_write_input_tokens");
        if (input + output + cached + cw == 0) return null;
        var cum = L(total, "total_tokens");
        var key = "cx:" + sessionId + ":" + cum + ":" + L(last, "total_tokens");
        var day = Time.NormalizeIso(root.StrN("timestamp")) is { } iso ? iso[..10] : fallbackDay;
        return new UsageRec(key, day, "codex", model, isSub, input, output, cached, cw);
    }
}
