using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Soz.Core.Tests;

/// <summary>
/// SENTETİK ajan veritabanları ("GERCEK KAYIT DEGIL"). Şemalar indirilmiş kaynak koddan alınmıştır:
///   Hermes: calisma\repo-inceleme\cok-ajan\hermes-agent\hermes_state_common.py (SCHEMA_VERSION 33; sessions ve messages tablolarının TÜM sütunları).
///   OpenCode 2.x: opencode\packages\core\src\database\schema.gen.ts (session_v2, session_message, session_part; yabancı anahtar satırları çıkarıldı).
///   OpenCode 1.x: ...\migration\20260127222353_familiar_lady_ursula.ts (session, message, part; yabancı anahtar satırları çıkarıldı).
/// Kemal'in gerçek hermes-home, opencode ve claude/codex klasörlerine hiçbir yerde dokunulmaz; her şey urun\_gecici\d2f altında üretilir.
/// </summary>
internal static class SahteAracVeritabani
{
    public const string Etiket = "GERCEK KAYIT DEGIL: sentetik test verisi";

    public static string YeniKlasor(string ad)
    {
        var d = Path.Combine(TestEnv.RepoRoot, "_gecici", "d2f", "veri", ad + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "GERCEK-KAYIT-DEGIL.txt"), Etiket);
        return d;
    }

    public static SqliteConnection Ac(string dosya, bool wal = false, bool havuz = false)
    {
        var c = new SqliteConnection($"Data Source={dosya};Pooling={havuz}");
        c.Open();
        if (wal) Exec(c, "PRAGMA journal_mode=WAL");
        return c;
    }

    public static int Exec(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    private static byte[] Oku(string dosya)
    {
        using var f = new FileStream(dosya, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream(); f.CopyTo(ms); return ms.ToArray();
    }
    public static string Ozet(string dosya) => File.Exists(dosya) ? Convert.ToHexString(SHA256.HashData(Oku(dosya))) : "(yok)";

    public static string Dizin(string klasor) => string.Join("|", Directory.GetFiles(klasor).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));

    public static double Epoch(string utc) => (DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal) - DateTime.UnixEpoch).TotalSeconds;
    public static long EpochMs(string utc) => (long)(Epoch(utc) * 1000);

    // ---------------- Hermes ----------------

    public const string HermesSemasi = """
        CREATE TABLE schema_version (version INTEGER NOT NULL);
        CREATE TABLE system_prompts (hash TEXT PRIMARY KEY, prompt TEXT NOT NULL);
        CREATE TABLE sessions (
            id TEXT PRIMARY KEY, runtime_revision INTEGER NOT NULL DEFAULT 0, runtime_generation INTEGER NOT NULL DEFAULT 0, source TEXT NOT NULL,
            created_source TEXT, user_id TEXT, session_key TEXT, chat_id TEXT, chat_type TEXT, thread_id TEXT, display_name TEXT, origin_json TEXT,
            expiry_finalized INTEGER DEFAULT 0, model TEXT, model_config TEXT, system_prompt TEXT, system_prompt_hash TEXT, parent_session_id TEXT,
            started_at REAL NOT NULL, ended_at REAL, end_reason TEXT, message_count INTEGER DEFAULT 0, tool_call_count INTEGER DEFAULT 0,
            input_tokens INTEGER DEFAULT 0, output_tokens INTEGER DEFAULT 0, cache_read_tokens INTEGER DEFAULT 0, cache_write_tokens INTEGER DEFAULT 0,
            reasoning_tokens INTEGER DEFAULT 0, cwd TEXT, git_branch TEXT, git_repo_root TEXT, git_metadata_generation INTEGER NOT NULL DEFAULT 0,
            billing_provider TEXT, billing_base_url TEXT, billing_mode TEXT, estimated_cost_usd REAL, actual_cost_usd REAL, cost_status TEXT,
            cost_source TEXT, pricing_version TEXT, title TEXT, title_source TEXT, last_activity_at REAL, last_activity_description TEXT,
            last_activity_provenance TEXT, api_call_count INTEGER DEFAULT 0, handoff_state TEXT, handoff_platform TEXT, handoff_error TEXT,
            compression_failure_cooldown_until REAL, compression_failure_error TEXT, compression_fallback_streak INTEGER NOT NULL DEFAULT 0,
            compression_ineffective_count INTEGER NOT NULL DEFAULT 0, compression_recovery_deadline REAL, compression_overload_streak INTEGER NOT NULL DEFAULT 0,
            profile_name TEXT, transport_profile TEXT, rewind_count INTEGER NOT NULL DEFAULT 0, archived INTEGER NOT NULL DEFAULT 0,
            auto_archived INTEGER NOT NULL DEFAULT 0, pinned INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0, last_read_at REAL,
            tool_names TEXT, transcript_revision INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE messages (
            id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL REFERENCES sessions(id), role TEXT NOT NULL, content TEXT, tool_call_id TEXT,
            tool_calls TEXT, tool_name TEXT, effect_disposition TEXT, timestamp REAL NOT NULL, token_count INTEGER, finish_reason TEXT, reasoning TEXT,
            reasoning_content TEXT, reasoning_details TEXT, codex_reasoning_items TEXT, codex_message_items TEXT, platform_message_id TEXT,
            observed INTEGER DEFAULT 0, _compressed_summary INTEGER NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1, compacted INTEGER NOT NULL DEFAULT 0,
            api_content TEXT, display_kind TEXT, display_metadata TEXT, display_identity BLOB, display_order INTEGER, message_uid TEXT,
            absorbed_message_uids TEXT, tool_call_uids TEXT, tool_call_uid TEXT
        );
        """;

    public static void HermesKur(SqliteConnection c, int surum = 33)
    {
        foreach (var stmt in HermesSemasi.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Exec(c, stmt);
        Exec(c, "INSERT INTO schema_version(version) VALUES(@v)", ("@v", surum));
        Exec(c, "INSERT INTO system_prompts(hash, prompt) VALUES('etiket', @e)", ("@e", Etiket));
    }

    public static void HermesOturum(SqliteConnection c, string id, string baslangic, string? bitis = null, string? neden = null, string? ust = null, string kaynak = "cli",
        string? cwd = null, string? baslik = null, string? sonEtkinlik = null, int mesaj = 0)
        => Exec(c, "INSERT INTO sessions(id, source, parent_session_id, started_at, ended_at, end_reason, message_count, cwd, title, last_activity_at) VALUES(@i,@s,@p,@st,@e,@r,@m,@c,@t,@l)",
            ("@i", id), ("@s", kaynak), ("@p", ust), ("@st", Epoch(baslangic)), ("@e", bitis is null ? null : Epoch(bitis)), ("@r", neden), ("@m", mesaj), ("@c", cwd), ("@t", baslik),
            ("@l", sonEtkinlik is null ? null : Epoch(sonEtkinlik)));

    public static void HermesMesaj(SqliteConnection c, string oturum, string rol, string? icerik, string zaman, bool aracCagrisi = false, string? bitisNedeni = null)
        => Exec(c, "INSERT INTO messages(session_id, role, content, tool_calls, timestamp, finish_reason) VALUES(@s,@r,@c,@tc,@ts,@f)",
            ("@s", oturum), ("@r", rol), ("@c", icerik), ("@tc", aracCagrisi ? "[{\"id\":\"c1\",\"function\":{\"name\":\"terminal\"}}]" : null), ("@ts", Epoch(zaman)), ("@f", bitisNedeni));

    // ---------------- OpenCode ----------------

    public const string OpenCode2Semasi = """
        CREATE TABLE session_v2 (
          id text PRIMARY KEY, project_id text NOT NULL, workspace_id text, parent_id text, fork_session_id text, fork_boundary text, slug text NOT NULL,
          directory text NOT NULL, path text, title text, version text NOT NULL, share_url text, summary_additions integer, summary_deletions integer,
          summary_files integer, summary_diffs text, metadata text, cost real DEFAULT 0 NOT NULL, tokens_input integer DEFAULT 0 NOT NULL,
          tokens_output integer DEFAULT 0 NOT NULL, tokens_reasoning integer DEFAULT 0 NOT NULL, tokens_cache_read integer DEFAULT 0 NOT NULL,
          tokens_cache_write integer DEFAULT 0 NOT NULL, revert text, permission text, agent text, model text, time_created integer NOT NULL,
          time_updated integer NOT NULL, time_busy integer, time_idle integer, time_viewed integer, idle_outcome text, time_compacting integer,
          time_archived integer, time_suspended integer, resume_attempts integer DEFAULT 0 NOT NULL
        );
        CREATE TABLE session_message (
          id text PRIMARY KEY, session_id text NOT NULL, type text NOT NULL, seq integer NOT NULL, time_created integer NOT NULL, time_updated integer NOT NULL, data text NOT NULL
        );
        CREATE UNIQUE INDEX session_message_session_seq_idx ON session_message (session_id, seq);
        CREATE TABLE session_part (
          message_id text NOT NULL, session_id text NOT NULL, ordinal integer NOT NULL, type text NOT NULL, call_id text,
          time_created integer NOT NULL, time_updated integer NOT NULL, data text NOT NULL, CONSTRAINT session_part_pk PRIMARY KEY(message_id, ordinal)
        );
        """;

    public const string OpenCode1Semasi = """
        CREATE TABLE message (id text PRIMARY KEY, session_id text NOT NULL, time_created integer NOT NULL, time_updated integer NOT NULL, data text NOT NULL);
        CREATE TABLE part (id text PRIMARY KEY, message_id text NOT NULL, session_id text NOT NULL, time_created integer NOT NULL, time_updated integer NOT NULL, data text NOT NULL);
        CREATE TABLE session (
          id text PRIMARY KEY, project_id text NOT NULL, parent_id text, slug text NOT NULL, directory text NOT NULL, title text NOT NULL, version text NOT NULL,
          share_url text, summary_additions integer, summary_deletions integer, summary_files integer, summary_diffs text, revert text, permission text,
          time_created integer NOT NULL, time_updated integer NOT NULL, time_compacting integer, time_archived integer
        );
        """;

    public static void OpenCodeKur(SqliteConnection c, int aile)
    {
        foreach (var stmt in (aile == 2 ? OpenCode2Semasi : OpenCode1Semasi).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Exec(c, stmt);
    }

    public static void OpenCode2Oturum(SqliteConnection c, string id, string dizin, string olusma, string guncelleme, string? baslik = null, string? ust = null, string? mesgul = null, string? bosta = null, string? arsiv = null)
        => Exec(c, "INSERT INTO session_v2(id, project_id, parent_id, slug, directory, title, version, time_created, time_updated, time_busy, time_idle, time_archived) VALUES(@i,'GERCEK-KAYIT-DEGIL',@p,@sl,@d,@t,'2.0.0-sahte',@c,@u,@b,@id,@a)",
            ("@i", id), ("@p", ust), ("@sl", id), ("@d", dizin), ("@t", baslik), ("@c", EpochMs(olusma)), ("@u", EpochMs(guncelleme)),
            ("@b", mesgul is null ? null : EpochMs(mesgul)), ("@id", bosta is null ? null : EpochMs(bosta)), ("@a", arsiv is null ? null : EpochMs(arsiv)));

    private static int _seq;
    public static void OpenCode2Kullanici(SqliteConnection c, string oturum, string metin, string zaman)
    {
        var seq = ++_seq;
        Exec(c, "INSERT INTO session_message(id, session_id, type, seq, time_created, time_updated, data) VALUES(@i,@s,'user',@q,@t,@t,@d)",
            ("@i", "msg_u" + seq), ("@s", oturum), ("@q", seq), ("@t", EpochMs(zaman)), ("@d", System.Text.Json.JsonSerializer.Serialize(new { text = metin })));
    }

    public static void OpenCode2AsistanMetni(SqliteConnection c, string oturum, string metin, string zaman)
    {
        var seq = ++_seq;
        Exec(c, "INSERT INTO session_message(id, session_id, type, seq, time_created, time_updated, data) VALUES(@i,@s,'assistant',@q,@t,@t,'{}')", ("@i", "msg_a" + seq), ("@s", oturum), ("@q", seq), ("@t", EpochMs(zaman)));
        Exec(c, "INSERT INTO session_part(message_id, session_id, ordinal, type, time_created, time_updated, data) VALUES(@i,@s,0,'text',@t,@t,@d)",
            ("@i", "msg_a" + seq), ("@s", oturum), ("@t", EpochMs(zaman)), ("@d", System.Text.Json.JsonSerializer.Serialize(new { type = "text", text = metin })));
    }

    public static void OpenCode1Oturum(SqliteConnection c, string id, string dizin, string olusma, string guncelleme, string baslik = "baslik", string? ust = null)
        => Exec(c, "INSERT INTO session(id, project_id, parent_id, slug, directory, title, version, time_created, time_updated) VALUES(@i,'GERCEK-KAYIT-DEGIL',@p,@i,@d,@t,'1.4.7-sahte',@c,@u)",
            ("@i", id), ("@p", ust), ("@d", dizin), ("@t", baslik), ("@c", EpochMs(olusma)), ("@u", EpochMs(guncelleme)));

    public static void OpenCode1Mesaj(SqliteConnection c, string oturum, string rol, string metin, string zaman, bool tamamlandi)
    {
        var n = ++_seq;
        var t = EpochMs(zaman);
        var data = rol == "assistant"
            ? System.Text.Json.JsonSerializer.Serialize(new { role = rol, time = new { created = t, completed = tamamlandi ? (long?)t : null } })
            : System.Text.Json.JsonSerializer.Serialize(new { role = rol, time = new { created = t } });
        Exec(c, "INSERT INTO message(id, session_id, time_created, time_updated, data) VALUES(@i,@s,@t,@t,@d)", ("@i", "m" + n), ("@s", oturum), ("@t", t), ("@d", data));
        Exec(c, "INSERT INTO part(id, message_id, session_id, time_created, time_updated, data) VALUES(@i,@m,@s,@t,@t,@d)",
            ("@i", "p" + n), ("@m", "m" + n), ("@s", oturum), ("@t", t), ("@d", System.Text.Json.JsonSerializer.Serialize(new { type = "text", text = metin })));
    }
}
