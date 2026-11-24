using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Restore;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>s1 / B7: proje klasörü başka yere taşınınca kayıt, olay defteri ve geri alma noktaları yeni yere bağlanır (DB'ye elle yazmadan).</summary>
public class S1TasimaTests
{
    [Fact]
    public void Klasor_tasininca_proje_yolu_olay_ve_geri_alma_noktalari_yeni_yere_baglanir()
    {
        using var env = new TestEnv(copySamples: false);
        var eski = NodeSample.Create(env, "ornek-eski");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(eski);
        string uid;
        using (var c = env.Svc.Db.Open()) uid = (string)c.Scalar("SELECT uid FROM projects WHERE id=@i", null, ("@i", pid))!;

        using (var rs = new RestoreService(env.Svc.Db))
            Assert.True(rs.Snapshots.Take(new SnapshotRequest(eski, "manual", uid)).Ok);

        // Klasörün "taşınmış" hali: içeriği başka yerde.
        var yeni = Path.Combine(env.Root, "ornek-yeni-yer");
        Directory.CreateDirectory(yeni);
        foreach (var f in Directory.GetFiles(eski, "*", SearchOption.AllDirectories))
        {
            var hedef = Path.Combine(yeni, Path.GetRelativePath(eski, f));
            Directory.CreateDirectory(Path.GetDirectoryName(hedef)!);
            File.Copy(f, hedef);
        }

        var r = w.RelocateProject(pid, yeni);
        Assert.True(r.Changed);
        Assert.Equal(1, r.SnapshotsMoved);
        Assert.Equal(Path.GetFullPath(yeni), r.Path);

        using (var c = env.Svc.Db.Open())
        {
            Assert.Equal(Path.GetFullPath(yeni), (string)c.Scalar("SELECT path FROM projects WHERE id=@i", null, ("@i", pid))!);
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='project.relocated'"));
        }
        // Aynı yere ikinci çağrı sessizce "değişmedi" der, ikinci olay yazmaz.
        Assert.False(w.RelocateProject(pid, yeni).Changed);
        using (var c = env.Svc.Db.Open()) Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='project.relocated'"));

        // Geri alma noktası artık yeni klasörün adıyla bulunur.
        using var rs2 = new RestoreService(env.Svc.Db);
        using var g = rs2.Snapshots.Depo.Open();
        Assert.Equal(1L, g.ScalarLong("SELECT COUNT(*) FROM snapshot WHERE root_path=@r COLLATE NOCASE", null, ("@r", SnapshotEngine.NormalizeRoot(yeni))));
    }

    [Fact]
    public void Tasima_hatalari_acik_soylenir()
    {
        using var env = new TestEnv(copySamples: false);
        var a = NodeSample.Create(env, "ornek-a");
        var b = NodeSample.Create(env, "ornek-b");
        var w = new WorkService(env.Svc.Db);
        var pa = w.EnsureProjectByPath(a);
        w.EnsureProjectByPath(b);
        Assert.Equal(409, Assert.Throws<SozException>(() => w.RelocateProject(pa, b)).Status);          // başka projenin klasörü
        Assert.Equal(404, Assert.Throws<SozException>(() => w.RelocateProject(pa, Path.Combine(env.Root, "yok-boyle"))).Status);
        Assert.Equal(404, Assert.Throws<SozException>(() => w.RelocateProject(99999, a)).Status);
    }
}
