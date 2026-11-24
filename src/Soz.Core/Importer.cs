using System.Diagnostics;
using System.Text.Json;
using Soz.Core.Cluster;
using Soz.Core.Data;
using Soz.Core.Extract;
using Soz.Core.Proposals;
using Soz.Core.Readers;
using Soz.Core.Text;

namespace Soz.Core;

public sealed class ImportOptions
{
    public string? ClaudeRoot { get; set; }
    public string? CodexRoot { get; set; }
    public bool SkipClaude { get; set; }
    public bool SkipCodex { get; set; }
    public int? SinceDays { get; set; }
    /// <summary>Kayıt defterindeki öteki gözle araçları için açık kök (araç kimliği → yol). Araç yine de kapalı gelir: bağlayıcısı Tanışma'da kabul edilmemişse okunmaz.</summary>
    public Dictionary<string, string?> ToolRoots { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bu içe aktarmada atlanacak araç kimlikleri (claude ve codex için SkipClaude/SkipCodex ile aynı etki).</summary>
    public HashSet<string> SkipTools { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Kabul edilen aracın "~" yolunu açacak kökler; boşsa bu bilgisayarın ortamı. Test sentetik ağaç verir.</summary>
    public Soz.Core.Onboarding.OnboardingRoots? Roots { get; set; }
    /// <summary>
    /// Kendiliğinden (arka plan) içe alma kipi (n6): kayıt dosyalarına yalnız bakılır; hiçbir dosya değişmediyse ayıklama, kümeleme ve olay kaydı hiç çalışmaz (işlemciyi yormaz).
    /// Değişen varsa aynı hat çalışır. Düğmeyle başlatılan içe alma bu kipte değildir.
    /// </summary>
    public bool SkipWhenIdle { get; set; }
}

public sealed class ImportSummary
{
    public List<ReadReport> Sources { get; set; } = new();
    /// <summary>Gözle araçları için sade notlar (kapalı araç, okunamayan kayıt). Boşsa söylenecek bir şey yok.</summary>
    public List<string> ObserveNotes { get; set; } = new();
    public int NewUtterances { get; set; }
    public int NewCompactions { get; set; }
    public int Extracted { get; set; }
    public int NewCandidates { get; set; }
    public int NewClusters { get; set; }
    public int NewRepeatClusters { get; set; }
    public int ParentClusters { get; set; }
    public int NewProposals { get; set; }
    public int UpdatedProposals { get; set; }
    public int WithdrawnProposals { get; set; }
    public long ReadMs { get; set; }
    public Soz.Core.Usage.UsageReport? UsageIngested { get; set; }
    public long ExtractMs { get; set; }
    public long ClusterMs { get; set; }
    public long TotalMs { get; set; }
    public string FinishedAt { get; set; } = "";
    /// <summary>SkipWhenIdle kipinde hiçbir kayıt değişmediği için hat atlandı.</summary>
    public bool Idle { get; set; }
    /// <summary>Bu içe almada en az bir kayıt dosyası okundu ya da yeni mesaj geldi.</summary>
    public bool Changed { get; set; }
}

/// <summary>İçe alma hattı: oku → ayıkla → kümele → öner. Aynı veri klasöründe aynı anda tek içe alma.</summary>
public static class Importer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static ImportSummary Run(SozDb db, ImportOptions? opt = null)
    {
        opt ??= new ImportOptions();
        Gate.Wait();
        try { return RunCore(db, opt); }
        finally { Gate.Release(); }
    }

    /// <summary>Başka bir içe alma sürüyorsa beklemeden null döner (arka plan içe alma düğmeyle başlatılanı bekletmez ve ona takılmaz).</summary>
    public static ImportSummary? TryRun(SozDb db, ImportOptions? opt = null)
    {
        opt ??= new ImportOptions();
        if (!Gate.Wait(0)) return null;
        try { return RunCore(db, opt); }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Kişinin Tanışma'da kabul ettiği gözle araçları (Hermes, OpenCode gibi) ile açıkça adı verilenleri okur. Hiçbir araç kendiliğinden okunmaz: kök yalnız kabul edilen
    /// maddeden (ya da açık bayraktan) gelir ve bağlayıcı kaydı onaylı olmalıdır; değilse sade bir not düşülür. Claude ve Codex burada değil, yukarıdaki dosya yolundadır.
    /// </summary>
    private static void ObserveAccepted(SozDb db, ImportOptions opt, ReadOptions ro, ImportSummary sum)
    {
        var tools = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, r) in new Soz.Core.Onboarding.OnboardingService(db).AcceptedObserveRoots(opt.Roots ?? Soz.Core.Onboarding.OnboardingRoots.FromEnvironment())) tools[t] = r;
        foreach (var (t, r) in opt.ToolRoots) tools[t] = r;
        foreach (var skip in opt.SkipTools) tools.Remove(skip);
        var load = Soz.Core.Registry.RegistryHost.Load(db);
        var active = Soz.Core.Agents.AgentRegistry.SpecsFrom(load);
        var definitions = Soz.Core.Agents.AgentRegistry.DefinitionsFrom(load);
        foreach (var (tool, root) in tools)
        {
            var def = definitions.FirstOrDefault(x => x.Id.Equals(tool, StringComparison.OrdinalIgnoreCase));
            if (def is null) { if (opt.ToolRoots.ContainsKey(tool)) sum.ObserveNotes.Add($"\"{tool}\" kayıt defterinde yok."); continue; }
            if (Readers.ReaderCatalog.Find(def.Reader)?.IsFileBased == true) continue; // dosya tabanlı araçlar (claude, codex) yukarıdaki yoldan okunur
            if (!active.Any(x => x.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase)))
            {
                sum.ObserveNotes.Add($"{def.Title} kapalı: Ayarlar sayfasından açılana kadar dosyalarına bakılmaz.");
                continue;
            }
            foreach (var r in Readers.ObserveRunner.Run(db, new Dictionary<string, string?> { [def.Id] = root }, ro, active))
            {
                if (r.Report is not null)
                {
                    sum.Sources.Add(r.Report);
                    if (!string.IsNullOrWhiteSpace(r.Report.Note)) sum.ObserveNotes.Add(r.Report.Note);
                }
                if (r.Problem is not null) sum.ObserveNotes.Add(r.Problem);
            }
        }
    }

    private static ImportSummary RunCore(SozDb db, ImportOptions opt)
    {
        var total = Stopwatch.StartNew();
        var settings = SozSettings.Load(db);
        var store = new IngestStore(db);
        var ro = new ReadOptions { SinceUtc = opt.SinceDays is > 0 ? Time.UtcNow().AddDays(-opt.SinceDays.Value) : null };
        var sum = new ImportSummary();

        var sw = Stopwatch.StartNew();
        if (!opt.SkipClaude && !opt.SkipTools.Contains("claude"))
        {
            var root = SourceLocator.Claude(opt.ClaudeRoot, settings.ClaudeRoot);
            sum.Sources.Add(new ClaudeCodeReader().Read(store, root.Root!, ro));
        }
        if (!opt.SkipCodex && !opt.SkipTools.Contains("codex"))
        {
            var root = SourceLocator.Codex(opt.CodexRoot, settings.CodexRoot);
            sum.Sources.Add(new CodexReader().Read(store, root.Root!, ro));
        }
        ObserveAccepted(db, opt, ro, sum);
        sum.ReadMs = sw.ElapsedMilliseconds;
        sum.Changed = sum.Sources.Any(x => x.FilesRead > 0 || x.NewUtterances > 0 || x.NewCompactions > 0);
        if (opt.SkipWhenIdle && !sum.Changed)
        {
            sum.Idle = true; sum.TotalMs = total.ElapsedMilliseconds; sum.FinishedAt = Time.NowIso();
            return sum;
        }
        try { sum.UsageIngested = Soz.Core.Usage.UsageIngest.Run(db, opt.SkipClaude ? null : SourceLocator.Claude(opt.ClaudeRoot, settings.ClaudeRoot).Root, opt.SkipCodex ? null : SourceLocator.Codex(opt.CodexRoot, settings.CodexRoot).Root, ro.SinceUtc); } // Harcama (B)
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException) { }
        sum.NewUtterances = sum.Sources.Sum(s => s.NewUtterances);
        sum.NewCompactions = sum.Sources.Sum(s => s.NewCompactions);

        using var c = db.Open();
        if (opt.SkipWhenIdle && sum.NewUtterances == 0 && sum.NewCompactions == 0)
        {
            // Yalnız oturum durumu (asistan satırları, araç çağrıları) değişti: oturum kayıtları okuyucuda güncellendi, ayıklama ve kümeleme yeni bir şey bulamaz.
            sum.TotalMs = total.ElapsedMilliseconds; sum.FinishedAt = Time.NowIso();
            return sum;
        }
        sw.Restart();
        var engine = new ProposalEngine(new RuleBasedExtractor(), settings.ExtractThreshold);
        var (ex, cands) = engine.ExtractPending(c);
        SecretFilter.PurgeExisting(c);
        sum.Extracted = ex; sum.NewCandidates = cands;
        sum.ExtractMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var cs = new RepeatClusterer(settings.RepeatThreshold, settings.ExtractThreshold - 0.15).Run(c);
        sum.NewClusters = cs.NewClusters; sum.NewRepeatClusters = cs.NewRepeatClusters; sum.ParentClusters = cs.ParentClusters;
        var ps = engine.Propose(c, ex, cands);
        sum.NewProposals = ps.NewProposals; sum.UpdatedProposals = ps.UpdatedProposals; sum.WithdrawnProposals = ps.Withdrawn;
        sum.ClusterMs = sw.ElapsedMilliseconds;

        sum.TotalMs = total.ElapsedMilliseconds;
        sum.FinishedAt = Time.NowIso();
        // Arka plan içe almada yalnız gerçekten yeni bir şey gelince olay yazılır (her 15 sn'de bir satır defteri doldurmasın).
        if (!opt.SkipWhenIdle || sum.NewUtterances > 0 || sum.NewProposals > 0)
            Events.Log(c, "import", null, JsonSerializer.Serialize(new
            {
                sum.NewUtterances, sum.NewCandidates, sum.NewProposals, sum.NewRepeatClusters, sum.TotalMs,
                unrecognized = sum.Sources.Sum(s => s.Unrecognized), badLines = sum.Sources.Sum(s => s.BadLines),
            }));
        SozSettings.SetInternal(c, "_lastImportAt", sum.FinishedAt);
        return sum;
    }
}

// Events sınıfı (Log, Append, ReadSince, VerifyChain) Data\Events.cs içine taşındı (şema v11, ekle-yalnız olay kaydı).
