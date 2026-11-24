using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Soz.Core.Agents;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Judge;
using Soz.Core.Proof;
using Soz.Core.Readers;
using Soz.Core.Sessions;
using Soz.Core.Text;

namespace Soz.Core.Run;

/// <summary>
/// Yürütücü (terminal yok turu): Söz ajanı (claude -p) arka planda, penceresiz, proje klasöründe kendisi başlatır.
/// Kancalar projeye yazılmaz; Söz veri klasöründeki ayar dosyası `--settings` ile verilir. Çıktı stream-json olarak okunur,
/// sade Türkçe satırlara çevrilir (run_lines + canlı olaylar). Yalnız kendi başlattığı süreç ağacını kapatır.
/// Bir RunManager bir süreçte (sunucu ya da CLI) çalışır; durum veritabanındadır, başka süreçten de okunur ve durdurulur.
/// </summary>
public sealed class RunManager : IAsyncDisposable, IDisposable
{
    private readonly SozDb _db;
    private readonly ConcurrentDictionary<long, ActiveRun> _active = new();
    private readonly object _gate = new();
    private readonly object _settingsLock = new();

    /// <summary>Kancaların çağıracağı soz.exe (mutlak yol). Boşsa uygulama klasöründeki soz.exe, sonra çalışan sürecin kendisi (soz.exe ise).</summary>
    public string? SozExePath { get; set; }

    /// <summary>Her ilerleme satırında (jobId, runId, satır). CLI satırları buradan yazar.</summary>
    public event Action<long, long, RunLine>? LineEmitted;

    public RunManager(SozDb db) { _db = db; Restore = new Soz.Core.Restore.RestoreService(db); }

    /// <summary>Geri alma yeteneği: ajan başlamadan önce ve bitince klasörün görüntüsünü alır; API (önizle/uygula/geri al) de bunu kullanır.</summary>
    public Soz.Core.Restore.RestoreService Restore { get; }

    /// <summary>Önce görüntü bu süre içinde biterse koşu eskisi gibi eşzamanlı başlar (küçük klasör); bitmezse görüntü arka planda sürer ve ajan bitince başlar.</summary>
    private static readonly TimeSpan SnapshotSyncWait = TimeSpan.FromSeconds(2);

    /// <summary>Görüntü alınamadıysa ya da sınırlıysa kişi bunu ilerleme akışında görür (bütçesiz: kişi o işi şimdi verdi). Tam korumada sessiz kalınır.</summary>
    private void NoteSnapshot(long runId, JobDto job, Soz.Core.Restore.SnapshotResult r)
    {
        if (r.Ok && r.Protection == Soz.Core.Restore.ProtectionLevel.Full) return;
        try
        {
            using var c = _db.Open();
            RunStore.AddLine(c, runId, job.Id, job.ProjectId, "state", r.Ok ? "Geri alma noktası alındı ama bazı dosyalar korunamıyor: " + r.Note : "Geri alma noktası alınamadı, bu iş korumasız çalışıyor. " + r.Note, null);
        }
        catch (Exception) { /* bildirim yazılamazsa koşu bozulmaz */ }
    }

    private sealed class ActiveRun
    {
        public long RunId, JobId;
        public ProcessTree? Tree;
        public volatile string? StopReason;
        public Task Done = Task.CompletedTask;
        public Task? MonitorTask;
        public IAgentConnector? Connector;
        /// <summary>Dış kapı döngüsünde bu turun kapı sırası (kullanıcı turu 1, geri yollama turları 2, 3...).</summary>
        public int GateAttempt = 1;
        public string? ModelUsed;
    }

    // ------------------------------------------------------------------ dış API
    private sealed record Queued(string Kind, string Prompt, string? Resume, string? Model, string? UserLine, string? ApprovalNote, int GateAttempt = 1);

    /// <summary>Dış kapı döngüsü: kapı kalınca aynı oturuma geri yollanacak metin, bir sonraki kapı sırası ve kullanıcıya gösterilecek not.</summary>
    private sealed record GateRetry(string Message, int NextAttempt, string Note);

    /// <summary>
    /// İşi başlatır; süreç başladığında döner (bitişi beklemez). Başlatılamazsa (claude yok vb.) iş "baslatilamadi" olur ve sade neden yazılır; hata fırlatmaz.
    /// Aynı anda çalışan iş sayısı doluysa iş "sirada" durumuna girer ve null döner; biri bitince kendiliğinden başlar (K-6).
    /// </summary>
    public RunDto? Start(long jobId, string? modelOverride = null, string? agentOverride = null, bool writeCheckFirst = false)
    {
        CheckModel(modelOverride);
        var wantAgent = string.IsNullOrWhiteSpace(agentOverride) ? null : AgentRegistry.Get(agentOverride).Id; // bilinmeyen ajan: 400
        string prompt;
        using (var c = _db.Open())
        {
            var j = JobStore.Get(c, jobId) ?? throw new SozException(404, $"İş bulunamadı: {jobId}");
            if (j.State is JobStates.Passed or JobStates.Cancelled) throw new SozException(409, "Bu iş bitmiş, yeniden başlatılamaz. Yeni bir iş yaz.");
            if (j.State == JobStates.Queued) throw new SozException(409, "Bu iş zaten sırada, kendiliğinden başlayacak.");
            if (j.State == JobStates.AwaitingApproval && RunStore.PendingApprovals(c, jobId).Count > 0) throw new SozException(409, "Önce bekleyen onay isteklerine Evet ya da Hayır de.");
            if (wantAgent is not null && !wantAgent.Equals(j.Agent, StringComparison.OrdinalIgnoreCase))
            {
                // Ajan yalnız henüz hiç konuşmamış işte değişir: oturum bir ajana aittir, başka ajanda sürdürülemez.
                if (RunStore.LastSessionId(c, jobId) is not null) throw new SozException(409, $"Bu iş {AgentRegistry.Get(j.Agent).DisplayName} ile konuştu, yardımcı değiştirilemez. Yeni bir iş yaz.");
                c.Exec("UPDATE jobs SET agent=@a WHERE id=@j", null, ("@a", wantAgent), ("@j", jobId));
            }
            if (writeCheckFirst && !j.WriteCheckFirst)
            {
                // "Önce deneme yaz": istem ancak ajana ilk verilişte değişir (taslak). Verilmiş işin istemi ve ölçüt özeti kıpırdamaz.
                if (j.State != JobStates.Draft) throw new SozException(409, "Bu iş daha önce başlamış. \"Önce deneme yaz\" ancak ilk başlatmada verilir. Yeni bir iş yaz.");
                c.Exec("UPDATE jobs SET write_check_first=1 WHERE id=@j", null, ("@j", jobId));
                JobStore.RefreshDraftPrompt(c, jobId);
                j = JobStore.Get(c, jobId)!;
            }
            prompt = j.PromptText;
        }
        return Launch(jobId, "start", prompt, resumeSession: null, modelOverride, userLine: null);
    }

    /// <summary>Kullanıcının cevabı aynı oturumda sürer (--resume).</summary>
    public RunDto? Reply(long jobId, string text, string? modelOverride = null)
    {
        CheckModel(modelOverride);
        if (string.IsNullOrWhiteSpace(text)) throw new SozException(400, "Cevap metni boş olamaz.");
        string? session;
        using (var c = _db.Open())
        {
            var j = JobStore.Get(c, jobId) ?? throw new SozException(404, $"İş bulunamadı: {jobId}");
            if (j.State is JobStates.Passed or JobStates.Cancelled) throw new SozException(409, "Bu iş bitmiş, cevap yazılamaz.");
            if (j.State == JobStates.Queued) throw new SozException(409, "Bu iş sırada bekliyor, başlayınca cevap yazabilirsin.");
            if (RunStore.PendingApproval(c, jobId) is not null) throw new SozException(409, "Önce bekleyen onay isteğine Evet ya da Hayır de.");
            session = RunStore.LastSessionId(c, jobId);
        }
        if (session is null) throw new SozException(409, "Bu iş henüz yardımcıyla konuşmadı (oturum yok). Önce Başlat de.");
        return Launch(jobId, "reply", text.Trim(), session, modelOverride, userLine: text.Trim());
    }

