using System.Drawing;
#if WINDOWS7_0_OR_GREATER || WINDOWS
using Microsoft.Win32;
#endif

namespace Soz.Shell;

/// <summary>
/// Kabuk, panel zemini, tepsi ve kapsül renkleri.
/// Altı onaylı tema ailesi (cam, terminal, mat, ton, duz, sicak) ve açık/koyu kipler desteklenir.
/// </summary>
internal sealed record Palette(
    bool Light,
    Color Body, Color Border, Color Text, Color SubText, Color Icon, Color Hover,
    Color Warm, Color Warn, Color Calm, Color BadgeWarmText, Color BadgeWarnText,
    int ShadowAlpha,
    string Family = "cam",
    string Mode = "sistem",
    Color PanelBg = default,
    Color Accent = default)
{
    public static readonly string[] Families = { "cam", "terminal", "mat", "ton", "duz", "sicak" };

    /// <summary>Panel için tam opak zemin rengi (WebView2 soğuk açılışta siyah ya da beyaz patlama yapmasın diye).</summary>
    public Color EffectivePanelBg => PanelBg.A == 0
        ? (Light ? Color.FromArgb(255, 247, 249, 254) : Color.FromArgb(255, 22, 26, 36))
        : Color.FromArgb(255, PanelBg.R, PanelBg.G, PanelBg.B);

    public Color EffectiveAccent => Accent.A == 0 ? Calm : Accent;

    // ---- 1. cam (serin buzlu mavi) ----
    public static readonly Palette LightTheme = new(
        true,
        Color.FromArgb(244, 251, 251, 253), Color.FromArgb(34, 20, 30, 60), Color.FromArgb(255, 18, 22, 29), Color.FromArgb(255, 65, 75, 94),
        Color.FromArgb(255, 48, 56, 72), Color.FromArgb(24, 20, 40, 90),
        Color.FromArgb(255, 226, 120, 24), Color.FromArgb(255, 185, 44, 39), Color.FromArgb(255, 11, 108, 196),
        Color.FromArgb(255, 36, 20, 4), Color.FromArgb(255, 255, 255, 255),
        7, "cam", "acik", Color.FromArgb(255, 247, 249, 254), Color.FromArgb(255, 11, 108, 196));

    public static readonly Palette DarkTheme = new(
        false,
        Color.FromArgb(246, 24, 28, 40), Color.FromArgb(56, 255, 255, 255), Color.FromArgb(255, 242, 245, 251), Color.FromArgb(255, 180, 189, 207),
        Color.FromArgb(255, 205, 213, 228), Color.FromArgb(32, 255, 255, 255),
        Color.FromArgb(255, 255, 199, 129), Color.FromArgb(255, 255, 112, 104), Color.FromArgb(255, 108, 192, 255),
        Color.FromArgb(255, 36, 20, 4), Color.FromArgb(255, 44, 10, 8),
        16, "cam", "koyu", Color.FromArgb(255, 22, 26, 36), Color.FromArgb(255, 108, 192, 255));

    // ---- 2. terminal (koyu yeşil ve kehribar net konsol) ----
    private static readonly Palette TerminalLight = new(
        true,
        Color.FromArgb(255, 251, 251, 248), Color.FromArgb(255, 19, 19, 19), Color.FromArgb(255, 19, 19, 19), Color.FromArgb(255, 88, 88, 79),
        Color.FromArgb(255, 19, 19, 19), Color.FromArgb(28, 19, 19, 19),
        Color.FromArgb(255, 180, 112, 6), Color.FromArgb(255, 160, 64, 56), Color.FromArgb(255, 5, 119, 59),
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255),
        5, "terminal", "acik", Color.FromArgb(255, 251, 251, 248), Color.FromArgb(255, 5, 119, 59));

    private static readonly Palette TerminalDark = new(
        false,
        Color.FromArgb(255, 14, 15, 16), Color.FromArgb(255, 109, 108, 102), Color.FromArgb(255, 233, 232, 225), Color.FromArgb(255, 156, 155, 147),
        Color.FromArgb(255, 233, 232, 225), Color.FromArgb(36, 233, 232, 225),
        Color.FromArgb(255, 208, 144, 51), Color.FromArgb(255, 228, 124, 114), Color.FromArgb(255, 88, 181, 117),
        Color.FromArgb(255, 14, 15, 16), Color.FromArgb(255, 14, 15, 16),
        10, "terminal", "koyu", Color.FromArgb(255, 14, 15, 16), Color.FromArgb(255, 88, 181, 117));

    // ---- 3. mat (yumuşak taş gri) ----
    private static readonly Palette MatLight = new(
        true,
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 214, 217, 224), Color.FromArgb(255, 22, 24, 29), Color.FromArgb(255, 80, 85, 98),
        Color.FromArgb(255, 56, 60, 72), Color.FromArgb(24, 22, 24, 29),
        Color.FromArgb(255, 190, 120, 16), Color.FromArgb(255, 156, 68, 60), Color.FromArgb(255, 75, 79, 201),
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255),
        6, "mat", "acik", Color.FromArgb(255, 244, 245, 247), Color.FromArgb(255, 75, 79, 201));

    private static readonly Palette MatDark = new(
        false,
        Color.FromArgb(255, 30, 31, 36), Color.FromArgb(255, 58, 61, 72), Color.FromArgb(255, 236, 236, 241), Color.FromArgb(255, 163, 167, 179),
        Color.FromArgb(255, 200, 204, 214), Color.FromArgb(32, 255, 255, 255),
        Color.FromArgb(255, 204, 145, 64), Color.FromArgb(255, 223, 128, 117), Color.FromArgb(255, 158, 164, 245),
        Color.FromArgb(255, 22, 23, 26), Color.FromArgb(255, 22, 23, 26),
        14, "mat", "koyu", Color.FromArgb(255, 22, 23, 26), Color.FromArgb(255, 158, 164, 245));

    // ---- 4. ton (dingin çivit ve teal katmanlar) ----
    private static readonly Palette TonLight = new(
        true,
        Color.FromArgb(255, 220, 234, 246), Color.FromArgb(44, 26, 40, 80), Color.FromArgb(255, 17, 26, 46), Color.FromArgb(255, 53, 70, 104),
        Color.FromArgb(255, 36, 52, 92), Color.FromArgb(24, 26, 40, 80),
        Color.FromArgb(255, 184, 110, 8), Color.FromArgb(255, 153, 39, 36), Color.FromArgb(255, 45, 70, 185),
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255),
        8, "ton", "acik", Color.FromArgb(255, 238, 243, 250), Color.FromArgb(255, 45, 70, 185));

    private static readonly Palette TonDark = new(
        false,
        Color.FromArgb(255, 28, 37, 65), Color.FromArgb(56, 184, 204, 255), Color.FromArgb(255, 230, 238, 255), Color.FromArgb(255, 170, 184, 218),
        Color.FromArgb(255, 200, 212, 242), Color.FromArgb(32, 230, 238, 255),
        Color.FromArgb(255, 229, 154, 35), Color.FromArgb(255, 250, 129, 117), Color.FromArgb(255, 123, 156, 255),
        Color.FromArgb(255, 17, 23, 43), Color.FromArgb(255, 17, 23, 43),
        16, "ton", "koyu", Color.FromArgb(255, 17, 23, 43), Color.FromArgb(255, 123, 156, 255));

    // ---- 5. duz (yüksek kontrast net siyah-beyaz) ----
    private static readonly Palette DuzLight = new(
        true,
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 10, 10, 10), Color.FromArgb(255, 10, 10, 10), Color.FromArgb(255, 70, 70, 63),
        Color.FromArgb(255, 10, 10, 10), Color.FromArgb(32, 10, 10, 10),
        Color.FromArgb(255, 198, 244, 50), Color.FromArgb(255, 183, 25, 28), Color.FromArgb(255, 10, 10, 10),
        Color.FromArgb(255, 10, 10, 10), Color.FromArgb(255, 255, 255, 255),
        0, "duz", "acik", Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 10, 10, 10));

    private static readonly Palette DuzDark = new(
        false,
        Color.FromArgb(255, 11, 11, 11), Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 185, 185, 177),
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(38, 255, 255, 255),
        Color.FromArgb(255, 198, 244, 50), Color.FromArgb(255, 255, 101, 90), Color.FromArgb(255, 198, 244, 50),
        Color.FromArgb(255, 10, 10, 10), Color.FromArgb(255, 10, 10, 10),
        0, "duz", "koyu", Color.FromArgb(255, 11, 11, 11), Color.FromArgb(255, 198, 244, 50));

    // ---- 6. sicak (krem ve toprak sıcak ton) ----
    private static readonly Palette SicakLight = new(
        true,
        Color.FromArgb(255, 255, 253, 248), Color.FromArgb(255, 220, 207, 185), Color.FromArgb(255, 36, 31, 26), Color.FromArgb(255, 95, 85, 74),
        Color.FromArgb(255, 72, 62, 52), Color.FromArgb(24, 60, 40, 10),
        Color.FromArgb(255, 180, 112, 12), Color.FromArgb(255, 156, 68, 60), Color.FromArgb(255, 138, 47, 99),
        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255),
        7, "sicak", "acik", Color.FromArgb(255, 246, 242, 234), Color.FromArgb(255, 138, 47, 99));

    private static readonly Palette SicakDark = new(
        false,
        Color.FromArgb(255, 38, 35, 32), Color.FromArgb(255, 74, 66, 55), Color.FromArgb(255, 243, 237, 226), Color.FromArgb(255, 189, 179, 163),
        Color.FromArgb(255, 222, 212, 198), Color.FromArgb(32, 243, 237, 226),
        Color.FromArgb(255, 204, 145, 64), Color.FromArgb(255, 223, 128, 117), Color.FromArgb(255, 233, 163, 201),
        Color.FromArgb(255, 29, 26, 23), Color.FromArgb(255, 29, 26, 23),
        15, "sicak", "koyu", Color.FromArgb(255, 29, 26, 23), Color.FromArgb(255, 233, 163, 201));

    /// <summary>Girdiyi 6 kanonik tema ailesinden birine eşler (murekkep -> terminal, kagit -> sicak). Tanınmazsa null döner.</summary>
    public static string? NormalizeFamilyOrNull(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;
        var s = family.Trim().ToLowerInvariant()
            .Replace('ü', 'u').Replace('â', 'a').Replace('ı', 'i')
            .Replace('ö', 'o').Replace('ç', 'c').Replace('ş', 's').Replace('ğ', 'g');
        return s switch
        {
            "cam" => "cam",
            "terminal" or "murekkep" => "terminal",
            "mat" => "mat",
            "ton" => "ton",
            "duz" => "duz",
            "sicak" or "kagit" => "sicak",
            _ => null,
        };
    }

    public static string NormalizeFamily(string? family) => NormalizeFamilyOrNull(family) ?? "cam";

    /// <summary>Kip eşlemesi: "light"/"acik", "dark"/"koyu", "system"/"sistem".</summary>
    public static string NormalizeModeSetting(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return "system";
        var s = mode.Trim().ToLowerInvariant()
            .Replace('ç', 'c').Replace('ı', 'i').Replace('ş', 's');
        return s switch
        {
            "light" or "acik" => "light",
            "dark" or "koyu" => "dark",
            _ => "system",
        };
    }

    /// <summary>Aile ve açık/koyu durumuna göre doğrudan palet döner (kayıt defterinden bağımsız, saf).</summary>
    public static Palette ForExplicit(string? family, bool light, string? rawMode = null)
    {
        var fam = NormalizeFamily(family);
        var p = (fam, light) switch
        {
            ("terminal", true) => TerminalLight,
            ("terminal", false) => TerminalDark,
            ("mat", true) => MatLight,
            ("mat", false) => MatDark,
            ("ton", true) => TonLight,
            ("ton", false) => TonDark,
            ("duz", true) => DuzLight,
            ("duz", false) => DuzDark,
            ("sicak", true) => SicakLight,
            ("sicak", false) => SicakDark,
            (_, true) => LightTheme,
            _ => DarkTheme,
        };
        if (rawMode != null)
        {
            var m = NormalizeModeSetting(rawMode) switch { "light" => "acik", "dark" => "koyu", _ => "sistem" };
            return p with { Mode = m };
        }
        return p;
    }

    /// <summary>"system" | "light" | "dark" (ve isteğe bağlı tema ailesi ya da "aile:kip" birleşimi).</summary>
    public static Palette For(string? themeSetting, string? familySetting = null)
    {
        string? fam = familySetting;
        string? mode = themeSetting;
        if (!string.IsNullOrWhiteSpace(themeSetting))
        {
            var raw = themeSetting.Trim();
            var sep = raw.IndexOfAny(new[] { ':', '/', '\t' });
            if (sep > 0)
            {
                fam ??= raw[..sep];
                mode = raw[(sep + 1)..];
            }
            else if (NormalizeFamilyOrNull(raw) is { } onlyFam && string.IsNullOrWhiteSpace(familySetting))
            {
                fam = onlyFam;
                mode = "system";
            }
        }
        var normMode = NormalizeModeSetting(mode);
        var light = normMode switch
        {
            "light" => true,
            "dark" => false,
            _ => SystemIsLight(),
        };
        return ForExplicit(fam, light, normMode);
    }

    public static bool SystemIsLight()
    {
#if WINDOWS7_0_OR_GREATER || WINDOWS
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch { return true; }
#else
        return true;
#endif
    }
}
