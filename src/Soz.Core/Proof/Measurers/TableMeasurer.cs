using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>
/// Tablo mutabakatı (`table-recon`): CSV dosyasında satır sayısı, sütun sayısı, bir sütunun toplamı, boş olmaması gereken sütunlar ve tekil sütun.
/// Ayraç (virgül, noktalı virgül, sekme, |) sezilir; tırnaklı hücre ve hücre içi satır sonu doğru okunur; kodlama sezilir (UTF-8 değilse Windows-1254 varsayılır ve söylenir).
/// Sayılar Türkçe (1.234,56) ve İngilizce (1,234.56) yazımıyla okunur; "1.234" gibi iki anlama gelebilen yazım ve sayı olmayan hücre "emin değil" olur (spec.decimal ile söylenir), tahmin edilmez.
/// Ölçülecek hiçbir şey bulunamazsa (ör. toplanacak sayı yok) "doğrulanamadı" olur, asla "geçti" değil. 32 MB'tan büyük dosya okunmaz.
/// Excel (xlsx) bu sürümde okunmaz: CSV olarak dışa aktarılmış hâli ölçülür.
/// </summary>
public sealed class TableMeasurer : MeasurerBase
{
    private const int MaxRows = 1_000_000;

    public override string Kind => CheckKinds.TableRecon;
    public override string Level => Confidence.A;

    public override string? Validate(JsonObject spec)
    {
        if (string.IsNullOrWhiteSpace(S(spec, "path"))) return "table-recon kontrolü için spec.path gerekli.";
        var hasSum = !string.IsNullOrWhiteSpace(S(spec, "sumColumn"));
        var hasExpected = spec["expectSum"] is not null;
        if (hasSum != hasExpected) return "Toplam kontrolü için hem spec.sumColumn hem spec.expectSum gerekli.";
        var any = L(spec, "expectRows") is not null || L(spec, "minRows") is not null || L(spec, "maxRows") is not null || L(spec, "expectColumns") is not null
                  || hasSum || List(spec, "notEmpty").Count > 0 || !string.IsNullOrWhiteSpace(S(spec, "uniqueColumn"));
        if (!any) return "Tablo kontrolü en az bir koşul içermeli (satır sayısı, sütun sayısı, toplam, boş olmayan sütun ya da tekil sütun).";
        if (L(spec, "minRows") is { } mn && L(spec, "maxRows") is { } mx && mx < mn) return "En çok satır, en az satırdan küçük olamaz.";
        if (S(spec, "decimal") is { } d && d is not ("," or ".")) return "spec.decimal yalnız \",\" ya da \".\" olabilir.";
        if (L(spec, "expectRows") is < 0 || L(spec, "minRows") is < 0 || L(spec, "maxRows") is < 0 || L(spec, "expectColumns") is < 1) return "Satır ve sütun sayıları geçersiz.";
        return SchemaProblem(spec);
    }

    public override string Describe(JsonObject spec) => S(spec, "path") is { } p ? $" (dosya: {p})" : "";

