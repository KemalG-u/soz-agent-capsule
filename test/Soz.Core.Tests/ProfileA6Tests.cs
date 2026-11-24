using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Onboarding;
using Soz.Core.Profile;
using Xunit;
using OnboardingScanContext = Soz.Core.Onboarding.ScanContext;

namespace Soz.Core.Tests;

/// <summary>a6: "şu an açık" hükmü süreç kanıtına dayanır; üst proje kuralı iki yönde; Antigravity, yardımcı oturum adları, Codex atlananlar, sınama yolları.</summary>
public sealed class ProfileA6Tests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    private static SessionMetadata S(string id, string? cwd, double minutesAgo, string tool = "claude", string? parent = null, string? title = null)
        => new(tool, id, cwd, null, Now.AddMinutes(-minutesAgo - 60), Now.AddMinutes(-minutesAgo), parent, "synthetic-" + id, Now, Now, "fixture", Title: title);

    private static ProjectResolver Resolver(IProjectFileSystem fs) => new(fs, new(@"C:\Users\synthetic", @"E:\SozData"));

    private static ProcessInventory Inventory(params ProcessObservation[] processes) => ProcessProbe.Inspect(new ProfileP2ExtraTests.FakeProcesses(processes), Now);

    internal sealed class ContractFs : IProjectFileSystem
    {
        public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Gits = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Markers = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Contracts = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string[]> Children = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectoryExists(string p) => Dirs.Contains(p);
        public bool HasGit(string p) => Gits.Contains(p);
        public bool HasMarker(string p) => Markers.Contains(p) || Contracts.Contains(p);
        public bool HasTests(string p) => false;
        public bool HasProjectContract(string p) => Contracts.Contains(p);
        public IEnumerable<string> ChildDirectories(string p) => Children.TryGetValue(p, out var list) ? list : [];
    }

    private static LiveSession[] LiveOf(ProjectResolver resolver, IReadOnlyList<SessionMetadata> rows, ProcessInventory? inventory, LiveHintContext? hints = null)
        => resolver.Build(rows, Now, inventory, hints).Live.SelectMany(l => l.Sessions).ToArray();

    // ---------------------------------------------------------------- 1. açık hükmü süreç kanıtına dayanır

    [Fact]
    public void OldSession_InSameFolderAsRunningProcess_IsNotOpen_BecauseProcessStartedAfterIt()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        // 10 gün önce yazılmış oturum, 1 gün önce başlamış süreç: süreç bu oturumun sahibi olamaz
        var rows = new[] { S("old", @"E:\P", 60 * 24 * 10) };
        var inventory = Inventory(new ProcessObservation("claude", 10, Now.AddDays(-1), CurrentDirectory: @"E:\P"));
        var live = LiveOf(Resolver(fs), rows, inventory);
        Assert.DoesNotContain(live, s => s.AcikAdayi);
        Assert.DoesNotContain(live, s => s.Id == "old");
    }

    [Fact]
    public void SessionWrittenAfterProcessStart_InSameFolder_IsOpenWithProcessEvidence()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var rows = new[] { S("fresh", @"E:\P", 30), S("stale", @"E:\P", 60 * 4) };
        var inventory = Inventory(new ProcessObservation("claude", 10, Now.AddHours(-3), CurrentDirectory: @"E:\P"));
        var open = LiveOf(Resolver(fs), rows, inventory).Where(s => s.AcikAdayi).ToArray();
        var one = Assert.Single(open);
        Assert.Equal("fresh", one.Id); Assert.Equal(LiveRules.EvidenceProcess, one.Evidence); Assert.Equal(10, one.Pid);
    }

    [Fact]
    public void ProcessWithUnknownFolder_OnlyRecentSessionsAreOpen_AsNearTime_NeverOlderThanThreshold()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var rows = new[] { S("recent", @"E:\P", 60), S("three-hours", @"E:\P", 180), S("ancient", @"E:\P", 60 * 24 * 12) };
        var inventory = Inventory(new ProcessObservation("claude", 1, Now), new ProcessObservation("claude", 2, Now), new ProcessObservation("claude", 3, Now));
        var open = LiveOf(Resolver(fs), rows, inventory).Where(s => s.AcikAdayi).ToArray();
        Assert.Equal(["recent"], open.Select(s => s.Id).ToArray());
        Assert.All(open, s => Assert.Equal(LiveRules.EvidenceNearTime, s.Evidence));
    }

    [Fact]
    public void SessionsWithoutProcess_AreNeverOpen_ButRecentOnesKeepNearTimeEvidence()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var live = LiveOf(Resolver(fs), [S("recent", @"E:\P", 20), S("today", @"E:\P", 60 * 5)], Inventory());
        Assert.All(live, s => Assert.False(s.AcikAdayi));
        Assert.Equal(LiveRules.EvidenceNearTime, live.Single(s => s.Id == "recent").Evidence);
        if (live.SingleOrDefault(s => s.Id == "today") is { } today) Assert.Equal(LiveRules.EvidenceNone, today.Evidence); // "bugün" penceresi saat dilimine bağlı
    }

    [Fact]
    public void ClaudeRegistry_BindsExactSession_EvenWhenSessionIsOld_AndIgnoresReusedPid()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var started = Now.AddDays(-2);
        var rows = new[] { S("old-open", @"E:\P", 60 * 24 * 3), S("other-old", @"E:\P", 60 * 24 * 5) };
        var inventory = Inventory(new ProcessObservation("claude", 7856, started, CurrentDirectory: @"E:\Baska"));
        var good = new LiveHintContext(ClaudeRegistry: [new ClaudeRegistryEntry(7856, "old-open", started, Now)]);
        var open = LiveOf(Resolver(fs), rows, inventory, good).Where(s => s.AcikAdayi).ToArray();
        var one = Assert.Single(open);
        Assert.Equal("old-open", one.Id); Assert.Equal(LiveRules.EvidenceProcess, one.Evidence); Assert.Equal(7856, one.Pid);

        // aynı pid başka bir sürece geçmiş (başlangıç saati uyuşmuyor): kayıt geçersiz, eski oturum açık sayılmaz
        var reused = new LiveHintContext(ClaudeRegistry: [new ClaudeRegistryEntry(7856, "old-open", started.AddHours(-9), Now)]);
        Assert.DoesNotContain(LiveOf(Resolver(fs), rows, inventory, reused), s => s.AcikAdayi);
    }

    [Fact]
    public void ClaudeRegistry_SessionStartedInSystemFolder_IsShownInProjectWhereProcessWorks()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.UnionWith([@"E:\P", @"C:\WINDOWS\system32"]); fs.Markers.Add(@"E:\P");
        var started = Now.AddHours(-5);
        var rows = new[] { S("anchor", @"E:\P", 600), S("terminal", @"C:\WINDOWS\system32", 3) };
        var inventory = Inventory(new ProcessObservation("claude", 50, started, CurrentDirectory: @"E:\P"));
        var hints = new LiveHintContext(ClaudeRegistry: [new ClaudeRegistryEntry(50, "terminal", started, Now)]);
        var result = Resolver(fs).Build(rows, Now, inventory, hints);
        var group = Assert.Single(result.Live, l => l.ProjectPath == @"E:\P");
        Assert.Contains(group.Sessions, s => s.Id == "terminal" && s.AcikAdayi && s.Evidence == LiveRules.EvidenceProcess);
        Assert.DoesNotContain(result.Live.Where(l => l.ProjectPath is null).SelectMany(l => l.Sessions), s => s.Id == "terminal");
    }

    [Fact]
    public void ClaudeRegistryReader_ReadsValidEntries_SkipsBrokenAndForeignFiles()
    {
        var home = ProfileMetadataTests.Fixture();
        var dir = Path.Combine(home, "sessions"); Directory.CreateDirectory(dir);
        var procStart = Now.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(dir, "123.json"), $$"""{"pid":123,"sessionId":"abc","procStart":"{{procStart}}","updatedAt":1795381382816,"cwd":"C:\\x"}""");
        File.WriteAllText(Path.Combine(dir, "456.json"), "{\"pid\":456,\"sessionId\":\"yarim");           // yarım yazılmış
        File.WriteAllText(Path.Combine(dir, "789.json"), """{"pid":1,"sessionId":"uyumsuz"}""");           // dosya adı ile pid farklı
        File.WriteAllText(Path.Combine(dir, "12.key"), "gizli");                                           // anahtar dosyası okunmaz
        File.WriteAllText(Path.Combine(dir, "abc.json"), """{"pid":5,"sessionId":"x"}""");
        var entries = ClaudeSessionRegistry.Read(home);
        var entry = Assert.Single(entries);
        Assert.Equal(123, entry.Pid); Assert.Equal("abc", entry.SessionId);
        Assert.Equal(Now, entry.ProcessStartedAt);
        Assert.Empty(ClaudeSessionRegistry.Read(Path.Combine(home, "yok")));
    }

    [Fact]
    public void LauncherWrapper_AndRealProcess_CountAsOneClaude()
    {
        var inventory = Inventory(
            new ProcessObservation("claude", 10, Now, ParentId: 5),                 // node sarmalayıcısı (komut satırından claude diye tanındı)
            new ProcessObservation("claude", 11, Now, ParentId: 10),                // asıl süreç
            new ProcessObservation("claude", 20, Now, ParentId: 5));                // başka bir terminal
        Assert.Equal(2, inventory.Tools.Single(t => t.Tool == "claude").Count);
        Assert.True(inventory.Processes.Single(p => p.Id == 10).IsHelperProcess);
        Assert.False(inventory.Processes.Single(p => p.Id == 11).IsHelperProcess);
    }

    [Fact]
    public void Hermes_OldSessionsAreNotOpen_WindowIsShownHonestly_RecentSessionIsNearTime()
    {
        var fs = new ProfileInventoryTests.FakeFs();
        var window = Inventory(new ProcessObservation("hermes", 5, Now.AddHours(-9), WindowHandle: 77));
        // klasörü bilinmeyen eski oturum, klasörü bilinmeyen süreçle "aynı proje" sanılmamalı (null == null tuzağı)
        var oldOnly = LiveOf(Resolver(fs), [S("h-old", null, 60 * 5, "hermes")], window);
        Assert.DoesNotContain(oldOnly, s => s.Id == "h-old" && s.AcikAdayi);
        var app = Assert.Single(oldOnly, s => s.AcikAdayi);
        Assert.Equal("uygulama-5", app.Id); Assert.Equal(LiveRules.EvidenceProcess, app.Evidence);

        var recent = LiveOf(Resolver(fs), [S("h-old", null, 60 * 5, "hermes"), S("h-new", null, 10, "hermes")], window).Where(s => s.AcikAdayi).ToArray();
        var one = Assert.Single(recent);
        Assert.Equal("h-new", one.Id); Assert.Equal(LiveRules.EvidenceNearTime, one.Evidence);
    }

    [Fact]
    public void Hermes_ActiveFile_IsIgnoredWithoutRunningProcess()
    {
        var fs = new ProfileInventoryTests.FakeFs();
        var hints = new LiveHintContext(HermesActiveIds: new HashSet<string> { "h1" });
        Assert.DoesNotContain(LiveOf(Resolver(fs), [S("h1", null, 30, "hermes")], Inventory(), hints), s => s.AcikAdayi);
        var withProcess = LiveOf(Resolver(fs), [S("h1", null, 30, "hermes")], Inventory(new ProcessObservation("hermes", 5, Now.AddHours(-1), WindowHandle: 9)), hints);
        Assert.Contains(withProcess, s => s.Id == "h1" && s.AcikAdayi && s.Evidence == LiveRules.EvidenceProcess);
    }

    [Fact]
    public void Hermes_SeveralProcessesOfOneWindow_AreOneWindow()
    {
        var fs = new ProfileInventoryTests.FakeFs();
        var inventory = Inventory(
            new ProcessObservation("hermes", 5, Now.AddHours(-1), WindowHandle: 9),
            new ProcessObservation("hermes", 6, Now.AddHours(-1), WindowHandle: 9),
            new ProcessObservation("hermes", 7, Now.AddHours(-1), WindowHandle: 9));
        var open = LiveOf(Resolver(fs), [S("a", null, 3, "hermes"), S("b", null, 4, "hermes"), S("c", null, 5, "hermes")], inventory).Where(s => s.AcikAdayi).ToArray();
        Assert.Single(open);
    }

    [Fact]
    public void Antigravity_InternalSidecarProcess_IsNotAWindow_AndSameWindowIsOneEntry()
    {
        var fs = new ProfileInventoryTests.FakeFs();
        var sidecar = @"C:\Users\x\.gemini\antigravity\builtin\plugins\swarm\sidecars\swarm_board";
        Assert.True(ProcessProbe.IsAssistantInternalPath(sidecar));
        Assert.False(ProcessProbe.IsAssistantInternalPath(@"E:\Projeler\agy"));
        var inventory = Inventory(
            new ProcessObservation("Antigravity", 25, Now.AddHours(-3), CurrentDirectory: @"C:\Users\x\AppData\Local\Programs\antigravity", WindowHandle: 68, WindowTitle: "Proje - Antigravity"),
            new ProcessObservation("Antigravity", 26, Now.AddHours(-3), CurrentDirectory: sidecar, WindowHandle: 68, WindowTitle: "Proje - Antigravity"),
            new ProcessObservation("Antigravity", 27, Now.AddHours(-3), CurrentDirectory: @"C:\Users\x\AppData\Local\Programs\antigravity", WindowHandle: 68, WindowTitle: "Proje - Antigravity"));
        var apps = LiveOf(Resolver(fs), [], inventory).Where(s => s.AcikAdayi).ToArray();
        Assert.Single(apps);
        Assert.Equal(LiveRules.EvidenceProcess, apps[0].Evidence);
    }

    [Fact]
    public void Antigravity_PresenceLockWithoutRunningProcess_IsNotOpen()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"D:\tools\alfa"); fs.Markers.Add(@"D:\tools\alfa");
        var hints = new LiveHintContext(AntigravityPresenceIds: new HashSet<string> { "agy" });
        var row = S("agy", @"D:\tools\alfa", 5, "antigravity");
        Assert.DoesNotContain(LiveOf(Resolver(fs), [row], Inventory(), hints), s => s.AcikAdayi);
    }

    [Fact]
    public void View_DropsStaleCandidates_AndOldSnapshotsWithoutEvidence_AreNotTrusted()
    {
        var db = SozDb.OpenAndMigrate(Path.Combine(ProfileMetadataTests.Fixture(), "data"));
        var service = new ProfileService(db);
        static string Row(string id, DateTime last, string? evidence, bool open)
            => "{\"tool\":\"claude\",\"id\":\"" + id + "\",\"lastActivity\":\"" + last.ToString("O", CultureInfo.InvariantCulture)
               + "\",\"level\":null,\"acikAdayi\":" + (open ? "true" : "false") + ",\"kind\":\"cikarim\",\"pid\":5"
               + (evidence is null ? "" : ",\"evidence\":\"" + evidence + "\"") + "}";
        var rowsJson = string.Join(",", Row("legacy-old", Now.AddDays(-3), null, true), Row("legacy-new", Now.AddMinutes(-30), null, true),
            Row("near-old", Now.AddDays(-2), LiveRules.EvidenceNearTime, true), Row("process-old", Now.AddDays(-12), LiveRules.EvidenceProcess, true));
        var body = "{\"projects\":[],\"live\":[{\"projectPath\":null,\"relations\":[],\"sessions\":[" + rowsJson + "]}]}";
        using (var c = db.Open()) c.Exec("INSERT INTO profile_snapshot(id,body) VALUES(1,@b)", null, ("@b", body));
        var sessions = service.View(Now)["live"]![0]!["sessions"]!.AsArray();
        var ids = sessions.Select(s => (string?)s!["id"]).ToArray();
        Assert.Equal(["legacy-new", "process-old"], ids.Order().ToArray());
        Assert.Equal(LiveRules.EvidenceNearTime, (string?)sessions.Single(s => (string?)s!["id"] == "legacy-new")!["evidence"]);
        Assert.Equal(LiveRules.EvidenceProcess, (string?)sessions.Single(s => (string?)s!["id"] == "process-old")!["evidence"]);
    }

    [Fact]
    public void LiveSessionJson_CarriesEvidenceField()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var live = Resolver(fs).Build([S("a", @"E:\P", 1)], Now, Inventory()).Live;
        var json = ProfileService.ToJson(new ProfileSnapshot(new(null, null, null, null, null, [], []), [], [], new(0, new Dictionary<string, int>(), Now), live,
            new(null, null, [], Now), new(0, 0, 0), 0, []));
        Assert.Equal("yakin-zaman", (string?)json["live"]![0]!["sessions"]![0]!["evidence"]);
        Assert.Equal(2.0, LiveRules.NearTime.TotalHours); Assert.Equal(24.0, LiveRules.StaleAfter.TotalHours);
    }

    // ---------------------------------------------------------------- 3. üst proje kuralı: iki yön

    [Fact]
    public void ChildGitProject_UnderSingleProjectParentWithContract_JoinsParent()
    {
        var fs = new ContractFs();
        fs.Dirs.UnionWith([@"E:\Is", @"E:\Is\urun", @"E:\Is\urun\src", @"E:\Is\calisma"]);
        fs.Contracts.Add(@"E:\Is"); fs.Gits.Add(@"E:\Is\urun");
        var result = Resolver(fs).Build([S("top", @"E:\Is", 5), S("kod", @"E:\Is\urun\src", 6), S("not", @"E:\Is\calisma", 7)], Now);
        var project = Assert.Single(result.Projects);
        Assert.Equal(@"E:\Is", project.Path);
        Assert.Equal(3, project.SessionsByTool["claude"]);
        Assert.Contains(project.Areas, a => a.Path == @"E:\Is\urun");
        Assert.Equal(0, result.Unassigned.Count);
    }

    [Fact]
    public void ChildGitProject_ParentWithContractButNoOwnSessions_StillJoinsParent()
    {
        var fs = new ContractFs();
        fs.Dirs.UnionWith([@"E:\Is", @"E:\Is\urun"]);
        fs.Contracts.Add(@"E:\Is"); fs.Gits.Add(@"E:\Is\urun");
        var result = Resolver(fs).Build([S("kod", @"E:\Is\urun", 5)], Now);
        Assert.Equal(@"E:\Is", Assert.Single(result.Projects).Path);
    }

    [Fact]
    public void WorkspaceFolder_WithSeveralProjects_DoesNotSwallowThem_EvenWithContract()
    {
        var fs = new ContractFs();
        fs.Dirs.UnionWith([@"D:\Calisma", @"D:\Calisma\a", @"D:\Calisma\b"]);
        fs.Contracts.Add(@"D:\Calisma"); fs.Gits.UnionWith([@"D:\Calisma\a", @"D:\Calisma\b"]);
        var all = Resolver(fs).Build([S("root", @"D:\Calisma", 5), S("a", @"D:\Calisma\a", 5), S("b", @"D:\Calisma\b", 5)], Now);
        Assert.Equal(3, all.Projects.Count);
        var childrenOnly = Resolver(fs).Build([S("a", @"D:\Calisma\a", 5), S("b", @"D:\Calisma\b", 5)], Now);
        Assert.Equal(2, childrenOnly.Projects.Count);
        Assert.DoesNotContain(childrenOnly.Projects, p => p.Path == @"D:\Calisma");
    }

    [Fact]
    public void WorkspaceFolder_SiblingProjectsKnownOnlyFromDisk_AlsoCountAsWorkspace()
    {
        var fs = new ContractFs();
        fs.Dirs.UnionWith([@"D:\Calisma", @"D:\Calisma\a", @"D:\Calisma\b"]);
        fs.Contracts.Add(@"D:\Calisma"); fs.Gits.UnionWith([@"D:\Calisma\a", @"D:\Calisma\b"]);
        fs.Children[@"D:\Calisma"] = [@"D:\Calisma\a", @"D:\Calisma\b"];
        var result = Resolver(fs).Build([S("a", @"D:\Calisma\a", 5)], Now);
        Assert.Equal(@"D:\Calisma\a", Assert.Single(result.Projects).Path);
    }

    [Fact]
    public void ProjectInsideProject_WithoutContractOnParent_StaysSeparate_AndAnotherProjectBetweenStopsTheSearch()
    {
        var plain = new ContractFs();
        plain.Dirs.UnionWith([@"E:\Is", @"E:\Is\urun"]);
        plain.Markers.UnionWith([@"E:\Is", @"E:\Is\urun"]); plain.Gits.Add(@"E:\Is\urun");
        Assert.Equal(2, Resolver(plain).Build([S("a", @"E:\Is", 5), S("b", @"E:\Is\urun", 5)], Now).Projects.Count);

        var between = new ContractFs();
        between.Dirs.UnionWith([@"E:\Is", @"E:\Is\orta", @"E:\Is\orta\x"]);
        between.Contracts.Add(@"E:\Is"); between.Gits.UnionWith([@"E:\Is\orta", @"E:\Is\orta\x"]);
        var paths = Resolver(between).Build([S("x", @"E:\Is\orta\x", 5)], Now).Projects.Select(p => p.Path).ToArray();
        Assert.DoesNotContain(@"E:\Is", paths);
    }

    [Fact]
    public void RealProjectFileSystem_DetectsContractFilesAndChildFolders()
    {
        var root = ProfileMetadataTests.Fixture();
        var child = Path.Combine(root, "alt"); Directory.CreateDirectory(child);
        var fs = new ProjectFileSystem();
        Assert.False(fs.HasProjectContract(root));
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "x");
        Assert.True(fs.HasProjectContract(root));
        Assert.Contains(child, fs.ChildDirectories(root));
        Assert.Empty(fs.ChildDirectories(Path.Combine(root, "yok")));
    }

    // ---------------------------------------------------------------- 2. Antigravity ve yardımcı oturum adları

    [Fact]
    public void Antigravity_ChildSessions_InheritFolderThroughSeveralAncestors()
    {
        var path = Path.Combine(ProfileMetadataTests.Fixture(), "conversation_summaries.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            c.Exec("CREATE TABLE conversation_summaries(conversation_id TEXT PRIMARY KEY,title TEXT,last_modified_time TEXT,workspace_uris TEXT,status TEXT,parent_conversation_id TEXT,not_fully_idle INTEGER)");
            void Add(string id, string? uris, string? parent) => c.Exec("INSERT INTO conversation_summaries VALUES(@i,NULL,@t,@u,'',@p,0)", null,
                ("@i", id), ("@t", Now.ToString("O", CultureInfo.InvariantCulture)), ("@u", uris), ("@p", parent));
            Add("kok", """["file:///d%3A/tools/proje-alfa"]""", null);
            Add("orta", """["file:///e%3A/"]""", "kok");
            Add("torun", """["file:///e%3A/"]""", "orta");
            Add("yetim", """["file:///e%3A/"]""", null);
            Add("yetim-alt", """["file:///e%3A/"]""", "yetim");
        }
        var rows = ProfileSources.ReadAntigravityDb(path, Now).ToDictionary(r => r.Id);
        Assert.Equal(@"D:\tools\proje-alfa", rows["orta"].Cwd);
        Assert.Equal(@"D:\tools\proje-alfa", rows["torun"].Cwd);
        // kendi kaydında klasör olmayan oturum uydurulmaz
        Assert.Equal(@"E:\", rows["yetim"].Cwd);
        Assert.Equal(@"E:\", rows["yetim-alt"].Cwd);
    }

    [Fact]
    public void SubSessionTitles_UseParentTitle_AndOrder_AndNeverOverwriteOwnTitle()
    {
        var parent = S("ana", @"E:\Proje", 100, title: "Bitki listesi");
        var rows = new List<SessionMetadata>
        {
            parent,
            S("c2", @"E:\Proje", 50, parent: "ana"), S("c1", @"E:\Proje", 90, parent: "ana"),
            S("kendi", @"E:\Proje", 70, parent: "ana", title: "Özel ad"),
        };
        var filled = SubSessionTitles.Fill(rows).ToDictionary(r => r.Id);
        Assert.Equal("Bitki listesi", filled["ana"].Title);
        Assert.Equal("Bitki listesi (yardımcı 1)", filled["c1"].Title);
        Assert.Equal("Özel ad", filled["kendi"].Title);
        Assert.StartsWith("Bitki listesi (yardımcı ", filled["c2"].Title);
        Assert.NotEqual(filled["c1"].Title, filled["c2"].Title);

        var noTitle = SubSessionTitles.Fill([S("p", @"E:\Soz", 10), S("k", @"E:\Soz", 5, parent: "p")]).Single(r => r.Id == "k");
        Assert.Equal("Soz (yardımcı 1)", noTitle.Title);
        var generic = SubSessionTitles.Fill([S("p", @"C:\WINDOWS\system32", 10), S("k", @"C:\WINDOWS\system32", 5, parent: "p")]).Single(r => r.Id == "k");
        Assert.Equal("Yardımcı pencere 1", generic.Title);
    }

    [Fact]
    public void SessionsEndpointData_FillsSubSessionTitles_FromParentOutsideTheLimit()
    {
        var r = new ProfileInventoryTests.Rig();
        r.Claude("ana", @"E:\P");
        for (var i = 0; i < 3; i++) r.Claude("alt" + i, @"E:\P", "ana");
        var service = r.Service(); service.Scan(r.Context());
        var data = service.Sessions(@"E:\P", 10);
        var titles = data["sessions"]!.AsArray().Select(s => (string?)s!["title"]).Where(t => t is not null).ToArray();
        Assert.Equal(3, titles.Length);
        Assert.All(titles, t => Assert.Contains("yardımcı", t!, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 4c. Codex'te atlananlar

    [Fact]
    public void Codex_SkippedCompressedAndOlderStateFiles_AreCountedAsUnreadable_AndWarned()
    {
        var root = ProfileMetadataTests.Fixture();
        var db = SozDb.OpenAndMigrate(Path.Combine(root, "data"));
        var roots = OnboardingRoots.Under(Path.Combine(root, "home"));
        var sessionsDir = Path.Combine(roots.CodexHome, "sessions", "2026", "10"); Directory.CreateDirectory(sessionsDir);
        const string known = "01a0fbfc-ebb8-7242-bbd2-b0591536d9b0", unknown1 = "02b1fbfc-ebb8-7242-bbd2-b0591536d9b1", unknown2 = "03c2fbfc-ebb8-7242-bbd2-b0591536d9b2";
        using (var c = new SqliteConnection($"Data Source={Path.Combine(roots.CodexHome, "state_5.sqlite")};Pooling=False"))
        {
            c.Open();
            c.Exec("CREATE TABLE threads(id TEXT PRIMARY KEY,cwd TEXT,created_at INTEGER,updated_at INTEGER,git_branch TEXT,title TEXT)");
            var epoch = (long)(Now - DateTime.UnixEpoch).TotalSeconds;
            c.Exec("INSERT INTO threads VALUES(@i,@c,@a,@a,'main','T')", null, ("@i", known), ("@c", @"E:\P"), ("@a", epoch));
        }
        File.WriteAllBytes(Path.Combine(sessionsDir, $"rollout-2026-10-02T12-40-51-{known}.jsonl.zst"), [0x28, 0xB5, 0x2F, 0xFD]);
        File.WriteAllBytes(Path.Combine(sessionsDir, $"rollout-2026-10-02T12-41-51-{unknown1}.jsonl.zst"), [0x28, 0xB5, 0x2F, 0xFD]);
        File.WriteAllBytes(Path.Combine(sessionsDir, $"rollout-2026-10-02T12-42-51-{unknown2}.jsonl.zst"), [0x28, 0xB5, 0x2F, 0xFD]);
        File.WriteAllBytes(Path.Combine(roots.CodexHome, "state_4.sqlite"), [1, 2, 3]); // eski sürüm özet, okunmaz
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var svc = new ProfileService(db)
        {
            Machine = new ProfileInventoryTests.FakeMachine(), Processes = new ProfileP2ExtraTests.FakeProcesses(), FileSystem = fs,
            ExclusionsFactory = r => new ProjectExclusions(r.Home, db.DataDir),
        };
        var snap = svc.Scan(new OnboardingScanContext { Roots = roots, Probe = new NullSystemProbe(), UtcNow = () => Now });
        var codex = snap.Sources.Single(s => s.Tool == "codex");
        Assert.Equal(2, codex.Reasons[ProfileService.SkippedCompressed]);
        Assert.Equal(1, codex.Reasons[ProfileService.SkippedOlderState]);
        Assert.Equal(3, codex.Okunamayan);
        Assert.Contains(snap.Warnings!, w => w.Contains("Codex") && w.Contains("sıkıştırılmış 2 kayıt dosyası"));
        Assert.Contains(snap.Warnings!, w => w.Contains("Codex") && w.Contains("eski sürümden kalma özet dosyası"));
        Assert.DoesNotContain(snap.Warnings!, w => w.Contains("bozuk"));
        // görünüm de aynı uyarıyı taşır
        Assert.Equal(snap.Warnings!.Count, svc.View()["warnings"]!.AsArray().Count);
    }

    [Fact]
    public void Codex_NothingSkipped_MeansNoWarning()
    {
        var root = ProfileMetadataTests.Fixture();
        var db = SozDb.OpenAndMigrate(Path.Combine(root, "data"));
        var roots = OnboardingRoots.Under(Path.Combine(root, "home"));
        Directory.CreateDirectory(Path.Combine(roots.CodexHome, "sessions"));
        const string known = "01a0fbfc-ebb8-7242-bbd2-b0591536d9b0";
        using (var c = new SqliteConnection($"Data Source={Path.Combine(roots.CodexHome, "state_5.sqlite")};Pooling=False"))
        {
            c.Open();
            c.Exec("CREATE TABLE threads(id TEXT PRIMARY KEY,cwd TEXT,created_at INTEGER,updated_at INTEGER,git_branch TEXT,title TEXT)");
            c.Exec("INSERT INTO threads VALUES(@i,@c,1,1,'main','T')", null, ("@i", known), ("@c", @"E:\P"));
        }
        File.WriteAllBytes(Path.Combine(roots.CodexHome, "sessions", $"rollout-x-{known}.jsonl.zst"), [0x28]);
        var svc = new ProfileService(db)
        {
            Machine = new ProfileInventoryTests.FakeMachine(), Processes = new ProfileP2ExtraTests.FakeProcesses(), FileSystem = new ProfileInventoryTests.FakeFs(),
            ExclusionsFactory = r => new ProjectExclusions(r.Home, db.DataDir),
        };
        var snap = svc.Scan(new OnboardingScanContext { Roots = roots, Probe = new NullSystemProbe(), UtcNow = () => Now });
        Assert.Equal(0, snap.Sources.Single(s => s.Tool == "codex").Okunamayan);
        Assert.Empty(snap.Warnings!);
    }

    // ---------------------------------------------------------------- 5. sınama ve geçici yollar

    [Theory]
    [InlineData(@"E:\Work\_gecici\canli-proje")]
    [InlineData(@"E:\Work\canli-proje")]
    [InlineData(@"E:\Work\veri-deneme\x")]
    [InlineData(@"E:\Work\sinama")]
    [InlineData(@"C:\Users\synthetic\AppData\Local\Temp\proje")]
    public void OwnTestProjects_AndTemporaryPaths_NeverBecomeProjects(string path)
    {
        var result = Resolver(new ProfileInventoryTests.FakeFs()).Build([S("a", path, 5)], Now);
        Assert.Empty(result.Projects);
        Assert.Equal(1, result.Unassigned.Count);
    }

    [Fact]
    public void TestFolderUnderRealProject_ResolvesToThatProject()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.UnionWith([@"E:\P", @"E:\P\_gecici\canli-proje"]); fs.Markers.Add(@"E:\P");
        Assert.Equal(@"E:\P", Assert.Single(Resolver(fs).Build([S("a", @"E:\P\_gecici\canli-proje", 5)], Now).Projects).Path);
    }
}
