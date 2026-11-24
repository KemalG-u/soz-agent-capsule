using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

namespace Soz.Capsule;

/// <summary>
/// Yazı bit eşlemini Composition yüzeyine koyar. Yalnız WARP (yazılım) Direct3D 11 aygıtı açılır: donanım aygıtı ekran kartını uyandırır ve
/// belleği büyütür. Aygıt tembel açılır (ilk yazıda), uzun süre yazı yoksa bırakılır. Yüzey başına BeginDraw, UpdateSubresource, EndDraw.
/// </summary>
internal sealed unsafe class YaziYuzeyi : IDisposable
{
    private static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid IID_ISurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid IID_Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly Compositor _comp;
    private IntPtr _dev, _ctx;
    private CompositionGraphicsDevice? _gd;
    public string? Hata { get; private set; }
    public bool Hazir => _gd != null;

    private YaziYuzeyi(Compositor c) { _comp = c; }

    public static YaziYuzeyi Olustur(Compositor c)
    {
        var y = new YaziYuzeyi(c);
        try { y.Ac(); }
        catch (Exception e) { y.Hata = e.GetType().Name + ": " + e.Message; y.Kapat(); }
        return y;
    }

    private void Ac()
    {
        // D3D_DRIVER_TYPE_WARP = 5, D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20, D3D11_SDK_VERSION = 7
        var hr = N.D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out _dev, out _, out _ctx);
        Marshal.ThrowExceptionForHR(hr);
        var compPtr = MarshalInspectable<Compositor>.FromManaged(_comp);
        try
        {
            var iid = IID_ICompositorInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compPtr, in iid, out var interop));
            try
            {
                var vt = *(IntPtr**)interop;
                IntPtr res;
                // ICompositorInterop: CreateCompositionSurfaceForHandle(3), ForSwapChain(4), CreateGraphicsDevice(5)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vt[5])(interop, _dev, &res));
                _gd = MarshalInspectable<CompositionGraphicsDevice>.FromAbi(res);
            }
            finally { Marshal.Release(interop); }
        }
        finally { Marshal.Release(compPtr); }
    }

    /// <summary>Bit eşlemden yüzey fırçası. Boyut 1:1, doğrusal olmayan örnekleme (keskin).</summary>
    public CompositionSurfaceBrush? Firca(YaziBitmap b)
    {
        if (_gd == null) return null;
        try
        {
            var surf = _gd.CreateDrawingSurface(new Windows.Foundation.Size(b.W, b.H), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            var sp = MarshalInspectable<CompositionDrawingSurface>.FromManaged(surf);
            try
            {
                var iid = IID_ISurfaceInterop;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sp, in iid, out var si));
                try
                {
                    var vt = *(IntPtr**)si;
                    var tid = IID_Texture2D;
                    IntPtr tex; N.POINT off;
                    // ICompositionDrawingSurfaceInterop: BeginDraw(3), EndDraw(4), Resize(5), Scroll(6), ResumeDraw(7), SuspendDraw(8)
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, N.POINT*, int>)vt[3])(si, IntPtr.Zero, &tid, &tex, &off));
                    try
                    {
                        var box = new Box { Left = (uint)off.X, Top = (uint)off.Y, Front = 0, Right = (uint)(off.X + b.W), Bottom = (uint)(off.Y + b.H), Back = 1 };
                        var cvt = *(IntPtr**)_ctx;
                        fixed (byte* px = b.Piksel)
                            // ID3D11DeviceContext::UpdateSubresource (48)
                            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, Box*, byte*, uint, uint, void>)cvt[48])(_ctx, tex, 0, &box, px, (uint)(b.W * 4), 0);
                    }
                    finally
                    {
                        Marshal.Release(tex);
                        ((delegate* unmanaged[Stdcall]<IntPtr, int>)vt[4])(si);
                    }
                }
                finally { Marshal.Release(si); }
            }
            finally { Marshal.Release(sp); }
            var fr = _comp.CreateSurfaceBrush(surf);
            fr.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
            fr.Stretch = CompositionStretch.None;
            fr.HorizontalAlignmentRatio = 0; fr.VerticalAlignmentRatio = 0;
            return fr;
        }
        catch (Exception e) { Hata = e.GetType().Name + ": " + e.Message; return null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    private void Kapat()
    {
        _gd?.Dispose(); _gd = null;
        if (_ctx != IntPtr.Zero) { Marshal.Release(_ctx); _ctx = IntPtr.Zero; }
        if (_dev != IntPtr.Zero) { Marshal.Release(_dev); _dev = IntPtr.Zero; }
    }

    public void Dispose() => Kapat();
}
