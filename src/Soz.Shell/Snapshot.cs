using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Soz.Shell;

/// <summary>
/// --snapshot: kapsülü ekrana göstermeden bit eşleme çizip PNG'ye yazar. Dört dosya: açık/koyu tema x %100/%150.
/// Her sayfa birkaç durumu (sakin, bekleyen, takılan, uzun ad, üzerine gelme, uyarı, veri yok, vurgu, ipucu) iki zemin
/// üzerinde (kendi temasının zemini ve zıt zemin) gösterir. Çizim, gerçek pencerenin kullandığı aynı CapsuleRenderer'dır.
/// </summary>
internal static class Snapshot
{
    private sealed record Row(string Label, CapsuleModel Model, HoverPart Hover = HoverPart.None, float? Pulse = null, string? Tip = null);

    public static List<string> Run(string basePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(basePath)) ?? ".";
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(basePath);
        var written = new List<string>();
        foreach (var (themeName, pal) in new[] { ("acik", Palette.LightTheme), ("koyu", Palette.DarkTheme) })
            foreach (var pct in new[] { 100, 150 })
            {
                var path = Path.Combine(dir, $"{stem}-{themeName}-{pct}.png");
                using var bmp = RenderSheet(pal, pct / 100f);
                bmp.Save(path, ImageFormat.Png);
                written.Add(path);
            }
        return written;
    }

    public static Bitmap RenderSheet(Palette pal, float scale)
    {
        using var rend = new CapsuleRenderer(scale);
        var rows = new List<Row>
        {
            new("sakin", new CapsuleModel("Söz", 0, 0, true)),
            new("2 bekliyor", new CapsuleModel("Söz", 2, 0, true)),
            new("1 takili", new CapsuleModel("KarıncaAI", 0, 1, true)),
            new("3+1, uzun ad", new CapsuleModel("Çok uzun bir proje adı burada görünür", 3, 1, true)),
            new("12 rozet", new CapsuleModel("Deneme Projesi", 12, 0, true)),
            new("dikte ustunde", new CapsuleModel("Söz", 2, 0, true), HoverPart.Dictate),
            new("paket ustunde", new CapsuleModel("Söz", 0, 0, true), HoverPart.Packet),
            new("uyari", new CapsuleModel("Söz", 2, 0, true, "Paket panoda")),
            new("veri yok", new CapsuleModel("", 0, 0, false)),
            new("vurgu %35", new CapsuleModel("Söz", 2, 0, true), HoverPart.None, 0.35f),
            new("ipucu", new CapsuleModel("Söz", 2, 1, true), HoverPart.Body, null, "tip"),
        };

        var pad = (int)(14 * scale);
        var layouts = rows.Select(r => rend.Layout(r.Model)).ToList();
        var tipText = rows.Last().Model.Tooltip();
        var tipSize = rend.MeasureTip(tipText);
        var rowH = layouts.Max(l => l.WinH) + (int)(4 * scale);
        var colW = layouts.Max(l => l.WinW) + 2 * pad;
        var width = colW * 2;
        var height = pad * 2 + rows.Count * rowH + tipSize.Height;
        var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        // zemin: sol sütun temanın kendi zemini, sağ sütun zıt zemin
        var left = pal.Light ? Color.FromArgb(238, 241, 246) : Color.FromArgb(22, 23, 27);
        var right = pal.Light ? Color.FromArgb(52, 62, 84) : Color.FromArgb(226, 229, 236);
        using (var b = new SolidBrush(left)) g.FillRectangle(b, 0, 0, colW, height);
        using (var b = new LinearGradientBrush(new Rectangle(colW, 0, colW, height), right, Shade(right, pal.Light), 90f))
            g.FillRectangle(b, colW, 0, colW, height);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var L = layouts[i];
            using var cb = new Bitmap(L.WinW, L.WinH, PixelFormat.Format32bppPArgb);
            using (var cg = Graphics.FromImage(cb)) rend.Draw(cg, L, row.Model, pal, row.Hover, row.Pulse);
            var y = pad + i * rowH;
            g.CompositingMode = CompositingMode.SourceOver;
            g.DrawImageUnscaled(cb, pad, y);
            g.DrawImageUnscaled(cb, colW + pad, y);
        }

        // ipucu penceresi örneği
        using (var tb = new Bitmap(tipSize.Width, tipSize.Height, PixelFormat.Format32bppPArgb))
        {
            using (var tg = Graphics.FromImage(tb)) rend.DrawTip(tg, tipSize, tipText, pal);
            var y = pad + rows.Count * rowH;
            g.DrawImageUnscaled(tb, pad, y);
            g.DrawImageUnscaled(tb, colW + pad, y);
        }
        return sheet;
    }

    private static Color Shade(Color c, bool lightTheme) =>
        lightTheme ? Color.FromArgb(Math.Min(255, c.R + 30), Math.Min(255, c.G + 40), Math.Min(255, c.B + 50))
                   : Color.FromArgb(c.R - 40, c.G - 40, c.B - 30);

    /// <summary>Kapsülün tek karesi (saydam PNG) + gerçek yazı için: --snapshot ile birlikte kullanılmaz; selftest için ölçüm.</summary>
    public static (int W, int H) MeasureOne(CapsuleModel m, float scale)
    {
        using var r = new CapsuleRenderer(scale);
        var l = r.Layout(m);
        return (l.WinW, l.WinH);
    }
}
