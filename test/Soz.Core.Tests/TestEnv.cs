using Soz.Core;
using Soz.Core.Data;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Soz.Core.Tests;

/// <summary>Her test için temiz bir çalışma klasörü: urun\_gecici\testler\&lt;guid&gt; (örnek veri kopyalanır, test sonunda silinir).</summary>
public sealed class TestEnv : IDisposable
{
    public string Root { get; }
    public string DataDir => Path.Combine(Root, "veri");
    public string ClaudeRoot => Path.Combine(Root, "claude", "projects");
    public string CodexRoot => Path.Combine(Root, "codex", "sessions");
    public SozService Svc { get; }

    public static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Soz.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("Soz.sln bulunamadı");
        }
    }

    public static string SampleDir => Path.Combine(RepoRoot, "test", "ornek-veri");

    public TestEnv(bool copySamples = true)
    {
        Root = Path.Combine(RepoRoot, "_gecici", "testler", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        if (copySamples) CopyDir(SampleDir, Root);
        Directory.CreateDirectory(ClaudeRoot);
        Directory.CreateDirectory(CodexRoot);
        Time.UtcNow = () => new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        Svc = SozService.Open(DataDir);
    }

    public ImportSummary Import() => Svc.Import(new ImportOptions { ClaudeRoot = ClaudeRoot, CodexRoot = CodexRoot });

    public static void CopyDir(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
            if (file.EndsWith(".gitkeep")) continue;
            // Örnek oturum kayıtları depoda `.jsonl.sample` adıyla durur (ham kayıt taramalarına takılmasın); kopyada gerçek adlarına döner.
            var hedef = file.Replace(from, to);
            if (hedef.EndsWith(".jsonl.sample")) hedef = hedef[..^".sample".Length];
            File.Copy(file, hedef, overwrite: true);
        }
    }

    public void Dispose()
    {
        Time.UtcNow = () => DateTime.UtcNow;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var i = 0; i < 5; i++)
        {
            try { Directory.Delete(Root, recursive: true); return; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
