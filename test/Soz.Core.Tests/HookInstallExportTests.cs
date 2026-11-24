using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Data;
using Soz.Core.Export;
using Soz.Core.Hooks;
using Soz.Core.Readers;
using Xunit;

namespace Soz.Core.Tests;

public class HookTests
{
    private static string RunHook(TestEnv env, string ev, string stdin, Dictionary<string, string?>? envVars = null)
    {
        var o = new StringWriter();
        var code = HookHandler.Run(ev, new StringReader(stdin), o, env.DataDir, k => envVars is not null && envVars.TryGetValue(k, out var v) ? v : null);
        Assert.Equal(0, code);
        return o.ToString();
    }

    private static (long pid, long star) Seed(TestEnv env)
    {
        using var c = env.Svc.Db.Open();
        var pid = IngestStore.EnsureProject(c, null, @"E:\ornek-proje");
        var star = c.ScalarLong("SELECT id FROM projects WHERE key='*'");
        return (pid, star);
    }

    [Fact]
    public void SessionStart_Returns_Packet_As_AdditionalContext()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, _) = Seed(env);
        env.Svc.CreateDecision(pid, "ban", "KarincaAI.exe dosyasını asla çalıştırma.");
        var json = RunHook(env, "session-start", """{"session_id":"s1","transcript_path":"x","cwd":"E:\\ornek-proje\\alt-klasor","hook_event_name":"SessionStart","source":"compact"}""");
        var o = JsonNode.Parse(json)!;
        Assert.Equal("SessionStart", (string?)o["hookSpecificOutput"]!["hookEventName"]);
        var ctx = (string)o["hookSpecificOutput"]!["additionalContext"]!;
        Assert.Contains("[Söz defteri · ornek-proje]", ctx);       // alt klasörden üst projeye çıkıldı
        Assert.Contains("- KarincaAI.exe dosyasını asla çalıştırma.", ctx);
        using var c = env.Svc.Db.Open();
        Assert.Equal(1L, c.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='hook_inject'"));
    }

    [Fact]
    public void SessionStart_Prints_Nothing_When_No_Approved_Decisions()
    {
        using var env = new TestEnv(copySamples: false);
        Seed(env);
        Assert.Equal("", RunHook(env, "session-start", """{"session_id":"s1","cwd":"E:\\ornek-proje","source":"startup"}"""));
    }

    [Fact]
    public void SessionStart_Uses_CLAUDE_PROJECT_DIR()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, _) = Seed(env);
        env.Svc.CreateDecision(pid, "rule", "Her zaman Türkçe yaz.");
        var json = RunHook(env, "session-start", """{"session_id":"s1","cwd":"C:\\baska","source":"startup"}""", new() { ["CLAUDE_PROJECT_DIR"] = @"E:\ornek-proje" });
        Assert.Contains("Her zaman Türkçe yaz.", json);
    }

    [Fact]
    public void UserPromptSubmit_Captures_And_Alerts_On_Repeat()
    {
        using var env = new TestEnv(copySamples: false);
        var (pid, _) = Seed(env);
        var d = env.Svc.CreateDecision(pid, "rule", "Her zaman Türkçe yaz.");
        using (var c = env.Svc.Db.Open()) c.Exec("UPDATE decisions SET repeat_count=4 WHERE id=@id", null, ("@id", d.Id));

        var json = RunHook(env, "user-prompt-submit", """{"session_id":"sess-9","transcript_path":"t","cwd":"E:\\ornek-proje","hook_event_name":"UserPromptSubmit","prompt":"Kaç kere söyledim, her zaman Türkçe yaz!"}""");
        var o = JsonNode.Parse(json)!;
        Assert.Equal("UserPromptSubmit", (string?)o["hookSpecificOutput"]!["hookEventName"]);
        Assert.Equal("Kullanıcı bu kuralı daha önce 4 kez söyledi: 'Her zaman Türkçe yaz.'. Kalıcı kuraldır, uy.", (string?)o["hookSpecificOutput"]!["additionalContext"]);

        using var c2 = env.Svc.Db.Open();
        Assert.Equal("hook", c2.Scalar("SELECT source FROM utterances WHERE text='Kaç kere söyledim, her zaman Türkçe yaz!'"));
        Assert.Equal("human", c2.Scalar("SELECT kind FROM sessions WHERE external_id='sess-9'"));
        Assert.Equal(1L, c2.ScalarLong("SELECT COUNT(*) FROM events WHERE kind='repeat_alert'"));

        // İlgisiz istem: kayıt olur, uyarı olmaz.
        Assert.Equal("", RunHook(env, "user-prompt-submit", """{"session_id":"sess-9","cwd":"E:\\ornek-proje","prompt":"Şu dosyadaki hatayı bulur musun?"}"""));
        Assert.Equal(2L, c2.ScalarLong("SELECT COUNT(*) FROM utterances"));
    }

    [Fact]
    public void Broken_Input_Exits_Zero_And_Logs()
    {
        using var env = new TestEnv(copySamples: false);
        Assert.Equal("", RunHook(env, "user-prompt-submit", "{bozuk"));
        Assert.Contains("user-prompt-submit", File.ReadAllText(Path.Combine(env.DataDir, "soz-hook.log")));
    }
}

