using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Discovery;

/// <summary>
/// Susturma kipleri. Ignored: kişi öneriyi görüp başka şey seçti (ceza birikir, zamanla geçer; "bu nesne, bu proje" çifti için).
/// NeverHere: "Burada önerme" (nesne + proje, kalıcı). Never: "Hiç önerme" (nesne, her yerde, kalıcı).
/// </summary>
public enum DismissMode { Ignored, NeverHere, Never }

public sealed record DismissState(HashSet<string> Blocked, Dictionary<string, double> Penalty);

/// <summary>"Bir daha gösterme" kayıtları. Yuvalara dokunmaz; yalnız önerinin görünmesini etkiler.</summary>
public static class DismissStore
{
    public static void Dismiss(SqliteConnection c, string objectUid, string? projectUid, DismissMode mode, Moment at, DiscoveryParams p)
    {
        if (string.IsNullOrWhiteSpace(objectUid) || objectUid.Length > 200) throw new ArgumentException("Nesne kimliği boş ya da çok uzun.", nameof(objectUid));
        var project = projectUid ?? "";
        if (mode == DismissMode.NeverHere && project.Length == 0) throw new ArgumentException("\"Burada önerme\" için bir proje gerekir.", nameof(projectUid));
        using var tx = c.BeginTransaction(deferred: false);
        var now = Time.Iso(at.Utc);
        if (mode == DismissMode.Ignored)
        {
            var seq = ActiveDays.Today(c, tx, at.Local);
            var old = Penalty(c, tx, objectUid, project, seq, p);
            var next = Math.Min(p.DismissMax, old + p.DismissStep);
            Upsert(c, tx, objectUid, project, "penalty", next, seq, now);
        }
        else Upsert(c, tx, objectUid, mode == DismissMode.Never ? "" : project, "block", 1, 0, now);
        Events.Append(c, "discovery.suggestion.dismissed", "discovery_dismiss", null, project.Length > 0 ? project : null, null, "user",
            new JsonObject { ["object_uid"] = objectUid, ["mode"] = ModeName(mode) }.ToJsonString(), tx);
        tx.Commit();
    }

    /// <summary>Susturmayı geri alır (kişi "yine öner" der). Kaç kayıt kalktığını döndürür.</summary>
    public static int Restore(SqliteConnection c, string objectUid, string? projectUid, DismissMode mode, Moment at)
    {
        var project = projectUid ?? "";
        using var tx = c.BeginTransaction(deferred: false);
        var n = mode == DismissMode.Ignored
            ? c.Exec("DELETE FROM discovery_dismiss WHERE object_uid=@o AND project_uid=@p AND mode='penalty'", tx, ("@o", objectUid), ("@p", project))
            : c.Exec("DELETE FROM discovery_dismiss WHERE object_uid=@o AND project_uid=@p AND mode='block'", tx, ("@o", objectUid), ("@p", mode == DismissMode.Never ? "" : project));
        if (n > 0)
            Events.Append(c, "discovery.suggestion.restored", "discovery_dismiss", null, project.Length > 0 ? project : null, null, "user",
                new JsonObject { ["object_uid"] = objectUid, ["mode"] = ModeName(mode) }.ToJsonString(), tx);
        tx.Commit();
        return n;
    }

    public static string ModeName(DismissMode m) => m switch { DismissMode.Ignored => "ignored", DismissMode.NeverHere => "never-here", _ => "never" };
    public static DismissMode? ParseMode(string? s) => s switch { "ignored" => DismissMode.Ignored, "never-here" => DismissMode.NeverHere, "never" => DismissMode.Never, _ => null };

    private static void Upsert(SqliteConnection c, SqliteTransaction tx, string uid, string project, string mode, double value, double seq, string now)
        => c.Exec("INSERT INTO discovery_dismiss(object_uid, project_uid, mode, c, t_last, created_at) VALUES(@o,@p,@m,@c,@t,@n) " +
                  "ON CONFLICT(object_uid, project_uid, mode) DO UPDATE SET c=excluded.c, t_last=excluded.t_last",
            tx, ("@o", uid), ("@p", project), ("@m", mode), ("@c", value), ("@t", seq), ("@n", now));

    /// <summary>Bu nesne ve proje çifti için şimdiki ceza (yarılanmış), 0..DismissMax.</summary>
    public static double Penalty(SqliteConnection c, SqliteTransaction? tx, string uid, string project, double seqNow, DiscoveryParams p)
    {
        var row = c.Query("SELECT c, t_last FROM discovery_dismiss WHERE object_uid=@o AND project_uid=@p AND mode='penalty'",
            r => (c: r.GetDouble(0), t: r.GetDouble(1)), tx, ("@o", uid), ("@p", project)).FirstOrDefault();
        return row == default ? 0 : Frecency.Current(row.c, row.t, seqNow, p.DismissHalfLifeDays);
    }

    /// <summary>Verilen proje bağlamı için kalıcı susturulanlar ve güncel cezalar (salt okunur). Anahtar: nesne kimliği.</summary>
    public static DismissState Load(SqliteConnection c, string? projectUid, double seqNow, DiscoveryParams p)
    {
        var project = projectUid ?? "";
        var blocked = c.Query("SELECT DISTINCT object_uid FROM discovery_dismiss WHERE mode='block' AND (project_uid='' OR project_uid=@p)", r => r.GetString(0), null, ("@p", project))
            .ToHashSet(StringComparer.Ordinal);
        var penalty = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (uid, cv, t) in c.Query("SELECT object_uid, c, t_last FROM discovery_dismiss WHERE mode='penalty' AND project_uid=@p",
                     r => (r.GetString(0), r.GetDouble(1), r.GetDouble(2)), null, ("@p", project)))
            penalty[uid] = Frecency.Current(cv, t, seqNow, p.DismissHalfLifeDays);
        return new DismissState(blocked, penalty);
    }
}
