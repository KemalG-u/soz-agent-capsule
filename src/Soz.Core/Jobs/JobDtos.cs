using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Proof;
using Soz.Core.Run;

namespace Soz.Core.Jobs;

// ---------- API biçimleri (FABLE-VIZYON-2 bölüm 5; JSON camelCase) ----------
public sealed record JobCheckRef(long Id, string Label, string Kind, bool Enabled, long? JobId);
public sealed record ProofCounts(int Passed, int Failed, int Skipped, int Error);
public sealed record ClaimDto(bool ClaimsDone, bool ShowsEvidence, string Label);
public sealed record ProofResultDto(long Id, long CheckId, string Label, string Kind, string Status, int? ExitCode, string? OutputTail, string? ArtifactUrl, long DurationMs, string? ArtifactKind = null, int? TestsTotal = null, int? TestsSkipped = null, int? TestsPassed = null,
    string? Confidence = null, string? ConfidenceLabel = null, string? Source = null);
/// <summary>Koşunun güven özeti (A ölçüldü, B dolaylı, C ajan beyanı, D kanıt yok): Level geçen sonuçların en zayıfı; HumanAccepted: bir sonuç insan kabulüne dayanıyor ("Sen onayladın").</summary>
public sealed record ConfidenceDto(string Level, string Label, int A, int B, int C, int D, bool HumanAccepted);
/// <summary>
/// Kanıt çalışması. Denetim 2 alanları: ReasonCode (no_checks | no_tests | definition_changed | tests_changed | count_dropped | checks_changed | interrupted | null),
/// ReasonText (sade Türkçe), ChangedFiles, CountUnknown (test sayısı okunamadı), AddedTests (yeni eklenen test dosyaları), VerdictLabel (hükmün sade adı, nedene göre).
/// </summary>
public sealed record ProofRunDto(long Id, long ProjectId, string? ProjectName, long? JobId, string? JobTitle, long? SessionId, string Trigger, int Attempt,
    string? StartedAt, string? FinishedAt, string? Verdict, string? Summary, ProofCounts Counts, ClaimDto? Claim, List<ProofResultDto>? Results,
    string? ReasonCode = null, string? ReasonText = null, List<string>? ChangedFiles = null, bool CountUnknown = false, List<string>? AddedTests = null, string? VerdictLabel = null, bool Accepted = false, string? BaselineText = null,
    string? SnapshotId = null, List<Soz.Core.Proof.EvidenceBase.FileDiff>? Diffs = null, List<string>? Notes = null, string? Uid = null, ConfidenceDto? Confidence = null);
public sealed record PendingApprovalDto(long EventId, string Summary, string? Detail);
/// <summary>İşin toplam harcaması: harcama bildiren koşu sayısı, token toplamları ve aracın bildirdiği dolar toplamı (Codex dolar vermez, yalnız token).</summary>
public sealed record JobSpend(int Runs, long? InputTokens, long? CachedTokens, long? OutputTokens, long? ReasoningTokens, double? CostUsd);
public sealed record JobDto(long Id, long ProjectId, string? ProjectName, long? SessionId, string Title, string RawText, string PromptText, string Source, string State,
    bool Night, bool Restate, string? CreatedAt, string? GivenAt, string? FinishedAt, int Attempts, string? LastVerdict, long? LastRunId,
    List<JobCheckRef> Checks, List<ProofRunDto>? Runs = null, string? AgentState = null, JobRunInfo? Run = null,
    bool AcceptedByUser = false, string? StartError = null, string? Question = null, List<PendingApprovalDto>? PendingApprovals = null, string? BaseNote = null,
    string? AcceptedVia = null, bool AcceptedUnverified = false, string? AcceptedProcess = null, string Agent = "claude", JobSpend? Spend = null,
    string? Uid = null, string Kind = "kod", bool WriteCheckFirst = false, List<SuggestedCheckDto>? SuggestedChecks = null, TestOfferDto? TestOffer = null, string? LastReason = null);
