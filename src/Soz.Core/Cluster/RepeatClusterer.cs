using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Cluster;

public sealed record ClusterStats(int NewClusters, int Assigned, int NewRepeatClusters, int ParentClusters);

/// <summary>
/// Tekrar kümeleyici (sözleşme 3.3 + 11.3). Proje içinde artımlı: yeni aday, en benzer kümeye (eşik üstü) katılır, yoksa yeni küme açar.
/// Küme kimlikleri kalıcıdır (reddedilen öneri aynı kümeye bağlı kalır). İkinci geçiş: en az iki projede benzer kümeler '*' altında üst kümede toplanır.
/// </summary>
public sealed class RepeatClusterer
{
    public double Threshold { get; }
    public double ParentMinScore { get; }
    public RepeatClusterer(double threshold = 0.55, double parentMinScore = 0.45)
    {
        Threshold = threshold;
        ParentMinScore = parentMinScore;
    }

    private sealed class Cl
    {
        public long Id;
        public long ProjectId;
        public List<TextFeature> Members = new();
    }

    public ClusterStats Run(SqliteConnection c)
    {
        int newClusters = 0, assigned = 0;
        var touched = new HashSet<long>();
        var beforeRepeat = c.Query("SELECT id FROM clusters WHERE session_count >= 2", r => r.GetInt64(0)).ToHashSet();
        var projects = c.Query("SELECT DISTINCT project_id FROM candidates WHERE cluster_id IS NULL", r => r.Long(0));
        using (var tx = c.BeginTransaction())
        {
            foreach (var pid in projects)
            {
                // Mevcut kümeler ve üyeleri (küme başına en çok 12 üye karşılaştırılır).
                var clusters = new Dictionary<long, Cl>();
                var index = new Dictionary<string, HashSet<long>>();
                foreach (var (cid, text) in c.Query("""
                    SELECT c.cluster_id, c.text FROM candidates c
                    WHERE c.project_id=@p AND c.cluster_id IS NOT NULL
                    ORDER BY c.cluster_id, c.score DESC
                    """, r => (r.GetInt64(0), r.GetString(1)), tx, ("@p", pid)))
                {
                    if (!clusters.TryGetValue(cid, out var cl)) clusters[cid] = cl = new Cl { Id = cid, ProjectId = pid };
                    if (cl.Members.Count >= 12) continue;
                    var f = Similarity.Feature(text);
                    cl.Members.Add(f);
                    foreach (var k in Similarity.IndexKeys(f)) (index.TryGetValue(k, out var set) ? set : index[k] = new()).Add(cid);
                }

                var fresh = c.Query("""
                    SELECT c.id, c.text FROM candidates c JOIN utterances u ON u.id=c.utterance_id
                    WHERE c.project_id=@p AND c.cluster_id IS NULL ORDER BY u.ts, c.id
                    """, r => (r.GetInt64(0), r.GetString(1)), tx, ("@p", pid));
                foreach (var (candId, text) in fresh)
                {
                    var f = Similarity.Feature(text);
                    long bestId = -1; double best = 0;
                    var seen = new HashSet<long>();
                    foreach (var k in Similarity.IndexKeys(f))
                    {
                        if (!index.TryGetValue(k, out var set)) continue;
                        foreach (var cid in set)
                        {
                            if (!seen.Add(cid)) continue;
                            foreach (var m in clusters[cid].Members)
                            {
                                var s = Similarity.Score(f, m);
                                if (s > best) { best = s; bestId = cid; }
                            }
                        }
                    }
                    long target;
                    if (bestId >= 0 && best >= Threshold) { target = bestId; assigned++; }
                    else
                    {
                        c.Exec("INSERT INTO clusters(project_id, canonical, count, session_count, first_ts, last_ts, max_score) VALUES(@p,@t,0,0,NULL,NULL,0)", tx, ("@p", pid), ("@t", text));
                        target = c.ScalarLong("SELECT last_insert_rowid()", tx);
                        clusters[target] = new Cl { Id = target, ProjectId = pid };
                        newClusters++;
                    }
                    var cl = clusters[target];
                    if (cl.Members.Count < 12)
                    {
                        cl.Members.Add(f);
                        foreach (var k in Similarity.IndexKeys(f)) (index.TryGetValue(k, out var set) ? set : index[k] = new()).Add(target);
                    }
                    c.Exec("UPDATE candidates SET cluster_id=@c WHERE id=@id", tx, ("@c", target), ("@id", candId));
                    touched.Add(target);
                }
            }
            foreach (var id in touched) RefreshStats(c, tx, id);
            tx.Commit();
        }
        var parents = RunGlobalPass(c);
        var afterRepeat = c.Query("SELECT id FROM clusters WHERE session_count >= 2 AND parent_id IS NULL", r => r.GetInt64(0));
        return new ClusterStats(newClusters, assigned, afterRepeat.Count(id => !beforeRepeat.Contains(id)), parents);
    }

