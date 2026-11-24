using System.Text.Json.Nodes;

namespace Soz.Core.Registry;

/// <summary>Kayıt türleri. 13 ad ayrılır, gövdesi tanımlı olan dördü (<see cref="Defined"/>); ötekiler "tanınan ama henüz kullanılmayan tür".</summary>
public static class RegistryKinds
{
    public const string Setting = "setting", Connector = "connector", CheckKind = "check-kind", Reader = "reader";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Setting, Connector, CheckKind, "guard-template", "theme", "capsule-behavior", "notify-channel",
        "shortcut", "palette-command", "extractor", "reader", "job-kind", "mode",
    };

    public static readonly IReadOnlyList<string> Defined = new[] { Setting, Connector, CheckKind, Reader };

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
    public static bool IsDefined(string? kind) => kind is not null && Defined.Contains(kind);
}

public static class RegistryOwners
{
    public const string Fixed = "fixed", User = "user", System = "system";
}

public static class RegistryStates
{
    public const string Enabled = "enabled", Disabled = "disabled", Suggested = "suggested", Broken = "broken", Deprecated = "deprecated";
}

/// <summary>Düz metin ya da {tr,en}. Eksik dil tr'ye düşer; tr yoksa mevcut olan.</summary>
public sealed record LocalizedText(string Tr, string? En)
{
    public string Get(string? lang) => string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(En) ? En! : Tr;
    public bool IsBlank => string.IsNullOrWhiteSpace(Tr) && string.IsNullOrWhiteSpace(En);
    public override string ToString() => Tr;

    public static LocalizedText? From(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return new LocalizedText(s, null);
        if (n is JsonObject o)
        {
            var tr = (o["tr"] as JsonValue)?.GetValue<string>();
            var en = (o["en"] as JsonValue)?.GetValue<string>();
            return new LocalizedText(tr ?? en ?? "", en);
        }
        return null;
    }
}

public sealed record RegistryPermission(string Cap, string? Scope, LocalizedText Why);

/// <summary>Madde sözleşmesi: dört cümle (Vizyon ilke 9).</summary>
public sealed record RegistryContract(LocalizedText? Discover, LocalizedText? Disable, LocalizedText? OnError, LocalizedText? OnNewTool)
{
    public static readonly IReadOnlyList<string> Names = new[] { "discover", "disable", "onError", "onNewTool" };

    /// <summary>Boş ya da eksik cümlelerin adları.</summary>
    public IReadOnlyList<string> Missing()
    {
        var list = new List<string>();
        if (Discover is null || Discover.IsBlank) list.Add("discover");
        if (Disable is null || Disable.IsBlank) list.Add("disable");
        if (OnError is null || OnError.IsBlank) list.Add("onError");
        if (OnNewTool is null || OnNewTool.IsBlank) list.Add("onNewTool");
        return list;
    }

    public static RegistryContract? From(JsonNode? n)
        => n is JsonObject o
            ? new RegistryContract(LocalizedText.From(o["discover"]), LocalizedText.From(o["disable"]), LocalizedText.From(o["onError"]), LocalizedText.From(o["onNewTool"]))
            : null;
}

public sealed record RegistryDeprecation(string Since, string? ReplacedBy, LocalizedText? Message);

