using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soz.Core;
using Soz.Core.Attention;
using Soz.Core.Profile;

namespace Soz.Server.Endpoints;

public static class ProfileEndpoints
{
    /// <summary>Profil görünümü, anında yeniden tarama, açık pencereye odaklanma ve proje klasörünü Gezgin'de açma uçları.</summary>
    public static RouteGroupBuilder MapProfile(this RouteGroupBuilder api, ProfileService profile)
    {
        // "Şu an açık" bölümü her istekte taze hesaplanır (kısa önbellekle); ağır tam tarama POST /profile/scan ile ayrıdır.
        // Kabuğun sessiz yoklaması X-Soz-Arka başlığını taşır: "biri bakıyor" sayılmaz, açık oturum hesabı seyrek kalır.
        api.MapGet("/profile", (HttpContext ctx) => Results.Json(profile.View(null, OnboardingEndpoints.ContextFactory,
            background: ctx.Request.Headers.ContainsKey(LiveCadence.BackgroundHeader))));
        // Tam taramanın ilerlemesi: { running, phase, done, total, percent, tool, startedAt, elapsedMs }. Arayüz ilk açılışta "x / y dosya" göstermek için yoklar.
        api.MapGet("/profile/scan-progress", () => Results.Json(profile.Progress()));
        api.MapPost("/profile/scan", () =>
        {
            var snapshot = profile.Scan(OnboardingEndpoints.ContextFactory());
            return Results.Json(ProfileService.ToJson(snapshot));
        });
        api.MapGet("/profile/sessions", (string? project, int? limit) => string.IsNullOrWhiteSpace(project)
            || limit is <= 0 or > 500 ? Results.BadRequest(new { error = "Proje yolu ve 1 ile 500 arası sınır gerekli." })
            : Results.Json(profile.Sessions(project, limit ?? 100)));

        // Pencereyi öne getirir. Sunucu kabukla aynı süreçte çalışıyorsa kabuğun eşleştiricisi (SessionActions.Focuser) doğru sekmeyi seçer; yanıtta tabSelected ve gerekirse hint gelir.
        api.MapPost("/live/focus", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var path = StrVal(b, "projectPath") ?? StrVal(b, "path");
            var title = StrVal(b, "title") ?? StrVal(b, "sessionTitle");
            if (title is null && StrVal(b, "sessionId") is { } sid) title = profile.SessionTitle(StrVal(b, "tool"), sid);
            var req = new FocusRequest(LongVal(b, "hwnd"), IntVal(b, "pid"), string.IsNullOrWhiteSpace(path) ? null : ProfileSources.CleanPath(path), title);
            var outcome = await Task.Run(() => SessionActions.FocusDirect(req, BoolVal(b, "openFolder") ?? false));
            return Reply(outcome);
        });

        api.MapPost("/live/open-folder", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var rawPath = StrVal(b, "path");
            var clean = ProfileSources.CleanPath(rawPath);
            if (string.IsNullOrWhiteSpace(clean) || !Path.IsPathFullyQualified(clean) || !Directory.Exists(clean))
                throw new SozException(400, "Açılacak geçerli bir klasör yolu gerekli.");
            var full = Path.GetFullPath(clean);
            if (!Win32ProcessInspector.OpenFolderInExplorer(full))
                throw new SozException(500, "Klasör Windows Gezgini içinde açılamadı.");
            return Results.Json(new { ok = true, path = full });
        });

        return api;
    }

    private static IResult Reply(ActionOutcome o)
    {
        var body = new JsonObject { ["ok"] = o.Ok, [o.Ok ? "message" : "error"] = o.Message };
        if (o.Data is not null && System.Text.Json.JsonSerializer.SerializeToNode(o.Data, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) is JsonObject extra)
            foreach (var (k, v) in extra) body[k] = v?.DeepClone();
        return Results.Json(body, statusCode: o.Status);
    }

    private static async Task<JsonObject> Body(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is 0) return new JsonObject();
        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var text = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? throw new SozException(400, "Gövde bir JSON nesnesi olmalı.");
    }

    private static string? StrVal(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool? BoolVal(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static int? IntVal(JsonObject o, string k) => LongVal(o, k) is { } l and >= int.MinValue and <= int.MaxValue ? (int)l : null;
    private static long? LongVal(JsonObject o, string k)
        => o[k] is JsonValue v
            ? (v.TryGetValue<long>(out var l) ? l
                : v.TryGetValue<double>(out var d) ? (long)d
                : v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null)
            : null;
}