    /// <summary>Küme sayıları ve kanonik cümle: en yüksek puanlı, en uzun olmayan, okunur uzunlukta üye.</summary>
    public static void RefreshStats(SqliteConnection c, SqliteTransaction? tx, long clusterId)
    {
        var members = c.Query("""
            SELECT c.text, c.score, u.ts, u.session_id FROM candidates c JOIN utterances u ON u.id=c.utterance_id WHERE c.cluster_id=@c
            """, r => (text: r.GetString(0), score: r.Dbl(1), ts: r.Str(2), sid: r.Long(3)), tx, ("@c", clusterId));
        if (members.Count == 0) return;
        var longest = members.Max(m => m.text.Length);
        var canonical = members
            .Where(m => members.Count == 1 || m.text.Length < longest || members.All(x => x.text.Length == longest))
            .OrderByDescending(m => Math.Round(m.score / 0.05))
            .ThenBy(m => m.text.Length is >= 25 and <= 160 ? 0 : 1)
            .ThenBy(m => m.text.Length)
            .First().text;
        c.Exec("""
            UPDATE clusters SET canonical=@can, count=@n, session_count=@s, first_ts=@f, last_ts=@l, max_score=@m WHERE id=@id
            """, tx, ("@can", canonical), ("@n", members.Count), ("@s", members.Select(m => m.sid).Distinct().Count()),
            ("@f", members.Min(m => m.ts)), ("@l", members.Max(m => m.ts)), ("@m", members.Max(m => m.score)), ("@id", clusterId));
    }

    /// <summary>İkinci geçiş: farklı projelerde benzer kümeler → '*' projesinde üst küme. Dönen: dokunulan üst küme sayısı.</summary>
    public int RunGlobalPass(SqliteConnection c)
    {
        var star = c.ScalarLong("SELECT id FROM projects WHERE key='*'");
        var rows = c.Query("""
            SELECT id, project_id, canonical, parent_id FROM clusters
            WHERE project_id<>@star AND max_score >= @min
            """, r => (id: r.GetInt64(0), pid: r.Long(1), text: r.Str(2) ?? "", parent: r.LongN(3)), null, ("@star", star), ("@min", ParentMinScore));
        if (rows.Count < 2) return 0;
        var feats = rows.Select(r => Similarity.Feature(r.text)).ToList();
        var index = new Dictionary<string, List<int>>();
        for (var i = 0; i < rows.Count; i++)
            foreach (var k in Similarity.IndexKeys(feats[i])) (index.TryGetValue(k, out var l) ? l : index[k] = new()).Add(i);

        // Birleşim-bul: farklı projelerdeki eşik üstü çiftler bir grup olur.
        var parent = Enumerable.Range(0, rows.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (var i = 0; i < rows.Count; i++)
        {
            var seen = new HashSet<int>();
            foreach (var k in Similarity.IndexKeys(feats[i]))
                foreach (var j in index[k])
                {
                    if (j <= i || !seen.Add(j) || rows[j].pid == rows[i].pid) continue;
                    if (Similarity.Score(feats[i], feats[j]) >= Threshold) parent[Find(i)] = Find(j);
                }
        }
        var groups = Enumerable.Range(0, rows.Count).GroupBy(Find).Where(g => g.Select(i => rows[i].pid).Distinct().Count() >= 2).ToList();
        if (groups.Count == 0) return 0;
        var touched = new HashSet<long>();
        using var tx = c.BeginTransaction();
        foreach (var g in groups)
        {
            var existingParent = g.Select(i => rows[i].parent).FirstOrDefault(p => p.HasValue);
            long pidCluster;
            if (existingParent.HasValue) pidCluster = existingParent.Value;
            else
            {
                c.Exec("INSERT INTO clusters(project_id, canonical, count, session_count, max_score, parent_id) VALUES(@p,@t,0,0,0,NULL)", tx,
                    ("@p", star), ("@t", rows[g.First()].text));
                pidCluster = c.ScalarLong("SELECT last_insert_rowid()", tx);
            }
            foreach (var i in g)
                c.Exec("UPDATE clusters SET parent_id=@pp WHERE id=@id", tx, ("@pp", pidCluster), ("@id", rows[i].id));
            touched.Add(pidCluster);
        }
        foreach (var p in touched) RefreshParent(c, tx, p);
        tx.Commit();
        return touched.Count;
    }

    public static void RefreshParent(SqliteConnection c, SqliteTransaction? tx, long parentId)
    {
        var kids = c.Query("SELECT canonical, count, session_count, first_ts, last_ts, max_score FROM clusters WHERE parent_id=@p",
            r => (text: r.Str(0) ?? "", n: r.Long(1), s: r.Long(2), f: r.Str(3), l: r.Str(4), m: r.Dbl(5)), tx, ("@p", parentId));
        if (kids.Count == 0) return;
        var best = kids.OrderByDescending(k => k.m).ThenBy(k => k.text.Length).First();
        c.Exec("UPDATE clusters SET canonical=@can, count=@n, session_count=@s, first_ts=@f, last_ts=@l, max_score=@m WHERE id=@id", tx,
            ("@can", best.text), ("@n", kids.Sum(k => k.n)), ("@s", kids.Sum(k => k.s)), ("@f", kids.Min(k => k.f)), ("@l", kids.Max(k => k.l)),
            ("@m", best.m), ("@id", parentId));
    }
}
