using System.Text.Json.Nodes;
using Soz.Cli;
using Soz.Cli.Commands;
using Soz.Core.Registry;
using Xunit;
using static Soz.Core.Tests.RegistryTestKit;

namespace Soz.Core.Tests;

/// <summary>Tek kayıt defterinin çatısı: harf katlama, şema doğrulayıcı, gömülü sabit kayıtlar, sahiplik kuralları, onay kapısı, kural denetimi (d1b).</summary>
public class RegistryFoldTests
{
    [Theory]
    [InlineData("KIRMIZI", "kırmızı")]
    [InlineData("kırmızı", "KIRMIZI")]
    [InlineData("İstanbul", "ISTANBUL")]
    [InlineData("ıstanbul", "İSTANBUL")]
    [InlineData("istanbul", "Istanbul")]
    [InlineData("ŞĞÜÖÇ", "sguoc")]
    [InlineData("şğüöç", "SGUOC")]
    [InlineData("Kısayol", "kisayol")]
    public void Fold_TurkceHarfleriEsitler(string a, string b) => Assert.Equal(Fold.Text(a), Fold.Text(b));

    [Fact]
    public void Fold_FarkliKelimeleriBirlestirmez_Ve_BosGirdideCokmez()
    {
        Assert.NotEqual(Fold.Text("kasa"), Fold.Text("kara"));
        Assert.NotEqual(Fold.Text("ip"), Fold.Text("ıp") + "x");
        Assert.Equal("", Fold.Text(null));
        Assert.Equal("", Fold.Text(""));
        Assert.True(Fold.Contains("Sade Anlat Ayarı", "ANLAT"));
        Assert.False(Fold.Contains("Sade", "anlat"));
    }

    private static readonly string[] Words =
    {
        "IİıiŞ", "KIRMIZI", "kırmızı", "İSTANBUL", "ıslak", "IŞIK", "ışık", "Çığ", "ŞÖÜĞ", "şöüğ", "Kısayol", "ISPARTA", "ığdır", "İ", "i̇", "ÇAĞRI", "çağrı",
        "Sade Anlat Ayarı", "BÜYÜK küçük", "DİKTE köprüsü", "İ", "ı", "I", "i", "ǅ", "ß", "ﬁ", "Ａ", "ĞĞĞ", "oğuz", "OĞUZ",
    };

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void Fold_KulturdenBagimsiz_OtuzBirKelimedeDortKulturdeAyniSonuc(string culture)
    {
        // Referans sabit kültürle üretilir; her kültürde birebir aynı olmalı (Türkçe kültürde "I".ToLower() "ı" olurdu).
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var reference = Words.Select(Fold.Text).ToArray();
            Thread.CurrentThread.CurrentCulture = culture.Length == 0 ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo(culture);
            Assert.Equal(reference, Words.Select(Fold.Text).ToArray());
            Assert.Equal("iiiis", Fold.Text("IİıiŞ"));
            Assert.Equal(Fold.Text("KIRMIZI"), Fold.Text("kırmızı"));
            Assert.Equal(Fold.Text("İSTANBUL"), Fold.Text("istanbul"));
            Assert.Equal(Fold.Text("IŞIK"), Fold.Text("ışık"));
            Assert.Equal("i", Fold.Text("İ"));
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void Fold_ToLowerInvariantTuzagi_BasitKucultmeYetmez_Fold_Yeter()
    {
        // Tuzak: "KIRMIZI".ToLowerInvariant() = "kirmizi" ("kırmızı" ile eşleşmez); "İSTANBUL" ise nokta işaretli i ile kalır.
        Assert.NotEqual("kırmızı", "KIRMIZI".ToLowerInvariant());
        Assert.NotEqual("istanbul", "İSTANBUL".ToLowerInvariant());
        Assert.Equal(Fold.Text("kırmızı"), Fold.Text("KIRMIZI"));
        Assert.Equal("istanbul", Fold.Text("İSTANBUL"));
    }

    [Fact]
    public void Fold_YarimVekilKarakterdeCokmez()
    {
        // Denetim bulgu 10: "a\uD800" Normalize'da ArgumentException fırlatıyordu.
        Assert.Equal("a�", Fold.Text("a\uD800"));           // yalnız yüksek vekil
        Assert.Equal("�b", Fold.Text("\uDC00b"));           // yalnız alçak vekil
        Assert.Equal("a�b�", Fold.Text("a\uD800b\uDFFF"));
        Assert.Equal("😀", Fold.Text("😀"));  // geçerli çift korunur
        Assert.True(Fold.Contains("Kısayol \uD800", "KISAYOL"));
        Assert.True(Fold.Contains("a\uD800", "a"));
        var e = Assert.Single(LoadUser(SettingFile("setting:ornek.vekil", entryPatchJson: """{"title":"Vekil"}""")).Entries);
        Assert.False(e.Matches("a\uD800b"));                      // arama da çökmez
        Assert.False(e.Matches("\uD800"));                         // yalnız vekil → U+FFFD aranır, başlıkta yok; çökmez
    }

    [Fact]
    public void Arama_AnahtarSozcuklerdeTurkceKatlamaylaBulur()
    {
        var load = LoadUser(SettingFile("setting:ornek.kisayol", entryPatchJson: """{"title":"Paket","keywords":["kısayol","hotkey"]}"""));
        var e = Assert.Single(load.Entries);
        Assert.True(e.Matches("KISAYOL"));
        Assert.True(e.Matches("kısayol"));
        Assert.True(e.Matches("Kısayol"));
        Assert.True(e.Matches("HOTKEY"));
        Assert.True(e.Matches("paket"));
        Assert.False(e.Matches("telefon"));
        Assert.True(e.Matches(""));
    }
}

public class SchemaCheckTests
{
    private static IReadOnlyList<string> V(string schema, string value) => SchemaCheck.FromJson(schema).Validate(J(value));

