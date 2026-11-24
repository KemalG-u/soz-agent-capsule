using System.Runtime.InteropServices;

namespace Soz.Capsule;

/// <summary>Kapsülün kullandığı Win32 çağrıları. Yalnız gerekenler, WinForms yok.</summary>
internal static unsafe class N
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint WM_CREATE = 1, WM_DESTROY = 2, WM_SIZE = 5, WM_CLOSE = 0x10, WM_ERASEBKGND = 0x14, WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21,
        WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_TIMER = 0x113, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201,
        WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MOUSEWHEEL = 0x20A, WM_MOUSELEAVE = 0x2A3,
        WM_CAPTURECHANGED = 0x215, WM_DPICHANGED = 0x2E0, WM_DISPLAYCHANGE = 0x7E, WM_SETTINGCHANGE = 0x1A, WM_HOTKEY = 0x312, WM_GETOBJECT = 0x3D,
        WM_KILLFOCUS = 0x8, WM_SETFOCUS = 0x7, WM_ACTIVATE = 0x6, WM_THEMECHANGED = 0x31A, WM_DWMCOMPOSITIONCHANGED = 0x31E, WM_APP = 0x8000,
        WM_NCACTIVATE = 0x86, WM_IME_CHAR = 0x286, WM_UNICHAR = 0x109, WM_PAINT = 0xF, WM_LBUTTONDBLCLK = 0x203;
    public const int HTCLIENT = 1, HTTRANSPARENT = -1, MA_NOACTIVATE = 3, MA_ACTIVATE = 1;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    /// <summary>DwmSetWindowAttribute: pencereyi Aero Peek (görev çubuğu önizlemesi, "masaüstünü göster") dışında tut; yoksa peek sırasında kapsül gizlenir.</summary>
    public const int DWMWA_EXCLUDED_FROM_PEEK = 12;
    public const int GWL_EXSTYLE = -20;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LBUTTON = 1, VK_RBUTTON = 2, VK_ESCAPE = 0x1B, VK_RETURN = 0x0D, VK_BACK = 8,
        VK_DELETE = 0x2E, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_HOME = 0x24, VK_END = 0x23, VK_TAB = 9, VK_APPS = 0x5D, VK_F10 = 0x79, VK_UP = 0x26, VK_DOWN = 0x28, VK_SPACE = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
    [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb, PageFaultCount; public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize; public ulong SharedCommitUsage;
    }
    [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr h, IntPtr id);
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr c);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfoW(uint a, uint b, out RECT r, uint c);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfoW(uint a, uint b, out int v, uint c);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFOEXW mi);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMon, int type, out uint x, out uint y);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, char* buf, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string s);
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern uint GetSysColor(int idx);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? n);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool QueryPerformanceCounter(out long v);
    [DllImport("psapi.dll")] public static extern bool GetProcessMemoryInfo(IntPtr h, ref PROCESS_MEMORY_COUNTERS_EX2 c, uint cb);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] public static extern int RegGetValueW(IntPtr hkey, string sub, string val, uint flags, out uint type, out uint data, ref uint cb);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] public static extern int RegGetValueW(IntPtr hkey, string sub, string val, uint flags, out uint type, byte* data, ref uint cb);
    public static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    // gdi32: yazıyı küçük bit eşleme olarak çizmek için
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateFontW(int h, int w, int esc, int ori, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool GetTextExtentPoint32W(IntPtr dc, string s, int n, out SIZE size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool TextOutW(IntPtr dc, int x, int y, string s, int n);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, void* bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);

    // UI Automation sağlayıcı
    [DllImport("uiautomationcore.dll")] public static extern IntPtr UiaReturnRawElementProvider(IntPtr hwnd, IntPtr wp, IntPtr lp, [MarshalAs(UnmanagedType.Interface)] IRawElementProviderSimple? el);
    [DllImport("uiautomationcore.dll")] public static extern int UiaHostProviderFromHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] out object provider);
    [DllImport("uiautomationcore.dll")] public static extern int UiaDisconnectProvider([MarshalAs(UnmanagedType.IUnknown)] object provider);

    // Direct3D 11 (yazı yüzeyi için yalnız WARP aygıtı)
    [DllImport("d3d11.dll")] public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint nLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);

    public static RECT CalismaAlani() { SystemParametersInfoW(0x30, 0, out RECT r, 0); return r; }

    public static int X(IntPtr lp) => (short)((long)lp & 0xFFFF);
    public static int Y(IntPtr lp) => (short)(((long)lp >> 16) & 0xFFFF);

    /// <summary>Süreç özel çalışma kümesi (Görev Yöneticisi "Bellek" sütunu), MB.</summary>
    public static (double OzelCalismaMB, double TaahhutMB, double ToplamCalismaMB) Bellek()
    {
        var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX2) };
        if (!GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.cb)) return (-1, -1, -1);
        return (c.PrivateWorkingSetSize / 1048576.0, c.PrivateUsage / 1048576.0, c.WorkingSetSize / 1048576.0);
    }
}
