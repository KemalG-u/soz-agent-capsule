using System.Globalization;
using System.Text.Json;

namespace Soz.Core.Profile;

/// <summary>
/// "Şu an açık" hükmünün tek yerdeki kuralları. Eşikler adıyla burada durur; başka yerde sayı yazılmaz.
/// Kanıt türleri: <see cref="EvidenceProcess"/> (çalışan süreç oturuma ya da klasöre bağlandı),
/// <see cref="EvidenceNearTime"/> (süreç bağı yok, son hareket yakın), <see cref="EvidenceNone"/> (ikisi de yok).
/// </summary>
public static class LiveRules
{
    /// <summary>Üst proje aranırken kaç klasör yukarı bakılır.</summary>
    public const int MaxParentProjectDepth = 3;

    public const string EvidenceProcess = "surec";
    public const string EvidenceNearTime = "yakin-zaman";
    public const string EvidenceNone = "yok";

    /// <summary>Süreç bağı olmayan bir kayıt, son hareketi bu süreden yeniyse "yakın zaman" kanıtı sayılır.</summary>
    public static readonly TimeSpan NearTime = TimeSpan.FromHours(2);
    /// <summary>Süreç kanıtı yoksa bundan eski hiçbir kayıt açık adayı olamaz.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    /// <summary>Süreç bağı kurulamayan açık pencereye en güncel oturumu yalnız bu süre içinde eşleriz.</summary>
    public static readonly TimeSpan RecentBinding = TimeSpan.FromMinutes(15);
    /// <summary>Bir süreç, kendinden önce son yazılmış oturumun sahibi olamaz; saat sapması için pay.</summary>
    public static readonly TimeSpan ProcessStartGrace = TimeSpan.FromMinutes(5);
    /// <summary>Claude kayıt dosyasındaki süreç başlangıcı ile gerçek süreç başlangıcı arasındaki izin verilen fark.</summary>
    public static readonly TimeSpan ProcessStartTolerance = TimeSpan.FromSeconds(5);

    // ---- Uyarlanır yoklama (LiveCadence): "şu an açık" listesi ve ağır `claude agents` komutu ne sıklıkla yenilenir. Hepsi bu bölümde, adlarıyla. ----

    /// <summary>Kimse bakmıyorken (yakın zamanda arayüzden canlı görünüm ya da bekleyen listesi istenmedi) açık oturum hesabının aralığı.</summary>
    public static readonly TimeSpan LiveIdleInterval = TimeSpan.FromSeconds(60);
    /// <summary>Biri bakarken (yakın zamanda istek geldi) açık oturum hesabının aralığı; önbellek süresi de budur.</summary>
    public static readonly TimeSpan LiveActiveInterval = TimeSpan.FromSeconds(15);
    /// <summary>`claude agents --json` komutunun aralığı, biri bakarken. Komutu başlatmak (claude.exe'yi açmak) bu makinede ölçülen yaklaşık 1,5 sn işlemci yer (e7: antivirüs taraması); bu yüzden açık oturum hesabından (15 sn) seyrek. Kancalı oturumlar komutsuz, anlık güncellenir.</summary>
    public static readonly TimeSpan LiveCommandActiveInterval = TimeSpan.FromSeconds(60);
    /// <summary>`claude agents --json` komutunun aralığı, kimse bakmıyorken (ve oturumların hepsi kancayla kesin haber veriyorken en az bu kadar).</summary>
    public static readonly TimeSpan LiveCommandIdleInterval = TimeSpan.FromSeconds(120);
    /// <summary>Son istekten bu kadar süre geçmediyse "biri bakıyor" sayılır.</summary>
    public static readonly TimeSpan WatchWindow = TimeSpan.FromSeconds(40);
    /// <summary>Komut ardışık başarısız olursa aralık her seferinde bu kat uzar.</summary>
    public const int BackoffFactor = 2;
    /// <summary>Başarısızlık geri çekilmesinde aralığın çıkabileceği en uzun süre.</summary>
    public static readonly TimeSpan BackoffMax = TimeSpan.FromMinutes(5);
    /// <summary>Kanca haberi yoklamayı öne çekse de iki öne çekme arasında en az bu kadar süre geçer (araç çağrısı başına komut çalışmasın).</summary>
    public static readonly TimeSpan WakeMinGap = TimeSpan.FromSeconds(10);
    /// <summary>Aynı oturum için kanca haberiyle yoklama öne çekme hakkı bu sürede en çok bir kez kullanılır (komutun bulamadığı oturum döngü kurmasın).</summary>
    public static readonly TimeSpan WakePerSession = TimeSpan.FromMinutes(10);
    /// <summary>Kanca haberi kaydı bu süreden eskiyse unutulur (bellek sınırı; ayrıca <see cref="HookNoteLimit"/>).</summary>
    public static readonly TimeSpan HookNoteKeep = TimeSpan.FromHours(24);
    public const int HookNoteLimit = 512;
    /// <summary><c>claude agents --json</c> komutuna tanınan en uzun süre; aşılırsa kayıt dosyalarına düşülür.</summary>
    public static readonly TimeSpan AgentsCommandTimeout = TimeSpan.FromSeconds(6);
    /// <summary>Komutun bildirdiği oturum başlangıcı ile gerçek süreç başlangıcı arasındaki izin verilen fark (kayıt, sürecin birkaç sn sonrasını yazar).</summary>
    public static readonly TimeSpan AgentsStartTolerance = TimeSpan.FromSeconds(30);
    /// <summary>Canlı bölümde son kaç günün oturumları yeniden hesaplanır (listede olmayan açık oturumlar kimlikle ayrıca aranır).</summary>
    public const int LiveWindowDays = 30;

