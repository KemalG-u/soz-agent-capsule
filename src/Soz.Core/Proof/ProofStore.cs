using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;

namespace Soz.Core.Proof;

/// <summary>Kanıt çalışmaları: başlat, ölçütleri sırayla çalıştır, sonucu yaz. Hüküm kuralları sözleşme bölüm 4 (M3).</summary>
public static partial class ProofStore
{
    public sealed record RunRequest(long ProjectId, long? JobId, long? SessionId, string Trigger, int Attempt, bool? ClaimsDone = null, bool? ShowsEvidence = null);

    public static string EvidenceDir(SozDb db, long runId) => Path.Combine(db.DataDir, "kanit", runId.ToString());

    public const string OrphanSummary = "Deneme çalışması yarıda kesildi (Söz ya da terminal desteği kapandı). Sonuç bilinmiyor. Bu bir \"geçti\" değildir.";
    public const string InterruptedText = "Deneme çalışması yarıda kesildi. Sonuç bilinmiyor. Bu bir \"geçti\" değildir.";

    public static class Reasons
    {
        public const string NoChecks = "no_checks", NoTests = "no_tests", DefinitionChanged = "definition_changed", TestsChanged = "tests_changed", CountDropped = "count_dropped", Interrupted = "interrupted",
            ChecksChanged = "checks_changed", NotTracked = "not_tracked", DepsChanged = "deps_changed", NoBase = "no_base", AwaitingHuman = "awaiting_human", NothingMeasured = "nothing_measured",
            ApprovedFailed = "approved_failed", AgentTestsFail = "agent_tests_fail", VerdictUnavailable = "verdict_unavailable", NoChange = "no_change", NoWork = "no_work", IgnoredOnly = "ignored_only"; // no_work: iş değişiklik istiyordu (ya da isteyip istemediği bilinmiyor) ama klasörde hiçbir değişiklik yok; hüküm unchecked, asla passed. // ignored_only: ajan yalnız izlenmeyen klasörlere (coverage, dist, .git) yazdı, kaynakta değişiklik yok. // approved_failed: onayladığın testler ajanın yeni koduyla geçmiyor. agent_tests_fail: onaylı testler geçiyor, ajanın testleri geçmiyor. verdict_unavailable: onaylı testlerle ikinci koşu yapılamadı. no_change: ajan hiçbir dosyayı değiştirmedi. awaiting_human: insan onay listesinde işaretlenmemiş madde var. nothing_measured: ölçücü ölçülecek bir şey bulamadı. not_tracked: izleyemediğim kadar çok dosya var. no_base: iş başındaki görüntü alınamadı. deps_changed: bağımlılık klasörü elle değiştirildi. checks_changed: ölçüt (Söz tarafı) iş sırasında değişti: ajanın kurcalaması sayılmaz, kapı geri yollamaz
        /// <summary>Ajanın kanıtı bozmuş olabileceği nedenler: kapı bir kez geri yollar, ısrar ederse iş karar bekler.</summary>
        public static bool IsTampering(string? r) => r is DefinitionChanged or TestsChanged or CountDropped or DepsChanged or ApprovedFailed or AgentTestsFail;
    }

    /// <summary>Kullanıcı kabulü: işin son kanıt çalışması "kullanıcı kabul etti" diye işaretlenir; sonraki sayı karşılaştırmasında bu sonuç güvenilir taban olur.</summary>
    public static void AcceptLatest(SqliteConnection c, long jobId)
        => c.Exec("UPDATE proof_runs SET accepted=1 WHERE id=(SELECT MAX(id) FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL)", null, ("@j", jobId));

    /// <summary>
    /// Yarıda kalan ("deneniyor" görünen) çalışmalar "error / yarıda kesildi" olur (K-4). jobId verilirse yalnız o işin çalışmaları.
    /// Sürmekte olan gerçek bir çalışma sonradan kendi hükmünü yazarsa bu işaret ezilir.
    /// </summary>
    public static int InterruptRunning(SqliteConnection c, long? jobId = null)
    {
        var ids = c.Query("SELECT id, project_id, job_id FROM proof_runs WHERE verdict IS NULL AND finished_at IS NULL AND (@j IS NULL OR job_id=@j)",
            r => (id: r.GetInt64(0), p: r.Long(1), j: r.LongN(2)), null, ("@j", jobId));
        foreach (var (id, p, j) in ids)
        {
            c.Exec("UPDATE proof_runs SET finished_at=@n, verdict='error', summary=@s, reason_code='interrupted', reason_text=@s WHERE id=@id AND verdict IS NULL", null,
                ("@n", Time.NowIso()), ("@s", InterruptedText), ("@id", id));
            Live.Emit(c, Live.Proof, p, new { runId = id, projectId = p, jobId = j, phase = "finished", verdict = Verdicts.Error });
        }
        return ids.Count;
    }

