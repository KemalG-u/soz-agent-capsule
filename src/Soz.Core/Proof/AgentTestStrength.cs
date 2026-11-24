using System.Diagnostics;
using System.Text.RegularExpressions;
using Soz.Core.Restore;
using Soz.Core.Text;

namespace Soz.Core.Proof;

/// <summary>Ajanın yazdığı denemenin gücü (N5). Kind: strong (iş öncesi kodla kalıyor, iş sonrası geçiyor) | weak (iş öncesi de geçiyordu ya da gücü ölçülemedi, e3: ölçülemeyen zayıf sayılır) | always (komut test kalsa bile başarılı bitiyor). "unknown" artık üretilmez.</summary>
public sealed record StrengthResult(string Kind, string Sentence, string DefaultAnswer, bool Rejected);

/// <summary>
/// "Önce deneme yaz" işinde ajanın yazdığı deneme kişiye önerilirken gücü ölçülür: deneme, işten ÖNCEKİ kodla (geri alma görüntüsünden) koşar.
/// İş öncesi KALIYOR ve iş sonrası GEÇİYORSA deneme bir şey kanıtlar (güçlü). İş öncesi de geçiyorsa bir şey kanıtlamaz (zayıf, varsayılan cevap Hayır).
/// Komut test kalsa bile başarılı bitiyorsa öneri hiç kabul edilemez. Bu DAVRANIŞLA sınanır (SwallowsFailure): komut, testleri kesin kalan bir kopyada çalıştırılır; yine başarılıysa komut sonucu yutuyordur.
/// Kalıp listesi (AlwaysRx) yalnız hızlı ön süzgeçtir, kalıp uzatmak çözüm değildir.
/// </summary>
public static class AgentTestStrength
{
    public const string Strong = "strong", Weak = "weak", Unknown = "unknown", Always = "always";

    public const string WeakSentence = "Bu deneme iş yapılmadan önce de geçiyordu. Bir şey kanıtlamaz.";
    public const string AlwaysSentence = "Bu komut kod bozukken de başarılı bitiyor. Deneme sayamam.";

