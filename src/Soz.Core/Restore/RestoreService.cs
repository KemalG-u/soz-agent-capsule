using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Run;

namespace Soz.Core.Restore;

public sealed record ConflictDto(string Path, string Reason, string Text);
public sealed record PlanItemDto(string Path, string Action, string? Why, string Text);

/// <summary>Önizleme/uygulama/geri alma sonucu. Status: previewed | applied | failed | reverted. Tüm metinler sade Türkçe; içerik değil yol ve sayı taşır.</summary>
public sealed record UndoResultDto(string? UndoUid, string Status, string Summary, int Restored, int Removed, int Renamed, int DirsCreated, int DirsRemoved, int Skipped,
    string? PreSnapshotUid, long Ms, List<PlanItemDto> Items, List<ConflictDto> Conflicts, List<ConflictDto> Unrestorable, List<string> Notes);

public sealed record RunProtectionDto(long RunId, string? RunUid, string? Level, string? Note, string? BeforeSnapshotUid, string? AfterSnapshotUid, string? Started);

/// <summary>Kartta görünecek koruma durumu. Level: tam | sinirli | yok | null (iş henüz çalışmadı).</summary>
public sealed record ProtectionDto(long JobId, string? Level, string? Note, string Text, string Limits, bool CanUndo, string? CannotUndoWhy, List<RunProtectionDto> Runs);

public sealed record UndoRequest(long? RunId = null, string? Scope = null, IReadOnlyList<string>? Paths = null, bool Force = false, string? PreviewUid = null);

/// <summary>
/// Geri alma yeteneği (kayıt defteri sözleşmesi, dört cümle — kayda bağlamak yönetici işidir):
/// BULUNUR: iş kartında "koruma" satırı (tam / sınırlı / yok) ve "Geri al" düğmesi; API GET /api/jobs/{id}/protection, POST /api/jobs/{id}/undo/preview.
/// KAPATILIR: ayarlarda "setting:restore.enabled" kapatılırsa görüntü alınmaz, her iş "yok" korumayla çalışır; silinecek bir şey yoktur, depo atılabilir (&lt;veri&gt;\geri).
/// HATA ANINDA: görüntü alınamazsa (disk dolu, çok dosya, kilitli) ajan YİNE DE çalışır, kartta "korumasız" ve nedeni yazar; geri alma, önce şimdiki durumu kaydedemezse hiçbir şeye dokunmaz.
/// YENİ ARAÇ GELİNCE: değişen bir şey yoktur; geri alma ajanın değil klasörün değişikliğine bakar, hangi araç yazarsa yazsın aynı çalışır.
/// Söz bir kasa değil özen katmanıdır: paket kurulumları, ağ istekleri, gönderilmiş e-postalar, çalışan sunucular ve proje klasörü dışına yazılanlar geri alınamaz.
/// </summary>
public sealed class RestoreService : IDisposable
{
    public const string Limits = "Geri alınamayanlar: paket kurulumları, ağ istekleri, gönderilmiş e-postalar, çalışan sunucular ve bu klasörün dışına yazılanlar. Söz bir kasa değil, özen katmanıdır.";

    private readonly SozDb _db;
    private readonly RestoreOptions? _fixedOptions;
    private readonly object _lock = new();
    private SnapshotEngine? _snaps;
    private RestoreEngine? _engine;
    private DateTime _lastPrune = DateTime.MinValue;

    public RestoreService(SozDb db, RestoreOptions? options = null) { _db = db; _fixedOptions = options; }

    public string DepoDir => Path.Combine(_db.DataDir, "geri");

    internal static readonly (string Kind, string Description)[] EventKinds =
    {
        ("snapshot.taken", "Geri alma noktası alındı (snapshot_uid, kind, files, new_bytes, protection, scan_ms, store_ms)."),
        ("snapshot.failed", "Geri alma noktası alınamadı, iş korumasız çalıştı (kind, code)."),
        ("snapshot.expired", "Süresi dolan geri alma noktaları temizlendi (snapshots, freed_bytes)."),
        ("undo.previewed", "Geri alma önizlendi (undo_uid, restore, remove, conflicts)."),
        ("undo.applied", "Geri alma uygulandı (undo_uid, restored, removed, skipped, ms)."),
        ("undo.reverted", "Geri alma geri alındı (undo_uid, restored, removed, skipped, ms)."),
    };

