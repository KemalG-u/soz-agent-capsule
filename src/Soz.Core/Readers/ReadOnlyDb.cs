using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Readers;

/// <summary>
/// Başka bir aracın SQLite dosyasını SALT OKUNUR açar (gözle derinliği: hiçbir şey yazılmaz, kilit tutulmaz).
/// Dosyanın yanında -wal ya da -shm varsa araç o anda açıktır: normal salt okunur bağlantı (WAL'ı yazmaz, okuyucu olarak katılır).
/// Yoksa dosya kapalıdır: "immutable" açılır; böylece okuma yeni -wal/-shm dosyası YARATMAZ ve dosyanın tek baytı değişmez.
/// Havuz kapalı: dosya tutamağı işimiz bitince bırakılır. Kilitli ya da yarım yazılmış dosyada kısa yeniden dene, olmazsa çağıran "okuyamadım" der.
/// </summary>
internal static class ReadOnlyDb
{
    public static SqliteConnection Open(string path)
    {
        var full = Path.GetFullPath(path);
        var live = File.Exists(full + "-wal") || File.Exists(full + "-shm") || File.Exists(full + "-journal");
        var cs = live
            ? new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString()
            : new SqliteConnectionStringBuilder { DataSource = new Uri(full).AbsoluteUri + "?mode=ro&immutable=1", Pooling = false, DefaultTimeout = 2 }.ToString();
        var c = new SqliteConnection(cs);
        try
        {
            c.Open();
            c.Exec("PRAGMA query_only = 1");
            return c;
        }
        catch { c.Dispose(); throw; }
    }

    /// <summary>Kilit ve geçici okuma hatalarında 3 kez dener; yine olmazsa son hatayı atar.</summary>
    public static T Retry<T>(Func<T> work)
    {
        for (var i = 0; ; i++)
        {
            try { return work(); }
            catch (SqliteException e) when (i < 2 && e.SqliteErrorCode is 5 or 6 or 10)
            { Thread.Sleep(150); }
        }
    }

    public static bool HasTable(SqliteConnection c, string table)
        => c.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name=@n", null, ("@n", table)) > 0;

    public static HashSet<string> Columns(SqliteConnection c, string table)
    {
        // PRAGMA table_info salt okunur bir sorgudur (tablo adı biz yazdığımız sabit).
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in c.Query($"SELECT name FROM pragma_table_info('{table}')", r => r.GetString(0))) set.Add(n);
        return set;
    }

    /// <summary>Epoch saniye (Hermes) → ISO UTC.</summary>
    public static string? IsoFromSeconds(double? s)
        => s is null || double.IsNaN(s.Value) || s.Value <= 0 || s.Value > 4_000_000_000 ? null : Time.Iso(DateTime.UnixEpoch.AddSeconds(s.Value));

    /// <summary>Epoch milisaniye (OpenCode) → ISO UTC.</summary>
    public static string? IsoFromMillis(long? ms)
        => ms is null || ms.Value <= 0 || ms.Value > 4_000_000_000_000 ? null : Time.Iso(DateTime.UnixEpoch.AddMilliseconds(ms.Value));

    public static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];
}
