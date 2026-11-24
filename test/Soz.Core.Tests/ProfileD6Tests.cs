using System.Globalization;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Onboarding;
using Soz.Core.Profile;
using Xunit;
using OnboardingScanContext = Soz.Core.Onboarding.ScanContext;

namespace Soz.Core.Tests;

/// <summary>
/// a6 / denetim 6: Claude açık oturumları birincil olarak `claude agents --json` komutundan (sahte JSON ile), taze "şu an açık" hesabı,
/// klasörü olmayan proje, byReason doğruluğu, Hermes sınama düzeneği ve biçimi tanınmayan kayıt.
/// </summary>
public sealed class ProfileD6Tests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    private static SessionMetadata S(string id, string? cwd, double minutesAgo, string tool = "claude", string? parent = null)
        => new(tool, id, cwd, null, Now.AddMinutes(-minutesAgo - 60), Now.AddMinutes(-minutesAgo), parent, "synthetic-" + id, Now, Now, "fixture");

    private static ProjectResolver Resolver(IProjectFileSystem fs) => new(fs, new(@"C:\Users\synthetic", @"E:\SozData"));
    private static ProcessInventory Inventory(params ProcessObservation[] processes) => ProcessProbe.Inspect(new ProfileP2ExtraTests.FakeProcesses(processes), Now);

    private static long Ms(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static ClaudeRegistryEntry Agent(int pid, string session, string status, DateTime started, string? waitingFor = null, string? name = null, string? cwd = null)
        => new(pid, session, null, null, cwd, "interactive", started, name, status, waitingFor, FromCommand: true);

    private static LiveHintContext Strict(params ClaudeRegistryEntry[] entries) => new(ClaudeRegistry: entries, ClaudeSource: LiveRules.SourceCommand);

    private sealed class FakeAgents(Func<string?> json, string? failure = null) : IClaudeAgentsCommand
    {
        public int Calls;
        public string? Run(out string? fail) { Calls++; fail = failure; return json(); }
    }

    // ---------------------------------------------------------------- claude agents --json ayrıştırma

    [Fact]
    public void AgentsParser_ReadsFields_AndMapsStatesToTurkish()
    {
        var json = $$"""
            [
              {"pid":7856,"cwd":"C:\\WINDOWS\\system32","kind":"interactive","startedAt":{{Ms(Now)}},"sessionId":"a-1","name":"system32-30","status":"busy"},
              {"pid":23140,"cwd":"E:\\P","kind":"background","startedAt":{{Ms(Now)}},"sessionId":"a-2","name":"urun","status":"waiting","waitingFor":"izin"},
              {"pid":8700,"cwd":"E:\\P","kind":"interactive","startedAt":{{Ms(Now)}},"sessionId":"a-3","status":"idle"},
              {"pid":9,"cwd":"E:\\P","sessionId":"a-4","status":"shell"},
              {"pid":10,"sessionId":"a-5","status":"busy","waitingFor":"onay bekliyor"}
            ]
            """;
        var list = ClaudeAgents.Parse(json)!;
        Assert.Equal(5, list.Count);
        Assert.All(list, e => Assert.True(e.FromCommand));
        Assert.Equal(7856, list[0].Pid); Assert.Equal("a-1", list[0].SessionId); Assert.Equal(@"C:\WINDOWS\system32", list[0].Cwd); Assert.Equal(Now, list[0].StartedAt);
        Assert.Equal("system32-30", list[0].Name); Assert.Equal("interactive", list[0].Kind);
        Assert.Equal("background", list[1].Kind);
        Assert.Equal(LiveRules.StateWorking, list[0].State);
        Assert.Equal(LiveRules.StateWaiting, list[1].State);
        Assert.Equal(LiveRules.StateIdle, list[2].State);
        Assert.Equal(LiveRules.StateUnknown, list[3].State);
        Assert.Equal(LiveRules.StateWaiting, list[4].State); // bekleme nedeni varsa durum ne olursa olsun
        Assert.Equal("onay bekliyor", list[4].WaitingFor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"pid\":1}")]
    [InlineData("claude: komut tanınmadı")]
    [InlineData("[{\"pid\":1,")]
    public void AgentsParser_RejectsOutputThatIsNotAnArray(string text) => Assert.Null(ClaudeAgents.Parse(text));

    [Fact]
    public void AgentsParser_EmptyArrayIsValid_AndEntriesWithoutPidOrSessionAreSkipped()
    {
        Assert.Empty(ClaudeAgents.Parse("[]")!);
        var list = ClaudeAgents.Parse("""[{"sessionId":"x"},{"pid":5},{"pid":-4,"sessionId":"n"},{"pid":6,"sessionId":"ok"},"metin",7]""")!;
        Assert.Equal("ok", Assert.Single(list).SessionId);
    }

    // ---------------------------------------------------------------- komut yoksa kayıt dosyası, yedeğe düştüğü söylenir

    private static string ClaudeHomeWithRegistry()
    {
        var home = ProfileMetadataTests.Fixture();
        var dir = Path.Combine(home, "sessions"); Directory.CreateDirectory(dir);
        var procStart = Now.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(dir, "123.json"), $$"""{"pid":123,"sessionId":"abc","cwd":"C:\\x","startedAt":{{Ms(Now)}},"procStart":"{{procStart}}","updatedAt":{{Ms(Now)}},"name":"kayit-adi","status":"busy"}""");
        return home;
    }

    [Fact]
    public void AgentsRead_CommandSucceeds_IsPrimaryAndHasNoNote()
    {
        var cmd = new FakeAgents(() => """[{"pid":5,"sessionId":"s5","status":"idle"}]""");
        var r = ClaudeAgents.Read(cmd, ClaudeHomeWithRegistry());
        Assert.Equal(LiveRules.SourceCommand, r.Source); Assert.Null(r.Note);
        Assert.Equal("s5", Assert.Single(r.Entries).SessionId); // kayıt dosyasındaki abc karışmaz
    }

    [Theory]
    [InlineData(null, "komut-yok", "bulunamadı")]
    [InlineData(null, "zaman-asimi", "süresinde")]
    [InlineData(null, "eski-surum", "sürümü")]
    [InlineData("claude: bilinmeyen komut", null, "tanınmadı")]
    public void AgentsRead_CommandFails_FallsBackToRegistryFiles_AndSaysSo(string? output, string? failure, string expected)
    {
        var cmd = new FakeAgents(() => output, failure);
        var r = ClaudeAgents.Read(cmd, ClaudeHomeWithRegistry());
        Assert.Equal(LiveRules.SourceRegistry, r.Source);
        Assert.Equal(ClaudeAgents.SoftNote, r.Note); Assert.NotNull(r.Technical); Assert.Contains(expected, r.Technical); // kişiye sade cümle, neden teknik alanda
        var entry = Assert.Single(r.Entries);
        Assert.Equal("abc", entry.SessionId); Assert.False(entry.FromCommand);
        Assert.Equal("kayit-adi", entry.Name); Assert.Equal(LiveRules.StateWorking, entry.State); Assert.Equal(Now, entry.StartedAt);
    }

    [Fact]
    public void AgentsRead_CommandFailsAndNoRegistry_IsProcessEstimate()
    {
        var r = ClaudeAgents.Read(new FakeAgents(() => null, "komut-yok"), ProfileMetadataTests.Fixture());
        Assert.Equal(LiveRules.SourceProcess, r.Source); Assert.Empty(r.Entries); Assert.NotNull(r.Note);
    }

    [Fact]
    public void AgentsRead_NoCommand_UsesRegistryQuietly()
    {
        var r = ClaudeAgents.Read(null, ClaudeHomeWithRegistry());
        Assert.Equal(LiveRules.SourceRegistry, r.Source); Assert.Null(r.Note);
    }

    // ---------------------------------------------------------------- listeye göre bağlama

    [Fact]
    public void AgentsList_IsAuthoritative_ProcessesNotInListAreNotOpen_AndNoGuessing()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var started = Now.AddDays(-2);
        var rows = new[] { S("listed-old", @"E:\P", 60 * 24 * 3), S("recent-other", @"E:\P", 10) };
        var inventory = Inventory(
            new ProcessObservation("claude", 7856, started, CurrentDirectory: @"E:\Baska"),
            new ProcessObservation("claude", 99, Now.AddMinutes(-20), CurrentDirectory: @"E:\P")); // listede yok: terminal değil
        var live = Resolver(fs).Build(rows, Now, inventory, Strict(Agent(7856, "listed-old", "busy", started.AddSeconds(4), name: "ad-1"))).Live.SelectMany(l => l.Sessions).ToArray();
        var open = Assert.Single(live, s => s.AcikAdayi);
        Assert.Equal("listed-old", open.Id); Assert.Equal(LiveRules.EvidenceProcess, open.Evidence);
        Assert.Equal(LiveRules.StateWorking, open.State); Assert.Equal(7856, open.Pid);
        var other = Assert.Single(live, s => s.Id == "recent-other");
        Assert.False(other.AcikAdayi);
        Assert.Equal(LiveRules.StateUnknown, other.State);
    }

    [Fact]
    public void AgentsList_Empty_MeansNothingIsOpen_EvenWithClaudeProcesses()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var inventory = Inventory(new ProcessObservation("claude", 99, Now.AddMinutes(-20), CurrentDirectory: @"E:\P"));
        var live = Resolver(fs).Build([S("recent", @"E:\P", 10)], Now, inventory, Strict()).Live.SelectMany(l => l.Sessions);
        Assert.DoesNotContain(live, s => s.AcikAdayi);
    }

    [Theory]
    [InlineData("busy", null, LiveRules.StateWorking)]
    [InlineData("waiting", null, LiveRules.StateWaiting)]
    [InlineData("idle", null, LiveRules.StateIdle)]
    [InlineData("garip", null, LiveRules.StateUnknown)]
    [InlineData("busy", "Bash komutu için izin", LiveRules.StateWaiting)]
    public void AgentsList_CarriesStateAndWaitingText(string status, string? waitingFor, string state)
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var started = Now.AddHours(-3);
        var inventory = Inventory(new ProcessObservation("claude", 50, started, CurrentDirectory: @"C:\WINDOWS\system32"));
        var live = Resolver(fs).Build([S("s", @"E:\P", 30)], Now, inventory, Strict(Agent(50, "s", status, started.AddSeconds(4), waitingFor))).Live.SelectMany(l => l.Sessions);
        var row = Assert.Single(live, s => s.AcikAdayi);
        Assert.Equal(state, row.State); Assert.Equal(waitingFor, row.WaitingFor);
    }

    [Fact]
    public void AgentsList_SessionStartedInSystemFolder_IsBoundToProjectBySessionIdRecord()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.UnionWith([@"E:\P", @"C:\WINDOWS\system32"]); fs.Markers.Add(@"E:\P");
        var started = Now.AddHours(-5);
        var inventory = Inventory(new ProcessObservation("claude", 50, started, CurrentDirectory: @"C:\WINDOWS\system32"));
        var hints = Strict(Agent(50, "terminal", "busy", started.AddSeconds(4), cwd: @"C:\WINDOWS\system32"));
        var result = Resolver(fs).Build([S("terminal", @"E:\P", 3)], Now, inventory, hints);
        var group = Assert.Single(result.Live, l => l.ProjectPath == @"E:\P");
        Assert.Contains(group.Sessions, s => s.Id == "terminal" && s.AcikAdayi && s.State == LiveRules.StateWorking);
    }

    [Fact]
    public void AgentsList_SessionNotScannedYet_StillShowsAsOpen_InProcessFolderProject()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var started = Now.AddMinutes(-4);
        var inventory = Inventory(new ProcessObservation("claude", 61, started, CurrentDirectory: @"E:\P"));
        var result = Resolver(fs).Build([S("anchor", @"E:\P", 600)], Now, inventory, Strict(Agent(61, "brand-new", "idle", started.AddSeconds(4), name: "yeni-oturum")));
        var group = Assert.Single(result.Live, l => l.ProjectPath == @"E:\P");
        var row = Assert.Single(group.Sessions, s => s.Id == "brand-new");
        Assert.True(row.AcikAdayi); Assert.Equal(LiveRules.EvidenceProcess, row.Evidence); Assert.Equal(LiveRules.StateIdle, row.State); Assert.Equal(61, row.Pid);
    }

    [Fact]
    public void AgentsList_PidReusedByOtherProgram_OrWrongStart_IsIgnored()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var started = Now.AddHours(-2);
        var rows = new[] { S("s", @"E:\P", 20) };
        // pid artık başka bir yardımcı programın (claude değil)
        var other = Resolver(fs).Build(rows, Now, Inventory(new ProcessObservation("codex", 50, started)), Strict(Agent(50, "s", "busy", started))).Live.SelectMany(l => l.Sessions);
        Assert.DoesNotContain(other, s => s.AcikAdayi);
        // pid aynı ama süreç komutun bildirdiğinden çok sonra başlamış (pid başka sürece geçmiş)
        var reused = Resolver(fs).Build(rows, Now, Inventory(new ProcessObservation("claude", 50, started.AddHours(1))), Strict(Agent(50, "s", "busy", started))).Live.SelectMany(l => l.Sessions);
        Assert.DoesNotContain(reused, s => s.AcikAdayi);
    }

    [Fact]
    public void RegistryFiles_AreNotStrict_AndNeedAProcessToCount()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var started = Now.AddHours(-2);
        var entry = new ClaudeRegistryEntry(50, "s", started, Now, Status: "waiting");
        var hints = new LiveHintContext(ClaudeRegistry: [entry], ClaudeSource: LiveRules.SourceRegistry);
        var noProcess = Resolver(fs).Build([S("s", @"E:\P", 20)], Now, Inventory(), hints).Live.SelectMany(l => l.Sessions);
        Assert.DoesNotContain(noProcess, s => s.AcikAdayi);
        var withProcess = Resolver(fs).Build([S("s", @"E:\P", 20)], Now, Inventory(new ProcessObservation("claude", 50, started)), hints).Live.SelectMany(l => l.Sessions);
        var row = Assert.Single(withProcess, s => s.AcikAdayi);
        Assert.Equal(LiveRules.StateWaiting, row.State);
    }

    // ---------------------------------------------------------------- taze "şu an açık"

    private sealed class Rig
    {
        public required ProfileService Svc; public required OnboardingScanContext Ctx; public required FakeAgents Cmd; public required SozDb Db;
        public DateTime Clock; public Action<string> SetJson = _ => { };
    }

    private static Rig FreshRig()
    {
        var root = ProfileMetadataTests.Fixture();
        var db = SozDb.OpenAndMigrate(Path.Combine(root, "data"));
        var roots = OnboardingRoots.Under(Path.Combine(root, "home"));
        var dir = Path.Combine(roots.ClaudeHome, "projects", "E--P"); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sess-1.jsonl");
        File.WriteAllText(file, """{"type":"user","sessionId":"sess-1","cwd":"E:\\P","timestamp":"2026-11-12T11:50:00Z"}""" + "\n");
        File.SetLastWriteTimeUtc(file, Now.AddMinutes(-5));
        var started = Now.AddHours(-1);
        var json = "";
        void SetJson(string status) => json = $$"""[{"pid":77,"cwd":"C:\\WINDOWS\\system32","kind":"interactive","startedAt":{{Ms(started.AddSeconds(4))}},"sessionId":"sess-1","name":"p-1","status":"{{status}}"}]""";
        SetJson("busy");
        var cmd = new FakeAgents(() => json);
        var rig = new Rig { Svc = null!, Ctx = null!, Cmd = cmd, Db = db, Clock = Now, SetJson = SetJson };
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var svc = new ProfileService(db)
        {
            Machine = new ProfileInventoryTests.FakeMachine(),
            Processes = new ProfileP2ExtraTests.FakeProcesses(new ProcessObservation("claude", 77, started, CurrentDirectory: @"E:\P")),
            FileSystem = fs, ExclusionsFactory = r => new ProjectExclusions(r.Home, db.DataDir),
            AgentsCommand = cmd, Clock = () => rig.Clock, RefreshSyntheticRoots = true,
        };
        rig.Svc = svc;
        rig.Ctx = new OnboardingScanContext { Roots = roots, Probe = new NullSystemProbe(), UtcNow = () => Now };
        return rig;
    }

    private static DateTime Stamp(JsonNode? node) => DateTime.Parse((string)node!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static JsonNode? SessOf(JsonObject view, string id) => view["live"]!.AsArray().SelectMany(g => g!["sessions"]!.AsArray()).FirstOrDefault(s => (string?)s!["id"] == id);

    [Fact]
    public void View_RecomputesLiveFresh_CachesBriefly_AndReportsWhenAndFrom()
    {
        var rig = FreshRig(); var svc = rig.Svc; var ctx = rig.Ctx;
        var snapshot = svc.Scan(ctx);
        Assert.Equal(LiveRules.SourceCommand, snapshot.LiveSource); Assert.Equal(Now, snapshot.LiveObservedAt);
        Assert.Equal(1, rig.Cmd.Calls);

        var first = svc.View(null, () => ctx);
        Assert.Equal(2, rig.Cmd.Calls);
        Assert.Equal(LiveRules.SourceCommand, (string?)first["liveSource"]);
        Assert.Equal(Now, Stamp(first["liveObservedAt"]));
        var row = SessOf(first, "sess-1")!;
        Assert.True((bool?)row["acikAdayi"]);
        Assert.Equal("surec", (string?)row["evidence"]); Assert.Equal("calisiyor", (string?)row["state"]); Assert.Equal(77, (int?)row["pid"]);

        // süre dolmadan: aynı sonuç, komut yeniden çalışmaz
        rig.SetJson("waiting"); rig.Clock = Now.AddSeconds(3);
        var cached = svc.View(null, () => ctx);
        Assert.Equal(2, rig.Cmd.Calls);
        Assert.Equal("calisiyor", (string?)SessOf(cached, "sess-1")!["state"]);

        // süre doldu: baştan hesaplanır ve zaman damgası ilerler
        rig.Clock = Now.AddSeconds(65);   // komut aralığı bakarken 60 sn (LiveRules.LiveCommandActiveInterval)
        var fresh = svc.View(null, () => ctx);
        Assert.Equal(3, rig.Cmd.Calls);
        Assert.Equal("seni-bekliyor", (string?)SessOf(fresh, "sess-1")!["state"]);
        Assert.Equal(Now.AddSeconds(65), Stamp(fresh["liveObservedAt"]));

        // sabit zamanlı görünüm (testler) kayıtlı taramayı kullanır ve komut çalıştırmaz
        var stored = svc.View(Now, () => ctx);
        Assert.Equal(3, rig.Cmd.Calls);
        Assert.Equal(LiveRules.SourceCommand, (string?)stored["liveSource"]);
    }

    private static ProfileService Twin(Rig rig, IClaudeAgentsCommand command, DateTime clock) => new(rig.Db)
    {
        Machine = rig.Svc.Machine, Processes = rig.Svc.Processes, FileSystem = rig.Svc.FileSystem, ExclusionsFactory = rig.Svc.ExclusionsFactory,
        AgentsCommand = command, Clock = () => clock, RefreshSyntheticRoots = true,
    };

    [Fact]
    public void View_SessionClosedAfterScan_DisappearsFromOpenWithoutNewScan()
    {
        var rig = FreshRig();
        rig.Svc.Scan(rig.Ctx);
        Assert.True((bool?)SessOf(rig.Svc.View(null, () => rig.Ctx), "sess-1")!["acikAdayi"]);
        // komut artık boş liste verir (oturum kapandı): yeni tarama olmadan görünüm bunu yansıtır
        var view = Twin(rig, new FakeAgents(() => "[]"), Now.AddMinutes(5)).View(null, () => rig.Ctx);
        var row = SessOf(view, "sess-1");
        if (row is not null) Assert.False((bool?)row["acikAdayi"]);
        Assert.Equal(LiveRules.SourceCommand, (string?)view["liveSource"]);
    }

    [Fact]
    public void View_CommandFails_FallsBackAndExplains()
    {
        var rig = FreshRig();
        rig.Svc.Scan(rig.Ctx);
        var view = Twin(rig, new FakeAgents(() => null, "komut-yok"), Now).View(null, () => rig.Ctx);
        Assert.Equal(LiveRules.SourceProcess, (string?)view["liveSource"]); // kayıt dosyası da yok
        Assert.Equal(ClaudeAgents.SoftNote, (string?)view["liveNote"]); // kişiye sade cümle; ham neden ClaudeAgentsResult.Technical içinde
    }

    [Fact]
    public void View_SyntheticRoots_StayStored_UnlessRefreshIsExplicitlyEnabled()
    {
        var rig = FreshRig();
        var plain = new ProfileService(rig.Db) { Machine = rig.Svc.Machine, AgentsCommand = rig.Cmd, Processes = rig.Svc.Processes, FileSystem = rig.Svc.FileSystem,
            ExclusionsFactory = rig.Svc.ExclusionsFactory };
        plain.Scan(rig.Ctx);
        var before = rig.Cmd.Calls;
        plain.View(null, () => rig.Ctx);
        Assert.Equal(before, rig.Cmd.Calls);
    }

    // ---------------------------------------------------------------- klasörü olmayan proje, byReason

    [Fact]
    public void MissingFolderProject_IsOld_AndComesLast_EvenWhenMostUsed()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var rows = new List<SessionMetadata> { S("p1", @"E:\P", 600) };
        for (var i = 0; i < 4; i++) rows.Add(S("g" + i, @"E:\Other\Gone", 5 + i)); // sık kullanılmış ama klasörü artık yok
        var result = Resolver(fs).Build(rows, Now);
        Assert.Equal(2, result.Projects.Count);
        Assert.Equal(@"E:\P", result.Projects[0].Path);
        var gone = result.Projects[^1];
        Assert.Equal(@"E:\Other\Gone", gone.Path); Assert.True(gone.MissingFolder); Assert.Equal("eski", gone.Status);
    }

    [Fact]
    public void UnassignedBreakdown_ByReasonAndByTool_AddUpToTheCount()
    {
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var rows = new[]
        {
            S("a", @"E:\P", 5),
            S("b", null, 5), S("c", null, 60 * 24 * 40), S("x", null, 5, "codex"),
            S("d", @"E:\Work\_gecici\x", 60 * 24 * 3),
            S("e", @"E:\Work\canli-proje", 10, "codex"),
            S("sub", null, 7, parent: "b"),
        };
        var result = Resolver(fs).Build(rows, Now).Unassigned;
        Assert.Equal(6, result.Count);
        Assert.Equal(result.Count, result.Reasons.Values.Sum());
        Assert.Equal(result.Count, result.ByReason!.Values.Sum(b => b.Count));
        Assert.Equal(result.Count, result.ByTool!.Values.Sum(b => b.Count));
        Assert.Equal(result.Sessions7Days, result.ByReason.Values.Sum(b => b.Sessions7Days));
        Assert.Equal(result.Sessions30Days, result.ByTool.Values.Sum(b => b.Sessions30Days));
        foreach (var (reason, count) in result.Reasons) Assert.Equal(count, result.ByReason[reason].Count);
        Assert.Equal(4, result.ByReason["klasor-bilinmiyor"].Count);
        Assert.Equal(3, result.ByReason["klasor-bilinmiyor"].RootSessions);
        Assert.Equal(1, result.ByReason["klasor-bilinmiyor"].SubagentSessions);
        Assert.Equal(4, result.ByTool["claude"].Count);
        Assert.Equal(2, result.ByTool["codex"].Count);
    }

    // ---------------------------------------------------------------- Hermes sınama düzeneği

    [Fact]
    public void HermesProfiles_OnlyDirectProfileDatabasesAreRead_NotTestRigsDeeperDown()
    {
        var home = ProfileMetadataTests.Fixture();
        void Db(params string[] parts) { var dir = Path.Combine([home, .. parts[..^1]]); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, parts[^1]), "x"); }
        Db("state.db");
        Db("profiles", "ajan-terra", "state.db");
        Db("profiles", "rapor", "state.db");
        Db("profiles", "research", "hermes-11", "pytest-final", "state.db");
        Db("profiles", "cache", "metot-clean", "synthetic-canary", "state.db");
        Db("profiles", "jobs", "ASTRA-01", "state.db");
        var files = ProfileSources.Files(new ProfileRoot("hermes", home)).Select(f => Path.GetRelativePath(home, f)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(["profiles\\ajan-terra\\state.db", "profiles\\rapor\\state.db", "state.db"], files);
    }

    // ---------------------------------------------------------------- biçimi tanınmayan kayıt

    private static string Write(string name, params string[] lines)
    {
        var dir = ProfileMetadataTests.Fixture();
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void ClaudeRecord_WithoutFolder_ButLooksLikeASession_IsNotASession()
    {
        // alan adı değişmiş: cwd yerine workingDirectory
        var changed = Write("a.jsonl", """{"type":"user","sessionId":"s1","workingDirectory":"E:\\P","timestamp":"2026-11-12T11:50:00Z"}""");
        var ex = Assert.Throws<ProfileReadException>(() => SessionMetadataReader.Read("claude", changed, Now));
        Assert.Equal(ProfileSources.ReasonUnrecognizedFormat, ex.Reason);
        // tür yok ama kimlik var
        var untyped = Write("b.jsonl", """{"sessionId":"s2","directory":"E:\\P"}""");
        Assert.Equal(ProfileSources.ReasonUnrecognizedFormat, Assert.Throws<ProfileReadException>(() => SessionMetadataReader.Read("claude", untyped, Now)).Reason);
        // iyi kayıt etkilenmez
        var good = Write("c.jsonl", """{"type":"user","sessionId":"s3","cwd":"E:\\P","timestamp":"2026-11-12T11:50:00Z"}""");
        Assert.Equal(@"E:\P", SessionMetadataReader.Read("claude", good, Now)!.Cwd);
    }

    [Fact]
    public void ClaudeRecord_OnlyBookkeepingLines_IsNotAFormatProblem()
    {
        var path = Write("d.jsonl", """{"type":"file-history-snapshot","messageId":"m1","snapshot":{}}""", """{"type":"summary","summary":"x"}""");
        var row = SessionMetadataReader.Read("claude", path, Now);
        Assert.NotNull(row); Assert.Null(row!.Cwd);
    }

    [Fact]
    public void CodexRecord_WithoutSessionMeta_IsUnrecognized_ButMetaWithoutFolderIsNot()
    {
        var none = Write("rollout-1.jsonl", """{"type":"response_item","payload":{"type":"message"}}""");
        Assert.Equal(ProfileSources.ReasonUnrecognizedFormat, Assert.Throws<ProfileReadException>(() => SessionMetadataReader.Read("codex", none, Now)).Reason);
        var meta = Write("rollout-2.jsonl", """{"type":"session_meta","payload":{"id":"c-1","timestamp":"2026-11-12T11:50:00Z"}}""");
        Assert.Null(SessionMetadataReader.Read("codex", meta, Now)!.Cwd);
        var ok = Write("rollout-3.jsonl", """{"type":"session_meta","payload":{"id":"c-2","cwd":"E:\\P","timestamp":"2026-11-12T11:50:00Z"}}""");
        Assert.Equal(@"E:\P", SessionMetadataReader.Read("codex", ok, Now)!.Cwd);
    }

    [Fact]
    public void Scan_CountsFormatChangedFiles_WarnsOnce_AndDoesNotCountThemAsSessions()
    {
        var root = ProfileMetadataTests.Fixture();
        var db = SozDb.OpenAndMigrate(Path.Combine(root, "data"));
        var roots = OnboardingRoots.Under(Path.Combine(root, "home"));
        var dir = Path.Combine(roots.ClaudeHome, "projects", "E--P"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "good.jsonl"), """{"type":"user","sessionId":"good","cwd":"E:\\P","timestamp":"2026-11-12T11:50:00Z"}""" + "\n");
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(dir, $"changed{i}.jsonl"), $$"""{"type":"user","sessionId":"changed{{i}}","workingDirectory":"E:\\P","timestamp":"2026-11-12T11:50:00Z"}""" + "\n");
        File.WriteAllText(Path.Combine(dir, "half.jsonl"), "{\"type\":\"user\",\"sessionId\":\"yarim");
        var fs = new ProfileInventoryTests.FakeFs(); fs.Dirs.Add(@"E:\P"); fs.Markers.Add(@"E:\P");
        var svc = new ProfileService(db)
        {
            Machine = new ProfileInventoryTests.FakeMachine(), Processes = new ProfileP2ExtraTests.FakeProcesses(), FileSystem = fs,
            ExclusionsFactory = r => new ProjectExclusions(r.Home, db.DataDir),
        };
        var snap = svc.Scan(new OnboardingScanContext { Roots = roots, Probe = new NullSystemProbe(), UtcNow = () => Now });
        var claude = snap.Sources.Single(s => s.Tool == "claude");
        Assert.Equal(5, claude.Reasons[ProfileSources.ReasonUnrecognizedFormat]);
        Assert.Equal(6, claude.Okunamayan);
        Assert.Equal(1, claude.Sessions);
        Assert.Contains(snap.Warnings!, w => w.Contains("Claude") && w.Contains("5 dosyanın biçimi tanınmadı"));
        Assert.Contains(snap.Warnings!, w => w.Contains("1 dosya bozuk"));
    }
}
