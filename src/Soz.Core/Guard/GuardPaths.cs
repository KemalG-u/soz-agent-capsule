using System.Text.RegularExpressions;

namespace Soz.Core.Guard;

/// <summary>Normalleştirilmiş yol: Key = küçük harf, "/" ayraçlı (karşılaştırma); Display = kullanıcıya gösterilecek biçim.</summary>
public sealed record NPath(string Key, string Display);

/// <summary>
/// Yol çözümleme (saf metin; dosya sistemine bakmaz, sembolik bağlantı/junction İZLENMEZ):
/// göreli yol cwd'ye göre, "..", sürücü harfi, Git Bash "/e/..." ve WSL "/mnt/e/...", "E:\...", büyük/küçük harf duyarsız.
/// Çözülemeyen (bilinmeyen değişken, komut ikamesi) yol için null döner.
/// </summary>
public static class GuardPaths
{
    private static readonly Regex HomeVar = new(@"^(~|\$\{?HOME\}?|\$env:(USERPROFILE|HOME)|%USERPROFILE%|%HOME%)(?=[/\\]|$)", RegexOptions.IgnoreCase | RegexSpeed.Compiled);
    private static readonly Regex TempVar = new(@"^(\$\{?(TMP|TEMP|TMPDIR)\}?|\$env:(TEMP|TMP)|%TEMP%|%TMP%)(?=[/\\]|$)", RegexOptions.IgnoreCase | RegexSpeed.Compiled);
    private static readonly Regex AnyVar = new(@"\$[A-Za-z_{(]|%[A-Za-z_][A-Za-z0-9_]*%|`", RegexSpeed.Compiled);
    private static readonly Regex GitBashDrive = new(@"^/([A-Za-z])(?=/|$)", RegexSpeed.Compiled);
    private static readonly Regex WslDrive = new(@"^/(?:mnt|cygdrive)/([A-Za-z])(?=/|$)", RegexOptions.IgnoreCase | RegexSpeed.Compiled);
    private static readonly Regex DriveAbs = new(@"^[A-Za-z]:/", RegexSpeed.Compiled);
    private static readonly Regex DriveRel = new(@"^[A-Za-z]:(?!/)", RegexSpeed.Compiled);

