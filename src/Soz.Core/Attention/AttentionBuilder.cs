using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Morning;
using Soz.Core.Run;
using Soz.Core.Sessions;
using Soz.Core.Text;

namespace Soz.Core.Attention;

/// <summary>
/// Kişinin sırası gelen şeyleri tek listede toplar (yalnız okur, hiçbir şey yazmaz). Kaynaklar: iş kartları (cevap, onay, karar, kaldı, başlatılamadı, bitti),
/// ajan oturumları (soru, takılma) ve çalışan koşular. Süreçte yeni durum kuralı icat edilmez: durumlar işin ve oturumun kendi kayıtlarından gelir.
/// Her öğenin cümlesi düz Türkçedir; ham teknik ad ya da dosya yolu taşımaz.
/// </summary>
public static partial class AttentionBuilder
{
    public sealed record Collected(List<AttentionItem> Items, List<RunningItem> Running);

    /// <summary>Çalışıyor görünen ama bu kadar saniyedir süreci bulunamayan koşu "belirsiz" sayılır (başlangıç ölçümü sürerken yanlış alarm olmasın).</summary>
    public const int DeadRunGraceSeconds = 30;

    public static Collected Collect(SqliteConnection c, DateTime nowUtc, SozSettings st, IReadOnlyList<ExternalSession>? external = null, Func<ExternalSession, DateTime>? externalSince = null)
    {
        var items = new List<AttentionItem>();
        var running = new List<RunningItem>();
        var now = MorningBuilder.Now(c, nowUtc, StateThresholds.From(st));
        var nowIso = Time.Iso(nowUtc);
        var seenJobs = new HashSet<long>();

        // ---- seni bekleyen işler (cevap, onay, karar, görülmemiş kaldı) ----
        foreach (var j in now.WaitingJobs ?? new())
        {
            if (!seenJobs.Add(j.Id)) continue;
            var at = EnteredAt(c, j, nowIso);
            switch (j.State)
            {
                case JobStates.AwaitingAnswer:
                    items.Add(JobItem(j, AttentionKinds.Ask, "question", $"«{Title(j)}» işinde yardımcı sana bir soru sordu.", Clean(j.Question, 240), at, firm: true,
                        new() { new("reply", "Cevap yaz", "POST", $"/api/jobs/{j.Id}/reply", new JsonObject { ["text"] = "" }) }));
                    break;
                case JobStates.AwaitingApproval:
                {
                    var ap = j.PendingApprovals?.FirstOrDefault();
                    var actions = new List<AttentionAction>();
                    if (ap is not null)
                    {
                        actions.Add(new("allow", "İzin ver", "POST", $"/api/jobs/{j.Id}/approve", new JsonObject { ["allow"] = true, ["eventId"] = ap.EventId }));
                        actions.Add(new("deny", "İzin verme", "POST", $"/api/jobs/{j.Id}/approve", new JsonObject { ["allow"] = false, ["eventId"] = ap.EventId }));
                    }
                    items.Add(JobItem(j, AttentionKinds.Ask, "approval", $"«{Title(j)}» işinde yardımcı bir işlem için senden izin bekliyor.", Clean(ap?.Summary, 240), at, firm: true, actions));
                    break;
                }
                case JobStates.AwaitingDecision:
                {
                    var snap = c.Scalar("SELECT snapshot_id FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", null, ("@j", j.Id)) as string;
                    var why = c.Scalar("SELECT COALESCE(reason_text, summary) FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", null, ("@j", j.Id)) as string;
                    var body = new JsonObject { ["accept"] = true };
                    if (snap is not null) body["snapshotId"] = snap;
                    items.Add(JobItem(j, AttentionKinds.Accept, "decision", $"«{Title(j)}» işinde deneme sonucu sayılamadı. Sonucu kabul edip etmeyeceğine sen karar ver.", Clean(why, 240), at, firm: true,
                        new()
                        {
                            new("accept", "Kabul ediyorum", "POST", $"/api/jobs/{j.Id}/accept", body),
                            new("reject", "Kabul etmiyorum", "POST", $"/api/jobs/{j.Id}/accept", new JsonObject { ["accept"] = false }),
                        }));
                    break;
                }
                case JobStates.Failed:
                {
                    var sum = c.Scalar("SELECT summary FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", null, ("@j", j.Id)) as string;
                    items.Add(JobItem(j, AttentionKinds.Blocked, AttentionKinds.ProofFailedReason, $"«{Title(j)}» için deneme sonucu geçmedi.", Clean(sum, 240), at, firm: true, FailedActions(j.Id)));
                    break;
                }
            }
        }

        // ---- başlatılamayan işler ----
        var doneCut = Time.Iso(nowUtc.AddHours(-Math.Max(1, st.ClampedInt("attentionDoneHours"))));
        foreach (var id in c.Query("SELECT id FROM jobs WHERE state='baslatilamadi' AND seen_at IS NULL AND finished_at>=@t ORDER BY id", r => r.GetInt64(0), null, ("@t", doneCut)))
        {
            if (!seenJobs.Add(id) || JobStore.Get(c, id) is not { } j) continue;
            items.Add(JobItem(j, AttentionKinds.Blocked, AttentionKinds.StartFailedReason, $"«{Title(j)}» başlatılamadı.", Clean(j.StartError, 240), j.FinishedAt ?? nowIso, firm: true, FailedActions(j.Id)));
        }

        // ---- kanıtıyla biten ve sen henüz görmediğin işler ("bitti") ----
        foreach (var id in c.Query("""
            SELECT id FROM jobs WHERE state='gecti' AND COALESCE(accepted_by_user,0)=0 AND seen_at IS NULL AND finished_at>=@t ORDER BY finished_at DESC LIMIT 50
            """, r => r.GetInt64(0), null, ("@t", doneCut)))
        {
            if (!seenJobs.Add(id) || JobStore.Get(c, id) is not { } j) continue;
            var sum = c.Scalar("SELECT summary FROM proof_runs WHERE job_id=@j AND verdict='passed' ORDER BY id DESC LIMIT 1", null, ("@j", id)) as string;
            var passedReason = c.Scalar("SELECT reason_code FROM proof_runs WHERE job_id=@j AND verdict='passed' ORDER BY id DESC LIMIT 1", null, ("@j", id)) as string;
            var noChange = passedReason is "no_change" or "ignored_only"; // ikisi de sakin etiket; cümle ayrı (K7)
            // Ajan proje klasörünün dışına da yazdıysa "bitti" cümlesi bunu söyler (kanıt geçse de kişi bilmeli, denetim 5, B11).
            var outside = c.Scalar("SELECT COUNT(*) FROM events WHERE kind=@k AND json_extract(detail,'$.jobId')=@j", null, ("@k", Soz.Core.Guard.GuardHook.OutsideWriteEvent), ("@j", id)) is long n && n > 0;
            // N6: ajan hiçbir dosyayı değiştirmediyse "kanıtıyla bitti" denmez (yeşil değil, sakin etiket); öncelik yine "bitti" sınıfında (4).
            var ignoredNames = passedReason == "ignored_only" ? System.Text.RegularExpressions.Regex.Match(sum ?? "", @"izlenmeyen klasörlere yazdı \((?<n>[^)]*)\)").Groups["n"].Value : "";
            var doneSentence = passedReason == "ignored_only" ? $"«{Title(j)}» için yardımcı yalnız izlenmeyen klasörlere yazdı{(ignoredNames.Length > 0 ? " (" + ignoredNames + ")" : "")}. Kaynakta değişiklik yok."
                : noChange ? $"«{Title(j)}» için yardımcı hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu."
                : outside ? $"«{Title(j)}» deneme sonucuyla bitti, ama yardımcı proje klasörünün dışına da yazdı." : $"«{Title(j)}» deneme sonucuyla bitti.";
            var doneActions = AckOnly();
            var offerText = j.TestOffer?.Sentence;
            // Eklenen yeni testler tek tıkla katılır. Değiştirilen onaylı testler için tek tıkla kabul YOK: "Değişikliği gör" fark özetini açar, kabul onun kimliğiyle ayrı yapılır (e3).
            if (j.TestOffer is { Added: > 0 }) doneActions.Insert(0, new("approve-tests", "Evet, kat", "POST", $"/api/jobs/{j.Id}/approve-new-tests", new JsonObject { ["scope"] = "added" }));
            if (j.TestOffer is { Added: > 0 }) doneActions.Add(new("decline-tests", "Hayır", "POST", $"/api/jobs/{j.Id}/decline-new-tests", new JsonObject { ["scope"] = "added" }));
            if (j.TestOffer is { Edited: > 0 }) doneActions.Add(new("decline-test-edit", "Değişikliği kabul etme", "POST", $"/api/jobs/{j.Id}/decline-new-tests", new JsonObject { ["scope"] = "edited" }));
            if (j.TestOffer is { Edited: > 0 }) doneActions.Insert(j.TestOffer.Added > 0 ? 1 : 0, new("view-test-edit", "Değişikliği gör", "GET", $"/api/jobs/{j.Id}/test-offer", null));
            items.Add(JobItem(j, AttentionKinds.Done, noChange ? "no_change" : "passed", doneSentence, Clean(offerText is null ? sum : (sum is null ? offerText : offerText + " " + sum), 300), j.FinishedAt ?? nowIso, firm: true, doneActions));
        }

        // ---- ajan bitti ama Söz kanıtlayamadı: "bitti" de "kaldı" da denmez ----
        foreach (var id in c.Query("""
            SELECT j.id FROM jobs j
            WHERE j.state='verildi' AND j.seen_at IS NULL
              AND (SELECT r.status FROM runs r WHERE r.job_id=j.id ORDER BY r.id DESC LIMIT 1)='done'
              AND COALESCE((SELECT p.verdict FROM proof_runs p WHERE p.job_id=j.id AND p.verdict IS NOT NULL ORDER BY p.id DESC LIMIT 1),'unchecked') IN ('unchecked','error')
              AND (SELECT r.ended FROM runs r WHERE r.job_id=j.id ORDER BY r.id DESC LIMIT 1)>=@t
            ORDER BY j.id
            """, r => r.GetInt64(0), null, ("@t", doneCut)))
        {
            if (!seenJobs.Add(id) || JobStore.Get(c, id) is not { } j) continue;
            var ended = j.Run?.Ended ?? j.FinishedAt ?? nowIso;
            // Neden kanıtlanamadığı kanıt çalışmasının kendi cümlesinden gelir (ölçüt yok, ajan deneme yazdı, ölçülemedi...).
            var why = c.Scalar("SELECT summary FROM proof_runs WHERE job_id=@j AND verdict IS NOT NULL ORDER BY id DESC LIMIT 1", null, ("@j", id)) as string;
            items.Add(JobItem(j, AttentionKinds.Uncertain, "unproven", $"«{Title(j)}» denenmeden bitti: yardımcı bitirdi ama Söz bunu deneyemedi. Bitti mi bilmiyorum.",
                string.IsNullOrWhiteSpace(why) ? "Bu iş için çalışan bir kontrol yok ya da denenemedi. Sonucu kendin açıp bakmalısın." : Clean(why, 240) + " Sonucu kendin açıp bakmalısın.", ended, firm: false, AckOnly()));
        }

        items.AddRange(SessionItems(c, now, nowUtc, st, nowIso, external, externalSince));

        // ---- çalışanlar + süreci kaybolmuş koşular ----
        foreach (var j in now.RunningJobs)
        {
            running.Add(RunningOf(j, nowUtc));
            if (j.Run is { Status: RunStatus.Running } run && Time.Parse(run.Started) is { } started && (nowUtc - started).TotalSeconds > DeadRunGraceSeconds
                && !WorkService.HasLiveRun(c, j.Id))
                items.Add(JobItem(j, AttentionKinds.Uncertain, "run_lost", $"«{Title(j)}» çalışıyor görünüyor ama yardımcının süreci bulunamadı. Durumu belirsiz.", null, run.Started ?? nowIso, firm: false,
                    new() { new("stop", "Durdur", "POST", $"/api/jobs/{j.Id}/stop") }));
        }

        items = items.Select(i => i.AgeSeconds is null && Time.Parse(i.At) is { } t ? i with { AgeSeconds = (int)Math.Max(0, (nowUtc - t).TotalSeconds) } : i)
            .OrderBy(i => i.Priority).ThenBy(i => AttentionKinds.KindOrder(i.Kind)).ThenBy(i => i.At, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        return new Collected(items, running);
    }

    private static List<AttentionAction> FailedActions(long jobId) => new()
    {
        new("continue", "Devam et", "POST", $"/api/jobs/{jobId}/continue"),
        new("seen", "Gördüm", "POST", "/api/attention/ack"),
    };

    private static List<AttentionAction> AckOnly() => new() { new("seen", "Gördüm", "POST", "/api/attention/ack") };

    /// <summary>Çalışan işin satırı: başlangıç anı koşunun başlangıcıdır, yoksa işin verilme anı; ikisi de okunamazsa süre alanları boş kalır (uydurma süre yok).
    /// Gelecekteki bir an (saat kayması) 0 sn sayılır.</summary>
    public static RunningItem RunningOf(JobDto j, DateTime nowUtc)
    {
        var start = Time.Parse(j.Run?.Started) ?? Time.Parse(j.GivenAt);
        if (start is not { } s) return new RunningItem(j.Uid, j.Id, Title(j), j.ProjectName);
        return new RunningItem(j.Uid, j.Id, Title(j), j.ProjectName, Time.Iso(s), (int)Math.Max(0, (nowUtc - s).TotalSeconds));
    }

    private static string Title(JobDto j) => string.IsNullOrWhiteSpace(j.Title) ? "Adsız iş" : TextNormalizer.Truncate(j.Title.Trim(), 60);

    private static string? Clean(string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? null : Plain(SecretFilter.MaskSecrets(TextNormalizer.Truncate(s.Trim(), max)));

    /// <summary>Kutudaki metinde teknik söz bırakmaz (n6): ajan araçlarından gelen hata cümlelerindeki "kum havuzu" gibi sözler sade karşılığına döner.</summary>
    internal static string Plain(string s)
        => s.Replace("Windows kum havuzu", "Windows güvenli çalışma alanı", StringComparison.OrdinalIgnoreCase).Replace("kum havuzu", "güvenli çalışma alanı", StringComparison.OrdinalIgnoreCase)
            .Replace("sandbox", "güvenli çalışma alanı", StringComparison.OrdinalIgnoreCase);

    /// <summary>Yardımcının kişiye gösterilen adı (Claude Code, Codex...). Katalogda yoksa kimlik olduğu gibi döner.</summary>
    internal static string? HelperName(string? tool)
        => string.IsNullOrWhiteSpace(tool) ? null : Soz.Core.Assistants.AssistantCatalog.All.FirstOrDefault(a => a.Id.Equals(tool, StringComparison.OrdinalIgnoreCase))?.Title ?? tool;

    private static AttentionItem JobItem(JobDto j, string kind, string reason, string sentence, string? detail, string at, bool firm, List<AttentionAction> actions)
    {
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var prefix = $"«{Title(j)}» için ";
            var body = sentence.StartsWith(prefix, StringComparison.Ordinal) ? sentence[prefix.Length..] : sentence;
            if (detail.Equals(sentence, StringComparison.OrdinalIgnoreCase)) detail = null;
            else if (detail.StartsWith(body, StringComparison.OrdinalIgnoreCase))
            {
                detail = detail[body.Length..].Trim();
                if (detail.Length == 0) detail = null;
            }
        }
        // "Gördüm" eylemi öğe kimliğiyle çalışır; kimlik aşağıda doldurulur
        var id = $"{kind}:job:{j.Uid ?? j.Id.ToString()}:{at}";
        var acts = actions.Select(a => a.Path == "/api/attention/ack" ? a with { Body = new JsonObject { ["id"] = id } } : a).ToList();
        return new AttentionItem(id, kind, reason, AttentionKinds.Priority(kind, reason), sentence, detail, Title(j), j.Uid, j.Id, j.SessionId, j.ProjectName, at, firm ? "firm" : "unknown", false, false, acts,
            Helper: HelperName(j.Agent), Source: "job", ObservedAt: at);
    }

    /// <summary>
    /// İşin şu durumuna girdiği an. Biten durumlarda işin kendi bitiş damgası; soru, onay, karar gibi bekleme durumlarında olay kaydındaki son durum değişimi
    /// (iş aynı durumu ikinci kez yaşarsa farklı an, dolayısıyla yeni öğe olur). Bulunamazsa işin verilme ya da oluşma anı.
    /// </summary>
    internal static string EnteredAt(SqliteConnection c, JobDto j, string fallbackIso)
    {
        if (j.State == JobStates.Failed && j.FinishedAt is not null) return j.FinishedAt;
        try
        {
            var min = Math.Max(0, Events.LastSeq(c) - 20000);
            var ts = c.Scalar("""
                SELECT ts FROM event_log
                WHERE seq>@m AND kind='legacy.job'
                  AND json_extract(json_extract(payload,'$.detail'),'$.id')=@id
                  AND json_extract(json_extract(payload,'$.detail'),'$.state')=@s
                ORDER BY seq DESC LIMIT 1
                """, null, ("@m", min), ("@id", j.Id), ("@s", j.State)) as string;
            if (ts is not null) return ts;
        }
        catch (SqliteException) { /* kayıt okunamazsa yedek an kullanılır */ }
        return j.GivenAt ?? j.CreatedAt ?? fallbackIso;
    }
}
