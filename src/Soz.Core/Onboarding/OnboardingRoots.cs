namespace Soz.Core.Onboarding;

/// <summary>
/// Keşfin baktığı kök yollar. Keşif kodu hiçbir yolu kendi bilmez: hepsi buradan gelir (testler sentetik ağaç verir, gerçek makinede ortamdan çözülür).
/// </summary>
public sealed record OnboardingRoots(
    string Home, string AppData, string LocalAppData, string ProgramFiles, string ProgramFilesX86,
    IReadOnlyList<string> PathEntries, string ClaudeHome, string CodexHome, string HermesHome, string OpenCodeData,
    string? AntigravityHomeOverride = null, string? AntigravityCliHomeOverride = null,
    string? VsCodeStorageOverride = null, string? CursorStorageOverride = null,
    string? ClaudeDesktopSessionsOverride = null, bool IsSynthetic = false)
{
    public string AntigravityHome => AntigravityHomeOverride ?? Path.Combine(Home, ".gemini", "antigravity");
    public string AntigravityCliHome => AntigravityCliHomeOverride ?? Path.Combine(Home, ".gemini", "antigravity-cli");
    public string VsCodeStorage => VsCodeStorageOverride ?? Path.Combine(AppData, "Code", "User", "workspaceStorage");
    public string CursorStorage => CursorStorageOverride ?? Path.Combine(AppData, "Cursor", "User", "workspaceStorage");
    public string ClaudeDesktopSessions => ClaudeDesktopSessionsOverride ?? Path.Combine(AppData, "Claude", "local-agent-mode-sessions");

    /// <summary>Tek bir ev klasörünün altında kurulu bir ağaç varsayar. Ortam değişkenine HİÇ bakmaz (test ve deneme için).</summary>
    public static OnboardingRoots Under(string home, IEnumerable<string>? pathEntries = null)
    {
        var appData = Path.Combine(home, "AppData", "Roaming");
        return new OnboardingRoots(home, appData, Path.Combine(home, "AppData", "Local"), Path.Combine(home, "Program Files"), Path.Combine(home, "Program Files (x86)"),
            (pathEntries ?? new[] { Path.Combine(appData, "npm"), Path.Combine(home, ".local", "bin") }).ToList(),
            Path.Combine(home, ".claude"), Path.Combine(home, ".codex"), Path.Combine(home, ".hermes"), Path.Combine(home, ".local", "share", "opencode"),
            IsSynthetic: true);
    }

    /// <summary>Bu bilgisayarın gerçek kökleri (ortam değişkenleri ve kullanıcı/makine PATH değeri). Yalnız gerçek ilk koşuda yönetici kullanır.</summary>
    public static OnboardingRoots FromEnvironment()
    {
        string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
        // Ev klasörü önce USERPROFILE ortam değişkeninden (sınama ve yalıtılmış koşular onu değiştirir; .NET'in kendi çağrısı bunu izlemez, gerçek profile sızar).
        var home = Env("USERPROFILE", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var paths = new List<string>();
        void AddPath(string? p) { if (!string.IsNullOrWhiteSpace(p)) paths.AddRange(p.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); }
        AddPath(Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows())
        {
            // İşlem PATH'i eksik olabilir (Git Bash'ten başlayan süreçte olduğu gibi): kullanıcı ve makine değerleri de eklenir.
            try { AddPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)); } catch (Exception e) when (e is System.Security.SecurityException or IOException) { }
            try { AddPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)); } catch (Exception e) when (e is System.Security.SecurityException or IOException) { }
        }
        var appData = Env("APPDATA", Path.Combine(home, "AppData", "Roaming"));
        var local = Env("LOCALAPPDATA", Path.Combine(home, "AppData", "Local"));
        var claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } cc ? cc : Path.Combine(home, ".claude");
        var codex = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } ch ? ch : Path.Combine(home, ".codex");
        var hermes = Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } hh ? hh : Path.Combine(home, ".hermes");
        var opencode = Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR") is { Length: > 0 } od ? od : Path.Combine(home, ".local", "share", "opencode");
        // Antigravity: diğer yardımcılardaki gibi kök değişkenleri (yoksa ev klasörünün altındaki varsayılan).
        var antigravity = Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME") is { Length: > 0 } ah ? ah : null;
        var antigravityCli = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_HOME") is { Length: > 0 } ach ? ach : null;
        return new OnboardingRoots(home, appData, local, Env("ProgramFiles", @"C:\Program Files"), Env("ProgramFiles(x86)", @"C:\Program Files (x86)"),
            paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), claude, codex, hermes, opencode,
            AntigravityHomeOverride: antigravity, AntigravityCliHomeOverride: antigravityCli);
    }

    /// <summary>Kullanıcıya gösterilecek yol: ev klasörü "~" olur (dışarı kullanıcı adı çıkmaz).</summary>
    public string Abbrev(string path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(Home)) return path;
        var home = Home.TrimEnd('\\', '/');
        if (path.Equals(home, StringComparison.OrdinalIgnoreCase)) return "~";
        if (path.Length > home.Length && path.StartsWith(home, StringComparison.OrdinalIgnoreCase) && (path[home.Length] is '\\' or '/'))
            return "~" + path[home.Length..];
        return path;
    }
}
