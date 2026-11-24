using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>
/// Dosyada başlık ya da yazı var (`doc-heading`). Metin dosyaları (md, txt, html...) ve Word (docx) okunur.
/// Başlık: Markdown (# ve alt çizgili), HTML (h1-h6) ya da Word başlık stili; kod bloğu içindeki "# yorum" başlık sayılmaz.
/// Karşılaştırma Türkçe harf katlamalıdır (İ/ı, ş, ç...), görünmez ve ayrılmaz boşluklar yok sayılır. Kodlama sezilir (UTF-8 değilse Windows-1254 varsayılır ve söylenir).
/// Okunamayan dosya (ikili, bozuk, çok büyük ve bulunamadı) "ölçülemedi" olur, asla "geçti" değil. 8 MB'tan büyük dosyada yalnız ilk 8 MB okunur.
/// Yanlış geçme yolu: başlık var ama altındaki içerik boş ya da yanlış olabilir (başlığın altına bakmaz).
/// </summary>
public sealed class DocHeadingMeasurer : MeasurerBase
{
    public override string Kind => CheckKinds.DocHeading;
    public override string Level => Confidence.B;

    public override string? Validate(JsonObject spec)
    {
        if (string.IsNullOrWhiteSpace(S(spec, "path"))) return "doc-heading kontrolü için spec.path gerekli.";
        if (string.IsNullOrWhiteSpace(S(spec, "heading")) && string.IsNullOrWhiteSpace(S(spec, "text"))) return "doc-heading kontrolü için spec.heading (başlık) ya da spec.text (yazı) gerekli.";
        if (L(spec, "level") is { } lv && (lv < 1 || lv > 6)) return "Başlık düzeyi 1 ile 6 arasında olmalı.";
        return SchemaProblem(spec);
    }

    public override string Describe(JsonObject spec) => S(spec, "path") is { } p ? $" (dosya: {p})" : "";

    private sealed record Heading(string Text, int Level, int Line);

