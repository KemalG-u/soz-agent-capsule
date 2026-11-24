using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Soz.Core;
using Soz.Core.Data;

namespace Soz.Server;

/// <summary>Kabuk ve CLI'nin ortak açılış noktası. Yalnız 127.0.0.1'de dinler.</summary>
public static class SozServer
{
    public const int DefaultPort = 49300;

    /// <summary>Sunucu başlarken profile_snapshot boşsa arka planda otomatik ilk tarama başlatılsın mı? Üretimde açık, birim testlerinde TestInit ile kapalıdır.</summary>
    public static bool AutoScanOnStartup { get; set; } = true;

    /// <summary>Sunucuyu başlatır. Port sırası: istenen → sonraki 20 → işletim sisteminin verdiği boş port.</summary>
    public static async Task<SozServerHandle> StartAsync(string dataDir, int port = DefaultPort, CancellationToken ct = default, bool? autoScan = null, Action<Soz.Core.Run.RunManager>? configureRuns = null)
    {
        Directory.CreateDirectory(dataDir);
        var service = SozService.Open(dataDir);
        try
        {
            using var rc = service.Db.Open();
            Soz.Core.Proof.ProofStore.ReapOrphans(rc, Soz.Core.Jobs.WorkService.GateBudgetSeconds(SozSettings.Load(rc)));
        }
        catch (Exception) { /* yetim temizliği açılışı engellemez */ }
        var runs = new Soz.Core.Run.RunManager(service.Db);
        configureRuns?.Invoke(runs); // testler: açılış kanca eşitlemesinden ÖNCE soz.exe yolunu verebilsin (yarış olmasın)
        try { runs.RecoverOnStartup(); } // "running" kalmış eski kayıtlar "stopped (yarım kaldı)" olur
        catch (Exception) { /* açılışı engellemez */ }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var hub = new EventHub();

        Exception? last = null;
        var candidates = Enumerable.Range(0, 21).Select(i => port + i).Where(p => p is > 0 and <= 65535).Append(0);
        foreach (var tryPort in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (tryPort != 0 && !IsFree(tryPort, out var probe)) { last = probe; continue; }
            var phone = new PhoneServer(service, tryPort == 0 ? port : tryPort);
            var app = Build(service, token, hub, tryPort, phone, runs);
            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
                var url = ActualUrl(app) ?? $"http://127.0.0.1:{tryPort}";
                var handle = new SozServerHandle(app, url, token, service.Db.DataDir, hub, phone, runs) { Service = service };
                handle.WriteServerFile();
                handle.BindHooks(service.Db);
                handle.StartHookSync(); // hızlı kip açıksa kancalar http'ye alınır (arka planda; kapalıysa yalnız eski http kalıntısı komuta döner)
                try { await phone.ApplyAsync(ct).ConfigureAwait(false); } catch (Exception) { /* telefon açılamasa da ana sunucu çalışır; Ayarlar hatayı gösterir */ }
                if (autoScan ?? AutoScanOnStartup)
                {
                    StartInitialProfileScanIfEmpty(app, service);
                    StartAutoImport(app, service, hub); // n6: oturumlar kişi "Yenile" demeden de içe alınır
                }
                return handle;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                await app.DisposeAsync().ConfigureAwait(false);
                await phone.DisposeAsync().ConfigureAwait(false);
            }
        }
        throw new IOException($"{port} ve sonraki 20 port içinde ya da sistemin verdiği portta sunucu açılamadı." + (last is null ? "" : " Son hata: " + last.Message), last);
    }

    private static string? ActualUrl(WebApplication app)
    {
        var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (addr is null) return null;
        return addr.Replace("localhost", "127.0.0.1").Replace("[::1]", "127.0.0.1").TrimEnd('/');
    }

    private static bool IsFree(int port, out Exception? error)
    {
        try
        {
            using var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            error = null;
            return true;
        }
        catch (SocketException ex) { error = ex; return false; }
    }

    private static WebApplication Build(SozService service, string token, EventHub hub, int port, PhoneServer phone, Soz.Core.Run.RunManager runs)
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(wwwroot);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = wwwroot,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });

        var app = builder.Build();
        var tokenBytes = Encoding.ASCII.GetBytes(token);

        // Güvenlik: Host yalnız 127.0.0.1/localhost; başka kökenden gelen API isteği reddedilir; değişiklik yapan uçlar belirteç ister.
        app.Use(async (ctx, next) =>
        {
            // Bu uygulama yalnız 127.0.0.1'de dinler; yine de geri döngü dışından gelen her istek reddedilir (telefon ayrı uygulamadır).
            if (ctx.Connection.RemoteIpAddress is { } rip && !IPAddress.IsLoopback(rip))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = "Yalnız bu bilgisayardan erişilebilir." });
                return;
            }
            var host = ctx.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost"))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Geçersiz Host başlığı." });
                return;
            }
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var isLocalHook = HttpMethods.IsPost(ctx.Request.Method)
                    && ctx.Request.Path.Value is { } hp
                    && hp.StartsWith("/api/hooks/", StringComparison.OrdinalIgnoreCase)
                    && !hp.Equals("/api/hooks/health", StringComparison.OrdinalIgnoreCase);

                var origin = ctx.Request.Headers.Origin.ToString();
                if (isLocalHook && origin.Length > 0)
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Kanca ucu tarayıcı kökeninden çağrılamaz." });
                    return;
                }
                if (origin.Length > 0 && !IsSameOrigin(origin, ctx))
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Başka kökenden istek kabul edilmez." });
                    return;
                }
                // Denetim 3, İlke 2: jeton ve yazma uçları yalnız insana. Yerel kanca ucu (/api/hooks/{eventName}) ajan alt sürecinden gelir, yalnız HookHandler çalıştırır.
                if (!isLocalHook && CallerGate.NeedsHuman(ctx))
                {
                    var who = CallerGate.Get(ctx, service.Db.DataDir);
                    if (who.Kind != Soz.Core.Security.CallerKind.Human)
                    {
                        CallerGate.LogDenied(service.Db, ctx, who);
                        ctx.Response.StatusCode = 403;
                        await ctx.Response.WriteAsJsonAsync(new { error = Soz.Core.Security.CallerIdentity.MessageFor(who) });
                        return;
                    }
                }
                if (!isLocalHook && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
                {
                    var given = Encoding.ASCII.GetBytes(ctx.Request.Headers["X-Soz-Token"].ToString());
                    if (!CryptographicOperations.FixedTimeEquals(given, tokenBytes))
                    {
                        ctx.Response.StatusCode = 401;
                        await ctx.Response.WriteAsJsonAsync(new { error = "X-Soz-Token eksik ya da yanlış." });
                        return;
                    }
                }
                try { await next(); }
                catch (SozException e)
                {
                    if (!ctx.Response.HasStarted)
                    {
                        ctx.Response.StatusCode = e.Status;
                        await ctx.Response.WriteAsJsonAsync(new { error = e.Message });
                    }
                }
                catch (JsonException e)
                {
                    if (!ctx.Response.HasStarted)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Geçersiz JSON: " + e.Message });
                    }
                }
                catch (BadHttpRequestException e)
                {
                    if (!ctx.Response.HasStarted)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Geçersiz istek: " + e.Message });
                    }
                }
                catch (Exception) when (!ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested)
                {
                    // Öteki beklenmeyen hatalar boş gövdeli 500 vermez (d1f): kişiye gösterilebilir sade bir cümle, JSON gövdeyle. Ayrıntı yanıta konmaz.
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Beklenmeyen bir hata oldu. Söz'ü kapatıp yeniden açmayı dene. Sürerse `soz doctor` çıktısına bak." });
                }
                return;
            }
            await next();
        });

        // Çıkış maskesi (B4): bütün /api JSON cevaplarında anahtar benzeri değerler maskelenir (güvenlik ara katmanının İÇİNDE, uç noktaların dışında).
        app.Use(OutputMask.Middleware);

        app.UseDefaultFiles();
        app.UseStaticFiles();

        var api = app.MapGroup("/api");
        api.MapGet("/health", () => Results.Json(new { ok = true }));
        Soz.Server.Endpoints.GuardUsageEndpoints.Map(api, service, hub, app.Lifetime); // Bekçi + Harcama (B)
        Soz.Server.Endpoints.JobsProofEndpoints.MapJobsProof(api, service, hub, runs); // İş kartı + Kanıt Kapısı + kuyruk + sabah/Bugün (A)
        Soz.Server.Endpoints.RegistryEndpoints.MapRegistry(api, service); // Kayıt defteri: etkin katalog + onayla/geri al (d1c, d2m)
        Soz.Server.Endpoints.DoneEndpoints.MapDone(api, service); // Bitti'nin tanımı: sorular, kanıt önerisi, kabul, onay listesi (d2a)
        Soz.Server.Endpoints.UndoEndpoints.MapUndo(api, runs); // Geri alma: koruma durumu, önizleme, uygula, geri almayı geri al (d2b)
        Soz.Server.Endpoints.DiscoveryEndpoints.MapDiscovery(api, service); // Keşif: sabit yerleşim, kullanım, gölge kip öneri, arama (d2d)
        Soz.Server.Endpoints.OnboardingEndpoints.MapOnboarding(api, service); // Tanışma: "Seni tanıyorum" (d2e)
        Soz.Server.Endpoints.FolderEndpoints.MapFolders(api); // Klasör gezme: tarayıcıda "klasör ekle" için sürücü ve alt klasör listesi (yalnız insan, yalnız klasör adı)
        Soz.Server.Endpoints.AttentionEndpoints.MapAttention(api, service, hub); // "Sıra bende" tek akışı (d2c)
        Soz.Server.Endpoints.ShellBridge.MapShellPrefs(api); // masaüstü kabuğunun tercihleri (açılışta başlama, bildirimler); kabuk yoksa available:false
        Soz.Server.Endpoints.FeedEndpoints.MapFeed(api, service); // "Ne oldu": olay defterini sade satırlarla okur (b2)
        api.MapGet("/session", () => Results.Json(new { token, version = SozInfo.Version, dataDir = service.Db.DataDir, dataDirSource = SourceLocator.SourceOf(service.Db.DataDir), language = service.Settings().Language }));
        // Telefon eşleştirme (yalnız bu bilgisayardan; yerel ağ uygulamasında bu uçlar YOK).
        api.MapGet("/phone/pairing", () => Results.Json(phone.Pairing()));
        api.MapPost("/phone/pairing/renew", () => { phone.Renew(); return Results.Json(phone.Pairing()); });
        api.MapGet("/summary", () => Results.Json(service.Summary()));
        api.MapGet("/projects", () => Results.Json(service.Projects()));
        api.MapGet("/decisions", (long? projectId, string? status, string? type) => Results.Json(service.Decisions(projectId, Empty(status), Empty(type))));
        api.MapPost("/decisions", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var d = service.CreateDecision(Long(b, "projectId") ?? throw new SozException(400, "projectId gerekli."), Str(b, "type") ?? "rule", Str(b, "text") ?? "");
            hub.Publish("decision", new { d.Id, d.Status });
            return Results.Json(d, statusCode: 201);
        });
        api.MapPatch("/decisions/{id:long}", async (long id, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var d = service.PatchDecision(id, Str(b, "status"), Str(b, "text"), Str(b, "type"), Bool(b, "pinned"), Long(b, "projectId"));
            hub.Publish("decision", new { d.Id, d.Status });
            return Results.Json(d);
        });
        api.MapGet("/repeats", (long? projectId, int? min) => Results.Json(service.Repeats(projectId, min ?? 2)));
        api.MapPost("/repeats/{clusterId:long}/promote", async (long clusterId, HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var d = service.Promote(clusterId, Str(b, "type"));
            hub.Publish("decision", new { d.Id, d.Status });
            return Results.Json(d);
        });
        api.MapGet("/utterances", (string? q, long? projectId, int? limit, int? offset) => Results.Json(service.Utterances(q, projectId, limit ?? 50, offset ?? 0)));
        api.MapGet("/packet", (long? projectId, int? budget) => Results.Json(service.Packet(projectId, budget)));
        api.MapPost("/packet/copied", (long? projectId) => { service.LogEvent("packet_copy", projectId); return Results.Json(new { ok = true }); });
        api.MapPost("/import", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var since = (int?)Long(b, "sinceDays");
            var sum = await Task.Run(() => service.Import(new ImportOptions { SinceDays = since }));
            hub.Publish("import", new { sum.NewUtterances, sum.NewCandidates, sum.NewProposals, sum.NewRepeatClusters, sum.TotalMs });
            if (sum.NewProposals > 0) hub.Publish("proposal", new { count = sum.NewProposals });
            if (sum.NewRepeatClusters > 0) hub.Publish("repeat", new { count = sum.NewRepeatClusters });
            var s = service.Summary();
            hub.Publish("session", new { s.Waiting, s.Working, s.Stalled });
            return Results.Json(sum);
        });
        api.MapGet("/report", (int? days) => Results.Json(service.Report(days ?? 30)));
        api.MapGet("/sessions", (double? hours, bool? includeStale) => Results.Json(Soz.Server.Endpoints.OzetEndpoints.KisaOzetEkle(service, service.Sessions(hours ?? 24, includeStale ?? false))));
        Soz.Server.Endpoints.OzetEndpoints.MapOzet(api, service); // oturum özeti: ne yaptı, denedi mi, senden ne bekleniyor (model yok)
        api.MapGet("/digest", (double? hours) => Results.Json(service.Digest(hours ?? 24)));
        api.MapPost("/sessions/{id:long}/explain", async (long id, CancellationToken ct) =>
        {
            var r = await service.ExplainAsync(id, null, ct);
            return r.Ok ? Results.Json(new { text = r.Text }) : Results.Json(new { error = r.Text }, statusCode: 502);
        });
        api.MapGet("/settings", () =>
        {
            var st = service.Settings();
            var body = st.ToJson();
            body["adjusted"] = st.AdjustedJson(); // geriye uyumlu ek alan: okunurken sınıra çekilen ya da varsayılana düşen kayıtlı değerler (d1f)
            return Results.Json(body);
        });
        api.MapPut("/settings", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var res = service.PutSettings(b);
            if (b.ContainsKey("lanEnabled")) await phone.ApplyAsync();
            if (b.ContainsKey("hooksFastMode")) _ = Task.Run(() => SozServerHandle.SyncHooks(service.Db, runs, revertOnly: false)); // hızlı kip anahtarı: kancalar hemen yeni yola geçer
            if (b.ContainsKey("maxParallelRuns")) _ = Task.Run(() => { try { runs.PumpQueue(); } catch (Exception) { } }); // sınır genişleyince sıradaki işler başlar
            return Results.Json(res);
        });
        api.MapPost("/export", async (HttpContext ctx) =>
        {
            var b = await Body(ctx);
            var pid = Long(b, "projectId") ?? throw new SozException(400, "projectId gerekli.");
            return Results.Json(service.ExportProject(pid, Bool(b, "agents") ?? true, Bool(b, "claude") ?? false));
        });
        api.MapGet("/doctor", () => Results.Json(service.Doctor()));
        api.MapGet("/hooks/health", () => Results.Json(service.HookHealth()));
        // Kanca jetonu (veri klasöründeki kanca-jeton dosyası): Claude Code'un http kancası ve soz-kanca rölesi X-Soz-Hook-Token başlığıyla yollar. Yazma jetonundan ayrıdır.
        var hookToken = Soz.Core.Hooks.HookToken.GetOrCreate(service.Db.DataDir);
        api.MapPost("/hooks/{eventName}", async (string eventName, HttpContext ctx) =>
        {
            if (!Soz.Core.Hooks.HookToken.Equal(ctx.Request.Headers[Soz.Core.Hooks.HookInstaller.TokenHeader].ToString(), hookToken))
                return Results.Json(new { error = "Kanca jetonu eksik ya da yanlış." }, statusCode: 401);
            if (eventName is not ("session-start" or "user-prompt-submit" or "stop" or "stop-failure" or "notification" or "pre-tool-use"))
                return Results.Json(new { error = "Bilinmeyen kanca olayı." }, statusCode: 400);
            var body = await Body(ctx);
            JsonObject hookInput;
            Dictionary<string, string?> envMap = new(StringComparer.OrdinalIgnoreCase);
            if (body.ContainsKey("stdin") && body["env"] is JsonObject envObj)
            {
                foreach (var (ek, ev) in envObj)
                    if (ev is JsonValue jv && jv.TryGetValue<string>(out var es))
                        envMap[ek] = es;
                if (body["stdin"] is JsonObject stdinObj)
                    hookInput = stdinObj;
                else if (body["stdin"] is JsonValue stdinVal && stdinVal.TryGetValue<string>(out var rawStdin) && !string.IsNullOrWhiteSpace(rawStdin))
                    hookInput = JsonNode.Parse(rawStdin) as JsonObject ?? new JsonObject();
                else
                    hookInput = new JsonObject();
            }
            else
            {
                hookInput = body;
            }
            var outJson = Soz.Core.Hooks.HookHandler.RunInMemory(
                eventName,
                hookInput,
                service.Db,
                k => envMap.TryGetValue(k, out var v) ? v : null);
            // Canlı yoklamaya haber: oturumun durumu hemen yansır, yeni oturumsa bir sonraki yoklama öne çekilir (ayrıntı: LiveCadence.NoteHook). Hata kancayı bozmaz.
            try
            {
                var cadence = service.Profile.Cadence;
                string? S(string k) => hookInput[k] is JsonValue hv && hv.TryGetValue<string>(out var hs) ? hs : null;
                cadence.NoteHook(eventName, S("session_id"), S("notification_type"), cadence.Now);
            }
            catch (Exception) { }
            return string.IsNullOrEmpty(outJson)
                ? Results.Text("", "text/plain; charset=utf-8")
                : Results.Text(outJson, "application/json; charset=utf-8");
        });
        api.MapGet("/events/stream", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            // Önce abone ol, sonra geçmişi ver: araya düşen olay kaybolmaz (aynı seq iki kez gelirse aşağıda elenir).
            var reader = hub.Subscribe(out var unsubscribe);
            try
            {
                await ctx.Response.WriteAsync(": bağlandı\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                // ?since=<seq> (ya da Last-Event-ID): kopukken kaçırılan canlı olaylar event_log'dan geri verilir.
                long replayedUpTo = 0;
                if (LiveFeed.SinceOf(ctx) is { } since)
                {
                    using var rc = service.Db.Open();
                    // İstemci olmayan bir geleceği söylüyorsa (since son seq'ten büyük: veri sıfırlanmış, yanlış cihaz) son seq'e indirilir; yoksa sonraki bütün canlı olaylar "zaten geri verildi" diye elenirdi.
                    var cursor = Math.Min(since, Events.LastSeq(rc));
                    var sent = 0;
                    var truncated = false;
                    while (true)
                    {
                        var rows = Events.ReadSince(rc, cursor, Math.Min(500, LiveFeed.ReplayCap - sent), LiveFeed.Kinds);
                        if (rows.Count == 0) break;
                        foreach (var row in rows)
                        {
                            cursor = row.Seq;
                            sent++;
                            if (LiveFeed.Parse(row) is { } ev) await ctx.Response.WriteAsync(EventHub.Format(ev.Name, ev.Payload, row.Seq), ctx.RequestAborted);
                        }
                        if (sent >= LiveFeed.ReplayCap) { truncated = true; break; }
                        if (rows.Count < 500) break;
                    }
                    if (truncated) await ctx.Response.WriteAsync($"event: replay.truncated\ndata: {{\"lastSeq\":{cursor}}}\n\n", ctx.RequestAborted);
                    replayedUpTo = cursor;
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    string msg;
                    try { msg = await reader.ReadAsync(timeout.Token); }
                    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { msg = ": canlı\n\n"; }
                    if (EventHub.IdOf(msg) is { } liveId && liveId <= replayedUpTo) continue; // geri verilenle aynı olay
                    await ctx.Response.WriteAsync(msg, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            finally { unsubscribe(); }
        });
        api.MapFallback((HttpContext ctx) => { ctx.Response.StatusCode = 404; return Results.Json(new { error = "Bilinmeyen API ucu." }, statusCode: 404); });

        // SPA geri dönüşü: /api dışındaki bilinmeyen yollar index.html'e düşer.
        app.MapFallbackToFile("index.html");
        return app;
    }

    /// <summary>Oturum kayıtlarını açılışta ve her 15 saniyede artımlı içe alır (yalnız değişen dosya, düşük öncelik). Değişince panele "session" olayı gider.</summary>
    private static void StartAutoImport(WebApplication app, SozService service, EventHub hub)
    {
        var auto = new AutoImporter(service.Db, onChanged: sum =>
        {
            if (sum.NewUtterances > 0) hub.Publish("import", new { sum.NewUtterances, sum.NewCandidates, sum.NewProposals, sum.NewRepeatClusters, sum.TotalMs });
            var s = service.Summary();
            hub.Publish("session", new { s.Waiting, s.Working, s.Stalled });
        });
        app.Lifetime.ApplicationStopping.Register(auto.Dispose);
        auto.Start();
    }

    private static void StartInitialProfileScanIfEmpty(WebApplication app, SozService service)
    {
        var db = service.Db;
        try
        {
            using var c = db.Open();
            if (c.ScalarLong("SELECT COUNT(*) FROM profile_snapshot") > 0) return;
        }
        catch (Exception) { return; }

        var stopping = app.Lifetime.ApplicationStopping;
        _ = Task.Run(() =>
        {
            if (stopping.IsCancellationRequested) return;
            try
            {
                service.Profile.Scan(Soz.Server.Endpoints.OnboardingEndpoints.ContextFactory());
            }
            catch (Exception) { /* ilk arka plan taraması sunucuyu hiçbir zaman düşürmez */ }
        }, stopping);
    }

    private static bool IsSameOrigin(string origin, HttpContext ctx)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
        // Yerel köken (her port: Vite geliştirme vekili dahil). Başka siteler reddedilir; CORS başlığı hiç verilmez.
        return o.Scheme == "http" && o.Host is "127.0.0.1" or "localhost";
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static async Task<JsonObject> Body(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is 0) return new JsonObject();
        using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var text = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? throw new SozException(400, "Gövde bir JSON nesnesi olmalı.");
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static long? Long(JsonObject o, string k) => o[k] is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : null) : null;
    private static bool? Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}