    public const string StateWorking = "calisiyor";
    public const string StateWaiting = "seni-bekliyor";
    public const string StateIdle = "bosta";
    public const string StateUnknown = "bilinmiyor";

    /// <summary>Canlı kaynağın adı: komut, kayıt dosyası ya da yalnız süreç tahmini.</summary>
    public const string SourceCommand = "agents-komutu";
    public const string SourceRegistry = "kayit-dosyasi";
    public const string SourceProcess = "surec-tahmini";

    /// <summary>Claude'un durum sözcüğünü Türkçe duruma çevirir. Bekleme nedeni doluysa durum ne olursa olsun "seni-bekliyor".</summary>
    public static string StateOf(string? status, string? waitingFor)
    {
        if (!string.IsNullOrWhiteSpace(waitingFor)) return StateWaiting;
        return status?.Trim().ToLowerInvariant() switch
        {
            "busy" => StateWorking,
            "waiting" => StateWaiting,
            "idle" => StateIdle,
            _ => StateUnknown,
        };
    }

    /// <summary>Oturum, bu sürecin oturumu olabilir mi? Süreç başladığından önce son yazılmış oturum bu sürecin değildir.</summary>
    public static bool Plausible(DateTime? sessionLastActivity, DateTime? processStartedAt)
    {
        if (processStartedAt is not { } started) return true;
        return sessionLastActivity is { } last && last >= started - ProcessStartGrace;
    }

    public static string EvidenceForActivity(DateTime? lastActivity, DateTime now)
        => lastActivity is { } last && last <= now + TimeSpan.FromMinutes(1) && now - last <= NearTime ? EvidenceNearTime : EvidenceNone;

    /// <summary>Kanıt türü ve yaşa göre açık adayı olunabilir mi (eski kayıtlı görünümü de bu kuralla süzer).</summary>
    public static bool MayBeOpen(string? evidence, DateTime? lastActivity, DateTime now)
    {
        if (evidence == EvidenceProcess) return true;
        if (evidence != EvidenceNearTime) return false;
        return lastActivity is { } last && now - last <= StaleAfter;
    }
}

/// <summary>
/// Claude Code'un açık oturum bilgisi: ya <c>claude agents --json</c> çıktısından ya da kendi süreç kaydından (<c>&lt;claude evi&gt;\sessions\&lt;pid&gt;.json</c>).
/// Yalnız süreç alanları okunur (pid, oturum kimliği, klasör, tür, başlangıç, ad, durum, bekleme nedeni); sohbet metni değil.
/// <c>Cwd</c> oturumun BAŞLADIĞI klasördür (ör. system32); projeye bağ oturum kimliği üzerinden kayıttaki klasörle kurulur.
/// </summary>
public sealed record ClaudeRegistryEntry(int Pid, string SessionId, DateTime? ProcessStartedAt, DateTime? UpdatedAt,
    string? Cwd = null, string Kind = "interactive", DateTime? StartedAt = null, string? Name = null, string? Status = null,
    string? WaitingFor = null, bool FromCommand = false)
{
    /// <summary>calisiyor / seni-bekliyor / bosta / bilinmiyor.</summary>
    public string State => LiveRules.StateOf(Status, WaitingFor);
}

public static class ClaudeSessionRegistry
{
    private const int MaxBytes = 64 * 1024;

    public static IReadOnlyList<ClaudeRegistryEntry> Read(string? claudeHome)
    {
        var result = new List<ClaudeRegistryEntry>();
        if (string.IsNullOrWhiteSpace(claudeHome)) return result;
        try
        {
            var dir = Path.Combine(claudeHome, "sessions");
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var filePid)) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length is 0 or > MaxBytes) continue;
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
                    using var doc = JsonDocument.Parse(fs);
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    var pid = Num(r, "pid") is { } p ? (int)p : filePid;
                    var sessionId = r.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    if (pid != filePid || string.IsNullOrWhiteSpace(sessionId)) continue;
                    DateTime? procStart = null;
                    if (Num(r, "procStart") is { } ft and > 0) { try { procStart = DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { } }
                    DateTime? updated = Num(r, "updatedAt") is { } ms and > 0 ? ProfileSources.FromEpoch(ms) : null;
                    DateTime? started = Num(r, "startedAt") is { } sm and > 0 ? ProfileSources.FromEpoch(sm) : null;
                    result.Add(new(pid, sessionId.Trim(), procStart, updated, Text(r, "cwd", 1024), Text(r, "kind", 32) ?? "interactive", started,
                        Text(r, "name", 120), Text(r, "status", 32), Text(r, "waitingFor", 160)));
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { /* bozuk ya da yarım kayıt: yok say */ }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>Kısa metin alanı: denetim karakterleri boşluk olur, uzunluk sınırlıdır; yoksa null.</summary>
    internal static string? Text(JsonElement obj, string name, int max)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var text = v.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Length > max) text = text[..max];
        return string.Concat(text.Select(ch => char.IsControl(ch) ? ' ' : ch));
    }

    internal static long? Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }
}