    [Fact]
    public void Tur_Sayi_TamSayi_Ve_Null()
    {
        Assert.Empty(V("""{"type":"integer"}""", "3"));
        Assert.Empty(V("""{"type":"integer"}""", "3.0"));
        Assert.NotEmpty(V("""{"type":"integer"}""", "3.5"));
        Assert.NotEmpty(V("""{"type":"number"}""", "\"3\""));
        Assert.Empty(V("""{"type":["string","null"]}""", "null"));
        Assert.NotEmpty(V("""{"type":"string"}""", "null"));
        Assert.NotEmpty(V("""{"type":"boolean"}""", "1"));
        Assert.Contains("tür", V("""{"type":"object"}""", "[]").Single());
    }

    [Fact]
    public void Sinirlar_MinMax_Ve_Dislayan()
    {
        const string s = """{"type":"number","minimum":1,"maximum":3600}""";
        Assert.Empty(V(s, "1"));
        Assert.Empty(V(s, "3600"));
        Assert.NotEmpty(V(s, "0.99"));
        Assert.NotEmpty(V(s, "3600.5"));
        Assert.NotEmpty(V("""{"exclusiveMinimum":0}""", "0"));
        Assert.Empty(V("""{"exclusiveMinimum":0}""", "0.001"));
        Assert.NotEmpty(V("""{"exclusiveMaximum":10}""", "10"));
        Assert.Empty(V("""{"multipleOf":0.5}""", "2.5"));
        Assert.NotEmpty(V("""{"multipleOf":0.5}""", "2.3"));
    }

    [Fact]
    public void Metin_Enum_Const_Pattern()
    {
        Assert.Empty(V("""{"enum":["tr","en"]}""", "\"tr\""));
        Assert.Contains("listesinde yok", V("""{"enum":["tr","en"]}""", "\"de\"").Single());
        Assert.NotEmpty(V("""{"const":1}""", "2"));
        Assert.NotEmpty(V("""{"type":"string","minLength":2}""", "\"a\""));
        Assert.NotEmpty(V("""{"type":"string","maxLength":2}""", "\"abc\""));
        Assert.Contains("kalıba uymuyor", V("""{"type":"string","pattern":"^[a-z]+$"}""", "\"A1\"").Single());
        Assert.Empty(V("""{"enum":["a",null]}""", "null"));
    }

    [Fact]
    public void Nesne_ZorunluAlan_SikiAlan_Ve_Dizi()
    {
        const string s = """{"type":"object","required":["a"],"properties":{"a":{"type":"integer"},"l":{"type":"array","items":{"type":"integer"},"minItems":1,"maxItems":2,"uniqueItems":true}},"additionalProperties":false}""";
        Assert.Empty(V(s, """{"a":1,"l":[1,2]}"""));
        Assert.Contains("zorunlu alan yok: a", V(s, "{}").Single());
        Assert.Contains("bilinmeyen alan: z", V(s, """{"a":1,"z":2}""").Single());
        Assert.NotEmpty(V(s, """{"a":1,"l":[]}"""));
        Assert.NotEmpty(V(s, """{"a":1,"l":[1,2,3]}"""));
        Assert.NotEmpty(V(s, """{"a":1,"l":[1,1]}"""));
        Assert.Contains("$.l[1]", V(s, """{"a":1,"l":[1,"x"]}""").Single());
    }

