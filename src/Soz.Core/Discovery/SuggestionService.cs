using Microsoft.Data.Sqlite;

namespace Soz.Core.Discovery;

public sealed record Suggestion(string ObjectUid, double Score, string ReasonCode, string Reason, long ProjectCount, long TotalCount);

/// <summary>Öneri sonucu. Gölge kipte <see cref="Items"/> boştur, hesaplanan <see cref="Computed"/> yalnız kayıt ve ölçüm içindir (kişiye gösterilmez).</summary>
public sealed record SuggestionResult(string BatchUid, bool Shadow, IReadOnlyList<Suggestion> Items, IReadOnlyList<Suggestion> Computed, Nearest? Nearest = null);

/// <summary>Öneriye en yakın dayanak: kullanılmış ama dayanak sayısına (<see cref="SuggestionService.MinEvidence"/>) ulaşmamış nesne. Boş durumu açıklamak için, yalnız gerçek sayıdan.</summary>
public sealed record Nearest(string ObjectUid, long Count, long Needed);

/// <summary>
/// Öneri hesabı. Öneri AYRI ve SABİT bir bölümdür; hiçbir yuvanın yerini değiştirmez, yuvaya bir şey koymaz (bu sınıf <see cref="LayoutStore"/> yazma yöntemlerini çağırmaz).
/// Kalıcı susturulan (Burada önerme / Hiç önerme) nesne hiç önerilmez. Gölge kip açıkken hesap ve kayıt yapılır; kendiliğinden çıkan öneri (bildirim, kapsül, iş ekranı) için kişiye boş liste döner,
/// kişinin kendi açtığı "Söz'ün önerdikleri" sayfası (<c>forPage</c>) ise dayanağı olan öneriyi her zaman gösterir.
/// </summary>
public static class SuggestionService
{
    public static SuggestionResult Suggest(SqliteConnection c, string? projectUid, Moment at, DiscoveryParams p, int? limit = null, bool forPage = false)
    {
        var project = string.IsNullOrWhiteSpace(projectUid) ? null : projectUid;
        var seq = ActiveDays.Current(c, at.Local);
        var weights = UsageStore.Weights(c, project, at, p);
        var placed = LayoutStore.PlacedUids(c);
        var dismissed = DismissStore.Load(c, project, seq, p);
        var max = Math.Clamp(limit ?? p.MaxSuggestions, 1, Math.Max(1, p.MaxSuggestions));

        var computed = new List<Suggestion>();
        foreach (var w in weights.Values)
        {
            if (dismissed.Blocked.Contains(w.ObjectUid)) continue;
            var u = placed.Contains(w.ObjectUid) ? p.InSlotFactor : 1.0;
            var cpen = dismissed.Penalty.GetValueOrDefault(w.ObjectUid);
            var score = Frecency.Score(w.Fg, w.Fc, w.Fs, u, cpen, p);
            // Dayanak kuralı (B8): en az MinEvidence GERÇEK kullanım yeter. Zaman ve proje penceresi puanı yükseltir ama kapı değildir;
            // aksi halde iki kullanım "şu an başka saat dilimindeyiz" diye hiç öneri doğurmazdı. Yuvada olan ve yok sayılan nesne bu kapıdan geçmez.
            var floor = !placed.Contains(w.ObjectUid) && cpen <= 0 && w.Ng >= MinEvidence;
            if (score <= 0 || (score < p.Threshold && !floor)) continue;
            // Dayanağı olmayan öneri çıkmaz: neden cümlesi, o önerinin gerçek kullanım sayısından kurulur.
            if (Why(w, p, at) is not { } why) continue;
            computed.Add(new Suggestion(w.ObjectUid, score, why.Code, why.Text, w.Nc, w.Ng));
        }
        var top = computed.OrderByDescending(s => s.Score).ThenBy(s => s.ObjectUid, StringComparer.Ordinal).Take(max).ToList();

        // Gölge kip yalnız KENDİLİĞİNDEN çıkan öneriyi susturur. Kişinin kendi açtığı sayfa (forPage) dayanağı olan öneriyi her zaman gösterir.
        var shown = forPage || !p.ShadowMode;
        var batch = ShadowLog.WriteBatch(c, project, at, shown: shown, top.Select((s, i) => new ShadowLog.Impression(i + 1, s.ObjectUid, s.Score)).ToList());
        Nearest? nearest = null;
        if (top.Count == 0)
        {
            var near = weights.Values.Where(w => !dismissed.Blocked.Contains(w.ObjectUid) && !placed.Contains(w.ObjectUid) && w.Ng > 0 && w.Ng < MinEvidence)
                .OrderByDescending(w => w.Ng).ThenByDescending(w => w.Fg).ThenBy(w => w.ObjectUid, StringComparer.Ordinal).FirstOrDefault();
            if (near is not null) nearest = new Nearest(near.ObjectUid, near.Ng, MinEvidence - near.Ng);
        }
        return new SuggestionResult(batch, p.ShadowMode, shown ? top : Array.Empty<Suggestion>(), top, nearest);
    }

    /// <summary>En az kaç kullanım bir "dayanak" sayılır. Tek seferlik kullanım örüntü değildir, onunla öneri yapılmaz.</summary>
    public const long MinEvidence = 2;

    private static readonly string[] DayNames = { "Pazar", "Pazartesi", "Salı", "Çarşamba", "Perşembe", "Cuma", "Cumartesi" };

    /// <summary>
    /// "Neden?" cümlesi: puana en çok katkı veren, dayanağı olan bileşen (proje, gün ve saat dilimi, genel). Her cümle o bileşenin gerçek sayısını ve zamanını söyler.
    /// Hiçbir bileşenin en az <see cref="MinEvidence"/> kullanımı yoksa null döner (öneri çıkmaz). Kök neden notu (b2): eskiden "gün ve saat" cümlesi tek kullanımla ve sayısız çıkıyordu,
    /// çünkü zaman ağırlığı (4) tek kullanımın genel ağırlığını (1) hep geçiyordu.
    /// </summary>
    private static (string Code, string Text)? Why(UsageWeights w, DiscoveryParams p, Moment at)
    {
        var cands = new List<(double Score, int Order, string Code, string Text)>();
        if (w.Nc >= MinEvidence) cands.Add((p.ProjectWeight * w.Fc, 0, "project", $"Bu projede {w.Nc} kez kullandın."));
        if (w.Ns >= MinEvidence)
        {
            var start = at.Local.Hour / p.TimeBlockHours * p.TimeBlockHours;
            cands.Add((p.TimeWeight * w.Fs, 1, "time", $"{DayNames[(int)at.Local.DayOfWeek]} günleri {start:00}:00 ile {start + p.TimeBlockHours:00}:00 arasında {w.Ns} kez kullandın."));
        }
        if (w.Ng >= MinEvidence) cands.Add((w.Fg, 2, "recent", $"Toplam {w.Ng} kez kullandın{Last(w, at)}."));
        if (cands.Count == 0) return null;
        var best = cands.OrderByDescending(x => x.Score).ThenBy(x => x.Order).First();
        return (best.Code, best.Text);
    }

    private static string Last(UsageWeights w, Moment at)
    {
        if (w.LastDay is null || !DateTime.TryParseExact(w.LastDay, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) return "";
        var days = (int)(at.Local.Date - d.Date).TotalDays;
        return days <= 0 ? ", en son bugün" : days == 1 ? ", en son dün" : $", en son {days} gün önce";
    }
}
