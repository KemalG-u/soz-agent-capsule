using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soz.Core.Proof;

/// <summary>
/// Tarama bağlamı (denetim 4, A): onaylı ölçüt komutları ve bu komutların çağırdığı package.json betikleri.
/// Bir dosyanın "sınamayı etkileyip etkilemediği" buna bakılarak ayrılır: ölçüt eslint'i çağırmıyorsa eslint ayarı yalnız bilgi notudur, çağırıyorsa kuşkuludur.
/// Hata sızmaz; okunamayan package.json boş bağlam verir (kuşkulu yön: yalnız bilgi sınıfları büyümez).
/// </summary>
public sealed class ScanContext
{
    public static readonly ScanContext Empty = new();

    /// <summary>Komutların ya da kullanılan betiklerin adını andığı proje dosyaları (köke göre, / ile). Bunlar kuşkulu sayılır.</summary>
    public HashSet<string> Referenced { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Onaylı sınama kümesinin üyesi olarak bilinen yollar (onaylı tabandan): içe aktarma grafiği bugünkü hâlde farklı bir sonuç verse bile (ör. ajan bir kaynak dosyadan yardımcıyı içe aktardı)
    /// bu dosyalar izlenir ve test sınıfında kalır. Böylece onaylı bir yardımcı, ajan işini değiştirerek kümeden çıkarılamaz.
    /// </summary>
    public HashSet<string> Pinned { get; init; } = new(StringComparer.Ordinal);

    public ScanContext WithPinned(IEnumerable<string> paths) => new()
    {
        Referenced = Referenced, UsedScripts = UsedScripts, Text = Text, Pinned = new HashSet<string>(paths, StringComparer.Ordinal),
    };

    /// <summary>Ölçütlerin kullandığı package.json betik adları (pre/post ve iç içe çağrılar dahil).</summary>
    public HashSet<string> UsedScripts { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Ölçüt komutları ile kullanılan betik gövdelerinin birleşik metni (küçük harf).</summary>
    public string Text { get; init; } = "";

    public bool RunsTool(string tool) => Text.Length > 0 && Regex.IsMatch(Text, @"(?<![\w-])" + Regex.Escape(tool) + @"(?![\w-])", RegexOptions.CultureInvariant);

    /// <summary>Komut dist/ ya da build/ içinden bir şey çalıştırıyor ya da yüklüyor mu?</summary>
    public bool MentionsBuildOutput => Text.Length > 0 && Regex.IsMatch(Text, @"(?<![\w-])(?:dist|build)[/\\]", RegexOptions.CultureInvariant);

    /// <summary>Komut kendisi derleme yapıyor mu? Öyleyse dist/build içeriği sınamadan önce yeniden üretilir, parmak izine gerek kalmaz.</summary>
    public bool Rebuilds => Text.Length > 0 && Regex.IsMatch(Text, @"(?<![\w-])(?:build|compile|tsc|esbuild|rollup|webpack|parcel|tsup)(?![\w-])", RegexOptions.CultureInvariant);

    private static readonly Regex SegmentSplit = new(@"&&|\|\||;|\||&", RegexSpeed.Compiled);
    private static readonly Regex TokenSplit = new(@"[\s""'`()<>]+", RegexSpeed.Compiled);
    private static readonly HashSet<string> Runners = new(StringComparer.OrdinalIgnoreCase) { "npm", "yarn", "pnpm", "bun" };
    private static readonly HashSet<string> NpmLifecycle = new(StringComparer.OrdinalIgnoreCase) { "test", "t", "tst", "start", "stop", "restart" };

    private static string NormTool(string tok)
    {
        var t = tok.Replace('\\', '/');
        var i = t.LastIndexOf('/');
        if (i >= 0) t = t[(i + 1)..];
        foreach (var ext in new[] { ".cmd", ".exe", ".bat", ".ps1" })
            if (t.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { t = t[..^ext.Length]; break; }
        return t;
    }

    /// <summary>Komut metninin çağırdığı package.json betik adları.</summary>
    internal static IEnumerable<string> ScriptCalls(string text, IReadOnlyDictionary<string, string> scripts)
    {
        foreach (var seg in SegmentSplit.Split(text))
        {
            var toks = TokenSplit.Split(seg).Where(x => x.Length > 0).ToList();
            for (var i = 0; i < toks.Count; i++)
            {
                var tool = NormTool(toks[i]);
                if (!Runners.Contains(tool)) continue;
                var rest = toks.Skip(i + 1).Where(x => !x.StartsWith('-')).ToList();
                if (rest.Count == 0) break;
                var first = rest[0];
                if (first is "run" or "run-script" or "rum" or "urn")
                {
                    if (rest.Count > 1) yield return rest[1];
                }
                else if (tool.Equals("npm", StringComparison.OrdinalIgnoreCase))
                {
                    if (NpmLifecycle.Contains(first)) yield return first is "t" or "tst" ? "test" : first;
                }
                else
                {
                    // yarn test, pnpm lint, bun test: betik adıdır (yalnız package.json'da varsa; yoksa yerleşik alt komut)
                    foreach (var r in rest) if (scripts.ContainsKey(r)) { yield return r; break; }
                    if (tool.Equals("yarn", StringComparison.OrdinalIgnoreCase) || tool.Equals("pnpm", StringComparison.OrdinalIgnoreCase) || tool.Equals("bun", StringComparison.OrdinalIgnoreCase))
                        if (first is "test" or "start") yield return first;
                }
                break;
            }
        }
    }

    internal static Dictionary<string, string> ReadScripts(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var pkg = Path.Combine(root, "package.json");
            if (File.Exists(pkg) && JsonNode.Parse(File.ReadAllText(pkg), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject p
                && p["scripts"] is JsonObject sc)
                foreach (var kv in sc) if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s)) map[kv.Key] = s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return map;
    }

    /// <summary>Onaylı komut ölçütlerinden bağlam kurar. Hata sızmaz.</summary>
    public static ScanContext Build(string root, IEnumerable<string> commands)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var texts = new List<string>(commands);
        try
        {
            var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
            var scripts = ReadScripts(rootFull);
            void Use(string name, int depth)
            {
                if (depth > 8 || !scripts.TryGetValue(name, out var body) || !used.Add(name)) return;
                texts.Add(body);
                foreach (var inner in ScriptCalls(body, scripts)) Use(inner, depth + 1);
                Use("pre" + name, depth + 1);
                Use("post" + name, depth + 1);
            }
            foreach (var cmd in commands.ToList())
                foreach (var name in ScriptCalls(cmd, scripts)) Use(name, 0);
            foreach (var t in texts)
                foreach (var raw in Regex.Split(t, @"[\s""'`;&|<>()]+"))
                {
                    var tok = raw.Trim();
                    if (tok.Length < 2 || tok.StartsWith('-')) continue;
                    var eq = tok.IndexOf('=');
                    if (eq >= 0) tok = tok[(eq + 1)..];
                    tok = tok.Replace('\\', '/');
                    while (tok.StartsWith("./")) tok = tok[2..];
                    if (tok.Length == 0 || tok.Contains("..") || Path.IsPathRooted(tok)) continue;
                    var full = Path.GetFullPath(Path.Combine(rootFull, tok));
                    if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                    referenced.Add(full[(rootFull.Length + 1)..].Replace('\\', '/'));
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new ScanContext { Referenced = referenced, UsedScripts = used, Text = string.Join("\n", texts).ToLowerInvariant() };
    }
}
