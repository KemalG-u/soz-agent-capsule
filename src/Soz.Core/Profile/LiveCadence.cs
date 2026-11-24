namespace Soz.Core.Profile;

/// <summary>Kanca (terminal haberi) ile öğrenilen son durum: oturum, hangi durumda, ne zaman. Yoklama beklemeden "şu an açık" listesine yansır.</summary>
public sealed record HookNote(string State, bool Waiting, DateTime At);

/// <summary>
/// "Şu an açık" hesabı ve ağır <c>claude agents</c> komutu için UYARLANIR yoklama kuralları (tek yer; sayılar <see cref="LiveRules"/> içindedir).
/// Kimse bakmıyorken seyrek (<see cref="LiveRules.LiveIdleInterval"/>), biri bakarken sık (<see cref="LiveRules.LiveActiveInterval"/>) yenilenir;
/// komut ardışık başarısız olursa aralık uzar; kanca haberi gelince ilgili oturum hemen güncellenir (<see cref="NoteHook"/>) ve yalnız yeni bir oturum görüldüyse
/// bir sonraki yoklama öne çekilir (sınırlı sıklıkta). Oturumun kancayla kesin haber verdiği biliniyorsa komuta gerek kalmaz (<see cref="Covered"/>).
/// Süreç içinde tek örnek paylaşılır (SozService.Profile); saat dışarıdan verilebilir (testler).
/// </summary>
public sealed class LiveCadence(Func<DateTime>? clock = null)
{
    /// <summary>Bu başlığı taşıyan istek "arka plan" sayılır: kabuk ve kapsülün sessiz yoklaması kimsenin baktığı anlamına gelmez.</summary>
    public const string BackgroundHeader = "X-Soz-Arka";

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private DateTime _lastWatch = DateTime.MinValue;
    private DateTime _wakeAt = DateTime.MinValue, _lastWake = DateTime.MinValue;
    private int _failures;
    private HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HookNote> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _woken = new(StringComparer.Ordinal);

    public DateTime Now => _clock();

    /// <summary>Biri (arayüz, bekleyen listesi isteği) açık oturumlara bakıyor: aralık kısalır.</summary>
    public void Watch(DateTime? now = null)
    {
        var t = now ?? _clock();
        lock (_gate) if (t > _lastWatch) _lastWatch = t;
    }

    public bool IsWatched(DateTime now)
    {
        lock (_gate) return now >= _lastWatch && now - _lastWatch <= LiveRules.WatchWindow;
    }

    /// <summary>Açık oturum hesabının şimdiki aralığı (aynı zamanda önbellek süresi).</summary>
    public TimeSpan Interval(DateTime now) => IsWatched(now) ? LiveRules.LiveActiveInterval : LiveRules.LiveIdleInterval;

    /// <summary>
    /// Ağır komutun şimdiki aralığı (bakarken <see cref="LiveRules.LiveCommandActiveInterval"/>, bakmıyorken <see cref="LiveRules.LiveCommandIdleInterval"/>), oturumların hepsi kancayla kesin haber veriyorsa en az seyrek aralık, ardışık başarısızlıkta
    /// her seferinde <see cref="LiveRules.BackoffFactor"/> kat (en çok <see cref="LiveRules.BackoffMax"/>).
    /// </summary>
    public TimeSpan CommandInterval(DateTime now, bool covered)
    {
        var interval = IsWatched(now) ? LiveRules.LiveCommandActiveInterval : LiveRules.LiveCommandIdleInterval;
        if (covered && interval < LiveRules.LiveCommandIdleInterval) interval = LiveRules.LiveCommandIdleInterval;
        int failures;
        lock (_gate) failures = _failures;
        for (var i = 0; i < failures; i++)
        {
            interval *= LiveRules.BackoffFactor;
            if (interval >= LiveRules.BackoffMax) return LiveRules.BackoffMax;
        }
        return interval;
    }

