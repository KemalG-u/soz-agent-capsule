using System.Globalization;
using System.Text;

namespace Soz.Core.Profile;

/// <summary>
/// Başlığı olmayan yardımcı (alt) oturumlara anlamlı ad verir: ana oturumun başlığı ve kardeşler arasındaki sıra
/// ("Bitki listesi (yardımcı 2)"). Ana oturumun başlığı yoksa klasör adı, o da yoksa "Yardımcı pencere 2". Yalnız gösterim adıdır.
/// </summary>
public static class SubSessionTitles
{
    private const int MaxBaseRunes = 60;
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
        { "system32", "windows", "desktop", "documents", "users", "temp", "tmp", "masaüstü", "belgeler" };

    /// <param name="rows">Adı doldurulacak oturumlar.</param>
    /// <param name="context">Ana oturumları ve kardeşleri içeren ek kayıtlar (örn. sınırlı bir listeleme için). Yoksa rows kendisi kullanılır.</param>
    public static List<SessionMetadata> Fill(IReadOnlyList<SessionMetadata> rows, IReadOnlyList<SessionMetadata>? context = null)
    {
        var all = context is null ? rows : rows.Concat(context).ToList();
        var byId = new Dictionary<(string Tool, string Id), SessionMetadata>();
        foreach (var r in all) byId.TryAdd((r.Tool, r.Id), r);

        var ordinal = new Dictionary<(string Tool, string Id), int>();
        foreach (var group in byId.Values.Where(r => !string.IsNullOrWhiteSpace(r.ParentId)).GroupBy(r => (r.Tool, Parent: r.ParentId!.Trim())))
        {
            var n = 0;
            foreach (var r in group.OrderBy(r => r.StartedAt ?? r.LastActivity ?? DateTime.MaxValue).ThenBy(r => r.Id, StringComparer.Ordinal))
                ordinal[(r.Tool, r.Id)] = ++n;
        }

        var output = new List<SessionMetadata>(rows.Count);
        foreach (var r in rows)
        {
            if (!string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.ParentId) || !ordinal.TryGetValue((r.Tool, r.Id), out var number))
            { output.Add(r); continue; }
            byId.TryGetValue((r.Tool, r.ParentId!.Trim()), out var parent);
            var baseText = Shorten(parent?.Title) ?? FolderName(parent?.Cwd) ?? FolderName(r.Cwd);
            var n = number.ToString(CultureInfo.InvariantCulture);
            output.Add(r with { Title = baseText is null ? "Yardımcı pencere " + n : baseText + " (yardımcı " + n + ")" });
        }
        return output;
    }

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var runes = text.Trim().EnumerateRunes().ToArray();
        if (runes.Length <= MaxBaseRunes) return text.Trim();
        var sb = new StringBuilder();
        foreach (var r in runes.Take(MaxBaseRunes)) sb.Append(r.ToString());
        return sb.ToString().TrimEnd() + "...";
    }

    private static string? FolderName(string? cwd)
    {
        var clean = ProfileSources.CleanPath(cwd);
        if (string.IsNullOrWhiteSpace(clean) || ProfileSources.IsDriveRoot(clean)) return null;
        var leaf = Path.GetFileName(clean.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(leaf) || GenericFolders.Contains(leaf) ? null : leaf;
    }
}
