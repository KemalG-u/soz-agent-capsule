using System.Text;
using System.Text.Json.Nodes;

namespace Soz.Core.Registry;

/// <summary>`registry check` sonucu. Hata varsa Ok=false (çıkış kodu 1).</summary>
public sealed class RegistryCheckReport
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public int EntryCount { get; init; }
    public IReadOnlyDictionary<string, int> ByKind { get; init; } = new Dictionary<string, int>();
    public bool Ok => Errors.Count == 0;

    /// <summary>Sade Türkçe çıktı.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        var kinds = string.Join(", ", ByKind.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Value} {k.Key}"));
        sb.AppendLine($"Kayıt denetimi: {EntryCount} kayıt" + (kinds.Length > 0 ? $" ({kinds})" : "") + $", {Errors.Count} hata, {Warnings.Count} uyarı.");
        foreach (var e in Errors) sb.AppendLine("HATA   " + e);
        foreach (var w in Warnings) sb.AppendLine("UYARI  " + w);
        sb.AppendLine(Ok ? "Sonuç: GEÇTİ." : "Sonuç: KALDI. Yukarıdaki hataları düzeltmeden bu kayıtlar kullanılamaz.");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Kural denetimi (rapor 1.10, hassfest benzeri): şemanın söyleyemediği kurallar. Kimlikler tekil, bağlı kimlik var, kart başına en çok 7 ayar,
/// her fixed kayıtta contract dört cümle dolu, K4 ayarda securityGate var, K3/K4 modeSettable olamaz (şema da söyler, burada yüklenmiş veriden ikinci kez bakılır),
/// ayarın varsayılan değeri kendi şemasına uyar, eski anahtarlar tekil.
/// </summary>
public static class RegistryChecker
{
    public const int MaxSettingsPerCard = 7;

    public static RegistryCheckReport Check(RegistryLoadResult load)
    {
        var report = new RegistryCheckReport
        {
            EntryCount = load.Entries.Count,
            ByKind = load.Entries.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()),
        };

        // 1) Yüklenemeyenler: sabit ya da kişi dosyası fark etmez, reddedilen her kayıt bir hatadır.
        foreach (var r in load.Rejected) report.Errors.Add($"reddedildi: {r}");
        foreach (var w in load.Warnings)
        {
            // Sabit pakette bilinmeyen tür paketin bozuk olduğu demektir; kişi dosyasında yalnız uyarı.
            var inFixed = w.Code == "unknown-kind" && w.IsFixed;
            (inFixed ? report.Errors : report.Warnings).Add(w.ToString());
        }

        // 2) Kimlikler tekil (yükleyici yinelemeyi reddeder; yine de yüklenmiş listeden bakılır) ve eski anahtarlar tekil.
        foreach (var g in load.Entries.GroupBy(e => e.Id).Where(g => g.Count() > 1))
            report.Errors.Add($"{g.Key}: kimlik {g.Count()} kez kullanılmış.");
        foreach (var g in load.Settings.Where(e => e.Setting?.LegacyKey is not null).GroupBy(e => e.Setting!.LegacyKey!).Where(g => g.Count() > 1))
            report.Errors.Add($"eski anahtar \"{g.Key}\" birden çok ayarda: {string.Join(", ", g.Select(e => e.Id))}.");

        // 3) Bağlı kimlik var.
        var ids = new HashSet<string>(load.Entries.Select(e => e.Id), StringComparer.Ordinal);
        foreach (var e in load.Entries)
        {
            foreach (var (path, id) in References(e))
                if (!ids.Contains(id)) report.Errors.Add($"{e.Id}: {path} olarak yazılan \"{id}\" kayıt defterinde yok.");
        }

        // 4) Kart başına en çok 7 ayar.
        foreach (var g in load.Settings.Where(e => e.Setting?.Card is not null).GroupBy(e => (e.Setting!.Group, e.Setting.Card)))
            if (g.Count() > MaxSettingsPerCard)
                report.Errors.Add($"\"{g.Key.Card}\" kartında ({g.Key.Group}) {g.Count()} ayar var. En çok {MaxSettingsPerCard} olabilir.");

