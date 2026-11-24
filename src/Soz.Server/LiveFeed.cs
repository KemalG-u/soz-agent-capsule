using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core;
using Soz.Core.Jobs;

namespace Soz.Server;

/// <summary>
/// Canlı olay akışı (SSE) ile olay kayıtları arasındaki köprü. İki kaynağı birleştirir:
/// (1) event_log: anlamlı canlı olaylar (job, proof, queue, ...) "legacy.&lt;tür&gt;" olarak durur; akış bunları seq ile yayınlar
///     ("id: &lt;seq&gt;"), kopan istemci "?since=&lt;seq&gt;" (ya da Last-Event-ID başlığı) ile kaçırdıklarını geri alır.
/// (2) events: nabız olayları (<see cref="Events.NoiseKinds"/> içinden canlı akışa girenler, ör. job_progress) event_log'a yazılmaz;
///     akış bunları eski tablodan yayınlar. Nabız olayı geçicidir: "id:" taşımaz ve "since" ile geri verilmez (kalıcı kopyası run_lines'tadır).
/// </summary>
public static class LiveFeed
{
    /// <summary>Akışa giren event_log türleri (nabız olmayan canlı türler).</summary>
    public static readonly string[] Kinds = Live.Kinds.Where(k => !Events.IsNoise(k)).Select(k => Events.LegacyPrefix + k).ToArray();

    /// <summary>Akışa giren nabız türleri (events tablosundaki adlarıyla).</summary>
    public static readonly string[] NoiseKinds = Live.Kinds.Where(Events.IsNoise).ToArray();

    /// <summary>Tek seferde geri verilen en çok olay; fazlası için istemci son aldığı seq ile yeniden ister.</summary>
    public const int ReplayCap = 5000;

    /// <summary>Bir yoklamada kaynak başına okunan en çok satır; doluysa sonraki yoklama kaldığı yerden sürer.</summary>
    public const int PollBatch = 500;

    /// <summary>Yoklama imleci: event_log için son yayınlanan seq, events için son işlenen kimlik.</summary>
    public sealed class Cursor
    {
        public long Seq { get; set; }
        public long LegacyId { get; set; }
    }

    /// <summary>İmleci şimdiki sona koyar (bundan önceki olaylar yayınlanmaz).</summary>
    public static Cursor Start(SqliteConnection c) => new() { Seq = Events.LastSeq(c), LegacyId = Events.LastLegacyId(c) };

    /// <summary>
    /// Yeni olayları okuyup <paramref name="publish"/> ile yayınlar (ad, yük, seq; nabız olayında seq null). İmleci ilerletir, yayınlanan sayıyı döndürür.
    /// Her kaynakta önce son sıra okunur, sonra yalnız o sıraya kadar olan satırlar alınır: okuma sırasında araya yazılan olay bu turda alınmaz,
    /// sonraki turda bir kez alınır (çift yayın yok). İki kaynak zaman damgasına göre birleştirilir (aynı damgada event_log önce).
    /// </summary>
    public static int Poll(SqliteConnection c, Cursor cur, Action<string, JsonNode, long?> publish)
    {
        var batch = new List<(string Ts, int Src, string Name, JsonNode Payload, long? Seq)>();

        var maxSeq = Events.LastSeq(c);
        if (maxSeq > cur.Seq)
        {
            var rows = Events.ReadSince(c, cur.Seq, PollBatch, Kinds, maxSeq);
            foreach (var x in rows)
                if (Parse(x) is { } ev) batch.Add((x.Ts, 0, ev.Name, ev.Payload, x.Seq));
            cur.Seq = rows.Count == PollBatch ? rows[^1].Seq : maxSeq;
        }

        if (NoiseKinds.Length > 0)
        {
            var maxId = Events.LastLegacyId(c);
            if (maxId > cur.LegacyId)
            {
                var rows = Events.ReadLegacySince(c, cur.LegacyId, maxId, NoiseKinds, PollBatch);
                foreach (var x in rows)
                    if (ParseDetail(x.Kind, x.Detail) is { } ev) batch.Add((x.Ts, 1, ev.Name, ev.Payload, null));
                cur.LegacyId = rows.Count == PollBatch ? rows[^1].Id : maxId;
            }
        }

        foreach (var b in batch.OrderBy(b => b.Ts, StringComparer.Ordinal).ThenBy(b => b.Src))
            publish(b.Name, b.Payload, b.Seq);
        return batch.Count;
    }

    /// <summary>event_log satırından SSE olayı. Yük okunamıyorsa null (yayınlanmaz).</summary>
    public static (string Name, JsonNode Payload)? Parse(EventRow row)
    {
        try
        {
            var kind = row.Kind.StartsWith(Events.LegacyPrefix, StringComparison.Ordinal) ? row.Kind[Events.LegacyPrefix.Length..] : row.Kind;
            return ParseDetail(kind, JsonNode.Parse(row.Payload)?["detail"]?.GetValue<string>());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Olay türü ve ayrıntı metninden (JSON) SSE olayı. Okunamıyorsa null.</summary>
    public static (string Name, JsonNode Payload)? ParseDetail(string kind, string? detail)
    {
        if (detail is null) return null;
        try
        {
            var payload = JsonNode.Parse(detail);
            return payload is null ? null : (Live.SseName(kind), payload);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>İstemcinin son gördüğü seq: ?since=N ya da Last-Event-ID başlığı. Yoksa ya da okunamazsa null (geri verme yok).</summary>
    public static long? SinceOf(HttpContext ctx)
    {
        var raw = ctx.Request.Query["since"].FirstOrDefault() ?? ctx.Request.Headers["Last-Event-ID"].FirstOrDefault();
        return long.TryParse(raw, out var n) ? Math.Max(0, n) : null;
    }
}
