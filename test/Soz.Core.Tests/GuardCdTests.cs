using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Guard;
using Soz.Core.Hooks;
using Soz.Core.Readers;
using Xunit;
using Xunit.Abstractions;

namespace Soz.Core.Tests;

/// <summary>
/// Çalışma klasörü izleme tablosu: komut zincirindeki cd/pushd/popd/Set-Location/-C, göreli yazma hedefini değiştirir.
/// Hiçbir komut ÇALIŞTIRILMAZ; motor yalnız metni değerlendirir. Gerçek oturumda kaçan komutun birebir kopyası da burada.
/// </summary>
public class GuardCdTests
{
    private readonly ITestOutputHelper _out;
    public GuardCdTests(ITestOutputHelper o) => _out = o;

    private const string Root = @"E:\proj";
    private const string Home = @"C:\Users\tester";
    private const string Temp = @"C:\Users\tester\AppData\Local\Temp";

    private static IReadOnlyList<GuardRule> Rules(string root = Root)
    {
        var list = new List<GuardRule>();
        long id = 1;
        foreach (var tid in new[] { "temkinli", "surec-oldurme", "genel-kurulum", "disk", "korunan-yollar" })
            foreach (var tr in GuardTemplates.Find(tid)!.Rules)
                list.Add(new GuardRule(id++, 1, tr.Kind, tr.Label, tr.Spec, tr.Action, true, null, "template", null));
        return list;
    }

    private static GuardDecision Eval(string tool, string command, string root = Root)
        => GuardEngine.Evaluate(Rules(root), new GuardInput(tool, new JsonObject { ["command"] = command }, root, root, Home, Temp));

    private static char Code(GuardDecision d) => d.Verdict switch { "deny" => 'D', "ask" => 'K', _ => 'A' };

