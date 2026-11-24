using System.Text.Json.Nodes;
using Soz.Core.Registry;

namespace Soz.Core.Proof;

/// <summary>Ölçüt türü ölçücüsünün başlangıç ölçümündeki yeri (ajan başlamadan önce, bir kez).</summary>
public enum BaselineMode
{
    /// <summary>Başlangıç ölçümüne girmez (ekran görüntüsü, insan listesi, fark özeti).</summary>
    None,
    /// <summary>Ölçüt başlangıçta bir kez koşulur, sayıları "önceki durum" olur.</summary>
    Measure,
}

/// <summary>
/// Güven etiketi (Vizyon ilke 4): A Söz ölçtü, B dolaylı (vekil bir ölçü), C ajan beyanı (Söz doğrulamadı), D kanıt yok (yalnız insan kabulü ya da hiç ölçülmedi).
/// Etiket ölçüt türünün gücünden ve sonucun durumundan hesaplanır; veritabanında ayrı sütun yoktur.
/// </summary>
public static class Confidence
{
    public const string A = "A", B = "B", C = "C", D = "D";
    public const string SozMeasured = "soz_measured", HumanAccepted = "human_accepted", AgentClaim = "agent_claim", NoEvidence = "none";

    public static string Label(string level) => level switch
    {
        A => "ölçüldü",
        B => "dolaylı",
        C => "yardımcı beyanı",
        _ => "deneme sonucu yok",
    };

    /// <summary>Sonucun güveni: yalnız ölçülmüş sonuç (geçti ya da kaldı) ölçücünün düzeyini taşır; emin olunamayan, atlanan ve bekleyen sonuç D'dir.</summary>
    public static string Of(string kind, string status)
    {
        if (status is not (ResultStatus.Passed or ResultStatus.Failed)) return D;
        return MeasurerRegistry.Find(kind)?.Level ?? D;
    }

    /// <summary>Sonucun kaynağı: Söz ölçtü, insan kabul etti, ajan söyledi ya da kanıt yok. "Ölçülen gerçek" ile "ajanın özeti" bu alanla karışmaz.</summary>
    public static string SourceOf(string kind, string status)
    {
        var level = Of(kind, status);
        if (status == ResultStatus.Passed && MeasurerRegistry.Find(kind) is HumanChecklistMeasurer) return HumanAccepted;
        return level switch { A or B => SozMeasured, C => AgentClaim, _ => NoEvidence };
    }

    /// <summary>Koşunun tek sözü: geçen sonuçların EN ZAYIF düzeyi (geçen yoksa D).</summary>
    public static string Weakest(IEnumerable<(string kind, string status)> results)
    {
        var passed = results.Where(r => r.status == ResultStatus.Passed).Select(r => Of(r.kind, r.status)).ToList();
        return passed.Count == 0 ? D : passed.OrderByDescending(x => x, StringComparer.Ordinal).First();
    }
}

/// <summary>Ölçücünün çalıştığı yer: proje kökü, kanıt klasörü ve (varsa) iş ile veri klasörü.</summary>
public sealed class MeasureContext
{
    public required string Root { get; init; }
    public required string EvidenceDir { get; init; }
    public Func<string?> FindEdge { get; init; } = () => EdgeLocator.Find();
    public string? DataDir { get; init; }
    public long? JobId { get; init; }
    /// <summary>İnsan onay listesinde kişinin işaretlediği madde numaraları (ölçüt başına). Verilmezse hiçbir madde işaretli sayılmaz.</summary>
    public Func<CheckToRun, IReadOnlySet<int>>? HumanTicks { get; init; }

    /// <summary>Kök altındaki yol; kök dışına çıkan göreli yol reddedilir.</summary>
    public string UnderRoot(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return Root;
        var full = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(Root, rel));
        if (!SozService.IsUnder(full, Root) && !string.Equals(full.TrimEnd('\\'), Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kontrol klasörü proje kökünün dışında: " + rel);
        return full;
    }

    public string SaveEvidence(string name, string content)
    {
        Directory.CreateDirectory(EvidenceDir);
        File.WriteAllText(Path.Combine(EvidenceDir, name), content, new System.Text.UTF8Encoding(false));
        return name;
    }

}

/// <summary>
/// Bir ölçüt türünün ölçücüsü. Tür adı kayıt defterindeki `check-kind:&lt;ad&gt;` kaydının `measure.name` değeridir.
/// Yeni tür: bir kayıt (olcutler.soz.json) + bir ölçücü sınıfı + <see cref="MeasurerRegistry"/> satırı.
/// Kural: emin olunamayan her durum "error"; ölçülecek bir şey bulunamayan durum "unchecked"; ölçücü asla tahminle "passed" vermez.
/// </summary>
public interface IMeasurer
{
    string Kind { get; }
    /// <summary>Geçti ya da kaldı sonucunun güven düzeyi (A ölçüldü, B dolaylı, C ajan beyanı, D kanıt yok).</summary>
    string Level { get; }
    BaselineMode Baseline { get; }
    /// <summary>Ölçüt tanımı (spec) geçersizse sade Türkçe neden, geçerliyse null.</summary>
    string? Validate(JsonObject spec);
    /// <summary>Ajan istemine eklenen kısa ayrıntı (" (dosya: x)"); yoksa boş metin. Sır süzgeci çağıranda.</summary>
    string Describe(JsonObject spec);
    CheckOutcome Measure(MeasureContext ctx, CheckToRun check, TimeSpan timeout);
    /// <summary>Düz cümle şablonundan etiket ve tanım. Şablonlar kayıt defterindedir; bu yalnız değerleri tanıma çevirir.</summary>
    (string label, JsonObject spec) FromTemplate(string templateId, IReadOnlyDictionary<string, string> values, string? root);
}

/// <summary>Ortak yardımcılar ve varsayılanlar.</summary>
public abstract class MeasurerBase : IMeasurer
{
    public abstract string Kind { get; }
    public abstract string Level { get; }
    public virtual BaselineMode Baseline => BaselineMode.Measure;
    public abstract string? Validate(JsonObject spec);
    public virtual string Describe(JsonObject spec) => "";
    public abstract CheckOutcome Measure(MeasureContext ctx, CheckToRun check, TimeSpan timeout);
    public virtual (string label, JsonObject spec) FromTemplate(string templateId, IReadOnlyDictionary<string, string> values, string? root)
        => throw new SozException(400, "Bu kontrol türünün düz cümle şablonu yok: " + Kind);

