using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Soz.Core.Restore;

namespace Soz.Core.Proof;

/// <summary>Onaylı dosyanın içeriğini bir yere yazan kaynak (geri alma deposu ya da test için sahte).</summary>
public interface IApprovedBlobs : IDisposable
{
    /// <summary>İçeriği SHA-256 özeti (hex, küçük harf) verilen dosyayı hedef yola yazar. Depoda yoksa false.</summary>
    bool TryWrite(string sha, string targetPath);
}

/// <summary>Geri alma deposu (içerik adresli: blob özeti = dosyanın düz içeriğinin SHA-256'sı) üzerinden okuyan kaynak. Depo yoksa <see cref="TryOpen"/> null verir ve hiçbir şey oluşturmaz.</summary>
public sealed class DepotBlobs : IApprovedBlobs
{
    private readonly GeriDepo _depo;
    private readonly bool _owns;
    private DepotBlobs(GeriDepo depo, bool owns = true) { _depo = depo; _owns = owns; }

    /// <summary>Açık duran bir depoyu sarar (kapatmaz).</summary>
    public static DepotBlobs Wrap(GeriDepo depo) => new(depo, false);

    public static DepotBlobs? TryOpen(string dataDir)
    {
        try
        {
            var dir = Path.Combine(dataDir, "geri");
            if (!File.Exists(Path.Combine(dir, "geri.db"))) return null;
            return new DepotBlobs(new GeriDepo(dir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or SozException) { return null; }
    }

    public bool TryWrite(string sha, string targetPath)
    {
        try
        {
            using var c = _depo.Open();
            var br = _depo.GetBlob(c, sha);
            if (br is null) return false;
            List<BlobRow>? parts = null;
            if (br.Codec == "parca")
            {
                parts = new();
                foreach (var ch in _depo.GetChunks(c, br.Hash))
                {
                    var cb = _depo.GetBlob(c, ch.ChunkHash);
                    if (cb is null) return false;
                    parts.Add(cb);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (File.Exists(targetPath)) File.SetAttributes(targetPath, FileAttributes.Normal);
            using var f = File.OpenHandle(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.None, br.Size);
            _depo.WriteBlobTo(f, br, parts);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or SirAcilamadiException or Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    public void Dispose() { if (_owns) _depo.Dispose(); }
}

/// <summary>
/// Sonuç koşusunun (ikinci deneme) geçici proje kopyası (e2). Kopya YALNIZ &lt;veri&gt;\hukum\ altında kurulur; ağır ve değişmeyen bağımlılık klasörleri (node_modules, .venv, venv, vendor) kopyalanmaz,
/// bağlantı (junction) ile gösterilir. Kaldırma YALNIZ kendi kurduğu klasör içindir (yol denetimi + işaret dosyası) ve bağlantıyı izlemez: bağlantının kendisi silinir, HEDEFİ silinmez.
/// </summary>
public static class TempTree
{
    public const string Marker = ".soz-hukum-kopya";
    public const string ParentName = "hukum";

    public sealed record CopyStats(int Files, long Bytes, int Junctions);

    public static string ParentDir(string dataDir) => Path.Combine(Path.GetFullPath(dataDir), ParentName);

    /// <summary>Yeni geçici taban klasörü kurar (işaret dosyasıyla) ve yolunu verir. İçine proje kopyası "p" altına gider.</summary>
    public static string NewBase(string dataDir, string name)
    {
        var parent = ParentDir(dataDir);
        Directory.CreateDirectory(parent);
        var b = Path.Combine(parent, name);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, Marker), "Söz hüküm koşusunun geçici kopyası. İş bitince kendiliğinden silinir.\n");
        return b;
    }

    private static bool IsUnder(string full, string parent)
        => full.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Proje kökünü dest'e kopyalar. Sınırlar aşılırsa (stats null) nedeni verir. veriDir kökün içindeyse atlanır.</summary>
    public static (CopyStats? stats, string? why) Copy(string root, string dest, string dataDir, long maxBytes, int maxFiles)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
        var dataFull = Path.GetFullPath(dataDir).TrimEnd('\\', '/');
        var files = 0; long bytes = 0; var junctions = 0;
        string? why = null;
        void Walk(string src, string dst)
        {
            if (why is not null) return;
            Directory.CreateDirectory(dst);
            IEnumerable<FileSystemInfo> items;
            try { items = new DirectoryInfo(src).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var it in items)
            {
                if (why is not null) return;
                var target = Path.Combine(dst, it.Name);
                var isReparse = (it.Attributes & FileAttributes.ReparsePoint) != 0;
                if (it is DirectoryInfo)
                {
                    if (Soz.Core.Restore.Exclusions.DepRoots.Contains(it.Name)) { if (MakeJunction(target, it.FullName)) junctions++; else why = $"«{it.Name}» klasörüne bağlantı kurulamadı."; continue; }
                    if (isReparse) continue; // başka bağlantı klasörleri izlenmez (kanıt taraması da izlemez)
                    var full = it.FullName.TrimEnd('\\', '/');
                    if (full.Equals(dataFull, StringComparison.OrdinalIgnoreCase) || (IsUnder(dataFull, full))) continue; // veri klasörü projenin içindeyse kopyalanmaz
                    if (Soz.Core.Restore.Exclusions.Skip.Contains(it.Name)) continue;
                    Walk(it.FullName, target);
                    continue;
                }
                if (isReparse) continue;
                var fi = (FileInfo)it;
                if (++files > maxFiles) { why = $"Klasörde {maxFiles:N0} dosyadan fazlası var, geçici bir kopya kurulamadı."; return; }
                bytes += fi.Length;
                if (bytes > maxBytes) { why = $"Klasör {maxBytes / (1024 * 1024):N0} MB sınırından büyük, geçici bir kopya kurulamadı."; return; }
                try { File.Copy(fi.FullName, target, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = $"«{it.Name}» dosyası kopyalanamadı: {e.Message}"; return; }
            }
        }
        Walk(rootFull, Path.GetFullPath(dest));
        return why is null ? (new CopyStats(files, bytes, junctions), null) : (null, why);
    }

    /// <summary>Bağlantı (junction) kurar. Windows'ta mklink /J (yönetici hakkı gerekmez), başka sistemde sembolik bağlantı.</summary>
    public static bool MakeJunction(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return true; }
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch (Exception) { } return false; }
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Geçici tabanı kaldırır. Güvenlik: klasör allowedParent'ın ALTINDA olmalı, işaret dosyası taşımalı ve allowedParent'a kendisi eşit olamaz.
    /// Bağlantılar izlenmez: bağlantı klasörü ya da dosyası ise yalnız bağlantının kendisi silinir, hedefe dokunulmaz. Dönen: kaldırıldı mı (yol denetimi reddederse false, hiçbir şey silinmez).
    /// </summary>
    public static bool SafeRemove(string baseDir, string allowedParent)
    {
        try
        {
            var full = Path.GetFullPath(baseDir).TrimEnd('\\', '/');
            var parent = Path.GetFullPath(allowedParent).TrimEnd('\\', '/');
            if (full.Equals(parent, StringComparison.OrdinalIgnoreCase) || !IsUnder(full, parent)) return false;
            if (!Directory.Exists(full)) return true;
            var info = new DirectoryInfo(full);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return false; // taban klasörün kendisi bağlantı olamaz
            if (!File.Exists(Path.Combine(full, Marker))) return false;
            DeleteTree(info, Path.Combine(full, Marker));
            return !Directory.Exists(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Kaldırır; dosya ya da klasör hâlâ başka bir sürecin elindeyse (zaman aşımıyla öldürülen sınama sürecinin çalışma klasörü gibi) birkaç kez bekleyip yeniden dener.
    /// Yine olmazsa klasör kalır, bir sonraki hüküm koşusunun başında <see cref="VerdictRun.SweepOld"/> süpürür.
    /// </summary>
    public static bool RemoveWithRetry(string baseDir, string allowedParent, int tries = 8, int delayMs = 500)
    {
        for (var i = 0; i < tries; i++)
        {
            if (SafeRemove(baseDir, allowedParent)) return true;
            if (!Directory.Exists(baseDir)) return true;
            Thread.Sleep(delayMs);
        }
        return !Directory.Exists(baseDir);
    }

    private static void DeleteTree(DirectoryInfo dir, string? keepLast = null)
    {
        foreach (var it in dir.EnumerateFileSystemInfos().ToList())
        {
            // İşaret dosyası en sona kalır: silme yarıda kalırsa (bir süreç klasörü hâlâ tutuyorsa) yeniden deneme yol denetiminden geçebilsin.
            if (keepLast is not null && it.FullName.Equals(keepLast, StringComparison.OrdinalIgnoreCase)) continue;
            if ((it.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Bağlantı: yalnız bağlantı kaldırılır. İçine girilmez, hedefin dosyalarına dokunulmaz.
                if (it is DirectoryInfo) Directory.Delete(it.FullName, false);
                else { File.SetAttributes(it.FullName, FileAttributes.Normal); File.Delete(it.FullName); }
            }
            else if (it is DirectoryInfo d) DeleteTree(d);
            else { File.SetAttributes(it.FullName, FileAttributes.Normal); File.Delete(it.FullName); }
        }
        if (keepLast is not null && File.Exists(keepLast)) { File.SetAttributes(keepLast, FileAttributes.Normal); File.Delete(keepLast); }
        dir.Attributes = FileAttributes.Normal;
        Directory.Delete(dir.FullName, false);
    }
}

public sealed record VerdictRunRequest(string Root, string DataDir, long JobId, long RunId, EvidenceBase.Snapshot Approved, EvidenceBase.Snapshot Now,
    IReadOnlyList<CheckToRun> Checks, int BudgetSeconds, long MaxCopyBytes, IApprovedBlobs? Blobs, Func<string?>? EdgeLocator);

/// <summary>Hüküm koşusunun sonucu. Ran=false: koşu yapılamadı (Why sade neden); Outcomes boş.</summary>
public sealed record VerdictRunResult(bool Ran, string? Why, List<CheckOutcome> Outcomes, long Ms, int Restored, int Removed, int CopiedFiles);

/// <summary>
/// HÜKÜM KOŞUSU (e2). İş bitince Söz ajanın YENİ kaynak kodunu ONAYLI sınama kümesiyle koşar: geçici bir proje kopyasında ajanın dokunduğu sınama dosyaları onaylı hâlleriyle
/// değiştirilir, ajanın eklediği YENİ test dosyaları çıkarılır, onaylı komut çalıştırılır. Kullanıcının proje klasörüne hiçbir şey yazılmaz.
/// Onaylı içerik önce görüntüdeki küçük metin kopyasından (özeti uyuyorsa), olmazsa geri alma deposundan (içerik adresli) gelir. İkisi de yoksa koşu yapılamaz: sessizce başka yola düşülmez.
/// Bilinen sınır: kaynak koda teste özel kısayol yazmak (ör. test ortamında başka değer döndürmek) bu yöntemle de yakalanmaz.
/// </summary>
public static class VerdictRun
{
    public static VerdictRunResult Execute(VerdictRunRequest req)
    {
        var sw = Stopwatch.StartNew();
        VerdictRunResult Fail(string why, int copied = 0, int restored = 0, int removed = 0) => new(false, why, new(), sw.ElapsedMilliseconds, restored, removed, copied);
        var parent = TempTree.ParentDir(req.DataDir);
        string? baseDir = null;
        try
        {
            baseDir = TempTree.NewBase(req.DataDir, $"{req.JobId}-{req.RunId}-{Guid.NewGuid().ToString("N")[..8]}");
            var copy = Path.Combine(baseDir, "p");
            var (stats, why) = TempTree.Copy(req.Root, copy, req.DataDir, req.MaxCopyBytes, EvidenceBase.MaxFiles);
            if (stats is null) return Fail(why ?? "Geçici kopya kurulamadı.");

            var copyFull = Path.GetFullPath(copy).TrimEnd('\\', '/');
            string? Dest(string rel)
            {
                var d = Path.GetFullPath(Path.Combine(copyFull, rel.Replace('/', Path.DirectorySeparatorChar)));
                return d.StartsWith(copyFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? d : null;
            }

            var nowMap = req.Now.Suspects.ToDictionary(e => e.Path, StringComparer.Ordinal);
            var approvedMap = req.Approved.Suspects.ToDictionary(e => e.Path, StringComparer.Ordinal);
            var restored = 0; var removed = 0;

            var produced = new HashSet<string>(req.Approved.Produced ?? new(), StringComparer.Ordinal);
            foreach (var m in ApprovedSet.Members(req.Approved))
            {
                if (nowMap.TryGetValue(m.Path, out var cur) && cur.Sha == m.Sha) continue; // ajan dokunmamış
                if (produced.Contains(m.Path) && nowMap.ContainsKey(m.Path)) continue;     // Söz'ün kendi koşusunun ürettiği dosya: onaylı sayılmaz
                var dest = Dest(m.Path);
                if (dest is null) return Fail($"Onaylı dosyanın yolu güvenli değil: {m.Path}", stats.Files, restored, removed);
                if (Path.GetFileName(m.Path).Equals("package.json", StringComparison.OrdinalIgnoreCase) && m.Extra is not null)
                {
                    var current = File.Exists(dest) ? File.ReadAllText(dest) : null;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllText(dest, ApprovedSet.MergePackage(m.Extra, current));
                    restored++;
                    continue;
                }
                if (!Restore(m, dest, req.Blobs))
                    return Fail($"Onayladığın «{m.Path}» dosyasının kopyasını bulamadım (geri alma görüntüsünde yok).", stats.Files, restored, removed);
                restored++;
            }
            foreach (var n in req.Now.Suspects)
            {
                if (approvedMap.ContainsKey(n.Path) || produced.Contains(n.Path) || !ApprovedSet.RemoveWhenAdded(n)) continue;
                var dest = Dest(n.Path);
                if (dest is null || !File.Exists(dest)) continue;
                File.SetAttributes(dest, FileAttributes.Normal);
                File.Delete(dest);
                removed++;
            }

            var evidence = Path.Combine(req.DataDir, "kanit", $"{req.RunId}-hukum");
            var runner = new ProofRunner(copyFull, evidence, req.EdgeLocator, req.DataDir, req.JobId, null);
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, req.BudgetSeconds));
            var outcomes = new List<CheckOutcome>();
            foreach (var ch in req.Checks)
            {
                var left = deadline - DateTime.UtcNow;
                if (left < TimeSpan.FromSeconds(1))
                {
                    outcomes.Add(new CheckOutcome(ch.Id, ch.Label, ResultStatus.Skipped, null, $"Onayladığın testleri yeniden denemek için ayrılan süre ({req.BudgetSeconds} saniye) bitti, çalıştırılmadı.", null, 0));
                    continue;
                }
                var t = TimeSpan.FromSeconds(Math.Max(1, ch.TimeoutS));
                outcomes.Add(runner.Run(ch, t < left ? t : left));
            }
            return new VerdictRunResult(true, null, outcomes, sw.ElapsedMilliseconds, restored, removed, stats.Files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail("Geçici bir kopya kurulurken dosya hatası oldu: " + e.Message);
        }
        finally
        {
            try
            {
                // İkinci koşunun kanıt dosyaları (çıktı metni) hükme taşınmaz: yalnız çıktı kuyruğu kullanılır, klasör kalmasın.
                var ev = Path.Combine(req.DataDir, "kanit", $"{req.RunId}-hukum");
                if (Directory.Exists(ev)) Directory.Delete(ev, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (baseDir is not null) TempTree.RemoveWithRetry(baseDir, parent);
        }
    }

    /// <summary>Onaylı kaydın içeriğini dest'e yazar: önce kayıttaki metin (özeti uyuyorsa), sonra geri alma deposu.</summary>
    internal static bool Restore(EvidenceBase.Entry m, string dest, IApprovedBlobs? blobs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (m.Content is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(m.Content);
                if (Convert.ToHexString(SHA256.HashData(bytes)).Equals(m.Sha, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                    File.WriteAllBytes(dest, bytes);
                    return true;
                }
            }
            return blobs is not null && blobs.TryWrite(m.Sha, dest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Eski geçici kopyaları süpürür (önceki bir koşu yarıda kesilmişse). Yalnız &lt;veri&gt;\hukum\ altındaki işaretli klasörler, 1 saatten eskiler.</summary>
    public static int SweepOld(string dataDir)
    {
        var parent = TempTree.ParentDir(dataDir);
        var n = 0;
        try
        {
            if (!Directory.Exists(parent)) return 0;
            foreach (var d in Directory.GetDirectories(parent))
                if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-1) && TempTree.SafeRemove(d, parent)) n++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return n;
    }
}
