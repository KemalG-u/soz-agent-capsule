using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Soz.Core.Data;

namespace Soz.Core.Restore;

public static class ProtectionLevel
{
    public const string Full = "tam", Limited = "sinirli", None = "yok";
}

public sealed record SnapshotRequest(string Root, string Kind, string ProjectUid, string? JobUid = null, string? RunUid = null);

/// <summary>
/// Görüntü sonucu. Ok=false ise nokta ALINAMADI: Protection "yok", ErrorCode kısa kod (cok-dosya, disk-yetersiz, butce, klasor-yok, hata), Note kişiye gösterilecek sade Türkçe neden.
/// IsNew=false: klasör önceki noktadan beri değişmemişti, yeni nokta yazılmadı (Id/Uid önceki noktanındır).
/// </summary>
public sealed record SnapshotResult(bool Ok, long Id, string? Uid, bool IsNew, string Protection, string? Note, string? ErrorCode,
    int Files, long TotalBytes, long NewBytes, int Skipped, long ScanMs, long StoreMs)
{
    public static SnapshotResult Failed(string code, string note) => new(false, 0, null, false, ProtectionLevel.None, note, code, 0, 0, 0, 0, 0, 0);
}

/// <summary>Düşük öncelikli iş parçacıklarıyla paralel çalıştırır (kişinin bilgisayarı akıcı kalsın). İlk hata çalışma bitince yeniden atılır.</summary>
internal static class WorkPool
{
    public static void ForEach<T>(IReadOnlyList<T> items, int degree, Action<T> action)
    {
        if (items.Count == 0) return;
        degree = Math.Clamp(degree, 1, Math.Max(1, items.Count));
        var next = -1;
        Exception? error = null;
        var threads = new Thread[degree];
        for (var t = 0; t < degree; t++)
        {
            threads[t] = new Thread(() =>
            {
                while (Volatile.Read(ref error) is null)
                {
                    var i = Interlocked.Increment(ref next);
                    if (i >= items.Count) return;
                    try { action(items[i]); }
                    catch (Exception e) { Interlocked.CompareExchange(ref error, e, null); return; }
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "soz-geri" };
            threads[t].Start();
        }
        foreach (var th in threads) th.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}

/// <summary>
/// Anlık görüntü motoru (spike: calisma\spike-geri-alma-net.md). Klasörü tarar, önceki noktayla boyut+mtime ile karşılaştırır, yalnız değişenleri özetler ve depoya yazar.
/// Fark zinciri: her nokta ebeveynine göre yalnız değişen satırları taşır, <see cref="RestoreOptions.FullEvery"/> noktada bir tam görüntü yazılır.
/// Görüntü alınamazsa istisna FIRLATMAZ: <see cref="SnapshotResult.Ok"/>=false ve sade neden döner (koşu bundan durmaz).
/// </summary>
public sealed class SnapshotEngine : IDisposable
{
    public GeriDepo Depo { get; }
    public RestoreOptions Options => Depo.Options;
    private readonly Dictionary<long, PointState> _cache = new();
    private readonly Queue<long> _cacheOrder = new();
    private readonly object _cacheLock = new();
    private readonly ConcurrentDictionary<string, object> _rootLocks = new(StringComparer.OrdinalIgnoreCase);

    public SnapshotEngine(GeriDepo depo) { Depo = depo; }

    /// <summary>Aynı kökte (görüntü + geri alma) tek işlem. Monitor yeniden girilebilir: geri alma kilidi tutarken görüntü de alabilir.</summary>
    public object RootLock(string root) => _rootLocks.GetOrAdd(NormalizeRoot(root), _ => new object());

    public static string NormalizeRoot(string root) => Path.GetFullPath(root).TrimEnd('\\', '/');

    // ---------------------------------------------------------------- görüntü al

    public SnapshotResult Take(SnapshotRequest req)
    {
        var root = NormalizeRoot(req.Root);
        lock (RootLock(root))
        {
            Depo.Gate.EnterReadLock();
            try { return TakeCore(req, root); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return SnapshotResult.Failed("hata", "Klasör okunurken ya da geri alma deposuna yazılırken bir hata oldu: " + Short(e.Message));
            }
            finally { Depo.Gate.ExitReadLock(); }
        }
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] : s;

    private SnapshotResult TakeCore(SnapshotRequest req, string root)
    {
        if (!Directory.Exists(root)) return SnapshotResult.Failed("klasor-yok", "Proje klasörü bulunamadı.");
        var top = Stopwatch.StartNew();
        var scan = FolderScanner.Scan(root, Options.MaxFiles);
        if (scan.TooMany) return SnapshotResult.Failed("cok-dosya", $"Klasörde {Options.MaxFiles:N0} dosyadan fazlası var, geri alma noktası alınamadı.");

        SnapshotRow? prev;
        using (var c = Depo.Open()) prev = Depo.LatestFor(c, root);
        var before = prev is null ? null : Resolve(prev.Id);

        // ---- karşılaştır: boyut + mtime aynıysa içeriğe bakılmaz ----
        // "Racily clean" tuzağına karşı (git ile aynı kural): önceki noktanın zamanından hemen önce (son 3 sn) yazılmış bir dosya aynı saat dilimi içinde
        // bir daha yazılmış olabilir; boyut ve zaman aynı kalır. Bu dosyalar önbelleğe güvenilmeyip yeniden özetlenir (az sayıda, ucuz).
        var racyFrom = long.MaxValue;
        if (prev is not null && Time.Parse(prev.TakenAt) is { } prevTaken) racyFrom = FolderScanner.ToNs(prevTaken.Ticks) - 3_000_000_000L;
        var racy = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<ScanItem>();
        var removed = new List<string>();
        var seen = prev is null ? null : new HashSet<string>(scan.Items.Count, StringComparer.OrdinalIgnoreCase);
        long estimate = 0;
        foreach (var it in scan.Items)
        {
            seen?.Add(it.Path);
            if (before is not null && before.TryGetValue(it.Path, out var p))
            {
                if (Same(p, it))
                {
                    if (it.IsDir || p.Skip is not null || it.MtimeNs < racyFrom) continue;
                    racy.Add(it.Path); // yeniden özetlenecek
                }
                else if (!string.Equals(p.Path, it.Path, StringComparison.Ordinal)) removed.Add(p.Path); // yalnız büyük-küçük harf farkı: eski ad 'd', yeni ad 'a'
            }
            changed.Add(it);
            if (!it.IsDir && it.Size <= Options.MaxCopyBytes) estimate += it.Size;
        }
        if (before is not null)
            foreach (var (k, v) in before) if (!seen!.Contains(k)) removed.Add(v.Path);

        if (prev is not null && changed.Count == 0 && removed.Count == 0)
            return new SnapshotResult(true, prev.Id, prev.Uid, false, prev.Protection, prev.ProtectionNote, null, (int)prev.FileCount, prev.TotalBytes, 0, 0, scan.Ms, 0);

        // ---- disk ve bütçe ----
        var refuse = CheckBudget(root, estimate, prev);
        if (refuse is not null) return refuse;

        // ---- içeriği depola ----
        var sw = Stopwatch.StartNew();
        var work = new StoreWork(Depo, Options);
        var recs = work.Run(root, changed);
        var storeMs = sw.ElapsedMilliseconds;
        if (racy.Count > 0 && prev is not null && removed.Count == 0 && work.Vanished.IsEmpty)
        {
            // yeniden özetlenenlerden hiçbiri değişmemişse (ve başka değişiklik yoksa) yeni nokta yazılmaz
            var real = false;
            foreach (var r in recs)
            {
                if (racy.Contains(r.Path) && before!.TryGetValue(r.Path, out var q) && q.Skip is null && r.Skip is null && q.Blob == r.Blob && q.Size == r.Size && q.MtimeNs == r.MtimeNs) continue;
                real = true; break;
            }
            if (!real) return new SnapshotResult(true, prev.Id, prev.Uid, false, prev.Protection, prev.ProtectionNote, null, (int)prev.FileCount, prev.TotalBytes, 0, 0, scan.Ms, storeMs);
        }

        // ---- yeni durum ----
        var state = before is not null ? new PointState(before) : new PointState();
        foreach (var r in removed) state.Remove(r);
        foreach (var r in recs) state[r.Path] = r;
        foreach (var gone in work.Vanished)
        {
            if (before is not null && before.TryGetValue(gone, out var bp)) removed.Add(bp.Path); // taramadan sonra silinmiş dosya
            state.Remove(gone);
        }

        var skipped = state.Values.Where(v => v.Skip is not null).ToList();
        var protection = skipped.Count == 0 ? ProtectionLevel.Full : ProtectionLevel.Limited;
        var note = skipped.Count == 0 ? null : SkipNote(skipped);
        long total = 0; var fileCount = 0;
        foreach (var v in state.Values) if (!v.IsDir) { total += v.Size; fileCount++; }

        var depth = prev is null ? 0 : prev.Depth + 1;
        var full = prev is null || depth >= Options.FullEvery;
        if (full) depth = 0;
        var uid = Uid.New();

        long id;
        using (var c = Depo.Open())
        {
            using var tx = c.BeginTransaction();
            id = c.ScalarLong("""
                INSERT INTO snapshot(uid,project_uid,root_path,kind,job_uid,run_uid,parent_id,full,depth,taken_at,scan_ms,store_ms,file_count,total_bytes,new_blob_bytes,protection,protection_note,dep_json,state)
                VALUES(@u,@p,@r,@k,@j,@ru,@pa,@f,@d,@t,@sm,@st,@fc,@tb,@nb,@pr,@pn,@dj,'ok'); SELECT last_insert_rowid();
                """, tx, ("@u", uid), ("@p", req.ProjectUid), ("@r", root), ("@k", req.Kind), ("@j", req.JobUid), ("@ru", req.RunUid),
                ("@pa", full ? null : prev!.Id), ("@f", full ? 1 : 0), ("@d", depth), ("@t", Time.NowIso()), ("@sm", scan.Ms), ("@st", storeMs),
                ("@fc", fileCount), ("@tb", total), ("@nb", work.NewBytes), ("@pr", protection), ("@pn", note), ("@dj", JsonSerializer.Serialize(scan.Deps)));
            InsertEntries(c, tx, id, full, state, recs, removed);
            InsertBlobs(c, tx, work);
            tx.Commit();
        }
        Depo.MarkKnown(work.NewBlobs.Select(b => b.Hash));
        Remember(id, state);
        return new SnapshotResult(true, id, uid, true, protection, note, null, fileCount, total, work.NewBytes, skipped.Count, scan.Ms, storeMs);
    }

    private static bool Same(FileRec p, ScanItem g)
        => p.IsDir == g.IsDir && string.Equals(p.Path, g.Path, StringComparison.Ordinal)
           && (g.IsDir || (p.Size == g.Size && p.MtimeNs == g.MtimeNs && (p.Skip is null or "buyuk"))); // klasörde yalnız varlık sayılır (mtime içerik değişince oynar)

    private static string SkipNote(List<FileRec> skipped)
    {
        var big = skipped.Count(s => s.Skip == "buyuk");
        var locked = skipped.Count(s => s.Skip == "kilitli");
        var other = skipped.Count - big - locked;
        var parts = new List<string>();
        if (big > 0) parts.Add($"{big} dosya çok büyük");
        if (locked > 0) parts.Add($"{locked} dosya başka program tarafından kilitli");
        if (other > 0) parts.Add($"{other} dosya okunamadı");
        return string.Join(", ", parts) + ": bunların içeriği saklanmadı, geri alınamaz.";
    }

    private SnapshotResult? CheckBudget(string root, long estimate, SnapshotRow? prev)
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Depo.Dir)!).AvailableFreeSpace;
            if (estimate > 0 && free < estimate * Options.DiskFreeFactor)
                return SnapshotResult.Failed("disk-yetersiz", "Disk dolmak üzere olduğu için geri alma noktası alınamadı. Bu iş korumasız çalışır.");
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { /* ağ yolu vb.: boş yer bilinemiyor, denemeye devam */ }

        long stored;
        using (var c = Depo.Open()) stored = Depo.StoredBytes(c);
        var projectBytes = prev?.TotalBytes ?? estimate;
        var projectBudget = Math.Max(Options.MinProjectBudgetBytes, (long)(Options.ProjectBudgetFactor * projectBytes));
        var budget = Math.Min(projectBudget, Options.GlobalBudgetBytes);
        if (stored + estimate > budget && stored + estimate > Options.GlobalBudgetBytes)
            return SnapshotResult.Failed("butce", "Geri alma noktaları için ayrılan disk alanı doldu. Bu iş korumasız çalışır. Eski noktalar süresi dolunca temizlenir.");
        return null;
    }

