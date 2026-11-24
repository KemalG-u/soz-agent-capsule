using System.Text.RegularExpressions;

namespace Soz.Core.Text;

/// <summary>
/// Kullanıcıya giden cümlelerde sade sözcük (C10): "ajan" yerine "yardımcı", "ölçüt" yerine "kontrol", "kanıtıyla bitti" yerine "deneme sonucuyla bitti".
/// Cümlelerin kaynağında yazılır; bu yardımcı yalnız başka bir katmanın (ör. dikkat listesi) hazır cümlelerini aynı sözcüklere çevirmek içindir. Makine alan adlarına ve neden kodlarına dokunmaz.
/// </summary>
public static class UserWords
{
    private static readonly (Regex re, string to)[] Rules =
    {
        (new Regex(@"\bAjanın", RegexOptions.CultureInvariant), "Yardımcının"), (new Regex(@"\bajanın", RegexOptions.CultureInvariant), "yardımcının"),
        (new Regex(@"\bAjana", RegexOptions.CultureInvariant), "Yardımcıya"), (new Regex(@"\bajana", RegexOptions.CultureInvariant), "yardımcıya"),
        (new Regex(@"\bAjanı", RegexOptions.CultureInvariant), "Yardımcıyı"), (new Regex(@"\bajanı", RegexOptions.CultureInvariant), "yardımcıyı"),
        (new Regex(@"\bAjan(?=lar|dan|da|la|\b)", RegexOptions.CultureInvariant), "Yardımcı"), (new Regex(@"\bajan(?=lar|dan|da|la|\b)", RegexOptions.CultureInvariant), "yardımcı"),
        (new Regex(@"ölçütten", RegexOptions.CultureInvariant), "kontrolden"), (new Regex(@"ölçütte", RegexOptions.CultureInvariant), "kontrolde"),
        (new Regex(@"ölçüt", RegexOptions.CultureInvariant), "kontrol"), (new Regex(@"Ölçüt", RegexOptions.CultureInvariant), "Kontrol"),
        (new Regex(@"kanıtıyla bitti", RegexOptions.CultureInvariant), "deneme sonucuyla bitti"),
    };

    public static string Simplify(string text)
    {
        foreach (var (re, to) in Rules) text = re.Replace(text, to);
        return text;
    }
}
