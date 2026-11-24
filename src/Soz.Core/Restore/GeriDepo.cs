using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Soz.Core.Data;

namespace Soz.Core.Restore;

/// <summary>Bir noktadaki dosya (ya da boş klasör) kaydı. Blob = tüm içeriğin SHA-256 özeti (hex). Skip: buyuk | kilitli | okunamadi.</summary>
public sealed record FileRec(string Path, long Size, long MtimeNs, string? Blob, string? Skip, int Attr, bool IsDir = false);

/// <summary>Bir noktanın tam durumu (yol, büyük-küçük harf duyarsız).</summary>
public sealed class PointState : Dictionary<string, FileRec>
{
    public PointState() : base(StringComparer.OrdinalIgnoreCase) { }
    public PointState(PointState o) : base(o, StringComparer.OrdinalIgnoreCase) { }
}

public sealed record BlobRow(string Hash, long Size, long Stored, string Codec, int? Pak, long? Ofs);
public sealed record ChunkRow(string BlobHash, int Seq, string ChunkHash, long Size);

public sealed record SnapshotRow(long Id, string Uid, string ProjectUid, string RootPath, string Kind, string? JobUid, string? RunUid, long? ParentId, bool Full, int Depth, string TakenAt,
    long FileCount, long TotalBytes, long NewBlobBytes, string Protection, string? ProtectionNote, string? DepJson, string State, bool Pinned);

/// <summary>
/// Geri alma deposu: &lt;veri&gt;\geri\geri.db (türetilmiş, atılabilir, kendi sürümü ve application_id'si "SOZG") + blob\xx\... + pak\*.pak + tmp\.
/// İçerik adresli: küçük blob'lar 32 MB'lık paketlere eklenir, büyükler dosya olarak yazılır, 32 MB üstü dosyalar 4 MiB parçalanır.
/// Manifest yalnız bütün içerik diske (fsync) yazıldıktan sonra tek işlemle yazılır; yarım kalan görüntü kimseye görünmez.
/// </summary>
public sealed class GeriDepo : IDisposable
{
    public const long AppId = 1397709383; // "SOZG" 0x534F5A47
    public const int SchemaVersion = 1;

    public string Dir { get; }
    public RestoreOptions Options { get; }

    /// <summary>Çöp toplama (yazma kilidi) görüntü almayı ve geri yüklemeyi (okuma kilidi) engellemeden önce bekler: toplanan blob yeni görüntüye girmesin.</summary>
    public ReaderWriterLockSlim Gate { get; } = new(LockRecursionPolicy.SupportsRecursion);
    private readonly string _connString;
    private readonly ConcurrentDictionary<int, SafeFileHandle> _packReaders = new();
    private readonly object _readerLock = new();

    /// <summary>Paketin okuma tutamacı (paket başına bir tane). Fabrikası yarışsız çalışır: ConcurrentDictionary.GetOrAdd iki iş parçacığında iki tutamak açıp birini sızdırabilirdi.</summary>
    private SafeFileHandle PackReader(int id)
    {
        if (_packReaders.TryGetValue(id, out var h)) return h;
        lock (_readerLock)
        {
            if (_packReaders.TryGetValue(id, out h)) return h;
            h = File.OpenHandle(PackPath(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _packReaders[id] = h;
            return h;
        }
    }
    private HashSet<string>? _known;
    private readonly object _knownLock = new();
    private int _lastPack = -1;
    private long _tmpCounter;
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];

    public string DbPath => Path.Combine(Dir, "geri.db");
    public string TmpDir => Path.Combine(Dir, "tmp");
    public string PackDir => Path.Combine(Dir, "pak");
    public string BlobDir => Path.Combine(Dir, "blob");

    public GeriDepo(string dir, RestoreOptions? options = null)
    {
        Dir = Path.GetFullPath(dir);
        Options = options ?? new RestoreOptions();
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(TmpDir);
        Directory.CreateDirectory(PackDir);
        Directory.CreateDirectory(BlobDir);
        _connString = new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true, Cache = SqliteCacheMode.Private }.ToString();
        InitSchema();
        CleanTmp();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        c.Exec("PRAGMA busy_timeout=10000; PRAGMA foreign_keys=OFF;");
        return c;
    }

