using Soz.Core.Registry;

namespace Soz.Core.Discovery;

/// <summary>Aranabilir bir nesne. Kimlik kalıcıdır; yön, nesnenin hangi türe (8 yönden birine) ait olduğunu söyler (yoksa null).</summary>
public sealed record DiscoveryItem(string Uid, int? KindDir, string Title, IReadOnlyList<string> Aliases, string? Description, IReadOnlyList<string> Keywords)
{
    public static DiscoveryItem Of(string uid, int? kindDir, string title, string? description = null, IEnumerable<string>? aliases = null, IEnumerable<string>? keywords = null)
        => new(uid, kindDir, title, (aliases ?? Array.Empty<string>()).ToList(), description, (keywords ?? Array.Empty<string>()).ToList());
}

/// <summary>Eşleşme basamakları, büyük olan üstte (repo-4 madde 12: sözcüksel merdiven; aynı basamakta sıra kullanım ağırlığıyla belirlenir).</summary>
public enum MatchTier
{
    None = -1,
    /// <summary>Sorgu boş: hiçbir şey düşmez, hepsi tabanda toplanır.</summary>
    Empty = 0,
    Subsequence = 1,
    Text = 2,
    Contains = 3,
    Acronym = 4,
    WordPrefix = 5,
    Prefix = 6,
    TitleExact = 7,
    AliasExact = 8,
}

public sealed record SearchHit(DiscoveryItem Item, MatchTier Tier, double Weight);

/// <summary>
/// Arama sıralaması (saf fonksiyon). Hem sorgu hem hedef aynı Türkçe katlamadan geçer (<see cref="Fold"/>: I İ ı i hepsi "i", ş ğ ü ö ç sade harf); karşılaştırma Ordinal.
/// Sıra: (1) basamak (takma ad tam, ad tam, ad öneki, sözcük öneki, baş harfler, ad içinde, açıklama/anahtar sözcük, harf dizisi), (2) aynı basamakta kullanım ağırlığı,
/// (3) kısa ad, (4) katlanmış ad, (5) kimlik. Yazı sorgusunda yer değiştirme yoktur; yuvalar bu sıralamadan etkilenmez.
/// </summary>
public static class SearchRanker
{
    /// <summary>Harf dizisi (fuzzy) eşleşmesi için en kısa sorgu: daha kısası gürültü üretir.</summary>
    public const int MinSubsequenceLength = 3;

    public static IReadOnlyList<SearchHit> Rank(string? query, IEnumerable<DiscoveryItem> items, Func<string, double> weightOf, int limit = 50)
    {
        var q = Fold.Text(query).Trim();
        var hits = new List<(SearchHit Hit, string FoldedTitle)>();
        foreach (var it in items)
        {
            var tier = q.Length == 0 ? MatchTier.Empty : Classify(q, it);
            if (tier == MatchTier.None) continue;
            hits.Add((new SearchHit(it, tier, weightOf(it.Uid)), Fold.Text(it.Title)));
        }
        return hits
            .OrderByDescending(h => h.Hit.Tier)
            .ThenByDescending(h => h.Hit.Weight)
            .ThenBy(h => h.FoldedTitle.Length)
            .ThenBy(h => h.FoldedTitle, StringComparer.Ordinal)
            .ThenBy(h => h.Hit.Item.Uid, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .Select(h => h.Hit)
            .ToList();
    }

    private static MatchTier Classify(string q, DiscoveryItem it)
    {
        foreach (var a in it.Aliases) if (Fold.Text(a).Trim() == q) return MatchTier.AliasExact;
        var title = Fold.Text(it.Title);
        if (title == q) return MatchTier.TitleExact;
        if (title.StartsWith(q, StringComparison.Ordinal)) return MatchTier.Prefix;
        var words = Words(title);
        if (words.Any(w => w.StartsWith(q, StringComparison.Ordinal))) return MatchTier.WordPrefix;
        if (q.Length >= 2 && words.Count >= q.Length && new string(words.Select(w => w[0]).ToArray()).StartsWith(q, StringComparison.Ordinal)) return MatchTier.Acronym;
        if (title.Contains(q, StringComparison.Ordinal)) return MatchTier.Contains;
        foreach (var a in it.Aliases) if (Fold.Text(a).Contains(q, StringComparison.Ordinal)) return MatchTier.Contains;
        if (Fold.Contains(it.Description, q) || it.Keywords.Any(k => Fold.Contains(k, q))) return MatchTier.Text;
        if (q.Length >= MinSubsequenceLength && IsSubsequence(q, title)) return MatchTier.Subsequence;
        return MatchTier.None;
    }

    private static List<string> Words(string folded)
    {
        var list = new List<string>();
        var start = -1;
        for (var i = 0; i <= folded.Length; i++)
        {
            var word = i < folded.Length && char.IsLetterOrDigit(folded[i]);
            if (word && start < 0) start = i;
            else if (!word && start >= 0) { list.Add(folded[start..i]); start = -1; }
        }
        return list;
    }

    private static bool IsSubsequence(string q, string target)
    {
        var qi = 0;
        foreach (var ch in target) if (qi < q.Length && ch == q[qi]) qi++;
        return qi == q.Length;
    }
}