/// <summary>SSE olayları için süreç içi yayın.</summary>
public sealed class EventHub
{
    private readonly List<Channel<string>> _subs = new();
    private readonly object _lock = new();

    public ChannelReader<string> Subscribe(out Action unsubscribe)
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock) _subs.Add(ch);
        unsubscribe = () => { lock (_lock) _subs.Remove(ch); ch.Writer.TryComplete(); };
        return ch.Reader;
    }

    public void Publish(string evt, object data, long? id = null)
    {
        var msg = Format(evt, data, id);
        lock (_lock) foreach (var s in _subs) s.Writer.TryWrite(msg);
    }

    /// <summary>SSE iletisi. id verilirse "id: &lt;seq&gt;" satırı eklenir (event_log sırası; EventSource Last-Event-ID olarak geri yollar).</summary>
    public static string Format(string evt, object data, long? id = null)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return id is null ? $"event: {evt}\ndata: {json}\n\n" : $"id: {id}\nevent: {evt}\ndata: {json}\n\n";
    }

    /// <summary>İletideki "id:" satırı (yoksa null).</summary>
    public static long? IdOf(string msg)
    {
        if (!msg.StartsWith("id: ", StringComparison.Ordinal)) return null;
        var end = msg.IndexOf('\n');
        return end > 4 && long.TryParse(msg.AsSpan(4, end - 4), out var n) ? n : null;
    }

    public int SubscriberCount { get { lock (_lock) return _subs.Count; } }
}