    /// <summary>Olay türlerini ilk kullanımda kayıt eder (şema göçüne bağlı değil: yeni tür eklemek göç gerektirmez).</summary>
    private static void EnsureEventKinds(SqliteConnection c)
    {
        foreach (var (kind, desc) in EventKinds)
            c.Exec("INSERT OR IGNORE INTO event_kind(kind, owner, schema_v, since, description) VALUES(@k,'soz.core',1,@t,@d)", null, ("@k", kind), ("@t", Time.NowIso()), ("@d", desc));
    }

    private (SnapshotEngine snaps, RestoreEngine engine) Engines()
    {
        lock (_lock)
        {
            if (_snaps is null)
            {
                RestoreOptions opts;
                if (_fixedOptions is not null) opts = _fixedOptions;
                else { using var c = _db.Open(); opts = RestoreOptions.Load(c); }
                _snaps = new SnapshotEngine(new GeriDepo(DepoDir, opts));
                _engine = new RestoreEngine(_snaps);
            }
            return (_snaps, _engine!);
        }
    }

    public SnapshotEngine Snapshots => Engines().snaps;

    private bool Enabled()
    {
        using var c = _db.Open();
        try { return SozSettings.Load(c).Bool(RestoreOptions.KEnabled); }
        catch (SqliteException) { return true; }
    }

    // ================================================================ koşu kancaları

    private sealed record RunCtx(string RunUid, long JobId, string JobUid, string ProjectUid, string? Path);

    private RunCtx? RunContext(SqliteConnection c, long runId)
        => c.Query("SELECT r.uid, r.job_id, j.uid, p.uid, p.path FROM runs r JOIN jobs j ON j.id=r.job_id JOIN projects p ON p.id=j.project_id WHERE r.id=@r",
            r => new RunCtx(r.Str(0) ?? "", r.GetInt64(1), r.Str(2) ?? "", r.Str(3) ?? "", r.Str(4)), null, ("@r", runId)).FirstOrDefault();

    /// <summary>
    /// Ajan başlamadan ÖNCE klasörün görüntüsünü alır, koşuya yazar. Alınamazsa koşu DURMAZ: koruma düzeyi "yok" ve nedeni yazılır (hiçbir zaman istisna fırlatmaz).
    /// </summary>
    public SnapshotResult TakeBeforeRun(long runId)
    {
        RunCtx? ctx;
        using (var c = _db.Open()) ctx = RunContext(c, runId);
        if (ctx is null) return SnapshotResult.Failed("hata", "Koşu bulunamadı.");
        var r = TakeFor(ctx, "before_run");
        using (var c = _db.Open())
        {
            RunStore.SetBeforeSnapshot(c, runId, r.Ok ? r.Uid : null, r.Ok ? r.Protection : ProtectionLevel.None, r.Note);
            WriteSnapshotEvent(c, ctx, "before_run", r);
        }
        return r;
    }

    /// <summary>Ajan bitince (sonuç ne olursa olsun) klasörün son hâlini alır: geri almada "ajanın son hâli" budur.</summary>
    public SnapshotResult TakeAfterRun(long runId)
    {
        RunCtx? ctx;
        string? before;
        using (var c = _db.Open())
        {
            ctx = RunContext(c, runId);
            before = c.Scalar("SELECT before_snapshot_uid FROM runs WHERE id=@r", null, ("@r", runId)) as string;
        }
        if (ctx is null) return SnapshotResult.Failed("hata", "Koşu bulunamadı.");
        if (before is null) return SnapshotResult.Failed("onceki-yok", "Bu koşunun önce görüntüsü yok."); // korumasız koşunun sonrası da alınmaz
        var r = TakeFor(ctx, "after_run");
        using (var c = _db.Open())
        {
            RunStore.SetAfterSnapshot(c, runId, r.Ok ? r.Uid : null, r.Ok ? r.Protection : ProtectionLevel.None, r.Ok ? r.Note : "Yardımcının son hâli kaydedilemedi: " + r.Note);
            WriteSnapshotEvent(c, ctx, "after_run", r);
        }
        return r;
    }