    /// <summary>
    /// "Bu işe devam et" (K-6): biten (geçti, kaldı, durduruldu, başlatılamadı) ya da karar bekleyen işin AYNI oturumunu --resume ile yeni tur olarak açar; iş çalışıyora döner.
    /// Metin verilmezse ajana "Kaldığın yerden devam et." gider. İptal edilmiş iş devam ettirilemez.
    /// </summary>
    public RunDto? Continue(long jobId, string? text = null, string? modelOverride = null)
    {
        CheckModel(modelOverride);
        string? session;
        bool stoppedBeforeAgent;
        string promptText;
        using (var c = _db.Open())
        {
            var j = JobStore.Get(c, jobId) ?? throw new SozException(404, $"İş bulunamadı: {jobId}");
            if (j.State == JobStates.Cancelled) throw new SozException(409, "Bu iş iptal edilmiş, devam ettirilemez. Yeni bir iş yaz.");
            if (j.State == JobStates.Queued) throw new SozException(409, "Bu iş sırada bekliyor, kendiliğinden başlayacak.");
            if (j.State == JobStates.Draft) throw new SozException(409, "Bu iş henüz başlamadı. Önce Başlat de.");
            if (RunStore.PendingApprovals(c, jobId).Count > 0) throw new SozException(409, "Önce bekleyen onay isteklerine Evet ya da Hayır de.");
            session = RunStore.LastSessionId(c, jobId);
            stoppedBeforeAgent = j.State == JobStates.Stopped;
            promptText = j.PromptText;
        }
        // Başlangıç ölçümü sürerken durdurulan iş ajanla hiç konuşmamıştır: devam edilecek oturum yok, iş baştan başlar. (Verildi/taslak iş yine reddedilir.)
        if (session is null && stoppedBeforeAgent)
        {
            var first = string.IsNullOrWhiteSpace(text) ? promptText : promptText + "\n\nEk not: " + text.Trim();
            return Launch(jobId, "start", first, resumeSession: null, modelOverride, userLine: string.IsNullOrWhiteSpace(text) ? null : text.Trim());
        }
        if (session is null) throw new SozException(409, "Bu iş henüz yardımcıyla konuşmadı (oturum yok). Önce Başlat de.");
        var msg = string.IsNullOrWhiteSpace(text) ? "Kaldığın yerden devam et." : text.Trim();
        return Launch(jobId, "continue", msg, session, modelOverride, userLine: string.IsNullOrWhiteSpace(text) ? "Bu işe devam et." : text.Trim());
    }

    /// <summary>
    /// Bekçinin onay isteğine cevap: allow=true ise o işleme tek seferlik izin yazılır, false ise ajana "kullanıcı bu işlemi istemedi" denir.
    /// Birden çok onay bekliyorsa tek tek (en eski önce) cevaplanır; her cevap yalnız kaydedilir, null döner. HEPSİ cevaplanınca TEK süreç başlar ve döner (Ö-8).
    /// </summary>
    public RunDto? Approve(long jobId, long eventId, bool allow, string? modelOverride = null)
    {
        CheckModel(modelOverride);
        string? session;
        List<ApprovalDto> decided;
        using (var c = _db.Open())
        {
            var j = JobStore.Get(c, jobId) ?? throw new SozException(404, $"İş bulunamadı: {jobId}");
            var a = eventId > 0 ? RunStore.ApprovalByEvent(c, jobId, eventId) : RunStore.PendingApprovals(c, jobId).FirstOrDefault();
            if (a is null) throw new SozException(404, "Bu işte böyle bir onay isteği yok.");
            if (a.Status != "pending") throw new SozException(409, "Bu onay isteğine zaten cevap verilmiş.");
            var ev = a.EventId;
            session = RunStore.LastSessionId(c, jobId);
            if (session is null) throw new SozException(409, "Oturum bulunamadı, iş yeniden başlatılmalı.");
            if (_active.Values.Any(r => r.JobId == jobId)) throw new SozException(409, "Yardımcı hâlâ çalışıyor, birkaç saniye sonra dene.");
            if (!RunStore.SetApprovalStatus(c, jobId, ev, "pending", allow ? "allowed" : "denied")) throw new SozException(409, "Bu onay isteğine zaten cevap verilmiş.");
            var left = RunStore.PendingApprovals(c, jobId);
            var prev = RunStore.Latest(c, jobId);
            if (left.Count > 0)
            {
                // Sıradaki onay kullanıcıdan bekleniyor: süreç başlamaz.
                if (prev is not null)
                {
                    RunStore.AddLine(c, prev.Id, jobId, j.ProjectId, "state", $"Cevabın kaydedildi. {left.Count} onay daha bekliyor.", null);
                    RunStore.EmitState(c, prev.Id);
                }
                return null;
            }
            // Son onay: önceki süreç (onay bekleyen) tamamlandı sayılır; bu süreçte cevaplanan hepsi tek mesajla ajana gider.
            decided = prev is null ? new() : c.Query("SELECT guard_event_id, summary, status FROM run_approvals WHERE job_id=@j AND run_id=@r AND status IN ('allowed','denied') ORDER BY id",
                r => new ApprovalDto(r.Long(0), jobId, prev.Id, "", r.Str(1) ?? "", r.Str(2) ?? "", null, null), null, ("@j", jobId), ("@r", prev.Id));
            if (decided.All(d => d.EventId != ev)) decided.Add(new ApprovalDto(ev, jobId, prev?.Id ?? 0, a.Tool, a.Summary, allow ? "allowed" : "denied", null, null));
            if (prev is { Status: RunStatus.WaitingApproval }) RunStore.SetStatus(c, prev.Id, RunStatus.Done, ended: true);
            if (prev is not null) RunStore.EmitState(c, prev.Id);
        }
        var yes = decided.Where(d => d.Status == "allowed").ToList();
        var no = decided.Where(d => d.Status == "denied").ToList();
        string msg;
        if (decided.Count <= 1)
            msg = allow
                ? "Kullanıcı az önce engellenen işlemi onayladı. Aynı işlemi aynen yeniden çalıştırarak işe devam et."
                : "Kullanıcı bu işlemi istemedi. Başka bir yol dene. Yoksa bunun yapılamadığını açıkça yaz.";
        else
        {
            var sb = new StringBuilder();
            if (yes.Count > 0) sb.Append("Kullanıcı şu işlemleri onayladı, her birini aynen yeniden çalıştırarak işe devam et:\n").Append(string.Join("\n", yes.Select(d => "- " + d.Summary))).Append('\n');
            if (no.Count > 0) sb.Append("Kullanıcı şu işlemleri istemedi, başka bir yol dene, yoksa bunların yapılamadığını açıkça yaz:\n").Append(string.Join("\n", no.Select(d => "- " + d.Summary)));
            msg = sb.ToString().TrimEnd();
        }
        var note = decided.Count <= 1 ? (allow ? "Onay verdin, yardımcı devam ediyor." : "Reddettin, yardımcıya bildirildi.") : $"{decided.Count} onayın hepsi cevaplandı, yardımcı devam ediyor.";
        return Launch(jobId, "approve", msg, session, modelOverride, userLine: null, approvalNote: note);
    }

