using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Cluster;
using Soz.Core.Data;
using Soz.Core.Extract;
using Soz.Core.Packet;
using Soz.Core.Readers;
using Soz.Core.Text;

namespace Soz.Core.Hooks;

/// <summary>
/// Claude Code kanca uçları. Giriş stdin JSON (session_id, transcript_path, cwd, hook_event_name, source | prompt);
/// çıktı {"hookSpecificOutput":{"hookEventName":…,"additionalContext":…}} (code.claude.com/docs/en/hooks, 2026-11-10 okundu).
/// Her hata sessizce yutulur: çıkış 0, çıktı yok, ayrıntı veri\soz-hook.log dosyasına.
/// </summary>
public static class HookHandler
{
    public const int MaxContextChars = 10_000;

    private static readonly JsonSerializerOptions OutOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string eventName, TextReader stdin, TextWriter stdout, string dataDir, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        JsonObject? input = null;
        JsonObject? output = null;
        SozDb? db = null;
        SqliteConnection? conn = null;
        Exception? failure = null;
        try
        {
            var raw = stdin.ReadToEnd();
            input = string.IsNullOrWhiteSpace(raw) ? new JsonObject() : JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
            var fast = SozDb.OpenFastForHook(dataDir, busyTimeoutMs: 1500);
            db = fast.Db;
            conn = fast.Conn;
            output = ExecuteOnConnection(eventName, conn, db, input, env, fast.IsCurrentSchema);
            if (output is not null)
            {
                stdout.Write(output.ToJsonString(OutOptions));
                stdout.Flush();
            }
        }
        catch (Exception e)
        {
            failure = e;
            Log(dataDir, eventName, e);
        }
        try
        {
            Pulse(dataDir, db, conn, eventName, input, env, sw.ElapsedMilliseconds, failure);
        }
        finally
        {
            conn?.Dispose();
        }
        return 0;
    }

    /// <summary>Sıcak açık SozDb üzerinde (POST /api/hooks/{eventName}) ~2-5 ms bellek içi kanca yürütmesi.</summary>
    public static string? RunInMemory(string eventName, JsonObject input, SozDb db, Func<string, string?>? env = null)
    {
        env ??= _ => null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        JsonObject? output = null;
        Exception? failure = null;
        using var conn = db.Open();
        try
        {
            output = ExecuteOnConnection(eventName, conn, db, input, env, isCurrentSchema: true);
        }
        catch (Exception e)
        {
            failure = e;
            Log(db.DataDir, eventName, e);
        }
        Pulse(db.DataDir, db, conn, eventName, input, env, sw.ElapsedMilliseconds, failure);
        return output?.ToJsonString(OutOptions);
    }

    private static JsonObject? ExecuteOnConnection(string eventName, SqliteConnection c, SozDb db, JsonObject input, Func<string, string?> env, bool isCurrentSchema)
        => eventName switch
        {
            "session-start" => SessionStart(c, input, env),
            "user-prompt-submit" => UserPromptSubmit(c, input, env),
            "stop" => GateHooks.Stop(c, db, input, env),
            "stop-failure" => GateHooks.StopFailure(c, input, env),
            "notification" => GateHooks.Notification(c, input, env),
            "pre-tool-use" => Soz.Core.Guard.GuardHook.Handle(c, input, env, skipEnsureTables: isCurrentSchema),
            _ => throw new ArgumentException("bilinmeyen kanca: " + eventName),
        };

    public const string PulseKind = "hook_pulse";
    public static readonly string[] HookNames = { "session-start", "user-prompt-submit", "stop", "stop-failure", "notification", "pre-tool-use" };

    /// <summary>Hata sınıfı: veritabanı kilidi ya da iptal "zaman aşımı", geri kalan "hata".</summary>
    public static string ResultOf(Exception? e) => e switch
    {
        null => "ok",
        TimeoutException or OperationCanceledException => "zaman aşımı",
        SqliteException { SqliteErrorCode: 5 or 6 } => "zaman aşımı", // SQLITE_BUSY / SQLITE_LOCKED: busy_timeout doldu
        _ => "hata",
    };

    /// <summary>
    /// Nabız (EK B): her kanca çalışması events'e "hook_pulse" yazar (ad, ms, sonuç, maskeli kısa hata). Yazılamazsa soz-hook.log'a düşer.
    /// Kanca kullanıcının oturumunu asla bozmaz: buradaki her hata yutulur.
    /// </summary>
    private static void Pulse(string dataDir, SozDb? db, SqliteConnection? openConn, string eventName, JsonObject? input, Func<string, string?> env, long ms, Exception? failure)
    {
        var result = ResultOf(failure);
        string? err = failure is null ? null : Short(SecretFilter.MaskSecrets($"{failure.GetType().Name}: {failure.Message}"), 300);
        try
        {
            var ownConn = openConn is null;
            if (ownConn)
            {
                db ??= SozDb.OpenAndMigrate(dataDir, busyTimeoutMs: 1500);
                openConn = db.Open();
            }
            try
            {
                long? pid = null;
                try { if (input is not null) pid = FindProject(openConn!, env("CLAUDE_PROJECT_DIR") ?? Str(input, "cwd")).projectId; } catch (Exception) { }
                Events.Log(openConn!, PulseKind, pid, PulsePayload(eventName, ms, result, err));
            }
            finally
            {
                if (ownConn) openConn?.Dispose();
            }
        }
        catch (Exception e)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                File.AppendAllText(Path.Combine(dataDir, "soz-hook.log"),
                    $"{Time.NowIso()} nabız yazılamadı: {eventName} {ms} ms {result}{(err is null ? "" : " · " + err)} · {e.GetType().Name}: {SecretFilter.MaskSecrets(e.Message)}{Environment.NewLine}");
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Nabız yükü: <c>{"name":…,"ms":…,"result":…,"error":…}</c>. Eskiden anonim tür JsonSerializer.Serialize ile yazılıyordu; o yol ilk kullanımda yansıma kurduğu için
    /// her kancada 15-20 ms tutuyordu. Aynı varsayılan kaçışla (JavaScriptEncoder.Default) doğrudan Utf8JsonWriter ile yazılır; çıktı bayt bayt aynıdır (bir test eski serileştiriciyle karşılaştırır).
    /// </summary>
    internal static string PulsePayload(string name, long ms, string result, string? error)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteNumber("ms", ms);
            w.WriteString("result", result);
            w.WriteString("error", error);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void Log(string dataDir, string eventName, Exception e)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.AppendAllText(Path.Combine(dataDir, "soz-hook.log"), $"{Time.NowIso()} {eventName} {e.GetType().Name}: {SecretFilter.MaskSecrets(e.Message)}{Environment.NewLine}");
        }
        catch (Exception) { /* kanca kullanıcının oturumunu asla bozmaz */ }
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Proje dizini: CLAUDE_PROJECT_DIR → cwd. Kayıtlı proje bulunamazsa üst klasörlere bakılır.</summary>
    public static (long? projectId, string? dir) FindProject(SqliteConnection c, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return (null, null);
        var d = dir;
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(d); i++)
        {
            var id = c.Scalar("SELECT id FROM projects WHERE key=@k", null, ("@k", TextNormalizer.ProjectKey(d)));
            if (id is not null) return (Convert.ToInt64(id), d);
            d = Path.GetDirectoryName(d.TrimEnd('\\', '/'));
        }
        return (null, dir);
    }

    private static JsonObject Out(string eventName, string context) => new()
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = eventName,
            ["additionalContext"] = context.Length > MaxContextChars ? context[..MaxContextChars] : context,
        },
    };

    private static JsonObject? SessionStart(SqliteConnection c, JsonObject input, Func<string, string?> env)
    {
        var dir = env("CLAUDE_PROJECT_DIR") ?? Str(input, "cwd");
        var settings = SozSettings.Load(c);
        var (pid, _) = FindProject(c, dir);
        var packet = PacketBuilder.Build(c, pid, settings.PacketBudget, settings.Language);
        if (packet.Text.Length == 0) return null;
        Events.Log(c, "hook_inject", pid, JsonSerializer.Serialize(new { source = Str(input, "source"), chars = packet.Chars, decisions = packet.DecisionCount }));
        return Out("SessionStart", packet.Text);
    }

    private static JsonObject? UserPromptSubmit(SqliteConnection c, JsonObject input, Func<string, string?> env)
    {
        var prompt = Str(input, "prompt") ?? "";
        var sessionId = Str(input, "session_id");
        var cwd = Str(input, "cwd");
        var dir = env("CLAUDE_PROJECT_DIR") ?? cwd;
        var settings = SozSettings.Load(c);
        var clean = SecretFilter.MaskSecrets(TextNormalizer.CleanUserText(prompt));
        var (pid, _) = FindProject(c, dir);
        GateHooks.OnUserPrompt(c, input, dir); // A: iş kartını oturuma bağla (hata yutulur)

        // 1) Canlı yakalama.
        if (clean.Length > 0 && !string.IsNullOrWhiteSpace(sessionId) && !clean.StartsWith('/'))
        {
            var now = Time.NowIso();
            using var tx = c.BeginTransaction();
            var (sid, spid) = IngestStore.EnsureSession(c, tx, Soz.Core.Agents.AgentRegistry.HookSessionTool, sessionId!, dir, Str(input, "transcript_path"), SessionKinds.Human);
            pid ??= spid;
            IngestStore.InsertUtterance(c, tx, sid, spid, now, clean, "hook", 0);
            c.Exec("""
                UPDATE sessions SET last_user_text=@t, last_user_ts=@ts, last_event_kind='user', last_event_ts=@ts, turn_ended=0,
                  last_at=CASE WHEN last_at IS NULL OR last_at < @ts THEN @ts ELSE last_at END,
                  started_at=COALESCE(started_at, @ts), kind='human'
                WHERE id=@id
                """, tx, ("@t", clean), ("@ts", now), ("@id", sid));
            tx.Commit();
        }

        // 2) Onaylı bir kararın tekrarı mı?
        if (clean.Length == 0) return null;
        var star = c.ScalarLong("SELECT id FROM projects WHERE key='*'");
        var decisions = c.Query("SELECT id, text, repeat_count, project_id FROM decisions WHERE status='approved' AND (project_id=@s OR project_id=@p)",
            r => (id: r.GetInt64(0), text: r.Str(1) ?? "", n: r.Long(2), pid: r.Long(3)), null, ("@s", star), ("@p", pid ?? star));
        decisions = decisions.Where(d => !SecretFilter.ContainsSecret(d.text)).ToList(); // kanca çıktısına sır girmez
        if (decisions.Count == 0) return null;
        var pieces = RuleBasedExtractor.SplitSentences(clean).ToList();
        if (clean.Length <= 300) pieces.Add(clean);
        var feats = pieces.Select(Similarity.Feature).ToList();
        (long id, string text, long n)? best = null;
        double bestScore = 0;
        foreach (var d in decisions)
        {
            var df = Similarity.Feature(d.text);
            foreach (var f in feats)
            {
                var s = Similarity.Score(f, df);
                if (s > bestScore) { bestScore = s; best = (d.id, d.text, d.n); }
            }
        }
        if (best is null || bestScore < settings.RepeatThreshold) return null;
        var msg = Strings.Get(settings.Language, "hook.repeat").Replace("{n}", Math.Max(1, best.Value.n).ToString()).Replace("{text}", best.Value.text);
        Events.Log(c, "repeat_alert", pid, JsonSerializer.Serialize(new { decisionId = best.Value.id, score = Math.Round(bestScore, 3) }));
        return Out("UserPromptSubmit", msg);
    }
}
