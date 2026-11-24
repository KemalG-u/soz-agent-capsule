using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace Soz.Shell;

/// <summary>Yüklü bir klavye düzeni. <see cref="Handle"/> yalnız gerçek ölçümde anlamlıdır (sınamada sahte düzen verilir).</summary>
internal sealed record LayoutDescriptor(IntPtr Handle, string Hkl, string Klid, string Name, string Culture);

/// <summary>Klavye düzeni ölçümünü soyutlar: gerçek (Win32) ve sınama (sahte düzen) uygulamaları var.</summary>
internal interface ILayoutProbe
{
    IReadOnlyList<LayoutDescriptor> Layouts { get; }

    /// <summary>
    /// Ctrl+Alt (ve isteğe bağlı Shift) basılıyken <paramref name="vk"/> tuşunun o düzende ürettiği karakter:
    /// 0 = karakter yok, &gt;0 = kod noktası (ilk UTF-16 birimi), &lt;0 = ölü tuş.
    /// </summary>
    int CtrlAltChar(LayoutDescriptor layout, uint vk, bool shift);
}

internal sealed record LayoutHit(LayoutDescriptor Layout, int CodePoint, bool ProducesText);

/// <summary>Bir kısayol isteğinin AltGr ölçümü (kayıt öncesi).</summary>
internal sealed record HotkeyMeasure(string Requested, string Display, bool Parsed, bool HasCtrlAlt, IReadOnlyList<LayoutHit> Hits)
{
    public bool Conflict => HasCtrlAlt && Hits.Any(h => h.ProducesText);

    public string? ConflictReason => !Conflict ? null :
        "AltGr çakışması: Ctrl+Alt bu düzenlerde karakter üretiyor: " +
        string.Join(", ", Hits.Where(h => h.ProducesText).Select(h => $"{h.Layout.Name} [{h.Layout.Klid}] (" + CodePointText(h.CodePoint) + ")"));

    public static string CodePointText(int cp) => cp < 0 ? "ölü tuş" : "U+" + cp.ToString("X4");
}

/// <summary>Kısayol kayıt kararının sonucu. <see cref="Reason"/>: istenen birleşimden neden vazgeçildi; <see cref="FailureReason"/>: son kayıt da olmadıysa neden.</summary>
internal sealed record HotkeyResolution(string Requested, string Display, bool Registered, bool FellBack, string? Reason, string? FailureReason);

/// <summary>Saf mantık: AltGr çakışması ölçümü ve düşme (fallback) kararı. Win32'ye dokunmaz; sınama sahte probe verir.</summary>
internal static class HotkeyPolicy
{
    public const string FallbackSpec = "Ctrl+Shift+Space";
    public const string NewJobDefault = "Ctrl+Alt+J";

    public static bool HasCtrlAlt(HotkeySpec s) => (s.Modifiers & (Native.MOD_CONTROL | Native.MOD_ALT)) == (Native.MOD_CONTROL | Native.MOD_ALT);

    /// <summary>Kod noktası yazılabilir bir karakter mi? Denetim karakterleri (Ctrl+harf kodu) kullanıcının yazdığı metin değildir; ölü tuş yazmayı etkiler.</summary>
    public static bool ProducesText(int cp) => cp != 0 && (cp < 0 || (cp >= 0x20 && cp != 0x7F));

    public static HotkeyMeasure Measure(string? text, ILayoutProbe probe)
    {
        if (!HotkeyManager.TryParse(text, out var spec, out _)) return new HotkeyMeasure(text ?? "", text ?? "", false, false, []);
        var hasCa = HasCtrlAlt(spec);
        var hits = new List<LayoutHit>();
        if (hasCa)
        {
            var shift = (spec.Modifiers & Native.MOD_SHIFT) != 0;
            foreach (var l in probe.Layouts)
            {
                var cp = probe.CtrlAltChar(l, spec.VirtualKey, shift);
                hits.Add(new LayoutHit(l, cp, ProducesText(cp)));
            }
        }
        return new HotkeyMeasure(text ?? "", spec.Display, true, hasCa, hits);
    }