    [Fact]
    public void IfThen_OneOf_AnyOf_AllOf_Ref()
    {
        const string s = """
            {"$defs":{"N":{"type":"integer"}},
             "allOf":[{"if":{"properties":{"k":{"const":"a"}},"required":["k"]},"then":{"required":["x"]}}],
             "properties":{"n":{"$ref":"#/$defs/N"},"o":{"oneOf":[{"type":"string"},{"type":"integer"}]},"p":{"anyOf":[{"const":1},{"const":2}]}}}
            """;
        Assert.Empty(V(s, """{"k":"b"}"""));
        Assert.Contains("zorunlu alan yok: x", V(s, """{"k":"a"}""").Single());
        Assert.Empty(V(s, """{"k":"a","x":1}"""));
        Assert.NotEmpty(V(s, """{"n":"x"}"""));
        Assert.NotEmpty(V(s, """{"o":true}"""));
        Assert.Empty(V(s, """{"o":3,"p":2}"""));
        Assert.NotEmpty(V(s, """{"p":3}"""));
    }

    [Fact]
    public void DesteklenmeyenAnahtarSozcuk_Bulunur_AciklamaSozcukleriYokSayilir()
    {
        var found = SchemaCheck.UnsupportedKeywords(J("""{"type":"object","description":"x","properties":{"default":{"type":"string"},"a":{"format":"uri"}}}"""));
        Assert.Single(found);
        Assert.Contains("format", found[0]);
        Assert.Empty(SchemaCheck.UnsupportedKeywords(J("""{"type":"string","title":"x","description":"y","default":"z"}""")));
    }

    [Fact]
    public void KayitSemasi_YalnizDesteklenenSozcukleriKullanir()
    {
        Assert.Empty(SchemaCheck.UnsupportedKeywords(JsonNode.Parse(RegistryLoader.SchemaText)));
    }
}

public class RegistryEmbeddedTests
{
    private static RegistryLoadResult Load() => RegistryLoader.LoadEmbedded();

