using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Soz.Capsule;

/// <summary>Açılır yüzeyde bir satır: ön bakış listesi ve sağ tık panosu aynı yüzeyi kullanır.</summary>
internal sealed record AcilirSatir(string Kimlik, string Metin, string? Sag = null, string? Glif = null, DurumSinifi Sinif = DurumSinifi.Yok,
    bool Baslik = false, bool Ayirici = false, bool Soluk = false, bool Isaretli = false, bool Tiklanir = true);

/// <summary>
/// Küçük, odak almayan yerel yüzey (P3 ön bakış, P2 sağ tık panosu). Kapsülle aynı Compositor ve yazı yolunu kullanır; WebView, WinForms yok.
/// Kapsülden doğar (ölçek 0,96 → 1 ve opaklık yayı), kapanış 120 ms. Tembel: gerektiğinde açılır, kapanınca yok olur.
/// </summary>
internal sealed unsafe class Acilir : IDisposable
{
    private static readonly N.WndProc Proc = StaticProc;
    private static readonly Dictionary<IntPtr, Acilir> Pencereler = new();
    private static bool _sinifKayitli;
    private const double Pay = 24, SatirY = 36, BaslikY = 30, AyiriciY = 10, Ic = 8, Radius = 12;

    private readonly Gorsel _g;
    private readonly TemaKip _t;
    private readonly double _olcek;
    private readonly IReadOnlyList<AcilirSatir> _satirlar;
    private readonly Action<string> _tik;
    private readonly Action<bool> _ustunde;
    private IntPtr _hwnd;
    private DesktopWindowTarget? _target;
    private ContainerVisual _root = null!;
    private ShapeVisual _vurgu = null!;
    private readonly List<(double Y, double H, AcilirSatir S)> _yerler = new();
    private int _secili = -1;
    private bool _izliyor, _kapaniyor;
    public bool Acik => _hwnd != IntPtr.Zero && !_kapaniyor;
    public IntPtr Hwnd => _hwnd;
    public Dik GovdePx { get; private set; }

