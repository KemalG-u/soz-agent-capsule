using System.Text;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Text;

namespace Soz.Core.Packet;

public sealed record PacketResult(string Text, int Chars, int DecisionCount);

/// <summary>
/// Proje paketi (sözleşme 3.5): onaylı kararlar; önce sabitlenenler, sonra genel (*), sonra proje; tür sırası rule, ban, decision, preference.
/// Karakter bütçesini aşan madde eklenmez. Onaylı karar yoksa paket boş dizedir.
/// </summary>
public static class PacketBuilder
{
    private static readonly string[] TypeOrder = { "rule", "ban", "decision", "preference" };

    public static PacketResult Build(SqliteConnection c, long? projectId, int budget, string language = "tr")
    {
        var star = c.ScalarLong("SELECT id FROM projects WHERE key='*'");
        var name = projectId is null ? Strings.Get(language, "packet.general") :
            (c.Scalar("SELECT name FROM projects WHERE id=@p", null, ("@p", projectId)) as string ?? "?");
        if (projectId == star) name = Strings.Get(language, "packet.general");
        var rows = c.Query("""
            SELECT id, project_id, type, text, pinned FROM decisions
            WHERE status='approved' AND (project_id=@star OR project_id=@p)
            """, r => (id: r.GetInt64(0), pid: r.Long(1), type: r.Str(2) ?? "rule", text: (r.Str(3) ?? "").Trim(), pinned: r.Long(4) == 1),
            null, ("@star", star), ("@p", projectId ?? star));
        rows = rows.Where(r => !SecretFilter.ContainsSecret(r.text)).ToList(); // sır içeren karar pakete girmez
        if (rows.Count == 0) return new PacketResult("", 0, 0);

        var ordered = rows
            .OrderBy(r => r.pinned ? 0 : 1)
            .ThenBy(r => r.pid == star ? 0 : 1)
            .ThenBy(r => r.id)
            .ToList();

        var header = Strings.Get(language, "packet.header").Replace("{project}", name);
        // Bütçeye göre seçim: öncelik sırasıyla, her madde "- metin\n" kadar yer tutar; bölüm başlığı ilk maddede eklenir.
        var chosen = new List<(string type, string text)>();
        var used = header.Length + 1;
        var sectionsUsed = new HashSet<string>();
        foreach (var r in ordered)
        {
            var t = r.type == "correction" ? "rule" : (Array.IndexOf(TypeOrder, r.type) < 0 ? "rule" : r.type);
            var line = "- " + r.text.Replace('\n', ' ') + "\n";
            var cost = line.Length + (sectionsUsed.Contains(t) ? 0 : Strings.Get(language, "packet.section." + t).Length + 1);
            if (used + cost > budget) continue;
            used += cost;
            sectionsUsed.Add(t);
            chosen.Add((t, r.text.Replace('\n', ' ')));
        }
        if (chosen.Count == 0) return new PacketResult("", 0, 0);

        var sb = new StringBuilder();
        sb.Append(header).Append('\n');
        foreach (var t in TypeOrder)
        {
            var items = chosen.Where(x => x.type == t).ToList();
            if (items.Count == 0) continue;
            sb.Append(Strings.Get(language, "packet.section." + t)).Append('\n');
            foreach (var it in items) sb.Append("- ").Append(it.text).Append('\n');
        }
        var text = sb.ToString().TrimEnd('\n');
        return new PacketResult(text, text.Length, chosen.Count);
    }
}
