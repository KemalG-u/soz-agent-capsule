using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Registry;

namespace Soz.Core.Discovery;

/// <summary>
/// Öneri formülünün ve ölçümün sayıları. HEPSİ kayıt defterinden (`Registry\kayit\kesif.soz.json`, ayar kayıtları) gelir; kodda varsayılan sayı yoktur.
/// Kayıtta bir ayar yoksa sessizce uydurma değer kullanılmaz, açık hata verilir.
/// </summary>
public sealed record DiscoveryParams(
    double HalfLifeDays, double ProjectWeight, double TimeWeight, double Saturation, double Threshold, double InSlotFactor,
    bool ShadowMode, int MaxSuggestions, double HitWindowMinutes, double DismissStep, double DismissMax, double DismissHalfLifeDays, int TimeBlockHours)
{
    public const string Prefix = "setting:discovery.";
    public const string KHalfLife = Prefix + "half-life-days", KProjectWeight = Prefix + "project-weight", KTimeWeight = Prefix + "time-weight",
        KSaturation = Prefix + "saturation", KThreshold = Prefix + "threshold", KInSlot = Prefix + "in-slot-factor", KShadow = Prefix + "shadow-mode",
        KMax = Prefix + "max-suggestions", KHitWindow = Prefix + "hit-window-minutes", KDismissStep = Prefix + "dismiss-step",
        KDismissMax = Prefix + "dismiss-max", KDismissHalfLife = Prefix + "dismiss-half-life-days", KTimeBlock = Prefix + "time-block-hours";

    public static readonly IReadOnlyList<string> Keys = new[]
    {
        KHalfLife, KProjectWeight, KTimeWeight, KSaturation, KThreshold, KInSlot, KShadow, KMax, KHitWindow, KDismissStep, KDismissMax, KDismissHalfLife, KTimeBlock,
    };

    /// <summary>Kayıt defterindeki varsayılanlar.</summary>
    public static DiscoveryParams FromDefaults(RegistryLoadResult catalog)
    {
        var d = SettingIndex.Of(catalog).Defaults;
        return Build(k => d.TryGetValue(k, out var v) ? v : null);
    }

    /// <summary>Kişinin kayıtlı ayarları (yoksa kayıttaki varsayılan).</summary>
    public static DiscoveryParams From(SozSettings settings)
    {
        var json = settings.ToJson();
        return Build(k => json[k]);
    }

    private static DiscoveryParams Build(Func<string, JsonNode?> get)
    {
        double N(string k) => get(k) is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Keşif ayarı kayıt defterinde yok ya da sayı değil: {k}");
        bool B(string k) => get(k) is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? v.GetValueKind() == JsonValueKind.True
            : throw new InvalidOperationException($"Keşif ayarı kayıt defterinde yok ya da açık/kapalı değil: {k}");
        return new DiscoveryParams(N(KHalfLife), N(KProjectWeight), N(KTimeWeight), N(KSaturation), N(KThreshold), N(KInSlot),
            B(KShadow), (int)N(KMax), N(KHitWindow), N(KDismissStep), N(KDismissMax), N(KDismissHalfLife), (int)N(KTimeBlock));
    }
}
