using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Cluster;
using Soz.Core.Data;
using Soz.Core.Export;
using Soz.Core.Extract;
using Soz.Core.Packet;
using Soz.Core.Readers;
using Soz.Core.Report;
using Soz.Core.Sessions;
using Soz.Core.Text;

namespace Soz.Core;

// ---------- API biçimleri (sözleşme 5, 10; JSON camelCase) ----------
public sealed record SourceInfo(string Tool, string? Root, bool Found, int Files, int Unrecognized, string? Origin = null);
public sealed record SummaryDto(long Projects, long Sessions, long Utterances, long Proposed, long Approved, long RepeatClusters, string? LastImportAt,
    List<SourceInfo> Sources, int Waiting, int Working, int Stalled);
public sealed record ProjectDto(long Id, string Name, string? Path, long Utterances, long Proposed, long Approved, long Repeats, string? LastAt, string? Uid = null);
public sealed record OriginDto(string Tool, string? Ts, long SessionId);
public sealed record DecisionDto(long Id, long ProjectId, string ProjectName, string Type, string Text, string Status, bool Pinned, long RepeatCount,
    long SessionCount, string? CreatedAt, string? UpdatedAt, OriginDto? Origin, string? Uid = null);
public sealed record MemberDto(long UtteranceId, string Text, string? Ts, string Tool, long SessionId);
public sealed record RepeatDto(long ClusterId, long ProjectId, string ProjectName, string Canonical, long Count, long SessionCount, string? FirstTs, string? LastTs,
    long? DecisionId, List<MemberDto> Members);
public sealed record UtteranceDto(long Id, string Text, string? Ts, string Tool, string? ProjectName, long SessionId);
public sealed record UtterancePage(long Total, List<UtteranceDto> Items);
public sealed record PacketDto(string Text, int Chars, int DecisionCount);
public sealed record SessionDto(long Id, string Tool, long? ProjectId, string? ProjectName, string? Title, string Kind, string State, string? Since,
    string? LastUserText, string? LastUserTs, string? LastAssistantText, string? LastAssistantTs, bool AsksQuestion, bool ClaimsDone, bool ShowsEvidence, int SubCount,
    string? StateSource = null, string? StateConfidence = null, string? EndClass = null)
{
    /// <summary>Kısa oturum özeti (Sessions/Ozet). Yalnız sunucunun /api/sessions ucu doldurur; henüz hazır değilse null.</summary>
    public Soz.Core.Sessions.Ozet.OturumOzetiKisa? Ozet { get; set; }
}
public sealed record DigestDto(string Text, int Waiting, int Working, int Idle, int Stalled);
public sealed record ExportResult(bool Ok, string Message, List<string> Files);

public sealed class SozException : Exception
{
    public int Status { get; }
    public SozException(int status, string message) : base(message) => Status = status;
}

/// <summary>CLI ve sunucunun ortak iş katmanı.</summary>
public sealed class SozService
{
    public SozDb Db { get; }
    public SozService(SozDb db) { Db = db; Attention = new Soz.Core.Attention.AttentionService(db); Profile = new Soz.Core.Profile.ProfileService(db); }

    /// <summary>Profil motorunun tek paylaşımlı örneği: /api/profile, bekleyen listesinin dış oturum beslemesi ve kanca haberleri aynı canlı önbelleği ve aynı yoklama kurallarını (Profile.Cadence) kullanır.</summary>
    public Soz.Core.Profile.ProfileService Profile { get; }

    /// <summary>"Sıra bende" listesinin tek paylaşımlı örneği: /api/attention, kapsül ve telefon aynı öncelik tablosunu ve aynı dikkat defterini bunun üzerinden okur.</summary>
    public Soz.Core.Attention.AttentionService Attention { get; }
    public static SozService Open(string dataDir) => new(SozDb.OpenAndMigrate(dataDir));

    public SozSettings Settings() => SozSettings.Load(Db);
    private SqliteConnection Conn() => Db.Open();
    private static long Star(SqliteConnection c) => c.ScalarLong("SELECT id FROM projects WHERE key='*'");

    // ---------- İçe alma ----------
    public ImportSummary Import(ImportOptions? opt = null)
    {
        var sum = Importer.Run(Db, opt);
        using var c = Conn();
        SozSettings.SetInternal(c, "_lastImportSources", JsonSerializer.Serialize(sum.Sources.Select(s => new SourceInfo(s.Tool, s.Root, s.Found, s.Files + s.SubagentFiles, s.Unrecognized))));
        return sum;
    }