    private void InitSchema()
    {
        using var c = Open();
        c.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        var ver = c.ScalarLong("PRAGMA user_version");
        if (ver > SchemaVersion) throw new SozSchemaTooNewException((int)ver, SchemaVersion);
        c.Exec($$"""
            PRAGMA application_id={{AppId}};
            CREATE TABLE IF NOT EXISTS snapshot(
              id INTEGER PRIMARY KEY, uid TEXT NOT NULL UNIQUE, project_uid TEXT NOT NULL, root_path TEXT NOT NULL,
              kind TEXT NOT NULL, job_uid TEXT NULL, run_uid TEXT NULL,
              parent_id INTEGER NULL, full INTEGER NOT NULL DEFAULT 0, depth INTEGER NOT NULL DEFAULT 0, taken_at TEXT NOT NULL,
              scan_ms INTEGER, store_ms INTEGER, file_count INTEGER, total_bytes INTEGER, new_blob_bytes INTEGER,
              protection TEXT NOT NULL, protection_note TEXT NULL, dep_json TEXT NULL,
              state TEXT NOT NULL DEFAULT 'ok', pinned INTEGER NOT NULL DEFAULT 0, expires_at TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_snapshot_project ON snapshot(project_uid, id);
            CREATE INDEX IF NOT EXISTS ix_snapshot_root ON snapshot(root_path COLLATE NOCASE, id);
            CREATE INDEX IF NOT EXISTS ix_snapshot_run ON snapshot(run_uid);
            CREATE TABLE IF NOT EXISTS snapshot_entry(
              snapshot_id INTEGER NOT NULL, path TEXT NOT NULL, op TEXT NOT NULL, size INTEGER, mtime_ns INTEGER,
              blob TEXT NULL, skip_reason TEXT NULL, attr INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(snapshot_id, path)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS blob(
              hash TEXT PRIMARY KEY, size INTEGER NOT NULL, stored_size INTEGER NOT NULL,
              codec TEXT NOT NULL DEFAULT 'raw', first_seen TEXT NOT NULL, pak INTEGER NULL, ofs INTEGER NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS blob_parca(
              blob_hash TEXT NOT NULL, seq INTEGER NOT NULL, chunk_hash TEXT NOT NULL, size INTEGER NOT NULL,
              PRIMARY KEY(blob_hash, seq)) WITHOUT ROWID;
            PRAGMA user_version={{SchemaVersion}};
            """);
    }

    /// <summary>Açılışta yarım kalmış yazımların geçici dosyalarını temizler (10 dakikadan eskileri; başka süreç şimdi yazıyor olabilir).</summary>
    private void CleanTmp()
    {
        try
        {
            foreach (var f in new DirectoryInfo(TmpDir).GetFiles())
                if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromMinutes(10)) try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
    }

    // ---------------------------------------------------------------- bilinen blob kümesi

    public bool IsKnown(string hash)
    {
        EnsureKnown();
        lock (_knownLock) return _known!.Contains(hash);
    }

    public void MarkKnown(IEnumerable<string> hashes)
    {
        EnsureKnown();
        lock (_knownLock) foreach (var h in hashes) _known!.Add(h);
    }

    public void ResetKnown() { lock (_knownLock) _known = null; }

    private void EnsureKnown()
    {
        lock (_knownLock)
        {
            if (_known is not null) return;
            using var c = Open();
            _known = new HashSet<string>(c.Query("SELECT hash FROM blob", r => r.GetString(0)), StringComparer.Ordinal);
        }
    }

    // ---------------------------------------------------------------- yazma yardımcıları

    public string NewTmpPath() => Path.Combine(TmpDir, $"{_run}-{Interlocked.Increment(ref _tmpCounter)}");
    public string PackPath(int id) => Path.Combine(PackDir, $"{id:D5}.pak");
    public string LooseBlobPath(string hex) => Path.Combine(BlobDir, hex[..2], hex[2..]);