    private static void InsertEntries(SqliteConnection c, SqliteTransaction tx, long id, bool full, PointState state, List<FileRec> recs, List<string> removed)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO snapshot_entry(snapshot_id,path,op,size,mtime_ns,blob,skip_reason,attr) VALUES($s,$p,$o,$z,$m,$b,$k,$a)";
        var ps = cmd.Parameters;
        var pS = ps.Add("$s", SqliteType.Integer); var pP = ps.Add("$p", SqliteType.Text); var pO = ps.Add("$o", SqliteType.Text);
        var pZ = ps.Add("$z", SqliteType.Integer); var pM = ps.Add("$m", SqliteType.Integer); var pB = ps.Add("$b", SqliteType.Text);
        var pK = ps.Add("$k", SqliteType.Text); var pA = ps.Add("$a", SqliteType.Integer);
        pS.Value = id;
        cmd.Prepare();
        void Row(FileRec r)
        {
            pP.Value = r.Path; pO.Value = "a"; pZ.Value = r.IsDir ? -1 : r.Size; pM.Value = r.MtimeNs;
            pB.Value = (object?)r.Blob ?? DBNull.Value; pK.Value = (object?)r.Skip ?? DBNull.Value; pA.Value = r.Attr;
            cmd.ExecuteNonQuery();
        }
        if (full) { foreach (var r in state.Values) Row(r); return; }
        foreach (var path in removed)
        {
            pP.Value = path; pO.Value = "d"; pZ.Value = DBNull.Value; pM.Value = DBNull.Value; pB.Value = DBNull.Value; pK.Value = DBNull.Value; pA.Value = 0;
            cmd.ExecuteNonQuery();
        }
        foreach (var r in recs) Row(r);
    }

    private static void InsertBlobs(SqliteConnection c, SqliteTransaction tx, StoreWork work)
    {
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO blob(hash,size,stored_size,codec,first_seen,pak,ofs) VALUES($h,$z,$s,$c,$f,$p,$o)";
            var ps = cmd.Parameters;
            var h = ps.Add("$h", SqliteType.Text); var z = ps.Add("$z", SqliteType.Integer); var s = ps.Add("$s", SqliteType.Integer);
            var cc = ps.Add("$c", SqliteType.Text); var f = ps.Add("$f", SqliteType.Text); var p = ps.Add("$p", SqliteType.Integer); var o = ps.Add("$o", SqliteType.Integer);
            f.Value = Time.NowIso()[..10];
            cmd.Prepare();
            foreach (var b in work.NewBlobs)
            {
                h.Value = b.Hash; z.Value = b.Size; s.Value = b.Stored; cc.Value = b.Codec;
                p.Value = (object?)b.Pak ?? DBNull.Value; o.Value = (object?)b.Ofs ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
        if (work.NewChunks.Count == 0) return;
        using var cc2 = c.CreateCommand();
        cc2.Transaction = tx;
        cc2.CommandText = "INSERT OR IGNORE INTO blob_parca(blob_hash,seq,chunk_hash,size) VALUES($b,$s,$c,$z)";
        var b2 = cc2.Parameters.Add("$b", SqliteType.Text); var s2 = cc2.Parameters.Add("$s", SqliteType.Integer);
        var c2 = cc2.Parameters.Add("$c", SqliteType.Text); var z2 = cc2.Parameters.Add("$z", SqliteType.Integer);
        foreach (var ch in work.NewChunks) { b2.Value = ch.BlobHash; s2.Value = ch.Seq; c2.Value = ch.ChunkHash; z2.Value = ch.Size; cc2.ExecuteNonQuery(); }
    }

    // ---------------------------------------------------------------- nokta çözme

    private void Remember(long id, PointState s)
    {
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(id)) return;
            _cache[id] = s;
            _cacheOrder.Enqueue(id);
            while (_cacheOrder.Count > 4) _cache.Remove(_cacheOrder.Dequeue());
        }
    }

    public void ForgetCache() { lock (_cacheLock) { _cache.Clear(); _cacheOrder.Clear(); } }

    /// <summary>Bir noktanın tam durumu: tam görüntüye kadar zinciri geri gidip fark satırlarını uygular. Önbelleğe alınır.</summary>
    public PointState Resolve(long id)
    {
        lock (_cacheLock) if (_cache.TryGetValue(id, out var hit)) return hit;
        using var c = Depo.Open();
        var chain = new List<long>();
        PointState? baseState = null;
        long? cur = id;
        while (cur is { } cid)
        {
            lock (_cacheLock) if (_cache.TryGetValue(cid, out var h)) { baseState = h; break; }
            var parent = c.Query("SELECT parent_id FROM snapshot WHERE id=@i", r => r.LongN(0), null, ("@i", cid));
            if (parent.Count == 0) throw new InvalidOperationException("Geri alma noktası yok: " + cid);
            chain.Add(cid);
            cur = parent[0];
        }
        var state = baseState is null ? new PointState() : new PointState(baseState);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var add = new List<FileRec>(); var del = new List<string>();
            using (var cmd = c.Cmd("SELECT path,op,size,mtime_ns,blob,skip_reason,attr FROM snapshot_entry WHERE snapshot_id=@i", null, ("@i", chain[i])))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (r.GetString(1) == "d") { del.Add(r.GetString(0)); continue; }
                    var size = r.GetInt64(2);
                    add.Add(new FileRec(r.GetString(0), size, r.GetInt64(3), r.Str(4), r.Str(5), r.GetInt32(6), size < 0));
                }
            foreach (var d in del) state.Remove(d);   // önce silmeler: yalnız harf değişiminde aynı anahtara 'a' da gelir
            foreach (var a in add) state[a.Path] = a;
        }
        Remember(id, state);
        return state;
    }

    public void Dispose() { Depo.Dispose(); }
}