    // ---------- Özet ----------
    public SummaryDto Summary(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? Time.UtcNow();
        using var c = Conn();
        var st = SozSettings.Load(c);
        var cards = SessionQueries.Cards(c, st.WaitingStaleHours, false, StateThresholds.From(st), now);
        return new SummaryDto(
            c.ScalarLong("SELECT COUNT(*) FROM projects WHERE key<>'*' AND id IN (SELECT DISTINCT project_id FROM sessions WHERE kind='human')"),
            c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE kind='human'"),
            c.ScalarLong("SELECT COUNT(*) FROM utterances"),
            c.ScalarLong("SELECT COUNT(*) FROM decisions WHERE status='proposed'"),
            c.ScalarLong("SELECT COUNT(*) FROM decisions WHERE status='approved'"),
            c.ScalarLong("SELECT COUNT(*) FROM clusters WHERE parent_id IS NULL AND session_count >= 2"),
            SozSettings.GetInternal(c, "_lastImportAt"),
            Sources(c, st),
            cards.Count(x => x.State == SessionStates.Waiting),
            cards.Count(x => x.State == SessionStates.Working),
            cards.Count(x => x.State == SessionStates.Stalled));
    }

    private static List<SourceInfo> Sources(SqliteConnection c, SozSettings st)
    {
        var last = new Dictionary<string, SourceInfo>();
        var raw = SozSettings.GetInternal(c, "_lastImportSources");
        if (raw is not null)
        {
            try
            {
                foreach (var s in JsonSerializer.Deserialize<List<SourceInfo>>(raw) ?? new()) last[s.Tool] = s;
            }
            catch (JsonException) { }
        }
        var list = new List<SourceInfo>();
        foreach (var (r, _) in SourceLocator.FileSources(st))
        {
            last.TryGetValue(r.Tool, out var l);
            var files = (int)c.ScalarLong("SELECT COUNT(*) FROM file_state WHERE tool=@t", null, ("@t", r.Tool));
            list.Add(new SourceInfo(r.Tool, r.Root, r.Exists, files, l?.Unrecognized ?? 0, r.Origin));
        }
        return list;
    }

    // ---------- Projeler ----------
    public List<ProjectDto> Projects()
    {
        using var c = Conn();
        return c.Query("""
            SELECT p.id, p.name, p.path, p.key,
              (SELECT COUNT(*) FROM utterances u WHERE u.project_id=p.id),
              (SELECT COUNT(*) FROM decisions d WHERE d.project_id=p.id AND d.status='proposed'),
              (SELECT COUNT(*) FROM decisions d WHERE d.project_id=p.id AND d.status='approved'),
              (SELECT COUNT(*) FROM clusters k WHERE k.project_id=p.id AND k.parent_id IS NULL AND k.session_count>=2),
              (SELECT MAX(s.last_at) FROM sessions s WHERE s.project_id=p.id AND s.kind='human'), p.uid
            FROM projects p
            WHERE p.key='*' OR COALESCE(p.added,0)=1 OR EXISTS (SELECT 1 FROM sessions s WHERE s.project_id=p.id AND s.kind='human') OR EXISTS (SELECT 1 FROM decisions d WHERE d.project_id=p.id)
               OR EXISTS (SELECT 1 FROM jobs j WHERE j.project_id=p.id) OR EXISTS (SELECT 1 FROM checks k2 WHERE k2.project_id=p.id)
            ORDER BY CASE WHEN p.key='*' THEN 0 ELSE 1 END, 9 DESC
            """, r => new ProjectDto(r.GetInt64(0), r.Str(1) ?? "?", r.Str(2), r.Long(4), r.Long(5), r.Long(6), r.Long(7), r.Str(8), r.Str(9)));
    }

    /// <summary>Proje bulma: id, ad, yol ya da '*'.</summary>
    public long? ResolveProject(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        using var c = Conn();
        if (spec == "*") return Star(c);
        if (long.TryParse(spec, out var id) && c.Scalar("SELECT id FROM projects WHERE id=@i", null, ("@i", id)) is not null) return id;
        var byKey = c.Scalar("SELECT id FROM projects WHERE key=@k", null, ("@k", TextNormalizer.ProjectKey(spec)));
        if (byKey is not null) return Convert.ToInt64(byKey);
        var byName = c.Query("SELECT id FROM projects WHERE lower(name)=lower(@n) ORDER BY id", r => r.GetInt64(0), null, ("@n", spec));
        if (byName.Count > 0) return byName[0];
        if (Directory.Exists(spec)) { var (pid, _) = Hooks.HookHandler.FindProject(c, spec); return pid; }
        return null;
    }

