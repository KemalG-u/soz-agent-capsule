using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Soz.Core.Onboarding;

/// <summary>Kayıt defterindeki "tarayıcılar" listesinden bir satır: görünen ad ve (varsa) çalıştırma dosyası. Başlat menüsü kaydıdır, profil değildir.</summary>
public sealed record ProbeBrowser(string Name, string? ExePath, string Source);

/// <summary>Varsayılan web tarayıcısı: sistemin tuttuğu program kimliği (ProgId) ve o kimliğin açtığı dosyanın adı (argümansız).</summary>
public sealed record ProbeDefaultBrowser(string? ProgId, string? ExeName);

/// <summary>
/// Windows'a özgü okumalar için tek soyutlama (kayıt defteri ve sistem ayarları). Keşif kodu kayıt defterine doğrudan dokunmaz, buradan sorar;
/// testler sahte bir uygulama verir, gerçek makinede <see cref="WindowsSystemProbe"/> kullanılır. Hepsi SALT OKUNURDUR; okunamayan şey null/boş döner.
/// </summary>
public interface ISystemProbe
{
    /// <summary>Kayıt defterinde tarayıcı olarak bildirilmiş programlar.</summary>
    IReadOnlyList<ProbeBrowser> Browsers();
    DefaultBrowserResult DefaultBrowser();
    /// <summary>Ekran ölçeği yüzde olarak (96 DPI = 100, 120 DPI = 125). Okunamazsa null.</summary>
    int? DisplayScalePercent();
    /// <summary>Sistem dili, örnek "tr-TR". Okunamazsa null.</summary>
    string? Language();
}

/// <summary>Varsayılan tarayıcı sorgusunun sonucu: okunabildi mi, ve okunduysa ne.</summary>
public sealed record DefaultBrowserResult(bool Readable, ProbeDefaultBrowser? Value)
{
    public static readonly DefaultBrowserResult Unreadable = new(false, null);
}

/// <summary>Hiçbir şey bilmeyen uygulama (Windows olmayan makine ya da okuma kapalıyken): her şey "okunamadı".</summary>
public sealed class NullSystemProbe : ISystemProbe
{
    public IReadOnlyList<ProbeBrowser> Browsers() => Array.Empty<ProbeBrowser>();
    public DefaultBrowserResult DefaultBrowser() => DefaultBrowserResult.Unreadable;
    public int? DisplayScalePercent() => null;
    public string? Language() => null;
}

/// <summary>
/// Gerçek Windows kayıt defteri okuyucusu. SALT OKUNUR: yalnız <c>OpenSubKey</c> (yazma izni istenmez), <c>GetValue</c>, <c>GetSubKeyNames</c>.
/// Hiçbir tarayıcı profiline, geçmişe, çerezlere dokunmaz: yalnız "hangi tarayıcılar kayıtlı" ve "varsayılan hangisi".
/// Bu sınıf testlerde ÇALIŞTIRILMAZ (gerçek makinenin kaydını okur); testler <see cref="ISystemProbe"/>'in sahte uygulamasını kullanır.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemProbe : ISystemProbe
{
    public IReadOnlyList<ProbeBrowser> Browsers()
    {
        var list = new List<ProbeBrowser>();
        foreach (var (hive, view, label) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM-32"),
                     (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        var display = k?.GetValue(null) as string;
                        using var cmd = k?.OpenSubKey(@"shell\open\command");
                        var exe = ExePathFrom(cmd?.GetValue(null) as string);
                        list.Add(new ProbeBrowser(string.IsNullOrWhiteSpace(display) ? name : display!, exe, $"kayıt defteri ({label})"));
                    }
                    catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return list;
    }

    public DefaultBrowserResult DefaultBrowser()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (k is null) return new DefaultBrowserResult(true, null); // seçim yapılmamış: Windows karar verir
            var progId = k.GetValue("ProgId") as string;
            string? exeName = null;
            if (!string.IsNullOrWhiteSpace(progId))
            {
                using var cmd = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
                var path = ExePathFrom(cmd?.GetValue(null) as string);
                exeName = path is null ? null : Path.GetFileName(path);
            }
            return new DefaultBrowserResult(true, new ProbeDefaultBrowser(progId, exeName));
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return DefaultBrowserResult.Unreadable; }
    }

    public int? DisplayScalePercent()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics");
            var dpi = k?.GetValue("AppliedDPI") as int?;
            if (dpi is null or <= 0)
            {
                using var d = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                dpi = d?.GetValue("LogPixels") as int?;
            }
            return dpi is > 0 ? (int)Math.Round(dpi.Value * 100.0 / 96.0) : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public string? Language()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
            var name = k?.GetValue("LocaleName") as string;
            if (string.IsNullOrWhiteSpace(name)) return null;
            return name.Length <= 20 ? name : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Komut metninden yalnız çalıştırma dosyasının yolu (tırnaklı ya da .exe'ye kadar). Argümanlar atılır.</summary>
    internal static string? ExePathFrom(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? s[..(i + 4)] : null;
    }
}
