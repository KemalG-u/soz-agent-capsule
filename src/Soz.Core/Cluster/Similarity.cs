using System.Text;
using System.Text.RegularExpressions;
using Soz.Core.Extract;
using Soz.Core.Text;

namespace Soz.Core.Cluster;

/// <summary>Bir cümlenin benzerlik özellikleri: kaba kökler (ilk 5 harf) ve karakter üçlüsü vektörü.</summary>
public sealed class TextFeature
{
    public required string[] Stems { get; init; }
    public required Dictionary<string, double> Trigrams { get; init; }
    public double TrigramNorm { get; init; }
    /// <summary>Virgül/noktalı virgülle ayrılmış yan cümlelerin özellikleri (en az iki kökü olanlar; tek yan cümlede boş).</summary>
    public TextFeature[] Clauses { get; init; } = [];
}

/// <summary>Sözleşme 3.3: kök kümelerinde ağırlıklı Jaccard + karakter üçlüsü kosinüsünün ortalaması.</summary>
public static class Similarity
{
    private static readonly Regex Word = new(@"[a-z0-9]+", RegexSpeed.Compiled);
    private static readonly Lazy<IReadOnlySet<string>> Stop = new(() => RuleBasedExtractor.Stopwords);

    private static readonly Regex ClauseSplit = new(@"[,;:]", RegexSpeed.Compiled);

    public static TextFeature Feature(string text)
    {
        var whole = Plain(TextNormalizer.Fold(text));
        var parts = ClauseSplit.Split(TextNormalizer.Fold(text)).Where(p => p.Trim().Length > 0).ToList();
        if (parts.Count < 2) return whole;
        var clauses = parts.Select(Plain).Where(c => c.Stems.Length >= 2).ToArray();
        return new TextFeature { Stems = whole.Stems, Trigrams = whole.Trigrams, TrigramNorm = whole.TrigramNorm, Clauses = clauses };
    }

    private static TextFeature Plain(string f)
    {
        var words = Word.Matches(f).Select(m => m.Value).Where(w => w.Length > 1 && !Stop.Value.Contains(w)).ToList();
        var stems = words.Select(w => w.Length > 5 ? w[..5] : w).Distinct().ToArray();
        var joined = " " + string.Join(' ', words) + " ";
        var tri = new Dictionary<string, double>();
        for (var i = 0; i + 3 <= joined.Length; i++)
        {
            var g = joined.Substring(i, 3);
            tri[g] = tri.GetValueOrDefault(g) + 1;
        }
        var norm = Math.Sqrt(tri.Values.Sum(v => v * v));
        return new TextFeature { Stems = stems, Trigrams = tri, TrigramNorm = norm };
    }

    private static bool StemMatch(string a, string b)
    {
        if (a == b) return true;
        var min = Math.Min(a.Length, b.Length);
        if (min < 3) return false;
        return string.CompareOrdinal(a, 0, b, 0, min) == 0;
    }

    public static double WeightedJaccard(string[] a, string[] b, Func<string, double>? weight = null)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        weight ??= _ => 1.0;
        double inter = 0, wa = 0, wb = 0;
        foreach (var x in a) wa += weight(x);
        foreach (var y in b) wb += weight(y);
        var used = new bool[b.Length];
        foreach (var x in a)
        {
            for (var j = 0; j < b.Length; j++)
            {
                if (used[j] || !StemMatch(x, b[j])) continue;
                used[j] = true;
                inter += Math.Min(weight(x), weight(b[j]));
                break;
            }
        }
        var union = wa + wb - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public static double Cosine(TextFeature a, TextFeature b)
    {
        if (a.TrigramNorm == 0 || b.TrigramNorm == 0) return 0;
        var (small, big) = a.Trigrams.Count <= b.Trigrams.Count ? (a, b) : (b, a);
        double dot = 0;
        foreach (var (k, v) in small.Trigrams)
            if (big.Trigrams.TryGetValue(k, out var v2)) dot += v * v2;
        return dot / (a.TrigramNorm * b.TrigramNorm);
    }

    private static double Pair(TextFeature a, TextFeature b, Func<string, double>? weight)
        => (WeightedJaccard(a.Stems, b.Stems, weight) + Cosine(a, b)) / 2.0;

    /// <summary>Bütün cümle ile yan cümlelerin en iyi eşleşmesi: "X'i asla çalıştırma, yalnız oku" ile "Asla X'i çalıştırma" aynı yasağı taşır.</summary>
    public static double Score(TextFeature a, TextFeature b, Func<string, double>? weight = null)
    {
        var best = Pair(a, b, weight);
        if (a.Clauses.Length == 0 && b.Clauses.Length == 0) return best;
        foreach (var x in a.Clauses.Length > 0 ? a.Clauses : [a])
            foreach (var y in b.Clauses.Length > 0 ? b.Clauses.Append(b) : [b])
                best = Math.Max(best, Pair(x, y, weight));
        foreach (var y in b.Clauses) best = Math.Max(best, Pair(a, y, weight));
        return best;
    }

    public static double Score(string a, string b) => Score(Feature(a), Feature(b));

    /// <summary>Ters dizin anahtarı: kökün ilk 3 harfi.</summary>
    public static IEnumerable<string> IndexKeys(TextFeature f) => f.Stems.Where(s => s.Length >= 3).Select(s => s[..3]).Distinct();
}
