using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Soz.Core.Text;

/// <summary>Metin normalleştirme. Kanca ve okuyucu AYNI işlevleri çağırır (sözleşme 12.6).</summary>
public static partial class TextNormalizer
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    // Düzenli ifadeler derleme zamanında üretilir ([GeneratedRegex]); RegexOptions.Compiled her süreç başlangıcında (kanca) ifade başına ~2,6 ms tutuyordu.
    // Kalıp ve seçenekler eskisiyle birebir aynıdır (bir test eski yorumlayıcı ifadeleriyle karşılaştırır).

    // İçeriğiyle birlikte atılan bloklar: kullanıcının sözü değil, araç/ortam enjeksiyonu.
    [GeneratedRegex(@"<(system-reminder|environment_context|user_instructions|codex_internal_context|in-app-browser-context|external_codex_apps_open_page|local-command-stdout|local-command-stderr|bash-stdout|bash-stderr)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex DropBlocks { get; }

    // Araç/oturum enjeksiyonu (kullanıcının sözü değil): içeriğiyle birlikte atılır. Kanca ve okuyucu aynı kuralı kullanır.
    private const string InjectedTags = @"task-notification|system-reminder|local-command-[\w-]+|command-name|command-message|command-args|bash-input|bash-stdout|bash-stderr|agent-message[\w-]*|teammate-message|cross-session[\w-]*|user-prompt-submit-hook|session-start-hook";

    [GeneratedRegex(@"<(" + InjectedTags + @")\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex InjectedBlocks { get; }

    [GeneratedRegex(@"<(" + InjectedTags + @")\b[^>]*/>", RegexOptions.CultureInvariant)]
    private static partial Regex InjectedSelfClosing { get; }

    // Kapanışı olmayan (kesilmiş) blok: yalnız satır başındaysa sona kadar atılır.
    [GeneratedRegex(@"(?m)^[ \t]*<(" + InjectedTags + @")\b[^>]*>[\s\S]*$", RegexOptions.CultureInvariant)]
    private static partial Regex InjectedUnclosed { get; }

    // "[SYSTEM NOTIFICATION ..." satırı ve bağlı olduğu paragraf (boş satıra kadar).
    [GeneratedRegex(@"(?m)^[ \t]*\[SYSTEM NOTIFICATION[^\n]*(?:\n(?![ \t]*\n)[^\n]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex SystemNotice { get; }

    // Yalnız etiketi atılan (içeriği kullanıcıya ait olabilen) sarmalayıcılar.
    [GeneratedRegex(@"</?(realtime_delegation|send_user_message_question_reply|command-message|command-args|command-name|bash-input)\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex WrapperTags { get; }

    [GeneratedRegex(@"<pasted_content\b[^>]*>[\s\S]*?</pasted_content[^>]*>")]
    private static partial Regex PastedBlock { get; }
    [GeneratedRegex(@"[ \t\f\v\u00A0]+")]
    private static partial Regex HSpace { get; }
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines { get; }

    /// <summary>Kullanıcı metnini temizler: gömülü sistem blokları ve sarmalayıcı etiketler atılır, boşluk düzenlenir.</summary>
    public static string CleanUserText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (s.TrimStart().StartsWith("[SYSTEM NOTIFICATION", StringComparison.Ordinal)) return ""; // baştan sona sistem bildirimi
        s = DropBlocks.Replace(s, "");
        s = InjectedBlocks.Replace(s, "");
        s = InjectedSelfClosing.Replace(s, "");
        s = InjectedUnclosed.Replace(s, "");
        s = SystemNotice.Replace(s, "");
        s = WrapperTags.Replace(s, "");
        s = HSpace.Replace(s, " ");
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].Trim();
        s = string.Join('\n', lines);
        s = ManyNewlines.Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>Özetleme için: tek kimlik normalleştirmesi (= CleanUserText).</summary>
    public static string ForHash(string? text) => CleanUserText(text);

    public static string Hash(string? text)
    {
        var norm = ForHash(text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm))).ToLowerInvariant();
    }

    /// <summary>Yapıştırılmış içerik bloklarını atar (ayıklayıcı yalnız kullanıcının kendi cümlelerine bakar).</summary>
    public static string RemovePasted(string text) => PastedBlock.Replace(text, "\n");

    [GeneratedRegex(@"^/[A-Za-z][\w:.-]*(\s|$)")]
    private static partial Regex SlashCommand { get; }

    /// <summary>"/compact", "/hafta …" gibi eğik çizgi komutları kullanıcının sözü sayılmaz.</summary>
    public static bool IsSlashCommand(string text) => SlashCommand.IsMatch(text);

    public static string TurkishLower(string s) => s.ToLower(Tr);

    /// <summary>Türkçe küçük harf + ASCII katlama (ç→c, ğ→g, ı→i, ö→o, ş→s, ü→u, şapkalılar). Arama ve kalıp eşleme için.</summary>
    public static string Fold(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var lower = s.ToLower(Tr);
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            sb.Append(ch switch
            {
                'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u',
                'â' => 'a', 'î' => 'i', 'û' => 'u', 'é' => 'e', 'á' => 'a', 'à' => 'a', 'è' => 'e',
                '’' or '‘' or '`' or '´' => '\'',
                '“' or '”' or '«' or '»' => '"',
                '\u0307' => '\0', // birleşik nokta (İ→i̇ durumunda)
                _ => ch,
            });
        }
        return sb.Replace("\0", "").ToString();
    }

    /// <summary>Görüntü için kısaltır (sözcük ortasında kesmeden, …).</summary>
    public static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Replace('\n', ' ').Trim();
        if (t.Length <= max) return t;
        var cut = t.LastIndexOf(' ', Math.Max(0, max - 1), Math.Min(max, 40));
        if (cut < max / 2) cut = max - 1;
        return t[..cut].TrimEnd() + "…";
    }

    /// <summary>Proje anahtarı: küçük harf, eğik çizgiler '\' olarak, sondaki ayırıcı ve \\?\ öneki atılmış.</summary>
    public static string ProjectKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(bilinmiyor)";
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        while (p.Length > 3 && p.EndsWith('\\')) p = p[..^1];
        return p.ToLowerInvariant();
    }

    public static string ProjectName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(bilinmiyor)";
        var p = path.Trim().Replace('/', '\\').TrimEnd('\\');
        var name = p.Split('\\').LastOrDefault(x => x.Length > 0) ?? p;
        return name.Length == 0 ? p : name;
    }
}
