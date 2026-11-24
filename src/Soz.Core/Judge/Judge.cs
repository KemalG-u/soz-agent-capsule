using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Soz.Core.Data;
using Soz.Core.Extract;
using Soz.Core.Sessions;

namespace Soz.Core.Judge;

/// <summary>Hakemin cevabı. Answer = seçeneklerden biri (karar veremediyse null). Confidence 0..1. Engine: "rules" | "claude-cli" | "rules(yedek)".</summary>
public sealed record JudgeResult(string? Answer, double Confidence, string Engine, long Ms);

/// <summary>Ortak karar katmanı (M9). Soru anahtarı: JudgeQuestions sabitleri ya da serbest metin (kurallı motor bilmediği soruya cevap veremez; Answer=null).</summary>
public interface IJudge
{
    JudgeResult Decide(string question, IReadOnlyList<string> options, string context);
}

public static class JudgeQuestions
{
    /// <summary>Ajanın son mesajı "bitti" diyor mu? Seçenekler: evet / hayır.</summary>
    public const string ClaimsDone = "claims_done";
    public const string ShowsEvidence = "shows_evidence";
    public const string AsksQuestion = "asks_question";
    /// <summary>Bu cümle kalıcı bir kural/yasak/karar mı? Seçenekler: evet / hayır.</summary>
    public const string IsRule = "is_rule";
    public static readonly string[] YesNo = { "evet", "hayır" };
}

/// <summary>Kurallı hakem (0 ms sınıfı): mevcut işaret çıkarıcıları (SessionStateResolver.Flags, RuleBasedExtractor) sarar.</summary>
public sealed class RulesJudge : IJudge
{
    public double RuleThreshold { get; init; } = 0.60;

    public JudgeResult Decide(string question, IReadOnlyList<string> options, string context)
    {
        var sw = Stopwatch.StartNew();
        string? answer = null; double conf = 0;
        var yes = options.FirstOrDefault(o => o.Equals("evet", StringComparison.OrdinalIgnoreCase) || o.Equals("yes", StringComparison.OrdinalIgnoreCase));
        var no = options.FirstOrDefault(o => o.Equals("hayır", StringComparison.OrdinalIgnoreCase) || o.Equals("hayir", StringComparison.OrdinalIgnoreCase) || o.Equals("no", StringComparison.OrdinalIgnoreCase));
        if (yes is not null && no is not null)
        {
            switch (question)
            {
                case JudgeQuestions.ClaimsDone:
                {
                    var (asks, done, evidence) = SessionStateResolver.Flags(context);
                    var ambiguous = done && asks; // "bitti, devam edeyim mi?"
                    answer = done ? yes : no;
                    conf = ambiguous ? 0.45 : done ? (evidence ? 0.9 : 0.75) : 0.7;
                    break;
                }
                case JudgeQuestions.ShowsEvidence:
                {
                    var (_, done, evidence) = SessionStateResolver.Flags(context);
                    answer = evidence ? yes : no;
                    conf = evidence ? 0.75 : (done ? 0.7 : 0.6);
                    break;
                }
                case JudgeQuestions.AsksQuestion:
                {
                    var (asks, _, _) = SessionStateResolver.Flags(context);
                    answer = asks ? yes : no;
                    conf = asks ? (context.TrimEnd().EndsWith('?') ? 0.9 : 0.65) : 0.75;
                    break;
                }
                case JudgeQuestions.IsRule:
                {
                    var s = new RuleBasedExtractor().Score(context);
                    var isRule = s.Type is not null && s.Score >= RuleThreshold && !s.IsQuestion && !s.TooShort;
                    answer = isRule ? yes : no;
                    // eşiğe yakınsa emin değil
                    conf = Math.Clamp(0.5 + Math.Abs(s.Score - RuleThreshold), 0.5, 0.95);
                    break;
                }
            }
        }
        sw.Stop();
        return new JudgeResult(answer, conf, "rules", sw.ElapsedMilliseconds);
    }
}

/// <summary>
/// İsteğe bağlı model hakemi (varsayılan KAPALI): kullanıcının kendi aboneliğiyle `claude -p --model haiku`; 20 sn zaman aşımı;
/// ortamdan ANTHROPIC_* anahtarları silinir (fatura çıkmasın); başarısızlıkta RulesJudge'e düşer.
/// </summary>
public sealed class ClaudeCliJudge : IJudge
{
    private readonly string _command;
    private readonly IJudge _fallback;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    public ClaudeCliJudge(string command, IJudge fallback) { _command = command; _fallback = fallback; }

