using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;

namespace Soz.Core.Usage;

public sealed record UsageTokens(long Messages, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public long Total => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
    /// <summary>Önbellekten okunan hariç (yeni işlenen) token: giriş + çıkış + önbellek yazma.</summary>
    public long Fresh => InputTokens + OutputTokens + CacheWriteTokens;
}

public sealed record UsageRow(string Day, string Tool, string Model, bool IsSubagent, UsageTokens Tokens, double? CostUsd);

/// <summary>settings.prices = {"model-adı-ya-da-parçası": {"input":$/1M, "output":.., "cacheRead":.., "cacheWrite":..}}. Varsayılan boş: fiyat UYDURULMAZ.</summary>
public static class UsagePricing
{
    public sealed record Price(double Input, double Output, double CacheRead, double CacheWrite);

    public static string? Validate(JsonNode? v)
    {
        if (v is null) return null;
        if (v is not JsonObject o) return "prices: {model: {input, output, cacheRead, cacheWrite}} nesnesi olmalı (1 milyon token başına USD).";
        foreach (var (k, p) in o)
        {
            if (p is not JsonObject po) return $"prices.{k}: nesne olmalı.";
            foreach (var (f, n) in po)
            {
                if (f is not ("input" or "output" or "cacheRead" or "cacheWrite")) return $"prices.{k}.{f}: bilinmeyen alan (input, output, cacheRead, cacheWrite).";
                if (n is null || n.GetValueKind() != JsonValueKind.Number || double.Parse(n.ToJsonString(), CultureInfo.InvariantCulture) < 0) return $"prices.{k}.{f}: sıfır ya da pozitif sayı olmalı.";
            }
        }
        return null;
    }

    public static Dictionary<string, Price> Parse(JsonNode? v)
    {
        var res = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase);
        if (v is not JsonObject o) return res;
        double D(JsonObject po, string f) => po[f] is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number ? double.Parse(jv.ToJsonString(), CultureInfo.InvariantCulture) : 0;
        foreach (var (k, p) in o)
            if (p is JsonObject po) res[k] = new Price(D(po, "input"), D(po, "output"), D(po, "cacheRead"), D(po, "cacheWrite"));
        return res;
    }

    public static Price? Find(Dictionary<string, Price> prices, string model)
    {
        if (prices.TryGetValue(model, out var p)) return p;
        return prices.Where(kv => model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
    }

    public static double Cost(Price p, UsageTokens t)
        => (t.InputTokens * p.Input + t.OutputTokens * p.Output + t.CacheReadTokens * p.CacheRead + t.CacheWriteTokens * p.CacheWrite) / 1_000_000.0;
}

public static class UsageQuery
{
    public const string Disclaimer = "Bu tutar, kayıtlardaki token sayılarının API fiyatıyla çarpımıdır (tahmini, API eşdeğeri). Abonelikte gerçek fatura DEĞİLDİR.";

    private static double Pct(long part, long whole) => whole <= 0 ? 0 : Math.Round(part * 100.0 / whole, 1);

    /// <summary>
    /// Kayıtları okunan ama token sayıları harcamaya alınmayan açık araçlar (dosya tabanlı okuyucusu olmayan gözle araçları: Hermes, OpenCode). Kapalı araç listeye girmez.
    /// </summary>
    public static List<object> NotTracked(SozDb db)
    {
        var list = new List<object>();
        foreach (var spec in Soz.Core.Agents.AgentRegistry.SpecsFrom(Soz.Core.Registry.RegistryHost.Load(db)).Where(x => x.CanObserve))
            if (Soz.Core.Readers.ReaderCatalog.Find(spec.Reader)?.IsFileBased != true)
                list.Add(new { tool = spec.Id, title = spec.Title, reason = spec.Title + " için harcama toplamı yok: kayıtları okunuyor ama token sayıları henüz harcamaya alınmıyor." });
        return list;
    }

