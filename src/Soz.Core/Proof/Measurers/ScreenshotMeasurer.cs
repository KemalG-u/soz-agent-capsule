using System.Diagnostics;
using System.Text.Json.Nodes;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>Ekran görüntüsü: sayfayı başsız Edge ile açıp görüntü alır; doğruluğuna insan bakar. Eski ProofRunner.RunScreenshot, davranışı aynen.</summary>
public sealed class ScreenshotMeasurer : MeasurerBase
{
    public override string Kind => CheckKinds.Screenshot;
    public override string Level => Confidence.B;
    /// <summary>Edge açmak yavaş ve ekrana bağlı: başlangıç ölçümüne girmez.</summary>
    public override BaselineMode Baseline => BaselineMode.None;

    public override string? Validate(JsonObject spec) => string.IsNullOrWhiteSpace(S(spec, "url")) ? "screenshot kontrolü için spec.url gerekli." : null;

    public override string Describe(JsonObject spec) => S(spec, "url") is { } u ? $" (adres: {u})" : "";

    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var url = S(c.Spec, "url");
        if (string.IsNullOrWhiteSpace(url)) return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, "Adres boş.", null, 0);
        var w = (int)Math.Clamp(L(c.Spec, "width") ?? 1280, 200, 4000);
        var h = (int)Math.Clamp(L(c.Spec, "height") ?? 800, 200, 4000);
        var sw = Stopwatch.StartNew();
        // 1) Sayfa açılıyor mu? (http/https ise durum kodu; file:// ise dosya)
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile)
        {
            if (!File.Exists(u.LocalPath)) return new CheckOutcome(c.Id, c.Label, ResultStatus.Failed, null, "Dosya yok: " + u.LocalPath, null, 0);
        }
        else
        {
            var (status, _, err) = UrlMeasurer.Get(url, timeout);
            if (status is null) return new CheckOutcome(c.Id, c.Label, ResultStatus.Failed, null, ProofRunner.Tail("Sayfa açılmadı: " + err), null, 0);
            if (status >= 400) return new CheckOutcome(c.Id, c.Label, ResultStatus.Failed, status, ProofRunner.Tail($"Sayfa açılmadı: durum {status}"), null, 0);
        }
        // 2) Başsız Edge ile ekran görüntüsü (ayrı geçici profil: kullanıcının açık Edge'ine dokunmaz).
        var edge = ctx.FindEdge();
        if (edge is null) return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, "Edge (msedge.exe) bulunamadı. Ekran görüntüsü alınamadı.", null, 0);
        Directory.CreateDirectory(ctx.EvidenceDir);
        var name = $"ekran-{c.Id}.png";
        var png = Path.Combine(ctx.EvidenceDir, name);
        var profile = Path.Combine(ctx.EvidenceDir, "edge-profil-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);
        try
        {
            var args = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-extensions --hide-scrollbars " +
                       $"--user-data-dir=\"{profile}\" --window-size={w},{h} --screenshot=\"{png}\" \"{url}\"";
            var left = timeout - sw.Elapsed;
            if (left < TimeSpan.FromSeconds(5)) left = TimeSpan.FromSeconds(5);
            using var tree = ProcessTree.Start(edge, args, ctx.EvidenceDir, null, ctx.DataDir);
            var finished = tree.WaitForExit(left, out _);
            if (!finished) return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, "Ekran görüntüsü zaman aşımına uğradı. Edge süreç ağacı kapatıldı.", null, 0);
            if (!File.Exists(png) || new FileInfo(png).Length == 0)
                return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, ProofRunner.Tail("Edge ekran görüntüsü yazmadı.\n" + tree.Output), null, 0);
            return new CheckOutcome(c.Id, c.Label, ResultStatus.Passed, null, $"Sayfa açıldı, ekran görüntüsü alındı ({w}x{h}).", name, 0);
        }
        finally
        {
            for (var i = 0; i < 5; i++)
            {
                try { if (Directory.Exists(profile)) Directory.Delete(profile, true); break; }
                catch (Exception) { Thread.Sleep(300); }
            }
        }
    }
}
