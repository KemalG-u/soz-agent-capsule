using System.Text.RegularExpressions;

namespace Soz.Core.Guard;

/// <summary>
/// "Ajan Söz'ü kendi eliyle kullanamaz" denetimi (denetim 3, ilke 2). Bir komutun Söz'ün kendisine uzandığını arar:
/// soz / Soz-Masaustu çağırma, Söz veri klasörüne ya da soz.db'ye değinme, Söz sunucusuna giden her loopback adresi biçimi.
/// Hook çağrısı (soz hook ...) serbesttir. Değişkenle ya da dosya içinde kurulan adresler görülemez: asıl koruma sunucudaki çağıran kimliği denetimidir (CallerIdentity).
/// </summary>
public static class SozSelfGuard
{
    public const string Pattern = "@soz-self";
    public const string Label = "Söz'ün kendisini kullanma yasak";

    private static readonly HashSet<string> SozHeads = new(StringComparer.OrdinalIgnoreCase) { "soz", "soz-masaustu", "soz.exe", "soz-masaustu.exe" };
    private static readonly HashSet<string> DbTools = new(StringComparer.OrdinalIgnoreCase)
        { "sqlite", "sqlite3", "litecli", "sqlitebrowser", "python", "python3", "py", "node", "nodejs", "deno", "bun", "ruby", "perl", "php", "powershell", "pwsh" };

    // 127.x.y.z, 127.1, 0x7f..., 2130706433, 017700000001, [::1], localhost, 0.0.0.0
    private const string LoopHost = @"(?:\b127(?:\.\d{1,3}){1,3}\b|\b0x7f[0-9a-f.x]*|\b2130706433\b|\b0?17700000001\b|\[(?:::1|0*:0*:0*:0*:0*:0*:0*:0*1|::ffff:[0-9a-f.:]+)\]|\blocalhost\b|\b0\.0\.0\.0\b|\[::\])";
    private static readonly Regex Loop = new(LoopHost, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex DefaultPorts = new(@"[:\s](?:4930\d|4931\d|49320)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Komut Söz'ün kendisine uzanıyorsa kısa Türkçe ayrıntı, yoksa null.</summary>
    public static string? Hit(Cmd c, string? dataDir)
    {
        var args = c.Args;
        var full = c.Full;
        if (SozHeads.Contains(c.Head) || SozHeads.Contains(c.HeadRaw ?? ""))
        {
            if (args.Count > 0 && args[0].Text.Equals("hook", StringComparison.OrdinalIgnoreCase)) return null;
            return "Söz komutu çağrılıyor (kabul, ayar ve kayıt işlemleri yalnız insanın elindedir)";
        }
        if (full.Contains("Soz-Masaustu", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(full, @"(?:^|[\\/\s""'])soz(?:\.exe)?(?:$|[\s""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) && args.Any(a => a.Text.Contains("job", StringComparison.OrdinalIgnoreCase) || a.Text.Contains("accept", StringComparison.OrdinalIgnoreCase)))
            return "Söz programı başka bir komut üzerinden çağrılıyor";

        if (full.Contains("soz.db", StringComparison.OrdinalIgnoreCase)) return "Söz'ün veri tabanına değiniyor";
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            var d1 = dataDir.TrimEnd('\\', '/');
            var d2 = d1.Replace('\\', '/');
            if (full.Contains(d1, StringComparison.OrdinalIgnoreCase) || full.Contains(d2, StringComparison.OrdinalIgnoreCase) || Flatten(full).Contains(Flatten(d1), StringComparison.OrdinalIgnoreCase))
                return "Söz'ün veri klasörüne değiniyor";
        }
        if (DbTools.Contains(c.Head) && full.Contains("sqlite", StringComparison.OrdinalIgnoreCase) && full.Contains("soz", StringComparison.OrdinalIgnoreCase))
            return "Söz'ün veri tabanına değiniyor";

        if (LoopOrOwnHost(full))
        {
            if (DefaultPorts.IsMatch(full)) return "Söz'ün sunucusuna doğrudan istek gidiyor";
            if (ServerPortHit(full, dataDir)) return "Söz'ün sunucusuna doğrudan istek gidiyor";
        }
        return null;
    }

    private static string Flatten(string s) => s.Replace('\\', '/').Replace("//", "/");

    private static bool LoopOrOwnHost(string full)
    {
        try
        {
            if (Loop.IsMatch(full)) return true;
            var host = Environment.MachineName;
            return host.Length > 2 && full.Contains(host, StringComparison.OrdinalIgnoreCase);
        }
        catch (RegexMatchTimeoutException) { return true; }
    }

    private static bool ServerPortHit(string full, string? dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return false;
        try
        {
            var info = ServerFile.Read(dataDir);
            if (info?.Url is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            return Regex.IsMatch(full, @"[:\s]" + u.Port + @"\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception e) when (e is IOException or RegexMatchTimeoutException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Komut metninin tamamı için (Söz koşusunda şablondan bağımsız denetim).</summary>
    public static string? HitAny(IEnumerable<Cmd> cmds, string? dataDir)
    {
        foreach (var c in cmds)
            if (Hit(c, dataDir) is { } why) return why;
        return null;
    }
}