/// <summary>`setting` gövdesi (rapor 1.4). Doğrulamayı yapan şey <see cref="Schema"/>; öteki alanlar sayfa, arama ve sınıf kuralları içindir.</summary>
public sealed class SettingBody
{
    public JsonObject Schema { get; init; } = new();
    public JsonNode? Default { get; init; }
    public string Class { get; init; } = "";
    public IReadOnlyList<string> Scopes { get; init; } = new[] { "global" };
    public string? Group { get; init; }
    public string? Card { get; init; }
    public int Order { get; init; }
    public string Control { get; init; } = "auto";
    public string? Unit { get; init; }
    public string Restart { get; init; } = "none";
    public bool Sensitive { get; init; }
    /// <summary>K4: en kısıtlayıcı kazanır ("false-wins", liste ayarında "intersection-wins" gibi). Yoksa null.</summary>
    public string? Restrictive { get; init; }
    public IReadOnlyList<string> Validators { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Effects { get; init; } = Array.Empty<string>();
    public bool ModeSettable { get; init; }
    /// <summary>Veritabanında eskiden kalmış sınır dışı SAYI okunurken kayıt sınırına kıstırılır (ör. gateBudgetSeconds 5 → 10). Yoksa uygunsuz değer varsayılana düşer.</summary>
    public bool ClampOnRead { get; init; }
    public string? LegacyKey { get; init; }

    private static IReadOnlyList<string> Strings(JsonNode? n, IReadOnlyList<string> fallback)
        => n is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : fallback;

    public static SettingBody From(JsonObject b) => new()
    {
        Schema = b["schema"] as JsonObject ?? new JsonObject(),
        Default = b["default"],
        Class = (b["class"] as JsonValue)?.GetValue<string>() ?? "",
        Scopes = Strings(b["scopes"], new[] { "global" }),
        Group = (b["group"] as JsonValue)?.GetValue<string>(),
        Card = (b["card"] as JsonValue)?.GetValue<string>(),
        Order = (b["order"] as JsonValue) is { } ov && ov.TryGetValue<int>(out var ord) ? ord : 0,
        Control = (b["control"] as JsonValue)?.GetValue<string>() ?? "auto",
        Unit = (b["unit"] as JsonValue)?.GetValue<string>(),
        Restart = (b["restart"] as JsonValue)?.GetValue<string>() ?? "none",
        Sensitive = (b["sensitive"] as JsonValue)?.GetValue<bool>() ?? false,
        Restrictive = ((b["securityGate"] as JsonObject)?["restrictive"] as JsonValue)?.GetValue<string>(),
        Validators = Strings(b["validators"], Array.Empty<string>()),
        Effects = Strings(b["effects"], Array.Empty<string>()),
        ModeSettable = (b["modeSettable"] as JsonValue)?.GetValue<bool>() ?? false,
        ClampOnRead = (b["clampOnRead"] as JsonValue)?.GetValue<bool>() ?? false,
        LegacyKey = (b["legacyKey"] as JsonValue)?.GetValue<string>(),
    };
}

/// <summary>Yüklenmiş bir kayıt. Dosyada yazılan alanlar + yükleyicinin ürettiği alanlar (Origin, Sha256, Problems, State).</summary>
public sealed class RegistryEntry
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Owner { get; init; }
    public required LocalizedText Title { get; init; }
    public LocalizedText? Description { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Surfaces { get; init; } = Array.Empty<string>();
    public IReadOnlyList<RegistryPermission> Permissions { get; init; } = Array.Empty<RegistryPermission>();
    public string Stability { get; init; } = "ground";
    public string? Since { get; init; }
    public RegistryDeprecation? Deprecated { get; init; }
    public RegistryContract? Contract { get; init; }
    public JsonObject Body { get; init; } = new();

    /// <summary>Dosyada yazılan durum (yoksa null). Etkin durum <see cref="State"/>: onay kapısı ve sahiplik kuralları onu değiştirebilir.</summary>
    public string? DeclaredState { get; init; }
    public required string State { get; init; }

    // Yükleyicinin ürettiği alanlar (dosyada yazılmaz).
    /// <summary>core, file:&lt;yol&gt;.</summary>
    public required string Origin { get; init; }
    /// <summary>Kaydın geldiği dosyanın baytlarının SHA-256 özeti (küçük harf hex). Onay bu özete bağlanır.</summary>
    public required string Sha256 { get; init; }
    /// <summary>
    /// Onayın bağlandığı özet. Dosyadan gelen (kişi) kayıtta dosyanın özetidir (<see cref="Sha256"/>); sabit (fixed) bir kayıtta yalnız kaydın KENDİ özetidir,
    /// böylece aynı dosyadaki öteki kayıtların değişmesi onayı düşürmez. Onay ve onay geri alma bu özetle çalışır.
    /// </summary>
    public string ApprovalSha { get; init; } = "";
    /// <summary>Yüklemeyi engellemeyen notlar (onay bekliyor, tanınan ama kullanılmayan tür gibi).</summary>
    public List<string> Problems { get; } = new();

    public SettingBody? Setting => Kind == RegistryKinds.Setting ? _setting ??= SettingBody.From(Body) : null;
    private SettingBody? _setting;

    /// <summary>Türkçe harf katlamasıyla arama: kimlik, başlık (iki dil), açıklama ve anahtar sözcüklerde.</summary>
    public bool Matches(string? query)
    {
        var q = Fold.Text(query).Trim();
        if (q.Length == 0) return true;
        var hay = new List<string?> { Id, Title.Tr, Title.En, Description?.Tr, Description?.En };
        hay.AddRange(Keywords);
        return hay.Any(h => Fold.Text(h).Contains(q, StringComparison.Ordinal));
    }
}

/// <summary>Bir sorun ya da uyarı: hangi kaynakta, hangi kayıtta, hangi türden ve sade neden.</summary>
public sealed record RegistryProblem(string Source, string? Id, string Code, string Message, bool IsFixed = false)
{
    public override string ToString() => (Id is null ? Source : $"{Source}: {Id}") + $": {Message}";
}

/// <summary>Yükleme sonucu. Reddedilen kayıt yüklenmez (Rejected); tanınmayan tür uyarı olur (Warnings) ve silinmez, yalnız yüklenmez.</summary>
public sealed class RegistryLoadResult
{
    public List<RegistryEntry> Entries { get; } = new();
    public List<RegistryProblem> Rejected { get; } = new();
    public List<RegistryProblem> Warnings { get; } = new();

