namespace Soz.Core.Profile;

public static class ProjectStateThresholds
{
    public const int RecentDays=7;
    public const int PausedQuietDays=14;
    public const int OldQuietDays=60;
    public const int MinimumHighSessions=5;
    public const int MinimumHighActiveDays=3;
    public const int SetupDays=7;
    public const int SetupSessions=6;
    public const int MaintenanceWindowDays=42;
    public const int MaintenanceWeeks=3;
    public const int MaintenanceMaxPerWeek=2;
    public const int MaintenanceMaxQuietDays=21;
}

/// <summary>Heuristics from root timestamps only. Never an assertion of completion, intent or time worked.</summary>
public static class ProjectStates
{
    private sealed record Timeline(int Sessions,int Days,int? Quiet,DateTime? First,bool Maintenance);
    public static List<ProjectProfile> Apply(IReadOnlyList<ProjectProfile> projects,
        IReadOnlyDictionary<string,List<SessionMetadata>> groups,DateTime now)
    {
        var timelines=new Dictionary<string,Timeline>(StringComparer.OrdinalIgnoreCase);
        foreach(var project in projects)
        {
            var roots=groups[project.Path].Where(ProjectResolver.IsRoot).ToArray();
            var events=roots.SelectMany(s=>new DateTime?[]{s.StartedAt,s.LastActivity})
                .Where(d=>d.HasValue&&d.Value!=DateTime.MinValue&&d.Value<=now.AddMinutes(1)).Select(d=>d!.Value).ToArray();
            var days=events.Select(d=>d.ToLocalTime().Date).Distinct().Count();
            var quiet=events.Length==0?(int?)null:Math.Max(0,(int)Math.Floor((now-events.Max()).TotalDays));
            var first=events.Length==0?(DateTime?)null:events.Min();
            var setupEnd=first?.AddDays(ProjectStateThresholds.SetupDays);
            var setup=roots.Count(s=>(s.StartedAt??s.LastActivity)>=first&&(s.StartedAt??s.LastActivity)<setupEnd);
            var visits=roots.Where(s=>(s.StartedAt??s.LastActivity)>=setupEnd&&s.LastActivity>=now.AddDays(-ProjectStateThresholds.MaintenanceWindowDays)&&s.LastActivity<=now.AddMinutes(1))
                .GroupBy(s=>WeekStart(s.LastActivity!.Value)).ToArray();
            var maintenance=setup>=ProjectStateThresholds.SetupSessions&&quiet<=ProjectStateThresholds.MaintenanceMaxQuietDays
                &&visits.Length>=ProjectStateThresholds.MaintenanceWeeks&&visits.All(g=>g.Count()<=ProjectStateThresholds.MaintenanceMaxPerWeek);
            timelines[project.Path]=new(roots.Length,days,quiet,first,maintenance);
        }
        int UpperHalf(IEnumerable<int> values){var sorted=values.OrderDescending().ToArray();return sorted.Length==0?int.MaxValue:sorted[(sorted.Length-1)/2];}
        var sessionsThreshold=Math.Max(ProjectStateThresholds.MinimumHighSessions,UpperHalf(timelines.Values.Select(t=>t.Sessions)));
        var daysThreshold=Math.Max(ProjectStateThresholds.MinimumHighActiveDays,UpperHalf(timelines.Values.Select(t=>t.Days)));
        return projects.Select(p=>{
            var t=timelines[p.Path];var high=t.Sessions>=sessionsThreshold||t.Days>=daysThreshold;
            var status=t.First>=now.AddDays(-ProjectStateThresholds.RecentDays)?"yeni":
                t.Maintenance?"yasayan-sistem":
                p.Sessions7Days>0?"etkin":
                high&&t.Quiet>=ProjectStateThresholds.PausedQuietDays?"duraklamis":
                !high&&t.Quiet>=ProjectStateThresholds.OldQuietDays?"eski":"belirsiz";
            return p with {Status=status,ActiveDays=t.Days,LastQuietDays=t.Quiet};
        }).ToList();
    }

    private static DateTime WeekStart(DateTime timestamp)
    {
        var date=timestamp.ToLocalTime().Date;
        return date.AddDays(-((int)date.DayOfWeek+6)%7);
    }
}
