using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Soz.Core.Agents;
using Soz.Core.Hooks;
using Soz.Core.Registry;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Kanca gecikmesi düzeltmesi (etiket kt2). Üç hafifletme de davranışı korumalı; her biri eski yolla yan yana karşılaştırılır:
/// A) kanca yolunda bağlayıcı kaydının hafif okunması = tam kayıt defteri yüklemesi; B) nabız yükü eski serileştirici çıktısıyla bayt bayt aynı;
/// C) TextNormalizer'ın üretilmiş düzenli ifadeleri eski yorumlayıcı ifadeleriyle aynı sonucu verir.
/// </summary>
public class KancaGecikmeKt2Tests
{
    // ---------- A: HookSessionTool hafif okuma ----------

    private static JsonObject Conn(string id, string? state = "enabled", string[]? depth = null, string? protocol = "dialect", string? dialect = "claude-stream-json",
        string? beforeTool = "native-hook", bool caps = true, string owner = "fixed", bool title = true)
    {
        var body = new JsonObject
        {
            ["depth"] = new JsonArray((depth ?? new[] { "observe", "run" }).Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
            ["detect"] = new JsonObject { ["commands"] = new JsonArray(id.Replace("connector:", "")) },
        };
        if (protocol is not null)
        {
            var launch = new JsonObject { ["protocol"] = protocol };
            if (dialect is not null) launch["dialect"] = dialect;
            body["launch"] = launch;
        }
        if (caps) body["capabilities"] = new JsonObject { ["headless"] = true, ["beforeTool"] = beforeTool };
        var e = new JsonObject { ["id"] = id, ["kind"] = "connector", ["owner"] = owner };
        if (title) e["title"] = id;
        if (state is not null) e["state"] = state;
        e["body"] = body;
        return e;
    }

    private static RegistrySource File(string name, params JsonObject[] entries)
        => RegistrySource.FromText(name, "core", new JsonObject { ["schemaVersion"] = 1, ["entries"] = new JsonArray(entries.Select(x => (JsonNode)x.DeepClone()).ToArray()) }.ToJsonString());

    /// <summary>Eski (tam) yol: kayıt defterini şema doğrulamasıyla yükle, bağlayıcı listesini kur, seç. Çalıştırılabilir bağlayıcı yoksa atar.</summary>
    private static string Full(IReadOnlyList<RegistrySource> sources)
    {
        var load = RegistryLoader.Load(sources, Array.Empty<RegistrySource>());
        var list = AgentRegistry.RunConnectorsFrom(AgentRegistry.DefinitionsFrom(load));
        return AgentRegistry.HookToolFull(list);
    }

    private static string? FullOrNull(IReadOnlyList<RegistrySource> sources)
    {
        try { return Full(sources); } catch (InvalidOperationException) { return null; }
    }

    /// <summary>Üretimdeki seçim: hafif okuma, "bilmiyorum" derse tam yol.</summary>
    private static string? Effective(IReadOnlyList<RegistrySource> sources)
    {
        var light = AgentRegistry.HookToolLight(sources);
        if (light is not null) return light;
        return FullOrNull(sources);
    }

    [Fact]
    public void A_GomuluKayit_HafifOkuma_TamYuklemeyleAyni()
    {
        var real = RegistryLoader.EmbeddedSources();
        var full = Full(real);
        Assert.Equal("claude", full);
        Assert.Equal(full, AgentRegistry.HookToolLight(real)); // hafif yol gömülü kayıtta kendisi karar verir (tam yola düşmez)
        Assert.Equal(full, AgentRegistry.HookSessionTool);
        Assert.Equal(AgentRegistry.Connectors.First(c => c.Capabilities.BeforeTool == GuardTier.NativeHook).SessionTool, AgentRegistry.HookSessionTool);
    }

    public static IEnumerable<object[]> Scenarios()
    {
        var claude = Conn("connector:claude");
        var codex = Conn("connector:codex", dialect: "codex-exec-json", beforeTool: "os-sandbox");
        yield return new object[] { "claude+codex", new[] { File("a.soz.json", claude, codex) } };
        yield return new object[] { "codex önce, claude sonra (kancalı olan kazanır)", new[] { File("a.soz.json", codex, claude) } };
        yield return new object[] { "yalnız codex (kancalı yok: ilk çalıştırılabilir)", new[] { File("a.soz.json", codex) } };
        yield return new object[] { "iki dosya, ada göre sıra", new[] { File("b.soz.json", claude), File("a.soz.json", codex) } };
        yield return new object[] { "claude kapalı (disabled)", new[] { File("a.soz.json", Conn("connector:claude", state: "disabled"), codex) } };
        yield return new object[] { "claude öneri (suggested)", new[] { File("a.soz.json", Conn("connector:claude", state: "suggested"), codex) } };
        yield return new object[] { "claude kullanımdan kalkmış (deprecated) hâlâ etkin", new[] { File("a.soz.json", Conn("connector:claude", state: "deprecated"), codex) } };
        yield return new object[] { "state yok = etkin", new[] { File("a.soz.json", Conn("connector:claude", state: null)) } };
        yield return new object[] { "bilinmeyen lehçe atlanır", new[] { File("a.soz.json", Conn("connector:yeni", dialect: "bilinmeyen-lehce"), codex) } };
        yield return new object[] { "yalnız gözlenen atlanır", new[] { File("a.soz.json", Conn("connector:hermes", depth: new[] { "observe" }, protocol: null, caps: false), codex) } };
        yield return new object[] { "capabilities yok atlanır", new[] { File("a.soz.json", Conn("connector:claude", caps: false), codex) } };
        yield return new object[] { "protocol acp atlanır", new[] { File("a.soz.json", Conn("connector:yeni", protocol: "acp", dialect: null), codex) } };
        yield return new object[] { "beforeTool none: ilk çalıştırılabilir", new[] { File("a.soz.json", Conn("connector:claude", beforeTool: "none"), codex) } };
        yield return new object[] { "çalıştırılabilir bağlayıcı yok", new[] { File("a.soz.json", Conn("connector:hermes", depth: new[] { "observe" }, protocol: null, caps: false)) } };
        yield return new object[] { "hiç bağlayıcı yok", new[] { RegistrySource.FromText("a.soz.json", "core", """{"schemaVersion":1,"entries":[]}""") } };
        // Hafif okumanın "bilmiyorum" deyip tam yola düştüğü bozuk kümeler:
        yield return new object[] { "başlıksız (şemaya aykırı) bağlayıcı tam yolda reddedilir", new[] { File("a.soz.json", Conn("connector:claude", title: false), codex) } };
        yield return new object[] { "owner=user dosyada reddedilir", new[] { File("a.soz.json", Conn("connector:claude", owner: "user"), codex) } };
        yield return new object[] { "tekrarlı kimlik: ikincisi reddedilir", new[] { File("a.soz.json", Conn("connector:codex", dialect: "codex-exec-json", beforeTool: "none"), Conn("connector:codex", beforeTool: "native-hook")) } };
        yield return new object[] { "state=broken", new[] { File("a.soz.json", Conn("connector:claude", state: "broken"), codex) } };
        yield return new object[] { "bilinmeyen state", new[] { File("a.soz.json", Conn("connector:claude", state: "calisiyor"), codex) } };
        yield return new object[] { "geçersiz kimlik biçimi", new[] { File("a.soz.json", Conn("connector:Claude"), codex) } };
        yield return new object[] { "kimlik öneki türle uyuşmuyor", new[] { File("a.soz.json", Conn("setting:claude"), codex) } };
        yield return new object[] { "bozuk JSON dosyası", new[] { RegistrySource.FromText("a.soz.json", "core", """{"schemaVersion":1,"entries":[{"id":"connector:claude","kind":"connector" connector"""), File("b.soz.json", codex) } };
        yield return new object[] { "tanınmayan şema sürümü", new[] { RegistrySource.FromText("a.soz.json", "core", """{"schemaVersion":2,"entries":[{"id":"connector:claude","kind":"connector"}]}"""), File("b.soz.json", codex) } };
        yield return new object[] { "yinelenen alan (aynı nesnede iki kez)", new[] { RegistrySource.FromText("a.soz.json", "core", """{"schemaVersion":1,"entries":[],"entries":[],"not":"connector"}"""), File("b.soz.json", codex) } };
        yield return new object[] { "bilinmeyen tür bağlayıcı sayılmaz", new[] { File("a.soz.json", new JsonObject { ["id"] = "tema:claude", ["kind"] = "tema", ["owner"] = "fixed", ["title"] = "x", ["body"] = new JsonObject() }, codex) } };
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void A_HafifOkuma_Ya_TamYolaEsit_Ya_BilmiyorumDer(string ad, RegistrySource[] sources)
    {
        var full = FullOrNull(sources);
        var light = AgentRegistry.HookToolLight(sources);
        if (light is not null) Assert.True(full == light, $"{ad}: hafif {light}, tam {full ?? "(hata)"}");
        Assert.Equal(full, Effective(sources)); // hafif "bilmiyorum" derse tam yol karar verir: sonuç ve hata aynı
        // Çalıştırılabilir bağlayıcı yoksa eski davranış (sade hata) korunur: Full atar, Effective null verir.
        if (full is null) Assert.Throws<InvalidOperationException>(() => Full(sources));
    }

    [Fact]
    public void A_HafifOkuma_BomluDosyaVeKisiKaydi()
    {
        var withBom = new RegistrySource("a.soz.json", "core", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(File("x", Conn("connector:claude")).Bytes).ToArray());
        Assert.Equal(Full(new[] { withBom }), AgentRegistry.HookToolLight(new[] { withBom }));
        // Kişi klasöründen gelen (owner=user) bağlayıcı tam yolda onaysız etkin olamaz; hafif okuma da onu görmez, karar gömülü kayda göre kalır.
        var real = RegistryLoader.EmbeddedSources().Append(File("kisi.soz.json", Conn("connector:benim", owner: "user", dialect: "codex-exec-json", beforeTool: "native-hook"))).ToList();
        Assert.Equal("claude", Full(RegistryLoader.EmbeddedSources().ToList()));
        Assert.Null(AgentRegistry.HookToolLight(real)); // owner=user: hafif yol karar vermez, tam yol (Loaded) gömülü kayıtla kalır
    }

    [Fact]
    public void A_KancaYolu_TamKayitDefteriniYuklemeden_AyniOturumAdini_Verir()
    {
        // HookSessionTool, gömülü kayıtla yüklenen bağlayıcı listesindeki seçimle aynıdır
        Assert.Equal(AgentRegistry.Connectors.First(c => c.Capabilities.BeforeTool == GuardTier.NativeHook).SessionTool, AgentRegistry.HookSessionTool);
        Assert.Equal("claude", AgentRegistry.HookSessionTool);
    }

    // ---------- B: nabız yükü ----------

    private static string OldPulse(string name, long ms, string result, string? error)
        => JsonSerializer.Serialize(new { name, ms, result, error });

    public static IEnumerable<object?[]> PulseSamples()
    {
        yield return new object?[] { "user-prompt-submit", 123L, "ok", null };
        yield return new object?[] { "stop", 0L, "ok", null };
        yield return new object?[] { "pre-tool-use", 9_999_999_999L, "hata", "InvalidOperationException: bozuk" };
        yield return new object?[] { "notification", -5L, "zaman aşımı", "SqliteException: database is locked" };
        yield return new object?[] { "session-start", 17L, "hata", "Türkçe: ığüşöçİĞÜŞÖÇ ve ‘eğri’ “tırnak” …" };
        yield return new object?[] { "stop", 1L, "hata", "tırnak \" ters \\ bölü / eğik <tag> & 'tek' + \n yeni\tsatır \r dönüş \u0001 \u001f \u007f" };
        yield return new object?[] { "stop", 1L, "hata", "" };
        yield return new object?[] { "", 1L, "", "" };
        yield return new object?[] { "stop", 1L, "hata", "   boşluk   " };
        yield return new object?[] { "stop", 1L, "hata", "C:\\Users\\kemal\\proje\\dosya.txt: \u2028\u2029 \u00A0 \ud83d\ude00 emoji" };
        yield return new object?[] { "stop", 1L, "hata", new string('ş', 300) };
        yield return new object?[] { "stop", long.MaxValue, "ok", null };
        yield return new object?[] { "stop", long.MinValue, "ok", null };
    }

    [Theory]
    [MemberData(nameof(PulseSamples))]
    public void B_NabizYuku_EskiSerilestiriciyleBaytBaytAyni(string name, long ms, string result, string? error)
    {
        var old = OldPulse(name, ms, result, error);
        var now = HookHandler.PulsePayload(name, ms, result, error);
        Assert.Equal(old, now);
        Assert.Equal(Encoding.UTF8.GetBytes(old), Encoding.UTF8.GetBytes(now));
        // geri okunur ve değerler korunur
        using var doc = JsonDocument.Parse(now);
        Assert.Equal(name, doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(ms, doc.RootElement.GetProperty("ms").GetInt64());
        Assert.Equal(error, doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void B_NabizYuku_TekBasinaYarimSurrogate_EskiSerilestiriciyleAyniDavranir()
    {
        // Short() 300. karakterde keserse bir vekil çifti ikiye bölünebilir; eski yol ne yapıyorsa (yazar ya da atar) yeni yol da aynısını yapar.
        var s = new string('x', 298) + "\ud83d\ude00";
        var cut = s[..299]; // yarım vekil
        string? oldOut = null, newOut = null;
        Exception? oldEx = null, newEx = null;
        try { oldOut = OldPulse("stop", 1, "hata", cut); } catch (Exception e) { oldEx = e; }
        try { newOut = HookHandler.PulsePayload("stop", 1, "hata", cut); } catch (Exception e) { newEx = e; }
        Assert.Equal(oldEx?.GetType(), newEx?.GetType());
        Assert.Equal(oldOut, newOut);
    }
}

/// <summary>C: TextNormalizer'ın [GeneratedRegex] ifadeleri, eski (RegexOptions.Compiled'sız) kalıp ve seçeneklerle aynı sonucu verir.</summary>
public class KancaGecikmeKt2NormalizerTests
{
    private const string InjectedTags = @"task-notification|system-reminder|local-command-[\w-]+|command-name|command-message|command-args|bash-input|bash-stdout|bash-stderr|agent-message[\w-]*|teammate-message|cross-session[\w-]*|user-prompt-submit-hook|session-start-hook";
    private const RegexOptions Ci = RegexOptions.CultureInvariant;

    private static readonly Regex DropBlocks = new(@"<(system-reminder|environment_context|user_instructions|codex_internal_context|in-app-browser-context|external_codex_apps_open_page|local-command-stdout|local-command-stderr|bash-stdout|bash-stderr)\b[^>]*>[\s\S]*?</\1\s*>", Ci);
    private static readonly Regex InjectedBlocks = new(@"<(" + InjectedTags + @")\b[^>]*>[\s\S]*?</\1\s*>", Ci);
    private static readonly Regex InjectedSelfClosing = new(@"<(" + InjectedTags + @")\b[^>]*/>", Ci);
    private static readonly Regex InjectedUnclosed = new(@"(?m)^[ \t]*<(" + InjectedTags + @")\b[^>]*>[\s\S]*$", Ci);
    private static readonly Regex SystemNotice = new(@"(?m)^[ \t]*\[SYSTEM NOTIFICATION[^\n]*(?:\n(?![ \t]*\n)[^\n]*)*", Ci);
    private static readonly Regex WrapperTags = new(@"</?(realtime_delegation|send_user_message_question_reply|command-message|command-args|command-name|bash-input)\b[^>]*>", Ci);
    private static readonly Regex PastedBlock = new(@"<pasted_content\b[^>]*>[\s\S]*?</pasted_content[^>]*>");
    private static readonly Regex HSpace = new(@"[ \t\f\v ]+");
    private static readonly Regex ManyNewlines = new(@"\n{3,}");
    private static readonly Regex SlashCommand = new(@"^/[A-Za-z][\w:.-]*(\s|$)");

    /// <summary>Eski gövde (kt2 öncesi TextNormalizer.CleanUserText), yalnız ifadeler yorumlayıcı.</summary>
    private static string OldClean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (s.TrimStart().StartsWith("[SYSTEM NOTIFICATION", StringComparison.Ordinal)) return "";
        s = DropBlocks.Replace(s, "");
        s = InjectedBlocks.Replace(s, "");
        s = InjectedSelfClosing.Replace(s, "");
        s = InjectedUnclosed.Replace(s, "");
        s = SystemNotice.Replace(s, "");
        s = WrapperTags.Replace(s, "");
        s = HSpace.Replace(s, " ");
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].Trim();
        s = string.Join('\n', lines);
        s = ManyNewlines.Replace(s, "\n\n");
        return s.Trim();
    }

    private static IEnumerable<string> Corpus()
    {
        var fixedSamples = new[]
        {
            "", " ", "merhaba", "Lütfen testleri çalıştır.", "İĞÜŞÖÇ ığüşöç  çok   boşluk\t\ttab nbsp",
            "<system-reminder>gizli\nsatır</system-reminder>kullanıcı sözü", "<system-reminder>kapanmamış\nikinci satır",
            "ilk\n<system-reminder>kapanmamış\nikinci", "<local-command-stdout>x</local-command-stdout>ok", "<local-command-foo-1>x</local-command-foo-1>ok",
            "<command-name>/hafta</command-name><command-message>x</command-message><command-args>a b</command-args>", "<bash-input>ls</bash-input>",
            "<task-notification a='b'/>sade", "<cross-session-x>z</cross-session-x>", "<agent-message-1>m</agent-message-1> kalan",
            "[SYSTEM NOTIFICATION kanca] bu satır\ndevam satırı\n\nbağımsız paragraf", "  [SYSTEM NOTIFICATION baştan sona",
            "önce\n[SYSTEM NOTIFICATION x\ndevam\n\nsonra", "<realtime_delegation>içerik kalır</realtime_delegation>",
            "<send_user_message_question_reply x=1>cevap</send_user_message_question_reply>", "a\n\n\n\n\nb", "a\r\n\r\n\r\nb\rc",
            "<pasted_content id=1>yapıştı\nçok</pasted_content>kalan", "<user-prompt-submit-hook>h</user-prompt-submit-hook>t", "<SYSTEM-REMINDER>büyük</SYSTEM-REMINDER>",
            "<system-reminder>iç <system-reminder>iç iç</system-reminder> dış</system-reminder> son", "<in-app-browser-context>b</in-app-browser-context>",
            "<environment_context>cwd</environment_context>\n<user_instructions>u</user_instructions>sözüm",
        };
        foreach (var s in fixedSamples) yield return s;
        var frags = new[]
        {
            "<system-reminder>", "</system-reminder>", "<local-command-stdout>", "</local-command-stdout>", "<bash-input>", "</bash-input>", "<command-name>", "</command-name>",
            "[SYSTEM NOTIFICATION ", "<pasted_content>", "</pasted_content>", "<task-notification/>", "\n", "\n\n", "\n\n\n", "\r\n", " ", "\t", " ", "\f", "\v",
            "kelime", "Türkçe İ ı ğ", "/hafta", "/compact ", "<", ">", "/", "<a>", "x", "<realtime_delegation>", "</realtime_delegation>",
        };
        var rnd = new Random(20261111);
        for (var i = 0; i < 4000; i++)
        {
            var sb = new StringBuilder();
            var n = rnd.Next(1, 14);
            for (var j = 0; j < n; j++) sb.Append(frags[rnd.Next(frags.Length)]);
            yield return sb.ToString();
        }
    }

    [Fact]
    public void C_CleanUserText_EskiYorumlayiciIfadelerleAyni()
    {
        var count = 0;
        foreach (var s in Corpus())
        {
            Assert.Equal(OldClean(s), Soz.Core.Text.TextNormalizer.CleanUserText(s));
            count++;
        }
        Assert.True(count > 4000);
    }

    [Fact]
    public void C_RemovePasted_IsSlashCommand_Hash_EskiyleAyni()
    {
        foreach (var s in Corpus())
        {
            Assert.Equal(PastedBlock.Replace(s, "\n"), Soz.Core.Text.TextNormalizer.RemovePasted(s));
            Assert.Equal(SlashCommand.IsMatch(s), Soz.Core.Text.TextNormalizer.IsSlashCommand(s));
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(OldClean(s)))).ToLowerInvariant(), Soz.Core.Text.TextNormalizer.Hash(s));
        }
    }
}

