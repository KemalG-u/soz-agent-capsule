using System.Windows.Forms;

namespace Soz.Shell;

internal readonly record struct HotkeySpec(uint Modifiers, uint VirtualKey, string Display);

/// <summary>
/// Yalnız RegisterHotKey ile bir birleşim (en az bir değiştirici + bir ana tuş). Caps Lock ve tek tuş reddedilir;
/// hiçbir klavye kancası (hook) kullanılmaz.
/// </summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private readonly int _id;
    private bool _registered;

    public event Action? Pressed;
    public bool IsRegistered => _registered;
    public string? FailureReason { get; private set; }
    public string Display { get; private set; } = "";

    public HotkeyManager(int id = 0x504B, string windowName = "SozHotkeyWindow")
    {
        _id = id;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */, Caption = windowName });
    }

    public static bool TryParse(string? text, out HotkeySpec spec, out string error)
    {
        spec = default; error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "boş"; return false; }
        uint mods = 0; Keys? main = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": case "ctl": mods |= Native.MOD_CONTROL; continue;
                case "alt": mods |= Native.MOD_ALT; continue;
                case "shift": mods |= Native.MOD_SHIFT; continue;
                case "win": case "windows": mods |= Native.MOD_WIN; continue;
            }
            if (main != null) { error = "birden çok ana tuş"; return false; }
            if (raw.Length == 1 && char.IsDigit(raw[0])) main = (Keys)((int)Keys.D0 + (raw[0] - '0'));
            else if (Enum.TryParse<Keys>(raw, true, out var k) && Enum.IsDefined(k)) main = k;
            else { error = "tanınmayan tuş: " + raw; return false; }
        }
        if (main == null) { error = "ana tuş yok"; return false; }
        if (mods == 0) { error = "değiştirici (Ctrl/Alt/Shift/Win) şart. Tek tuş yasak"; return false; }
        if (main is Keys.CapsLock or Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin or Keys.None)
        { error = "bu tuş ana tuş olamaz"; return false; }

        var parts = new List<string>();
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
        var keyName = main.Value is >= Keys.D0 and <= Keys.D9 ? ((int)main.Value - (int)Keys.D0).ToString() : main.Value.ToString();
        parts.Add(keyName);
        spec = new HotkeySpec(mods, (uint)main.Value, string.Join("+", parts));
        return true;
    }

    public bool Register(string? text)
    {
        Unregister();
        if (!TryParse(text, out var spec, out var err)) { FailureReason = err; Display = text ?? ""; return false; }
        Display = spec.Display;
        if (Native.RegisterHotKey(Handle, _id, spec.Modifiers | Native.MOD_NOREPEAT, spec.VirtualKey))
        {
            _registered = true; FailureReason = null; return true;
        }
        FailureReason = "başka bir program bu birleşimi almış (Win32 hata " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")";
        return false;
    }

    public void Unregister()
    {
        if (_registered) { Native.UnregisterHotKey(Handle, _id); _registered = false; }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == _id)
        {
            try { Pressed?.Invoke(); } catch (Exception ex) { Log.Error("kısayol işleyici", ex); }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