    /// <summary>Kayıt defterindeki specSchema'ya göre ilk uyumsuzluk (varsa).</summary>
    protected string? SchemaProblem(JsonObject spec)
    {
        var e = FixedCatalog.Get.Get("check-kind:" + Kind);
        if (e?.Body["specSchema"] is not JsonObject schema) return null;
        var errs = new SchemaCheck(schema).Validate(spec);
        return errs.Count == 0 ? null : "Kontrol tanımı uygun değil: " + errs[0];
    }

    protected static CheckOutcome Res(CheckToRun c, string status, string text, string? artifact = null, int? exit = null, string? note = null, string? reasonCode = null)
        => new(c.Id, c.Label, status, exit, ProofRunner.Tail(text), artifact, 0, 0, note, null, null, false, null, reasonCode);

    protected static CheckOutcome Passed(CheckToRun c, string text) => Res(c, ResultStatus.Passed, text);
    protected static CheckOutcome Failed(CheckToRun c, string text) => Res(c, ResultStatus.Failed, text);
    protected static CheckOutcome Error(CheckToRun c, string text) => Res(c, ResultStatus.Error, text);
    /// <summary>Ölçülecek bir şey bulunamadı: "geçti" değil, "doğrulanamadı".</summary>
    protected static CheckOutcome NothingMeasured(CheckToRun c, string text)
        => Res(c, ResultStatus.Unchecked, text + " Bu bir \"geçti\" değildir.", note: text, reasonCode: ProofStore.Reasons.NothingMeasured);
}

/// <summary>Ölçüt tanımından (spec) güvenli okuma.</summary>
internal static class SpecJson
{
    public static string? S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static long? L(JsonObject o, string k) => o[k] is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : null) : null;
    public static bool? B(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public static decimal? M(JsonObject o, string k) => o[k] is JsonValue v ? (v.TryGetValue<decimal>(out var m) ? m : v.TryGetValue<double>(out var d) ? (decimal)d : null) : null;

    /// <summary>Dizi (["a","b"]) ya da satır/virgül/noktalı virgülle ayrılmış metin ("a, b"): boşlar atılır.</summary>
    public static List<string> List(JsonObject o, string k)
    {
        var res = new List<string>();
        if (o[k] is JsonArray a) foreach (var n in a) { if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) res.Add(s.Trim()); }
        else if (S(o, k) is { } text) res.AddRange(SplitList(text));
        return res;
    }

    public static IEnumerable<string> SplitList(string text)
        => text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Length > 0);
}

/// <summary>Tür adı → ölçücü kaydı. Bilinmeyen tür ASLA "passed" vermez (ProofRunner "error" yazar).</summary>
public static class MeasurerRegistry
{
    private static readonly object Gate = new();
    private static List<IMeasurer> _list = new()
    {
        new CommandMeasurer(), new FileMeasurer(), new UrlMeasurer(), new ScreenshotMeasurer(),
        new FileRangeMeasurer(), new DocHeadingMeasurer(), new DiffSummaryMeasurer(), new LocalAddressMeasurer(), new TableMeasurer(), new HumanChecklistMeasurer(),
    };

    public static IReadOnlyList<IMeasurer> All { get { lock (Gate) return _list; } }
    public static IReadOnlyList<string> Kinds => All.Select(m => m.Kind).ToList();

    public static IMeasurer? Find(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return null;
        lock (Gate) return _list.FirstOrDefault(m => string.Equals(m.Kind, kind, StringComparison.Ordinal));
    }

    /// <summary>Yeni ölçücü ekler (aynı ad ikinci kez eklenemez). Üründe ölçücüler yukarıdaki listedir; bu yol yeni türün tek dosyayla eklenebildiğini sınamak içindir.</summary>
    internal static void Register(IMeasurer m)
    {
        lock (Gate)
        {
            if (_list.Any(x => x.Kind == m.Kind)) throw new InvalidOperationException("Bu kontrol türü zaten kayıtlı: " + m.Kind);
            _list = new List<IMeasurer>(_list) { m };
        }
    }

    internal static void Unregister(string kind)
    {
        lock (Gate) _list = _list.Where(x => x.Kind != kind).ToList();
    }
}