/// <summary>Bir görüntünün içerik yazma işi: dosyaları özetler, bilinmeyen içeriği pakete ya da dosyaya yazar. Bütün yazımlar fsync edilmeden sonuç dönmez.</summary>
internal sealed class StoreWork
{
    private readonly GeriDepo _depo;
    private readonly RestoreOptions _o;
    private readonly ConcurrentDictionary<string, byte> _claims = new(StringComparer.Ordinal);
    private readonly List<Worker> _workers = new();
    public ConcurrentBag<string> Vanished { get; } = new();
    public List<BlobRow> NewBlobs { get; } = new();
    public List<ChunkRow> NewChunks { get; } = new();
    public long NewBytes { get; private set; }

    public StoreWork(GeriDepo d, RestoreOptions o) { _depo = d; _o = o; }

    private sealed class Worker : IDisposable
    {
        public List<BlobRow> Blobs = new();
        public List<ChunkRow> Chunks = new();
        public List<FileRec> Recs = new();
        public long NewBytes;
        private readonly GeriDepo _depo;
        private SafeFileHandle? _h; private int _id; private long _ofs;
        private readonly byte[] _buf = new byte[1 << 20]; private int _n;
        public Worker(GeriDepo d) { _depo = d; }

        public (int id, long ofs) Append(ReadOnlySpan<byte> v)
        {
            if (_h is null || _ofs + _n + v.Length > _depo.Options.PackFileBytes) { Close(); (_id, _h) = _depo.OpenNewPack(); _ofs = 0; _n = 0; }
            if (_n + v.Length > _buf.Length) Flush();
            var ofs = _ofs + _n;
            if (v.Length >= _buf.Length) { RandomAccess.Write(_h!, v, _ofs); _ofs += v.Length; return (_id, ofs); }
            v.CopyTo(_buf.AsSpan(_n)); _n += v.Length;
            return (_id, ofs);
        }