    // ---------- Kararlar ----------
    private const string DecisionSelect = """
        SELECT d.id, d.project_id, p.name, p.key, d.type, d.text, d.status, d.pinned, d.repeat_count, k.session_count, d.created_at, d.updated_at,
               s.tool, u.ts, u.session_id, d.uid
        FROM decisions d
        LEFT JOIN projects p ON p.id=d.project_id
        LEFT JOIN clusters k ON k.id=d.cluster_id
        LEFT JOIN candidates c ON c.id=d.origin_candidate_id
        LEFT JOIN utterances u ON u.id=c.utterance_id
        LEFT JOIN sessions s ON s.id=u.session_id
        """;

    private static DecisionDto MapDecision(SqliteDataReader r) => new(
        r.GetInt64(0), r.Long(1), r.Str(3) == "*" ? "Genel" : r.Str(2) ?? "?", r.Str(4) ?? "rule", SecretFilter.MaskSecrets(r.Str(5)), r.Str(6) ?? "proposed",
        r.Long(7) == 1, r.Long(8), r.IsDBNull(9) ? 1 : r.Long(9), r.Str(10), r.Str(11),
        r.IsDBNull(12) ? null : new OriginDto(r.GetString(12), r.Str(13), r.Long(14)), r.Str(15));

    public List<DecisionDto> Decisions(long? projectId = null, string? status = null, string? type = null)
    {
        using var c = Conn();
        return c.Query(DecisionSelect + """
             WHERE (@p IS NULL OR d.project_id=@p) AND (@s IS NULL OR d.status=@s) AND (@t IS NULL OR d.type=@t)
             ORDER BY d.pinned DESC, CASE d.status WHEN 'proposed' THEN 0 WHEN 'approved' THEN 1 ELSE 2 END, d.repeat_count DESC, d.id DESC
            """, MapDecision, null, ("@p", projectId), ("@s", status), ("@t", type));
    }

    public DecisionDto Decision(long id)
    {
        using var c = Conn();
        return c.Query(DecisionSelect + " WHERE d.id=@id", MapDecision, null, ("@id", id)).FirstOrDefault()
               ?? throw new SozException(404, $"Karar bulunamadı: {id}");
    }

    private static readonly string[] Statuses = { "proposed", "approved", "archived", "rejected" };

    public DecisionDto CreateDecision(long projectId, string type, string text)
    {
        if (!CandidateTypes.DecisionTypes.Contains(type)) throw new SozException(400, "Tür rule, ban, decision ya da preference olmalı.");
        if (string.IsNullOrWhiteSpace(text)) throw new SozException(400, "Metin boş olamaz.");
        using var c = Conn();
        if (c.Scalar("SELECT id FROM projects WHERE id=@p", null, ("@p", projectId)) is null) throw new SozException(404, "Proje bulunamadı.");
        var now = Time.NowIso();
        c.Exec("INSERT INTO decisions(project_id,type,text,status,pinned,repeat_count,created_at,updated_at,uid) VALUES(@p,@t,@x,'approved',0,1,@n,@n,@u)", null,
            ("@u", Uid.New()), ("@p", projectId), ("@t", type), ("@x", text.Trim()), ("@n", now));
        var id = c.ScalarLong("SELECT last_insert_rowid()");
        Events.Log(c, "add", projectId, id.ToString());
        return Decision(id);
    }

