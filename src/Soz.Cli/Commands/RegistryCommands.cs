using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Registry;

namespace Soz.Cli.Commands;

/// <summary>
/// `soz registry check` ve `soz registry list`: tek kayıt defterini (gömülü sabit kayıtlar + isteğe bağlı kişi klasörü) denetler ve listeler. Yalnız okur.
/// Kişi klasörü `--dir` ile verilir; verilmezse yalnız pakete gömülü kayıtlar bakılır (veri klasörüne hiç dokunulmaz).
/// `--dir` verilip klasör yoksa, dosyaysa, ağ/aygıt yoluysa ya da bağlantıysa açık hata ve çıkış 1 (yanlış yazılan klasör sessizce "tamam" demez).
/// Çıkış kodu: 0 başarı, 1 kural ihlali ya da geçersiz --dir, 2 kullanım hatası.
/// </summary>
public static class RegistryCommands
{
    private const string UsageText = "Kullanım: soz registry check [--dir <kişi kayıt klasörü>] | soz registry list [--kind <tür>] [--dir <klasör>] [--json]";

    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(Args a, TextWriter output, TextWriter error, IReadOnlyList<RegistrySource>? fixedSources = null)
    {
        var sub = a.Positional.Count > 0 ? a.Positional[0] : null;
        if (sub is not ("check" or "list")) { error.WriteLine(UsageText); return 2; }

        // --dir açıkça verildiyse önce yolu denetle: olmayan klasör "geçti" sayılmaz.
        if (a.Has("dir") && RegistryLoader.CheckDir(a.Opt("dir"), out _) is { } why)
        {
            error.WriteLine("HATA   --dir kullanılamıyor: " + why);
            error.WriteLine("Sonuç: KALDI. Kayıt klasörü okunamadığı için hiçbir kişi kaydı denetlenmedi.");
            return 1;
        }
        return sub == "check" ? Check(a, output, fixedSources) : List(a, output, error, fixedSources);
    }

    /// <summary>fixedSources yalnız testler içindir (bozuk bir sabit paketi sınamak için); verilmezse gömülü kayıtlar.</summary>
    private static RegistryLoadResult Load(Args a, IReadOnlyList<RegistrySource>? fixedSources)
    {
        if (fixedSources is null) return RegistryLoader.LoadEmbedded(a.Opt("dir"));
        var read = RegistryLoader.ReadUserDir(a.Opt("dir"));
        var res = RegistryLoader.Load(fixedSources, read.Sources);
        res.Rejected.AddRange(read.Problems);
        res.Warnings.AddRange(read.Warnings);
        return res;
    }

    private static int Check(Args a, TextWriter output, IReadOnlyList<RegistrySource>? fixedSources)
    {
        var report = RegistryChecker.Check(Load(a, fixedSources));
        output.WriteLine(report.Format());
        return report.Ok ? 0 : 1;
    }

    private static int List(Args a, TextWriter output, TextWriter error, IReadOnlyList<RegistrySource>? fixedSources)
    {
        var kind = a.Opt("kind");
        if (kind is not null && !RegistryKinds.IsKnown(kind))
        {
            error.WriteLine($"Bilinmeyen tür: {SchemaCheck.Safe(kind)}. Bilinen türler: {string.Join(", ", RegistryKinds.All)}.");
            return 2;
        }
        var load = Load(a, fixedSources);
        var entries = load.Entries.Where(e => kind is null || e.Kind == kind).OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();

        if (a.Has("json"))
        {
            var arr = new JsonArray();
            foreach (var e in entries)
                arr.Add(new JsonObject { ["id"] = e.Id, ["kind"] = e.Kind, ["owner"] = e.Owner, ["state"] = e.State, ["title"] = e.Title.Tr, ["stability"] = e.Stability, ["origin"] = e.Origin, ["sha256"] = e.Sha256 });
            output.WriteLine(arr.ToJsonString(JsonOut));
            return 0;
        }

        var sb = new StringBuilder();
        var w = Math.Max(2, entries.Select(e => e.Id.Length).DefaultIfEmpty(2).Max());
        foreach (var e in entries)
            sb.AppendLine($"{e.Id.PadRight(w)}  {e.Owner,-6}  {e.State,-9}  {e.Title.Tr}");
        sb.Append($"{entries.Count} kayıt" + (kind is null ? "" : $" ({kind})") + ".");
        if (load.Rejected.Count > 0) sb.Append($" {load.Rejected.Count} kayıt reddedildi (ayrıntı için: soz registry check).");
        output.WriteLine(sb.ToString());
        return 0;
    }
}
