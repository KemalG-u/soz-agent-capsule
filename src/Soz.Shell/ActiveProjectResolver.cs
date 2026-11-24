namespace Soz.Shell;

internal sealed record ActiveProject(long Id, string Name, string Source);

/// <summary>
/// Sözleşme bölüm 7: öndeki pencere başlığı proje adı/yolunu içeriyorsa o; yoksa son 10 dakikada en son mesaj alan
/// (insan) oturumun projesi; yoksa en son proje. Kabuğun kendi penceresi öndeyse son bilinen değer korunur.
/// </summary>
internal sealed class ActiveProjectResolver
{
    private readonly Func<string> _titleProvider;
    private readonly Func<bool> _foregroundIsOwn;
    private ActiveProject? _last;

    public ActiveProjectResolver(Func<string>? titleProvider = null, Func<bool>? foregroundIsOwn = null)
    {
        _titleProvider = titleProvider ?? (() => Native.GetWindowTitle(Native.GetForegroundWindow()));
        _foregroundIsOwn = foregroundIsOwn ?? (() => Native.IsOwnWindow(Native.GetForegroundWindow()));
    }

    public ActiveProject? Last => _last;

    public ActiveProject? Resolve(IReadOnlyList<ProjectInfo> projects, IReadOnlyList<SessionInfo> sessions, DateTimeOffset now)
    {
        string title; bool own;
        try { own = _foregroundIsOwn(); title = own ? "" : _titleProvider(); }
        catch { own = false; title = ""; }
        var r = Pick(title, own, projects, sessions, now, _last);
        if (r != null) _last = r;
        return r;
    }

    public static ActiveProject? Pick(string title, bool ownWindowForeground, IReadOnlyList<ProjectInfo> projects,
        IReadOnlyList<SessionInfo> sessions, DateTimeOffset now, ActiveProject? last)
    {
        if (ownWindowForeground && last != null) return last;

        // 1) başlık eşleşmesi: en uzun eşleşen ad/yol kazanır
        if (!string.IsNullOrWhiteSpace(title))
        {
            var t = Norm(title);
            ProjectInfo? best = null; var bestLen = 0;
            foreach (var p in projects)
            {
                if (string.IsNullOrWhiteSpace(p.Path)) continue; // Genel (*) projesinin yolu yoktur; başlıkla eşleşmez
                var len = MatchLength(t, p);
                if (len > bestLen) { best = p; bestLen = len; }
            }
            if (best != null) return new ActiveProject(best.Id, best.Name, "title");
        }

        // 2) son 10 dakikada en son mesaj alan insan oturumu
        SessionInfo? recent = null; DateTimeOffset recentTs = default;
        foreach (var s in sessions)
        {
            if (s.Kind.Length > 0 && s.Kind != "human") continue;
            var ts = Max(s.LastUserTs, s.LastAssistantTs);
            if (ts == null || now - ts.Value > TimeSpan.FromMinutes(10)) continue;
            if (recent == null || ts.Value > recentTs) { recent = s; recentTs = ts.Value; }
        }
        if (recent != null && recent.ProjectId != 0) return new ActiveProject(recent.ProjectId, recent.ProjectName, "session");

        // 3) son bilinen değer varsa onu koru, yoksa en son proje
        if (last != null && projects.Any(p => p.Id == last.Id)) return last;
        var latest = projects.Where(p => !string.IsNullOrWhiteSpace(p.Path)).OrderByDescending(p => p.LastAt ?? DateTimeOffset.MinValue).FirstOrDefault();
        return latest == null ? last : new ActiveProject(latest.Id, latest.Name, "latest");
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a == null ? b : b == null ? a : (a > b ? a : b);

    private static string Norm(string s) => s.Replace('/', '\\').ToLowerInvariant();

    private static int MatchLength(string normTitle, ProjectInfo p)
    {
        var len = 0;
        if (!string.IsNullOrWhiteSpace(p.Path))
        {
            var path = Norm(p.Path.Trim()).TrimEnd('\\');
            if (path.Length >= 3 && normTitle.Contains(path)) len = Math.Max(len, path.Length + 1000);
        }
        var name = p.Name.Trim().ToLowerInvariant();
        if (name.Length >= 3 && normTitle.Contains(name)) len = Math.Max(len, name.Length);
        return len;
    }
}
