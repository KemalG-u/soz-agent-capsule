using System.Text;

namespace Soz.Core.Export;

/// <summary>
/// AGENTS.md / CLAUDE.md içine yönetilen blok yazar: &lt;!-- soz:basla --&gt; … &lt;!-- soz:bitir --&gt;.
/// Blok dışındaki içeriğe dokunmaz; dosya yoksa oluşturur; paket boşsa blok kaldırılır.
/// </summary>
public static class AgentsFileExporter
{
    public const string Begin = "<!-- soz:basla -->";
    public const string End = "<!-- soz:bitir -->";

    public static string WriteBlock(string filePath, string packetText)
    {
        var existing = File.Exists(filePath) ? File.ReadAllText(filePath) : "";
        var updated = Merge(existing, packetText);
        if (updated != existing)
        {
            var tmp = filePath + ".soz.tmp";
            File.WriteAllText(tmp, updated, new UTF8Encoding(false));
            File.Move(tmp, filePath, overwrite: true);
        }
        return filePath;
    }

    /// <summary>Saf işlev: var olan metne bloğu yerleştirir/günceller/kaldırır.</summary>
    public static string Merge(string existing, string packetText)
    {
        var nl = existing.Contains("\r\n") ? "\r\n" : "\n";
        var body = packetText.Replace("\r\n", "\n").Replace("\n", nl).TrimEnd();
        var block = Begin + nl + body + nl + End;
        var b = existing.IndexOf(Begin, StringComparison.Ordinal);
        var e = b >= 0 ? existing.IndexOf(End, b, StringComparison.Ordinal) : -1;
        if (b >= 0 && e > b)
        {
            var after = e + End.Length;
            if (body.Length == 0)
            {
                // Bloğu ve onu çevreleyen tek boş satırı kaldır.
                var before = existing[..b].TrimEnd('\r', '\n');
                var rest = existing[after..].TrimStart('\r', '\n');
                return before.Length == 0 ? rest : rest.Length == 0 ? before + nl : before + nl + nl + rest;
            }
            return existing[..b] + block + existing[after..];
        }
        if (body.Length == 0) return existing;
        if (existing.Length == 0) return block + nl;
        var sep = existing.EndsWith(nl + nl) ? "" : existing.EndsWith(nl) ? nl : nl + nl;
        return existing + sep + block + nl;
    }
}