    /// <summary>Çekirdek 33 ayarın eski anahtarları: dondurulmuş dosyadan (d1c). SozSettings.Defaults artık kayıttan türediği için yeni ayarlar onu büyütür; çekirdek küme sabit kalır.</summary>
    private static readonly Lazy<IReadOnlyList<string>> CoreKeys = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "ayar-eski-davranis.json")))!.AsObject().Select(kv => kv.Key).ToList());

    [Fact]
    public void GomuluKayitlar_HatasizYuklenir_SayilariDogru()
    {
        var load = Load();
        Assert.Empty(load.Rejected);
        Assert.Empty(load.Warnings);
        // d2m: sayıdan bağımsız. Çekirdek 33 ayar (SozSettings.Defaults) kayıtta var; öteki şeritlerin kayıtları üstüne eklenebilir.
        Assert.Equal(33, CoreKeys.Value.Count);
        Assert.True(load.Settings.Count() >= CoreKeys.Value.Count);
        Assert.All(CoreKeys.Value, k => Assert.NotNull(load.SettingByLegacyKey(k)));
        var conns = load.OfKind(RegistryKinds.Connector).Select(e => e.Id).ToHashSet();
        Assert.Superset(new HashSet<string> { "connector:claude", "connector:codex", "connector:hermes", "connector:opencode", "connector:antigravity" }, conns);
        var kinds = load.OfKind(RegistryKinds.CheckKind).Select(e => e.Id["check-kind:".Length..]).ToHashSet();
        Assert.Superset(new HashSet<string>(Soz.Core.Proof.CheckKinds.All), kinds);                          // eski dört + d2a altı yerleşik kanıt türü (kod listesiyle aynı)
        Assert.Subset(kinds, new HashSet<string>(Soz.Core.Proof.CheckKinds.All));
        Assert.NotEmpty(load.OfKind(RegistryKinds.Reader));                                                   // gözle okuyucu kaydı (tanışma) uyarısız yüklenir
        // Tek kullanım dışı kayıt: workingSeconds. Yerleşik bağlayıcılar kurulu araçlar engele takılmasın diye açık gelir, isteyen Ayarlar'dan kapatabilir.
        var suggested = new HashSet<string>();
        Assert.All(load.Entries, e => { Assert.Equal(RegistryOwners.Fixed, e.Owner); Assert.Equal(e.Id == "setting:session.working-seconds" ? RegistryStates.Deprecated : suggested.Contains(e.Id) ? RegistryStates.Suggested : RegistryStates.Enabled, e.State); Assert.Equal("core", e.Origin); Assert.Matches("^[0-9a-f]{64}$", e.Sha256); Assert.Empty(e.Problems); });
        Assert.Equal(new[] { "setting:session.working-seconds" }, load.Entries.Where(e => e.State == RegistryStates.Deprecated).Select(e => e.Id).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(load.Get("setting:session.working-seconds")!.Deprecated!.Message?.Tr));
    }

    [Fact]
    public void GomuluKayitlar_RegistryCheckten_GecerVeKuralDenetimiTemizdir()
    {
        var report = RegistryChecker.Check(Load());
        Assert.True(report.Ok, report.Format());
        Assert.Equal(Load().Entries.Count, report.EntryCount);   // d2m: sabit 39 yerine yüklenen kayıt sayısı; yeni kayıt eklemek bu testi kırmaz
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void OtuzUcAyar_EskiAnahtarlariSozlukleBireBirAyni_Ve_VarsayilanlarEsit()
    {
        var load = Load();
        // d2m: eski 33 ayarın her biri kayıtta, aynı varsayılanla duruyor. Sonradan eklenen ayarlar (kesif, sirabende, gerialma...) bu sözlükte olmak zorunda değil.
        Assert.All(CoreKeys.Value, k => Assert.True(SozSettings.Defaults.ContainsKey(k), k));
        foreach (var key in CoreKeys.Value)
        {
            var def = SozSettings.Defaults[key];
            var e = load.SettingByLegacyKey(key)!;
            Assert.True(SameJson(def, e.Setting!.Default), $"{key}: kayıttaki varsayılan {e.Setting.Default?.ToJsonString()} bugünkü {def?.ToJsonString()} değil");
            Assert.Matches(@"^setting:[a-z][a-z0-9-]*\.[a-z][a-z0-9.-]*$", e.Id); // alan.ad biçimi
        }
    }

    private static bool SameJson(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonValue va && b is JsonValue vb && va.GetValueKind() == System.Text.Json.JsonValueKind.Number && vb.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            return Math.Abs(double.Parse(a.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) - double.Parse(b.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)) < 1e-12;
        return JsonNode.DeepEquals(a, b);
    }

    [Fact]
    public void AyarSiniflari_KartDagilimi_Ve_Kurallar()
    {
        var load = Load();
        var dist = load.Settings.GroupBy(e => e.Setting!.Class).ToDictionary(g => g.Key, g => g.Count());
        // d2m: dondurulan çekirdek 33 ayarın dağılımı (yeni kayıtlar bunu değiştirmez). Program başlatan ayarlar K4 (güvenlik kapısı): komut ayarları (d1f) ve claudePath, codexPath, dictationPath (d2m).
        var core = load.Settings.Where(e => CoreKeys.Value.Contains(e.Setting!.LegacyKey ?? e.Id)).GroupBy(e => e.Setting!.Class).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int> { ["K1"] = 6, ["K2"] = 12, ["K3"] = 3, ["K4"] = 10, ["K5"] = 2 }, core);
        foreach (var k in new[] { "claudePath", "codexPath", "dictationPath" })
        {
            var st = load.SettingByLegacyKey(k)!.Setting!;
            Assert.Equal("K4", st.Class); Assert.False(st.ModeSettable); Assert.False(string.IsNullOrEmpty(st.Restrictive));
        }
        Assert.True(dist.Values.Sum() == load.Settings.Count());
        // runnerAllowedTools güvenlik kapısı oldu: liste ayarında en dar olan (kesişim) kazanır
        Assert.Equal("K4", load.SettingByLegacyKey("runnerAllowedTools")!.Setting!.Class);
        Assert.Equal("intersection-wins", load.SettingByLegacyKey("runnerAllowedTools")!.Setting!.Restrictive);
        Assert.All(load.Settings.Where(e => e.Setting!.Class != "K5"), e => { Assert.NotNull(e.Setting!.Group); Assert.NotNull(e.Setting.Card); });
        Assert.All(load.Settings.Where(e => e.Setting!.Class == "K5"), e => Assert.Null(e.Setting!.Group));
        Assert.All(load.Settings.Where(e => e.Setting!.Class == "K4"), e => Assert.False(string.IsNullOrEmpty(e.Setting!.Restrictive)));
        Assert.All(load.Settings.Where(e => e.Setting!.Class is "K3" or "K4"), e => Assert.False(e.Setting!.ModeSettable));
        var cards = load.Settings.Where(e => e.Setting!.Card is not null).GroupBy(e => (e.Setting!.Group, e.Setting.Card)).ToList();
        Assert.All(cards, g => Assert.InRange(g.Count(), 1, 7));
    }

    [Fact]
    public void IkiBilinenFark_KayitDogruSiniriTasir()
    {
        var load = Load();
        Assert.Equal((10.0, 3600.0), Range(load.SettingByLegacyKey("gateBudgetSeconds")!));
        Assert.Equal((1.0, 20.0), Range(load.SettingByLegacyKey("gateMaxAttempts")!));
    }

    private static (double, double) Range(RegistryEntry e) => (e.Setting!.Schema["minimum"]!.GetValue<double>(), e.Setting.Schema["maximum"]!.GetValue<double>());

    [Fact]
    public void Baglayicilar_Ve_OlcutTurleri_GercekKodlaUyumlu()
    {
        var load = Load();
        foreach (var c in Soz.Core.Agents.AgentRegistry.Connectors)
        {
            var e = load.Get("connector:" + c.Id);
            Assert.NotNull(e);
            var caps = e!.Body["capabilities"]!.AsObject();
            Assert.Equal(c.Capabilities.Headless, caps["headless"]!.GetValue<bool>());
            Assert.Equal(c.Capabilities.EventStream, caps["eventStream"]!.GetValue<bool>());
            Assert.Equal(c.Capabilities.Resume, caps["resume"]!.GetValue<bool>());
            Assert.Equal(c.Capabilities.Stop, caps["stop"]!.GetValue<bool>());
            Assert.Equal(Kebab(c.Capabilities.BeforeTool.ToString()), caps["beforeTool"]!.GetValue<string>());
            Assert.Equal(Kebab(c.Capabilities.TurnEnd.ToString()), caps["turnEnd"]!.GetValue<string>());
            Assert.Equal(Kebab(c.Capabilities.Ask.ToString()), caps["ask"]!.GetValue<string>());
            Assert.Equal(Kebab(c.Capabilities.Cost.ToString()), caps["cost"]!.GetValue<string>());
            Assert.NotNull(load.Get(e.Body["detect"]!["pathSetting"]!.GetValue<string>()));
        }
        var kinds = load.OfKind(RegistryKinds.CheckKind).Select(e => e.Id["check-kind:".Length..]).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(Soz.Core.Proof.CheckKinds.All.OrderBy(x => x, StringComparer.Ordinal), kinds);
        Assert.All(RegistryChecker.BuiltinMeasures, m => Assert.Contains(m, Soz.Core.Proof.CheckKinds.All));
        // Şablon kimlikleri CheckTemplates.For ile aynı yedi cümle.
        var ids = load.OfKind(RegistryKinds.CheckKind).SelectMany(e => e.Body["templates"]!.AsArray().Select(t => t!["id"]!.GetValue<string>())).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(Soz.Core.Proof.CheckTemplates.For(null).Templates.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal), ids);
    }

    private static string Kebab(string pascal) => string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? "-" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));

    // ---- sözleşme (contract) kuralı ve CLI çıkış kodu
    private static List<RegistrySource> EmbeddedWith(string fileName, Action<JsonObject> mutate)
    {
        var list = new List<RegistrySource>();
        foreach (var s in RegistryLoader.EmbeddedSources())
        {
            if (s.Name != fileName) { list.Add(s); continue; }
            var root = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(s.Bytes))!.AsObject();
            mutate(root["entries"]!.AsArray()[0]!.AsObject());
            list.Add(RegistrySource.FromText(s.Name, "core", root.ToJsonString()));
        }
        return list;
    }

    private static (int code, string text) Cli(IReadOnlyList<RegistrySource> fixedSources, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var a = Args.Parse(args, new HashSet<string> { "json" });
        var code = RegistryCommands.Run(a, o, e, fixedSources);
        return (code, o.ToString() + e.ToString());
    }

    [Fact]
    public void RegistryCheck_BozulmamisPaketta_SifirDoner()
    {
        var (code, text) = Cli(RegistryLoader.EmbeddedSources().ToList(), "check");
        Assert.Equal(0, code);
        Assert.Contains("Sonuç: GEÇTİ", text);
        Assert.Contains($"{RegistryLoader.LoadEmbedded().Entries.Count} kayıt", text);
    }

    [Fact]
    public void RegistryCheck_ContractCumlesiSilinince_BirDoner()
    {
        // 1) cümle anahtarı silindi: şema yüklemeyi reddeder, denetim hata verir
        var (c1, t1) = Cli(EmbeddedWith("ayarlar.soz.json", e => e["contract"]!.AsObject().Remove("onError")), "check");
        Assert.Equal(1, c1);
        Assert.Contains("onError", t1);
        Assert.Contains("Sonuç: KALDI", t1);
        // 2) cümle boşluğa çevrildi: şema geçer, kural denetimi yakalar
        var (c2, t2) = Cli(EmbeddedWith("ayarlar.soz.json", e => e["contract"]!["discover"] = "   "), "check");
        Assert.Equal(1, c2);
        Assert.Contains("contract cümlesi eksik ya da boş: discover", t2);
        // 3) contract bütünüyle silindi
        var (c3, t3) = Cli(EmbeddedWith("baglayicilar.soz.json", e => e.Remove("contract")), "check");
        Assert.Equal(1, c3);
        Assert.Contains("contract (dört cümle) yok", t3);
    }

    [Fact]
    public void RegistryCheck_KuralIhlalleri_Yakalanir()
    {
        var load = Load();
        // aynı kart 8 ayar: 7 sınırı
        var many = Enumerable.Range(1, 8).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = $"setting:kart.ayar{i}", ["kind"] = "setting", ["owner"] = "user", ["title"] = "x",
            ["body"] = JsonNode.Parse("""{"schema":{"type":"boolean"},"default":false,"class":"K1","group":"genel","card":"Kalabalık","order":1}"""),
        }).ToArray();
        var file = new JsonObject { ["schemaVersion"] = 1, ["entries"] = new JsonArray(many) }.ToJsonString();
        var r = RegistryChecker.Check(LoadUser(file));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, x => x.Contains("\"Kalabalık\" kartında") && x.Contains("8 ayar"));

        // bağlı kimlik yok
        var conn = LoadUser("""{"schemaVersion":1,"entries":[{"id":"connector:x","kind":"connector","owner":"user","title":"x","body":{"depth":["run"],"detect":{"commands":["x"],"pathSetting":"setting:yok.boyle"},"launch":{"protocol":"acp","command":"x"}}}]}""");
        Assert.Contains(RegistryChecker.Check(conn).Errors, x => x.Contains("setting:yok.boyle") && x.Contains("kayıt defterinde yok"));

        // varsayılan değer kendi şemasına uymuyor
        var bad = LoadUser(SettingFile("setting:ornek.yanlis-varsayilan", """{"schema":{"type":"integer","minimum":5},"default":1}"""));
        Assert.Contains(RegistryChecker.Check(bad).Errors, x => x.Contains("varsayılan değer kendi şemasına uymuyor"));

        // eski anahtar iki ayarda
        var twice = LoadUser(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["entries"] = new JsonArray(
                JsonNode.Parse(SettingFile("setting:ornek.bir", """{"legacyKey":"ayniAnahtar"}"""))!["entries"]![0]!.DeepClone(),
                JsonNode.Parse(SettingFile("setting:ornek.iki", """{"legacyKey":"ayniAnahtar"}"""))!["entries"]![0]!.DeepClone()),
        }.ToJsonString());
        // d2m: yükleyici ikinci ayarı kendisi reddeder (d1f); denetim bunu hata sayar. Denetimin kendi "birden çok ayarda" kuralı yüklenmiş listeye bakar.
        Assert.Contains(RegistryChecker.Check(twice).Errors, x => x.Contains("ayniAnahtar") && x.Contains("İki ayar aynı anahtarı paylaşamaz"));
        Assert.True(RegistryChecker.Check(load).Ok);
    }

    [Fact]
    public void RegistryList_KindSuzgeci_Ve_BilinmeyenTur()
    {
        var src = RegistryLoader.EmbeddedSources().ToList();
        var (c, t) = Cli(src, "list", "--kind", "connector");
        Assert.Equal(0, c);
        Assert.Contains("connector:claude", t);
        Assert.DoesNotContain("setting:", t);
        Assert.Contains("5 kayıt (connector).", t);
        var (c2, t2) = Cli(src, "list", "--kind", "hologram");
        Assert.Equal(2, c2);
        Assert.Contains("Bilinmeyen tür", t2);
        var (c3, _) = Cli(src, "list", "--json");
        Assert.Equal(0, c3);
        var (c4, t4) = Cli(src, "yok");
        Assert.Equal(2, c4);
        Assert.Contains("Kullanım", t4);
    }
}

