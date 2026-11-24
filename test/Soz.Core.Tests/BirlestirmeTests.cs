using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Cli;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Registry;
using Soz.Core.Restore;
using Soz.Server;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// d2m birleştirme testleri: şeritlerin ayrı ayrı sınadığı parçaların birbirine bağlandığı yerler.
/// Hepsi sentetik veriyle, TestEnv klasörü altında çalışır; gerçek ajan araçları ve gerçek ev klasörü açılmaz.
/// </summary>
public class BirlestirmeTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static StringContent J(string s) => new(s, Encoding.UTF8, "application/json");

    // ---------------------------------------------------------------- kayıt defteri onay ucu

    private const string OrnekBaglayici = """
        {"schemaVersion":1,"entries":[{"id":"connector:d2m-ornek","kind":"connector","owner":"user","title":"D2M örnek","stability":"scaffold",
         "permissions":[{"cap":"exec","scope":"d2m-ornek","why":"Ajanı başlatmak için"}],
         "body":{"depth":["run"],"detect":{"commands":["d2m-ornek"]},"launch":{"protocol":"acp","command":"d2m-ornek","args":["acp"],"acpProtocolVersion":1},"history":{"reader":"none"},"registry":{"id":"d2m-ornek"}}}]}
        """;

    [Fact]
    public async Task Registry_Onay_Ve_Geri_Alma_Ucu_YalnizInsandan_JetonluDir_OlayYazar_BaglayiciEtkinlesir()
    {
        using var env = new TestEnv(copySamples: false);
        var yetenek = Path.Combine(env.DataDir, RegistryHost.UserDirName);
        Directory.CreateDirectory(yetenek);
        File.WriteAllText(Path.Combine(yetenek, "ornek.soz.json"), OrnekBaglayici);
        await using var h = await SozServer.StartAsync(env.DataDir, FreePort());
        using var http = new HttpClient { BaseAddress = new Uri(h.Url) };
        HttpRequestMessage Post(string url, string body, bool token = true) { var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = J(body) }; if (token) r.Headers.Add("X-Soz-Token", h.Token); return r; }

        // kişi klasöründeki bağlayıcı onay bekliyor (çalıştırabilen kayıt kendiliğinden etkin olmaz)
        var liste = JsonNode.Parse(await http.GetStringAsync("/api/registry?kind=connector"))!["entries"]!.AsArray();
        var e0 = liste.Single(e => (string?)e!["id"] == "connector:d2m-ornek")!;
        Assert.Equal("suggested", (string?)e0["state"]);
        Assert.True((bool)e0["awaitsApproval"]!);
        Assert.DoesNotContain(Soz.Core.Agents.AgentRegistry.SpecsFrom(RegistryHost.Load(env.Svc.Db)), s => s.Id == "d2m-ornek");

        // ajana kapalı ve jetonsuz reddedilir
        Assert.False(CallerGate.AgentMayWrite("POST", "/api/registry/approve"));
        Assert.False(CallerGate.AgentMayWrite("POST", "/api/registry/revoke"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post("/api/registry/approve", """{"id":"connector:d2m-ornek"}""", token: false))).StatusCode);

        // onay: etkinleşir, ajan listesine girer, olay defterine yazılır
        var r1 = await http.SendAsync(Post("/api/registry/approve", """{"id":"connector:d2m-ornek"}"""));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var b1 = JsonNode.Parse(await r1.Content.ReadAsStringAsync())!;      // gövde dolu olmalı (ifade gövdeli async lambda yanıtı boş bırakıyordu)
        Assert.Equal("enabled", (string?)b1["state"]); Assert.True((bool)b1["changed"]!);
        Assert.Contains(Soz.Core.Agents.AgentRegistry.SpecsFrom(RegistryHost.Load(env.Svc.Db)), s => s.Id == "d2m-ornek");
        var b2 = JsonNode.Parse(await (await http.SendAsync(Post("/api/registry/approve", """{"id":"connector:d2m-ornek"}"""))).Content.ReadAsStringAsync())!;
        Assert.False((bool)b2["changed"]!);                                       // ikinci onay bir şey değiştirmez
        using (var c = env.Svc.Db.Open())
        {
            Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='registry.approved'"));
            Assert.Contains("connector:d2m-ornek", (string)c.Scalar("SELECT payload FROM event_log WHERE kind='registry.approved'")!);
        }

        // geri alma: yine öneri olur, silinmez
        var b3 = JsonNode.Parse(await (await http.SendAsync(Post("/api/registry/revoke", """{"id":"connector:d2m-ornek"}"""))).Content.ReadAsStringAsync())!;
        Assert.Equal("suggested", (string?)b3["state"]); Assert.True((bool)b3["changed"]!);
        Assert.DoesNotContain(Soz.Core.Agents.AgentRegistry.SpecsFrom(RegistryHost.Load(env.Svc.Db)), s => s.Id == "d2m-ornek");
        using (var c = env.Svc.Db.Open()) Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM event_log WHERE kind='registry.revoked'"));

        // hata kodları: kimlik yok 400, kayıt yok 404
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Post("/api/registry/approve", "{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Post("/api/registry/approve", """{"id":"connector:yok-boyle"}"""))).StatusCode);
    }

    [Fact]
    public void Registry_Host_GomuluSabitOlanBaglayici_OnayBekler_OnayliOzetleEtkinlesir()
    {
        using var env = new TestEnv(copySamples: false);
        var once = RegistryHost.Load(env.Svc.Db).Get("connector:hermes")!;
        Assert.Equal(RegistryStates.Enabled, once.State);
        RegistryHost.Revoke(env.Svc.Db, "connector:hermes");
        Assert.Equal(RegistryStates.Disabled, RegistryHost.Load(env.Svc.Db).Get("connector:hermes")!.State);
        var r = RegistryHost.Approve(env.Svc.Db, "connector:hermes");
        Assert.Equal("enabled", (string?)r["state"]);
        Assert.Equal(RegistryStates.Enabled, RegistryHost.Load(env.Svc.Db).Get("connector:hermes")!.State);
        // iki ayrı veritabanı bağımsızdır: kapatma/onay yalnız bu veriye aittir
        using var env2 = new TestEnv(copySamples: false);
        RegistryHost.Revoke(env.Svc.Db, "connector:hermes");
        Assert.Equal(RegistryStates.Enabled, RegistryHost.Load(env2.Svc.Db).Get("connector:hermes")!.State);
    }

    // ---------------------------------------------------------------- harcama: izlenmeyen araç

    [Fact]
    public void Harcama_IzlenmeyenArac_KapaliykenListelenmez_AcilincaAcikcaYokDenir()
    {
        using var env = new TestEnv(copySamples: false);
        foreach (var id in new[] { "connector:hermes", "connector:opencode", "connector:antigravity" })
            RegistryHost.Revoke(env.Svc.Db, id);
        Assert.Empty(Soz.Core.Usage.UsageQuery.NotTracked(env.Svc.Db));                      // kapatılan araçlar listelenmez
        RegistryHost.Approve(env.Svc.Db, "connector:hermes");
        var list = Soz.Core.Usage.UsageQuery.NotTracked(env.Svc.Db);
        var hermes = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(list))!.AsArray().Single()!;
        Assert.Equal("hermes", (string?)hermes["tool"]);
        Assert.Contains("harcama toplamı yok", (string?)hermes["reason"]);
        // yanıtta da görünür (arayüz ve CLI buradan okur)
        var build = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Soz.Core.Usage.UsageQuery.Build(env.Svc.Db, 7)))!;
        Assert.Equal("hermes", (string?)build["notTracked"]![0]!["tool"]);
    }

    // ---------------------------------------------------------------- diff-summary: sonradan eklenen ölçüt

    [Fact]
    public void DiffSummary_OlcutIsBasladiktanSonraEklendiyse_NedeniniSoyler_DegilseGenelCumle()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(dir);
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        var job = w.CreateJob(pid, "Bir iş");
        var chk = w.AddCheckFromTemplate(pid, job.Id, "diff-only", JsonNode.Parse("""{"allowed":"src/**"}""")!.AsObject(), null, true);
        using (var c = env.Svc.Db.Open())
        {
            c.Exec("UPDATE checks SET created_at='2026-11-10T12:30:00Z' WHERE id=@c", null, ("@c", chk.Id));
        }
        // işin hiç koşusu yok: genel cümle
        var genel = Soz.Core.Proof.DiffSummaryMeasurer.NoBaselineText(env.DataDir, job.Id, chk.Id);
        Assert.Contains("alınmamış", genel);
        Assert.DoesNotContain("sonra ekledin", genel);
        // iş (ilk koşu) ölçütten ÖNCE başlamış
        using (var c = env.Svc.Db.Open()) RunStore_Insert(c, job.Id, pid);
        using (var c = env.Svc.Db.Open()) c.Exec("UPDATE runs SET started='2026-11-10T12:00:00Z' WHERE job_id=@j", null, ("@j", job.Id));
        var neden = Soz.Core.Proof.DiffSummaryMeasurer.NoBaselineText(env.DataDir, job.Id, chk.Id);
        Assert.Contains("işin yardımcısı başladıktan sonra ekledin", neden);
        Assert.Contains("iş verilmeden önce ekle", neden);
        // ölçüt işten ÖNCE eklenmişse (ama klasör listesi yoksa) yine genel cümle
        using (var c = env.Svc.Db.Open()) c.Exec("UPDATE checks SET created_at='2026-11-10T11:00:00Z' WHERE id=@c", null, ("@c", chk.Id));
        Assert.DoesNotContain("sonra ekledin", Soz.Core.Proof.DiffSummaryMeasurer.NoBaselineText(env.DataDir, job.Id, chk.Id));
    }

    private static void RunStore_Insert(SqliteConnection c, long jobId, long projectId) => Soz.Core.Run.RunStore.Insert(c, jobId, projectId, "start");

    // ---------------------------------------------------------------- CLI: undo list ve checks add --kind

    private static (int code, string text) Konsol(Func<int> is_)
    {
        var o = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { var code = is_(); return (code, sw.ToString()); }
        finally { Console.SetOut(o); }
    }

    [Fact]
    public void Cli_Undo_YalnizListAjanaAcik_SaltOkunur_OlayYazmaz()
    {
        var bayrak = new HashSet<string> { "json" };
        Assert.True(CliGuard.AgentMayRun("undo", Args.Parse(new[] { "list", "1" }, bayrak)));
        Assert.False(CliGuard.AgentMayRun("undo", Args.Parse(new[] { "preview", "1" }, bayrak)));
        Assert.False(CliGuard.AgentMayRun("undo", Args.Parse(new[] { "apply", "1" }, bayrak)));
        Assert.False(CliGuard.AgentMayRun("undo", Args.Parse(new[] { "revert", "1" }, bayrak)));
        Assert.False(CliGuard.AgentMayRun("undo", Args.Parse(Array.Empty<string>(), bayrak)));

        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(dir);
        var w = new WorkService(env.Svc.Db);
        var job = w.CreateJob(w.EnsureProjectByPath(dir), "Bir iş");
        long once, dosyaBoyu;
        using (var c = env.Svc.Db.Open()) once = c.ScalarLong("SELECT COUNT(*) FROM event_log");
        SqliteConnection.ClearAllPools();
        dosyaBoyu = new FileInfo(Path.Combine(env.DataDir, "soz.db")).Length;

        var (code, text) = Konsol(() => Soz.Cli.Commands.UndoCommands.Run(Args.Parse(new[] { "list", job.Id.ToString(), "--data", env.DataDir }, bayrak)));
        Assert.Equal(0, code);
        Assert.Contains($"İş #{job.Id}", text);
        Assert.Contains("koruma:", text);
        Assert.Contains("Geri alınamaz", text);                                          // koruma noktası yok: sade cümle, hata değil
        var (jc, jt) = Konsol(() => Soz.Cli.Commands.UndoCommands.Run(Args.Parse(new[] { "list", job.Id.ToString(), "--json", "--data", env.DataDir }, bayrak)));
        Assert.Equal(0, jc);
        Assert.NotNull(JsonNode.Parse(jt)!["protection"]);
        using (var c = env.Svc.Db.Open()) Assert.Equal(once, c.ScalarLong("SELECT COUNT(*) FROM event_log"));   // olay yazılmadı
        Assert.Throws<ArgumentException>(() => Soz.Cli.Commands.UndoCommands.Run(Args.Parse(new[] { "preview", "1" }, bayrak)));
        Assert.Throws<ArgumentException>(() => Soz.Cli.Commands.UndoCommands.Run(Args.Parse(new[] { "list", "abc" }, bayrak)));
    }

    [Fact]
    public void Cli_ChecksAdd_Kind_KayitDefterindenGelir_AlanlarSemadanDolar_BilinmeyenTurTurleriSayar()
    {
        var bayrak = new HashSet<string> { "json", "enable" };
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "proje");
        Directory.CreateDirectory(dir);
        var w = new WorkService(env.Svc.Db);
        w.EnsureProjectByPath(dir);
        string[] Ortak(params string[] ek) => new[] { "add", "--project", dir, "--data", env.DataDir }.Concat(ek).ToArray();

        // kayıttaki bir tür (file): tam sayı alanı kayıttaki türden, verilmeyen alan kayıttaki varsayılandan; eski/yeni ayrımı yok, tür adı koda yazılı değil
        var (code, text) = Konsol(() => Soz.Cli.Commands.JobProofCommands.Run("checks", Args.Parse(Ortak("--kind", "file", "--label", "Dosya var", "--path", "cikti.txt", "--min-bytes", "5", "--json"), bayrak)));
        Assert.Equal(0, code);
        var c1 = JsonNode.Parse(text)!;
        Assert.Equal("file", (string?)c1["kind"]);
        var spec = c1["spec"]!.AsObject();
        Assert.Equal("cikti.txt", (string?)spec["path"]);
        Assert.Equal(5, (int)spec["minBytes"]!);
        Assert.True((bool)spec["exists"]!);                                             // varsayılan kayıttan

        // command de aynı yoldan gelir (özel kod yok)
        var (c2, t2) = Konsol(() => Soz.Cli.Commands.JobProofCommands.Run("checks", Args.Parse(Ortak("--kind", "command", "--label", "Test", "--cmd", "dotnet test", "--json"), bayrak)));
        Assert.Equal(0, c2);
        Assert.Equal(0, (int)JsonNode.Parse(t2)!["spec"]!["expectExit"]!);

        // hatalar sade: bilinmeyen tür kayıttaki türleri sayar, zorunlu alan eksikse söyler, tam sayı yerine yazı reddedilir
        var bilinmeyen = Assert.Throws<ArgumentException>(() => Soz.Cli.Commands.JobProofCommands.Run("checks", Args.Parse(Ortak("--kind", "hologram", "--label", "x"), bayrak)));
        Assert.Contains("file-range", bilinmeyen.Message); Assert.Contains("diff-summary", bilinmeyen.Message);
        Assert.Throws<ArgumentException>(() => Soz.Cli.Commands.JobProofCommands.Run("checks", Args.Parse(Ortak("--kind", "file-range", "--label", "x"), bayrak)));
        var tam = Assert.Throws<ArgumentException>(() => Soz.Cli.Commands.JobProofCommands.Run("checks", Args.Parse(Ortak("--kind", "file-range", "--label", "x", "--path", "a", "--min-bytes", "çok"), bayrak)));
        Assert.Contains("tam sayı", tam.Message);
    }

    // ---------------------------------------------------------------- geri alma ayarları kayıt defterinden

    [Fact]
    public void GeriAlma_Ayarlari_KayitDefterinde_KodDegeriKayittanOkur_SihirliSabitYok()
    {
        var load = RegistryLoader.LoadEmbedded();
        var kimlikler = new[] { "enabled", "max-copy-mb", "max-files", "parallel", "keep-accepted-days", "keep-last-points", "undo-point-days", "global-budget-gb", "min-project-budget-gb", "project-budget-factor", "disk-free-factor" }
            .Select(k => RestoreOptions.Prefix + k).ToList();
        Assert.All(kimlikler, k => Assert.NotNull(load.Get(k)));
        // varsayılanlar: kodun kendi varsayılanı kayıttakiyle aynı
        var d = new RestoreOptions();
        Assert.Equal((long)load.Get(RestoreOptions.Prefix + "max-copy-mb")!.Setting!.Default!.GetValue<int>() << 20, d.MaxCopyBytes);
        Assert.Equal(load.Get(RestoreOptions.Prefix + "max-files")!.Setting!.Default!.GetValue<int>(), d.MaxFiles);
        Assert.Equal(load.Get(RestoreOptions.Prefix + "parallel")!.Setting!.Default!.GetValue<int>(), d.Parallel);
        Assert.Equal(load.Get(RestoreOptions.Prefix + "global-budget-gb")!.Setting!.Default!.GetValue<int>() * (1L << 30), d.GlobalBudgetBytes);
        Assert.True(RegistryChecker.Check(load).Ok);

        // kişi değeri değiştirince geri alma motoru onu kullanır; sınır dışı değer kayıt sınırıyla reddedilir
        using var env = new TestEnv(copySamples: false);
        using (var c = env.Svc.Db.Open())
        {
            Assert.Null(SozSettings.Validate(new JsonObject { [RestoreOptions.Prefix + "max-files"] = 5000, [RestoreOptions.Prefix + "parallel"] = 2 }));
            SozSettings.Save(c, new JsonObject { [RestoreOptions.Prefix + "max-files"] = 5000, [RestoreOptions.Prefix + "parallel"] = 2 });
            var o = RestoreOptions.Load(c);
            Assert.Equal(5000, o.MaxFiles); Assert.Equal(2, o.Parallel);
        }
        Assert.NotNull(SozSettings.Validate(new JsonObject { [RestoreOptions.Prefix + "parallel"] = 99 }));          // en çok 32
        Assert.NotNull(SozSettings.Validate(new JsonObject { [RestoreOptions.Prefix + "max-files"] = 10 }));         // en az 1000
        Assert.NotNull(SozSettings.Validate(new JsonObject { [RestoreOptions.Prefix + "parallel"] = 1.5 }));         // tam sayı
    }

    // d2m madde 12: oturum kartının durum kanıtı alanları (kaynak, güven, kayıt sonu) /api/sessions cevabında da görünür.
    [Fact]
    public void Oturum_listesi_durum_kaynagi_ve_guveni_tasir()
    {
        using var env = new TestEnv();
        env.Import();
        var kartlar = env.Svc.Sessions(24 * 365 * 5, includeStale: true);
        Assert.NotEmpty(kartlar);
        Assert.All(kartlar, k => { Assert.False(string.IsNullOrEmpty(k.StateSource)); Assert.False(string.IsNullOrEmpty(k.StateConfidence)); });
        var json = System.Text.Json.JsonSerializer.Serialize(kartlar[0], new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"stateSource\"", json);
        Assert.Contains("\"stateConfidence\"", json);
    }
}
