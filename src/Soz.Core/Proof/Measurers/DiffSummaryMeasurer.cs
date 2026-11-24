using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>
/// Fark özeti (`diff-summary`): iş başladığından beri hangi dosyalar eklendi, değişti, silindi ve hepsi beklenen kümenin içinde mi.
/// "Önceki durum" iş çalışmaya başladığı anda alınan klasör listesidir (yol, boyut, zaman, küçük dosyalar için özet); kayıt veri klasöründe,
/// işe özel durur (`kanit\is-&lt;iş&gt;\fark-&lt;ölçüt&gt;.json`). Önceki durum yoksa (iş Söz'ün gördüğü bir anda başlamadı) sonuç "doğrulanamadı": asla "geçti" değil.
/// Beklenen küme: dosya adı ya da desen (`*` bir klasör içinde, `**` her yerde, sonu `/` olan desen klasör). Hiçbir şey değişmediyse ve değişiklik bekleniyorsa "kaldı".
/// Yanlış geçme yolu: beklenen dosya değişti ama içine yanlış şey yazıldı (içeriğe başka ölçüt bakar); boyutu ve zamanı aynı bırakan elle oynama küçük dosyalarda özetle yakalanır.
/// </summary>
public sealed class DiffSummaryMeasurer : MeasurerBase
{
    public const int MaxFiles = EvidenceBase.MaxFiles;
    public const long HashLimit = 256 * 1024;
    public const int MaxPatterns = 50, MaxPatternLength = 200;

    public override string Kind => CheckKinds.DiffSummary;
    public override string Level => Confidence.B;
    public override BaselineMode Baseline => BaselineMode.None;

    public override string? Validate(JsonObject spec)
    {
        var allowed = List(spec, "allowed");
        if (allowed.Count == 0) return "Beklenen dosya kümesi boş: spec.allowed içinde en az bir dosya adı ya da desen olmalı.";
        if (allowed.Count > MaxPatterns) return $"En çok {MaxPatterns} desen yazılabilir.";
        foreach (var p in allowed)
        {
            if (p.Length > MaxPatternLength) return $"Desen en çok {MaxPatternLength} karakter olabilir: {p[..20]}…";
            if (Path.IsPathRooted(p) || p.Contains(':') || p.Split('/', '\\').Contains("..")) return "Desenler proje klasörüne göre yazılır (mutlak yol ve .. olmaz): " + p;
        }
        if (L(spec, "maxChanged") is < 0) return "En çok değişiklik sayısı 0 ya da daha büyük olmalı.";
        return SchemaProblem(spec);
    }

    public override string Describe(JsonObject spec) => $" (yalnız şu dosyalar değişebilir: {string.Join(", ", List(spec, "allowed").Take(5))})";

    // ---------- klasör listesi ----------
    public sealed record Entry(long Size, long MTime, string? Sha);
    public sealed record Manifest(string At, bool Truncated, Dictionary<string, Entry> Files);

    public static string ManifestPath(string dataDir, long jobId, long checkId) => Path.Combine(dataDir, "kanit", "is-" + jobId, $"fark-{checkId}.json");

    private static bool Ignored(string name) => EvidenceBase.IsIgnoredDir(name);

