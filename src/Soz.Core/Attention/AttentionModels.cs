using System.Text.Json.Nodes;

namespace Soz.Core.Attention;

/// <summary>
/// "Sıra bende" öğe türleri ve TEK öncelik sırası. Sıra yalnız burada (sunucuda) yazılır; kapsül ve arayüz kendi sırasını icat etmez, öğedeki `priority` alanına uyar.
///
/// | priority | tür / neden                                   | anlamı                                                            |
/// |----------|-----------------------------------------------|-------------------------------------------------------------------|
/// | 1        | ask (question, approval, session_question, session_permission, session_input, session_waiting) | Ajan sana soru sordu, izin ya da bilgi bekliyor |
/// | 2        | accept (decision)                             | Kanıt sayılamadı, kabul edip etmeyeceğine sen karar vereceksin    |
/// | 2        | blocked (proof_failed)                        | Kanıtı KALDI: devam mı edilsin bırakılsın mı kararı senin; kabul bekleyenle aynı önemde |
/// | 3        | blocked (start_failed, session_failed ve öteki) | İş başlatılamadı ya da ajan hata verip durdu (engel)            |
/// | 4        | done (passed, session_done)                   | Kanıtıyla bitti, görmeni bekliyor                                 |
/// | 5        | uncertain (unproven, run_lost, session_quiet, session_turn_end) | Söz emin değil; ayrı durur, kimseyi uyandırmaz (session_turn_end: yardımcı durdu, "bitti" dediğine dair işaret yok) |
///
/// Aynı sırada olanlar önce tür sırasıyla (ask, accept, blocked, done, uncertain), sonra durumun başladığı ana göre (eski önce) dizilir.
/// </summary>
public static class AttentionKinds
{
    public const string Ask = "ask", Accept = "accept", Blocked = "blocked", Done = "done", Uncertain = "uncertain";

    /// <summary>Kişi bir şey denerken çıkan engelin nedeni: dikkat bütçesine girmez, yerinde gösterilir.</summary>
    public const string StartFailedReason = "start_failed";
    /// <summary>Kanıtı kalan iş: kişinin kararını bekler, "kabul bekleyen" ile aynı önemdedir.</summary>
    public const string ProofFailedReason = "proof_failed";
    public static readonly string[] All = { Ask, Accept, Blocked, Done, Uncertain };

    /// <summary>Yukarıdaki tablo: (tür, neden ya da null = türün geri kalanı, priority). Test bunu sabitler.</summary>
    public static readonly IReadOnlyList<(string Kind, string? Reason, int Priority)> Table = new (string, string?, int)[]
    {
        (Ask, null, 1), (Accept, null, 2), (Blocked, ProofFailedReason, 2), (Blocked, null, 3), (Done, null, 4), (Uncertain, null, 5),
    };

    public static int Priority(string kind, string? reason = null)
    {
        foreach (var t in Table) if (t.Kind == kind && t.Reason is not null && t.Reason == reason) return t.Priority;
        foreach (var t in Table) if (t.Kind == kind && t.Reason is null) return t.Priority;
        return Table[^1].Priority;
    }

    /// <summary>Aynı priority içinde tür sırası.</summary>
    public static int KindOrder(string kind) { var i = Array.IndexOf(All, kind); return i < 0 ? All.Length : i; }
}

/// <summary>Öğe için yapılabilecek bir şey: düz Türkçe düğme adı ve onu yapacak API ucu (yazma uçları yalnız insandan kabul edilir).</summary>
public sealed record AttentionAction(string Id, string Label, string Method, string Path, JsonObject? Body = null);

/// <summary>
/// "Sıra bende" öğesi. Id durum girişine bağlıdır (aynı iş yeniden soru sorarsa yeni öğedir). At: öğenin ait olduğu durumun başladığı an (ISO UTC).
/// Confidence: firm (Söz'ün kendi kaydı) ya da unknown (tahmin, belirsiz). Signal: bu öğe için bugünkü istem dışı işaret hakkı verildi mi (kapsül yalnız buna bakarak uyanır).
/// IsNew: istemcinin verdiği since değerinden sonra ortaya çıktı mı. Reason: öğenin nedeni (question, approval, decision, proof_failed, start_failed, passed, unproven, session_question, session_permission, session_input, session_waiting, session_failed, session_done, session_quiet, run_lost).
/// n6 ek alanları (hepsi eski istemcilere zararsızdır, boş olabilir): Helper yardımcının adı (Claude Code, Codex...), ExternalSessionId oturumun yardımcıdaki kimliği,
/// Source öğenin kaynağı (job, hook, agents, log, hook+agents), ObservedAt kaynağın bu bilgiyi en son gördüğü an, AgeSeconds durumun başlamasından bu yana geçen saniye,
/// ProjectPath proje klasörü (varsa).
/// </summary>
public sealed record AttentionItem(string Id, string Kind, string Reason, int Priority, string Sentence, string? Detail, string Title, string? JobUid, long? JobId, long? SessionId,
    string? ProjectName, string At, string Confidence, bool Signal, bool IsNew, List<AttentionAction> Actions,
    string? Helper = null, string? ExternalSessionId = null, string? Source = null, string? ObservedAt = null, int? AgeSeconds = null, string? ProjectPath = null,
    string? SozNotu = null, bool? CanProve = null, string? CanProveReason = null);
// SozNotu: Söz'ün KENDİ notu (Detail yardımcının sözüdür, ikisi karışmaz). CanProve: "bu işi sına" çalışır mı (yalnız session_done öğesinde dolu; false ise
// CanProveReason no_checks | no_project ve "prove" eylemi listede yoktur). session_turn_end (uncertain): yardımcı durdu, bitirdiğine dair işaret yok.

/// <summary>Çalışan iş (dikkat istemez; kapsül "çalışıyor" göstersin diye listelenir).</summary>
/// StartedAt: çalışmanın başladığı an (ISO UTC; koşunun başlangıcı, yoksa işin verilme anı), AgeSeconds: o andan bu yana geçen saniye. İkisi de bilinmiyorsa boş (eski istemciler için zararsız ek alan).
public sealed record RunningItem(string? JobUid, long JobId, string Title, string? ProjectName, string? StartedAt = null, int? AgeSeconds = null);

public sealed record AttentionCounts(int Ask, int Accept, int Blocked, int Done, int Uncertain, int Total, int Running);

/// <summary>Bugünkü dikkat bütçesi: günlük hak, harcanan işaret, hakkı yetmediği için sessizce listeye giren öğe sayısı.</summary>
public sealed record AttentionBudget(int DailyLimit, int UsedToday, int SuppressedToday, string Day);

public sealed record AttentionSnapshot(long Rev, string Now, string Sentence, AttentionCounts Counts, AttentionBudget Budget, List<AttentionItem> Items, List<RunningItem> Running);
