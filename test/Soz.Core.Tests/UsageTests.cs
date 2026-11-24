using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Usage;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

public class UsageTests
{
    private static string AsstLine(string id, string model, int i, int o, int cr, int cw, string ts, string block = "text", bool sidechain = false, string? requestId = "req_1")
        => "{\"parentUuid\":\"p\",\"isSidechain\":" + (sidechain ? "true" : "false") + ",\"type\":\"assistant\",\"requestId\":\"" + requestId + "\",\"message\":{\"model\":\"" + model + "\",\"id\":\"" + id + "\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"" + block + "\"" + (block == "text" ? ",\"text\":\"merhaba \\\"usage\\\":{x}\"" : "") + "}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":" + i + ",\"cache_creation_input_tokens\":" + cw + ",\"cache_read_input_tokens\":" + cr + ",\"output_tokens\":" + o + ",\"service_tier\":\"standard\",\"cache_creation\":{\"ephemeral_5m_input_tokens\":" + cw + "}}},\"uuid\":\"u\",\"timestamp\":\"" + ts + "\",\"cwd\":\"E:\\\\p\",\"sessionId\":\"s\"}";

    private static string Root(TestEnv env, string slug = "E--p") { var d = Path.Combine(env.ClaudeRoot, slug); Directory.CreateDirectory(d); return d; }

    private static JsonNode Usage(TestEnv env, int days = 30) => JsonSerializer(UsageQuery.Build(env.Svc.Db, days));
    private static JsonNode JsonSerializer(object o) => System.Text.Json.JsonSerializer.SerializeToNode(o, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    [Fact]
    public void Usage_daily_tekil_v3_kopyalari_v4_gocunde_toplanir_ve_ikinci_satir_reddedilir()
    {
        using var env = new TestEnv(copySamples: false);
        using (var c = env.Svc.Db.Open())
        {
            // v3 veritabanını taklit et: dizini kaldır, aynı anahtarla iki satır yaz, sürümü 3'e indir.
            c.Exec("DROP INDEX IF EXISTS ux_usage_daily");
            c.Exec("INSERT INTO usage_daily VALUES('2026-11-01','claude','m',0,1,10,20,30,40)");
            c.Exec("INSERT INTO usage_daily VALUES('2026-11-01','claude','m',0,2,1,2,3,4)");
            c.Exec("INSERT INTO usage_daily VALUES('2026-11-01','claude','m',1,5,5,5,5,5)");
            c.Exec("PRAGMA user_version=3");
        }
        env.Svc.Db.Migrate();
        using (var c = env.Svc.Db.Open())
        {
            Assert.Equal((long)SozDb.CurrentVersion, c.ScalarLong("PRAGMA user_version"));
            Assert.Equal(2L, c.ScalarLong("SELECT COUNT(*) FROM usage_daily"));
            Assert.Equal(3L, c.ScalarLong("SELECT messages FROM usage_daily WHERE is_subagent=0"));
            Assert.Equal(44L, c.ScalarLong("SELECT cache_write_tokens FROM usage_daily WHERE is_subagent=0"));
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => c.Exec("INSERT INTO usage_daily VALUES('2026-11-01','claude','m',0,1,1,1,1,1)"));
        }
    }

