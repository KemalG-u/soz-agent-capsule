using System.Diagnostics;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Hooks;
using Soz.Core.Jobs;
using Soz.Core.Proof;
using Xunit;

namespace Soz.Core.Tests;

/// <summary>Örnek proje kurucu: bozuk testi olan küçük Node projesi (deneme-proje'nin kopyası, sentetik).</summary>
public static class NodeSample
{
    public const string Package = """{ "name": "ornek", "version": "1.0.0", "private": true, "scripts": { "test": "node --test" } }""";
    public const string BrokenLib = "'use strict';\nfunction topla(a, b) { return a - b; }\nfunction carp(a, b) { return a * b; }\nmodule.exports = { topla, carp };\n";
    public const string FixedLib = "'use strict';\nfunction topla(a, b) { return a + b; }\nfunction carp(a, b) { return a * b; }\nmodule.exports = { topla, carp };\n";
    public const string Tests = """
        'use strict';
        const test = require('node:test');
        const assert = require('node:assert/strict');
        const { topla, carp } = require('../hesap.js');
        test('carpma iki sayiyi carpar', () => { assert.equal(carp(3, 4), 12); });
        test('toplama iki sayiyi toplar', () => { assert.equal(topla(2, 3), 5); });
        """;

    public static string Create(TestEnv env, string name = "ornek-node")
    {
        var dir = Path.Combine(env.Root, name);
        Directory.CreateDirectory(Path.Combine(dir, "test"));
        File.WriteAllText(Path.Combine(dir, "package.json"), Package);
        File.WriteAllText(Path.Combine(dir, "hesap.js"), BrokenLib);
        File.WriteAllText(Path.Combine(dir, "test", "hesap.test.js"), Tests);
        return dir;
    }

    public static void Fix(string dir) => File.WriteAllText(Path.Combine(dir, "hesap.js"), FixedLib);
}

public class ProofTests
{
    private static WorkService Work(TestEnv env) => new(env.Svc.Db);

    private static CheckToRun Cmd(string cmd, int timeout = 60, string? must = null, string? mustNot = null)
    {
        var spec = new JsonObject { ["cmd"] = cmd, ["expectExit"] = 0 };
        if (must is not null) spec["mustContain"] = must;
        if (mustNot is not null) spec["mustNotContain"] = mustNot;
        return new CheckToRun(1, CheckKinds.Command, "deneme", spec, timeout);
    }

