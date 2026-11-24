using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Soz.Shell;

/// <summary>Piksel başına alfalı çizim yüzeyi: DIB bölümü + premultiplied bitmap + Graphics.</summary>
internal sealed class LayeredSurface : IDisposable
{
    private readonly IntPtr _memDc;
    private readonly IntPtr _hBitmap;
    private readonly IntPtr _oldObj;
    public Bitmap Bitmap { get; }
    public Graphics Graphics { get; }
    public int Width { get; }
    public int Height { get; }

    public LayeredSurface(int w, int h)
    {
        Width = Math.Max(1, w); Height = Math.Max(1, h);
        var bi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = Width, biHeight = -Height,
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            },
        };
        _hBitmap = Native.CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
        if (_hBitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("DIB oluşturulamadı");
        _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        _oldObj = Native.SelectObject(_memDc, _hBitmap);
        Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits);
        Graphics = Graphics.FromImage(Bitmap);
    }

    public bool Present(IntPtr hwnd, int x, int y)
    {
        var dst = new Native.POINT(x, y);
        var size = new Native.SIZE(Width, Height);
        var src = new Native.POINT(0, 0);
        var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
        var screen = Native.GetDC(IntPtr.Zero);
        try { return Native.UpdateLayeredWindow(hwnd, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA); }
        finally { Native.ReleaseDC(IntPtr.Zero, screen); }
    }

    public void Dispose()
    {
        Graphics.Dispose();
        Bitmap.Dispose();
        Native.SelectObject(_memDc, _oldObj);
        Native.DeleteObject(_hBitmap);
        Native.DeleteDC(_memDc);
    }
}

/// <summary>
/// ODAK ALMAYAN, her zaman üstte, görev çubuğunda görünmeyen katmanlı pencere. Kapsül ve ipucu bunun üzerine kurulu.
/// WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW + WS_EX_TOPMOST + WS_EX_LAYERED, ShowWithoutActivation, WM_MOUSEACTIVATE → MA_NOACTIVATE.
/// </summary>
internal class LayeredForm : Form
{
    private const int WM_DPICHANGED = 0x02E0;
    private LayeredSurface? _surface;

    protected int PosX { get; private set; }
    protected int PosY { get; private set; }
    protected int PxWidth { get; private set; }
    protected int PxHeight { get; private set; }

    protected float DpiScale
    {
        get
        {
            if (!IsHandleCreated) return 1f;
            var d = Native.GetDpiForWindow(Handle);
            return d == 0 ? 1f : d / 96f;
        }
    }

    public LayeredForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = Native.WS_POPUP;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;
            case WM_DPICHANGED:
                m.Result = IntPtr.Zero;
                BeginInvoke(new Action(OnScaleChanged));
                return;
        }
        base.WndProc(ref m);
    }

    protected virtual void OnScaleChanged() { }

    /// <summary>Yüzeyi gösterir/günceller. Pencere gizliyse odak almadan gösterir.</summary>
    protected void Present(int x, int y, int w, int h, Action<Graphics> draw)
    {
        if (!IsHandleCreated) _ = Handle; // CreateControl görünmez formda tutacak oluşturmaz
        if (_surface == null || _surface.Width != w || _surface.Height != h)
        {
            _surface?.Dispose();
            _surface = new LayeredSurface(w, h);
        }
        draw(_surface.Graphics);
        PosX = x; PosY = y; PxWidth = w; PxHeight = h;
        if (Left != x || Top != y || Width != w || Height != h) SetBounds(x, y, w, h); // WinForms önbelleğini gerçek konumla eşle
        _surface.Present(Handle, x, y);
    }

    protected void RepresentAt(int x, int y)
    {
        if (_surface == null) return;
        PosX = x; PosY = y;
        _surface.Present(Handle, x, y);
    }

    /// <summary>Odak almadan görünür yapar.</summary>
    public void ShowNoActivate()
    {
        if (!IsHandleCreated) _ = Handle; // CreateControl görünmez formda tutacak oluşturmaz
        if (!Visible) Show(); // ShowWithoutActivation = true → SW_SHOWNOACTIVATE
        // Show() WinForms önbelleğindeki eski sınırları uygulayabilir: gerçek konum/boyutu yeniden ver, yüzeyi yeniden sun.
        if (PxWidth > 0)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, PosX, PosY, PxWidth, PxHeight, Native.SWP_NOACTIVATE);
            _surface?.Present(Handle, PosX, PosY);
        }
        else Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void HideWindow()
    {
        if (IsHandleCreated && Visible) Hide();
    }

    public bool IsShown => IsHandleCreated && Visible;

    public Rectangle NativeBounds
    {
        get
        {
            if (!IsHandleCreated || !Native.GetWindowRect(Handle, out var r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _surface?.Dispose(); _surface = null; }
        base.Dispose(disposing);
    }
}
