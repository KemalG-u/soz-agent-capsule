using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Registry;

namespace Soz.Core;

/// <summary>
/// Ayarlar (sözleşme 11.4). Değerler settings tablosunda JSON metni olarak durur (eski düz anahtarlarla: <c>legacyKey</c>).
/// Sınır, tür, enum, varsayılan ve okunurken düzeltme KAYIT DEFTERİNDEKİ `setting` kayıtlarından gelir (Registry\kayit\ayarlar.soz.json);
/// burada ayar sınırı ya da varsayılan sayısı yazılmaz. Yeni ayar eklemek için kayda bir giriş eklemek yeter.
/// Okuma (<see cref="Load(SqliteConnection)"/>) kayıt defterinin tamamını ayrıştırmaz: kanca gibi sık ve kısa ömürlü yollar yalnız ayarların varsayılan ve sınırını
/// aynı kayıt dosyalarından ucuz yoldan alır (<see cref="SettingIndex.Embedded"/>). Doğrulama (<see cref="Validate(JsonObject)"/>) tam kayıt defterini kullanır.
/// </summary>
public sealed class SozSettings
{
    /// <summary>Gömülü kayıt defterindeki varsayılanlar (eski anahtar → değer). Kayıt bozuksa ilk erişimde <see cref="RegistryUnavailable"/> atılır (tür başlatıcısı çökmez).</summary>
    public static IReadOnlyDictionary<string, JsonNode?> Defaults => SettingIndex.Embedded.Defaults;

    private readonly Dictionary<string, JsonNode?> _values;
    private readonly SettingIndex _index;
    private SozSettings(Dictionary<string, JsonNode?> v, SettingIndex index, List<SettingAdjustment> adjusted) { _values = v; _index = index; Adjusted = adjusted; }

    /// <summary>
    /// Okurken düzeltilen değerler: veritabanındaki değer ayarın kuralına uymadığı için başka bir değerle (sınıra çekilmiş ya da varsayılan) kullanılıyor.
    /// Veritabanındaki değer değişmez; bu liste kişiye "5 yazdım, neden 10" sorusunun cevabını verir (GET /api/settings `adjusted`, `soz doctor`).
    /// </summary>
    public IReadOnlyList<SettingAdjustment> Adjusted { get; }

    /// <summary>`adjusted` JSON dizisi: [{ key, stored, applied, reason }]. Gizli ayarın değeri yazılmaz, uzun metin kısaltılır.</summary>
    public JsonArray AdjustedJson()
    {
        var arr = new JsonArray();
        foreach (var a in Adjusted)
        {
            var hide = _index.IsSensitive(a.Key);
            arr.Add(new JsonObject { ["key"] = a.Key, ["stored"] = hide ? "(gizli)" : Shorten(a.Stored), ["applied"] = hide ? "(gizli)" : Shorten(a.Applied), ["reason"] = a.Reason });
        }
        return arr;
    }

    private static JsonNode? Shorten(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 80 ? JsonValue.Create(s[..79] + "…") : n?.DeepClone();

    /// <summary>Kayıt defterinin tamamını yüklemeden okur (kanca yolu). Gömülü kayıt bozuksa <see cref="RegistryUnavailable"/> atar.</summary>
    public static SozSettings Load(SqliteConnection c) => Load(c, SettingIndex.Embedded);

    /// <summary>
    /// Veritabanından okur. Eski değer kayıt şemasına uymuyorsa çökmez: `clampOnRead` ayarlarında sayı kayıt sınırına kıstırılır,
    /// öteki ayarlarda varsayılana düşülür. Kayıtta olmayan anahtar olduğu gibi gelir (iç anahtarlar `_` ile başlar, atlanır).
    /// </summary>
    public static SozSettings Load(SqliteConnection c, RegistryLoadResult catalog) => Load(c, SettingIndex.Of(catalog));

    private static SozSettings Load(SqliteConnection c, SettingIndex index)
    {
        var adjusted = new List<SettingAdjustment>();
        var v = index.Defaults.ToDictionary(k => k.Key, k => k.Value?.DeepClone());
        foreach (var (k, raw) in c.Query("SELECT key, value FROM settings", r => (r.GetString(0), r.Str(1))))
        {
            if (k.StartsWith('_')) continue; // iç anahtarlar (ör. _lastImportAt)
            JsonNode? parsed;
            try { parsed = raw is null ? null : JsonNode.Parse(raw); } catch (JsonException) { parsed = raw; }
            v[k] = index.Normalize(k, parsed, out var reason);
            if (reason is not null) adjusted.Add(new SettingAdjustment(k, parsed, v[k], reason));
        }
        return new SozSettings(v, index, adjusted);
    }

    public static SozSettings Load(SozDb db) { using var c = db.Open(); return Load(c); }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        foreach (var (k, v) in _values) o[k] = v?.DeepClone();
        return o;
    }

