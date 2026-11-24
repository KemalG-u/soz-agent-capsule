using System.Text.Json.Nodes;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>
/// Dosya var/yok ve boyut aralığı (`file-range`). Eski `file` ölçütünden farkı: yol proje klasöründen çıkamaz (`..`, kısayol, mutlak yol: "ölçülemedi"),
/// en az boyut varsayılan 1 bayttır (boş dosya "var" sayılmaz), üst sınır konabilir, dosya içeriği hiç okunmaz (1 GB'lık dosya belleği doldurmaz).
/// Yanlış geçme yolu: dosya var ve boyutu uygun ama içi yanlış olabilir (içeriğe `doc-heading` ya da `table-recon` bakar).
/// </summary>
public sealed class FileRangeMeasurer : MeasurerBase
{
    public override string Kind => CheckKinds.FileRange;
    public override string Level => Confidence.B;

    public override string? Validate(JsonObject spec)
    {
        if (string.IsNullOrWhiteSpace(S(spec, "path"))) return "file-range kontrolü için spec.path gerekli.";
        var min = L(spec, "minBytes"); var max = L(spec, "maxBytes");
        if (min is < 0 || max is < 0) return "Boyut sınırları 0 ya da daha büyük olmalı.";
        if (min is { } a && max is { } b && b < a) return $"En çok boyut ({b}) en az boyuttan ({a}) küçük olamaz.";
        return SchemaProblem(spec);
    }

    public override string Describe(JsonObject spec) => S(spec, "path") is { } p ? $" (dosya: {p})" : "";

    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var rel = S(c.Spec, "path");
        var problem = SafeFiles.Resolve(ctx.Root, rel, out var full);
        if (problem is not null) return Error(c, problem);
        var wantExists = B(c.Spec, "exists") ?? true;
        var isFile = File.Exists(full);
        if (!wantExists)
            return isFile ? Failed(c, "Dosya var (olmaması bekleniyordu): " + rel) : Passed(c, "Dosya yok (beklendiği gibi): " + rel);
        if (!isFile) return Failed(c, Directory.Exists(full) ? "Bu bir klasör, dosya değil: " + rel : "Dosya yok: " + rel);

        long size;
        try { size = new FileInfo(full).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Error(c, "Dosya boyutu okunamadı: " + rel); }
        var min = L(c.Spec, "minBytes") ?? 1;
        var max = L(c.Spec, "maxBytes");
        var reasons = new List<string>();
        if (size < min) reasons.Add(size == 0 ? "dosya boş (0 bayt)" : $"boyut {size} bayt (en az {min})");
        if (max is { } m && size > m) reasons.Add($"boyut {size} bayt (en çok {m})");
        return reasons.Count == 0
            ? Passed(c, $"Dosya var ({size} bayt{(max is null ? "" : $", izin verilen en çok {max}")}): {rel}")
            : Failed(c, $"{rel}: " + string.Join(", ", reasons));
    }

    public override (string label, JsonObject spec) FromTemplate(string templateId, IReadOnlyDictionary<string, string> values, string? root)
    {
        var p = TemplateValues.Path(values, "path", root);
        if (templateId == "file-absent")
            return ($"Şu dosya olmasın: {p}", new JsonObject { ["path"] = p, ["exists"] = false, ["template"] = templateId });
        var min = TemplateValues.Long(values, "minBytes", "En az boyut") ?? 1;
        var max = TemplateValues.Long(values, "maxBytes", "En çok boyut");
        if (max is { } b && b < min) throw new SozException(400, $"En çok boyut ({b}) en az boyuttan ({min}) küçük olamaz.");
        var spec = new JsonObject { ["path"] = p, ["exists"] = true, ["minBytes"] = min };
        if (max is not null) spec["maxBytes"] = max;
        spec["template"] = templateId;
        var label = max is null
            ? (min <= 1 ? $"Şu dosya oluşsun ve boş olmasın: {p}" : $"Şu dosya oluşsun, en az {min} bayt olsun: {p}")
            : $"Şu dosya oluşsun, {min}-{max} bayt arasında olsun: {p}";
        return (label, spec);
    }
}
