using System.Text;

namespace Soz.Core.Proof;

/// <summary>
/// İş klasörde bir değişiklik istiyor mu? Yardımcı "bitti" dediği halde klasörde hiçbir şey değişmediyse bu bir başarı değildir,
/// ama yalnız okuma ya da soru türü işte (incele, açıkla, listele) değişiklik olmaması DOĞRUDUR. Ayrımı işin metninden ve türünden yaparız.
/// Üç sonuç: Expected (değişiklik bekleniyor), ReadOnly (beklenmiyor), Unknown (ayıramadım). Unknown "geçti" üretmez: emin olamadığımız yerde yanlış yeşil vermeyiz.
/// Eşleşme sözcük başına ve ASCII'ye indirgenmiş (ş=s, ı=i...) yapılır; değişiklik fiili bir okuma fiilinden önce gelir (örn. "incele ve düzelt" = değişiklik).
/// </summary>
public static class ChangeExpectation
{
    public enum Kind { Expected, ReadOnly, Unknown }

    // Kök + serbest ek (uzun kökler). Kısa sözcükler (sil, ekle, add...) ayrı, kesin listede.
    private static readonly string[] ChangeStems =
    {
        "duzelt", "degistir", "guncelle", "olustur", "kaldir", "tasi", "adlandir", "uygula", "gelistir", "onar", "iyilestir", "duzenle", "cevir", "yeniden yaz",
        "implement", "refactor", "rename", "rewrite", "replace", "install", "update", "create", "remove", "delete", "change", "modify", "patch", "migrate", "build",
        "yazim", "temizle", "birlestir", "ayir", "optimize", "kopyala", "uret", "ekleyin", "ekle", "cikar", "ayarla", "tamamla", "bitir", "kodla", "programla",
    };
    private static readonly HashSet<string> ChangeExact = new(StringComparer.Ordinal)
    {
        "sil", "silin", "silme", "silsin", "yap", "yapin", "koy", "kur", "coz", "boz", "fix", "add", "make", "edit", "move",
        "yapsin", "eklesin", "ekler", "tasin", "duzeltin",
    };
    private static readonly HashSet<string> WeakChange = new(StringComparer.Ordinal) { "yaz", "yazin", "yazdir", "write" };
    private static readonly string[] ReadStems =
    {
        "oku", "incele", "acikla", "anlat", "ozetle", "listele", "goster", "analiz", "denetle", "karsilastir", "arastir", "ogren", "kontrol", "calistir", "dogrula", "sinama",
        "explain", "review", "summar", "describe", "analy", "audit", "inspect", "investigate", "compare", "verify", "rapor",
    };
    private static readonly HashSet<string> ReadExact = new(StringComparer.Ordinal)
    {
        "bul", "bak", "dene", "list", "show", "check", "run", "tell", "report", "nedir", "neden", "nasil", "nerede", "hangi", "kim", "ne", "kac", "find", "what", "why", "how", "where", "which", "who", "read", "look",
    };

    /// <summary>kind: iş türü ("arastirma" salt okuma sayılır; varsayılan "kod" hiçbir şey söylemez). text: işin ham metni.</summary>
    public static Kind Classify(string? text, string? kind = null)
    {
        if (string.Equals(kind, "arastirma", StringComparison.OrdinalIgnoreCase)) return Kind.ReadOnly;
        if (string.IsNullOrWhiteSpace(text)) return Kind.Unknown;
        var words = Tokens(text);
        var change = false;
        var read = false;
        var weak = false; // "yaz": cevabı sohbete yazmak da olabilir, dosya da; tek başına değişiklik sayılmaz
        foreach (var w in words)
        {
            if (WeakChange.Contains(w)) weak = true;
            else if (ChangeExact.Contains(w) || ChangeStems.Any(s => w.StartsWith(s, StringComparison.Ordinal) && w.Length <= s.Length + 6)) change = true;
            else if (ReadExact.Contains(w) || ReadStems.Any(s => w.StartsWith(s, StringComparison.Ordinal) && w.Length <= s.Length + 6)) read = true;
        }
        if (text.Contains("yeniden yaz", StringComparison.OrdinalIgnoreCase)) change = true;
        if (change) return Kind.Expected;
        if (weak) return read ? Kind.Unknown : Kind.Expected;
        if (read || text.TrimEnd().EndsWith('?')) return Kind.ReadOnly;
        return Kind.Unknown;
    }

    /// <summary>Klasörde HİÇBİR değişiklik yokken kullanıcıya giden tek cümle. claimedWork: yardımcı iş yaptığını söyledi mi. Unknown için "bilemedim" cümlesi.</summary>
    public static string NoChangeSentence(Kind k, bool claimedWork)
    {
        if (k == Kind.Unknown)
            return "Klasörde hiçbir değişiklik yok. İşin klasörde değişiklik isteyip istemediğini bilemedim, bu yüzden \"geçti\" demiyorum.";
        return claimedWork
            ? "Yardımcı iş yaptığını söyledi ama klasörde hiçbir değişiklik yok. Bu iş yapılmamış görünüyor."
            : "Klasörde hiçbir değişiklik yok. İş yapılmamış görünüyor.";
    }

    private static List<string> Tokens(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                'İ' or 'I' or 'ı' => 'i', 'Ş' or 'ş' => 's', 'Ğ' or 'ğ' => 'g', 'Ü' or 'ü' => 'u', 'Ö' or 'ö' => 'o', 'Ç' or 'ç' => 'c', 'Â' or 'â' => 'a', 'Î' or 'î' => 'i', 'Û' or 'û' => 'u',
                _ => char.ToLowerInvariant(ch),
            });
        }
        var list = new List<string>();
        var cur = new StringBuilder();
        foreach (var ch in sb.ToString())
        {
            if (char.IsLetterOrDigit(ch)) cur.Append(ch);
            else if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }
}
