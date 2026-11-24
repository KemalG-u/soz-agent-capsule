using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Soz.Core.Text;

namespace Soz.Core.Extract;

public static class CandidateTypes
{
    public const string Rule = "rule", Ban = "ban", Decision = "decision", Preference = "preference", Correction = "correction";
    public static readonly string[] All = { Rule, Ban, Decision, Preference, Correction };
    public static readonly string[] DecisionTypes = { Rule, Ban, Decision, Preference };
}

public sealed record ExtractedCandidate(string Type, string Text, double Score);

/// <summary>Bir cümlenin puanlama ayrıntısı (test ve ayar için).</summary>
public sealed record SentenceScore(string? Type, double Score, bool IsQuestion, bool TooShort, IReadOnlyList<string> Reasons)
{
    /// <summary>Kararda kullanılacak tür: düzeltme cümlesi taşıdığı kurala göre rule/ban olur.</summary>
    public bool Negative { get; init; }
}

public interface IDecisionExtractor
{
    IReadOnlyList<ExtractedCandidate> Extract(string message);
}

/// <summary>
/// v1 kural tabanlı ayıklayıcı. Kesinlik geri çağırmadan önemli: aday olmak için kalıcılık/emir/karar işareti gerekir,
/// soru, şikâyet, kısa ve çok uzun cümleler elenir. Kalıplar patterns.tr.json / patterns.en.json içinde.
/// </summary>
public sealed class RuleBasedExtractor : IDecisionExtractor
{
    public const double DefaultFloor = 0.35;
    public double Floor { get; }

    private sealed record Pat(string Type, double W, Regex Re, bool Raw);
    private sealed record Pen(double W, Regex Re);
    private sealed class Lang
    {
        public string Code = "";
        public List<Pat> Patterns = new();
        public List<Pen> Penalties = new();
        public HashSet<string> Imperatives = new();
        public HashSet<string> NegExclusions = new();
        public Regex? Question;
        public HashSet<string> Stopwords = new();
        public HashSet<string> FunctionWords = new();
        public List<string> ResourceNouns = new();
        public HashSet<string> NounDeterminers = new();
    }

    private readonly Lang _tr, _en;
    private static readonly Lazy<(Lang tr, Lang en)> Shared = new(() => (Load("patterns.tr.json"), Load("patterns.en.json")));

    public RuleBasedExtractor(double floor = DefaultFloor)
    {
        Floor = floor;
        (_tr, _en) = Shared.Value;
    }

    /// <summary>Kümeleyicinin durak sözcükleri (iki dil + kalıp işaret sözcükleri).</summary>
    public static IReadOnlySet<string> Stopwords
    {
        get
        {
            var (tr, en) = Shared.Value;
            var s = new HashSet<string>(tr.Stopwords);
            s.UnionWith(en.Stopwords);
            return s;
        }
    }

