using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Registry;

/// <summary>
/// `soz doctor` için kayıt defteri ve olay defteri sağlığı (d1f): hiçbir zaman atmaz, her şeyi satıra çevirir.
/// (1) ayar kaydı okunabiliyor mu (okunamıyorsa tek sade cümle, <see cref="RegistryUnavailable.Message"/>),
/// (2) olay defterinin zinciri (<see cref="Events.VerifyChainDetailed"/>),
/// (3) okunurken sınıra çekilen ya da varsayılana düşen kayıtlı ayar değerleri (<see cref="SozSettings.Adjusted"/>).
/// </summary>
public sealed class DoctorHealth
{
    /// <summary>Ayar kaydı okunamadıysa kişiye gösterilecek cümle; sağlamsa null.</summary>
    public string? RegistryError { get; private init; }
    /// <summary>Ayar kaydı okunamadıysa teknik ayrıntı (hangi dosya, ne bozuk).</summary>
    public string? RegistryDetail { get; private init; }
    /// <summary>Olay defteri zinciri; veritabanı okunamadıysa null.</summary>
    public ChainReport? Chain { get; private init; }
    public IReadOnlyList<SettingAdjustment> Adjusted { get; private init; } = Array.Empty<SettingAdjustment>();

    public static DoctorHealth Collect(SozDb db)
    {
        var ok = FixedCatalog.TryGet(out _, out var detail);
        ChainReport? chain = null;
        IReadOnlyList<SettingAdjustment> adjusted = Array.Empty<SettingAdjustment>();
        try
        {
            using var c = db.Open();
            chain = Events.VerifyChainDetailed(c);
            if (ok) adjusted = SozSettings.Load(c).Adjusted;
        }
        catch (Exception e) when (e is SqliteException or IOException or SozException) { /* doctor satırı eksik kalır, doctor çökmez */ }
        return new DoctorHealth { RegistryError = ok ? null : RegistryUnavailable.Message, RegistryDetail = detail, Chain = chain, Adjusted = adjusted };
    }

    /// <summary>Doctor çıktısına eklenecek satırlar (sade Türkçe).</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>();
        if (RegistryError is not null)
        {
            lines.Add(RegistryError);
            if (RegistryDetail is not null) lines.Add($"  ayrıntı: {RegistryDetail}");
        }
        if (Chain is not null) lines.Add(Chain.Line());
        if (RegistryError is null)
            lines.Add(Adjusted.Count == 0
                ? "ayarlar: kayıtlı bütün değerler geçerli, düzeltilen yok"
                : $"ayarlar: {Adjusted.Count} kayıtlı değer kurala uymadığı için düzeltilerek kullanılıyor: "
                  + string.Join(", ", Adjusted.Take(5).Select(a => $"{a.Key} {Show(a.Stored)} → {Show(a.Applied)}")) + (Adjusted.Count > 5 ? ", ..." : ""));
        return lines;
    }

    private static string Show(JsonNode? n)
    {
        var s = n?.ToJsonString() ?? "boş";
        return s.Length <= 24 ? s : s[..23] + "…";
    }

    /// <summary>`soz doctor --json` için eklenen alanlar: registry, eventLog, settingsAdjusted.</summary>
    public JsonObject ToJson() => new()
    {
        ["registry"] = new JsonObject { ["ok"] = RegistryError is null, ["error"] = RegistryError, ["detail"] = RegistryDetail },
        ["eventLog"] = Chain is null ? null : new JsonObject
        {
            ["total"] = Chain.Total, ["verified"] = Chain.Verified, ["legacyUnverified"] = Chain.Legacy, ["brokenSeq"] = Chain.BrokenSeq, ["line"] = Chain.Line(),
        },
        ["settingsAdjusted"] = new JsonArray(Adjusted.Select(a => (JsonNode?)new JsonObject { ["key"] = a.Key, ["stored"] = a.Stored?.DeepClone(), ["applied"] = a.Applied?.DeepClone(), ["reason"] = a.Reason }).ToArray()),
    };
}
