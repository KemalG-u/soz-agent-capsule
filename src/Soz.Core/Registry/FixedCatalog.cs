using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soz.Core.Registry;

/// <summary>
/// Gömülü kayıt defteri okunamayınca atılan tek hata: tek sade cümle, kişiye olduğu gibi gösterilir (sunucu JSON gövdeli 500 verir, komut satırı ve `soz doctor` aynı cümleyi yazar).
/// Koda gömülü asgari varsayılan yoktur: ayar sınırı tek yerde (kayıt dosyası) olduğu için bozuk kurulumda sessizce devam etmek yanlış olur.
/// Teknik ayrıntı cümleye girmez, <see cref="DetailOf"/> ile alınır (doctor ve günlük içindir).
/// </summary>
public static class RegistryUnavailable
{
    public const string Message = "Söz'ün ayar kaydı okunamadı, kurulum bozulmuş olabilir. Yeniden kur.";

    public static SozException Create(string detail)
    {
        var e = new SozException(500, Message);
        e.Data["detail"] = detail;
        return e;
    }

    public static bool Is(Exception? e) => e is SozException { Status: 500 } s && s.Message == Message;

    /// <summary>Hatanın teknik ayrıntısı (hangi dosya, ne bozuk); yoksa null.</summary>
    public static string? DetailOf(Exception e) => e.Data["detail"] as string;
}

/// <summary>
/// Gömülü (fixed) kayıt defterinin tek önbellekli TAM kopyası: bütün kayıtlar yüklenir, şemaya göre doğrulanır. Uzun ömürlü yollar (sunucu, `registry` komutları, doğrulama) bunu kullanır.
/// Kanca gibi kısa ömürlü yollar bunu yüklemez: <see cref="SettingIndex.Embedded"/> yalnız ayar varsayılanını ve sınırını aynı kayıt dosyalarından ucuz yoldan okur.
/// Gömülü paket bozuksa <see cref="RegistryUnavailable"/> atılır.
/// </summary>
public static class FixedCatalog
{
    private static readonly Lazy<RegistryLoadResult> L = new(Create);

    private static RegistryLoadResult Create()
    {
        RegistryLoadResult r;
        try { r = RegistryLoader.LoadEmbedded(); }
        catch (Exception e) when (e is not (OutOfMemoryException or SozException))
        { throw RegistryUnavailable.Create($"{e.GetType().Name}: {e.Message}"); }
        if (r.Rejected.Count > 0) throw RegistryUnavailable.Create(string.Join(" | ", r.Rejected.Take(3)));
        if (!r.Settings.Any()) throw RegistryUnavailable.Create("gömülü kayıt dosyalarında hiç ayar kaydı yok");
        return r;
    }

    /// <summary>Tam kayıt defteri. Bozuksa <see cref="RegistryUnavailable"/> atar (sonuç önbelleğe alınır, her çağrıda aynı hata gelir).</summary>
    public static RegistryLoadResult Get => RegistryLoader.EmbeddedOverride.Value is null ? L.Value : Create();

    /// <summary>Atmadan dener: kayıt defteri sağlamsa true, değilse false ve teknik ayrıntı.</summary>
    public static bool TryGet(out RegistryLoadResult? catalog, out string? detail)
    {
        try { catalog = Get; detail = null; return true; }
        catch (SozException e) when (RegistryUnavailable.Is(e)) { catalog = null; detail = RegistryUnavailable.DetailOf(e); return false; }
    }
}

/// <summary>Okurken uygulanan düzeltme: veritabanındaki değer ayarın kuralına uymadığı için başka bir değerle kullanıldı (kişi görsün diye).</summary>
public sealed record SettingAdjustment(string Key, JsonNode? Stored, JsonNode? Applied, string Reason);

/// <summary>
/// Bir kayıt kümesindeki ayarların eski anahtara göre dizini: varsayılanlar, sınırlar ve okunurken düzeltme. İki kaynaktan kurulur:
/// (1) <see cref="Of"/>: yüklenmiş tam kayıt defterinden; (2) <see cref="Embedded"/>: gömülü kayıt dosyalarından yalnız `setting` kayıtlarının
/// varsayılan, şema ve düzeltme bayrağını okuyan hafif yol (kayıt defterinin tamamını ayrıştırmaz, doğrulamaz; sıcak yol için). İkisi aynı sonucu verir,
/// bir test bunu bütün ayarlarda denetler. Kayıt kümesi başına bir kez kurulur.
/// </summary>
public sealed class SettingIndex
{
    private sealed record Def(string Key, JsonNode? Default, JsonObject Schema, bool ClampOnRead, bool Sensitive, double? Min, double? Max, bool IntegerOnly);

