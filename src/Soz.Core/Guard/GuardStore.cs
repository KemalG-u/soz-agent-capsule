using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Text;

namespace Soz.Core.Guard;

public sealed record GuardEventRow(long Id, string? At, long? ProjectId, string? ProjectName, string? SessionId, string? Tool, string? Target, long? RuleId, string? RuleLabel, string? Action, string? Reason);
public sealed record TemplateApplyResult(string Template, List<GuardRule> Created, List<GuardRule> Existing);

/// <summary>guard_rules ve guard_events erişimi. Tablolar sözleşme bölüm 3 alan adlarıyla; göçle aynı DDL (IF NOT EXISTS).</summary>
public static class GuardStore
{
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS guard_rules(id INTEGER PRIMARY KEY, project_id INTEGER NULL, kind TEXT, label TEXT, spec_json TEXT, action TEXT, enabled INTEGER DEFAULT 1, decision_id INTEGER NULL, origin TEXT DEFAULT 'user', created_at TEXT);
        CREATE TABLE IF NOT EXISTS guard_events(id INTEGER PRIMARY KEY, at TEXT, project_id INTEGER, tool_session_id TEXT, tool TEXT, target_summary TEXT, rule_id INTEGER, action TEXT, reason TEXT);
        CREATE INDEX IF NOT EXISTS ix_guard_events_at ON guard_events(at);
        """;

    public static void EnsureTables(SqliteConnection c) => c.Exec(Ddl);

    private static GuardRule Map(SqliteDataReader r)
    {
        JsonObject spec;
        try { spec = JsonNode.Parse(r.Str(4) ?? "{}") as JsonObject ?? new JsonObject(); }
        catch (JsonException) { spec = new JsonObject(); }
        return new GuardRule(r.GetInt64(0), r.LongN(1), r.Str(2) ?? "", r.Str(3) ?? "", spec, r.Str(5) ?? "deny", r.Long(6) != 0, r.LongN(7), r.Str(8) ?? "user", r.Str(9), r.Str(10));
    }

    private const string Cols = "id, project_id, kind, label, spec_json, action, enabled, decision_id, origin, created_at, uid";

    /// <summary>Kancanın kullandığı: etkin kurallar; proje kuralları + genel (project_id NULL ya da '*' projesi).</summary>
    public static List<GuardRule> RulesFor(SqliteConnection c, long? projectId)
    {
        var star = c.ScalarLong("SELECT id FROM projects WHERE key='*'");
        return c.Query($"SELECT {Cols} FROM guard_rules WHERE enabled=1 AND (project_id IS NULL OR project_id=@s OR project_id=@p) ORDER BY id", Map, null,
            ("@s", star), ("@p", projectId ?? -1));
    }

    public static List<GuardRule> List(SqliteConnection c, long? projectId)
    {
        if (projectId is null) return c.Query($"SELECT {Cols} FROM guard_rules ORDER BY id", Map);
        var star = c.ScalarLong("SELECT id FROM projects WHERE key='*'");
        return c.Query($"SELECT {Cols} FROM guard_rules WHERE project_id IS NULL OR project_id=@s OR project_id=@p ORDER BY id", Map, null, ("@s", star), ("@p", projectId));
    }

    public static GuardRule? Get(SqliteConnection c, long id)
        => c.Query($"SELECT {Cols} FROM guard_rules WHERE id=@i", Map, null, ("@i", id)).FirstOrDefault();

    public static string? ValidateSpec(string kind, string action, JsonObject spec)
    {
        if (!GuardKinds.All.Contains(kind)) return "kind şunlardan biri olmalı: " + string.Join(", ", GuardKinds.All);
        if (action is not ("deny" or "ask")) return "action deny ya da ask olmalı.";
        switch (kind)
        {
            case GuardKinds.PathProtect:
                if (GuardEngine.SpecList(spec, "globs").Count == 0) return "path_protect için spec.globs (en az bir yol kalıbı) gerekli.";
                break;
            case GuardKinds.CommandBlock:
                if (GuardEngine.SpecList(spec, "patterns").Count == 0) return "command_block için spec.patterns (en az bir komut kalıbı) gerekli.";
                break;
            case GuardKinds.AskBefore:
                if (GuardEngine.SpecList(spec, "patterns").Count == 0 && GuardEngine.SpecList(spec, "globs").Count == 0) return "ask_before için spec.patterns ya da spec.globs gerekli.";
                break;
        }
        foreach (var p in GuardEngine.SpecList(spec, "patterns"))
            if (p.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            {
                try { _ = new System.Text.RegularExpressions.Regex(p[3..]); }
                catch (ArgumentException e) { return "Geçersiz regex kalıbı: " + e.Message; }
            }
        return null;
    }

    public static GuardRule Create(SqliteConnection c, long? projectId, string kind, string? label, JsonObject spec, string? action, bool enabled = true, long? decisionId = null, string origin = "user")
    {
        action ??= kind == GuardKinds.AskBefore ? "ask" : "deny";
        var err = ValidateSpec(kind, action, spec);
        if (err is not null) throw new SozException(400, err);
        if (origin is not ("template" or "user" or "defter")) origin = "user";
        var lbl = string.IsNullOrWhiteSpace(label) ? DefaultLabel(kind, spec) : label.Trim();
        c.Exec("INSERT INTO guard_rules(project_id, kind, label, spec_json, action, enabled, decision_id, origin, created_at, uid) VALUES(@p,@k,@l,@s,@a,@e,@d,@o,@t,@u)", null,
            ("@u", Uid.New()), ("@p", projectId), ("@k", kind), ("@l", lbl), ("@s", spec.ToJsonString()), ("@a", action), ("@e", enabled ? 1 : 0), ("@d", decisionId), ("@o", origin), ("@t", Time.NowIso()));
        return Get(c, c.ScalarLong("SELECT last_insert_rowid()"))!;
    }

    private static string DefaultLabel(string kind, JsonObject spec) => kind switch
    {
        GuardKinds.ScopeOnly => "Proje dışına yazma yasak",
        GuardKinds.PathProtect => "Korunan yollar: " + string.Join(", ", GuardEngine.SpecList(spec, "globs").Take(2)),
        _ => "Komut kuralı: " + string.Join(", ", GuardEngine.SpecList(spec, "patterns").Take(2)),
    };

    public static GuardRule Patch(SqliteConnection c, long id, string? label, JsonObject? spec, string? action, bool? enabled)
    {
        var r = Get(c, id) ?? throw new SozException(404, "Kural bulunamadı.");
        var nspec = spec ?? r.Spec;
        var nact = action ?? r.Action;
        var err = ValidateSpec(r.Kind, nact, nspec);
        if (err is not null) throw new SozException(400, err);
        var nenabled = enabled ?? r.Enabled;
        c.Exec("UPDATE guard_rules SET label=@l, spec_json=@s, action=@a, enabled=@e WHERE id=@i", null,
            ("@l", string.IsNullOrWhiteSpace(label) ? r.Label : label.Trim()), ("@s", nspec.ToJsonString()), ("@a", nact), ("@e", nenabled ? 1 : 0), ("@i", id));
        var saved = Get(c, id) ?? throw new SozException(404, "Kural bulunamadı.");
        // istenen ile kaydedilen aynı olmalı; değilse başarı dönülmez
        if (saved.Action != nact || saved.Enabled != nenabled)
            throw new SozException(409, "Değişiklik kaydedilemedi: kural istenen durumda saklanmadı. Hiçbir şey değişmiş sayılmamalı, sayfayı yenileyip tekrar dene.");
        return saved;
    }

    private static readonly string[] PatchFields = { "label", "spec", "action", "enabled" };
    // arayüzün geri gönderebileceği, değeri değişmedikçe zararsız alanlar
    private static readonly string[] EchoFields = { "id", "projectName", "createdAt", "sentence", "kind", "projectId", "origin", "decisionId" };

    private static string? BodyStr(JsonObject b, string key)
    {
        if (b[key] is null) return null;
        if (b[key] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        throw new SozException(400, $"«{key}» metin olmalı.");
    }

    private static long? AsLong(JsonValue v)
        => v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : null;

    private static bool? BodyBool(JsonObject b, string key)
    {
        if (b[key] is null) return null;
        if (b[key] is JsonValue v && v.TryGetValue<bool>(out var x)) return x;
        throw new SozException(400, $"«{key}» doğru ya da yanlış olmalı.");
    }

    private static JsonObject? BodySpec(JsonObject b)
    {
        if (b["spec"] is null) return null;
        return b["spec"] as JsonObject ?? throw new SozException(400, "«spec» bir nesne olmalı.");
    }

    /// <summary>PATCH gövdesini sıkı işler: bilinmeyen alan, yanlış türde değer, değiştirilemeyen alanın değişimi ve boş istek sessizce yutulmaz, açık 400 döner.</summary>
    public static GuardRule PatchFromBody(SqliteConnection c, long id, JsonObject b)
    {
        var r = Get(c, id) ?? throw new SozException(404, "Kural bulunamadı.");
        foreach (var kv in b)
        {
            if (PatchFields.Contains(kv.Key)) continue;
            if (!EchoFields.Contains(kv.Key)) throw new SozException(400, $"Bilinmeyen alan: «{kv.Key}». Değiştirilebilenler: etiket (label), kapsam (spec), sor/durdur (action), açık/kapalı (enabled).");
        }
        // değiştirilemeyen alan geri gönderilebilir ama değeri aynı olmalı
        if (BodyStr(b, "kind") is { } k && k != r.Kind) throw new SozException(400, "Kuralın türü sonradan değiştirilemez. Yeni bir kural ekleyip bunu sil.");
        if (b["origin"] is not null && BodyStr(b, "origin") != r.Origin) throw new SozException(400, "Kuralın kaynağı sonradan değiştirilemez.");
        if (b["projectId"] is JsonValue pv)
        {
            long? np = AsLong(pv);
            if (np != r.ProjectId) throw new SozException(400, "Kural başka projeye taşınamaz. Yeni bir kural ekleyip bunu sil.");
        }
        else if (b.ContainsKey("projectId") && r.ProjectId is not null) throw new SozException(400, "Kural başka projeye taşınamaz. Yeni bir kural ekleyip bunu sil.");

        var label = BodyStr(b, "label");
        if (b.ContainsKey("label") && string.IsNullOrWhiteSpace(label)) throw new SozException(400, "Etiket boş olamaz.");
        var action = BodyStr(b, "action");
        if (b.ContainsKey("action") && action is not ("deny" or "ask")) throw new SozException(400, "action deny (durdur) ya da ask (önce sor) olmalı.");
        var enabled = BodyBool(b, "enabled");
        var spec = BodySpec(b);
        if (label is null && action is null && enabled is null && spec is null) throw new SozException(400, "Değiştirilecek bir alan verilmedi (label, spec, action ya da enabled).");
        return Patch(c, id, label, spec, action, enabled);
    }

    /// <summary>POST gövdesini sıkı işler (PATCH ile aynı ilke): yanlış türde değer, bilinmeyen kaynak ve olmayan proje sessizce yutulmaz.</summary>
    public static GuardRule CreateFromBody(SqliteConnection c, JsonObject b)
    {
        var kind = BodyStr(b, "kind") ?? throw new SozException(400, "kind gerekli.");
        var spec = b["spec"] is null ? new JsonObject() : BodySpec(b)!;
        long? projectId = null;
        if (b["projectId"] is { } pn)
        {
            if (pn is not JsonValue pv || AsLong(pv) is not { } pid) throw new SozException(400, "«projectId» sayı olmalı.");
            if (c.Scalar("SELECT id FROM projects WHERE id=@i", null, ("@i", pid)) is null) throw new SozException(404, "Proje bulunamadı.");
            projectId = pid;
        }
        var action = BodyStr(b, "action");
        if (b.ContainsKey("action") && b["action"] is not null && action is not ("deny" or "ask")) throw new SozException(400, "action deny (durdur) ya da ask (önce sor) olmalı.");
        var origin = BodyStr(b, "origin") ?? "user";
        if (origin is not ("template" or "user" or "defter")) throw new SozException(400, "origin template, user ya da defter olmalı.");
        long? decisionId = null;
        if (b["decisionId"] is JsonValue dv) decisionId = AsLong(dv) ?? throw new SozException(400, "«decisionId» sayı olmalı.");
        return Create(c, projectId, kind, BodyStr(b, "label"), spec, action, BodyBool(b, "enabled") ?? true, decisionId, origin);
    }

    public static bool Delete(SqliteConnection c, long id) => c.Exec("DELETE FROM guard_rules WHERE id=@i", null, ("@i", id)) > 0;

    /// <summary>Şablonu projeye uygular; aynı etiketli şablon kuralı varsa yenisini açmaz (yeniden etkinleştirir).</summary>
    public static TemplateApplyResult ApplyTemplate(SqliteConnection c, long projectId, string templateId)
    {
        var t = GuardTemplates.Find(templateId) ?? throw new SozException(404, "Şablon bulunamadı: " + templateId + ". Geçerli şablonlar: " + string.Join(", ", GuardTemplates.All.Select(x => x.Id)));
        var res = new TemplateApplyResult(t.Id, new(), new());
        foreach (var tr in t.Rules)
        {
            var ex = c.Query($"SELECT {Cols} FROM guard_rules WHERE project_id=@p AND origin='template' AND label=@l", Map, null, ("@p", projectId), ("@l", tr.Label)).FirstOrDefault();
            if (ex is not null)
            {
                if (!ex.Enabled) ex = Patch(c, ex.Id, null, null, null, true);
                res.Existing.Add(ex);
            }
            else res.Created.Add(Create(c, projectId, tr.Kind, tr.Label, (JsonObject)tr.Spec.DeepClone(), tr.Action, true, null, "template"));
        }
        return res;
    }

    // ---------------------------------------------------------------- olaylar
    public static long AddEvent(SqliteConnection c, long? projectId, string? sessionId, string tool, string target, GuardRule? rule, string action, string? reason)
    {
        var summary = TextNormalizer.Truncate(SecretFilter.MaskSecrets(target).ReplaceLineEndings(" "), 200);
        c.Exec("INSERT INTO guard_events(at, project_id, tool_session_id, tool, target_summary, rule_id, action, reason) VALUES(@a,@p,@s,@t,@x,@r,@ac,@re)", null,
            ("@a", Time.NowIso()), ("@p", projectId), ("@s", sessionId), ("@t", tool), ("@x", summary), ("@r", rule?.Id), ("@ac", action), ("@re", reason));
        return c.ScalarLong("SELECT last_insert_rowid()");
    }

    public static List<GuardEventRow> Events(SqliteConnection c, int days, long? projectId, int limit = 200)
    {
        var since = Time.Iso(Time.UtcNow().AddDays(-Math.Max(1, days)));
        return c.Query("""
            SELECT e.id, e.at, e.project_id, p.name, e.tool_session_id, e.tool, e.target_summary, e.rule_id, r.label, e.action, e.reason
            FROM guard_events e LEFT JOIN projects p ON p.id=e.project_id LEFT JOIN guard_rules r ON r.id=e.rule_id
            WHERE e.at >= @s AND (@p IS NULL OR e.project_id=@p) ORDER BY e.id DESC LIMIT @l
            """,
            r => new GuardEventRow(r.GetInt64(0), r.Str(1), r.LongN(2), r.Str(3), r.Str(4), r.Str(5), r.Str(6), r.LongN(7), r.Str(8), r.Str(9), r.Str(10)), null,
            ("@s", since), ("@p", projectId), ("@l", limit));
    }

    public static object Dto(GuardRule r, string? projectName = null) => new
    {
        id = r.Id, projectId = r.ProjectId, projectName, kind = r.Kind, label = r.Label, spec = r.Spec, action = r.Action,
        enabled = r.Enabled, decisionId = r.DecisionId, origin = r.Origin, createdAt = r.CreatedAt, sentence = GuardText.Describe(r),
    };
}