    // (araç, komut, beklenen: A izin / D engel / K sor)
    private static readonly (string tool, string cmd, char exp)[] Table =
    {
        // ---- cd ile dışarı çık, yaz: engel
        ("Bash", "cd /e/other && echo x > a.txt", 'D'),
        ("Bash", "cd .. && echo x > a.txt", 'D'),
        ("Bash", "cd ../other; printf hi > f.txt", 'D'),
        ("Bash", "cd \"/e/other dir\" && echo x > \"a b.txt\"", 'D'),
        ("Bash", @"cd E:\other && echo x > a.txt", 'D'),
        ("Bash", "cd /e/other && cp a.txt b.txt", 'D'),
        ("Bash", "cd /e/other && rm a.txt", 'D'),
        ("Bash", "cd /e/other && mkdir sub", 'D'),
        ("Bash", "cd /e/other\necho x > a.txt", 'D'),
        ("Bash", "cd /e/proj; cd /e/other; echo x > a.txt", 'D'),
        ("Bash", "cd /e/other && echo x > a.txt && cd /e/proj", 'D'),
        ("Bash", "{ cd /e/other; echo x > a.txt; }", 'D'),
        ("Bash", "if true; then cd /e/other; echo x > a.txt; fi", 'D'),
        ("Bash", "cd ~ && echo x > n.txt", 'D'),
        ("Bash", "cd && echo x > n.txt", 'D'),
        ("Bash", "chdir /e/other && echo x > a.txt", 'D'),
        ("Bash", "builtin cd /e/other && echo x > a.txt", 'D'),
        ("Bash", "command cd /e/other && echo x > a.txt", 'D'),
        ("Bash", "cd /e/proj/src && echo x > ../../a.txt", 'D'),
        ("Bash", "cd src && cd ../.. && echo x > a.txt", 'D'),
        ("Bash", "cd $PWD/../.. && echo x > a.txt", 'D'),
        ("Bash", "cd /e/other && rm -rf node_modules", 'D'),
        ("Bash", "cd $X && echo x > /e/other/a.txt", 'D'),
        // ---- pushd / popd
        ("Bash", "pushd /e/other && echo x > a.txt", 'D'),
        ("Bash", "pushd /e/other; echo x > a.txt; popd", 'D'),
        ("Bash", "pushd /e/other; popd; echo x > a.txt", 'A'),
        ("Bash", "popd && echo x > a.txt", 'K'),
        // ---- alt kabuk
        ("Bash", "( cd /e/other && echo x > a.txt )", 'D'),
        ("Bash", "(cd /e/other && echo x > a.txt); echo y > b.txt", 'D'),
        ("Bash", "(cd /e/other); echo x > a.txt", 'A'),
        ("Bash", "(cd /e/other && ls); echo y > b.txt", 'A'),
        ("Bash", "echo $(cd /e/other && echo x > a.txt)", 'D'),
        ("Bash", "echo $(cd /e/other && pwd); echo y > b.txt", 'A'),
        // ---- sarmalayıcılar: iç komutlar ayrıştırılır
        ("Bash", "bash -c \"cd /e/other && echo x > a.txt\"", 'D'),
        ("Bash", "sh -c 'cd /e/other; echo x > a.txt'", 'D'),
        ("Bash", @"cmd /c ""cd /d E:\other && echo x > a.txt""", 'D'),
        ("Bash", @"cmd /c cd /d E:\other && echo x > a.txt", 'A'),   // cmd /c içindeki cd kabuğu değiştirmez
        ("Bash", "sudo cd /e/other && echo x > a.txt", 'A'),         // ayrı süreçte cd: etkisiz
        ("Bash", @"powershell -Command ""Set-Location E:\other; Set-Content a.txt hi""", 'D'),
        ("Bash", "eval \"cd /e/other\"; echo x > a.txt", 'D'),
        ("Bash", "env -C /e/other rm a.txt", 'D'),
        ("Bash", "wsl --cd /mnt/e/other rm a.txt", 'D'),
        ("Bash", "git -C /e/other checkout .", 'D'),
        ("Bash", "git -C ../other clean -fd", 'D'),
        ("Bash", "tar -xzf a.tgz -C /e/other", 'D'),
        ("Bash", "npm --prefix /e/other install", 'D'),
        // ---- PowerShell
        ("PowerShell", @"Set-Location E:\other; Set-Content -Path a.txt -Value hi", 'D'),
        ("PowerShell", @"sl E:\other; ""x"" | Out-File a.txt", 'D'),
        ("PowerShell", @"Push-Location E:\other; Remove-Item a.txt", 'D'),
        ("PowerShell", @"Set-Location -Path E:\other; Add-Content log.txt x", 'D'),
        ("PowerShell", @"cd E:\other; echo hi > a.txt", 'D'),
        ("PowerShell", @"(Set-Location E:\other); Set-Content a.txt x", 'D'),   // PowerShell parantezi klasörü geri almaz
        ("PowerShell", @"Set-Location $env:USERPROFILE; Set-Content a.txt x", 'D'),
        ("PowerShell", @"Push-Location E:\other; Pop-Location; Set-Content a.txt x", 'A'),
        ("PowerShell", @"cd $env:FOO; Set-Content a.txt x", 'K'),
        ("PowerShell", @"Start-Process notepad -WorkingDirectory E:\other", 'A'), // bilinen sınır: sürecin içeriği görülmez
        // ---- cd ile proje içine gir, yaz: izin
        ("Bash", "cd src && echo x > out.txt", 'A'),
        ("Bash", "cd src/sub && cd .. && echo x > out.txt", 'A'),
        ("Bash", "cd /e/proj/src && echo x > a.txt", 'A'),
        ("Bash", "cd \"$PWD/src\" && echo x > a.txt", 'A'),
        ("Bash", "cd /e/proj/src && rm -rf node_modules", 'A'),
        ("Bash", "cd /e/other && echo x > /e/proj/a.txt", 'A'),          // mutlak proje içi hedef
        ("Bash", "cd /e/other && echo x > /dev/null", 'A'),
        ("Bash", "cd /e/other && echo x > /tmp/x.txt", 'A'),             // temp serbest
        ("Bash", "cd /e/other && echo \"x > a.txt\"", 'A'),              // tırnak içi komut değil
        // ---- dışarı çık, sonra proje içine dön: izin
        ("Bash", "cd /e/other && cd /e/proj && echo x > a.txt", 'A'),
        ("Bash", "cd /e/other && cd /e/proj/src && echo x > a.txt", 'A'),
        ("Bash", "cd /e/other && cd ../proj && echo x > a.txt", 'A'),
        ("PowerShell", @"Set-Location E:\other; Set-Location E:\proj; Set-Content a.txt x", 'A'),
        // ---- yazmayan komutlar: izin
        ("Bash", "cd /e/other && npm test", 'A'),
        ("Bash", "cd /e/other && ls -la | head", 'A'),
        ("Bash", "cd /e/other && cat a.txt", 'A'),
        ("Bash", "cd /e/other && git status", 'A'),
        ("Bash", "cd /e/other && dotnet build", 'A'),                    // bilinen sınır: yan etki görülmez
        ("Bash", "git -C /e/other status", 'A'),
        ("Bash", "git -C /e/proj/src add .", 'A'),
        ("Bash", "cd $X && ls", 'A'),
        // ---- çözülemeyen cd sonrası: göreli hedef sorulur, mutlak hedef normal değerlendirilir
        ("Bash", "cd $X && echo x > a.txt", 'K'),
        ("Bash", "cd \"$(git rev-parse --show-toplevel)\" && echo x > a.txt", 'K'),
        ("Bash", "cd - && echo x > a.txt", 'K'),
        ("Bash", "cd $X && echo x > ./a.txt", 'K'),
        ("Bash", "cd $X && rm a.txt", 'K'),
        ("Bash", "cd $X && echo x > /e/proj/a.txt", 'A'),
        ("Bash", "cd $X && cd /e/proj && echo x > a.txt", 'A'),          // sonraki mutlak cd durumu düzeltir
        // ---- denetim düzeltmesi B-7: cd sonrası find/ln/indirme
        ("Bash", "cd /e/other && find . -delete", 'D'),
        ("Bash", "cd /e/other && find . -name x -exec rm {} \\;", 'D'),
        ("Bash", "cd /e/other && ln -s a b", 'D'),
        ("Bash", "cd /e/other && curl -o x.zip https://x.test/x", 'D'),
        ("Bash", "cd /e/other && curl -O https://x.test/x.zip", 'D'),
        ("Bash", "cd /e/proj/sub && curl -O https://x.test/x.zip", 'A'),
        ("Bash", "cd /e/proj/sub && find . -name x -exec rm {} \\;", 'A'),
        ("PowerShell", @"cd E:\other; Invoke-WebRequest https://x.test/a -OutFile a.zip", 'D'),
    };

