using System.Text.RegularExpressions;
using Soz.Core.Restore;

namespace Soz.Core.Proof;

/// <summary>
/// YARDIMCI DOSYA BULUCU (K1, yapısal). Onaylı testlerin içe aktardığı dosyalardan, test OLMAYAN hiçbir kaynak dosyanın içe aktarmadığı
/// ve iddia kurabilen (test ya da iddia kütüphanesi çağıran, doğrudan ya da zincirle) olanlar "yalnız testin kullandığı destek dosyası"dır:
/// adı ne olursa olsun, nerede dururlarsa dursunlar onaylı kümeye girerler. Test verisi olarak okunan json dosyaları da yalnız testler içe aktarıyorsa girer.
/// Bilinen sınırlar: hem test hem kaynak tarafından kullanılan dosya kaynaktır; iddia kütüphanesi çağırmayan saf yardımcı (hesap, veri üreten) kaynak sayılır;
/// sabit olmayan içe aktarma çözülemez (o test için "tam belirlenemedi" notu).
/// </summary>
public static class SupportFinder
{
    public const int MaxFileBytes = 512 * 1024;
    public const long MaxTotalBytes = 48L * 1024 * 1024;
    public const int MaxPartialChecks = 500;

    public sealed record Result(HashSet<string> Support, List<string> Partial);

    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".txt", ".csv", ".yml", ".yaml", ".xml", ".snap", ".html", ".md" };
    private static readonly HashSet<string> LockFiles = new(StringComparer.OrdinalIgnoreCase)
        { "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock", "bun.lock", "bun.lockb", "cargo.lock", "poetry.lock", "pipfile.lock", "composer.lock", "gemfile.lock", "go.sum" };
    private static readonly Regex Literal = new(@"(?<q>['""`])(?<s>[^'""`\r\n]*)\k<q>", RegexOptions.CultureInvariant);
    private static readonly Regex Join = new(@"\b(?:path\s*\.\s*)?(?:join|resolve)\s*\((?<args>[^()\r\n]*)\)", RegexOptions.CultureInvariant);

    /// <summary>Kaynak, sır, genel belge, paket ve kilit dosyası bu yolla yargıç olmaz.</summary>
    public static bool IsDataFile(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        var name = parts[^1];
        return DataExtensions.Contains(Path.GetExtension(name)) &&
            !name.Equals("package.json", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("README", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) &&
            !LockFiles.Contains(name) &&
            !SirKutusu.IsSecretFile(path) &&
            !parts.SkipLast(1).Any(p => Exclusions.Skip.Contains(p) || Exclusions.DepRoots.Contains(p) || Exclusions.Build.Contains(p));
    }

    private static string? DataPath(string dir, string spec)
    {
        spec = spec.Replace('\\', '/');
        if (spec.StartsWith('/') || spec.Contains(':') || spec.Contains("${", StringComparison.Ordinal)) return null;
        var parts = new List<string>();
        foreach (var part in (dir + "/" + spec).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        var path = string.Join('/', parts);
        return IsDataFile(path) ? path : null;
    }

    private static IEnumerable<string> DataReferences(string rel, string text, Func<string, bool> exists, IReadOnlyList<string>? candidateDataFiles)
    {
        var slash = rel.LastIndexOf('/');
        var dir = slash < 0 ? "" : rel[..slash];
        var dynamicSuffixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Join.Matches(text))
        {
            var args = m.Groups["args"].Value;
            var rest = Literal.Replace(args, "").Replace("__dirname", "").Replace(",", "");
            if (string.IsNullOrWhiteSpace(rest))
            {
                var spec = string.Join('/', Literal.Matches(args).Select(x => x.Groups["s"].Value));
                var p = DataPath(args.Contains("__dirname", StringComparison.Ordinal) ? dir : "", spec);
                if (p is not null && exists(p)) { yield return p; continue; }
                if (!string.IsNullOrEmpty(spec)) dynamicSuffixes.Add(spec);
                continue;
            }
            // K13: path.join(kok, 'veri', 'beklenen.json') veya path.join(degisken, 'beklenen.json')
            var tailSegs = ExtractTrailingLiteralSegments(args);
            if (tailSegs is not null) dynamicSuffixes.Add(tailSegs);
        }

        foreach (Match m in Literal.Matches(text))
        {
            var spec = m.Groups["s"].Value;
            var matchedDirect = false;
            foreach (var basis in new[] { dir, "" }.Distinct())
            {
                if (DataPath(basis, spec) is { } p && exists(p))
                {
                    matchedDirect = true;
                    yield return p;
                }
            }
            if (!matchedDirect)
            {
                var suffix = ExtractConstantDataSuffix(spec);
                if (suffix is not null) dynamicSuffixes.Add(suffix);
            }
        }

        if (candidateDataFiles is { Count: > 0 } && dynamicSuffixes.Count > 0)
        {
            foreach (var rawSuffix in dynamicSuffixes)
            {
                var clean = CleanSuffix(rawSuffix);
                if (clean is null || !IsDataFile(clean)) continue;
                var hits = candidateDataFiles
                    .Where(c => c.Equals(clean, StringComparison.Ordinal) || c.EndsWith("/" + clean, StringComparison.Ordinal))
                    .Take(12)
                    .ToList();
                // Çok genel dosya adı yüzlerce klasörde çıkmasın; makul sayıda eşleşmeyi destek kümesine al.
                if (hits.Count is > 0 and <= 10)
                    foreach (var h in hits) yield return h;
            }
        }
    }

    private static string? ExtractTrailingLiteralSegments(string args)
    {
        var parts = args.Split(',');
        var trailing = new List<string>();
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var t = parts[i].Trim();
            var m = Literal.Match(t);
            if (m.Success && m.Index == 0 && m.Length == t.Length)
            {
                var seg = m.Groups["s"].Value.Trim('/', '\\');
                if (seg.Length > 0 && !seg.Contains("${", StringComparison.Ordinal) && seg is not "." and not "..")
                    trailing.Insert(0, seg);
                else break;
            }
            else break;
        }
        return trailing.Count == 0 ? null : string.Join('/', trailing);
    }

    private static string? ExtractConstantDataSuffix(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var s = spec.Replace('\\', '/');
        var interp = s.LastIndexOf('}');
        if (interp >= 0) s = s[(interp + 1)..];
        s = s.TrimStart('.', '/');
        return s.Length > 0 && IsDataFile(s) ? s : null;
    }

    private static string? CleanSuffix(string raw)
    {
        var parts = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>();
        foreach (var p in parts)
        {
            if (p == ".") continue;
            if (p == "..") { if (clean.Count > 0) clean.RemoveAt(clean.Count - 1); continue; }
            if (p.Contains(':') || p.Contains("${", StringComparison.Ordinal)) return null;
            clean.Add(p);
        }
        return clean.Count == 0 ? null : string.Join('/', clean);
    }

    /// <param name="sources">Projedeki bütün dil dosyaları (köke göre yol, tam yol, boyut).</param>
    /// <param name="roots">Yargıç olan dosyalar: onaylı testler, koşucu ayarları, ölçütün çağırdığı dosyalar (yalnız dil dosyaları içe aktarma kaynağı olabilir).</param>
    /// <param name="isTestRoot">Bu kök bir test dosyası mı (ayar dosyaları "tam belirlenemedi" notu üretmez)?</param>
    /// <param name="exists">Köke göre yol dosya mı?</param>
    /// <param name="candidateDataFiles">K13: proje ağacındaki aday veri dosyaları (köke göre yol).</param>
    public static Result Find(IReadOnlyList<(string rel, string full, long len)> sources, IReadOnlyCollection<string> roots, Func<string, bool> isTestRoot, Func<string, bool> exists, IReadOnlyList<string>? candidateDataFiles = null)
    {
        var support = new HashSet<string>(StringComparer.Ordinal);
        var partial = new List<string>();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var info = new Dictionary<string, (HashSet<string> imports, bool fw, bool unres, IImportResolver res)>(StringComparer.Ordinal);
        long total = 0; var tooBig = false;
        foreach (var (rel, full, len) in sources)
        {
            var res = RunnerDefs.ResolverFor(rel);
            if (res is null) continue;
            if (len > MaxFileBytes) continue;
            total += len;
            if (total > MaxTotalBytes) { tooBig = true; break; }
            string text;
            try { text = File.ReadAllText(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            texts[rel] = text;
            var ii = res.Scan(rel, text);
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in ii.Relative) if (res.Resolve(rel, spec, exists) is { } target) set.Add(target);
            info[rel] = (set, ii.UsesFramework, ii.Unresolved, res);
        }
        if (tooBig) { partial.Add("*"); return new Result(support, partial); }

        var rootSet = new HashSet<string>(roots, StringComparer.Ordinal);
        var rootJs = rootSet.Where(info.ContainsKey).ToList();
        if (rootJs.Count == 0) return new Result(support, partial);

        // Bütün dosyaların ters haritası: kim kimi içe aktarıyor.
        var importers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (rel, d) in info)
            foreach (var t in d.imports)
            {
                if (!importers.TryGetValue(t, out var l)) importers[t] = l = new HashSet<string>(StringComparer.Ordinal);
                l.Add(rel);
            }

        // Köklerden ulaşılan dosyalar.
        var reach = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(rootJs);
        var seen = new HashSet<string>(rootJs, StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            if (!info.TryGetValue(f, out var d)) continue;
            foreach (var t in d.imports)
                if (seen.Add(t)) { stack.Push(t); if (!rootSet.Contains(t)) reach.Add(t); }
        }

        // İddia kurabilen dosyalar: iddia kütüphanesini kendisi çağırır ya da çağıran bir dosyayı içe aktarır.
        var cap = new HashSet<string>(info.Where(kv => kv.Value.fw).Select(kv => kv.Key), StringComparer.Ordinal);
        var q = new Queue<string>(cap);
        while (q.Count > 0)
        {
            var g = q.Dequeue();
            if (!importers.TryGetValue(g, out var imps)) continue;
            foreach (var f in imps) if (cap.Add(f)) q.Enqueue(f);
        }

        bool AllIn(string f, Func<string, bool> ok) => !importers.TryGetValue(f, out var imps) || imps.All(ok);
        var s = new HashSet<string>(reach.Where(f => info.ContainsKey(f) && cap.Contains(f)), StringComparer.Ordinal);
        // Test OLMAYAN bir kaynak içe aktarıyorsa dosya kaynaktır: elenir. Eleme başkasının elenmesine yol açabilir, durana kadar.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var f in s.ToList())
                if (!AllIn(f, i => rootSet.Contains(i) || s.Contains(i))) { s.Remove(f); changed = true; }
        }
        // Yalnız destek dosyalarının kullandığı saf yardımcılar (iddia çağırmasa da) destekle birlikte yargıçtır.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var f in reach)
            {
                if (s.Contains(f) || !info.ContainsKey(f) || !importers.TryGetValue(f, out var imps) || imps.Count == 0) continue;
                if (imps.All(s.Contains)) { s.Add(f); changed = true; }
            }
        }
        foreach (var f in s) support.Add(f);
        // Test verisi olarak içe aktarılan dosyalar (yalnız testler ve destek dosyaları).
        foreach (var f in seen)
            if (!info.ContainsKey(f) && IsDataFile(f)
                && importers.TryGetValue(f, out var ji) && ji.All(i => rootSet.Contains(i) || support.Contains(i)))
                support.Add(f);

        foreach (var f in rootSet.Concat(s).Distinct())
            if (texts.TryGetValue(f, out var text))
                foreach (var data in DataReferences(f, text, exists, candidateDataFiles)) support.Add(data);

        // "Tam belirlenemedi": testten ulaşılan dosyalarda sabit olmayan içe aktarma var mı?
        var tests = rootJs.Where(isTestRoot).Take(MaxPartialChecks).ToList();
        foreach (var t in tests)
        {
            var st = new Stack<string>(); st.Push(t);
            var vis = new HashSet<string>(StringComparer.Ordinal) { t };
            var bad = false;
            while (st.Count > 0 && !bad)
            {
                var f = st.Pop();
                if (!info.TryGetValue(f, out var d)) continue;
                if (d.unres) { bad = true; break; }
                foreach (var x in d.imports) if (support.Contains(x) && vis.Add(x)) st.Push(x);
            }
            if (bad) partial.Add(t);
        }
        partial.Sort(StringComparer.Ordinal);
        return new Result(support, partial);
    }
}
