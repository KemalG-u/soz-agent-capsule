using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Soz.Shell;

internal enum HoverPart { None, Body, NewJob, Dictate, Packet }

internal sealed record CapsuleModel(string ProjectName, int Waiting, int Stalled, bool HasData, string? Flash = null, int FailedJobs = 0)
{
    public int Badge => Waiting + Stalled + FailedJobs;

    public string DisplayText =>
        Flash ?? (!HasData ? Strings.Get("cap.nodata") : ProjectName.Length > 0 ? ProjectName : Strings.Get("cap.noproject"));

    public string BadgeText => Badge > 99 ? "99+" : Badge.ToString();

    /// <summary>İpucu: "2 bekliyor, 1 durumu belli değil, 1 iş kaldı · Proje: …" (sıfır olan parça yazılmaz)</summary>
    public string Tooltip()
    {
        if (!HasData) return Strings.Get("tip.nodata");
        var parts = new List<string>();
        if (Waiting > 0) parts.Add(Strings.Get("tip.waiting", Waiting));
        if (Stalled > 0) parts.Add(Strings.Get("tip.stalled", Stalled));
        if (FailedJobs > 0) parts.Add(Strings.Get("tip.jobsleft", FailedJobs));
        var head = parts.Count == 0 ? Strings.Get("tip.none") : string.Join(", ", parts);
        var projPart = ProjectName.Length > 0 ? Strings.Get("tip.project", ProjectName) : Strings.Get("tip.noproject");
        return head + " · " + projPart;
    }
}

/// <summary>Kapsülün ölçüleri. Hem çizim hem tıklama sınaması aynı yerleşimi kullanır.</summary>
internal sealed class CapsuleLayout
{
    public const float Margin = 8f;   // gölge payı (mantıksal px)
    public const float Height = 40f;
    public const float MinWidth = 168f;
    public const float MaxWidth = 292f;
    public const float BtnSize = 24f;

    public float Scale { get; }
    public int WinW { get; }
    public int WinH { get; }
    public RectangleF Body { get; }
    public RectangleF Dot { get; }
    public RectangleF NameRect { get; }
    public RectangleF? BadgeRect { get; }
    public RectangleF NewJob { get; }
    public RectangleF Dictate { get; }
    public RectangleF Packet { get; }

    public CapsuleLayout(CapsuleModel m, float s, Func<string, float> measureLogical)
    {
        Scale = s;
        var nameW = measureLogical(m.DisplayText);
        var badgeW = m.Badge > 0 ? (m.BadgeText.Length >= 2 ? 26f : 20f) : 0f;
        var rightChrome = 10f + BtnSize + 2f + BtnSize + 2f + BtnSize + 6f + (badgeW > 0 ? badgeW + 6f : 0f);
        var w = Math.Clamp(30f + nameW + 4f + rightChrome, MinWidth, MaxWidth);

        WinW = (int)Math.Ceiling((w + 2 * Margin) * s);
        WinH = (int)Math.Ceiling((Height + 2 * Margin) * s);
        Body = new RectangleF(Margin * s, Margin * s, w * s, Height * s);

        float X(float lx) => (Margin + lx) * s;
        float Y(float ly) => (Margin + ly) * s;
        Dot = new RectangleF(X(14), Y(Height / 2 - 4), 8 * s, 8 * s);
        Packet = new RectangleF(X(w - 10 - BtnSize), Y((Height - BtnSize) / 2), BtnSize * s, BtnSize * s);
        Dictate = new RectangleF(X(w - 10 - BtnSize - 2 - BtnSize), Y((Height - BtnSize) / 2), BtnSize * s, BtnSize * s);
        NewJob = new RectangleF(X(w - 10 - BtnSize - 2 - BtnSize - 2 - BtnSize), Y((Height - BtnSize) / 2), BtnSize * s, BtnSize * s);
        var rightEdge = NewJob.Left - 6 * s;
        if (badgeW > 0)
        {
            BadgeRect = new RectangleF(rightEdge - badgeW * s, Y((Height - 18) / 2), badgeW * s, 18 * s);
            rightEdge = BadgeRect.Value.Left - 6 * s;
        }
        NameRect = new RectangleF(X(30), Body.Top, Math.Max(10, rightEdge - X(30)), Body.Height);
    }