public class RegistryOwnershipTests
{
    [Fact]
    public void FixedKimliginiTaklitEdenKullaniciDosyasi_Reddedilir()
    {
        // gerçek bir fixed kimliği, sahip user
        var fixedId = RegistryLoader.LoadEmbedded().Settings.First().Id;
        using var dir = new RegistryUserDir();
        dir.Write("taklit.soz.json", SettingFile(fixedId));
        var load = RegistryLoader.LoadEmbedded(dir.Path);
        Assert.Contains(load.Rejected, p => p.Code == "fixed-id" && p.Id == fixedId && p.Message.Contains("Başka sahip"));
        Assert.Equal(RegistryLoader.LoadEmbedded().Entries.Count, load.Entries.Count);                 // gömülü kayıtlar bozulmadı
        Assert.Equal("core", load.Get(fixedId)!.Origin);      // kimliği gömülü kayıt tutuyor
        // sahip fixed yazılırsa iki neden de görünür
        dir.Write("taklit.soz.json", SettingFile(fixedId, entryPatchJson: """{"owner":"fixed"}"""));
        var load2 = RegistryLoader.LoadEmbedded(dir.Path);
        Assert.Contains(load2.Rejected, p => p.Code == "owner");
        Assert.Contains(load2.Rejected, p => p.Code == "fixed-id");
        Assert.False(RegistryChecker.Check(load2).Ok);
    }