    [Fact]
    public void Tablo_cd_sonrasi_goreli_yol_kararlari_beklenenle_ayni()
    {
        var falseAlarms = new List<string>(); var misses = new List<string>(); var wrongAction = new List<string>();
        var totals = new Dictionary<char, int>();
        foreach (var (tool, cmd, exp) in Table)
        {
            var d = Eval(tool, cmd);
            var got = Code(d);
            totals[exp] = totals.GetValueOrDefault(exp) + 1;
            if (got == exp) continue;
            var line = $"{tool} {cmd.Replace("\n", "\\n")} -> beklenen {exp}, çıkan {got} ({d.Reason})";
            if (exp == 'A') falseAlarms.Add(line);
            else if (got == 'A') misses.Add(line);
            else wrongAction.Add(line);
        }
        _out.WriteLine($"Durum sayısı: {Table.Length} (izin {totals.GetValueOrDefault('A')}, engel {totals.GetValueOrDefault('D')}, sor {totals.GetValueOrDefault('K')})");
        _out.WriteLine($"Yanlış alarm: {falseAlarms.Count}, kaçırma: {misses.Count}, yanlış eylem: {wrongAction.Count}");
        foreach (var l in falseAlarms.Concat(misses).Concat(wrongAction)) _out.WriteLine("  " + l);
        Assert.True(Table.Length >= 25);
        Assert.Empty(falseAlarms);
        Assert.Empty(misses);
        Assert.Empty(wrongAction);
    }

    private const string RealRoot = @"E:\Sample Work\urun\deneme-proje";
    private const string RealCommand = "cd \"/e/Sample Work/urun/_gecici\" && printf 'merhaba' > \"disari-yazma-denemesi.txt\"; echo \"yazma çıkışı: $?\"; cat \"disari-yazma-denemesi.txt\"";

