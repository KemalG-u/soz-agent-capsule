using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Proof;

/// <summary>Klasörde kalıcı bir ölçüt (kartı atlayan tek satır özet için).</summary>
public sealed record DoneSummaryCheck(long Id, string Label, string Kind, string Level, string LevelLabel, string Who);

/// <summary>Ajanın yazdığı denemeden Söz'ün önerdiği, kişinin henüz kabul etmediği ölçüt.</summary>
public sealed record DoneSummaryAgentCheck(long CheckId, string Label, string? Cmd, long? JobId, string Level, string LevelLabel);

/// <summary>
/// "Bitti sayılması için" kartı bu işte tam çıkmalı mı (k1)? İş başlatılmadan önce tek çağrı. Tam kart gerekir: klasörde daha önce hiç iş verilmediyse
/// (FirstJob) ya da kalıcı ölçüt yoksa; aksi halde arayüz kartı atlar ve <see cref="Line"/> tek satırını gösterir.
/// Reason: first_job | no_checks | has_checks. Hiçbir komut çalıştırılmaz, yalnız veritabanı okunur.
/// </summary>
public sealed record DoneSummaryDto(long ProjectId, long? JobId, int JobsGiven, bool FirstJob, int PermanentCount, int MeasurableCount, IReadOnlyList<DoneSummaryCheck> Permanent,
    bool NeedsFullCard, string Reason, string Line, IReadOnlyList<DoneSummaryAgentCheck> AgentSuggested);

public static class DoneSummary
{
    public static DoneSummaryDto Build(SqliteConnection c, long projectId, long? jobId = null)
    {
        // Kalıcı ölçüt = projeye bağlı (iş başına olmayan) ve açık olan. Kapalı öneri ölçüt sayılmaz (kişi açmadan hiçbir ölçüt çalışmaz).
        var permanent = CheckStore.List(c, projectId).Where(x => x.JobId is null && x.Enabled)
            .Select(x =>
            {
                var level = MeasurerRegistry.Find(x.Kind)?.Level ?? Confidence.D;
                return new DoneSummaryCheck(x.Id, x.Label, x.Kind, level, Confidence.Label(level), level == Confidence.D ? DoneDefinition.HumanWho : DoneDefinition.SozWho);
            }).ToList();
        // "İlk iş": bu klasörde ajana verilmiş (ya da çalışmış) iş yok. Henüz başlamamış taslaklar (bu işin kendisi dahil) sayılmaz.
        var given = (int)c.ScalarLong("SELECT COUNT(*) FROM jobs WHERE project_id=@p AND given_at IS NOT NULL AND (@j IS NULL OR id<>@j)", null, ("@p", projectId), ("@j", jobId));
        var first = given == 0;
        var measurable = permanent.Count(x => x.Level != Confidence.D);
        var needs = first || permanent.Count == 0;
        var reason = first ? "first_job" : permanent.Count == 0 ? "no_checks" : "has_checks";
        string line;
        // Ekranda "ölçüt" sözcüğü yok: arayüzün ortak karşılığı "kontrol" (yardımcı "bitti" deyince Söz'ün kendisinin denediği şey).
        if (first) line = "Bu klasörde ilk işin: yardımcı \"bitti\" derse neye bakacağımızı birlikte seçelim.";
        else if (permanent.Count == 0) line = "Bu klasörde henüz bir bitti kontrolü yok: neye bakacağımızı seçelim.";
        else
        {
            var names = permanent.Take(3).Select(x => x.Label).ToList();
            var more = permanent.Count - names.Count;
            var what = string.Join(", ", names) + (more > 0 ? $" ve {more} tane daha" : "");
            line = measurable == 0
                ? $"Bu klasörde bitti kontrolü yalnız senin onayın: {what}."
                : $"Bu klasörde {permanent.Count} kontrol var, iş bitince bunlara bakacağım: {what}.";
        }
        var suggested = c.Query("SELECT id, label, spec_json FROM checks WHERE project_id=@p AND origin=@o AND enabled=0 ORDER BY id",
            r =>
            {
                var spec = CheckStore.ParseSpec(r.Str(2));
                var cmd = spec["cmd"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                long? by = spec["suggestedByJob"] is JsonValue jv && jv.TryGetValue<long>(out var l) ? l : null;
                return new DoneSummaryAgentCheck(r.GetInt64(0), r.Str(1) ?? "", cmd, by, Confidence.C, Confidence.Label(Confidence.C));
            }, null, ("@p", projectId), ("@o", CheckStore.AgentTestOrigin));
        return new DoneSummaryDto(projectId, jobId, given, first, permanent.Count, measurable, permanent, needs, reason, line, suggested);
    }
}
