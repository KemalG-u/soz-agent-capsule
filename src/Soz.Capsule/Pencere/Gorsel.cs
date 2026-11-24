using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Soz.Capsule;

/// <summary>
/// Kapsülün çizimi: Windows.UI.Composition görsel ağacı. Boyut ve opaklık yayları DWM iş parçacığında koşar (uygulama iş parçacığı tıkansa
/// da hareket sürer). Hiçbir uygulama karesi üretmez: durum değişince yaylar başlatılır, sonra her şey durur. Birim: px (dp * ölçek).
/// </summary>
internal sealed unsafe class Gorsel : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions o, out IntPtr controller);

    public const double GolgePay = 20;      // dp, kapsül çevresindeki saydam pay (gölge için)
    public const double TuvalYukseklik = 120;
    public static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private readonly IntPtr _hwnd;
    private IntPtr _dqc;
    private Compositor _comp = null!;
    private DesktopWindowTarget _target = null!;
    private ContainerVisual _root = null!;
    private CompositionPropertySet _p = null!;
    private CompositionRoundedRectangleGeometry _body = null!;
    private ShapeVisual _svGolge = null!, _svNormal = null!, _svNokta = null!, _glif = null!, _rozet = null!, _cizgi = null!;
    private ContainerVisual _origin = null!, _icerik = null!, _girisK = null!;
    private SpriteVisual? _yazi, _girisYazi, _chipYazi, _imlec;
    private ContainerVisual _girisKirpma = null!;
    private ShapeVisual _chip = null!;
    private readonly List<object> _tut = new();
    private YaziYuzeyi? _yuzey;
    private readonly YaziCizici _cizici;
    private readonly Dictionary<(string, int, int, uint), (CompositionSurfaceBrush Firca, YaziBitmap Meta)> _fircalar = new();

    public double Olcek { get; private set; }
    public TemaKip Tema { get; private set; }
    public Ayar Ayar;
    public double TuvalGenisligiDp => Ayar.GirisW + 2 * GolgePay + 8;
    public int TuvalW => (int)Math.Round(TuvalGenisligiDp * Olcek);
    public int TuvalH => (int)Math.Round(TuvalYukseklik * Olcek);
    public Compositor Compositor => _comp;
    public YaziYuzeyi? Yuzey => _yuzey;
    public YaziCizici Cizici => _cizici;
    public string? YaziHatasi => _yuzey?.Hata;
    public bool AnimasyonAzalt;
    public int YaziAgirligi => Tema.YaziAgirligi;
    public const double YaziDp = 13;

    // hedefleri tekrar göndermemek için son uygulananlar
    private (double W, double H) _sonBoyut; private double _sonOp = -1; private bool _sonNokta; private bool _sonGiris; private bool _sonRozet; private bool _sonCalisiyor;
    private string? _sonYazi; private string? _sonGlif; private DurumSinifi _sonSinif; private int _sonTaraf = -1; private bool _hizliSon;

    public Gorsel(IntPtr hwnd, double olcek, Ayar ayar, TemaKip tema, YaziCizici cizici)
    {
        _hwnd = hwnd; Olcek = olcek; Ayar = ayar; Tema = tema; _cizici = cizici;
        var opt = new DispatcherQueueOptions { dwSize = sizeof(DispatcherQueueOptions), threadType = 2, apartmentType = 0 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(opt, out _dqc));
        _comp = new Compositor();
        var compPtr = MarshalInspectable<Compositor>.FromManaged(_comp);
        var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"); // ICompositorDesktopInterop
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compPtr, in iid, out var interop));
        var vt = *(IntPtr**)interop;
        IntPtr res;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)vt[3])(interop, hwnd, 1, &res);
        Marshal.Release(interop); Marshal.Release(compPtr);
        Marshal.ThrowExceptionForHR(hr);
        _target = MarshalInspectable<DesktopWindowTarget>.FromAbi(res);
        Kur();
    }

    // ------------------------------------------------------------------ yardımcılar
    private static Color C(Renk r) => Color.FromArgb(r.A, r.R, r.G, r.B);
    private float F(double v) => (float)v;
    private double Px(double dp) => dp * Olcek;

    private void Ifade(CompositionObject hedef, string ozellik, string ifade, bool bodyKullan = true)
    {
        var ex = _comp.CreateExpressionAnimation(ifade);
        ex.SetReferenceParameter("p", _p);
        if (bodyKullan) ex.SetReferenceParameter("b", _body);
        hedef.StartAnimation(ozellik, ex);
        _tut.Add(ex);
    }

    private CompositionColorBrush Renkli(Renk r) => _comp.CreateColorBrush(C(r));

    /// <summary>Gövdenin sol-üst köşesi (tuval pikseli) için ifade parçası.</summary>
    private const string SolUst = "p.ax - p.side * b.Size.X, p.cy - b.Size.Y * 0.5";
    private string _yar = "";

    // ------------------------------------------------------------------ ağaç
    private void Kur()
    {
        var s = Olcek;
        _p = _comp.CreatePropertySet();
        _p.InsertScalar("ax", F(Px(GolgePay + 4) + 0)); _p.InsertScalar("cy", F(Px(TuvalYukseklik / 2))); _p.InsertScalar("side", 1f); _p.InsertScalar("prog", 0.06f);
        _root = _comp.CreateContainerVisual();
        _root.Size = new Vector2(TuvalW, TuvalH);
        _root.Opacity = 0f;

        var (bw, bh) = (Px(Ayar.Big), Px(Ayar.Big));
        _body = _comp.CreateRoundedRectangleGeometry();
        _body.Size = new Vector2(F(bw), F(bh));
        _body.CornerRadius = new Vector2(F(bh / 2));
        _tut.Add(_body);
        _yar = $"Min(Min(b.Size.X, b.Size.Y) * 0.5, Min(b.Size.Y * {N(Tema.YaricapOrani)}, {N(Px(Tema.YaricapEn))}))";
        Ifade(_body, "CornerRadius", $"Vector2({_yar}, {_yar})");

        // gölge (altta)
        _svGolge = _comp.CreateShapeVisual(); _svGolge.Size = new Vector2(TuvalW, TuvalH);
        var golge = new (double Sis, double Alfa)[] { (14, 0.020), (11, 0.030), (8, 0.045), (5, 0.060), (3, 0.080), (1.5, 0.100) };
        foreach (var (sis, alfa) in golge)
        {
            var i = Px(sis); var dy = Px(3);
            var g = _comp.CreateRoundedRectangleGeometry();
            var shp = _comp.CreateSpriteShape(g);
            shp.FillBrush = _comp.CreateColorBrush(Color.FromArgb((byte)Math.Round(Math.Min(1, alfa * Tema.GolgeOlcegi) * 255), 0, 0, 0));
            Ifade(g, "Size", $"Vector2(b.Size.X + {N(2 * i)}, b.Size.Y + {N(2 * i)})");
            Ifade(g, "CornerRadius", $"Vector2({_yar} + {N(i)}, {_yar} + {N(i)})");
            Ifade(shp, "Offset", $"Vector2(p.ax - p.side * b.Size.X - {N(i)}, p.cy - b.Size.Y * 0.5 - {N(i)} + {N(dy)})");
            _svGolge.Shapes.Add(shp); _tut.Add(g); _tut.Add(shp);
        }
        if (Tema.GolgeOlcegi <= 0.01) _svGolge.IsVisible = false;
        _root.Children.InsertAtTop(_svGolge);

        // normal gövde: dolgu + parlama + kenar. Nokta (uyku): düz renk + kenar. Biri diğerine yumuşak geçer.
        _svNormal = _comp.CreateShapeVisual(); _svNormal.Size = new Vector2(TuvalW, TuvalH);
        var dolgu = _comp.CreateSpriteShape(_body); dolgu.FillBrush = Renkli(Tema.Govde);
        Ifade(dolgu, "Offset", $"Vector2({SolUst})");
        _svNormal.Shapes.Add(dolgu);
        if (Tema.ParlamaUst.A > 0 || Tema.ParlamaAlt.A > 0)
        {
            var gr = _comp.CreateLinearGradientBrush();
            gr.StartPoint = new Vector2(0.5f, 0); gr.EndPoint = new Vector2(0.5f, 1);
            gr.ColorStops.Add(_comp.CreateColorGradientStop(0f, C(Tema.ParlamaUst)));
            gr.ColorStops.Add(_comp.CreateColorGradientStop(1f, C(Tema.ParlamaAlt)));
            var par = _comp.CreateSpriteShape(_body); par.FillBrush = gr;
            Ifade(par, "Offset", $"Vector2({SolUst})");
            _svNormal.Shapes.Add(par); _tut.Add(par);
        }
        if (Tema.Kenar.A > 0)
        {
            var kenar = _comp.CreateSpriteShape(_body); kenar.StrokeBrush = Renkli(Tema.Kenar); kenar.StrokeThickness = F(Math.Max(1, Px(1)));
            Ifade(kenar, "Offset", $"Vector2({SolUst})");
            _svNormal.Shapes.Add(kenar); _tut.Add(kenar);
        }
        _root.Children.InsertAtTop(_svNormal);
        _tut.Add(dolgu);

        _svNokta = _comp.CreateShapeVisual(); _svNokta.Size = new Vector2(TuvalW, TuvalH); _svNokta.Opacity = 0f;
        var nd = _comp.CreateSpriteShape(_body); nd.FillBrush = Renkli(Tema.Nokta);
        nd.StrokeBrush = Renkli(Tema.NoktaKenar); nd.StrokeThickness = F(Math.Max(1, Px(1)));
        Ifade(nd, "Offset", $"Vector2({SolUst})");
        _svNokta.Shapes.Add(nd); _tut.Add(nd);
        _root.Children.InsertAtTop(_svNokta);

        // içerik: gövdenin sol-üstüne bağlı kap
        _origin = _comp.CreateContainerVisual(); _origin.Size = new Vector2(TuvalW, TuvalH);
        Ifade(_origin, "Offset", $"Vector3({SolUst}, 0)");
        _root.Children.InsertAtTop(_origin);

        _icerik = _comp.CreateContainerVisual(); _icerik.Size = new Vector2(TuvalW, TuvalH); _icerik.Opacity = 1f;
        _origin.Children.InsertAtTop(_icerik);

        _glif = _comp.CreateShapeVisual(); _glif.Size = new Vector2(F(Px(20)), F(Px(20)));
        Ifade(_glif, "Offset", $"Vector3((b.Size.Y - {N(Px(20))}) * 0.5, (b.Size.Y - {N(Px(20))}) * 0.5, 0)");
        _icerik.Children.InsertAtTop(_glif);

        // çalışan iş çizgisi (2 dp): hapın alt düz kısmında, DWM'de ilerler
        _cizgi = _comp.CreateShapeVisual(); _cizgi.Size = new Vector2(TuvalW, TuvalH); _cizgi.Opacity = 0f;
        var cg = _comp.CreateLineGeometry();
        var cs = _comp.CreateSpriteShape(cg); cs.StrokeBrush = Renkli(Tema.Vurgu); cs.StrokeThickness = F(Math.Max(2, Px(2)));
        cs.StrokeStartCap = CompositionStrokeCap.Round; cs.StrokeEndCap = CompositionStrokeCap.Round;
        Ifade(cg, "Start", $"Vector2(0, 0)");
        Ifade(cg, "End", $"Vector2(Max(0, p.prog * (b.Size.X - b.Size.Y)), 0)");
        Ifade(cs, "Offset", $"Vector2(b.Size.Y * 0.5, b.Size.Y - {N(Px(3))})");
        _cizgi.Shapes.Add(cs); _tut.Add(cg); _tut.Add(cs);
        _icerik.Children.InsertAtTop(_cizgi);

        // rozet (iş bekliyor işareti), gövdenin sağ üst köşesinde
        _rozet = _comp.CreateShapeVisual(); _rozet.Size = new Vector2(F(Px(9)), F(Px(9))); _rozet.Opacity = 0f;
        _root.Children.InsertAtTop(_rozet);
        Ifade(_rozet, "Offset", $"Vector3(p.ax + (1 - p.side) * b.Size.X - {N(Px(6))}, p.cy - b.Size.Y * 0.5 - {N(Px(3))}, 0)");

        KurGiris();
        _target.Root = _root;
        _sonBoyut = (0, 0); _sonOp = -1; _sonYazi = null; _sonGlif = null; _sonTaraf = -1;
    }

    private void KurGiris()
    {
        _girisK = _comp.CreateContainerVisual(); _girisK.Size = new Vector2(TuvalW, TuvalH); _girisK.Opacity = 0f; _girisK.IsVisible = false;
        _origin.Children.InsertAtTop(_girisK);
        _girisKirpma = _comp.CreateContainerVisual();
        _girisK.Children.InsertAtTop(_girisKirpma);
        _chip = _comp.CreateShapeVisual(); _chip.Size = new Vector2(F(Px(156)), F(Px(32)));
        _girisK.Children.InsertAtTop(_chip);
        _imlec = _comp.CreateSpriteVisual(); _imlec.Brush = Renkli(Tema.Vurgu); _imlec.Size = new Vector2(F(Math.Max(1.5, Px(1.5))), F(Px(20)));
        _imlec.Opacity = 1f; _girisK.Children.InsertAtTop(_imlec);
    }

    // ------------------------------------------------------------------ yazı
    private (CompositionSurfaceBrush? Firca, YaziBitmap? Meta) YaziFircasi(string yazi, Renk renk, double dp, int agirlik, double enFazlaDp)
    {
        var px = Math.Max(6, (int)Math.Round(dp * Olcek));
        var anahtar = (yazi, px * 1000 + agirlik, enFazlaDp > 1e5 ? int.MaxValue : (int)Math.Round(enFazlaDp * Olcek), (uint)(renk.A << 24 | renk.R << 16 | renk.G << 8 | renk.B));
        if (_fircalar.TryGetValue(anahtar, out var v)) return (v.Firca, v.Meta);
        if (_yuzey == null) _yuzey = YaziYuzeyi.Olustur(_comp);
        if (!_yuzey.Hazir) return (null, null);
        var bm = _cizici.Ciz(yazi, px, agirlik, renk, enFazlaDp > 1e5 ? int.MaxValue : (int)Math.Round(enFazlaDp * Olcek));
        var fr = _yuzey.Firca(bm);
        if (fr == null) return (null, null);
        if (_fircalar.Count > 60) { foreach (var kv in _fircalar.Values) kv.Firca.Dispose(); _fircalar.Clear(); }
        _fircalar[anahtar] = (fr, bm);
        return (fr, bm);
    }

    /// <summary>Yazıyı bir sprite olarak üretir; sprite konumu çağıranda ayarlanır (kenar payı çıkarılmış olarak).</summary>
    public SpriteVisual? YaziSprite(string yazi, Renk renk, double dp, int agirlik, double enFazlaDp, out YaziBitmap? meta)
    {
        var (fr, bm) = YaziFircasi(yazi, renk, dp, agirlik, enFazlaDp);
        meta = bm;
        if (fr == null || bm == null) return null;
        var sp = _comp.CreateSpriteVisual();
        sp.Brush = fr; sp.Size = new Vector2(bm.W, bm.H);
        return sp;
    }

    public double YaziGenisligiDp(string yazi) => _cizici.Olc(yazi, Math.Max(6, (int)Math.Round(YaziDp * Olcek)), Tema.YaziAgirligi) / Olcek;

    // ------------------------------------------------------------------ glif
    private sealed record Op(char Tur, double A = 0, double B = 0, double C = 0, double D = 0, double E = 0, double F = 0, double[]? Noktalar = null);

    private static Op Cember(double cx, double cy, double r) => new('c', cx, cy, r);
    private static Op Cizgi(double x1, double y1, double x2, double y2) => new('l', x1, y1, x2, y2);
    private static Op Nokta(double x, double y) => new('d', x, y);
    private static Op Dikdortgen(double x, double y, double w, double h, double r) => new('r', x, y, w, h, r);
    private static Op Yay_(double cx, double cy, double r, double basla, double bitis, double donus) => new('a', cx, cy, r, basla, bitis, donus);
    private static Op Cokgen(params double[] pts) => new('p', Noktalar: pts);

    private static readonly Dictionary<string, Op[]> Glifler = new()
    {
        ["ok"] = new[] { Cember(12, 12, 8.2), Cokgen(8.2, 12.3, 10.8, 14.9, 15.8, 9.6) },
        ["dec"] = new[] { Cokgen(12, 3.4, 20.6, 12, 12, 20.6, 3.4, 12, 12, 3.4), Cizgi(12, 8.4, 12, 12.5), Nokta(12, 15.5) },
        ["fail"] = new[] { Cokgen(12, 4, 21, 19.6, 3, 19.6, 12, 4), Cizgi(12, 10, 12, 14.2), Nokta(12, 16.9) },
        ["ask"] = new[] { Dikdortgen(5.5, 5, 13, 9.2, 2), Cokgen(11, 14.2, 7.3, 17.6, 7.3, 14.2), Cokgen(10.4, 8.7, 10.7, 8.0, 11.3, 7.6, 12.1, 7.6, 12.8, 8.0, 13.0, 8.7, 12.7, 9.4, 12.0, 9.9, 12.0, 10.9), Nokta(12, 12.5) },
        ["run"] = new[] { Yay_(12, 12, 7.8, 0.0, 0.75, 0.0), Nokta(12, 12) },
        ["mic"] = new[] { Dikdortgen(9, 3.5, 6, 11, 3), Yay_(12, 11.4, 6.4, 0.0, 0.5, 0.0), Cizgi(12, 17.8, 12, 20.8) },
        ["brand"] = new[] { Dikdortgen(5.5, 5, 13, 9.2, 2), Cokgen(11, 14.2, 7.3, 17.6, 7.3, 14.2) },
        ["tam"] = new[] { Cokgen(5, 12.5, 9.2, 16.7, 19, 7) },
        // belirsiz: içi boş halka ve soru işareti (durumdan emin olmadığını söyler, "bitti" ya da "kaldı" demez)
        ["unk"] = new[] { Cember(12, 12, 8.2), Cokgen(9.4, 9.6, 9.7, 8.4, 10.6, 7.7, 12.0, 7.5, 13.4, 7.7, 14.3, 8.4, 14.6, 9.6, 14.1, 10.7, 13.2, 11.5, 12.4, 12.2, 12.0, 13.2), Nokta(12, 16.2) },
    };

    /// <summary>Açılır yüzeylerin satır simgeleri için: glif kutusu dp (24 birimlik çizim bu kutuya sığar).</summary>
    public void GlifCiz(ShapeVisual sv, string glif, Renk renk, double kutuDp, double kalinlik) => GlifKur(sv, glif, renk, Px(kutuDp), 24, kalinlik);

    private void GlifKur(ShapeVisual sv, string glif, Renk renk, double kutuPx, double viewBox, double kalinlikBirim)
    {
        sv.Shapes.Clear();
        if (!Glifler.TryGetValue(glif, out var ops)) return;
        var gs = kutuPx / viewBox;
        var kal = F(Math.Max(1.2, kalinlikBirim * gs));
        void Cizgisel(CompositionSpriteShape sh)
        {
            sh.StrokeBrush = Renkli(renk); sh.StrokeThickness = kal;
            sh.StrokeStartCap = CompositionStrokeCap.Round; sh.StrokeEndCap = CompositionStrokeCap.Round; sh.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        }
        foreach (var op in ops)
        {
            switch (op.Tur)
            {
                case 'c':
                {
                    var g = _comp.CreateEllipseGeometry(); g.Center = new Vector2(F(op.A * gs), F(op.B * gs)); g.Radius = new Vector2(F(op.C * gs), F(op.C * gs));
                    var sh = _comp.CreateSpriteShape(g); Cizgisel(sh); sv.Shapes.Add(sh); break;
                }
                case 'a':
                {
                    var g = _comp.CreateEllipseGeometry(); g.Center = new Vector2(F(op.A * gs), F(op.B * gs)); g.Radius = new Vector2(F(op.C * gs), F(op.C * gs));
                    g.TrimStart = F(op.D); g.TrimEnd = F(op.E); g.TrimOffset = F(op.F);
                    var sh = _comp.CreateSpriteShape(g); Cizgisel(sh); sv.Shapes.Add(sh); break;
                }
                case 'l':
                {
                    var g = _comp.CreateLineGeometry(); g.Start = new Vector2(F(op.A * gs), F(op.B * gs)); g.End = new Vector2(F(op.C * gs), F(op.D * gs));
                    var sh = _comp.CreateSpriteShape(g); Cizgisel(sh); sv.Shapes.Add(sh); break;
                }
                case 'd':
                {
                    var g = _comp.CreateEllipseGeometry(); g.Center = new Vector2(F(op.A * gs), F(op.B * gs)); var r = F(kal * 0.62); g.Radius = new Vector2(r, r);
                    var sh = _comp.CreateSpriteShape(g); sh.FillBrush = Renkli(renk); sv.Shapes.Add(sh); break;
                }
                case 'r':
                {
                    var g = _comp.CreateRoundedRectangleGeometry(); g.Size = new Vector2(F(op.C * gs), F(op.D * gs)); g.CornerRadius = new Vector2(F(op.E * gs));
                    var sh = _comp.CreateSpriteShape(g); sh.Offset = new Vector2(F(op.A * gs), F(op.B * gs)); Cizgisel(sh); sv.Shapes.Add(sh); break;
                }
                case 'p':
                {
                    var n = op.Noktalar!;
                    for (var i = 0; i + 3 < n.Length; i += 2)
                    {
                        var g = _comp.CreateLineGeometry(); g.Start = new Vector2(F(n[i] * gs), F(n[i + 1] * gs)); g.End = new Vector2(F(n[i + 2] * gs), F(n[i + 3] * gs));
                        var sh = _comp.CreateSpriteShape(g); Cizgisel(sh); sv.Shapes.Add(sh);
                    }
                    break;
                }
            }
        }
    }

    private void RozetKur(DurumSinifi sinif)
    {
        _rozet.Shapes.Clear();
        var r = Tema.Durum(sinif); var u = Px(9);
        var kenar = Renkli(new Renk(140, 0, 0, 0));
        CompositionSpriteShape Dolu(CompositionGeometry g) { var sh = _comp.CreateSpriteShape(g); sh.FillBrush = Renkli(r); return sh; }
        switch (sinif)
        {
            case DurumSinifi.Karar: // baklava
            {
                var g = _comp.CreateRectangleGeometry(); g.Size = new Vector2(F(u * 0.7), F(u * 0.7));
                var sh = Dolu(g); sh.CenterPoint = new Vector2(F(u * 0.35), F(u * 0.35)); sh.RotationAngleInDegrees = 45; sh.Offset = new Vector2(F(u * 0.5 - u * 0.35), F(u * 0.5 - u * 0.35));
                _rozet.Shapes.Add(sh); break;
            }
            case DurumSinifi.Kaldi: // üçgen: kalın dış çizgi
            {
                var pts = new[] { (u * 0.5, u * 0.08), (u * 0.94, u * 0.88), (u * 0.06, u * 0.88), (u * 0.5, u * 0.08) };
                for (var i = 0; i < 3; i++)
                {
                    var g = _comp.CreateLineGeometry(); g.Start = new Vector2(F(pts[i].Item1), F(pts[i].Item2)); g.End = new Vector2(F(pts[i + 1].Item1), F(pts[i + 1].Item2));
                    var sh = _comp.CreateSpriteShape(g); sh.StrokeBrush = Renkli(r); sh.StrokeThickness = F(Math.Max(2, u * 0.26)); sh.StrokeStartCap = CompositionStrokeCap.Round; sh.StrokeEndCap = CompositionStrokeCap.Round;
                    _rozet.Shapes.Add(sh);
                }
                break;
            }
            case DurumSinifi.Soru: // yuvarlak köşeli kare
            {
                var g = _comp.CreateRoundedRectangleGeometry(); g.Size = new Vector2(F(u * 0.86), F(u * 0.86)); g.CornerRadius = new Vector2(F(u * 0.18));
                var sh = Dolu(g); sh.Offset = new Vector2(F(u * 0.07), F(u * 0.07)); _rozet.Shapes.Add(sh); break;
            }
            case DurumSinifi.Belirsiz: // içi boş halka: dolu daire "kesin" der, halka "emin değilim" der
            {
                var g = _comp.CreateEllipseGeometry(); g.Center = new Vector2(F(u / 2), F(u / 2)); g.Radius = new Vector2(F(u * 0.36), F(u * 0.36));
                var sh = _comp.CreateSpriteShape(g); sh.StrokeBrush = Renkli(r); sh.StrokeThickness = F(Math.Max(2, u * 0.24)); _rozet.Shapes.Add(sh); break;
            }
            default: // daire (bitti, dikte)
            {
                var g = _comp.CreateEllipseGeometry(); g.Center = new Vector2(F(u / 2), F(u / 2)); g.Radius = new Vector2(F(u * 0.46), F(u * 0.46));
                var sh = Dolu(g); sh.StrokeBrush = kenar; sh.StrokeThickness = 1f; _rozet.Shapes.Add(sh); break;
            }
        }
    }

    // ------------------------------------------------------------------ yaylar
    private SpringVector2NaturalMotionAnimation BoyutYayi(double w, double h, bool hizli)
    {
        var k = hizli ? Ayar.KFast : Ayar.K; var z = hizli ? Ayar.ZFast : Ayar.Z;
        var a = _comp.CreateSpringVector2Animation();
        a.DampingRatio = F(z); a.Period = TimeSpan.FromSeconds(Yay.CompositionPeriyodu(k));
        a.FinalValue = new Vector2(F(w), F(h));
        return a;
    }

    private void OpaklikHedefle(double op, bool yukselis)
    {
        if (AnimasyonAzalt)
        {
            var kf = _comp.CreateScalarKeyFrameAnimation();
            kf.InsertKeyFrame(1f, F(op), _comp.CreateLinearEasingFunction());
            kf.Duration = TimeSpan.FromMilliseconds(Ayar.ReducedFadeMs);
            _root.StartAnimation("Opacity", kf);
            return;
        }
        var ms = yukselis ? Ayar.WakeMs : Ayar.FadeMs;
        var a = _comp.CreateSpringScalarAnimation();
        a.DampingRatio = 1f; a.Period = TimeSpan.FromSeconds(Yay.CompositionPeriyodu(Yay.SertlikSureden(ms)));
        a.FinalValue = F(op);
        _root.StartAnimation("Opacity", a);
    }

    private void Gecis(CompositionObject hedef, string ozellik, double deger, double ms = 140)
    {
        if (AnimasyonAzalt) ms = Ayar.ReducedFadeMs;
        var kf = _comp.CreateScalarKeyFrameAnimation();
        kf.InsertKeyFrame(1f, F(deger), _comp.CreateLinearEasingFunction());
        kf.Duration = TimeSpan.FromMilliseconds(ms);
        hedef.StartAnimation(ozellik, kf);
    }

    // ------------------------------------------------------------------ dışarıya açık işlemler
    public void DurumUygula(Gorunum g, Taraf taraf, bool hizli, bool ilk = false)
    {
        var s = Olcek;
        var boyut = (Px(g.W), Px(g.H));
        // yan: sağ ya da sol çapa
        var sagTaraf = taraf == Taraf.Sag ? 1 : 0;
        if (_sonTaraf != sagTaraf)
        {
            _sonTaraf = sagTaraf;
            _p.InsertScalar("side", sagTaraf);
            _p.InsertScalar("ax", F(sagTaraf == 1 ? TuvalW - Px(GolgePay + 4) : Px(GolgePay + 4)));
        }
        if (boyut != _sonBoyut || ilk)
        {
            _body.StopAnimation("Size");
            if (AnimasyonAzalt || ilk) _body.Size = new Vector2(F(boyut.Item1), F(boyut.Item2));
            else _body.StartAnimation("Size", BoyutYayi(boyut.Item1, boyut.Item2, hizli));
            _sonBoyut = boyut;
        }
        // opaklık
        if (Math.Abs(g.Op - _sonOp) > 0.004 || ilk)
        {
            var yuk = g.Op > _sonOp;
            if (ilk) { _root.StopAnimation("Opacity"); _root.Opacity = F(g.Op); }
            else OpaklikHedefle(g.Op, yuk);
            _sonOp = g.Op;
        }
        var nokta = g.H < Px(22) / Olcek;
        if (nokta != _sonNokta || ilk) { _sonNokta = nokta; Gecis(_svNokta, "Opacity", nokta ? 1 : 0); Gecis(_svNormal, "Opacity", nokta ? 0 : 1); Gecis(_icerik, "Opacity", nokta ? 0 : 1); }
        _hizliSon = hizli;
        Icerik(g);
    }

    private void Icerik(Gorunum g)
    {
        var c = g.Icerik;
        var glif = g.Giris ? "brand" : c?.Glif ?? "brand";
        var sinif = g.Giris ? DurumSinifi.Yok : c?.Sinif ?? DurumSinifi.Yok;
        if (glif != _sonGlif || sinif != _sonSinif)
        {
            _sonGlif = glif; _sonSinif = sinif;
            GlifKur(_glif, glif, sinif == DurumSinifi.Yok ? Tema.Yazi : Tema.Durum(sinif), Px(20), 24, Tema.CizgiKalinligi);
        }
        // yazı yalnız hapta
        var yazi = g.Hap && !g.Giris ? c?.Yazi : null;
        if (yazi != _sonYazi)
        {
            _sonYazi = yazi;
            if (_yazi != null) { _icerik.Children.Remove(_yazi); _yazi.Dispose(); _yazi = null; }
            if (yazi != null)
            {
                var azami = Math.Max(20, Ayar.PillMax - Ayar.Big - 14);
                var sp = YaziSprite(yazi, Tema.Yazi, YaziDp, Tema.YaziAgirligi, azami, out var meta);
                if (sp != null && meta != null)
                {
                    Ifade(sp, "Offset", $"Vector3(b.Size.Y - {N(YaziBitmap.Pay)}, (b.Size.Y - {N(meta.H - 2 * YaziBitmap.Pay)}) * 0.5 - {N(YaziBitmap.Pay)}, 0)");
                    _icerik.Children.InsertAtTop(sp); _yazi = sp;
                }
            }
        }
        // rozet
        if (g.Rozet != _sonRozet || (g.Rozet && c != null && sinif != _rozetSinif))
        {
            if (g.Rozet && c != null) { RozetKur(c.Sinif); _rozetSinif = c.Sinif; }
            Gecis(_rozet, "Opacity", g.Rozet ? 1 : 0, 120);
            _sonRozet = g.Rozet;
        }
        // ilerleme çizgisi
        var cal = c is { Calisiyor: true } && !g.Giris && g.H >= Px(22) / Olcek;
        if (cal != _sonCalisiyor)
        {
            _sonCalisiyor = cal;
            _p.StopAnimation("prog");
            if (cal)
            {
                _p.InsertScalar("prog", 0.06f);
                var kf = _comp.CreateScalarKeyFrameAnimation();
                kf.InsertKeyFrame(1f, 0.94f, _comp.CreateLinearEasingFunction());
                kf.Duration = TimeSpan.FromMilliseconds(Ayar.ProgressMs);
                _p.StartAnimation("prog", kf);
            }
            Gecis(_cizgi, "Opacity", cal ? 1 : 0, 160);
        }
        // giriş
        if (g.Giris != _sonGiris)
        {
            _sonGiris = g.Giris;
            _girisK.IsVisible = true;
            Gecis(_girisK, "Opacity", g.Giris ? 1 : 0, 140);
            Gecis(_glif, "Opacity", g.Giris ? 0 : 1, 100);
        }
    }
    private DurumSinifi _rozetSinif;

    // ------------------------------------------------------------------ giriş satırı
    private string? _girisSon; private int _girisImlecSon = -1; private string? _chipSon; private double _girisKaydirma;

    /// <summary>Giriş satırını yeniden çizer: metin (ya da ipucu), imleç, hedef çipi. Hedef genişlik girisW (dp) için yerleştirilir.</summary>
    public void GirisGuncelle(string metin, int imlec, string chip, string ipucu, double girisWDp, bool zorla = false)
    {
        var s = Olcek;
        var cw = Math.Min(156, Math.Max(40, YaziGenisligiDp(chip) + 26));
        var chipLeftFromRight = 6 + cw;
        var alanL = 16.0; var alanW = Math.Max(40, girisWDp - alanL - chipLeftFromRight - 6);
        var ham = metin.Length == 0 ? ipucu : metin;
        if (!zorla && ham == _girisSon && imlec == _girisImlecSon && chip == _chipSon) return;
        // metin sprite'ı
        if (_girisYazi != null) { _girisKirpma.Children.Remove(_girisYazi); _girisYazi.Dispose(); _girisYazi = null; }
        var renk = metin.Length == 0 ? Tema.Yazi2 : Tema.Yazi;
        var sp = YaziSprite(ham, renk, YaziDp, Tema.YaziAgirligi, 4000, out var meta);
        var imlecX = metin.Length == 0 ? 0 : _cizici.OlcOn(metin, imlec, Math.Max(6, (int)Math.Round(YaziDp * s)), Tema.YaziAgirligi);
        var alanPx = alanW * s;
        if (imlecX - _girisKaydirma > alanPx - 4) _girisKaydirma = imlecX - alanPx + 4;
        if (imlecX - _girisKaydirma < 0) _girisKaydirma = imlecX;
        if (_girisKaydirma < 0 || metin.Length == 0) _girisKaydirma = 0;
        _girisKirpma.Size = new Vector2(F(alanPx), F(Px(Ayar.Big)));
        _girisKirpma.Clip = _comp.CreateInsetClip(0, 0, 0, 0);
        Ifade(_girisKirpma, "Offset", $"Vector3({N(Px(alanL))}, 0, 0)");
        if (sp != null && meta != null)
        {
            sp.Offset = new Vector3(F(-_girisKaydirma - YaziBitmap.Pay), F((Px(Ayar.Big) - (meta.H - 2 * YaziBitmap.Pay)) / 2 - YaziBitmap.Pay), 0);
            _girisKirpma.Children.InsertAtTop(sp); _girisYazi = sp;
        }
        if (_imlec != null)
        {
            _imlec.Offset = new Vector3(F(Px(alanL) + imlecX - _girisKaydirma), F((Px(Ayar.Big) - Px(20)) / 2), 0);
            _imlec.Opacity = 1f;
            _imlec.StopAnimation("Opacity");
            var kf = _comp.CreateScalarKeyFrameAnimation();
            kf.InsertKeyFrame(0f, 1f); kf.InsertKeyFrame(0.55f, 1f, _comp.CreateStepEasingFunction()); kf.InsertKeyFrame(0.56f, 0f); kf.InsertKeyFrame(1f, 0f);
            kf.Duration = TimeSpan.FromMilliseconds(1060); kf.IterationBehavior = AnimationIterationBehavior.Forever;
            if (!AnimasyonAzalt) _imlec.StartAnimation("Opacity", kf);
        }
        // hedef çipi (sağda)
        if (chip != _chipSon || zorla)
        {
            if (_chipYazi != null) { _girisK.Children.Remove(_chipYazi); }
            _chip.Shapes.Clear();
            var cr = Math.Min(16, Tema.Yaricap(32));
            var g = _comp.CreateRoundedRectangleGeometry(); g.Size = new Vector2(F(Px(cw)), F(Px(32))); g.CornerRadius = new Vector2(F(Px(cr)));
            var sh = _comp.CreateSpriteShape(g); sh.StrokeBrush = Renkli(Tema.Kenar.A > 0 ? Tema.Kenar : Tema.Yazi2.Alfa(.4)); sh.StrokeThickness = F(Math.Max(1, Px(1)));
            _chip.Shapes.Add(sh);
            _chip.Size = new Vector2(F(Px(cw)), F(Px(32)));
            _chip.Opacity = 1f;
            Ifade(_chip, "Offset", $"Vector3(b.Size.X - {N(Px(chipLeftFromRight))}, (b.Size.Y - {N(Px(32))}) * 0.5, 0)");
            if (_chipYazi != null) { _chipYazi.Dispose(); _chipYazi = null; }
            var cs = YaziSprite(chip, Tema.Yazi, 12, Tema.YaziAgirligi, cw - 20, out var cm);
            if (cs != null && cm != null)
            {
                cs.Offset = new Vector3(F((Px(cw) - (cm.W - 2 * YaziBitmap.Pay)) / 2 - YaziBitmap.Pay), F((Px(32) - (cm.H - 2 * YaziBitmap.Pay)) / 2 - YaziBitmap.Pay), 0);
                _girisK.Children.InsertAtTop(cs); _chipYazi = cs;
                // çip metni çipin konumuna bağlanır
                Ifade(cs, "Offset", $"Vector3(b.Size.X - {N(Px(chipLeftFromRight))} + {N((Px(cw) - (cm.W - 2 * YaziBitmap.Pay)) / 2 - YaziBitmap.Pay)}, (b.Size.Y - {N(Px(32))}) * 0.5 + {N((Px(32) - (cm.H - 2 * YaziBitmap.Pay)) / 2 - YaziBitmap.Pay)}, 0)");
            }
        }
        _girisSon = ham; _girisImlecSon = imlec; _chipSon = chip;
    }

    /// <summary>Giriş kapanınca yazı ve imleç nesneleri bırakılır (bellek).</summary>
    public void GirisBirak()
    {
        if (_girisYazi != null) { _girisKirpma.Children.Remove(_girisYazi); _girisYazi.Dispose(); _girisYazi = null; }
        if (_chipYazi != null) { _girisK.Children.Remove(_chipYazi); _chipYazi.Dispose(); _chipYazi = null; }
        _imlec?.StopAnimation("Opacity");
        _girisSon = null; _chipSon = null; _girisImlecSon = -1; _girisKaydirma = 0;
    }

    /// <summary>Giriş tamamen kapandığında görünmez katmanı gerçekten gizle.</summary>
    public void GirisGizle() { _girisK.IsVisible = false; }

    /// <summary>Hedef çipinin tuvalde (dp) kapladığı alan: tıklama için. Kapsül sağ ucuna göre.</summary>
    public (double Sag, double Genislik) ChipYerlesimi(string chip)
    {
        var cw = Math.Min(156, Math.Max(40, YaziGenisligiDp(chip) + 26));
        return (6 + cw, cw);
    }

    // ------------------------------------------------------------------ yaşam döngüsü
    public void Yeniden(double olcek, TemaKip tema)
    {
        Olcek = olcek; Tema = tema;
        _yazi?.Dispose(); _yazi = null;
        _girisYazi = null; _chipYazi = null; _imlec = null;
        foreach (var kv in _fircalar.Values) kv.Firca.Dispose(); _fircalar.Clear();
        _target.Root = null;
        _root.Dispose(); _tut.Clear(); _sonYazi = null; _sonGlif = null; _sonRozet = false; _sonGiris = false; _sonCalisiyor = false; _sonNokta = false;
        _sonBoyut = (0, 0); _sonOp = -1; _sonTaraf = -1;
        Kur();
    }

    /// <summary>Yazı yüzeyini (WARP aygıtı) ve önbelleği bırakır. Boşta uzun süre yazı yoksa çağrılır; sonraki yazıda yeniden açılır.</summary>
    public void YaziAygitiniBirak()
    {
        if (_yazi != null) { _icerik.Children.Remove(_yazi); _yazi.Dispose(); _yazi = null; _sonYazi = null; }
        if (_girisYazi != null) return;
        foreach (var kv in _fircalar.Values) kv.Firca.Dispose();
        _fircalar.Clear();
        _yuzey?.Dispose(); _yuzey = null;
    }

    public void Dispose()
    {
        _target.Root = null;
        _root.Dispose();
        foreach (var kv in _fircalar.Values) kv.Firca.Dispose();
        _yuzey?.Dispose();
        _target.Dispose(); _comp.Dispose();
    }

    /// <summary>Kapsülün tuvaldeki çapası (px): sağ taraf sağ kenardan, sol taraf soldan GolgePay + 4 içeride.</summary>
    public (double Ax, double Cy) CapaTuvalde(Taraf t) => (t == Taraf.Sag ? TuvalW - Px(GolgePay + 4) : Px(GolgePay + 4), TuvalH / 2.0);
}