        private void Flush() { if (_n > 0) { RandomAccess.Write(_h!, _buf.AsSpan(0, _n), _ofs); _ofs += _n; _n = 0; } }

        private void Close()
        {
            if (_h is null) return;
            Flush();
            if (_depo.Options.Fsync) RandomAccess.FlushToDisk(_h);
            _h.Dispose(); _h = null;
        }

        public void Dispose() => Close();
    }

    /// <summary>Verilen giriş listesini işler; her giriş için bir <see cref="FileRec"/> döner (kaybolan dosyalar <see cref="Vanished"/>'a girer).</summary>
    public List<FileRec> Run(string root, List<ScanItem> items)
    {
        var dirs = new List<FileRec>();
        var files = new List<ScanItem>();
        foreach (var it in items) { if (it.IsDir) dirs.Add(new FileRec(it.Path, -1, it.MtimeNs, null, null, it.Attr, true)); else files.Add(it); }
        files.Sort((x, y) => y.Size.CompareTo(x.Size)); // büyükler başa: kuyruğun sonunda tek uzun iş kalmasın

        var tls = new ThreadLocal<Worker>(() => { var w = new Worker(_depo); lock (_workers) _workers.Add(w); return w; }, trackAllValues: false);
        try
        {
            WorkPool.ForEach(files, _o.Parallel, f => Process(root, f, tls.Value!));
        }
        finally
        {
            // Hata olsa da paketler kapanır (tutamaçlar sızmasın). Başarısızsa blob satırı yazılmayacağı için yetim bayt çöp toplamada gider.
            foreach (var w in _workers) w.Dispose();
            tls.Dispose();
        }
        var all = new List<FileRec>(items.Count);
        all.AddRange(dirs);
        foreach (var w in _workers) { all.AddRange(w.Recs); NewBlobs.AddRange(w.Blobs); NewChunks.AddRange(w.Chunks); NewBytes += w.NewBytes; }
        return all;
    }