/// <summary>
/// Ölçüm verisi hazırlayıcı (test değil): SOZ_KT2_HAZIRLA ortam değişkeni doluysa o klasörde sentetik bir veri kümesi kurar
/// (örnek oturumlar içe aktarılır, bütün öneriler onaylanır, açık bir iş verilir). Yoksa hiçbir şey yapmaz.
/// Söz'ün "Claude oturumundan geldi" korumasına dokunmaz: doğrudan çekirdek yardımcılarıyla çalışır.
/// </summary>
public class KancaGecikmeKt2Hazirlik
{
    [Fact]
    public void Hazirla_OlcumVerisi_YalnizOrtamDegiskeniVarsa()
    {
        var target = Environment.GetEnvironmentVariable("SOZ_KT2_HAZIRLA");
        if (string.IsNullOrWhiteSpace(target)) return;
        Directory.CreateDirectory(target);
        var data = Path.Combine(target, "veri");
        var claudeRoot = Path.Combine(target, "claude", "projects");
        var codexRoot = Path.Combine(target, "codex", "sessions");
        TestEnv.CopyDir(TestEnv.SampleDir, target);
        Directory.CreateDirectory(claudeRoot);
        Directory.CreateDirectory(codexRoot);
        var svc = Soz.Core.SozService.Open(data);
        svc.Import(new Soz.Core.ImportOptions { ClaudeRoot = claudeRoot, CodexRoot = codexRoot });
        foreach (var d in svc.Decisions(status: "proposed")) svc.PatchDecision(d.Id, status: "approved");
        var proje = Path.Combine(target, "proje");
        Directory.CreateDirectory(proje);
        var w = new Soz.Core.Jobs.WorkService(svc.Db);
        var pid = w.EnsureProjectByPath(proje);
        var job = w.CreateJob(pid, "Hafif kanca ölçümü için açık iş.");
        w.PatchJob(job.Id, Soz.Core.Jobs.JobStates.Given);
        File.WriteAllText(Path.Combine(target, "is-istemi.txt"), job.PromptText);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}

/// <summary>Notification kancasının canlı olay yükü: eski anonim tür + Web varsayılanlı serileştirmeyle bayt bayt aynı.</summary>
public class KancaGecikmeKt2CanliOlayTests
{
    [Theory]
    [InlineData(1L, "idle_prompt")]
    [InlineData(123456789012L, "permission_prompt")]
    [InlineData(0L, "notification")]
    [InlineData(-3L, "")]
    [InlineData(7L, "Türkçe ığüşöçİĞÜŞÖÇ ‘tırnak’ \"çift\" 'tek' \\ ters / eğik <tag> & + \n \t \u0001")]
    [InlineData(7L, "emoji sade")]
    public void NeedsUserPayload_EskiSerilestiriciyleBaytBaytAyni(long sid, string reason)
    {
        var old = System.Text.Json.JsonSerializer.Serialize(new { sessionId = sid, needsUser = true, reason }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var now = GateHooks.NeedsUserPayload(sid, reason);
        Assert.Equal(old, now);
        Assert.Equal(Encoding.UTF8.GetBytes(old), Encoding.UTF8.GetBytes(now));
    }
}

public class KancaGecikmeKt2CanliOlayOzelTests
{
    [Fact]
    public void NeedsUserPayload_VekilCifti_Ve_SatirAyiricilar_EskiSerilestiriciyleAyni()
    {
        var reason = "emoji " + new string(new[] { (char)0xD83D, (char)0xDE00, (char)0x2028, (char)0x2029, (char)0xA0 }) + " son";
        var old = System.Text.Json.JsonSerializer.Serialize(new { sessionId = 7L, needsUser = true, reason }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(old, GateHooks.NeedsUserPayload(7L, reason));
    }
}