    public DecisionDto PatchDecision(long id, string? status = null, string? text = null, string? type = null, bool? pinned = null, long? projectId = null)
    {
        if (status is not null && !Statuses.Contains(status)) throw new SozException(400, "Durum proposed, approved, archived ya da rejected olmalı.");
        if (type is not null && !CandidateTypes.DecisionTypes.Contains(type)) throw new SozException(400, "Tür rule, ban, decision ya da preference olmalı.");
        if (text is not null && string.IsNullOrWhiteSpace(text)) throw new SozException(400, "Metin boş olamaz.");
        using var c = Conn();
        var cur = c.Query("SELECT project_id, status FROM decisions WHERE id=@id", r => (pid: r.Long(0), st: r.Str(1)), null, ("@id", id)).FirstOrDefault();
        if (cur == default) throw new SozException(404, $"Karar bulunamadı: {id}");
        if (projectId is not null && c.Scalar("SELECT id FROM projects WHERE id=@p", null, ("@p", projectId)) is null) throw new SozException(404, "Proje bulunamadı.");
        c.Exec("""
            UPDATE decisions SET status=COALESCE(@s,status), text=COALESCE(@x,text), type=COALESCE(@t,type), pinned=COALESCE(@pin,pinned),
              project_id=COALESCE(@p,project_id), updated_at=@n WHERE id=@id
            """, null, ("@s", status), ("@x", text?.Trim()), ("@t", type), ("@pin", pinned.HasValue ? (pinned.Value ? 1 : 0) : null), ("@p", projectId),
            ("@n", Time.NowIso()), ("@id", id));
        if (status is not null && status != cur.st)
            Events.Log(c, status switch { "approved" => "approve", "rejected" => "reject", "archived" => "archive", _ => "status" }, cur.pid, id.ToString());
        if (pinned == true) Events.Log(c, "pin", cur.pid, id.ToString());
        return Decision(id);
    }

    // ---------- Tekrarlar ----------
    public List<RepeatDto> Repeats(long? projectId = null, int min = 2, int memberLimit = 50)
    {
        using var c = Conn();
        var rows = c.Query("""
            SELECT k.id, k.project_id, p.name, p.key, k.canonical, k.count, k.session_count, k.first_ts, k.last_ts,
                   (SELECT d.id FROM decisions d WHERE d.cluster_id=k.id ORDER BY d.id DESC LIMIT 1)
            FROM clusters k LEFT JOIN projects p ON p.id=k.project_id
            WHERE k.parent_id IS NULL AND k.session_count >= @m AND (@p IS NULL OR k.project_id=@p)
            ORDER BY k.session_count DESC, k.count DESC, k.last_ts DESC
            """, r => (id: r.GetInt64(0), pid: r.Long(1), name: r.Str(3) == "*" ? "Genel" : r.Str(2) ?? "?", text: r.Str(4) ?? "", n: r.Long(5), s: r.Long(6),
            f: r.Str(7), l: r.Str(8), d: r.LongN(9)), null, ("@m", min), ("@p", projectId));
        var list = new List<RepeatDto>();
        foreach (var k in rows)
        {
            var members = c.Query("""
                SELECT u.id, c.text, u.ts, s.tool, u.session_id FROM candidates c
                JOIN utterances u ON u.id=c.utterance_id JOIN sessions s ON s.id=u.session_id
                WHERE c.cluster_id=@k OR c.cluster_id IN (SELECT id FROM clusters WHERE parent_id=@k)
                ORDER BY u.ts DESC LIMIT @lim
                """, r => new MemberDto(r.GetInt64(0), SecretFilter.MaskSecrets(r.Str(1)), r.Str(2), r.Str(3) ?? "?", r.Long(4)), null, ("@k", k.id), ("@lim", memberLimit));
            list.Add(new RepeatDto(k.id, k.pid, k.name, SecretFilter.MaskSecrets(k.text), k.n, k.s, k.f, k.l, k.d, members));
        }
        return list;
    }

