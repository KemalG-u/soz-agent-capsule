using Microsoft.Win32;

namespace Soz.Shell;

/// <summary>
/// Windows açılışında Söz'ü başlatma kaydı (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>).
/// İlk kurulumda (kullanıcı daha önce kapatmadıysa) açılır; tepsi menüsündeki işaretli seçenekle kapatılıp açılabilir.
/// Görev Yöneticisi'nin "Başlangıç" sekmesinden kapatılmış olması da "kapalı" sayılır (StartupApproved kaydı).
/// Sınama için kayıt yolları verilebilir; varsayılanlar gerçek Windows yollarıdır.
/// </summary>
internal static class StartWithWindows
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "Soz";

    /// <summary>Kurulum kancası: kullanıcı önceden "kapalı" seçtiyse (tercih dosyası) kayıt eklenmez, yoksa eklenir.</summary>
    public static void EnableOnFreshInstall(string? exePath, IEnumerable<string>? tercihKlasorleri = null, string runKeyPath = RunKeyPath, string approvedKeyPath = ApprovedKeyPath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        foreach (var k in tercihKlasorleri ?? [])
            if (KabukTercihleri.Oku(k).AcilistaBasla == false) return;
        Enable(exePath, runKeyPath, approvedKeyPath);
    }

    public static bool Enable(string? exePath, string runKeyPath = RunKeyPath, string approvedKeyPath = ApprovedKeyPath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            var full = Path.GetFullPath(exePath);
            if (!File.Exists(full)) return false;
            using (var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true))
                key.SetValue(ValueName, $"\"{full}\" --background", RegistryValueKind.String);
            // Görev Yöneticisi'nde daha önce kapatılmışsa kullanıcı şimdi açıkça istedi: onay kaydını da aç.
            using (var ap = Registry.CurrentUser.OpenSubKey(approvedKeyPath, writable: true))
                ap?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }

    public static void Disable(string runKeyPath = RunKeyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    /// <summary>Kayıt var ve Görev Yöneticisi'nde kapatılmamış mı? Tepsideki işaretin tek doğruluk kaynağı.</summary>
    public static bool IsEnabled(string runKeyPath = RunKeyPath, string approvedKeyPath = ApprovedKeyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string s || string.IsNullOrWhiteSpace(s)) return false;
            using var ap = Registry.CurrentUser.OpenSubKey(approvedKeyPath, writable: false);
            if (ap?.GetValue(ValueName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1) return false; // 3 = kapatılmış
            return true;
        }
        catch { return false; }
    }
}
