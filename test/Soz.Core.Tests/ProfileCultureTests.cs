using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Profile;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Hermes state.db zamanı ondalıklı (REAL) olunca tr-TR makinede 0001-01-01 çıkıyordu (reader.GetValue(i).ToString() kültüre duyarlı: "1763810000,123").
/// Bu sınıf: sentetik SQLite, üç kültür (tr-TR, en-US, değişmez), zaman yoksa null (asla 0001).
/// </summary>
public sealed class ProfileCultureTests
{
    private static string Fixture()
    {
        var p = Path.Combine(TestEnv.RepoRoot, "_gecici", "p3", "sentetik", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p); return p;
    }

    public static readonly string[] Cultures = ["tr-TR", "en-US", ""];

    private static void WithCulture(string name, Action body)
    {
        var thread = Thread.CurrentThread;
        var old = (thread.CurrentCulture, thread.CurrentUICulture);
        var culture = CultureInfo.GetCultureInfo(name);
        try { thread.CurrentCulture = culture; thread.CurrentUICulture = culture; body(); }
        finally { thread.CurrentCulture = old.CurrentCulture; thread.CurrentUICulture = old.CurrentUICulture; }
    }

    private static readonly DateTime T = new(2025, 11, 22, 10, 0, 0, DateTimeKind.Utc);
    private static readonly double Sec = 1763810000.123;
    private static DateTime FromSec(double s) => DateTime.UnixEpoch.AddSeconds(s);

    /// <summary>(etiket, SQLite değeri, beklenen an; null = zaman yok sayılır)</summary>
    public static IEnumerable<object?[]> Cases()
    {
        foreach (var c in Cultures)
        {
            yield return [c, "tam sayi", (long)1763810000, FromSec(1763810000)];
            yield return [c, "REAL saniye ondalikli", Sec, FromSec(Sec)];
            yield return [c, "REAL milisaniye ondalikli", 1763810000123.5, FromSec(1763810000.1235)];
            yield return [c, "REAL bilimsel", 1.76381e9, FromSec(1763810000)];
            yield return [c, "metin noktali", "1763810000.5", FromSec(1763810000.5)];
            yield return [c, "metin virgullu", "1763810000,5", FromSec(1763810000.5)];
            yield return [c, "metin bilimsel", "1.76381e9", FromSec(1763810000)];
            yield return [c, "metin NaN", "NaN", null];
            yield return [c, "metin sonsuz", "Infinity", null];
            yield return [c, "REAL eksi", -5.5, null];
            yield return [c, "tam sayi eksi", (long)-5, null];
            yield return [c, "metin eksi", "-1763810000", null];
            yield return [c, "metin bos", "  ", null];
            yield return [c, "metin cop", "yok", null];
            yield return [c, "ISO metin", T.ToString("O"), T];
            yield return [c, "ISO metin ofsetli", "2025-11-22T13:00:00+03:00", T];
        }
    }

    private static void AssertTime(DateTime? expected, DateTime? actual, string what)
    {
        if (expected is null) { Assert.True(actual is null, what + ": zaman yok sayılmalı (null), geldi: " + actual?.ToString("O")); return; }
        Assert.True(actual is not null && actual.Value.Year > 1, what + ": 0001 yılına düştü veya null: " + actual?.ToString("O"));
        Assert.True(Math.Abs((actual!.Value - expected.Value).TotalMilliseconds) < 1, what + $": beklenen {expected:O}, gelen {actual:O}");
    }

    [Theory, MemberData(nameof(Cases))]
    public void Hermes_StartedAndEnded(string culture, string label, object value, DateTime? expected) => WithCulture(culture, () =>
    {
        var path = Path.Combine(Fixture(), "state.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            c.Exec("CREATE TABLE sessions(id TEXT,cwd TEXT,started_at,ended_at)");
            c.Exec("INSERT INTO sessions VALUES('s',@cwd,@v,@v)", null, ("@cwd", @"E:\P"), ("@v", value));
        }
        var row = Assert.Single(ProfileSources.ReadDatabase("hermes", path, DateTime.UtcNow));
        AssertTime(expected, row.StartedAt, $"[{culture}] hermes started_at {label}");
        AssertTime(expected, row.LastActivity, $"[{culture}] hermes ended_at {label}");
    });

