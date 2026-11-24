using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Soz.Core;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Text;

namespace Soz.Server;

/// <summary>
/// Telefon (M11, sözleşme bölüm 4). Varsayılan KAPALI: lanEnabled=false iken yerel ağ adresinde HİÇBİR şey dinlemez.
/// Açılınca yerel ağ adres(ler)inde AYRI bir küçük uygulama açılır; bu uygulamada yalnız /t/* (telefon sayfası) ve
/// /api/phone/now, /api/phone/queue vardır. Ayar, defter, arama, içe alma, kanıt dosyası, SSE bu uygulamada YOKTUR (yapısal yalıtım).
/// Kimlik: Ayarlar'daki eşleştirme adresindeki 128 bit kod → HttpOnly, SameSite=Strict çerez (kodun SHA-256 türevi). Kod yenilenince eski çerezler geçersiz olur.
/// </summary>
public sealed class PhoneServer : IAsyncDisposable
{
    public const string CookieName = "soz_t";
    public const int MaxQueueText = 1000;
    public const int MaxBodyBytes = 16 * 1024;
    public const int PhoneTextChars = 200;
    public const int QueuePerMinute = 10;
    public const int PairFailPerMinute = 10;
    private const string CodeKey = "_phoneCode";

    /// <summary>Sınama için: yerel ağ adresi yerine dinlenecek adresler (ör. 127.0.0.1). null = gerçek yerel ağ adresleri.</summary>
    public static IReadOnlyList<IPAddress>? AddressOverride { get; set; }

    private readonly SozService _service;
    private readonly WorkService _work;
    private readonly int _preferredPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new();

    public PhoneServer(SozService service, int preferredPort)
    {
        _service = service;
        _work = new WorkService(service.Db) { Actor = "telefon" };
        _preferredPort = preferredPort;
    }

    public bool Listening => _app is not null;
    public IReadOnlyList<string> Urls { get; private set; } = Array.Empty<string>();
    public string? LastError { get; private set; }

    // ------------------------------------------------------------ kod ve çerez
    public string Code()
    {
        using var c = _service.Db.Open();
        var code = SozSettings.GetInternal(c, CodeKey);
        if (code is { Length: 32 }) return code;
        code = NewCode();
        SozSettings.SetInternal(c, CodeKey, code);
        return code;
    }

    public string Renew()
    {
        using var c = _service.Db.Open();
        var code = NewCode();
        SozSettings.SetInternal(c, CodeKey, code);
        Events.Log(c, "phone_code_renewed", null, null);
        return code;
    }

    private static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // 128 bit

