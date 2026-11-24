using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soz.Core.Registry;

/// <summary>
/// Ayar değerini KAYITTAKİ tanıma göre doğrular: sınır, tür ve enum yalnız kayıttaki `schema` içinde yazılıdır; şemayla anlatılamayan dallar
/// kayıttaki `validators` adlarıyla çağrılan küçük sabit kümedir (path.exists, agent.id, model.name, prices).
/// `SozSettings.Validate` bu sınıfa devredilmiştir. Hata metinleri sade Türkçe ve bugünkü mesaj sınıfını korur ("ayar: ... olmalı").
/// </summary>
public static class SettingValidator
{
    private static readonly char[] BadPathChars = { '\n', '\r', '|', '<', '>', '&' };

    /// <summary>Kısmi güncelleme: anahtarlar eski düz anahtardır (legacyKey). Bilinmeyen anahtar ya da geçersiz değer → hata metni, yoksa null.</summary>
    public static string? Validate(RegistryLoadResult catalog, JsonObject patch)
    {
        var index = SettingIndex.Of(catalog);
        foreach (var (key, value) in patch)
        {
            if (!index.ByKey.TryGetValue(key, out var entry)) return $"Bilinmeyen ayar: {SchemaCheck.Safe(key)}";
            if (ValidateValue(entry, value) is { } err) return err;
        }
        return null;
    }

    /// <summary>Tek ayar değeri. Hata metni ayarın eski anahtarıyla (yoksa kimliğiyle) başlar.</summary>
    public static string? ValidateValue(RegistryEntry setting, JsonNode? value)
    {
        var body = setting.Setting ?? throw new ArgumentException("Bu kayıt bir ayar değil: " + setting.Id);
        var name = body.LegacyKey ?? setting.Id;

        if (new SchemaCheck(body.Schema).Validate(value).Count > 0)
        {
            // Türü ne olursa olsun kendi açıklayıcı mesajını veren doğrulayıcı (fiyat tablosu) varsa onun mesajı daha iyidir.
            foreach (var v in body.Validators)
                if (v == "prices" && Run(v, name, value) is { } own) return own;
            return Describe(name, body);
        }

        foreach (var v in body.Validators)
            if (Run(v, name, value) is { } err) return err;
        return null;
    }

    private static string? Run(string validator, string name, JsonNode? value) => validator switch
    {
        "prices" => Usage.UsagePricing.Validate(value),
        "agent.id" => value is JsonValue a && a.TryGetValue<string>(out var id) ? Agents.AgentRegistry.Check(id.Trim()) : null,
        "model.name" => value is JsonValue m && m.TryGetValue<string>(out var model) ? Soz.Core.Run.ModelName.Check(model) : null,
        "path.exists" => value is JsonValue p && p.TryGetValue<string>(out var path) ? PathError(name, path) : null,
        _ => null,
    };


    /// <summary>Yol verildiyse var olan bir dosya olmalı; "claude" gibi yalın ad PATH'te aranır (dosya denetimi yok).</summary>
    private static string? PathError(string name, string raw)
    {
        var cp = raw.Trim();
        if (cp.Length == 0) return null;
        if (cp.Contains('"') || cp.IndexOfAny(BadPathChars) >= 0) return $"{name}: geçersiz karakter var.";
        if ((cp.Contains('\\') || cp.Contains('/')) && !File.Exists(cp)) return $"{name}: bu yolda dosya yok. Programın .exe dosyasının yolunu ver.";
        return null;
    }

    // ---------------------------------------------------------------- mesaj: şemadan türetilir, sınır sayısı bir yerde (kayıtta) yazar

    /// <summary>Şema uymayınca ayar adıyla başlayan sade cümle: "maxParallelRuns: 1 ile 6 arası tam sayı olmalı". Sayılar şemadan okunur.</summary>
    internal static string Describe(string name, SettingBody body)
    {
        var s = body.Schema;
        var types = s["type"] switch
        {
            JsonArray ta => ta.Select(t => (t as JsonValue)?.GetValue<string>() ?? "").ToList(),
            JsonValue tv when tv.TryGetValue<string>(out var one) => new List<string> { one },
            _ => new List<string>(),
        };
        var nullable = types.Contains("null");
        var core = types.Where(t => t != "null").ToList();

        string rule;
        if (s["enum"] is JsonArray en && en.Count > 0)
        {
            var items = en.Select(x => x is JsonValue v && v.TryGetValue<string>(out var str) ? str : x?.ToJsonString() ?? "boş").ToList();
            rule = (items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " ya da " + items[^1]) + " olmalı";
        }
        else if (core.Count == 1 && core[0] == "boolean") rule = "true/false olmalı";
        else if (core.Count == 1 && core[0] is "number" or "integer")
        {
            var (min, max) = (Num(s["minimum"]), Num(s["maximum"]));
            var kind = core[0] == "integer" ? "tam sayı" : "sayı";
            if (min is { } lo && max is { } hi) rule = $"{Fmt(lo)} ile {Fmt(hi)} arası {kind} olmalı";
            else if (Num(s["exclusiveMinimum"]) == 0 && max is null) rule = core[0] == "integer" ? "pozitif tam sayı olmalı" : "pozitif sayı olmalı";
            else if (min is { } only) rule = $"en az {Fmt(only)} olan {kind} olmalı";
            else if (max is { } top) rule = $"en çok {Fmt(top)} olan {kind} olmalı";
            else rule = $"{kind} olmalı";
            if (nullable) rule += " (boş da olabilir)";
        }
        else if (core.Count == 1 && core[0] == "string")
        {
            if (body.Control == "shortcut") rule = "kısayol metni olmalı (ör. Ctrl+Alt+J)";
            else if (Num(s["maxLength"]) is { } ml) rule = $"en çok {Fmt(ml)} karakterlik metin olmalı";
            else rule = "metin olmalı";
        }
        else if (core.Count == 1 && core[0] == "object") rule = nullable ? "nesne olmalı (boş da olabilir)" : "nesne olmalı";
        else rule = "geçerli bir değer olmalı";
        return $"{name}: {rule}";
    }

    private static double? Num(JsonNode? n)
        => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string Fmt(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