public class InstallerTests
{
    [Fact]
    public void Install_Preserves_Existing_And_Uninstall_Removes_Only_Ours()
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = """
            {
              // kullanıcının yorumu
              "model": "opus",
              "permissions": { "allow": ["Bash(ls:*)"] },
              "hooks": {
                "SessionStart": [ { "matcher": "startup", "hooks": [ { "type": "command", "command": "echo merhaba" } ] } ],
                "Stop": [ { "hooks": [ { "type": "command", "command": "echo bitti" } ] } ]
              }
            }
            """;
        File.WriteAllText(path, original);

        HookInstaller.Install(path, @"E:\Sample Work\urun\yayin\soz.exe", @"E:\Sample Work\urun\veri");
        HookInstaller.Install(path, @"E:\Sample Work\urun\yayin\soz.exe", @"E:\Sample Work\urun\veri"); // ikinci kurulum çoğaltmaz
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.json.soz-kurulum-*.bak")); // ikinci kurulum değişmediği için yeni yedek yazmaz
        var written = File.ReadAllText(path);
        Assert.Contains("// kullanıcının yorumu", written); // yorum korunur
        var o = JsonNode.Parse(written, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();
        Assert.Equal("opus", (string?)o["model"]);
        Assert.Equal("Bash(ls:*)", (string?)o["permissions"]!["allow"]![0]);
        var ss = o["hooks"]!["SessionStart"]!.AsArray();
        Assert.Equal(2, ss.Count);
        Assert.Equal("echo merhaba", (string?)ss[0]!["hooks"]![0]!["command"]);
        Assert.Equal("'E:/Sample Work/urun/yayin/soz.exe' hook session-start --data 'E:/Sample Work/urun/veri' 2>/dev/null || true", (string?)ss[1]!["hooks"]![0]!["command"]);
        Assert.Single(o["hooks"]!["UserPromptSubmit"]!.AsArray());
        Assert.NotNull(o["hooks"]!["Stop"]);

        Assert.True(HookInstaller.Uninstall(path));
        Assert.Equal(original, File.ReadAllText(path)); // kurulumdan önceki içerik bayt bayt
        var after = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();
        Assert.Single(after["hooks"]!["SessionStart"]!.AsArray());
        Assert.Null(after["hooks"]!["UserPromptSubmit"]);
        Assert.NotNull(after["hooks"]!["Stop"]);
        Assert.Equal("opus", (string?)after["model"]);
        Assert.False(HookInstaller.Uninstall(path));
    }

    [Fact]
    public void Install_Creates_File_When_Missing()
    {
        using var env = new TestEnv(copySamples: false);
        var path = Path.Combine(env.Root, "yeni", ".claude", "settings.json");
        HookInstaller.Install(path, @"C:\x\soz.exe", null, dotnetRoot: @"D:\dotnet");
        var cmd = (string?)JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!["command"];
        Assert.Equal("DOTNET_ROOT='D:/dotnet' 'C:/x/soz.exe' hook user-prompt-submit 2>/dev/null || true", cmd);
        Assert.False(File.Exists(path + ".bak"));
    }
}