    /// <summary>
    /// Yetim çalışmalar: hükmü boş ve başlangıcı (kapı bütçesi + 120 sn) öncesinde kalan çalışma artık sürmüyor olamaz
    /// (her ölçüt bütçe içinde öldürülür). Bunlar "error" olur. Sunucu açılışında ve her yeni çalışmadan önce çağrılır.
    /// </summary>
    public static int ReapOrphans(SqliteConnection c, int budgetSeconds, DateTimeOffset? now = null)
    {
        var t = now ?? new DateTimeOffset(DateTime.SpecifyKind(Time.UtcNow(), DateTimeKind.Utc));
        var cutoff = t.AddSeconds(-(Math.Max(1, budgetSeconds) + 120)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        var ids = c.Query("SELECT id, project_id, job_id FROM proof_runs WHERE verdict IS NULL AND finished_at IS NULL AND started_at < @c",
            r => (id: r.GetInt64(0), p: r.Long(1), j: r.LongN(2)), null, ("@c", cutoff));
        foreach (var (id, p, j) in ids)
        {
            c.Exec("UPDATE proof_runs SET finished_at=@n, verdict='error', summary=@s, reason_code='interrupted', reason_text=@s WHERE id=@id AND verdict IS NULL", null,
                ("@n", Time.NowIso()), ("@s", OrphanSummary), ("@id", id));
            Live.Emit(c, Live.Proof, p, new { runId = id, projectId = p, jobId = j, phase = "finished", verdict = Verdicts.Error });
        }
        return ids.Count;
    }

    public static long Begin(SqliteConnection c, RunRequest req)
    {
        try { ReapOrphans(c, Jobs.WorkService.GateBudgetSeconds(SozSettings.Load(c))); } catch (SqliteException) { /* yetim temizliği çalışmayı engellemez */ }
        c.Exec("""
            INSERT INTO proof_runs(project_id, job_id, session_id, trigger, attempt, started_at, claims_done, shows_evidence, uid)
            VALUES(@p,@j,@s,@t,@a,@n,@cd,@se,@u)
            """, null, ("@u", Uid.New()), ("@p", req.ProjectId), ("@j", req.JobId), ("@s", req.SessionId), ("@t", req.Trigger), ("@a", req.Attempt), ("@n", Time.NowIso()),
            ("@cd", req.ClaimsDone is null ? null : req.ClaimsDone.Value ? 1 : 0), ("@se", req.ShowsEvidence is null ? null : req.ShowsEvidence.Value ? 1 : 0));
        var id = c.ScalarLong("SELECT last_insert_rowid()");
        Live.Emit(c, Live.Proof, req.ProjectId, new { runId = id, projectId = req.ProjectId, jobId = req.JobId, phase = "started" });
        return id;
    }

    /// <summary>Çalışmayı yürütür ve bitirir. Bağlantı ölçütler çalışırken açık tutulmaz.</summary>
    public static ProofRunDto Complete(SozDb db, long runId, int budgetSeconds, Func<string?>? edgeLocator = null)
    {
        long projectId; long? jobId; string? root;
        List<CheckDto> checks;
        long? jobProject = null; string? storedHash = null, nowHash = null;
        EvidenceBase.Snapshot? baseSnap = null; string? baseNote = null;
        BaselineText.Baseline? baseline = null;
        EvidenceBase.Snapshot? snapNow = null; // ölçütler koşmadan ÖNCE alınır: sınamanın kendi ürettiği dosyalar ajan işi sayılmasın
        var writeCheckFirst = false; string? jobAgent = null; List<string> outsideWrites = new();
        using (var c = db.Open())
        {
            var run = c.Query("SELECT project_id, job_id FROM proof_runs WHERE id=@id", r => (p: r.Long(0), j: r.LongN(1)), null, ("@id", runId)).First();
            projectId = run.p; jobId = run.j;
            root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
            checks = CheckStore.EnabledFor(c, projectId, jobId);
            if (jobId is not null)
            {
                var jr = c.Query("SELECT project_id, checks_hash, base_note, baseline_json FROM jobs WHERE id=@j", r => (p: r.LongN(0), h: r.Str(1), n: r.Str(2), b: r.Str(3)), null, ("@j", jobId)).FirstOrDefault();
                jobProject = jr.p; storedHash = jr.h; baseNote = jr.n;
                baseline = BaselineText.Parse(jr.b);
                if (jr.p == projectId) nowHash = CheckStore.DefinitionHash(checks);
                baseSnap = EvidenceBase.Load(c, jobId.Value);
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    try { snapNow = EvidenceBase.ScanProject(c, projectId, jobId, root); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { snapNow = null; }
                }
                try
                {
                    writeCheckFirst = c.ScalarLong("SELECT COALESCE(write_check_first,0) FROM jobs WHERE id=@j", null, ("@j", jobId)) == 1;
                    jobAgent = c.Scalar("SELECT COALESCE(agent,'claude') FROM jobs WHERE id=@j", null, ("@j", jobId)) as string;
                    outsideWrites = OutsideWrites(c, jobId.Value);
                    // Ajanın yazdığı deneme: kişi kabul ederse onaylı tabanın ilerlemesi için dosyalar kaydedilir (B6).
                    if (writeCheckFirst && baseSnap is not null && snapNow is not null) EvidenceBase.RecordAgentFiles(c, projectId, jobId.Value, baseSnap, snapNow);
                }
                catch (SqliteException) { /* kayıt alınamazsa hüküm etkilenmez */ }
            }
        }

        var outcomes = new List<CheckOutcome>();
        string verdict, summary;
        string? reason = null, baselineText = null;
        var changed = new List<string>();
        var added = new List<string>();
        var fileDiffs = new List<EvidenceBase.FileDiff>();
        var noteList = new List<string>();
        string? snapshotId = null;
        string? zeroSentence = null; // önce test koşuyordu, şimdi hiçbiri koşmadı (cümle). Doluysa kanıt tabanı karşılaştırması da çalışır
        var countUnknown = 0; // 0 yok, 1 test sayısı okunamadı, 2 testler atlanmış ve önceki durum bilinmiyor
        if (jobId is not null && jobProject != projectId)
        {
            // İş başka projeye ait: o projenin ölçütleriyle "geçti" olamaz (B-1).
            verdict = Verdicts.Error;
            summary = "Bu iş başka bir projeye ait: proje eşleşmedi, kontroller çalıştırılmadı. Bu bir \"geçti\" değildir.";
        }
        else if (jobId is not null && storedHash is not null && nowHash != storedHash)
        {
            // İş verildiğinden beri ölçüt tanımları değişti ya da ölçüt kapandı (B-3b): çalıştırmadan "geçti" denmez.
            verdict = Verdicts.Unchecked;
            reason = Reasons.ChecksChanged;
            summary = "Kapı sonucu belirsiz: kontrol iş sırasında değişti (tanım, açık ya da kapalı durumu ya da kontrol sayısı iş verildiğindekinden farklı). Kontroller çalıştırılmadı. Bu bir \"geçti\" değildir. Değişikliği Söz'deki olay kaydında görebilirsin.";
        }
        else if (checks.Count == 0)
        {
            verdict = Verdicts.Unchecked;
            reason = Reasons.NoChecks;
            summary = "Bu iş için denenecek açık bir kontrol yok. Bu bir \"geçti\" değildir.";
            // "Önce deneme yaz" işi bitti ve klasörde bir test komutu belirdi: ajanın yazdığı deneme kabulünü bekliyor (B2).
            if (writeCheckFirst && CheckTemplates.Detect(root).test is { } agentTest)
                summary = $"Yardımcı bir deneme yazdı ({agentTest}), sen kabul edersen bundan sonra Söz çalıştırır. Bu iş için denenecek açık bir kontrol yoktu, bu bir \"geçti\" değildir.";
            // C7: kontrolü olmayan klasörde de "hiçbir değişiklik yok" bilgisi söylenir (hüküm zaten kanıtsız, neden no_checks kalır).
            if (jobId is not null && baseSnap is { TreeId: not null } && snapNow is { TreeId: not null } && baseSnap.TreeId == snapNow.TreeId && outsideWrites.Count == 0 && IgnoredChanges(baseSnap, snapNow).Count == 0)
            {
                var ex0 = JobExpectation(db, jobId.Value);
                if (ex0 != ChangeExpectation.Kind.ReadOnly)
                {
                    int? ce = null;
                    try { using var cc0 = db.Open(); ce = ChangeClaim.Read(cc0, jobId.Value); } catch (SqliteException) { }
                    summary = ChangeExpectation.NoChangeSentence(ex0, (ce ?? 0) > 0) + " " + summary;
                }
            }
        }
        else if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            verdict = Verdicts.Error;
            summary = "Proje klasörü bulunamadı, kontroller çalıştırılamadı.";
        }
        else
        {
            var runner = new ProofRunner(root, EvidenceDir(db, runId), edgeLocator, db.DataDir, jobId, runId, TicksFor(db, jobId));
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, budgetSeconds));
            foreach (var ch in checks)
            {
                var left = deadline - DateTime.UtcNow;
                CheckOutcome o;
                if (left < TimeSpan.FromSeconds(1))
                    o = new CheckOutcome(ch.Id, ch.Label, ResultStatus.Skipped, null, $"Toplam süre bütçesi ({budgetSeconds} sn) bitti, çalıştırılmadı.", null, 0);
                else
                {
                    var t = TimeSpan.FromSeconds(Math.Max(1, ch.TimeoutS));
                    o = runner.Run(new CheckToRun(ch.Id, ch.Kind, ch.Label, ch.Spec, ch.TimeoutS), t < left ? t : left);
                }
                outcomes.Add(o);
                using var c = db.Open();
                c.Exec("""
                    INSERT INTO proof_results(run_id, check_id, label, status, exit_code, output_tail, artifact_path, duration_ms, tests_total, tests_passed, tests_skipped, cmd_key)
                    VALUES(@r,@c,@l,@s,@e,@o,@a,@d,@tt,@tp,@ts,@ck)
                    """, null, ("@r", runId), ("@c", o.CheckId), ("@l", o.Label), ("@s", o.Status), ("@e", o.ExitCode), ("@o", o.OutputTail), ("@a", o.ArtifactName), ("@d", o.DurationMs),
                    ("@tt", o.TestsTotal), ("@tp", o.TestsPassed), ("@ts", o.TestsTotal is null ? null : o.SkippedTests), ("@ck", o.CmdKey));
                Live.Emit(c, Live.Proof, projectId, new { runId, projectId, jobId, phase = "check", label = o.Label, status = o.Status });
            }
            // Testin çalışırken kendi yazdığı dosyalar (rapor, log, görüntü kaydı, önbellek) ajan işi sayılmaz: ölçütlerden sonra yeniden taranıp "Söz'ün ürettiği" diye işaretlenir (e3, K3).
            if (jobId is not null && snapNow is not null && !snapNow.NotTracked && !string.IsNullOrWhiteSpace(root))
            {
                try { using var cp = db.Open(); EvidenceBase.AddProduced(cp, projectId, snapNow, EvidenceBase.ScanProject(cp, projectId, jobId, root)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { }
            }
            (verdict, summary) = Judge(outcomes);
            if (verdict == Verdicts.Unchecked && outcomes.Any(x => x.Status == ResultStatus.Unchecked)) reason = outcomes.Where(x => x.Status == ResultStatus.Unchecked).Select(x => x.ReasonCode).FirstOrDefault(r => r is not null) ?? Reasons.NoTests;
            else if (verdict == Verdicts.Unchecked && outcomes.Any(x => x.Status == ResultStatus.AwaitingHuman)) reason = Reasons.AwaitingHuman;
            if (outcomes.Any(x => x.CountUnknown)) countUnknown = 1;
            if (baseline is not null && jobId is not null) baselineText = BaselineText.Compose(baseline, outcomes, verdict);
            // Önce test koşan klasörde şimdi hiç test koşmadıysa (silindi, atlandı, adı değişip bulunamadı) iş "verildi"de sessizce kalmaz: sayı düşmesi gibi karar bekler (B3).
            if (verdict == Verdicts.Unchecked && reason == Reasons.NoTests && jobId is not null)
            {
                using var cz = db.Open();
                zeroSentence = ZeroAfterTests(cz, projectId, runId, baseline, outcomes);
            }

            // KANIT TABANI + HÜKÜM KOŞUSU (e2). Yargıç, kişinin onayladığı testlerdir; ajanın düzenlediği testler değil.
            // Ajanın bıraktığı hâl (A) ölçütler gerçek klasörde koştu. Ajan onaylı sınama kümesine dokunduysa ayrıca ONAYLI testlerle ikinci koşu (B) yapılır
            // (geçici kopyada, ajanın yeni kaynak kodu + onaylı testler). Hüküm iki koşunun birleşimidir; test dosyası farkı hüküm vermez, yalnız karttaki "ne değişti" özetidir.
            var trackedOk = jobId is not null && baseSnap is not null && snapNow is not null && !baseSnap.NotTracked && !snapNow.NotTracked;
            EvidenceBase.Snapshot? approvedSnap = null;
            if (trackedOk) { using var ca = db.Open(); approvedSnap = EvidenceBase.LoadApproved(ca, projectId); }
            var cmdIds = checks.Where(x => x.Kind == CheckKinds.Command).Select(x => x.Id).ToHashSet();
            var cmdOutcomes = outcomes.Where(o => cmdIds.Contains(o.CheckId)).ToList();
            var memberChg = approvedSnap is not null && !approvedSnap.NotTracked && snapNow is not null ? ApprovedSet.Changes(approvedSnap, snapNow) : ApprovedSet.MemberChanges.None;
            // İŞ SEVİYESİ değişiklik (e3, K2): ajan bu işte gerçekten neye dokundu? İş öncesi görüntüye göre (onaylı tabana göre değil, o iş başlamadan önceki elle düzenlemeleri de içerir).
            var producedNames = approvedSnap?.Produced;
            EvidenceBase.Snapshot WithProd(EvidenceBase.Snapshot s) => producedNames is null ? s : s with { Produced = producedNames };
            var jobChg = trackedOk ? ApprovedSet.Changes(WithProd(baseSnap!), WithProd(snapNow!)) : ApprovedSet.MemberChanges.None;
            var canJudgeApproved = trackedOk && approvedSnap is not null && !approvedSnap.NotTracked && cmdOutcomes.Count > 0 && memberChg.Any;
            Judged? vb = null;
            Judged RunB() => vb ??= RunApproved(db, projectId, jobId!.Value, runId, root!, checks, approvedSnap!, snapNow!, edgeLocator, Math.Max(10, (int)(deadline - DateTime.UtcNow).TotalSeconds + 20));

            // Ajan kaldı: onaylı testler yeni kodla geçiyorsa kodda değil, ajanın yazdığı testlerde sorun var. Kişi karar verir.
            if (verdict == Verdicts.Failed && zeroSentence is null && canJudgeApproved && Judge(cmdOutcomes).verdict == Verdicts.Failed)
            {
                var b = RunB();
                if (b.Cat == "passed")
                {
                    verdict = Verdicts.Unchecked; reason = Reasons.AgentTestsFail;
                    baselineText = null;
                    summary = AgentTestsFailSentence(outcomes);
                    AddNamesDiff(fileDiffs, "agent-fail", "Yardımcının testlerinden geçmeyenler", FailedNames(outcomes));
                }
            }

            if ((verdict == Verdicts.Passed || zeroSentence is not null) && jobId is not null)
            {
                if (zeroSentence is not null && (baseSnap is null || snapNow is null || baseSnap.NotTracked || snapNow.NotTracked))
                {
                    reason = Reasons.CountDropped;
                    summary = zeroSentence + " Sonucu doğrulayamam. Bu bir \"geçti\" değildir.";
                }
                else if (baseSnap is null || snapNow is null || baseSnap.NotTracked || snapNow.NotTracked)
                {
                    var tooMany = (snapNow?.TooMany ?? false) || (baseSnap?.TooMany ?? false);
                    verdict = Verdicts.Unchecked; reason = tooMany ? Reasons.NotTracked : Reasons.NoBase;
                    var why = snapNow?.NotTrackedWhy ?? baseSnap?.NotTrackedWhy ?? (baseSnap is null ? "İş başlarken klasörün görüntüsü alınamamış." : "Klasör taranamadı.");
                    summary = tooMany ? $"İzleyemediğim kadar çok dosya var, sonucu doğrulayamam. {why} Bu bir \"geçti\" değildir." : $"Klasörün görüntüsünü alamadım, sonucu doğrulayamam. {why} Bu bir \"geçti\" değildir.";
                }
                else
                {
                    var diff = EvidenceBase.Compare(baseSnap, snapNow, root);
                    added = diff.AddedTests;
                    EvidenceBase.Diff drift;
                    using (var cd = db.Open()) drift = EvidenceBase.DriftVsApproved(cd, projectId, snapNow);
                    var dpc = diff.DepsChanged.Concat(drift.DepsChanged).Distinct().ToList();
                    // e3 (K3): koşucu ayarı ya da sınama komutundaki değişiklik artık hüküm vermez: onaylı koşuda onaylı hâline döner, burada yalnız not ve katma teklifi vardır.
                    // Eski alarm yalnız onaylı koşu YAPILAMADIĞINDA kalır: onaylı taban yoksa değişen ayarı onaylı hâliyle yeniden deneyecek bir yol yoktur.
                    var cfg = new List<string>();
                    if (!canJudgeApproved && cmdOutcomes.Count > 0 && (approvedSnap is null || approvedSnap.NotTracked) && trackedOk)
                    {
                        var clsAt = baseSnap.Suspects.Concat(snapNow.Suspects).GroupBy(x => x.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Cls, StringComparer.Ordinal);
                        cfg = jobChg.All.Where(p => clsAt.TryGetValue(p, out var k) && k != EvidenceBase.ClsTest).Distinct(StringComparer.Ordinal).ToList();
                    }
                    foreach (var fd in diff.FileDiffs.Concat(drift.FileDiffs)) if (!fileDiffs.Any(x => x.Path == fd.Path)) fileDiffs.Add(fd);
                    foreach (var nt in diff.NoteList.Concat(drift.NoteList)) if (!noteList.Contains(nt)) noteList.Add(nt);
                    // Bir testin kullandığı yardımcı dosyalar tam belirlenemediyse (sabit olmayan içe aktarma, çok büyük klasör) kişiye sakin bir not düşülür (K1).
                    if (snapNow.Partial is { Count: > 0 } partialList)
                    {
                        var pn = partialList.Where(x => x != "*").ToList();
                        noteList.Add(pn.Count == 0 ? "Klasör çok büyük, testlerin kullandığı yardımcı dosyaları tam belirleyemedim"
                            : $"Bir testin kullandığı yardımcı dosyaları tam belirleyemedim ({string.Join(", ", pn.Take(2))}{(pn.Count > 2 ? $" ve {pn.Count - 2} tane daha" : "")})");
                    }
                    var onlyDrift = !diff.Any && cfg.Count == 0;
                    if (cfg.Count > 0 || dpc.Count > 0)
                    {
                        verdict = Verdicts.Unchecked;
                        reason = cfg.Count > 0 ? Reasons.DefinitionChanged : Reasons.DepsChanged;
                        changed = cfg.Concat(dpc).Distinct().ToList();
                        string L(List<string> l) => string.Join(", ", l.Take(8)) + (l.Count > 8 ? " ve diğerleri" : "");
                        var parts = new List<string>();
                        if (cfg.Count > 0) parts.Add($"Test ayarı değişti: {L(cfg)}. Onayladığın ayarlarla yeniden deneyemedim.");
                        if (dpc.Count > 0) parts.Add($"Bağımlılık klasörü elle değiştirildi: {L(dpc)}.");
                        summary = onlyDrift
                            ? $"Bu klasörde son onayladığından beri: {string.Join(" ", parts)} Sonucu doğrulayamam, sen kabul edene kadar. Bu bir \"geçti\" değildir."
                            : $"{(zeroSentence is null ? "" : zeroSentence + " ")}Yardımcı işi yaparken: {string.Join(" ", parts)} Sonucu doğrulayamam. Bu bir \"geçti\" değildir.";
                    }
                    else if (canJudgeApproved)
                    {
                        var b = RunB();
                        var aZero = zeroSentence is not null;
                        var anyEdit = jobChg.Edited.Count + jobChg.Removed.Count > 0; // ajan BU işte onaylı bir dosyayı gerçekten değiştirdi mi (K2)
                        if (b.Cat == "unavail")
                        {
                            verdict = Verdicts.Unchecked;
                            if (aZero) { reason = Reasons.CountDropped; summary = $"{zeroSentence} Onayladığın testleri yeniden çalıştıramadım: {b.Why} Sonucu doğrulayamam. Bu bir \"geçti\" değildir."; }
                            else { reason = Reasons.VerdictUnavailable; summary = $"Onayladığın testleri yeniden çalıştıramadım: {b.Why} Sonucu doğrulayamam. Bu bir \"geçti\" değildir."; }
                        }
                        else if (b.Cat == "notests")
                        {
                            string? zs;
                            using (var cz2 = db.Open()) zs = ZeroAfterTests(cz2, projectId, runId, baseline, b.Outcomes);
                            verdict = Verdicts.Unchecked;
                            if (zs is not null || aZero) { reason = Reasons.CountDropped; summary = (zs ?? zeroSentence) + " Sonucu doğrulayamam. Bu bir \"geçti\" değildir."; }
                            else { reason = Reasons.VerdictUnavailable; summary = "Onayladığın testleri yeniden çalıştırırken koşacak test bulamadım. Sonucu doğrulayamam. Bu bir \"geçti\" değildir."; }
                        }
                        else if (b.Cat == "failed")
                        {
                            verdict = Verdicts.Unchecked; reason = Reasons.ApprovedFailed;
                            baselineText = null; // "Başlamadan önce hepsi geçiyordu, şimdi hepsi geçiyor" ajanın bıraktığı klasörü anlatır, karar cümlesiyle çelişir
                            summary = ApprovedFailedSentence(b.Outcomes, anyEdit);
                            AddNamesDiff(fileDiffs, "approved-fail", "Onayladığın testlerden geçmeyenler", FailedNames(b.Outcomes));
                        }
                        else if (aZero)
                        {
                            verdict = Verdicts.Unchecked; reason = Reasons.AgentTestsFail;
                            summary = zeroSentence + " Onayladığın testler yeni kodla geçiyor, ama yardımcının bıraktığı klasörde bu testler silinmiş ya da atlanmış. Kararı sen ver.";
                        }
                        // aksi: onaylı testler de geçti, yeşil devam eder (sayı düşmesi A için anlamsız: onaylı küme yargıç)
                    }
                    else if (zeroSentence is not null)
                    {
                        reason = Reasons.CountDropped;
                        summary = zeroSentence + " Sonucu doğrulayamam. Bu bir \"geçti\" değildir.";
                    }
                    else
                    {
                        using var c3 = db.Open();
                        var (drop, hadBasis) = CountDrop(c3, projectId, runId, baseline, outcomes);
                        if (drop is not null)
                        {
                            verdict = Verdicts.Unchecked; reason = Reasons.CountDropped;
                            summary = drop + " Sonucu doğrulayamam. Bu bir \"geçti\" değildir.";
                        }
                        else if (!hadBasis && outcomes.Sum(o => o.SkippedTests) > 0 && countUnknown == 0)
                        {
                            countUnknown = 2;
                            summary += $" {outcomes.Sum(o => o.SkippedTests)} test atlanmış, önceki durumu bilmiyorum.";
                        }
                    }
                    if (verdict == Verdicts.Passed)
                    {
                        var treeSame = vb is null && baseSnap.TreeId is not null && baseSnap.TreeId == snapNow.TreeId && outsideWrites.Count == 0;
                        // K7: "hiçbir dosyayı değiştirmedi" yalnız gerçekten hiçbir şey değişmediyse. İzlenmeyen klasöre (coverage, dist, .git) yazdıysa bu açıkça söylenir.
                        var ignoredNames = treeSame ? IgnoredChanges(baseSnap, snapNow) : new List<string>();
                        var noChange = treeSame && ignoredNames.Count == 0;
                        var ignoredOnly = treeSame && ignoredNames.Count > 0;
                        // E4: yardımcının "n dosya değiştirdim" beyanı ile klasörde bulunan yan yana söylenir (akış satırı ile hüküm tek cümlede birleşir).
                        int? claimedEdits = null;
                        try { using var cc = db.Open(); claimedEdits = ChangeClaim.Read(cc, jobId.Value); } catch (SqliteException) { }
                        bool? foundChange = baseSnap?.TreeId is not null && snapNow?.TreeId is not null && outsideWrites.Count == 0 ? !treeSame : null;
                        var compare = ChangeClaim.Sentence(claimedEdits, foundChange, ignoredOnly);
                        var expectation = noChange ? JobExpectation(db, jobId.Value) : ChangeExpectation.Kind.ReadOnly;
                        if (noChange && expectation != ChangeExpectation.Kind.ReadOnly)
                        {
                            // C7: iş klasörde değişiklik istiyordu (ya da isteyip istemediği anlaşılamadı) ve klasörde hiçbir şey değişmedi. Kontroller işten önce de geçiyordu:
                            // bu "geçti" değildir. Hüküm "kanıtsız" (unchecked): iş "verildi"de kalır, kullanıcı "iş yapılmamış görünüyor" cümlesini görür.
                            var claimed = (claimedEdits ?? 0) > 0; // "bitti" demesi iddia sayılmaz (her yardımcı bitti der): yalnız "n dosya değiştirdim" beyanı
                            verdict = Verdicts.Unchecked;
                            reason = Reasons.NoWork;
                            summary = ChangeExpectation.NoChangeSentence(expectation, claimed) + " Kontroller geçiyor ama bunlar işten önce de geçiyordu. Bu bir \"geçti\" değildir.";
                        }
                        else if (noChange)
                        {
                            // N6: yalnız okuma / soru türü iş: değişiklik beklenmiyordu, klasörün aynı kalması doğru.
                            reason = Reasons.NoChange;
                            summary = "Yardımcı hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu. Bu iş değişiklik istemiyordu. " + (compare is null ? "" : compare + " ") + summary;
                        }
                        else if (ignoredOnly)
                        {
                            reason = Reasons.IgnoredOnly;
                            summary = $"Yardımcı yalnız izlenmeyen klasörlere yazdı ({string.Join(", ", ignoredNames)}). Kaynakta değişiklik yok. " + (compare is null ? "" : compare + " ") + summary;
                        }
                        else
                        {
                            if (compare is not null) summary += " " + compare;
                            if (vb is { Cat: "passed" } && (jobChg.Edited.Count + jobChg.Removed.Count) > 0)
                                summary += " Yardımcı onayladığın testlere de dokundu. Sonuç senin onayladığın testlerle alındı.";
                        }
                        // Doğru notla geçer: yalnız bilgi sınıfındaki değişiklikler cümleyle söylenir.
                        if (noteList.Count > 0) summary += " Not: " + string.Join(", ", noteList.Take(6)) + (noteList.Count > 6 ? $". Ve {noteList.Count - 6} not daha" : "") + ".";
                        // Onaylı taban ilerler ama ONAYLI SINAMA KÜMESİ yerinde kalır: ajanın testleri kişi onaylayana kadar kümeye girmez.
                        if (!noChange && !ignoredOnly)
                        {
                            try
                            {
                                using var c4 = db.Open();
                                var prev = approvedSnap ?? EvidenceBase.LoadApproved(c4, projectId);
                                var auto = SozSettings.Load(c4).Bool("setting:proof.auto-approve-new-tests");
                                // Katılmamış ajan testleri için dürüstlük satırı (e3, madde 8): kartta sonuca etkisi olmadığı söylenir. Kendiliğinden katma açıksa bu satır yanlış olurdu.
                                var addedTestCount = jobChg.Added.Count(EvidenceBase.IsTestFile);
                                if (addedTestCount > 0 && !auto) summary += $" Yardımcının eklediği {addedTestCount} test henüz onaylı değil, sonuca etkisi yok.";
                                EvidenceBase.SaveApproved(c4, projectId, prev is null || prev.NotTracked ? snapNow! : EvidenceBase.AdvanceKeepingMembers(prev, snapNow!));
                                BuildOffer(c4, projectId, jobId.Value, WithProd(baseSnap!), WithProd(snapNow!), prev, auto);
                            }
                            catch (SqliteException) { }
                        }
                    }
                }
            }
            if (verdict == Verdicts.Unchecked && Reasons.IsTampering(reason) && trackedOk)
            {
                // Karar kartı: kartın dayandığı görüntünün kimliği (kabul bunu taşır, E). Ölçütler koştuktan SONRA yeniden taranır: sınamanın kendi ürettiği dosyalar kimliği oynatmasın.
                try
                {
                    using var cs = db.Open();
                    snapshotId = EvidenceBase.SnapshotId(EvidenceBase.ScanProject(cs, projectId, jobId, root!));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException) { snapshotId = null; }
            }
            if (verdict == Verdicts.Passed && countUnknown == 1) summary += " Test sayısını okuyamadım, yalnız komutun hatasız bittiğini gördüm.";
        }

        if (jobId is not null && jobProject == projectId)
        {
            foreach (var w in OutsideNotes(outsideWrites, jobAgent)) if (!noteList.Contains(w)) noteList.Add(w);
            if (verdict == Verdicts.Passed && outsideWrites.Count > 0 && !summary.Contains(OutsidePrefix, StringComparison.Ordinal))
                summary += " Dikkat: " + noteList.First(x => x.StartsWith(OutsidePrefix, StringComparison.Ordinal)) + ".";
        }

        using (var c = db.Open())
        {
            c.Exec("""
                UPDATE proof_runs SET finished_at=@n, verdict=@v, summary=@s, reason_code=@rc, reason_text=@rt, changed_files=@cf, count_unknown=@cu, added_tests=@at, baseline_text=@bt, snapshot_id=@si, diff_json=@dj, notes_json=@nj WHERE id=@id
                """, null, ("@n", Time.NowIso()), ("@v", verdict), ("@s", summary), ("@rc", reason),
                ("@rt", reason is null ? null : summary), ("@cf", changed.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(changed)),
                ("@cu", countUnknown), ("@at", added.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(added)), ("@bt", baselineText), ("@id", runId),
                ("@si", snapshotId), ("@dj", fileDiffs.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(fileDiffs, DiffJson)), ("@nj", noteList.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(noteList)));
            Live.Emit(c, Live.Proof, projectId, new { runId, projectId, jobId, phase = "finished", verdict, reason });
            return Get(c, runId)!;
        }
    }

