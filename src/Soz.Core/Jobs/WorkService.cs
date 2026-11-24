using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Guard;
using Soz.Core.Hooks;
using Soz.Core.Morning;
using Soz.Core.Proof;
using Soz.Core.Queue;
using Soz.Core.Run;
using Soz.Core.Readers;
using Soz.Core.Sessions;
using Soz.Core.Text;

namespace Soz.Core.Jobs;

/// <summary>İş kartı, kanıt kapısı, kuyruk, sabah raporu ve Bugün için CLI ile sunucunun ortak katmanı.</summary>
public sealed class WorkService
{
    public SozDb Db { get; }
    public WorkService(SozDb db) => Db = db;
    public static WorkService Open(string dataDir) => new(SozDb.OpenAndMigrate(dataDir));

    /// <summary>Değişikliği yapan kanal ("cli", "api", "telefon"). Ölçüt ve kapı değişiklikleri events tablosuna bu adla yazılır.</summary>
    public string Actor { get; init; } = "cli";

    private static readonly System.Text.Json.JsonSerializerOptions EventJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Ölçüt tanımının kısa özeti (olay kaydı için); metinler sır süzgecinden geçer.</summary>
    private static JsonObject Summ(CheckDto? x)
    {
        if (x is null) return new JsonObject();
        var spec = new JsonObject();
        foreach (var (k, v) in x.Spec)
            spec[k] = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? JsonValue.Create(SecretFilter.MaskSecrets(s.Length > 300 ? s[..300] + "…" : s)) : v?.DeepClone();
        return new JsonObject { ["id"] = x.Id, ["jobId"] = x.JobId, ["kind"] = x.Kind, ["label"] = SecretFilter.MaskSecrets(x.Label), ["enabled"] = x.Enabled, ["timeoutS"] = x.TimeoutS, ["spec"] = spec };
    }

    /// <summary>Ölçüt ve kapı değişikliği her zaman olay olarak yazılır (kim: api/cli, eski ve yeni tanım). Yazılamazsa değişiklik de yapılmaz (hata dışarı çıkar).</summary>
    private void LogChange(SqliteConnection c, SqliteTransaction tx, string kind, long? projectId, JsonObject? oldV, JsonObject? newV)
    {
        var d = new JsonObject { ["actor"] = Actor, ["old"] = oldV, ["new"] = newV };
        Events.Log(c, kind, projectId, d.ToJsonString(EventJson), tx);
    }

    public Func<string?>? EdgeLocatorOverride { get; set; }

    private SqliteConnection Conn() => Db.Open();

    public static int GateBudgetSeconds(SozSettings s) => s.ClampedInt("gateBudgetSeconds");
    public static int GateMaxAttempts(SozSettings s) => s.ClampedInt("gateMaxAttempts");

    private static void RequireProject(SqliteConnection c, long projectId)
    {
        if (c.Scalar("SELECT id FROM projects WHERE id=@p AND key<>'*'", null, ("@p", projectId)) is null) throw new SozException(404, $"Proje bulunamadı: {projectId}");
    }

    /// <summary>Klasör yoluyla projeyi bulur ya da kaydeder (yalnız var olan klasör).</summary>
    public long EnsureProjectByPath(string path)
    {
        if (CheckProjectPath(path, null, Db.DataDir) is { } bad) throw new SozException(400, bad);
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new SozException(404, $"Klasör yok: {full}");
        using var c = Conn();
        var id = IngestStore.EnsureProject(c, null, full);
        if (c.Scalar("SELECT uid FROM projects WHERE id=@i", null, ("@i", id)) is string puid) ProjectPaths.Seen(c, null, puid, full); // yeniden görüldü: geçmiş yolu yenilenir
        c.Exec("UPDATE projects SET added=1 WHERE id=@i AND COALESCE(added,0)=0", null, ("@i", id)); // elle eklenen proje arayüz listesinde görünür
        return id;
    }

