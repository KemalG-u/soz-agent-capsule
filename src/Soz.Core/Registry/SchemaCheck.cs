using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soz.Core.Registry;

/// <summary>
/// Küçük JSON Schema doğrulayıcı (taslak 2020-12 alt kümesi, ek paket yok). Yalnız kayıt şemasının ve ayar kayıtlarının kullandığı anahtar sözcükleri bilir
/// (<see cref="Supported"/>). Bilinmeyen sözcük sessizce yok sayılmaz: <see cref="UnsupportedKeywords"/> onu bulur, <see cref="Lint"/> reddeder.
/// Hata metinleri sade Türkçe, "yol: neden" biçiminde; yol `$.body.schema` gibidir. Hata metinleri dosyadaki DEĞERİ basmaz (alan adı ve neden yeter).
/// Kişi dosyasından gelen şema hiçbir durumda doğrulayıcıyı çökertmemeli: önce <see cref="Lint"/> (sözcük, tür, regex), sonra doğrulama (derinlik ve adım bütçesi).
/// </summary>
public sealed class SchemaCheck
{
    /// <summary>Doğrulanan anahtar sözcükler.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        "$ref", "allOf", "anyOf", "oneOf", "if", "then", "else", "type", "const", "enum", "pattern", "minLength", "maxLength",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "items", "minItems", "maxItems", "uniqueItems",
        "required", "properties", "additionalProperties", "minProperties",
    };

    /// <summary>Doğrulamayı etkilemeyen açıklama sözcükleri (yok sayılması doğru olanlar).</summary>
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal) { "$schema", "$id", "$defs", "title", "description", "$comment", "default", "examples" };

    private static readonly string[] TypeNames = { "string", "number", "integer", "boolean", "array", "object", "null" };

    /// <summary>Şema ve değer özyinelemesinin en çok derinliği; kendine başvuran `$ref` bu sınırda durur.</summary>
    public const int MaxDepth = 32;
    /// <summary>Tek doğrulamada en çok adım (üstel şişen şemaları keser).</summary>
    public const int MaxSteps = 50_000;
    /// <summary>Kişi şemasında en çok düğüm.</summary>
    public const int MaxSchemaNodes = 400;

    private readonly JsonObject _root;

    public SchemaCheck(JsonObject root) => _root = root;

    public static SchemaCheck FromJson(string json)
        => new(JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Şema bir JSON nesnesi olmalı."));

    private sealed class Ctx { public int Steps; public bool Aborted; }

    /// <summary>Değeri şemaya göre doğrular. defName verilirse kök yerine `#/$defs/&lt;ad&gt;` kullanılır. Boş liste = geçerli. Asla istisna fırlatmaz.</summary>
    public IReadOnlyList<string> Validate(JsonNode? value, string? defName = null)
    {
        var errs = new List<string>();
        try
        {
            JsonNode? schema = _root;
            if (defName is not null)
            {
                schema = Resolve("#/$defs/" + defName);
                if (schema is null) { errs.Add($"$: şemada \"{Safe(defName)}\" tanımı yok"); return errs; }
            }
            Check(schema, value, "$", errs, new Ctx(), 0);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            errs.Add($"$: şema bu değere uygulanamadı ({e.GetType().Name})");
        }
        return errs;
    }

    /// <summary>Şemada (açıklama olmayan) desteklenmeyen anahtar sözcükler: "yol: sözcük".</summary>
    public static IReadOnlyList<string> UnsupportedKeywords(JsonNode? schema)
    {
        var found = new List<string>();
        Scan(schema, "#", found, 0);
        return found;
    }

    private static void Scan(JsonNode? s, string path, List<string> found, int depth)
    {
        if (s is not JsonObject o || depth > MaxDepth) return;
        foreach (var (k, v) in o)
        {
            if (Annotations.Contains(k) && k != "$defs") continue;
            if (k == "$defs" || k == "properties")
            {
                if (v is JsonObject map) foreach (var (name, sub) in map) Scan(sub, $"{path}/{k}/{Safe(name)}", found, depth + 1);
                continue;
            }
            if (!Supported.Contains(k)) { found.Add($"{path}: {Safe(k)}"); continue; }
            switch (k)
            {
                case "items" or "if" or "then" or "else" or "additionalProperties": Scan(v, $"{path}/{k}", found, depth + 1); break;
                case "allOf" or "anyOf" or "oneOf":
                    if (v is JsonArray arr) for (var i = 0; i < arr.Count; i++) Scan(arr[i], $"{path}/{k}/{i}", found, depth + 1);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- kişi şemasını önceden denetleme

    /// <summary>
    /// Kişi dosyasından gelen bir şemayı çalıştırmadan önce denetler: yalnız desteklenen sözcükler, doğru türde değerler, geçerli ve doğrusal zamanlı
    /// (<see cref="RegexOptions.NonBacktracking"/>) düzenli ifade, yalnız yerel var olan `#/$defs/ad` başvurusu, makul büyüklük. Boş liste = güvenli.
    /// </summary>
    public static IReadOnlyList<string> Lint(JsonNode? schema, string path = "$")
    {
        var errs = new List<string>();
        var count = 0;
        LintNode(schema, path, schema as JsonObject, errs, ref count, 0);
        return errs;
    }

    private static void LintNode(JsonNode? s, string path, JsonObject? root, List<string> errs, ref int count, int depth)
    {
        if (errs.Count >= 12) return;
        if (s is JsonValue bv && bv.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return;
        if (s is not JsonObject o) { errs.Add($"{path}: şema bir nesne olmalı"); return; }
        if (++count > MaxSchemaNodes) { if (count == MaxSchemaNodes + 1) errs.Add($"{path}: şema çok büyük (en çok {MaxSchemaNodes} parça)"); return; }
        if (depth > 16) { errs.Add($"{path}: şema çok derin iç içe"); return; }

        foreach (var (k, v) in o)
        {
            var at = $"{path}.{Safe(k)}";
            if (k == "$defs")
            {
                if (v is not JsonObject defs) { errs.Add($"{at}: nesne olmalı"); continue; }
                foreach (var (dn, sub) in defs) LintNode(sub, $"{at}.{Safe(dn)}", root, errs, ref count, depth + 1);
                continue;
            }
            if (Annotations.Contains(k)) continue;
            if (!Supported.Contains(k)) { errs.Add($"{at}: desteklenmeyen anahtar sözcük"); continue; }
            switch (k)
            {
                case "$ref":
                    if (Str(v) is not { } r || !IsLocalRef(r)) errs.Add($"{at}: yalnız yerel \"#/$defs/ad\" başvurusu desteklenir");
                    else if ((root?["$defs"] as JsonObject)?[r["#/$defs/".Length..]] is null) errs.Add($"{at}: başvurulan tanım şemada yok");
                    break;
                case "type":
                    if (Str(v) is { } t) { if (!TypeNames.Contains(t)) errs.Add($"{at}: bilinmeyen tür adı"); }
                    else if (v is JsonArray ta && ta.Count > 0 && ta.Count <= TypeNames.Length && ta.All(x => Str(x) is { } n && TypeNames.Contains(n))) { }
                    else errs.Add($"{at}: tür adı ya da tür adları dizisi olmalı");
                    break;
                case "enum":
                    if (v is not JsonArray en || en.Count == 0 || en.Count > 200) errs.Add($"{at}: 1 ile 200 öğeli dizi olmalı");
                    break;
                case "pattern":
                    if (Str(v) is not { } pat) errs.Add($"{at}: metin olmalı");
                    else if (SafeRegex.Get(pat, out var perr) is null) errs.Add($"{at}: {perr}");
                    break;
                case "minLength" or "maxLength" or "minItems" or "maxItems" or "minProperties":
                    if (WholeNumber(v) is not { } n0 || n0 < 0 || n0 > 1_000_000) errs.Add($"{at}: 0 ile 1000000 arası tam sayı olmalı");
                    break;
                case "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum":
                    if (Dbl(v) is null) errs.Add($"{at}: sayı olmalı");
                    break;
                case "multipleOf":
                    if (Dbl(v) is not { } mo || !(mo > 0)) errs.Add($"{at}: sıfırdan büyük sayı olmalı");
                    break;
                case "uniqueItems":
                    if (v is not JsonValue uv || uv.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) errs.Add($"{at}: true/false olmalı");
                    break;
                case "required":
                    if (v is not JsonArray ra || ra.Count > 200 || ra.Any(x => Str(x) is null)) errs.Add($"{at}: metinlerden oluşan dizi olmalı");
                    break;
                case "properties":
                    if (v is not JsonObject props) errs.Add($"{at}: nesne olmalı");
                    else foreach (var (pn, sub) in props) LintNode(sub, $"{at}.{Safe(pn)}", root, errs, ref count, depth + 1);
                    break;
                case "items" or "if" or "then" or "else":
                    LintNode(v, at, root, errs, ref count, depth + 1);
                    break;
                case "additionalProperties":
                    LintNode(v, at, root, errs, ref count, depth + 1);
                    break;
                case "allOf" or "anyOf" or "oneOf":
                    if (v is not JsonArray arr || arr.Count == 0 || arr.Count > 50) errs.Add($"{at}: 1 ile 50 şemalı dizi olmalı");
                    else for (var i = 0; i < arr.Count; i++) LintNode(arr[i], $"{at}[{i}]", root, errs, ref count, depth + 1);
                    break;
            }
        }
    }

    /// <summary>Yerel başvuru: "#/$defs/" ve ardından 1-64 karakter (harf, rakam, alt çizgi, nokta, tire). Düzenli ifade değil döngü: ifade kurmak soğuk açılışta pahalıdır.</summary>
    private static bool IsLocalRef(string r)
    {
        const string prefix = "#/$defs/";
        if (!r.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var name = r.AsSpan(prefix.Length);
        if (name.Length is < 1 or > 64) return false;
        foreach (var ch in name)
            if (!(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-')) return false;
        return true;
    }

    // ---------------------------------------------------------------- yardımcılar (hiçbiri istisna atmaz)

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool IsNumber(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number;
    private static double? Dbl(JsonNode? v)
        => IsNumber(v) && double.TryParse(v!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    private static double Num(JsonNode v) => Dbl(v) ?? double.NaN;
    private static int? WholeNumber(JsonNode? v)
        => Dbl(v) is { } d && !double.IsInfinity(d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : null;
    private static bool? Bool(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() is JsonValueKind.True ? true : v is JsonValue jf && jf.GetValueKind() == JsonValueKind.False ? false : null;

    /// <summary>Dosyadan gelen ad ya da kimlik parçasını mesaja koymadan önce: denetim karakterleri atılır, kısaltılır.</summary>
    public static string Safe(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s) { if (!char.IsControl(ch)) sb.Append(ch); if (sb.Length >= 40) { sb.Append("..."); break; } }
        return sb.ToString();
    }

    private JsonNode? Resolve(string reference)
    {
        const string prefix = "#/$defs/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return (_root["$defs"] as JsonObject)?[reference[prefix.Length..]];
    }

    private static string TypeName(JsonNode? v) => v switch
    {
        null => "null",
        JsonObject => "nesne",
        JsonArray => "dizi",
        JsonValue jv => jv.GetValueKind() switch
        {
            JsonValueKind.String => "metin",
            JsonValueKind.Number => "sayı",
            JsonValueKind.True or JsonValueKind.False => "true/false",
            _ => "null",
        },
        _ => "bilinmeyen",
    };

    private static bool IsType(string t, JsonNode? v) => t switch
    {
        "null" => v is null,
        "string" => v is JsonValue s && s.GetValueKind() == JsonValueKind.String,
        "boolean" => v is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => IsNumber(v),
        "integer" => IsNumber(v) && Num(v!) is var d && !double.IsInfinity(d) && !double.IsNaN(d) && d == Math.Floor(d),
        "object" => v is JsonObject,
        "array" => v is JsonArray,
        _ => false,
    };

    private static string ExpectedType(string t) => t switch
    {
        "string" => "metin", "number" => "sayı", "integer" => "tam sayı", "boolean" => "true/false", "object" => "nesne", "array" => "dizi", _ => Safe(t),
    };

    /// <summary>Şemada yazan (dosya değeri olmayan) bir parçayı kısa gösterir.</summary>
    private static string Show(JsonNode? v)
    {
        var s = v?.ToJsonString() ?? "null";
        return s.Length <= 60 ? s : s[..57] + "...";
    }

    private void Check(JsonNode? schema, JsonNode? v, string path, List<string> errs, Ctx ctx, int depth)
    {
        if (ctx.Aborted) return;
        if (depth > MaxDepth) { errs.Add($"{path}: şema çok derin ya da kendine başvuruyor"); ctx.Aborted = true; return; }
        if (++ctx.Steps > MaxSteps) { errs.Add($"{path}: şema bu değer için fazla karmaşık"); ctx.Aborted = true; return; }
        if (errs.Count >= 64) return;

        if (schema is JsonValue bv && bv.GetValueKind() == JsonValueKind.True) return;
        if (schema is JsonValue fv && fv.GetValueKind() == JsonValueKind.False) { errs.Add($"{path}: bu alana değer yazılamaz"); return; }
        if (schema is not JsonObject s) return;

        if (Str(s["$ref"]) is { } reference)
        {
            if (Resolve(reference) is { } target) Check(target, v, path, errs, ctx, depth + 1);
            else errs.Add($"{path}: şemada başvurulan tanım yok");
        }
        if (s["allOf"] is JsonArray all) foreach (var sub in all) Check(sub, v, path, errs, ctx, depth + 1);

        if (s.ContainsKey("if"))
        {
            var probe = new List<string>();
            Check(s["if"], v, path, probe, ctx, depth + 1);
            if (probe.Count == 0) { if (s.ContainsKey("then")) Check(s["then"], v, path, errs, ctx, depth + 1); }
            else if (s.ContainsKey("else")) Check(s["else"], v, path, errs, ctx, depth + 1);
        }

        foreach (var key in new[] { "anyOf", "oneOf" })
        {
            if (s[key] is not JsonArray branches) continue;
            var results = branches.Select(b => { var e = new List<string>(); Check(b, v, path, e, ctx, depth + 1); return e; }).ToList();
            var passed = results.Count(r => r.Count == 0);
            if (passed == 0)
            {
                var why = string.Join(" | ", results.Select(r => r.FirstOrDefault()).Where(x => x is not null)).Trim();
                errs.Add($"{path}: değer seçeneklerden hiçbirine uymuyor ({Trunc(why)})");
            }
            else if (key == "oneOf" && passed > 1) errs.Add($"{path}: değer birden çok seçeneğe uyuyor");
        }

        if (s["type"] is { } typeNode)
        {
            var types = typeNode is JsonArray ta ? ta.Select(t => Str(t) ?? "").ToArray() : new[] { Str(typeNode) ?? "" };
            if (!types.Any(t => IsType(t, v)))
            {
                errs.Add($"{path}: tür {TypeName(v)}, beklenen {string.Join(" ya da ", types.Select(ExpectedType))}");
                return; // tür tutmuyorsa ayrıntılı sınır denetimleri anlamsız
            }
        }

        if (s.ContainsKey("const") && !JsonNode.DeepEquals(v, s["const"])) errs.Add($"{path}: değer {Show(s["const"])} olmalı");
        if (s["enum"] is JsonArray en && !en.Any(e => JsonNode.DeepEquals(v, e)))
            errs.Add($"{path}: izinli değerler listesinde yok ({Trunc(string.Join(", ", en.Select(Show)), 120)})");

        if (v is JsonValue sv && sv.GetValueKind() == JsonValueKind.String && sv.TryGetValue<string>(out var str))
        {
            if (Str(s["pattern"]) is { } pat)
            {
                var re = SafeRegex.Get(pat, out var rerr);
                if (re is null) errs.Add($"{path}: kalıp kullanılamıyor ({rerr})");
                else
                {
                    try { if (!re.IsMatch(str)) errs.Add($"{path}: beklenen kalıba uymuyor ({Trunc(pat, 100)})"); }
                    catch (RegexMatchTimeoutException) { errs.Add($"{path}: kalıp denetimi zaman aşımına uğradı"); }
                }
            }
            if (WholeNumber(s["minLength"]) is { } mn && str.Length < mn) errs.Add($"{path}: en az {mn} karakter olmalı");
            if (WholeNumber(s["maxLength"]) is { } mx && str.Length > mx) errs.Add($"{path}: en çok {mx} karakter olabilir");
        }

        if (IsNumber(v))
        {
            var d = Num(v!);
            if (Dbl(s["minimum"]) is { } mi && d < mi) errs.Add($"{path}: en az {Fmt(mi)} olmalı");
            if (Dbl(s["maximum"]) is { } ma && d > ma) errs.Add($"{path}: en çok {Fmt(ma)} olabilir");
            if (Dbl(s["exclusiveMinimum"]) is { } emi && d <= emi) errs.Add($"{path}: {Fmt(emi)} değerinden büyük olmalı");
            if (Dbl(s["exclusiveMaximum"]) is { } ema && d >= ema) errs.Add($"{path}: {Fmt(ema)} değerinden küçük olmalı");
            if (Dbl(s["multipleOf"]) is { } mo && mo > 0 && Math.Abs(d / mo - Math.Round(d / mo)) > 1e-9) errs.Add($"{path}: {Fmt(mo)} değerinin katı olmalı");
        }

        if (v is JsonArray arr)
        {
            if (WholeNumber(s["minItems"]) is { } mi && arr.Count < mi) errs.Add($"{path}: en az {mi} öğe olmalı");
            if (WholeNumber(s["maxItems"]) is { } ma && arr.Count > ma) errs.Add($"{path}: en çok {ma} öğe olabilir");
            if (Bool(s["uniqueItems"]) == true)
                for (var i = 0; i < arr.Count; i++)
                    for (var j = i + 1; j < arr.Count; j++)
                        if (JsonNode.DeepEquals(arr[i], arr[j])) { errs.Add($"{path}: aynı öğe iki kez yazılmış"); i = arr.Count; break; }
            if (s["items"] is { } items) for (var i = 0; i < arr.Count; i++) Check(items, arr[i], $"{path}[{i}]", errs, ctx, depth + 1);
        }

        if (v is JsonObject obj)
        {
            if (s["required"] is JsonArray req)
                foreach (var r in req) if (Str(r) is { } name && !obj.ContainsKey(name)) errs.Add($"{path}: zorunlu alan yok: {Safe(name)}");
            if (WholeNumber(s["minProperties"]) is { } mp && obj.Count < mp) errs.Add($"{path}: en az {mp} alan olmalı");
            var props = s["properties"] as JsonObject;
            foreach (var (k, sub) in props ?? new JsonObject())
                if (obj.TryGetPropertyValue(k, out var child)) Check(sub, child, $"{path}.{Safe(k)}", errs, ctx, depth + 1);
            foreach (var (k, child) in obj)
            {
                if (props is not null && props.ContainsKey(k)) continue;
                var ap = s["additionalProperties"];
                if (ap is JsonValue av && av.GetValueKind() == JsonValueKind.False) errs.Add($"{path}: bilinmeyen alan: {Safe(k)}");
                else if (ap is JsonObject) Check(ap, child, $"{path}.{Safe(k)}", errs, ctx, depth + 1);
            }
        }
    }

    private static string Fmt(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Trunc(string s, int max = 160) => s.Length <= max ? s : s[..(max - 3)] + "...";
}

/// <summary>
/// Düzenli ifade üretimi: yalnız doğrusal zamanlı motor (<see cref="RegexOptions.NonBacktracking"/>), geri başvuru, bakış ve atomik grup kabul edilmez.
/// Böylece kişi dosyasındaki kalıp (`(a+)+$` gibi) hiçbir girdiyle işlemciyi kilitleyemez. Üretilen ifadeler önbelleğe alınır.
/// </summary>
public static class SafeRegex
{
    public const int MaxPatternLength = 512;
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>Kalıp geçerliyse ifadeyi, değilse null ve sade Türkçe nedeni döner (kalıbın kendisi nedene yazılmaz).</summary>
    public static Regex? Get(string pattern, out string? error)
    {
        error = null;
        if (pattern.Length > MaxPatternLength) { error = $"kalıp çok uzun (en çok {MaxPatternLength} karakter)"; return null; }
        if (Cache.TryGetValue(pattern, out var hit)) return hit;
        try
        {
            var re = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(500));
            if (Cache.Count < 256) Cache[pattern] = re;
            return re;
        }
        catch (NotSupportedException) { error = "kalıp geri başvuru, bakış ya da atomik grup içeriyor. Bunlar desteklenmez"; return null; }
        catch (ArgumentException) { error = "kalıp geçerli bir düzenli ifade değil"; return null; }
    }
}