    /// <summary>Test dosyası alarmının cümlesi: her dosya için neyin değiştiği (TestFileDiff) yazılır; ayrıntı yoksa dosya adıyla yetinilir.</summary>
    private static string TestSentence(List<string> paths, List<EvidenceBase.FileDiff> diffs)
    {
        var sentences = new List<string>();
        foreach (var path in paths.Take(3))
        {
            var fd = diffs.FirstOrDefault(x => x.Path == path && x.Kind == "test");
            sentences.Add(fd?.Sentence ?? $"Test dosyası değişti: {path}.");
        }
        if (paths.Count > 3) sentences.Add($"Ve {paths.Count - 3} test dosyası daha.");
        return string.Join(" ", sentences);
    }

    /// <summary>
    /// Önce test koşuyordu, şimdi hiçbiri koşmadı (hiç test yok ya da hepsi atlandı) mu? Önceki sayı: başlangıç ölçümünden, yoksa aynı komutun önceki güvenilir sonucundan.
    /// Dönüş: "Önce 3 test koşuyordu, şimdi hiçbiri koşmadı." gibi cümle ("Sonucu doğrulayamam" eki çağıranda), önceden de test yoksa null (o iş zaten kanıtsızdı).
    /// </summary>
    private static string? ZeroAfterTests(SqliteConnection c, long projectId, long runId, BaselineText.Baseline? baseline, IReadOnlyList<CheckOutcome> outcomes)
    {
        foreach (var o in outcomes.Where(x => x.Status == ResultStatus.Unchecked && x.ReasonCode == Reasons.NoTests))
        {
            if (o.CmdKey is null) continue;
            long? before = null;
            var b = baseline?.Checks.FirstOrDefault(x => x.Key == o.CmdKey && x.Total is not null);
            if (b is not null) before = b.Total > 0 ? b.Total : null;
            else
            {
                var prev = c.Query("""
                    SELECT x.tests_total FROM proof_results x JOIN proof_runs r ON r.id=x.run_id
                    WHERE r.project_id=@p AND x.cmd_key=@k AND x.tests_total IS NOT NULL AND x.tests_total>0 AND r.id<@cur AND x.status IN ('passed','failed')
                      AND (r.reason_code IS NULL OR r.reason_code IN ('no_change','ignored_only') OR COALESCE(r.accepted,0)=1) ORDER BY x.id DESC LIMIT 1
                    """, r => r.GetInt64(0), null, ("@p", projectId), ("@k", o.CmdKey), ("@cur", runId)).FirstOrDefault();
                if (prev > 0) before = prev;
            }
            if (before is null) continue;
            var allSkipped = o.TestsTotal is > 0 && o.TestsPassed is <= 0;
            return $"Önce {before} test koşuyordu, şimdi {(allSkipped ? "hepsi atlandı" : "hiçbiri koşmadı")}.";
        }
        return null;
    }