    private static readonly Regex AlwaysRx = new(
        @"\|\|\s*(?:true\b|:(?:\s|$)|exit\s+0\b|exit\s*/b\s*0\b|echo\b|\(?\s*exit\s+0|cd\s+\.|/bin/true\b|ver\s*>\s*nul|cmd\s*/c\s*exit\s+0|\$true\b)" +
        @"|(?:;|&&|&)\s*exit\s*(?:/b\s*)?0\b|;\s*true\s*$|\bset\s+\+e\b|\bcontinue-on-error\b|-ErrorAction\s+SilentlyContinue",
        RegexSpeed.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TrivialRx = new(@"^\s*(?:(?:cmd\s*/c\s+)?echo\b[^&|;\r\n]*|true|exit\s+(?:/b\s*)?0|:|cmd\s*/c\s+exit\s+(?:/b\s*)?0)\s*$", RegexSpeed.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Komut ve çağırdığı package.json betikleri hangi sonucu alırsa alsın başarılı mı bitiyor? (Tanınan kalıplar; her olası kaçamağı yakalamaz.)</summary>
    public static bool AlwaysSucceeds(string cmd, string? root)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var texts = new List<string> { cmd };
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            try { texts.Add(ScanContext.Build(root, new[] { cmd }).Text); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        // Betik gövdeleri satır satır ayrılır (Build.Text satır sonuyla birleştirir): her parça kendi başına da bakılır.
        foreach (var t in texts)
            foreach (var part in t.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (AlwaysRx.IsMatch(part) || TrivialRx.IsMatch(part)) return true;
        return false;
    }

    /// <summary>
    /// Gücü ölçer. before: işin başındaki görüntü (geri alma noktası); now: ajanın bıraktığı klasörün taraması (yeni testler ve ayarları yerinde kalır).
    /// Ölçülemezse (görüntü yok, dosya kopyası eksik, kopya kurulamadı) Unknown döner, uydurma sonuç vermez.
    /// </summary>
    public static StrengthResult Measure(string root, string dataDir, long jobId, string cmd, PointState before, IApprovedBlobs blobs, EvidenceBase.Snapshot now,
        long maxCopyBytes, int timeoutS, Func<string?>? edge = null)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan Remaining() => TimeSpan.FromSeconds(Math.Max(0, timeoutS - clock.Elapsed.TotalSeconds));
        if (AlwaysSucceeds(cmd, root)) return new StrengthResult(Always, AlwaysSentence, "hayir", true);
        string? baseDir = null;
        var parent = TempTree.ParentDir(dataDir);
        try
        {
            baseDir = TempTree.NewBase(dataDir, $"n5-{jobId}-{Guid.NewGuid().ToString("N")[..8]}");
            var copy = Path.Combine(baseDir, "p");
            var (stats, why) = TempTree.Copy(root, copy, dataDir, maxCopyBytes, EvidenceBase.MaxFiles);
            if (stats is null) return Unknown_("Geçici bir kopya kurulamadı: " + why);
            var copyFull = Path.GetFullPath(copy).TrimEnd('\\', '/');

            // 1) Ajanın değiştirdiği ya da sildiği dosyalar işten ÖNCEKİ içeriğine döner.
            foreach (var (p, rec) in before)
            {
                if (rec.IsDir || ShouldKeep(p, now)) continue;
                var dest = Path.GetFullPath(Path.Combine(copyFull, p.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(copyFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || SirKutusu.IsSecretFile(p) || InSkippedDir(p)) continue;
                var fi = new FileInfo(dest);
                var same = rec.Size > 65536 && fi.Exists && fi.Length == rec.Size && FolderScanner.ToNs(fi.LastWriteTimeUtc.Ticks) == rec.MtimeNs;
                if (same) continue;
                if (rec.Blob is null) return Unknown_($"İş öncesi «{p}» dosyasının kopyası alınmamış (çok büyük ya da atlanmış).");
                if (!blobs.TryWrite(rec.Blob, dest)) return Unknown_($"İş öncesi «{p}» dosyasının kopyasını geri alma deposunda bulamadım.");
            }
            // 2) Ajanın eklediği dosyalar kalkar; yalnız yeni deneme ve onun yardımcıları (test dosyaları, test ayarları, package.json) yerinde kalır.
            foreach (var n in now.Suspects)
            {
                if (before.ContainsKey(n.Path) || ShouldKeep(n.Path, now)) continue;
                var dest = Path.GetFullPath(Path.Combine(copyFull, n.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (dest.StartsWith(copyFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) { File.SetAttributes(dest, FileAttributes.Normal); File.Delete(dest); }
            }
            // Kuşkulu olmayan, ajanın eklediği kaynak dosyalar da (ör. lib/yeni.js) kalkar.
            RemoveAdded(copyFull, copyFull, before, now);

            var spec = new System.Text.Json.Nodes.JsonObject { ["cmd"] = cmd, ["expectExit"] = 0 };
            var ev = Path.Combine(dataDir, "kanit", $"n5-{jobId}-{Guid.NewGuid().ToString("N")[..6]}");
            try
            {
                var runner = new ProofRunner(copyFull, ev, edge, dataDir, jobId, null);
                if (Remaining() <= TimeSpan.Zero) return Unmeasured("Süre sınırı doldu.");
                var o = runner.Run(new CheckToRun(0, CheckKinds.Command, "güç ölçümü", spec, timeoutS), Remaining());
                if (o.Status == ResultStatus.Passed)
                    return new StrengthResult(Weak, WeakSentence, "hayir", false);
                if (o.Status != ResultStatus.Failed)
                    return Unknown_("İş öncesi koşu sonuç vermedi.");
                // İş öncesi kaldı: iş sonrası geçiyor mu? (Geçmiyorsa deneme zaten bozuk: güçlü sayılmaz.)
                if (Remaining() <= TimeSpan.Zero) return Unmeasured("Süre sınırı doldu.");
                var after = new ProofRunner(root, ev, edge, dataDir, jobId, null).Run(new CheckToRun(0, CheckKinds.Command, "güç ölçümü", spec, timeoutS), Remaining());
                return after.Status == ResultStatus.Passed
                    ? new StrengthResult(Strong, "Bu deneme iş yapılmadan önce geçmiyordu, şimdi geçiyor. Bir şey kanıtlar.", "evet", false)
                    : Unknown_("Deneme iş yapıldıktan sonra da geçmiyor.");
            }
            finally { try { if (Directory.Exists(ev)) Directory.Delete(ev, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Unknown_("Geçici bir kopyada dosya hatası: " + e.Message);
        }
        finally
        {
            if (baseDir is not null) TempTree.RemoveWithRetry(baseDir, parent);
        }
    }

    private static StrengthResult Unknown_(string why) => Unmeasured(why);

    /// <summary>Gücü ölçülemeyen deneme "zayıf" sayılır (e3, K6): varsayılan cevap Hayır, cümle ölçemediğimi açıkça söyler.</summary>
    public static StrengthResult Unmeasured(string why)
    {
        var w = why.Trim();
        if (w.Length > 120) w = w[..120].TrimEnd() + "…";
        if (!w.EndsWith('.') && !w.EndsWith('…')) w += ".";
        return new StrengthResult(Weak, "Bu denemenin işten önce geçip geçmediğini ölçemedim: " + w + " Zayıf sayıyorum.", "hayir", false);
    }

    /// <summary>
    /// DAVRANIŞLA SINAMA (e3, K6): komutu, testleri KESİN KALAN bir kopyada çalıştırır. Kopyada ajanın test dosyalarının ve komutun çağırdığı dosyaların sonuna dil tanımının
    /// "kesin kalan" satırı eklenir (RunnerDefs.FailAppendFor). Komut yine başarılı bitiyorsa test sonucunu yutuyordur: true. Kalıyorsa false.
    /// Sınanamadıysa (kesin kalan yapılabilecek dosya yok, kopya kurulamadı, komut sonuç vermedi) null: bu yoldan karar verilmez.
    /// </summary>
    public static bool? SwallowsFailure(string root, string dataDir, long jobId, string cmd, EvidenceBase.Snapshot now, long maxCopyBytes, int timeoutS, Func<string?>? edge = null)
    {
        var clock = Stopwatch.StartNew();
        string? baseDir = null;
        var parent = TempTree.ParentDir(dataDir);
        try
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in now.Suspects) if (e.Cls == EvidenceBase.ClsTest) targets.Add(e.Path);
            foreach (var r in ScanContext.Build(root, new[] { cmd }).Referenced) targets.Add(r);
            baseDir = TempTree.NewBase(dataDir, $"k6-{jobId}-{Guid.NewGuid().ToString("N")[..8]}");
            var copy = Path.Combine(baseDir, "p");
            var (stats, _) = TempTree.Copy(root, copy, dataDir, maxCopyBytes, EvidenceBase.MaxFiles);
            if (stats is null) return null;
            var copyFull = Path.GetFullPath(copy).TrimEnd('\\', '/');
            var broken = 0;
            foreach (var rel in targets)
            {
                var add = RunnerDefs.FailAppendFor(rel);
                if (add is null || SirKutusu.IsSecretFile(rel)) continue;
                var dest = Path.GetFullPath(Path.Combine(copyFull, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(copyFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(dest)) continue;
                File.SetAttributes(dest, FileAttributes.Normal);
                File.AppendAllText(dest, add);
                broken++;
            }
            if (broken == 0) return null;
            var spec = new System.Text.Json.Nodes.JsonObject { ["cmd"] = cmd, ["expectExit"] = 0 };
            var ev = Path.Combine(dataDir, "kanit", $"k6-{jobId}-{Guid.NewGuid().ToString("N")[..6]}");
            try
            {
                var remaining = TimeSpan.FromSeconds(Math.Max(0, timeoutS - clock.Elapsed.TotalSeconds));
                if (remaining <= TimeSpan.Zero) return null;
                var o = new ProofRunner(copyFull, ev, edge, dataDir, jobId, null).Run(new CheckToRun(0, CheckKinds.Command, "kesin kalan sınama", spec, timeoutS), remaining);
                // Ölçü komutun ÇIKIŞ KODUdur: testler kesin kalırken komut 0 ile bitiyorsa sonucu yutuyordur (Söz'ün çıktı denetimi ayrıca "geçmedi" dese bile).
                return o.ExitCode is null ? null : o.ExitCode == 0;
            }
            finally { try { if (Directory.Exists(ev)) Directory.Delete(ev, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        finally { if (baseDir is not null) TempTree.RemoveWithRetry(baseDir, parent); }
    }

    private static bool InSkippedDir(string rel)
        => rel.Replace('\\', '/').Split('/').SkipLast(1).Any(s => Exclusions.Skip.Contains(s) || Exclusions.DepRoots.Contains(s) || Exclusions.Build.Contains(s));

    /// <summary>Ajanın yeni denemesi ve yardımcıları: iş öncesi görüntüye çevrilmez, olduğu gibi kalır.</summary>
    private static bool ShouldKeep(string rel, EvidenceBase.Snapshot now)
    {
        var e = now.Suspects.FirstOrDefault(x => x.Path == rel);
        if (e is null) return false;
        return e.Cls == EvidenceBase.ClsTest || ApprovedSet.RemoveWhenAdded(e) || Path.GetFileName(rel).Equals("package.json", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveAdded(string root, string dir, PointState before, EvidenceBase.Snapshot now)
    {
        foreach (var d in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(d);
            if (Exclusions.Skip.Contains(name) || Exclusions.DepRoots.Contains(name) || Exclusions.Build.Contains(name)) continue;
            if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            RemoveAdded(root, d, before, now);
        }
        foreach (var f in Directory.GetFiles(dir))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (before.ContainsKey(rel) || ShouldKeep(rel, now) || SirKutusu.IsSecretFile(rel) || rel.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase)) continue;
            // İş öncesi görüntüde olmayan her dosya ajanın eklediğidir; test yardımcısı ya da fixture ise ShouldKeep zaten tuttu.
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
    }
}
