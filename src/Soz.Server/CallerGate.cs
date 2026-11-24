using System.Text.RegularExpressions;
using Soz.Core;
using Soz.Core.Data;
using Soz.Core.Security;

namespace Soz.Server;

/// <summary>
/// "Kabul ve ayar yalnız insandan" sunucu tarafı (denetim 3, İlke 2). API isteğinin karşı ucundaki SÜREÇ sınıflanır (CallerIdentity).
/// Ajan çağrısına: GET /api/session jetonu VERİLMEZ (403) ve yazma uçlarının HEPSİ kapalıdır; yalnız "reply", kuyruk ve bekçi denemesi insan denetiminden muaftır.
/// DİKKAT (denetim 5 tur 2, B18): bu muafiyet yalnız insan denetimini geçirir. Bu uçlar yine X-Soz-Token ister ve ajan jeton alamaz, bu yüzden ajan bunları HTTP ile çağırırsa jetonsuz 401 alır.
/// Ajanın yolu komut satırıdır (soz job reply, soz queue add, soz guard test; CliGuard bunlara izin verir). Bu bilinçli bir tasarımdır, hata değil.
/// Süreç bulunamazsa (Unknown) yazma uçları için ret (güvenli taraf); okuma uçları serbest kalır.
/// </summary>
public static class CallerGate
{
    private static readonly Regex[] AgentWrites =
    {
        new(@"^/api/jobs/\d+/reply$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^/api/queue$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^/api/guard/test$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    private const string ItemKey = "soz.caller";
    private static int _logged;

    /// <summary>Ajanın da çağırabileceği yazma uçları.</summary>
    public static bool AgentMayWrite(string method, string path)
    {
        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase)) return false;
        return AgentWrites.Any(r => r.IsMatch(path.TrimEnd('/')));
    }

    /// <summary>Bu istek insan ister mi? (jeton ucu ve ajana kapalı yazma uçları)</summary>
    public static bool NeedsHuman(HttpContext ctx)
    {
        var path = (ctx.Request.Path.Value ?? "").TrimEnd('/');
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
            // Jeton ucu ve klasör gezme ucu (bilgisayarın klasör haritasını ajan çıkarmasın) yalnız insana açıktır.
            return path.Equals("/api/session", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/fs/", StringComparison.OrdinalIgnoreCase);
        return !AgentMayWrite(ctx.Request.Method, path);
    }

    public static CallerInfo Get(HttpContext ctx, string dataDir)
    {
        if (ctx.Items.TryGetValue(ItemKey, out var o) && o is CallerInfo ci) return ci;
        var info = CallerIdentity.ClassifyPeer(ctx.Connection.RemotePort, ctx.Connection.LocalPort, dataDir);
        ctx.Items[ItemKey] = info;
        return info;
    }

    /// <summary>Reddedilen ajan çağrısı olay kaydına yazılır (en çok 500 kayıt, süreç başına).</summary>
    public static void LogDenied(SozDb db, HttpContext ctx, CallerInfo who)
    {
        if (Interlocked.Increment(ref _logged) > 500) return;
        try
        {
            using var c = db.Open();
            Events.Log(c, "caller_denied", null, System.Text.Json.JsonSerializer.Serialize(new { method = ctx.Request.Method, path = ctx.Request.Path.Value, pid = who.Pid, process = who.ProcessName, kind = who.Kind.ToString(), why = who.Why }));
        }
        catch (Exception) { /* kayıt yazılamazsa ret yine geçerli */ }
    }
}
