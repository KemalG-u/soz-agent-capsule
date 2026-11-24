using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Discovery;

/// <summary>Bir nesnenin üç kapsamdaki güncel ağırlığı (şimdiye göre yaşlandırılmış) ve ham sayıları. LastDay: genel kullanımın en son görüldüğü gün (yyyy-MM-dd), bilinmiyorsa null.</summary>
public sealed record UsageWeights(string ObjectUid, double Fg, double Fc, double Fs, long Ng, long Nc, long Ns, string? LastDay = null)
{
    public double Fw(DiscoveryParams p) => Frecency.Combined(Fg, Fc, Fs, p);
}

/// <summary>
/// Kullanım sayacı: her (nesne, kapsam) için iki sayı (F, tSon) ve ham sayı. Olay listesi gerekmez (O(1) saklama).
/// Kapsamlar: genel, proje (kapsam anahtarı proje kimliği) ve zaman (hafta günü + saat dilimi). Yaş aktif gün sayacıyla ölçülür.
/// Bu sınıf yuvalara DOKUNMAZ: kullanım, yerleşimi değil yalnız öneri puanını etkiler.
/// </summary>
public static class UsageStore
{
    /// <summary>
    /// Kişi bir nesneyi kullandı. Aynı işlemde: sayaçlar güncellenir, "discovery.used" olayı yazılır, açık bir öneri grubu varsa isabet ölçümüne kaydedilir.
    /// Proje boşsa proje kapsamı yazılmaz.
    /// </summary>
    public static void Record(SqliteConnection c, string objectUid, string? projectUid, Moment at, DiscoveryParams p, string source = "ui")
    {
        if (string.IsNullOrWhiteSpace(objectUid) || objectUid.Length > 200) throw new ArgumentException("Nesne kimliği boş ya da çok uzun.", nameof(objectUid));
        var project = projectUid ?? "";
        using var tx = c.BeginTransaction(deferred: false);
        var seq = ActiveDays.Today(c, tx, at.Local);
        Bump(c, tx, objectUid, "global", "", seq, p);
        if (project.Length > 0) Bump(c, tx, objectUid, "project", project, seq, p);
        Bump(c, tx, objectUid, "time", ActiveDays.TimeKey(at.Local, p.TimeBlockHours), seq, p);
        Events.Append(c, "discovery.used", "discovery_usage", null, project.Length > 0 ? project : null, null, "user",
            new JsonObject { ["object_uid"] = objectUid, ["source"] = source }.ToJsonString(), tx);
        ShadowLog.Resolve(c, tx, objectUid, project, at, p);
        tx.Commit();
    }

    private static void Bump(SqliteConnection c, SqliteTransaction tx, string uid, string scope, string key, long seq, DiscoveryParams p)
    {
        var row = c.Query("SELECT f, t_last, n FROM discovery_usage WHERE object_uid=@o AND scope=@s AND scope_key=@k",
            r => (f: r.GetDouble(0), t: r.GetDouble(1), n: r.GetInt64(2)), tx, ("@o", uid), ("@s", scope), ("@k", key)).FirstOrDefault();
        var f = row == default ? 1.0 : Frecency.Accumulate(row.f, row.t, seq, p.HalfLifeDays);
        c.Exec("INSERT INTO discovery_usage(object_uid, scope, scope_key, f, t_last, n) VALUES(@o,@s,@k,@f,@t,1) " +
               "ON CONFLICT(object_uid, scope, scope_key) DO UPDATE SET f=excluded.f, t_last=excluded.t_last, n=n+1",
            tx, ("@o", uid), ("@s", scope), ("@k", key), ("@f", f), ("@t", (double)seq));
    }

    /// <summary>
    /// Tüm kullanılmış nesnelerin güncel ağırlıkları (salt okunur). Proje boşsa Fc 0; zaman anahtarı <paramref name="at"/>'tan gelir.
    /// </summary>
    public static Dictionary<string, UsageWeights> Weights(SqliteConnection c, string? projectUid, Moment at, DiscoveryParams p)
    {
        var now = ActiveDays.Current(c, at.Local);
        var project = projectUid ?? "";
        var timeKey = ActiveDays.TimeKey(at.Local, p.TimeBlockHours);
        var acc = new Dictionary<string, double[]>(StringComparer.Ordinal); // fg fc fs ng nc ns

        void Load(string scope, string key, int fi, int ni)
        {
            foreach (var (uid, f, t, n) in c.Query("SELECT object_uid, f, t_last, n FROM discovery_usage WHERE scope=@s AND scope_key=@k",
                         r => (r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetInt64(3)), null, ("@s", scope), ("@k", key)))
            {
                if (!acc.TryGetValue(uid, out var a)) acc[uid] = a = new double[6];
                a[fi] = Frecency.Current(f, t, now, p.HalfLifeDays);
                a[ni] = n;
            }
        }
        Load("global", "", 0, 3);
        if (project.Length > 0) Load("project", project, 1, 4);
        Load("time", timeKey, 2, 5);
        var last = c.Query("SELECT u.object_uid, d.day FROM discovery_usage u JOIN discovery_active_day d ON d.seq=CAST(u.t_last AS INTEGER) WHERE u.scope='global' AND u.scope_key=''",
            r => (uid: r.GetString(0), day: r.GetString(1))).ToDictionary(x => x.uid, x => x.day, StringComparer.Ordinal);
        return acc.ToDictionary(kv => kv.Key, kv => new UsageWeights(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], (long)kv.Value[3], (long)kv.Value[4], (long)kv.Value[5], last.GetValueOrDefault(kv.Key)), StringComparer.Ordinal);
    }
}
