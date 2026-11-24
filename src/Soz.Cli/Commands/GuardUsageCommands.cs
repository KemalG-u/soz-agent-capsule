using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core;
using Soz.Core.Guard;
using Soz.Core.Usage;

namespace Soz.Cli.Commands;

/// <summary>soz guard list|apply|test|events ve soz usage.</summary>
public static class GuardUsageCommands
{
    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string HelpText = """
          guard list [--project <p>] [--json] | guard templates | guard apply --project <p> --template <ad> | guard test --project <p> [--tool Bash] "<komut>" | guard events [--days 7] [--json]
          usage [--days 30] [--tool claude|codex] [--refresh] [--json]
        """;

    private static long? Project(SozService svc, Args a, bool required)
    {
        var spec = a.Opt("project");
        if (spec is null) { if (required) throw new ArgumentException("--project gerekli (ad, yol, id ya da *)."); return null; }
        return svc.ResolveProject(spec) ?? throw new SozException(404, $"Proje bulunamadı: {spec}");
    }

    public static int Guard(Args a, SozService svc)
    {
        var sub = a.Positional.FirstOrDefault();
        using var c = svc.Db.Open();
        GuardStore.EnsureTables(c);
        switch (sub)
        {
            case "list":
            {
                var rules = GuardStore.List(c, Project(svc, a, false));
                if (a.Has("json")) { Console.WriteLine(JsonSerializer.Serialize(rules.Select(r => GuardStore.Dto(r)), JsonOut)); return 0; }
                if (rules.Count == 0) { Console.WriteLine("Bekçi kuralı yok. Başlamak için: soz guard apply --project <p> --template temkinli"); return 0; }
                foreach (var r in rules)
                    Console.WriteLine($"#{r.Id,-4} [{(r.Enabled ? "açık" : "kapalı")}] {r.Kind,-13} {r.Action,-4} proje {(r.ProjectId?.ToString() ?? "tümü"),-5} {r.Label}\n        {GuardText.Describe(r)}");
                return 0;
            }
            case "templates":
                foreach (var t in GuardTemplates.All) Console.WriteLine($"{t.Id,-18} {t.Label}: {t.Description}");
                return 0;
            case "apply":
            {
                var pid = Project(svc, a, true)!.Value;
                var tpl = a.Opt("template") ?? throw new ArgumentException("--template gerekli (soz guard templates ile listele).");
                var res = GuardStore.ApplyTemplate(c, pid, tpl);
                if (a.Has("json")) { Console.WriteLine(JsonSerializer.Serialize(new { template = res.Template, created = res.Created.Select(r => GuardStore.Dto(r)), existing = res.Existing.Select(r => GuardStore.Dto(r)) }, JsonOut)); return 0; }
                Console.WriteLine($"«{res.Template}» uygulandı: {res.Created.Count} yeni kural, {res.Existing.Count} zaten vardı.");
                foreach (var r in res.Created) Console.WriteLine($"  + #{r.Id} {r.Label}");
                return 0;
            }
            case "test":
            {
                var cmd = string.Join(' ', a.Positional.Skip(1));
                if (string.IsNullOrWhiteSpace(cmd)) throw new ArgumentException("Kullanım: soz guard test --project <p> [--tool Bash] \"<komut ya da dosya yolu>\"");
                var tool = a.Opt("tool") ?? "Bash";
                var r = GuardService.Test(svc.Db, Project(svc, a, false), tool, JsonValue.Create(cmd), a.Opt("cwd"));
                if (a.Has("json")) { Console.WriteLine(JsonSerializer.Serialize(r, JsonOut)); return 0; }
                Console.WriteLine(r.Verdict switch { "deny" => "KARAR: engellenir", "ask" => "KARAR: sorulur", _ => "KARAR: izin verilir" });
                if (r.Reason is not null) Console.WriteLine(r.Reason);
                if (r.Note.Length > 0) Console.WriteLine(r.Note);
                if (r.Targets.Count > 0) Console.WriteLine("Çıkarılan hedefler: " + string.Join(" | ", r.Targets));
                return 0;
            }
            case "events":
            {
                var ev = GuardStore.Events(c, a.Int("days") ?? 7, Project(svc, a, false), a.Int("limit") ?? 100);
                if (a.Has("json")) { Console.WriteLine(JsonSerializer.Serialize(ev, JsonOut)); return 0; }
                if (ev.Count == 0) { Console.WriteLine("Bu sürede Bekçi olayı yok."); return 0; }
                foreach (var e in ev)
                    Console.WriteLine($"{Local(e.At)} · {e.Action,-4} · {e.ProjectName} · {e.Tool} · {e.RuleLabel} · {Cut(e.Target ?? "", 90)}");
                return 0;
            }
            default:
                Console.Error.WriteLine("Kullanım:\n" + HelpText);
                return 2;
        }
    }