    public static object Build(SozDb db, int days, string? tool = null)
    {
        days = Math.Clamp(days, 1, 3650);
        var now = Time.UtcNow();
        var since = now.Date.AddDays(-(days - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var until = now.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var c = db.Open();
        UsageIngest.EnsureTables(c);
        var pricesNode = SozSettings.Load(c).ToJson()["prices"];
        var prices = UsagePricing.Parse(pricesNode);
        var configured = prices.Count > 0;

        var rows = c.Query("""
            SELECT day, tool, model, is_subagent, messages, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens
            FROM usage_daily WHERE day >= @s AND (@t IS NULL OR tool=@t) ORDER BY day, tool, model, is_subagent
            """,
            r => (day: r.Str(0)!, tool: r.Str(1) ?? "", model: r.Str(2) ?? "?", sub: r.Long(3) != 0,
                  t: new UsageTokens(r.Long(4), r.Long(5), r.Long(6), r.Long(7), r.Long(8))), null, ("@s", since), ("@t", string.IsNullOrWhiteSpace(tool) ? null : tool));

        double? CostOf(string model, UsageTokens t) => UsagePricing.Find(prices, model) is { } p ? UsagePricing.Cost(p, t) : null;
        UsageTokens Sum(IEnumerable<UsageTokens> xs) => xs.Aggregate(new UsageTokens(0, 0, 0, 0, 0), (a, b) => new UsageTokens(a.Messages + b.Messages, a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.CacheReadTokens + b.CacheReadTokens, a.CacheWriteTokens + b.CacheWriteTokens));
        object Tok(UsageTokens t) => new { messages = t.Messages, inputTokens = t.InputTokens, outputTokens = t.OutputTokens, cacheReadTokens = t.CacheReadTokens, cacheWriteTokens = t.CacheWriteTokens, totalTokens = t.Total, freshTokens = t.Fresh };

        var all = Sum(rows.Select(r => r.t));
        var sub = Sum(rows.Where(r => r.sub).Select(r => r.t));
        var priced = rows.Where(r => CostOf(r.model, r.t) is not null).ToList();
        double? totalCost = configured && priced.Count > 0 ? priced.Sum(r => CostOf(r.model, r.t)!.Value) : null;
        var unpricedModels = rows.Where(r => CostOf(r.model, r.t) is null).Select(r => r.model).Distinct().OrderBy(x => x).ToList();

        var byDay = rows.GroupBy(r => r.day).Select(g =>
        {
            var t = Sum(g.Select(r => r.t));
            var st = Sum(g.Where(r => r.sub).Select(r => r.t));
            var cost = g.Select(r => CostOf(r.model, r.t)).Where(x => x is not null).Select(x => x!.Value).ToList();
            return new { day = g.Key, tokens = Tok(t), subagentTokens = Tok(st), costUsd = cost.Count > 0 ? (double?)Math.Round(cost.Sum(), 4) : null };
        }).ToList();

        var byModel = rows.GroupBy(r => (r.tool, r.model)).Select(g =>
        {
            var t = Sum(g.Select(r => r.t));
            var main = Sum(g.Where(r => !r.sub).Select(r => r.t));
            var st = Sum(g.Where(r => r.sub).Select(r => r.t));
            return new
            {
                tool = g.Key.tool, model = g.Key.model, tokens = Tok(t), main = Tok(main), subagent = Tok(st),
                sharePercent = Pct(t.Fresh, all.Fresh), subagentSharePercent = Pct(st.Fresh, t.Fresh),
                costUsd = CostOf(g.Key.model, t) is { } cc ? (double?)Math.Round(cc, 4) : null, priced = CostOf(g.Key.model, t) is not null,
            };
        }).OrderByDescending(x => x.sharePercent).ToList();

        return new
        {
            days, since, until, tool = string.IsNullOrWhiteSpace(tool) ? null : tool,
            dayBasis = "UTC",
            totals = Tok(all),
            subagent = new
            {
                tokens = Tok(sub),
                sharePercent = Pct(sub.Fresh, all.Fresh),
                sharePercentOfAllTokens = Pct(sub.Total, all.Total),
                sharePercentOfMessages = Pct(sub.Messages, all.Messages),
                basis = "sharePercent = yeni işlenen token (giriş + çıkış + önbellek yazma) içindeki pay. Önbellekten okunanlar hariç",
            },
            estimatedCostUsd = totalCost is { } tc ? (double?)Math.Round(tc, 4) : null,
            pricing = new
            {
                configured,
                note = configured ? (unpricedModels.Count > 0 ? "Fiyatı girilmeyen modeller toplama katılmadı: " + string.Join(", ", unpricedModels) : "Tahmini (API eşdeğeri).") : "fiyat girilmedi",
                unpricedModels,
                disclaimer = Disclaimer,
                howTo = "Ayarlar'da prices: {\"model-adı\": {\"input\": USD/1M, \"output\": …, \"cacheRead\": …, \"cacheWrite\": …}}",
            },
            // Açık gözle araçlarından harcama toplamı tutulmayanlar: sessizce atlanmaz, "yok" diye söylenir (arayüz ve komut gösterir).
            notTracked = NotTracked(db),
            byDay,
            byModel,
            rows = rows.Select(r => new { day = r.day, tool = r.tool, model = r.model, isSubagent = r.sub, tokens = Tok(r.t), costUsd = CostOf(r.model, r.t) is { } cc ? (double?)Math.Round(cc, 4) : null }).ToList(),
        };
    }
}
