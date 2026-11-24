using System.Text.RegularExpressions;

namespace Soz.Core.Guard;

/// <summary>
/// Söz'ün yürüttüğü işte (SOZ_RUN_ID) süreç kaçıran ve kalıcı ayar değiştiren komutların reddi (denetim 4, F). EN İYİ ÇABA BİR LİSTEDİR, TAM ENGEL DEĞİL:
/// aynı Windows hesabında çalışan kararlı bir ajan listede olmayan bir yol bulabilir (ör. değişkenle kurulan komut, başka bir yorumlayıcı). Amaç sıradan yolları kapatmak ve denemeyi olay kaydına yazmaktır.
/// Reddedilenler: wmic process call create, Invoke-CimMethod/Invoke-WmiMethod/[wmiclass], schtasks /create, Register-ScheduledTask, Start-Process, COM ShellExecute ve WScript.Shell Run/Exec,
/// setx, npm config set, git config core.hooksPath (ve --global/--system), .git/hooks altına yazma, kullanıcı profilindeki .npmrc ve .gitconfig yazımı.
/// </summary>
public static class EscapeGuard
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(150);

    private static readonly (Regex Re, string Why)[] Shell =
    {
        (new(@"\bwmic(?:\.exe)?\b[^|;&\r\n]*\bprocess\b[^|;&\r\n]*\bcall\b[^|;&\r\n]*\bcreate\b", Opt, Limit), "wmic ile süreç yaratılıyor (Söz'ün iş nesnesinden çıkış yolu)"),
        (new(@"\bInvoke-(?:Cim|Wmi)Method\b|\[wmiclass\]|\[wmi\]|\bNew-CimInstance\b[^|;&\r\n]*Win32_|Win32_Process\b[^|;&\r\n]*\bCreate\b|\bCreate\b[^|;&\r\n]*Win32_Process\b", Opt, Limit), "WMI ile süreç yaratılıyor (Söz'ün iş nesnesinden çıkış yolu)"),
        (new(@"\bschtasks(?:\.exe)?\b[^|;&\r\n]*[/-]create\b|\bRegister-ScheduledTask\b|\bNew-ScheduledTask\b|\bSchedule\.Service\b", Opt, Limit), "zamanlanmış görev kuruluyor"),
        (new(@"\bStart-Process\b|(?<![\w-])saps(?![\w-])", Opt, Limit), "Start-Process ile süreç başlatılıyor"),
        (new(@"\bShellExecute(?:Ex)?\b|\bWScript\.Shell\b[^|;&\r\n]*\.(?:Run|Exec)\b|\bMMC20\.Application\b", Opt, Limit), "COM üzerinden süreç başlatılıyor"),
        (new(@"(?<![\w-])setx(?:\.exe)?(?![\w-])", Opt, Limit), "kalıcı ortam değişkeni yazılıyor (setx)"),
        (new(@"(?<![\w-])(?:npm|npm\.cmd|yarn|pnpm)(?:\.cmd)?\s+(?:config|c)\s+(?:set|delete|edit)\b|(?<![\w-])npm(?:\.cmd)?\s+set\b", Opt, Limit), "npm ayarı değiştiriliyor"),
        (new(@"(?<![\w-])git(?:\.exe)?\b[^|;&\r\n]*core\.hooksPath", Opt, Limit), "git'in otomatik betik klasörü değiştiriliyor (core.hooksPath)"),
        (new(@"(?<![\w-])git(?:\.exe)?\s+(?:[^|;&\r\n]*\s)?config\b[^|;&\r\n]*--(?:global|system)\b", Opt, Limit), "kullanıcının genel git ayarı yazılıyor"),
    };

    private static readonly Regex GitHooksPath = new(@"(?:^|[\s\\/""'=>:])\.git[\\/]hooks(?:[\\/\s""']|$)", Opt, Limit);
    private static readonly Regex WriteWords = new(@">|\btee\b|\bcp\b|\bmv\b|\bcopy\b|\bmove\b|\bSet-Content\b|\bAdd-Content\b|\bOut-File\b|\bNew-Item\b|\bWrite-Output\b|\bsc\b|\bac\b|\bni\b|\bchmod\b|\bln\b|\bmklink\b|\binstall\b|\bwriteFile|\bappendFile|\becho\b|\bprintf\b|\bcat\b", Opt, Limit);
    private static readonly Regex UserFiles = new(@"(?:~|\$HOME|\$env:(?:USERPROFILE|HOME)|%USERPROFILE%|%HOMEPATH%|[A-Za-z]:[\\/]Users[\\/][^\\/\s""']+)[\\/]\.(?:npmrc|gitconfig)\b|[\\/]\.config[\\/]git[\\/]config\b", Opt, Limit);

    /// <summary>Kabuk komutu yasak listesinden birine uyuyorsa kısa Türkçe ayrıntı, yoksa null.</summary>
    public static string? HitCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        try
        {
            foreach (var (re, why) in Shell) if (re.IsMatch(command)) return why;
            if (GitHooksPath.IsMatch(command) && WriteWords.IsMatch(command)) return ".git/hooks altına yazılıyor";
            if (UserFiles.IsMatch(command) && WriteWords.IsMatch(command)) return "kullanıcı profilindeki .npmrc ya da .gitconfig yazılıyor";
        }
        catch (RegexMatchTimeoutException) { return "komut çözümlenemedi (kaçış denemesi olabilir)"; }
        return null;
    }

    /// <summary>Dosya aracı (Write, Edit) hedefi yasak yerde mi? (.git/hooks, kullanıcı profilindeki .npmrc ve .gitconfig)</summary>
    public static string? HitPath(string? path, string? home)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('\\', '/');
        if (GitHooksPath.IsMatch(p)) return ".git/hooks altına yazılıyor";
        var name = p[(p.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (name is ".npmrc" or ".gitconfig" && !string.IsNullOrWhiteSpace(home))
        {
            var h = home.Replace('\\', '/').TrimEnd('/');
            var dir = p[..p.LastIndexOf('/')].TrimEnd('/');
            if (dir.Equals(h, StringComparison.OrdinalIgnoreCase) || p.StartsWith("~/", StringComparison.Ordinal)) return "kullanıcı profilindeki .npmrc ya da .gitconfig yazılıyor";
        }
        if (p.EndsWith("/.config/git/config", StringComparison.OrdinalIgnoreCase)) return "kullanıcı profilindeki git ayarı yazılıyor";
        return null;
    }
}