    /// <summary>
    /// İstenen birleşimi dener: Ctrl+Alt içerip yüklü herhangi bir düzende karakter üretiyorsa ya da kayıt başarısızsa
    /// <see cref="FallbackSpec"/>'e düşer. <paramref name="register"/>: birleşim metnini kaydeder; null = başarılı, aksi halde hata metni.
    /// </summary>
    public static HotkeyResolution Resolve(string? requestedText, string defaultSpec, ILayoutProbe probe, Func<string, string?> register)
    {
        var requested = string.IsNullOrWhiteSpace(requestedText) ? defaultSpec : requestedText.Trim();
        string reason;
        if (!HotkeyManager.TryParse(requested, out var spec, out var parseError)) reason = "geçersiz birleşim: " + parseError;
        else
        {
            var m = Measure(requested, probe);
            if (m.ConflictReason is { } c) reason = c;
            else
            {
                var err = register(spec.Display);
                if (err == null) return new HotkeyResolution(requested, spec.Display, true, false, null, null);
                reason = "RegisterHotKey başarısız: " + err;
                if (string.Equals(spec.Display, FallbackSpec, StringComparison.OrdinalIgnoreCase)) // zaten yedek birleşim; tekrar denemenin anlamı yok
                    return new HotkeyResolution(requested, spec.Display, false, false, reason, err);
            }
        }
        HotkeyManager.TryParse(FallbackSpec, out var fb, out _);
        var ferr = register(fb.Display);
        return new HotkeyResolution(requested, fb.Display, ferr == null, true, reason, ferr);
    }
}

/// <summary>Gerçek ölçüm: GetKeyboardLayoutList + MapVirtualKeyEx + ToUnicodeEx (durum değiştirmeyen bayrakla; klavye durumuna dokunmaz).</summary>
internal sealed class WinLayoutProbe : ILayoutProbe
{
    private IReadOnlyList<LayoutDescriptor>? _layouts;

    public IReadOnlyList<LayoutDescriptor> Layouts => _layouts ??= Enumerate();

    private static IReadOnlyList<LayoutDescriptor> Enumerate()
    {
        var list = new List<LayoutDescriptor>();
        try
        {
            var n = Native.GetKeyboardLayoutList(0, null);
            if (n <= 0) return list;
            var arr = new IntPtr[n];
            n = Native.GetKeyboardLayoutList(n, arr);
            for (var i = 0; i < n; i++)
            {
                var (klid, name, culture) = Describe(arr[i]);
                list.Add(new LayoutDescriptor(arr[i], "0x" + ((long)arr[i] & 0xFFFFFFFF).ToString("X8"), klid, name, culture));
            }
        }
        catch (Exception ex) { Log.Error("klavye düzeni listesi", ex); }
        return list;
    }

    public int CtrlAltChar(LayoutDescriptor layout, uint vk, bool shift)
    {
        try
        {
            var sc = Native.MapVirtualKeyEx(vk, Native.MAPVK_VK_TO_VSC, layout.Handle);
            var state = new byte[256];
            state[0x11] = 0x80; state[0xA2] = 0x80; // VK_CONTROL, VK_LCONTROL
            state[0x12] = 0x80; state[0xA5] = 0x80; // VK_MENU, VK_RMENU (AltGr = LCtrl + RAlt)
            if (shift) { state[0x10] = 0x80; state[0xA0] = 0x80; }
            var sb = new StringBuilder(8);
            var n = Native.ToUnicodeEx(vk, sc, state, sb, 8, Native.TOUNICODE_NO_STATE_CHANGE, layout.Handle);
            if (n < 0) return -1;
            if (n == 0 || sb.Length == 0) return 0;
            return sb[0];
        }
        catch (Exception ex) { Log.Error("ToUnicodeEx", ex); return 0; }
    }

    private static (string Klid, string Name, string Culture) Describe(IntPtr hkl)
    {
        var v = (long)hkl & 0xFFFFFFFF;
        var lo = (int)(v & 0xFFFF);
        var hi = (int)((v >> 16) & 0xFFFF);
        string culture;
        try { culture = new CultureInfo(lo).Name; } catch { culture = "0x" + lo.ToString("X4"); }
        string klid;
        const string Root = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";
        if (hi == lo) klid = lo.ToString("X8");
        else if ((hi & 0xF000) == 0xF000)
        {
            klid = v.ToString("X8");
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(Root);
                foreach (var sub in root?.GetSubKeyNames() ?? [])
                {
                    if (!sub.EndsWith(lo.ToString("X4"), StringComparison.OrdinalIgnoreCase)) continue;
                    using var k = root!.OpenSubKey(sub);
                    if (k?.GetValue("Layout Id") is string id && int.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lid) && lid == (hi & 0x0FFF))
                    { klid = sub; break; }
                }
            }
            catch { /* kayıt defteri okunamazsa ham değer kalır */ }
        }
        else klid = hi.ToString("X4") + lo.ToString("X4");

        var name = culture;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(Root + "\\" + klid);
            if (k?.GetValue("Layout Text") is string t && t.Length > 0) name = t;
        }
        catch { }
        return (klid, name, culture);
    }
}