    private static void CheckModel(string? model)
    {
        if (ModelName.Check(model) is { } err) throw new SozException(400, err);
    }

    /// <summary>İşin çalışan sürecini (yalnız Söz'ün başlattığını) kapatır. Onay bekleyen işte bekleyen onayları reddeder, sıradaki işi sıradan çıkarır. Yapılacak bir şey yoksa false.</summary>
    public bool Stop(long jobId, string reason = "durduruldu")
    {
        var act = _active.Values.FirstOrDefault(a => a.JobId == jobId);
        if (act is not null)
        {
            act.StopReason = reason;
            try { KillRun(act); } catch (Exception) { }
            if (act.Tree is null)
            {
                // Başlangıç ölçümü sürüyor, ajan henüz başlamadı: kayıtları burada yaz, ölçüm bitince ajan başlamaz.
                using var cb = _db.Open();
                var jb = JobStore.Get(cb, jobId);
                RunStore.SetStatus(cb, act.RunId, RunStatus.Stopped, lastLine: reason, ended: true);
                RunStore.AddLine(cb, act.RunId, jobId, jb?.ProjectId, "state", "İş durduruldu.", null);
                JobTo(cb, jobId, jb?.ProjectId, JobStates.Stopped);
            }
            return true;
        }
        using var c = _db.Open();
        var job = JobStore.Get(c, jobId);
        if (job is null) return false;
        var r = RunStore.Latest(c, jobId);
        // Sırada bekleyen iş: sıradan çıkar.
        if (job.State == JobStates.Queued)
        {
            JobStore.SetState(c, jobId, JobStates.Stopped, job.ProjectId);
            if (r is not null) RunStore.AddLine(c, r.Id, jobId, job.ProjectId, "state", "İş sıradan çıkarıldı.", null);
            return true;
        }
        // Onay bekleyen iş (süreç bitmiş, kullanıcıdan cevap bekliyor): bekleyen onaylar reddedilir, iş durur.
        if (r is { Status: RunStatus.WaitingApproval })
        {
            foreach (var a in RunStore.PendingApprovals(c, jobId)) RunStore.SetApprovalStatus(c, jobId, a.EventId, "pending", "denied");
            RunStore.SetStatus(c, r.Id, RunStatus.Stopped, lastLine: reason, ended: true);
            RunStore.AddLine(c, r.Id, jobId, r.ProjectId, "state", "İş durduruldu. Bekleyen onay istekleri reddedildi.", null);
            ProofStore.InterruptRunning(c, jobId);
            JobStore.SetState(c, jobId, JobStates.Stopped, r.ProjectId);
            return true;
        }
        // Başka süreçte (ör. CLI) başlatılmış olabilir: kayıttaki pid + başlangıç zamanı eşleşirse kapat.
        if (r is null || r.Status != RunStatus.Running) return false;
        var (pid, start) = RunStore.ProcessOf(c, r.Id);
        RunStore.SetStatus(c, r.Id, RunStatus.Stopped, lastLine: reason, ended: true);
        RunStore.AddLine(c, r.Id, jobId, r.ProjectId, "state", "İş durduruldu.", null);
        ProofStore.InterruptRunning(c, jobId);
        JobTo(c, jobId, r.ProjectId, JobStates.Stopped);
        if (pid is { } p) ProcessTree.KillByPid(p, start);
        return true;
    }