    private SnapshotResult TakeFor(RunCtx ctx, string kind)
    {
        try
        {
            if (!Enabled()) return SnapshotResult.Failed("kapali", "Geri alma noktası alma ayarlardan kapatılmış.");
            if (string.IsNullOrWhiteSpace(ctx.Path) || !Directory.Exists(ctx.Path)) return SnapshotResult.Failed("klasor-yok", "Proje klasörü bulunamadı.");
            var (snaps, _) = Engines();
            MaybePrune(snaps);
            var req = new SnapshotRequest(ctx.Path, kind, ctx.ProjectUid, ctx.JobUid, ctx.RunUid);
            var r = snaps.Take(req);
            if (!r.Ok && r.ErrorCode == "butce")
            {
                TryPrune(snaps, aggressive: true);
                r = snaps.Take(req);
            }
            return r;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return SnapshotResult.Failed("hata", "Geri alma deposu açılamadı: " + (e.Message.Length > 160 ? e.Message[..160] : e.Message));
        }
    }

    private void WriteSnapshotEvent(SqliteConnection c, RunCtx ctx, string kind, SnapshotResult r)
    {
        try
        {
            var payload = r.Ok
                ? new JsonObject { ["snapshot_uid"] = r.Uid, ["kind"] = kind, ["new"] = r.IsNew, ["files"] = r.Files, ["total_bytes"] = r.TotalBytes, ["new_bytes"] = r.NewBytes, ["skipped"] = r.Skipped, ["protection"] = r.Protection, ["scan_ms"] = r.ScanMs, ["store_ms"] = r.StoreMs }
                : new JsonObject { ["kind"] = kind, ["code"] = r.ErrorCode };
            EnsureEventKinds(c);
            Events.Append(c, r.Ok ? "snapshot.taken" : "snapshot.failed", "run", ctx.RunUid, ctx.ProjectUid, ctx.JobUid, "soz", payload.ToJsonString());
        }
        catch (SqliteException) { /* olay yazılamazsa koşu bozulmaz */ }
    }

    // ================================================================ ömür

    private void MaybePrune(SnapshotEngine snaps)
    {
        var now = Time.UtcNow();
        if (now - _lastPrune < TimeSpan.FromHours(6)) return;
        _lastPrune = now;
        TryPrune(snaps, aggressive: false);
    }

    private PruneResult? TryPrune(SnapshotEngine snaps, bool aggressive)
    {
        try
        {
            var res = RestoreGc.Prune(snaps.Depo, snaps, _db, Time.UtcNow(), aggressive);
            if (res.Snapshots > 0)
                using (var c = _db.Open()) { EnsureEventKinds(c); Events.Append(c, "snapshot.expired", null, null, null, null, "soz", new JsonObject { ["snapshots"] = res.Snapshots, ["freed_bytes"] = res.FreedBytes, ["aggressive"] = aggressive }.ToJsonString()); }
            return res;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { return null; }
    }

    /// <summary>
    /// Proje klasörü taşındığında o projenin geri alma noktaları yeni klasöre bağlanır (noktalardaki yollar klasöre göredir). Depo yoksa hiçbir şey yapmaz.
    /// Dönen: bağlanan nokta sayısı.
    /// </summary>
    public int RelocateRoot(string projectUid, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(projectUid) || !File.Exists(Path.Combine(DepoDir, "geri.db"))) return 0;
        var depo = Snapshots.Depo;
        using var c = depo.Open();
        return c.Exec("UPDATE snapshot SET root_path=@r WHERE project_uid=@u", null, ("@r", SnapshotEngine.NormalizeRoot(newRoot)), ("@u", projectUid));
    }

