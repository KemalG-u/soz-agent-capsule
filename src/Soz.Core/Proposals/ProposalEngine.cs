using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Extract;

namespace Soz.Core.Proposals;

public sealed record ProposalStats(int Extracted, int NewCandidates, int NewProposals, int UpdatedProposals, int Withdrawn);

/// <summary>
/// Ayıkla → (kümeleyici dışarıda) → öner. Öneri kuralı: küme en yüksek puanı ≥ eşik, ya da küme tekrar (≥2 oturum) ve puan ≥ eşik−0,15.
/// Küme başına tek öneri; reddedilmiş küme yeniden önerilmez; üst kümeye bağlanan alt kümenin bekleyen önerisi geri çekilir.
/// </summary>
public sealed class ProposalEngine
{
    private readonly IDecisionExtractor _extractor;
    public double Threshold { get; }
    public ProposalEngine(IDecisionExtractor extractor, double threshold)
    {
        _extractor = extractor;
        Threshold = threshold;
    }

    /// <summary>extracted=0 olan mesajları ayıklar, adayları yazar.</summary>
    public (int extracted, int candidates) ExtractPending(SqliteConnection c)
    {
        int ex = 0, cands = 0;
        while (true)
        {
            var batch = c.Query("SELECT id, project_id, text FROM utterances WHERE extracted=0 ORDER BY id LIMIT 2000",
                r => (id: r.GetInt64(0), pid: r.LongN(1), text: r.Str(2) ?? ""));
            if (batch.Count == 0) break;
            using var tx = c.BeginTransaction();
            foreach (var u in batch)
            {
                foreach (var cand in _extractor.Extract(u.text))
                {
                    c.Exec("INSERT INTO candidates(utterance_id, project_id, type, text, score, cluster_id) VALUES(@u,@p,@t,@x,@s,NULL)", tx,
                        ("@u", u.id), ("@p", u.pid), ("@t", cand.Type), ("@x", cand.Text), ("@s", cand.Score));
                    cands++;
                }
                c.Exec("UPDATE utterances SET extracted=1 WHERE id=@id", tx, ("@id", u.id));
                ex++;
            }
            tx.Commit();
        }
        return (ex, cands);
    }

    public ProposalStats Propose(SqliteConnection c, int extracted = 0, int newCandidates = 0)
    {
        int created = 0, updated = 0, withdrawn = 0;
        var now = Time.NowIso();
        using var tx = c.BeginTransaction();

        // Üst kümeye bağlanmış alt kümelerin bekleyen önerileri geri çekilir (öneri üst kümede yaşar).
        withdrawn = c.Exec("""
            DELETE FROM decisions WHERE status='proposed' AND cluster_id IN (SELECT id FROM clusters WHERE parent_id IS NOT NULL)
            """, tx);

        var clusters = c.Query("""
            SELECT id, project_id, canonical, count, session_count, max_score FROM clusters WHERE parent_id IS NULL
            """, r => (id: r.GetInt64(0), pid: r.Long(1), text: r.Str(2) ?? "", n: r.Long(3), s: r.Long(4), m: r.Dbl(5)), tx);
        var existing = c.Query("SELECT cluster_id, id, status FROM decisions WHERE cluster_id IS NOT NULL",
            r => (cid: r.GetInt64(0), id: r.GetInt64(1), status: r.Str(2)), tx).GroupBy(x => x.cid).ToDictionary(g => g.Key, g => g.First());

        foreach (var cl in clusters)
        {
            var qualifies = cl.m >= Threshold || (cl.s >= 2 && cl.m >= Threshold - 0.15);
            if (existing.TryGetValue(cl.id, out var d))
            {
                if (d.status == "proposed")
                {
                    var (type, origin) = BestType(c, tx, cl.id);
                    if (!qualifies)
                    {
                        c.Exec("DELETE FROM decisions WHERE id=@id", tx, ("@id", d.id));
                        withdrawn++;
                        continue;
                    }
                    updated += c.Exec("UPDATE decisions SET text=@t, type=@ty, repeat_count=@n, origin_candidate_id=COALESCE(origin_candidate_id,@o), updated_at=@now WHERE id=@id AND (text<>@t OR repeat_count<>@n OR type<>@ty)", tx,
                        ("@t", cl.text), ("@ty", type), ("@n", cl.n), ("@o", origin), ("@now", now), ("@id", d.id));
                }
                else
                {
                    c.Exec("UPDATE decisions SET repeat_count=@n WHERE id=@id AND repeat_count<>@n", tx, ("@n", cl.n), ("@id", d.id));
                }
                continue;
            }
            if (!qualifies) continue;
            var (t, o) = BestType(c, tx, cl.id);
            c.Exec("""
                INSERT INTO decisions(project_id, type, text, status, pinned, origin_candidate_id, cluster_id, repeat_count, created_at, updated_at, uid)
                VALUES(@p,@t,@x,'proposed',0,@o,@c,@n,@now,@now,@u)
                """, tx, ("@u", Uid.New()), ("@p", cl.pid), ("@t", t), ("@x", cl.text), ("@o", o), ("@c", cl.id), ("@n", cl.n), ("@now", now));
            created++;
        }
        tx.Commit();
        return new ProposalStats(extracted, newCandidates, created, updated, withdrawn);
    }

    /// <summary>Kümenin karar türü: en yüksek puanlı üyenin türü (düzeltme → olumsuzsa yasak, değilse kural).</summary>
    private (string type, long? origin) BestType(SqliteConnection c, SqliteTransaction tx, long clusterId)
    {
        var rows = c.Query("""
            SELECT c.id, c.type, c.text, c.score FROM candidates c
            WHERE c.cluster_id=@c OR c.cluster_id IN (SELECT id FROM clusters WHERE parent_id=@c)
            ORDER BY c.score DESC, length(c.text) LIMIT 1
            """, r => (id: r.GetInt64(0), type: r.Str(1) ?? "rule", text: r.Str(2) ?? ""), tx, ("@c", clusterId));
        if (rows.Count == 0) return ("rule", null);
        var best = rows[0];
        var type = best.type;
        if (type == CandidateTypes.Correction && _extractor is RuleBasedExtractor rb)
            type = RuleBasedExtractor.DecisionTypeFor(type, rb.Score(best.text).Negative);
        else if (type == CandidateTypes.Correction) type = CandidateTypes.Rule;
        return (type, best.id);
    }
}
