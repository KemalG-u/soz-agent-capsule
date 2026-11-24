using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Soz.Core.Restore;

/// <summary>Planlanan tek iş. Action: geri-yukle | sil | adi-duzelt | klasor-olustur | klasor-sil. Why: ajan-degistirdi | ajan-sildi | ajan-olusturdu | ...</summary>
public sealed record PlanItem(string Path, string Action, string? Why);

/// <summary>Atlanan dosya. Reason: degisti (sen ya da başka program değiştirmiş) | degisti-onbellek | kilitli | buyuk | okunamadi | depo-eksik | guvensiz-yol | baglanti | tur-degisti.</summary>
public sealed record Conflict(string Path, string Reason);

/// <summary>
/// Bir geri alma isteği. TargetId dönülecek nokta (B), AfterId ajanın son hâli (A; null ise klasördeki her fark ajanın sayılır),
/// Paths verilirse yalnız o dosya/klasörler (kısmi geri alma; klasör "yol/" ya da "yol"), Force çakışanları da geri alır.
/// </summary>
public sealed record RestoreRequest(string Root, long TargetId, long? AfterId, IReadOnlyList<string>? Paths, bool Force, string ProjectUid, string? JobUid = null);

public sealed class RestoreOutcome
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public long PreId { get; set; }
    public string? PreUid { get; set; }
    public bool PreIsNew { get; set; }
    public long PostId { get; set; }
    public string? PostUid { get; set; }
    public int Restored { get; set; }
    public int Removed { get; set; }
    public int Renamed { get; set; }
    public int DirsCreated { get; set; }
    public int DirsRemoved { get; set; }
    public int AlreadyOk { get; set; }
    public List<PlanItem> Items { get; } = new();
    public List<Conflict> Conflicts { get; } = new();
    public List<Conflict> Unrestorable { get; } = new();
    public List<string> Notes { get; } = new();
    public long PreMs { get; set; }
    public long TotalMs { get; set; }
}

/// <summary>
/// Geri yükleyici. Üç yönlü karar (spike GeriAl.cs, rapor 4.4): B = dönülecek nokta, A = ajanın son hâli, C = şimdiki klasör.
/// Her dosya için: C = B ise dokunma; C = A ise ajanın son hâlidir, B'ye döndür; C ≠ A ise sen ya da başka program dokunmuş, ÇAKIŞMA: atla (Force ile yine de geri al);
/// A = B ise ajan dokunmamış, kimsenin değişikliğine el sürülmez. Her geri almadan ÖNCE şimdiki durum otomatik nokta olarak kaydedilir
/// ("geri almayı geri al" bu noktaya döner). Hiçbir işlem proje klasörünün dışına yazmaz ya da silmez, bağlantıların (junction) içine girmez.
/// </summary>
public sealed class RestoreEngine
{
    public const string TempSuffix = ".soz-geri";
    private readonly SnapshotEngine _snaps;

    public RestoreEngine(SnapshotEngine snaps) { _snaps = snaps; }

    private GeriDepo Depo => _snaps.Depo;

    // ---------------------------------------------------------------- önizleme ve uygulama

    /// <summary>Hiçbir dosyaya yazmaz (yalnız şimdiki durumun noktası alınır). Sonuç, Apply'ın yapacağıyla aynıdır.</summary>
    public RestoreOutcome Preview(RestoreRequest req) => Run(req, apply: false);

    public RestoreOutcome Apply(RestoreRequest req) => Run(req, apply: true);

    private sealed class Plan
    {
        public List<(FileRec b, FileRec? c, string why)> Write = new();
        public List<(FileRec b, FileRec c)> Rename = new();
        public List<FileRec> Remove = new();
        public List<string> MakeDirs = new();
        public List<string> RemoveDirs = new();
    }

    private RestoreOutcome Run(RestoreRequest req, bool apply)
    {
        var root = SnapshotEngine.NormalizeRoot(req.Root);
        var total = Stopwatch.StartNew();
        var o = new RestoreOutcome();
        lock (_snaps.RootLock(root))
        {
            Depo.Gate.EnterReadLock();
            try { RunCore(req, root, apply, o); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                o.Ok = false;
                o.Error ??= "Geri alma sırasında beklenmeyen bir hata oldu: " + (e.Message.Length > 200 ? e.Message[..200] : e.Message);
            }
            finally { Depo.Gate.ExitReadLock(); }
        }
        o.TotalMs = total.ElapsedMilliseconds;
        return o;
    }

