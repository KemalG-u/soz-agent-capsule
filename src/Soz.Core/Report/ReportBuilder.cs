using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Report;

public sealed class ToolShare
{
    public string Tool { get; set; } = "";
    public long Sessions { get; set; }
    public long Utterances { get; set; }
    public long Compactions { get; set; }
}

public sealed class TopRepeat
{
    public long ClusterId { get; set; }
    public string Text { get; set; } = "";
    public long Count { get; set; }
    public long SessionCount { get; set; }
}

public sealed class DailyCount
{
    public string Date { get; set; } = "";
    public long Utterances { get; set; }
}

public sealed class ReportCard
{
    public int Days { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public long Projects { get; set; }
    public long Sessions { get; set; }
    public long Utterances { get; set; }
    public List<ToolShare> Tools { get; set; } = new();
    public long Compactions { get; set; }
    public long Candidates { get; set; }
    public long RepeatClusters { get; set; }
    public long TotalRepeats { get; set; }
    public List<TopRepeat> TopRepeats { get; set; } = new();
    public List<DailyCount> Daily { get; set; } = new();
    public long AutomationSessions { get; set; }
    public long SubagentSessions { get; set; }
    public long Proposed { get; set; }
    public long Approved { get; set; }
}

/// <summary>Karne (sözleşme 3.8). Sayılar yalnız insan oturumlarının mesajlarından; otomasyon ve alt ajan ayrı sayılır.</summary>
public static class ReportBuilder
{
    public static ReportCard Build(SqliteConnection c, int days, DateTime nowUtc)
    {
        var from = Time.Iso(nowUtc.AddDays(-days));
        var to = Time.Iso(nowUtc);
        var r = new ReportCard { Days = days, From = from, To = to };
        (string, object?) F = ("@f", from);

        r.Utterances = c.ScalarLong("SELECT COUNT(*) FROM utterances WHERE ts >= @f", null, F);
        r.Projects = c.ScalarLong("SELECT COUNT(DISTINCT project_id) FROM utterances WHERE ts >= @f", null, F);
        r.Sessions = c.ScalarLong("SELECT COUNT(DISTINCT session_id) FROM utterances WHERE ts >= @f", null, F);
        r.Tools = c.Query("""
            SELECT s.tool, COUNT(DISTINCT u.session_id), COUNT(*) FROM utterances u JOIN sessions s ON s.id=u.session_id
            WHERE u.ts >= @f GROUP BY s.tool ORDER BY s.tool
            """, x => new ToolShare { Tool = x.GetString(0), Sessions = x.Long(1), Utterances = x.Long(2) }, null, F);
        foreach (var (tool, n) in c.Query("""
            SELECT s.tool, COUNT(*) FROM compactions k JOIN sessions s ON s.id=k.session_id
            WHERE k.ts >= @f AND s.kind='human' GROUP BY s.tool
            """, x => (x.GetString(0), x.Long(1)), null, F))
        {
            var t = r.Tools.FirstOrDefault(x => x.Tool == tool);
            if (t is null) r.Tools.Add(t = new ToolShare { Tool = tool });
            t.Compactions = n;
        }
        r.Compactions = r.Tools.Sum(t => t.Compactions);
        r.Candidates = c.ScalarLong("SELECT COUNT(*) FROM candidates c JOIN utterances u ON u.id=c.utterance_id WHERE u.ts >= @f", null, F);

        // Tekrar: dönem içinde en az iki ayrı oturumda geçen küme (üst kümeler dahil, alt kümeler üstte sayılır).
        var repeats = c.Query("""
            SELECT t.top, (SELECT canonical FROM clusters WHERE id=t.top), t.n, t.s FROM (
              SELECT COALESCE(k.parent_id, k.id) AS top, COUNT(*) AS n, COUNT(DISTINCT u.session_id) AS s
              FROM candidates c JOIN clusters k ON k.id = c.cluster_id JOIN utterances u ON u.id = c.utterance_id
              WHERE u.ts >= @f GROUP BY top HAVING COUNT(DISTINCT u.session_id) >= 2) t
            ORDER BY t.n DESC, t.s DESC
            """, x => new TopRepeat { ClusterId = x.GetInt64(0), Text = x.Str(1) ?? "", Count = x.Long(2), SessionCount = x.Long(3) }, null, F);
        r.RepeatClusters = repeats.Count;
        r.TotalRepeats = repeats.Sum(x => x.Count - 1);
        r.TopRepeats = repeats.Take(5).ToList();

        var tz = TimeZoneInfo.Local;
        r.Daily = c.Query("SELECT ts FROM utterances WHERE ts >= @f", x => x.GetString(0), null, F)
            .Select(ts => Time.Parse(ts))
            .Where(t => t.HasValue)
            .GroupBy(t => TimeZoneInfo.ConvertTimeFromUtc(t!.Value, tz).ToString("yyyy-MM-dd"))
            .OrderBy(g => g.Key)
            .Select(g => new DailyCount { Date = g.Key, Utterances = g.LongCount() })
            .ToList();
        r.AutomationSessions = c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE kind='automation' AND COALESCE(last_at,'') >= @f", null, F);
        r.SubagentSessions = c.ScalarLong("SELECT COUNT(*) FROM sessions WHERE kind='subagent' AND COALESCE(last_at,'') >= @f", null, F);
        r.Proposed = c.ScalarLong("SELECT COUNT(*) FROM decisions WHERE status='proposed'");
        r.Approved = c.ScalarLong("SELECT COUNT(*) FROM decisions WHERE status='approved'");
        return r;
    }
}
