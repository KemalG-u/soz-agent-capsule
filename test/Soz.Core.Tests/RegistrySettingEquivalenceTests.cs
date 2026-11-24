using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Registry;
using Xunit;
using static Soz.Core.Tests.RegistryTestKit;

namespace Soz.Core.Tests;

/// <summary>
/// Eşdeğerlik (d1c): ayar doğrulaması artık kayıt defterinden geliyor (<see cref="SozSettings.Validate"/> → <see cref="SettingValidator"/>), "eski Validate" kodda kalmadı.
/// Bu yüzden değişiklikten ÖNCEKİ davranış dondurulmuş bir veri dosyasında durur: <c>Data\ayar-eski-davranis.json</c> (her ayar için anahtar, değer, eski kabul/ret ve eski mesaj;
/// 33 ayarın hepsinde ilk iş olan 286 örnek + rastgele havuz, her ayarda yüzlerce değer). Yeni kod bu dosyayla sınanır.
/// İzinli farklar yalnız yönetici kararındaki ayarlardır (<see cref="Allowed"/>): ilk dokuz (d1c) ve d1f'te tam sayı olması gereken dört sayaç ayar (gateBudgetSeconds, gateMaxAttempts, packetBudget, runnerMaxMinutes: kesirli değer artık reddedilir);
/// her biri adıyla ve kendi bağımsız kuralıyla; ötekilerde sonuç birebir aynı olmalı.
/// </summary>
public class RegistrySettingEquivalenceTests
{
    private static readonly string RealFile = typeof(RegistrySettingEquivalenceTests).Assembly.Location;

    private sealed record Frozen(string Key, string ValueJson, bool OldAccepted, string? OldMessage);