    private static readonly Regex Atx = new(@"^ {0,3}(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$", RegexSpeed.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Setext = new(@"^ {0,3}(=+|-+)[ \t]*$", RegexSpeed.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Html = new(@"<h([1-6])\b[^>]*>(.*?)</h\1\s*>", RegexSpeed.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
    private static readonly Regex Tags = new(@"<[^>]*>", RegexSpeed.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)", RegexSpeed.Compiled);

    private static string Clean(string t)
    {
        t = Link.Replace(t, "$1");
        t = Tags.Replace(t, "");
        t = t.Replace("**", "").Replace("__", "").Replace("`", "").Replace("*", "");
        return System.Net.WebUtility.HtmlDecode(t).Trim();
    }

    private static List<Heading> Find(string text)
    {
        var res = new List<Heading>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var fenceChar = '\0';
        var start = 0;
        // YAML ön bilgi bloğu (--- ... ---) başlık değildir.
        if (lines.Length > 1 && lines[0].Trim() == "---")
        {
            var close = Array.FindIndex(lines, 1, l => l.Trim() == "---" || l.Trim() == "...");
            if (close > 0) start = close + 1;
        }
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.TrimStart();
            if (t.StartsWith("```") || t.StartsWith("~~~"))
            {
                var ch = t[0];
                if (fenceChar == '\0') fenceChar = ch; else if (fenceChar == ch) fenceChar = '\0';
                continue;
            }
            if (fenceChar != '\0') continue;
            var m = Atx.Match(line);
            if (m.Success) { res.Add(new Heading(Clean(m.Groups[2].Value), m.Groups[1].Length, i + 1)); continue; }
            if (i + 1 < lines.Length && line.Trim().Length > 0 && !line.TrimStart().StartsWith("-") && !line.TrimStart().StartsWith("*") && !line.TrimStart().StartsWith(">")
                && Setext.Match(lines[i + 1]) is { Success: true } sm && sm.Groups[1].Length >= 2)
                res.Add(new Heading(Clean(line), sm.Groups[1].Value[0] == '=' ? 1 : 2, i + 1));
        }
        try
        {
            foreach (Match h in Html.Matches(text))
            {
                var line = 1 + text.AsSpan(0, h.Index).Count('\n');
                res.Add(new Heading(Clean(h.Groups[2].Value), h.Groups[1].Value[0] - '0', line));
            }
        }
        catch (RegexMatchTimeoutException) { /* HTML başlık taraması zaman aşımına uğradı: yalnız Markdown başlıkları sayılır */ }
        return res;
    }

    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var rel = S(c.Spec, "path");
        var problem = SafeFiles.Resolve(ctx.Root, rel, out var full);
        if (problem is not null) return Error(c, problem);
        if (!File.Exists(full)) return Failed(c, Directory.Exists(full) ? "Bu bir klasör, dosya değil: " + rel : "Dosya yok: " + rel);

        var wantHeading = S(c.Spec, "heading");
        var wantText = S(c.Spec, "text");
        var wantLevel = (int?)L(c.Spec, "level");

        List<Heading> headings;
        string body;
        bool truncated = false;
        string? note = null;
        if (full.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            var paras = SafeFiles.ReadDocx(full, out var derr);
            if (paras is null) return Error(c, derr ?? "Word belgesi okunamadı.");
            headings = paras.Select((p, i) => (p, i)).Where(x => x.p.level > 0).Select(x => new Heading(x.p.text.Trim(), x.p.level, 0)).ToList();
            body = string.Join("\n", paras.Select(p => p.text));
        }
        else
        {
            var info = new FileInfo(full);
            if (info.Length == 0) return Failed(c, "Dosya boş (0 bayt): " + rel);
            var read = SafeFiles.ReadText(full);
            if (read.Error is not null) return Error(c, rel + ": " + read.Error);
            body = read.Text!; truncated = read.Truncated; note = read.Note;
            headings = Find(body);
        }
        if (body.Trim().Length == 0) return Failed(c, "Dosyada hiç yazı yok: " + rel);

        var found = new List<string>();
        var missing = new List<string>();
        Heading? hit = null;
        if (!string.IsNullOrWhiteSpace(wantHeading))
        {
            var key = SafeFiles.Norm(wantHeading);
            hit = headings.FirstOrDefault(h => (wantLevel is null || h.Level == wantLevel) && SafeFiles.Norm(h.Text) == key);
            if (hit is not null) found.Add($"başlık \"{hit.Text}\" bulundu (düzey {hit.Level}{(hit.Line > 0 ? $", {hit.Line}. satır" : "")})");
            else
            {
                var existing = headings.Take(8).Select(h => "\"" + (h.Text.Length > 40 ? h.Text[..40] + "…" : h.Text) + "\"").ToList();
                missing.Add($"\"{wantHeading}\" başlığı yok" + (wantLevel is null ? "" : $" (düzey {wantLevel})") + (existing.Count > 0 ? $". Bulunan başlıklar: {string.Join(", ", existing)}{(headings.Count > 8 ? " ..." : "")}" : ". Dosyada hiç başlık bulunamadı"));
            }
        }
        if (!string.IsNullOrWhiteSpace(wantText))
        {
            if (SafeFiles.Norm(body).Contains(SafeFiles.Norm(wantText), StringComparison.Ordinal)) found.Add($"yazı \"{wantText}\" bulundu");
            else missing.Add($"içinde \"{wantText}\" yazısı yok");
        }

        if (missing.Count == 0)
            return Passed(c, $"{rel}: {string.Join(", ", found)}." + (note is null ? "" : " Not: " + note));
        // Dosyanın yalnız bir kısmı okunduysa "yok" kesin değildir: ölçülemedi.
        if (truncated)
            return Error(c, $"{rel}: dosya çok büyük, yalnız ilk {SafeFiles.MaxTextBytes / (1024 * 1024)} MB okundu ve aranan bulunamadı. Kesin söyleyemem.");
        return Failed(c, $"{rel}: {string.Join(", ", missing)}." + (note is null ? "" : " Not: " + note));
    }

    public override (string label, JsonObject spec) FromTemplate(string templateId, IReadOnlyDictionary<string, string> values, string? root)
    {
        var p = TemplateValues.Path(values, "path", root);
        if (templateId == "doc-text")
        {
            var x = TemplateValues.Get(values, "text");
            return ($"Belgede şu yazı geçsin: \"{x}\" ({p})", new JsonObject { ["path"] = p, ["text"] = x, ["template"] = templateId });
        }
        var h = TemplateValues.Get(values, "heading");
        return ($"Belgede şu başlık olsun: \"{h}\" ({p})", new JsonObject { ["path"] = p, ["heading"] = h, ["template"] = templateId });
    }
}
