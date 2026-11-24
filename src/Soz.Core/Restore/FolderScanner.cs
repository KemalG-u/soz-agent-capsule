using System.IO.Enumeration;

namespace Soz.Core.Restore;

/// <summary>Taramadan gelen bir giriş. Path "/" ayraçlı ve kök klasöre göredir. MtimeNs Unix nanosaniyesi (tick x 100 Int64'ü taşırır). Boş klasör: IsDir, Size -1.</summary>
public sealed record ScanItem(string Path, long Size, long MtimeNs, int Attr, bool IsDir);

/// <summary>Bağımlılık/derleme klasörünün parmak izi: içeriği saklanmaz, geri alma yalnız "değişti, yeniden kurmak gerekebilir" der.</summary>
public sealed record DepPrint(string Path, int Files, long Bytes, long MtimeNs);

public sealed class ScanResult
{
    public List<ScanItem> Items { get; init; } = new();
    public List<DepPrint> Deps { get; init; } = new();
    public int FileCount { get; set; }
    public bool TooMany { get; set; }
    public long Ms { get; set; }
}

/// <summary>
/// Klasör tarayıcısı (spike: FileSystemEnumerable, 63 bin dosyada ~160 ms). Bağlantılar (junction, sembolik bağ) izlenmez,
/// dışlama listesindeki klasörler girilmez, geçici dosyalar görülmez. Boş klasörler de kaydedilir (spike'ta eksikti).
/// </summary>
public static class FolderScanner
{
    public const long EpochTicks = 621355968000000000L; // 1970-01-01 UTC

    public static long ToNs(long utcTicks) => (utcTicks - EpochTicks) * 100;
    public static long ToTicks(long unixNs) => unixNs / 100 + EpochTicks;

    private static string Rel(ref FileSystemEntry e)
    {
        var d = e.Directory.Slice(e.RootDirectory.Length);
        while (d.Length > 0 && (d[0] == '\\' || d[0] == '/')) d = d.Slice(1);
        var n = e.FileName;
        var len = d.Length == 0 ? n.Length : d.Length + 1 + n.Length;
        return string.Create(len, (d.ToString(), n.ToString()), static (span, x) =>
        {
            var p = 0;
            if (x.Item1.Length > 0) { x.Item1.AsSpan().CopyTo(span); p = x.Item1.Length; span[p++] = '/'; }
            x.Item2.AsSpan().CopyTo(span.Slice(p));
            span.Replace('\\', '/');
        });
    }

    public static ScanResult Scan(string root, int maxFiles, bool useExclusions = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fingerprint = new List<(string rel, string full)>();
        var opt = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        var all = new List<ScanItem>();
        var files = 0;
        var tooMany = false;
        var en = new FileSystemEnumerable<ScanItem>(root, static (ref FileSystemEntry e) => new ScanItem(Rel(ref e), e.IsDirectory ? -1 : e.Length, FolderScanner.ToNs(e.LastWriteTimeUtc.UtcTicks), (int)(e.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)), e.IsDirectory), opt)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory ? !(useExclusions && Exclusions.IsExcludedDir(e.FileName)) : !(useExclusions && Exclusions.IsTempFile(e.FileName)),
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
            {
                if (!useExclusions || !Exclusions.IsExcludedDir(e.FileName)) return true;
                var name = e.FileName.ToString();
                if (Exclusions.IsFingerprinted(name)) fingerprint.Add((Rel(ref e), System.IO.Path.Combine(e.Directory.ToString(), name)));
                return false;
            },
        };
        foreach (var it in en)
        {
            if (!it.IsDir && ++files > maxFiles) { tooMany = true; break; }
            all.Add(it);
        }

        // Boş klasör = hiçbir (dışlanmamış) girişin üst klasörü olmayan klasör. Dolu klasör, içindeki dosyalardan zaten yeniden kurulur.
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in all)
        {
            var i = it.Path.LastIndexOf('/');
            if (i > 0) parents.Add(it.Path[..i]);
        }
        var items = new List<ScanItem>(all.Count);
        foreach (var it in all)
            if (!it.IsDir || !parents.Contains(it.Path)) items.Add(it);

        var deps = new List<DepPrint>();
        foreach (var (rel, full) in fingerprint) deps.Add(Fingerprint(rel, full));
        deps.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ScanResult { Items = items, Deps = deps, FileCount = files, TooMany = tooMany, Ms = sw.ElapsedMilliseconds };
    }

    private static DepPrint Fingerprint(string rel, string full)
    {
        int n = 0; long bytes = 0, mtime = 0;
        try
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (var e in new FileSystemEnumerable<(long len, long m)>(full, static (ref FileSystemEntry x) => (x.Length, x.LastWriteTimeUtc.UtcTicks), opt) { ShouldIncludePredicate = static (ref FileSystemEntry x) => !x.IsDirectory })
            {
                n++; bytes += e.len;
                if (e.m > mtime) mtime = e.m;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* okunamayan klasör: eldeki kadarı */ }
        return new DepPrint(rel, n, bytes, mtime == 0 ? 0 : ToNs(mtime));
    }
}
