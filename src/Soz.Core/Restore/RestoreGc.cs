using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Restore;

public sealed record PruneResult(int Snapshots, long FreedBytes, int PacksRemoved, int PacksCompacted, int LooseRemoved);

/// <summary>
/// Görüntü ömrü ve çöp toplama (rapor 4.6). Ömür: kabul edilmemiş/açık işin noktaları süresiz; kabul edilmiş iş 7 gün sonra silinebilir ama projede son 5 nokta kalır;
/// "geri almadan önce" noktaları (ve uygulanmış geri almaların noktaları) 30 gün; sabitlenmiş nokta süresiz. Bütçe aşımında (Aggressive) kabul edilmişlerin
/// en eskisinden gider; kabul edilmemiş işin noktasına dokunulmaz. Çöp toplama sayaçlara güvenmez: kalan manifestlerden erişilebilen blob kümesini işaretler, kalanı süpürür.
/// </summary>
public static class RestoreGc
{
    private static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(10);

    public static PruneResult Prune(GeriDepo depo, SnapshotEngine snaps, SozDb soz, DateTime nowUtc, bool aggressive = false)
    {
        depo.Gate.EnterWriteLock();
        try
        {
            var removedSnaps = DropSnapshots(depo, snaps, soz, nowUtc, aggressive);
            var (freed, packsRemoved, packsCompacted, loose) = Sweep(depo);
            snaps.ForgetCache();
            depo.ResetKnown();
            return new PruneResult(removedSnaps, freed, packsRemoved, packsCompacted, loose);
        }
        finally { depo.Gate.ExitWriteLock(); }
    }

    // ---------------------------------------------------------------- ömür

    private static int DropSnapshots(GeriDepo depo, SnapshotEngine snaps, SozDb soz, DateTime now, bool aggressive)
    {
        var o = depo.Options;
        var keepAcceptedDays = aggressive ? 0 : o.KeepAcceptedDays;
        // soz.db tarafı: hangi nokta uid'si hangi iş durumuna bağlı
        var protectedUids = new HashSet<string>(StringComparer.Ordinal);   // açık (kabul edilmemiş) işin noktaları
        var droppableUids = new HashSet<string>(StringComparer.Ordinal);   // kabul edilmiş ve süresi dolmuş işin noktaları
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var undoKeep = new HashSet<string>(StringComparer.Ordinal);
        using (var c = soz.Open())
        {
            var rows = c.Query("""
                SELECT r.before_snapshot_uid, r.after_snapshot_uid, COALESCE(j.accepted_by_user,0), COALESCE(j.finished_at, j.created_at, r.started)
                FROM runs r LEFT JOIN jobs j ON j.id=r.job_id WHERE r.before_snapshot_uid IS NOT NULL OR r.after_snapshot_uid IS NOT NULL
                """, r => (b: r.Str(0), a: r.Str(1), acc: r.GetInt64(2) != 0, at: Time.Parse(r.Str(3))));
            foreach (var r in rows)
            {
                var expired = r.acc && (r.at is null || now - r.at.Value > TimeSpan.FromDays(keepAcceptedDays));
                foreach (var u in new[] { r.b, r.a })
                {
                    if (u is null) continue;
                    referenced.Add(u);
                    if (expired) droppableUids.Add(u); else protectedUids.Add(u);
                }
            }
            // uygulanmış geri almaların noktaları 30 gün ("geri almayı geri al" penceresi)
            foreach (var r in c.Query("SELECT pre_snapshot_uid, post_snapshot_uid, target_snapshot_uid, COALESCE(finished_at, started_at) FROM undo WHERE status IN ('applied','previewed')",
                         r => (p: r.Str(0), q: r.Str(1), t: r.Str(2), at: Time.Parse(r.Str(3)))))
                if (r.at is null || now - r.at.Value <= TimeSpan.FromDays(o.UndoPointDays))
                    foreach (var u in new[] { r.p, r.q, r.t }) if (u is not null) undoKeep.Add(u);
        }

        var removed = 0;
        using var g = depo.Open();
        var roots = g.Query("SELECT DISTINCT lower(root_path) FROM snapshot WHERE state='ok'", r => r.GetString(0));
        foreach (var root in roots)
        {
            var list = depo.ListFor(g, root).Where(s => s.State == "ok").ToList();
            var lastN = new HashSet<long>(list.OrderByDescending(s => s.Id).Take(o.KeepLastPoints).Select(s => s.Id));
            bool Keep(SnapshotRow s)
            {
                if (s.Pinned || lastN.Contains(s.Id)) return true;
                if (protectedUids.Contains(s.Uid) || undoKeep.Contains(s.Uid)) return true;
                if (droppableUids.Contains(s.Uid)) return false;
                var taken = Time.Parse(s.TakenAt);
                if (s.Kind is "before_undo" or "after_undo") return taken is null || now - taken.Value <= TimeSpan.FromDays(o.UndoPointDays);
                // hiçbir koşunun bağlamadığı koşu noktası (ör. başarısız başlatma): bir gün sonra gider
                if (!referenced.Contains(s.Uid)) return taken is null || now - taken.Value <= TimeSpan.FromDays(1);
                return true;
            }
            // yalnız en eski kesintisiz öneki silinebilir: ardındaki nokta zincirde ona yaslanabilir (en eski kalan nokta tam görüntüye çevrilir)
            var firstKept = list.FindIndex(s => Keep(s));
            var cut = firstKept < 0 ? list.Count : firstKept;
            if (cut == 0) continue;
            if (firstKept >= 0) MakeFull(depo, snaps, list[firstKept]);
            using var tx = g.BeginTransaction();
            foreach (var s in list.Take(cut))
            {
                g.Exec("DELETE FROM snapshot_entry WHERE snapshot_id=@i", tx, ("@i", s.Id));
                g.Exec("DELETE FROM snapshot WHERE id=@i", tx, ("@i", s.Id));
                removed++;
            }
            tx.Commit();
        }
        return removed;
    }

