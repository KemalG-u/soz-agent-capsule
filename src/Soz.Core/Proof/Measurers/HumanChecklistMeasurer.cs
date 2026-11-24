using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>
/// İnsan onay listesi (`human-checklist`): kişinin kendisinin işaretleyeceği maddeler. Bu bir ÖLÇÜM DEĞİL, KABULDÜR: güven etiketi D, kaynak "human_accepted"
/// ("Sen onayladın" etiketi). Hiçbir madde işaretli değilken sonuç "senin onayını bekliyor" olur (asla "geçti"); hepsi işaretlenince "geçti, sen onayladın".
/// İşareti yalnız insan koyar: uç POST olduğu için CallerGate ajan çağrısını reddeder (kabul yalnız insandan, Vizyon ilke 2).
/// İşaretler olay defterine (event_log: check.human_ticked) yazılır, ayrı tablo yoktur; işaret İŞE bağlıdır (proje ölçütü her işte yeniden onay ister)
/// ve ajan işe yeniden başlayınca (son koşunun başlangıcından önceki işaretler) düşer: değişen işi eski onay kapsamaz.
/// </summary>
public sealed class HumanChecklistMeasurer : MeasurerBase
{
    public const int MaxItems = 20, MaxItemLength = 200;
    public const string TickedKind = "check.human_ticked";

    public override string Kind => CheckKinds.HumanChecklist;
    public override string Level => Confidence.D;
    public override BaselineMode Baseline => BaselineMode.None;

    public static List<string> Items(JsonObject spec) => List(spec, "items");

    public override string? Validate(JsonObject spec)
    {
        var items = Items(spec);
        if (items.Count == 0) return "Onay listesinde en az bir madde olmalı (spec.items).";
        if (items.Count > MaxItems) return $"Onay listesinde en çok {MaxItems} madde olabilir.";
        if (items.Any(i => i.Length > MaxItemLength)) return $"Her madde en çok {MaxItemLength} karakter olabilir.";
        return SchemaProblem(spec);
    }

    public override string Describe(JsonObject spec) => " (sen işaretleyeceksin: " + Items(spec).Count + " madde)";

    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var items = Items(c.Spec);
        if (items.Count == 0) return Error(c, "Onay listesi boş.");
        var ticks = ctx.HumanTicks?.Invoke(c) ?? new HashSet<int>();
        var missing = Enumerable.Range(0, items.Count).Where(i => !ticks.Contains(i)).ToList();
        if (missing.Count == 0)
            return Res(c, ResultStatus.Passed, $"Sen onayladın: {items.Count} maddenin hepsi işaretli.");
        var list = string.Join(", ", missing.Take(5).Select(i => $"\"{(items[i].Length > 60 ? items[i][..60] + "…" : items[i])}\""));
        return Res(c, ResultStatus.AwaitingHuman,
            $"{items.Count} maddeden {missing.Count} tanesi senin onayını bekliyor: {list}{(missing.Count > 5 ? " ..." : "")}. Bu bir \"geçti\" değildir.",
            note: $"{missing.Count} madde onay bekliyor", reasonCode: ProofStore.Reasons.AwaitingHuman);
    }

    public override (string label, JsonObject spec) FromTemplate(string templateId, IReadOnlyDictionary<string, string> values, string? root)
    {
        var items = SplitList(TemplateValues.Get(values, "items").Replace(",", "\n").Replace(";", "\n")).ToList();
        if (items.Count == 0) throw new SozException(400, "Onay listesinde en az bir madde olmalı.");
        if (items.Count > MaxItems) throw new SozException(400, $"Onay listesinde en çok {MaxItems} madde olabilir.");
        if (items.Any(i => i.Length > MaxItemLength)) throw new SozException(400, $"Her madde en çok {MaxItemLength} karakter olabilir.");
        var label = items.Count == 1 ? $"Sen bakacaksın: {items[0]}" : $"Sen bakacaksın ({items.Count} madde)";
        return (label, new JsonObject { ["items"] = new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()), ["template"] = templateId });
    }

    private static IEnumerable<string> SplitList(string text)
        => text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Length > 0);
}

/// <summary>İnsan onay listesi işaretleri (olay defterinden okunur, olay defterine yazılır).</summary>
public static class HumanChecklistStore
{
    public sealed record ItemState(int Index, string Text, bool Ticked);
    public sealed record ChecklistState(long CheckId, string Label, long? JobId, IReadOnlyList<ItemState> Items, int Waiting, bool AllTicked);