    public DecisionDto Promote(long clusterId, string? type = null)
    {
        if (type is not null && !CandidateTypes.DecisionTypes.Contains(type)) throw new SozException(400, "Tür rule, ban, decision ya da preference olmalı.");
        using var c = Conn();
        var k = c.Query("SELECT project_id, canonical, count, parent_id FROM clusters WHERE id=@id",
            r => (pid: r.Long(0), text: r.Str(1) ?? "", n: r.Long(2), parent: r.LongN(3)), null, ("@id", clusterId)).FirstOrDefault();
        if (k == default) throw new SozException(404, $"Küme bulunamadı: {clusterId}");
        var best = c.Query("""
            SELECT c.id, c.type, c.text FROM candidates c WHERE c.cluster_id=@k OR c.cluster_id IN (SELECT id FROM clusters WHERE parent_id=@k)
            ORDER BY c.score DESC LIMIT 1
            """, r => (id: r.GetInt64(0), type: r.Str(1) ?? "rule", text: r.Str(2) ?? ""), null, ("@k", clusterId)).FirstOrDefault();
        var t = type ?? (best == default ? "rule" : RuleBasedExtractor.DecisionTypeFor(best.type, new RuleBasedExtractor().Score(best.text).Negative));
        var now = Time.NowIso();
        var existing = c.Scalar("SELECT id FROM decisions WHERE cluster_id=@k ORDER BY id DESC LIMIT 1", null, ("@k", clusterId));
        long id;
        if (existing is not null)
        {
            id = Convert.ToInt64(existing);
            c.Exec("UPDATE decisions SET status='approved', type=@t, repeat_count=@n, updated_at=@now WHERE id=@id", null, ("@t", t), ("@n", k.n), ("@now", now), ("@id", id));
        }
        else
        {
            c.Exec("""
                INSERT INTO decisions(project_id,type,text,status,pinned,origin_candidate_id,cluster_id,repeat_count,created_at,updated_at,uid)
                VALUES(@p,@t,@x,'approved',0,@o,@k,@n,@now,@now,@u)
                """, null, ("@u", Uid.New()), ("@p", k.pid), ("@t", t), ("@x", k.text), ("@o", best == default ? null : best.id), ("@k", clusterId), ("@n", k.n), ("@now", now));
            id = c.ScalarLong("SELECT last_insert_rowid()");
        }
        Events.Log(c, "approve", k.pid, id.ToString());
        return Decision(id);
    }

    // ---------- Arama ----------
    public UtterancePage Utterances(string? q, long? projectId, int limit = 50, int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var fq = string.IsNullOrWhiteSpace(q) ? null : "%" + TextNormalizer.Fold(q.Trim()).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        using var c = Conn();
        const string where = "WHERE (@q IS NULL OR u.folded LIKE @q ESCAPE '\\') AND (@p IS NULL OR u.project_id=@p)";
        var total = c.ScalarLong("SELECT COUNT(*) FROM utterances u " + where, null, ("@q", fq), ("@p", projectId));
        var items = c.Query($"""
            SELECT u.id, u.text, u.ts, s.tool, p.name, u.session_id FROM utterances u
            JOIN sessions s ON s.id=u.session_id LEFT JOIN projects p ON p.id=u.project_id
            {where} ORDER BY u.ts DESC LIMIT @lim OFFSET @off
            """, r => new UtteranceDto(r.GetInt64(0), SecretFilter.MaskSecrets(r.Str(1)), r.Str(2), r.Str(3) ?? "?", r.Str(4), r.Long(5)), null,
            ("@q", fq), ("@p", projectId), ("@lim", limit), ("@off", offset));
        return new UtterancePage(total, items);
    }

    // ---------- Paket ----------
    public PacketDto Packet(long? projectId, int? budget = null)
    {
        using var c = Conn();
        var st = SozSettings.Load(c);
        var p = PacketBuilder.Build(c, projectId, budget ?? st.PacketBudget, st.Language);
        return new PacketDto(p.Text, p.Chars, p.DecisionCount);
    }

    // ---------- Karne ----------
    public ReportCard Report(int days = 30, DateTime? nowUtc = null)
    {
        using var c = Conn();
        var card = ReportBuilder.Build(c, Math.Max(1, days), nowUtc ?? Time.UtcNow());
        foreach (var t in card.TopRepeats) t.Text = SecretFilter.MaskSecrets(t.Text);
        return card;
    }

    // ---------- Oturumlar / Toparla ----------
    public List<SessionDto> Sessions(double hours = 24, bool includeStale = false, DateTime? nowUtc = null)
    {
        using var c = Conn();
        var st = SozSettings.Load(c);
        return SessionQueries.Cards(c, hours, includeStale, StateThresholds.From(st), nowUtc ?? Time.UtcNow()).Select(ToDto).ToList();
    }

    private static SessionDto ToDto(SessionCard x) => new(x.Id, x.Tool, x.ProjectId, x.ProjectName, x.Title, x.Kind, x.State, x.Since,
        x.LastUserText is null ? null : SecretFilter.MaskSecrets(DigestBuilder.Display(x.LastUserText)), x.LastUserTs, x.LastAssistantText, x.LastAssistantTs,
        x.AsksQuestion, x.ClaimsDone, x.ShowsEvidence, x.SubCount, x.StateSource, x.StateConfidence, x.EndClass);

    public DigestDto Digest(double hours = 24, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? Time.UtcNow();
        using var c = Conn();
        var st = SozSettings.Load(c);
        var cards = SessionQueries.Cards(c, hours, false, StateThresholds.From(st), now);
        var (text, w, s, wk, i) = DigestBuilder.Build(cards, hours, now, st.Language);
        return new DigestDto(text, w, wk, i, s);
    }

