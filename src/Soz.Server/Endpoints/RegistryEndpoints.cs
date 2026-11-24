using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text;
using Soz.Core;
using Soz.Core.Registry;

namespace Soz.Server.Endpoints;

/// <summary>
/// `GET /api/registry?kind=<tür>`: tek kayıt defterini salt okunur gösterir. Gövde: <c>{ kind, entries: [...], problems: [...] }</c>.
/// Kayıtlar üretilen alanlarıyla gelir (origin: core ya da file, sha256, state, problems). Sırlar ve dosya yolları dışarı verilmez:
/// dosya yolları kaldırılır (yalnız dosya adı kalır), yol gibi görünen metinler gizlenir, `sensitive` ayarın varsayılan değeri verilmez.
/// Etkin kayıt defteri sunulur: gömülü kayıtlar + kişi klasörü (`&lt;veri&gt;\yetenek\`) + onaylar (<see cref="RegistryHost"/>).
/// `POST /api/registry/approve {id}` ve `/revoke {id}`: kişi bir öneriyi onaylar ya da onayı geri alır. YALNIZ insandan (CallerGate: ajana kapalı, jeton şart); olay defterine yazılır.
/// Cevap: `{ id, kind, state, origin, changed }`. Kayıt yoksa 404, onay beklemiyorsa 409 (sade cümle).
/// </summary>
public static class RegistryEndpoints
{
    public static RouteGroupBuilder MapRegistry(this RouteGroupBuilder api, SozService service)
    {
        api.MapGet("/registry", (string? kind) =>
        {
            if (!string.IsNullOrWhiteSpace(kind) && !RegistryKinds.IsKnown(kind))
                return Results.Json(new { error = $"Bilinmeyen tür. Bilinen türler: {string.Join(", ", RegistryKinds.All)}." }, statusCode: 400);
            return Results.Json(Build(RegistryHost.Load(service.Db), string.IsNullOrWhiteSpace(kind) ? null : kind));
        });
        api.MapPost("/registry/approve", async (HttpContext ctx) => { var id = await IdOf(ctx); return Results.Json(RegistryHost.Approve(service.Db, id)); });
        api.MapPost("/registry/revoke", async (HttpContext ctx) => { var id = await IdOf(ctx); return Results.Json(RegistryHost.Revoke(service.Db, id)); });
        return api;
    }

    private static async Task<string?> IdOf(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is 0) return null;
        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var text = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        var o = JsonNode.Parse(text) as JsonObject ?? throw new SozException(400, "Gövde bir JSON nesnesi olmalı.");
        return o["id"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;
    }

    /// <summary>
    /// Windows yol belirteçleri: sürücü yolu (C:\x), ağ yolu (\\sunucu\x) ve kullanıcı klasörü (~/x). Yol BOŞLUK içerebilir ("E:/Sample Work/urun"), bu yüzden belirteç
    /// yolun nerede bittiğini bilemez; güvenli tarafta kalır: başladığı yerden bir sonraki ayırıcıya kadar HEPSİNİ gizler (tırnak, &lt;, &gt;, |, satır sonu ya da
    /// virgül / noktalı virgül / iki nokta / nokta ve ardından boşluk). Yolun kuyruğu açıkta kalmaz; arkasındaki birkaç sözcük de gizlenebilir.
    /// </summary>
    private static readonly Regex PathToken = new(@"(?:[A-Za-z]:[\\/]|\\\\(?=\S)|(?<![\w.])~[\\/])(?:(?!, |; |: |\. )[^""'<>|\r\n])*", RegexOptions.CultureInvariant);
    public const string Hidden = "(yol gizlendi)";

    /// <summary>Yanıt gövdesi. Genel çağrıya açık: test, kişi kayıtlı bir kayıt kümesiyle de sınar.</summary>
    public static JsonObject Build(RegistryLoadResult load, string? kind)
    {
        var entries = new JsonArray();
        foreach (var e in load.Entries.Where(e => kind is null || e.Kind == kind).OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal))
            entries.Add(View(e));

        var problems = new JsonArray();
        foreach (var (p, level) in load.Rejected.Select(p => (p, "hata")).Concat(load.Warnings.Select(p => (p, "uyarı"))))
            problems.Add(new JsonObject
            {
                ["level"] = level,
                ["source"] = FileNameOnly(p.Source),
                ["id"] = p.Id,
                ["code"] = p.Code,
                ["message"] = Scrub(p.Message),
            });

        return new JsonObject { ["kind"] = kind, ["count"] = entries.Count, ["entries"] = entries, ["problems"] = problems };
    }

    private static JsonObject View(RegistryEntry e)
    {
        var o = new JsonObject
        {
            ["id"] = e.Id,
            ["kind"] = e.Kind,
            ["owner"] = e.Owner,
            ["state"] = e.State,
            ["declaredState"] = e.DeclaredState,
            ["awaitsApproval"] = RegistryHost.AwaitsApproval(e),
            ["title"] = Text(e.Title),
            ["description"] = e.Description is null ? null : Text(e.Description),
            ["keywords"] = new JsonArray(e.Keywords.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["surfaces"] = new JsonArray(e.Surfaces.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["permissions"] = new JsonArray(e.Permissions.Select(p => (JsonNode?)new JsonObject
            {
                ["cap"] = p.Cap,
                ["scope"] = p.Scope is null ? null : (p.Scope.IndexOfAny(new[] { (char)92, (char)47 }) >= 0 || PathToken.IsMatch(p.Scope) ? Hidden : p.Scope),
                ["why"] = Text(p.Why),
            }).ToArray()),
            ["stability"] = e.Stability,
            ["since"] = e.Since,
            ["deprecated"] = e.Deprecated is null ? null : new JsonObject { ["since"] = e.Deprecated.Since, ["replacedBy"] = e.Deprecated.ReplacedBy, ["message"] = e.Deprecated.Message is null ? null : Text(e.Deprecated.Message) },
            ["contract"] = e.Contract is null ? null : new JsonObject
            {
                ["discover"] = e.Contract.Discover is null ? null : Text(e.Contract.Discover),
                ["disable"] = e.Contract.Disable is null ? null : Text(e.Contract.Disable),
                ["onError"] = e.Contract.OnError is null ? null : Text(e.Contract.OnError),
                ["onNewTool"] = e.Contract.OnNewTool is null ? null : Text(e.Contract.OnNewTool),
            },
            // Üretilen alanlar
            ["origin"] = e.Origin == "core" ? "core" : "file",
            ["sha256"] = e.Sha256,
            ["problems"] = new JsonArray(e.Problems.Select(p => (JsonNode?)JsonValue.Create(Scrub(p))).ToArray()),
        };
        var body = (JsonObject)e.Body.DeepClone();
        if (e.Setting is { Sensitive: true }) body["default"] = null;
        o["body"] = ScrubNode(body);
        return o;
    }

    private static JsonNode Text(LocalizedText t) => new JsonObject { ["tr"] = Scrub(t.Tr), ["en"] = t.En is null ? null : Scrub(t.En) };

    private static string FileNameOnly(string source) => source.Contains('\\') || source.Contains('/') ? Path.GetFileName(source.Replace('\\', '/')) : source;

    private static string Scrub(string s) => PathToken.Replace(s, Hidden);

    /// <summary>Gövdedeki yol gibi görünen her metin gizlenir (anahtarlar korunur). Yeni ağaç döndürür; kaynağı değiştirmez.</summary>
    private static JsonNode? ScrubNode(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ScrubNode(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(ScrubNode).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Scrub(s)),
        null => null,
        _ => n.DeepClone(),
    };
}