    public static string CookieFor(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("soz-phone-cookie-v1|" + code))).ToLowerInvariant();

    private static bool FixedEquals(string? a, string b)
        => a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public bool CodeMatches(string? given) => given is { Length: 32 } && FixedEquals(given.ToLowerInvariant(), Code());
    public bool CookieMatches(string? given) => given is { Length: 64 } && FixedEquals(given, CookieFor(Code()));

    // ------------------------------------------------------------ hız sınırı
    private bool Allow(string key, int perMinute)
    {
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            var now = DateTime.UtcNow;
            while (q.Count > 0 && (now - q.Peek()).TotalSeconds > 60) q.Dequeue();
            if (q.Count >= perMinute) return false;
            q.Enqueue(now);
            return true;
        }
    }

    // ------------------------------------------------------------ yaşam döngüsü
    /// <summary>Ayara göre aç ya da kapat. Kapalıysa yerel ağda dinleyen hiçbir şey kalmaz.</summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var want = _service.Settings().LanEnabled;
            if (!want) { await StopCoreAsync().ConfigureAwait(false); return; }
            if (_app is not null) return;
            _ = Code(); // ilk açılışta kod üret
            var addrs = AddressOverride ?? LanAddresses();
            if (addrs.Count == 0) { LastError = "Yerel ağ adresi bulunamadı (bilgisayar bir ağa bağlı olmayabilir)."; return; }
            Exception? last = null;
            foreach (var port in Enumerable.Range(0, 21).Select(i => _preferredPort + i).Where(p => p is > 0 and <= 65535))
            {
                var app = Build(addrs, port);
                try
                {
                    await app.StartAsync(ct).ConfigureAwait(false);
                    _app = app;
                    var feat = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                    Urls = feat?.Addresses.Select(a => a.TrimEnd('/')).ToList() ?? new List<string>();
                    LastError = null;
                    return;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    last = e;
                    await app.DisposeAsync().ConfigureAwait(false);
                }
            }
            LastError = "Telefon bağlantısı açılamadı. Bilgisayarın yerel ağa bağlı olduğundan ve bir programın bu bağlantıyı engellemediğinden emin ol.";
        }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var app = _app;
        _app = null;
        Urls = Array.Empty<string>();
        if (app is null) return;
        try { await app.StopAsync().ConfigureAwait(false); } catch (Exception) { }
        await app.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Özel (RFC 1918) IPv4 adresleri; ağ geçidi olan arabirim önce.</summary>
    public static List<IPAddress> LanAddresses()
    {
        var list = new List<(IPAddress Ip, bool Gw)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                var gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ua.Address)) list.Add((ua.Address, gw));
            }
        }
        catch (NetworkInformationException) { }
        return list.OrderByDescending(x => x.Gw).Select(x => x.Ip).Distinct().ToList();
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || IPAddress.IsLoopback(ip);
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Yerel ağdan gelen istek kaynağı kabul edilir mi: özel ağ, bağlantı-yerel ya da (yalnız sınamada) geri döngü.</summary>
    private static bool SourceAllowed(IPAddress? ip) => ip is not null && (IsPrivate(ip) || IPAddress.IsLoopback(ip));

    // ------------------------------------------------------------ uygulama
    private WebApplication Build(IReadOnlyList<IPAddress> addrs, int port)
    {
        var wwwT = Path.Combine(AppContext.BaseDirectory, "wwwroot", "t");
        Directory.CreateDirectory(wwwT);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, WebRootPath = wwwT });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxBodyBytes;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            k.Limits.MaxRequestLineSize = 4096;
            k.Limits.MaxConcurrentConnections = 32;
            foreach (var a in addrs) k.Listen(a, port);
        });
        var app = builder.Build();
        var hosts = new HashSet<string>(addrs.SelectMany(a => new[] { a.ToString(), $"{a}:{port}" }), StringComparer.OrdinalIgnoreCase);

        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Cache-Control"] = "no-store";
            h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

            // 1) Kaynak: yalnız yerel ağ (özel adres) ya da sınamada geri döngü.
            if (!SourceAllowed(ctx.Connection.RemoteIpAddress)) { await Deny(ctx, 403, "Yalnız yerel ağdan erişilebilir."); return; }
            // 2) Host başlığı: dinlenen adreslerden biri olmalı (DNS yeniden bağlama ve Host oyunlarına karşı).
            if (!hosts.Contains(ctx.Request.Host.Value ?? "")) { await Deny(ctx, 400, "Bu adresle bağlanılamıyor. Bilgisayardaki Ayarlar > Telefon bölümünde yazan adresi kullan."); return; }
            // 3) Ham hedefte kaçış ve kodlanmış ayraç yok.
            var raw = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
            var rawPath = raw.Split('?')[0];
            if (rawPath.Contains("..", StringComparison.Ordinal) || rawPath.Contains('\\') || rawPath.Contains("%2e", StringComparison.OrdinalIgnoreCase)
                || rawPath.Contains("%2f", StringComparison.OrdinalIgnoreCase) || rawPath.Contains("%5c", StringComparison.OrdinalIgnoreCase) || rawPath.Contains("%00", StringComparison.Ordinal))
            { await Deny(ctx, 400, "Bu adres geçersiz."); return; }
            // 4) İzinli yollar dışında hiçbir şey yok.
            var p = ctx.Request.Path.Value ?? "";
            var isPage = p == "/t" || p.StartsWith("/t/", StringComparison.Ordinal);
            var isApi = p is "/api/phone/now" or "/api/phone/queue";
            if (!isPage && !isApi) { await Deny(ctx, 404, "Bu adres telefondan kullanılamaz."); return; }
            if (isPage && !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))) { await Deny(ctx, 405, "Bu sayfa yalnız açılabilir."); return; }
            await next();
        });

        // Eşleştirme: /t/?k=<kod> → çerez → /t/ (kod adreste kalmaz).
        app.Use(async (ctx, next) =>
        {
            var p = ctx.Request.Path.Value ?? "";
            if (p == "/t") { ctx.Response.Redirect("/t/" + ctx.Request.QueryString.Value, false); return; }
            if (p == "/t/" && ctx.Request.Query.ContainsKey("k"))
            {
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                if (!Allow("pair:" + ip, PairFailPerMinute)) { await Deny(ctx, 429, "Çok fazla deneme. Bir dakika sonra yeniden dene."); return; }
                var given = ctx.Request.Query["k"].ToString();
                if (!CodeMatches(given)) { await Deny(ctx, 403, "Eşleştirme kodu geçersiz ya da yenilenmiş. Bilgisayardaki Ayarlar > Telefon'dan yeni kodu tara."); return; }
                ctx.Response.Cookies.Append(CookieName, CookieFor(Code()), new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", Secure = false, IsEssential = true, MaxAge = TimeSpan.FromDays(30),
                });
                Log("phone_paired");
                ctx.Response.Redirect("/t/", false);
                return;
            }
            await next();
        });

        var files = new PhysicalFileProvider(wwwT);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = "/t" });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, RequestPath = "/t", ServeUnknownFileTypes = false });
        // Yönlendirme statik dosyalardan SONRA: aksi hâlde WebApplication yönlendirmeyi en başa koyar, MapFallback her isteği yakalar
        // ve statik dosya ara katmanı (uç seçilmiş isteklere dokunmaz) /t/ sayfasını hiç sunmaz.
        app.UseRouting();

        MapApi(app);
        app.MapFallback(async ctx => await Deny(ctx, 404, "Bulunamadı."));
        return app;
    }

    private void MapApi(WebApplication app)
    {
        app.MapGet("/api/phone/now", async (HttpContext ctx) =>
        {
            if (!await Authorized(ctx)) return;
            await ctx.Response.WriteAsJsonAsync(NowPayload(), Json);
        });
        app.MapPost("/api/phone/queue", async (HttpContext ctx) =>
        {
            if (!await Authorized(ctx)) return;
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            { await Deny(ctx, 403, "Bu istek başka bir siteden geldiği için kabul edilmedi."); return; }
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (!Allow("queue:" + ip, QueuePerMinute)) { await Deny(ctx, 429, $"Dakikada en çok {QueuePerMinute} mesaj."); return; }
            if (ctx.Request.ContentLength is > MaxBodyBytes) { await Deny(ctx, 413, "Mesaj çok uzun."); return; }
            JsonObject? b;
            try
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                if (ms.Length > MaxBodyBytes) { await Deny(ctx, 413, "Mesaj çok uzun."); return; }
                b = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray())) as JsonObject;
            }
            catch (JsonException) { await Deny(ctx, 400, "Mesaj okunamadı. Yeniden dene."); return; }
            catch (BadHttpRequestException) { await Deny(ctx, 413, "Mesaj çok uzun."); return; }
            if (b is null) { await Deny(ctx, 400, "Mesaj okunamadı. Yeniden dene."); return; }
            var text = (b["text"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t.Trim() : "";
            if (text.Length == 0) { await Deny(ctx, 400, "Mesaj boş olamaz."); return; }
            if (text.Length > MaxQueueText) { await Deny(ctx, 400, $"Mesaj en çok {MaxQueueText} karakter olabilir."); return; }
            long? sid = Num(b, "sessionId"), pid = Num(b, "projectId");
            if (sid is null && pid is null) { await Deny(ctx, 400, "Mesajın hangi pencere ya da projeye gideceği belli değil."); return; }
            try
            {
                var q = _work.Enqueue(sid is null ? pid : null, sid, text, "telefon");
                ctx.Response.StatusCode = 201;
                await ctx.Response.WriteAsJsonAsync(new { ok = true, id = q.Id }, Json);
            }
            catch (SozException e) { await Deny(ctx, e.Status, e.Message); }
        });
    }

    private static long? Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    private async Task<bool> Authorized(HttpContext ctx)
    {
        var cookie = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(cookie)) { await Deny(ctx, 401, "Bu telefon bilgisayarla eşleşmemiş. Bilgisayardaki Ayarlar > Telefon'dan kodu tara."); return false; }
        if (!CookieMatches(cookie)) { await Deny(ctx, 403, "Eşleşme geçersiz, kod yenilenmiş olabilir. Telefonu yeniden eşleştir."); return false; }
        return true;
    }

    /// <summary>Telefona giden her metin sır süzgecinden geçer ve en çok 200 karakterdir.</summary>
    public static string? Short(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = SecretFilter.MaskSecrets(s.ReplaceLineEndings(" ").Trim());
        return m.Length <= PhoneTextChars ? m : m[..(PhoneTextChars - 1)] + "…";
    }

    public object NowPayload()
    {
        var now = _work.Now();
        var morning = _work.Morning();
        double? usd = null;
        try
        {
            var u = JsonSerializer.SerializeToNode(Soz.Core.Usage.UsageQuery.Build(_service.Db, 1), Json);
            if (u?["estimatedCostUsd"] is JsonValue v && v.TryGetValue<double>(out var d)) usd = d;
        }
        catch (Exception) { }
        // B13: telefon, bilgisayardaki "Sıra bende" ile AYNI öncelik tablosunu gösterir (tek kaynak: AttentionService, sıra yalnız sunucuda).
        var attn = _service.Attention.Snapshot();
        return new
        {
            sentence = Short(attn.Sentence),
            counts = attn.Counts,
            items = attn.Items.Select(i => new
            {
                id = i.Id,
                kind = i.Kind,
                reason = i.Reason,
                priority = i.Priority,
                title = Short(i.Title),
                sentence = Short(i.Sentence),
                detail = Short(i.Detail),
                projectName = Short(i.ProjectName),
                sessionId = i.SessionId,
                jobId = i.JobId,
                at = i.At,
                confidence = i.Confidence,
            }).ToList(),
            running = attn.Running.Select(r => new { jobId = r.JobId, title = Short(r.Title), projectName = Short(r.ProjectName) }).ToList(),
            waiting = now.NeedsYou.Select(n => new
            {
                sessionId = n.SessionId,
                projectId = n.ProjectId,
                projectName = Short(n.ProjectName),
                tool = n.Tool,
                since = n.Since,
                lastAssistantText = Short(n.Question ?? n.Title),
                asksQuestion = n.Question is not null,
            }).ToList(),
            morning = new
            {
                passed = morning.Passed.Count,
                failed = morning.Failed.Count,
                @unchecked = morning.Unverified.Count,
                guardStops = morning.GuardBlocked,
                sentence = Short(morning.Sentence),
                usage = new { estimatedUsd = usd },
            },
        };
    }

    private void Log(string kind)
    {
        try { using var c = _service.Db.Open(); Events.Log(c, kind, null, null); } catch (Exception) { }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static async Task Deny(HttpContext ctx, int status, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = message }, Json);
    }

    /// <summary>Ayarlar'da gösterilecek eşleştirme bilgisi (yalnız 127.0.0.1 uygulamasından verilir).</summary>
    public object Pairing()
    {
        var enabled = _service.Settings().LanEnabled;
        var url = enabled && Listening && Urls.Count > 0 ? Urls[0] + "/t/?k=" + Code() : null;
        return new { enabled, listening = Listening, url, addresses = Urls, error = enabled ? LastError : null };
    }
}