    /// <summary>Süresi dolan noktaları temizler (bütçe aşımında aggressive).</summary>
    public PruneResult? Prune(bool aggressive = false) => TryPrune(Engines().snaps, aggressive);

    // ================================================================ koruma durumu

    private sealed record JobCtx(long JobId, string JobUid, long ProjectId, string ProjectUid, string? Path);

    private static JobCtx JobContext(SqliteConnection c, long jobId)
        => c.Query("SELECT j.id, j.uid, j.project_id, p.uid, p.path FROM jobs j LEFT JOIN projects p ON p.id=j.project_id WHERE j.id=@j",
            r => new JobCtx(r.GetInt64(0), r.Str(1) ?? "", r.Long(2), r.Str(3) ?? "", r.Str(4)), null, ("@j", jobId)).FirstOrDefault()
           ?? throw new SozException(404, $"İş bulunamadı: {jobId}");

    private sealed record RunRow(long Id, string? Uid, string? Before, string? After, string? Protection, string? Note, string? Started, string Status);

    private static List<RunRow> RunsOf(SqliteConnection c, long jobId)
        => c.Query("SELECT id, uid, before_snapshot_uid, after_snapshot_uid, protection, protection_note, started, status FROM runs WHERE job_id=@j ORDER BY id",
            r => new RunRow(r.GetInt64(0), r.Str(1), r.Str(2), r.Str(3), r.Str(4), r.Str(5), r.Str(6), r.Str(7) ?? ""), null, ("@j", jobId));

    /// <summary>İşin koruma düzeyi (koşularının en kötüsü) ve karttaki cümleler. Geri alma yoksa nedeni yazar.</summary>
    public ProtectionDto ProtectionOf(long jobId)
    {
        using var c = _db.Open();
        _ = JobContext(c, jobId);
        var runs = RunsOf(c, jobId);
        var dto = runs.Select(r => new RunProtectionDto(r.Id, r.Uid, r.Protection, r.Note, r.Before, r.After, r.Started)).ToList();
        if (runs.Count == 0)
            return new ProtectionDto(jobId, null, null, "Bu iş henüz çalışmadı. Yardımcı başlamadan önce klasörün geri alma noktası alınır.", Limits, false, "İş henüz çalışmadı.", dto);

        string level;
        if (runs.Any(r => r.Protection == ProtectionLevel.None || r.Protection is null)) level = runs.Any(r => r.Before is not null) ? ProtectionLevel.Limited : ProtectionLevel.None;
        else if (runs.Any(r => r.Protection == ProtectionLevel.Limited)) level = ProtectionLevel.Limited;
        else level = ProtectionLevel.Full;
        var notes = runs.Select(r => r.Note).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        var note = notes.Count == 0 ? null : string.Join(" ", notes);
        var anyBefore = runs.Any(r => r.Before is not null);
        var text = level switch
        {
            ProtectionLevel.Full => "Bu işin klasördeki dosya değişiklikleri geri alınabilir.",
            ProtectionLevel.Limited => "Bu iş kısmen korunuyor." + (note is null ? "" : " " + note),
            _ => "Bu iş korumasız çalıştı, geri alınamaz." + (note is null ? "" : " " + note),
        };
        return new ProtectionDto(jobId, level, note, text, Limits, anyBefore, anyBefore ? null : (note ?? "Geri alma noktası yok."), dto);
    }

    // ================================================================ önizle, uygula, geri al

    private sealed record Target(SnapshotRow B, SnapshotRow? A, long? RunId, string Mode, List<string> Notes);

