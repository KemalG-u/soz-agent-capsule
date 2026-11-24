using System.Text.RegularExpressions;

namespace Soz.Core.Run;

/// <summary>
/// Başlatılacak komut: dosya adı + öncesinde gelen sabit argümanlar (ör. node betik.mjs) + .cmd sarmalayıcısının ayarladığı
/// ortam değişkenleri (Env, boş olabilir; sarmalayıcı atlanınca kaybolmasın).
/// </summary>
public sealed record LaunchCommand(string FileName, IReadOnlyList<string> PrefixArgs, string Display, IReadOnlyDictionary<string, string>? Env = null);

/// <summary>Model adı doğrulaması (denetim 2, K-3): yalnız harf, rakam ve . _ : - [ ] ile en çok 64 karakter. Kabuk karakteri geçmez.</summary>
public static class ModelName
{
    private static readonly Regex Valid = new(@"^[A-Za-z0-9._:\-\[\]]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static bool IsValid(string? name) => name is not null && Valid.IsMatch(name);

    /// <summary>Boş değer geçerlidir (varsayılan model). Geçersizse sade hata cümlesi, geçerliyse null.</summary>
    public static string? Check(string? name)
        => string.IsNullOrWhiteSpace(name) || IsValid(name.Trim()) ? null : "Model adı geçersiz. Yalnız harf, rakam ve . _ : - [ ] kullanılabilir (en çok 64 karakter).";
}

/// <summary>
/// `claude` komutunu çözer (genel çözümleme: <see cref="Soz.Core.Agents.AgentLocator"/>). Sıra: ayar (claudePath) → PATH üzerinde claude.
/// .exe doğrudan çalışır. npm'in .cmd/.bat kısayolu cmd.exe İLE ÇALIŞTIRILMAZ (kabuk enjeksiyonu yolu): kısayolun içinden gerçek .exe ya da node + cli.js çözülür;
/// çözülemezse başlatılamadı hatası verilir ("Claude yolunu Ayarlar'dan .exe olarak ver"). .mjs/.js betiği node ile çalışır (sınama için sahte claude).
/// </summary>
public static class ClaudeLocator
{
    public const string NotFoundText = "Claude Code bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et.";
    public const string ShimText = "Claude Code açılamadı. Ayarlar'dan yolunu kontrol et.";

    public static LaunchCommand? Resolve(string? configured) => ResolveDetailed(configured).Cmd;

    public sealed record Resolution(LaunchCommand? Cmd, string? Error);

    public static Resolution ResolveDetailed(string? configured)
    {
        var r = Soz.Core.Agents.AgentLocator.Resolve(configured, "claude", NotFoundText, ShimText);
        return new Resolution(r.Cmd, r.Error);
    }

    internal static string? ShimTarget(string cmdFile) => Soz.Core.Agents.AgentLocator.ShimTarget(cmdFile);
}
