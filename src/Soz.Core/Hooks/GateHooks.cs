using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Judge;
using Soz.Core.Run;
using Soz.Core.Proof;
using Soz.Core.Queue;
using Soz.Core.Readers;
using Soz.Core.Sessions;
using Soz.Core.Text;

namespace Soz.Core.Hooks;

/// <summary>
/// Stop ve Notification kancaları (A) ve UserPromptSubmit'in iş bağlama eki.
/// Biçim (code.claude.com/docs/en/hooks, 2026-11-10 okundu):
///  Stop girdi: session_id, transcript_path, cwd, hook_event_name, stop_hook_active, last_assistant_message.
///  Stop çıktı: {"decision":"block","reason":"…"} durmayı engeller ve reason ajana iletilir; alan yoksa durur. Matcher desteklemez.
///  Notification girdi: message, title?, notification_type (permission_prompt, idle_prompt, …). Çıktısı yok sayılır.
/// Bu sınıfın bütün hataları HookHandler.Run'da yutulur (çıkış 0, boş çıktı): ajan asla kilitlenmez.
/// </summary>
public static class GateHooks
{
    public const int MaxReasonChars = 3500;
    private const int PerCheckTail = 700;

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static (long sid, long? pid) Session(SqliteConnection c, JsonObject input, string? dir)
    {
        var ext = Str(input, "session_id");
        if (string.IsNullOrWhiteSpace(ext)) throw new InvalidDataException("session_id yok");
        var (projectId, _) = HookHandler.FindProject(c, dir);
        var (sid, spid) = IngestStore.EnsureSession(c, null, Soz.Core.Agents.AgentRegistry.HookSessionTool, ext!, dir, Str(input, "transcript_path"), SessionKinds.Human);
        return (sid, projectId ?? spid);
    }