    /// <summary>Kısmi güncelleme. Bilinmeyen anahtar ya da geçersiz değer → hata metni. Karar kayıt defterindeki `setting` kaydının şemasından gelir.</summary>
    public static string? Validate(JsonObject patch) => Validate(patch, FixedCatalog.Get);

    public static string? Validate(JsonObject patch, RegistryLoadResult catalog)
    {
        try { return SettingValidator.Validate(catalog, patch); }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return "Geçersiz ayar değeri: " + e.Message; }
    }

    public static void Save(SqliteConnection c, JsonObject patch)
    {
        using var tx = c.BeginTransaction();
        foreach (var (k, v) in patch)
            c.Exec("INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", tx,
                ("@k", k), ("@v", v?.ToJsonString()));
        tx.Commit();
    }

    public static void SetInternal(SqliteConnection c, string key, string? value)
        => c.Exec("INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", null, ("@k", key), ("@v", value));

    public static string? GetInternal(SqliteConnection c, string key)
        => c.Scalar("SELECT value FROM settings WHERE key=@k", null, ("@k", key)) as string;

    public string? Str(string key) => _values.GetValueOrDefault(key) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
    public double Num(string key, double fallback)
    {
        var v = _values.GetValueOrDefault(key);
        return v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number ? double.Parse(jv.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : fallback;
    }
    /// <summary>Sayı değeri; geri dönüş değeri kayıttaki varsayılandır (koda ikinci kez yazılmaz).</summary>
    public double Num(string key) => Num(key, DefaultNum(key));
    public bool Bool(string key) => _values.GetValueOrDefault(key) is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private double DefaultNum(string key)
        => _index.Defaults.GetValueOrDefault(key) is JsonValue d && d.GetValueKind() == JsonValueKind.Number ? double.Parse(d.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : 0;
    private string? DefaultStr(string key) => _index.Defaults.GetValueOrDefault(key) is JsonValue d && d.GetValueKind() == JsonValueKind.String ? d.GetValue<string>() : null;

    /// <summary>Tam sayı ayar, kayıttaki sınıra kıstırılmış (sınır kodda yazılmaz).</summary>
    public int ClampedInt(string key) => (int)_index.Clamp(key, Num(key));

    public string Language => Str("language") ?? DefaultStr("language") ?? "tr";
    public string Theme => Str("theme") ?? DefaultStr("theme") ?? "system";
    public string ThemeFamily => Str("themeFamily") ?? DefaultStr("themeFamily") ?? "cam";
    public string NavTabs => Str("navTabs") ?? DefaultStr("navTabs") ?? "suan,projeler,ayarlar";
    public double ExtractThreshold => Num("extractThreshold");
    public double RepeatThreshold => Num("repeatThreshold");
    public int PacketBudget => (int)Num("packetBudget");
    public double WaitingStaleHours => Num("waitingStaleHours");
    public double StalledMinutes => Num("stalledMinutes");
    public double WorkingSeconds => Num("workingSeconds");
    public double IdleMinutes => Num("idleMinutes");
    public bool ExplainEnabled => Bool("explainEnabled");
    public string ExplainCommand => Str("explainCommand") ?? DefaultStr("explainCommand") ?? "";
    public bool AllowRealProjectWrites => Bool("allowRealProjectWrites");
    public bool LanEnabled => Bool("lanEnabled");
    public string? ClaudeRoot => Str("claudeRoot");
    public string? CodexRoot => Str("codexRoot");
    public string? ClaudePath => string.IsNullOrWhiteSpace(Str("claudePath")) ? null : Str("claudePath");
    public string? CodexPath => string.IsNullOrWhiteSpace(Str("codexPath")) ? null : Str("codexPath");
    public string? HermesPath => string.IsNullOrWhiteSpace(Str("hermesPath")) ? null : Str("hermesPath");
    public string? OpenCodePath => string.IsNullOrWhiteSpace(Str("opencodePath")) ? null : Str("opencodePath");
    public string? AntigravityPath => string.IsNullOrWhiteSpace(Str("antigravityPath")) ? null : Str("antigravityPath");
    public string DefaultAgent => Soz.Core.Agents.AgentRegistry.Find(Str("defaultAgent")) is { } da ? da.Id : Soz.Core.Agents.AgentRegistry.Default;
    public string RunnerModel => (Str("runnerModel") ?? "").Trim();
    public int MaxParallelRuns => ClampedInt("maxParallelRuns");
    public string RunnerAllowedTools => string.IsNullOrWhiteSpace(Str("runnerAllowedTools")) ? DefaultStr("runnerAllowedTools") ?? "" : Str("runnerAllowedTools")!;
    public bool BaselineRun => Bool("baselineRun");
    public int RunnerMaxMinutes => ClampedInt("runnerMaxMinutes");
}
