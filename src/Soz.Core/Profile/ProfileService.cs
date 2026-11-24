using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Assistants;
using Soz.Core.Data;
using Soz.Core.Onboarding;

namespace Soz.Core.Profile;

public sealed class ProfileService(SozDb db)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    public IMachineProbe Machine { get; init; } = new MachineProbe();
    public IProjectFileSystem FileSystem { get; init; } = new ProjectFileSystem();
    public IProcessProvider Processes { get; init; } = new ProcessProbe();
    public Func<OnboardingRoots, ProjectExclusions>? ExclusionsFactory { get; init; }
    /// <summary>Claude'un açık oturumlarını veren komut. Boşsa gerçek köklerde <c>claude agents --json</c> çalıştırılır; sentetik köklerde hiç çalıştırılmaz. Testler sahte verir.</summary>
    public IClaudeAgentsCommand? AgentsCommand { get; init; }
    /// <summary>Saat. Canlı bölümün önbellek süresi bununla ölçülür (testler sabit saat verir).</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;
    /// <summary>Sentetik köklerde de "şu an açık" bölümü her istekte yeniden hesaplanır (yalnız testler açar; gerçek süreç taramasını önlemek için varsayılan kapalıdır).</summary>
    public bool RefreshSyntheticRoots { get; init; }

    /// <summary>Uyarlanır yoklama kuralları ve kanca haberleri. Süreçte tek paylaşımlı örnek verilebilir; verilmezse bu servise özel, saati <see cref="Clock"/>'tur.</summary>
    public LiveCadence Cadence { get => _cadence ??= new LiveCadence(() => Clock()); init => _cadence = value; }
    private LiveCadence? _cadence;
    /// <summary>Claude'un açık oturum kayıtlarına bakarak "Claude çalışıyor mu" kararı (ucuz süreç yoklaması). Boşsa: enjekte komut varsa hep "var", yoksa gerçek süreç listesi.</summary>
    public Func<IReadOnlyList<ClaudeRegistryEntry>, bool>? ClaudePresent { get; init; }

    // ---- İlk tarama ilerlemesi (D6-18): kaç dosyadan kaçı okundu ----
    private readonly object _progressGate = new();
    private ScanProgress _progress = new(false, "bos", 0, 0, null, null, 0, 0);
    private Stopwatch? _progressWatch;

    /// <summary>Tarama sürerken (ya da en son bittiğinde) ilerleme. Arayüz bunu yoklayıp "x / y dosya" gösterir; tarama yoksa Running=false.</summary>
    public ScanProgress Progress()
    {
        lock (_progressGate) return _progress with { ElapsedMs = _progressWatch?.ElapsedMilliseconds ?? _progress.ElapsedMs };
    }

    private void SetProgress(Func<ScanProgress, ScanProgress> change)
    {
        lock (_progressGate) _progress = change(_progress);
    }

    private readonly object _liveGate = new();
    private readonly object _computeGate = new();
    private readonly object _agentsGate = new();
    private LiveCache? _liveCache;
    private AgentsCache? _agents;
    /// <summary><c>StateAt</c>: durum bilgisinin gerçekte alındığı an (komut önbellekten dönüyorsa hesap anından eski olabilir); kanca notları buna göre geçerlidir.</summary>
    private sealed record LiveCache(DateTime At, string LiveJson, string ProcessesJson, string Source, string? Note, DateTime StateAt);
    private sealed record AgentsCache(DateTime At, ClaudeAgentsResult Result);

    /// <summary>
    /// Kayıtlı görünüm. <paramref name="context"/> verilirse ve gerçek kökler söz konusuysa "şu an açık" bölümü (live, processes) kayıtlı taramadan değil
    /// taze hesaplanır: <see cref="LiveCadence.Interval"/> kadar önbelleğe alınır (biri bakıyorsa sık, kimse bakmıyorsa seyrek), hesaplanma zamanı <c>liveObservedAt</c>,
    /// kaynağı <c>liveSource</c> olarak yazılır. Hesap sürerken gelen ikinci istek beklemez, son bilineni alır (üst üste binmez). Kanca haberi gelen oturumun durumu
    /// hemen yansır. Ağır tam tarama (<see cref="Scan"/>) ayrıdır. <paramref name="at"/> verilirse (testler) kayıtlı görünüm aynen kullanılır.
    /// <paramref name="background"/>: sessiz yoklama (kabuk, bekleyen listesinin arka plan beslemesi); "biri bakıyor" sayılmaz.
    /// </summary>
    public JsonObject View(DateTime? at = null, Func<ScanContext>? context = null, bool background = false)
    {
        using var c = db.Open();
        var json = c.Scalar("SELECT body FROM profile_snapshot WHERE id=1") as string;
        if (json is null) return ToJson(Empty(at ?? DateTime.UtcNow));
        var view = JsonNode.Parse(json)!.AsObject();
        CleanLegacyTimes(view);
        if (!view.ContainsKey("scannedAt")) view["scannedAt"] = null;
        if (!view.ContainsKey("warnings") || view["warnings"] is null) view["warnings"] = new JsonArray();
        if (!view.ContainsKey("liveObservedAt")) view["liveObservedAt"] = view["scannedAt"]?.DeepClone();
        if (!view.ContainsKey("liveSource")) view["liveSource"] = null;
        if (!view.ContainsKey("liveNote")) view["liveNote"] = null;
        if (at is null && context is not null) TryRefreshLive(view, context, background);
        var now = at ?? Clock();
        foreach (var project in view["projects"]!.AsArray()) project?.AsObject().Remove("activeSessions");
        var live = view["live"]!.AsArray();
        foreach (var group in live.ToArray())
        {
            var sessions = group!["sessions"]!.AsArray();
            foreach (var row in sessions.ToArray())
            {
                DateTime? last = DateTime.TryParse((string?)row?["lastActivity"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
                var level = last is { } lastValue ? ProjectResolver.ActivityLevel(lastValue, now) : null;
                var open = (bool?)row?["acikAdayi"] == true;
                var evidence = (string?)row?["evidence"];
                // Kanıt alanı olmayan (eski) kayıtta süreç bağına güvenilmez: yalnız yakın zaman sayılır.
                if (evidence is null) evidence = open ? LiveRules.EvidenceNearTime : LiveRules.EvidenceForActivity(last, now);
                else if (evidence != LiveRules.EvidenceProcess && !open) evidence = LiveRules.EvidenceForActivity(last, now);
                // Süreç kanıtı yoksa 24 saatten eski hiçbir kayıt açık adayı değildir (kayıtlı görünüm sonradan eskiyebilir).
                if (open && !LiveRules.MayBeOpen(evidence, last, now)) { open = false; row!["acikAdayi"] = false; evidence = LiveRules.EvidenceNone; }
                if (level is null && !open) sessions.Remove(row);
                else
                {
                    row!["evidence"] = evidence;
                    if (row["state"] is null) row["state"] = LiveRules.StateUnknown;
                    if (!row.AsObject().ContainsKey("waitingFor")) row["waitingFor"] = null;
                    row["level"] = level ?? (open && (string?)row["level"] == "acik" ? "acik" : null);
                }
            }
            if (sessions.Count == 0) { live.Remove(group); continue; }
            var ids = sessions.Select(s => ((string?)s!["tool"], (string?)s["id"])).ToHashSet();
            var relations = group["relations"]!.AsArray();
            foreach (var row in relations.ToArray())
                if (!ids.Contains(((string?)row!["tool"], (string?)row["sessionId"])) || !ids.Contains(((string?)row["otherTool"], (string?)row["otherSessionId"]))) relations.Remove(row);
        }
        return view;
    }

    /// <summary>Canlı bölümü taze hesaplayıp görünüme yazar. Hesaplanamazsa (kilit, izin, kayıt yok) kayıtlı görünüm olduğu gibi kalır.</summary>
    private void TryRefreshLive(JsonObject view, Func<ScanContext> contextFactory, bool background)
    {
        try
        {
            var context = contextFactory();
            if (context.Roots.IsSynthetic && !RefreshSyntheticRoots) return;
            var now = Clock().ToUniversalTime();
            if (!background) Cadence.Watch(now);
            LiveCache? FreshCache()
            {
                lock (_liveGate)
                    return _liveCache is { } known && now >= known.At && now - known.At < Cadence.Interval(now) && Cadence.WakeAt <= known.At ? known : null;
            }
            var cache = FreshCache();
            if (cache is null)
            {
                LiveCache? stale; lock (_liveGate) stale = _liveCache;
                // Hesap sürüyorsa ikinci istek beklemez (komut üst üste binmez): son bilinen görünür. İlk hesapta başka çare olmadığından beklenir.
                if (!Monitor.TryEnter(_computeGate, stale is null ? Timeout.Infinite : 0)) cache = stale;
                else
                {
                    try
                    {
                        cache = FreshCache();
                        if (cache is null) { cache = ComputeLive(context, now); lock (_liveGate) _liveCache = cache; }
                    }
                    finally { Monitor.Exit(_computeGate); }
                }
            }
            view["live"] = JsonNode.Parse(cache!.LiveJson);
            view["processes"] = JsonNode.Parse(cache.ProcessesJson);
            view["liveObservedAt"] = cache.At.ToString("O", CultureInfo.InvariantCulture);
            view["liveSource"] = cache.Source;
            view["liveNote"] = cache.Note;
            ApplyHookNotes(view["live"]!.AsArray(), cache.StateAt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or JsonException or InvalidOperationException
            or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            // Taze hesap yapılamadı: kayıtlı görünüm kalır ve bu açıkça söylenir (eski taramadan olduğu liveObservedAt'ten anlaşılır).
            view["liveNote"] = "Açık pencereler şu an yeniden hesaplanamadı. Son taramadaki durum gösteriliyor.";
        }
    }

    /// <summary>
    /// Hesaptan SONRA kancayla haber veren Claude oturumlarının durumunu hemen yansıtır (bir sonraki yoklamayı beklemez).
    /// Kanca haberi, yoklamanın söylediğinden yeni olduğu için onu geçer; çalışıyor/boşta için bekleme nedeni silinir.
    /// </summary>
    private void ApplyHookNotes(JsonArray live, DateTime computedAt)
    {
        foreach (var group in live)
        {
            if (group?["sessions"] is not JsonArray sessions) continue;
            foreach (var row in sessions)
            {
                if (row is not JsonObject o || (string?)o["tool"] != "claude" || (string?)o["id"] is not { } id) continue;
                if (Cadence.NoteOf(id) is not { } note || note.At <= computedAt) continue;
                o["state"] = note.State;
                if (note.State != LiveRules.StateWaiting) o["waitingFor"] = null;
            }
        }
    }

    private LiveCache ComputeLive(ScanContext context, DateTime now)
    {
        var settings = SozSettings.Load(db);
        var roots = ProfileSources.Resolve(settings, context.Roots);
        var processes = ProcessProbe.Inspect(Processes, now);
        var hints = ReadLiveHints(context.Roots, now, settings, force: false);
        DateTime stateAt; lock (_agentsGate) stateAt = _agents is { } known && known.At <= now ? known.At : now;
        Cadence.SetKnown((hints.ClaudeRegistry ?? []).Select(e => e.SessionId));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in hints.ClaudeRegistry ?? []) ids.Add(entry.SessionId);
        foreach (var id in hints.HermesActiveIds ?? new HashSet<string>()) ids.Add(id);
        foreach (var id in hints.AntigravityPresenceIds ?? new HashSet<string>()) ids.Add(id);

        var projectOf = new Dictionary<(string Tool, string Path, string Id), string?>();
        List<SessionMetadata> stored;
        using (var c = db.Open())
        {
            var since = Iso(now.AddDays(-(LiveRules.LiveWindowDays + 1)))!;
            var idList = ids.Take(400).ToArray();
            var marks = string.Join(",", idList.Select((_, i) => "@x" + i.ToString(CultureInfo.InvariantCulture)));
            var parameters = new List<(string, object?)> { ("@since", since) };
            parameters.AddRange(idList.Select((id, i) => ("@x" + i.ToString(CultureInfo.InvariantCulture), (object?)id)));
            var rows = c.Query($"""
                SELECT tool,session_id,cwd,git_branch,started_at,last_activity,parent_id,record_path,record_modified_at,observed_at,source_type,git_repo_root,source_channel,title,project_path
                FROM profile_session WHERE present=1 AND (last_activity >= @since{(idList.Length > 0 ? " OR session_id IN (" + marks + ")" : "")})
                """, r => (Row: Stored(r), Project: r.Str(14)), null, parameters.ToArray());
            stored = rows.Select(r => r.Row).ToList();
            foreach (var r in rows) projectOf[(r.Row.Tool, r.Row.RecordPath, r.Row.Id)] = r.Project;
        }
        var sessions = SubSessionTitles.Fill(DeduplicateSessions(stored));
        var merged = new Dictionary<string, List<SessionMetadata>>(StringComparer.OrdinalIgnoreCase);
        var unassigned = new List<SessionMetadata>();
        foreach (var s in sessions)
        {
            var project = projectOf.GetValueOrDefault((s.Tool, s.RecordPath, s.Id));
            if (string.IsNullOrWhiteSpace(project)) { unassigned.Add(s); continue; }
            if (!merged.TryGetValue(project, out var list)) merged[project] = list = [];
            list.Add(s);
        }
        var exclusions = ExclusionsFactory?.Invoke(context.Roots) ?? new ProjectExclusions(context.Roots.Home, db.DataDir, OwnTemporaryDir(), Path.GetTempPath(), roots);
        var live = new ProjectResolver(FileSystem, exclusions).BuildLive(sessions, merged, unassigned, now, processes, hints);
        return new LiveCache(now, JsonSerializer.SerializeToNode(live, JsonOptions)!.ToJsonString(),
            JsonSerializer.SerializeToNode(processes, JsonOptions)!.ToJsonString(), hints.ClaudeSource, hints.ClaudeNote, stateAt);
    }

    public static JsonObject ToJson(ProfileSnapshot snapshot) => JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
    public static JsonObject ToJsonProject(ProjectProfile project) => JsonSerializer.SerializeToNode(project, JsonOptions)!.AsObject();
    private static ProfileSnapshot Empty(DateTime now) => new(new(null, null, null, null, null, [], []), [], [], new(0, new Dictionary<string, int>(), now), [], new(null, null, [], now), new(0, 0, 0), 0, [], Warnings: []);

    public ProfileSnapshot Scan(ScanContext context)
    {
        lock (_gate)
        {
            var sw = Stopwatch.StartNew();
            lock (_progressGate) { _progressWatch = sw; _progress = new ScanProgress(true, "sayiliyor", 0, 0, null, DateTime.UtcNow, 0, 0); }
            try { return ScanCore(context, sw); }
            finally { lock (_progressGate) { _progress = _progress with { Running = false, Phase = "bitti", Done = Math.Max(_progress.Done, _progress.Total), Percent = 100, ElapsedMs = sw.ElapsedMilliseconds }; _progressWatch = null; } }
        }
    }

    private ProfileSnapshot ScanCore(ScanContext context, Stopwatch sw)
    {
        {
            var now = context.UtcNow().ToUniversalTime();
            var roots = ProfileSources.Resolve(SozSettings.Load(db), context.Roots);
            using var c = db.Open();
            var states = c.Query("SELECT tool,path,fingerprint,ok FROM profile_file", r => (Tool: r.GetString(0), Path: r.GetString(1), Fingerprint: r.GetString(2), Ok: r.GetInt32(3) == 1))
                .ToDictionary(r => (r.Tool, r.Path));
            var changes = new List<(string Tool, string Path, string Fingerprint, bool Ok, IReadOnlyList<SessionMetadata> Rows)>();
            var seen = new HashSet<(string Tool, string Path)>();
            var rawReports = new List<ProfileSource>();

            // İlerleme için önce dosyalar sayılır (yalnız dizin listesi, okuma yok).
            var total = 0;
            foreach (var countRoot in roots)
            {
                try { total += ProfileSources.Files(countRoot).Count(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
            SetProgress(p => p with { Phase = "okunuyor", Total = total });

            foreach (var root in roots)
            {
                var unreadable = 0; var read = 0; var unchanged = 0; var successful = 0;
                var reasons = new Dictionary<string, int>();
                void Failure(Exception e)
                {
                    unreadable++;
                    var reason = ProfileSources.FailureReason(e);
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                }
                try
                {
                    foreach (var path in ProfileSources.Files(root))
                    {
                        seen.Add((root.Tool, path));
                        SetProgress(p => p with { Done = p.Done + 1, Tool = root.Tool, Percent = total <= 0 ? 0 : (int)Math.Min(90L, (p.Done + 1) * 90L / total) });
                        var fingerprint = "unreadable";
                        try
                        {
                            fingerprint = ProfileSources.Fingerprint(path, root.Tool);
                            if (states.TryGetValue((root.Tool, path), out var old) && old.Ok && old.Fingerprint == fingerprint)
                            { unchanged++; successful++; continue; }
                            read++;
                            var rows = ReadSourceEntry(root, path, now);
                            if (ProfileSources.Fingerprint(path, root.Tool) != fingerprint) throw new ProfileReadException("kilitli");
                            changes.Add((root.Tool, path, fingerprint, true, rows));
                            successful++;
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or JsonException or System.Security.SecurityException)
                        { Failure(e); changes.Add((root.Tool, path, fingerprint, false, [])); }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Failure(e); }

                var sourceKind = root.Tool switch
                {
                    "claude" or "codex" => "jsonl",
                    "vscode" or "cursor" => "workspace",
                    _ => "sqlite",
                };
                rawReports.Add(new(root.Tool, successful, unreadable, 0, read, unchanged,
                    new(root.Tool, sourceKind, root.Path, now), now, reasons));
            }

            SetProgress(p => p with { Phase = "kaydediliyor", Tool = null, Percent = Math.Max(p.Percent, 92) });
            using (var tx = c.BeginTransaction())
            {
                foreach (var old in states.Keys.Where(k => !seen.Contains(k)))
                {
                    c.Exec("UPDATE profile_session SET present=0 WHERE tool=@t AND record_path=@p", tx, ("@t", old.Tool), ("@p", old.Path));
                    c.Exec("UPDATE profile_file SET ok=0 WHERE tool=@t AND path=@p", tx, ("@t", old.Tool), ("@p", old.Path));
                }
                using var insert = c.Cmd("""
                    INSERT INTO profile_session(tool,session_id,cwd,git_branch,started_at,last_activity,parent_id,record_path,record_modified_at,observed_at,source_type,git_repo_root,source_channel,title,kind,present)
                    VALUES(@tool,@id,@cwd,@branch,@start,@last,@parent,@path,@modified,@observed,@type,@repo,@channel,@title,'gozlem',1)
                    ON CONFLICT(tool,record_path,session_id) DO UPDATE SET cwd=excluded.cwd,git_branch=excluded.git_branch,started_at=excluded.started_at,
                    last_activity=excluded.last_activity,parent_id=excluded.parent_id,record_modified_at=excluded.record_modified_at,
                    observed_at=excluded.observed_at,source_type=excluded.source_type,git_repo_root=excluded.git_repo_root,source_channel=excluded.source_channel,title=excluded.title,present=1
                    """, tx);
                foreach (var key in new[] { "tool", "id", "cwd", "branch", "start", "last", "parent", "path", "modified", "observed", "type", "repo", "channel", "title" }) insert.Parameters.AddWithValue("@" + key, DBNull.Value);
                insert.Prepare();
                foreach (var ch in changes)
                {
                    c.Exec("UPDATE profile_session SET present=0 WHERE tool=@t AND record_path=@p", tx, ("@t", ch.Tool), ("@p", ch.Path));
                    c.Exec("INSERT INTO profile_file(tool,path,fingerprint,ok) VALUES(@t,@p,@f,@o) ON CONFLICT(tool,path) DO UPDATE SET fingerprint=excluded.fingerprint,ok=excluded.ok", tx,
                        ("@t", ch.Tool), ("@p", ch.Path), ("@f", ch.Fingerprint), ("@o", ch.Ok ? 1 : 0));
                    foreach (var s in ch.Rows)
                    {
                        object?[] vals = [s.Tool, s.Id, s.Cwd, s.GitBranch, Iso(s.StartedAt), Iso(s.LastActivity) ?? "", s.ParentId, s.RecordPath, Iso(s.RecordModifiedAt), Iso(s.ObservedAt), s.SourceType, s.GitRepoRoot, s.SourceChannel, s.Title];
                        for (var i = 0; i < vals.Length; i++) insert.Parameters[i].Value = vals[i] ?? DBNull.Value;
                        insert.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }

            var storedSessions = c.Query("SELECT tool,session_id,cwd,git_branch,started_at,last_activity,parent_id,record_path,record_modified_at,observed_at,source_type,git_repo_root,source_channel,title FROM profile_session WHERE present=1",
                r => Stored(r));
            var sessions = SubSessionTitles.Fill(DeduplicateSessions(storedSessions));
            var reports = AddSkippedCodex(MergeReportsByTool(rawReports, sessions), roots, sessions);
            var warnings = BuildWarnings(reports);

            var machine = Machine.Read(now);
            var exclusions = ExclusionsFactory?.Invoke(context.Roots) ?? new ProjectExclusions(context.Roots.Home, db.DataDir, OwnTemporaryDir(), Path.GetTempPath(), roots);
            var processes = ProcessProbe.Inspect(Processes, now);
            var liveHints = ReadLiveHints(context.Roots, now, SozSettings.Load(db), force: true);
            var resolver = new ProjectResolver(FileSystem, exclusions);
            var built = resolver.Build(sessions, now, processes, liveHints);

            using (var tx = c.BeginTransaction())
            using (var assign = c.Cmd("UPDATE profile_session SET project_path=@project,unassigned_reason=@reason WHERE tool=@tool AND record_path=@path AND session_id=@id", tx))
            {
                foreach (var name in new[] { "project", "reason", "tool", "path", "id" }) assign.Parameters.AddWithValue("@" + name, DBNull.Value);
                assign.Prepare();
                foreach (var st in storedSessions)
                {
                    if (!resolver.Assignments.TryGetValue((st.Tool, st.RecordPath, st.Id), out var value))
                    {
                        // DeduplicateSessions ile elenen ikincil kayıtlar (ör. Codex SQLite varken aynı kimlikli rollout.jsonl) birincil kaydın projesini alır
                        var primary = sessions.FirstOrDefault(x => x.Tool == st.Tool && string.Equals(x.Id, st.Id, StringComparison.Ordinal));
                        if (primary is not null) resolver.Assignments.TryGetValue((primary.Tool, primary.RecordPath, primary.Id), out value);
                    }
                    object?[] values = [value.Project, value.Reason, st.Tool, st.RecordPath, st.Id];
                    for (var i = 0; i < values.Length; i++) assign.Parameters[i].Value = values[i] ?? DBNull.Value;
                    assign.ExecuteNonQuery();
                }
                tx.Commit();
            }

            var tools = reports.Select(r => new ProfileTool(r.Tool, r.Sessions, r.Source, now)).ToArray();
            var snapshot = new ProfileSnapshot(
                machine,
                tools,
                built.Projects,
                built.Unassigned,
                built.Live,
                built.Defaults,
                new(sessions.Count, built.Projects.Count, machine.Facts.Count + machine.Disks.Count + built.Projects.Count + tools.Length + 2),
                sw.ElapsedMilliseconds,
                reports,
                processes,
                ScannedAt: context.UtcNow().ToUniversalTime(),
                Warnings: warnings,
                LiveObservedAt: now, LiveSource: liveHints.ClaudeSource, LiveNote: liveHints.ClaudeNote);
            c.Exec("INSERT INTO profile_snapshot(id,body) VALUES(1,@b) ON CONFLICT(id) DO UPDATE SET body=excluded.body", null, ("@b", ToJson(snapshot).ToJsonString()));
            lock (_liveGate) _liveCache = null; // yeni tarama: önbellekteki açık oturum hesabı bayat
            lock (_agentsGate) _agents = null;
            Cadence.SetKnown((liveHints.ClaudeRegistry ?? []).Select(e => e.SessionId));
            return snapshot;
        }
    }

    private static IReadOnlyList<SessionMetadata> ReadSourceEntry(ProfileRoot root, string path, DateTime now)
    {
        if (root.Tool == "claude" && ProfileSources.IsClaudeDesktopRoot(root.Path))
            return ProfileSources.ReadClaudeDesktopEntry(path, now);
        if (root.Tool == "codex" && path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            return ProfileSources.ReadCodexStateDb(path, now);
        if (root.Tool is "claude" or "codex")
        {
            var row = SessionMetadataReader.Read(root.Tool, path, now);
            if (row is null) throw new ProfileReadException("bozuk");
            return [row];
        }
        if (root.Tool == "antigravity")
            return ProfileSources.ReadAntigravityDb(path, now);
        if (root.Tool is "vscode" or "cursor")
            return ProfileSources.ReadWorkspaceStorage(root.Tool, path, now);
        return ProfileSources.ReadDatabase(root.Tool, path, now);
    }

    /// <summary>Codex SQLite + rollout.jsonl veya Antigravity IDE + CLI arasında aynı oturum kimliği varsa tekilleştirir (SQLite öncelikli).</summary>
    private static List<SessionMetadata> DeduplicateSessions(List<SessionMetadata> rows)
    {
        var output = new List<SessionMetadata>(rows.Count);
        foreach (var group in rows.GroupBy(r => r.Tool, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Equals("codex", StringComparison.OrdinalIgnoreCase) || group.Key.Equals("antigravity", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var byId in group.GroupBy(r => r.Id, StringComparer.Ordinal))
                {
                    var items = byId.ToList();
                    if (items.Count == 1) { output.Add(items[0]); continue; }
                    var best = items
                        .OrderByDescending(x => x.SourceType == "sqlite" ? 1 : 0)
                        .ThenByDescending(x => x.LastActivity ?? DateTime.MinValue)
                        .First();
                    if (best.Cwd is null)
                        best = best with { Cwd = items.Select(x => x.Cwd).FirstOrDefault(x => x is not null) };
                    if (best.ParentId is null)
                        best = best with { ParentId = items.Select(x => x.ParentId).FirstOrDefault(x => x is not null) };
                    if (best.Title is null)
                        best = best with { Title = items.Select(x => x.Title).FirstOrDefault(x => x is not null) };
                    output.Add(best);
                }
            }
            else
            {
                output.AddRange(group);
            }
        }
        return output;
    }

    private static List<ProfileSource> MergeReportsByTool(List<ProfileSource> rawReports, List<SessionMetadata> sessions)
    {
        var list = new List<ProfileSource>();
        foreach (var g in rawReports.GroupBy(r => r.Tool, StringComparer.OrdinalIgnoreCase))
        {
            var items = g.ToList();
            var first = items[0];
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in items)
                foreach (var (rk, rv) in item.Reasons)
                    reasons[rk] = reasons.GetValueOrDefault(rk) + rv;

            list.Add(new ProfileSource(
                first.Tool,
                items.Sum(x => x.Okunan),
                items.Sum(x => x.Okunamayan),
                sessions.Count(s => s.Tool.Equals(first.Tool, StringComparison.OrdinalIgnoreCase)),
                items.Sum(x => x.FilesRead),
                items.Sum(x => x.FilesUnchanged),
                first.Source,
                first.ObservedAt,
                reasons));
        }
        return list;
    }

    /// <summary>Codex'in okunmadan atlanan kayıtlarını (özet veritabanında karşılığı olmayan sıkıştırılmış dosyalar, eski sürüm özetler, arşiv) okunamayan sayısına ve nedenlerine ekler.</summary>
    private static List<ProfileSource> AddSkippedCodex(List<ProfileSource> reports, IReadOnlyList<ProfileRoot> roots, IReadOnlyList<SessionMetadata> sessions)
    {
        var known = sessions.Where(s => s.Tool == "codex").Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int compressed = 0, older = 0, archived = 0;
        foreach (var root in roots.Where(r => r.Tool == "codex"))
        {
            var skipped = ProfileSources.CountSkippedCodex(root.Path, known);
            compressed += skipped.Compressed; older += skipped.OlderStateDbs; archived += skipped.Archived;
        }
        if (compressed + older + archived == 0) return reports;
        return reports.Select(r =>
        {
            if (!r.Tool.Equals("codex", StringComparison.OrdinalIgnoreCase)) return r;
            var reasons = new Dictionary<string, int>(r.Reasons, StringComparer.Ordinal);
            if (compressed > 0) reasons[SkippedCompressed] = compressed;
            if (older > 0) reasons[SkippedOlderState] = older;
            if (archived > 0) reasons[SkippedArchive] = archived;
            return r with { Okunamayan = r.Okunamayan + compressed + older + archived, Reasons = reasons };
        }).ToList();
    }

    public const string SkippedCompressed = "sikistirilmis";
    public const string SkippedOlderState = "eski-ozet";
    public const string SkippedArchive = "arsiv";

    public static IReadOnlyList<string> BuildWarnings(IEnumerable<ProfileSource> reports, int compressedCodexWithoutSqlite = 0)
    {
        var list = new List<string>();
        foreach (var r in reports)
        {
            if (r.Okunamayan <= 0) continue;
            var title = AssistantCatalog.Get(r.Tool)?.Title ?? r.Tool;
            var compressedCount = r.Reasons.GetValueOrDefault(SkippedCompressed);
            var olderCount = r.Reasons.GetValueOrDefault(SkippedOlderState);
            var archiveCount = r.Reasons.GetValueOrDefault(SkippedArchive);
            if (compressedCount > 0)
                list.Add($"{title} kaynağında sıkıştırılmış {compressedCount.ToString(CultureInfo.InvariantCulture)} kayıt dosyası, özet dosyasında karşılığı olmadığı için okunamadı.");
            if (olderCount > 0)
                list.Add($"{title} kaynağında {olderCount.ToString(CultureInfo.InvariantCulture)} eski sürümden kalma özet dosyası okunmadı.");
            if (archiveCount > 0)
                list.Add($"{title} kaynağında arşivlenmiş {archiveCount.ToString(CultureInfo.InvariantCulture)} kayıt dosyası okunmadı.");
            var unrecognized = r.Reasons.GetValueOrDefault(ProfileSources.ReasonUnrecognizedFormat);
            if (unrecognized > 0)
                list.Add($"{title} kaynağında {unrecognized.ToString(CultureInfo.InvariantCulture)} dosyanın biçimi tanınmadı. Bu dosyalar sayılmadı. Yardımcı kayıt biçimini değiştirmiş olabilir.");
            var remaining = r.Okunamayan - compressedCount - olderCount - archiveCount - unrecognized;
            if (remaining <= 0) continue;
            var countStr = remaining.ToString(CultureInfo.InvariantCulture);
            var topReason = r.Reasons.Where(kv => kv.Key is not (SkippedCompressed or SkippedOlderState or SkippedArchive or ProfileSources.ReasonUnrecognizedFormat))
                .OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "bozuk";
            var sentence = topReason switch
            {
                "sema-farkli" => $"{title} kaynağında {countStr} dosyanın kayıt biçimi farklı olduğu için okunamadı.",
                "kilitli" or "wal-var-shm-yok" => $"{title} kaynağında {countStr} dosya başka bir program tarafından kullanıldığı için okunamadı.",
                "okunamadi" => $"{title} kaynağında {countStr} dosya şu an okunamadı. Geçici bir sorun olabilir, sonraki taramada yeniden denenir.",
                _ => $"{title} kaynağında {countStr} dosya bozuk ya da yarım yazılmış olduğu için okunamadı.",
            };
            list.Add(sentence);
        }
        if (compressedCodexWithoutSqlite > 0)
        {
            var n = compressedCodexWithoutSqlite.ToString(CultureInfo.InvariantCulture);
            list.Add($"Codex kaynağında sıkıştırılmış {n} kayıt dosyası var ve özet dosyası bulunamadığı için okunamadı.");
        }
        return list;
    }

    private static bool PidAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Ucuz süreç yoklaması: Claude Code açık mı? Kayıt dosyasındaki süreçlerden biri yaşıyorsa ya da "claude" adlı süreç varsa evet (tarama birkaç ms).</summary>
    private static bool SystemClaudePresent(IReadOnlyList<ClaudeRegistryEntry> registry)
    {
        if (registry.Any(e => PidAlive(e.Pid))) return true;
        try
        {
            var found = Process.GetProcessesByName("claude");
            var any = found.Length > 0;
            foreach (var p in found) p.Dispose();
            return any;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; } // sayılamadı: güvenli taraf, komut çalışsın
    }

    /// <summary>
    /// Claude'un açık oturum listesi (ağır <c>claude agents --json</c> komutu). Kurallar (<see cref="LiveCadence"/>):
    /// Claude süreci yoksa komut HİÇ çalışmaz (liste kesin boştur); oturumların hepsi kancayla haber veriyorsa en az seyrek aralıkta; aksi halde
    /// biri bakarken 15 sn, bakmıyorken 60 sn aralıkta; ardışık başarısızlıkta aralık uzar; asla üst üste binmez. <paramref name="force"/> (tam tarama) kuralları atlar.
    /// </summary>
    private ClaudeAgentsResult ReadAgents(OnboardingRoots roots, DateTime now, SozSettings settings, bool force)
    {
        var command = AgentsCommand ?? (roots.IsSynthetic ? null : new ClaudeAgentsProcessCommand(settings.ClaudePath));
        if (command is null) return ClaudeAgents.Read(null, roots.ClaudeHome);
        lock (_agentsGate)
        {
            var registry = ClaudeSessionRegistry.Read(roots.ClaudeHome);
            var present = ClaudePresent is { } custom ? custom(registry) : AgentsCommand is not null || SystemClaudePresent(registry);
            if (!force && !present)
            {
                _agents = null;
                Cadence.CommandSucceeded();
                return new ClaudeAgentsResult([], LiveRules.SourceCommand, null); // Claude süreci yok: komutun vereceği liste de kesin boş
            }
            var covered = !force && Cadence.Covered(registry.Where(e => AgentsCommand is not null || PidAlive(e.Pid)).Select(e => e.SessionId).ToArray());
            var cached = _agents;
            var due = force || cached is null || now < cached.At || now - cached.At >= Cadence.CommandInterval(now, covered) || Cadence.WakeAt > cached.At;
            if (!due)
            {
                if (cached!.Result.Source == LiveRules.SourceCommand) return cached.Result;
                return ClaudeAgents.Read(null, roots.ClaudeHome) with { Note = cached.Result.Note, Technical = cached.Result.Technical }; // komut geri çekilmede: kayıt dosyaları taze, neden aynı
            }
            var result = ClaudeAgents.Read(command, roots.ClaudeHome);
            _agents = new AgentsCache(now, result);
            if (result.Source == LiveRules.SourceCommand) Cadence.CommandSucceeded(); else Cadence.CommandFailed();
            return result;
        }
    }

    private LiveHintContext ReadLiveHints(OnboardingRoots roots, DateTime now, SozSettings settings, bool force)
    {
        var hermesActive = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var activeFile = Path.Combine(roots.HermesHome, "runtime", "active_sessions.json");
            if (File.Exists(activeFile))
            {
                var fi = new FileInfo(activeFile);
                if (fi.Length <= 512 * 1024 && fi.LastWriteTimeUtc >= now.AddHours(-2))
                {
                    using var fs = new FileStream(activeFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
                    using var doc = JsonDocument.Parse(fs);
                    ExtractActiveHermesIds(doc.RootElement, hermesActive);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }

        var agPresence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var presenceDir = Path.Combine(roots.AntigravityHome, "presence");
            if (Directory.Exists(presenceDir))
            {
                foreach (var lockFile in Directory.EnumerateFiles(presenceDir, "*.lock", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(lockFile);
                    if (fi.LastWriteTimeUtc >= now.AddHours(-2))
                    {
                        var convId = Path.GetFileNameWithoutExtension(fi.Name);
                        if (!string.IsNullOrWhiteSpace(convId)) agPresence.Add(convId.Trim());
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        // Claude: birincil kaynak `claude agents --json`; sentetik köklerde komut çalıştırılmaz (gerçek claude'a dokunmasın). Sıklık kuralları ReadAgents'ta.
        var agents = ReadAgents(roots, now, settings, force);
        return new LiveHintContext(hermesActive, agPresence, agents.Entries, agents.Source, agents.Note);
    }

    private static void ExtractActiveHermesIds(JsonElement root, HashSet<string> target)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) target.Add(s);
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var id = (item.TryGetProperty("session_id", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null)
                             ?? (item.TryGetProperty("id", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null);
                    if (!string.IsNullOrWhiteSpace(id)) target.Add(id);
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("sessions", out var sessEl) && sessEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                ExtractActiveHermesIds(sessEl, target);
                return;
            }
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.Number or JsonValueKind.String)
                    target.Add(prop.Name);
            }
        }
    }

    /// <summary>Taranmış oturumun başlığı (sekme eşleştirmesine ipucu). Yoksa ya da başlıksızsa null; sohbet metni değil, aracın kendi yazdığı başlıktır.</summary>
    public string? SessionTitle(string? tool, string sessionId)
    {
        try
        {
            using var c = db.Open();
            var title = string.IsNullOrWhiteSpace(tool)
                ? c.Scalar("SELECT title FROM profile_session WHERE present=1 AND session_id=@i AND title IS NOT NULL ORDER BY last_activity DESC LIMIT 1", null, ("@i", sessionId)) as string
                : c.Scalar("SELECT title FROM profile_session WHERE present=1 AND tool=@t AND session_id=@i AND title IS NOT NULL ORDER BY last_activity DESC LIMIT 1", null, ("@t", tool), ("@i", sessionId)) as string;
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException) { return null; }
    }

    public JsonObject Sessions(string project, int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        using var c = db.Open();
        // Aynı oturum iki kayıt dosyasından gelebilir (ör. Codex özet veritabanı ve rollout dosyası): tarama bunları tek oturuma indirir, liste ve sayı da öyle olmalı (D6-25).
        var total = c.ScalarLong("""
            SELECT COUNT(*) FROM (
              SELECT 1 FROM profile_session WHERE present=1 AND project_path=@p COLLATE NOCASE AND tool NOT IN ('codex','antigravity')
              UNION ALL
              SELECT 1 FROM (SELECT DISTINCT tool,session_id FROM profile_session WHERE present=1 AND project_path=@p COLLATE NOCASE AND tool IN ('codex','antigravity')))
            """, null, ("@p", project));
        var raw = c.Query("""
            SELECT tool,session_id,cwd,git_branch,started_at,last_activity,parent_id,record_path,record_modified_at,observed_at,source_type,git_repo_root,source_channel,title
            FROM profile_session WHERE present=1 AND project_path=@p COLLATE NOCASE
            ORDER BY last_activity DESC,tool,session_id,record_path LIMIT @fetch
            """, r => Stored(r), null, ("@p", project), ("@fetch", limit * 3 + 50));
        var rows = DeduplicateSessions(raw).OrderByDescending(r => r.LastActivity ?? DateTime.MinValue).ThenBy(r => r.Tool, StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.RecordPath, StringComparer.Ordinal).Take(limit).ToList();
        // Başlıksız yardımcı oturumlar: ana oturumun başlığı ve kardeş sırası için sınır dışı kayıtlar da okunur.
        var parentIds = rows.Where(r => !string.IsNullOrWhiteSpace(r.ParentId) && string.IsNullOrWhiteSpace(r.Title)).Select(r => r.ParentId!.Trim()).Distinct(StringComparer.Ordinal).Take(500).ToArray();
        if (parentIds.Length > 0)
        {
            var marks = string.Join(",", parentIds.Select((_, i) => "@x" + i.ToString(CultureInfo.InvariantCulture)));
            var parameters = parentIds.Select((id, i) => ("@x" + i.ToString(CultureInfo.InvariantCulture), (object?)id)).ToArray();
            var context = c.Query($"""
                SELECT tool,session_id,cwd,git_branch,started_at,last_activity,parent_id,record_path,record_modified_at,observed_at,source_type,git_repo_root,source_channel,title
                FROM profile_session WHERE present=1 AND (session_id IN ({marks}) OR parent_id IN ({marks}))
                """, r => Stored(r), null, parameters);
            rows = SubSessionTitles.Fill(rows, context);
        }
        return new() { ["project"] = project, ["limit"] = limit, ["total"] = total, ["count"] = rows.Count, ["sessions"] = JsonSerializer.SerializeToNode(rows, JsonOptions) };
    }

    private static string? Iso(DateTime? value) => value?.ToString("O", CultureInfo.InvariantCulture);

    private static SessionMetadata Stored(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.Str(2), r.Str(3), ProfileSources.ParseTimestamp(r.Str(4)),
        ProfileSources.ParseTimestamp(r.Str(5)), r.Str(6), r.GetString(7), ProfileSources.ParseTimestamp(r.Str(8)) ?? DateTime.UnixEpoch,
        ProfileSources.ParseTimestamp(r.Str(9)) ?? DateTime.UnixEpoch, r.GetString(10), GitRepoRoot: r.Str(11), SourceChannel: r.Str(12), Title: r.Str(13));

    private static void CleanLegacyTimes(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "lastActivity", "firstActivity" })
                if (obj[key] is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith("0001-", StringComparison.Ordinal)) obj[key] = null;
            foreach (var child in obj.Select(p => p.Value).ToArray()) CleanLegacyTimes(child);
        }
        else if (node is JsonArray array) foreach (var child in array) CleanLegacyTimes(child);
    }

    private static string? OwnTemporaryDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Soz.sln"))) return Path.Combine(d.FullName, "_gecici");
        return null;
    }
}