    // ---------------- Stop ----------------
    public static JsonObject? Stop(SqliteConnection c, SozDb db, JsonObject input, Func<string, string?> env)
    {
        var dir = env("CLAUDE_PROJECT_DIR") ?? Str(input, "cwd");
        var (sid, pid) = Session(c, input, dir);
        var lastMsg = Str(input, "last_assistant_message");
        var now = Time.NowIso();
        c.Exec("""
            UPDATE sessions SET last_event_kind='assistant', last_event_ts=@n, turn_ended=1, needs_user_since=NULL, needs_user_kind=NULL,
              last_assistant_text=COALESCE(@m, last_assistant_text), last_assistant_ts=CASE WHEN @m IS NULL THEN last_assistant_ts ELSE @n END,
              last_at=CASE WHEN last_at IS NULL OR last_at < @n THEN @n ELSE last_at END, started_at=COALESCE(started_at,@n)
            WHERE id=@id
            """, null, ("@n", now), ("@m", lastMsg), ("@id", sid));

        // (1) Kuyruk: kullanıcının bekleyen mesajı varsa önce o iletilir.
        // Teslim işareti en sona konur (çıktı hazırlandıktan sonra); yan çağrılar (olay günlüğü) patlasa da mesaj kaybolmaz:
        // ya ajana iletilir ya da bekliyor kalır, "iletildi ama ulaşmadı" olmaz.
        var pending = QueueStore.Pending(c, sid, pid);
        if (pending.Count > 0)
        {
            var block = Block(FormatQueue(pending));
            try { Events.Log(c, "queue_delivered", pid, $"{{\"count\":{pending.Count}}}"); } catch (Exception) { /* yan çağrı mesajı düşürmez */ }
            try { QueueStore.MarkDelivered(c, sid, pending); } catch (Exception) { /* işaret konamazsa mesaj bekliyor kalır; ajan yine de alır, kaybolmaz */ }
            return block;
        }

        // (2) Kanıt Kapısı.
        if (pid is null) return null;
        var gateEnabled = c.ScalarLong("SELECT COALESCE(gate_enabled,0) FROM projects WHERE id=@p", null, ("@p", pid)) == 1;
        var job = JobStore.ActiveFor(c, sid, pid);
        if (job is { } jobNow)
        {
            // Söz yürütücüsünde bekçi "sor" dediyse iş onay bekliyor: kapı şimdi çalışmaz, onaydan sonraki duruşta çalışır.
            try { if (RunStore.PendingApproval(c, jobNow.id) is not null) return null; } catch (SqliteException) { }
            // Hakem (1): ajanın son sözünü sınıfla (soru soruyor / bitti diyor / kanıt gösteriyor / takıldı); iş listesindeki durum.
            var agentState = AgentWord.ClassifyAndStore(db, jobNow.id, pid, lastMsg, "stop_hook");
            // Ö-3: ajan kullanıcıya soru soruyorsa kapı koşmaz (zorla sürdürmek ajanı kör bir "bitirdim" döngüsüne iter); durmasına izin verilir, iş cevap bekler.
            if (IsQuestion(agentState, lastMsg))
            {
                if (jobNow.state != JobStates.Passed) JobStore.SetAwaitingAnswer(c, jobNow.id, lastMsg, pid);
                return null;
            }
        }
        else if ((gateEnabled || JobStore.NightOpen(c, pid)) && !string.IsNullOrWhiteSpace(lastMsg))
        {
            // İşsiz kapı: soru soran ajanı zorla sürdürme.
            try { if (IsQuestion(AgentWord.Classify(JudgeFactory.Create(db, pid, null, "stop_hook"), lastMsg), lastMsg)) return null; }
            catch (Exception) { /* sınıflama yapılamazsa kapı eskisi gibi çalışır */ }
        }
        if (!gateEnabled && job is null && !JobStore.NightOpen(c, pid)) return null;

        var st = SozSettings.Load(c);
        var max = WorkService.GateMaxAttempts(st);
        var budget = WorkService.GateBudgetSeconds(st);
        // stop_hook_active=false: yeni tur başlangıcı (sayaç bu duruş zinciri için sıfırdan); true: zincirin devamı.
        // UserPromptSubmit kancası kurulu olmasa da sayaç böylece doğru çalışır. Alan yoksa eski sayım (geriye uyum).
        bool? chainActive = input["stop_hook_active"] is JsonValue av && av.TryGetValue<bool>(out var ab) ? ab : null;
        int attempt;
        if (chainActive == false) attempt = 1;
        else if (chainActive == true)
        {
            // Zincirin önceki duruşunun turu + 1. Önceki tur üst sınıra ulaşmışsa o zincir bitmiştir (bırakılmıştı): yeni zincir.
            var last = job is { } jc
                ? c.Query("SELECT attempt FROM proof_runs WHERE job_id=@j AND trigger='stop_hook' ORDER BY id DESC LIMIT 1", r => (int)r.GetInt64(0), null, ("@j", jc.id)).FirstOrDefault()
                : c.Query("SELECT attempt FROM proof_runs WHERE session_id=@s AND job_id IS NULL AND trigger='stop_hook' ORDER BY id DESC LIMIT 1", r => (int)r.GetInt64(0), null, ("@s", sid)).FirstOrDefault();
            attempt = last <= 0 || last >= max ? 1 : last + 1;
        }
        else if (job is { } j)
            attempt = (int)c.ScalarLong("SELECT COUNT(*) FROM proof_runs WHERE job_id=@j AND trigger='stop_hook'", null, ("@j", j.id)) + 1;
        else
        {
            // İşsiz kapı: bu oturumda kullanıcının son isteminden sonraki kapı turları.
            var lastUser = c.Scalar("SELECT last_user_ts FROM sessions WHERE id=@s", null, ("@s", sid)) as string ?? "";
            attempt = (int)c.ScalarLong("SELECT COUNT(*) FROM proof_runs WHERE session_id=@s AND job_id IS NULL AND trigger='stop_hook' AND started_at>=@u",
                null, ("@s", sid), ("@u", lastUser)) + 1;
        }
        var (_, claimsDone, evidence) = SessionStateResolver.Flags(lastMsg);
        var runId = ProofStore.Begin(c, new ProofStore.RunRequest(pid.Value, job?.id, sid, "stop_hook", attempt, lastMsg is null ? null : claimsDone, lastMsg is null ? null : evidence));
        var run = ProofStore.Complete(db, runId, budget);
        if (run.Verdict == Verdicts.Unchecked && !string.IsNullOrWhiteSpace(lastMsg))
        {
            // Hakem (2): ölçütsüz işte "ajan şunu gösterdi" özeti. Hüküm yine unchecked; ajanın sözü doğrulanmış sayılmaz.
            try
            {
                var shown = AgentWord.EvidenceExcerpt(JudgeFactory.Create(db, pid, job?.id, "stop_hook"), lastMsg);
                if (shown is not null)
                {
                    var ns = run.Summary + " Yardımcı şunu gösterdi (Söz doğrulamadı): " + SecretFilter.MaskSecrets(shown);
                    c.Exec("UPDATE proof_runs SET summary=@s WHERE id=@i", null, ("@s", ns), ("@i", run.Id));
                    run = run with { Summary = ns };
                }
            }
            catch (Exception) { /* özet eklenemezse hüküm yine unchecked */ }
        }

        var wasPassed = job is { state: JobStates.Passed };
        if (wasPassed && run.Verdict != Verdicts.Passed)
        {
            // K-2: geçtikten sonra aynı oturumda bozulma. Nihai hüküm SON kapı sonucudur; kartta nedeni görünür.
            var note = "Önceki duruşta geçmişti, sonra bozuldu. ";
            c.Exec("UPDATE proof_runs SET summary=@s WHERE id=@i", null, ("@s", note + run.Summary), ("@i", run.Id));
            run = run with { Summary = note + run.Summary };
        }

        // K-1: ajan test tanımını ya da test dosyasını değiştirdiği için kanıt sayılamadı. Bir kez geri yolla; ısrar ederse karar kullanıcıda.
        if (job is { } jt && run.Verdict == Verdicts.Unchecked && ProofStore.Reasons.IsTampering(run.ReasonCode))
        {
            var prevReason = c.Scalar("SELECT reason_code FROM proof_runs WHERE job_id=@j AND trigger='stop_hook' AND id<@i ORDER BY id DESC LIMIT 1", null, ("@j", jt.id), ("@i", run.Id)) as string;
            if (attempt > 1 && ProofStore.Reasons.IsTampering(prevReason))
            {
                JobStore.SetState(c, jt.id, JobStates.AwaitingDecision, pid);
                return new JsonObject { ["systemMessage"] = $"Söz kanıt: {(run.VerdictLabel ?? ProofStore.VerdictWord(run.Verdict, run.ReasonCode))}. {run.ReasonText ?? run.Summary} Karar sende." };
            }
            JobStore.SetState(c, jt.id, JobStates.Running, pid);
            return Block(TamperReason(run));
        }

        if (run.Verdict == Verdicts.Failed && attempt < max)
        {
            if (job is { } jb) JobStore.SetState(c, jb.id, JobStates.Running, pid);
            return Block(FailReason(run, attempt, max, r => FullOutput(db, run.Id, r)));
        }
        if (job is { } jf)
        {
            if (run.Verdict == Verdicts.Passed) { JobStore.SetState(c, jf.id, JobStates.Passed, pid); c.Exec("UPDATE jobs SET accepted_by_user=0 WHERE id=@j", null, ("@j", jf.id)); }
            else if (run.Verdict == Verdicts.Failed) JobStore.SetState(c, jf.id, JobStates.Failed, pid);
            else if (wasPassed) JobStore.SetState(c, jf.id, JobStates.Given, pid); // geçmişti, sonra belirsizleşti: artık "geçti" değil
            // unchecked / error: iş "geçti" sayılmaz, açık kalır; Bugün ve sabah raporunda "kanıtı belirsiz" görünür.
        }
        return new JsonObject { ["systemMessage"] = $"Söz kanıt: {(run.VerdictLabel ?? ProofStore.VerdictWord(run.Verdict, run.ReasonCode))}. {run.Summary}" };
    }

