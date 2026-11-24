using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Text;

namespace Soz.Core.Readers;

public sealed record FileStateRow(string Path, string? Tool, long Offset, long LineNo, string? Mtime, long Size);

/// <summary>Okuyucuların veritabanına yazdığı tek yer. Dosya başına kısa bir işlem açar (kancalar beklemesin).</summary>
public sealed class IngestStore
{
    private readonly SozDb _db;
    public IngestStore(SozDb db) => _db = db;
    public SozDb Db => _db;

    public Dictionary<string, FileStateRow> LoadFileStates(string tool)
    {
        using var c = _db.Open();
        return c.Query("SELECT path, tool, offset, line_no, mtime, size FROM file_state WHERE tool=@t",
                r => new FileStateRow(r.GetString(0), r.Str(1), r.Long(2), r.Long(3), r.Str(4), r.Long(5)), null, ("@t", tool))
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
    }

    public (int utterances, int compactions) Apply(IReadOnlyList<FileBatch> batches, DateTime? sinceUtc)
    {
        if (batches.Count == 0) return (0, 0);
        var sinceIso = sinceUtc.HasValue ? Time.Iso(sinceUtc.Value) : null;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        int nu = 0, nc = 0;
        foreach (var b in batches)
        {
            long? sessionId = null, projectId = null;
            if (b.Session is { } s)
            {
                (sessionId, projectId) = UpsertSession(c, tx, s, b.Path);
                foreach (var u in b.Utterances)
                {
                    if (sinceIso is not null && string.CompareOrdinal(u.Ts, sinceIso) < 0) continue;
                    if (InsertUtterance(c, tx, sessionId.Value, projectId, u.Ts, u.Text, "transcript", u.Line) is not null) nu++;
                }
                foreach (var ts in b.CompactionTs)
                    nc += c.Exec("INSERT OR IGNORE INTO compactions(session_id, ts) VALUES(@s,@t)", tx, ("@s", sessionId), ("@t", ts));
                FinalizeKind(c, tx, sessionId.Value, s);
            }
            c.Exec("""
                INSERT INTO file_state(path, tool, offset, mtime, size, line_no) VALUES(@p,@tool,@o,@m,@sz,@ln)
                ON CONFLICT(path) DO UPDATE SET tool=excluded.tool, offset=excluded.offset, mtime=excluded.mtime, size=excluded.size, line_no=excluded.line_no
                """, tx, ("@p", b.Path), ("@tool", b.Tool), ("@o", b.NewOffset), ("@m", b.Mtime), ("@sz", b.Size), ("@ln", b.NewLineNo));
        }
        tx.Commit();
        return (nu, nc);
    }

    public static long EnsureProject(SqliteConnection c, SqliteTransaction? tx, string? cwd)
    {
        var key = TextNormalizer.ProjectKey(cwd);
        var id = c.Scalar("SELECT id FROM projects WHERE key=@k", tx, ("@k", key));
        if (id is not null) return Convert.ToInt64(id);
        var uid = Uid.New();
        var now = Time.NowIso();
        c.Exec("INSERT INTO projects(key,name,path,created_at,uid) VALUES(@k,@n,@p,@t,@u)", tx,
            ("@k", key), ("@n", TextNormalizer.ProjectName(cwd)), ("@p", cwd), ("@t", now), ("@u", uid));
        var newId = c.ScalarLong("SELECT last_insert_rowid()", tx);
        ProjectPaths.Seen(c, tx, uid, cwd);
        return newId;
    }

