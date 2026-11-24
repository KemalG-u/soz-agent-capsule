using System.Globalization;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Discovery;

/// <summary>Bir an: evrensel zaman (kayıt damgası için) ve yerel zaman (gün ve saat dilimi için). Testler ikisini açıkça verir.</summary>
public readonly record struct Moment(DateTime Utc, DateTime Local)
{
    public static Moment Now() { var u = Time.UtcNow(); return new Moment(u, u.ToLocalTime()); }
    public string Iso => Time.Iso(Utc);
}

/// <summary>
/// Aktif gün sayacı (repo-4 2.2 sorun 3): yalnız Söz'ün kullanıldığı günler 1, 2, 3 diye numaralanır. Yaş "kaç aktif gün önce" diye ölçülür;
/// 14 günlük tatil kullanımları eskitmez. Bugün hiç kullanım yoksa "şimdi" en son aktif gündür (bugün sayılmaz).
/// </summary>
public static class ActiveDays
{
    public static string DayKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Bugünün sıra numarası; bugün henüz yoksa bir sonraki numarayla eklenir. Çağıranın yazma işleminde çalışır.</summary>
    public static long Today(SqliteConnection c, SqliteTransaction? tx, DateTime local)
    {
        var day = DayKey(local);
        if (c.Scalar("SELECT seq FROM discovery_active_day WHERE day=@d", tx, ("@d", day)) is long s) return s;
        var seq = c.ScalarLong("SELECT COALESCE(MAX(seq),0)+1 FROM discovery_active_day", tx);
        c.Exec("INSERT INTO discovery_active_day(day, seq) VALUES(@d,@s)", tx, ("@d", day), ("@s", seq));
        return seq;
    }

    /// <summary>Salt okunur "şimdi": bugün aktifse bugünün numarası, değilse en son aktif günün numarası (hiç yoksa 0).</summary>
    public static long Current(SqliteConnection c, DateTime local, SqliteTransaction? tx = null)
        => c.Scalar("SELECT seq FROM discovery_active_day WHERE day=@d", tx, ("@d", DayKey(local))) is long s
            ? s
            : c.ScalarLong("SELECT COALESCE(MAX(seq),0) FROM discovery_active_day", tx);

    private static readonly string[] Dow = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    /// <summary>Zaman dilimi anahtarı: hafta günü ve saat dilimi, ör. "mon-09-12" (blok 3 saat). Blok uzunluğu 24'ü bölmelidir.</summary>
    public static string TimeKey(DateTime local, int blockHours)
    {
        if (blockHours is < 1 or > 24 || 24 % blockHours != 0) throw new ArgumentOutOfRangeException(nameof(blockHours), "Saat dilimi uzunluğu 24'ü bölmeli.");
        var start = local.Hour / blockHours * blockHours;
        return $"{Dow[(int)local.DayOfWeek]}-{start:00}-{start + blockHours:00}";
    }
}
