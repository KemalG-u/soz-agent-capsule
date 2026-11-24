using System.Text.Json;
using Soz.Core;
using Soz.Core.Restore;

namespace Soz.Cli.Commands;

/// <summary>
/// <c>soz undo list &lt;iş&gt;</c>: bir işin geri alma durumunu SALT OKUNUR gösterir (koruma düzeyi, kartta yazan cümle, geri alınabilir mi, daha önce yapılmış geri almalar).
/// Hiçbir dosyaya dokunmaz, hiçbir şey kaydetmez, olay yazmaz. Önizleme ve uygulama (olay yazarlar, dosya değiştirirler) bu komutta YOKTUR: yalnız insan arayüzündendir,
/// ajanlar çalıştırabildiği komut listesine yalnız bu okuma alt komutu girer (bkz. CliGuard).
/// </summary>
public static class UndoCommands
{
    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(Args a)
    {
        const string usage = "Kullanım: soz undo list <iş-no> [--json]   (yalnız okur, geri alma işlemi arayüzden yapılır)";
        var sub = a.Positional.FirstOrDefault()?.ToLowerInvariant();
        if (sub != "list") throw new ArgumentException(usage);
        if (a.Positional.Count < 2 || !long.TryParse(a.Positional[1], out var jobId)) throw new ArgumentException(usage);
        var svc = SozService.Open(SourceLocator.ResolveDataDir(a.Opt("data")));
        using var restore = new RestoreService(svc.Db);
        var protection = restore.ProtectionOf(jobId);
        var history = restore.History(jobId);
        if (a.Has("json")) { Console.WriteLine(JsonSerializer.Serialize(new { protection, undo = history }, JsonOut)); return 0; }
        Console.WriteLine($"İş #{jobId} · koruma: {protection.Level ?? "henüz yok"}");
        Console.WriteLine("  " + protection.Text);
        Console.WriteLine(protection.CanUndo ? "  Geri alınabilir (arayüzde işin kartından)." : "  Geri alınamaz: " + (protection.CannotUndoWhy ?? "geri alma noktası yok."));
        foreach (var r in protection.Runs)
            Console.WriteLine($"  tur #{r.RunId}: {r.Level ?? "yok"}{(string.IsNullOrWhiteSpace(r.Note) ? "" : " · " + r.Note)}");
        if (history.Count == 0) Console.WriteLine("  Bu işte yapılmış geri alma yok.");
        foreach (var u in history) Console.WriteLine($"  geri alma {u.Uid} · {u.Status} · {u.Mode}");
        Console.WriteLine("  " + protection.Limits);
        return 0;
    }
}