    [Fact]
    public void KullaniciKaydi_GomuluSabitlerleYanYanaYuklenir_VeCheckGecer()
    {
        using var dir = new RegistryUserDir();
        dir.Write("alt/ornek.soz.json", SettingFile("setting:ornek.gurultu"));
        var load = RegistryLoader.LoadEmbedded(dir.Path);
        Assert.Empty(load.Rejected);
        var e = load.Get("setting:ornek.gurultu")!;
        Assert.Equal(RegistryOwners.User, e.Owner);
        Assert.StartsWith("file:", e.Origin);
        Assert.Equal(RegistryStates.Enabled, e.State);
        Assert.True(RegistryChecker.Check(load).Ok);
        Assert.Equal(RegistryLoader.LoadEmbedded().Entries.Count + 1, load.Entries.Count);
    }

    [Fact]
    public void OlmayanKlasor_AcikHata_GomuluKayitlarYineYuklenir()
    {
        // Eski test bunu "hata değil" diye sabitliyordu: yazım hatalı --dir sessizce "tamam" derdi (denetim bulgu 3). Artık açık hata.
        var load = RegistryLoader.LoadEmbedded(Path.Combine(TestEnv.RepoRoot, "_gecici", "testler", "d1b-yok-" + Guid.NewGuid().ToString("N")));
        var p = Assert.Single(load.Rejected);
        Assert.Equal("dir", p.Code);
        Assert.Contains("klasör bulunamadı", p.Message);
        Assert.Equal(RegistryLoader.LoadEmbedded().Entries.Count, load.Entries.Count);                 // gömülü kayıtlar yine yüklendi
        Assert.False(RegistryChecker.Check(load).Ok);
    }

