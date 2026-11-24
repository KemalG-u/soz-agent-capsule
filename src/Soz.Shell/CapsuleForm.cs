using System.Drawing;
using System.Windows.Forms;

namespace Soz.Shell;

internal static class ScreenMath
{
    /// <summary>Pencereyi, en çok kesiştiği (yoksa ilk) çalışma alanının içine çeker.</summary>
    public static Rectangle ClampToWorkAreas(Rectangle r, IReadOnlyList<Rectangle> workAreas)
    {
        if (workAreas.Count == 0) return r;
        var best = workAreas[0]; long bestArea = -1;
        foreach (var wa in workAreas)
        {
            var i = Rectangle.Intersect(r, wa);
            long area = i.IsEmpty ? 0 : (long)i.Width * i.Height;
            if (area > bestArea) { best = wa; bestArea = area; }
        }
        var x = r.Width >= best.Width ? best.Left : Math.Clamp(r.X, best.Left, best.Right - r.Width);
        var y = r.Height >= best.Height ? best.Top : Math.Clamp(r.Y, best.Top, best.Bottom - r.Height);
        return new Rectangle(x, y, r.Width, r.Height);
    }

    public static IReadOnlyList<Rectangle> WorkAreas() => Screen.AllScreens.Select(s => s.WorkingArea).ToList();
}

/// <summary>İpucu penceresi: fare olaylarına karşı saydam, odak almaz.</summary>
internal sealed class TipWindow : LayeredForm
{
    private const int WM_NCHITTEST = 0x0084;
    private CapsuleRenderer? _renderer;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)(-1); return; } // HTTRANSPARENT
        base.WndProc(ref m);
    }

    public void ShowFor(Rectangle anchorScreen, string text, Palette pal, float scale)
    {
        if (_renderer == null || Math.Abs(_renderer.Scale - scale) > 0.001f) { _renderer?.Dispose(); _renderer = new CapsuleRenderer(scale); }
        var size = _renderer.MeasureTip(text);
        var gap = (int)(2 * scale);
        var x = anchorScreen.Left + anchorScreen.Width / 2 - size.Width / 2;
        var y = anchorScreen.Top - size.Height + gap;
        var was = ScreenMath.WorkAreas();
        var wa = was.FirstOrDefault(a => a.Contains(anchorScreen.Location));
        if (wa.IsEmpty) wa = was.Count > 0 ? was[0] : Rectangle.Empty;
        if (!wa.IsEmpty)
        {
            if (y < wa.Top) y = anchorScreen.Bottom - gap; // üstte yer yoksa altına
            x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - size.Width));
        }
        Present(x, y, size.Width, size.Height, g => _renderer.DrawTip(g, size, text, pal));
        ShowNoActivate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _renderer?.Dispose(); _renderer = null; }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Ekranda duran küçük kapsül. Odak almaz (bkz. LayeredForm). Sürükle-bırak: 4 px eşiği geçmeyen hareket tıktır.
/// </summary>
internal sealed class CapsuleForm : LayeredForm
{
    private const int DragThreshold = 4;

    private CapsuleRenderer _renderer;
    private float _scale = 1f;
    private CapsuleModel _base = new("", 0, 0, false);
    private string? _flash;
    private Palette _pal;
    private CapsuleLayout? _layout;
    private HoverPart _hover = HoverPart.None;
    private float? _pulse;
    private int _x, _y;

