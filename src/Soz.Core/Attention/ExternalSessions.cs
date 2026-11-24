using System.Globalization;
using Soz.Core.Data;
using System.Text.Json.Nodes;

namespace Soz.Core.Attention;

/// <summary>Dışarıdaki (Söz'ün başlatmadığı) açık oturumun canlı durumu. Değerler profil motorunun canlı kaydındaki <c>state</c> alanıyla aynıdır.</summary>
public static class ExternalStates
{
    public const string Working = "calisiyor", WaitingForYou = "seni-bekliyor", Idle = "bosta", Unknown = "bilinmiyor";

    public static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "calisiyor" or "çalışıyor" or "busy" or "working" => Working,
        "seni-bekliyor" or "waiting" or "needs-you" => WaitingForYou,
        "bosta" or "boşta" or "idle" => Idle,
        _ => Unknown,
    };
}

/// <summary>
/// Dışarıda açık bir yardımcı oturumunun anlık görüntüsü (örnek kaynak: <c>claude agents --json</c>; pid, çalışma klasörü, oturum kimliği, durum, ne beklediği).
/// State: ExternalStates değerlerinden biri. WaitingFor: yardımcının ne beklediğini söylediği kısa metin (olduğu gibi, boş olabilir). Evidence: durumun neye dayandığı
/// (süreç, kayıt, yakın zaman...). ObservedUtc: bilginin alındığı an (bayat bilgi kutuya girmez). Pid ve Hwnd varsa pencereye gidilebilir.
/// </summary>
public sealed record ExternalSession(string Tool, string SessionId, string? ProjectPath, string? Title, int? Pid, long? Hwnd, string State, string? WaitingFor, string? Evidence,
    DateTime ObservedUtc, DateTime? SinceUtc = null);

/// <summary>Dış oturum kaynağı. Sunucu, profil motorundaki canlı kaydı bu arabirimle verir; test sahte uygulama koyar.</summary>
public interface IExternalSessionSource
{
    IReadOnlyList<ExternalSession> Snapshot();
}

/// <summary>Sabit liste döndüren kaynak (test ve elle bağlama için).</summary>
public sealed class FixedExternalSessions(IEnumerable<ExternalSession> rows) : IExternalSessionSource
{
    public List<ExternalSession> Rows { get; } = rows.ToList();
    public IReadOnlyList<ExternalSession> Snapshot() => Rows.ToList();
}

/// <summary>
/// Profil görünümünün (<c>ProfileService.View()</c> çıktısı, JSON) canlı bölümünü dış oturum listesine çevirir. Alan adlarıyla okur: profil motoru
/// <c>state</c>, <c>waitingFor</c> ve <c>evidence</c> alanlarını eklediğinde kendiliğinden devreye girer; eklemediği sürece durum "bilinmiyor" kalır ve kutuya girmez.
/// Bilginin yaşı görünümün <c>liveObservedAt</c> (yoksa <c>scannedAt</c>) anıdır; <paramref name="maxAge"/>'den eskiyse hiçbir oturum güvenilmez sayılır (boş liste döner).
/// </summary>
public sealed class ProfileExternalSessions(Func<JsonObject> view, TimeSpan? maxAge = null, Func<DateTime>? now = null) : IExternalSessionSource
{
    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromMinutes(3);
    private readonly Func<DateTime> _now = now ?? (Func<DateTime>)(() => Time.UtcNow());