    [Fact]
    public void SistemKaydi_YalnizSuggested()
    {
        var ok = LoadUser(SettingFile("setting:oneri.a", entryPatchJson: """{"owner":"system"}"""));
        Assert.Equal(RegistryStates.Suggested, Assert.Single(ok.Entries).State); // durum yazılmasa da etkin başlamaz
        var bad = LoadUser(SettingFile("setting:oneri.b", entryPatchJson: """{"owner":"system","state":"enabled"}"""));
        Assert.Empty(bad.Entries);
        Assert.Contains(bad.Rejected, p => p.Code == "system-state");
        var disabled = LoadUser(SettingFile("setting:oneri.c", entryPatchJson: """{"owner":"system","state":"disabled"}"""));
        Assert.Contains(disabled.Rejected, p => p.Code == "system-state");
    }

    [Fact]
    public void CalistirilabilirKullaniciKaydi_OnaysizSuggested_OnayliOzetteEnabled_DosyaDegisinceTekrarSuggested()
    {
        const string connector = """{"schemaVersion":1,"entries":[{"id":"connector:opencode","kind":"connector","owner":"user","title":"OpenCode","body":{"depth":["run"],"detect":{"commands":["opencode"]},"launch":{"protocol":"acp","command":"opencode","args":["acp"]}}}]}""";
        var approvals = new MemoryRegistryApprovals();

        // 1) onaysız
        var first = LoadUser(connector, approvals);
        var e1 = Assert.Single(first.Entries);
        Assert.Equal(RegistryStates.Suggested, e1.State);
        Assert.Contains(e1.Problems, p => p.StartsWith("Onay bekliyor"));

        // 2) onaylı özet
        approvals.Approve(e1.Id, e1.Sha256);
        var second = LoadUser(connector, approvals);
        Assert.Equal(RegistryStates.Enabled, Assert.Single(second.Entries).State);
        Assert.Empty(second.Entries[0].Problems);

        // 3) dosya değişti (komut değişti): özet değişir, yeniden suggested
        var changed = connector.Replace("\"command\":\"opencode\"", "\"command\":\"baska-program\"");
        var third = LoadUser(changed, approvals);
        var e3 = Assert.Single(third.Entries);
        Assert.NotEqual(e1.Sha256, e3.Sha256);
        Assert.Equal(RegistryStates.Suggested, e3.State);

        // 4) aynı içerik başka adlı dosyada da aynı özetle onaylı sayılır; başka kimlik onaylı sayılmaz
        Assert.Equal(RegistryStates.Enabled, Assert.Single(LoadUser(connector, approvals, "baska-ad.soz.json").Entries).State);
        approvals.Revoke(e1.Id, e1.Sha256);
        Assert.Equal(RegistryStates.Suggested, Assert.Single(LoadUser(connector, approvals).Entries).State);
    }

    [Fact]
    public void ApprovalsNull_VarsayilanOlarakHicbirSeyOnayliDegil()
    {
        var e = Assert.Single(LoadUser("""{"schemaVersion":1,"entries":[{"id":"connector:x","kind":"connector","owner":"user","title":"x","body":{"depth":["run"],"detect":{"commands":["x"]},"launch":{"protocol":"acp"}}}]}""", null).Entries);
        Assert.Equal(RegistryStates.Suggested, e.State);
    }

    [Fact]
    public void K4Ayar_Ve_ExecIzniIsteyenKayit_IlkGelisteSuggested_OtekilerEnabled()
    {
        var k4 = LoadUser(SettingFile("setting:ornek.kapi", """{"class":"K4","securityGate":{"restrictive":"false-wins"},"group":"kural-kanit"}"""));
        Assert.Equal(RegistryStates.Suggested, Assert.Single(k4.Entries).State);

        var k1 = LoadUser(SettingFile("setting:ornek.tercih"));
        Assert.Equal(RegistryStates.Enabled, Assert.Single(k1.Entries).State);

        var exec = LoadUser(SettingFile("setting:ornek.izinli", entryPatchJson: """{"permissions":[{"cap":"exec","scope":"x","why":"çünkü"}]}"""));
        Assert.Equal(RegistryStates.Suggested, Assert.Single(exec.Entries).State);

        var fsRead = LoadUser(SettingFile("setting:ornek.okur", entryPatchJson: """{"permissions":[{"cap":"fs.read","scope":"x","why":"çünkü"}]}"""));
        Assert.Equal(RegistryStates.Enabled, Assert.Single(fsRead.Entries).State);
    }