    private Target ResolveTarget(GeriDepo depo, SqliteConnection soz, JobCtx j, UndoRequest req)
    {
        var runs = RunsOf(soz, j.JobId);
        var notes = new List<string>();
        var withBefore = runs.Where(r => r.Before is not null).ToList();
        if (withBefore.Count == 0)
            throw new SozException(409, "Bu iş için geri alma noktası yok" + (runs.Count > 0 && runs[^1].Note is { Length: > 0 } n ? ": " + n : ".") + " Dosyalar geri alınamaz.");
        RunRow first, last; string mode;
        if (req.RunId is { } rid)
        {
            first = last = runs.FirstOrDefault(r => r.Id == rid) ?? throw new SozException(404, "Bu işte böyle bir tur yok.");
            if (first.Before is null) throw new SozException(409, "Bu turun geri alma noktası yok" + (first.Note is { Length: > 0 } n2 ? ": " + n2 : "."));
            mode = "last";
        }
        else if (string.Equals(req.Scope, "last", StringComparison.OrdinalIgnoreCase)) { first = last = withBefore[^1]; mode = "last"; }
        else
        {
            first = withBefore[0]; last = runs[^1]; mode = "all";
            if (first.Id != runs[0].Id) notes.Add("İşin ilk turlarının geri alma noktası yok. İlk noktası olan turdan itibaren geri alınır.");
        }
        if (req.Paths is { Count: > 0 }) mode = "partial";

        using var g = depo.Open();
        var b = depo.GetSnapshotByUid(g, first.Before!);
        if (b is null || b.State != "ok") throw new SozException(409, "Bu işin geri alma noktasının süresi dolmuş ya da silinmiş. Dosyalar geri alınamaz.");
        SnapshotRow? a = null;
        if (last.After is not null) a = depo.GetSnapshotByUid(g, last.After);
        if (a is null) notes.Add("Yardımcının son durumu kayıtlı değil: klasördeki her fark yardımcının sayılacak. Bu yüzden kendi değişikliklerinin üstüne yazılabilir, dikkatle bak.");
        return new Target(b, a, req.RunId, mode, notes);
    }

    private void GuardIdle(SqliteConnection c, JobCtx j)
    {
        if (string.IsNullOrWhiteSpace(j.Path) || !Directory.Exists(j.Path)) throw new SozException(404, "Projenin klasörü bulunamadı.");
        if (c.ScalarLong("SELECT COUNT(*) FROM runs r JOIN jobs x ON x.id=r.job_id WHERE x.project_id=@p AND r.status='running'", null, ("@p", j.ProjectId)) > 0)
            throw new SozException(409, "Bu projede bir iş çalışıyor. Geri almadan önce işi durdur ya da bitmesini bekle.");
    }

    public UndoResultDto Preview(long jobId, UndoRequest req) => RunUndo(jobId, req, apply: false);

    public UndoResultDto Apply(long jobId, UndoRequest req) => RunUndo(jobId, req, apply: true);

    private UndoResultDto RunUndo(long jobId, UndoRequest req, bool apply)
    {
        var (snaps, engine) = Engines();
        JobCtx j; Target t; string undoUid;
        using (var c = _db.Open())
        {
            j = JobContext(c, jobId);
            GuardIdle(c, j);
            t = ResolveTarget(snaps.Depo, c, j, req);
            // önizlemeden gelen kayıt yeniden kullanılır
            var prev = apply && req.PreviewUid is not null ? UndoStore.Get(c, req.PreviewUid) : null;
            undoUid = prev is { JobId: var pj, Status: "previewed" } && pj == jobId ? prev.Uid : UndoStore.Insert(c, jobId, t.RunId, t.B.Uid, t.Mode, "previewed");
        }

        var rr = new RestoreRequest(j.Path!, t.B.Id, t.A?.Id, req.Paths, req.Force, j.ProjectUid, j.JobUid);
        RestoreOutcome o;
        try { o = apply ? engine.Apply(rr) : engine.Preview(rr); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            using var c2 = _db.Open();
            UndoStore.Finish(c2, undoUid, "failed", null, "beklenmeyen hata");
            throw new SozException(500, "Geri alma sırasında beklenmeyen bir hata oldu. Dosyaların durumunu kontrol et. " + (e.Message.Length > 160 ? e.Message[..160] : e.Message));
        }
        o.Notes.InsertRange(0, t.Notes);
        return Complete(j, undoUid, o, apply ? "applied" : "previewed", apply ? "undo.applied" : "undo.previewed", reverted: false);
    }