    /// <summary>Durdurur ve süreç kayıtlarının oturmasını bekler (en çok 6 sn): yanıttaki iş durumu bayat kalmasın (denetim 3, Y8).</summary>
    public async Task<bool> StopAsync(long jobId, string reason = "durduruldu")
    {
        var act = _active.Values.FirstOrDefault(a => a.JobId == jobId);
        var ok = Stop(jobId, reason);
        if (act is not null) { try { await act.Done.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); } catch (Exception) { } }
        return ok;
    }

    public bool IsRunning(long jobId) => _active.Values.Any(a => a.JobId == jobId);

    /// <summary>Süreç bitene kadar bekler (CLI). Çalışmıyorsa hemen döner.</summary>
    public async Task WaitAsync(long runId, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(runId, out var a)) return;
        var jobId = a.JobId;
        await a.Done.WaitAsync(ct).ConfigureAwait(false);
        // Dış kapı döngüsü: kapı kalınca aynı işin sıradaki turu (geri yollama) bu turun izleme görevinin sonunda başlar; onu da bekle.
        for (var guard = 0; guard < 12; guard++)
        {
            var next = _active.Values.FirstOrDefault(x => x.JobId == jobId && x.RunId != a.RunId && x.RunId > a.RunId);
            if (next is null) break;
            a = next;
            await a.Done.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private static void KillRun(ActiveRun a)
    {
        if (a.Tree is null) return;
        if (a.Connector is { } cn) cn.Stop(a.Tree); else a.Tree.KillTree();
    }

    /// <summary>Sunucu açılışı: "running" kalmış ama süreci olmayan kayıtlar "stopped (yarım kaldı)" olur, "deneniyor" kalmış kanıtlar "yarıda kesildi" olur, sıradaki işler başlar. Başka Söz sürecinin yaşayan süreçlerine dokunulmaz.</summary>
    public int RecoverOnStartup()
    {
        int n;
        using (var c = _db.Open())
        {
            n = RecoverStale(c, startup: true);
            try { ProofStore.InterruptRunning(c); } catch (Microsoft.Data.Sqlite.SqliteException) { }
        }
        PumpQueue();
        return n;
    }

    /// <summary>Sunucu kapanırken: çalışan süreçleri düzgün kapat, kayıtları "stopped" yap.</summary>
    public async Task StopAllAsync(TimeSpan? wait = null)
    {
        var all = _active.Values.ToList();
        foreach (var a in all)
        {
            a.StopReason ??= "Söz kapandığı için iş durduruldu.";
            try { KillRun(a); } catch (Exception) { }
        }
        var limit = wait ?? TimeSpan.FromSeconds(8);
        try { await Task.WhenAll(all.Select(a => a.Done)).WaitAsync(limit).ConfigureAwait(false); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync() { await StopAllAsync().ConfigureAwait(false); Restore.Dispose(); }
    public void Dispose() { StopAllAsync().GetAwaiter().GetResult(); Restore.Dispose(); }

    // ------------------------------------------------------------------ iç
    private int RecoverStale(Microsoft.Data.Sqlite.SqliteConnection c, bool startup = false)
    {
        var n = 0;
        foreach (var r in RunStore.WithStatus(c, RunStatus.Running))
        {
            if (_active.ContainsKey(r.Id)) continue;
            var (pid, start) = RunStore.ProcessOf(c, r.Id);
            var started = Time.Parse(r.Started);
            var alive = pid is { } p && ProcessTree.IsAlive(p, start);
            // Süreç henüz kaydedilmemiş yeni satır (başlatılıyor): 60 sn tolerans.
            if (pid is null && !startup && started is { } st && (DateTime.UtcNow - st).TotalSeconds < 60) continue;
            if (alive) continue;
            RunStore.SetStatus(c, r.Id, RunStatus.Stopped, lastLine: "yarım kaldı", ended: true);
            RunStore.AddLine(c, r.Id, r.JobId, r.ProjectId, "state", "Söz kapanmıştı, iş yarım kaldı. Sürdürmek için \"Aynı işe yeni bir şey ekle\" düğmesini kullanabilirsin.", null);
            JobTo(c, r.JobId, r.ProjectId, JobStates.Stopped);
            n++;
        }
        return n;
    }

    /// <summary>Süreç artık çalışmıyor: iş hâlâ "calisiyor" (ya da onay/sıra) görünüyorsa verilen duruma çekilir. Başka durumlara (geçti, kaldı, cevap bekliyor...) dokunmaz.</summary>
    private static void JobTo(Microsoft.Data.Sqlite.SqliteConnection c, long jobId, long? projectId, string state)
    {
        var st = c.Scalar("SELECT state FROM jobs WHERE id=@j", null, ("@j", jobId)) as string;
        if (st is JobStates.Running or JobStates.AwaitingApproval or JobStates.Queued) JobStore.SetState(c, jobId, state, projectId);
    }

    /// <summary>Başlatılamadı ya da çöktü: iş "baslatilamadi" olur, sade neden işe yazılır.</summary>
    private static void JobStartFailed(Microsoft.Data.Sqlite.SqliteConnection c, long jobId, long? projectId, string reason)
    {
        var st = c.Scalar("SELECT state FROM jobs WHERE id=@j", null, ("@j", jobId)) as string;
        if (st is not (JobStates.Running or JobStates.AwaitingApproval or JobStates.Queued)) return;
        JobStore.SetState(c, jobId, JobStates.StartFailed, projectId);
        c.Exec("UPDATE jobs SET start_error=@e WHERE id=@j", null, ("@e", SecretFilter.MaskSecrets(TextNormalizer.Truncate(reason, 400))), ("@j", jobId));
    }

    private readonly object _pumpLock = new();

    /// <summary>
    /// Sıradaki işleri başlatır (K-6): çalışan sayısı sınırın altındaysa, en eski sıradaki iş kendiliğinden başlar. Bir süreç bitince, sunucu açılınca ve ayar değişince çağrılır.
    /// </summary>
    public int PumpQueue()
    {
        var started = 0;
        lock (_pumpLock)
        {
            for (var guard = 0; guard < 50; guard++)
            {
                long jobId; string? json;
                using (var c = _db.Open())
                {
                    var st = SozSettings.Load(c);
                    if (c.ScalarLong("SELECT COUNT(*) FROM runs WHERE status='running'") >= st.MaxParallelRuns) break;
                    var next = c.Query("SELECT id, queued_json FROM jobs WHERE state='sirada' AND queued_json IS NOT NULL ORDER BY COALESCE(queued_at,''), id LIMIT 1",
                        r => (id: r.GetInt64(0), json: r.Str(1)), null).FirstOrDefault();
                    if (next == default) break;
                    jobId = next.id; json = next.json;
                }
                Queued? q = null;
                try { q = json is null ? null : JsonSerializer.Deserialize<Queued>(json); } catch (JsonException) { }
                if (q is null)
                {
                    using var c = _db.Open();
                    JobStore.SetState(c, jobId, JobStates.Given); // bozuk kayıt: sıradan çıkar
                    continue;
                }
                RunDto? run;
                try { run = Launch(jobId, q.Kind, q.Prompt, q.Resume, q.Model, q.UserLine, q.ApprovalNote, fromQueue: true, gateAttempt: q.GateAttempt); }
                catch (SozException) { run = null; }
                if (run is null) break; // yer yok ya da hata: sonra yeniden denenir
                started++;
            }
        }
        return started;
    }

    private RunDto? Launch(long jobId, string kind, string prompt, string? resumeSession, string? modelOverride, string? userLine, string? approvalNote = null, bool fromQueue = false, int gateAttempt = 1)
    {
        SozSettings st;
        JobDto job;
        string? projectPath;
        bool wantBaseline;
        using (var c = _db.Open())
        {
            st = SozSettings.Load(c);
            job = JobStore.Get(c, jobId) ?? throw new SozException(404, $"İş bulunamadı: {jobId}");
            projectPath = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", job.ProjectId)) as string;
            // Kapalı gelen ajan (hermes, opencode) kişi Tanışma'da onaylayana kadar iş başlatmaz; claude ve codex için kayıt defteri okunmaz.
            if (!AgentRegistry.IsOpenIn(job.Agent, () => Soz.Core.Registry.RegistryHost.Load(_db)))
                throw new SozException(409, $"{AgentRegistry.Get(job.Agent).DisplayName} henüz açık değil. Tanışma sayfasında aracın kayıt klasörü maddesini onaylayınca açılır. Ya da işi claude ya da codex ile yaz.");
            // Başlangıç ölçümü yalnız işin ilk başlatılışında (ajan oturumu yokken) ve bir kez yapılır.
            wantBaseline = kind == "start" && st.BaselineRun && c.Scalar("SELECT baseline_json FROM jobs WHERE id=@j", null, ("@j", jobId)) is null
                && CheckStore.EnabledFor(c, job.ProjectId, jobId).Any(x => x.Kind != CheckKinds.Screenshot);
        }

        long runId;
        lock (_gate)
        {
            using var c = _db.Open();
            RecoverStale(c);
            if (_active.Values.Any(a => a.JobId == jobId) || RunStore.Latest(c, jobId) is { Status: RunStatus.Running })
                throw new SozException(409, "Bu iş zaten çalışıyor.");
            var running = c.ScalarLong("SELECT COUNT(*) FROM runs WHERE status='running'");
            if (running >= st.MaxParallelRuns)
            {
                if (fromQueue) return null; // sırada kalır
                // K-6: sınır doluysa iş sıraya girer, biri bitince kendiliğinden başlar.
                var q = JsonSerializer.Serialize(new Queued(kind, prompt, resumeSession, modelOverride, userLine, approvalNote, gateAttempt));
                JobStore.SetState(c, jobId, JobStates.Queued, job.ProjectId);
                c.Exec("UPDATE jobs SET queued_json=@q, queued_at=@n WHERE id=@j", null, ("@q", q), ("@n", Time.NowIso()), ("@j", jobId));
                return null;
            }
            runId = RunStore.Insert(c, jobId, job.ProjectId, kind, job.Agent);
            if (resumeSession is not null) RunStore.SetSession(c, runId, resumeSession);
            if (userLine is not null) RunStore.AddLine(c, runId, jobId, job.ProjectId, "user", SecretFilter.MaskSecrets(userLine), null);
            if (approvalNote is not null) RunStore.AddLine(c, runId, jobId, job.ProjectId, "state", approvalNote, null);
            JobStore.SetState(c, jobId, JobStates.Running, job.ProjectId);
        }

        // GERİ ALMA NOKTASI: ajan başlamadan ÖNCE klasörün görüntüsü alınır (alınamazsa koşu durmaz, kartta "korumasız" yazar).
        var wantSnapshot = !string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath);
        if (!wantBaseline && !wantSnapshot) return StartProcess(runId, job, projectPath, st, prompt, resumeSession, modelOverride, null, gateAttempt);
        Task<Soz.Core.Restore.SnapshotResult>? snapTask = null;
        if (!wantBaseline)
        {
            snapTask = Task.Run(() => Restore.TakeBeforeRun(runId));
            if (snapTask.Wait(SnapshotSyncWait)) // küçük klasör: eskisi gibi eşzamanlı başla
            {
                NoteSnapshot(runId, job, snapTask.Result);
                return StartProcess(runId, job, projectPath, st, prompt, resumeSession, modelOverride, null, gateAttempt);
            }
        }

        // BAŞLANGIÇ ÖLÇÜMÜ (denetim 3, İlke 1): ajan başlamadan ÖNCE açık ölçütler bir kez koşulur; sayılar işe yazılır.
        // İstek bekletilmez: süreç satırı "çalışıyor" görünür, ölçüm arka planda biter, sonra ajan başlar.
        var act = new ActiveRun { RunId = runId, JobId = jobId, GateAttempt = gateAttempt };
        _active[runId] = act;
        act.Done = Task.Run(async () =>
        {
            Task? monitor = null;
            try
            {
                if (wantBaseline)
                {
                    using (var c = _db.Open()) RunStore.AddLine(c, runId, jobId, job.ProjectId, "progress", "Başlamadan önce durumu ölçüyorum", null);
                    var budget = Math.Min(WorkService.GateBudgetSeconds(st), 120);
                    BaselineText.Baseline? b = null;
                    try { b = ProofStore.MeasureBaseline(_db, jobId, budget, () => act.StopReason is not null); }
                    catch (Exception) { /* ölçüm yapılamazsa ajan yine başlar; karşılaştırma tabanı terminal yolundaki gibi önceki kanıttır */ }
                    using (var c = _db.Open())
                    {
                        if (b is not null)
                        {
                            c.Exec("UPDATE jobs SET baseline_json=@b WHERE id=@j", null, ("@b", BaselineText.Serialize(b)), ("@j", jobId));
                            RunStore.AddLine(c, runId, jobId, job.ProjectId, "progress", BaselineLine(b), null);
                        }
                        if (act.StopReason is not null) return; // ölçüm sırasında durduruldu: Stop() kayıtları zaten yazdı
                        EvidenceBase.TakeForJob(c, jobId, refresh: true); // ölçümün kendi ürettiği dosyalar taban sayılır; ajan bundan sonra başlar
                    }
                }
                if (wantSnapshot) // ölçümün ürettiği dosyalardan SONRA: geri alma bunları ajanın işi saymasın
                {
                    if (snapTask is not null && act.StopReason is null) { using var c = _db.Open(); RunStore.AddLine(c, runId, jobId, job.ProjectId, "progress", "Geri alma noktası alınıyor (klasör büyük, biraz sürebilir)", null); }
                    var snap = snapTask is null ? Restore.TakeBeforeRun(runId) : await snapTask.ConfigureAwait(false);
                    NoteSnapshot(runId, job, snap);
                    if (act.StopReason is not null) return; // görüntü sırasında durduruldu
                }
                StartProcess(runId, job, projectPath, st, prompt, resumeSession, modelOverride, act, gateAttempt);
                monitor = act.MonitorTask;
            }
            catch (Exception e)
            {
                try { using var c = _db.Open(); Fail(runId, job, "Başlangıç ölçümünde beklenmedik hata: " + SecretFilter.MaskSecrets(e.Message)); } catch (Exception) { }
            }
            finally
            {
                if (monitor is null) { _active.TryRemove(runId, out _); try { PumpQueue(); } catch (Exception) { } }
            }
            if (monitor is not null) await monitor.ConfigureAwait(false);
        });
        using var rc = _db.Open();
        return RunStore.Get(rc, runId)!;
    }

    private static string BaselineLine(BaselineText.Baseline b)
    {
        if (b.Total is { } t && t > 0)
        {
            var failed = Math.Max(0, t - (b.Passed ?? 0) - b.Skipped);
            return failed > 0 ? $"Başlamadan önce {t} testten {BaselineText.Poss(failed)} geçmiyor." : $"Başlamadan önce {t} testin hepsi geçiyor.";
        }
        return b.Verdict == Verdicts.Passed ? "Başlamadan önce kontroller geçiyor." : b.Verdict == Verdicts.Failed ? "Başlamadan önce kontroller geçmiyor." : "Başlamadan önce kontrollerin durumu belirsiz.";
    }

    /// <summary>Ajan sürecini başlatır. existing doluysa (başlangıç ölçümü yolu) o koşu nesnesi kullanılır; izleme görevi existing.MonitorTask'a konur.</summary>
    private RunDto StartProcess(long runId, JobDto job, string? projectPath, SozSettings st, string prompt, string? resumeSession, string? modelOverride, ActiveRun? existing, int gateAttempt = 1)
    {
        var jobId = job.Id;
        // ---- ön koşullar: sade neden metniyle başarısız ol, sunucuyu bozma ----
        IAgentConnector connector;
        try { connector = AgentRegistry.Get(job.Agent); }
        catch (SozException e) { return Fail(runId, job, e.Message); }
        var res = connector.Locate(st);
        if (res.Cmd is null) return Fail(runId, job, res.Error ?? $"{connector.DisplayName} bulunamadı.");
        var cmd = res.Cmd;
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            return Fail(runId, job, "Projenin klasörü bulunamadı. Klasörü yeniden ekle.");
        var model = string.IsNullOrWhiteSpace(modelOverride) ? connector.DefaultModel(st) : modelOverride.Trim();
        if (ModelName.Check(model) is { } modelErr) return Fail(runId, job, modelErr);

        var ctx = new AgentRunContext(projectPath, prompt, model, AgentPermissionMode.Careful, runId, jobId, st, () => WriteHookSettings(st));
        AgentLaunch launch;
        try { launch = resumeSession is null ? connector.BuildStart(cmd, ctx) : connector.BuildResume(cmd, ctx, resumeSession); }
        catch (AgentLaunchException e) { return Fail(runId, job, e.Message); }

        var tr = connector.CreateEventStream();
        var act = existing ?? new ActiveRun { RunId = runId, JobId = jobId };
        act.Connector = connector;
        act.GateAttempt = gateAttempt;
        act.ModelUsed = model;
        var stderrTail = new Queue<string>();
        // Bağlayıcının eklediği değişkenler (.cmd sarmalayıcısının ayarları dahil) + Söz'ün kendi işaretleri. Silinecekler (faturalı anahtar vb.) araç başınadır.
        var env = new Dictionary<string, string?>(launch.AddEnv, StringComparer.OrdinalIgnoreCase) { ["SOZ_RUN_ID"] = runId.ToString(), ["SOZ_JOB_ID"] = jobId.ToString() };

        ProcessTree tree;
        try
        {
            tree = ProcessTree.StartStreaming(launch.FileName, launch.Args, projectPath, env, launch.Stdin,
                line => OnStdout(act, job, tr, line),
                err => { lock (stderrTail) { stderrTail.Enqueue(err); while (stderrTail.Count > 20) stderrTail.Dequeue(); } },
                launch.RemoveEnv, _db.DataDir);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return Fail(runId, job, StartFailure.Sentence(connector.DisplayName, e), SecretFilter.MaskSecrets(e.Message));
        }
        act.Tree = tree;
        _active[runId] = act;
        using (var c = _db.Open())
        {
            RunStore.SetProcess(c, runId, tree.Pid, tree.StartTimeUtc);
            RunStore.EmitState(c, runId);
        }
        act.MonitorTask = Task.Run(() => MonitorAsync(act, job, tr, tree, stderrTail, st));
        if (existing is null) act.Done = act.MonitorTask;
        using var rc = _db.Open();
        return RunStore.Get(rc, runId)!;
    }

    private RunDto Fail(long runId, JobDto job, string reason, string? technical = null)
    {
        using var c = _db.Open();
        RunStore.SetStatus(c, runId, RunStatus.Failed, lastLine: reason, failReason: reason, ended: true, force: true);
        RunStore.AddLine(c, runId, job.Id, job.ProjectId, "error", reason, technical);
        JobStartFailed(c, job.Id, job.ProjectId, reason);
        return RunStore.Get(c, runId)!;
    }

    private void OnStdout(ActiveRun act, JobDto job, IAgentEventStream tr, string line)
    {
        try
        {
            var before = tr.SessionId;
            var lines = tr.Feed(line);
            if (tr.SessionId is { } sid && sid != before) OnSession(act, job, sid);
            if (lines.Count == 0) return;
            using var c = _db.Open();
            foreach (var l in lines)
            {
                RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, l.Kind, l.Text, l.Detail);
                try { LineEmitted?.Invoke(act.JobId, act.RunId, l); } catch (Exception) { }
            }
        }
        catch (Exception) { /* bir satırın çevrilememesi koşuyu bozmaz */ }
    }

    /// <summary>init olayı: oturum kimliğini kaydet, işi bu oturuma bağla (kapı bu oturumun Stop kancasında işi bulur).</summary>
    private void OnSession(ActiveRun act, JobDto job, string sessionId)
    {
        using var c = _db.Open();
        RunStore.SetSession(c, act.RunId, sessionId);
        var path = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", job.ProjectId)) as string;
        var (sid, _) = IngestStore.EnsureSession(c, null, act.Connector?.SessionTool ?? AgentRegistry.Get(null).SessionTool, sessionId, path, null, SessionKinds.Human);
        c.Exec("UPDATE jobs SET session_id=@s WHERE id=@j", null, ("@s", sid), ("@j", act.JobId));
        var state = c.Scalar("SELECT state FROM jobs WHERE id=@j", null, ("@j", act.JobId)) as string;
        if (state is JobStates.Draft or JobStates.Given) JobStore.SetState(c, act.JobId, JobStates.Running, job.ProjectId);
    }

    private async Task MonitorAsync(ActiveRun act, JobDto job, IAgentEventStream tr, ProcessTree tree, Queue<string> stderrTail, SozSettings st)
    {
        int? exit = null;
        var timedOut = false;
        GateRetry? retry = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(st.RunnerMaxMinutes));
            try { exit = await tree.WaitExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { timedOut = true; tree.KillTree(); try { exit = await tree.WaitExitAsync().ConfigureAwait(false); } catch (Exception) { } }

            using var c = _db.Open();
            foreach (var l in tr.Flush())
            {
                RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, l.Kind, l.Text, l.Detail);
                try { LineEmitted?.Invoke(act.JobId, act.RunId, l); } catch (Exception) { }
            }
            try { RunStore.SetUsage(c, act.RunId, tr.Usage); } catch (Exception) { /* harcama yazılamazsa koşu bozulmaz */ } // 12. işlem: harcama (Claude dolar, Codex token)
            var final = tr.LastAgentText ?? tr.ResultText;
            var cur = RunStore.Get(c, act.RunId);
            var wasStopped = act.StopReason is not null || cur is { Status: RunStatus.Stopped };

            if (wasStopped)
            {
                var why = act.StopReason ?? "durduruldu";
                if (cur is { Status: RunStatus.Running or RunStatus.WaitingApproval })
                {
                    RunStore.SetStatus(c, act.RunId, RunStatus.Stopped, lastLine: why, exitCode: exit, ended: true);
                    RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, "state", why == "durduruldu" ? "İş durduruldu." : why, null);
                    // Araç işi kendi hizmetinde sürdürüyorsa (Capabilities.Stop false, ör. Hermes) kişi bunu bilir: durdurmayı garanti etmeyiz.
                    if (act.Connector?.StopNote is { } stopNote) RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, "state", stopNote, null);
                }
                ProofStore.InterruptRunning(c, act.JobId); // K-4: yarıda kalan "deneniyor" kanıt satırı yarıda kesildi olur
                JobTo(c, act.JobId, job.ProjectId, JobStates.Stopped);
                return;
            }
            if (timedOut)
            {
                var msg = $"Süre doldu ({st.RunnerMaxMinutes} dk), yardımcı durduruldu. Sürdürmek için \"Aynı işe yeni bir şey ekle\" düğmesini kullanabilirsin.";
                RunStore.SetStatus(c, act.RunId, RunStatus.Failed, lastLine: msg, failReason: msg, exitCode: exit, ended: true);
                RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, "error", msg, null);
                ProofStore.InterruptRunning(c, act.JobId);
                JobTo(c, act.JobId, job.ProjectId, JobStates.Stopped);
                return;
            }

            var failed = exit != 0 || tr.ResultIsError || !tr.ResultSeen;
            var pending = RunStore.PendingApprovals(c, act.JobId);
            if (pending.Count > 0 && exit == 0 && !tr.ResultIsError)
            {
                RunStore.SetStatus(c, act.RunId, RunStatus.WaitingApproval, lastLine: $"senden onay bekliyor: {pending[0].Summary}", exitCode: exit, ended: true);
                RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, "state", pending.Count > 1 ? $"Yardımcı durdu, senden {pending.Count} onay bekliyor." : "Yardımcı durdu, senden onay bekliyor.", null, live: true);
                JobTo(c, act.JobId, job.ProjectId, JobStates.AwaitingApproval);
                return;
            }
            if (failed)
            {
                string stderr; lock (stderrTail) stderr = string.Join("\n", stderrTail);
                var reason = (act.Connector ?? AgentRegistry.Get(job.Agent)).FailureReason(exit, tr, stderr);
                RunStore.SetStatus(c, act.RunId, RunStatus.Failed, lastLine: reason, failReason: reason, exitCode: exit, ended: true);
                // Kişiye giden cümle (reason) sade kalır; yardımcının ham hata metni ve çıkış kodu yalnız teknik ayrıntıya (detail) yazılır.
                var rawParts = new List<string>();
                if (exit is { } ex && ex != 0) rawParts.Add($"çıkış kodu {ex}");
                if (!string.IsNullOrWhiteSpace(tr.ResultText)) rawParts.Add(tr.ResultText.Trim());
                if (!string.IsNullOrWhiteSpace(stderr)) rawParts.Add(stderr);
                RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, "error", reason, SecretFilter.MaskSecrets(TextNormalizer.Truncate(string.Join("\n", rawParts), 1500)));
                ProofStore.InterruptRunning(c, act.JobId);
                JobStartFailed(c, act.JobId, job.ProjectId, reason);
                AgentWord.ClassifyAndStore(_db, act.JobId, job.ProjectId, final, "run_end");
                return;
            }
            RunStore.SetStatus(c, act.RunId, RunStatus.Done, lastLine: null, exitCode: exit, ended: true);
            RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, "state", "Yardımcı işi bitirdi.", null);
            AgentWord.ClassifyAndStore(_db, act.JobId, job.ProjectId, final, "run_end");
            retry = ConcludeJob(act, job, cur?.Started, st, final);
            if (retry is null) { try { SuggestAgentTest(job); } catch (Exception) { /* öneri kaybı işi bozmaz */ } }
        }
        catch (Exception e)
        {
            try
            {
                using var c = _db.Open();
                var msg = "Yürütücüde beklenmedik hata: " + SecretFilter.MaskSecrets(e.Message);
                RunStore.SetStatus(c, act.RunId, RunStatus.Failed, lastLine: msg, failReason: msg, ended: true);
                RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, "error", msg, null);
                JobStartFailed(c, act.JobId, job.ProjectId, msg);
            }
            catch (Exception) { }
        }
        finally
        {
            try { tree.Dispose(); } catch (Exception) { }
            try { Restore.TakeAfterRun(act.RunId); } catch (Exception) { } // ajanın son hâli: geri almada "ajanın değişikliği" bununla karşılaştırılır
            _active.TryRemove(act.RunId, out _);
            // Dış kapı döngüsü: kapı kaldı, ajan aynı oturumda yeni tur olarak geri yollanır. Bu tur artık kayıtlı değil, yenisi açılabilir.
            if (retry is not null) StartGateRetry(act, job, retry);
            try { PumpQueue(); } catch (Exception) { } // yer açıldı: sıradaki iş kendiliğinden başlar
        }
    }

    /// <summary>
    /// "Önce deneme yaz" ile başlayan iş bitince klasörde test komutu belirdiyse (ajan yazdı) onu KAPALI ölçüt olarak önerir (düzey "ajan beyanı").
    /// Kendiliğinden açılmaz: ajanın yazdığı test kişi kabul edene kadar ölçülmez. Aynı komut zaten bir ölçütse (açık ya da kapalı) yeniden önerilmez.
    /// </summary>
    private void SuggestAgentTest(JobDto job)
    {
        using var c = _db.Open();
        if (c.ScalarLong("SELECT COALESCE(write_check_first,0) FROM jobs WHERE id=@j", null, ("@j", job.Id)) != 1) return;
        var root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", job.ProjectId)) as string;
        var (test, _) = CheckTemplates.Detect(root);
        if (test is null) return;
        string Cmd(CheckDto x) => x.Spec["cmd"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        if (CheckStore.List(c, job.ProjectId, job.Id).Any(x => x.Kind == CheckKinds.Command && Cmd(x).Equals(test, StringComparison.OrdinalIgnoreCase))) return;
        var spec = new System.Text.Json.Nodes.JsonObject { ["cmd"] = test, ["expectExit"] = 0, ["suggestedByJob"] = job.Id };
        try
        {
            // N5: denemenin gücü işten ÖNCEKİ kodla ölçülür; zayıfsa varsayılan cevap Hayır, her zaman başarılı komut kabul edilemez.
            var sr = MeasureStrength(c, job, root!, test);
            spec["strength"] = sr.Kind; spec["strengthNote"] = sr.Sentence; spec["defaultAnswer"] = sr.DefaultAnswer; spec["rejected"] = sr.Rejected;
        }
        catch (Exception) { var um = AgentTestStrength.Unmeasured("ölçüm sırasında hata oldu."); spec["strength"] = um.Kind; spec["strengthNote"] = um.Sentence; spec["defaultAnswer"] = "hayir"; spec["rejected"] = false; }
        CheckStore.Insert(c, null, job.ProjectId, null, CheckKinds.Command, $"Yardımcının yazdığı deneme geçsin ({test})",
            spec, CheckStore.DefaultTimeoutS, enabled: false, origin: CheckStore.AgentTestOrigin);
    }

    private StrengthResult MeasureStrength(Microsoft.Data.Sqlite.SqliteConnection c, JobDto job, string root, string test)
    {
        if (AgentTestStrength.AlwaysSucceeds(test, root)) return new StrengthResult(AgentTestStrength.Always, AgentTestStrength.AlwaysSentence, "hayir", true);
        var st = SozSettings.Load(c);
        var now = EvidenceBase.ScanProject(c, job.ProjectId, job.Id, root);
        var copyMax = (long)st.ClampedInt("setting:proof.verdict-copy-max-mb") << 20;
        var timeoutS = Math.Min(st.ClampedInt("setting:proof.verdict-run-seconds"), WorkService.GateBudgetSeconds(st));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var res = MeasureBefore(c, job, root, test, now, copyMax, timeoutS);
        var remainingS = (int)Math.Floor(timeoutS - clock.Elapsed.TotalSeconds);
        if (remainingS <= 0) return AgentTestStrength.Unmeasured("Süre sınırı doldu.");
        // K6: kalıp listesi yetmez, komut DAVRANIŞLA sınanır. Testler kesin kalan bir kopyada komut yine başarılıysa komut sonucu yutuyordur: kabul edilemez.
        if (res.Kind != AgentTestStrength.Always && AgentTestStrength.SwallowsFailure(root, _db.DataDir, job.Id, test, now, copyMax, remainingS) == true)
            return new StrengthResult(AgentTestStrength.Always, AgentTestStrength.AlwaysSentence, "hayir", true);
        if (clock.Elapsed.TotalSeconds >= timeoutS - 1) return AgentTestStrength.Unmeasured("Süre sınırı doldu.");
        return res;
    }

    private StrengthResult MeasureBefore(Microsoft.Data.Sqlite.SqliteConnection c, JobDto job, string root, string test, EvidenceBase.Snapshot now, long copyMax, int timeoutS)
    {
        var uid = c.Scalar("SELECT before_snapshot_uid FROM runs WHERE job_id=@j AND before_snapshot_uid IS NOT NULL ORDER BY id LIMIT 1", null, ("@j", job.Id)) as string;
        if (uid is null) return AgentTestStrength.Unmeasured("iş öncesi görüntü yok.");
        var snaps = Restore.Snapshots;
        Soz.Core.Restore.SnapshotRow? row;
        using (var rc = snaps.Depo.Open()) row = snaps.Depo.GetSnapshotByUid(rc, uid);
        if (row is null) return AgentTestStrength.Unmeasured("iş öncesi görüntü depoda bulunamadı.");
        var before = snaps.Resolve(row.Id);
        using var blobs = DepotBlobs.Wrap(snaps.Depo);
        return AgentTestStrength.Measure(root, _db.DataDir, job.Id, test, before, blobs, now, copyMax, timeoutS);
    }

    private void StartGateRetry(ActiveRun act, JobDto job, GateRetry retry)
    {
        try
        {
            string? session;
            using (var c = _db.Open()) session = RunStore.LastSessionId(c, act.JobId);
            if (session is null) throw new SozException(409, "Oturum bulunamadı.");
            var run = Launch(act.JobId, "gate", retry.Message, session, string.IsNullOrWhiteSpace(act.ModelUsed) ? null : act.ModelUsed, null, retry.Note, gateAttempt: retry.NextAttempt);
            _ = run; // null: sıraya girdi, biri bitince kendiliğinden başlar
        }
        catch (Exception e)
        {
            try
            {
                using var c = _db.Open();
                var msg = "Yardımcıya geri yollanamadı: " + SecretFilter.MaskSecrets(e.Message);
                RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, "error", msg, null);
                JobTo(c, act.JobId, job.ProjectId, JobStates.Given);
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Süreç temiz bitti ama iş hâlâ "calisiyor" ise hüküm vermeden bırakılmaz:
    /// ajan soru sorduysa iş "cevap_bekliyor" olur; bu süreçte kapı zaten çalışmışsa onun hükmü işe yansır (nihai hüküm SON kapı sonucudur);
    /// hiç çalışmamışsa kapıyı Söz kendisi çalıştırır. "Geçti" yalnız kapı geçtiyse yazılır. Kanıtı bozulmuş görünen iş "karar_bekliyor" olur.
    /// </summary>
    private GateRetry? ConcludeJob(ActiveRun act, JobDto job, string? runStarted, SozSettings st, string? finalText)
    {
        // Kancası olmayan araç (Codex): kapı süreç çıkınca Söz'ün kendisi tarafından koşar, kalırsa aynı oturuma geri yollanır.
        if (act.Connector?.Capabilities.TurnEnd == TurnEndMode.OuterLoop) return ConcludeOuter(act, job, st, finalText);
        try
        {
            string? verdict; string? reasonCode;
            long? sessionDbId;
            using (var c = _db.Open())
            {
                var state = c.Scalar("SELECT state FROM jobs WHERE id=@j", null, ("@j", act.JobId)) as string;
                if (state != JobStates.Running) return null;
                var agent = c.Scalar("SELECT agent_state FROM jobs WHERE id=@j", null, ("@j", act.JobId)) as string;
                if (GateHooks.IsQuestion(agent, finalText)) { JobStore.SetAwaitingAnswer(c, act.JobId, finalText, job.ProjectId); return null; }
                var last = c.Query("SELECT verdict, reason_code FROM proof_runs WHERE job_id=@j AND started_at>=@s AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1",
                    r => (v: r.GetString(0), rc: r.Str(1)), null, ("@j", act.JobId), ("@s", runStarted ?? "")).FirstOrDefault();
                verdict = last.v; reasonCode = last.rc;
                sessionDbId = c.Scalar("SELECT session_id FROM jobs WHERE id=@j", null, ("@j", act.JobId)) as long?;
                if (verdict is null)
                {
                    var attempt = (int)c.ScalarLong("SELECT COUNT(*) FROM proof_runs WHERE job_id=@j", null, ("@j", act.JobId)) + 1;
                    var runId = ProofStore.Begin(c, new ProofStore.RunRequest(job.ProjectId, act.JobId, sessionDbId, "run_end", attempt));
                    var run = ProofStore.Complete(_db, runId, WorkService.GateBudgetSeconds(st));
                    verdict = run.Verdict; reasonCode = run.ReasonCode;
                }
                var next = verdict switch
                {
                    Verdicts.Passed => JobStates.Passed,
                    Verdicts.Failed => JobStates.Failed,
                    Verdicts.Unchecked when ProofStore.Reasons.IsTampering(reasonCode) => JobStates.AwaitingDecision,
                    _ => JobStates.Given,
                };
                JobStore.SetState(c, act.JobId, next, job.ProjectId);
            }
        }
        catch (Exception)
        {
            try { using var c = _db.Open(); JobTo(c, act.JobId, job.ProjectId, JobStates.Given); } catch (Exception) { }
        }
        return null;
    }

    /// <summary>
    /// DIŞ KAPI DÖNGÜSÜ (kancasız araç). Tur bitince Söz ölçütleri kendisi koşar (ProofRunner, ajan iş nesnesinde; başlangıç ölçümü, kanıt tabanı ve hile tespiti aynen).
    /// Geçerse iş "geçti". Kalırsa ve tur sınırı (gateMaxAttempts, varsayılan 3 kapı turu) dolmadıysa neden aynı oturuma yeni tur olarak geri yollanır (GateRetry döner);
    /// sınır dolunca "kaldı". Test dosyası, test tanımı ya da test sayısı değişmişse (hile) bir kez geri yollanır, ısrar ederse iş "karar bekliyor" olur.
    /// Hükmün işe yazılması Claude'un Stop kancasındaki (GateHooks.Stop) mantıkla aynıdır.
    /// </summary>
    private GateRetry? ConcludeOuter(ActiveRun act, JobDto job, SozSettings st, string? finalText)
    {
        try
        {
            using var c = _db.Open();
            var state = c.Scalar("SELECT state FROM jobs WHERE id=@j", null, ("@j", act.JobId)) as string;
            if (state != JobStates.Running) return null;
            var agentState = c.Scalar("SELECT agent_state FROM jobs WHERE id=@j", null, ("@j", act.JobId)) as string;
            if (GateHooks.IsQuestion(agentState, finalText)) { JobStore.SetAwaitingAnswer(c, act.JobId, finalText, job.ProjectId); return null; }

            var attempt = Math.Max(1, act.GateAttempt);
            var max = WorkService.GateMaxAttempts(st);
            var sessionDbId = c.Scalar("SELECT session_id FROM jobs WHERE id=@j", null, ("@j", act.JobId)) as long?;
            var canResume = (act.Connector?.Capabilities.Resume ?? false) && RunStore.LastSessionId(c, act.JobId) is not null;
            var (_, claimsDone, evidence) = SessionStateResolver.Flags(finalText);
            var runId = ProofStore.Begin(c, new ProofStore.RunRequest(job.ProjectId, act.JobId, sessionDbId, "outer_gate", attempt,
                finalText is null ? null : claimsDone, finalText is null ? null : evidence));
            var run = ProofStore.Complete(_db, runId, WorkService.GateBudgetSeconds(st));

            if (run.Verdict == Verdicts.Unchecked && ProofStore.Reasons.IsTampering(run.ReasonCode))
            {
                var prevReason = c.Scalar("SELECT reason_code FROM proof_runs WHERE job_id=@j AND trigger='outer_gate' AND id<@i ORDER BY id DESC LIMIT 1", null, ("@j", act.JobId), ("@i", run.Id)) as string;
                if (!canResume || attempt >= max || (attempt > 1 && ProofStore.Reasons.IsTampering(prevReason)))
                {
                    JobStore.SetState(c, act.JobId, JobStates.AwaitingDecision, job.ProjectId);
                    RunStore.AddLine(c, act.RunId, act.JobId, job.ProjectId, "state", "Sonucu doğrulayamıyorum, karar sende.", null);
                    return null;
                }
                return new GateRetry(GateHooks.TamperReason(run), attempt + 1, $"Deneme sonucu sayılamadı, yardımcıya geri yollandı (tur {attempt + 1}/{max}).");
            }
            if (run.Verdict == Verdicts.Failed && attempt < max && canResume)
                return new GateRetry(GateHooks.FailReason(run, attempt, max, r => GateHooks.FullOutput(_db, run.Id, r)), attempt + 1,
                    $"Kontroller geçmiyor, yardımcıya geri yollandı (tur {attempt + 1}/{max}).");

            var next = run.Verdict switch
            {
                Verdicts.Passed => JobStates.Passed,
                Verdicts.Failed => JobStates.Failed,
                Verdicts.Unchecked when ProofStore.Reasons.IsTampering(run.ReasonCode) => JobStates.AwaitingDecision,
                _ => JobStates.Given,
            };
            JobStore.SetState(c, act.JobId, next, job.ProjectId);
            if (next == JobStates.Passed) c.Exec("UPDATE jobs SET accepted_by_user=0 WHERE id=@j", null, ("@j", act.JobId));
        }
        catch (Exception)
        {
            try { using var c = _db.Open(); JobTo(c, act.JobId, job.ProjectId, JobStates.Given); } catch (Exception) { }
        }
        return null;
    }

    // ------------------------------------------------------------------ kanca ayar dosyası
    public string ResolveSozExe()
    {
        if (!string.IsNullOrWhiteSpace(SozExePath)) return Path.GetFullPath(SozExePath);
        var beside = Path.Combine(AppContext.BaseDirectory, "soz.exe");
        if (File.Exists(beside)) return beside;
        var self = Environment.ProcessPath;
        if (self is not null && Path.GetFileNameWithoutExtension(self).Equals("soz", StringComparison.OrdinalIgnoreCase)) return self;
        throw new FileNotFoundException("soz.exe bulunamadı (uygulama klasöründe olmalı).");
    }

    public string ResolveHookExe()
    {
        var soz = ResolveSozExe();
        var kanca = Path.Combine(Path.GetDirectoryName(soz) ?? "", "soz-kanca.exe");
        return File.Exists(kanca) ? kanca : soz;
    }

    /// <summary>Söz veri klasöründe kanca ayar dosyası (beş kanca, soz-kanca.exe ya da soz.exe mutlak yolu ve --data ile). Aynıysa dokunulmaz.</summary>
    public string WriteHookSettings(SozSettings st)
    {
        var exe = ResolveHookExe();
        if (!File.Exists(exe)) throw new FileNotFoundException("soz.exe yok: " + exe);
        var dir = Path.Combine(_db.DataDir, "yurutucu");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "claude-ayar.json");
        lock (_settingsLock)
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                HookInstaller.Install(tmp, exe, _db.DataDir, HookInstaller.DotnetRootFor(exe), null, WorkService.GateBudgetSeconds(st), backup: false);
                var fresh = File.ReadAllText(tmp);
                if (!File.Exists(path) || File.ReadAllText(path) != fresh) File.Move(tmp, path, overwrite: true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
        }
        return path;
    }
}
