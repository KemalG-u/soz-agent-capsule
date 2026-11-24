using Soz.Core.Data;
using Soz.Core.Readers;
using Xunit;

namespace Soz.Core.Tests;

public class ReaderTests(Xunit.Abstractions.ITestOutputHelper _out)
{
    private static List<(string tool, string ext, string text, string source)> Utterances(TestEnv env)
    {
        using var c = env.Svc.Db.Open();
        return c.Query("SELECT s.tool, s.external_id, u.text, u.source FROM utterances u JOIN sessions s ON s.id=u.session_id ORDER BY u.ts",
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
    }

    [Fact]
    public void Claude_OnlyHumanMessages_AreRead()
    {
        using var env = new TestEnv();
        var rep = new ClaudeCodeReader().Read(new IngestStore(env.Svc.Db), env.ClaudeRoot, new ReadOptions());
        Assert.True(rep.Found);
        Assert.Equal(3, rep.Files);
        Assert.Equal(1, rep.SubagentFiles);
        Assert.Equal(1, rep.BadLines);            // "{bozuk satır" sayıldı, çökmedi
        Assert.Equal(0, rep.Unrecognized);
        Assert.Equal(1, rep.NewCompactions);

        var texts = Utterances(env).Where(u => u.tool == "claude").Select(u => u.text).ToList();
        foreach (var t in texts) _out.WriteLine(t);
        // Oturum 1: 6 insan mesajı (biri kuyruğa yazılmış, biri liste içerikli, biri öneriden kabul); oturum 2: 2; proje 2: 2.
        Assert.Equal(10, texts.Count);
        Assert.Contains("Asla KarincaAI.exe dosyasını çalıştırma.", texts);           // gömülü <system-reminder> temizlendi
        Assert.Contains("Kaç kere söyledim, raporları Türkçe yaz.", texts);           // queued_command (origin human)
        Assert.Contains("Varsayılan dil Türkçe olsun, İngilizce ikinci dil.", texts); // liste içerik
        Assert.DoesNotContain(texts, t => t.Contains("Fransızca"));   // sistem hatırlatması
        Assert.DoesNotContain(texts, t => t.Contains("Almanca"));     // sıkıştırma özeti
        Assert.DoesNotContain(texts, t => t.Contains("İspanyolca"));  // başka oturumdan gelen mesaj
        Assert.DoesNotContain(texts, t => t.Contains("Rusça"));       // sdk-cli otomasyon girdisi
        Assert.DoesNotContain(texts, t => t.Contains("İngilizce yaz")); // alt ajan dosyası
        Assert.DoesNotContain(texts, t => t.StartsWith("/"));          // eğik çizgi komutu
        Assert.DoesNotContain(texts, t => t.Contains("Request interrupted") || t.Contains("Caveat") || t.Contains("task-notification") || t.Contains("local-command"));
    }

    [Fact]
    public void Claude_SessionKinds_Titles_And_State()
    {
        using var env = new TestEnv();
        new ClaudeCodeReader().Read(new IngestStore(env.Svc.Db), env.ClaudeRoot, new ReadOptions());
        using var c = env.Svc.Db.Open();
        var rows = c.Query("SELECT external_id, kind, title, parent_external_id, last_event_kind, turn_ended, last_assistant_text FROM sessions WHERE tool='claude'",
            r => (id: r.GetString(0), kind: r.GetString(1), title: r.Str(2), parent: r.Str(3), lek: r.Str(4), te: r.Long(5), last: r.Str(6)))
            .ToDictionary(x => x.id);
        Assert.Equal("human", rows["aaaaaaaa-0000-4000-8000-000000000001"].kind);
        Assert.Equal("Not uygulaması", rows["aaaaaaaa-0000-4000-8000-000000000001"].title); // custom-title ai-title'ı yener
        Assert.Equal("assistant_text", rows["aaaaaaaa-0000-4000-8000-000000000001"].lek);
        Assert.Equal(1, rows["aaaaaaaa-0000-4000-8000-000000000001"].te);
        Assert.StartsWith("Bitti. Testler geçti", rows["aaaaaaaa-0000-4000-8000-000000000001"].last);
        Assert.Equal("subagent", rows["agent-a0000000000000001"].kind);
        Assert.Equal("aaaaaaaa-0000-4000-8000-000000000001", rows["agent-a0000000000000001"].parent);
        Assert.Equal("tool", rows["bbbbbbbb-0000-4000-8000-000000000003"].lek);
        Assert.Equal(0, rows["bbbbbbbb-0000-4000-8000-000000000003"].te);
        // Proje = oturumun çalışma dizini
        var projects = c.Query("SELECT key FROM projects", r => r.GetString(0));
        Assert.Contains(@"e:\ornek-proje", projects);
        Assert.Contains(@"e:\ikinci-proje", projects);
    }

    [Fact]
    public void Claude_Incremental_And_PartialLine()
    {
        using var env = new TestEnv();
        var store = new IngestStore(env.Svc.Db);
        var reader = new ClaudeCodeReader();
        reader.Read(store, env.ClaudeRoot, new ReadOptions());
        var before = Utterances(env).Count;
        var file = Path.Combine(env.ClaudeRoot, "E--ikinci-proje", "bbbbbbbb-0000-4000-8000-000000000003.jsonl");

        // Yarım satır: işlenmez, ofset ilerlemez.
        const string line = """{"parentUuid":"c1","isSidechain":false,"type":"user","message":{"role":"user","content":"Bundan sonra her zaman kısa cevap ver."},"uuid":"w3","timestamp":"2026-11-10T10:05:00.000Z","entrypoint":"cli","cwd":"E:\\ikinci-proje","sessionId":"bbbbbbbb-0000-4000-8000-000000000003","origin":{"kind":"human"},"promptSource":"typed"}""";
        File.AppendAllText(file, line[..60]);
        var r1 = reader.Read(store, env.ClaudeRoot, new ReadOptions());
        Assert.Equal(0, r1.NewUtterances);
        Assert.Equal(0, r1.BadLines);

        // Satır tamamlanınca yalnız yeni mesaj okunur.
        File.AppendAllText(file, line[60..] + "\n");
        var r2 = reader.Read(store, env.ClaudeRoot, new ReadOptions());
        Assert.Equal(1, r2.NewUtterances);
        Assert.Equal(before + 1, Utterances(env).Count);
        Assert.Equal(2, r2.FilesSkippedUnchanged); // değişmeyen iki ana dosya hiç açılmadı

        // Tekrar okuma hiçbir şey eklemez.
        var r3 = reader.Read(store, env.ClaudeRoot, new ReadOptions());
        Assert.Equal(0, r3.NewUtterances);
        using var c = env.Svc.Db.Open();
        var lineNo = c.ScalarLong("SELECT line_no FROM file_state WHERE path=@p", null, ("@p", file));
        Assert.Equal(4, lineNo);
    }

    [Fact]
    public void Codex_Human_Automation_Subagent_Separated()
    {
        using var env = new TestEnv();
        File.WriteAllText(Path.Combine(env.CodexRoot, "2026", "11", "09", "rollout-2026-11-09T13-00-00-bilinmeyen.jsonl"), "{\"type\":\"baska_bicim\"}\n");
        var rep = new CodexReader().Read(new IngestStore(env.Svc.Db), env.CodexRoot, new ReadOptions());
        Assert.Equal(4, rep.Files);
        Assert.Equal(1, rep.Unrecognized); // "okuyamadım" görünür
        Assert.Equal(2, rep.NewCompactions);

        var u = Utterances(env).Where(x => x.tool == "codex").ToList();
        Assert.Equal(2, u.Count); // yalnız insan oturumu; exec brifi ve alt ajan sayılmaz
        Assert.Contains(u, x => x.text == "KarincaAI.exe dosyasını asla çalıştırma."); // in-app-browser-context atıldı
        Assert.DoesNotContain(u, x => x.text.Contains("Japonca"));

        using var c = env.Svc.Db.Open();
        var kinds = c.Query("SELECT external_id, kind, parent_external_id, turn_ended, last_assistant_text FROM sessions WHERE tool='codex'",
            r => (id: r.GetString(0), kind: r.GetString(1), parent: r.Str(2), te: r.Long(3), last: r.Str(4))).ToDictionary(x => x.id);
        Assert.Equal("human", kinds["cccccccc-0000-7000-8000-000000000001"].kind);
        Assert.Equal("automation", kinds["cccccccc-0000-7000-8000-000000000002"].kind);
        Assert.Equal("subagent", kinds["cccccccc-0000-7000-8000-000000000003"].kind);
        Assert.Equal("cccccccc-0000-7000-8000-000000000001", kinds["cccccccc-0000-7000-8000-000000000003"].parent);
        Assert.Equal(1, kinds["cccccccc-0000-7000-8000-000000000001"].te);
        Assert.Equal("Anlaşıldı.", kinds["cccccccc-0000-7000-8000-000000000001"].last);
    }

    [Fact]
    public void Codex_Incremental_Append()
    {
        using var env = new TestEnv();
        var store = new IngestStore(env.Svc.Db);
        new CodexReader().Read(store, env.CodexRoot, new ReadOptions());
        var file = Directory.GetFiles(env.CodexRoot, "*000000000001.jsonl", SearchOption.AllDirectories).Single();
        File.AppendAllText(file, """{"timestamp":"2026-11-09T11:10:00.000Z","type":"event_msg","payload":{"type":"item_completed","thread_id":"x","turn_id":"t3","item":{"type":"UserMessage","id":"m3","content":[{"type":"text","text":"Tercihim kısa ve sade cevaplar.","text_elements":[]}]}}}""" + "\n");
        var rep = new CodexReader().Read(store, env.CodexRoot, new ReadOptions());
        Assert.Equal(1, rep.NewUtterances);
        Assert.Equal(2, rep.FilesSkippedUnchanged);
        using var c = env.Svc.Db.Open();
        Assert.Equal("user", c.Scalar("SELECT last_event_kind FROM sessions WHERE external_id='cccccccc-0000-7000-8000-000000000001'"));
        Assert.Equal(0L, c.ScalarLong("SELECT turn_ended FROM sessions WHERE external_id='cccccccc-0000-7000-8000-000000000001'"));
    }

    [Fact]
    public void Hook_And_Transcript_Same_Message_Is_Stored_Once_As_Both()
    {
        using var env = new TestEnv();
        using (var c = env.Svc.Db.Open())
        {
            var (sid, pid) = IngestStore.EnsureSession(c, null, "claude", "bbbbbbbb-0000-4000-8000-000000000003", @"E:\ikinci-proje", null, "human");
            // Kanca, dosyadaki kayıttan 20 sn önce yazmış olsun.
            Assert.NotNull(IngestStore.InsertUtterance(c, null, sid, pid, "2026-11-10T09:59:40.000Z", "Bu projede de her zaman Türkçe yaz.", "hook", 0));
        }
        new ClaudeCodeReader().Read(new IngestStore(env.Svc.Db), env.ClaudeRoot, new ReadOptions());
        var rows = Utterances(env).Where(u => u.text == "Bu projede de her zaman Türkçe yaz.").ToList();
        Assert.Single(rows);
        Assert.Equal("both", rows[0].source);
    }

    [Fact]
    public void TextNormalizer_ForHash_Is_Shared_And_Stable()
    {
        var a = Soz.Core.Text.TextNormalizer.Hash("<system-reminder>x</system-reminder>\r\n  Merhaba   dünya  ");
        var b = Soz.Core.Text.TextNormalizer.Hash("Merhaba dünya");
        Assert.Equal(a, b);
        Assert.True(Soz.Core.Text.TextNormalizer.IsSlashCommand("/compact"));
        Assert.True(Soz.Core.Text.TextNormalizer.IsSlashCommand("/hafta notu ekle"));
        Assert.False(Soz.Core.Text.TextNormalizer.IsSlashCommand("/c/Users/x yolunda dosya var"));
    }
}