    private static Lang Load(string name)
    {
        var asm = typeof(RuleBasedExtractor).Assembly;
        var res = asm.GetManifestResourceNames().First(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        using var st = asm.GetManifestResourceStream(res)!;
        using var doc = JsonDocument.Parse(st);
        var r = doc.RootElement;
        var l = new Lang { Code = r.GetProperty("language").GetString() ?? "" };
        var o = RegexSpeed.Compiled | RegexOptions.CultureInvariant;
        foreach (var p in r.GetProperty("patterns").EnumerateArray())
            l.Patterns.Add(new Pat(p.GetProperty("type").GetString()!, p.GetProperty("w").GetDouble(), new Regex(p.GetProperty("re").GetString()!, o),
                p.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.True));
        foreach (var p in r.GetProperty("penalties").EnumerateArray())
            l.Penalties.Add(new Pen(p.GetProperty("w").GetDouble(), new Regex(p.GetProperty("re").GetString()!, o)));
        foreach (var v in r.GetProperty("imperativeVerbs").EnumerateArray()) l.Imperatives.Add(v.GetString()!);
        foreach (var v in r.GetProperty("negImperativeExclusions").EnumerateArray()) l.NegExclusions.Add(v.GetString()!);
        foreach (var v in r.GetProperty("stopwords").EnumerateArray()) l.Stopwords.Add(v.GetString()!);
        if (r.TryGetProperty("functionWords", out var fw)) foreach (var v in fw.EnumerateArray()) l.FunctionWords.Add(v.GetString()!);
        if (r.TryGetProperty("nounDeterminers", out var nd)) foreach (var v in nd.EnumerateArray()) l.NounDeterminers.Add(v.GetString()!);
        if (r.TryGetProperty("resourceNouns", out var rn)) foreach (var v in rn.EnumerateArray()) l.ResourceNouns.Add(v.GetString()!);
        l.Question = new Regex(r.GetProperty("questionRe").GetString()!, o);
        return l;
    }

    public IReadOnlyList<ExtractedCandidate> Extract(string message)
    {
        var list = new List<ExtractedCandidate>();
        var seen = new HashSet<string>();
        foreach (var sentence in SplitSentences(message))
        {
            var sc = Score(sentence);
            if (sc.Type is null || sc.Score < Floor) continue;
            if (SecretFilter.ContainsSecret(sentence)) continue; // sır içeren cümle öneri adayı olmaz
            if (!seen.Add(TextNormalizer.Fold(sentence))) continue;
            list.Add(new ExtractedCandidate(sc.Type, sentence, Math.Round(sc.Score, 3)));
        }
        return list;
    }

    // ---------- Cümlelere bölme ----------

    private static readonly Regex SentenceSplit = new(@"(?<=[.!?…])\s+(?=\S)", RegexSpeed.Compiled);
    private static readonly Regex CommaSplit = new(@"(?<=[,;:])\s+", RegexSpeed.Compiled);
    private static readonly Regex BulletPrefix = new(@"^\s*([-*•]|\d+[.)])\s+", RegexSpeed.Compiled);
    private static readonly Regex CodeLike = new(@"(^\s*(at |File ""|Traceback|\$ |PS [A-Z]:\\|>>>|[{}\[\]]\s*$))|(;\s*$)|(=>)|(\b(var|const|let|function|public|private|class|import|return|def)\b.*[({=])|(\b\d{2}:\d{2}:\d{2}\b)|(\b(Error|Exception|WARN|INFO|DEBUG)\b[:\]])", RegexSpeed.Compiled);
    // Alıntılayıp cevap verme: "<asistanın cümlesi>.: <kullanıcının cevabı>" → yalnız cevap kullanıcınındır.
    private static readonly Regex QuotePrefix = new(@"^.{4,600}?[.?!…]\s*:\s*", RegexSpeed.Compiled);
    private static readonly Regex BoxDrawing = new(@"[─-╿]", RegexSpeed.Compiled);
    private static readonly Regex HeavyPathOrUrl = new(@"(https?://\S+)|([A-Za-z]:\\\S+)|(\S+/\S+/\S+)", RegexSpeed.Compiled);

    /// <summary>Mesajı aday cümlelere böler; kod blokları, günlük satırları, 400 karakterden uzun tek satırlar ve yapıştırmalar atlanır.</summary>
    public static IEnumerable<string> SplitSentences(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) yield break;
        var text = TextNormalizer.RemovePasted(TextNormalizer.CleanUserText(message));
        var lines = text.Split('\n');

        // Yapıştırılmış rapor sezgisi: uzun ve yapılı (başlık/tablo/çok madde) mesajda yalnız ilk ve son paragraf kullanıcınındır.
        var structured = lines.Count(l => l.StartsWith('#') || l.StartsWith('|') || l.StartsWith("```"));
        var bullets = lines.Count(l => BulletPrefix.IsMatch(l));
        var keep = new bool[lines.Length];
        if (text.Length > 1500 && (structured >= 3 || bullets >= 6))
        {
            for (var i = 0; i < lines.Length && lines[i].Length > 0; i++) keep[i] = true;
            for (var i = lines.Length - 1; i >= 0 && lines[i].Length > 0; i--) keep[i] = true;
        }
        else Array.Fill(keep, true);

        var inCode = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```")) { inCode = !inCode; continue; }
            if (inCode || !keep[i]) continue;
            if (line.Length == 0 || line.Length > 400) continue;
            if (line.StartsWith('#') || line.StartsWith('|') || line.StartsWith('>')) continue; // başlık, tablo, alıntı
            if (CodeLike.IsMatch(line)) continue;
            if (BoxDrawing.IsMatch(line)) continue; // tablo/ağaç çizimi yapıştırması
            var q = QuotePrefix.Match(line);
            if (q.Success) line = line[q.Length..];
            if (line.Trim().Length == 0) continue;
            line = BulletPrefix.Replace(line, "");
            if (line.StartsWith("**")) continue; // kalın başlıklı yapıştırılmış madde
            foreach (var raw in SentenceSplit.Split(line))
            {
                var s = raw.Trim().Trim('"', '\'', '“', '”', '«', '»').Trim();
                if (s.Length == 0) continue;
                if (s.Length > 160)
                {
                    foreach (var part in CommaSplit.Split(s))
                    {
                        var p = part.Trim().TrimEnd(',', ';', ':').Trim();
                        if (p.Length > 0) yield return p;
                    }
                }
                else yield return s;
            }
        }
    }

    // ---------- Puanlama ----------

    private static readonly Regex Word = new(@"[a-z0-9']+", RegexSpeed.Compiled);
    private static readonly HashSet<string> EnHints = new() { "the", "you", "to", "and", "is", "it", "please", "always", "never", "don't", "dont", "do", "not", "we", "i", "this", "that", "with", "for", "use", "make", "sure" };
    private static readonly HashSet<string> TrHints = new() { "ve", "bu", "bir", "de", "da", "icin", "ile", "ama", "cok", "sen", "ben", "ne", "her", "bunu", "olarak", "gibi", "daha", "kadar", "artik", "hep", "asla" };

    // yazsın, kullansın, etsin (3. tekil emir); yapıyorsun/yaparsın/yapacaksın değil.
    private static readonly Regex ThirdPersonImperative = new(@"^\w+[^aeiouyrks](sin|sun)$", RegexSpeed.Compiled);

    private static readonly Regex ClauseSplit = new(@"[,;:]", RegexSpeed.Compiled);
    private static readonly Regex PossessiveDative = new(@"(im|in|um|un|imiz|iniz)(a|e)$", RegexSpeed.Compiled);
    private static readonly Regex ShouldHave = new(@"(acaktin|ecektin|acaktiniz|ecektiniz)$", RegexSpeed.Compiled);
    private static readonly Regex Accusative = new(@"\w{2,}(ni|nu|yi|yu|lari|leri)$", RegexSpeed.Compiled);
    private static readonly HashSet<string> NotParticiple = new() { "zaman", "hemen", "neden", "insan", "sonra", "ozellikle", "yeniden", "asla", "tamamen", "gercekten", "ben", "sen", "en" };

    /// <summary>Sıfat-fiil (izleyen, çalışan, gelen): ardındaki -ma/-me sözcüğü emir değil addır. Ayrılma hâli (-dan/-den) sayılmaz.</summary>
    private static bool IsParticiple(string w)
        => w.Length >= 4 && (w.EndsWith("en") || w.EndsWith("an")) && !NotParticiple.Contains(w)
           && !(w.EndsWith("dan") || w.EndsWith("den") || w.EndsWith("tan") || w.EndsWith("ten") || w.EndsWith("man") || w.EndsWith("men"));

    private static double NoisyOr(double a, double b) => 1 - (1 - a) * (1 - b);

    public SentenceScore Score(string sentence)
    {
        var reasons = new List<string>();
        var f = TextNormalizer.Fold(sentence).Trim();
        var rawLower = TextNormalizer.TurkishLower(sentence);
        var words = Word.Matches(f).Select(m => m.Value).ToList();
        int en = words.Count(EnHints.Contains), tr = words.Count(TrHints.Contains);
        var isEnglish = en > tr && en >= 2;
        var lang = isEnglish ? _en : _tr;

        var isQuestion = lang.Question!.IsMatch(f) || (!isEnglish && f.Contains('?'));
        if (isQuestion) return new SentenceScore(null, 0, true, false, new[] { "soru" });
        // Sözleşme: en az 4 anlamlı sözcük (bağlaç/edat gibi işlev sözcükleri sayılmaz).
        var meaningful = words.Count(w => w.Length > 1 && !_tr.FunctionWords.Contains(w));
        if (meaningful < 4) return new SentenceScore(null, 0, false, true, new[] { "kısa" });

        var w = new Dictionary<string, double> { ["rule"] = 0, ["ban"] = 0, ["decision"] = 0, ["preference"] = 0, ["correction"] = 0 };
        double durable = 0;
        foreach (var l in new[] { _tr, _en })
        {
            foreach (var p in l.Patterns)
            {
                if (!p.Re.IsMatch(p.Raw ? rawLower : f)) continue;
                reasons.Add($"{p.Type}:{p.Re}");
                if (p.Type == "durable") durable = NoisyOr(durable, p.W);
                else w[p.Type] = NoisyOr(w[p.Type], p.W);
            }
        }

        // Türkçe olumsuz emir: cümlenin ya da virgülle ayrılmış bir yan cümlenin son sözcüğü -ma/-me (ad istisnaları hariç).
        var lastWord = words.Count > 0 ? words[^1] : "";
        var lastTwo = words.Count > 1 ? words[^2] + " " + words[^1] : lastWord;
        var negImperative = false;
        if (!isEnglish)
        {
            var clausesTr = ClauseSplit.Split(f);
            // "ses alma, işleme, yazma": tek sözcüklü -ma/-me yan cümleleri bir ad listesidir, emir değil.
            var nounList = clausesTr.Count(cl => { var x = Word.Matches(cl); return x.Count == 1 && (x[0].Value.EndsWith("ma") || x[0].Value.EndsWith("me")); }) >= 1
                           && clausesTr.Length >= 3;
            foreach (var clause in nounList ? [] : clausesTr)
            {
                var cw = Word.Matches(clause).Select(m => m.Value).ToList();
                if (cw.Count < 2) continue;
                var lw = cw[^1];
                var l2 = cw[^2] + " " + lw;
                // Önündeki sözcük sıfat/belirleyici ya da sıfat-fiilse (-en/-an) bu bir addır: "iki küçük bekleme", "izleyen bekleme".
                var prev = cw[^2];
                var nounLike = _tr.NounDeterminers.Contains(prev) || IsParticiple(prev) || char.IsDigit(prev[0])
                               || (PossessiveDative.IsMatch(lw) && !_tr.Imperatives.Contains(lw[..^2])); // "işime", "kendine": iyelik + yönelme
                // İstisna listesindeki ad biçimi (okuma, yazma…) belirtme hâlindeki bir nesneden sonra gelirse yine emirdir: "dosyalarını okuma".
                var excluded = _tr.NegExclusions.Contains(lw) || _tr.NegExclusions.Contains(l2);
                if (excluded && Accusative.IsMatch(prev)) excluded = false;
                if ((lw.EndsWith("ma") || lw.EndsWith("me")) && lw.Length >= 4 && !nounLike && !excluded)
                { negImperative = true; break; }
            }
            if (negImperative)
            {
                w["ban"] = NoisyOr(w["ban"], 0.5);
                reasons.Add("ban:olumsuz-emir");
            }
        }

        // Emir kipi: Türkçede sondaki fiil, İngilizcede baştaki fiil.
        var imperative = false;
        if (isEnglish)
        {
            foreach (var clause in ClauseSplit.Split(f))
            {
                var cw = Word.Matches(clause).Select(m => m.Value).ToList();
                if (cw.Count == 0) continue;
                if (lang.Imperatives.Contains(cw[0]) || cw[0] is "always" or "never" or "don't" or "dont" || (cw[0] == "do" && cw.Count > 1 && cw[1] == "not"))
                { imperative = true; break; }
            }
        }
        else imperative = negImperative || _tr.Imperatives.Contains(lastWord) || _tr.Imperatives.Contains(lastTwo)
                          || (lastWord.Length > 4 && (lastWord.EndsWith("in") || lastWord.EndsWith("un")) && _tr.Imperatives.Contains(lastWord[..^2]))
                          || ThirdPersonImperative.IsMatch(lastWord)
                          || ShouldHave.IsMatch(lastWord); // "yazacaktın": yapılması gereken bir kuralı hatırlatır

        var negative = w["ban"] > 0;
        if (durable > 0)
        {
            if (negative) w["ban"] = NoisyOr(w["ban"], durable);
            else w["rule"] = NoisyOr(w["rule"], durable);
        }

        // Yasak bir kaynağa (klasör, dosya, süreç, secret…) dokunuyorsa daha olası bir kalıcı yasaktır.
        if (negative && (_tr.ResourceNouns.Concat(_en.ResourceNouns)).Any(n => words.Any(x => x.StartsWith(n, StringComparison.Ordinal))))
        {
            w["ban"] = NoisyOr(w["ban"], 0.2);
            reasons.Add("ban:kaynak");
        }

        var best = CandidateTypes.DecisionTypes.OrderByDescending(t => w[t]).First();
        if (imperative && w[best] > 0 && best is "rule" or "ban")
        {
            w[best] = NoisyOr(w[best], 0.25);
            reasons.Add("emir");
        }
        var corr = w["correction"];
        string type;
        double score;
        if (w[best] < 0.25 && corr >= 0.5)
        {
            type = CandidateTypes.Correction;
            // Yalnız sitem ("defalarca söylemiştim") deftere girecek bir kural taşımaz; emir yoksa yarı puan.
            score = imperative ? NoisyOr(corr, 0.2) : corr * 0.5;
        }
        else
        {
            type = best;
            score = NoisyOr(w[best], corr * 0.5);
        }
        if (score <= 0) return new SentenceScore(null, 0, false, false, reasons) { Negative = negative };

        double pen = 0;
        foreach (var l in new[] { _tr, _en })
            foreach (var p in l.Penalties)
                if (p.Re.IsMatch(f)) { pen += p.W; reasons.Add($"ceza:{p.Re}"); }
        var lastIsPast = !isEnglish && Regex.IsMatch(lastWord, "(dim|dum|tim|tum|dik|duk|tik|tuk)$") && w["decision"] < 0.4 && corr < 0.5;
        if (lastIsPast) { pen += 0.2; reasons.Add("ceza:geçmiş-anlatı"); }
        if (sentence.Length > 220) pen += sentence.Length > 320 ? 0.35 : 0.15;
        if (HeavyPathOrUrl.IsMatch(sentence)) pen += 0.1;
        score = Math.Max(0, score - Math.Min(pen, 0.6));
        return new SentenceScore(type, score, false, false, reasons) { Negative = negative };
    }

    /// <summary>Aday türünden karar türü: düzeltme cümlesi taşıdığı kurala göre kural ya da yasak olur.</summary>
    public static string DecisionTypeFor(string candidateType, bool negative)
        => candidateType == CandidateTypes.Correction ? (negative ? CandidateTypes.Ban : CandidateTypes.Rule) : candidateType;
}