    public async Task<ExplainResult> ExplainAsync(long sessionId, PlainExplainer? explainer = null, CancellationToken ct = default)
    {
        string? text;
        SozSettings st;
        using (var c = Conn())
        {
            st = SozSettings.Load(c);
            if (c.Scalar("SELECT id FROM sessions WHERE id=@id", null, ("@id", sessionId)) is null) throw new SozException(404, $"Oturum bulunamadı: {sessionId}");
            text = c.Scalar("SELECT last_assistant_text FROM sessions WHERE id=@id", null, ("@id", sessionId)) as string;
        }
        var r = await (explainer ?? new PlainExplainer()).ExplainAsync(st, text, ct);
        if (r.Disabled) throw new SozException(409, r.Text);
        return r;
    }

    // ---------- Ayarlar ----------
    public JsonObject GetSettings() => Settings().ToJson();

    public JsonObject PutSettings(JsonObject patch)
    {
        // C13: panel düzeni (navTabs) kayıtta virgüllü metindir; sekme adları DİZİ olarak gelirse (arayüzler listeyi doğrudan gönderir) metne çevrilir, başka biçim anlaşılır hata verir.
        if (patch.ContainsKey("navTabs") && patch["navTabs"] is not null && patch["navTabs"] is not JsonValue)
        {
            if (patch["navTabs"] is JsonArray tabs && tabs.All(t => t is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && !s.Contains(',')))
                patch["navTabs"] = string.Join(",", tabs.Select(t => t!.GetValue<string>().Trim()));
            else throw new SozException(400, "navTabs: sekme adlarının listesi ya da virgülle ayrılmış metin olmalı (ör. suan,projeler,ayarlar).");
        }
        var err = SozSettings.Validate(patch);
        if (err is not null) throw new SozException(400, err);
        using var c = Conn();
        SozSettings.Save(c, patch);
        return SozSettings.Load(c).ToJson();
    }