    /// <summary>Geri alınmış bir işi geri alır: geri almadan önce kaydedilen noktaya döner ("geri almayı geri al"). Geri almadan sonra yapılan değişikliklere çakışma kuralı uygulanır.</summary>
    public UndoResultDto Revert(string undoUid, bool force = false)
    {
        var (snaps, engine) = Engines();
        UndoDto u; JobCtx j;
        using (var c = _db.Open())
        {
            u = UndoStore.Get(c, undoUid) ?? throw new SozException(404, "Geri alma kaydı bulunamadı.");
            j = JobContext(c, u.JobId);
            if (u.Status == "reverted") throw new SozException(409, "Bu geri alma zaten geri alınmış.");
            if (u.Status != "applied") throw new SozException(409, "Yalnız uygulanmış bir geri alma geri alınabilir.");
            GuardIdle(c, j);
        }
        SnapshotRow? pre, post = null;
        using (var g = snaps.Depo.Open())
        {
            pre = u.PreSnapshotUid is null ? null : snaps.Depo.GetSnapshotByUid(g, u.PreSnapshotUid);
            if (u.PostSnapshotUid is not null) post = snaps.Depo.GetSnapshotByUid(g, u.PostSnapshotUid);
        }
        if (pre is null || pre.State != "ok") throw new SozException(409, "Geri almadan önceki durumun noktası bulunamadı (süresi dolmuş olabilir).");
        var rr = new RestoreRequest(j.Path!, pre.Id, post?.Id, null, force, j.ProjectUid, j.JobUid);
        var o = engine.Apply(rr);
        if (post is null) o.Notes.Add("Geri almanın sonundaki durum kayıtlı değil: klasördeki her fark geri almanın sayıldı.");
        return Complete(j, undoUid, o, "reverted", "undo.reverted", reverted: true);
    }

    private UndoResultDto Complete(JobCtx j, string undoUid, RestoreOutcome o, string okStatus, string eventKind, bool reverted)
    {
        var status = o.Ok ? okStatus : "failed";
        using (var c = _db.Open())
        {
            if (okStatus == "previewed" && o.Ok) UndoStore.Finish(c, undoUid, "previewed", o, null);
            else if (reverted && o.Ok) { UndoStore.Finish(c, undoUid, "reverted", o, null); }
            else UndoStore.Finish(c, undoUid, status, o, o.Error);
            if (o.Ok)
            {
                var payload = new JsonObject { ["undo_uid"] = undoUid, ["restored"] = o.Restored + o.Renamed + o.DirsCreated, ["removed"] = o.Removed + o.DirsRemoved, ["skipped"] = o.Conflicts.Count + o.Unrestorable.Count, ["restore"] = o.Items.Count(i => i.Action != "sil" && i.Action != "klasor-sil"), ["conflicts"] = o.Conflicts.Count, ["ms"] = o.TotalMs };
                EnsureEventKinds(c);
                Events.Append(c, eventKind, "undo", undoUid, j.ProjectUid, j.JobUid, "soz", payload.ToJsonString());
                // Kişi geri aldıysa işe bakmış ve karar vermiştir: kalan/başlatılamayan/durdurulan işin "Sıra bende" satırı düşer (k1; yalnız uygulanan geri alma, önizleme değil).
                if (okStatus == "applied")
                    c.Exec("UPDATE jobs SET seen_at=@n WHERE id=@id AND seen_at IS NULL AND state IN ('kaldi','baslatilamadi','durduruldu')", null, ("@n", Time.NowIso()), ("@id", j.JobId));
            }
        }
        if (!o.Ok) throw new SozException(409, o.Error ?? "Geri alma yapılamadı.");
        return ToDto(undoUid, status, o);
    }