    public IReadOnlyList<ExternalSession> Snapshot()
    {
        var list = new List<ExternalSession>();
        JsonObject v;
        try { v = view(); }
        catch (Exception) { return list; }
        var scanned = ParseTime(v["liveObservedAt"]) ?? ParseTime(v["scannedAt"]);
        if (scanned is null || _now() - scanned.Value > _maxAge) return list; // bayat görünüm: durum söylenmez
        if (v["live"] is not JsonArray live) return list;
        foreach (var g in live)
        {
            if (g is not JsonObject group) continue;
            var path = Str(group["projectPath"]);
            if (group["sessions"] is not JsonArray sessions) continue;
            foreach (var s in sessions)
            {
                if (s is not JsonObject row) continue;
                var tool = Str(row["tool"]); var id = Str(row["id"]);
                if (tool is null || id is null) continue;
                var pid = Num(row["pid"]) is { } pl ? (int?)pl : null;
                var hwnd = Num(row["hwnd"]);
                list.Add(new ExternalSession(tool, id, path, Str(row["title"]), pid, hwnd, ExternalStates.Normalize(Str(row["state"])),
                    Str(row["waitingFor"]), Str(row["evidence"]), scanned.Value, ParseTime(row["stateSince"])));
            }
        }
        return list;
    }

    private static long? Num(JsonNode? n)
        => n is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (long)d : null) : null;

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static DateTime? ParseTime(JsonNode? n)
        => Str(n) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

/// <summary>
/// Yavaş bir dış oturum kaynağını (profil canlı görünümü, komut çalıştırır, ~2,5 sn) kutuyu bekletmeden verir: son bilinen listeyi hemen döndürür,
/// yenilemeyi arka planda yapar. İlk çağrıda en çok <paramref name="firstWait"/> bekler. Yenileme patlarsa son liste kalır ve aralık uzar
/// (ardışık hatada her seferinde iki kat, en çok <see cref="MaxBackoff"/>); bir yenileme sürerken ikincisi hiç başlamaz. Bayat liste için asıl kaynak
/// (ProfileExternalSessions) kendi tazelik süresiyle boş döner, bu sarmalayıcı tazelik kararı vermez.
/// Aralık sabit (<paramref name="refreshEvery"/>) ya da uyarlanır olabilir (<paramref name="interval">: kimse bakmıyorken seyrek, bakarken sık);
/// <paramref name="wakeAt"/> son "öne çek" isteğinin anıdır: yenilemenin başladığı andan yeniyse aralık beklenmez (kanca haberi yeni oturum gördü).
/// </summary>
public sealed class BackgroundExternalSessions(IExternalSessionSource inner, TimeSpan? refreshEvery = null, TimeSpan? firstWait = null,
    Func<TimeSpan>? interval = null, Func<DateTime>? wakeAt = null) : IExternalSessionSource
{
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _every = refreshEvery ?? TimeSpan.FromSeconds(8), _firstWait = firstWait ?? TimeSpan.FromMilliseconds(400);
    private readonly object _gate = new();
    private IReadOnlyList<ExternalSession> _last = Array.Empty<ExternalSession>();
    private DateTime _lastStart = DateTime.MinValue;
    private Task? _running;
    private int _failures;
    public int Refreshes { get; private set; }
    public int Failures { get { lock (_gate) return _failures; } }

    private TimeSpan Every()
    {
        var every = interval?.Invoke() ?? _every;
        for (var i = 0; i < _failures; i++) { every *= 2; if (every >= MaxBackoff) return MaxBackoff; }
        return every;
    }

    public IReadOnlyList<ExternalSession> Snapshot()
    {
        Task? wait = null;
        lock (_gate)
        {
            var woken = wakeAt?.Invoke() is { } w && w > _lastStart;
            if (_running is null && (woken || DateTime.UtcNow - _lastStart >= Every()))
            {
                _lastStart = DateTime.UtcNow;
                var first = Refreshes == 0;
                _running = Task.Run(() =>
                {
                    var ok = false;
                    try { var r = inner.Snapshot(); lock (_gate) _last = r; ok = true; }
                    catch (Exception) { /* son liste kalır */ }
                    finally { lock (_gate) { Refreshes++; _failures = ok ? 0 : Math.Min(_failures + 1, 16); _running = null; } }
                });
                if (first) wait = _running;
            }
            else if (Refreshes == 0) wait = _running;
        }
        if (wait is not null) { try { wait.Wait(_firstWait); } catch (Exception) { } }
        lock (_gate) return _last;
    }
}
