using Microsoft.Data.Sqlite;
using Soz.Core.Cluster;
using Soz.Core.Data;
using Soz.Core.Packet;
using Soz.Core.Readers;
using Xunit;
using Xunit.Abstractions;

namespace Soz.Core.Tests;

public class ClusterTests
{
    private readonly ITestOutputHelper _out;
    public ClusterTests(ITestOutputHelper o) => _out = o;

    private static readonly string[] SameRule =
    {
        "Bundan sonra her zaman Türkçe yaz.",
        "Cevapları hep Türkçe yaz, unutma.",
        "Kaç kere söyledim, Türkçe yazmalısın.",
        "Her zaman Türkçe yazacaksın, bu bir kural.",
    };

    private static readonly string[] Unrelated =
    {
        "KarincaAI.exe dosyasını asla çalıştırma.",
        "Ağır kurulumları her zaman E diskine yap.",
        "Veritabanı olarak SQLite ile devam edelim.",
    };

    [Fact]
    public void Four_Phrasings_Form_One_Cluster_Unrelated_Stay_Apart()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var all = SameRule.Concat(Unrelated).ToArray();
        for (var i = 0; i < all.Length; i++)
        {
            var (sid, pid) = IngestStore.EnsureSession(c, null, "claude", "s" + i, @"E:\p", null, "human");
            var uid = IngestStore.InsertUtterance(c, null, sid, pid, $"2026-11-0{i + 1}T10:00:00.000Z", all[i], "transcript", 1)!.Value;
            c.Exec("INSERT INTO candidates(utterance_id, project_id, type, text, score) VALUES(@u,@p,'rule',@t,0.8)", null, ("@u", uid), ("@p", pid), ("@t", all[i]));
        }
        foreach (var a in SameRule)
            foreach (var b in SameRule)
                if (a != b) _out.WriteLine($"{Similarity.Score(a, b):0.00}  {a} ~ {b}");

        var stats = new RepeatClusterer(0.55).Run(c);
        var clusters = c.Query("SELECT id, count, session_count FROM clusters WHERE parent_id IS NULL", r => (id: r.GetInt64(0), n: r.Long(1), s: r.Long(2)));
        Assert.Equal(4, clusters.Count); // 1 küme (4 söyleyiş) + 3 ilgisiz
        Assert.Contains(clusters, k => k.n == 4 && k.s == 4);
        Assert.Equal(1, stats.NewRepeatClusters);
        var groupOfFirst = c.ScalarLong("SELECT cluster_id FROM candidates WHERE text=@t", null, ("@t", SameRule[0]));
        foreach (var u in Unrelated)
            Assert.NotEqual(groupOfFirst, c.ScalarLong("SELECT cluster_id FROM candidates WHERE text=@t", null, ("@t", u)));
    }

    [Fact]
    public void Similar_Clusters_In_Two_Projects_Get_General_Parent()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var data = new[] { (@"E:\a", "Bundan sonra her zaman Türkçe yaz."), (@"E:\b", "Her zaman Türkçe yaz, unutma."), (@"E:\b", "Veritabanı olarak SQLite ile devam edelim.") };
        var i = 0;
        foreach (var (dir, text) in data)
        {
            var (sid, pid) = IngestStore.EnsureSession(c, null, "claude", "x" + i, dir, null, "human");
            var uid = IngestStore.InsertUtterance(c, null, sid, pid, $"2026-11-0{++i}T10:00:00.000Z", text, "transcript", 1)!.Value;
            c.Exec("INSERT INTO candidates(utterance_id, project_id, type, text, score) VALUES(@u,@p,'rule',@t,0.8)", null, ("@u", uid), ("@p", pid), ("@t", text));
        }
        var stats = new RepeatClusterer(0.55).Run(c);
        Assert.Equal(1, stats.ParentClusters);
        var star = c.ScalarLong("SELECT id FROM projects WHERE key='*'");
        var parent = c.Query("SELECT id, count, session_count FROM clusters WHERE project_id=@s", r => (id: r.GetInt64(0), n: r.Long(1), s: r.Long(2)), null, ("@s", star)).Single();
        Assert.Equal(2, parent.n);
        Assert.Equal(2, parent.s);
        Assert.Equal(2L, c.ScalarLong("SELECT COUNT(*) FROM clusters WHERE parent_id=@p", null, ("@p", parent.id)));
    }
}

public class PacketTests
{
    private static long Add(SqliteConnection c, long pid, string type, string text, string status = "approved", bool pinned = false)
    {
        c.Exec("INSERT INTO decisions(project_id,type,text,status,pinned,repeat_count,created_at,updated_at) VALUES(@p,@t,@x,@s,@pin,1,'2026-11-10','2026-11-10')", null,
            ("@p", pid), ("@t", type), ("@x", text), ("@s", status), ("@pin", pinned ? 1 : 0));
        return c.ScalarLong("SELECT last_insert_rowid()");
    }

    [Fact]
    public void Empty_When_No_Approved()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var pid = IngestStore.EnsureProject(c, null, @"E:\proje");
        Add(c, pid, "rule", "Önerilmiş ama onaylanmamış kural.", "proposed");
        var p = PacketBuilder.Build(c, pid, 2000);
        Assert.Equal("", p.Text);
        Assert.Equal(0, p.DecisionCount);
    }

    [Fact]
    public void Order_Sections_And_Budget()
    {
        using var env = new TestEnv(copySamples: false);
        using var c = env.Svc.Db.Open();
        var pid = IngestStore.EnsureProject(c, null, @"E:\proje");
        var star = c.ScalarLong("SELECT id FROM projects WHERE key='*'");
        Add(c, pid, "preference", "Kısa cümle tercih edilir.");
        Add(c, pid, "decision", "Veritabanı SQLite.");
        Add(c, pid, "ban", "KarincaAI.exe çalıştırılmaz.");
        Add(c, star, "rule", "Her zaman Türkçe yaz.");
        Add(c, pid, "rule", "Testleri teslimden önce çalıştır.", pinned: true);
        Add(c, pid, "rule", "Arşivlenmiş kural.", "archived");

        var p = PacketBuilder.Build(c, pid, 2000);
        Assert.StartsWith("[Söz defteri · proje] Kullanıcının daha önce verdiği, hâlâ geçerli kararlar.", p.Text);
        var lines = p.Text.Split('\n');
        Assert.Equal(new[] { "Kurallar:", "- Testleri teslimden önce çalıştır.", "- Her zaman Türkçe yaz.", "Yasaklar:", "- KarincaAI.exe çalıştırılmaz.",
            "Kararlar:", "- Veritabanı SQLite.", "Tercihler:", "- Kısa cümle tercih edilir." }, lines[1..]);
        Assert.DoesNotContain("Arşivlenmiş", p.Text);
        Assert.Equal(5, p.DecisionCount);
        Assert.Equal(p.Text.Length, p.Chars);

        // Bütçe: başlık + sabitlenmiş + genel kural sığar, gerisi düşer; boş bölüm yazılmaz.
        var header = lines[0].Length + 1;
        var budget = header + "Kurallar:\n".Length + "- Testleri teslimden önce çalıştır.\n".Length + "- Her zaman Türkçe yaz.\n".Length;
        var small = PacketBuilder.Build(c, pid, budget);
        Assert.Equal(2, small.DecisionCount);
        Assert.DoesNotContain("Yasaklar:", small.Text);
        Assert.True(small.Chars <= budget);
    }
}