    public RegistryEntry? Get(string id) => Entries.FirstOrDefault(e => e.Id == id);
    public IEnumerable<RegistryEntry> OfKind(string kind) => Entries.Where(e => e.Kind == kind);
    public IEnumerable<RegistryEntry> Settings => OfKind(RegistryKinds.Setting);
    /// <summary>Eski düz anahtara (maxParallelRuns) göre ayar kaydı.</summary>
    public RegistryEntry? SettingByLegacyKey(string key) => Settings.FirstOrDefault(e => e.Setting?.LegacyKey == key);
}

/// <summary>
/// Onay özetini okuyan şey. Dosyadan gelen çalıştırılabilir kayıt, özeti onaylanana kadar `suggested` kalır; dosya değişirse özet değişir, onay düşer.
/// Sabit (fixed) bir kayıt da dosyada `state: suggested` yazılıysa (ör. Hermes ve OpenCode: çalıştırılabilir ama kapalı gelir) kişi onaylayana kadar öneri kalır.
/// Üretimdeki uygulama veritabanıdır (<see cref="Soz.Core.Onboarding.DbRegistryApprovals"/>, registry_state.approved_sha256); bellek içi uygulama sınama içindir.
/// </summary>
public interface IRegistryApprovals
{
    bool IsApproved(string id, string sha256);
    bool IsDisabled(string id) => false;
}

public sealed class MemoryRegistryApprovals : IRegistryApprovals
{
    private readonly HashSet<(string, string)> _approved = new();
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);
    public void Approve(string id, string sha256) { _disabled.Remove(id); _approved.Add((id, sha256)); }
    public void Revoke(string id, string sha256) => _approved.Remove((id, sha256));
    public void SetEnabled(string id, bool enabled) { if (enabled) _disabled.Remove(id); else _disabled.Add(id); }
    public bool IsApproved(string id, string sha256) => !_disabled.Contains(id) && _approved.Contains((id, sha256));
    public bool IsDisabled(string id) => _disabled.Contains(id);
}
