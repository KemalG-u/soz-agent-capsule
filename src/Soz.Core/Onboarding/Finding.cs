using System.Text.Json.Nodes;

namespace Soz.Core.Onboarding;

/// <summary>"Nerede gördüm" satırı: ne gördüm, hangi yerde (ev klasörü "~"), ne zaman değişmiş. İçerik değil, varlık bilgisidir.</summary>
public sealed record Evidence(string What, string Where, string? When = null);

/// <summary>Kartın sabit altı satırı (kabuk-8 "Seni böyle tanıdım"): bulgu hiç olmasa da satır durur ve "0" yazar.</summary>
public static class FindingGroups
{
    public const string Tools = "tools", Records = "records", Projects = "projects", Browsers = "browsers", Display = "display", Language = "language";

    public static readonly IReadOnlyList<(string Key, string Title)> All = new[]
    {
        (Tools, "Yapay zekâ araçların"),
        (Records, "Araçların kayıt klasörleri"),
        (Projects, "Çalıştığın klasörler"),
        (Browsers, "Tarayıcıların"),
        (Display, "Ekranın"),
        (Language, "Dilin"),
    };

    public static bool IsKnown(string? key) => All.Any(g => g.Key == key);
}

/// <summary>Bulgunun ne kadar kesin olduğu. "Kurulu" yalnız dosya diskte görüldüyse; iz kurulu demek değildir.</summary>
public static class Confidence
{
    /// <summary>Dosyası diskte görüldü.</summary>
    public const string Installed = "installed";
    /// <summary>İz var (kayıt klasörü, kayıt defteri satırı) ama çalıştırma dosyası görülmedi: kurulu doğrulanmadı.</summary>
    public const string Trace = "trace";
    /// <summary>Sistem ayarından ya da klasörden doğrudan görüldü (kurulum sorusu yok).</summary>
    public const string Seen = "seen";
}

/// <summary>
/// Keşfin bir bulgusu. Kimlik kararlıdır (aynı şey ikinci keşifte aynı kimliği alır, bu yüzden çoğalmaz).
/// <see cref="Facts"/> yalnız ad, sürüm, sayı, tarih ve (~ ile kısaltılmış) yer taşır; konuşma, komut metni, başlık ya da ayar değeri asla.
/// </summary>
public sealed class Finding
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    /// <summary>Kişiye gösterilen kısa cümle (sade Türkçe).</summary>
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public int? Count { get; init; }
    public required string Confidence { get; init; }
    public JsonObject Facts { get; init; } = new();
    public List<Evidence> Evidence { get; } = new();
}

/// <summary>Bir keşif koşusunun çıktısı. Hiçbir şeyi yazmaz; yazımı <see cref="OnboardingService"/> yapar.</summary>
public sealed class ScanResult
{
    public List<Finding> Findings { get; } = new();
    /// <summary>Okunamayan (erişim reddi, kilit) yer sayısı.</summary>
    public int Unreadable { get; set; }
    /// <summary>Sistemden okunamayan konular (kişiye "okuyamadım" diye söylenir).</summary>
    public List<string> CouldNotRead { get; } = new();
}
