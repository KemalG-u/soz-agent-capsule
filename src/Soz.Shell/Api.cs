using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Soz.Shell;

internal sealed record SummaryInfo(int Projects, int Sessions, int Proposed, int Waiting, int Stalled, int Working);
internal sealed record ProjectInfo(long Id, string Name, string Path, DateTimeOffset? LastAt);
internal sealed record SessionInfo(string Id, string Tool, long ProjectId, string ProjectName, string Kind, string State,
    DateTimeOffset? LastUserTs, DateTimeOffset? LastAssistantTs, DateTimeOffset? Since);
internal sealed record PacketInfo(string Text, int Chars, int DecisionCount);
/// <summary>/api/now cevabının kapsülün ihtiyacı olan kısmı (counts nesnesi).</summary>
internal sealed record NowInfo(int Waiting, int Stalled, int JobsFailed);

/// <summary>GET api/attention öğesinden bildirim için gereken alanlar.</summary>
internal sealed record AttentionItemInfo(string Id, string Reason, string Project, string Helper, string Sentence, string Detail, string Title, string Kind = "",
    long JobId = 0, long SessionId = 0, string ExternalId = "");
/// <summary>/api/profile cevabının "defaults" bölümü: kişinin projelerinin durduğu sürücü ve yeni proje için önerilen üst klasör (profil henüz yoksa null).</summary>
internal sealed record ProfilVarsayilan(string? Surucu, string? UstKlasor);

/// <summary>Kabuğun sunucuyla konuştuğu uçlar. Sahte (FakeApiClient) ve gerçek (ApiClient) uygulamalar var.</summary>
internal interface IApiClient
{
    Task<SummaryInfo?> GetSummaryAsync(CancellationToken ct = default);
    /// <summary>GET api/now: counts.waiting, counts.stalled, counts.jobsFailed. Cevap yoksa null (çağıran özete düşer).</summary>
    Task<NowInfo?> GetNowAsync(CancellationToken ct = default);
    /// <summary>GET api/attention: bildirim için öğeler. Cevap yoksa null (bildirim bu turda atlanır).</summary>
    Task<IReadOnlyList<AttentionItemInfo>?> GetAttentionAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AttentionItemInfo>?>(null);
    /// <summary>GET api/attention: Söz'ün çalıştırdığı işlerin sayısı ("running" dizisi). Cevap yoksa null (kapatma sorusu bunu "bilmiyorum" diye ele alır).</summary>
    Task<int?> GetRunningJobCountAsync(CancellationToken ct = default) => Task.FromResult<int?>(null);
    /// <summary>GET api/profile: yalnız defaults.projeSurucusu ve defaults.yeniProjeUstKlasoru. Cevap yoksa ya da profil boşsa null. Çağıran süre sınırını <paramref name="ct"/> ile koyar.</summary>
    Task<ProfilVarsayilan?> GetProfileDefaultsAsync(CancellationToken ct = default) => Task.FromResult<ProfilVarsayilan?>(null);
    Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(int hours, CancellationToken ct = default);
    Task<PacketInfo?> GetPacketAsync(long? projectId, CancellationToken ct = default);
    Task<bool> ImportAsync(CancellationToken ct = default);
    Task<Dictionary<string, string>> GetSettingsAsync(CancellationToken ct = default);
    /// <summary>Kısmi güncelleme: yalnız verilen anahtarlar. Değerler türünde gider (sayı sayı, bool bool); sunucu yanlış türü 400 ile reddeder.</summary>
    Task<bool> PutSettingsAsync(Dictionary<string, object?> values, CancellationToken ct = default);
    /// <summary>SSE akışı: her olayda <paramref name="onEvent"/>; akış kopunca ya da hata olunca döner (istisna fırlatabilir).</summary>
    Task StreamEventsAsync(Action<string> onEvent, Action onConnected, CancellationToken ct);
}

internal sealed class ApiClient : IApiClient, IDisposable
{
    /// <summary>Sessiz yoklama başlığı; Soz.Core.Profile.LiveCadence.BackgroundHeader ile aynı olmalı (bu dosya Core'a başvurmayan test projesiyle de derlendiği için burada yazılı; selftest eşitliği sınar).</summary>
    internal const string ArkaBaslik = "X-Soz-Arka";
    private readonly HttpClient _http;
    private readonly HttpClient _stream;

    public ApiClient(string baseUrl, string token)
    {
        _http = Create(baseUrl, token, TimeSpan.FromSeconds(6));
        _stream = Create(baseUrl, token, Timeout.InfiniteTimeSpan);
    }

