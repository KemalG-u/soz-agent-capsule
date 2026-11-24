using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Discovery;

public sealed record ShadowStats(int Days, int Batches, int BatchesWithSuggestion, int Resolved, int ResolvedWithSuggestion, int Hits, int HitsAtRank1, double? HitRate, double? Rank1Rate, int Shown);

/// <summary>
/// Gölge kip kaydı. Her öneri hesaplanışı bir "grup" (batch) yazar: o an ne önerilirdi, kişiye gösterildi mi. Kişi kısa süre sonra bir şey kullanınca
/// (<see cref="UsageStore.Record"/>) en yeni açık grubun seçimi yazılır: "önerilseydi kişi onu seçer miydi". İsabet = seçilen nesne önerilenler arasında.
/// Bu kayıt formülün sayılarını (hiçbiri ölçülmedi) canlıya almadan önce sınamak içindir.
/// </summary>
public static class ShadowLog
{
    public sealed record Impression(int Rank, string ObjectUid, double Score);

    public static string WriteBatch(SqliteConnection c, string? projectUid, Moment at, bool shown, IReadOnlyList<Impression> items)
    {
        var id = Uid.New();
        using var tx = c.BeginTransaction(deferred: false);
        c.Exec("INSERT INTO discovery_batch(batch_uid, ts, project_uid, shown) VALUES(@b,@t,@p,@s)", tx,
            ("@b", id), ("@t", Time.Iso(at.Utc)), ("@p", projectUid ?? ""), ("@s", shown ? 1 : 0));
        foreach (var i in items)
            c.Exec("INSERT INTO discovery_impression(batch_uid, rank, object_uid, score) VALUES(@b,@r,@o,@s)", tx, ("@b", id), ("@r", i.Rank), ("@o", i.ObjectUid), ("@s", i.Score));
        tx.Commit();
        return id;
    }

    /// <summary>Kullanım kaydıyla aynı işlemde: aynı projedeki, ölçüm süresi içindeki en yeni açık gruba seçimi yazar. Hiç uygun grup yoksa hiçbir şey yapmaz.</summary>
    internal static void Resolve(SqliteConnection c, SqliteTransaction tx, string chosenUid, string project, Moment at, DiscoveryParams p)
    {
        var since = Time.Iso(at.Utc.AddMinutes(-p.HitWindowMinutes));
        c.Exec("""
            UPDATE discovery_batch SET chosen_uid=@o, chosen_ts=@now
            WHERE batch_uid=(SELECT batch_uid FROM discovery_batch WHERE chosen_uid IS NULL AND project_uid=@p AND ts>=@since AND ts<=@now ORDER BY ts DESC LIMIT 1)
            """, tx, ("@o", chosenUid), ("@now", Time.Iso(at.Utc)), ("@p", project), ("@since", since));
    }

    /// <summary>Son <paramref name="days"/> gün için isabet özeti (salt okunur).</summary>
    public static ShadowStats Stats(SqliteConnection c, int days, Moment at)
    {
        var since = Time.Iso(at.Utc.AddDays(-Math.Max(1, days)));
        var rows = c.Query("""
            SELECT b.batch_uid, b.shown, b.chosen_uid,
                   (SELECT COUNT(*) FROM discovery_impression i WHERE i.batch_uid=b.batch_uid) AS n,
                   (SELECT MIN(rank) FROM discovery_impression i WHERE i.batch_uid=b.batch_uid AND i.object_uid=b.chosen_uid) AS hit_rank
            FROM discovery_batch b WHERE b.ts>=@s
            """, r => (shown: r.GetInt64(1) == 1, chosen: r.Str(2), n: r.GetInt64(3), hit: r.LongN(4)), null, ("@s", since));
        var withSug = rows.Count(r => r.n > 0);
        var resolved = rows.Where(r => r.chosen is not null).ToList();
        var resolvedSug = resolved.Count(r => r.n > 0);
        var hits = resolved.Count(r => r.hit is not null);
        var hits1 = resolved.Count(r => r.hit == 1);
        return new ShadowStats(days, rows.Count, withSug, resolved.Count, resolvedSug, hits, hits1,
            resolvedSug == 0 ? null : (double)hits / resolvedSug, resolvedSug == 0 ? null : (double)hits1 / resolvedSug, rows.Count(r => r.shown));
    }
}