    /// <summary>İşin metninden ve türünden: klasörde değişiklik bekleniyor mu (C7). Okunamazsa Unknown (yanlış "geçti" vermemek için).</summary>
    internal static ChangeExpectation.Kind JobExpectation(SozDb db, long jobId)
    {
        try
        {
            using var c = db.Open();
            var row = c.Query("SELECT COALESCE(raw_text,''), COALESCE(NULLIF(prompt_text,''),''), COALESCE(kind,'kod') FROM jobs WHERE id=@j", r => (raw: r.Str(0) ?? "", prompt: r.Str(1) ?? "", kind: r.Str(2)), null, ("@j", jobId)).FirstOrDefault();
            if (row == default) return ChangeExpectation.Kind.Unknown;
            // prompt_text Söz'ün eklediği kural ve "ne olursa bitti" metinlerini de taşır; ham metin kişinin kendi sözüdür, önce o.
            return ChangeExpectation.Classify(string.IsNullOrWhiteSpace(row.raw) ? row.prompt : row.raw, row.kind);
        }
        catch (SqliteException) { return ChangeExpectation.Kind.Unknown; }
    }

    /// <summary>Ajanın proje klasörü dışına yazdığı dosyaların notunun başı (kanca kaydı: araç öncesi kancası, yalnız Write/Edit türü araçlar).</summary>
    public const string OutsidePrefix = "Yardımcı bu klasörün dışında şu dosyalara yazdı";