    public static (long sessionId, long? projectId) EnsureSession(SqliteConnection c, SqliteTransaction? tx, string tool, string externalId, string? cwd, string? filePath, string kind)
    {
        using (var cmd = c.Cmd("SELECT id, project_id FROM sessions WHERE tool=@t AND external_id=@e", tx, ("@t", tool), ("@e", externalId)))
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read()) return (r.GetInt64(0), r.LongN(1));
        }
        var pid = EnsureProject(c, tx, cwd);
        c.Exec("INSERT INTO sessions(tool, external_id, project_id, file_path, kind, turn_ended) VALUES(@t,@e,@p,@f,@k,0)", tx,
            ("@t", tool), ("@e", externalId), ("@p", pid), ("@f", filePath), ("@k", kind));
        return (c.ScalarLong("SELECT last_insert_rowid()", tx), pid);
    }

    private static (long, long?) UpsertSession(SqliteConnection c, SqliteTransaction tx, SessionUpdate s, string filePath)
    {
        var (id, pid) = EnsureSession(c, tx, s.Tool, s.ExternalId, s.Cwd, filePath, s.Kind ?? SessionKinds.Human);
        if (pid is null && s.Cwd is not null)
        {
            pid = EnsureProject(c, tx, s.Cwd);
            c.Exec("UPDATE sessions SET project_id=@p WHERE id=@id", tx, ("@p", pid), ("@id", id));
        }
        var safeTitle = s.Title is null ? null : SecretFilter.MaskSecrets(s.Title);
        var safeUser = s.LastUserText is null ? null : SecretFilter.MaskSecrets(s.LastUserText);
        var safeAsst = s.LastAssistantText is null ? null : SecretFilter.MaskSecrets(s.LastAssistantText);
        c.Exec("""
            UPDATE sessions SET
              file_path = COALESCE(file_path, @f),
              parent_external_id = COALESCE(@parent, parent_external_id),
              title = COALESCE(@title, title),
              started_at = CASE WHEN started_at IS NULL OR (@st IS NOT NULL AND @st < started_at) THEN COALESCE(@st, started_at) ELSE started_at END,
              last_at = CASE WHEN last_at IS NULL OR (@la IS NOT NULL AND @la > last_at) THEN COALESCE(@la, last_at) ELSE last_at END,
              last_user_text = CASE WHEN @lut IS NOT NULL AND (last_user_ts IS NULL OR @luts >= last_user_ts) THEN @lut ELSE last_user_text END,
              last_user_ts = CASE WHEN @lut IS NOT NULL AND (last_user_ts IS NULL OR @luts >= last_user_ts) THEN @luts ELSE last_user_ts END,
              last_assistant_text = CASE WHEN @lat IS NOT NULL AND (last_assistant_ts IS NULL OR @lats >= last_assistant_ts) THEN @lat ELSE last_assistant_text END,
              last_assistant_ts = CASE WHEN @lat IS NOT NULL AND (last_assistant_ts IS NULL OR @lats >= last_assistant_ts) THEN @lats ELSE last_assistant_ts END,
              last_event_kind = CASE WHEN @lek IS NOT NULL AND (last_event_ts IS NULL OR @lets IS NULL OR @lets >= last_event_ts) THEN @lek ELSE last_event_kind END,
              turn_ended = CASE WHEN @te IS NOT NULL AND (last_event_ts IS NULL OR @lets IS NULL OR @lets >= last_event_ts) THEN @te ELSE turn_ended END,
              last_event_ts = CASE WHEN @lets IS NOT NULL AND (last_event_ts IS NULL OR @lets >= last_event_ts) THEN @lets ELSE last_event_ts END
            WHERE id=@id
            """, tx,
            ("@f", filePath), ("@parent", s.ParentExternalId), ("@title", safeTitle), ("@st", s.StartedAt), ("@la", s.LastAt),
            ("@lut", safeUser), ("@luts", s.LastUserTs ?? s.LastAt), ("@lat", safeAsst), ("@lats", s.LastAssistantTs ?? s.LastAt),
            ("@lek", s.LastEventKind), ("@lets", s.LastEventTs), ("@te", s.TurnEnded.HasValue ? (s.TurnEnded.Value ? 1 : 0) : null), ("@id", id));
        return (id, pid);
    }

    private static void FinalizeKind(SqliteConnection c, SqliteTransaction tx, long sessionId, SessionUpdate s)
    {
        string kind;
        if (s.Kind is SessionKinds.Subagent or SessionKinds.Automation && !s.SawHumanInput) kind = s.Kind;
        else
        {
            var hasHuman = s.SawHumanInput || c.ScalarLong("SELECT COUNT(*) FROM utterances WHERE session_id=@s", tx, ("@s", sessionId)) > 0;
            if (hasHuman) kind = SessionKinds.Human;
            else if (s.SawAutomationInput) kind = SessionKinds.Automation;
            else kind = (c.Scalar("SELECT kind FROM sessions WHERE id=@s", tx, ("@s", sessionId)) as string) ?? SessionKinds.Human;
        }
        c.Exec("UPDATE sessions SET kind=@k WHERE id=@s", tx, ("@k", kind), ("@s", sessionId));
    }

    /// <summary>
    /// Kullanıcı mesajını yazar. Kanca ile okuyucu çift kaydını önler (sözleşme 11.2):
    /// aynı oturumda ±120 sn içinde kancadan gelmiş aynı (ya da aynı başlangıçlı) metin varsa yeni satır açılmaz, o satır 'both' olur.
    /// Kuyruğa yazılmış mesajın iki kaydı (aynı metin ±300 sn) tek sayılır.
    /// </summary>
    public static long? InsertUtterance(SqliteConnection c, SqliteTransaction? tx, long sessionId, long? projectId, string ts, string text, string source, long line)
    {
        var clean = TextNormalizer.ForHash(SecretFilter.MaskSecrets(text));
        if (clean.Length == 0) return null;
        var hash = TextNormalizer.Hash(clean);
        var t = Time.Parse(ts) ?? Time.UtcNow();
        var lo120 = Time.Iso(t.AddSeconds(-120)); var hi120 = Time.Iso(t.AddSeconds(120));

        if (source == "transcript")
        {
            var prefix = Prefix(clean);
            var hooks = c.Query("SELECT id, hash, text FROM utterances WHERE session_id=@s AND source='hook' AND ts BETWEEN @lo AND @hi",
                r => (id: r.GetInt64(0), hash: r.Str(1), text: r.Str(2) ?? ""), tx, ("@s", sessionId), ("@lo", lo120), ("@hi", hi120));
            foreach (var h in hooks)
            {
                if (h.hash == hash || Prefix(h.text) == prefix)
                {
                    try
                    {
                        c.Exec("UPDATE utterances SET source='both', ts=@ts, source_line=@l WHERE id=@id", tx, ("@ts", ts), ("@l", line), ("@id", h.id));
                    }
                    catch (SqliteException) { c.Exec("UPDATE utterances SET source='both' WHERE id=@id", tx, ("@id", h.id)); }
                    return null;
                }
            }
        }
        else if (source == "hook")
        {
            // Okuyucu zaten yazdıysa (ör. içe alma kancadan önce koştu) ikinciyi açma.
            var existing = c.Scalar("SELECT id FROM utterances WHERE session_id=@s AND hash=@h AND ts BETWEEN @lo AND @hi", tx,
                ("@s", sessionId), ("@h", hash), ("@lo", lo120), ("@hi", hi120));
            if (existing is not null)
            {
                c.Exec("UPDATE utterances SET source='both' WHERE id=@id", tx, ("@id", existing));
                return null;
            }
        }

        var lo300 = Time.Iso(t.AddSeconds(-300)); var hi300 = Time.Iso(t.AddSeconds(300));
        var dup = c.Scalar("SELECT id FROM utterances WHERE session_id=@s AND hash=@h AND ts BETWEEN @lo AND @hi", tx,
            ("@s", sessionId), ("@h", hash), ("@lo", lo300), ("@hi", hi300));
        if (dup is not null) return null;

        var n = c.Exec("INSERT OR IGNORE INTO utterances(session_id, project_id, ts, text, source_line, hash, source, extracted, folded) VALUES(@s,@p,@ts,@x,@l,@h,@src,0,@f)", tx,
            ("@s", sessionId), ("@p", projectId), ("@ts", ts), ("@x", clean), ("@l", line), ("@h", hash), ("@src", source), ("@f", TextNormalizer.Fold(clean)));
        return n == 0 ? null : c.ScalarLong("SELECT last_insert_rowid()", tx);
    }

    private static string Prefix(string text)
    {
        var s = TextNormalizer.Fold(System.Text.RegularExpressions.Regex.Replace(text, "<[^>]{1,200}>", " "));
        s = new string(s.Where(char.IsLetterOrDigit).ToArray());
        return s.Length > 120 ? s[..120] : s;
    }
}