        // 5) Her fixed kayıtta contract dört cümle dolu.
        foreach (var e in load.Entries.Where(e => e.Owner == RegistryOwners.Fixed))
        {
            if (e.Contract is null) { report.Errors.Add($"{e.Id}: sabit kayıtta contract (dört cümle) yok."); continue; }
            var missing = e.Contract.Missing();
            if (missing.Count > 0) report.Errors.Add($"{e.Id}: contract cümlesi eksik ya da boş: {string.Join(", ", missing)}.");
        }

        // 6) Ayar kuralları.
        foreach (var e in load.Settings)
        {
            var b = e.Setting!;
            if (b.Class == "K4" && string.IsNullOrEmpty(b.Restrictive)) report.Errors.Add($"{e.Id}: K4 (korumayı etkileyen ayar) ayarında securityGate yok.");
            if (b.Class is "K3" or "K4" && b.ModeSettable) report.Errors.Add($"{e.Id}: {b.Class} ayar kip ile gevşetilemez (modeSettable false olmalı).");
            if (b.Class == "K5" && b.Group is not null) report.Errors.Add($"{e.Id}: K5 (iç durum) ayarı sayfada görünmez, group null olmalı.");
            if (b.Class != "K5" && (b.Group is null || b.Card is null)) report.Errors.Add($"{e.Id}: sayfada görünen ayarın group ve card alanı olmalı.");
            var bad = new SchemaCheck(b.Schema).Validate(b.Default);
            if (bad.Count > 0) report.Errors.Add($"{e.Id}: varsayılan değer kendi şemasına uymuyor: {bad[0]}");
            var unsupported = SchemaCheck.UnsupportedKeywords(b.Schema);
            if (unsupported.Count > 0) report.Errors.Add($"{e.Id}: schema içinde desteklenmeyen anahtar sözcük: {string.Join(", ", unsupported)}.");
        }

        // 7) Bağlayıcıda lehçe varsa geçerli bir ad (şema söyler); ölçüt türünde ölçer adı bilinen yerleşik mi?
        foreach (var e in load.OfKind(RegistryKinds.CheckKind))
        {
            var m = e.Body["measure"] as JsonObject;
            if ((m?["via"] as JsonValue)?.GetValue<string>() == "builtin" && !BuiltinMeasures.Contains((m?["name"] as JsonValue)?.GetValue<string>() ?? ""))
                report.Warnings.Add($"{e.Id}: yerleşik ölçer \"{(m?["name"] as JsonValue)?.GetValue<string>()}\" bu Söz sürümünde yok. Ölçüt çalıştırılırsa 'error' olur.");
        }

        return report;
    }

    /// <summary>Bugünkü yerleşik ölçerler: ölçücü kaydındaki (MeasurerRegistry) tür adları.</summary>
    public static IReadOnlySet<string> BuiltinMeasures => new HashSet<string>(Soz.Core.Proof.MeasurerRegistry.Kinds, StringComparer.Ordinal);

    /// <summary>Bir kaydın başka kayda bağlandığı yerler: (yer, kimlik).</summary>
    private static IEnumerable<(string path, string id)> References(RegistryEntry e)
    {
        if (e.Deprecated?.ReplacedBy is { } r) yield return ("deprecated.replacedBy", r);
        if (e.Kind == RegistryKinds.Connector)
        {
            if (((e.Body["detect"] as JsonObject)?["pathSetting"] as JsonValue)?.GetValue<string>() is { } ps) yield return ("detect.pathSetting", ps);
            if (((e.Body["history"] as JsonObject)?["rootSetting"] as JsonValue)?.GetValue<string>() is { } rs) yield return ("history.rootSetting", rs);
        }
    }
}