    [Fact]
    public void OlcutTuru_CommandVeModulOlcerleriOnayIster_DosyaOlcerEtkinBaslar()
    {
        string Kind(string id, string via, string name) => """{"schemaVersion":1,"entries":[{"id":"ID_","kind":"check-kind","owner":"user","title":"x","body":{"specSchema":{"type":"object"},"measure":{"via":"VIA_","name":"NAME_"},"reportsCount":true}}]}""".Replace("ID_", id).Replace("VIA_", via).Replace("NAME_", name);
        Assert.Equal(RegistryStates.Suggested, Assert.Single(LoadUser(Kind("check-kind:a", "builtin", "command")).Entries).State);
        Assert.Equal(RegistryStates.Suggested, Assert.Single(LoadUser(Kind("check-kind:b", "module", "bir.modul")).Entries).State);
        Assert.Equal(RegistryStates.Enabled, Assert.Single(LoadUser(Kind("check-kind:c", "builtin", "file")).Entries).State);
    }

    [Fact]
    public void KullaniciKaydi_FixedSahipVeBrokenYazamaz_AyniKimlikIkinciKezReddedilir()
    {
        Assert.Contains(LoadUser(SettingFile("setting:o.a", entryPatchJson: """{"owner":"fixed"}""")).Rejected, p => p.Code == "owner");
        Assert.Contains(LoadUser(SettingFile("setting:o.b", entryPatchJson: """{"state":"broken"}""")).Rejected, p => p.Code == "state");
        var res = RegistryLoader.Load(Array.Empty<RegistrySource>(), new[] { Source("1.soz.json", SettingFile("setting:o.c")), Source("2.soz.json", SettingFile("setting:o.c")) });
        Assert.Single(res.Entries);
        Assert.Contains(res.Rejected, p => p.Code == "duplicate" && p.Source == "2.soz.json");
    }

    [Fact]
    public void BilinmeyenTur_Yuklenmez_UyariListesineDuser_TaninanTurUyariylaYuklenir()
    {
        var res = LoadUser("""{"schemaVersion":1,"entries":[{"id":"hologram:x","kind":"hologram","owner":"user","title":"x"},{"id":"theme:x","kind":"theme","owner":"user","title":"Tema","body":{"serbest":1}}]}""");
        Assert.Empty(res.Rejected);
        Assert.Single(res.Entries);
        Assert.Equal("theme:x", res.Entries[0].Id);
        Assert.Contains(res.Warnings, w => w.Code == "unknown-kind" && w.Id == "hologram:x");
        Assert.Contains(res.Warnings, w => w.Code == "unused-kind" && w.Id == "theme:x" && w.Message.Contains("henüz kullanılmayan"));
        Assert.Contains(res.Entries[0].Problems, p => p.Contains("henüz kullanılmayan"));
        // kişi dosyasında yalnız uyarı: denetim geçer
        Assert.True(RegistryChecker.Check(res).Ok);
        // sabit pakette bilinmeyen tür hatadır
        var fx = RegistryLoader.Load(new[] { Source("sabit.soz.json", """{"schemaVersion":1,"entries":[{"id":"hologram:x","kind":"hologram","owner":"fixed","title":"x"}]}""") }, Array.Empty<RegistrySource>());
        Assert.False(RegistryChecker.Check(fx).Ok);
    }

    [Fact]
    public void BozukDosya_JsonHatasi_ve_SurumHatasi_DigerDosyalariEtkilemez()
    {
        var res = RegistryLoader.Load(Array.Empty<RegistrySource>(), new[]
        {
            Source("bozuk.soz.json", "{ bu json degil"),
            Source("surum.soz.json", """{"schemaVersion":7,"entries":[]}"""),
            Source("liste.soz.json", """{"schemaVersion":1,"entries":{}}"""),
            Source("tamam.soz.json", SettingFile("setting:o.d")),
        });
        Assert.Single(res.Entries);
        Assert.Contains(res.Rejected, p => p.Source == "bozuk.soz.json" && p.Code == "json");
        Assert.Contains(res.Rejected, p => p.Source == "surum.soz.json" && p.Message.Contains("desteklenmiyor"));
        Assert.Contains(res.Rejected, p => p.Source == "liste.soz.json" && p.Message.Contains("entries bir dizi olmalı"));
    }
}