    // ---------- CSV ----------
    internal static char DetectDelimiter(string text)
    {
        char[] cands = { ',', ';', '\t', '|' };
        var counts = cands.ToDictionary(x => x, _ => new List<int>());
        var lines = 0;
        var inQuote = false;
        var cur = cands.ToDictionary(x => x, _ => 0);
        foreach (var ch in text)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (ch == '\n')
            {
                if (cur.Values.Any(v => v > 0)) { foreach (var k in cands) counts[k].Add(cur[k]); lines++; }
                foreach (var k in cands) cur[k] = 0;
                if (lines >= 20) break;
                continue;
            }
            if (cur.ContainsKey(ch)) cur[ch]++;
        }
        if (lines == 0 && cur.Values.Any(v => v > 0)) foreach (var k in cands) counts[k].Add(cur[k]);
        // Her satırda aynı (sıfırdan büyük) sayıda geçen ayraç en güçlüsüdür; yoksa en çok geçen.
        char best = ',';
        double bestScore = -1;
        foreach (var k in cands)
        {
            var l = counts[k];
            if (l.Count == 0 || l.All(x => x == 0)) continue;
            var consistent = l.All(x => x == l[0]) ? 1000 : 0;
            var score = consistent + l.Min() * 10 + l.Average();
            if (score > bestScore) { bestScore = score; best = k; }
        }
        return best;
    }

    internal static List<string[]> ParseCsv(string text, char delim, out bool brokenQuote)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuote = false;
        var cellWasQuoted = false;
        brokenQuote = false;
        void EndCell() { row.Add(cell.ToString()); cell.Clear(); cellWasQuoted = false; }
        void EndRow() { EndCell(); rows.Add(row.ToArray()); row.Clear(); }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuote)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuote = false;
                }
                else cell.Append(ch);
                continue;
            }
            if (ch == '"' && cell.Length == 0 && !cellWasQuoted) { inQuote = true; cellWasQuoted = true; continue; }
            if (ch == delim) { EndCell(); continue; }
            if (ch == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); if (rows.Count > MaxRows) break; continue; }
            if (ch == '\n') { EndRow(); if (rows.Count > MaxRows) break; continue; }
            cell.Append(ch);
        }
        if (inQuote) brokenQuote = true;
        if (cell.Length > 0 || row.Count > 0 || cellWasQuoted) EndRow();
        return rows;
    }

    // ---------- sayı ----------
    /// <summary>Hücreyi sayıya çevirir. ambiguous: "1.234" gibi iki anlama gelebilen yazım (decimalSep verilmemişse).</summary>
    internal static bool TryNumber(string raw, char? decimalSep, out decimal value, out bool ambiguous)
    {
        value = 0; ambiguous = false;
        var s = raw.Trim().Replace('−', '-').Replace("₺", "").Replace("$", "").Replace("€", "").Replace("TL", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "").Replace(" ", "").Replace("'", "");
        if (s.Length == 0) return false;
        var neg = false;
        if (s.StartsWith('-')) { neg = true; s = s[1..]; } else if (s.StartsWith('+')) s = s[1..];
        if (s.Length == 0 || !s.All(ch => char.IsAsciiDigit(ch) || ch is '.' or ',')) return false;
        if (!s.Any(char.IsAsciiDigit)) return false;

        string norm;
        var dots = s.Count(ch => ch == '.');
        var commas = s.Count(ch => ch == ',');
        if (decimalSep == ',') norm = s.Replace(".", "").Replace(',', '.');
        else if (decimalSep == '.') norm = s.Replace(",", "");
        else if (dots > 0 && commas > 0)
        {
            var lastDot = s.LastIndexOf('.'); var lastComma = s.LastIndexOf(',');
            norm = lastComma > lastDot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        }
        else if (commas > 0 || dots > 0)
        {
            var sep = commas > 0 ? ',' : '.';
            var count = Math.Max(commas, dots);
            if (count > 1) norm = s.Replace(sep.ToString(), "");              // 1.234.567 ya da 1,234,567: binlik
            else
            {
                var at = s.IndexOf(sep);
                var lead = s[..at]; var after = s[(at + 1)..];
                if (after.Length == 3 && lead.Length is >= 1 and <= 3 && lead[0] != '0') { ambiguous = true; return false; }
                norm = lead + "." + after;
            }
        }
        else norm = s;
        if (!decimal.TryParse(norm, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (neg) value = -value;
        return true;
    }

    // ---------- ölçüm ----------
    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var rel = S(c.Spec, "path");
        var problem = SafeFiles.Resolve(ctx.Root, rel, out var full);
        if (problem is not null) return Error(c, problem);
        if (!File.Exists(full)) return Failed(c, Directory.Exists(full) ? "Bu bir klasör, dosya değil: " + rel : "Dosya yok: " + rel);
        long length;
        try { length = new FileInfo(full).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Error(c, "Dosya boyutu okunamadı: " + rel); }
        if (length > SafeFiles.MaxTableBytes) return Error(c, $"{rel}: tablo çok büyük ({length / (1024 * 1024)} MB). En çok {SafeFiles.MaxTableBytes / (1024 * 1024)} MB okunur, ölçülemedi.");

        var read = SafeFiles.ReadText(full, SafeFiles.MaxTableBytes);
        if (read.Error is not null) return Error(c, rel + ": " + read.Error);
        var text = read.Text!;
        var note = read.Note is null ? "" : " Not: " + read.Note;

        var expectRows = L(c.Spec, "expectRows");
        var minRows = L(c.Spec, "minRows");
        var maxRows = L(c.Spec, "maxRows");
        var expectCols = L(c.Spec, "expectColumns");
        var hasHeader = B(c.Spec, "hasHeader") ?? true;
        var decSep = S(c.Spec, "decimal") is { Length: 1 } ds ? ds[0] : (char?)null;

        if (text.Trim().Length == 0)
            return expectRows == 0 ? Passed(c, $"{rel}: tablo boş, 0 satır (beklendiği gibi).{note}") : Failed(c, $"{rel}: tablo boş (hiç satır yok).{note}");

        var delimSpec = S(c.Spec, "delimiter");
        var delim = delimSpec is null or "auto" ? DetectDelimiter(text) : delimSpec == "\\t" || delimSpec == "tab" ? '\t' : delimSpec[0];
        var all = ParseCsv(text, delim, out var broken);
        if (broken) return Error(c, $"{rel}: tırnak kapanmamış, tablo okunamadı.");
        if (all.Count > MaxRows) return Error(c, $"{rel}: {MaxRows:N0} satırdan fazlası var, ölçülemedi.");
        all = all.Where(r => r.Any(x => !string.IsNullOrWhiteSpace(x))).ToList();
        string[] header = hasHeader && all.Count > 0 ? all[0] : Array.Empty<string>();
        var data = hasHeader && all.Count > 0 ? all.Skip(1).ToList() : all;
        var cols = hasHeader ? header.Length : (all.Count > 0 ? all.Max(r => r.Length) : 0);
        var firstLine = hasHeader ? 2 : 1; // veri satırının dosyadaki satır numarası (tırnaklı satır sonları ihmal edilir: yaklaşık)

        var fails = new List<string>();
        var oks = new List<string>();
        var measured = 0;

        // satır sayısı
        var rows = data.Count;
        if (expectRows is { } er) { measured++; if (rows == er) oks.Add($"{rows} satır"); else fails.Add($"{rows} satır var (beklenen {er})"); }
        if (minRows is { } mn) { measured++; if (rows >= mn) oks.Add($"{rows} satır (en az {mn})"); else fails.Add($"{rows} satır var (en az {mn} olmalı)"); }
        if (maxRows is { } mx) { measured++; if (rows <= mx) oks.Add($"{rows} satır (en çok {mx})"); else fails.Add($"{rows} satır var (en çok {mx} olmalı)"); }

        // sütun sayısı
        if (expectCols is { } ec)
        {
            measured++;
            var bad = data.Count(r => r.Length != ec);
            if (cols == ec && bad == 0) oks.Add($"{cols} sütun");
            else if (cols != ec) fails.Add($"{cols} sütun var (beklenen {ec})");
            else fails.Add($"{bad} satırın sütun sayısı {ec} değil (bozuk satır olabilir, ilki {firstLine + data.FindIndex(r => r.Length != ec)}. satır)");
        }

        int? ColIndex(string name, out string? err)
        {
            err = null;
            var key = SafeFiles.Norm(name);
            for (var i = 0; i < header.Length; i++) if (SafeFiles.Norm(header[i]) == key) return i;
            if (int.TryParse(name.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Math.Max(cols, 1)) return n - 1;
            err = $"\"{name}\" sütunu yok" + (header.Length > 0 ? $" (bulunan sütunlar: {string.Join(", ", header.Take(10).Select(h => "\"" + h.Trim() + "\""))}{(header.Length > 10 ? " ..." : "")})" : "");
            return null;
        }
        static string Cell(string[] r, int i) => i < r.Length ? r[i] : "";

        // boş olmayan sütunlar
        foreach (var name in List(c.Spec, "notEmpty"))
        {
            var ci = ColIndex(name, out var err);
            if (ci is null) { measured++; fails.Add(err!); continue; }
            if (data.Count == 0) continue; // ölçülecek satır yok: ölçüm sayılmaz
            measured++;
            var empties = data.Select((r, i) => (r, i)).Where(x => string.IsNullOrWhiteSpace(Cell(x.r, ci.Value))).Select(x => firstLine + x.i).ToList();
            if (empties.Count == 0) oks.Add($"\"{name}\" sütunu dolu");
            else fails.Add($"\"{name}\" sütununda {empties.Count} boş hücre var (ilk satırlar: {string.Join(", ", empties.Take(3))})");
        }

        // tekil sütun
        if (S(c.Spec, "uniqueColumn") is { Length: > 0 } uq)
        {
            var ci = ColIndex(uq, out var err);
            if (ci is null) { measured++; fails.Add(err!); }
            else if (data.Count > 0)
            {
                measured++;
                var dups = data.Select(r => SafeFiles.Norm(Cell(r, ci.Value))).Where(x => x.Length > 0).GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (dups.Count == 0) oks.Add($"\"{uq}\" sütunu tekil");
                else fails.Add($"\"{uq}\" sütununda {dups.Count} tekrar eden değer var (ör. \"{dups[0]}\")");
            }
        }

        // toplam
        if (S(c.Spec, "sumColumn") is { Length: > 0 } sc && M(c.Spec, "expectSum") is { } expectSum)
        {
            var ci = ColIndex(sc, out var err);
            if (ci is null) { measured++; fails.Add(err!); }
            else
            {
                decimal sum = 0; var n = 0; var nonNumeric = new List<string>(); var ambiguous = new List<string>();
                for (var i = 0; i < data.Count; i++)
                {
                    var raw = Cell(data[i], ci.Value);
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    if (TryNumber(raw, decSep, out var v, out var amb)) { sum += v; n++; }
                    else if (amb) ambiguous.Add($"{firstLine + i}. satır \"{raw.Trim()}\"");
                    else nonNumeric.Add($"{firstLine + i}. satır \"{(raw.Trim().Length > 20 ? raw.Trim()[..20] + "…" : raw.Trim())}\"");
                }
                if (ambiguous.Count > 0)
                    return Error(c, $"{rel}: \"{sc}\" sütununda sayı yazımı belirsiz ({string.Join(", ", ambiguous.Take(3))}): nokta binlik mi ondalık mı? spec.decimal alanıyla söyle. Tahmin etmedim.");
                if (nonNumeric.Count > 0)
                    return Error(c, $"{rel}: \"{sc}\" sütununda sayı olmayan hücre var ({string.Join(", ", nonNumeric.Take(3))}), {nonNumeric.Count} hücre. Toplam hesaplanamadı.");
                if (n == 0) { /* toplanacak sayı yok: ölçüm sayılmaz */ }
                else
                {
                    measured++;
                    var tol = M(c.Spec, "tolerance") ?? 0m;
                    if (Math.Abs(sum - expectSum) <= tol) oks.Add($"\"{sc}\" toplamı {sum.ToString(CultureInfo.InvariantCulture)}");
                    else fails.Add($"\"{sc}\" toplamı {sum.ToString(CultureInfo.InvariantCulture)} (beklenen {expectSum.ToString(CultureInfo.InvariantCulture)}{(tol > 0 ? $", ±{tol.ToString(CultureInfo.InvariantCulture)}" : "")})");
                }
            }
        }

        if (fails.Count > 0) return Failed(c, $"{rel}: {string.Join(", ", fails)}.{note}");
        if (measured == 0) return NothingMeasured(c, $"{rel}: ölçülecek bir şey bulunamadı (tabloda {rows} veri satırı var, toplanacak sayı ya da bakılacak hücre yok).");
        return Passed(c, $"{rel}: {rows} satır, {cols} sütun: {string.Join(", ", oks)}.{note}");
    }

    public override (string label, JsonObject spec) FromTemplate(string templateId, IReadOnlyDictionary<string, string> values, string? root)
    {
        var p = TemplateValues.Path(values, "path", root);
        switch (templateId)
        {
            case "table-sum":
            {
                var col = TemplateValues.Get(values, "column");
                var total = TemplateValues.Number(values, "total", "Beklenen toplam") ?? throw new SozException(400, "Beklenen toplam boş olamaz.");
                return ($"Tabloda \"{col}\" sütununun toplamı {total.ToString(CultureInfo.InvariantCulture)} olsun ({p})",
                    new JsonObject { ["path"] = p, ["sumColumn"] = col, ["expectSum"] = total, ["template"] = templateId });
            }
            case "table-filled":
            {
                var col = TemplateValues.Get(values, "column");
                return ($"Tabloda \"{col}\" sütunu boş kalmasın ({p})", new JsonObject { ["path"] = p, ["notEmpty"] = new JsonArray(col), ["template"] = templateId });
            }
            default:
            {
                var rows = TemplateValues.Long(values, "rows", "Satır sayısı") ?? throw new SozException(400, "Satır sayısı boş olamaz.");
                return ($"Tabloda {rows} satır olsun ({p})", new JsonObject { ["path"] = p, ["expectRows"] = rows, ["template"] = templateId });
            }
        }
    }
}
