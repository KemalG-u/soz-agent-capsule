using System.Text.Json;
using Soz.Core.Guard;
using Soz.Core.Proof;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>
/// Denetim 4 düzeltmeleri: A yanlış alarm (13 dürüst değişiklikten en çok 3'ü karara düşer), C bağımlılık parmak izi, D kendiliğinden yüklenen adlar,
/// E görüntü kimliği, F süreç kaçıran komutlar.
/// </summary>
public class DenetimDortTests
{
    private static string W(string dir, string rel, string s)
    {
        var f = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, s);
        return f;
    }

    private static string Rich(TestEnv env, string name)
    {
        var dir = NodeSample.Create(env, name);
        NodeSample.Fix(dir);
        W(dir, "README.md", "# deneme\n");
        W(dir, "package-lock.json", """{ "name": "ornek", "version": "1.0.0", "lockfileVersion": 3, "packages": { "": { "name": "ornek", "version": "1.0.0" } } }""");
        W(dir, ".gitignore", "node_modules/\n");
        W(dir, "locales/tr.json", """{ "selam": "Merhaba" }""");
        W(dir, "eslint.config.js", "module.exports = [{ rules: { \"no-unused-vars\": \"warn\" } }];\n");
        W(dir, "src/testimonials.js", "module.exports = [\"Harika urun\"];\n");
        W(dir, "src/setup-db.js", "module.exports = { port: 5432 };\n");
        W(dir, ".github/workflows/ci.yml", "name: ci\non: push\n");
        return dir;
    }

    private static EvidenceBase.Diff Change(string dir, Action edit)
    {
        var ctx = ScanContext.Build(dir, new[] { "npm test" });
        var before = EvidenceBase.Scan(dir, ctx);
        edit();
        return EvidenceBase.Compare(before, EvidenceBase.Scan(dir, ctx));
    }

    private static void Pkg(string dir, Action<System.Text.Json.Nodes.JsonObject> f)
    {
        var p = Path.Combine(dir, "package.json");
        var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p))!.AsObject();
        f(j);
        File.WriteAllText(p, j.ToJsonString());
    }

    // ------------------------------------------------------------------ A
    [Fact]
    public void A_Thirteen_Honest_Changes_Fall_To_Decision_At_Most_Three()
    {
        var flagged = new List<string>();
        var names = new List<string>();
        void Case(string name, Func<string, Action> make)
        {
            using var env = new TestEnv(copySamples: false);
            var dir = Rich(env, "d-" + name);
            var d = Change(dir, make(dir));
            names.Add(name);
            if (d.Any) flagged.Add(name + " -> " + string.Join(",", d.ChangedConfig.Concat(d.ChangedTests).Concat(d.DepsChanged)));
        }
        Case("readme", d => () => File.AppendAllText(Path.Combine(d, "README.md"), "Toplama duzeltildi.\n"));
        Case("yenitest", d => () => W(d, "test/carp2.test.js", "'use strict';\nconst test = require('node:test');\ntest('carp sifir', () => {});\n"));
        Case("bagimlilik", d => () =>
        {
            Pkg(d, j => j["dependencies"] = new System.Text.Json.Nodes.JsonObject { ["left-pad"] = "1.3.0" });
            W(d, "package-lock.json", """{ "name": "ornek", "lockfileVersion": 3, "packages": { "": { "dependencies": { "left-pad": "1.3.0" } }, "node_modules/left-pad": { "version": "1.3.0" } } }""");
        });
        Case("yarn", d => () => { Pkg(d, j => j["dependencies"] = new System.Text.Json.Nodes.JsonObject { ["left-pad"] = "1.3.0" }); W(d, "yarn.lock", "# yarn lockfile v1\n"); });
        Case("gitignore", d => () => File.AppendAllText(Path.Combine(d, ".gitignore"), "coverage/\n"));
        Case("json", d => () => W(d, "locales/tr.json", """{ "selam": "Merhaba!" }"""));
        Case("jpeg", d => () => File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(d, "assets")).FullName, "logo.jpeg"), new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0, 0x10 }));
        Case("eslint", d => () => W(d, "eslint.config.js", "module.exports = [{ rules: { \"no-unused-vars\": \"error\" } }];\n"));
        Case("mts", d => () => W(d, "src/yardim.mts", "export const iki = 2\n"));
        Case("testimonial", d => () => W(d, "src/testimonials.js", "module.exports = [\"Harika!\"];\n"));
        Case("script", d => () => Pkg(d, j => j["scripts"]!.AsObject()["lint"] = "eslint ."));
        Case("yaml", d => () => File.AppendAllText(Path.Combine(d, ".github", "workflows", "ci.yml"), "jobs: {}\n"));
        Case("setup", d => () => W(d, "src/setup-db.js", "module.exports = { port: 5433 };\n"));

        Assert.Equal(13, names.Count);
        Assert.True(flagged.Count <= 3, "Yanlış alarm: " + string.Join(" | ", flagged));
        // en azından bunlar kesin dürüst: karara düşmez
        foreach (var n in new[] { "readme", "yenitest", "bagimlilik", "yarn", "gitignore", "json", "jpeg", "testimonial", "setup", "script", "mts" })
            Assert.DoesNotContain(flagged, f => f.StartsWith(n + " ->"));
    }

    [Theory]
    [InlineData("test/a.js", true)]
    [InlineData("src/a.test.ts", true)]
    [InlineData("tests/x/y.py", true)]
    [InlineData("pkg/foo_test.go", true)]
    [InlineData("Tests/FooTests.cs", true)]
    [InlineData("src/testimonials.js", false)]
    [InlineData("src/setup-db.js", false)]
    [InlineData("src/latest.js", false)]
    [InlineData("src/contest.py", false)]
    public void A_Test_Name_Recognition_Is_Narrow(string rel, bool isTest) => Assert.Equal(isTest, EvidenceBase.IsTestFile(rel));

    [Fact]
    public void A_Package_Json_Field_Level_Info_And_Suspicious()
    {
        var used = new HashSet<string> { "test" };
        const string a = """{"name":"x","version":"1.0.0","scripts":{"test":"node --test","lint":"eslint ."}}""";
        // bilgi: sürüm, açıklama, kullanılmayan betik
        var (s1, n1, _, _) = EvidenceBase.ComparePackage(a, a.Replace("1.0.0", "1.0.1"), used, false);
        Assert.Empty(s1);
        var (s2, _, _, _) = EvidenceBase.ComparePackage(a, a.Replace("eslint .", "eslint src"), used, false);
        Assert.Empty(s2);
        // e2: kullanılan betik hüküm koşusunda onaylı hâliyle geri konur: alarm değil, not. Kişi öneriyi kabul ederken (testKeysRestored: false) kuşkulu sayılır.
        var (s3, n3, _, _) = EvidenceBase.ComparePackage(a, a.Replace("node --test", "node -e 0"), used, false);
        Assert.Empty(s3);
        Assert.Contains(n3, n => n.Contains("sonuç onayladığın hâliyle alınır"));
        var (s3b, _, _, _) = EvidenceBase.ComparePackage(a, a.Replace("node --test", "node -e 0"), used, false, testKeysRestored: false);
        Assert.NotEmpty(s3b);
        // bilgi: bağımlılık ekleme (kilitli ya da kilitsiz). Denetim 5, B1: yalnız ekleme sınamayı gevşetmez, not düşülür; var olan bağımlılığın değişmesi ayrı kural.
        var withDep = a.Replace("\"scripts\"", "\"dependencies\":{\"y\":\"1\"},\"scripts\"");
        var (s4, n4, _, _) = EvidenceBase.ComparePackage(a, withDep, used, false);
        Assert.Empty(s4);
        Assert.NotEmpty(n4);
        var (s5, n5, _, _) = EvidenceBase.ComparePackage(a, withDep, used, true);
        Assert.Empty(s5);
        Assert.NotEmpty(n5);
    }

    [Fact]
    public void A_Line_Diff_Is_Short_And_Shows_Before_After()
    {
        var before = string.Join("\n", Enumerable.Range(1, 40).Select(i => "satir " + i));
        var after = before.Replace("satir 20", "satir yirmi");
        var d = EvidenceBase.LineDiff(before, after);
        Assert.InRange(d.Count, 2, 12);
        Assert.Contains(d, l => l.Text.Contains("satir yirmi"));
        Assert.Contains(d, l => l.Text.Contains("satir 20"));
    }

    // ------------------------------------------------------------------ C
    [Fact]
    public void C_Dependency_Folder_Edited_Without_Lock_Change_Is_Suspicious_With_Lock_Is_Info()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Rich(env, "bagparmak");
        W(dir, "node_modules/minicheck/package.json", """{ "name": "minicheck", "version": "1.0.0" }""");
        W(dir, "node_modules/minicheck/index.js", "module.exports = 1;\n");
        // elle yama: kilit aynı
        var d1 = Change(dir, () => File.WriteAllText(Path.Combine(dir, "node_modules", "minicheck", "index.js"), "module.exports = 2; // yama\n"));
        Assert.NotEmpty(d1.DepsChanged);
        Assert.True(d1.Any);
        // yeni paket + kilit güncellemesi: bilgi
        var d2 = Change(dir, () =>
        {
            W(dir, "node_modules/yeni/package.json", """{ "name": "yeni", "version": "2.0.0" }""");
            W(dir, "node_modules/yeni/index.js", "module.exports = 3;\n");
            W(dir, "package-lock.json", """{ "lockfileVersion": 3, "packages": { "node_modules/yeni": { "version": "2.0.0" } } }""");
        });
        Assert.Empty(d2.DepsChanged);
    }

    // ------------------------------------------------------------------ D
    [Theory]
    [InlineData("conftest.py", true)]
    [InlineData("tests/conftest.py", true)]
    [InlineData("sitecustomize.py", true)]
    [InlineData("jest.setup.js", true)]
    [InlineData("vitest.config.ts", true)]
    [InlineData("pytest.py", true)]
    [InlineData("mocha.js", true)]
    [InlineData("src/mocha.js", false)]
    [InlineData("hesap.js", false)]
    [InlineData("README.md", false)]
    public void D_Auto_Loaded_Names(string rel, bool expected) => Assert.Equal(expected, EvidenceBase.IsAutoLoaded(rel));

    [Fact]
    public void D_New_Conftest_Is_Not_An_Alarm_But_Is_Removed_In_The_Verdict_Run()
    {
        // e2: yeni eklenen koşucu ayarı (conftest.py) alarm değil; hüküm koşusunda geçici kopyadan çıkarılır, karta not düşer.
        using var env = new TestEnv(copySamples: false);
        var dir = Rich(env, "conftest");
        var once = EvidenceBase.Scan(dir);
        W(dir, "conftest.py", "import os\n");
        var simdi = EvidenceBase.Scan(dir);
        var d = EvidenceBase.Compare(once, simdi, dir);
        Assert.DoesNotContain("conftest.py", d.ChangedConfig);
        Assert.Contains(d.NoteList, n => n.Contains("conftest.py"));
        Assert.Contains("conftest.py", ApprovedSet.Changes(once, simdi).Added);
    }

    // ------------------------------------------------------------------ E
    [Fact]
    public void E_Snapshot_Id_Is_Stable_And_Moves_With_Suspicious_Change()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Rich(env, "kimlik");
        var a = EvidenceBase.SnapshotId(EvidenceBase.Scan(dir));
        Assert.Equal(16, a.Length);
        Assert.Equal(a, EvidenceBase.SnapshotId(EvidenceBase.Scan(dir)));
        File.AppendAllText(Path.Combine(dir, "README.md"), "x\n"); // zararsız dosya kimliği oynatmaz
        Assert.Equal(a, EvidenceBase.SnapshotId(EvidenceBase.Scan(dir)));
        Pkg(dir, j => j["scripts"]!.AsObject()["test"] = "node -e 0");
        Assert.NotEqual(a, EvidenceBase.SnapshotId(EvidenceBase.Scan(dir)));
    }

    [Fact]
    public void E_Snapshot_Serialize_Roundtrip_Keeps_Deps_And_Content()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Rich(env, "tur");
        W(dir, "node_modules/p/package.json", """{ "name": "p", "version": "1.0.0" }""");
        var s = EvidenceBase.Scan(dir, ScanContext.Build(dir, new[] { "npm test" }));
        var back = EvidenceBase.Parse(EvidenceBase.Serialize(s));
        Assert.NotNull(back);
        Assert.Equal(EvidenceBase.SnapshotId(s), EvidenceBase.SnapshotId(back!));
        Assert.Equal(s.Deps?.Count ?? 0, back!.Deps?.Count ?? 0);
        Assert.Null(EvidenceBase.Parse("""{"v":2}"""));
    }

    // ------------------------------------------------------------------ F
    [Theory]
    [InlineData("wmic process call create \"node x.js\"")]
    [InlineData("powershell -c \"Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='calc'}\"")]
    [InlineData("([wmiclass]'Win32_Process').Create('calc')")]
    [InlineData("schtasks /create /tn x /tr calc /sc once /st 00:00")]
    [InlineData("Register-ScheduledTask -TaskName x -Action $a")]
    [InlineData("Start-Process node -ArgumentList x.js")]
    [InlineData("setx PATH \"%PATH%;C:\\x\"")]
    [InlineData("npm config set script-shell cmd.exe")]
    [InlineData("git config core.hooksPath /tmp/x")]
    [InlineData("git config --global user.name x")]
    [InlineData("echo hi > .git/hooks/pre-commit")]
    [InlineData("echo registry=x >> ~/.npmrc")]
    public void F_Escape_Commands_Are_Denied(string cmd) => Assert.NotNull(EscapeGuard.HitCommand(cmd));

    [Theory]
    [InlineData("npm test")]
    [InlineData("node --test")]
    [InlineData("git status")]
    [InlineData("git diff HEAD~1")]
    [InlineData("git config user.name")]
    [InlineData("cat package.json")]
    [InlineData("echo merhaba > notlar.txt")]
    [InlineData("python -m pytest -q")]
    [InlineData("dotnet test")]
    [InlineData("ls .git")]
    public void F_Ordinary_Commands_Are_Not_Denied(string cmd) => Assert.Null(EscapeGuard.HitCommand(cmd));

    [Fact]
    public void F_Path_Writes_To_Hooks_And_User_Config_Are_Denied()
    {
        Assert.NotNull(EscapeGuard.HitPath(@"E:\proje\.git\hooks\pre-commit", @"C:\Users\x"));
        Assert.NotNull(EscapeGuard.HitPath(@"C:\Users\x\.npmrc", @"C:\Users\x"));
        Assert.NotNull(EscapeGuard.HitPath(@"C:\Users\x\.gitconfig", @"C:\Users\x"));
        Assert.Null(EscapeGuard.HitPath(@"E:\proje\.npmrc", @"C:\Users\x")); // proje içi .npmrc ayrı yolda (kuşkulu sınıf) izlenir
        Assert.Null(EscapeGuard.HitPath(@"E:\proje\src\a.js", @"C:\Users\x"));
    }

    private static string PreTool(TestEnv env, string tool, object toolInput, bool inSozRun)
    {
        var o = new StringWriter();
        var stdin = JsonSerializer.Serialize(new Dictionary<string, object?> { ["session_id"] = "f1", ["cwd"] = env.Root, ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = toolInput });
        Func<string, string?> envFn = k => inSozRun && k == "SOZ_RUN_ID" ? "1" : null;
        Assert.Equal(0, Soz.Core.Hooks.HookHandler.Run("pre-tool-use", new StringReader(stdin), o, env.DataDir, envFn));
        return o.ToString();
    }

    [Fact]
    public void F_Hook_Denies_Escape_Only_Inside_Soz_Run()
    {
        using var env = new TestEnv(copySamples: false);
        var inside = PreTool(env, "Bash", new { command = "wmic process call create \"node x.js\"" }, true);
        Assert.Contains("deny", inside);
        Assert.Contains("kapalıdır", inside);
        Assert.Equal("", PreTool(env, "Bash", new { command = "wmic process call create \"node x.js\"" }, false).Trim()); // Söz dışında kullanıcının kendi işi
        Assert.Equal("", PreTool(env, "Bash", new { command = "npm test" }, true).Trim());
        Assert.Contains("deny", PreTool(env, "Write", new { file_path = Path.Combine(env.Root, "p", ".git", "hooks", "pre-commit"), content = "x" }, true));
    }

    /// <summary>Ölçüm: 2.000 paketli sahte node_modules ile anlık görüntü ve bağımlılık parmak izi süreleri. Sınır cömert (5 sn), sayılar test çıktısına yazılır.</summary>
    [Fact]
    public void C_Two_Thousand_Package_Dependency_Fingerprint_Is_Fast_Enough()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Rich(env, "olcum");
        for (var i = 0; i < 2000; i++)
        {
            var p = Path.Combine(dir, "node_modules", "pkg" + i);
            Directory.CreateDirectory(Path.Combine(p, "lib"));
            File.WriteAllText(Path.Combine(p, "package.json"), "{\"name\":\"pkg" + i + "\",\"version\":\"1.0." + (i % 9) + "\"}");
            File.WriteAllText(Path.Combine(p, "index.js"), "module.exports=" + i + ";");
            File.WriteAllText(Path.Combine(p, "lib", "a.js"), "exports.a=1;");
        }
        var ctx = ScanContext.Build(dir, new[] { "npm test" });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cold = EvidenceBase.Scan(dir, ctx);
        var coldMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var warm = EvidenceBase.Scan(dir, ctx);
        var warmMs = sw.ElapsedMilliseconds;
        File.WriteAllText(Path.Combine(env.Root, "..", "..", "fix4-olcum.txt"),
            $"2000 paket x 3 dosya: ilk tarama {coldMs} ms (ScanMs={cold.ScanMs}, DepsMs={cold.DepsMs}), ikinci tarama {warmMs} ms (ScanMs={warm.ScanMs}, DepsMs={warm.DepsMs}), Deps={cold.Deps?.Count}\n");
        Assert.True(coldMs < 5000, $"ilk tarama {coldMs} ms");
        Assert.True(warmMs < 5000, $"ikinci tarama {warmMs} ms");
        Assert.Equal(EvidenceBase.SnapshotId(cold), EvidenceBase.SnapshotId(warm));
    }

    // ------------------------------------------------------------------ E: kabul gösterileni onaylar
    private static string Hk(TestEnv env, string ev, string stdin)
    {
        var o = new StringWriter();
        Assert.Equal(0, Soz.Core.Hooks.HookHandler.Run(ev, new StringReader(stdin), o, env.DataDir, _ => null));
        return o.ToString();
    }

    private static (Soz.Core.Jobs.WorkService w, Soz.Core.Jobs.JobDto job, string dir, string snap) DecisionJob(TestEnv env, string name)
    {
        var dir = NodeSample.Create(env, name);
        NodeSample.Fix(dir);
        var w = new Soz.Core.Jobs.WorkService(env.Svc.Db);
        var pid = w.EnsureProjectByPath(dir);
        w.AddCheck(pid, null, "command", "Testler", new System.Text.Json.Nodes.JsonObject { ["cmd"] = "npm test", ["expectExit"] = 0 }, 120, enabled: true);
        var job = w.CreateJob(pid, "Düzelt " + name);
        w.PatchJob(job.Id, Soz.Core.Jobs.JobStates.Given);
        Hk(env, "user-prompt-submit", JsonSerializer.Serialize(new { session_id = "e-" + name, cwd = dir, prompt = job.PromptText }));
        W(dir, "hesap.js", NodeSample.BrokenLib);   // e3: ayar dosyası eklemek alarm değil; karar bekletmek için gevşetilmiş test + bozuk kod
        W(dir, "test/hesap.test.js", NodeSample.Tests.Replace("assert.equal(topla(2, 3), 5)", "assert.ok(true)"));
        string Stop(bool active) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["session_id"] = "e-" + name, ["transcript_path"] = "t.jsonl", ["cwd"] = dir, ["hook_event_name"] = "Stop", ["stop_hook_active"] = active, ["last_assistant_message"] = "Bitti." });
        Hk(env, "stop", Stop(false));
        Hk(env, "stop", Stop(true));
        Assert.Equal(Soz.Core.Jobs.JobStates.AwaitingDecision, w.Job(job.Id).State);
        var snap = w.Runs(null, job.Id)[0].SnapshotId;
        Assert.False(string.IsNullOrEmpty(snap));
        return (w, job, dir, snap!);
    }

    [Fact]
    public void E_Accept_Requires_Snapshot_Id_And_Rejects_Stale_Card_With_409()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, job, dir, snap) = DecisionJob(env, "kabul-kimlik");
        // kimliksiz kabul (sunucu ucu): 400
        Assert.Equal(400, Assert.Throws<Soz.Core.SozException>(() => w.Accept(job.Id, true, null, null, requireSnapshot: true)).Status);
        // kart çizildikten sonra klasör değişti: eski kimlikle kabul 409, iş hâlâ karar bekliyor
        W(dir, "test/gizli.test.js", "'use strict';\n");
        File.AppendAllText(Path.Combine(dir, "package.json"), "\n");
        Assert.Equal(409, Assert.Throws<Soz.Core.SozException>(() => w.Accept(job.Id, true, null, snap, requireSnapshot: true)).Status);
        Assert.Equal(Soz.Core.Jobs.JobStates.AwaitingDecision, w.Job(job.Id).State);
        Assert.False(w.Job(job.Id).AcceptedByUser);
    }

    [Fact]
    public void E_Accept_With_Matching_Snapshot_Id_Passes_And_Reject_Needs_No_Id()
    {
        using var env = new TestEnv(copySamples: false);
        var (w, job, _, snap) = DecisionJob(env, "kabul-dogru");
        var done = w.Accept(job.Id, true, null, snap, requireSnapshot: true);
        Assert.Equal(Soz.Core.Jobs.JobStates.Passed, done.State);
        Assert.True(done.AcceptedByUser);

        var (w2, job2, _, _) = DecisionJob(env, "red-kimliksiz");
        Assert.Equal(Soz.Core.Jobs.JobStates.Failed, w2.Accept(job2.Id, false, null, null, requireSnapshot: true).State);
    }

    // ------------------------------------------------------------------ G: claude tanıma dar
    [Theory]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js --resume x", true)]
    [InlineData(@"node /home/u/.claude/local/node_modules/.bin/claude -p", true)]
    [InlineData(@"node E:\proje\claude-notlari.mjs", false)]
    [InlineData(@"node E:\Sample Work\urun\_gecici\fix4-arac\kos4.mjs --bayrak claude", false)]
    [InlineData(@"node C:\araclar\claude-yardimci\index.js", false)]
    public void G_Claude_Code_Command_Line_Recognition_Is_Narrow(string cmd, bool expected)
        => Assert.Equal(expected, Soz.Core.Security.CallerIdentity.IsClaudeCodeCommandLine(cmd));

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\AnthropicClaude\app-1.2.3\claude.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0_x64\app\claude.exe", true)]
    [InlineData(@"C:\Users\u\AppData\Roaming\npm\claude.exe", false)]
    public void G_Desktop_App_Path_Is_Not_The_Agent(string path, bool desktop)
        => Assert.Equal(desktop, Soz.Core.Security.CallerIdentity.IsDesktopAppPath(path));
}
