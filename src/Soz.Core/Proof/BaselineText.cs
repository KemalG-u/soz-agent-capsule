using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soz.Core.Proof;

/// <summary>
/// Başlangıç ölçümü (denetim 3, İlke 1 madde 4): ajan başlamadan önce açık ölçütler bir kez koşulur, sayılar işe yazılır.
/// Kapıda karşılaştırma tabanı budur ("geçen test sayısı düştü mü, atlanan arttı mı") ve kartta sade bir cümle olarak görünür:
/// "Başlamadan önce 2 testten 1'i geçmiyordu. Şimdi 2'si de geçiyor."
/// </summary>
public static class BaselineText
{
    public sealed record Check(string Key, string Label, string Status, int? Total, int? Passed, int Skipped);
    public sealed record Baseline(string At, string Verdict, List<Check> Checks)
    {
        // AYNI komutu koşan ölçütler (aynı Key) bir kez sayılır: biri projede kalıcı, biri karttan iş kopyası olarak açıksa test sayısı ikiye katlanmasın ("2 testten 2'si" değil "4 testten 2'si" görünmesin).
        private IEnumerable<Check> Distinct() => Checks.GroupBy(c => c.Key, StringComparer.Ordinal).Select(g => g.First());
        public int? Total => Checks.Any(c => c.Total is not null) ? Distinct().Sum(c => c.Total ?? 0) : null;
        public int? Passed => Checks.Any(c => c.Total is not null) ? Distinct().Sum(c => c.Passed ?? 0) : null;
        public int Skipped => Distinct().Sum(c => c.Skipped);
    }

    public static string Serialize(Baseline b)
        => JsonSerializer.Serialize(new { at = b.At, verdict = b.Verdict, checks = b.Checks.Select(c => new { key = c.Key, label = c.Label, status = c.Status, total = c.Total, passed = c.Passed, skipped = c.Skipped }) });

    public static Baseline? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var list = new List<Check>();
            foreach (var x in o["checks"]?.AsArray() ?? new JsonArray())
                if (x is JsonObject c)
                    list.Add(new Check(c["key"]?.GetValue<string>() ?? "", c["label"]?.GetValue<string>() ?? "", c["status"]?.GetValue<string>() ?? "",
                        c["total"]?.GetValue<int>(), c["passed"]?.GetValue<int>(), c["skipped"]?.GetValue<int>() ?? 0));
            return new Baseline(o["at"]?.GetValue<string>() ?? "", o["verdict"]?.GetValue<string>() ?? "", list);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Türkçe iyelik eki: 1'i, 2'si, 3'ü, 10'u ...</summary>
    public static string Poss(int n)
    {
        if (n % 10 == 0)
        {
            var t = n % 100;
            if (n == 0) return "0'ı";
            return n + "'" + (t switch { 0 => "ü", 10 or 30 => "u", 20 or 50 => "si", 40 or 60 or 90 => "ı", 70 or 80 => "i", _ => "ı" });
        }
        return n + "'" + ((n % 10) switch { 1 or 5 or 8 => "i", 2 or 7 => "si", 3 or 4 => "ü", 6 => "sı", _ => "u" });
    }

    private static string Before(Baseline b)
    {
        if (b.Total is { } t && t > 0)
        {
            var failed = Math.Max(0, t - (b.Passed ?? 0) - b.Skipped);
            var parts = new List<string>();
            if (failed > 0) parts.Add($"{t} testten {Poss(failed)} geçmiyordu");
            else parts.Add($"{t} testin hepsi geçiyordu");
            if (b.Skipped > 0) parts.Add($"{b.Skipped} test atlanmıştı");
            return "Başlamadan önce " + string.Join(", ", parts) + ".";
        }
        return b.Verdict == Verdicts.Passed ? "Başlamadan önce kontroller geçiyordu." : b.Verdict == Verdicts.Failed ? "Başlamadan önce kontroller geçmiyordu." : "Başlamadan önce kontroller belirsizdi.";
    }

    private static string Now(int? total, int? passed, int skipped, string verdict)
    {
        if (total is { } t && t > 0)
        {
            var failed = Math.Max(0, t - (passed ?? 0) - skipped);
            string s;
            if (failed > 0) s = $"Şimdi {t} testten {Poss(failed)} geçmiyor.";
            else if (t == 1) s = "Şimdi geçiyor.";
            else if (t == 2) s = "Şimdi 2'si de geçiyor.";
            else s = $"Şimdi {t} testin hepsi geçiyor.";
            if (skipped > 0) s += $" {skipped} test atlanmış.";
            return s;
        }
        return verdict == Verdicts.Passed ? "Şimdi kontroller geçiyor." : verdict == Verdicts.Failed ? "Şimdi kontroller geçmiyor." : "Şimdi kontroller belirsiz.";
    }

    /// <summary>Kart cümlesi: başlangıç ve şimdiki durum. Şimdiki sayılar ölçüt çıktısından, okunamazsa hükümden gelir.</summary>
    public static string Compose(Baseline b, IReadOnlyList<CheckOutcome> outcomes, string verdict)
    {
        int? total = null, passed = null; var skipped = 0;
        // Aynı komut (CmdKey) birden çok ölçütle koşulduysa tek koşu sayılır (anahtarsız ölçütler ayrı sayılır).
        var known = outcomes.Where(o => o.TestsTotal is not null).GroupBy(o => o.CmdKey ?? ("#" + o.CheckId), StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (known.Count > 0) { total = known.Sum(o => o.TestsTotal ?? 0); passed = known.Sum(o => o.TestsPassed ?? 0); skipped = known.Sum(o => o.SkippedTests); }
        return Before(b) + " " + Now(total, passed, skipped, verdict);
    }
}
