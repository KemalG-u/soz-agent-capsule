using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Soz.Shell;

/// <summary>
/// Velopack kaldırma (uninstall) ve güncelleme öncesi kancalar. Kaldırma etkileşimsiz çalışır, bu yüzden güvenli yol varsayılandır:
/// - Çalışan Söz örneği varsa adlandırılmış kanal üzerinden nazikçe kapanmasını ister.
/// - Kayıtlı projelerin <c>.claude\settings.local.json</c> ve <c>.claude\settings.json</c> dosyalarından YALNIZ Söz'ün kanca kayıtlarını çıkarır;
///   dosyanın geri kalanı bayt bayt korunur, değişiklikten önce zaman damgalı yedek alınır (<see cref="AyarDosyasiTemizleyici"/>).
/// - Windows açılış kaydını (<c>HKCU\...\Run\Soz</c>) siler.
/// - Kullanıcının verisini (<c>veri</c>: iş geçmişi, kanıtlar) SİLMEZ. Kurulum klasörünün dışına (<c>Soz-veri</c>) taşır ve nerede olduğunu
///   <c>SOZ-VERI-NEREDE.txt</c> dosyasıyla söyler. Yeniden kurulumda ilk açılışta geri gelir.
///   Silme yalnız kullanıcı tepsi menüsünden "Kaldırırken verimi de sil" seçeneğini açıkça açtıysa yapılır.
/// - Geliştirici bilgisayarında yalıtılmış sınama için <c>SOZ_DEV_UNINSTALL_ROOT</c> verilirse gerçek kullanıcı verisine ve açılış kaydına dokunmaz.
/// </summary>
internal static class UninstallCleanup
{
    internal const string TestRootEnv = "SOZ_DEV_UNINSTALL_ROOT";

    internal sealed record Result(int HooksRemoved, int FilesRemoved, bool StartupCleared, string? KorunanKlasor = null, bool VeriSilindi = false, IReadOnlyList<string>? Notlar = null);

    public static void StopRunningInstance(Options? opt = null, string? dataDir = null)
    {
        try
        {
            var o = opt ?? Options.Parse([]);
            var dir = dataDir ?? o.ResolveDataDir();
            var pipe = SingleInstanceChannel.BuildPipeName(o, dir);
            if (SingleInstanceChannel.TrySignalExit(pipe, 1500))
            {
                Thread.Sleep(350);
            }
        }
        catch { }
    }

    public static Result RunDefault()
    {
        try
        {
            var testRoot = Environment.GetEnvironmentVariable(TestRootEnv);
            if (!string.IsNullOrWhiteSpace(testRoot))
            {
                var fullTest = Path.GetFullPath(testRoot);
                if (Directory.Exists(fullTest))
                    return RunInRoot(fullTest, clearStartup: false, TimeSpan.FromSeconds(15));
                return new Result(0, 0, false);
            }

            StopRunningInstance();
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var installRoot = Path.Combine(localApp, "Soz");
            return RunInRoot(installRoot, clearStartup: true, TimeSpan.FromSeconds(20));
        }
        catch
        {
            return new Result(0, 0, false);
        }
    }

    internal static Result RunInRoot(string installRoot, bool clearStartup, TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        var hooksRemoved = 0;
        var notlar = new List<string>();
        var startupCleared = false;

        if (clearStartup)
        {
            StartWithWindows.Disable();
            startupCleared = true;
        }

        var dataDir = Path.Combine(installRoot, "veri");
        var dbPath = Path.Combine(dataDir, "soz.db");
        if (File.Exists(dbPath))
        {
            foreach (var projPath in ReadRegisteredProjectPaths(dbPath))
            {
                if (sw.Elapsed > budget) { notlar.Add("süre doldu, bazı projelerin ayar dosyasına bakılmadı"); break; }
                hooksRemoved += TryUninstallProjectHooks(projPath, notlar);
            }
        }
        // Veri tabanı bağlantı havuzu dosyayı tutmasın (taşıma ve silme için).
        try { SqliteConnection.ClearAllPools(); } catch { }

        if (!Directory.Exists(dataDir)) return new Result(hooksRemoved, 0, startupCleared, null, false, notlar);

        // Silme yalnız kullanıcının açık seçimiyle.
        if (KabukTercihleri.Oku(dataDir).KaldirirkenVeriyiSil)
        {
            var silinen = RemoveDirectorySafe(dataDir);
            return new Result(hooksRemoved, silinen, startupCleared, null, true, notlar);
        }

        var k = VeriKorumasi.Koru(dataDir, installRoot);
        if (k.Hata != null) notlar.Add("veri korunurken uyarı: " + k.Hata);
        return new Result(hooksRemoved, 0, startupCleared, k.Klasor, false, notlar);
    }

    internal static IReadOnlyList<string> ReadRegisteredProjectPaths(string dbPath)
    {
        var paths = new List<string>();
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM projects WHERE path IS NOT NULL AND path <> ''";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var p = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(p)) paths.Add(p);
            }
        }
        catch { }
        return paths;
    }

    /// <summary>Projedeki iki Claude ayar dosyasından yalnız Söz kancalarını çıkarır; kaç kanca kaldırıldığını döndürür.</summary>
    internal static int TryUninstallProjectHooks(string projectDir, List<string>? notlar = null)
    {
        var removed = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir)) return 0;
            foreach (var file in new[]
            {
                Path.Combine(projectDir, ".claude", "settings.local.json"),
                Path.Combine(projectDir, ".claude", "settings.json"),
            })
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    var r = AyarDosyasiTemizleyici.Temizle(file);
                    if (r.Hata != null) notlar?.Add($"{file}: {r.Hata}");
                    else removed += r.Kaldirilan;
                }
                catch { }
            }
        }
        catch { }
        return removed;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private static int RemoveDirectorySafe(string path)
    {
        var count = 0;
        try
        {
            if (IsReparsePoint(path))
            {
                Directory.Delete(path, recursive: false);
                return 1;
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (Directory.Exists(entry))
                {
                    count += RemoveDirectorySafe(entry);
                }
                else
                {
                    try
                    {
                        File.SetAttributes(entry, FileAttributes.Normal);
                        File.Delete(entry);
                        count++;
                    }
                    catch { }
                }
            }
            Directory.Delete(path, recursive: false);
        }
        catch { }
        return count;
    }
}