    // ---------- proje yolu koruması (denetim 2, Ö-4) ----------
    private static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var r = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
        var p = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Proje olarak eklenemeyecek yerler: göreli yol, ağ (UNC) yolu, sürücü kökü, Windows klasörü ve altı, Program Files (x86 dahil), ProgramData,
    /// kullanıcının ev klasörünün KENDİSİ (altındaki klasörler serbest), .claude ya da .codex klasörü ve altı. Sorun yoksa null, varsa sade hata cümlesi.
    /// </summary>
    public static string? CheckProjectPath(string? raw, string? homeOverride = null, string? dataDir = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Klasör yolu gerekli.";
        var path = raw.Trim().Trim('"');
        if (path.StartsWith(@"\\") || path.StartsWith("//")) return "Ağ klasörleri proje olarak eklenemez. Bu bilgisayardaki bir klasörü seç.";
        if (!System.IO.Path.IsPathFullyQualified(path)) return @"Klasör yolunun tam yazılması gerekir (ör. D:\projelerim\benim-isim).";
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "Klasör yolu geçersiz."; }
        // Denetim 3, Y6: yol bir bağlantı (junction/sembolik bağlantı) olabilir. Gerçek hedef çözülür; yazılan yol ve gerçek hedef ikisi de denenir.
        var real = RealPath(full);
        if (real.StartsWith(@"\\") || real.StartsWith("//")) return "Ağ klasörleri proje olarak eklenemez. Bu bilgisayardaki bir klasörü seç.";
        var bad = CheckOne(full, homeOverride, dataDir);
        if (bad is not null) return bad;
        if (!real.Equals(full, StringComparison.OrdinalIgnoreCase)) bad = CheckOne(real, homeOverride, dataDir);
        return bad;
    }

    private static string? CheckOne(string full, string? homeOverride, string? dataDir)
    {
        var trimmed = full.TrimEnd('\\', '/');
        var root = System.IO.Path.GetPathRoot(full)?.TrimEnd('\\', '/') ?? "";
        if (trimmed.Length == 0 || trimmed.Equals(root, StringComparison.OrdinalIgnoreCase)) return "Bir sürücünün tamamı proje olarak eklenemez. İçindeki bir klasörü seç.";
        foreach (var f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
            if (IsUnder(full, Environment.GetFolderPath(f))) return "Windows ve program klasörleri proje olarak eklenemez. Kendi proje klasörünü seç.";
        var home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeReal = string.IsNullOrWhiteSpace(home) ? null : RealPath(System.IO.Path.GetFullPath(home));
        foreach (var h0 in new[] { home, homeReal }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var h = System.IO.Path.GetFullPath(h0!).TrimEnd('\\', '/');
            if (trimmed.Equals(h, StringComparison.OrdinalIgnoreCase)) return "Kullanıcı klasörünün kendisi proje olarak eklenemez. Altındaki bir proje klasörünü seç.";
            var parent = System.IO.Path.GetDirectoryName(h);
            if (!string.IsNullOrEmpty(parent) && trimmed.Equals(parent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return "Kullanıcılar klasörü proje olarak eklenemez. Kendi proje klasörünü seç.";
            // Uygulama verisi (AppData: Roaming, Local, LocalLow): tarayıcı ve program ayarları burada durur.
            if (IsUnder(full, System.IO.Path.Combine(h, "AppData"))) return "Uygulama verisi klasörleri (AppData) proje olarak eklenemez. Kendi proje klasörünü seç.";
        }
        foreach (var f in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            if (homeOverride is null && IsUnder(full, Environment.GetFolderPath(f))) return "Uygulama verisi klasörleri (AppData) proje olarak eklenemez. Kendi proje klasörünü seç.";
        if (trimmed.Split('\\', '/').Any(s => s.Equals(".claude", StringComparison.OrdinalIgnoreCase) || s.Equals(".codex", StringComparison.OrdinalIgnoreCase)))
            return "Claude ve Codex'in kendi ayar klasörleri proje olarak eklenemez.";
        if (!string.IsNullOrWhiteSpace(dataDir) && (IsUnder(full, dataDir) || IsUnder(full, RealPath(System.IO.Path.GetFullPath(dataDir)))))
            return "Söz'ün kendi veri klasörü proje olarak eklenemez.";
        return null;
    }

    /// <summary>
    /// Yolun gerçek hâli: yoldaki her klasör bağlantıysa (junction, sembolik bağlantı) hedefiyle değiştirilir. Çözülemeyen (var olmayan, erişilemeyen) parça olduğu gibi kalır.
    /// </summary>
    public static string RealPath(string full, int depth = 0)
    {
        try
        {
            full = System.IO.Path.GetFullPath(full);
            if (depth > 8) return full;
            var root = System.IO.Path.GetPathRoot(full) ?? "";
            var rest = full[root.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var cur = root;
            for (var i = 0; i < rest.Length; i++)
            {
                cur = System.IO.Path.Combine(cur, rest[i]);
                FileSystemInfo? info = Directory.Exists(cur) ? new DirectoryInfo(cur) : File.Exists(cur) ? new FileInfo(cur) : null;
                if (info is null) { cur = System.IO.Path.Combine(new[] { cur }.Concat(rest.Skip(i + 1)).ToArray()); break; }
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var target = info.ResolveLinkTarget(true);
                if (target is null) continue;
                var rt = RealPath(target.FullName, depth + 1);
                cur = rt.TrimEnd('\\', '/');
                if (cur.Length == 2 && cur[1] == ':') cur += "\\";
            }
            return cur.Length > 3 ? cur.TrimEnd('\\', '/') : cur;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return full; }
    }

    /// <summary>Terminal kancası hedefi kullanıcının kendi .claude klasörü mü? (çift emniyet: her koşulda reddedilir)</summary>
    public static bool IsUserClaudeSettings(string settingsPath, string? homeOverride = null)
    {
        var home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return false;
        return IsUnder(settingsPath, System.IO.Path.Combine(home, ".claude"));
    }

    // ---------- proje ekleme (tek adım kurulum) ----------
    public sealed record ProjectAddResult(long Id, string Name, string Path, bool Created, bool GateEnabled, List<CheckDto> Suggested, string Card);

    /// <summary>
    /// Klasörü ekler (yalnız var olan klasör), ölçütleri KAPALI önerir (hiçbir komut çalıştırılmaz) ve tek onay kartının metnini döndürür.
    /// Aynı klasör ikinci kez eklenirse yeni kayıt açılmaz; öneriler yenilenir.
    /// </summary>
    public ProjectAddResult AddProject(string path)
    {
        if (CheckProjectPath(path, null, Db.DataDir) is { } bad) throw new SozException(400, bad);
        var full = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
        if (!Directory.Exists(full)) throw new SozException(404, $"Bu klasör yok: {full}");
        bool created;
        using (var c = Conn())
            created = c.Scalar("SELECT id FROM projects WHERE key=@k", null, ("@k", TextNormalizer.ProjectKey(full))) is null;
        var id = EnsureProjectByPath(full);
        using var c2 = Conn();
        var suggested = CheckStore.Suggest(c2, id, full).Checks.Where(x => x.JobId is null && x.Kind == CheckKinds.Command).ToList();
        var gate = c2.ScalarLong("SELECT COALESCE(gate_enabled,0) FROM projects WHERE id=@i", null, ("@i", id)) == 1;
        var name = c2.Scalar("SELECT name FROM projects WHERE id=@i", null, ("@i", id)) as string ?? full;
        return new ProjectAddResult(id, name, full, created, gate, suggested, ApprovalCard(suggested));
    }

    public sealed record ProjectRelocateResult(long Id, string Name, string OldPath, string Path, bool Changed, int SnapshotsMoved);

    /// <summary>
    /// Projenin klasör yerini günceller (klasör taşındı, kopyalandı ya da yeniden adlandırıldı). Kimlik, işler, ölçütler ve geçmiş aynı kalır; yol geçmişine yeni yol eklenir,
    /// geri alma noktaları yeni klasöre bağlanır, olay defterine "project.relocated" yazılır. Yeni yol başka bir projeye aitse 409. Yalnız insan çağırır (CLI'da ajana kapalı).
    /// </summary>
    public ProjectRelocateResult RelocateProject(long projectId, string newPath)
    {
        if (CheckProjectPath(newPath, null, Db.DataDir) is { } bad) throw new SozException(400, bad);
        var full = System.IO.Path.GetFullPath(newPath.Trim().Trim('"'));
        if (!Directory.Exists(full)) throw new SozException(404, $"Bu klasör yok: {full}");
        string uid, name, old;
        using (var c = Conn())
        {
            var row = c.Query("SELECT uid, COALESCE(NULLIF(name,''), key), COALESCE(path,'') FROM projects WHERE id=@i AND key<>'*'", r => (r.GetString(0), r.GetString(1), r.GetString(2)), null, ("@i", projectId)).FirstOrDefault();
            if (row == default) throw new SozException(404, $"Proje bulunamadı: {projectId}");
            (uid, name, old) = row;
            var key = TextNormalizer.ProjectKey(full);
            if (c.Scalar("SELECT id FROM projects WHERE key=@k AND id<>@i", null, ("@k", key), ("@i", projectId)) is { } other)
                throw new SozException(409, $"Bu klasör zaten başka bir proje olarak kayıtlı (#{Convert.ToInt64(other)}). Başka bir klasör seç.");
            if (string.Equals(old.TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return new ProjectRelocateResult(projectId, name, old, full, false, 0);
            using var tx = c.BeginTransaction();
            c.Exec("UPDATE projects SET path=@p, key=@k WHERE id=@i", tx, ("@p", full), ("@k", key), ("@i", projectId));
            ProjectPaths.Seen(c, tx, uid, full);
            c.Exec("INSERT OR IGNORE INTO event_kind(kind, owner, schema_v, since, description) VALUES('project.relocated','soz.core',1,@t,'Projenin klasör yeri güncellendi (project, old_name, new_name).')", tx, ("@t", Time.NowIso()));
            Events.Append(c, "project.relocated", "project", uid, uid, null, Actor == "cli" ? "kisi" : Actor,
                new JsonObject { ["project"] = name, ["old_name"] = System.IO.Path.GetFileName(old.TrimEnd('\\', '/')), ["new_name"] = System.IO.Path.GetFileName(full.TrimEnd('\\', '/')) }.ToJsonString(), tx);
            tx.Commit();
        }
        int moved;
        using (var rs = new Soz.Core.Restore.RestoreService(Db)) moved = rs.RelocateRoot(uid, full);
        return new ProjectRelocateResult(projectId, name, old, full, true, moved);
    }

    /// <summary>Onay kartının cümlesi: ölçüt varsa "… şunu çalıştırıp deneyeceğim: `npm test`. Olur mu?", yoksa kanıtsız yürüyeceğini söyler.</summary>
    public static string ApprovalCard(IReadOnlyList<CheckDto> checks)
    {
        var cmds = checks.Select(x => x.Spec["cmd"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : x.Label).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (cmds.Count == 0)
            return "Bu klasörde otomatik bir test bulamadım. İş bitince neyi deneyeyim? Aşağıdan bir cümle seç. Hiçbirini seçmezsen iş denenmeden yürür ve öyle etiketlenir.";
        var list = string.Join(", ", cmds.Select(x => "`" + x + "`"));
        return cmds.Count == 1
            ? $"Bu klasörde iş bitince şunu çalıştırıp deneyeceğim: {list}. Olur mu?"
            : $"Bu klasörde iş bitince şunları çalıştırıp deneyeceğim: {list}. Olur mu?";
    }

    public sealed record ApproveSetupResult(ProjectGateDto Gate, List<CheckDto> Checks, string? GuardTemplate, int GuardRulesCreated);

    /// <summary>
    /// TEK ONAY: önerilen (kapalı) proje ölçütlerinin hepsini (ya da verilenleri) açar ve kanıt kapısını açar. Kullanıcı bu çağrıyı kendisi yapar; öncesinde hiçbir komut çalışmaz.
    /// checkIds verilmezse ajanın önerdiği deneme ölçütü (origin agent-test) AÇILMAZ: onu yalnız kişi açıkça kabul eder. guardTemplate verilirse (ör. "temkinli") bekçi şablonu da uygulanır. Her değişiklik olay olarak yazılır.
    /// </summary>
    public ApproveSetupResult ApproveSetup(long projectId, IReadOnlyList<long>? checkIds = null, bool gate = true, string? guardTemplate = null)
    {
        List<CheckDto> chosen;
        using (var c = Conn())
        {
            RequireProject(c, projectId);
            var all = CheckStore.List(c, projectId).Where(x => x.JobId is null).ToList();
            chosen = checkIds is { Count: > 0 } ? all.Where(x => checkIds.Contains(x.Id)).ToList() : all.Where(x => !x.Enabled && x.Origin != CheckStore.AgentTestOrigin).ToList(); // ajanın önerdiği (kapalı) deneme yalnız kişinin açık kabulüyle (checkIds ya da PATCH) açılır
            if (checkIds is { Count: > 0 } && chosen.Count != checkIds.Distinct().Count()) throw new SozException(400, "Kontrollerden biri bu projeye ait değil.");
        }
        foreach (var ch in chosen.Where(x => !x.Enabled)) PatchCheck(ch.Id, enabled: true);
        var g = gate ? SetGate(projectId, true) : ProjectGate(projectId);
        var created = 0;
        string? applied = null;
        if (!string.IsNullOrWhiteSpace(guardTemplate))
        {
            using var c = Conn();
            GuardStore.EnsureTables(c);
            var r = GuardStore.ApplyTemplate(c, projectId, guardTemplate!);
            created = r.Created.Count;
            applied = r.Template;
        }
        using var c3 = Conn();
        return new ApproveSetupResult(g, CheckStore.List(c3, projectId).Where(x => x.JobId is null).ToList(), applied, created);
    }

    // ---------- Söz ajanı: zaman çizelgesi ve terminal kancaları ----------
    public List<TimelineItem> Timeline(long jobId, bool detail)
    {
        using var c = Conn();
        if (JobStore.Get(c, jobId) is null) throw new SozException(404, $"İş bulunamadı: {jobId}");
        return RunStore.Timeline(c, jobId, detail);
    }

    public sealed record TerminalHooksResult(string SettingsPath, string Exe, bool Installed, string[] Hooks, Dictionary<string, string>? Transport = null, string? Backup = null);

    /// <summary>"Bu klasörde terminalden de çalışacağım": YALNIZ seçilen projenin klasöründeki .claude\settings.json dosyasına Söz kancalarını kurar (mevcut içerik korunur, .bak yedeği alınır).</summary>
    public TerminalHooksResult InstallTerminalHooks(long projectId, string exePath)
    {
        string? root; int budget; string? claudePath; bool fastMode;
        using (var c = Conn())
        {
            RequireProject(c, projectId);
            root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
            var st = SozSettings.Load(c);
            budget = GateBudgetSeconds(st);
            claudePath = st.ClaudePath;
            fastMode = st.Bool("hooksFastMode");
        }
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new SozException(409, "Projenin klasörü bilinmiyor ya da yok.");
        if (!File.Exists(exePath)) throw new SozException(409, "soz.exe bulunamadı. Terminal desteği kurulamadı.");
        var kancaPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exePath) ?? "", "soz-kanca.exe");
        if (File.Exists(kancaPath)) exePath = kancaPath;
        // Denetim 3, Y6: proje klasörü ya da .claude bir bağlantı olabilir; kullanıcının kendi ayar dosyasına gerçek hedefle de bakılır.
        if (CheckProjectPath(root, null, Db.DataDir) is { } badRoot) throw new SozException(400, badRoot);
        var settings = System.IO.Path.Combine(root, ".claude", "settings.json");
        // Ayar dosyası gerçekten projenin kendi klasöründe durmalı: .claude başka yere bağlantıysa (ör. kullanıcının kendi .claude klasörü) yazılmaz.
        if (!IsUnder(RealPath(settings), RealPath(root))) throw new SozException(400, "Bu klasörün .claude klasörü başka bir yere bağlı. Terminal desteği oraya yazılmaz.");
        if (IsUserClaudeSettings(settings) || IsUserClaudeSettings(RealPath(settings))) throw new SozException(400, "Bu klasörün terminal desteği ayarı kullanıcının kendi Claude ayar dosyasına denk geliyor. Oraya yazılmaz.");
        // Varsayılan komut yolu (soz-kanca.exe, sessiz). http yalnız "hızlı kip" açıksa (Ayarlar ya da SOZ_HOOK_TRANSPORT=http), Söz açıkken ve Claude Code sürümü yeterliyse.
        var http = HookTransportPlanner.Plan(Db.DataDir, claudePath, fastMode: fastMode);
        HookInstallResult installed;
        try { installed = HookInstaller.InstallDetailed(settings, exePath, Db.DataDir, HookInstaller.DotnetRootFor(exePath), null, budget, http); }
        catch (InvalidDataException e) { throw new SozException(409, e.Message); }
        using var c2 = Conn();
        EvidenceBase.RefreshApprovedUnder(c2, projectId, root, ".claude/"); // Söz'ün kendi yazdığı ayar dosyası kuşkulu sayılmasın
        Events.Log(c2, "hooks_installed", projectId, System.Text.Json.JsonSerializer.Serialize(new { actor = Actor, settings }));
        return new TerminalHooksResult(settings, exePath, true, HookInstaller.Hooks.Select(h => h.Event).ToArray(), new Dictionary<string, string>(installed.Transport), installed.BackupPath);
    }

    public bool UninstallTerminalHooks(long projectId)
    {
        using var c = Conn();
        RequireProject(c, projectId);
        var root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
        if (string.IsNullOrWhiteSpace(root)) return false;
        var removed = HookInstaller.Uninstall(System.IO.Path.Combine(root, ".claude", "settings.json"));
        EvidenceBase.RefreshApprovedUnder(c, projectId, root, ".claude/");
        return removed;
    }

    // ---------- işler ----------
    public JobDto CreateJob(long projectId, string rawText, string source = "yazi", bool restate = false, bool night = false, IReadOnlyList<long>? checkIds = null, string? title = null, string? agent = null, string? kind = null, bool writeCheckFirst = false)
    {
        if (string.IsNullOrWhiteSpace(rawText)) throw new SozException(400, "İş metni boş olamaz.");
        if (source is not ("dikte" or "yazi")) throw new SozException(400, "source dikte ya da yazi olmalı.");
        using var c = Conn();
        RequireProject(c, projectId);
        var st = SozSettings.Load(c);
        // Ajan verilmediyse ayardaki varsayılan; bilinmeyen kimlik 400 (sessizce başka ajana düşmez).
        var agentId = Soz.Core.Agents.AgentRegistry.Get(string.IsNullOrWhiteSpace(agent) ? st.DefaultAgent : agent).Id;
        return JobStore.Create(c, projectId, rawText, source, restate, night, checkIds ?? Array.Empty<long>(), title, st.Language, agentId, kind, writeCheckFirst);
    }

    public List<JobDto> Jobs(long? projectId = null, string? state = null, int limit = 50)
    {
        if (state is not null && !JobStates.All.Contains(state)) throw new SozException(400, "Geçersiz iş durumu.");
        using var c = Conn();
        return JobStore.List(c, projectId, state, limit);
    }

    public JobDto Job(long id)
    {
        using var c = Conn();
        var j = JobStore.Get(c, id) ?? throw new SozException(404, $"İş bulunamadı: {id}");
        return j with { Runs = ProofStore.List(c, null, id, 50) };
    }

    /// <summary>
    /// Kullanıcı işi arayüzde açtı. BAKMAK KAPATMAK DEĞİLDİR (k1): bu çağrı artık hiçbir şeyi işaretlemez, "Sıra bende" satırı yerinde kalır.
    /// Satır yalnız şunlarla düşer: kişi "Gördüm" der (POST /api/attention/ack), işi kabul eder ya da kabul etmez, geri alır ya da iş yeniden çalışır.
    /// Uç eski arayüzler için durur (404 yalnız iş yoksa); arayüz bunu çağırmayı bırakmalı.
    /// </summary>
    public void MarkSeen(long id)
    {
        using var c = Conn();
        _ = JobStore.Get(c, id) ?? throw new SozException(404, $"İş bulunamadı: {id}");
    }

    /// <summary>İşin ajan süreci hâlâ canlı mı? (Başlangıç ölçümü sürerken süreç numarası henüz yoktur: 10 dakikaya kadar canlı sayılır.)</summary>
    public static bool HasLiveRun(SqliteConnection c, long jobId)
    {
        foreach (var r in RunStore.ListForJob(c, jobId, 5).Where(x => x.Status == RunStatus.Running))
        {
            var (pid, start) = RunStore.ProcessOf(c, r.Id);
            if (pid is null)
            {
                if (Time.Parse(r.Started) is { } st && (Time.UtcNow() - st).TotalMinutes < 10) return true;
                continue;
            }
            if (Proof.ProcessTree.IsAlive(pid.Value, start)) return true;
        }
        return false;
    }

    /// <summary>Kabul isteğinin taşıdığı görüntü kimliği şimdiki durumla uyuşmadığında verilen metinler (denetim 4, E).</summary>
    public const string StaleCardMessage = "Kart açıldıktan sonra dosyalar değişti, yeniden ölçüyorum.";
    public const string OldCardMessage = "Gösterilen kart eskimiş. Güncel karta bakıp yeniden karar ver.";

    /// <summary>
    /// "Bu sonucu kabul ediyorum" (K-1): yalnız kanıtı bozulmuş görünen ve kararı bekleyen iş (karar_bekliyor).
    /// accept=true: iş kullanıcı kabulüyle "geçti" olur (acceptedByUser=true), KARTIN GÖSTERDİĞİ görüntü yeni onaylı taban olur. accept=false: iş "kaldı" olur.
    /// Denetim 3: ajan süreci hâlâ canlıyken kabul EDİLEMEZ (409, çağıran kim olursa olsun). Kabul olayı çağıranın süreç numarası, adı ve kanalıyla yazılır; iş kartında kanal görünür.
    /// Denetim 4, E: kabul, kartın dayandığı anlık görüntü kimliğini (snapshotId) taşır. Şimdiki durum farklıysa 409 ve yeniden ölçüm tetiklenir: kabul, gösterileni onaylar.
    /// requireSnapshot: kart kimliği olmadan kabul edilemez (sunucu ucu); komut satırı ve testler kimliği kendisi okuyabilir.
    /// </summary>
    public JobDto Accept(long id, bool accept, Security.CallerInfo? caller = null, string? snapshotId = null, bool requireSnapshot = false)
    {
        EvidenceBase.Snapshot? cardSnap = null;
        using (var c = Conn())
        {
            var j = JobStore.Get(c, id) ?? throw new SozException(404, $"İş bulunamadı: {id}");
            if (j.State != JobStates.AwaitingDecision) throw new SozException(409, "Bu iş senin kararını beklemiyor.");
            if (HasLiveRun(c, id)) throw new SozException(409, "Yardımcı hâlâ çalışıyor. Kararı yardımcı durduktan sonra verebilirsin.");
            if (accept)
            {
                var latestId = c.Scalar("SELECT snapshot_id FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", null, ("@j", id)) as string;
                if (latestId is not null)
                {
                    if (requireSnapshot && string.IsNullOrWhiteSpace(snapshotId)) throw new SozException(400, "Kartın anlık görüntü kimliği (snapshotId) gerekli.");
                    var root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", j.ProjectId)) as string;
                    if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                    {
                        cardSnap = EvidenceBase.ScanProject(c, j.ProjectId, id, root);
                        var curId = EvidenceBase.SnapshotId(cardSnap);
                        if (curId != latestId)
                        {
                            Remeasure(j.ProjectId, id);
                            throw new SozException(409, StaleCardMessage);
                        }
                        if (!string.IsNullOrWhiteSpace(snapshotId) && snapshotId != latestId) throw new SozException(409, OldCardMessage);
                    }
                }
            }
            // Kabul olayı DURUM DEĞİŞMEDEN önce yazılır: iş "geçti" göründüğü anda kabul kaydı da vardır (yoksa iş "kanıtsız kabul" diye işaretlenirdi).
            var via = caller is { Pid: > 0 } ? Security.CallerIdentity.Channel(caller.Pid, caller.ProcessName) : Actor == "api" ? "arayüz" : "komut satırı";
            Events.Log(c, accept ? "job_accepted" : "job_rejected", j.ProjectId, System.Text.Json.JsonSerializer.Serialize(new { actor = Actor, jobId = id, via, pid = caller?.Pid ?? Environment.ProcessId, process = caller?.ProcessName ?? "", at = Time.NowIso() }));
            if (accept)
            {
                EvidenceBase.TakeForJob(c, id, force: true, given: cardSnap); // kartın gösterdiği görüntü artık onaylı
                ProofStore.AcceptLatest(c, id);
                c.Exec("UPDATE jobs SET accepted_by_user=1 WHERE id=@j", null, ("@j", id));
                JobStore.SetState(c, id, JobStates.Passed, j.ProjectId);
            }
            else JobStore.SetState(c, id, JobStates.Failed, j.ProjectId);
        }
        return Job(id);
    }

    /// <summary>
    /// "Ajanın yeni testlerini onaylı testlere kat" (e2): yalnız insan çağırabilir (CallerGate). İşin kayıtlı teklifindeki dosyalar, hâlâ aynıysa onaylı sınama kümesine girer ve taban ilerler.
    /// Teklif yoksa, cevaplandıysa ya da dosyalar sonradan değiştiyse 409.
    /// </summary>
    public JobDto ApproveNewTests(long id, string? scope = null, string? diffId = null)
    {
        scope = string.IsNullOrWhiteSpace(scope) ? EvidenceBase.ScopeAdded : scope;
        if (scope != EvidenceBase.ScopeAdded && scope != EvidenceBase.ScopeEdited)
            throw new SozException(409, "Eklenen testler ile değiştirilen testler ayrı ayrı kabul edilir. Birini seç.");
        using (var c = Conn())
        {
            var j = JobStore.Get(c, id) ?? throw new SozException(404, $"İş bulunamadı: {id}");
            var offer = EvidenceBase.PendingOffer(c, id);
            if (scope == EvidenceBase.ScopeAdded)
            {
                if (offer is null || offer.Added == 0) throw new SozException(409, "Bu işte onay bekleyen yeni test teklifi yok.");
            }
            else
            {
                if (offer is null || offer.Edited == 0 || offer.EditedId is null) throw new SozException(409, "Bu işte onay bekleyen bir test değişikliği yok.");
                if (string.IsNullOrWhiteSpace(diffId) || diffId != offer.EditedId || !EvidenceBase.OfferSeen(c, id, offer.EditedId))
                    throw new SozException(409, "Değişikliği görmeden kabul edemezsin. Önce yardımcının değiştirdiği satırlara bak.");
            }
            if (EvidenceBase.ApproveNewTests(c, j.ProjectId, id, scope: scope) == 0) throw new SozException(409, "Teklifteki dosyalar sonradan değişmiş ya da bulunamadı, katılamadı.");
        }
        return Job(id);
    }

    /// <summary>"Hayır": teklif sunucuda cevaplanmış sayılır (yalnız insan, CallerGate), olay defterine yazılır. scope: added, edited ya da all. Teklif yoksa 409.</summary>
    public JobDto DeclineNewTests(long id, string? scope = null)
    {
        scope = string.IsNullOrWhiteSpace(scope) ? "all" : scope;
        if (scope is not (EvidenceBase.ScopeAdded or EvidenceBase.ScopeEdited or "all")) throw new SozException(400, "scope added, edited ya da all olmalı.");
        using (var c = Conn())
        {
            var j = JobStore.Get(c, id) ?? throw new SozException(404, $"İş bulunamadı: {id}");
            var offer = EvidenceBase.PendingOffer(c, id);
            var open = scope == "all" ? offer is not null : scope == EvidenceBase.ScopeAdded ? offer is { Added: > 0 } : offer is { Edited: > 0 };
            if (!open) throw new SozException(409, "Bu işte cevap bekleyen bir test teklifi yok.");
            EvidenceBase.DeclineOffer(c, j.ProjectId, id, scope);
        }
        return Job(id);
    }

    /// <summary>
    /// Test değişikliği teklifinin fark özeti (e3). Okumak "gördü" kaydı yazar: değişikliği kabul etmek için bu kayıt ve fark kimliği gerekir.
    /// Teklif yoksa ya da cevaplandıysa 404.
    /// </summary>
    public TestOfferDto TestOffer(long id, bool human)
    {
        using var c = Conn();
        var j = JobStore.Get(c, id) ?? throw new SozException(404, $"İş bulunamadı: {id}");
        EvidenceBase.RefreshPendingOfferIfChanged(c, j.ProjectId, id);
        var offer = JobStore.OfferFor(c, id, true) ?? throw new SozException(404, "Bu işte bekleyen test teklifi yok.");
        if (offer.EditedId is { } eid) EvidenceBase.MarkOfferSeen(c, j.ProjectId, id, eid, human);
        return offer;
    }

    /// <summary>Kart eskidi: işin kanıtı arka planda yeniden ölçülür (yeni kart gelir). Hata sızmaz.</summary>
    private void Remeasure(long projectId, long jobId)
    {
        long runId;
        try { using var c = Conn(); runId = ProofStore.Begin(c, new ProofStore.RunRequest(projectId, jobId, null, "manual", 1)); }
        catch (SqliteException) { return; }
        _ = Task.Run(() =>
        {
            try { CompleteRun(runId); }
            catch (Exception e)
            {
                try { using var c = Conn(); c.Exec("UPDATE proof_runs SET verdict='error', summary=@s, finished_at=@n WHERE id=@id AND verdict IS NULL", null, ("@s", "Yeniden ölçüm hatası: " + e.Message), ("@n", Time.NowIso()), ("@id", runId)); } catch (Exception) { }
            }
        });
    }

    public JobDto PatchJob(long id, string state)
    {
        if (state is not (JobStates.Given or JobStates.Cancelled)) throw new SozException(400, "Yalnız verildi ya da iptal yapılabilir.");
        using var c = Conn();
        var j = JobStore.Get(c, id) ?? throw new SozException(404, $"İş bulunamadı: {id}");
        if (JobStates.IsFinal(j.State)) throw new SozException(409, "Bitmiş iş değiştirilemez.");
        if (state == JobStates.Given && j.State != JobStates.Draft) return Job(id); // zaten verilmiş / çalışıyor
        JobStore.SetState(c, id, state, j.ProjectId);
        return Job(id);
    }

    // ---------- ölçütler ----------
    public List<CheckDto> Checks(long projectId, long? jobId = null)
    {
        using var c = Conn();
        RequireProject(c, projectId);
        return CheckStore.List(c, projectId, jobId);
    }

    public SuggestResult SuggestChecks(long projectId)
    {
        using var c = Conn();
        RequireProject(c, projectId);
        var path = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) throw new SozException(409, "Projenin klasörü bilinmiyor ya da yok. Öneri çıkarılamadı.");
        return CheckStore.Suggest(c, projectId, path);
    }

    public CheckDto AddCheck(long projectId, long? jobId, string kind, string label, JsonObject spec, int? timeoutS, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new SozException(400, "Etiket boş olamaz.");
        var err = CheckStore.Validate(kind, spec);
        if (err is not null) throw new SozException(400, err);
        using var c = Conn();
        RequireProject(c, projectId);
        if (jobId is not null && c.Scalar("SELECT id FROM jobs WHERE id=@j AND project_id=@p", null, ("@j", jobId), ("@p", projectId)) is null)
            throw new SozException(404, "İş bu projede bulunamadı.");
        using var tx = c.BeginTransaction();
        var id = CheckStore.Insert(c, tx, projectId, jobId, kind, label, spec, timeoutS ?? CheckStore.DefaultTimeoutS, enabled, "user");
        var made = c.Query("SELECT id, project_id, job_id, kind, label, spec_json, timeout_s, enabled, origin, created_at, uid FROM checks WHERE id=@id", CheckStore.Map, tx, ("@id", id)).First();
        LogChange(c, tx, "check_added", projectId, null, Summ(made));
        tx.Commit();
        return made;
    }

    /// <summary>
    /// "Bitti sayılması için" kartının kabulü (k1). scope "job": ölçüt yalnız o işe bağlanır; "project": klasörde kalıcı olur (iş de onu görür).
    /// Alan göndermeyen eski istemci: jobId yoksa eski davranış (proje düzeyinde kalıcı, legacy=true); jobId var ama scope yoksa "job".
    /// İş yalnız başlamadan önce (taslak) ölçüt alabilir: başladıktan sonra eklemek ölçüt tanımının özetini değiştirir ve iş "ölçüt değişti" ile kanıtsız kalırdı.
    /// </summary>
    public (List<CheckDto> Created, string Scope, bool Legacy) AcceptDone(long projectId, long? jobId, string? scope, IReadOnlyList<(string Template, JsonObject? Values, bool Enabled)> items)
    {
        var sc = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim().ToLowerInvariant();
        if (sc is not (null or "job" or "project")) throw new SozException(400, "scope \"job\" (yalnız bu iş) ya da \"project\" (bu klasörde kalıcı) olmalı.");
        if (sc == "job" && jobId is null) throw new SozException(400, "scope \"job\" için jobId gerekli.");
        var legacy = sc is null && jobId is null;
        var effective = sc ?? (jobId is null ? "project" : "job");
        if (jobId is not null)
        {
            using var c = Conn();
            RequireProject(c, projectId);
            var st = c.Scalar("SELECT state FROM jobs WHERE id=@j AND project_id=@p", null, ("@j", jobId), ("@p", projectId)) as string
                ?? throw new SozException(404, "İş bu projede bulunamadı.");
            if (st != JobStates.Draft) throw new SozException(409, "Bu iş başladı. Kontrol ancak iş başlamadan önce eklenir. Yeni bir iş yaz ya da kalıcı kontrol için kontrol ekranını kullan.");
        }
        var created = new List<CheckDto>();
        foreach (var it in items)
            created.Add(AddCheckFromTemplate(projectId, effective == "job" ? jobId : null, it.Template, it.Values, null, it.Enabled));
        if (jobId is not null) { using var c2 = Conn(); JobStore.RefreshDraftPrompt(c2, jobId.Value); }
        return (created, effective, legacy);
    }

    public CheckTemplates.Catalog CheckTemplateCatalog(long projectId)
    {
        using var c = Conn();
        RequireProject(c, projectId);
        return CheckTemplates.For(c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string);
    }

    /// <summary>Düz cümleden ölçüt (EK A). Etiket kullanıcının cümlesidir; kullanıcı kendisi eklediği için varsayılan açık.</summary>
    public CheckDto AddCheckFromTemplate(long projectId, long? jobId, string template, JsonObject? values, int? timeoutS, bool enabled)
    {
        string? root;
        using (var c = Conn())
        {
            RequireProject(c, projectId);
            root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
        }
        var (kind, label, spec) = CheckTemplates.Build(template, values, root);
        return AddCheck(projectId, jobId, kind, label, spec, timeoutS, enabled);
    }

    public CheckDto PatchCheck(long id, bool? enabled = null, string? label = null, JsonObject? spec = null, int? timeoutS = null)
    {
        using var c = Conn();
        var cur = CheckStore.Get(c, id) ?? throw new SozException(404, $"Kontrol bulunamadı: {id}");
        if (label is not null && string.IsNullOrWhiteSpace(label)) throw new SozException(400, "Etiket boş olamaz.");
        if (spec is not null && CheckStore.Validate(cur.Kind, spec) is { } err) throw new SozException(400, err);
        // N5: ajanın yazdığı denemeyi kabul etmek her zaman başarılı bir komutla yapılamaz (|| true, ; exit 0 gibi): bir şey kanıtlamaz.
        if (enabled == true && !cur.Enabled && cur.Origin == CheckStore.AgentTestOrigin)
        {
            var cmdNow = (spec ?? cur.Spec)["cmd"] is JsonValue cvn && cvn.TryGetValue<string>(out var ct) ? ct : null;
            var rootNow = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", cur.ProjectId)) as string;
            if (cmdNow is not null && AgentTestStrength.AlwaysSucceeds(cmdNow, rootNow)) throw new SozException(409, AgentTestStrength.AlwaysSentence);
            // K6: öneri davranışla "test kalsa bile başarılı" bulunduysa (spec.rejected) komut değişmedikçe kabul edilemez.
            var wasRejected = cur.Spec["rejected"] is JsonValue rjv && rjv.TryGetValue<bool>(out var rjb) && rjb;
            var curCmd = cur.Spec["cmd"] is JsonValue ccv && ccv.TryGetValue<string>(out var cct) ? cct : null;
            if (wasRejected && string.Equals(cmdNow?.Trim(), curCmd?.Trim(), StringComparison.Ordinal)) throw new SozException(409, AgentTestStrength.AlwaysSentence);
            // Öneri hiç ölçülmemişse (ölçüm atlandı ya da kayıt elle eklendi) kabul anında davranışla sınanır: testler kesin kalırken komut başarılıysa kabul edilemez.
            if (!wasRejected && cur.Spec["rejected"] is null && cmdNow is not null && !string.IsNullOrWhiteSpace(rootNow) && Directory.Exists(rootNow))
            {
                var st = SozSettings.Load(c);
                var snapNow = EvidenceBase.ScanProject(c, cur.ProjectId, cur.JobId, rootNow);
                if (AgentTestStrength.SwallowsFailure(rootNow, Db.DataDir, cur.JobId ?? 0, cmdNow, snapNow, (long)st.ClampedInt("setting:proof.verdict-copy-max-mb") << 20, Math.Min(st.ClampedInt("setting:proof.verdict-run-seconds"), GateBudgetSeconds(st))) == true)
                    throw new SozException(409, AgentTestStrength.AlwaysSentence);
            }
        }
        using var tx = c.BeginTransaction();
        c.Exec("""
            UPDATE checks SET enabled=COALESCE(@e,enabled), label=COALESCE(@l,label), spec_json=COALESCE(@s,spec_json), timeout_s=COALESCE(@t,timeout_s) WHERE id=@id
            """, tx, ("@e", enabled is null ? null : enabled.Value ? 1 : 0), ("@l", label?.Trim()), ("@s", spec?.ToJsonString()),
            ("@t", timeoutS is null ? null : Math.Clamp(timeoutS.Value, 1, 3600)), ("@id", id));
        var now = c.Query("SELECT id, project_id, job_id, kind, label, spec_json, timeout_s, enabled, origin, created_at, uid FROM checks WHERE id=@id", CheckStore.Map, tx, ("@id", id)).First();
        LogChange(c, tx, "check_changed", cur.ProjectId, Summ(cur), Summ(now));
        tx.Commit();
        // Kişi ajanın yazdığı deneme önerisini kabul etti (yalnız insan çağırabilir, CallerGate): o işte ajanın deneme için yaptığı değişiklikler onaylanmış sayılır, onaylı taban ilerler (denetim 5, B6).
        if (enabled == true && !cur.Enabled && cur.Origin == CheckStore.AgentTestOrigin && now.Spec["suggestedByJob"] is JsonValue sj && sj.TryGetValue<long>(out var byJob))
            EvidenceBase.ApproveAgentTest(c, now.ProjectId, byJob, now.Spec["cmd"] is JsonValue cv && cv.TryGetValue<string>(out var cmdText) ? cmdText : null);
        return now;
    }

    /// <summary>Ajanın önerdiği denemeye Hayır. Ölçüt silinmez, kapalı kalır, aynı öneri tekrar gösterilmez.</summary>
    public JobDto DeclineSuggestedCheck(long id)
    {
        long jobId;
        using (var c = Conn())
        {
            var check = CheckStore.Get(c, id) ?? throw new SozException(404, $"Deneme bulunamadı: {id}");
            if (check.Origin != CheckStore.AgentTestOrigin || check.Enabled ||
                check.Spec["suggestedByJob"] is not JsonValue jv || !jv.TryGetValue<long>(out jobId))
                throw new SozException(409, "Bu deneme cevap bekleyen bir yardımcı önerisi değil.");
            if (check.Spec["declined"] is JsonValue dv && dv.TryGetValue<bool>(out var declined) && declined)
                throw new SozException(409, "Bu deneme önerisine zaten Hayır dedin.");
            var job = JobStore.Get(c, jobId) ?? throw new SozException(404, $"İş bulunamadı: {jobId}");
            if (job.ProjectId != check.ProjectId) throw new SozException(409, "Deneme bu işe ait değil.");
            check.Spec["declined"] = true;
            using var tx = c.BeginTransaction();
            c.Exec("UPDATE checks SET spec_json=@s WHERE id=@id", tx, ("@s", check.Spec.ToJsonString()), ("@id", id));
            Events.Log(c, "job_check_declined", check.ProjectId, System.Text.Json.JsonSerializer.Serialize(new { jobId, checkId = id }), tx);
            tx.Commit();
        }
        return Job(jobId);
    }

    public void DeleteCheck(long id)
    {
        using var c = Conn();
        var cur = CheckStore.Get(c, id) ?? throw new SozException(404, $"Kontrol bulunamadı: {id}");
        using var tx = c.BeginTransaction();
        c.Exec("DELETE FROM checks WHERE id=@id", tx, ("@id", id));
        LogChange(c, tx, "check_removed", cur.ProjectId, Summ(cur), null);
        tx.Commit();
    }

    // ---------- kanıt ----------
    /// <summary>Elle çalıştırma: çalışmayı kaydeder ve kimliğini döndürür (yürütme CompleteRun ile).</summary>
    public long BeginManualRun(long projectId, long? jobId)
    {
        using var c = Conn();
        RequireProject(c, projectId);
        if (jobId is not null && c.Scalar("SELECT id FROM jobs WHERE id=@j AND project_id=@p", null, ("@j", jobId), ("@p", projectId)) is null)
            throw new SozException(404, "İş bu projede bulunamadı.");
        return ProofStore.Begin(c, new ProofStore.RunRequest(projectId, jobId, null, "manual", 1));
    }

    public ProofRunDto CompleteRun(long runId) => ProofStore.Complete(Db, runId, GateBudgetSeconds(SozSettings.Load(Db)), EdgeLocatorOverride);

    public ProofRunDto RunProof(long projectId, long? jobId) => CompleteRun(BeginManualRun(projectId, jobId));

    public List<ProofRunDto> Runs(long? projectId, long? jobId, int limit = 20)
    {
        using var c = Conn();
        return ProofStore.List(c, projectId, jobId, limit);
    }

    public ProofRunDto Run(long id)
    {
        using var c = Conn();
        return ProofStore.Get(c, id) ?? throw new SozException(404, $"Deneme çalışması bulunamadı: {id}");
    }

    public string? ArtifactPath(long runId, string name) => ProofStore.ArtifactPath(Db, runId, name);

    // ---------- kuyruk ----------
    public List<QueueItemDto> Queue(long? projectId, long? sessionId, bool pendingOnly)
    {
        using var c = Conn();
        return QueueStore.List(c, projectId, sessionId, pendingOnly);
    }

    public QueueItemDto Enqueue(long? projectId, long? sessionId, string text, string origin = "panel")
    {
        using var c = Conn();
        return QueueStore.Add(c, projectId, sessionId, text, origin);
    }

    // ---------- proje kapısı ----------
    public ProjectGateDto ProjectGate(long id)
    {
        using var c = Conn();
        RequireProject(c, id);
        return c.Query("""
            SELECT p.id, p.name, p.path, p.gate_enabled,
              (SELECT COUNT(*) FROM checks k WHERE k.project_id=p.id AND k.job_id IS NULL),
              (SELECT COUNT(*) FROM checks k WHERE k.project_id=p.id AND k.job_id IS NULL AND k.enabled=1)
            FROM projects p WHERE p.id=@id
            """, r => new ProjectGateDto(r.GetInt64(0), r.Str(1) ?? "?", r.Str(2), r.Long(3) == 1, (int)r.Long(4), (int)r.Long(5)), null, ("@id", id)).First();
    }

    public ProjectGateDto SetGate(long id, bool enabled)
    {
        using (var c = Conn())
        {
            RequireProject(c, id);
            var old = c.ScalarLong("SELECT COALESCE(gate_enabled,0) FROM projects WHERE id=@id", null, ("@id", id)) == 1;
            using var tx = c.BeginTransaction();
            c.Exec("UPDATE projects SET gate_enabled=@g WHERE id=@id", tx, ("@g", enabled ? 1 : 0), ("@id", id));
            LogChange(c, tx, "gate_changed", id, new JsonObject { ["gateEnabled"] = old }, new JsonObject { ["gateEnabled"] = enabled });
            tx.Commit();
        }
        return ProjectGate(id);
    }

    // ---------- sabah raporu / Bugün ----------
    public MorningDto Morning(DateTime? sinceUtc = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? Time.UtcNow();
        using var c = Conn();
        return MorningBuilder.Morning(c, sinceUtc ?? now.AddHours(-16), now, StateThresholds.From(SozSettings.Load(c)));
    }

    public NowDto Now(DateTime? nowUtc = null)
    {
        using var c = Conn();
        return MorningBuilder.Now(c, nowUtc ?? Time.UtcNow(), StateThresholds.From(SozSettings.Load(c)));
    }
}
