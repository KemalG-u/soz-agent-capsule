using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core;
using Soz.Core.Agents;
using Soz.Core.Assistants;
using Soz.Core.Data;
using Soz.Core.Jobs;
using Soz.Core.Onboarding;
using Soz.Core.Profile;
using Soz.Core.Proof;
using Soz.Core.Queue;
using Soz.Core.Readers;
using Soz.Core.Registry;
using Soz.Core.Run;
using Soz.Core.Security;
using Soz.Core.Text;
using Soz.Server;
using Xunit;
using OnboardingScanContext = Soz.Core.Onboarding.ScanContext;

namespace Soz.Core.Tests;

public sealed class KolAYeniOzelliklerTests
{
    private static string YeniGeciciKlasor(string ad)
    {
        var dir = Path.Combine(TestEnv.RepoRoot, "_gecici", "kola", "testler", ad + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HttpRequestMessage Istek(HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (method != HttpMethod.Get) req.Headers.Add("X-Soz-Token", token);
        return req;
    }

    private static SessionMetadata Meta(string tool, string id, string? cwd, DateTime started, DateTime last, string? parent = null)
        => new(tool, id, cwd, null, started, last, parent, "sentetik-" + id, last, last, "fixture");

    [Fact]
    public async Task AssistantCatalog_YeniAyarlar_Ve_SadeTurkceKayitlar()
    {
        var ids = AssistantCatalog.All.Select(a => a.Id).ToArray();
        Assert.Contains("antigravity", ids);
        Assert.Contains("vscode", ids);
        Assert.Contains("cursor", ids);
        Assert.Contains("hermes", ids);
        Assert.Equal("Antigravity", AssistantCatalog.Who("antigravity"));
        Assert.Equal("VS Code", AssistantCatalog.Who("vscode"));
        Assert.Equal("Cursor", AssistantCatalog.Who("cursor"));

        using var env = new TestEnv(copySamples: false);
        await using var server = await SozServer.StartAsync(env.DataDir, 0);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };

        var getRes = await http.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.NotNull(getRes);
        Assert.Equal("cam", (string?)getRes!["themeFamily"]);
        Assert.Equal("suan,projeler,ayarlar", (string?)getRes["navTabs"]);

        var putRes = await http.SendAsync(Istek(HttpMethod.Put, "/api/settings", server.Token, new
        {
            themeFamily = "terminal",
            navTabs = "suan,ayarlar",
            defaultAgent = "antigravity"
        }));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var updated = SozSettings.Load(env.Svc.Db);
        Assert.Equal("terminal", updated.ThemeFamily);
        Assert.Equal("suan,ayarlar", updated.NavTabs);
        Assert.Equal("antigravity", updated.DefaultAgent);

        var putHermes = await http.SendAsync(Istek(HttpMethod.Put, "/api/settings", server.Token, new { defaultAgent = "hermes" }));
        Assert.Equal(HttpStatusCode.OK, putHermes.StatusCode);

        var putBadTheme = await http.SendAsync(Istek(HttpMethod.Put, "/api/settings", server.Token, new { themeFamily = "gecersiz" }));
        Assert.Equal(HttpStatusCode.BadRequest, putBadTheme.StatusCode);

        // Güncellenen .soz.json dosyalarında mühendis terimleri ve yasaklı noktalama olmamalı
        var asm = typeof(RegistryLoader).Assembly;
        foreach (var name in new[] { "ayarlar.soz.json", "baglayicilar.soz.json", "gerialma.soz.json", "kesif.soz.json" })
        {
            var resName = asm.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            using var sr = new StreamReader(asm.GetManifestResourceStream(resName)!, Encoding.UTF8);
            var text = sr.ReadToEnd();
            Assert.DoesNotContain("kum havuzu", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kanıt kapısı dış döngüyle", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("iş parçacığı", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Zaten yuvada olanın puan çarpanı", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", text);
            Assert.DoesNotContain("\u2013", text);
            Assert.DoesNotContain(";", text);
        }
    }

    [Fact]
    public void Antigravity_Sqlite_Okuyucu_UriCozme_BosParent_Ve_PresenceLock()
    {
        var root = YeniGeciciKlasor("agy-sqlite");
        var db = SozDb.OpenAndMigrate(Path.Combine(root, "data"));
        var roots = OnboardingRoots.Under(Path.Combine(root, "home"));
        Directory.CreateDirectory(roots.AntigravityHome);
        Directory.CreateDirectory(roots.AntigravityCliHome);
        Directory.CreateDirectory(Path.Combine(roots.AntigravityHome, "presence"));

        var now = new DateTime(2026, 11, 22, 18, 0, 0, DateTimeKind.Utc);
        var mainDb = Path.Combine(roots.AntigravityHome, "conversation_summaries.db");
        using (var c = new SqliteConnection($"Data Source={mainDb};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE conversation_summaries (
                    conversation_id TEXT PRIMARY KEY,
                    title TEXT,
                    last_modified_time TEXT,
                    workspace_uris TEXT,
                    status TEXT,
                    parent_conversation_id TEXT,
                    not_fully_idle INTEGER
                );
                INSERT INTO conversation_summaries VALUES (
                    'agy-parent-1', 'Ana Oturum', @t1, '[""file:///d%3A/tools/proje-alfa""]', 'done', '', 0
                );
                INSERT INTO conversation_summaries VALUES (
                    'agy-child-1', 'Alt Ajan Oturumu', @t2, '[""file:///e%3A/""]', 'running', 'agy-parent-1', 1
                );";
            cmd.Parameters.AddWithValue("@t1", now.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@t2", now.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        var cliDb = Path.Combine(roots.AntigravityCliHome, "conversation_summaries.db");
        using (var c = new SqliteConnection($"Data Source={cliDb};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE conversation_summaries (
                    conversation_id TEXT PRIMARY KEY,
                    title TEXT,
                    last_modified_time TEXT,
                    workspace_uris TEXT,
                    status TEXT,
                    parent_conversation_id TEXT,
                    not_fully_idle INTEGER
                );
                INSERT INTO conversation_summaries VALUES (
                    'agy-cli-1', 'Komut Satiri Oturumu', @t, '[""file:///d%3A/tools/proje-alfa""]', 'done', '', 0
                );";
            cmd.Parameters.AddWithValue("@t", now.AddMinutes(-20).ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        var lockPath = Path.Combine(roots.AntigravityHome, "presence", "agy-parent-1.lock");
        File.WriteAllText(lockPath, "");
        File.SetLastWriteTimeUtc(lockPath, now.AddMinutes(-2));

        var fs = new ProfileInventoryTests.FakeFs();
        fs.Dirs.Add(@"D:\tools\proje-alfa");
        fs.Markers.Add(@"D:\tools\proje-alfa");

        var svc = new ProfileService(db)
        {
            Machine = new ProfileInventoryTests.FakeMachine(),
            Processes = new ProfileP2ExtraTests.FakeProcesses(new ProcessObservation("Antigravity", 4200, now, WindowHandle: 99001)),
            FileSystem = fs,
            ExclusionsFactory = r => new ProjectExclusions(r.Home, db.DataDir)
        };

        var snap = svc.Scan(new OnboardingScanContext { Roots = roots, Probe = new NullSystemProbe(), UtcNow = () => now });
        Assert.Equal(3, snap.Counts.Sessions);
        var proj = Assert.Single(snap.Projects);
        Assert.Equal(@"D:\tools\proje-alfa", proj.Path);
        Assert.Equal(3, proj.SessionsByTool["antigravity"]);
        Assert.Equal(2, proj.RootSessions);
        Assert.Equal(1, proj.SubagentSessions);

        var liveProj = Assert.Single(snap.Live);
        Assert.Contains(liveProj.Sessions, s => s.Id == "agy-parent-1" && s.AcikAdayi);
        Assert.Contains(liveProj.Relations, rel => rel.SessionId == "agy-parent-1" && rel.OtherSessionId == "agy-child-1" && rel.Relation == "ana ve yardımcı");

        // AntigravityDbReader da salt okunur çalışır
        var store = new IngestStore(db);
        var readReport = new AntigravityDbReader().Read(store, roots.AntigravityHome, new ReadOptions());
        Assert.True(readReport.Found);
        Assert.Equal(2, readReport.FilesRead);
    }

    [Fact]
    public void Codex_StateSqlite_UzunYolOneki_RolloutBirlestirme_Ve_ZstUyarisi()
    {
        var root = YeniGeciciKlasor("codex-sqlite");
        var db = SozDb.OpenAndMigrate(Path.Combine(root, "data"));
        var roots = OnboardingRoots.Under(Path.Combine(root, "home"));
        Directory.CreateDirectory(Path.Combine(roots.CodexHome, "sessions"));

        var now = new DateTime(2026, 11, 22, 18, 0, 0, DateTimeKind.Utc);
        var stateDb = Path.Combine(roots.CodexHome, "state_5.sqlite");
        using (var c = new SqliteConnection($"Data Source={stateDb};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE threads (
                    id TEXT PRIMARY KEY,
                    cwd TEXT,
                    created_at INTEGER,
                    updated_at INTEGER,
                    git_branch TEXT,
                    title TEXT
                );
                CREATE TABLE thread_spawn_edges (
                    parent_thread_id TEXT,
                    child_thread_id TEXT
                );
                INSERT INTO threads VALUES ('cx-ana', '\\?\E:\Projeler\DenemeKutu', @c1, @u1, 'main', 'Ana');
                INSERT INTO threads VALUES ('cx-alt', '\\?\E:\Projeler\DenemeKutu', @c2, @u2, 'main', 'Alt');
                INSERT INTO thread_spawn_edges VALUES ('cx-ana', 'cx-alt');";
            var epoch = (long)(now - DateTime.UnixEpoch).TotalSeconds;
            cmd.Parameters.AddWithValue("@c1", epoch - 600);
            cmd.Parameters.AddWithValue("@u1", epoch - 60);
            cmd.Parameters.AddWithValue("@c2", epoch - 300);
            cmd.Parameters.AddWithValue("@u2", epoch - 30);
            cmd.ExecuteNonQuery();
        }

        // Aynı cx-ana oturumu rollout içinde de varsa çift sayılmaz, cx-ek oturumu eklenir
        File.WriteAllText(
            Path.Combine(roots.CodexHome, "sessions", "rollout-cx-ana.jsonl"),
            JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "cx-ana", cwd = @"\\?\E:\Projeler\DenemeKutu", timestamp = now.ToString("O", CultureInfo.InvariantCulture) } }) + "\n");
        File.WriteAllText(
            Path.Combine(roots.CodexHome, "sessions", "rollout-cx-ek.jsonl"),
            JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "cx-ek", cwd = @"\\?\E:\Projeler\DenemeKutu", timestamp = now.ToString("O", CultureInfo.InvariantCulture) } }) + "\n");

        var fs = new ProfileInventoryTests.FakeFs();
        fs.Dirs.Add(@"E:\Projeler\DenemeKutu");
        fs.Markers.Add(@"E:\Projeler\DenemeKutu");

        var svc = new ProfileService(db)
        {
            Machine = new ProfileInventoryTests.FakeMachine(),
            Processes = new ProfileP2ExtraTests.FakeProcesses(),
            FileSystem = fs,
            ExclusionsFactory = r => new ProjectExclusions(r.Home, db.DataDir)
        };

        var snap = svc.Scan(new OnboardingScanContext { Roots = roots, Probe = new NullSystemProbe(), UtcNow = () => now });
        Assert.Equal(3, snap.Counts.Sessions);
        var proj = Assert.Single(snap.Projects);
        Assert.Equal(@"E:\Projeler\DenemeKutu", proj.Path);
        Assert.Equal(2, proj.RootSessions);
        Assert.Equal(1, proj.SubagentSessions);

        // SQLite yokken .jsonl.zst dosyası varsa dürüst uyarı üretilir
        var rootZst = YeniGeciciKlasor("codex-zst");
        var dbZst = SozDb.OpenAndMigrate(Path.Combine(rootZst, "data"));
        var rootsZst = OnboardingRoots.Under(Path.Combine(rootZst, "home"));
        Directory.CreateDirectory(Path.Combine(rootsZst.CodexHome, "sessions"));
        File.WriteAllBytes(Path.Combine(rootsZst.CodexHome, "sessions", "rollout-eski.jsonl.zst"), new byte[] { 0x28, 0xB5, 0x2F, 0xFD });
        var svcZst = new ProfileService(dbZst)
        {
            Machine = new ProfileInventoryTests.FakeMachine(),
            Processes = new ProfileP2ExtraTests.FakeProcesses(),
            FileSystem = fs,
            ExclusionsFactory = r => new ProjectExclusions(r.Home, dbZst.DataDir)
        };
        var snapZst = svcZst.Scan(new OnboardingScanContext { Roots = rootsZst, Probe = new NullSystemProbe(), UtcNow = () => now });
        Assert.NotNull(snapZst.Warnings);
        Assert.Contains(snapZst.Warnings!, w => w.Contains("Codex", StringComparison.OrdinalIgnoreCase) && w.Contains("sıkıştırılmış", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VsCode_Cursor_WorkspaceStorage_Ve_ClaudeDesktop_YalnizUstBilgi()
    {
        var root = YeniGeciciKlasor("ide-storage");
        var db = SozDb.OpenAndMigrate(Path.Combine(root, "data"));
        var roots = OnboardingRoots.Under(Path.Combine(root, "home"));
        var now = new DateTime(2026, 11, 22, 18, 0, 0, DateTimeKind.Utc);

        // VS Code: chatSessions var, gizli içerik okunmaz
        var vsWs = Path.Combine(roots.VsCodeStorage, "ws-1");
        Directory.CreateDirectory(Path.Combine(vsWs, "chatSessions"));
        File.WriteAllText(Path.Combine(vsWs, "workspace.json"), @"{ ""folder"": ""file:///e%3A/Projeler/IdeProje"" }");
        var chatFile = Path.Combine(vsWs, "chatSessions", "vs-sohbet-1.json");
        File.WriteAllText(chatFile, @"{ ""secret"": ""GIZLI_IDE_METNI_OKUMA_9911"" }");
        File.SetLastWriteTimeUtc(chatFile, now.AddMinutes(-3));

        // Cursor: chatSessions boş ama workspace.json var -> son erişim zamanıyla çalışma alanı tanınır
        var curWs = Path.Combine(roots.CursorStorage, "ws-2");
        Directory.CreateDirectory(curWs);
        var curWsJson = Path.Combine(curWs, "workspace.json");
        File.WriteAllText(curWsJson, @"{ ""folder"": ""file:///e%3A/Projeler/CursorProje"" }");
        File.SetLastWriteTimeUtc(curWsJson, now.AddMinutes(-10));

        // Claude Desktop local-agent-mode-sessions
        Directory.CreateDirectory(roots.ClaudeDesktopSessions);
        var claudeLocal = Path.Combine(roots.ClaudeDesktopSessions, "yerel-oturum-1.json");
        File.WriteAllText(claudeLocal, JsonSerializer.Serialize(new { sessionId = "cl-yerel-1", cwd = @"E:\Projeler\IdeProje", timestamp = now.AddMinutes(-4).ToString("O", CultureInfo.InvariantCulture) }));

        var fs = new ProfileInventoryTests.FakeFs();
        fs.Dirs.UnionWith(new[] { @"E:\Projeler\IdeProje", @"E:\Projeler\CursorProje" });
        fs.Markers.UnionWith(new[] { @"E:\Projeler\IdeProje", @"E:\Projeler\CursorProje" });

        var svc = new ProfileService(db)
        {
            Machine = new ProfileInventoryTests.FakeMachine(),
            Processes = new ProfileP2ExtraTests.FakeProcesses(),
            FileSystem = fs,
            ExclusionsFactory = r => new ProjectExclusions(r.Home, db.DataDir)
        };

        var snap = svc.Scan(new OnboardingScanContext { Roots = roots, Probe = new NullSystemProbe(), UtcNow = () => now });
        Assert.Contains(snap.Projects, p => p.Path == @"E:\Projeler\IdeProje" && p.SessionsByTool.ContainsKey("vscode") && p.SessionsByTool.ContainsKey("claude"));
        Assert.Contains(snap.Projects, p => p.Path == @"E:\Projeler\CursorProje" && p.SessionsByTool.ContainsKey("cursor"));

        using var c = db.Open();
        foreach (var table in c.Query("SELECT name FROM sqlite_master WHERE type='table'", x => x.GetString(0)))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                    Assert.DoesNotContain("GIZLI_IDE_METNI_OKUMA_9911", r.GetValue(i)?.ToString() ?? "");
        }
    }

    [Fact]
    public async Task AntigravityConnector_HermesHomeEnjeksiyonu_Ve_KuruluOlmayanArac409()
    {
        using var env = new TestEnv(copySamples: false);
        var st = SozSettings.Load(env.Svc.Db);
        var cmd = new LaunchCommand(@"C:\yerel\agy.exe", Array.Empty<string>(), "agy");
        var agy = new AntigravityConnector();

        var startAuto = agy.BuildStart(cmd, new AgentRunContext(@"E:\Proje", "Bunu düzelt", "gemini-2.5-pro", AgentPermissionMode.Careful, 1, 1, st, null));
        Assert.Equal(@"C:\yerel\agy.exe", startAuto.FileName);
        Assert.Equal(new[] { "-p", "--output-format", "stream-json", "--mode", "auto", "--add-dir", @"E:\Proje", "--model", "gemini-2.5-pro" }, startAuto.Args);
        Assert.Equal("Bunu düzelt", startAuto.Stdin);

        var startPlan = agy.BuildStart(cmd, new AgentRunContext(@"E:\Proje", "Yalnız incele", "", AgentPermissionMode.ReadOnly, 1, 1, st, null));
        Assert.Contains("--mode", startPlan.Args);
        Assert.Equal("plan", startPlan.Args[Array.IndexOf(startPlan.Args.ToArray(), "--mode") + 1]);

        var resume = agy.BuildResume(cmd, new AgentRunContext(@"E:\Proje", "Devam et", "", AgentPermissionMode.Careful, 1, 1, st, null), "conv-42");
        Assert.Contains("--conversation", resume.Args);
        Assert.Contains("conv-42", resume.Args);

        var stream = agy.CreateEventStream();
        var l1 = stream.Feed(@"{""type"":""init"",""conversation_id"":""conv-99""}");
        Assert.Equal("conv-99", stream.SessionId);
        Assert.Empty(l1);
        var l2 = stream.Feed(@"{""type"":""tool_call"",""name"":""read_file"",""arguments"":{""path"":""E:\\Proje\\index.js""}}");
        Assert.Contains(l2, x => x.Kind == "progress" && x.Text.Contains("index.js dosyasını okuyor", StringComparison.OrdinalIgnoreCase));
        var l3 = stream.Feed(@"{""type"":""result"",""text"":""Tamamlandı."",""usage"":{""input_tokens"":120,""output_tokens"":45}}");
        Assert.True(stream.ResultSeen);
        Assert.False(stream.ResultIsError);
        Assert.NotNull(stream.Usage);
        Assert.Equal(120, stream.Usage!.InputTokens);
        Assert.Equal(45, stream.Usage.OutputTokens);
        Assert.Contains(l3, x => x.Kind == "agent" && x.Text == "Tamamlandı.");

        // HermesConnector: bin\hermes.exe üst klasöründe state.db varsa HERMES_HOME otomatik verilir
        var hermesHome = YeniGeciciKlasor("hermes-home");
        var hermesBin = Path.Combine(hermesHome, "bin");
        Directory.CreateDirectory(hermesBin);
        var fakeHermesExe = Path.Combine(hermesBin, "hermes.exe");
        File.WriteAllText(fakeHermesExe, "");
        File.WriteAllText(Path.Combine(hermesHome, "state.db"), "");

        var savedHermesEnv = Environment.GetEnvironmentVariable("HERMES_HOME");
        Environment.SetEnvironmentVariable("HERMES_HOME", null);
        try
        {
            var hSpec = new HermesConnector().BuildStart(
                new LaunchCommand(fakeHermesExe, Array.Empty<string>(), fakeHermesExe),
                new AgentRunContext(@"E:\Proje", "Test", "", AgentPermissionMode.Careful, 1, 1, st, null));
            Assert.Equal(hermesHome, hSpec.AddEnv["HERMES_HOME"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERMES_HOME", savedHermesEnv);
        }

        // Kurulu olmayan bağlayıcıda POST /api/jobs/{id}/start anında 409 döner, taban testi veya koşu başlatmaz
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", "");
        try
        {
            var projDir = YeniGeciciKlasor("kurulu-degil-proje");
            var w = new WorkService(env.Svc.Db);
            var pid = w.EnsureProjectByPath(projDir);
            w.AddCheck(pid, null, "command", "AgirTabanTesti", new JsonObject { ["cmd"] = "exit 1", ["expectExit"] = 0 }, 60, true);
            var job = w.CreateJob(pid, "Bunu yap", agent: "antigravity");

            await using var server = await SozServer.StartAsync(env.DataDir, 0);
            using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
            var resp = await http.SendAsync(Istek(HttpMethod.Post, $"/api/jobs/{job.Id}/start", server.Token, new { agent = "antigravity" }));
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            var errBody = await resp.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Contains("bulunamadı", (string?)errBody!["error"] ?? "", StringComparison.OrdinalIgnoreCase);

            using var c = env.Svc.Db.Open();
            Assert.Equal(0L, c.ScalarLong("SELECT COUNT(*) FROM proof_runs WHERE job_id=@j", null, ("@j", job.Id)));
            Assert.Empty(RunStore.ListForJob(c, job.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    [Fact]
    public void ProjectResolver_IsaretciliAltProjeYutulmaz_KapKlasor_Ve_AracGeciciDislama()
    {
        var now = new DateTime(2026, 11, 22, 18, 0, 0, DateTimeKind.Utc);
        var fs = new ProfileInventoryTests.FakeFs();
        fs.Dirs.UnionWith(new[] { @"D:\tools", @"D:\tools\kapsul", @"D:\tools\kokpit" });
        fs.Gits.Add(@"D:\tools\kapsul");
        fs.Markers.Add(@"D:\tools\kokpit");

        var exclusions = new ProjectExclusions(@"C:\Users\demo", @"E:\SozVeri");
        Assert.Equal("arac-gecici-klasoru", exclusions.HardReason(@"C:\Users\demo\.agents\skills"));
        Assert.Equal("arac-gecici-klasoru", exclusions.HardReason(@"C:\Users\demo\.gemini\antigravity\brain"));
        Assert.Equal("arac-gecici-klasoru", exclusions.HardReason(@"C:\Users\demo\AppData\Roaming\Code"));
        Assert.Equal("arac-gecici-klasoru", exclusions.HardReason(@"C:\Users\demo\Documents\Codex\deneme"));
        Assert.Equal("arac-gecici-klasoru", exclusions.HardReason(@"C:\Users\demo\Belgeler\Codex\deneme"));
        Assert.Equal("arac-gecici-klasoru", exclusions.HardReason(@"C:\Users\demo\OneDrive\Documents\Codex\deneme"));
        Assert.Equal("arac-gecici-klasoru", exclusions.HardReason(@"C:\Users\demo\OneDrive\Belgeler\Codex\deneme"));

        // D:\tools üst klasöründe 8 oturum olsa bile altındaki işaretçili projeler yutulmaz ve D:\tools kap-klasor olur
        var sessions = new List<SessionMetadata>();
        for (var i = 0; i < 8; i++)
            sessions.Add(Meta("claude", "ust-" + i, @"\\?\D:\tools", now.AddMinutes(-30), now.AddMinutes(-10)));
        sessions.Add(Meta("claude", "alt-1", @"\\?\D:\tools\kapsul", now.AddMinutes(-20), now.AddMinutes(-5)));
        sessions.Add(Meta("codex", "alt-2", @"\\?\D:\tools\kokpit", now.AddMinutes(-15), now.AddMinutes(-2)));

        var resolver = new ProjectResolver(fs, exclusions);
        var res = resolver.Build(sessions, now);
        Assert.Equal(2, res.Projects.Count);
        Assert.Contains(res.Projects, p => p.Path == @"D:\tools\kapsul");
        Assert.Contains(res.Projects, p => p.Path == @"D:\tools\kokpit");
        Assert.DoesNotContain(res.Projects, p => p.Path == @"D:\tools");
        Assert.Equal(8, res.Unassigned.Reasons["kap-klasor"]);
        Assert.Equal(@"D:\tools", res.Defaults.YeniProjeUstKlasoru);
    }

    [Fact]
    public void CanliAciklik_SahteAcikAdayiOnleme_HermesElectron_Ve_ChatGptCodex()
    {
        var now = new DateTime(2026, 11, 22, 18, 0, 0, DateTimeKind.Utc);
        var processes = new ProfileP2ExtraTests.FakeProcesses(
            // Hermes ana süreç + Electron renderer/gpu alt süreçleri -> tek pencere sayılmalı
            new ProcessObservation("Hermes", 100, now, ParentId: 4, CommandLine: @"""C:\Hermes\Hermes.exe""", WindowHandle: 5001),
            new ProcessObservation("Hermes", 101, now, ParentId: 100, CommandLine: @"""C:\Hermes\Hermes.exe"" --type=gpu-process", ParentName: "Hermes", IsHelperProcess: true),
            new ProcessObservation("Hermes", 102, now, ParentId: 100, CommandLine: @"""C:\Hermes\Hermes.exe"" --type=renderer", ParentName: "Hermes", IsHelperProcess: true),
            // ChatGPT masaüstü altındaki yardımcı codex.exe -> bağımsız açık codex sayılmamalı
            new ProcessObservation("ChatGPT", 200, now, ParentId: 4),
            new ProcessObservation("codex", 201, now, ParentId: 200, CommandLine: @"""C:\Program Files\ChatGPT\codex.exe""", ParentName: "ChatGPT", IsHelperProcess: true)
        );

        var inv = ProcessProbe.Inspect(processes, now);
        Assert.Equal(1, inv.Tools.Single(t => t.Tool == "hermes").Count);
        Assert.Equal(0, inv.Tools.Single(t => t.Tool == "codex").Count);

        var fs = new ProfileInventoryTests.FakeFs();
        fs.Dirs.Add(@"E:\Projeler\EskiProje");
        fs.Markers.Add(@"E:\Projeler\EskiProje");

        var fiveDaysAgo = now.AddDays(-5);
        var rows = new[]
        {
            Meta("hermes", "eski-hermes", @"E:\Projeler\EskiProje", fiveDaysAgo, fiveDaysAgo),
            Meta("codex", "eski-codex", @"E:\Projeler\EskiProje", fiveDaysAgo, fiveDaysAgo)
        };

        var built = new ProjectResolver(fs, new ProjectExclusions(@"C:\Users\demo", @"E:\SozVeri")).Build(rows, now, inv);
        // 5 gün önceki ölü oturumların hiçbiri açık gösterilmez, yalnız açık Hermes penceresi dürüstçe güncel uygulama olarak yer alır
        Assert.DoesNotContain(built.Live, lp => lp.ProjectPath == @"E:\Projeler\EskiProje");
        Assert.DoesNotContain(built.Live.SelectMany(lp => lp.Sessions), s => s.Id is "eski-hermes" or "eski-codex");
        var openApp = Assert.Single(built.Live);
        Assert.Null(openApp.ProjectPath);
        Assert.Contains(openApp.Sessions, s => s.Tool == "hermes" && s.CanFocus && s.Hwnd == 5001);
    }

    [Fact]
    public async Task KanitKapisi_K13DinamikVeriYolu_K14BetikGurultusuYok_Ve_U6TestOfferYenileme()
    {
        // K13: Şablon/dinamik birleştirmeyle okunan veri dosyası (veri/beklenen.json) EvidenceBase.Scan tarafından destek kümesine alınır
        var k13Dir = YeniGeciciKlasor("k13-proje");
        Directory.CreateDirectory(Path.Combine(k13Dir, "veri"));
        Directory.CreateDirectory(Path.Combine(k13Dir, "test"));
        File.WriteAllText(Path.Combine(k13Dir, "package.json"), @"{""name"":""k13"",""scripts"":{""test"":""node test/hesap.test.js""}}");
        File.WriteAllText(Path.Combine(k13Dir, "hesap.js"), "module.exports = (a, b) => a + b\n");
        File.WriteAllText(Path.Combine(k13Dir, "veri", "beklenen.json"), @"{""sonuc"": 5}");
        File.WriteAllText(
            Path.Combine(k13Dir, "test", "hesap.test.js"),
            "const assert = require('node:assert/strict')\nconst fs = require('fs')\nconst path = require('path')\nconst kok = process.cwd()\nconst veri = JSON.parse(fs.readFileSync(path.join(kok, `veri/beklenen.json`), 'utf8'))\nconst topla = require('../hesap')\nassert.equal(topla(2, 3), veri.sonuc)\n");

        var k13Ctx = Soz.Core.Proof.ScanContext.Build(k13Dir, new[] { "npm test" });
        var k13Snap = EvidenceBase.Scan(k13Dir, k13Ctx);
        Assert.Contains(k13Snap.Suspects, e => e.Path == "veri/beklenen.json" && e.Cls == EvidenceBase.ClsTest);

        // K14: start, dev, build, lint betikleri eklendiğinde "Kullanılmayan betik eklendi" gürültüsü çıkmaz
        var (_, k14Notes, _, _) = EvidenceBase.ComparePackage(
            @"{""scripts"":{""test"":""node t.js""}}",
            @"{""scripts"":{""test"":""node t.js"",""start"":""node app.js"",""dev"":""vite"",""build"":""vite build"",""lint"":""eslint .""}}",
            new HashSet<string>(StringComparer.Ordinal) { "test" },
            lockChanged: false);
        Assert.DoesNotContain(k14Notes, n => n.Contains("Kullanılmayan betik eklendi", StringComparison.OrdinalIgnoreCase));

        // u6: Teklif oluştuktan sonra diskteki test dosyası değişirse GET /api/jobs/{id}/test-offer editedId'yi tazeler ve onay geçer
        using var env = new TestEnv(copySamples: false);
        var u6Dir = YeniGeciciKlasor("u6-proje");
        Directory.CreateDirectory(Path.Combine(u6Dir, "test"));
        File.WriteAllText(Path.Combine(u6Dir, "package.json"), @"{""name"":""u6"",""scripts"":{""test"":""node test/u6.test.js""}}");
        File.WriteAllText(Path.Combine(u6Dir, "u6.js"), "module.exports = () => 2\n");
        var testFile = Path.Combine(u6Dir, "test", "u6.test.js");
        File.WriteAllText(testFile, "const assert = require('node:assert/strict')\nconst f = require('../u6')\nassert.equal(f(), 1)\n");

        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(u6Dir);
        w.AddCheck(pid, null, "command", "Testler", new JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 60, true);
        w.SetGate(pid, true);
        using (var c = env.Svc.Db.Open())
        {
            Assert.True(EvidenceBase.ApproveCurrent(c, pid));
        }

        var job = w.CreateJob(pid, "u6 düzelt");
        using (var c = env.Svc.Db.Open())
        {
            var approved = EvidenceBase.LoadApproved(c, pid)!;
            // İlk düzenleme ve teklif kaydı
            File.WriteAllText(testFile, "const assert = require('node:assert/strict')\nconst f = require('../u6')\nassert.equal(f(), 2)\n");
            var snap1 = EvidenceBase.ScanProject(c, pid, job.Id, u6Dir);
            EvidenceBase.RecordTestOffer(c, pid, job.Id, snap1, Array.Empty<string>(), new[] { "test/u6.test.js" }, approved);
        }

        string initialEditedId;
        using (var c = env.Svc.Db.Open())
        {
            initialEditedId = EvidenceBase.PendingOffer(c, job.Id)!.EditedId!;
        }

        // Tekliften sonra test dosyası diskte yeniden düzenlenir
        File.WriteAllText(testFile, "const assert = require('node:assert/strict')\nconst f = require('../u6')\nassert.strictEqual(f(), 2)\n");

        await using var server = await SozServer.StartAsync(env.DataDir, 0);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        var offer = await http.GetFromJsonAsync<JsonObject>($"/api/jobs/{job.Id}/test-offer");
        Assert.NotNull(offer);
        var freshEditedId = (string)offer!["editedId"]!;
        Assert.NotEqual(initialEditedId, freshEditedId);

        var approveRes = await http.SendAsync(Istek(HttpMethod.Post, $"/api/jobs/{job.Id}/approve-new-tests", server.Token, new
        {
            scope = "edited",
            diffId = freshEditedId
        }));
        Assert.Equal(HttpStatusCode.OK, approveRes.StatusCode);
    }

    [Fact]
    public async Task GizliAnahtar_InsanProjeAdiniBozmaz_SqliteYazmadaVeKanitCiktisindaMaskeler()
    {
        // İnsan yapımı proje adları gizli anahtar sayılmaz
        Assert.False(SecretFilter.ContainsSecret("KisiselWebSitesiProjesiV2Final klasöründeki başlığı düzelt"));
        Assert.False(SecretFilter.ContainsSecret("ders-notlari-2026-bahar-donemi içindeki dosyaları sırala"));
        Assert.Equal("KisiselWebSitesiProjesiV2Final", SecretFilter.MaskSecrets("KisiselWebSitesiProjesiV2Final"));
        Assert.Equal("ders-notlari-2026-bahar-donemi", SecretFilter.MaskSecrets("ders-notlari-2026-bahar-donemi"));

        // Gerçek sır biçimleri eksiksiz yakalanır
        var fakeOpenAi = "sk-proj-" + "A9b8C7d6E5f4G3h2J1k0L9m8N7p6Q5r4";
        var fakeGhp = "ghp_" + "1234567890abcdefABCDEF1234567890abcd";
        var fakeGoogle = "AIzaSy" + "D1234567890abcdefABCDEF123456789";
        var fakeAws = "AKIA" + "IOSFODNN7EXAMPLE";
        var fakeJwt = "eyJhbGciOi" + "JIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkttbCJ9.TJVA95OrM7E2cBab30RMHrHDcEfxjoYZgeFONFh7HgQ";
        var fakeSlack = "xo" + "xb-123456789012-123456789012-abcdefABCDEF123456789012";

        foreach (var secret in new[] { fakeOpenAi, fakeGhp, fakeGoogle, fakeAws, fakeJwt, fakeSlack })
        {
            Assert.True(SecretFilter.ContainsSecret("Anahtar: " + secret), secret);
            Assert.DoesNotContain(secret, SecretFilter.MaskSecrets("Anahtar: " + secret));
        }

        // QueueStore ve /api/proof/artifact maskeleme doğrulaması
        using var env = new TestEnv(copySamples: false);
        var w = new WorkService(env.Svc.Db);
        var projDir = YeniGeciciKlasor("kanit-maske");
        var pid = w.EnsureProjectByPath(projDir);
        var q = w.Enqueue(pid, null, "Sıradaki iş anahtarı " + fakeGhp);
        Assert.DoesNotContain(fakeGhp, q.Text);
        using (var c = env.Svc.Db.Open())
        {
            var diskQ = c.Scalar("SELECT text FROM queue WHERE id=@id", null, ("@id", q.Id)) as string ?? "";
            Assert.DoesNotContain(fakeGhp, diskQ);
        }

        w.AddCheck(pid, null, "command", "SirCiktisi", new JsonObject { ["cmd"] = $"node -e \"console.log('{fakeOpenAi}')\"", ["expectExit"] = 0 }, 30, true);
        var proofRun = w.RunProof(pid, null);
        Assert.NotNull(proofRun.Results);
        Assert.NotEmpty(proofRun.Results!);
        var artifactUrl = proofRun.Results![0].ArtifactUrl!;

        await using var server = await SozServer.StartAsync(env.DataDir, 0);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        var artText = await http.GetStringAsync(artifactUrl);
        Assert.DoesNotContain(fakeOpenAi, artText);
    }

    [Fact]
    public async Task PostProfileScan_CallerIdentity_Ve_25MsAltiHizliKanca()
    {
        Assert.True(CallerIdentity.IsAgentExecutableName("agy.exe"));
        Assert.True(CallerIdentity.IsAgentExecutableName("hermes"));
        Assert.True(CallerIdentity.IsAgentExecutableName("opencode.exe"));
        Assert.False(CallerIdentity.IsAgentExecutableName("explorer.exe"));
        Assert.True(CallerIdentity.IsClaudeCodeCommandLine(@"node ""C:\Tools\antigravity-cli\index.js"""));

        using var env = new TestEnv(copySamples: false);
        var projDir = YeniGeciciKlasor("hiz-kanca-proje");
        var w = new WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(projDir);
        w.ApproveSetup(pid, gate: false, guardTemplate: "temkinli");

        await using var server = await SozServer.StartAsync(env.DataDir, 0);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add(Soz.Core.Hooks.HookInstaller.TokenHeader, Soz.Core.Hooks.HookToken.GetOrCreate(env.DataDir)); // kanca ucu jetonlu (k6)

        // Isınma çağrısı
        var warmPayload = JsonSerializer.Serialize(new { session_id = "hiz-test-1", cwd = projDir, tool_name = "Read", tool_input = new { file_path = Path.Combine(projDir, "a.txt") } });
        var warmRes = await http.PostAsync("/api/hooks/pre-tool-use", new StringContent(warmPayload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, warmRes.StatusCode);

        // 10 ardışık pre-tool-use çağrısının ortalaması 25 ms altında olmalı
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            var r = await http.PostAsync("/api/hooks/pre-tool-use", new StringContent(warmPayload, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        sw.Stop();
        var avgMs = sw.Elapsed.TotalMilliseconds / 10.0;
        Assert.True(avgMs < 25.0, $"Ortalama kanca süresi {avgMs.ToString("F2", CultureInfo.InvariantCulture)} ms oldu");

        // Tehlikeli komutta temkinli bekçi şablonu kanca kararını (ask ya da deny) eksiksiz döndürmeli, allow vermemeli
        var dangerPayload = JsonSerializer.Serialize(new { session_id = "hiz-test-1", cwd = projDir, tool_name = "Bash", tool_input = new { command = "git reset --hard HEAD~3" } });
        var dangerRes = await http.PostAsync("/api/hooks/pre-tool-use", new StringContent(dangerPayload, Encoding.UTF8, "application/json"));
        var dangerJson = await dangerRes.Content.ReadAsStringAsync();
        Assert.Contains("permissionDecision", dangerJson, StringComparison.Ordinal);
        Assert.True(dangerJson.Contains("\"ask\"", StringComparison.Ordinal) || dangerJson.Contains("\"deny\"", StringComparison.Ordinal), dangerJson);

        // OpenFastForHook şema güncelken hızlı bağlantıyı açar
        var fast = SozDb.OpenFastForHook(env.DataDir);
        using (fast.Conn)
        {
            Assert.True(fast.IsCurrentSchema);
            Assert.True(fast.Conn.ScalarLong("SELECT COUNT(*) FROM settings") >= 0);
        }
    }
}