    private static readonly ConditionalWeakTable<RegistryLoadResult, SettingIndex> Cache = new();
    private static readonly Lazy<SettingIndex> EmbeddedLazy = new(FromEmbedded);

    public static SettingIndex Of(RegistryLoadResult catalog) => Cache.GetValue(catalog, c => new SettingIndex(c));

    /// <summary>Gömülü kayıt dosyalarından hafif dizin. Gömülü kayıt bozuksa <see cref="RegistryUnavailable"/> atar.</summary>
    public static SettingIndex Embedded => RegistryLoader.EmbeddedOverride.Value is null ? EmbeddedLazy.Value : FromEmbedded();

    private readonly Dictionary<string, Def> _defs;
    private readonly Dictionary<string, RegistryEntry>? _byKey;

    /// <summary>Eski anahtar → ayar kaydı. Eski anahtarı olmayan ayar kimliğiyle girer. Yalnız tam kayıt defterinden kurulan dizinde vardır.</summary>
    public IReadOnlyDictionary<string, RegistryEntry> ByKey => _byKey ?? throw new InvalidOperationException("Hafif ayar dizininde kayıt yok. Tam kayıt defterinden kurulmuş dizin gerekir.");
    /// <summary>Eski anahtar → varsayılan değer (kayıttaki `default`).</summary>
    public IReadOnlyDictionary<string, JsonNode?> Defaults { get; }

    private SettingIndex(RegistryLoadResult catalog)
    {
        _defs = new(StringComparer.Ordinal);
        _byKey = new(StringComparer.Ordinal);
        foreach (var e in catalog.Settings)
        {
            var b = e.Setting!;
            var key = b.LegacyKey ?? e.Id;
            _byKey[key] = e;
            _defs[key] = MakeDef(key, b.Default, b.Schema, b.ClampOnRead, b.Sensitive);
        }
        Defaults = _defs.ToDictionary(k => k.Key, k => k.Value.Default, StringComparer.Ordinal);
    }

    private SettingIndex(Dictionary<string, Def> defs)
    {
        _defs = defs;
        Defaults = defs.ToDictionary(k => k.Key, k => k.Value.Default, StringComparer.Ordinal);
    }

    private static Def MakeDef(string key, JsonNode? def, JsonObject schema, bool clamp, bool sensitive)
        => new(key, def, schema, clamp, sensitive, Num(schema["minimum"]), Num(schema["maximum"]), IsIntegerOnly(schema));

    /// <summary>Şemanın türü yalnız `integer` (ve isteğe bağlı `null`) ise tam sayı ayardır.</summary>
    private static bool IsIntegerOnly(JsonObject schema)
    {
        var types = schema["type"] switch
        {
            JsonArray ta => ta.Select(t => (t as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null).ToList(),
            JsonValue tv when tv.TryGetValue<string>(out var one) => new List<string?> { one },
            _ => new List<string?>(),
        };
        return types.Contains("integer") && !types.Contains("number");
    }

    /// <summary>Gömülü kayıt dosyalarını yalnız `setting` kayıtları için okur (JsonDocument, tek geçiş; düzenli ifade ve şema denetimi yok).</summary>
    private static SettingIndex FromEmbedded()
    {
        var defs = new Dictionary<string, Def>(StringComparer.Ordinal);
        foreach (var src in RegistryLoader.EmbeddedSources())
        {
            var bytes = src.Bytes.AsSpan();
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
            try
            {
                using var doc = JsonDocument.Parse(bytes.ToArray());
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    throw RegistryUnavailable.Create($"{src.Name}: entries dizisi yok");
                foreach (var e in entries.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != RegistryKinds.Setting) continue;
                    var id = e.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                    if (id is null || !e.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object
                        || !body.TryGetProperty("schema", out var schemaEl) || schemaEl.ValueKind != JsonValueKind.Object)
                        throw RegistryUnavailable.Create($"{src.Name}: ayar kaydı eksik ({id ?? "kimliksiz"})");
                    var legacy = body.TryGetProperty("legacyKey", out var lk) && lk.ValueKind == JsonValueKind.String ? lk.GetString() : null;
                    var key = legacy ?? id;
                    if (defs.ContainsKey(key)) throw RegistryUnavailable.Create($"{src.Name}: ayar anahtarı iki kez var ({key})");
                    JsonNode? def = body.TryGetProperty("default", out var dv) ? JsonNode.Parse(dv.GetRawText()) : null;
                    var schema = (JsonObject)JsonNode.Parse(schemaEl.GetRawText())!;
                    defs[key] = MakeDef(key, def, schema, Flag(body, "clampOnRead"), Flag(body, "sensitive"));
                }
            }
            catch (JsonException e) { throw RegistryUnavailable.Create($"{src.Name}: JSON okunamadı ({e.Message})"); }
        }
        if (defs.Count == 0) throw RegistryUnavailable.Create("gömülü kayıt dosyalarında hiç ayar kaydı yok");
        return new SettingIndex(defs);
    }