    private void RunCore(RestoreRequest req, string root, bool apply, RestoreOutcome o)
    {
        SnapshotRow? target, after = null;
        using (var c = Depo.Open())
        {
            target = Depo.GetSnapshot(c, req.TargetId);
            if (req.AfterId is { } aid) after = Depo.GetSnapshot(c, aid);
        }
        if (target is null || target.State != "ok") { o.Error = "Dönülecek geri alma noktası bulunamadı (süresi dolmuş olabilir)."; return; }
        if (!string.Equals(target.RootPath, root, StringComparison.OrdinalIgnoreCase)) { o.Error = "Geri alma noktası başka bir klasöre ait. Bu proje klasörüne uygulanmaz."; return; }
        if (req.AfterId is not null && after is null) o.Notes.Add("Yardımcının son durumunun noktası bulunamadı. Klasördeki her fark yardımcının sayıldı.");

        // 1) şimdiki durumu otomatik nokta olarak kaydet (değişiklik yoksa yeni nokta yazılmaz, önceki nokta kullanılır)
        var sw = Stopwatch.StartNew();
        var pre = _snaps.Take(new SnapshotRequest(root, "before_undo", req.ProjectUid, req.JobUid));
        o.PreMs = sw.ElapsedMilliseconds;
        if (!pre.Ok)
        {
            o.Error = "Geri almadan önce şimdiki durum kaydedilemedi (" + pre.Note + ") Güvenli olmadığı için hiçbir şey geri alınmadı.";
            return;
        }
        o.PreId = pre.Id; o.PreUid = pre.Uid; o.PreIsNew = pre.IsNew;

        var B = _snaps.Resolve(req.TargetId);
        var A = after is null ? null : _snaps.Resolve(after.Id);
        var C = _snaps.Resolve(pre.Id);
        var plan = BuildPlan(root, B, A, C, req, o);
        RehashCheck(root, plan, req.Force, o);
        DependencyNotes(target, pre, o);
        FillItems(plan, o, root, B);
        if (!apply) { o.Ok = true; return; }

        ExecutePlan(root, plan, B, o);
        // 2) geri almanın sonundaki durum: "geri almayı geri al" ve sonraki çakışma kararı bunu A olarak kullanır
        var post = _snaps.Take(new SnapshotRequest(root, "after_undo", req.ProjectUid, req.JobUid));
        if (post.Ok) { o.PostId = post.Id; o.PostUid = post.Uid; }
        else o.Notes.Add("Geri almanın sonundaki durum kaydedilemedi. \"Geri almayı geri al\" çakışmaları ayıramayabilir.");
        o.Ok = true;
    }

    // ---------------------------------------------------------------- karar

    private static bool BlobEq(FileRec? x, FileRec? y)
    {
        if (x is null && y is null) return true;
        if (x is null || y is null) return false;
        return x.Blob != null && x.Blob == y.Blob;
    }

    /// <summary>B'den A'ya ajan dokunmuş mu? Özeti olmayan (kilitli/okunamadı) kayıtlar boyut ve zamanla karşılaştırılır.</summary>
    private static bool AgentTouched(FileRec b, FileRec a)
    {
        if (b.Blob != null && a.Blob != null) return b.Blob != a.Blob || !string.Equals(b.Path, a.Path, StringComparison.Ordinal);
        return b.Size != a.Size || b.MtimeNs != a.MtimeNs;
    }

