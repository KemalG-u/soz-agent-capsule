using System.Globalization;
using System.Text;

namespace Soz.Core.Registry;

/// <summary>
/// Türkçe arama için harf katlama, kültürden bağımsız. I, İ, ı, i hepsi "i"; ş ğ ü ö ç ve noktalı/şapkalı harfler sade harfe iner.
/// `ToLowerInvariant` tek başına yetmez: "I" küçülünce "i" olur ama "ı" ile "İ" ayrı kalır, "KIRMIZI" ile "kırmızı" eşleşmez.
/// Yarım vekil karakter (bozuk UTF-16) gelirse çökmez: o karakter U+FFFD olur.
/// </summary>
public static class Fold
{
    public static string Text(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string decomposed;
        try { decomposed = s.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { decomposed = ReplaceLoneSurrogates(s).Normalize(NormalizationForm.FormD); }
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue; // ş = s + çengel, İ = I + nokta
            sb.Append(ch is 'I' or 'İ' or 'ı' ? 'i' : char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string ReplaceLoneSurrogates(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); }
            else if (char.IsSurrogate(c)) sb.Append('\uFFFD');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Katlanmış metin, katlanmış aranan parçayı içeriyor mu? Boş aranan her şeyle eşleşir.</summary>
    public static bool Contains(string? haystack, string? needle) => Text(haystack).Contains(Text(needle), StringComparison.Ordinal);
}