    [Theory, MemberData(nameof(Cases))]
    public void Hermes_LastMessageTimestamp_Fallback(string culture, string label, object value, DateTime? expected) => WithCulture(culture, () =>
    {
        var path = Path.Combine(Fixture(), "state.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            c.Exec("CREATE TABLE sessions(id TEXT,cwd TEXT,started_at,ended_at);CREATE TABLE messages(session_id TEXT,timestamp,content TEXT);");
            c.Exec("INSERT INTO sessions VALUES('s',@cwd,NULL,NULL)", null, ("@cwd", @"E:\P"));
            c.Exec("INSERT INTO messages VALUES('s',@v,'P3_PRIVATE_MESSAGE')", null, ("@v", value));
        }
        var row = Assert.Single(ProfileSources.ReadDatabase("hermes", path, DateTime.UtcNow));
        AssertTime(expected, row.LastActivity, $"[{culture}] hermes MAX(timestamp) {label}");
        Assert.DoesNotContain("P3_PRIVATE_MESSAGE", JsonSerializer.Serialize(row));
    });

    [Theory, MemberData(nameof(Cases))]
    public void OpenCode_CreatedAndUpdated(string culture, string label, object value, DateTime? expected) => WithCulture(culture, () =>
    {
        var path = Path.Combine(Fixture(), "opencode.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            c.Exec("CREATE TABLE session(id TEXT,directory TEXT,time_created,time_updated)");
            c.Exec("INSERT INTO session VALUES('s',@cwd,@v,@v)", null, ("@cwd", @"E:\P"), ("@v", value));
        }
        var row = Assert.Single(ProfileSources.ReadDatabase("opencode", path, DateTime.UtcNow));
        AssertTime(expected, row.StartedAt, $"[{culture}] opencode time_created {label}");
        AssertTime(expected, row.LastActivity, $"[{culture}] opencode time_updated {label}");
    });

    [Theory, InlineData("tr-TR"), InlineData("en-US"), InlineData("")]
    public void Jsonl_Timestamp_ParsedInvariantAndAsUtc(string culture) => WithCulture(culture, () =>
    {
        var dir = Fixture();
        // Saat dilimi yok: UTC sayılır (yerel saat varsayımı yok). Gün/ay sırası değişmez kültürle okunur (11/22/2026).
        foreach (var (stamp, expected) in new[] {
            ("2025-11-22T10:00:00Z", T), ("2025-11-22T10:00:00", T), ("2025-11-22T13:00:00+03:00", T), ("11/22/2025 10:00:00", T) })
        {
            var p = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(p, JsonSerializer.Serialize(new { sessionId = "j", cwd = @"E:\P", timestamp = stamp }) + "\n");
            var m = SessionMetadataReader.Read("claude", p, DateTime.UtcNow)!;
            AssertTime(expected, m.StartedAt, $"[{culture}] jsonl timestamp '{stamp}'");
            Assert.Equal(DateTimeKind.Utc, m.StartedAt!.Value.Kind);
        }
        var none = Path.Combine(dir, "none.jsonl");
        File.WriteAllText(none, JsonSerializer.Serialize(new { sessionId = "j", cwd = @"E:\P", timestamp = "yok" }) + "\n");
        Assert.Null(SessionMetadataReader.Read("claude", none, DateTime.UtcNow)!.StartedAt);
    });

