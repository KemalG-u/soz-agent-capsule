using System.Text;
using System.Text.Json.Nodes;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>Dosya: var mı, en az şu kadar bayt mı, içinde yazı geçiyor mu. Eski ProofRunner.RunFile, davranışı aynen (yeni sıkı ölçücü: file-range).</summary>
public sealed class FileMeasurer : MeasurerBase
{
    public override string Kind => CheckKinds.File;
    public override string Level => Confidence.B;

    public override string? Validate(JsonObject spec) => string.IsNullOrWhiteSpace(S(spec, "path")) ? "file kontrolü için spec.path gerekli." : null;

    public override string Describe(JsonObject spec) => S(spec, "path") is { } p ? $" (dosya: {p})" : "";

    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var rel = S(c.Spec, "path");
        if (string.IsNullOrWhiteSpace(rel)) return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, "Dosya yolu boş.", null, 0);
        var path = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(ctx.Root, rel));
        var wantExists = B(c.Spec, "exists") ?? true;
        var exists = File.Exists(path);
        if (!wantExists)
            return new CheckOutcome(c.Id, c.Label, exists ? ResultStatus.Failed : ResultStatus.Passed, null, exists ? "Dosya var (olmaması bekleniyordu): " + rel : "Dosya yok (beklendiği gibi).", null, 0);
        if (!exists) return new CheckOutcome(c.Id, c.Label, ResultStatus.Failed, null, "Dosya yok: " + rel, null, 0);
        var size = new FileInfo(path).Length;
        var reasons = new List<string>();
        if (L(c.Spec, "minBytes") is { } min && size < min) reasons.Add($"boyut {size} bayt (en az {min})");
        if (S(c.Spec, "contains") is { Length: > 0 } contains)
        {
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                var buf = new char[4 * 1024 * 1024];
                var n = sr.ReadBlock(buf, 0, buf.Length);
                text = new string(buf, 0, n);
            }
            if (!text.Contains(contains, StringComparison.OrdinalIgnoreCase)) reasons.Add($"içinde \"{contains}\" yok");
        }
        return new CheckOutcome(c.Id, c.Label, reasons.Count == 0 ? ResultStatus.Passed : ResultStatus.Failed, null,
            ProofRunner.Tail(reasons.Count == 0 ? $"Dosya var ({size} bayt): {rel}" : $"{rel}: " + string.Join(", ", reasons)), null, 0);
    }
}