    [Fact]
    public void No_Command_Runs_Until_User_Enables_It()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "isaret");
        Directory.CreateDirectory(dir);
        // Önerilecek test betiği çalışırsa iz bırakır.
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "x", "scripts": { "test": "node -e \"require('fs').writeFileSync('calisti.txt','x')\"" } }""");
        var w = Work(env);
        var pid = w.EnsureProjectByPath(dir);

        var s = w.SuggestChecks(pid);
        Assert.Equal(1, s.Added);
        Assert.All(s.Checks, c => { Assert.False(c.Enabled); Assert.Equal("auto", c.Origin); });
        Assert.Equal(0, w.SuggestChecks(pid).Added); // aynısı yeniden eklenmez

        // Elle çalıştırma: açık ölçüt yok → unchecked, komut ÇALIŞMADI.
        var r1 = w.RunProof(pid, null);
        Assert.Equal(Verdicts.Unchecked, r1.Verdict);
        Assert.False(File.Exists(Path.Combine(dir, "calisti.txt")));

        // Kapı açık + Stop kancası: yine çalışmaz, block yok, geçti yok.
        w.SetGate(pid, true);
        var o = new StringWriter();
        HookHandler.Run("stop", new StringReader($$"""{"session_id":"s-ac","cwd":{{System.Text.Json.JsonSerializer.Serialize(dir)}},"stop_hook_active":false,"last_assistant_message":"Bitti, hepsi tamam."}"""), o, env.DataDir, _ => null);
        Assert.DoesNotContain("\"block\"", o.ToString());
        Assert.False(File.Exists(Path.Combine(dir, "calisti.txt")));
        Assert.Equal(Verdicts.Unchecked, w.Runs(pid, null)[0].Verdict);
        Assert.Equal("bitti dedi / deneme sonucu: Kontrol yok", w.Runs(pid, null)[0].Claim!.Label);

        // Kullanıcı açınca çalışır.
        w.PatchCheck(s.Checks[0].Id, enabled: true);
        var r2 = w.RunProof(pid, null);
        Assert.Equal(Verdicts.Passed, r2.Verdict);
        Assert.True(File.Exists(Path.Combine(dir, "calisti.txt")));
    }

    [Fact]
    public void Command_Pass_Fail_Contains_And_Secret_Masking()
    {
        using var env = new TestEnv(copySamples: false);
        var ev = Path.Combine(env.Root, "kanit");
        var runner = new ProofRunner(env.Root, ev, () => null);
        Assert.Equal(ResultStatus.Passed, runner.Run(Cmd("echo merhaba"), TimeSpan.FromSeconds(30)).Status);
        var f = runner.Run(Cmd("exit 3"), TimeSpan.FromSeconds(30));
        Assert.Equal(ResultStatus.Failed, f.Status);
        Assert.Equal(3, f.ExitCode);
        Assert.Equal(ResultStatus.Failed, runner.Run(Cmd("echo merhaba", must: "dunya"), TimeSpan.FromSeconds(30)).Status);
        Assert.Equal(ResultStatus.Failed, runner.Run(Cmd("echo HATA var", mustNot: "hata"), TimeSpan.FromSeconds(30)).Status);
        var sec = runner.Run(Cmd("echo token=gh" + "p_abcdefghijklmnopqrstu12345"), TimeSpan.FromSeconds(30));
        Assert.DoesNotContain("gh" + "p_abcdefghijklmnopqrstu12345", sec.OutputTail);
        Assert.Contains("•••", sec.OutputTail);
        Assert.DoesNotContain("gh" + "p_abcdefghijklmnopqrstu12345", File.ReadAllText(Path.Combine(ev, sec.ArtifactName!)));
    }

    [Fact]
    public void Timeout_Is_Error_Not_Passed_And_Kills_Own_Tree()
    {
        using var env = new TestEnv(copySamples: false);
        var runner = new ProofRunner(env.Root, Path.Combine(env.Root, "kanit"), () => null);
        var marker = "soz-zaman-asimi-" + Guid.NewGuid().ToString("N")[..8];
        var sw = Stopwatch.StartNew();
        var o = runner.Run(Cmd($"node -e \"setTimeout(()=>{{}},60000)\" {marker}"), TimeSpan.FromSeconds(2));
        Assert.Equal(ResultStatus.Error, o.Status);
        Assert.Contains("Zaman aşımı", o.OutputTail);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "zaman aşımı bekletmedi");
        Thread.Sleep(500);
        // Kendi başlattığımız node süreci kalmamalı (komut satırında işaret var).
        var left = Process.GetProcessesByName("node").Count(p => { try { return CommandLineContains(p.Id, marker); } catch { return false; } });
        Assert.Equal(0, left);
    }

    private static bool CommandLineContains(int pid, string marker)
    {
        // wmic yoksa sayılamaz: güvenli tarafta "yok" de.
        try
        {
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var s = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return s.Contains(marker);
        }
        catch { return false; }
    }

    [Fact]
    public void Judge_Never_Passes_On_Uncertainty()
    {
        CheckOutcome O(string s) => new(1, "x", s, null, null, null, 0);
        Assert.Equal(Verdicts.Unchecked, ProofStore.Judge(Array.Empty<CheckOutcome>()).verdict);
        Assert.Equal(Verdicts.Passed, ProofStore.Judge(new[] { O("passed"), O("passed") }).verdict);
        Assert.Equal(Verdicts.Failed, ProofStore.Judge(new[] { O("passed"), O("failed"), O("error") }).verdict);
        Assert.Equal(Verdicts.Error, ProofStore.Judge(new[] { O("passed"), O("error") }).verdict);
        Assert.Equal(Verdicts.Error, ProofStore.Judge(new[] { O("passed"), O("skipped") }).verdict);
    }

    [Fact]
    public void File_Url_And_Missing_Edge()
    {
        using var env = new TestEnv(copySamples: false);
        File.WriteAllText(Path.Combine(env.Root, "a.txt"), "merhaba dünya");
        var runner = new ProofRunner(env.Root, Path.Combine(env.Root, "kanit"), () => null);
        CheckToRun C(string kind, JsonObject spec) => new(1, kind, "x", spec, 5);
        Assert.Equal(ResultStatus.Passed, runner.Run(C("file", new() { ["path"] = "a.txt", ["contains"] = "dünya", ["minBytes"] = 3 }), TimeSpan.FromSeconds(5)).Status);
        Assert.Equal(ResultStatus.Failed, runner.Run(C("file", new() { ["path"] = "yok.txt" }), TimeSpan.FromSeconds(5)).Status);
        Assert.Equal(ResultStatus.Failed, runner.Run(C("file", new() { ["path"] = "a.txt", ["contains"] = "elma" }), TimeSpan.FromSeconds(5)).Status);
        Assert.Equal(ResultStatus.Passed, runner.Run(C("file", new() { ["path"] = "yok.txt", ["exists"] = false }), TimeSpan.FromSeconds(5)).Status);
        // Kapalı port: bağlanılamadı → failed (passed değil).
        Assert.Equal(ResultStatus.Failed, runner.Run(C("url", new() { ["url"] = "http://127.0.0.1:1/" }), TimeSpan.FromSeconds(5)).Status);
        // Edge yok → error (passed değil).
        File.WriteAllText(Path.Combine(env.Root, "s.html"), "<h1>x</h1>");
        var shot = runner.Run(C("screenshot", new() { ["url"] = new Uri(Path.Combine(env.Root, "s.html")).AbsoluteUri }), TimeSpan.FromSeconds(5));
        Assert.Equal(ResultStatus.Error, shot.Status);
        // Kök dışına cwd → error.
        Assert.Equal(ResultStatus.Error, runner.Run(new CheckToRun(1, "command", "x", new() { ["cmd"] = "echo x", ["cwd"] = @"..\.." }, 5), TimeSpan.FromSeconds(5)).Status);
    }

    [Fact]
    public void Screenshot_With_Headless_Edge_When_Available()
    {
        var edge = EdgeLocator.Find();
        if (edge is null) return; // bu makinede Edge yoksa sınanamaz (raporda belirtilir)
        using var env = new TestEnv(copySamples: false);
        var html = Path.Combine(env.Root, "sayfa.html");
        File.WriteAllText(html, "<html><body style='background:#fff'><h1>Söz kanıt</h1></body></html>");
        var ev = Path.Combine(env.Root, "kanit");
        var runner = new ProofRunner(env.Root, ev);
        var o = runner.Run(new CheckToRun(9, "screenshot", "ana sayfa açılsın", new() { ["url"] = new Uri(html).AbsoluteUri, ["width"] = 800, ["height"] = 600 }, 60), TimeSpan.FromSeconds(60));
        Assert.Equal(ResultStatus.Passed, o.Status);
        var png = Path.Combine(ev, o.ArtifactName!);
        Assert.True(new FileInfo(png).Length > 100);
        Assert.Empty(Directory.GetDirectories(ev, "edge-profil-*")); // geçici profil temizlendi
    }

    [Fact]
    public void Suggest_Scans_Project_Files_Without_Running()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "tara");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "scripts": { "test": "echo \"Error: no test specified\" && exit 1", "build": "vite build", "lint": "eslint ." } }""");
        File.WriteAllText(Path.Combine(dir, "Uyg.sln"), "");
        File.WriteAllText(Path.Combine(dir, "go.mod"), "module x");
        File.WriteAllText(Path.Combine(dir, "Makefile"), "test:\n\tgo test\n");
        var cmds = CheckStore.Scan(dir).Select(s => s.Cmd).ToList();
        Assert.DoesNotContain("npm test", cmds); // npm'in boş varsayılanı önerilmez
        Assert.Contains("npm run build", cmds);
        Assert.Contains("npm run lint", cmds);
        Assert.Contains("dotnet test \"Uyg.sln\"", cmds);
        Assert.Contains("go test ./...", cmds);
        Assert.Contains("make test", cmds);
    }

    [Fact]
    public void Proof_Run_Records_Results_And_Artifact()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = NodeSample.Create(env);
        var w = Work(env);
        var pid = w.EnsureProjectByPath(dir);
        var c = w.AddCheck(pid, null, "command", "Testler geçsin", new JsonObject { ["cmd"] = "npm test" }, 120, enabled: true);
        var r = w.RunProof(pid, null);
        Assert.Equal(Verdicts.Failed, r.Verdict);
        var res = Assert.Single(r.Results!);
        Assert.Equal(c.Id, res.CheckId);
        Assert.Equal("failed", res.Status);
        Assert.Contains("toplama iki sayiyi toplar", res.OutputTail);
        Assert.NotNull(w.ArtifactPath(r.Id, res.ArtifactUrl!.Split('/').Last()));
        Assert.Null(w.ArtifactPath(r.Id, "..\\..\\soz.db"));
        NodeSample.Fix(dir);
        Assert.Equal(Verdicts.Passed, w.RunProof(pid, null).Verdict);
        Assert.Equal(2, w.Runs(pid, null).Count);
    }
}