    /// <summary>Ajanın sözü kullanıcıya soru mu? "Bitti, başka bir şey ister misin?" gibi bitti diyen ve nazikçe soran mesaj soru sayılmaz: kapı koşar.</summary>
    internal static bool IsQuestion(string? agentState, string? lastMsg)
    {
        if (agentState != AgentStates.Asks || string.IsNullOrWhiteSpace(lastMsg)) return false;
        var (_, done, _) = SessionStateResolver.Flags(lastMsg);
        return !done;
    }

    /// <summary>Ajana giden ilk geri yollama metni: kanıt bozulmuş görünüyor.</summary>
    internal static string TamperReason(ProofRunDto run)
    {
        if (run.ReasonCode == ProofStore.Reasons.ApprovedFailed)
            return SecretFilter.MaskSecrets($"Söz kanıt kapısı: kişinin onayladığı testler senin yeni kodunla geçmiyor. {run.ReasonText ?? run.Summary}\nKodu, onaylı testler geçecek şekilde düzelt. Bir testin değişmesi gerçekten gerekiyorsa nedenini açıkça yaz; değiştirmeye devam edersen karar kullanıcıya kalır.");
        if (run.ReasonCode == ProofStore.Reasons.AgentTestsFail)
            return SecretFilter.MaskSecrets($"Söz kanıt kapısı: onaylı testler geçiyor ama senin eklediğin ya da değiştirdiğin testler geçmiyor. {run.ReasonText ?? run.Summary}\nKendi testlerini düzelt ya da geri al. Düzeltmezsen karar kullanıcıya kalır.");
        return SecretFilter.MaskSecrets($"Söz kanıt kapısı: {(run.Results?.Any(x => x.Status == ResultStatus.Unchecked) == true ? "ölçütler doğrulanamadı" : "ölçütler geçti")} ama kanıtı sayamıyorum. {run.ReasonText ?? run.Summary}\nTest tanımını ya da test dosyasını eski hâline getir, ya da bunu neden değiştirdiğini açıkça yaz. Değiştirmeye devam edersen karar kullanıcıya kalır.");
    }

