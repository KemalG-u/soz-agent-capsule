using System.Text.Json.Nodes;
using Soz.Core.Guard;
using Xunit;
using Xunit.Abstractions;

namespace Soz.Core.Tests;

/// <summary>Bekçi motoru tablo testi. Hiçbir komut ÇALIŞTIRILMAZ: motor yalnız metni değerlendirir.</summary>
public class GuardEngineTests
{
    private readonly ITestOutputHelper _out;
    public GuardEngineTests(ITestOutputHelper o) => _out = o;

    private const string Root = @"E:\proj";
    private const string Home = @"C:\Users\tester";
    private const string Temp = @"C:\Users\tester\AppData\Local\Temp";

    private static IReadOnlyList<GuardRule> Rules()
    {
        var list = new List<GuardRule>();
        long id = 1;
        foreach (var tid in new[] { "temkinli", "surec-oldurme", "genel-kurulum", "disk", "korunan-yollar" })
            foreach (var tr in GuardTemplates.Find(tid)!.Rules)
                list.Add(new GuardRule(id++, 1, tr.Kind, tr.Label, tr.Spec, tr.Action, true, null, "template", null));
        return list;
    }

    private static GuardDecision Eval(string tool, string key, string value, IReadOnlyList<GuardRule>? rules = null, string cwd = Root)
        => GuardEngine.Evaluate(rules ?? Rules(), new GuardInput(tool, new JsonObject { [key] = value }, cwd, Root, Home, Temp));

    private static GuardDecision Sh(string cmd, string tool = "Bash") => Eval(tool, "command", cmd);