    [Fact]
    public void Gercek_oturumda_kacan_komut_birebir_engellenir()
    {
        var d = Eval("Bash", RealCommand, RealRoot);
        Assert.Equal("deny", d.Verdict);
        Assert.Contains("Proje dışına yazma yasak", d.Reason);
        Assert.Contains(@"e:\sample work\urun\_gecici\disari-yazma-denemesi.txt", d.Reason!.ToLowerInvariant());
    }

    [Fact]
    public void Gercek_komut_kancadan_gecince_deny_cikar_ve_olay_yazilir()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "projem");
        Directory.CreateDirectory(Path.Combine(env.Root, "baska"));
        using (var c0 = env.Svc.Db.Open())
        {
            var pid0 = IngestStore.EnsureProject(c0, null, dir);
            GuardStore.EnsureTables(c0);
            GuardStore.ApplyTemplate(c0, pid0, "temkinli");
        }
        var cmd = $"cd \"{Path.Combine(env.Root, "baska").Replace('\\', '/')}\" && printf 'merhaba' > \"disari.txt\"; echo $?";
        var stdin = System.Text.Json.JsonSerializer.Serialize(new { session_id = "cd-1", cwd = dir, hook_event_name = "PreToolUse", tool_name = "Bash", tool_input = new { command = cmd } });
        var o = new StringWriter();
        Assert.Equal(0, HookHandler.Run("pre-tool-use", new StringReader(stdin), o, env.DataDir, _ => null));
        Assert.Equal("deny", (string?)JsonNode.Parse(o.ToString())!["hookSpecificOutput"]!["permissionDecision"]);
        using var c = env.Svc.Db.Open();
        Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM guard_events WHERE action='deny' AND tool='Bash' AND tool_session_id='cd-1'"));
    }

    [Fact]
    public void Cozulemeyen_cd_sonrasi_goreli_yazma_sorar_neden_cumlesi_turkce()
    {
        var d = Eval("Bash", "cd $X && echo x > a.txt");
        Assert.Equal("ask", d.Verdict);
        Assert.Contains("çözülemedi", d.Reason);
        Assert.Contains("Bekçi soruyor", d.Reason);
        Assert.Contains("Devam edilsin mi", d.Reason);
    }

    [Fact]
    public void Cozulemeyen_cd_sonrasi_ayni_komutta_kesin_disari_yazma_deny_kazanir()
    {
        Assert.Equal("deny", Eval("Bash", "cd $X && echo x > a.txt && echo y > /e/other/b.txt").Verdict);
    }

    [Fact]
    public void Bilinen_sinirlar_listesi_cd_izlemeyi_anar()
    {
        Assert.Contains("python -c", GuardEngine.KnownGaps);
        Assert.Contains("cd -", GuardEngine.KnownGaps);
        Assert.Contains("boru/arka plan", GuardEngine.KnownGaps);
        Assert.Contains("Start-Process -WorkingDirectory", GuardEngine.KnownGaps);
    }

    [Fact]
    public void Ayristirici_komuta_klasor_damgasi_vurur()
    {
        var cmds = ShellParser.Parse("echo a > x; cd /e/other && echo b > y; (cd sub; echo c > z); echo d > w", false);
        var ctx = GuardEngine.ResolveDir(cmds.First(c => c.Head == "echo").Dir, @"E:\proj", new GuardInput("Bash", null, @"E:\proj", @"E:\proj", Home, Temp));
        Assert.Equal(@"E:\proj", ctx.Cwd);
        string Where(string redir)
        {
            var c = cmds.First(k => k.Redirs.Any(r => r.Target.Text == redir));
            return GuardEngine.ResolveDir(c.Dir, @"E:\proj", new GuardInput("Bash", null, @"E:\proj", @"E:\proj", Home, Temp)).Cwd!;
        }
        Assert.Equal(@"E:\proj", Where("x"));
        Assert.Equal(@"E:\other", Where("y"));
        Assert.Equal(@"E:\other\sub", Where("z"));
        Assert.Equal(@"E:\other", Where("w")); // alt kabuk bitince cd /e/other durumuna dönülür
    }
}
