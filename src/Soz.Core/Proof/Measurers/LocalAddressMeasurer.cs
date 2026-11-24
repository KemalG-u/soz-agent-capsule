using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using static Soz.Core.Proof.SpecJson;

namespace Soz.Core.Proof;

/// <summary>
/// Adres cevap veriyor (`local-address`). Eski `url` ölçütünün güvenli kardeşi: YALNIZ kendi bilgisayarındaki adrese (localhost, 127.x.x.x, ::1) bakar.
/// Başka bir sunucuya gidebilmesi için kişinin spec.allowedHost alanına o sunucunun adını bilerek yazması gerekir. Yönlendirmeler tek tek izlenir ve
/// her adımda aynı kural uygulanır (yerel adres sizi dışarıya yönlendiremez). Adreste kullanıcı adı/parola olamaz. Kendiliğinden dış ağa çıkılmaz.
/// Yanlış geçme yolu: sayfa 200 döndü ama içeriği yanlış olabilir (`contains` yazısı bunu daraltır).
/// </summary>
public sealed class LocalAddressMeasurer : MeasurerBase
{
    private const int MaxHops = 5;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public override string Kind => CheckKinds.LocalAddress;
    public override string Level => Confidence.B;

    private const string NotLocal = "Bu kontrol yalnız kendi bilgisayarındaki adreslere bakar (localhost, 127.0.0.1). Başka bir sunucu için spec.allowedHost alanına o sunucunun adını bilerek yaz.";

    internal static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>Adres kurala uyuyor mu? Uymuyorsa sade Türkçe neden.</summary>
    internal static string? CheckUrl(string? url, string? allowedHost, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return "Adres boş.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return "Adres http:// ya da https:// ile başlamalı: " + url;
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Adreste kullanıcı adı ya da parola olamaz.";
        var host = u.IdnHost;
        if (!IsLoopbackHost(host) && !(!string.IsNullOrWhiteSpace(allowedHost) && host.Equals(allowedHost.Trim(), StringComparison.OrdinalIgnoreCase)))
            return NotLocal + " (Adres: " + u.Host + ")";
        uri = u;
        return null;
    }

    public override string? Validate(JsonObject spec)
    {
        var bad = CheckUrl(S(spec, "url"), S(spec, "allowedHost"), out _);
        if (bad is not null) return bad;
        if (L(spec, "expectStatus") is { } st && (st < 100 || st > 599)) return "Beklenen durum kodu 100 ile 599 arasında olmalı.";
        return SchemaProblem(spec);
    }

    public override string Describe(JsonObject spec) => S(spec, "url") is { } u ? $" (adres: {u})" : "";

    public override CheckOutcome Measure(MeasureContext ctx, CheckToRun c, TimeSpan timeout)
    {
        var allowed = S(c.Spec, "allowedHost");
        var bad = CheckUrl(S(c.Spec, "url"), allowed, out var uri);
        if (bad is not null) return Error(c, bad);
        var expect = (int)(L(c.Spec, "expectStatus") ?? 200);
        var contains = S(c.Spec, "contains");
        var followRedirects = expect is < 300 or > 399;
        var chain = new List<string>();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var current = uri!;
            for (var hop = 0; ; hop++)
            {
                using var resp = Http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
                var code = (int)resp.StatusCode;
                if (followRedirects && code is >= 300 and <= 399 && resp.Headers.Location is { } loc)
                {
                    var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                    var why = CheckUrl(next.ToString(), allowed, out var nu);
                    if (why is not null)
                        return Failed(c, $"Adres başka bir yere yönlendiriyor ({next.Host}). Kendi bilgisayarının dışına gitmedim. " + (why.StartsWith("Bu kontrol") ? "" : why));
                    chain.Add($"{code} → {nu}");
                    if (hop >= MaxHops) return Failed(c, $"Adres çok fazla yönlendiriyor ({MaxHops} adım).");
                    current = nu!;
                    continue;
                }
                using var s = resp.Content.ReadAsStream(cts.Token);
                var buf = new byte[1024 * 1024];
                var total = 0;
                int n;
                while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
                var body = Encoding.UTF8.GetString(buf, 0, total);

                var reasons = new List<string>();
                if (code != expect) reasons.Add($"durum {code} (beklenen {expect})");
                if (!string.IsNullOrEmpty(contains) && !SafeFiles.Norm(body).Contains(SafeFiles.Norm(contains), StringComparison.Ordinal)) reasons.Add($"sayfada \"{contains}\" yok");
                var via = chain.Count == 0 ? "" : $" (yönlendirme: {string.Join(", ", chain)})";
                return reasons.Count == 0
                    ? Res(c, ResultStatus.Passed, $"{uri} → {code}{via}", exit: code)
                    : Res(c, ResultStatus.Failed, $"{uri} → {string.Join(", ", reasons)}{via}", exit: code);
            }
        }
        catch (OperationCanceledException) { return Failed(c, $"Zaman aşımı ({timeout.TotalSeconds:0} sn): adres cevap vermedi."); }
        catch (HttpRequestException e) { return Failed(c, "Bağlanılamadı: " + e.Message); }
    }

    public override (string label, JsonObject spec) FromTemplate(string templateId, IReadOnlyDictionary<string, string> values, string? root)
    {
        var u = TemplateValues.Get(values, "url");
        var bad = CheckUrl(u, null, out _);
        if (bad is not null) throw new SozException(400, bad);
        var text = TemplateValues.Get(values, "text");
        var spec = new JsonObject { ["url"] = u, ["expectStatus"] = 200 };
        if (text.Length > 0) spec["contains"] = text;
        spec["template"] = templateId;
        return (text.Length > 0 ? $"Kendi bilgisayarımdaki adres cevap versin ve \"{text}\" görünsün: {u}" : $"Kendi bilgisayarımdaki şu adres cevap versin: {u}", spec);
    }
}
