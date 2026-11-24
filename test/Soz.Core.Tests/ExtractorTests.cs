using System.Globalization;
using System.Text;
using Soz.Core.Extract;
using Xunit;
using Xunit.Abstractions;

namespace Soz.Core.Tests;

public class ExtractorTests
{
    private readonly ITestOutputHelper _out;
    public ExtractorTests(ITestOutputHelper output) => _out = output;

    public static List<(string label, string text)> Labeled()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "etiketli-cumleler.tsv");
        return File.ReadAllLines(path, Encoding.UTF8)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t', 2))
            .Select(p => (p[0].Trim(), p[1].Trim()))
            .ToList();
    }

    public sealed record Metrics(double Threshold, int Tp, int Fp, int Fn, int Tn, double Precision, double Recall, double TypeAccuracy);

    public static Metrics Measure(double threshold)
    {
        var x = new RuleBasedExtractor();
        int tp = 0, fp = 0, fn = 0, tn = 0, typeOk = 0;
        foreach (var (label, text) in Labeled())
        {
            var s = x.Score(text);
            var predicted = s.Type is not null && s.Score >= threshold;
            var actual = label != "none";
            if (predicted && actual)
            {
                tp++;
                var t = s.Type!;
                var ok = t == label || (label == "correction" && t is "rule" or "ban" or "correction") || (t == "correction" && label is "rule" or "ban");
                if (ok) typeOk++;
            }
            else if (predicted) fp++;
            else if (actual) fn++;
            else tn++;
        }
        double p = tp + fp == 0 ? 1 : (double)tp / (tp + fp);
        double r = tp + fn == 0 ? 1 : (double)tp / (tp + fn);
        return new Metrics(threshold, tp, fp, fn, tn, p, r, tp == 0 ? 0 : (double)typeOk / tp);
    }

    [Fact]
    public void LabeledSet_Has_AtLeast_60_Sentences_TR_And_EN()
    {
        var l = Labeled();
        Assert.True(l.Count >= 60, $"etiketli cümle: {l.Count}");
        Assert.True(l.Count(x => x.label != "none") >= 30);
        Assert.True(l.Count(x => x.label == "none") >= 30);
        Assert.Contains(l, x => x.text.StartsWith("Always") || x.text.StartsWith("Never"));
    }

    [Fact]
    public void PrecisionRecall_Report_And_DefaultThreshold()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Etiketli küme: {Labeled().Count} cümle ({Labeled().Count(x => x.label != "none")} karar/kural, {Labeled().Count(x => x.label == "none")} gürültü)");
        sb.AppendLine("eşik   TP  FP  FN  TN  kesinlik  geri-çağırma  tür-doğruluğu");
        foreach (var t in new[] { 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75 })
        {
            var m = Measure(t);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{t:0.00}  {m.Tp,3} {m.Fp,3} {m.Fn,3} {m.Tn,3}   {m.Precision:0.000}     {m.Recall:0.000}        {m.TypeAccuracy:0.000}"));
        }
        var chosen = Measure(0.60);
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"SEÇİLEN eşik 0.60 → kesinlik {chosen.Precision:0.000}, geri çağırma {chosen.Recall:0.000}, tür doğruluğu {chosen.TypeAccuracy:0.000}"));
        _out.WriteLine(sb.ToString());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ayiklayici-olcum.txt"), sb.ToString());

        Assert.True(chosen.Precision >= 0.90, $"kesinlik {chosen.Precision:0.000}");
        Assert.True(chosen.Recall >= 0.60, $"geri çağırma {chosen.Recall:0.000}");
        Assert.True(chosen.TypeAccuracy >= 0.80, $"tür doğruluğu {chosen.TypeAccuracy:0.000}");
    }

    [Fact]
    public void Misses_Are_Listed_For_Tuning()
    {
        var x = new RuleBasedExtractor();
        foreach (var (label, text) in Labeled())
        {
            var s = x.Score(text);
            var predicted = s.Type is not null && s.Score >= 0.60;
            if (predicted != (label != "none"))
                _out.WriteLine($"{(predicted ? "YP" : "YN")} [{label}] → {s.Type}:{s.Score:0.00}  {text}  ({string.Join(" | ", s.Reasons)})");
        }
    }

    [Theory]
    [InlineData("Hala bekliyor musun?")]
    [InlineData("Yine de bir bakalım, belki düzelir.")]
    [InlineData("Tamam devam et.")]
    public void Noise_Is_Not_A_Candidate(string text)
    {
        Assert.Empty(new RuleBasedExtractor().Extract(text));
    }

    [Fact]
    public void Splitter_Skips_Code_Logs_LongLines_And_Pastes()
    {
        var msg = "Bundan sonra her zaman Türkçe yaz.\n```\nvar x = 1; // her zaman\n```\n" +
                  "2026-11-10 12:00:01 Error: her zaman çöküyor\n" + new string('a', 450) + "\n" +
                  "<pasted_content id=\"1\">Asla bunu yapma, her zaman böyle.</pasted_content>\nKarincaAI.exe dosyasını asla çalıştırma.";
        var sentences = RuleBasedExtractor.SplitSentences(msg).ToList();
        foreach (var s in sentences) _out.WriteLine("CÜMLE: " + s);
        Assert.Equal(new[] { "Bundan sonra her zaman Türkçe yaz.", "KarincaAI.exe dosyasını asla çalıştırma." }, sentences);
        var c = new RuleBasedExtractor().Extract(msg);
        Assert.Equal(2, c.Count);
        Assert.Equal("rule", c[0].Type);
        Assert.Equal("ban", c[1].Type);
    }
}
