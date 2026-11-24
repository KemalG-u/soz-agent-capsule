using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Text;

namespace Soz.Core.Proof;

/// <summary>Hüküm koşusunun (onaylı testlerle ikinci koşu) kanıt çalışmasına bağlanan kısmı: koşu, cümleler, yeni test teklifi (e2).</summary>
public static partial class ProofStore
{
    /// <summary>Hüküm koşusu sonucu. Cat: passed | failed | notests (onaylı koşu hiç test bulamadı) | unavail (koşu yapılamadı ya da sonuç belirsiz; Why sade neden).</summary>
    private sealed record Judged(string Cat, string? Why, List<CheckOutcome> Outcomes, long Ms);

    /// <summary>Hüküm koşusunun olay türü: süre ölçümü ve nedeni (kartta gösterilmez).</summary>
    public const string VerdictRunEvent = "verdict_run";

    private static Judged RunApproved(SozDb db, long projectId, long jobId, long runId, string root, List<CheckDto> checks,
        EvidenceBase.Snapshot approved, EvidenceBase.Snapshot now, Func<string?>? edge, int capSeconds)
    {
        SozSettings st; using (var c = db.Open()) st = SozSettings.Load(c);
        var secs = Math.Min(st.ClampedInt("setting:proof.verdict-run-seconds"), capSeconds); // Stop kancasının zaman aşımı kapı bütçesi + 30 sn: ikinci koşu bunu aşamaz
        var mb = st.ClampedInt("setting:proof.verdict-copy-max-mb");
        var toRun = checks.Where(x => x.Kind == CheckKinds.Command).Select(x => new CheckToRun(x.Id, x.Kind, x.Label, x.Spec, x.TimeoutS)).ToList();
        VerdictRunResult res;
        using (var blobs = DepotBlobs.TryOpen(db.DataDir))
        {
            VerdictRun.SweepOld(db.DataDir);
            res = VerdictRun.Execute(new VerdictRunRequest(root, db.DataDir, jobId, runId, approved, now, toRun, secs, (long)mb << 20, blobs, edge));
        }
        string cat; string? why = null, rawWhy = null;
        if (!res.Ran) { cat = "unavail"; why = res.Why; }
        else
        {
            var (v, _) = Judge(res.Outcomes);
            if (v == Verdicts.Passed) cat = "passed";
            else if (v == Verdicts.Failed) cat = "failed";
            else if (v == Verdicts.Unchecked && res.Outcomes.Any(o => o.Status == ResultStatus.Unchecked && o.ReasonCode == Reasons.NoTests)) cat = "notests";
            else
            {
                cat = "unavail";
                if (res.Outcomes.Any(o => o.Status == ResultStatus.Skipped)) why = $"testler {secs} saniyelik sürede bitmedi.";
                else
                {
                    var bad = res.Outcomes.FirstOrDefault(o => o.Status is ResultStatus.Error or ResultStatus.Unchecked);
                    var line = bad?.Note ?? bad?.OutputTail?.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    why = PlainRunnerMessage(line);
                    rawWhy = line;
                }
            }
        }
        try
        {
            using var c = db.Open();
            Events.Log(c, VerdictRunEvent, projectId, JsonSerializer.Serialize(new { jobId, runId, ok = res.Ran, cat, why, rawWhy, ms = res.Ms, restored = res.Restored, removed = res.Removed, files = res.CopiedFiles }));
        }
        catch (SqliteException) { }
        return new Judged(cat, why, res.Outcomes, res.Ms);
    }