    // (araç, anahtar, değer, beklenen: A izin / D engel / K sor, açıklama)
    private static readonly (string tool, string key, string val, char exp)[] Table =
    {
        // ---- izin verilmesi gerekenler
        ("Bash", "command", "git status", 'A'),
        ("Bash", "command", "ls -la", 'A'),
        ("Bash", "command", "echo \"rm -rf /\"", 'A'),
        ("Bash", "command", "echo 'rm -rf /'", 'A'),
        ("Bash", "command", "echo \"taskkill /f /im node.exe\"", 'A'),
        ("Bash", "command", "git commit -m \"remove rm -rf from docs\"", 'A'),
        ("Bash", "command", "grep -r \"rm -rf\" .", 'A'),
        ("Write", "file_path", @"E:\proj\src\a.cs", 'A'),
        ("Write", "file_path", "src/a.cs", 'A'),
        ("Edit", "file_path", "/e/proj/src/a.cs", 'A'),
        ("Edit", "file_path", "E:/PROJ/Src/A.cs", 'A'),
        ("Bash", "command", "echo hi > out.txt", 'A'),
        ("Bash", "command", @"echo hi >> E:\proj\log.txt", 'A'),
        ("Bash", "command", "echo hi > /dev/null", 'A'),
        ("Bash", "command", "ls 2>&1 | head", 'A'),
        ("Bash", "command", "dotnet build 2>nul", 'A'),
        ("Bash", "command", "cat > /tmp/x.txt <<'EOF'\nrm -rf /\nEOF", 'A'),
        ("Bash", "command", "npm install", 'A'),
        ("Bash", "command", "npm i -D vitest", 'A'),
        ("Bash", "command", "git push origin main", 'A'),
        ("Bash", "command", "git push -u origin feature", 'A'),
        ("Bash", "command", "git reset --soft HEAD~1", 'A'),
        ("Bash", "command", "rm file.txt", 'A'),
        ("Bash", "command", "rm -f build.log", 'A'),
        ("Bash", "command", "mkdir -p src/new", 'A'),
        ("Bash", "command", "cp a.txt b.txt", 'A'),
        ("Bash", "command", "mv old.txt new.txt", 'A'),
        ("Bash", "command", "sed -i 's/a/b/' src/a.cs", 'A'),
        ("PowerShell", "command", "Get-ChildItem -Recurse", 'A'),
        ("PowerShell", "command", "Set-Content -Path .\\a.txt -Value \"x\"", 'A'),
        ("PowerShell", "command", "Remove-Item .\\old.txt", 'A'),
        ("Bash", "command", "git log --format=%H", 'A'),
        ("Bash", "command", "echo killall", 'A'),
        ("Bash", "command", "# rm -rf /\nls", 'A'),
        ("Bash", "command", "python -m pytest", 'A'),
        ("Bash", "command", "curl -s https://example.com -o out.json", 'A'),
        ("Write", "file_path", @"C:\Users\tester\AppData\Local\Temp\claude\x.txt", 'A'),
        ("NotebookEdit", "notebook_path", @"E:\proj\n.ipynb", 'A'),
        ("Bash", "command", "echo \"a > b\"", 'A'),
        ("Bash", "command", "git commit -m \"$(cat <<'EOF'\nfix: rm -rf stuff, taskkill\nEOF\n)\"", 'A'),
        ("Read", "file_path", @"C:\Windows\win.ini", 'A'),
        ("Bash", "command", "cd /e/proj && npm test", 'A'),
        ("Bash", "command", @"cp E:\other\a.txt .", 'A'),
        ("Write", "file_path", @"E:\proj\.env.example", 'A'),
        ("PowerShell", "command", "Write-Output \"Remove-Item -Recurse C:\\\"", 'A'),
        ("Bash", "command", "printf 'taskkill' | cat", 'A'),
        ("mcp__x__y", "command", "rm -rf /", 'A'),
        ("Bash", "command", "git diff --stat | head -5", 'A'),
        ("Bash", "command", "cat README.md", 'A'),
        ("Bash", "command", "echo $HOME", 'A'),
        ("Bash", "command", "ls /e/proj/src", 'A'),
        // ---- engellenmesi gerekenler (proje dışı yazma, korunan yol, süreç, disk)
        ("Write", "file_path", @"E:\other\x.txt", 'D'),
        ("Write", "file_path", @"C:\Windows\system32\x.dll", 'D'),
        ("Edit", "file_path", "../outside/a.cs", 'D'),
        ("Write", "file_path", "/e/other/x", 'D'),
        ("Write", "file_path", @"E:\PROJ\..\other\x.txt", 'D'),
        ("Write", "file_path", @"e:\proj2\x.txt", 'D'),
        ("Bash", "command", @"echo x > E:\other\a.txt", 'D'),
        ("Bash", "command", "echo x >> /c/Users/tester/notes.txt", 'D'),
        ("Bash", "command", "cat a | tee ../outside.txt", 'D'),
        ("Bash", "command", "rm /e/other/a.txt", 'D'),
        ("Bash", "command", "mv a.txt ../b.txt", 'D'),
        ("Bash", "command", @"cp a.txt E:\other\", 'D'),
        ("Bash", "command", "sed -i 's/a/b/' ../x.cs", 'D'),
        ("PowerShell", "command", @"Set-Content -Path D:\x\a.txt -Value hi", 'D'),
        ("PowerShell", "command", @"Out-File -FilePath C:\out.txt", 'D'),
        ("PowerShell", "command", "\"x\" | Add-Content ~\\notes.txt", 'D'),
        ("PowerShell", "command", @"New-Item -ItemType File -Path E:\other\a.txt", 'D'),
        ("Bash", "command", "mkdir /e/other/dir", 'D'),
        ("Bash", "command", "touch ~/x", 'D'),
        ("Bash", "command", "bash -c \"echo x > E:\\other\\a\"", 'D'),
        ("Bash", "command", "cd E:\\proj && echo x > ../a.txt", 'D'),
        ("Write", "file_path", @"E:\proj\.env", 'D'),
        ("Edit", "file_path", @"E:\proj\.git\config", 'D'),
        ("Write", "file_path", "~/.ssh/config", 'D'),
        ("Bash", "command", "echo x > .env", 'D'),
        ("Bash", "command", "taskkill /f /im node.exe", 'D'),
        ("PowerShell", "command", "Stop-Process -Name node", 'D'),
        ("Bash", "command", "pm2 delete all", 'D'),
        ("Bash", "command", "cmd /c taskkill /f /im x.exe", 'D'),
        ("Bash", "command", "echo ok && taskkill /im a.exe", 'D'),
        ("Bash", "command", "powershell -Command \"Stop-Process -Id 4\"", 'D'),
        ("Bash", "command", "format C:", 'D'),
        ("Bash", "command", "diskpart", 'D'),
        ("Bash", "command", "pkill -f node", 'D'),
        ("Bash", "command", "echo $(taskkill /im a.exe)", 'D'),
        ("Bash", "command", "sudo rm -rf /var/x", 'D'),
        ("Bash", "command", "cmd.exe /c rd /s /q C:\\x", 'D'),
        ("Bash", "command", "rm -rf $HOME/x", 'D'),
        ("Bash", "command", "FOO=1 taskkill /im a.exe", 'D'),
        ("Bash", "command", "nohup taskkill /im a.exe &", 'D'),
        // ---- önce sorulması gerekenler (toplu silme, zorla gönderme, yayın, genel kurulum)
        ("Bash", "command", "rm -rf build_out", 'K'), // eskiden node_modules: 2. sürümde proje içi yeniden üretilebilir klasör sorulmaz (aşağıda)
        ("Bash", "command", "rm -fr build", 'K'),
        ("Bash", "command", "rm -r dir", 'K'),
        ("Bash", "command", "rm -Rf dir", 'K'),
        ("Bash", "command", "rm --recursive dir", 'K'),
        ("PowerShell", "command", "Remove-Item -Recurse -Force .\\build", 'K'),
        ("PowerShell", "command", "ri -r x", 'K'),
        ("PowerShell", "command", "Remove-Item -Path .\\x -Recurse", 'K'),
        ("PowerShell", "command", "Remove-Item -Rec .\\x", 'K'),
        ("Bash", "command", "del /s /q *.tmp", 'K'),
        ("Bash", "command", "rd /s /q build", 'K'),
        ("Bash", "command", "git push --force", 'K'),
        ("Bash", "command", "git push -f origin main", 'K'),
        ("Bash", "command", "git push origin main --force-with-lease", 'K'),
        ("Bash", "command", "git reset --hard HEAD~3", 'K'),
        ("Bash", "command", "npm publish", 'K'),
        ("Bash", "command", "gh release create v1", 'K'),
        ("Bash", "command", "vercel --prod", 'K'),
        ("Bash", "command", "npm i -g typescript", 'K'),
        ("Bash", "command", "npm install --global x", 'K'),
        ("Bash", "command", "winget install foo", 'K'),
        ("Bash", "command", "choco install x", 'K'),
        ("Bash", "command", "find . -name \"*.tmp\" -delete", 'K'),
        ("Bash", "command", "ls && rm -rf docs", 'K'), // eskiden dist: yeniden üretilebilir, artık sorulmaz
        ("Bash", "command", "echo a | xargs rm -rf", 'K'),
        ("Bash", "command", "bash -c 'rm -rf build'", 'K'),
        ("Bash", "command", "git clean -fd", 'K'),
        ("Bash", "command", "env FOO=1 rm -rf out", 'K'),
        ("Bash", "command", "find . -exec rm -rf {} \\;", 'K'),
        ("Bash", "command", "rm -rf \"build dir\"", 'K'),
        ("Bash", "command", "eval \"rm -rf x\"", 'K'),
        ("Bash", "command", "rm -rf $UNKNOWN/x", 'K'),
        ("Bash", "command", "if true; then rm -rf out; fi", 'K'),
        ("Bash", "command", "for f in a b; do rm -rf $f; done", 'K'),
        ("Bash", "command", "(cd sub && rm -rf out)", 'K'),
        // ---- 2. sürüm: bekçi varsayılan düzeltmeleri
        ("Bash", "command", "rm -rf node_modules", 'A'),
        ("Bash", "command", "ls && rm -rf dist", 'A'),
        ("Bash", "command", "rm -rf bin obj", 'A'),
        ("Bash", "command", "rm -rf .next target __pycache__", 'A'),
        ("PowerShell", "command", @"Remove-Item -Recurse -Force .\node_modules", 'A'),
        ("Bash", "command", "rd /s /q dist", 'A'),
        ("Bash", "command", "rm -rf src/app/__pycache__", 'A'),
        ("Bash", "command", "rm -rf dist/*", 'A'),
        ("Bash", "command", "rm -rf node_modules src", 'K'),
        ("Bash", "command", "rm -rf ../baska/node_modules", 'D'),
        ("Bash", "command", "rm -rf /e/other/node_modules", 'D'),
        ("Bash", "command", "rm -rf $DIR/node_modules", 'K'),
        ("Bash", "command", "rm -rf .", 'K'),
        ("Bash", "command", "kill -0 1234", 'A'),
        ("Bash", "command", "kill -l", 'A'),
        ("Bash", "command", "kill -s 0 1234", 'A'),
        ("Bash", "command", "kill 1234", 'D'),
        ("Bash", "command", "kill -9 1234", 'D'),
        ("Bash", "command", "npm run publish", 'A'),
        ("Bash", "command", "pnpm run publish", 'A'),
        ("Bash", "command", "yarn publish", 'K'),
        ("Bash", "command", "npm publish --access public", 'K'),
        ("Write", "file_path", @"C:\Users\tester\.claude\plans\plan-1.md", 'A'),
        ("Write", "file_path", @"C:\Users\tester\.claude\todos\a.json", 'A'),
        ("Write", "file_path", @"C:\Users\tester\.claude\projects\E--proj\memory\MEMORY.md", 'A'),
        ("Write", "file_path", "~/.claude/plans/x.md", 'A'),
        ("Write", "file_path", @"C:\Users\tester\.claude\settings.json", 'D'),
        ("Write", "file_path", @"C:\Users\tester\.claude\CLAUDE.md", 'D'),
        ("Write", "file_path", @"C:\Users\tester\.claude\projects\E--proj\abc.jsonl", 'D'),
        ("Bash", "command", "echo x > /tmp/soz.txt", 'A'),
        // ---- denetim düzeltmesi B-7: find/ln/indirme/xargs/zorla gönderme/git sıfırlama kaçakları
        ("Bash", "command", "find /e/other -name x -exec rm {} \\;", 'D'),
        ("Bash", "command", "find /e/other -type f -exec rm -f {} +", 'D'),
        ("Bash", "command", "find ../out -execdir rm {} \\;", 'D'),
        ("Bash", "command", "find /e/other -delete", 'D'),
        ("Bash", "command", "find ../out -type f -delete", 'D'),
        ("Bash", "command", "find . -name '*.log' -exec rm {} \\;", 'A'),
        ("Bash", "command", "find src -name '*.tmp' -delete", 'K'),
        ("Bash", "command", "find /e/proj/src -name x -exec cat {} \\;", 'A'),
        ("Bash", "command", "ln -s /e/proj/a /e/other/link", 'D'),
        ("Bash", "command", "ln -sf a ../out/link", 'D'),
        ("Bash", "command", "ln -s target link", 'A'),
        ("Bash", "command", "ln -s /e/other link", 'A'),
        ("PowerShell", "command", "Invoke-WebRequest https://x.test/a.zip -OutFile E:\\other\\a.zip", 'D'),
        ("PowerShell", "command", "iwr https://x.test/a.zip -OutFile ..\\a.zip", 'D'),
        ("PowerShell", "command", "Invoke-RestMethod https://x.test/a -OutFile .\\a.json", 'A'),
        ("PowerShell", "command", "irm https://x.test/a -OutFile C:\\Users\\tester\\a.json", 'D'),
        ("Bash", "command", "curl.exe -o E:\\other\\a.zip https://x.test/a.zip", 'D'),
        ("Bash", "command", "curl -sSLo /e/other/a https://x.test/a", 'D'),
        ("Bash", "command", "curl -O --output-dir /e/other https://x.test/a.zip", 'D'),
        ("Bash", "command", "curl -O https://x.test/a.zip", 'A'),
        ("Bash", "command", "wget -O /e/other/a https://x.test/a", 'D'),
        ("Bash", "command", "wget -P /e/other https://x.test/a.zip", 'D'),
        ("Bash", "command", "wget -O a.zip https://x.test/a.zip", 'A'),
        ("Bash", "command", "echo a | xargs rm", 'K'),
        ("Bash", "command", "git ls-files | xargs -0 rm -f", 'K'),
        ("Bash", "command", "ls | xargs -I{} rm {}", 'K'),
        ("Bash", "command", "echo a | xargs echo", 'A'),
        ("Bash", "command", "git push origin +main", 'K'),
        ("Bash", "command", "git push origin +HEAD:main", 'K'),
        ("Bash", "command", "git push --force-with-lease=main origin main", 'K'),
        ("Bash", "command", "git push origin main:release", 'A'),
        ("Bash", "command", "git checkout -- .", 'K'),
        ("Bash", "command", "git checkout .", 'K'),
        ("Bash", "command", "git restore .", 'K'),
        ("Bash", "command", "git restore -- .", 'K'),
        ("Bash", "command", "git clean -f", 'K'),
        ("Bash", "command", "git checkout main", 'A'),
        ("Bash", "command", "git restore --staged src/a.cs", 'A'),
        // ---- denetim düzeltmesi B-3(c): Söz'ün kendi kayıtları ve ayarları
        ("Write", "file_path", @"E:\proj\.claude\settings.json", 'D'),
        ("Edit", "file_path", @"E:\proj\.claude\settings.local.json", 'D'),
        ("Bash", "command", "echo {} > .claude/settings.json", 'D'),
        ("Bash", "command", "cp x.json .claude/settings.local.json", 'D'),
        ("Write", "file_path", @"E:\proj\.claude\notes.md", 'A'),
        ("Write", "file_path", @"E:\proj\veri\soz.db", 'D'),
        ("Bash", "command", "cp x.db /e/proj/veri/soz.db-wal", 'D'),
        ("Bash", "command", "sqlite3 veri/soz.db \"update checks set enabled=0\"", 'D'),
        ("Bash", "command", "soz gate off --project x", 'D'),
        ("Bash", "command", "soz.exe checks disable 3", 'D'),
        ("Bash", "command", "E:\\proj\\yayin\\soz.exe --data veri checks remove 3", 'D'),
        ("Bash", "command", "soz checks add --kind command --cmd \"exit 0\" --label x --project y", 'D'),
        ("Bash", "command", "soz guard apply temkinli --project x", 'D'),
        ("Bash", "command", "curl -X PATCH http://127.0.0.1:49300/api/checks/1 -d '{}'", 'D'),
        ("Bash", "command", "curl http://127.0.0.1:49300/api/session", 'D'),
        ("PowerShell", "command", "Invoke-RestMethod http://127.0.0.1:49300/api/checks/1 -Method Patch", 'D'),
        ("PowerShell", "command", "iwr http://127.0.0.1:49300/api/session", 'D'),
        ("Bash", "command", "soz status", 'D'),
        ("Bash", "command", "soz job show 3", 'D'),
        ("Bash", "command", "soz morning", 'D'),
        ("Bash", "command", "soz doctor", 'D'),
        ("Bash", "command", "soz checks list --project x", 'D'),
        ("Bash", "command", "soz hook pre-tool-use", 'A'),
        ("Bash", "command", "python -c \"import sqlite3; sqlite3.connect('veri/soz.db')\"", 'D'),
        ("Bash", "command", "curl http://127.1:49301/api/jobs/1/accept -X POST", 'D'),
        ("Bash", "command", "curl http://2130706433:49300/api/session", 'D'),
        ("Bash", "command", "curl http://[::1]:49300/api/session", 'D'),
        ("Bash", "command", "curl http://localhost:49300/api/settings", 'D'),
        ("Bash", "command", "node -e \"fetch('http://0x7f.0.0.1:49300/api/session')\"", 'D'),
        ("Bash", "command", "curl https://example.test/api/data", 'A'),
        ("Bash", "command", "curl https://example.com/api/checks", 'A'),
        ("Bash", "command", "curl http://localhost:3000/", 'A'),
    };

    private static char Code(GuardDecision d) => d.Verdict switch { "deny" => 'D', "ask" => 'K', _ => 'A' };

    [Fact]
    public void Tablo_bekci_kararlari_beklenenle_ayni()
    {
        var falseAlarms = new List<string>(); var misses = new List<string>(); var wrongAction = new List<string>();
        var totals = new Dictionary<char, int>();
        foreach (var (tool, key, val, exp) in Table)
        {
            var d = Eval(tool, key, val);
            var got = Code(d);
            totals[exp] = totals.GetValueOrDefault(exp) + 1;
            if (got == exp) continue;
            var line = $"{tool} {val.Replace("\n", "\\n")} -> beklenen {exp}, çıkan {got} ({d.Reason})";
            if (exp == 'A') falseAlarms.Add(line);
            else if (got == 'A') misses.Add(line);
            else wrongAction.Add(line);
        }
        _out.WriteLine($"Durum sayısı: {Table.Length} (izin {totals.GetValueOrDefault('A')}, engel {totals.GetValueOrDefault('D')}, sor {totals.GetValueOrDefault('K')})");
        _out.WriteLine($"Yanlış alarm: {falseAlarms.Count}, kaçırma: {misses.Count}, yanlış eylem: {wrongAction.Count}");
        foreach (var l in falseAlarms.Concat(misses).Concat(wrongAction)) _out.WriteLine("  " + l);
        Assert.True(Table.Length >= 60);
        Assert.Empty(falseAlarms);
        Assert.Empty(misses);
        Assert.Empty(wrongAction);
    }

    [Fact]
    public void Neden_cumlesi_kural_etiketini_icerir_ve_turkcedir()
    {
        var d = Sh(@"echo x > E:\other\a.txt");
        Assert.Equal("deny", d.Verdict);
        Assert.Contains("Proje dışına yazma yasak", d.Reason);
        Assert.Contains("Bekçi durdurdu", d.Reason);
        var k = Sh("git push --force");
        Assert.Equal("ask", k.Verdict);
        Assert.Contains("Zorla gönderme", k.Reason);
        Assert.Contains("Devam edilsin mi", k.Reason);
    }

    [Fact]
    public void Deny_ask_uzerinde_onceliklidir()
    {
        // proje dışına kopya hedefi + rm -rf: engel kazanır
        Assert.Equal("deny", Sh(@"rm -rf E:\other\x").Verdict);
    }

    [Fact]
    public void Cozulemeyen_hedef_bilinmiyor_sayilir_engellenmez()
    {
        var d = Sh("echo x > $OUT/file");
        Assert.Equal("allow", d.Verdict);
        Assert.True(d.Unknown);
        var d2 = Sh("echo x > $(mktemp)");
        Assert.Equal("allow", d2.Verdict);
        Assert.True(d2.Unknown);
    }

    [Fact]
    public void Yol_cozumleme_bicimleri()
    {
        string? K(string p, string cwd = @"E:\proj") => GuardPaths.Normalize(p, cwd, Home, Temp)?.Key;
        Assert.Equal("e:/proj/a/b.txt", K(@"E:\Proj\a\b.txt"));
        Assert.Equal("e:/proj/a/b.txt", K("/e/proj/a/b.txt"));
        Assert.Equal("e:/proj/a/b.txt", K("/mnt/e/proj/a/b.txt"));
        Assert.Equal("e:/proj/a/b.txt", K("a/b.txt"));
        Assert.Equal("e:/other", K("../other"));
        Assert.Equal("e:/other/x", K(@"E:\proj\..\other\x"));
        Assert.Equal("e:/proj/src/x", K("./src/./x"));
        Assert.Equal("c:/users/tester/x", K("~/x"));
        Assert.Equal("c:/users/tester/x", K("$HOME/x"));
        Assert.Equal("c:/users/tester/x", K(@"%USERPROFILE%\x"));
        Assert.Equal("c:/users/tester/appdata/local/temp/x", K("/tmp/x"));
        Assert.Null(K("$FOO/x"));
        Assert.Null(K("$(pwd)/x"));
        Assert.Equal("e:/", K("/e"));
        Assert.Equal("e:/proj", K(@"\\?\E:\proj"));
    }

    [Fact]
    public void Ayrisma_komut_yapisi()
    {
        var c = ShellParser.Parse("echo \"rm -rf /\"", false);
        Assert.Single(c);
        Assert.Equal("echo", c[0].Head);
        var c2 = ShellParser.Parse("a && b | c; d &", false);
        Assert.Equal(new[] { "a", "b", "c", "d" }, c2.Select(x => x.Head));
        var c3 = ShellParser.Parse("sudo -u root env X=1 nohup /usr/bin/rm.exe -rf a", false);
        Assert.Equal("rm", c3.Last().Head);
        var c4 = ShellParser.Parse("powershell -NoProfile -Command \"Remove-Item -Recurse x; Get-Date\"", false);
        Assert.Contains(c4, x => x.Head == "remove-item");
        var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("Stop-Process -Id 1"));
        var c5 = ShellParser.Parse("powershell -EncodedCommand " + enc, false);
        Assert.Contains(c5, x => x.Head == "stop-process");
    }