    public List<UndoDto> History(long jobId)
    {
        using var c = _db.Open();
        _ = JobContext(c, jobId);
        return UndoStore.ForJob(c, jobId);
    }

    // ================================================================ sade Türkçe metinler

    public static string ReasonText(string reason) => reason switch
    {
        "degisti" => "Sen ya da başka bir program bu dosyayı yardımcıdan sonra değiştirmiş. Dokunulmadı.",
        "degisti-onbellek" => "Dosyanın içeriği kayıttakinden farklı çıktı. Dokunulmadı.",
        "kilitli" => "Başka bir program bu dosyayı kullanıyor. Atlandı.",
        "buyuk" => "Dosya çok büyük olduğu için kopyası saklanmamıştı. Geri alınamaz.",
        "okunamadi" => "Dosya okunamamıştı. Geri alınamaz.",
        "depo-eksik" => "Geri alma deposunda bu dosyanın içeriği bulunamadı.",
        "sir-acilamadi" => "Bu dosya Windows kullanıcı korumasıyla saklanmıştı ve bu hesapla açılamadı. Dosyaya yazılmadı, olduğu gibi bırakıldı.",
        "depo-bozuk" => "Geri alma deposundaki içerik bozuk çıktı. Dosyaya yazılmadı, olduğu gibi bırakıldı.",
        "guvensiz-yol" => "Proje klasörünün dışına çıkan yol. Dokunulmadı.",
        "baglanti" => "Bağlantı (kısayol) klasörünün içine yazılmaz. Dokunulmadı.",
        "tur-degisti" => "Aynı adda farklı türde bir şey var (dosya ya da klasör); dokunulmadı.",
        _ => "Atlandı.",
    };

    private static string ItemText(PlanItem i) => i.Action switch
    {
        "geri-yukle" => i.Why == "ajan-sildi" ? "Yardımcı silmişti, geri gelir." : "Yardımcı değiştirmişti, eski hâline döner.",
        "sil" => "Yardımcı oluşturmuştu, silinir.",
        "adi-duzelt" => "Yardımcı adının büyük-küçük harfini değiştirmişti, eski adına döner.",
        "klasor-olustur" => "Yardımcı silmişti, boş klasör geri gelir.",
        "klasor-sil" => "Yardımcı oluşturmuştu, boş klasör kaldırılır.",
        _ => "",
    };

    private static UndoResultDto ToDto(string undoUid, string status, RestoreOutcome o)
    {
        var changes = o.Items.Count;
        string summary;
        var skipped = o.Conflicts.Count + o.Unrestorable.Count;
        if (status == "previewed")
        {
            summary = changes == 0 ? "Geri alınacak bir şey yok: klasör zaten bu işten önceki hâlinde ya da yardımcı hiçbir dosyaya dokunmamış."
                : $"Geri alınırsa {changes} değişiklik yapılır" + (skipped > 0 ? $". {skipped} dosyaya dokunulmaz." : ".");
        }
        else
        {
            var done = o.Restored + o.Renamed + o.Removed + o.DirsCreated + o.DirsRemoved;
            summary = status == "reverted" ? $"Geri alma geri alındı: {done} değişiklik yapıldı" : $"Geri alındı: {done} değişiklik yapıldı";
            summary += skipped > 0 ? $". {skipped} dosya atlandı." : ".";
        }
        return new UndoResultDto(undoUid, status, summary, o.Restored, o.Removed, o.Renamed, o.DirsCreated, o.DirsRemoved, skipped, o.PreUid, o.TotalMs,
            o.Items.Select(i => new PlanItemDto(i.Path, i.Action, i.Why, ItemText(i))).ToList(),
            o.Conflicts.Select(x => new ConflictDto(x.Path, x.Reason, ReasonText(x.Reason))).ToList(),
            o.Unrestorable.Select(x => new ConflictDto(x.Path, x.Reason, ReasonText(x.Reason))).ToList(), o.Notes);
    }

    public void Dispose() { _snaps?.Dispose(); }
}