    [Theory, InlineData("tr-TR"), InlineData("en-US"), InlineData("")]
    public void Scan_UnparseableTimes_NeverBecome0001_InStoreOrApi(string culture) => WithCulture(culture, () =>
    {
        var r = new ProfileInventoryTests.Rig();
        var db = Path.Combine(r.Roots.HermesHome, "state.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            c.Exec("CREATE TABLE sessions(id TEXT,cwd TEXT,started_at,ended_at,last_activity_at)");
            c.Exec("INSERT INTO sessions VALUES('muh',@cwd,'yok','yok',NULL),('iyi',@cwd,@t,@t,@t)", null, ("@cwd", @"E:\P"), ("@t", (double)1763810000.123));
        }
        var service = r.Service();
        var snapshot = service.Scan(r.Context());
        using (var c = r.Db.Open())
        {
            var stored = c.Query("SELECT session_id,last_activity,started_at FROM profile_session WHERE tool='hermes'", x => (Id: x.GetString(0), Last: x.GetString(1), Start: x.Str(2)));
            Assert.Equal(2, stored.Count);
            Assert.All(stored, s => { Assert.False(s.Last.StartsWith("0001"), "saklanan last_activity 0001: " + s.Id); Assert.False((s.Start ?? "").StartsWith("0001")); });
            var iyi = stored.Single(s => s.Id == "iyi");
            Assert.StartsWith("2025-11-22", iyi.Last);
        }
        var view = service.View().ToJsonString();
        Assert.DoesNotContain("0001-01-01", view);
        Assert.DoesNotContain("0001-01-01", JsonSerializer.Serialize(snapshot, ProfileService.JsonOptions));
        var sessions = service.Sessions(@"E:\P");
        var text = sessions.ToJsonString();
        Assert.DoesNotContain("0001-01-01", text);
        var rows = sessions["sessions"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Null(rows.Single(x => (string?)x!["id"] == "muh")!["lastActivity"]);
        Assert.NotNull(rows.Single(x => (string?)x!["id"] == "iyi")!["lastActivity"]);
        Assert.Contains("2025-11-22", (string?)rows.Single(x => (string?)x!["id"] == "iyi")!["lastActivity"]);
    });

    [Theory, InlineData("tr-TR"), InlineData("en-US"), InlineData("")]
    public void Scan_ProjectWithOnlyUnknownTimes_HasNullLastActivity(string culture) => WithCulture(culture, () =>
    {
        var r = new ProfileInventoryTests.Rig();
        var db = Path.Combine(r.Roots.HermesHome, "state.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            c.Exec("CREATE TABLE sessions(id TEXT,cwd TEXT,started_at,ended_at)");
            c.Exec("INSERT INTO sessions VALUES('muh',@cwd,NULL,'yok')", null, ("@cwd", @"E:\P"));
        }
        var service = r.Service();
        service.Scan(r.Context());
        var project = service.View()["projects"]!.AsArray().Single()!;
        Assert.Null(project["lastActivity"]);
        Assert.Null(project["firstActivity"]);
    });

    [Theory, InlineData("tr-TR"), InlineData("en-US"), InlineData("")]
    public void ParseTimestamp_Direct(string culture) => WithCulture(culture, () =>
    {
        Assert.Null(ProfileSources.ParseTimestamp(null));
        Assert.Null(ProfileSources.ParseTimestamp(""));
        Assert.Null(ProfileSources.ParseTimestamp("0001-01-01T00:00:00.0000000Z"));
        AssertTime(FromSec(1763810000.123), ProfileSources.ParseTimestamp("1763810000.123"), "metin");
        AssertTime(FromSec(1763810000.123), ProfileSources.ParseTimestamp("1763810000,123"), "metin virgullu");
        Assert.Null(ProfileSources.ParseTimestamp("1,763"));   // binlik ayırıcı olabilir: güvenle reddedilir
    });

    [Fact]
    public void ScannedAt_NullBeforeAnyScan_ThenScanEndUtc()
    {
        var r = new ProfileInventoryTests.Rig();
        var service = r.Service();
        Assert.True(service.View().ContainsKey("scannedAt"));
        Assert.Null(service.View()["scannedAt"]);
        r.Claude("s1", @"E:\P");
        service.Scan(r.Context());
        var raw = (string?)service.View()["scannedAt"];
        Assert.NotNull(raw);
        Assert.EndsWith("Z", raw);
        var at = DateTime.Parse(raw!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, at.Kind);
        Assert.Equal(r.Now, at);   // tarama bağlamının saati (UtcNow) bitiş anı olarak yazılır
        foreach (var key in new[] { "machine", "tools", "projects", "unassigned", "live", "defaults", "counts", "scanMs", "sources" }) Assert.NotNull(service.View()[key]);
    }
}