    [Fact]
    public void Bilinen_kacirmalar_dürüstçe_izin_verilir_ve_belgelidir()
    {
        // Bu biçimlerden hedef yol çıkarılamıyor: engellenmez (sözleşme). Raporda "bilinen kusur" olarak listelenir.
        var gaps = new[]
        {
            "python -c \"open('E:/other/x','w').write('x')\"",
            "node -e \"require('fs').writeFileSync('E:/other/x','y')\"",
            "bash run.sh",
            "echo x > $OUT/file",
            "ln -s /e/other link",
        };
        foreach (var g in gaps) Assert.Equal("allow", Sh(g).Verdict);
        Assert.Equal("allow", Sh("[IO.File]::WriteAllText('C:\\x.txt','y')", "PowerShell").Verdict);
        Assert.Contains("python -c", GuardEngine.KnownGaps);
    }

    [Fact]
    public void Devre_disi_kural_ve_bos_kural_listesi_izin_verir()
    {
        var off = Rules().Select(r => r with { Enabled = false }).ToList();
        Assert.Equal("allow", Eval("Bash", "command", "taskkill /im x.exe", off).Verdict);
        Assert.Equal("allow", Eval("Bash", "command", "taskkill /im x.exe", new List<GuardRule>()).Verdict);
    }