    private static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Ayarın sayısal alt/üst sınırı (kayıttaki `minimum`/`maximum`); yoksa null.</summary>
    public (double? Min, double? Max) Range(string key) => _defs.TryGetValue(key, out var d) ? (d.Min, d.Max) : (null, null);

    private static double? Num(JsonNode? n)
        => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>Sayıyı ayarın kayıttaki sınırına kıstırır.</summary>
    public double Clamp(string key, double value)
    {
        var (min, max) = Range(key);
        if (min is { } lo && value < lo) value = lo;
        if (max is { } hi && value > hi) value = hi;
        return value;
    }

    /// <summary>Okurken düzeltme (bkz. diğer aşırı yükleme); nedeni vermez.</summary>
    public JsonNode? Normalize(string key, JsonNode? stored) => Normalize(key, stored, out _);

    /// <summary>
    /// Veritabanından okunan eski değeri düzeltir (çökmez): kayıt şemasına uyuyorsa aynen; uymuyorsa `clampOnRead` ayarında sayı kayıt sınırına
    /// kıstırılır (tam sayı ayarda kesir atılır), öteki her durumda varsayılana düşer. Düzeltme olduysa <paramref name="reason"/> sade Türkçe nedeni verir
    /// (kişi "5 yazdım, neden 10" diye sorduğunda cevap buradan gelir); olmadıysa null. (Yol var mı gibi dosya denetimleri okurken yapılmaz, yalnız kaydederken.)
    /// </summary>
    public JsonNode? Normalize(string key, JsonNode? stored, out string? reason)
    {
        reason = null;
        if (!_defs.TryGetValue(key, out var d)) return stored;
        if (new SchemaCheck(d.Schema).Validate(stored).Count == 0) return stored;
        if (d.ClampOnRead && stored is JsonValue v && v.GetValueKind() == JsonValueKind.Number && Num(stored) is { } n && !double.IsNaN(n))
        {
            var c = Clamp(key, n);
            var wasInRange = c == n;
            var dropFraction = d.IntegerOnly && c != Math.Floor(c);
            if (dropFraction) c = Math.Truncate(c);
            reason = Sentence(d, n, c, wasInRange && dropFraction);
            return JsonValue.Create(c);
        }
        reason = d.Sensitive || d.Default is null
            ? "Kayıtlı değer bu ayarın kuralına uymuyor. Varsayılan değer kullanılıyor."
            : $"Kayıtlı değer bu ayarın kuralına uymuyor. Varsayılan değer ({Show(d.Default)}) kullanılıyor.";
        return d.Default?.DeepClone();
    }

    private static string Sentence(Def d, double stored, double applied, bool onlyFraction)
    {
        var rule = onlyFraction ? "tam sayı olmalı" : (d.Min, d.Max) switch
        {
            ({ } lo, { } hi) => $"{Fmt(lo)} ile {Fmt(hi)} arasında olmalı",
            ({ } lo, null) => $"en az {Fmt(lo)} olmalı",
            (null, { } hi) => $"en çok {Fmt(hi)} olmalı",
            _ => "geçerli bir sayı olmalı",
        };
        return $"Kayıtlı değer {Fmt(stored)}, bu ayar {rule}. {Fmt(applied)} kullanılıyor.";
    }

    private static string Show(JsonNode n) { var s = n.ToJsonString(); return s.Length <= 60 ? s : s[..57] + "..."; }
    private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Kayıtta `sensitive` işaretli ayar mı? Öyleyse değeri arayüze ve doctor'a yazılmaz (şimdilik böyle ayar yok).</summary>
    public bool IsSensitive(string key) => _defs.TryGetValue(key, out var d) && d.Sensitive;
}