/// <summary>Çalışan sunucunun tutacağı. DisposeAsync sunucuyu durdurur.</summary>
public sealed class SozServerHandle : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _disposed;

    private readonly PhoneServer _phone;
    private readonly Soz.Core.Run.RunManager _runs;

    internal SozServerHandle(WebApplication app, string url, string token, string dataDir, EventHub hub, PhoneServer phone, Soz.Core.Run.RunManager runs)
    {
        _app = app;
        _phone = phone;
        _runs = runs;
        Url = url;
        Token = token;
        DataDir = dataDir;
        Hub = hub;
    }

    public string Url { get; }
    public string Token { get; }
    public string DataDir { get; }
    public EventHub Hub { get; }
    public PhoneServer Phone => _phone;
    public Soz.Core.Run.RunManager Runs => _runs;
    /// <summary>Sunucunun iş katmanı (deneme ve araçlar için: dış oturum kaynağı, canlı yoklama kuralları).</summary>
    public SozService Service { get; init; } = null!;

    /// <summary>Kayıtlı uç noktalar (yöntem ve yol kalıbı). Sızıntı taraması bütün okuma uçlarını bu listeden gezer.</summary>
    public IReadOnlyList<(string Method, string Pattern)> Routes()
        => _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new List<string>()).Select(m => (m, e.RoutePattern.RawText ?? "")))
            .Where(r => r.Item2.StartsWith("/api", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Kabuk ve araçlar için: veri\sunucu.json = {url, pid, startedAt} (belirteç YAZILMAZ).</summary>
    private Soz.Core.Data.SozDb? _hookDb;
    private Task? _hookSync;
    private static readonly object HookLock = new();

    internal void BindHooks(Soz.Core.Data.SozDb db) => _hookDb = db;
    internal void StartHookSync() { var db = _hookDb; if (db is not null) _hookSync = Task.Run(() => SyncHooks(db, _runs, revertOnly: false)); }

    /// <summary>
    /// Kancaların komut/http yolunu ayara göre eşitler: hızlı kip (ayar hooksFastMode ya da SOZ_HOOK_TRANSPORT=http) açıksa http, değilse komut. revertOnly: koşulsuz komuta döner.
    /// Yalnız içinde Söz kancası olan ayar dosyalarına dokunur; hata dışarı sızmaz (null döner). Aynı anda tek geçiş çalışır.
    /// </summary>
    public static Soz.Core.Hooks.HookRetargetResult? SyncHooks(Soz.Core.Data.SozDb db, Soz.Core.Run.RunManager runs, bool revertOnly)
    {
        try
        {
            lock (HookLock)
            {
                var st = Soz.Core.SozSettings.Load(db);
                var toHttp = !revertOnly && Soz.Core.Hooks.HookTransportPlanner.FastModeWanted(st.Bool("hooksFastMode"));
                var exe = runs.ResolveHookExe();
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Soz.Core.Hooks.HookTransportSwitch.Retarget(db, exe, toHttp, string.IsNullOrWhiteSpace(home) ? null : home, st.ClaudePath);
            }
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// DÜZGÜN kapanışta çağrılacak tek yöntem: kancaları komut yoluna geri çevirir (Söz kapalıyken http kancası Claude'da hata notu gösterir). DisposeAsync bunu kendisi çağırır;
    /// kabuk (Soz.Shell) çıkışta handle.DisposeAsync() çağırdığı sürece ayrıca bir şey yapmasına gerek yok. Çökmede çalışmaz.
    /// </summary>
    public Soz.Core.Hooks.HookRetargetResult? RevertHooksForShutdown()
    {
        var db = _hookDb;
        if (db is null) return null;
        try { _hookSync?.Wait(TimeSpan.FromSeconds(8)); } catch (Exception) { }
        return SyncHooks(db, _runs, revertOnly: true);
    }

    internal void WriteServerFile()
    {
        try
        {
            var json = JsonSerializer.Serialize(new { url = Url, pid = Environment.ProcessId, startedAt = Time.NowIso() });
            File.WriteAllText(Path.Combine(DataDir, "sunucu.json"), json);
        }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { RevertHooksForShutdown(); } catch (Exception) { } // önce kancalar komuta döner: sunucu kapanınca http kancası hata notu üretmesin
        try { await _runs.StopAllAsync().ConfigureAwait(false); } catch (Exception) { } // çalışan ajan süreçleri düzgün kapanır
        try { await _phone.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        try { await _app.StopAsync().ConfigureAwait(false); }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            try
            {
                var f = Path.Combine(DataDir, "sunucu.json");
                if (File.Exists(f) && File.ReadAllText(f).Contains($"\"pid\":{Environment.ProcessId}")) File.Delete(f);
            }
            catch (IOException) { }
        }
    }
}