    /// <summary>İşin kayıtlı klasör dışı yazmaları (GuardHook yazar): benzersiz yollar, en çok 20.</summary>
    internal static List<string> OutsideWrites(SqliteConnection c, long jobId)
    {
        var list = new List<string>();
        foreach (var d in c.Query("SELECT detail FROM events WHERE kind=@k AND json_extract(detail,'$.jobId')=@j ORDER BY id", r => r.Str(0), null, ("@k", Guard.GuardHook.OutsideWriteEvent), ("@j", jobId)))
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(d ?? "") is System.Text.Json.Nodes.JsonObject o && (o["path"] as System.Text.Json.Nodes.JsonValue)?.GetValue<string>() is { } path && !list.Contains(path)) list.Add(path);
            }
            catch (System.Text.Json.JsonException) { }
            if (list.Count >= 20) break;
        }
        return list;
    }

    /// <summary>Kart notları: klasör dışına yazma (kayıt varsa) ve araç kancasızsa bunu görmediğimizin dürüst notu.</summary>
    internal static List<string> OutsideNotes(IReadOnlyList<string> outside, string? agentId)
    {
        var res = new List<string>();
        if (outside.Count > 0) res.Add($"{OutsidePrefix}: {string.Join(", ", outside.Take(5))}{(outside.Count > 5 ? $" ve {outside.Count - 5} dosya daha" : "")}");
        try
        {
            var tier = Soz.Core.Agents.AgentRegistry.Get(agentId).Capabilities.BeforeTool;
            if (tier != Soz.Core.Agents.GuardTier.NativeHook)
                res.Add(tier == Soz.Core.Agents.GuardTier.OsSandbox
                    ? "Bu araçta yardımcının klasör dışına yazıp yazmadığını Söz görmüyor, aracın kendi sınırı var."
                    : "Bu araçta yardımcının klasör dışına yazıp yazmadığını göremiyorum.");
        }
        catch (SozException) { /* bilinmeyen araç: not eklenmez */ }
        return res;
    }

    /// <summary>
    /// Test sayısı düştü mü, atlanan arttı mı? Karşılaştırma tabanı: Söz koşusunda BAŞLANGIÇ ÖLÇÜMÜ; yoksa aynı projede aynı ölçütün önceki güvenilir sonucu (terminal yolu).
    /// Dönüş: (sade cümle ya da null, bir karşılaştırma tabanı bulundu mu).
    /// </summary>
    private static (string? drop, bool hadBasis) CountDrop(SqliteConnection c, long projectId, long runId, BaselineText.Baseline? baseline, IReadOnlyList<CheckOutcome> outcomes)
    {
        var had = false;
        foreach (var o in outcomes)
        {
            if (o.TestsTotal is null || o.CmdKey is null) continue;
            var b = baseline?.Checks.FirstOrDefault(x => x.Key == o.CmdKey && x.Total is not null);
            if (b is not null)
            {
                had = true;
                if (o.TestsPassed < b.Passed) return ($"Başlamadan önce {b.Passed} test geçiyordu, şimdi {o.TestsPassed} geçiyor. Geçen test sayısı düştü.", true);
                if (o.TestsTotal < b.Total) return ($"Başlamadan önce {b.Total} test vardı, şimdi {o.TestsTotal} var. Test sayısı düştü.", true);
                if (o.SkippedTests > b.Skipped) return ($"Atlanan test sayısı arttı ({b.Skipped} iken {o.SkippedTests} oldu).", true);
                continue;
            }
            var prev = c.Query("""
                SELECT x.tests_total, COALESCE(x.tests_skipped,0) FROM proof_results x JOIN proof_runs r ON r.id=x.run_id
                WHERE r.project_id=@p AND x.cmd_key=@k AND x.tests_total IS NOT NULL AND r.id<@cur AND x.status IN ('passed','failed')
                  AND (r.reason_code IS NULL OR r.reason_code IN ('no_change','ignored_only') OR COALESCE(r.accepted,0)=1) ORDER BY x.id DESC LIMIT 1
                """, r => (total: r.GetInt64(0), skipped: r.GetInt64(1)), null, ("@p", projectId), ("@k", o.CmdKey), ("@cur", runId)).FirstOrDefault();
            if (prev == default) continue;
            had = true;
            if (o.TestsTotal < prev.total) return ($"Bu kontrolün önceki sonucunda {prev.total} test okunmuştu, şimdi {o.TestsTotal} test var. Test sayısı düştü.", true);
            if (o.SkippedTests > prev.skipped) return ($"Atlanan test sayısı arttı ({prev.skipped} iken {o.SkippedTests} oldu).", true);
        }
        return (null, had);
    }

    /// <summary>
    /// Başlangıç ölçümü: ajan başlamadan önce işin açık komut/dosya/adres/tablo ölçütlerini bir kez koşar (ekran görüntüsü, insan listesi ve fark özeti ölçütleri atlanır), sayıları döndürür.
    /// Deneme çalışması satırı AÇMAZ (zaman çizelgesinde sahte hüküm görünmesin). Açık ölçüt yoksa null.
    /// </summary>
    public static BaselineText.Baseline? MeasureBaseline(SozDb db, long jobId, int budgetSeconds, Func<bool>? cancelled = null, Func<string?>? edgeLocator = null)
    {
        long projectId; string? root; List<CheckDto> checks;
        using (var c = db.Open())
        {
            var pid = c.Scalar("SELECT project_id FROM jobs WHERE id=@j", null, ("@j", jobId)) as long?;
            if (pid is null) return null;
            projectId = pid.Value;
            root = c.Scalar("SELECT path FROM projects WHERE id=@p", null, ("@p", projectId)) as string;
            checks = CheckStore.EnabledFor(c, projectId, jobId).Where(x => MeasurerRegistry.Find(x.Kind)?.Baseline == BaselineMode.Measure).ToList();
        }
        if (checks.Count == 0 || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var dir = Path.Combine(db.DataDir, "kanit", "baslangic-" + jobId);
        var runner = new ProofRunner(root, dir, edgeLocator, db.DataDir, jobId, null);
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, budgetSeconds));
        var outs = new List<CheckOutcome>();
        foreach (var ch in checks)
        {
            if (cancelled?.Invoke() == true) break;
            var left = deadline - DateTime.UtcNow;
            if (left < TimeSpan.FromSeconds(1)) { outs.Add(new CheckOutcome(ch.Id, ch.Label, ResultStatus.Skipped, null, "bütçe bitti", null, 0)); continue; }
            var t = TimeSpan.FromSeconds(Math.Max(1, ch.TimeoutS));
            outs.Add(runner.Run(new CheckToRun(ch.Id, ch.Kind, ch.Label, ch.Spec, ch.TimeoutS), t < left ? t : left));
        }
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
        var (verdict, _) = Judge(outs);
        var list = outs.Select(o => new BaselineText.Check(o.CmdKey ?? ("#" + o.CheckId), o.Label, o.Status, o.TestsTotal, o.TestsPassed, o.SkippedTests)).ToList();
        return new BaselineText.Baseline(Time.NowIso(), verdict, list);
    }


    /// <summary>
    /// Hüküm: hepsi geçtiyse passed; biri kaldıysa failed; hata/atlandı error; doğrulanamayan (hiç test koşmadı, testler atlandı) unchecked.
    /// Asla tahminle passed yok. Atlanan test sayısı >0 ise özete "N test atlandı" notu düşülür.
    /// </summary>
    public static (string verdict, string summary) Judge(IReadOnlyList<CheckOutcome> o)
    {
        if (o.Count == 0) return (Verdicts.Unchecked, "Bu iş için denenecek açık bir kontrol yok. Bu bir \"geçti\" değildir.");
        var failed = o.Where(x => x.Status == ResultStatus.Failed).ToList();
        var unsure = o.Where(x => x.Status is ResultStatus.Error or ResultStatus.Skipped).ToList();
        var unverified = o.Where(x => x.Status == ResultStatus.Unchecked).ToList();
        if (failed.Count > 0) return (Verdicts.Failed, $"{o.Count} kontrolden {failed.Count} tanesi geçmedi: {string.Join(", ", failed.Select(x => x.Label))}");
        if (unsure.Count > 0) return (Verdicts.Error, $"{o.Count} kontrolden {unsure.Count} tanesinde emin olunamadı: {string.Join(", ", unsure.Select(x => x.Label))}");
        if (unverified.Count > 0)
            return (Verdicts.Unchecked, $"{o.Count} kontrolden {unverified.Count} tanesi doğrulanamadı: {string.Join(", ", unverified.Select(x => x.Note is { Length: > 0 } n ? $"{x.Label} ({n})" : x.Label))}. Bu bir \"geçti\" değildir.");
        var waiting = o.Where(x => x.Status == ResultStatus.AwaitingHuman).ToList();
        if (waiting.Count > 0)
            return (Verdicts.Unchecked, $"{o.Count} kontrolden {waiting.Count} tanesi senin onayını bekliyor: {string.Join(", ", waiting.Select(x => x.Label))}. Bu bir \"geçti\" değildir.");
        if (o.All(x => x.Status == ResultStatus.Passed))
        {
            var skipped = o.Sum(x => x.SkippedTests);
            var note = skipped > 0 ? $" {skipped} test atlandı." : "";
            return (Verdicts.Passed, (o.Count == 1 ? "1 kontrol geçti." : $"{o.Count} kontrolün hepsi geçti.") + note);
        }
        return (Verdicts.Error, "Sonuç belirlenemedi.");
    }

    // ---------- sorgular ----------
    private const string RunSelect = """
        SELECT r.id, r.project_id, p.name, r.job_id, j.title, r.session_id, r.trigger, r.attempt, r.started_at, r.finished_at, r.verdict, r.summary,
               r.claims_done, r.shows_evidence,
               (SELECT COUNT(*) FROM proof_results x WHERE x.run_id=r.id AND x.status='passed'),
               (SELECT COUNT(*) FROM proof_results x WHERE x.run_id=r.id AND x.status='failed'),
               (SELECT COUNT(*) FROM proof_results x WHERE x.run_id=r.id AND x.status IN ('skipped','unchecked')),
               (SELECT COUNT(*) FROM proof_results x WHERE x.run_id=r.id AND x.status='error'),
               r.reason_code, r.reason_text, r.changed_files, r.count_unknown, r.added_tests, r.accepted, r.baseline_text, r.snapshot_id, r.diff_json, r.notes_json, r.uid
        FROM proof_runs r LEFT JOIN projects p ON p.id=r.project_id LEFT JOIN jobs j ON j.id=r.job_id
        """;

    /// <summary>Hükmün sade adı. "unchecked" nedene göre: ölçüt yok / hiç test koşmadı / test tanımı değişti / test dosyası değişti / test sayısı düştü (K-5).</summary>
    public static string VerdictWord(string? v, string? reason = null, int countUnknown = 0) => v switch
    {
        Verdicts.Passed => reason == Reasons.NoChange ? "Yardımcı hiçbir dosyayı değiştirmedi" : reason == Reasons.IgnoredOnly ? "Yardımcı yalnız izlenmeyen klasörlere yazdı" : countUnknown == 1 ? "Hatasız bitti, test sayısını okuyamadım" : countUnknown == 2 ? "Geçti, bazı testler atlanmış" : "geçti",
        Verdicts.Failed => "olmadı",
        Verdicts.Unchecked => reason switch
        {
            Reasons.NoTests => "hiç test koşmadı",
            Reasons.DefinitionChanged => "sınama komutu ya da ayarı değişti",
            Reasons.TestsChanged => "test dosyası değişti",
            Reasons.CountDropped => "test sayısı düştü",
            Reasons.ChecksChanged => "kontrol değişti",
            Reasons.NotTracked => "izleyemediğim kadar çok dosya var",
            Reasons.NoBase => "başlangıç görüntüsü yok",
            Reasons.DepsChanged => "bağımlılık klasörü elle değiştirildi",
            Reasons.AwaitingHuman => "senin onayını bekliyor",
            Reasons.ApprovedFailed => "Onayladığın testler geçmiyor",
            Reasons.AgentTestsFail => "Yardımcının yeni testleri geçmiyor",
            Reasons.VerdictUnavailable => "onayladığın testler yeniden çalıştırılamadı",
            Reasons.NothingMeasured => "ölçülecek bir şey bulunamadı",
            Reasons.NoWork => "iş yapılmamış görünüyor",
            _ => "Kontrol yok",
        },
        Verdicts.Error => reason == Reasons.Interrupted ? "yarıda kesildi" : "emin değil",
        _ => "sürüyor",
    };

    internal static readonly System.Text.Json.JsonSerializerOptions DiffJson = new(System.Text.Json.JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static List<EvidenceBase.FileDiff>? DiffList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<EvidenceBase.FileDiff>>(json, DiffJson); } catch (System.Text.Json.JsonException) { return null; }
    }

    private static List<string>? JsonList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json); } catch (System.Text.Json.JsonException) { return null; }
    }

    private static ProofRunDto MapRun(SqliteDataReader r)
    {
        ClaimDto? claim = null;
        var reason = r.Str(18);
        if (!r.IsDBNull(12))
        {
            var done = r.Long(12) == 1;
            var ev = r.Long(13) == 1;
            claim = new ClaimDto(done, ev, $"{(done ? "bitti dedi" : "bitti demedi")} / deneme sonucu: {VerdictWord(r.Str(10), reason)}");
        }
        return new ProofRunDto(r.GetInt64(0), r.Long(1), r.Str(2), r.LongN(3), r.Str(4), r.LongN(5), r.Str(6) ?? "manual", (int)r.Long(7), r.Str(8), r.Str(9),
            r.Str(10), r.Str(11), new ProofCounts((int)r.Long(14), (int)r.Long(15), (int)r.Long(16), (int)r.Long(17)), claim, null,
            reason, r.Str(19), JsonList(r.Str(20)), r.Long(21) > 0, JsonList(r.Str(22)), r.Str(10) is null ? null : VerdictWord(r.Str(10), reason, (int)r.Long(21)), r.Long(23) == 1, r.Str(24),
            r.Str(25), DiffList(r.Str(26)), JsonList(r.Str(27)), r.Str(28));
    }

    public static List<ProofRunDto> List(SqliteConnection c, long? projectId, long? jobId, int limit = 20)
        => c.Query(RunSelect + " WHERE (@p IS NULL OR r.project_id=@p) AND (@j IS NULL OR r.job_id=@j) ORDER BY r.id DESC LIMIT @l", MapRun, null,
            ("@p", projectId), ("@j", jobId), ("@l", Math.Clamp(limit, 1, 500)));

    public static ProofRunDto? Get(SqliteConnection c, long id)
    {
        var run = c.Query(RunSelect + " WHERE r.id=@id", MapRun, null, ("@id", id)).FirstOrDefault();
        if (run is null) return null;
        var results = c.Query("""
            SELECT x.id, x.check_id, x.label, k.kind, x.status, x.exit_code, x.output_tail, x.artifact_path, x.duration_ms, x.tests_total, x.tests_skipped, x.tests_passed
            FROM proof_results x LEFT JOIN checks k ON k.id=x.check_id WHERE x.run_id=@r ORDER BY x.id
            """, r => new ProofResultDto(r.GetInt64(0), r.Long(1), r.Str(2) ?? "", r.Str(3) ?? "?", r.Str(4) ?? ResultStatus.Error, r.IsDBNull(5) ? null : (int)r.GetInt64(5),
            r.Str(6), r.Str(7) is { } a ? $"/api/proof/artifact/{id}/{a}" : null, r.Long(8), ArtifactKindOf(r.Str(7)), r.LongN(9) is { } tt ? (int)tt : null, r.LongN(10) is { } ts ? (int)ts : null, r.LongN(11) is { } tp ? (int)tp : null,
            Confidence.Of(r.Str(3) ?? "?", r.Str(4) ?? ResultStatus.Error), Confidence.Label(Confidence.Of(r.Str(3) ?? "?", r.Str(4) ?? ResultStatus.Error)), Confidence.SourceOf(r.Str(3) ?? "?", r.Str(4) ?? ResultStatus.Error)), null, ("@r", id));
        return run with { Results = results, Confidence = ConfidenceOf(results) };
    }

    /// <summary>Koşunun güven özeti: geçen sonuçların en zayıf düzeyi ve düzey sayıları (ölçülen gerçek ile insan kabulü ayrı sayılır).</summary>
    public static ConfidenceDto ConfidenceOf(IReadOnlyList<ProofResultDto> results)
    {
        var level = Confidence.Weakest(results.Select(r => (r.Kind, r.Status)));
        int N(string l) => results.Count(r => r.Confidence == l);
        return new ConfidenceDto(level, Confidence.Label(level), N(Confidence.A), N(Confidence.B), N(Confidence.C), N(Confidence.D),
            results.Any(r => r.Source == Confidence.HumanAccepted));
    }

    /// <summary>İnsan onay listesi işaretlerini ölçücüye verir (veritabanından, işe özel).</summary>
    internal static Func<CheckToRun, IReadOnlySet<int>> TicksFor(SozDb db, long? jobId) => check =>
    {
        using var c = db.Open();
        var dto = CheckStore.Get(c, check.Id);
        return dto is null ? new HashSet<int>() : HumanChecklistStore.Ticked(c, dto, jobId);
    };

    /// <summary>Kanıt dosyasının türü: ekran görüntüsü "image", komut çıktısı "text" (tam çıktı), tanınmayan "file". Dosya adı yoksa null.</summary>
    public static string? ArtifactKindOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Path.GetExtension(name).ToLowerInvariant() switch { ".png" or ".jpg" or ".jpeg" or ".webp" => "image", ".txt" or ".log" => "text", _ => "file" };
    }

    /// <summary>Kanıt dosyası yolu; ad yalnız güvenli karakterler, klasör dışına çıkış yok.</summary>
    public static string? ArtifactPath(SozDb db, long runId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]*$") || name.Contains("..")) return null;
        var p = Path.Combine(EvidenceDir(db, runId), name);
        return File.Exists(p) ? p : null;
    }
}
