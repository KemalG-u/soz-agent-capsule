using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Text;

namespace Soz.Core.Proof;

public static class CheckKinds
{
    public const string Command = "command", File = "file", Url = "url", Screenshot = "screenshot";
    public const string FileRange = "file-range", DocHeading = "doc-heading", DiffSummary = "diff-summary", LocalAddress = "local-address", TableRecon = "table-recon", HumanChecklist = "human-checklist";

    /// <summary>Bilinen bütün ölçüt türleri: ölçücü kaydından gelir (tek kaynak).</summary>
    public static IReadOnlyList<string> All => MeasurerRegistry.Kinds;
}

public static class ResultStatus
{
    public const string Passed = "passed", Failed = "failed", Skipped = "skipped", Error = "error";
    /// <summary>Komut başarılı göründü ama kanıt değeri yok (hiç test koşmadı, testler atlandı): "geçti" sayılmaz.</summary>
    public const string Unchecked = "unchecked";
    /// <summary>İnsan onay listesinde işaretlenmemiş madde var: ölçüm değil kabul bekleniyor. "geçti" sayılmaz.</summary>
    public const string AwaitingHuman = "awaiting_human";
}

public static class Verdicts
{
    public const string Passed = "passed", Failed = "failed", Unchecked = "unchecked", Error = "error";
}

/// <summary>Çalıştırılacak tek ölçüt.</summary>
public sealed record CheckToRun(long Id, string Kind, string Label, JsonObject Spec, int TimeoutS);

/// <summary>Tek ölçütün sonucu. OutputTail sır süzgecinden geçmiştir (en çok 4000 karakter).</summary>
public sealed record CheckOutcome(long CheckId, string Label, string Status, int? ExitCode, string? OutputTail, string? ArtifactName, long DurationMs, int SkippedTests = 0, string? Note = null,
    int? TestsTotal = null, int? TestsPassed = null, bool CountUnknown = false, string? CmdKey = null, string? ReasonCode = null);

/// <summary>
/// Ölçütleri ajandan bağımsız çalıştırır. Kural: emin olunamayan her durum "error" (asla "passed").
/// Hangi türün nasıl ölçüldüğü <see cref="MeasurerRegistry"/>'dedir; bilinmeyen tür "error" verir.
/// Komutlar proje kökünde (ya da kökün altındaki cwd'de), ayrı süreçte, kendi zaman aşımıyla çalışır.
/// Ortama sır eklenmez; yalnız renk kodlarını kapatan iki değişken verilir.
/// </summary>
public sealed class ProofRunner
{
    public const int MaxTail = 4000;
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexSpeed.Compiled);

    private readonly MeasureContext _ctx;

    /// <summary>dataDir verilirse komut ölçütleri Söz'ün ajan iş nesnesinde ve ajan ortam değişkenleriyle çalışır (proje kodu ajan kodudur).</summary>
    public ProofRunner(string projectRoot, string evidenceDir, Func<string?>? edgeLocator = null, string? dataDir = null, long? jobId = null, long? proofRunId = null,
        Func<CheckToRun, IReadOnlySet<int>>? humanTicks = null)
    {
        _ctx = new MeasureContext
        {
            Root = Path.GetFullPath(projectRoot),
            EvidenceDir = evidenceDir,
            FindEdge = edgeLocator ?? (() => EdgeLocator.Find()),
            DataDir = dataDir,
            JobId = jobId,
            HumanTicks = humanTicks,
        };
    }

    public CheckOutcome Run(CheckToRun c, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var m = MeasurerRegistry.Find(c.Kind);
            var o = m is null
                ? new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, "Bilinmeyen kontrol türü: " + c.Kind, null, 0)
                : m.Measure(_ctx, c, timeout);
            return o with { DurationMs = sw.ElapsedMilliseconds };
        }
        catch (Exception e)
        {
            return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, Tail($"Kontrol çalıştırılamadı: {e.GetType().Name}: {e.Message}"), null, sw.ElapsedMilliseconds);
        }
    }

    // ---------- yardımcılar ----------
    public static string Tail(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = Ansi.Replace(text, "").Replace("\r\n", "\n");
        var masked = SecretFilter.MaskSecrets(clean);
        return masked.Length <= MaxTail ? masked : "…" + masked[^(MaxTail - 1)..];
    }

    /// <summary>Aynı ölçütü kanıt geçmişinde tanımak için (iş kopyaları farklı numaralı olsa da): komut ve klasör.</summary>
    public static string CmdKeyOf(string cmd, string? cwd)
        => System.Text.RegularExpressions.Regex.Replace(cmd.Trim(), @"\s+", " ") + "|" + (cwd ?? "").Trim();

    private static readonly Regex TestCommandRe = new(@"(?:\btests?\b|\bpytest\b|\bjest\b|\bvitest\b|\bmocha\b|\bphpunit\b|\bctest\b|\bava\b|--test\b)", RegexSpeed.Compiled | RegexOptions.IgnoreCase);
    public static bool LooksLikeTestCommand(string cmd) => TestCommandRe.IsMatch(cmd);
}

/// <summary>msedge.exe yolunu bulur (ayar → Program Files (x86) → Program Files → LocalAppData).</summary>
public static class EdgeLocator
{
    public static string? Override { get; set; }

    public static string? Find()
    {
        if (!string.IsNullOrWhiteSpace(Override)) return File.Exists(Override) ? Override : null;
        var cands = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var root in cands)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