public class ExporterTests
{
    [Fact]
    public void Block_Is_Inserted_Updated_Removed_Without_Touching_Outside()
    {
        var original = "# Proje\n\nElle yazılmış kurallar.\n";
        var v1 = AgentsFileExporter.Merge(original, "[Söz defteri · p]\nKurallar:\n- A");
        Assert.StartsWith(original, v1);
        Assert.Contains("<!-- soz:basla -->\n[Söz defteri · p]\nKurallar:\n- A\n<!-- soz:bitir -->", v1);

        var edited = v1 + "\n## Kullanıcının sonradan eklediği bölüm\n";
        var v2 = AgentsFileExporter.Merge(edited, "[Söz defteri · p]\nKurallar:\n- B");
        Assert.Contains("- B", v2);
        Assert.DoesNotContain("- A", v2);
        Assert.StartsWith(original, v2);
        Assert.EndsWith("## Kullanıcının sonradan eklediği bölüm\n", v2);

        var v3 = AgentsFileExporter.Merge(v2, "");
        Assert.DoesNotContain("soz:basla", v3);
        Assert.StartsWith("# Proje", v3);
        Assert.Contains("## Kullanıcının sonradan eklediği bölüm", v3);
    }

    [Fact]
    public void Crlf_Is_Preserved_And_File_Created()
    {
        using var env = new TestEnv(copySamples: false);
        var f = Path.Combine(env.Root, "AGENTS.md");
        AgentsFileExporter.WriteBlock(f, "x\ny");
        Assert.Equal("<!-- soz:basla -->\nx\ny\n<!-- soz:bitir -->\n", File.ReadAllText(f));
        var crlf = AgentsFileExporter.Merge("a\r\nb\r\n", "x\ny");
        Assert.Equal("a\r\nb\r\n\r\n<!-- soz:basla -->\r\nx\r\ny\r\n<!-- soz:bitir -->\r\n", crlf);
    }

    [Fact]
    public void Export_Refuses_Real_Project_By_Default()
    {
        using var env = new TestEnv(copySamples: false);
        var ex = Assert.Throws<SozException>(() => env.Svc.Export(Path.Combine(env.Root, "gercek-proje"), agents: true, claude: false));
        Assert.Equal(409, ex.Status);
        Assert.False(File.Exists(Path.Combine(env.Root, "gercek-proje", "AGENTS.md")));
    }
}

public class MigrationTests
{
    [Fact]
    public void V1_Database_Is_Migrated_Keeping_Data()
    {
        using var env = new TestEnv(copySamples: false);
        var dir = Path.Combine(env.Root, "eski");
        Directory.CreateDirectory(dir);
        var db = new SozDb(dir);
        using (var c = db.Open())
        {
            c.Exec(SozDb.SchemaV1);
            c.Exec("PRAGMA user_version=1");
            c.Exec("INSERT INTO projects(key,name,path,created_at) VALUES('e:\\eski','eski','E:\\eski','2026-01-01')");
            c.Exec("INSERT INTO sessions(tool,external_id,project_id,file_path) VALUES('claude','s1',1,'f')");
            c.Exec("INSERT INTO utterances(session_id,project_id,ts,text,source_line,hash) VALUES(1,1,'2026-01-01T00:00:00.000Z','eski mesaj',3,'h')");
        }
        Assert.Equal(1, db.GetVersion());
        db.Migrate();
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion());
        using var c2 = db.Open();
        Assert.Equal("eski mesaj", c2.Scalar("SELECT text FROM utterances"));
        Assert.Equal("transcript", c2.Scalar("SELECT source FROM utterances"));
        Assert.Equal("human", c2.Scalar("SELECT kind FROM sessions"));
        Assert.Equal(1L, c2.ScalarLong("SELECT COUNT(*) FROM projects WHERE key='*'"));
        db.Migrate(); // ikinci kez çalıştırmak zararsız
        Assert.Equal(SozDb.CurrentVersion, db.GetVersion());
    }
}