    public static int UsageCmd(Args a, SozService svc)
    {
        if (a.Has("refresh"))
        {
            var s = svc.Settings();
            var rep = UsageIngest.Run(svc.Db, SourceLocator.Claude(a.Opt("claude"), s.ClaudeRoot).Root, SourceLocator.Codex(a.Opt("codex"), s.CodexRoot).Root, null);
            (a.Has("json") ? Console.Error : Console.Out).WriteLine($"Harcama içe alındı: {rep.Files} dosya ({rep.FilesRead} okundu, {rep.FilesSkippedUnchanged} değişmemiş), {rep.NewMessages} yeni mesaj, {rep.DuplicateLines} yinelenen satır, {rep.BytesRead / 1e6:0.0} MB, {rep.ElapsedMs / 1000.0:0.0} sn.");
        }
        var days = a.Int("days") ?? 30;
        var json = JsonSerializer.SerializeToNode(UsageQuery.Build(svc.Db, days, a.Opt("tool")), JsonOut)!;
        if (a.Has("json")) { Console.WriteLine(json.ToJsonString(JsonOut)); return 0; }
        var t = json["totals"]!;
        Console.WriteLine($"Harcama · son {days} gün ({json["since"]} … {json["until"]}, gün = UTC)");
        Console.WriteLine($"  mesaj {N(t["messages"])} · giriş {N(t["inputTokens"])} · çıkış {N(t["outputTokens"])} · önbellek okuma {N(t["cacheReadTokens"])} · önbellek yazma {N(t["cacheWriteTokens"])}");
        var sub = json["subagent"]!;
        Console.WriteLine($"  alt ajan payı: %{sub["sharePercent"]} (yeni işlenen token), %{sub["sharePercentOfAllTokens"]} (tüm token), %{sub["sharePercentOfMessages"]} (mesaj)");
        foreach (var m in json["byModel"]!.AsArray())
            Console.WriteLine($"  {m!["tool"],-7} {m["model"],-28} %{m["sharePercent"],5} · alt ajan %{m["subagentSharePercent"]} · çıkış {N(m["tokens"]!["outputTokens"])}{(m["costUsd"] is { } cu ? $" · ≈${cu}" : "")}");
        foreach (var nt in json["notTracked"]!.AsArray()) Console.WriteLine($"  yok: {nt!["reason"]}");
        var p = json["pricing"]!;
        Console.WriteLine(json["estimatedCostUsd"] is { } est ? $"  API eşdeğeri tahmini tutar: ${est}. {p["disclaimer"]}" : $"  Tutar: {p["note"]} (Ayarlar'da prices girilince API eşdeğeri tahmin gösterilir, abonelikte gerçek fatura değildir).");
        return 0;
    }

    private static string N(JsonNode? n) => long.TryParse(n?.ToString(), out var v) ? v.ToString("N0", CultureInfo.GetCultureInfo("tr-TR")) : n?.ToString() ?? "0";
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    private static string Local(string? iso)
    {
        var t = Soz.Core.Data.Time.Parse(iso);
        return t is null ? "-" : t.Value.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
    }
}