    public int Failures { get { lock (_gate) return _failures; } }
    public void CommandSucceeded() { lock (_gate) _failures = 0; }
    public void CommandFailed() { lock (_gate) _failures = Math.Min(_failures + 1, 16); }

    /// <summary>Son "yoklamayı öne çek" isteğinin anı. Bundan önce alınmış bir hesap bayat sayılır.</summary>
    public DateTime WakeAt { get { lock (_gate) return _wakeAt; } }

    /// <summary>Son hesapta görülen Claude oturum kimlikleri (kanca haberi bunların dışında bir oturumdan gelirse yoklama öne çekilir).</summary>
    public void SetKnown(IEnumerable<string> sessionIds)
    {
        var set = new HashSet<string>(sessionIds, StringComparer.Ordinal);
        lock (_gate) _known = set;
    }

    /// <summary>
    /// Kanca haberi (session-start, user-prompt-submit, pre-tool-use, stop, stop-failure, notification). Oturumun durumu hemen kaydedilir. Oturum bilinmiyorsa
    /// (yeni açılmış) ve bu oturum için yakın zamanda öne çekme yapılmadıysa ve son öne çekmeden <see cref="LiveRules.WakeMinGap"/> geçtiyse bir sonraki yoklama öne çekilir.
    /// Dönüş: yoklama öne çekildi mi.
    /// </summary>
    public bool NoteHook(string eventName, string? sessionId, string? notificationType, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var sid = sessionId.Trim();
        if (sid.Length > 128) return false;
        string state; bool waiting;
        switch (eventName)
        {
            case "user-prompt-submit":
            case "pre-tool-use": state = LiveRules.StateWorking; waiting = false; break;
            case "stop":
            case "stop-failure":
            case "session-start": state = LiveRules.StateIdle; waiting = false; break;
            case "notification":
                var idle = string.Equals(notificationType, "idle_prompt", StringComparison.OrdinalIgnoreCase);
                state = idle ? LiveRules.StateIdle : LiveRules.StateWaiting; waiting = !idle; break;
            default: return false;
        }
        lock (_gate)
        {
            _notes[sid] = new HookNote(state, waiting, now);
            if (_notes.Count > LiveRules.HookNoteLimit)
            {
                var cut = now - LiveRules.HookNoteKeep;
                foreach (var k in _notes.Where(p => p.Value.At < cut).Select(p => p.Key).ToList()) _notes.Remove(k);
                while (_notes.Count > LiveRules.HookNoteLimit) _notes.Remove(_notes.OrderBy(p => p.Value.At).First().Key);
            }
            if (_known.Contains(sid)) return false;
            if (_woken.TryGetValue(sid, out var last) && now - last < LiveRules.WakePerSession) return false;
            if (now - _lastWake < LiveRules.WakeMinGap && now >= _lastWake) return false;
            _woken[sid] = now; _lastWake = now; _wakeAt = now;
            if (_woken.Count > LiveRules.HookNoteLimit)
                foreach (var k in _woken.Where(p => now - p.Value >= LiveRules.WakePerSession).Select(p => p.Key).ToList()) _woken.Remove(k);
            return true;
        }
    }

    /// <summary>Oturum için kanca haberi (yoksa null).</summary>
    public HookNote? NoteOf(string sessionId)
    {
        lock (_gate) return _notes.TryGetValue(sessionId, out var n) ? n : null;
    }

    /// <summary>
    /// Açık Claude oturumlarının HEPSİ bu sunucu çalışırken kancayla haber verdiyse ve hiçbiri "seni bekliyor" işaretinde kalmadıysa komuta gerek yoktur:
    /// durumları kanca zaten anlık söyler. Oturum yoksa ya da biri kancasızsa false.
    /// </summary>
    public bool Covered(IReadOnlyCollection<string> liveSessionIds)
    {
        if (liveSessionIds.Count == 0) return false;
        lock (_gate)
            foreach (var id in liveSessionIds)
                if (!_notes.TryGetValue(id, out var n) || n.Waiting) return false;
        return true;
    }
}