    private readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = 450 };
    private readonly System.Windows.Forms.Timer _flashTimer = new();
    private DateTime _pulseStart;
    private readonly TipWindow _tip = new();

    // sürükleme
    private bool _leftDown, _dragging;
    private Point _downCursor, _downWindow;
    private HoverPart _downPart;

    public ContextMenuStrip? PopupMenu { get; set; }
    public bool ReducedMotion { get; set; }
    /// <summary>Yeni iş kısayolunun görünen adı (ipucu için); boşsa kısayol yok.</summary>
    public string NewJobHotkey { get; set; } = "";

    public event Action? BodyClicked;
    public event Action? NewJobClicked;
    public event Action? DictateClicked;
    public event Action? PacketClicked;
    public event Action<Point>? MoveEnded;

    public CapsuleForm(Palette palette)
    {
        _pal = palette;
        _renderer = new CapsuleRenderer(1f);
        _pulseTimer.Tick += (_, _) => PulseTick();
        _tipTimer.Tick += (_, _) => { _tipTimer.Stop(); ShowTip(); };
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flash = null; Render(); };
    }

    public CapsuleModel Model => _base with { Flash = _flash };
    public CapsuleLayout? CurrentLayout => _layout;
    public Palette Palette => _pal;
    public float CurrentScale => _scale;

    /// <summary>Kapsülü kaydedilmiş konuma (ya da varsayılana) koyar ve odak almadan gösterir.</summary>
    public void Start(Point? saved)
    {
        if (!IsHandleCreated) _ = Handle; // CreateControl görünmez formda tutacak oluşturmaz
        var probe = saved ?? Screen.PrimaryScreen?.WorkingArea.Location ?? Point.Empty;
        SetScale(Native.ScaleAtPoint(probe));
        var L = _renderer.Layout(Model);
        Point pos;
        if (saved is { } sp) pos = sp;
        else
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            var margin = (int)(16 * _scale);
            pos = new Point(wa.Right - L.WinW - margin, wa.Bottom - L.WinH - margin);
        }
        var clamped = ScreenMath.ClampToWorkAreas(new Rectangle(pos, new Size(L.WinW, L.WinH)), ScreenMath.WorkAreas());
        _x = clamped.X; _y = clamped.Y;
        Render();
        ShowNoActivate();
        // pencere yerleştikten sonra gerçek DPI farklıysa düzelt
        var real = DpiScale;
        if (Math.Abs(real - _scale) > 0.01f) { SetScale(real); Render(); }
    }

    private void SetScale(float s)
    {
        if (Math.Abs(s - _scale) < 0.001f && _renderer.Scale == s) return;
        _scale = s;
        _renderer.Dispose();
        _renderer = new CapsuleRenderer(s);
    }

    protected override void OnScaleChanged()
    {
        SetScale(DpiScale);
        Reclamp();
    }

    public void SetModel(CapsuleModel model)
    {
        var old = _base;
        _base = model with { Flash = null };
        if (model.Badge > old.Badge && old.HasData) Pulse();
        Render();
    }

    public void SetPalette(Palette p) { _pal = p; Render(); }

    public void Flash(string text, int ms = 2200)
    {
        _flash = text;
        _flashTimer.Stop(); _flashTimer.Interval = ms; _flashTimer.Start();
        Render();
    }

    public void Pulse()
    {
        if (ReducedMotion || !IsShown) return;
        _pulseStart = DateTime.UtcNow;
        _pulse = 0f;
        if (!_pulseTimer.Enabled) _pulseTimer.Start();
    }

    private void PulseTick()
    {
        var t = (float)((DateTime.UtcNow - _pulseStart).TotalMilliseconds / 600.0);
        if (t >= 1f) { _pulse = null; _pulseTimer.Stop(); }
        else _pulse = t;
        Render();
    }

    /// <summary>Ekran düzeni değişince kapsülü görünür alana çeker.</summary>
    public void Reclamp()
    {
        if (!IsHandleCreated) return;
        var L = _renderer.Layout(Model);
        var c = ScreenMath.ClampToWorkAreas(new Rectangle(_x, _y, L.WinW, L.WinH), ScreenMath.WorkAreas());
        _x = c.X; _y = c.Y;
        Render();
    }

    private void Render()
    {
        if (!IsHandleCreated) return;
        var m = Model;
        var L = _renderer.Layout(m);
        var oldW = _layout?.WinW ?? L.WinW;
        _layout = L;
        // genişlik değişince sağ kenar ekran dışına taşmasın
        var rect = ScreenMath.ClampToWorkAreas(new Rectangle(_x, _y, L.WinW, L.WinH), ScreenMath.WorkAreas());
        if (!_dragging) { _x = rect.X; _y = rect.Y; }
        Present(_x, _y, L.WinW, L.WinH, g => _renderer.Draw(g, L, m, _pal, _hover, _pulse));
    }

    // ---------- fare ----------
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        HideTip();
        if (e.Button != MouseButtons.Left || _layout == null) return;
        _downPart = _layout.HitTest(e.Location);
        if (_downPart == HoverPart.None) return;
        _leftDown = true; _dragging = false;
        _downCursor = Cursor.Position;
        _downWindow = new Point(_x, _y);
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_layout == null) return;
        if (_leftDown)
        {
            var cur = Cursor.Position;
            var dx = cur.X - _downCursor.X; var dy = cur.Y - _downCursor.Y;
            if (!_dragging && (Math.Abs(dx) >= DragThreshold || Math.Abs(dy) >= DragThreshold)) { _dragging = true; SetHover(HoverPart.None); }
            if (_dragging)
            {
                _x = _downWindow.X + dx; _y = _downWindow.Y + dy;
                RepresentAt(_x, _y);
            }
            return;
        }
        var part = _layout.HitTest(e.Location);
        if (part != _hover)
        {
            SetHover(part);
            HideTip();
        }
        if (part != HoverPart.None) { _tipTimer.Stop(); _tipTimer.Start(); } else { _tipTimer.Stop(); HideTip(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            if (_layout?.HitTest(e.Location) is HoverPart.Body or HoverPart.NewJob or HoverPart.Dictate or HoverPart.Packet)
            { HideTip(); PopupMenu?.Show(Cursor.Position); }
            return;
        }
        if (e.Button != MouseButtons.Left || !_leftDown) return;
        var wasDrag = _dragging;
        _leftDown = false; _dragging = false; Capture = false;
        if (wasDrag)
        {
            Reclamp();
            MoveEnded?.Invoke(new Point(_x, _y));
            return;
        }
        var up = _layout?.HitTest(e.Location) ?? HoverPart.None;
        if (up != _downPart) return;
        switch (up)
        {
            case HoverPart.NewJob: NewJobClicked?.Invoke(); break;
            case HoverPart.Dictate: DictateClicked?.Invoke(); break;
            case HoverPart.Packet: PacketClicked?.Invoke(); break;
            case HoverPart.Body: BodyClicked?.Invoke(); break;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_leftDown) return;
        _tipTimer.Stop(); HideTip(); SetHover(HoverPart.None);
    }

    private void SetHover(HoverPart p)
    {
        if (p == _hover) return;
        _hover = p;
        Render();
    }

    private void ShowTip()
    {
        if (_layout == null || _hover == HoverPart.None || _leftDown) return;
        var text = _hover switch
        {
            HoverPart.NewJob => NewJobHotkey.Length > 0 ? Strings.Get("tip.newjob", NewJobHotkey) : Strings.Get("tip.newjob.nokey"),
            HoverPart.Dictate => Strings.Get("tip.dictate"),
            HoverPart.Packet => Strings.Get("tip.packet"),
            _ => Model.Tooltip(),
        };
        var b = NativeBounds;
        var body = new Rectangle(b.Left + (int)_layout.Body.Left, b.Top + (int)_layout.Body.Top, (int)_layout.Body.Width, (int)_layout.Body.Height);
        _tip.ShowFor(body, text, _pal, _scale);
    }

    private void HideTip() { _tip.HideWindow(); }

    /// <summary>Duman ve sınama için: tüm yüzeyi PNG'ye yazar değil; yalnız ölçüm.</summary>
    public Rectangle BodyScreenRect
    {
        get
        {
            var b = NativeBounds;
            return _layout == null ? b : new Rectangle(b.Left + (int)_layout.Body.Left, b.Top + (int)_layout.Body.Top, (int)_layout.Body.Width, (int)_layout.Body.Height);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulseTimer.Dispose(); _tipTimer.Dispose(); _flashTimer.Dispose();
            _tip.Dispose(); _renderer.Dispose();
        }
        base.Dispose(disposing);
    }
}
