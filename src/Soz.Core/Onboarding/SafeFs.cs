using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soz.Core.Onboarding;

/// <summary>Bir dosyanın adı, boyutu ve son değişme zamanı. İçerik yok.</summary>
public sealed record FileStat(string Name, string Path, long Length, DateTime LastWriteUtc);

/// <summary>
/// Keşfin dosya sistemine açılan TEK kapısı (gizlilik güvencesi). Yalnız varlık, ad, sayı ve tarih döner.
/// Dosya İÇERİĞİ okunan tek yer sürüm okumasıdır: bir <c>package.json</c> içindeki "version" ya da bir .exe'nin sürüm bilgisi
/// (ikisi de programın kendi künyesidir, kişinin içeriği değil) ve değer sürüm kalıbına uymazsa atılır.
/// Her işlem <see cref="Log"/>'a düşer; test, kayıtlı bir konuşma, kimlik dosyası ya da tarayıcı profiline dokunulmadığını buradan doğrular.
/// Okunamayan yer (erişim reddi, kilit) sessizce atlanır ve <see cref="Unreadable"/> sayısını artırır. Bu sınıf hiçbir şey yazmaz, silmez, çalıştırmaz.
/// Ağ yolları (\\sunucu\paylaşım) hiç açılmaz: donma riski.
/// </summary>
public sealed class SafeFs
{
    public const int MaxFilesPerFolder = 5000;
    private const long MaxPackageJsonBytes = 1_000_000;
    private static readonly Regex VersionShape = new(@"^\d+(\.\d+){0,3}([\-+][0-9A-Za-z.\-+]{0,30})?$", RegexOptions.CultureInvariant | RegexSpeed.Compiled);

    /// <summary>(işlem, yol). İşlemler: exists, list, stat, read-version, read-exe-version, resolve.</summary>
    public List<(string Op, string Path)> Log { get; } = new();
    public int Unreadable { get; private set; }

    private static bool IsNetwork(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    public bool DirExists(string path)
    {
        if (IsNetwork(path)) return false;
        Log.Add(("exists", path));
        try { return Directory.Exists(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Unreadable++; return false; }
    }

    public bool FileExists(string path)
    {
        if (IsNetwork(path)) return false;
        Log.Add(("exists", path));
        try { return File.Exists(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Unreadable++; return false; }
    }

    /// <summary>Alt klasör adları (yol değil), ada göre sıralı.</summary>
    public IReadOnlyList<string> SubDirs(string path)
    {
        if (IsNetwork(path)) return Array.Empty<string>();
        Log.Add(("list", path));
        try
        {
            if (!Directory.Exists(path)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(path).Select(p => System.IO.Path.GetFileName(p)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Unreadable++; return Array.Empty<string>(); }
    }

    /// <summary>Klasördeki dosyalar (yalnız bu düzey), ad süzgeci isteğe bağlı. En çok <see cref="MaxFilesPerFolder"/> kayıt.</summary>
    public IReadOnlyList<FileStat> Files(string path, string pattern = "*")
    {
        if (IsNetwork(path)) return Array.Empty<FileStat>();
        Log.Add(("list", path));
        var list = new List<FileStat>();
        try
        {
            if (!Directory.Exists(path)) return list;
            foreach (var p in Directory.EnumerateFiles(path, pattern))
            {
                if (list.Count >= MaxFilesPerFolder) break;
                try
                {
                    var fi = new FileInfo(p);
                    list.Add(new FileStat(fi.Name, p, fi.Length, fi.LastWriteTimeUtc));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Unreadable++; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Unreadable++; }
        return list;
    }

    /// <summary>Dosyanın ya da klasörün son değişme zamanı (UTC), yoksa null. İçeriğe bakmaz.</summary>
    public DateTime? LastWrite(string path)
    {
        if (IsNetwork(path)) return null;
        Log.Add(("stat", path));
        try
        {
            if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
            if (Directory.Exists(path)) return Directory.GetLastWriteTimeUtc(path);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Unreadable++; return null; }
    }

    /// <summary>Bağlantı (junction, sembolik bağ) varsa gerçek hedef yol; yoksa yolun kendisi. Aynı kurulumu iki kez saymamak için.</summary>
    public string Resolve(string path)
    {
        if (IsNetwork(path)) return path;
        Log.Add(("resolve", path));
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? System.IO.Path.GetFullPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
    }

    /// <summary>npm paketinin <c>package.json</c> dosyasından YALNIZ "version" alanı. Başka hiçbir alan saklanmaz. Kalıba uymayan değer null.</summary>
    public string? PackageVersion(string packageJsonPath)
    {
        if (IsNetwork(packageJsonPath) || !string.Equals(System.IO.Path.GetFileName(packageJsonPath), "package.json", StringComparison.OrdinalIgnoreCase)) return null;
        Log.Add(("read-version", packageJsonPath));
        try
        {
            var fi = new FileInfo(packageJsonPath);
            if (!fi.Exists || fi.Length > MaxPackageJsonBytes) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(packageJsonPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString();
            return s is not null && VersionShape.IsMatch(s) ? s : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { Unreadable++; return null; }
    }

    /// <summary>.exe dosyasının kendi sürüm künyesi (dosya özellikleri sekmesi). Program ÇALIŞTIRILMAZ. Kalıba uymayan ya da boş değer null.</summary>
    public string? ExeVersion(string exePath)
    {
        if (IsNetwork(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        Log.Add(("read-exe-version", exePath));
        try
        {
            if (!File.Exists(exePath)) return null;
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            foreach (var s in new[] { vi.ProductVersion, vi.FileVersion })
                if (!string.IsNullOrWhiteSpace(s) && VersionShape.IsMatch(s.Trim())) return s.Trim();
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Unreadable++; return null; }
    }
}