/// <summary>
/// İş kanıtıyla bitince ajanın yaptığı iki ayrı teklif (e3). (a) Added: ajanın EKLEDİĞİ yeni test, test ayarı ve yardımcı dosyalar, AddedSentence ile sorulur, tek tıkla katılır.
/// (b) Edited: ajanın DEĞİŞTİRDİĞİ onaylı dosyalar, EditedSentence ile söylenir, varsayılan Hayır. Kabul için önce GET /api/jobs/{id}/test-offer ile fark özeti okunur, sonra EditedId ile kabul edilir.
/// Cevap yalnız insandan gelir (POST /api/jobs/{id}/approve-new-tests, gövde {scope, diffId}). Sentence: ikisinin birleşimi (eski alan).
/// </summary>
public sealed record TestOfferDto(int Added, int Edited, string Sentence, string? AddedSentence = null, string? EditedSentence = null, string? EditedId = null, List<TestOfferFileDto>? EditedFiles = null);
/// <summary>Değiştirilen onaylı dosyanın fark özeti. Lines yalnız GET /api/jobs/{id}/test-offer yanıtında dolu (iş kartında null).</summary>
public sealed record TestOfferFileDto(string Path, string Sentence, List<EvidenceBase.DiffLine>? Lines = null);
/// <summary>Ajanın yazdığı denemeden Söz'ün bulup ÖNERDİĞİ ölçüt (kapalı; kişi kabul edene kadar ölçülmez, düzey "ajan beyanı").</summary>
public sealed record SuggestedCheckDto(long CheckId, string Label, string? Cmd, string Level, string LevelLabel, string Sentence, string? Strength = null, string? StrengthNote = null, string DefaultAnswer = "evet", bool Rejected = false);
public sealed record CheckDto(long Id, long ProjectId, long? JobId, string Kind, string Label, JsonObject Spec, int TimeoutS, bool Enabled, string Origin, string? CreatedAt, string? Uid = null);
public sealed record SuggestResult(int Added, List<CheckDto> Checks);
/// <summary>DeliveryNote (n6): mesaj kuyrukta ama ajanın onu görmeyeceği ihtimali varsa sade uyarı (terminal desteği kurulu görünmüyor gibi); sorun yoksa null.</summary>
public sealed record QueueItemDto(long Id, long? ProjectId, string? ProjectName, long? SessionId, string Text, string? CreatedAt, string? DeliveredAt, string Origin, string? DeliveryNote = null);
public sealed record ProjectGateDto(long Id, string Name, string? Path, bool GateEnabled, int Checks, int EnabledChecks);

public static class JobStates
{
    public const string Draft = "taslak", Given = "verildi", Running = "calisiyor", Passed = "gecti", Failed = "kaldi", Cancelled = "iptal";
    /// <summary>Denetim 2: sıra bekleyen, kullanıcıdan cevap/onay/karar bekleyen, durdurulan ve hiç başlatılamayan iş.</summary>
    public const string Queued = "sirada", AwaitingAnswer = "cevap_bekliyor", AwaitingApproval = "onay_bekliyor", AwaitingDecision = "karar_bekliyor", Stopped = "durduruldu", StartFailed = "baslatilamadi";
    public static readonly string[] All = { Draft, Given, Queued, Running, AwaitingAnswer, AwaitingApproval, AwaitingDecision, Passed, Failed, Stopped, StartFailed, Cancelled };
    /// <summary>Bitmiş sayılan durumlar: değiştirilemez (PATCH), "geçti" olamaz.</summary>
    public static bool IsFinal(string s) => s is Passed or Failed or Cancelled;
    /// <summary>Kullanıcıdan bir şey bekleyen durumlar.</summary>
    public static bool IsWaitingUser(string s) => s is AwaitingAnswer or AwaitingApproval or AwaitingDecision;
    /// <summary>Oturuma bağlı etkin iş (kapı bu işin ölçütlerini çalıştırır).</summary>
    public static readonly string[] ActiveSet = { Given, Running, AwaitingAnswer, AwaitingApproval };
}

/// <summary>
/// Canlı olaylar: events tablosuna yazılır; sunucu tabloyu yoklayıp SSE olarak yayınlar.
/// Böylece ayrı süreçte çalışan kancaların değişiklikleri de arayüze ulaşır.
/// </summary>
public static class Live
{
    public const string Job = "job", Proof = "proof", Queue = "queue", Session = "session_needs_user", JobProgress = "job_progress", JobState = "job_state";
    public static readonly string[] Kinds = { Job, Proof, Queue, Session, JobProgress, JobState };

    public static void Emit(SqliteConnection c, string kind, long? projectId, object payload)
    {
        try { Events.Log(c, kind, projectId, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))); }
        catch (SqliteException) { /* canlı olay kaybı işi bozmaz */ }
    }

    /// <summary>
    /// Yükü hazır JSON olarak yazar (<see cref="Emit"/> ile aynı kayıt). Kanca yolunda anonim tür serileştirmesi ilk kullanımda yansıma kurduğu için
    /// (süreç başına onlarca ms) yük elle yazılır; biçim <see cref="Emit"/>'in Web varsayılanlı çıktısıyla bayt bayt aynı olmalıdır.
    /// </summary>
    public static void EmitJson(SqliteConnection c, string kind, long? projectId, string json)
    {
        try { Events.Log(c, kind, projectId, json); }
        catch (SqliteException) { /* canlı olay kaybı işi bozmaz */ }
    }

    /// <summary>events kaydının SSE olay adı.</summary>
    public static string SseName(string kind) => kind switch { Session => "session", JobProgress => "job.progress", JobState => "job.state", _ => kind };
}