    public Acilir(Gorsel g, TemaKip t, double olcek, IReadOnlyList<AcilirSatir> satirlar, double genislikDp, Dik kapsulPx, Dik calismaPx, bool azalt, Action<string> tik, Action<bool> ustunde)
    {
        _g = g; _t = t; _olcek = olcek; _satirlar = satirlar; _tik = tik; _ustunde = ustunde;
        var gen = genislikDp;
        double h = Ic * 2;
        foreach (var s in satirlar) { var sy = s.Ayirici ? AyiriciY : s.Baslik ? BaslikY : SatirY; _yerler.Add((h, sy, s)); h += sy; }
        var govW = gen * olcek; var govH = h * olcek;
        var pencW = (int)Math.Round(govW + 2 * Pay * olcek); var pencH = (int)Math.Round(govH + 2 * Pay * olcek);
        var (x, y, ox, oy) = KonumHesap.PopupYerle(kapsulPx, govW, govH, calismaPx, 10 * olcek, 8 * olcek);
        GovdePx = new Dik(x, y, x + govW, y + govH);
        var wx = (int)Math.Round(x - Pay * olcek); var wy = (int)Math.Round(y - Pay * olcek);

        var hInst = N.GetModuleHandleW(null);
        if (!_sinifKayitli)
        {
            var wc = new N.WNDCLASSEXW { cbSize = Marshal.SizeOf<N.WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc), hInstance = hInst, hCursor = N.LoadCursorW(IntPtr.Zero, 32512), lpszClassName = "SozKapsulAcilir" };
            N.RegisterClassExW(ref wc); _sinifKayitli = true;
        }
        _hwnd = N.CreateWindowExW(N.WS_EX_NOREDIRECTIONBITMAP | N.WS_EX_NOACTIVATE | N.WS_EX_TOOLWINDOW | N.WS_EX_TOPMOST, "SozKapsulAcilir", "", N.WS_POPUP, wx, wy, pencW, pencH, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("açılır pencere oluşturulamadı");
        Pencereler[_hwnd] = this;

        var comp = g.Compositor;
        var compPtr = MarshalInspectable<Compositor>.FromManaged(comp);
        var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compPtr, in iid, out var interop));
        var vt = *(IntPtr**)interop; IntPtr res;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)vt[3])(interop, _hwnd, 1, &res);
        Marshal.Release(interop); Marshal.Release(compPtr);
        Marshal.ThrowExceptionForHR(hr);
        _target = MarshalInspectable<DesktopWindowTarget>.FromAbi(res);

        Kur(comp, pencW, pencH, govW, govH, azalt, (ox + Pay * olcek, oy + Pay * olcek));
        N.ShowWindow(_hwnd, 4);
    }

    private static Color C(Renk r) => Color.FromArgb(r.A, r.R, r.G, r.B);

    private void Kur(Compositor comp, int pw, int ph, double govW, double govH, bool azalt, (double X, double Y) orijin)
    {
        var s = _olcek; var f = (float)s;
        _root = comp.CreateContainerVisual(); _root.Size = new Vector2(pw, ph);
        _root.Opacity = 0f; _root.CenterPoint = new Vector3((float)orijin.X, (float)orijin.Y, 0);
        _root.Scale = azalt ? new Vector3(1, 1, 1) : new Vector3(.96f, .96f, 1);
        var sv = comp.CreateShapeVisual(); sv.Size = new Vector2(pw, ph);
        var off = new Vector2((float)(Pay * s), (float)(Pay * s));
        foreach (var (sis, alfa) in new (double, double)[] { (16, .02), (12, .03), (9, .045), (6, .06), (3.5, .08), (1.5, .10) })
        {
            var g = comp.CreateRoundedRectangleGeometry(); var i = sis * s;
            g.Size = new Vector2((float)(govW + 2 * i), (float)(govH + 2 * i)); g.CornerRadius = new Vector2((float)(Radius * s + i));
            var sh = comp.CreateSpriteShape(g); sh.Offset = new Vector2(off.X - (float)i, off.Y - (float)i + (float)(4 * s));
            sh.FillBrush = comp.CreateColorBrush(Color.FromArgb((byte)Math.Round(Math.Min(1, alfa * Math.Max(.7, _t.GolgeOlcegi)) * 255), 0, 0, 0)); sv.Shapes.Add(sh);
        }
        var body = comp.CreateRoundedRectangleGeometry(); body.Size = new Vector2((float)govW, (float)govH); body.CornerRadius = new Vector2((float)(Radius * s));
        var bsh = comp.CreateSpriteShape(body); bsh.Offset = off; bsh.FillBrush = comp.CreateColorBrush(C(_t.PanelZemin)); sv.Shapes.Add(bsh);
        var ksh = comp.CreateSpriteShape(body); ksh.Offset = off; ksh.StrokeBrush = comp.CreateColorBrush(C(_t.PanelCizgi)); ksh.StrokeThickness = (float)Math.Max(1, s); sv.Shapes.Add(ksh);
        _root.Children.InsertAtTop(sv);

        // satır vurgusu
        _vurgu = comp.CreateShapeVisual(); _vurgu.Size = new Vector2(pw, ph); _vurgu.Opacity = 0f;
        var vg = comp.CreateRoundedRectangleGeometry(); vg.Size = new Vector2((float)(govW - 2 * Ic * s), (float)(SatirY * s - 4 * s)); vg.CornerRadius = new Vector2((float)(8 * s));
        var vs = comp.CreateSpriteShape(vg); vs.FillBrush = comp.CreateColorBrush(C(_t.Koyu ? Renk.Hex(0xFFFFFF, .11) : Renk.Hex(0x141E3C, .08)));
        _vurgu.Shapes.Add(vs); _root.Children.InsertAtTop(_vurgu);

        foreach (var (y, h, sa) in _yerler)
        {
            var top = (Pay + y) * s; var left = (Pay + Ic) * s;
            if (sa.Ayirici)
            {
                var lg = comp.CreateLineGeometry(); lg.Start = new Vector2((float)left, (float)(top + h * s / 2)); lg.End = new Vector2((float)(Pay * s + govW - Ic * s), (float)(top + h * s / 2));
                var ls = comp.CreateSpriteShape(lg); ls.StrokeBrush = comp.CreateColorBrush(C(_t.PanelCizgi)); ls.StrokeThickness = (float)Math.Max(1, s);
                var lv = comp.CreateShapeVisual(); lv.Size = new Vector2(pw, ph); lv.Shapes.Add(ls); _root.Children.InsertAtTop(lv); continue;
            }
            var metinRenk = sa.Baslik || sa.Soluk ? _t.PanelYazi2 : _t.PanelYazi;
            var solPad = 8.0;
            if (sa.Glif != null)
            {
                var gv = comp.CreateShapeVisual(); var kutu = 18.0; gv.Size = new Vector2((float)(kutu * s), (float)(kutu * s));
                gv.Offset = new Vector3((float)(left + solPad * s), (float)(top + (h - kutu) * s / 2), 0);
                _g.GlifCiz(gv, sa.Glif, sa.Sinif == DurumSinifi.Yok ? _t.PanelYazi : _t.Durum(sa.Sinif), kutu, _t.CizgiKalinligi);
                _root.Children.InsertAtTop(gv); solPad += 26;
            }
            else if (sa.Isaretli) { solPad += 0; }
            var sagGen = sa.Sag != null ? _g.YaziGenisligiDp(sa.Sag) + 8 : 0;
            var enFazla = Math.Max(30, (govW / s) - 2 * Ic - solPad - 8 - sagGen - 8);
            var agirlik = sa.Baslik ? Math.Max(_t.YaziAgirligi, 600) : _t.YaziAgirligi;
            var tx = _g.YaziSprite(sa.Metin, metinRenk, sa.Baslik ? 12 : 13, agirlik, enFazla, out var meta);
            if (tx != null && meta != null)
            {
                tx.Offset = new Vector3((float)(left + solPad * s - YaziBitmap.Pay), (float)(top + (h * s - (meta.H - 2 * YaziBitmap.Pay)) / 2 - YaziBitmap.Pay), 0);
                _root.Children.InsertAtTop(tx);
            }
            if (sa.Sag != null)
            {
                var sr = _g.YaziSprite(sa.Sag, sa.Sinif == DurumSinifi.Yok ? _t.PanelYazi2 : _t.Durum(sa.Sinif), 12, _t.YaziAgirligi, 230, out var m2);
                if (sr != null && m2 != null)
                {
                    sr.Offset = new Vector3((float)(Pay * s + govW - Ic * s - 8 * s - (m2.W - 2 * YaziBitmap.Pay) - YaziBitmap.Pay), (float)(top + (h * s - (m2.H - 2 * YaziBitmap.Pay)) / 2 - YaziBitmap.Pay), 0);
                    _root.Children.InsertAtTop(sr);
                }
            }
            if (sa.Isaretli)
            {
                var gv = comp.CreateShapeVisual(); var kutu = 16.0; gv.Size = new Vector2((float)(kutu * s), (float)(kutu * s));
                gv.Offset = new Vector3((float)(Pay * s + govW - Ic * s - 8 * s - kutu * s), (float)(top + (h - kutu) * s / 2), 0);
                _g.GlifCiz(gv, "tam", _t.Vurgu, kutu, 2); _root.Children.InsertAtTop(gv);
            }
        }
        _target!.Root = _root;

        // doğuş: kapsülün olduğu köşeden büyür
        if (azalt)
        {
            var kf = comp.CreateScalarKeyFrameAnimation(); kf.InsertKeyFrame(1f, 1f, comp.CreateLinearEasingFunction()); kf.Duration = TimeSpan.FromMilliseconds(80); _root.StartAnimation("Opacity", kf);
        }
        else
        {
            var sc = comp.CreateSpringVector3Animation(); sc.DampingRatio = 0.8f; sc.Period = TimeSpan.FromSeconds(Yay.CompositionPeriyodu(380)); sc.FinalValue = new Vector3(1, 1, 1); _root.StartAnimation("Scale", sc);
            var op = comp.CreateSpringScalarAnimation(); op.DampingRatio = 1f; op.Period = TimeSpan.FromSeconds(Yay.CompositionPeriyodu(1600)); op.FinalValue = 1f; _root.StartAnimation("Opacity", op);
        }
    }

    private static IntPtr StaticProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        if (Pencereler.TryGetValue(h, out var a)) return a.Islem(h, msg, wp, lp);
        return N.DefWindowProcW(h, msg, wp, lp);
    }

    private int SatirBul(double px, double py)
    {
        var ox = px - Pay * _olcek; var oy = py - Pay * _olcek;
        for (var i = 0; i < _yerler.Count; i++)
        {
            var (y, h, s) = _yerler[i];
            if (s.Ayirici || s.Baslik || !s.Tiklanir) continue;
            if (oy >= y * _olcek && oy < (y + h) * _olcek && ox >= 0 && ox < GovdePx.W) return i;
        }
        return -1;
    }

    private IntPtr Islem(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case N.WM_MOUSEACTIVATE: return N.MA_NOACTIVATE;
            case N.WM_NCHITTEST:
            {
                var sx = N.X(lp); var sy = N.Y(lp);
                return GovdePx.Icinde(sx, sy) && !_kapaniyor ? N.HTCLIENT : N.HTTRANSPARENT;
            }
            case N.WM_MOUSEMOVE:
            {
                if (!_izliyor) { var te = new N.TRACKMOUSEEVENT { cbSize = sizeof(N.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = h }; N.TrackMouseEvent(ref te); _izliyor = true; _ustunde(true); }
                var i = SatirBul(N.X(lp), N.Y(lp));
                if (i != _secili) VurguGit(i);
                ImlecAyarla();
                return IntPtr.Zero;
            }
            case WM_SETCURSOR:
                // yalnız tıklanır satırın üstünde el imleci: tıklanmayan satır (başlık, düz cümle) tıklanır görünmez
                if ((N.X(lp) & 0xFFFF) == N.HTCLIENT) { ImlecAyarla(); return (IntPtr)1; }
                break;
            case N.WM_MOUSELEAVE: _izliyor = false; VurguGit(-1); _ustunde(false); return IntPtr.Zero;
            case N.WM_LBUTTONUP:
            {
                var i = SatirBul(N.X(lp), N.Y(lp));
                if (i >= 0) _tik(_yerler[i].S.Kimlik);
                return IntPtr.Zero;
            }
            case N.WM_RBUTTONUP: _tik("__sagtik"); return IntPtr.Zero;
        }
        return N.DefWindowProcW(h, msg, wp, lp);
    }

    private const uint WM_SETCURSOR = 0x20;
    private const int IdcArrow = 32512, IdcHand = 32649;

    private void ImlecAyarla() => N.SetCursor(N.LoadCursorW(IntPtr.Zero, _secili >= 0 ? IdcHand : IdcArrow));

    private void VurguGit(int i)
    {
        _secili = i;
        var comp = _g.Compositor;
        if (i < 0) { var kf = comp.CreateScalarKeyFrameAnimation(); kf.InsertKeyFrame(1f, 0f); kf.Duration = TimeSpan.FromMilliseconds(100); _vurgu.StartAnimation("Opacity", kf); return; }
        var (y, h, _) = _yerler[i];
        var hedef = new Vector3((float)((Pay + Ic) * _olcek), (float)((Pay + y) * _olcek + 2 * _olcek), 0);
        // vurgu ilk kez görünüyorsa yerine atlar, sonra yayla kayar
        if (_vurgu.Opacity < 0.05f) { _vurgu.StopAnimation("Offset"); _vurgu.Offset = hedef; }
        var sp = comp.CreateSpringVector3Animation(); sp.DampingRatio = 0.8f; sp.Period = TimeSpan.FromSeconds(Yay.CompositionPeriyodu(800));
        sp.FinalValue = hedef;
        _vurgu.StartAnimation("Offset", sp);
        var kf2 = comp.CreateScalarKeyFrameAnimation(); kf2.InsertKeyFrame(1f, 1f); kf2.Duration = TimeSpan.FromMilliseconds(90); _vurgu.StartAnimation("Opacity", kf2);
    }

    /// <summary>Kapanış: 120 ms opaklık ve küçülme, sonra pencere yok edilir (çağıran bir zamanlayıcıyla <see cref="Yok"/> çağırır).</summary>
    public void Kapat(bool azalt)
    {
        if (_kapaniyor || _hwnd == IntPtr.Zero) return;
        _kapaniyor = true;
        var comp = _g.Compositor;
        var kf = comp.CreateScalarKeyFrameAnimation(); kf.InsertKeyFrame(1f, 0f, comp.CreateLinearEasingFunction()); kf.Duration = TimeSpan.FromMilliseconds(azalt ? 80 : 120);
        _root.StartAnimation("Opacity", kf);
        if (!azalt) { var sc = comp.CreateVector3KeyFrameAnimation(); sc.InsertKeyFrame(1f, new Vector3(.96f, .96f, 1), comp.CreateLinearEasingFunction()); sc.Duration = TimeSpan.FromMilliseconds(120); _root.StartAnimation("Scale", sc); }
    }

    public void Yok()
    {
        if (_hwnd == IntPtr.Zero) return;
        Pencereler.Remove(_hwnd);
        if (_ustundeBildir && _izliyor) _ustunde(false);
        _target?.Dispose(); _target = null;
        _root?.Dispose();
        N.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero;
    }
    private bool _ustundeBildir = false;

    public void Dispose() => Yok();
}