    [Fact]
    public void Scope_only_allow_listesi_ve_temp_kapatma()
    {
        var spec = new JsonObject { ["allow"] = new JsonArray(@"E:\paylasim\**"), ["allowTemp"] = false };
        var rules = new[] { new GuardRule(1, 1, "scope_only", "Kapsam", spec, "deny", true, null, "user", null) };
        Assert.Equal("allow", Eval("Write", "file_path", @"E:\paylasim\a.txt", rules).Verdict);
        Assert.Equal("deny", Eval("Write", "file_path", Temp + @"\a.txt", rules).Verdict);
        Assert.Equal("deny", Eval("Write", "file_path", @"E:\baska\a.txt", rules).Verdict);
    }

    [Fact]
    public void Ask_eylemli_scope_only_sorar()
    {
        var rules = new[] { new GuardRule(1, 1, "scope_only", "Kapsam", new JsonObject(), "ask", true, null, "user", null) };
        Assert.Equal("ask", Eval("Write", "file_path", @"E:\baska\a.txt", rules).Verdict);
    }

    [Fact]
    public void Regex_kalibi_calisir_ve_bozuk_regex_cokmez()
    {
        var good = new GuardRule(1, 1, "command_block", "Re", new JsonObject { ["patterns"] = new JsonArray("re:^curl .*example\\.com") }, "deny", true, null, "user", null);
        Assert.Equal("deny", Eval("Bash", "command", "curl http://example.com", new[] { good }).Verdict);
        var bad = new GuardRule(2, 1, "command_block", "Bozuk", new JsonObject { ["patterns"] = new JsonArray("re:(") }, "deny", true, null, "user", null);
        Assert.Equal("allow", Eval("Bash", "command", "ls", new[] { bad }).Verdict);
    }
}