    private bool Claim(string hex) => !_depo.IsKnown(hex) && _claims.TryAdd(hex, 0);

    private void Process(string root, ScanItem g, Worker w)
    {
        var full = Path.Combine(root, g.Path.Replace('/', '\\'));
        try
        {
            using var h = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = g.Size;
            if (len > _o.MaxCopyBytes)
            {
                w.Recs.Add(new FileRec(g.Path, len, g.MtimeNs, GeriDepo.HashFile(h, len), "buyuk", g.Attr));
                return;
            }
            if (len >= _o.ChunkFromBytes && SirKutusu.IsSecretFile(g.Path))
            {
                // Çok büyük sır dosyası parçalı düz yazılamaz: yalnız özeti alınır (içeriği saklanmaz).
                w.Recs.Add(new FileRec(g.Path, len, g.MtimeNs, GeriDepo.HashFile(h, len), "buyuk", g.Attr));
                return;
            }
            if (len >= _o.ChunkFromBytes) { Chunked(g, h, len, w); return; }
            var buf = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, len));
            try
            {
                var n = 0;
                while (n < len) { var r = RandomAccess.Read(h, buf.AsSpan(n, (int)len - n), n); if (r == 0) break; n += r; }
                var span = buf.AsSpan(0, n);
                var hex = Convert.ToHexStringLower(SHA256.HashData(span));
                // Sır türü dosya (adıyla ya da içindeki anahtar desenle) depoya düz yazılmaz: DPAPI ile şifrelenir (bkz. SirKutusu).
                var claimed = Claim(hex);
                byte[]? sealedBytes = claimed && (SirKutusu.IsSecretFile(g.Path) || SirKutusu.ContentLooksSecret(span)) ? SirKutusu.Protect(span) : null;
                w.Recs.Add(new FileRec(g.Path, n, g.MtimeNs, hex, null, g.Attr));
                if (claimed) Store(hex, span, w, sealedBytes);
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { Vanished.Add(g.Path); }
        catch (UnauthorizedAccessException) { w.Recs.Add(new FileRec(g.Path, g.Size, g.MtimeNs, null, "okunamadi", g.Attr)); }
        catch (IOException) { w.Recs.Add(new FileRec(g.Path, g.Size, g.MtimeNs, null, "kilitli", g.Attr)); }
    }