    private static readonly Lazy<IReadOnlyList<Frozen>> Records = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "ayar-eski-davranis.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var list = new List<Frozen>();
        foreach (var (key, arr) in root)
            foreach (var rec in arr!.AsArray())
                list.Add(new Frozen(key, rec![0]!.GetValue<string>(), rec[1]!.GetValue<string>() == "kabul", rec[2]?.GetValue<string>()?.Replace("@REAL@", RealFile)));
        return list;
    });

    private static JsonNode? Parse(string valueJson) => JsonNode.Parse(valueJson.Replace("@REAL@", RealFile.Replace("\\", "\\\\")));

    private static double? Num(JsonNode? n)
        => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) : null;

    private static bool IsStr(JsonNode? n, int max) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length <= max;
    private static bool IsInt(JsonNode? n, double lo, double hi) => Num(n) is { } d && !double.IsInfinity(d) && d == Math.Floor(d) && d >= lo && d <= hi;

    /// <summary>
    /// Yönetici kararı farkları (madde 9). Her ayar için YENİ kuralın bağımsız ifadesi: değer bu kurala uymuyorsa yeni kod reddetmeli
    /// (eski kod kabul etmiş olsa bile). Bu ayarlarda yeni sonuç = eski ret VEYA kural ihlali.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<JsonNode?, bool>> Allowed = new Dictionary<string, Func<JsonNode?, bool>>
    {
        ["gateBudgetSeconds"] = v => IsInt(v, 10, 3600),                                           // eski 1..3600, kesir kabul (d1f: tam sayı)
        ["gateMaxAttempts"] = v => IsInt(v, 1, 20),                                                // eski 1..3600, kesir kabul (d1f: tam sayı)
        ["packetBudget"] = v => IsInt(v, 1, double.MaxValue),                                      // d1f: adet, tam sayı (eski: her pozitif sayı)
        ["runnerMaxMinutes"] = v => IsInt(v, 1, 1440),                                             // d1f: dakika adedi, tam sayı (eski: 1..1440 kesirli)
        ["claudeRoot"] = v => v is null || IsStr(v, 1024),                                         // eski: her şey
        ["codexRoot"] = v => v is null || IsStr(v, 1024),
        ["dictationPath"] = v => v is null || IsStr(v, 1024),
        ["explainCommand"] = v => IsStr(v, 2000),                                                  // eski: her şey
        ["judgeCommand"] = v => IsStr(v, 2000),
        ["capsuleX"] = v => v is null || IsInt(v, -100000, 100000),                                // eski: her şey
        ["capsuleY"] = v => v is null || IsInt(v, -100000, 100000),
    };

    private static string? New(string key, JsonNode? v) => SozSettings.Validate(new JsonObject { [key] = v });

    [Fact]
    public void DondurulmusDosya_33AyarinHepsini_Ve_HerAyardaEnAzAltiDegeriKapsar()
    {
        var byKey = Records.Value.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Count());
        // Dondurulan 33 ayar ayarlar.soz.json içindedir, sonradan eklenen arayüz ayarları (themeFamily, navTabs) dondurulmuş 33 ayarın üst kümesidir.
        var ayarlarDosyasi = JsonNode.Parse(EmbeddedText("ayarlar.soz.json"))!["entries"]!.AsArray()
            .Where(e => (string?)e!["kind"] == "setting").Select(e => (string?)e!["body"]!["legacyKey"] ?? (string)e["id"]!).ToList();
        Assert.Superset(byKey.Keys.ToHashSet(StringComparer.Ordinal), ayarlarDosyasi.ToHashSet(StringComparer.Ordinal));
        Assert.All(ayarlarDosyasi, k => Assert.True(SozSettings.Defaults.ContainsKey(k), k));
        Assert.Equal(33, byKey.Count);
        Assert.All(byKey, kv => Assert.True(kv.Value >= 100, $"{kv.Key}: yalnız {kv.Value} örnek"));
        Assert.True(Records.Value.Count >= 33 * 100, $"toplam {Records.Value.Count}");
        // dosya gerçekten eski davranışı taşıyor: hem kabul hem ret var
        Assert.Contains(Records.Value, r => r.OldAccepted);
        Assert.Contains(Records.Value, r => !r.OldAccepted && r.OldMessage is not null);
    }

    [Fact]
    public void KabulRetSonucu_DondurulmusEskiDavranisla_Ayni_YalnizIzinliAyarlarFarkli()
    {
        var mismatches = new List<string>();
        var differing = new Dictionary<string, int>();
        foreach (var r in Records.Value)
        {
            var v = Parse(r.ValueJson);
            var newMsg = New(r.Key, v);
            var expectedNewAccept = r.OldAccepted && (!Allowed.TryGetValue(r.Key, out var rule) || rule(v));
            if (expectedNewAccept == (newMsg is null))
            {
                if (r.OldAccepted && newMsg is not null) differing[r.Key] = differing.GetValueOrDefault(r.Key) + 1;
                continue;
            }
            mismatches.Add($"{r.Key} = {Cut(r.ValueJson)}: eski {(r.OldAccepted ? "kabul" : "RET")}, beklenen yeni {(expectedNewAccept ? "kabul" : "RET")}, gelen {(newMsg is null ? "kabul" : "RET: " + newMsg)}");
        }
        Assert.True(mismatches.Count == 0, $"Beklenmeyen fark ({mismatches.Count}):\n" + string.Join("\n", mismatches.Take(40)));
        // izinli farkların her biri gerçekten gözleniyor (yani sınanıyor) ve başka ayarda fark yok
        Assert.Equal(Allowed.Keys.OrderBy(k => k, StringComparer.Ordinal), differing.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void RetMesajlari_AyarAdiylaBaslar_Ve_EskiMesajSinifiniKorur()
    {
        var bad = new List<string>();
        var compared = 0;
        foreach (var r in Records.Value.Where(r => !r.OldAccepted && r.OldMessage is not null))
        {
            var v = Parse(r.ValueJson);
            var msg = New(r.Key, v);
            if (msg is null) { bad.Add($"{r.Key} = {Cut(r.ValueJson)}: eski RET, yeni kabul"); continue; }
            // Eski kod bazen ham .NET iletisi veriyordu ("Geçersiz ayar değeri: ..."); yeni mesaj sade Türkçe ayar mesajıdır.
            if (r.OldMessage!.StartsWith("Geçersiz ayar değeri", StringComparison.Ordinal))
            {
                if (!msg.StartsWith(r.Key + ":", StringComparison.Ordinal) && !msg.StartsWith("prices", StringComparison.Ordinal)) bad.Add($"{r.Key} = {Cut(r.ValueJson)}: mesaj ayar adıyla başlamıyor: {msg}");
                continue;
            }
            compared++;
            // Yol ve değer-sınırı mesajları (yeni sınır aynı cümle kalıbında) ve gate (yeni sınır) hariç birebir.
            if (r.Key is "claudePath" or "codexPath") { if (msg != r.OldMessage && (!msg.StartsWith(r.Key + ":", StringComparison.Ordinal) || !msg.Contains("yolu"))) bad.Add($"{r.Key} = {Cut(r.ValueJson)}: eski \"{r.OldMessage}\" yeni \"{msg}\""); }
            else if (r.Key is "gateBudgetSeconds" or "gateMaxAttempts") { if (msg != $"{r.Key}: {(r.Key == "gateBudgetSeconds" ? "10 ile 3600" : "1 ile 20")} arası tam sayı olmalı") bad.Add($"{r.Key}: {msg}"); }
            else if (r.Key is "packetBudget" or "runnerMaxMinutes") { if (msg != r.OldMessage.Replace("sayı olmalı", "tam sayı olmalı")) bad.Add($"{r.Key} = {Cut(r.ValueJson)}: eski \"{r.OldMessage}\" yeni \"{msg}\" (tam sayı bekleniyordu)"); } // d1f: tam sayı ayar
            else if (r.Key == "defaultAgent") { if (msg != r.OldMessage.Replace("Bilinmeyen ajan", "Bilinmeyen yardımcı").Replace("Geçerli ajanlar: claude, codex.", "Geçerli yardımcılar: claude, codex, hermes, opencode, antigravity.")) bad.Add($"{r.Key} = {Cut(r.ValueJson)}: eski \"{r.OldMessage}\" yeni \"{msg}\""); }
            else if (msg != r.OldMessage) bad.Add($"{r.Key} = {Cut(r.ValueJson)}: eski \"{r.OldMessage}\" yeni \"{msg}\"");
        }
        Assert.True(compared > 500, $"karşılaştırılan ret mesajı: {compared}");
        Assert.True(bad.Count == 0, $"Mesaj farkı ({bad.Count}):\n" + string.Join("\n", bad.Take(30)));
    }

    [Fact]
    public void YeniKurallar_Dokuz_Ayar_AdiylaSinanir()
    {
        // gate: yönetici kararı, Validate de artık bunu söylüyor
        foreach (var (k, ok, bad) in new[] { ("gateBudgetSeconds", "10", "9.99"), ("gateMaxAttempts", "20", "21") })
        {
            Assert.Null(New(k, J(ok)));
            Assert.NotNull(New(k, J(bad)));
        }
        Assert.Null(New("gateBudgetSeconds", J("3600"))); Assert.NotNull(New("gateBudgetSeconds", J("3601")));
        Assert.Null(New("gateMaxAttempts", J("1"))); Assert.NotNull(New("gateMaxAttempts", J("0")));
        // d1f: sayaç/adet ayarlar tam sayıdır; kesir reddedilir, ondalıklı yazılmış tam değer (300.0) kabul
        foreach (var (k, frac, whole) in new[] { ("gateBudgetSeconds", "300.5", "300.0"), ("gateMaxAttempts", "2.5", "2.0"), ("packetBudget", "1500.5", "1500.0"), ("runnerMaxMinutes", "90.5", "90.0") })
        {
            Assert.Contains("tam sayı", New(k, J(frac))!);
            Assert.Null(New(k, J(whole)));
        }
        // yedi gevşek ayar
        foreach (var k in new[] { "claudeRoot", "codexRoot", "dictationPath" })
        {
            Assert.Null(New(k, null)); Assert.Null(New(k, J("\"\""))); Assert.Null(New(k, J("\"D:\\\\Kayit\\\\projects\"")));
            Assert.Null(New(k, J(Q(new string('a', 1024))))); Assert.NotNull(New(k, J(Q(new string('a', 1025)))));
            Assert.StartsWith(k + ":", New(k, J("5"))!); Assert.NotNull(New(k, J("true"))); Assert.NotNull(New(k, J("{}")));
        }
        foreach (var k in new[] { "explainCommand", "judgeCommand" })
        {
            Assert.Null(New(k, J("\"claude\""))); Assert.Null(New(k, J(Q(new string('a', 2000))))); Assert.NotNull(New(k, J(Q(new string('a', 2001)))));
            Assert.NotNull(New(k, J("5"))); Assert.NotNull(New(k, null));
        }
        foreach (var k in new[] { "capsuleX", "capsuleY" })
        {
            Assert.Null(New(k, null)); Assert.Null(New(k, J("120"))); Assert.Null(New(k, J("-100000"))); Assert.Null(New(k, J("100000"))); Assert.Null(New(k, J("12.0")));
            Assert.NotNull(New(k, J("100001"))); Assert.NotNull(New(k, J("-100001"))); Assert.NotNull(New(k, J("1.5"))); Assert.NotNull(New(k, J("\"120\""))); Assert.NotNull(New(k, J("true")));
            Assert.Contains("tam sayı", New(k, J("1.5"))!);
        }
        // runnerAllowedTools Validate açısından aynı (yalnız sınıfı değişti): metin ya da boş
        Assert.Null(New("runnerAllowedTools", J("\"Read,Edit\""))); Assert.Null(New("runnerAllowedTools", null)); Assert.NotNull(New("runnerAllowedTools", J("5")));
        // workingSeconds kullanım dışı ama doğrulaması aynen çalışıyor
        Assert.Null(New("workingSeconds", J("90"))); Assert.NotNull(New("workingSeconds", J("0")));
    }

    [Fact]
    public void BilinmeyenAnahtar_IkisindeDeReddedilir_KismiGuncellemeCokAnahtarli()
    {
        var bad = new JsonObject { ["yokBoyleAyar"] = 1 };
        Assert.Equal("Bilinmeyen ayar: yokBoyleAyar", SozSettings.Validate(bad));

        // çok anahtarlı yama: biri geçersizse hepsi ret
        Assert.Null(SozSettings.Validate(new JsonObject { ["language"] = "en", ["packetBudget"] = 500 }));
        Assert.NotNull(SozSettings.Validate(new JsonObject { ["language"] = "en", ["packetBudget"] = -1 }));
        // boş yama kabul
        Assert.Null(SozSettings.Validate(new JsonObject()));
        // çok uzun, denetim karakterli bilinmeyen anahtar mesaja taşınmaz
        var weird = SozSettings.Validate(new JsonObject { ["x\n" + new string('q', 200)] = 1 })!;
        Assert.DoesNotContain('\n', weird);
        Assert.True(weird.Length < 100);
    }

    [Fact]
    public void HataMetni_AyarAdiylaBaslar_SinirSayilariKayittanGelir()
    {
        var msg = New("maxParallelRuns", J("7"))!;
        Assert.Equal("maxParallelRuns: 1 ile 6 arası tam sayı olmalı", msg);
        Assert.Equal("language: tr ya da en olmalı", New("language", J("\"de\"")));
        Assert.Equal("theme: system, light ya da dark olmalı", New("theme", J("5")));
        Assert.Equal("hotkeyPacket: kısayol metni olmalı (ör. Ctrl+Alt+J)", New("hotkeyPacket", J("\"\"")));
        Assert.Equal("lanEnabled: true/false olmalı", New("lanEnabled", J("1")));
        Assert.Equal("packetBudget: pozitif tam sayı olmalı", New("packetBudget", J("0"))); // d1f: adet, tam sayı
        Assert.Equal("extractThreshold: 0 ile 1 arası sayı olmalı", New("extractThreshold", J("1.5")));
    }

    [Fact]
    public void ValidateValue_AyarOlmayanKaydiReddeder()
    {
        var conn = RegistryLoader.LoadEmbedded().Get("connector:claude")!;
        Assert.Throws<ArgumentException>(() => SettingValidator.ValidateValue(conn, null));
    }

    private static string Cut(string s) => s.Length <= 50 ? s : s[..47] + "...";
}