    /// <summary>Yeni paket dosyasını açar (kimlik çakışırsa sonrakini dener; başka süreç ya da önceki başarısız görüntü aynı kimliği almış olabilir).</summary>
    public (int id, SafeFileHandle handle) OpenNewPack()
    {
        lock (_knownLock)
        {
            if (_lastPack < 0)
            {
                using var c = Open();
                var max = (int)c.ScalarLong("SELECT COALESCE(MAX(pak),0) FROM blob");
                foreach (var f in Directory.EnumerateFiles(PackDir, "*.pak"))
                    if (int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) && n > max) max = n;
                _lastPack = max;
            }
        }
        while (true)
        {
            int id;
            lock (_knownLock) id = ++_lastPack;
            try { return (id, File.OpenHandle(PackPath(id), FileMode.CreateNew, FileAccess.Write, FileShare.None)); }
            catch (IOException) when (File.Exists(PackPath(id))) { /* kimlik dolu: sonrakini dene */ }
        }
    }

    /// <summary>Dosya olarak saklanan blob'u atomik yazar (geçici dosya, fsync, yeniden adlandırma). Aynı içerik eşzamanlı yazılmışsa geçici dosya atılır.</summary>
    public void WriteLoose(string hex, ReadOnlySpan<byte> data)
    {
        var target = LooseBlobPath(hex);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = NewTmpPath();
        try
        {
            using (var f = File.OpenHandle(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None, data.Length))
            {
                RandomAccess.Write(f, data, 0);
                if (Options.Fsync) RandomAccess.FlushToDisk(f);
            }
            try { File.Move(tmp, target, false); }
            catch (IOException) when (File.Exists(target)) { File.Delete(tmp); }
        }
        catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } throw; }
    }

    // ---------------------------------------------------------------- okuma

    public BlobRow? GetBlob(SqliteConnection c, string hash)
        => c.Query("SELECT hash,size,stored_size,codec,pak,ofs FROM blob WHERE hash=@h",
            r => new BlobRow(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt64(5)), null, ("@h", hash)).FirstOrDefault();

    public List<ChunkRow> GetChunks(SqliteConnection c, string blobHash)
        => c.Query("SELECT blob_hash,seq,chunk_hash,size FROM blob_parca WHERE blob_hash=@h ORDER BY seq",
            r => new ChunkRow(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetInt64(3)), null, ("@h", blobHash));

    /// <summary>Bir blob'un baytlarını okur (paketten ya da dosyadan); sır kutusundaysa (dpapi) çözülmüş düz içerik döner. Parçalı blob için ChunkRow listesi ayrıca okunur (<see cref="WriteBlobTo"/>).</summary>
    public byte[] ReadBlob(BlobRow b)
    {
        var stored = ReadStored(b);
        return b.Codec == SirKutusu.Codec ? SirKutusu.Unprotect(stored) : stored;
    }

    /// <summary>Depoda durduğu gibi (sır kutusundaysa ŞİFRELİ) baytlar. Çöp toplama paketleri taşırken bunu kullanır, içerik hiç çözülmez.</summary>
    public byte[] ReadStored(BlobRow b)
    {
        var buf = new byte[b.Stored];
        if (b.Pak is { } pk)
        {
            var h = PackReader(pk);
            Fill(h, buf, b.Ofs!.Value);
        }
        else
        {
            using var h = File.OpenHandle(LooseBlobPath(b.Hash), FileMode.Open, FileAccess.Read, FileShare.Read);
            Fill(h, buf, 0);
        }
        return buf;
    }

    private static void Fill(SafeFileHandle h, byte[] buf, long ofs)
    {
        var t = 0;
        while (t < buf.Length)
        {
            var n = RandomAccess.Read(h, buf.AsSpan(t), ofs + t);
            if (n == 0) throw new EndOfStreamException("Geri alma deposundaki içerik eksik.");
            t += n;
        }
    }

    /// <summary>
    /// Blob'un tüm içeriğini (parçalıysa parçaları sırayla) açık dosyaya yazar ve yazdığı baytların özetini blob özetiyle karşılaştırır:
    /// bozuk ya da eksik depo içeriği sessizce kişinin dosyasına geçmez (InvalidDataException, EndOfStreamException).
    /// </summary>
    public void WriteBlobTo(SafeFileHandle target, BlobRow b, IReadOnlyList<BlobRow>? chunkBlobs)
    {
        using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (b.Codec != "parca")
        {
            var data = ReadBlob(b);
            inc.AppendData(data);
            RandomAccess.Write(target, data, 0);
        }
        else
        {
            long o = 0;
            foreach (var row in chunkBlobs!) { var data = ReadBlob(row); inc.AppendData(data); RandomAccess.Write(target, data, o); o += data.Length; }
        }
        if (!string.Equals(Convert.ToHexStringLower(inc.GetHashAndReset()), b.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Geri alma deposundaki içerik bozuk.");
    }

    /// <summary>Boyutu ne olursa olsun bir dosyanın SHA-256 özeti (hex).</summary>
    public static string HashFile(SafeFileHandle h, long len)
    {
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(4 << 20);
        try
        {
            using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long o = 0;
            while (true)
            {
                var r = RandomAccess.Read(h, buf, o);
                if (r == 0) break;
                inc.AppendData(buf, 0, r);
                o += r;
            }
            return Convert.ToHexStringLower(inc.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }

    // ---------------------------------------------------------------- nokta okuma

    private const string SnapCols = "id, uid, project_uid, root_path, kind, job_uid, run_uid, parent_id, full, depth, taken_at, file_count, total_bytes, new_blob_bytes, protection, protection_note, dep_json, state, pinned";
    private static SnapshotRow MapSnap(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Str(5), r.Str(6), r.LongN(7), r.GetInt64(8) != 0, (int)r.Long(9), r.GetString(10),
        r.Long(11), r.Long(12), r.Long(13), r.GetString(14), r.Str(15), r.Str(16), r.GetString(17), r.GetInt64(18) != 0);

    public SnapshotRow? GetSnapshot(SqliteConnection c, long id) => c.Query($"SELECT {SnapCols} FROM snapshot WHERE id=@i", MapSnap, null, ("@i", id)).FirstOrDefault();
    public SnapshotRow? GetSnapshotByUid(SqliteConnection c, string uid) => c.Query($"SELECT {SnapCols} FROM snapshot WHERE uid=@u", MapSnap, null, ("@u", uid)).FirstOrDefault();
    public SnapshotRow? LatestFor(SqliteConnection c, string rootPath)
        => c.Query($"SELECT {SnapCols} FROM snapshot WHERE root_path=@r COLLATE NOCASE AND state='ok' ORDER BY id DESC LIMIT 1", MapSnap, null, ("@r", rootPath)).FirstOrDefault();
    public List<SnapshotRow> ListFor(SqliteConnection c, string rootPath)
        => c.Query($"SELECT {SnapCols} FROM snapshot WHERE root_path=@r COLLATE NOCASE ORDER BY id", MapSnap, null, ("@r", rootPath));

    public static List<DepPrint> ParseDeps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<DepPrint>>(json) ?? new(); } catch (JsonException) { return new(); }
    }

    /// <summary>Depodaki toplam saklanan bayt (bütçe için).</summary>
    public long StoredBytes(SqliteConnection c) => c.ScalarLong("SELECT COALESCE(SUM(stored_size),0) FROM blob");

    public void Dispose()
    {
        foreach (var h in _packReaders.Values) h.Dispose();
        _packReaders.Clear();
    }

    /// <summary>Çöp toplama sırasında bir paketin okuma tutamacını bırakır.</summary>
    public void ForgetPackReader(int id) { if (_packReaders.TryRemove(id, out var h)) h.Dispose(); }
}