    private static string? JobUid(SqliteConnection c, long? jobId) => jobId is null ? null : c.Scalar("SELECT uid FROM jobs WHERE id=@j", null, ("@j", jobId)) as string;

    /// <summary>İşin son koşusunun başlangıcı: bundan önce konan işaretler düşer. Koşu yoksa null.</summary>
    private static string? LastRunStart(SqliteConnection c, long? jobId)
        => jobId is null ? null : c.Scalar("SELECT MAX(started) FROM runs WHERE job_id=@j", null, ("@j", jobId)) as string;

    /// <summary>Bu ölçütte (bu iş için) işaretli madde numaraları: her madde için son işaret kazanır.</summary>
    public static HashSet<int> Ticked(SqliteConnection c, CheckDto check, long? jobId)
    {
        var res = new HashSet<int>();
        if (check.Uid is null) return res;
        var jobUid = JobUid(c, jobId);
        var since = LastRunStart(c, jobId);
        var rows = c.Query("SELECT ts, payload FROM event_log WHERE kind=@k AND entity_uid=@e AND job_uid IS @j ORDER BY seq",
            r => (ts: r.GetString(0), payload: r.GetString(1)), null, ("@k", HumanChecklistMeasurer.TickedKind), ("@e", check.Uid), ("@j", jobUid));
        var state = new Dictionary<int, bool>();
        foreach (var (ts, payload) in rows)
        {
            if (since is not null && string.CompareOrdinal(ts, since) < 0) { state.Clear(); continue; } // koşudan önceki işaretler düşer
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("item", out var i) && i.TryGetInt32(out var idx) && doc.RootElement.TryGetProperty("ticked", out var t))
                    state[idx] = t.ValueKind == JsonValueKind.True;
            }
            catch (JsonException) { /* bozuk kayıt işaret sayılmaz */ }
        }
        foreach (var (idx, on) in state) if (on) res.Add(idx);
        return res;
    }

    public static ChecklistState Get(SqliteConnection c, long checkId, long? jobId)
    {
        var check = CheckStore.Get(c, checkId) ?? throw new SozException(404, $"Kontrol bulunamadı: {checkId}");
        if (check.Kind != CheckKinds.HumanChecklist) throw new SozException(400, "Bu kontrol bir onay listesi değil.");
        var items = HumanChecklistMeasurer.Items(check.Spec);
        var ticks = Ticked(c, check, jobId);
        var states = items.Select((t, i) => new ItemState(i, t, ticks.Contains(i))).ToList();
        var waiting = states.Count(x => !x.Ticked);
        return new ChecklistState(check.Id, check.Label, jobId, states, waiting, waiting == 0 && states.Count > 0);
    }

    /// <summary>Maddeyi işaretler ya da işareti kaldırır. Çağıran uç yalnız insan çağrısına açıktır (POST, CallerGate).</summary>
    public static ChecklistState Tick(SqliteConnection c, long checkId, long? jobId, int item, bool ticked, string actor = "human")
    {
        var check = CheckStore.Get(c, checkId) ?? throw new SozException(404, $"Kontrol bulunamadı: {checkId}");
        if (check.Kind != CheckKinds.HumanChecklist) throw new SozException(400, "Bu kontrol bir onay listesi değil.");
        var items = HumanChecklistMeasurer.Items(check.Spec);
        if (item < 0 || item >= items.Count) throw new SozException(400, $"Madde numarası 0 ile {items.Count - 1} arasında olmalı.");
        if (jobId is not null)
        {
            var ok = c.Scalar("SELECT id FROM jobs WHERE id=@j AND project_id=@p", null, ("@j", jobId), ("@p", check.ProjectId));
            if (ok is null) throw new SozException(404, "İş bu kontrolün projesinde bulunamadı.");
            if (check.JobId is not null && check.JobId != jobId) throw new SozException(400, "Bu kontrol başka bir işe ait.");
        }
        else if (check.JobId is not null) jobId = check.JobId;
        var projectUid = c.Scalar("SELECT uid FROM projects WHERE id=@p", null, ("@p", check.ProjectId)) as string;
        var payload = JsonSerializer.Serialize(new { item, ticked, text = items[item] });
        Events.Append(c, HumanChecklistMeasurer.TickedKind, "check", check.Uid, projectUid, JobUid(c, jobId), actor, payload);
        return Get(c, checkId, jobId);
    }
}