    public JudgeResult Decide(string question, IReadOnlyList<string> options, string context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var prompt = BuildPrompt(question, options, context);
            var psi = PlainExplainer.BuildStartInfo(_command); // -p --model haiku, API anahtarı değişkenleri silinmiş
            using var p = new Process { StartInfo = psi };
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.StandardInput.Write(prompt);
            p.StandardInput.Close();
            if (!p.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return Fallback(question, options, context, sw);
            }
            var output = stdout.GetAwaiter().GetResult();
            _ = stderr;
            if (p.ExitCode != 0) return Fallback(question, options, context, sw);
            var answer = Pick(output, options);
            if (answer is null) return Fallback(question, options, context, sw);
            return new JudgeResult(answer, 0.8, "claude-cli", sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return Fallback(question, options, context, sw);
        }
    }

    private JudgeResult Fallback(string question, IReadOnlyList<string> options, string context, Stopwatch sw)
    {
        var r = _fallback.Decide(question, options, context);
        return r with { Engine = "rules(yedek)", Ms = sw.ElapsedMilliseconds };
    }

    public static string BuildPrompt(string question, IReadOnlyList<string> options, string context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aşağıdaki soruyu SADECE verilen seçeneklerden biriyle, tek sözcük olarak cevapla. Açıklama yazma.");
        sb.AppendLine("Soru: " + QuestionText(question));
        sb.AppendLine("Seçenekler: " + string.Join(" / ", options));
        sb.AppendLine("---");
        sb.Append(context.Length > 6000 ? context[..6000] : context);
        return sb.ToString();
    }

    private static string QuestionText(string q) => q switch
    {
        JudgeQuestions.ClaimsDone => "Bu ajan mesajı işin bittiğini iddia ediyor mu?",
        JudgeQuestions.ShowsEvidence => "Bu ajan mesajı iddiasını destekleyen kanıt (komut çıktısı, test sonucu, dosya yolu) gösteriyor mu?",
        JudgeQuestions.AsksQuestion => "Bu ajan mesajı kullanıcıdan bir cevap ya da onay bekliyor mu?",
        JudgeQuestions.IsRule => "Bu cümle kullanıcının kalıcı bir kuralı, yasağı ya da kararı mı?",
        _ => q,
    };

    /// <summary>Çıktıdaki ilk seçeneği (büyük/küçük harf ve Türkçe ı/i duyarsız, sözcük olarak) bulur.</summary>
    public static string? Pick(string output, IReadOnlyList<string> options)
    {
        var text = output.Trim().ToLowerInvariant().Replace('ı', 'i');
        if (text.Length == 0) return null;
        string? best = null; var bestIdx = int.MaxValue;
        foreach (var o in options)
        {
            var key = o.ToLowerInvariant().Replace('ı', 'i');
            var idx = 0;
            while ((idx = text.IndexOf(key, idx, StringComparison.Ordinal)) >= 0)
            {
                var before = idx == 0 || !char.IsLetter(text[idx - 1]);
                var after = idx + key.Length >= text.Length || !char.IsLetter(text[idx + key.Length]);
                if (before && after) { if (idx < bestIdx) { bestIdx = idx; best = o; } break; }
                idx++;
            }
        }
        return best;
    }
}

public static class JudgeFactory
{
    /// <summary>Ayara göre hakem: judgeClaudeEnabled açıksa ClaudeCliJudge (yedek RulesJudge), değilse RulesJudge. Her karar `events` tablosuna yazılır.</summary>
    public static IJudge Create(SozDb db, long? projectId = null, long? jobId = null, string? source = null)
    {
        var s = SozSettings.Load(db);
        IJudge inner = new RulesJudge();
        if (s.Bool("judgeClaudeEnabled")) inner = new ClaudeCliJudge(s.Str("judgeCommand") ?? "claude", inner);
        return new LoggingJudge(inner, db, projectId, jobId, source);
    }
}

/// <summary>Karar günlüğü: events(kind='judge', detail={question, engine, answer, confidence, ms}). Bağlam metni YAZILMAZ.</summary>
public sealed class LoggingJudge : IJudge
{
    private readonly IJudge _inner;
    private readonly SozDb _db;
    private readonly long? _projectId, _jobId;
    private readonly string? _source;
    public LoggingJudge(IJudge inner, SozDb db, long? projectId = null, long? jobId = null, string? source = null) { _inner = inner; _db = db; _projectId = projectId; _jobId = jobId; _source = source; }

    public JudgeResult Decide(string question, IReadOnlyList<string> options, string context)
    {
        var r = _inner.Decide(question, options, context);
        try
        {
            using var c = _db.Open();
            Events.Log(c, "judge", _projectId, JsonSerializer.Serialize(new { question = question.Length > 60 ? question[..60] : question, engine = r.Engine, answer = r.Answer, confidence = Math.Round(r.Confidence, 2), ms = r.Ms, jobId = _jobId, source = _source }));
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        return r;
    }
}