    private static HttpClient Create(string baseUrl, string token, TimeSpan timeout)
    {
        var c = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = timeout };
        c.DefaultRequestHeaders.Add("X-Soz-Token", token);
        return c;
    }

    public void Dispose() { _http.Dispose(); _stream.Dispose(); }

    /// <param name="arka">Kabuğun sessiz yoklaması: sunucuya "bakan yok, açık oturum hesabını seyrek tut" denir (X-Soz-Arka).</param>
    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct, bool arka = false)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (arka) req.Headers.TryAddWithoutValidation(ArkaBaslik, "1");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Write($"GET {path} başarısız: {ex.GetType().Name}: {ex.Message}"); return null; }
    }

    public async Task<SummaryInfo?> GetSummaryAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/summary", ct).ConfigureAwait(false);
        return doc == null ? null : Json.ParseSummary(doc.RootElement);
    }

    public async Task<NowInfo?> GetNowAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/now", ct).ConfigureAwait(false);
        return doc == null ? null : Json.ParseNow(doc.RootElement);
    }

    public async Task<IReadOnlyList<AttentionItemInfo>?> GetAttentionAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/attention", ct, arka: true).ConfigureAwait(false);
        return doc == null ? null : Json.ParseAttention(doc.RootElement);
    }

    public async Task<int?> GetRunningJobCountAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/attention", ct).ConfigureAwait(false);
        return doc == null ? null : Json.ParseRunningCount(doc.RootElement);
    }

    public async Task<ProfilVarsayilan?> GetProfileDefaultsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/profile", ct, arka: true).ConfigureAwait(false);
        return doc == null ? null : Json.ParseProfileDefaults(doc.RootElement);
    }

    public async Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/projects", ct).ConfigureAwait(false);
        return doc == null ? [] : Json.ParseProjects(doc.RootElement);
    }

    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(int hours, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"api/sessions?hours={hours}", ct).ConfigureAwait(false);
        return doc == null ? [] : Json.ParseSessions(doc.RootElement);
    }

    public async Task<PacketInfo?> GetPacketAsync(long? projectId, CancellationToken ct = default)
    {
        var q = projectId is { } id ? $"api/packet?projectId={id}" : "api/packet";
        using var doc = await GetJsonAsync(q, ct).ConfigureAwait(false);
        return doc == null ? null : Json.ParsePacket(doc.RootElement);
    }

    public async Task<bool> ImportAsync(CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/import", content, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Write("POST api/import başarısız: " + ex.Message); return false; }
    }

    public async Task<Dictionary<string, string>> GetSettingsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/settings", ct).ConfigureAwait(false);
        return doc == null ? new() : Json.ParseSettings(doc.RootElement);
    }

    public async Task<bool> PutSettingsAsync(Dictionary<string, object?> values, CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(values), Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync("api/settings", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) Log.Write("PUT api/settings reddedildi: " + (int)resp.StatusCode);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Write("PUT api/settings başarısız: " + ex.Message); return false; }
    }

    public async Task StreamEventsAsync(Action<string> onEvent, Action onConnected, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/events/stream");
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var resp = await _stream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new IOException("SSE " + (int)resp.StatusCode);
        onConnected();
        using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(s, Encoding.UTF8);
        string? kind = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) return; // sunucu kapattı
            if (line.StartsWith("event:")) kind = line[6..].Trim();
            else if (line.Length == 0) { if (kind != null) onEvent(kind); kind = null; }
            else if (line.StartsWith("data:") && kind == null) onEvent("message");
        }
    }
}

/// <summary>Sunucu hazır değilken (ya da --fake-api ile) kabuğu sınamak için sabit cevaplar.</summary>
internal sealed class FakeApiClient : IApiClient
{
    private readonly Dictionary<string, string> _settings = new();
    public static readonly string SamplePacket =
        "[Söz defteri · Söz] Kullanıcının daha önce verdiği, hâlâ geçerli kararlar. Bunlara uy. Çelişen istek gelirse önce sor.\n" +
        "Kurallar:\n- Her zaman Türkçe yaz.\n" +
        "Yasaklar:\n- Odak çalan pencere açma.\n";

    public Task<SummaryInfo?> GetSummaryAsync(CancellationToken ct = default) =>
        Task.FromResult<SummaryInfo?>(new SummaryInfo(2, 3, 3, 2, 1, 1));

    public Task<NowInfo?> GetNowAsync(CancellationToken ct = default) =>
        Task.FromResult<NowInfo?>(new NowInfo(2, 1, 1));

    public Task<int?> GetRunningJobCountAsync(CancellationToken ct = default) => Task.FromResult<int?>(0);

    public Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProjectInfo>>(new[]
        {
            new ProjectInfo(1, "Söz", @"E:\Sample Work\urun", DateTimeOffset.UtcNow.AddMinutes(-3)),
            new ProjectInfo(2, "Deneme Projesi", @"D:\projelerim\deneme", DateTimeOffset.UtcNow.AddHours(-5)),
        });