    [Fact]
    public void Yinelenen_satirlar_iki_kez_sayilmaz_alt_ajan_ayri_ve_artimli_birikim_dogru()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Root(env);
        var main = Path.Combine(dir, "s.jsonl");
        // msg_A: 3 satır (thinking/text/tool_use) AYNI usage; msg_B: 1 satır; <synthetic> sayılmaz
        File.WriteAllLines(main, new[]
        {
            AsstLine("msg_A", "claude-opus-5-5", 10, 100, 1000, 50, "2026-11-09T10:00:00.000Z", "thinking"),
            AsstLine("msg_A", "claude-opus-5-5", 10, 100, 1000, 50, "2026-11-09T10:00:01.000Z", "text"),
            AsstLine("msg_A", "claude-opus-5-5", 10, 100, 1000, 50, "2026-11-09T10:00:02.000Z", "tool_use"),
            AsstLine("msg_B", "claude-sonnet-5-5", 5, 20, 500, 0, "2026-11-09T11:00:00.000Z"),
            AsstLine("msg_S", "<synthetic>", 0, 0, 0, 0, "2026-11-09T11:00:00.000Z"),
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"merhaba\"},\"timestamp\":\"2026-11-09T09:59:00.000Z\"}",
        });
        var sub = Path.Combine(dir, "s", "subagents");
        Directory.CreateDirectory(sub);
        File.WriteAllLines(Path.Combine(sub, "agent-a1.jsonl"), new[]
        {
            AsstLine("msg_C", "claude-haiku-5-5", 3, 30, 300, 10, "2026-11-09T12:00:00.000Z", sidechain: true),
            AsstLine("msg_C", "claude-haiku-5-5", 3, 30, 300, 10, "2026-11-09T12:00:01.000Z", "tool_use", sidechain: true),
        });
        var r1 = UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        Assert.Equal(3, r1.NewMessages);
        Assert.True(r1.DuplicateLines >= 3);
        var u = Usage(env);
        Assert.Equal(3, (int)u["totals"]!["messages"]!);
        Assert.Equal(18, (int)u["totals"]!["inputTokens"]!);
        Assert.Equal(150, (int)u["totals"]!["outputTokens"]!);
        Assert.Equal(1800, (int)u["totals"]!["cacheReadTokens"]!);
        Assert.Equal(60, (int)u["totals"]!["cacheWriteTokens"]!);
        // alt ajan: yalnız msg_C
        Assert.Equal(1, (int)u["subagent"]!["tokens"]!["messages"]!);
        Assert.Equal(30, (int)u["subagent"]!["tokens"]!["outputTokens"]!);
        // fresh = in+out+cw: alt ajan 43 / toplam 18+150+60=228 → %18,9
        Assert.Equal(18.9, (double)u["subagent"]!["sharePercent"]!);
        // aynı dosya yeniden okunursa şişmez (değişmemiş → atlanır)
        var r2 = UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        Assert.Equal(0, r2.NewMessages);
        Assert.Equal(2, r2.FilesSkippedUnchanged);
        Assert.Equal(3, (int)Usage(env)["totals"]!["messages"]!);
        Assert.Equal(150, (int)Usage(env)["totals"]!["outputTokens"]!);
    }

    [Fact]
    public void Dosya_yeniden_yazilsa_ya_da_baska_dosyaya_kopyalansa_sismez_artimli_ekleme_yalniz_yeniyi_sayar()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Root(env);
        var f = Path.Combine(dir, "s.jsonl");
        File.WriteAllLines(f, new[] { AsstLine("msg_A", "claude-opus-5-5", 1, 10, 100, 5, "2026-11-09T10:00:00.000Z") });
        UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        // oturum çatallandı: aynı satır başka dosyaya kopyalandı + yeni mesaj eklendi
        File.WriteAllLines(Path.Combine(dir, "fork.jsonl"), new[] { AsstLine("msg_A", "claude-opus-5-5", 1, 10, 100, 5, "2026-11-09T10:00:00.000Z"), AsstLine("msg_N", "claude-opus-5-5", 2, 20, 200, 0, "2026-11-09T10:05:00.000Z") });
        // ilk dosyaya ekleme
        File.AppendAllText(f, AsstLine("msg_M", "claude-opus-5-5", 4, 40, 400, 0, "2026-11-09T10:06:00.000Z") + "\n");
        var r = UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        Assert.Equal(2, r.NewMessages); // msg_N ve msg_M; msg_A yinelenmedi
        var u = Usage(env);
        Assert.Equal(3, (int)u["totals"]!["messages"]!);
        Assert.Equal(70, (int)u["totals"]!["outputTokens"]!);
        // dosya aynı içerikle yeniden yazıldı (mtime değişti, offset geçersiz): yine şişmez
        var all = File.ReadAllLines(f);
        File.WriteAllLines(f, all);
        File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(5));
        UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        Assert.Equal(70, (int)Usage(env)["totals"]!["outputTokens"]!);
    }

    [Fact]
    public void Ayni_mesaj_sonradan_daha_buyuk_cikis_gelirse_yalniz_fark_eklenir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Root(env);
        var f = Path.Combine(dir, "s.jsonl");
        File.WriteAllLines(f, new[] { AsstLine("msg_A", "claude-opus-5-5", 1, 10, 100, 5, "2026-11-09T10:00:00.000Z", "thinking") });
        UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        File.AppendAllText(f, AsstLine("msg_A", "claude-opus-5-5", 1, 90, 100, 5, "2026-11-09T10:00:03.000Z", "text") + "\n");
        UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        var u = Usage(env);
        Assert.Equal(1, (int)u["totals"]!["messages"]!);
        Assert.Equal(90, (int)u["totals"]!["outputTokens"]!);
    }

    [Fact]
    public void Bozuk_ve_usage_olmayan_satirlar_cokmez_kimliksiz_mesaj_sayilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Root(env);
        var noId = "{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-5-5\",\"usage\":{\"input_tokens\":1,\"output_tokens\":7,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}},\"timestamp\":\"2026-11-09T10:00:00.000Z\"}";
        File.WriteAllLines(Path.Combine(dir, "s.jsonl"), new[] { "{bozuk \"usage\":{", noId, "{\"type\":\"assistant\",\"message\":{\"content\":[]}}" });
        var r = UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        Assert.Equal(1, r.NewMessages);
        Assert.Equal(1, r.NoIdMessages);
        Assert.True(r.BadLines >= 1);
    }

    [Fact]
    public void Codex_token_olaylari_okunur_ve_yinelenen_toplam_sayilmaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.CodexRoot, "2026", "11", "09");
        Directory.CreateDirectory(dir);
        string Tok(string ts, int totalIn, int lastIn, int cached, int lastOut)
            => "{\"timestamp\":\"" + ts + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":" + totalIn + ",\"cached_input_tokens\":0,\"output_tokens\":0,\"total_tokens\":" + (totalIn + lastOut) + "},\"last_token_usage\":{\"input_tokens\":" + lastIn + ",\"cached_input_tokens\":" + cached + ",\"cache_write_input_tokens\":0,\"output_tokens\":" + lastOut + ",\"total_tokens\":" + (lastIn + lastOut) + "}}}}";
        var f = Path.Combine(dir, "rollout-2026-11-09T10-00-00-cccccccc-0000-7000-8000-0000000000aa.jsonl");
        File.WriteAllLines(f, new[]
        {
            "{\"timestamp\":\"2026-11-09T10:00:00.000Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"cccccccc-aa\",\"cwd\":\"E:\\\\p\",\"originator\":\"codex_cli\"}}",
            "{\"timestamp\":\"2026-11-09T10:00:01.000Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6.1-terra\"}}",
            "{\"timestamp\":\"2026-11-09T10:00:02.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null}}",
            Tok("2026-11-09T10:00:03.000Z", 1000, 1000, 800, 50),
            Tok("2026-11-09T10:00:03.500Z", 1000, 1000, 800, 50), // aynı olay yinelendi
            Tok("2026-11-09T10:00:10.000Z", 2500, 1500, 1200, 70),
        });
        var r = UsageIngest.Run(env.Svc.Db, null, env.CodexRoot, null);
        Assert.True(r.CodexTokenEvents);
        Assert.Equal(2, r.NewMessages);
        var u = Usage(env);
        Assert.Equal(2, (int)u["totals"]!["messages"]!);
        Assert.Equal(200 + 300, (int)u["totals"]!["inputTokens"]!); // giriş - önbellekli
        Assert.Equal(2000, (int)u["totals"]!["cacheReadTokens"]!);
        Assert.Equal(120, (int)u["totals"]!["outputTokens"]!);
        Assert.Equal("gpt-6.1-terra", (string?)u["byModel"]![0]!["model"]);
        // artımlı: yeni olay eklenir, model bilgisi veritabanından devralınır
        File.AppendAllText(f, Tok("2026-11-09T10:01:00.000Z", 3000, 500, 400, 10) + "\n");
        File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(1));
        UsageIngest.Run(env.Svc.Db, null, env.CodexRoot, null);
        var u2 = Usage(env);
        Assert.Equal(3, (int)u2["totals"]!["messages"]!);
        Assert.Equal("gpt-6.1-terra", (string?)u2["byModel"]![0]!["model"]);
    }

    [Fact]
    public void Fiyat_girilmediyse_tutar_null_ve_fiyat_girilmedi_yazar_girilince_hesaplanir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Root(env);
        File.WriteAllLines(Path.Combine(dir, "s.jsonl"), new[] { AsstLine("msg_A", "claude-opus-5-5", 1000000, 1000000, 1000000, 1000000, "2026-11-09T10:00:00.000Z") });
        UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        var u = Usage(env);
        Assert.Null(u["estimatedCostUsd"]);
        Assert.Equal("fiyat girilmedi", (string?)u["pricing"]!["note"]);
        Assert.False((bool)u["pricing"]!["configured"]!);
        env.Svc.PutSettings(JsonNode.Parse("{\"prices\":{\"opus\":{\"input\":1,\"output\":2,\"cacheRead\":0.5,\"cacheWrite\":3}}}")!.AsObject());
        var u2 = Usage(env);
        Assert.Equal(6.5, (double)u2["estimatedCostUsd"]!);
        Assert.Contains("gerçek fatura DEĞİLDİR", (string?)u2["pricing"]!["disclaimer"]);
        Assert.Throws<SozException>(() => env.Svc.PutSettings(JsonNode.Parse("{\"prices\":{\"x\":{\"input\":-1}}}")!.AsObject()));
    }

    [Fact]
    public void Gun_penceresi_days_disindakileri_almaz()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Root(env);
        File.WriteAllLines(Path.Combine(dir, "s.jsonl"), new[]
        {
            AsstLine("msg_old", "claude-opus-5-5", 1, 5, 0, 0, "2026-09-01T10:00:00.000Z"),
            AsstLine("msg_new", "claude-opus-5-5", 1, 7, 0, 0, "2026-11-10T01:00:00.000Z"),
        });
        UsageIngest.Run(env.Svc.Db, env.ClaudeRoot, null, null);
        Assert.Equal(7, (int)Usage(env, 7)["totals"]!["outputTokens"]!);
        Assert.Equal(12, (int)Usage(env, 90)["totals"]!["outputTokens"]!);
    }

    [Fact]
    public async Task Usage_ucu_ve_import_entegrasyonu()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Root(env);
        File.WriteAllLines(Path.Combine(dir, "s.jsonl"), new[] { AsstLine("msg_A", "claude-opus-5-5", 2, 8, 0, 0, "2026-11-10T09:00:00.000Z") });
        var sum = env.Import(); // Importer usage'ı da içe alır
        Assert.NotNull(sum.UsageIngested);
        Assert.Equal(1, sum.UsageIngested!.NewMessages);
        await using var h = await SozServer.StartAsync(env.DataDir, 49361);
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        var j = JsonNode.Parse(await http.GetStringAsync("/api/usage?days=3"))!;
        Assert.Equal(8, (int)j["totals"]!["outputTokens"]!);
        Assert.Single(j["byDay"]!.AsArray());
    }
}