    /// <summary>Bir noktayı kendi kendine yeten tam görüntüye çevirir (ebeveyni silinecek).</summary>
    private static void MakeFull(GeriDepo depo, SnapshotEngine snaps, SnapshotRow s)
    {
        if (s.Full || s.ParentId is null) return;
        var state = snaps.Resolve(s.Id);
        using var c = depo.Open();
        using var tx = c.BeginTransaction();
        c.Exec("DELETE FROM snapshot_entry WHERE snapshot_id=@i", tx, ("@i", s.Id));
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO snapshot_entry(snapshot_id,path,op,size,mtime_ns,blob,skip_reason,attr) VALUES($s,$p,'a',$z,$m,$b,$k,$a)";
            var p = cmd.Parameters;
            var pS = p.Add("$s", SqliteType.Integer); var pP = p.Add("$p", SqliteType.Text); var pZ = p.Add("$z", SqliteType.Integer);
            var pM = p.Add("$m", SqliteType.Integer); var pB = p.Add("$b", SqliteType.Text); var pK = p.Add("$k", SqliteType.Text); var pA = p.Add("$a", SqliteType.Integer);
            pS.Value = s.Id;
            foreach (var r in state.Values)
            {
                pP.Value = r.Path; pZ.Value = r.IsDir ? -1 : r.Size; pM.Value = r.MtimeNs;
                pB.Value = (object?)r.Blob ?? DBNull.Value; pK.Value = (object?)r.Skip ?? DBNull.Value; pA.Value = r.Attr;
                cmd.ExecuteNonQuery();
            }
        }
        c.Exec("UPDATE snapshot SET full=1, parent_id=NULL, depth=0 WHERE id=@i", tx, ("@i", s.Id));
        tx.Commit();
    }

    // ---------------------------------------------------------------- işaretle ve süpür

    private static (long freed, int packsRemoved, int packsCompacted, int loose) Sweep(GeriDepo depo)
    {
        long freed = 0; int packsRemoved = 0, packsCompacted = 0, looseRemoved = 0;
        using var c = depo.Open();

        // işaretle: kalan manifestlerin başvurduğu blob'lar (ve parçalı blob'ların parçaları)
        c.Exec("DROP TABLE IF EXISTS temp.live; CREATE TEMP TABLE live(hash TEXT PRIMARY KEY) WITHOUT ROWID;");
        c.Exec("INSERT OR IGNORE INTO temp.live SELECT DISTINCT blob FROM snapshot_entry WHERE blob IS NOT NULL");
        c.Exec("INSERT OR IGNORE INTO temp.live SELECT chunk_hash FROM blob_parca WHERE blob_hash IN (SELECT hash FROM temp.live)");

        // süpür 1: erişilemeyen blob satırları. Dosya olanların dosyası silinir; paketteki baytlar paket kapanınca ya da sıkıştırılınca gider.
        var dead = c.Query("SELECT hash, stored_size, pak FROM blob WHERE hash NOT IN (SELECT hash FROM temp.live)", r => (hash: r.GetString(0), size: r.GetInt64(1), pak: r.LongN(2)));
        using (var tx = c.BeginTransaction())
        {
            foreach (var d in dead)
            {
                if (d.pak is null)
                {
                    var f = depo.LooseBlobPath(d.hash);
                    try { if (File.Exists(f)) { File.Delete(f); freed += d.size; looseRemoved++; } } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                c.Exec("DELETE FROM blob WHERE hash=@h", tx, ("@h", d.hash));
                c.Exec("DELETE FROM blob_parca WHERE blob_hash=@h", tx, ("@h", d.hash));
            }
            tx.Commit();
        }

        // süpür 2: paketler. Hiç canlı blob'u kalmayan paket silinir, canlı oranı yarıdan azsa canlılar yeni pakete taşınır.
        foreach (var file in Directory.EnumerateFiles(depo.PackDir, "*.pak").ToList())
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;
            var live = c.Query("SELECT hash, size, stored_size, codec, pak, ofs FROM blob WHERE pak=@p ORDER BY ofs",
                r => new BlobRow(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5)), null, ("@p", id));
            var fi = new FileInfo(file);
            if (live.Count == 0)
            {
                // satırı hiç olmayan paket yeni bir görüntünün yazdığı olabilir: yeterince eskiyse yetimdir
                if (DateTime.UtcNow - fi.LastWriteTimeUtc < OrphanAge) continue;
                depo.ForgetPackReader(id);
                try { freed += fi.Length; fi.Delete(); packsRemoved++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                continue;
            }
            var liveBytes = live.Sum(b => b.Stored);
            if (fi.Length > 0 && liveBytes * 2 < fi.Length)
            {
                var (newId, handle) = depo.OpenNewPack();
                long ofs = 0;
                var moved = new List<(string hash, long ofs)>();
                try
                {
                    foreach (var b in live) { var data = depo.ReadStored(b); RandomAccess.Write(handle, data, ofs); moved.Add((b.Hash, ofs)); ofs += data.Length; }
                    if (depo.Options.Fsync) RandomAccess.FlushToDisk(handle);
                }
                finally { handle.Dispose(); }
                using (var tx = c.BeginTransaction())
                {
                    foreach (var (h, newOfs) in moved) c.Exec("UPDATE blob SET pak=@n, ofs=@o WHERE hash=@h", tx, ("@n", newId), ("@o", newOfs), ("@h", h));
                    tx.Commit();
                }
                depo.ForgetPackReader(id);
                try { freed += fi.Length - ofs; fi.Delete(); packsCompacted++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // süpür 3: satırı olmayan eski dosya blob'ları ve geçici dosyalar (başarısız görüntülerden kalanlar)
        foreach (var sub in Directory.EnumerateDirectories(depo.BlobDir))
            foreach (var f in Directory.EnumerateFiles(sub))
            {
                var fi = new FileInfo(f);
                if (DateTime.UtcNow - fi.LastWriteTimeUtc < OrphanAge) continue;
                var hash = Path.GetFileName(sub) + Path.GetFileName(f);
                if (c.Scalar("SELECT 1 FROM blob WHERE hash=@h", null, ("@h", hash)) is not null) continue;
                try { freed += fi.Length; fi.Delete(); looseRemoved++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        c.Exec("DROP TABLE IF EXISTS temp.live");
        return (freed, packsRemoved, packsCompacted, looseRemoved);
    }
}
