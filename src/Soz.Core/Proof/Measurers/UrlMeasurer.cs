using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>Adres: http/https adresine istek atar, durum koduna ve (varsa) sayfadaki yazıya bakar. Eski ProofRunner.RunUrl, davranışı aynen (yalnız kendi bilgisayarına bakan sıkı ölçücü: local-address).</summary>
public sealed class UrlMeasurer : MeasurerBase
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public override string Kind => CheckKinds.Url;
    public override string Level => Confidence.B;

    public override string? Validate(JsonObject spec) => string.IsNullOrWhiteSpace(S(spec, "url")) ? "url kontrolü için spec.url gerekli." : null;

    public override string Describe(JsonObject spec) => S(spec, "url") is { } u ? $" (adres: {u})" : "";

    /// <summary>İstek atar; en çok 1 MiB gövde okur. (durum kodu, gövde, hata metni).</summary>
    internal static (int? status, string body, string? error) Get(string url, TimeSpan timeout)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return (null, "", "Adres http/https değil: " + url);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var resp = Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
            using var s = resp.Content.ReadAsStream(cts.Token);
            var buf = new byte[1024 * 1024];
            var total = 0;
            int n;
            while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
            return ((int)resp.StatusCode, Encoding.UTF8.GetString(buf, 0, total), null);
        }
        catch (OperationCanceledException) { return (null, "", $"Zaman aşımı ({timeout.TotalSeconds:0} sn)."); }
        catch (HttpRequestException e) { return (null, "", "Bağlanılamadı: " + e.Message); }
    }

    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var url = S(c.Spec, "url");
        if (string.IsNullOrWhiteSpace(url)) return new CheckOutcome(c.Id, c.Label, ResultStatus.Error, null, "Adres boş.", null, 0);
        var expect = (int)(L(c.Spec, "expectStatus") ?? 200);
        var (status, body, err) = Get(url, timeout);
        if (status is null) return new CheckOutcome(c.Id, c.Label, ResultStatus.Failed, null, ProofRunner.Tail(err), null, 0);
        var reasons = new List<string>();
        if (status != expect) reasons.Add($"durum {status} (beklenen {expect})");
        if (S(c.Spec, "contains") is { Length: > 0 } contains && !body.Contains(contains, StringComparison.OrdinalIgnoreCase)) reasons.Add($"sayfada \"{contains}\" yok");
        return new CheckOutcome(c.Id, c.Label, reasons.Count == 0 ? ResultStatus.Passed : ResultStatus.Failed, status,
            ProofRunner.Tail(reasons.Count == 0 ? $"{url} → {status}" : $"{url} → " + string.Join(", ", reasons)), null, 0);
    }
}
