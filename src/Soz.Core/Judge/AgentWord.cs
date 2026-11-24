using System.Text.RegularExpressions;
using Soz.Core.Data;
using Soz.Core.Jobs;

namespace Soz.Core.Judge;

/// <summary>Ajanın son sözünün sınıfı (iş listesindeki durum). soru: kullanıcıdan cevap bekliyor; kanit: bitti diyor ve kanıt gösteriyor; bitti: bitti diyor, kanıt yok; takildi: engel ya da başarısızlık anlatıyor.</summary>
public static class AgentStates
{
    public const string Asks = "soru", Done = "bitti", Evidence = "kanit", Stuck = "takildi";
}

/// <summary>Hakem (M9) bağlantısı: (1) son sözü sınıflar, (2) ölçütsüz işte "ajan şunu gösterdi" özeti çıkarır. Her karar Hakem aracılığıyla events'e yazılır.</summary>
public static class AgentWord
{
    private static readonly Regex StuckRe = new(@"(takıl|takil|yapamadım|yapamadim|olmadı\b|olmadi\b|engellen|izin (yok|verilmedi)|hata aldım|hata aldim|çalışmıyor|calismiyor|couldn't|could not|unable to|blocked|stuck|cannot|can't|failed to)", RegexSpeed.Compiled | RegexOptions.IgnoreCase);

    public static string? Classify(IJudge judge, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var yn = JudgeQuestions.YesNo;
        var asks = judge.Decide(JudgeQuestions.AsksQuestion, yn, text);
        if (asks.Answer == "evet") return AgentStates.Asks;
        var done = judge.Decide(JudgeQuestions.ClaimsDone, yn, text);
        if (done.Answer == "evet")
        {
            var ev = judge.Decide(JudgeQuestions.ShowsEvidence, yn, text);
            return ev.Answer == "evet" ? AgentStates.Evidence : AgentStates.Done;
        }
        return StuckRe.IsMatch(text) ? AgentStates.Stuck : null;
    }

    /// <summary>İşin ajan durumunu yazar (yalnız değer değiştiyse). Hata dışarı sızmaz: kanca ve yürütücü bozulmaz.</summary>
    public static string? ClassifyAndStore(SozDb db, long jobId, long? projectId, string? text, string source)
    {
        try
        {
            var state = Classify(JudgeFactory.Create(db, projectId, jobId, source), text);
            using var c = db.Open();
            c.Exec("UPDATE jobs SET agent_state=@s WHERE id=@j", null, ("@s", state), ("@j", jobId));
            Live.Emit(c, Live.Job, projectId, new { id = jobId, agentState = state });
            return state;
        }
        catch (Exception) { return null; }
    }

    private static readonly Regex EvidenceLine = new(@"(\b(?:test|tests|testler|testleri)\b.{0,60}\b(?:geçti|gecti|passed|başarılı|basarili)\b|\d+\s*(?:/\s*\d+\s*)?(?:geçti|gecti|passed|pass)\b|✓|✔|\bKontrol:|[A-Za-z]:\\\S+|\b[\w.-]+/[\w./-]+\.\w{1,6}\b|https?://\S+)", RegexSpeed.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Ajanın sözünden en çok 3 kanıt cümlesi (her biri 160 karakter). Hakem "kanıt gösteriyor" derse çağrılır; yoksa null.</summary>
    public static string? EvidenceExcerpt(IJudge judge, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var shows = judge.Decide(JudgeQuestions.ShowsEvidence, JudgeQuestions.YesNo, text);
        if (shows.Answer != "evet") return null;
        var lines = text.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•', ' ')).Where(l => l.Length > 3 && !l.StartsWith("```") && EvidenceLine.IsMatch(l))
            .Select(l => l.Length > 160 ? l[..160] + "…" : l).Distinct().Take(3).ToList();
        return lines.Count == 0 ? null : string.Join(" · ", lines.Select(l => "«" + l + "»"));
    }
}
