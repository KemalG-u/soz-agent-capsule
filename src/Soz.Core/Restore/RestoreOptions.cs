using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Registry;

namespace Soz.Core.Restore;

/// <summary>
/// Geri alma ayarları. Kişinin değiştirebildikleri kayıt defterindeki `setting:restore.*` kayıtlarından (`Registry\kayit\gerialma.soz.json`) gelir:
/// boyut sınırı, paralellik, saklama süreleri, disk bütçesi. Varsayılan sayılar kodda yazılmaz, aynı kayıttaki `default` alanından okunur
/// (ölçüm kaynağı: calisma\spike-geri-alma-net.md ve rapor bölüm 4.3 / 4.6). Aşağıdaki biçim sabitleri (parça boyutu, paket boyutu, fsync, tam görüntü aralığı)
/// diskteki depo biçimini belirler, kişi ayarı değildir; değişmeleri depo sürümünü değiştirir.
/// </summary>
public sealed class RestoreOptions
{
    public const int ChunkSize = 4 << 20;

    private static double Def(string key) => SettingIndex.Embedded.Defaults.GetValueOrDefault(Prefix + key) is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        ? double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)
        : throw new InvalidOperationException("Geri alma ayarı kayıt defterinde yok: " + Prefix + key);

    public const string Prefix = "setting:restore.";
    public const string KEnabled = Prefix + "enabled";
    private const string KMaxCopyMb = "max-copy-mb", KMaxFiles = "max-files", KParallel = "parallel", KKeepAccepted = "keep-accepted-days", KKeepLast = "keep-last-points",
        KUndoDays = "undo-point-days", KGlobalGb = "global-budget-gb", KMinProjectGb = "min-project-budget-gb", KProjectFactor = "project-budget-factor", KDiskFactor = "disk-free-factor";

    /// <summary>Bundan büyük dosya kopyalanmaz, yalnız özeti alınır (koruma "sınırlı").</summary>
    public long MaxCopyBytes { get; set; } = (long)Def(KMaxCopyMb) << 20;
    /// <summary>Bundan büyük dosya 4 MiB sabit parçalara bölünür (büyük dosyadaki küçük değişiklik ucuz kalır). Biçim sabiti.</summary>
    public long ChunkFromBytes { get; set; } = 32L << 20;
    /// <summary>Paket dosyası boyutu. Biçim sabiti.</summary>
    public long PackFileBytes { get; set; } = 32L << 20;
    /// <summary>Saklanan boyutu bundan küçük blob paket dosyasına eklenir (63 bin dosya yerine ~25 dosya). Biçim sabiti.</summary>
    public long PackItemMaxBytes { get; set; } = 256L << 10;
    public int Parallel { get; set; } = (int)Def(KParallel);
    /// <summary>Bundan çok dosyalı klasörde görüntü alınmaz (koruma "yok").</summary>
    public int MaxFiles { get; set; } = (int)Def(KMaxFiles);
    /// <summary>Yazılanlar diske zorlanır. Biçim sabiti.</summary>
    public bool Fsync { get; set; } = true;
    /// <summary>Zincirde bu kadar nokta üst üste gelince tam görüntü yazılır (çözümleme uzamasın). Biçim sabiti.</summary>
    public int FullEvery { get; set; } = 25;
    public long MinProjectBudgetBytes { get; set; } = (long)Def(KMinProjectGb) << 30;
    public double ProjectBudgetFactor { get; set; } = Def(KProjectFactor);
    public long GlobalBudgetBytes { get; set; } = (long)Def(KGlobalGb) << 30;
    /// <summary>Boş disk, tahmini yeni bayt x bu sayıdan azsa görüntü alınmaz.</summary>
    public double DiskFreeFactor { get; set; } = Def(KDiskFactor);
    public int KeepAcceptedDays { get; set; } = (int)Def(KKeepAccepted);
    public int KeepLastPoints { get; set; } = (int)Def(KKeepLast);
    public int UndoPointDays { get; set; } = (int)Def(KUndoDays);

    /// <summary>Kişinin kayıtlı ayarlarından (yoksa kayıttaki varsayılandan) kurar. Sınırlar kayıttaki `minimum`/`maximum` ile kıstırılır.</summary>
    public static RestoreOptions From(SozSettings s) => new()
    {
        MaxCopyBytes = (long)s.ClampedInt(Prefix + KMaxCopyMb) << 20,
        Parallel = s.ClampedInt(Prefix + KParallel),
        MaxFiles = s.ClampedInt(Prefix + KMaxFiles),
        KeepAcceptedDays = s.ClampedInt(Prefix + KKeepAccepted),
        KeepLastPoints = s.ClampedInt(Prefix + KKeepLast),
        UndoPointDays = s.ClampedInt(Prefix + KUndoDays),
        GlobalBudgetBytes = (long)s.ClampedInt(Prefix + KGlobalGb) << 30,
        MinProjectBudgetBytes = (long)s.ClampedInt(Prefix + KMinProjectGb) << 30,
        ProjectBudgetFactor = s.Num(Prefix + KProjectFactor),
        DiskFreeFactor = s.Num(Prefix + KDiskFactor),
    };

    public static RestoreOptions Load(SqliteConnection c) => From(SozSettings.Load(c));
}

/// <summary>
/// Dışlama listesi, TEK KAYNAK: bağımlılık klasörleri, derleme çıktısı ve araç önbellekleri taranmaz (bağımlılık ve derleme klasörleri için yalnız parmak izi alınır).
/// Kanıt motoru (Proof\EvidenceBase) bu kümeleri doğrudan buradan okur; geri alma da aynı kümeyi kullanır. Yeni bir klasör adı yalnız burada eklenir.
/// </summary>
public static class Exclusions
{
    /// <summary>Bağımlılık klasörleri: ad → tür (nm, py, vendor).</summary>
    public static readonly IReadOnlyDictionary<string, string> DepRootKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = "nm", [".venv"] = "py", ["venv"] = "py", ["vendor"] = "vendor",
    };

    /// <summary>Bağımlılık klasör adları.</summary>
    public static readonly IReadOnlySet<string> DepRoots = new HashSet<string>(DepRootKinds.Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>Derleme çıktısı.</summary>
    public static readonly IReadOnlySet<string> Build = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dist", "build" };

    /// <summary>Hiç taranmayan klasörler (araçların kendi önbellekleri sınama sırasında kendiliğinden oluşur, ajan işi değildir).</summary>
    public static readonly IReadOnlySet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "target", "__pycache__", ".next", "coverage",
        ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".nyc_output", ".cache", ".parcel-cache", ".turbo", ".svelte-kit", ".gradle", ".nuxt", ".vite", ".vitest",
    };

    public static bool IsExcludedDir(ReadOnlySpan<char> name)
    {
        var s = name.ToString();
        return DepRoots.Contains(s) || Build.Contains(s) || Skip.Contains(s);
    }

    /// <summary>Parmak izi alınan (içeriği saklanmayan) klasör mü?</summary>
    public static bool IsFingerprinted(string name) => DepRoots.Contains(name) || Build.Contains(name);

    /// <summary>Geçici dosyalar: Office kilit dosyası (~$...), *.tmp ve geri almanın kendi geçici dosyası.</summary>
    public static bool IsTempFile(ReadOnlySpan<char> name) =>
        name.StartsWith("~$") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(RestoreEngine.TempSuffix, StringComparison.OrdinalIgnoreCase);
}