    /// <summary>Kanıt klasöründeki tam (maskeli) çıktı; yoksa null.</summary>
    internal static string? FullOutput(SozDb db, long runId, ProofResultDto r)
    {
        try
        {
            var name = r.ArtifactUrl?.Split('/').LastOrDefault();
            if (name is null || !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return null;
            var p = ProofStore.ArtifactPath(db, runId, name);
            return p is null ? null : File.ReadAllText(p);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Ajana giden kuyruk metni. Telefondan gelen mesajın başına kaynağı yazılır (ajan kaynağı bilsin); panel mesajı eskisi gibi.</summary>
    internal static string FormatQueue(IReadOnlyList<QueueItemDto> items)
    {
        if (items.All(i => i.Origin != "telefon")) return "Kullanıcıdan mesaj: " + string.Join("\n---\n", items.Select(i => i.Text));
        return string.Join("\n---\n", items.Select(i => (i.Origin == "telefon" ? "Telefondan gelen mesaj: " : "Kullanıcıdan mesaj: ") + i.Text));
    }

    private static JsonObject Block(string reason) => new()
    {
        ["decision"] = "block",
        ["reason"] = reason.Length > MaxReasonChars ? reason[..MaxReasonChars] + "…" : reason,
    };

    public static string FailReason(ProofRunDto run, int attempt, int max, Func<ProofResultDto, string?>? fullOutput = null)
    {
        var sb = new StringBuilder();
        var bad = (run.Results ?? new()).Where(r => r.Status != ResultStatus.Passed).ToList();
        sb.Append($"Söz kanıt kapısı: iş bitmiş sayılmadı. {bad.Count} kontrol geçmedi (tur {attempt}/{max}). Düzelt ve yeniden bitir. Kontrolü değiştirme ya da atlama.\n");
        foreach (var r in bad)
        {
            sb.Append($"\n- {r.Label} [{r.Status}{(r.ExitCode is { } e ? $", çıkış {e}" : "")}]\n");
            var all = r.OutputTail ?? "";
            var highlights = Highlights(fullOutput?.Invoke(r) ?? all);
            if (highlights.Count > 0) sb.Append("  öne çıkanlar:\n").Append(string.Join('\n', highlights.Select(h => "    " + h))).Append('\n');
            var tail = all;
            if (tail.Length > PerCheckTail) tail = "…" + tail[^PerCheckTail..];
            if (tail.Length > 0) sb.Append("  çıktının sonu:\n").Append(tail.TrimEnd()).Append('\n');
        }
        return SecretFilter.MaskSecrets(sb.ToString());
    }

    private static readonly System.Text.RegularExpressions.Regex FailLine = new(
        @"(✖|✗|×|\bnot ok\b|\bFAIL(ED|URE)?\b|\bfailed\b|\bfailing\b|AssertionError|\berror [A-Z]{1,4}\d+|\[Söz\])", RegexSpeed.Compiled);

    /// <summary>Kalan testin adını taşıyan satırlar (✖ ad, not ok, FAIL …): en çok 12, her biri 200 karakter.</summary>
    public static List<string> Highlights(string output)
        => output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && FailLine.IsMatch(l) && !l.TrimStart().StartsWith("at "))
            .Select(l => l.Length > 200 ? l[..200] + "…" : l.Trim()).Distinct().Take(12).ToList();

    // ---------------- Notification ----------------
    public static JsonObject? Notification(SqliteConnection c, JsonObject input, Func<string, string?> env)
    {
        var dir = env("CLAUDE_PROJECT_DIR") ?? Str(input, "cwd");
        var (sid, pid) = Session(c, input, dir);
        var kind = Str(input, "notification_type") ?? "notification";
        var now = Time.NowIso();
        c.Exec("""
            UPDATE sessions SET needs_user_since=CASE WHEN needs_user_since IS NULL OR needs_user_kind IS NOT @k THEN @n ELSE needs_user_since END, needs_user_kind=@k, last_event_ts=@n,
              last_at=CASE WHEN last_at IS NULL OR last_at < @n THEN @n ELSE last_at END, started_at=COALESCE(started_at,@n)
            WHERE id=@id
            """, null, ("@n", now), ("@k", kind), ("@id", sid));
        Live.EmitJson(c, Live.Session, pid, NeedsUserPayload(sid, kind));
        return null;
    }

    // ---------------- StopFailure ----------------
    /// <summary>
    /// Ajan sıra sonunda API hatasıyla durdu (Claude Code StopFailure; girdi: error = rate_limit, authentication_failed, billing_error, invalid_request,
    /// server_error, max_output_tokens, unknown; error_details?). Çıktısı yok sayılır. Oturum "hata verip durdu" diye işaretlenir:
    /// needs_user_kind = "failure:&lt;ad&gt;" (<see cref="Soz.Core.Attention.FailureProbe.HookKindPrefix"/>). Kullanıcı yeni istem yazınca işaret kalkar.
    /// </summary>
    public static JsonObject? StopFailure(SqliteConnection c, JsonObject input, Func<string, string?> env)
    {
        var dir = env("CLAUDE_PROJECT_DIR") ?? Str(input, "cwd");
        var (sid, pid) = Session(c, input, dir);
        var name = SafeFailureName(Str(input, "error"));
        var kind = Soz.Core.Attention.FailureProbe.HookKindPrefix + name;
        var now = Time.NowIso();
        c.Exec("""
            UPDATE sessions SET needs_user_since=CASE WHEN needs_user_since IS NULL OR needs_user_kind IS NOT @k THEN @n ELSE needs_user_since END, needs_user_kind=@k, last_event_ts=@n, turn_ended=1,
              last_at=CASE WHEN last_at IS NULL OR last_at < @n THEN @n ELSE last_at END, started_at=COALESCE(started_at,@n)
            WHERE id=@id
            """, null, ("@n", now), ("@k", kind), ("@id", sid));
        Live.EmitJson(c, Live.Session, pid, NeedsUserPayload(sid, kind));
        return null;
    }

    /// <summary>Hata adı yalnız küçük harf, rakam ve alt çizgi; boş ya da bozuksa "unknown" (veritabanına serbest metin yazılmaz).</summary>
    internal static string SafeFailureName(string? raw)
    {
        var t = (raw ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0 || t.Length > 40 || !t.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return "unknown";
        return t;
    }

    /// <summary>"Seni bekliyor" canlı olayının yükü: <c>{"sessionId":…,"needsUser":true,"reason":…}</c> (eski anonim tür + Web varsayılanlı serileştirmeyle bayt bayt aynı; bir test karşılaştırır).</summary>
    internal static string NeedsUserPayload(long sessionId, string reason)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("sessionId", sessionId);
            w.WriteBoolean("needsUser", true);
            w.WriteString("reason", reason);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ---------------- UserPromptSubmit eki ----------------
    /// <summary>İstem bir işin metniyle eşleşirse işi bağlar; oturumun "seni bekliyor" işaretini kaldırır. Hata dışarı sızmaz.</summary>
    public static void OnUserPrompt(SqliteConnection c, JsonObject input, string? dir)
    {
        try
        {
            var ext = Str(input, "session_id");
            if (string.IsNullOrWhiteSpace(ext)) return;
            var (sid, spid) = IngestStore.EnsureSession(c, null, Soz.Core.Agents.AgentRegistry.HookSessionTool, ext!, dir, Str(input, "transcript_path"), SessionKinds.Human);
            var (projectId, _) = HookHandler.FindProject(c, dir);
            c.Exec("UPDATE sessions SET needs_user_since=NULL, needs_user_kind=NULL WHERE id=@id", null, ("@id", sid));
            JobStore.ResumeAnswered(c, sid, projectId ?? spid); // cevap bekleyen iş, kullanıcı yazınca çalışıyora döner
            // İş yalnız kendi projesindeki oturuma bağlanır (başka projeden gelen eşleşen istem işi bağlamaz).
            JobStore.LinkOnPrompt(c, Str(input, "prompt"), sid, projectId ?? spid);
        }
        catch (Exception) { /* kanca asla bozulmaz; bağlama yapılamazsa iş kartı "verildi" kalır */ }
    }
}