    /// <summary>32 MB üstü dosya: 4 MiB sabit parçalar, her parça ayrı blob (büyük dosyada küçük değişiklik yalnız o parçayı yeniden yazar).</summary>
    private void Chunked(ScanItem g, SafeFileHandle h, long len, Worker w)
    {
        var buf = ArrayPool<byte>.Shared.Rent(RestoreOptions.ChunkSize);
        try
        {
            using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunks = new List<ChunkRow>();
            long o = 0; var seq = 0;
            while (o < len)
            {
                var n = 0;
                while (n < RestoreOptions.ChunkSize && o + n < len)
                {
                    var r = RandomAccess.Read(h, buf.AsSpan(n, (int)Math.Min(RestoreOptions.ChunkSize - n, len - o - n)), o + n);
                    if (r == 0) break;
                    n += r;
                }
                if (n == 0) break;
                inc.AppendData(buf, 0, n);
                var ph = Convert.ToHexStringLower(SHA256.HashData(buf.AsSpan(0, n)));
                chunks.Add(new ChunkRow("", seq++, ph, n));
                if (Claim(ph)) Store(ph, buf.AsSpan(0, n), w);
                o += n;
            }
            var hex = Convert.ToHexStringLower(inc.GetHashAndReset());
            w.Recs.Add(new FileRec(g.Path, o, g.MtimeNs, hex, null, g.Attr));
            if (Claim(hex))
            {
                w.Blobs.Add(new BlobRow(hex, o, 0, "parca", null, null));
                foreach (var c in chunks) w.Chunks.Add(c with { BlobHash = hex });
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>sealedBytes verilirse (sır türü) depoya şifreli bayt yazılır; satırın Size'ı düz uzunluk, Stored'ı şifreli uzunluktur.</summary>
    private void Store(string hex, ReadOnlySpan<byte> data, Worker w, byte[]? sealedBytes = null)
    {
        ReadOnlySpan<byte> payload = sealedBytes is null ? data : sealedBytes;
        var codec = sealedBytes is null ? "raw" : SirKutusu.Codec;
        w.NewBytes += payload.Length;
        if (data.Length <= _o.PackItemMaxBytes)
        {
            var (id, ofs) = w.Append(payload);
            w.Blobs.Add(new BlobRow(hex, data.Length, payload.Length, codec, id, ofs));
            return;
        }
        _depo.WriteLoose(hex, payload);
        w.Blobs.Add(new BlobRow(hex, data.Length, payload.Length, codec, null, null));
    }
}