    private static bool Selected(string path, IReadOnlyList<string>? sel)
    {
        if (sel is null || sel.Count == 0) return true;
        foreach (var raw in sel)
        {
            var s = raw.Replace('\\', '/').Trim('/');
            if (s.Length == 0) return true;
            if (path.Equals(s, StringComparison.OrdinalIgnoreCase) || path.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private Plan BuildPlan(string root, PointState B, PointState? A, PointState C, RestoreRequest req, RestoreOutcome o)
    {
        var plan = new Plan();
        var force = req.Force;
        // B'de herhangi bir dosyanın ya da boş klasörün üst klasörü olan yollar: bunlar "ajanın yarattığı boş klasör" sayılmaz
        var bAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in B.Values)
        {
            var p = b.Path;
            int i;
            while ((i = p.LastIndexOf('/')) > 0) { p = p[..i]; if (!bAncestors.Add(p)) break; }
        }

        void Decide(string key, FileRec? b, FileRec? a, FileRec? c)
        {
            if (!Selected((b ?? c ?? a)!.Path, req.Paths)) return;
            if (b?.IsDir == true || a?.IsDir == true || c?.IsDir == true) { DecideDir(key, b, a, c); return; }

            if (BlobEq(b, c) || (b is null && c is null))
            {
                if (b != null && c != null && !string.Equals(b.Path, c.Path, StringComparison.Ordinal)) plan.Rename.Add((b, c));
                else o.AlreadyOk++;
                return;
            }
            var agentFinal = A is null || (a is not null && BlobEq(a, c)) || (a is null && c is null);
            if (!agentFinal && !force) { o.Conflicts.Add(new Conflict((b ?? c ?? a)!.Path, "degisti")); return; }
            if (b is null) { plan.Remove.Add(c!); return; }
            if (b.Skip is not null || b.Blob is null) { o.Unrestorable.Add(new Conflict(b.Path, b.Skip ?? "okunamadi")); return; }
            plan.Write.Add((b, c, c is null ? "ajan-sildi" : "ajan-degistirdi"));
        }

        void DecideDir(string key, FileRec? b, FileRec? a, FileRec? c)
        {
            if (b?.IsDir == true)
            {
                if (c is { IsDir: false }) { o.Conflicts.Add(new Conflict(b.Path, "tur-degisti")); return; }
                if (c is null && !Directory.Exists(Path.Combine(root, b.Path.Replace('/', '\\')))) plan.MakeDirs.Add(b.Path);
                return;
            }
            // B'de bu klasör yok. Şimdi boş klasör olarak duruyorsa ve ajan yarattıysa kaldırılır (bkz. Dir temizliği: dolu klasör dokunulmaz)
            if (c?.IsDir == true && !bAncestors.Contains(c.Path) && (A is null || a?.IsDir == true || force)) plan.RemoveDirs.Add(c.Path);
        }

        if (A is not null)
        {
            foreach (var (k, aa) in A)
            {
                B.TryGetValue(k, out var b);
                if (b is { IsDir: false } && !aa.IsDir && !AgentTouched(b, aa)) continue; // ajan dokunmamış
                C.TryGetValue(k, out var c);
                Decide(k, b, aa, c);
            }
            foreach (var (k, b) in B)
            {
                if (A.ContainsKey(k)) continue;
                C.TryGetValue(k, out var c);
                Decide(k, b, null, c); // ajan silmiş
            }
        }
        else
        {
            foreach (var (k, b) in B)
            {
                C.TryGetValue(k, out var c);
                if (!BlobEq(b, c) || (c != null && !string.Equals(b.Path, c.Path, StringComparison.Ordinal)) || b.IsDir) Decide(k, b, null, c);
            }
            foreach (var (k, c) in C) if (!B.ContainsKey(k)) Decide(k, null, null, c);
        }
        return plan;
    }

    /// <summary>
    /// Rapor 4.4: boyut ve zaman aynı ama içerik farklı olabilir (zamanı geri yazan araç). Dokunulacak dosyaların özeti diskten yeniden alınır;
    /// noktadaki özetle uyuşmayan dosya "değişmiş" sayılır ve (Force yoksa) atlanır.
    /// </summary>
    private void RehashCheck(string root, Plan plan, bool force, RestoreOutcome o)
    {
        var bad = new ConcurrentBag<string>();
        var targets = new List<FileRec>();
        foreach (var (_, c, _) in plan.Write) if (c is { Blob: not null }) targets.Add(c);
        foreach (var c in plan.Remove) if (c.Blob != null) targets.Add(c);
        foreach (var (_, c) in plan.Rename) if (c.Blob != null) targets.Add(c);
        WorkPool.ForEach(targets, Depo.Options.Parallel, c =>
        {
            var full = SafePath(root, c.Path);
            if (full is null) return;
            try
            {
                using var h = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (GeriDepo.HashFile(h, RandomAccess.GetLength(h)) != c.Blob) bad.Add(c.Path);
            }
            catch (IOException) { /* kilitliyse yazma aşamasında zaten çakışma olur */ }
            catch (UnauthorizedAccessException) { }
        });
        if (bad.IsEmpty || force) return;
        var set = new HashSet<string>(bad, StringComparer.OrdinalIgnoreCase);
        foreach (var w in plan.Write.Where(w => w.c != null && set.Contains(w.c.Path))) o.Conflicts.Add(new Conflict(w.b.Path, "degisti-onbellek"));
        foreach (var r in plan.Remove.Where(r => set.Contains(r.Path))) o.Conflicts.Add(new Conflict(r.Path, "degisti-onbellek"));
        plan.Write.RemoveAll(w => w.c != null && set.Contains(w.c.Path));
        plan.Remove.RemoveAll(r => set.Contains(r.Path));
        plan.Rename.RemoveAll(r => set.Contains(r.c.Path));
    }

    /// <summary>Bağımlılık ve derleme klasörleri saklanmaz; dönülen noktadan beri değiştiyse kişiye söylenir (içeriği geri yüklenmez).</summary>
    private void DependencyNotes(SnapshotRow target, SnapshotResult pre, RestoreOutcome o)
    {
        SnapshotRow? preRow;
        using (var c = Depo.Open()) preRow = Depo.GetSnapshot(c, pre.Id);
        if (preRow is null) return;
        var then = GeriDepo.ParseDeps(target.DepJson);
        var now = GeriDepo.ParseDeps(preRow.DepJson).ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var d in then)
            if (!now.TryGetValue(d.Path, out var x) || x.Files != d.Files || x.Bytes != d.Bytes || x.MtimeNs != d.MtimeNs)
                o.Notes.Add($"\"{d.Path}\" klasörü bu işten sonra değişti. Bu klasörlerin içeriği saklanmaz, geri yüklenmez. Gerekirse yeniden kurman gerekir.");
        var had = new HashSet<string>(then.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var d in now.Values.Where(d => !had.Contains(d.Path)))
            o.Notes.Add($"\"{d.Path}\" klasörü bu işten sonra oluşmuş. İçeriği saklanmadığı için olduğu gibi bırakıldı.");
    }

    /// <summary>
    /// Uygulama aşamasındaki klasör temizliğinin AYNISI, hiçbir şeye dokunmadan (denetim 5, B10): ajanın oluşturduğu dosyalar silinince boşalan klasörler
    /// de bir değişikliktir. Önizleme bunları sayar; böylece "N değişiklik yapılır" ile "N değişiklik yapıldı" aynı olur. Klasörün boş olup olmadığı,
    /// silinecek dosyalar ve kalkacak alt klasörler düşülerek diskteki gerçek içeriğe bakılarak bulunur. Bu yöntem ExecutePlan'daki klasör döngüsüyle aynı sırada ve aynı kurallarla çalışmalıdır.
    /// </summary>
    private static List<string> PredictDirRemovals(string root, Plan plan, PointState B)
    {
        var bDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in B.Values)
        {
            var p = b.IsDir ? b.Path + "/x" : b.Path;
            int i;
            while ((i = p.LastIndexOf('/')) > 0) { p = p[..i]; if (!bDirs.Add(p)) break; }
        }
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parents = new List<string>();
        foreach (var c in plan.Remove)
        {
            gone.Add(c.Path);
            var i = c.Path.LastIndexOf('/');
            if (i > 0) parents.Add(c.Path[..i]);
        }
        var removed = new List<string>();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d0 in plan.RemoveDirs.Concat(parents.Distinct(StringComparer.OrdinalIgnoreCase)).OrderByDescending(x => x.Length))
        {
            var d = d0;
            while (!string.IsNullOrEmpty(d) && tried.Add(d))
            {
                if (bDirs.Contains(d)) break;
                var full = SafePath(root, d);
                if (full is null) break;
                try
                {
                    if (!Directory.Exists(full)) { d = ParentOf(d); continue; }
                    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) break;
                    var left = Directory.EnumerateFileSystemEntries(full).Any(e => !gone.Contains(Path.GetRelativePath(root, e).Replace('\\', '/')));
                    if (left) break;
                    gone.Add(d);
                    removed.Add(d);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { break; }
                d = ParentOf(d);
            }
        }
        return removed;
    }

    private void FillItems(Plan plan, RestoreOutcome o, string root, PointState B)
    {
        foreach (var (b, _, why) in plan.Write) o.Items.Add(new PlanItem(b.Path, "geri-yukle", why));
        foreach (var c in plan.Remove) o.Items.Add(new PlanItem(c.Path, "sil", "ajan-olusturdu"));
        foreach (var (b, _) in plan.Rename) o.Items.Add(new PlanItem(b.Path, "adi-duzelt", "ajan-adini-degistirdi"));
        foreach (var d in plan.MakeDirs) o.Items.Add(new PlanItem(d, "klasor-olustur", "ajan-sildi"));
        foreach (var d in plan.RemoveDirs) o.Items.Add(new PlanItem(d, "klasor-sil", "ajan-olusturdu"));
        var listed = new HashSet<string>(plan.RemoveDirs, StringComparer.OrdinalIgnoreCase);
        foreach (var d in PredictDirRemovals(root, plan, B)) if (listed.Add(d)) o.Items.Add(new PlanItem(d, "klasor-sil", "ajan-olusturdu"));
        o.Items.Sort((x, y) => string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- yol güvenliği

    /// <summary>Kök klasörün altında kalan tam yol; ".." ya da kök dışına çıkan yol null.</summary>
    public static string? SafePath(string root, string rel)
    {
        if (string.IsNullOrEmpty(rel) || rel.Contains(':') || rel.StartsWith('/') || rel.StartsWith('\\')) return null;
        foreach (var seg in rel.Split('/', '\\')) if (seg == ".." || seg == ".") return null;
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
        return full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Hedefin üst klasörlerinden biri bağlantı (junction / sembolik bağ) mı? Öyleyse içine yazılmaz, dışarıya taşma olmaz.</summary>
    private static bool UnderReparse(string root, string full, ConcurrentDictionary<string, bool> cache)
    {
        var dir = Path.GetDirectoryName(full);
        var chain = new List<string>();
        while (dir is not null && dir.Length > root.Length)
        {
            if (cache.TryGetValue(dir, out var hit)) { if (hit) { MarkAll(chain, cache, true); return true; } break; }
            chain.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
        foreach (var d in chain)
        {
            bool reparse;
            try { reparse = Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { reparse = false; }
            cache[d] = reparse;
            if (reparse) return true;
        }
        return false;
    }

    private static void MarkAll(List<string> chain, ConcurrentDictionary<string, bool> cache, bool v) { foreach (var d in chain) cache[d] = v; }

    // ---------------------------------------------------------------- uygulama

    private void ExecutePlan(string root, Plan plan, PointState B, RestoreOutcome o)
    {
        var opts = Depo.Options;
        var reparse = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var skipped = new ConcurrentBag<Conflict>();
        var corrupt = new ConcurrentBag<Conflict>();

        // depo konumlarını tek bağlantıdan çöz (iş parçacıkları depoya yalnız okur)
        var rows = new Dictionary<string, BlobRow>();
        var chunks = new Dictionary<string, List<BlobRow>>();
        var missing = new List<(FileRec b, FileRec? c, string why)>();
        using (var c = Depo.Open())
        {
            var ok = new List<(FileRec b, FileRec? c, string why)>();
            foreach (var w in plan.Write)
            {
                var br = Depo.GetBlob(c, w.b.Blob!);
                List<BlobRow>? parts = null;
                var broken = br is null;
                if (br is { Codec: "parca" })
                {
                    parts = new();
                    foreach (var ch in Depo.GetChunks(c, br.Hash))
                    {
                        var cb = Depo.GetBlob(c, ch.ChunkHash);
                        if (cb is null) { broken = true; break; }
                        parts.Add(cb);
                    }
                }
                if (broken) { missing.Add(w); continue; }
                rows[w.b.Blob!] = br!;
                if (parts is not null) chunks[w.b.Blob!] = parts;
                ok.Add(w);
            }
            plan.Write = ok;
        }
        foreach (var (b, _, _) in missing) o.Unrestorable.Add(new Conflict(b.Path, "depo-eksik"));

        var restored = 0; var renamed = 0; var removed = 0;
        WorkPool.ForEach(plan.Write, opts.Parallel, w =>
        {
            var b = w.b;
            var target = SafePath(root, b.Path);
            if (target is null) { skipped.Add(new Conflict(b.Path, "guvensiz-yol")); return; }
            if (UnderReparse(root, target, reparse)) { skipped.Add(new Conflict(b.Path, "baglanti")); return; }
            var tmp = target + TempSuffix;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var f = File.OpenHandle(tmp, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.None, b.Size))
                {
                    Depo.WriteBlobTo(f, rows[b.Blob!], chunks.GetValueOrDefault(b.Blob!));
                    if (opts.Fsync) RandomAccess.FlushToDisk(f);
                }
                File.SetLastWriteTimeUtc(tmp, new DateTime(FolderScanner.ToTicks(b.MtimeNs), DateTimeKind.Utc));
                if (w.c is not null && !string.Equals(w.c.Path, b.Path, StringComparison.Ordinal)) // yalnız harf farkı: eski adı kaldır
                    DeleteQuiet(SafePath(root, w.c.Path));
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal); // salt okunur hedefin üstüne yazılabilsin
                File.Move(tmp, target, true);
                if (b.Attr != 0) File.SetAttributes(target, (FileAttributes)b.Attr);
                Interlocked.Increment(ref restored);
            }
            catch (SirAcilamadiException) { DeleteQuiet(tmp); corrupt.Add(new Conflict(b.Path, "sir-acilamadi")); } // sır kutusu bu hesapla açılamadı: dosyaya yazılmaz
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { DeleteQuiet(tmp); corrupt.Add(new Conflict(b.Path, "depo-bozuk")); } // depo içeriği eksik ya da özeti tutmuyor: dosyaya yazılmaz
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DeleteQuiet(tmp);
                skipped.Add(new Conflict(b.Path, "kilitli"));
            }
            catch { DeleteQuiet(tmp); throw; } // beklenmeyen hatada da yarım geçici dosya kalmasın
        });

        foreach (var (b, c) in plan.Rename)
        {
            var from = SafePath(root, c.Path); var to = SafePath(root, b.Path);
            if (from is null || to is null) { skipped.Add(new Conflict(b.Path, "guvensiz-yol")); continue; }
            var tmp = from + TempSuffix;
            try { File.Move(from, tmp); File.Move(tmp, to); renamed++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DeleteQuiet(tmp); skipped.Add(new Conflict(b.Path, "kilitli")); }
        }

        // ajanın oluşturduğu dosyaları sil
        var parents = new ConcurrentBag<string>();
        WorkPool.ForEach(plan.Remove, opts.Parallel, c =>
        {
            var full = SafePath(root, c.Path);
            if (full is null) { skipped.Add(new Conflict(c.Path, "guvensiz-yol")); return; }
            if (UnderReparse(root, full, reparse)) { skipped.Add(new Conflict(c.Path, "baglanti")); return; }
            try
            {
                if (File.Exists(full))
                {
                    if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) File.SetAttributes(full, FileAttributes.Normal);
                    File.Delete(full);
                    Interlocked.Increment(ref removed);
                }
                var i = c.Path.LastIndexOf('/');
                if (i > 0) parents.Add(c.Path[..i]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped.Add(new Conflict(c.Path, "kilitli")); }
        });

        // klasörler: ajanın sildiği boş klasör geri gelir; ajanın yarattığı boş klasör (ve bizim boşalttığımız dosya klasörleri) kalkar
        var bDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in B.Values)
        {
            var p = b.IsDir ? b.Path + "/x" : b.Path;
            int i;
            while ((i = p.LastIndexOf('/')) > 0) { p = p[..i]; if (!bDirs.Add(p)) break; }
        }
        var dirsCreated = 0;
        foreach (var d in plan.MakeDirs)
        {
            var full = SafePath(root, d);
            if (full is null || UnderReparse(root, full + "\\x", reparse)) continue;
            try { if (!Directory.Exists(full)) { Directory.CreateDirectory(full); dirsCreated++; } } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var dirsRemoved = 0;
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d0 in plan.RemoveDirs.Concat(parents.Distinct()).OrderByDescending(x => x.Length))
        {
            var d = d0;
            while (!string.IsNullOrEmpty(d) && tried.Add(d))
            {
                if (bDirs.Contains(d)) break;
                var full = SafePath(root, d);
                if (full is null) break;
                try
                {
                    if (!Directory.Exists(full)) { d = ParentOf(d); continue; }
                    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) break;
                    if (Directory.EnumerateFileSystemEntries(full).Any()) break;
                    Directory.Delete(full);
                    dirsRemoved++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { break; }
                d = ParentOf(d);
            }
        }

        o.Restored = restored; o.Renamed = renamed; o.Removed = removed; o.DirsCreated = dirsCreated; o.DirsRemoved = dirsRemoved;
        foreach (var s in skipped) o.Conflicts.Add(s);
        foreach (var s in corrupt) o.Unrestorable.Add(s);
    }

    private static string? ParentOf(string rel) { var i = rel.LastIndexOf('/'); return i > 0 ? rel[..i] : null; }

    private static void DeleteQuiet(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