    public HoverPart HitTest(Point p)
    {
        var pt = new PointF(p.X, p.Y);
        if (NewJob.Contains(pt)) return HoverPart.NewJob;
        if (Dictate.Contains(pt)) return HoverPart.Dictate;
        if (Packet.Contains(pt)) return HoverPart.Packet;
        return Body.Contains(pt) ? HoverPart.Body : HoverPart.None;
    }
}

/// <summary>GDI+ ile kapsül çizimi (kenar yumuşatmalı, piksel başına alfa). Ölçek başına bir örnek.</summary>
internal sealed class CapsuleRenderer : IDisposable
{
    private static string? _family;
    private static string? _boldFamily;
    private readonly Font _nameFont;
    private readonly Font _badgeFont;
    private readonly Font _tipFont;
    public float Scale { get; }

    public CapsuleRenderer(float scale)
    {
        Scale = scale;
        _family ??= PickFamily("Segoe UI Variable Text", "Segoe UI", "Tahoma");
        _boldFamily ??= PickFamily("Segoe UI Variable Text Semibold", "Segoe UI Semibold", _family);
        _nameFont = new Font(_boldFamily, 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _badgeFont = new Font(_boldFamily, 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _tipFont = new Font(_family, 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public void Dispose() { _nameFont.Dispose(); _badgeFont.Dispose(); _tipFont.Dispose(); }

    private static string PickFamily(params string[] names)
    {
        var installed = FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) if (installed.Contains(n)) return n;
        return "Segoe UI";
    }

    private static StringFormat Fmt(StringAlignment h, bool ellipsis) => new(StringFormatFlags.NoWrap | StringFormatFlags.NoClip)
    {
        Alignment = h,
        LineAlignment = StringAlignment.Center,
        Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None,
        HotkeyPrefix = HotkeyPrefix.None,
    };

    /// <summary>Metin genişliği (mantıksal px).</summary>
    public float MeasureLogical(string text)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var f = Fmt(StringAlignment.Near, false);
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        return g.MeasureString(text, _nameFont, new PointF(0, 0), f).Width / Scale;
    }

    public CapsuleLayout Layout(CapsuleModel m) => new(m, Scale, MeasureLogical);

    private static GraphicsPath Pill(RectangleF r)
    {
        var p = new GraphicsPath();
        var d = Math.Min(r.Height, r.Width);
        if (r.Width <= r.Height) { p.AddEllipse(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Color WithAlpha(Color c, int a) => Color.FromArgb(Math.Clamp(a, 0, 255), c);

    private static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private Color StateColor(CapsuleModel m, Palette pal) =>
        !m.HasData ? pal.SubText : m.Stalled > 0 || m.FailedJobs > 0 ? pal.Warn : m.Waiting > 0 ? pal.Warm : pal.Calm;

    /// <param name="pulse">0..1 arası vurgu ilerlemesi; null = yok</param>
    public void Draw(Graphics g, CapsuleLayout L, CapsuleModel m, Palette pal, HoverPart hover, float? pulse)
    {
        Prepare(g);
        g.Clear(Color.Transparent);
        var s = L.Scale;

        // gölge: üst üste binen yarı saydam halkalar (gerçek bulanıklık yerine ucuz ve yumuşak)
        var layers = (int)Math.Round(6 * s);
        for (var i = layers; i >= 1; i--)
        {
            var r = RectangleF.Inflate(L.Body, i * 0.95f, i * 0.95f);
            r.Offset(0, 1.6f * s);
            using var path = Pill(r);
            var a = (int)(pal.ShadowAlpha * (1f - (i - 1f) / layers) * 0.55f);
            using var b = new SolidBrush(Color.FromArgb(Math.Max(1, a), 0, 0, 0));
            g.FillPath(b, path);
        }

        using (var body = Pill(L.Body))
        {
            using var fill = new SolidBrush(pal.Body);
            g.FillPath(fill, body);
            // vurgu: sıcak kenar parıltısı, 600 ms içinde söner
            if (pulse is { } t)
            {
                var glow = StateColor(m, pal);
                using var pen = new Pen(WithAlpha(glow, (int)(200 * (1 - t))), 2.2f * s);
                var inner = RectangleF.Inflate(L.Body, -1.1f * s, -1.1f * s);
                using var ip = Pill(inner);
                g.DrawPath(pen, ip);
            }
            var bi = RectangleF.Inflate(L.Body, -0.5f * s, -0.5f * s);
            using var borderPath = Pill(bi);
            using var border = new Pen(pal.Border, Math.Max(1f, s));
            g.DrawPath(border, borderPath);
        }

        // durum noktası
        var dotColor = StateColor(m, pal);
        if (m.HasData && m.Badge > 0)
        {
            var halo = RectangleF.Inflate(L.Dot, 3.5f * s, 3.5f * s);
            using var hb = new SolidBrush(WithAlpha(dotColor, pal.Light ? 46 : 60));
            g.FillEllipse(hb, halo);
        }
        if (pulse is { } pt)
        {
            var grow = (2 + 9 * pt) * s;
            var ring = RectangleF.Inflate(L.Dot, grow, grow);
            using var rp = new Pen(WithAlpha(dotColor, (int)(150 * (1 - pt))), 1.6f * s);
            g.DrawEllipse(rp, ring);
        }
        using (var db = new SolidBrush(dotColor)) g.FillEllipse(db, L.Dot);

        // ad / ileti
        var nameColor = m.Flash != null ? (pal.Light ? Color.FromArgb(255, 160, 78, 6) : pal.Warm) : m.HasData ? pal.Text : pal.SubText;
        using (var tb = new SolidBrush(nameColor))
        using (var sf = Fmt(StringAlignment.Near, true))
            g.DrawString(m.DisplayText, _nameFont, tb, new RectangleF(L.NameRect.X, L.NameRect.Y - 0.5f * s, L.NameRect.Width, L.NameRect.Height), sf);

        // rozet
        if (L.BadgeRect is { } br && m.Badge > 0)
        {
            var stalledOnly = m.Waiting == 0;
            var fill = stalledOnly ? pal.Warn : pal.Warm;
            var txt = stalledOnly ? pal.BadgeWarnText : pal.BadgeWarmText;
            using var path = Pill(br);
            using var fb = new SolidBrush(fill);
            g.FillPath(fb, path);
            using var tb = new SolidBrush(txt);
            using var sf = Fmt(StringAlignment.Center, false);
            g.DrawString(m.BadgeText, _badgeFont, tb, new RectangleF(br.X, br.Y - 0.5f * s, br.Width, br.Height), sf);
        }

        DrawButton(g, L.NewJob, pal, hover == HoverPart.NewJob, PencilIcon, s);
        DrawButton(g, L.Dictate, pal, hover == HoverPart.Dictate, MicIcon, s);
        DrawButton(g, L.Packet, pal, hover == HoverPart.Packet, ClipboardIcon, s);
    }

    private static void DrawButton(Graphics g, RectangleF r, Palette pal, bool hover, Action<Graphics, float, float, Pen, Brush, float> icon, float s)
    {
        if (hover)
        {
            using var hb = new SolidBrush(pal.Hover);
            g.FillEllipse(hb, r);
        }
        using var pen = new Pen(pal.Icon, 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(pal.Icon);
        icon(g, r.X + r.Width / 2, r.Y + r.Height / 2, pen, brush, s);
    }

    private static void MicIcon(Graphics g, float cx, float cy, Pen pen, Brush brush, float s)
    {
        cy -= 0.8f * s;
        using (var body = RoundRect(new RectangleF(cx - 2.6f * s, cy - 6f * s, 5.2f * s, 8f * s), 2.6f * s)) g.DrawPath(pen, body);
        g.DrawArc(pen, cx - 4.8f * s, cy - 3.6f * s, 9.6f * s, 9.6f * s, 0, 180);
        g.DrawLine(pen, cx, cy + 6f * s, cx, cy + 8f * s);
        g.DrawLine(pen, cx - 2.6f * s, cy + 8f * s, cx + 2.6f * s, cy + 8f * s);
    }

    /// <summary>Kalem: köşegen gövde, uç ve omuz çizgisi; altında kısa çizgi (yeni yazı).</summary>
    private static void PencilIcon(Graphics g, float cx, float cy, Pen pen, Brush brush, float s)
    {
        PointF P(float x, float y) => new(cx + x * s, cy + y * s);
        using (var body = new GraphicsPath())
        {
            body.AddPolygon(new[] { P(-5.2f, 5.0f), P(-1.9f, 4.1f), P(5.7f, -3.5f), P(3.5f, -5.7f), P(-4.1f, 1.9f) });
            g.DrawPath(pen, body);
        }
        g.DrawLine(pen, P(-1.9f, 4.1f), P(-4.1f, 1.9f)); // omuz
        g.DrawLine(pen, P(0.5f, 7.4f), P(5.5f, 7.4f));   // alt çizgi
    }

    private static void ClipboardIcon(Graphics g, float cx, float cy, Pen pen, Brush brush, float s)
    {
        using (var board = RoundRect(new RectangleF(cx - 4.6f * s, cy - 5f * s, 9.2f * s, 11.6f * s), 1.9f * s)) g.DrawPath(pen, board);
        using (var tab = RoundRect(new RectangleF(cx - 2.6f * s, cy - 6.8f * s, 5.2f * s, 3.2f * s), 1.2f * s)) g.FillPath(brush, tab);
        g.DrawLine(pen, cx - 2.2f * s, cy - 0.2f * s, cx + 2.2f * s, cy - 0.2f * s);
        g.DrawLine(pen, cx - 2.2f * s, cy + 2.6f * s, cx + 1f * s, cy + 2.6f * s);
    }

    // ---------- ipucu ----------
    public Size MeasureTip(string text, float maxLogicalWidth = 320f)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var f = Fmt(StringAlignment.Near, false);
        f.FormatFlags &= ~StringFormatFlags.NoWrap;
        var sz = g.MeasureString(text, _tipFont, (int)(maxLogicalWidth * Scale), f);
        var pad = 10f * Scale;
        var shadow = 6f * Scale;
        return new Size((int)Math.Ceiling(sz.Width + 2 * pad + 2 * shadow), (int)Math.Ceiling(sz.Height + 2 * 6f * Scale + 2 * shadow));
    }

    public void DrawTip(Graphics g, Size size, string text, Palette pal)
    {
        Prepare(g);
        g.Clear(Color.Transparent);
        var s = Scale;
        var shadow = 6f * s;
        var body = new RectangleF(shadow, shadow, size.Width - 2 * shadow, size.Height - 2 * shadow);
        var layers = (int)Math.Round(4 * s);
        for (var i = layers; i >= 1; i--)
        {
            var r = RectangleF.Inflate(body, i * 0.9f, i * 0.9f);
            r.Offset(0, 1.2f * s);
            using var p = RoundRect(r, 8f * s + i * 0.9f);
            using var b = new SolidBrush(Color.FromArgb(Math.Max(1, (int)(pal.ShadowAlpha * (1f - (i - 1f) / layers) * 0.5f)), 0, 0, 0));
            g.FillPath(b, p);
        }
        using (var p = RoundRect(body, 8f * s))
        {
            using var fb = new SolidBrush(Color.FromArgb(250, pal.Body));
            g.FillPath(fb, p);
            using var pen = new Pen(pal.Border, Math.Max(1f, s));
            g.DrawPath(pen, p);
        }
        using var tb = new SolidBrush(pal.Text);
        using var sf = Fmt(StringAlignment.Near, false);
        sf.LineAlignment = StringAlignment.Center;
        sf.FormatFlags &= ~StringFormatFlags.NoWrap;
        g.DrawString(text, _tipFont, tb, new RectangleF(body.X + 10 * s, body.Y, body.Width - 20 * s, body.Height), sf);
    }
}