    // ------------------------------------------------------------------ cümleler
    /// <summary>Koşucunun ham iletisi ("[Söz] Zaman aşımı: 10 sn içinde bitmedi. Başlattığım süreç ağacı kapatıldı...") kişiye gün diliyle söylenir. Ham satır olay kaydında kalır.</summary>
    public static string PlainRunnerMessage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "Testler sonuç vermedi.";
        var l = line.Trim();
        var m = Regex.Match(l, @"Zaman aşımı\D{0,6}(?<n>\d+)\s*(?:sn|saniye)", RegexOptions.IgnoreCase);
        if (m.Success) return $"Testler {m.Groups["n"].Value} saniyede bitmedi.";
        if (l.Contains("Zaman aşımı", StringComparison.OrdinalIgnoreCase)) return "Testler süre sınırında bitmedi.";
        if (l.StartsWith("[Söz]", StringComparison.Ordinal)) l = l[5..].Trim();
        if (l.Length > 110) l = l[..110].TrimEnd() + "…";
        return l.Length == 0 ? "Testler sonuç vermedi." : "Testler sonuç vermedi (" + l.TrimEnd('.') + ").";
    }

    /// <summary>Test adı mı, dosya yolu mu? Kod okumayan kişiye yol gösterilmez, test adı gösterilir.</summary>
    private static bool LooksLikePath(string n) => n.Contains('/') || n.Contains('\\') || Regex.IsMatch(n, @"\.(?:js|mjs|cjs|ts|tsx|jsx|py|go|cs|java|rb)$", RegexOptions.IgnoreCase);

    private static string NameList(IReadOnlyList<string> names)
    {
        var ok = names.Where(n => !LooksLikePath(n)).Take(3).ToList();
        if (ok.Count == 0) return "";
        return (ok.Count == 1 ? " Geçmeyen test: " : " Geçmeyen testler: ") + string.Join(", ", ok.Select(n => "«" + n + "»")) + ".";
    }

    private const string FailAdvice = " Yardımcının değişikliğini kabul edebilir ya da işi yardımcıya geri gönderebilirsin.";

    /// <summary>İzlenmeyen klasörlerde (coverage, dist, .git...) iş boyunca değişenlerin adları. İş başı görüntüsü eski sürümse (parmak izi yok) karşılaştırılamaz: boş döner.</summary>
    internal static List<string> IgnoredChanges(EvidenceBase.Snapshot before, EvidenceBase.Snapshot now)
    {
        if (!before.Graph || before.Ignored is null) return new List<string>();
        var a = new HashSet<string>(before.Ignored, StringComparer.Ordinal);
        var b = new HashSet<string>(now.Ignored ?? new List<string>(), StringComparer.Ordinal);
        static string Name(string x) { var i = x.IndexOf('|'); return i < 0 ? x : x[..i]; }
        return a.Except(b).Concat(b.Except(a)).Select(Name).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static string Poss(int n)
    {
        if (n % 10 != 0) return (n % 10) switch { 1 => "i", 2 => "si", 3 => "ü", 4 => "ü", 5 => "i", 6 => "sı", 7 => "si", 8 => "i", _ => "u" };
        return n switch { 10 => "u", 20 => "si", 30 => "u", 40 => "ı", 50 => "si", 60 => "ı", 70 => "i", 80 => "i", 90 => "ı", _ => "" };
    }

    private static (int? total, int? failed) TestCounts(IEnumerable<CheckOutcome> o)
    {
        var known = o.Where(x => x.TestsTotal is not null).ToList();
        if (known.Count == 0) return (null, null);
        var t = known.Sum(x => x.TestsTotal!.Value);
        return (t, Math.Max(0, t - known.Sum(x => x.TestsPassed ?? 0) - known.Sum(x => x.SkippedTests)));
    }

    private static string ApprovedFailedSentence(IReadOnlyList<CheckOutcome> b, bool anyEdit)
    {
        var (t, f) = TestCounts(b);
        var head = t is > 0 && f is > 0
            ? $"Onayladığın {t} testten {f}{(Poss(f.Value) is { Length: > 0 } s ? "'" + s : " tanesi")} yeni kodla geçmiyor."
            : "Onayladığın testlerden bazıları yeni kodla geçmiyor.";
        var names = FailedNames(b);
        // K2: "Ajan bu testleri de değiştirmiş" yalnız ajan bu işte onaylı bir dosyayı gerçekten değiştirdiyse söylenir.
        var tail = anyEdit
            ? " Yardımcı bu testleri de değiştirmiş, değişiklik gerekli olabilir."
            : " Yardımcı testlere dokunmadı, sorun kodda görünüyor.";
        return head + NameList(names) + tail + FailAdvice;
    }

    private static string AgentTestsFailSentence(IReadOnlyList<CheckOutcome> a)
    {
        var names = FailedNames(a);
        return "Onayladığın testler geçiyor ama yardımcının eklediği yeni testler geçmiyor." + NameList(names) + FailAdvice;
    }

    private static readonly Regex FailPrefix = new(@"^\s*(?:✖|✗|✘|×|not ok(?:\s+\d+)?\s*-?|FAILED|FAIL|●)\s+(?<n>\S.*?)\s*$", RegexSpeed.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Dur = new(@"\s*\(\d[\d.,]*\s*(?:ms|s)\)\s*$|\s+#.*$|\s+\d[\d.,]*ms$", RegexSpeed.Compiled);

    /// <summary>Çıktıdan geçmeyen testlerin adları (en çok 3). Tanınmayan biçimde boş döner: ad uydurulmaz.</summary>
    internal static List<string> FailedNames(IEnumerable<CheckOutcome> outcomes)
    {
        var res = new List<string>();
        foreach (var o in outcomes.Where(x => x.Status == ResultStatus.Failed))
            foreach (var raw in (o.OutputTail ?? "").Split('\n'))
            {
                var m = FailPrefix.Match(raw.TrimEnd('\r'));
                if (!m.Success) continue;
                var n = Dur.Replace(m.Groups["n"].Value, "").Trim();
                if (n.Length == 0 || n.Contains("failing tests", StringComparison.OrdinalIgnoreCase) || n.Equals("tests", StringComparison.OrdinalIgnoreCase) || n.StartsWith("test at ", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Length > 70) n = n[..70] + "…";
                if (!res.Contains(n)) res.Add(n);
                if (res.Count >= 3) return res;
            }
        return res;
    }

    private static void AddNamesDiff(List<EvidenceBase.FileDiff> diffs, string kind, string sentence, List<string> names)
    {
        if (names.Count == 0) return;
        diffs.Add(new EvidenceBase.FileDiff(kind == "approved-fail" ? "onaylı testler" : "yardımcının testleri", kind, sentence, names.Select(n => new EvidenceBase.DiffLine("-", n)).ToList()));
    }

    // ------------------------------------------------------------------ yeni testleri onaylı kümeye katma teklifi
    /// <summary>
    /// İş kanıtıyla bitti: bu işte ajanın eklediği ya da düzenlediği test dosyaları için "onaylı testlere katayım mı?" teklifi kaydedilir.
    /// Ayar (setting:proof.auto-approve-new-tests, varsayılan kapalı) açıksa YENİ eklenen dosyalar kendiliğinden katılır; düzenlenenler yine sorulur.
    /// </summary>
    private static void BuildOffer(SqliteConnection c, long projectId, long jobId, EvidenceBase.Snapshot baseSnap, EvidenceBase.Snapshot snapNow, EvidenceBase.Snapshot? approvedPrev, bool auto)
    {
        var jobChg = ApprovedSet.Changes(baseSnap, snapNow);
        if (!jobChg.Any) return;
        var inApproved = approvedPrev?.Suspects.Select(e => e.Path).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var added = jobChg.Added.Concat(jobChg.Edited.Where(p => !inApproved.Contains(p))).Distinct().ToList();
        var edited = jobChg.Edited.Where(inApproved.Contains).ToList();
        if (auto && added.Count > 0)
        {
            EvidenceBase.RecordTestOffer(c, projectId, jobId, snapNow, added, new List<string>());
            EvidenceBase.ApproveNewTests(c, projectId, jobId, addedOnly: true);
            added = new List<string>();
        }
        if (added.Count + edited.Count > 0) EvidenceBase.RecordTestOffer(c, projectId, jobId, snapNow, added, edited, approvedPrev);
    }
}