    /// <summary>Klasörü tarar (kısayol/bağlantı klasörlerine girmez; ilgisiz araç klasörlerini ve veri klasörünü atlar).</summary>
    public static Manifest Scan(string root, string? dataDir)
    {
        var files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var skipFull = dataDir is null ? null : Path.GetFullPath(dataDir).TrimEnd('\\', '/');
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(root));
        while (stack.Count > 0 && !truncated)
        {
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (e is DirectoryInfo d)
                {
                    if (Ignored(d.Name)) continue;
                    if (skipFull is not null && string.Equals(d.FullName.TrimEnd('\\', '/'), skipFull, StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(d.FullName);
                    continue;
                }
                var f = (FileInfo)e;
                if (files.Count >= MaxFiles) { truncated = true; break; }
                string? sha = null;
                if (f.Length <= HashLimit)
                {
                    try { using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); sha = Convert.ToHexString(SHA256.HashData(fs))[..16]; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sha = null; }
                }
                files[Path.GetRelativePath(root, f.FullName).Replace('\\', '/')] = new Entry(f.Length, f.LastWriteTimeUtc.Ticks, sha);
            }
        }
        return new Manifest(Time.NowIso(), truncated, files);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>İş çalışmaya başlarken (ve başlangıç ölçümünden sonra, refresh) bu işin açık fark özeti ölçütleri için önceki durumu yazar. Hata sızmaz.</summary>
    public static void TakeForJob(SqliteConnection c, long jobId, long projectId, string root, bool refresh)
    {
        try
        {
            var dataDir = Path.GetDirectoryName(c.DataSource);
            if (string.IsNullOrEmpty(dataDir)) return;
            var checks = CheckStore.EnabledFor(c, projectId, jobId).Where(x => x.Kind == CheckKinds.DiffSummary).ToList();
            if (checks.Count == 0) return;
            Manifest? manifest = null;
            foreach (var ch in checks)
            {
                var path = ManifestPath(dataDir, jobId, ch.Id);
                if (File.Exists(path) && !refresh) continue;
                manifest ??= Scan(root, dataDir);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(manifest, Json), new UTF8Encoding(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or JsonException) { /* önceki durum yoksa ölçüt "doğrulanamadı" der */ }
    }

    private static Manifest? Load(string path)
    {
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json);
            return m is null ? null : m with { Files = new Dictionary<string, Entry>(m.Files, StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // ---------- desen ----------
    internal static Regex Glob(string pattern)
    {
        var p = pattern.Trim().Replace('\\', '/').TrimStart('/');
        if (p.StartsWith("./")) p = p[2..];
        var dirOnly = p.EndsWith('/');
        var hasWild = p.Contains('*') || p.Contains('?');
        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var ch = p[i];
            if (ch == '*')
            {
                if (i + 1 < p.Length && p[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < p.Length && p[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                    else sb.Append(".*");
                }
                else sb.Append("[^/]*");
            }
            else if (ch == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        // Joker yoksa ad hem dosya hem klasör olabilir: klasörse içindeki her şey kapsanır.
        if (dirOnly) sb.Append(".*");
        else if (!hasWild) sb.Append("(?:/.*)?");
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// "İş başlarken klasörün listesi alınmadı" cümlesi, kişiye NEDENİNİ söyler: ölçüt işin ajanı başladıktan sonra eklenmişse bunu açıkça yazar
    /// (fark özeti işin başındaki klasör listesine ihtiyaç duyar; sonradan eklenen ölçüt için o an geçmiştir). Neden okunamazsa genel cümle döner.
    /// </summary>
    internal static string NoBaselineText(string dataDir, long jobId, long checkId)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "soz.db"), Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var db = new SqliteConnection(cs);
            db.Open();
            var created = db.Scalar("SELECT created_at FROM checks WHERE id=@c", null, ("@c", checkId)) as string;
            var started = db.Scalar("SELECT MIN(started) FROM runs WHERE job_id=@j", null, ("@j", jobId)) as string;
            if (created is not null && started is not null && string.CompareOrdinal(created, started) > 0)
                return "Bu fark özeti kontrolünü işin yardımcısı başladıktan sonra ekledin. İşin başındaki klasör listesi o zaman alınmadığı için neyin değiştiğini söyleyemem. Kontrolü iş verilmeden önce ekle (ya da işi bu kontrolle yeniden ver).";
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { /* neden bulunamazsa genel cümle */ }
        return "İş başlarken klasörün listesi alınmamış (kontrol o an açık değildi ya da iş Söz'ün görmediği bir anda başladı), bu yüzden neyin değiştiğini söyleyemem.";
    }

    // ---------- ölçüm ----------
    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var allowed = List(c.Spec, "allowed");
        if (allowed.Count == 0) return Error(c, "Beklenen dosya kümesi boş.");
        if (ctx.DataDir is null || ctx.JobId is null)
            return Res(c, ResultStatus.Unchecked, "Bu kontrol bir işe bağlı çalışır. İş olmadığı için önceki durum bilinmiyor. Bu bir \"geçti\" değildir.", reasonCode: ProofStore.Reasons.NoBase);
        var basePath = ManifestPath(ctx.DataDir, ctx.JobId.Value, c.Id);
        var before = File.Exists(basePath) ? Load(basePath) : null;
        if (before is null)
            return Res(c, ResultStatus.Unchecked, NoBaselineText(ctx.DataDir, ctx.JobId.Value, c.Id) + " Bu bir \"geçti\" değildir.", reasonCode: ProofStore.Reasons.NoBase);

        var now = Scan(ctx.Root, ctx.DataDir);
        if (before.Truncated || now.Truncated)
            return Res(c, ResultStatus.Unchecked, $"Klasörde {MaxFiles:N0} dosyadan fazlası var, izleyemedim. Bu bir \"geçti\" değildir.", reasonCode: ProofStore.Reasons.NotTracked);

        var added = new List<string>(); var modified = new List<string>(); var deleted = new List<string>();
        foreach (var (rel, e) in now.Files)
        {
            if (!before.Files.TryGetValue(rel, out var b)) { added.Add(rel); continue; }
            if (b.Size == e.Size && b.MTime == e.MTime) continue;
            if (b.Size == e.Size && b.Sha is not null && b.Sha == e.Sha) continue; // yalnız zamanı değişmiş
            modified.Add(rel);
        }
        foreach (var rel in before.Files.Keys) if (!now.Files.ContainsKey(rel)) deleted.Add(rel);
        added.Sort(StringComparer.OrdinalIgnoreCase); modified.Sort(StringComparer.OrdinalIgnoreCase); deleted.Sort(StringComparer.OrdinalIgnoreCase);

        var changed = added.Select(x => (x, "eklendi")).Concat(modified.Select(x => (x, "değişti"))).Concat(deleted.Select(x => (x, "silindi"))).ToList();
        var matchers = allowed.Select(Glob).ToList();
        bool Allowed(string rel)
        {
            try { return matchers.Any(m => m.IsMatch(rel)); }
            catch (RegexMatchTimeoutException) { return false; }
        }
        var outside = changed.Where(x => !Allowed(x.x)).ToList();
        var artifact = changed.Count == 0 ? null : ctx.SaveEvidence($"fark-{c.Id}.txt", string.Join("\n", changed.Select(x => $"{x.Item2}: {x.x}")));
        string Show(IEnumerable<(string x, string)> l, int n = 6) { var a = l.ToList(); return string.Join(", ", a.Take(n).Select(t => $"{t.Item1} ({t.Item2})")) + (a.Count > n ? $" ve {a.Count - n} dosya daha" : ""); }

        if (outside.Count > 0)
            return Res(c, ResultStatus.Failed, $"Beklenen kümenin DIŞINDA {outside.Count} dosya değişmiş: {Show(outside)}. Toplam {changed.Count} değişiklik ({added.Count} eklendi, {modified.Count} değişti, {deleted.Count} silindi).", artifact);
        if (changed.Count == 0 && (B(c.Spec, "requireChange") ?? true))
            return Res(c, ResultStatus.Failed, "İş başladığından beri beklenen dosyaların hiçbiri değişmemiş.");
        if (L(c.Spec, "maxChanged") is { } max && changed.Count > max)
            return Res(c, ResultStatus.Failed, $"{changed.Count} dosya değişmiş (en çok {max} bekleniyordu): {Show(changed)}.", artifact);
        return Res(c, ResultStatus.Passed, changed.Count == 0 ? "Hiçbir dosya değişmemiş (beklendiği gibi)." : $"{changed.Count} dosya değişti, hepsi beklenen kümenin içinde: {Show(changed)}.", artifact);
    }

    public override (string label, JsonObject spec) FromTemplate(string templateId, IReadOnlyDictionary<string, string> values, string? root)
    {
        var allowed = SplitList(TemplateValues.Get(values, "allowed")).ToList();
        if (allowed.Count == 0) throw new SozException(400, "Beklenen dosyalar boş olamaz.");
        var spec = new JsonObject { ["allowed"] = new JsonArray(allowed.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["template"] = templateId };
        var err = new DiffSummaryMeasurer().Validate(spec);
        if (err is not null) throw new SozException(400, err);
        return ($"Yalnız şu dosyalar değişsin: {string.Join(", ", allowed.Take(5))}{(allowed.Count > 5 ? " ..." : "")}", spec);
    }
}