    public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(int hours, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SessionInfo>>(new[]
        {
            new SessionInfo("s1", "claude", 1, "Söz", "human", "waiting", DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow.AddMinutes(-3)),
        });

    public Task<PacketInfo?> GetPacketAsync(long? projectId, CancellationToken ct = default) =>
        Task.FromResult<PacketInfo?>(new PacketInfo(SamplePacket, SamplePacket.Length, 2));

    public Task<bool> ImportAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<Dictionary<string, string>> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(new Dictionary<string, string>(_settings));
    public Task<bool> PutSettingsAsync(Dictionary<string, object?> values, CancellationToken ct = default)
    {
        foreach (var kv in values) _settings[kv.Key] = kv.Value?.ToString() ?? "";
        return Task.FromResult(true);
    }

    public async Task StreamEventsAsync(Action<string> onEvent, Action onConnected, CancellationToken ct)
    {
        // Akış yok: bağlı sayılmaz, kabuk yoklamaya düşer.
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }
}

/// <summary>Cevapları gevşek ayrıştırır: alan eksikse ya da biçim farklıysa çökmez.</summary>
internal static class Json
{
    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static int Int(JsonElement e, string name)
    {
        var v = Prop(e, name);
        if (v is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var i)) return i;
        if (v is { ValueKind: JsonValueKind.String } s && int.TryParse(s.GetString(), out var j)) return j;
        return 0;
    }

    private static long Long(JsonElement e, string name)
    {
        var v = Prop(e, name);
        if (v is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var i)) return i;
        if (v is { ValueKind: JsonValueKind.String } s && long.TryParse(s.GetString(), out var j)) return j;
        return 0;
    }

    private static string Str(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : v is { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } x ? x.ToString() : "";
    }

    private static DateTimeOffset? Date(JsonElement e, string name)
    {
        var s = Str(e, name);
        return s.Length > 0 && DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    public static SummaryInfo? ParseSummary(JsonElement e) =>
        e.ValueKind != JsonValueKind.Object ? null :
        new SummaryInfo(Int(e, "projects"), Int(e, "sessions"), Int(e, "proposed"), Int(e, "waiting"), Int(e, "stalled"), Int(e, "working"));

    /// <summary>/api/now: yalnız <c>counts</c> nesnesi okunur; yoksa ya da nesne değilse null (eski özete düşülür).</summary>
    public static NowInfo? ParseNow(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (Prop(e, "counts") is not { ValueKind: JsonValueKind.Object } c) return null;
        return new NowInfo(Int(c, "waiting"), Int(c, "stalled"), Int(c, "jobsFailed"));
    }

    /// <summary>/api/attention: <c>items</c> dizisi; dizi yoksa boş liste, gövde nesne değilse null.</summary>
    public static IReadOnlyList<AttentionItemInfo>? ParseAttention(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var list = new List<AttentionItemInfo>();
        if (Prop(e, "items") is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var it in arr.EnumerateArray())
        {
            var id = Str(it, "id");
            if (id.Length == 0) continue;
            list.Add(new AttentionItemInfo(id, Str(it, "reason"), Str(it, "projectName"), Str(it, "helper"), Str(it, "sentence"), Str(it, "detail"), Str(it, "title"), Str(it, "kind"),
                Long(it, "jobId"), Long(it, "sessionId"), Str(it, "externalSessionId")));
        }
        return list;
    }

    /// <summary>/api/attention: çalışan iş sayısı. "running" dizisi yoksa 0 sayılmaz, null olur (bilinmiyor); gövde nesne değilse null.</summary>
    public static int? ParseRunningCount(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        return Prop(e, "running") is { ValueKind: JsonValueKind.Array } arr ? arr.GetArrayLength() : null;
    }

    /// <summary>/api/profile: yalnız <c>defaults</c> nesnesi okunur. İkisi de boşsa null (profil henüz yok).</summary>
    public static ProfilVarsayilan? ParseProfileDefaults(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Prop(e, "defaults") is not { ValueKind: JsonValueKind.Object } d) return null;
        var surucu = Str(d, "projeSurucusu"); var ust = Str(d, "yeniProjeUstKlasoru");
        return surucu.Length == 0 && ust.Length == 0 ? null : new ProfilVarsayilan(surucu.Length == 0 ? null : surucu, ust.Length == 0 ? null : ust);
    }

    public static List<ProjectInfo> ParseProjects(JsonElement e)
    {
        var list = new List<ProjectInfo>();
        if (e.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in e.EnumerateArray())
            list.Add(new ProjectInfo(Long(p, "id"), Str(p, "name"), Str(p, "path"), Date(p, "lastAt")));
        return list;
    }

    public static List<SessionInfo> ParseSessions(JsonElement e)
    {
        var list = new List<SessionInfo>();
        if (e.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in e.EnumerateArray())
            list.Add(new SessionInfo(Str(s, "id"), Str(s, "tool"), Long(s, "projectId"), Str(s, "projectName"), Str(s, "kind"),
                Str(s, "state"), Date(s, "lastUserTs"), Date(s, "lastAssistantTs"), Date(s, "since")));
        return list;
    }

    public static PacketInfo? ParsePacket(JsonElement e) =>
        e.ValueKind != JsonValueKind.Object ? null : new PacketInfo(Str(e, "text"), Int(e, "chars"), Int(e, "decisionCount"));

    /// <summary>GET /api/settings düz bir nesnedir ({"theme":"system","capsuleX":null,"packetBudget":2000,...}). Değerler metne çevrilir; null boş metin olur.</summary>
    public static Dictionary<string, string> ParseSettings(JsonElement e)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in e.EnumerateObject())
            d[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Null => "",
                _ => p.Value.GetRawText(),
            };
        return d;
    }
}