    // ---------- Dışa aktarma ----------
    /// <summary>Deneme projesi klasörü: SOZ_DEMO_PROJECT → exe'nin üst klasörlerinde 'deneme-proje'.</summary>
    public static string? DemoProjectDir()
    {
        var env = Environment.GetEnvironmentVariable("SOZ_DEMO_PROJECT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            var cand = Path.Combine(d.FullName, "deneme-proje");
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    public static bool IsUnder(string path, string? root)
    {
        if (root is null) return false;
        var p = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public ExportResult Export(string projectDir, bool agents, bool claude, long? projectId = null)
    {
        var st = Settings();
        if (!st.AllowRealProjectWrites && !IsUnder(projectDir, DemoProjectDir()))
            throw new SozException(409, Strings.Get(st.Language, "export.denied"));
        if (!agents && !claude) agents = true;
        Directory.CreateDirectory(projectDir);
        projectId ??= ResolveProject(projectDir);
        var packet = Packet(projectId);
        var files = new List<string>();
        if (agents) files.Add(AgentsFileExporter.WriteBlock(Path.Combine(projectDir, "AGENTS.md"), packet.Text));
        if (claude) files.Add(AgentsFileExporter.WriteBlock(Path.Combine(projectDir, "CLAUDE.md"), packet.Text));
        using var c = Conn();
        Events.Log(c, "export", projectId, JsonSerializer.Serialize(new { files, chars = packet.Chars }));
        return new ExportResult(true, $"{files.Count} dosya güncellendi ({packet.DecisionCount} karar).", files);
    }

    public ExportResult ExportProject(long projectId, bool agents, bool claude)
    {
        string? path;
        using (var c = Conn())
        {
            if (c.Scalar("SELECT id FROM projects WHERE id=@p", null, ("@p", projectId)) is null) throw new SozException(404, "Proje bulunamadı.");
            path = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
        }
        if (string.IsNullOrWhiteSpace(path)) throw new SozException(409, Strings.Get(Settings().Language, "export.nopath"));
        return Export(path, agents, claude, projectId);
    }

    public void LogEvent(string kind, long? projectId = null, string? detail = null)
    {
        using var c = Conn();
        Events.Log(c, kind, projectId, detail);
    }

    /// <summary>Kullanıcı ana klasörü (kullanıcı geneli kanca dosyası için). Sınamalar SOZ_USER_HOME ile sahte klasör verir.</summary>
    public static string? UserHome()
        => Environment.GetEnvironmentVariable("SOZ_USER_HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } u ? u : null;

    /// <summary>Kanca sağlığı (EK B): yalnız okur.</summary>
    public Hooks.HookHealthDto HookHealth()
    {
        using var c = Conn();
        return Hooks.HookHealth.Build(c, Db.DataDir, UserHome());
    }

    // ---------- Doktor ----------
    public JsonObject Doctor()
    {
        using var c = Conn();
        var st = SozSettings.Load(c);
        var o = new JsonObject
        {
            ["hookHealth"] = JsonSerializer.SerializeToNode(Hooks.HookHealth.Build(c, Db.DataDir, UserHome()), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ["version"] = SozInfo.Version,
            ["dataDir"] = Db.DataDir,
            ["dataDirSource"] = SourceLocator.SourceOf(Db.DataDir) ?? "bilinmiyor (kitaplık içinden açıldı)",
            ["exeDir"] = AppContext.BaseDirectory,
            ["dbPath"] = Db.DbPath,
            ["dbSchemaVersion"] = Convert.ToInt32(c.Scalar("PRAGMA user_version")),
            ["dbSizeBytes"] = File.Exists(Db.DbPath) ? new FileInfo(Db.DbPath).Length : 0,
            ["lastImportAt"] = SozSettings.GetInternal(c, "_lastImportAt"),
            ["demoProjectDir"] = DemoProjectDir(),
        };
        var sources = new JsonArray();
        foreach (var (r, reader) in SourceLocator.FileSources(st))
        {
            var s = new JsonObject { ["tool"] = r.Tool, ["root"] = r.Root, ["origin"] = r.Origin, ["exists"] = r.Exists };
            if (r.Exists)
            {
                var pattern = reader.FilePattern!;
                string[] files;
                try { files = Directory.GetFiles(r.Root!, pattern, SearchOption.AllDirectories); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files = Array.Empty<string>(); s["error"] = e.Message; }
                s["files"] = files.Length;
                if (reader.SubagentFolders) s["subagentFiles"] = files.Count(f => f.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar));
                s["totalMB"] = Math.Round(files.Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } }) / 1e6, 1);
                var newest = files.OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch (IOException) { return DateTime.MinValue; } }).FirstOrDefault();
                if (newest is not null)
                {
                    try
                    {
                        var first = JsonlScanner.ReadFirstLine(newest, 256 * 1024);
                        using var doc = first is null ? null : JsonlScanner.TryParse(first);
                        s["readable"] = doc is not null;
                        s["newestFileUtc"] = Time.Iso(File.GetLastWriteTimeUtc(newest));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s["readable"] = false; s["error"] = e.Message; }
                }
                s["importedFiles"] = c.ScalarLong("SELECT COUNT(*) FROM file_state WHERE tool=@t", null, ("@t", r.Tool));
            }
            sources.Add(s);
        }
        o["sources"] = sources;
        o["counts"] = new JsonObject
        {
            ["projects"] = c.ScalarLong("SELECT COUNT(*) FROM projects WHERE key<>'*'"),
            ["humanSessions"] = c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE kind='human'"),
            ["automationSessions"] = c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE kind='automation'"),
            ["subagentSessions"] = c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE kind='subagent'"),
            ["utterances"] = c.ScalarLong("SELECT COUNT(*) FROM utterances"),
            ["candidates"] = c.ScalarLong("SELECT COUNT(*) FROM candidates"),
            ["clusters"] = c.ScalarLong("SELECT COUNT(*) FROM clusters"),
            ["proposed"] = c.ScalarLong("SELECT COUNT(*) FROM decisions WHERE status='proposed'"),
            ["approved"] = c.ScalarLong("SELECT COUNT(*) FROM decisions WHERE status='approved'"),
            ["compactions"] = c.ScalarLong("SELECT COUNT(*) FROM compactions"),
        };
        var log = Path.Combine(Db.DataDir, "soz-hook.log");
        o["hookLogLines"] = File.Exists(log) ? File.ReadLines(log).Count() : 0;
        var sf = ServerFile.Read(Db.DataDir);
        o["serverFile"] = sf is null ? null : new JsonObject { ["url"] = sf.Url, ["pid"] = sf.Pid, ["startedAt"] = sf.StartedAt, ["alive"] = sf.Alive, ["stale"] = !sf.Alive };
        return o;
    }
}
