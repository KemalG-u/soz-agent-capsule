using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Proof;

/// <summary>
/// Yardımcının "dosya değiştirdim" beyanı ile Söz'ün klasörde bulduğunun yan yana cümlesi. Söyleyen ile kontrol eden ayrı olduğu için ikisi tek cümlede karşılaştırılır.
/// Beyan: Söz'ün kendi yürüttüğü işin akış satırlarından ("2 dosyayı değiştirdi", ayrıntı "değişen: a, b"). Akış hiç yoksa beyan okunamaz (null) ve cümle kurulmaz.
/// Bulgu: iş öncesi ve sonrası klasör parmak izi. Kaç dosya olduğu parmak izinde yoktur, bu yüzden bulgu yalnız var / yok söylenir (sayı uydurulmaz).
/// </summary>
public static class ChangeClaim
{
    private static readonly Regex CountRe = new(@"^(\d+) dosyayı değiştirdi$", RegexSpeed.Compiled);
    private const string OneSuffix = " dosyasını değiştirdi";

    /// <summary>
    /// İşin akış satırlarından beyan edilen dosya sayısı. İşin hiç akış satırı yoksa null (Söz başlatmadı ya da akış okunamadı). Satır var ama dosya değiştirme satırı yoksa 0.
    /// Aynı dosya ayrı satırlarda tekrar anılırsa bir sayılır (ayrıntıdaki adlarla), adı verilmeyen fazlalık sayıya eklenir.
    /// </summary>
    public static int? Read(SqliteConnection c, long jobId)
    {
        var lines = c.Query("SELECT kind, text, detail FROM run_lines WHERE job_id=@j ORDER BY id", r => (kind: r.Str(0), text: r.Str(1) ?? "", detail: r.Str(2)), null, ("@j", jobId));
        if (lines.Count == 0) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extra = 0;
        foreach (var l in lines)
        {
            if (l.kind != "progress") continue;
            int n; string? single = null;
            if (CountRe.Match(l.text) is { Success: true } m) n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            else if (l.text.EndsWith(OneSuffix, StringComparison.Ordinal)) { n = 1; single = l.text[..^OneSuffix.Length]; }
            else continue;
            var listed = new List<string>();
            if (l.detail is { } d && d.StartsWith("değişen: ", StringComparison.Ordinal))
                listed.AddRange(d["değişen: ".Length..].Split(", ", StringSplitOptions.RemoveEmptyEntries));
            else if (single is not null) listed.Add(single);
            foreach (var x in listed) names.Add(x);
            extra += Math.Max(0, n - listed.Count);
        }
        return names.Count + extra;
    }

    /// <summary>
    /// Karşılaştırma cümlesi. claimed: beyan (null = okunamadı), found: Söz klasörde değişiklik buldu mu (null = bilinmiyor), sourceOnly: bulunmadıysa
    /// "yalnız izlenmeyen klasörlere yazıldı" durumu (cümle "izlenen dosyalarda" der). İki taraf da "değişiklik yok" diyorsa ya da bir taraf bilinmiyorsa null (cümle yok).
    /// </summary>
    public static string? Sentence(int? claimed, bool? found, bool ignoredOnly = false)
    {
        if (claimed is null || found is null) return null;
        if (claimed > 0 && found == false)
            return $"Yardımcı {Count(claimed.Value)} değiştirdiğini söyledi. Ben {(ignoredOnly ? "izlenen dosyalarda" : "klasörde")} değişiklik bulamadım.";
        if (claimed > 0 && found == true)
            return $"Yardımcı {Count(claimed.Value)} değiştirdiğini söyledi. Ben de klasörde değişiklik buldum.";
        if (claimed == 0 && found == true)
            return "Yardımcı dosya değiştirdiğini söylemedi. Ben klasörde değişiklik buldum.";
        return null;
    }

    private static string Count(int n) => n == 1 ? "1 dosya" : $"{n} dosya";
}
