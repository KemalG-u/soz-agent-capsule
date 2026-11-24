using System.Drawing;
using System.Drawing.Drawing2D;

namespace Soz.Shell;

/// <summary>Tepsi ve panel simgesi çalışma zamanında çizilir (dosya ya da kaynak gerekmez).</summary>
internal static class TrayIconFactory
{
    public static Icon Create(int size, Palette pal)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            var s = size / 32f;
            // konuşma balonu: yuvarlak gövde + küçük kuyruk, içinde üç satır
            var body = new RectangleF(1.5f * s, 3f * s, 29f * s, 22f * s);
            using var path = new GraphicsPath();
            var r = 9f * s;
            path.AddArc(body.X, body.Y, r * 2, r * 2, 180, 90);
            path.AddArc(body.Right - r * 2, body.Y, r * 2, r * 2, 270, 90);
            path.AddArc(body.Right - r * 2, body.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddLine(body.X + 17f * s, body.Bottom, body.X + 7f * s, 30f * s);
            path.AddLine(body.X + 7f * s, 30f * s, body.X + 9f * s, body.Bottom - 0.2f * s);
            path.AddArc(body.X, body.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(pal.Calm);
            g.FillPath(fill, path);
            using var pen = new Pen(Color.White, Math.Max(1.6f, 2.4f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 8f * s, 10f * s, 24f * s, 10f * s);
            g.DrawLine(pen, 8f * s, 15f * s, 21f * s, 15f * s);
            g.DrawLine(pen, 8f * s, 20f * s, 16f * s, 20f * s);
        }
        var h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { Native.DestroyIcon(h); }
    }
}