    public static bool IsNullDevice(string raw)
    {
        var s = raw.Trim().Trim('"', '\'');
        return s.Equals("/dev/null", StringComparison.OrdinalIgnoreCase) || s.Equals("nul", StringComparison.OrdinalIgnoreCase)
            || s.Equals("nul:", StringComparison.OrdinalIgnoreCase) || s.Equals("$null", StringComparison.OrdinalIgnoreCase)
            || s.Equals("/dev/stdout", StringComparison.OrdinalIgnoreCase) || s.Equals("/dev/stderr", StringComparison.OrdinalIgnoreCase)
            || s.Equals("/dev/tty", StringComparison.OrdinalIgnoreCase) || s.Equals("con", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("&", StringComparison.Ordinal) || s == "-";
    }

    /// <summary>
    /// Yol bir klasöre göre mi çözülüyor (göreli)? ~, $HOME, %TEMP%, sürücü harfi, "/..." (Git Bash, WSL, kök) mutlaktır;
    /// çözülemeyen değişken içeren yol göreli SAYILMAZ (zaten bilinmiyor olarak ele alınır).
    /// </summary>
    public static bool IsRelative(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))) s = s[1..^1];
        if (HomeVar.IsMatch(s) || TempVar.IsMatch(s) || AnyVar.IsMatch(s)) return false;
        s = s.Replace('\\', '/');
        if (s.StartsWith('/') || DriveAbs.IsMatch(s) || DriveRel.IsMatch(s)) return false;
        if (s.Length == 2 && s[1] == ':') return false;
        return true;
    }

    public static NPath? Normalize(string? raw, string? cwd, string? home, string? temp)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))) s = s[1..^1];

        var hm = HomeVar.Match(s);
        if (hm.Success)
        {
            if (string.IsNullOrEmpty(home)) return null;
            s = home.TrimEnd('\\', '/') + s[hm.Length..];
        }
        else
        {
            var tm = TempVar.Match(s);
            if (tm.Success)
            {
                if (string.IsNullOrEmpty(temp)) return null;
                s = temp.TrimEnd('\\', '/') + s[tm.Length..];
            }
        }
        if (AnyVar.IsMatch(s)) return null; // çözülemeyen değişken / komut ikamesi

        s = s.Replace('\\', '/');
        if (s.StartsWith("//?/", StringComparison.Ordinal)) s = s[4..];
        var wm = WslDrive.Match(s);
        if (wm.Success) s = char.ToUpperInvariant(wm.Groups[1].Value[0]) + ":" + s[wm.Length..];
        else
        {
            var gm = GitBashDrive.Match(s);
            if (gm.Success) s = char.ToUpperInvariant(gm.Groups[1].Value[0]) + ":" + s[gm.Length..];
            else if (temp is not null && (s == "/tmp" || s.StartsWith("/tmp/", StringComparison.Ordinal)))
                s = temp.Replace('\\', '/').TrimEnd('/') + s[4..];
        }
        if (s.Length >= 2 && s[1] == ':' && char.IsLetter(s[0]) && DriveRel.IsMatch(s)) s = s[..2] + "/" + s[2..];
        if (s.Length == 2 && s[1] == ':') s += "/";

        bool absolute = DriveAbs.IsMatch(s) || s.StartsWith('/');
        if (!absolute)
        {
            if (string.IsNullOrWhiteSpace(cwd)) return null;
            var c = Normalize(cwd, null, home, temp);
            if (c is null) return null;
            s = c.Display.Replace('\\', '/').TrimEnd('/') + "/" + s;
        }

        // . ve .. çözümü
        string prefix; string rest;
        if (DriveAbs.IsMatch(s)) { prefix = s[..2]; rest = s[2..]; }
        else if (s.StartsWith("//", StringComparison.Ordinal)) { prefix = "//"; rest = s[2..]; }
        else { prefix = ""; rest = s; }
        var parts = new List<string>();
        foreach (var p in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(p);
        }
        var disp = prefix + "/" + string.Join('/', parts);
        if (prefix == "//") disp = "//" + string.Join('/', parts);
        if (disp.Length > 1) disp = disp.TrimEnd('/');
        if (DriveAbs.IsMatch(disp + "/") && disp.Length == 2) disp += "/";
        return new NPath(disp.ToLowerInvariant(), disp.Replace('/', '\\'));
    }

    public static bool IsUnder(string key, string rootKey)
    {
        rootKey = rootKey.TrimEnd('/');
        if (rootKey.Length == 0) return true;
        return key == rootKey || key.StartsWith(rootKey + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Kalıp: "*" tek ayraç içinde, "**" sınırsız, "?" tek karakter. Ayraçsız göreli kalıp (".env") her derinlikte dosya adına uyar;
    /// ayraçlı göreli kalıp proje köküne, "~", sürücü ya da "/" ile başlayan kalıp kendi köküne göre.
    /// </summary>
    public static bool GlobMatch(string glob, NPath target, string? rootKey, string? home, string? temp)
    {
        var g = glob.Trim().Replace('\\', '/');
        if (g.Length == 0) return false;
        var hm = HomeVar.Match(glob.Trim());
        if (hm.Success)
        {
            if (string.IsNullOrEmpty(home)) return false;
            g = home.Replace('\\', '/').TrimEnd('/') + glob.Trim().Replace('\\', '/')[hm.Length..];
        }
        bool abs = DriveAbs.IsMatch(g) || g.StartsWith('/');
        string pattern;
        if (abs)
        {
            var n = Normalize(g.Replace("*", "\u0001").Replace("?", "\u0002"), "/", home, temp);
            pattern = (n?.Key ?? g.ToLowerInvariant()).Replace("\u0001", "*").Replace("\u0002", "?");
        }
        else if (!g.Contains('/')) pattern = "**/" + g.ToLowerInvariant();
        else
        {
            if (rootKey is null) return false;
            pattern = rootKey.TrimEnd('/') + "/" + g.TrimStart('/').ToLowerInvariant();
        }
        return ToRegex(pattern).IsMatch(target.Key);
    }

    private static readonly Dictionary<string, Regex> Cache = new();

    private static Regex ToRegex(string glob)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(glob, out var r)) return r;
            var sb = new System.Text.StringBuilder("^");
            for (var i = 0; i < glob.Length; i++)
            {
                var ch = glob[i];
                if (ch == '*')
                {
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        // "**/" = sıfır ya da daha çok klasör; sonda "/**" klasörün kendisi de uyar
                        if (i + 1 < glob.Length && glob[i + 1] == '/') { sb.Append("(?:.*/)?"); i++; }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                }
                else if (ch == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(ch.ToString()));
            }
            var s = sb.ToString();
            if (s.EndsWith("/.*", StringComparison.Ordinal)) s = s[..^3] + "(?:/.*)?";
            s += "$";
            r = new Regex(s, RegexSpeed.Compiled | RegexOptions.CultureInvariant);
            Cache[glob] = r;
            return r;
        }
    }
}
