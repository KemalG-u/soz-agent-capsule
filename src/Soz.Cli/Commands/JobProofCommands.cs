using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core;
using Soz.Core.Jobs;

namespace Soz.Cli.Commands;

/// <summary>İş kartı, ölçüt, kanıt, kapı, kuyruk, sabah raporu ve Bugün komutları (A).</summary>
public static class JobProofCommands
{
    public static HashSet<string> BoolFlags => new() { "json", "night", "restate", "enable", "pending", "wait" };

    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void PrintJson(object o) => Console.WriteLine(JsonSerializer.Serialize(o, JsonOut));
    private static string DataDir(Args a) => SourceLocator.ResolveDataDir(a.Opt("data"));
    private static WorkService Work(Args a) => WorkService.Open(DataDir(a));
    private static SozService Svc(Args a) => SozService.Open(DataDir(a));

    private static string Sub(Args a, string usage)
        => a.Positional.Count > 0 ? a.Positional[0] : throw new ArgumentException(usage);

    private static long IdArg(Args a, int index, string usage)
        => a.Positional.Count > index && long.TryParse(a.Positional[index], out var id) ? id : throw new ArgumentException(usage);

    /// <summary>--project: id, ad, yol ya da var olan klasör (kayıtlı değilse kaydedilir).</summary>
    private static long Project(Args a, bool required = true)
    {
        var spec = a.Opt("project");
        if (spec is null) { if (required) throw new ArgumentException("--project gerekli (id, ad ya da klasör yolu)."); return 0; }
        var pid = Svc(a).ResolveProject(spec);
        if (pid is not null)
        {
            // Klasör verilip üst klasördeki projeye çıkıldıysa bile tam klasör tercih edilir.
            if (Directory.Exists(spec) && !long.TryParse(spec, out _)) return Work(a).EnsureProjectByPath(spec);
            return pid.Value;
        }
        if (Directory.Exists(spec)) return Work(a).EnsureProjectByPath(spec);
        throw new SozException(404, $"Proje bulunamadı: {spec}");
    }

    private static long? OptLong(Args a, string n) => a.Opt(n) is { } s ? (long.TryParse(s, out var v) ? v : throw new ArgumentException($"--{n} sayı olmalı.")) : null;

    public static int Run(string cmd, Args a) => cmd switch
    {
        "job" => Job(a),
        "checks" => Checks(a),
        "proof" => Proof(a),
        "gate" => Gate(a),
        "project" => ProjectCmd(a),
        "queue" => Queue(a),
        "morning" => Morning(a),
        "now" => Now(a),
        _ => throw new ArgumentException("Bilinmeyen komut: " + cmd),
    };

    // ---------------- job ----------------
    private static int Job(Args a)
    {
        const string usage = "Kullanım: soz job new|list|show|give|cancel|start|reply|continue|accept|stop …";
        var sub = Sub(a, usage);
        var w = Work(a);
        switch (sub)
        {
            case "new":
            {
                var text = string.Join(' ', a.Positional.Skip(1));
                if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("İş metni gerekli: soz job new --project <p> \"…\"");
                var checkIds = (a.Opt("check") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => long.TryParse(x, out var v) ? v : throw new ArgumentException("--check virgülle ayrılmış ölçüt numaraları olmalı.")).ToList();
                var j = w.CreateJob(Project(a), text, a.Opt("source") ?? "yazi", a.Has("restate"), a.Has("night"), checkIds, a.Opt("title"), a.Opt("agent"));
                if (a.Has("json")) { PrintJson(j); return 0; }
                Console.WriteLine($"İş #{j.Id} oluşturuldu [{j.State}] · {j.ProjectName} · {j.Checks.Count} ölçüt · ajan {j.Agent}");
                Console.WriteLine("---- terminale verilecek istem ----");
                Console.WriteLine(j.PromptText);
                return 0;
            }
            case "list":
            {
                var pid = a.Opt("project") is null ? (long?)null : Project(a);
                var list = w.Jobs(pid, a.Opt("state"), a.Int("limit") ?? 50);
                if (a.Has("json")) { PrintJson(list); return 0; }
                if (list.Count == 0) { Console.WriteLine("İş yok."); return 0; }
                foreach (var j in list)
                    Console.WriteLine($"#{j.Id,-4} [{j.State,-14}] {(j.Night ? "gece " : "")}{j.ProjectName} · {j.Title} · tur {j.Attempts} · son hüküm {j.LastVerdict ?? "-"}");
                return 0;
            }
            case "show":
            {
                var j = w.Job(IdArg(a, 1, "Kullanım: soz job show <id>"));
                if (a.Has("json")) { PrintJson(j); return 0; }
                Console.WriteLine($"İş #{j.Id} [{j.State}] · {j.ProjectName} · {j.Title} · ajan {j.Agent}");
                Console.WriteLine($"  oluşturma {j.CreatedAt} · verilme {j.GivenAt ?? "-"} · bitiş {j.FinishedAt ?? "-"} · oturum {(j.SessionId?.ToString() ?? "-")}");
                Console.WriteLine($"  ölçütler: {(j.Checks.Count == 0 ? "yok" : string.Join(", ", j.Checks.Select(c => $"#{c.Id} {c.Label}")))}");
                foreach (var r in j.Runs ?? new()) Console.WriteLine($"  tur {r.Attempt} · çalışma #{r.Id} · {r.Trigger} · {r.Verdict ?? "sürüyor"} · {r.Summary}");
                return 0;
            }
            case "give":
            {
                var j = w.PatchJob(IdArg(a, 1, "Kullanım: soz job give <id>"), JobStates.Given);
                Console.WriteLine($"İş #{j.Id} [{j.State}]");
                return 0;
            }
            case "start":
            {
                var id = IdArg(a, 1, "Kullanım: soz job start <n> [--agent claude|codex] [--model m]");
                return RunAgent(a, w, id, rm => rm.Start(id, a.Opt("model"), a.Opt("agent")));
            }
            case "reply":
            {
                var id = IdArg(a, 1, "Kullanım: soz job reply <n> \"<metin>\"");
                var text = string.Join(' ', a.Positional.Skip(2));
                if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Cevap metni gerekli: soz job reply <n> \"<metin>\"");
                return RunAgent(a, w, id, rm => rm.Reply(id, text, a.Opt("model")));
            }
            case "continue":
            {
                var id = IdArg(a, 1, "Kullanım: soz job continue <n> [\"<metin>\"]");
                var text = string.Join(' ', a.Positional.Skip(2));
                return RunAgent(a, w, id, rm => rm.Continue(id, string.IsNullOrWhiteSpace(text) ? null : text, a.Opt("model")));
            }
            case "accept":
            {
                var id = IdArg(a, 1, "Kullanım: soz job accept <n> [--no]");
                // Kabul, gösterileni onaylar: önce kartın metni ve farkı yazılır, kabul aynı görüntü kimliğiyle yapılır.
                var card = w.Runs(null, id, 1).FirstOrDefault();
                if (card is not null && !a.Has("no"))
                {
                    Console.WriteLine("Onayladığın kart: " + (card.ReasonText ?? card.Summary));
                    foreach (var d in card.Diffs ?? new())
                    {
                        Console.WriteLine($"  {d.Path}: {d.Sentence}");
                        foreach (var l in d.Lines) Console.WriteLine($"    {l.Op} {l.Text}");
                    }
                }
                var who = Soz.Core.Security.CallerIdentity.ClassifySelf(w.Db.DataDir);
                var j = w.Accept(id, !a.Has("no"), who, card?.SnapshotId);
                Console.WriteLine($"İş #{j.Id} [{j.State}]{(j.AcceptedByUser ? " (kullanıcı kabulü)" : "")}");
                return 0;
            }
            case "stop":
            {
                var id = IdArg(a, 1, "Kullanım: soz job stop <n>");
                using var rm = new Soz.Core.Run.RunManager(w.Db);
                Console.WriteLine(rm.Stop(id) ? $"İş #{id} durduruldu." : $"İş #{id} için çalışan bir ajan yok.");
                return 0;
            }
            case "cancel":
            {
                var j = w.PatchJob(IdArg(a, 1, "Kullanım: soz job cancel <id>"), JobStates.Cancelled);
                Console.WriteLine($"İş #{j.Id} [{j.State}]");
                return 0;
            }
            default: throw new ArgumentException(usage);
        }
    }

    // ---------------- Söz ajanı (start/reply/stop) ----------------
    private static int RunAgent(Args a, WorkService w, long jobId, Func<Soz.Core.Run.RunManager, Soz.Core.Run.RunDto?> launch)
    {
        using var rm = new Soz.Core.Run.RunManager(w.Db);
        rm.LineEmitted += (_, _, l) => Console.WriteLine((l.Kind == "agent" ? "  ajan: " : "  ") + l.Text);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; rm.Stop(jobId); };
        Console.CancelKeyPress += onCancel;
        try
        {
            var run = launch(rm);
            if (run is null)
            {
                Console.WriteLine($"İş #{jobId} sırada: aynı anda çalışan iş sınırı dolu. Biri bitince ya da Söz açıkken kendiliğinden başlar.");
                return 0;
            }
            if (run.Status == Soz.Core.Run.RunStatus.Failed) { Console.Error.WriteLine(run.FailReason ?? "Başlatılamadı."); return 1; }
            Console.WriteLine($"İş #{jobId}: ajan arka planda çalışıyor (süreç {run.Pid}). Durdurmak için Ctrl+C.");
            rm.WaitAsync(run.Id).GetAwaiter().GetResult();
            var j = w.Job(jobId);
            var r = j.Run;
            Console.WriteLine($"Bitti: süreç {r?.Status ?? "?"} · iş durumu {j.State} · son hüküm {j.LastVerdict ?? "-"}{(j.AgentState is { } s ? " · ajan: " + s : "")}");
            if (r?.FailReason is { } why) Console.WriteLine("  " + why);
            if (j.StartError is { } se) Console.WriteLine("  Başlatılamadı: " + se);
            if (j.Question is { } qn) Console.WriteLine("  Ajan sana soruyor: " + qn);
            if (r?.PendingApproval is { } pa) Console.WriteLine($"  Senden onay bekliyor: {pa.Summary} (arayüzden Evet/Hayır de)");
            return r?.Status switch { "done" => j.State == JobStates.Failed ? 3 : 0, "waiting_approval" => 4, "stopped" => 5, _ => 1 };
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static int ProjectCmd(Args a)
    {
        const string usage = "Kullanım: soz project add <klasör yolu> | soz project list | soz project relocate <proje-no> <yeni klasör yolu>";
        var sub = Sub(a, usage);
        switch (sub)
        {
            case "add":
            {
                var path = string.Join(' ', a.Positional.Skip(1));
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(usage);
                var r = Work(a).AddProject(path);
                if (a.Has("json")) { PrintJson(r); return 0; }
                Console.WriteLine($"{(r.Created ? "Proje eklendi" : "Proje zaten kayıtlıydı")}: #{r.Id} {r.Name} · {r.Path}");
                Console.WriteLine(r.Card);
                PrintChecks(r.Suggested);
                if (r.Suggested.Count > 0) Console.WriteLine($"Onaylıyorsan: soz checks enable <id> ve soz gate on --project {r.Id} (ya da arayüzde tek onay).");
                return 0;
            }
            case "relocate":
            {
                if (a.Positional.Count < 3 || !long.TryParse(a.Positional[1], out var pid)) throw new ArgumentException(usage);
                var r = Work(a).RelocateProject(pid, string.Join(' ', a.Positional.Skip(2)));
                if (a.Has("json")) { PrintJson(r); return 0; }
                Console.WriteLine(r.Changed ? $"Proje #{r.Id} ({r.Name}) yeni klasöre bağlandı: {r.Path} ({r.SnapshotsMoved} geri alma noktası taşındı)" : $"Proje #{r.Id} zaten bu klasöre bağlı: {r.Path}");
                return 0;
            }
            case "list":
            {
                var list = Svc(a).Projects();
                if (a.Has("json")) { PrintJson(list); return 0; }
                foreach (var p in list) Console.WriteLine($"#{p.Id,-4} {p.Name} · {p.Path ?? "-"}");
                return 0;
            }
            default: throw new ArgumentException(usage);
        }
    }

    // ---------------- checks ----------------
    private static int Checks(Args a)
    {
        const string usage = "Kullanım: soz checks suggest|list|enable|disable|add …";
        var sub = Sub(a, usage);
        var w = Work(a);
        switch (sub)
        {
            case "suggest":
            {
                var r = w.SuggestChecks(Project(a));
                if (a.Has("json")) { PrintJson(r); return 0; }
                Console.WriteLine($"{r.Added} yeni öneri eklendi (hepsi KAPALI, açmadan hiçbir komut çalışmaz).");
                PrintChecks(r.Checks);
                return 0;
            }
            case "list":
            {
                var list = w.Checks(Project(a), OptLong(a, "job"));
                if (a.Has("json")) { PrintJson(list); return 0; }
                PrintChecks(list);
                return 0;
            }
            case "enable" or "disable":
            {
                var c = w.PatchCheck(IdArg(a, 1, $"Kullanım: soz checks {sub} <id>"), enabled: sub == "enable");
                Console.WriteLine($"Ölçüt #{c.Id} {(c.Enabled ? "AÇIK" : "kapalı")}: {c.Label}");
                return 0;
            }
            case "add":
            {
                var kind = a.Opt("kind") ?? "command";
                var spec = SpecFromRegistry(a, kind);
                var label = a.Opt("label") ?? throw new ArgumentException("--label gerekli.");
                var c = w.AddCheck(Project(a), OptLong(a, "job"), kind, label, spec, a.Int("timeout"), a.Has("enable"));
                if (a.Has("json")) { PrintJson(c); return 0; }
                Console.WriteLine($"Ölçüt #{c.Id} eklendi ({(c.Enabled ? "AÇIK" : "kapalı; açmak için: soz checks enable " + c.Id)}): {c.Label}");
                return 0;
            }
            default: throw new ArgumentException(usage);
        }
    }

    /// <summary>
    /// `checks add --kind <tür>`: tür ve alanları kayıt defterindeki ölçüt türü kaydından (`specSchema`) gelir; koda tür adı yazılı değildir.
    /// Her alan `--<alan-adı>` bayrağıyla verilir (camelCase tireli yazılır: expectExit → --expect-exit). Verilmeyen alan, şemada varsa `default` değerini alır.
    /// Dizi ya da nesne alanı için `--spec '{...}'` (tam tanım JSON'u) verilir. Zorunlu alan yoksa "--alan gerekli." denir.
    /// </summary>
    private static JsonObject SpecFromRegistry(Args a, string kind)
    {
        var kinds = Soz.Core.Registry.FixedCatalog.Get.OfKind(Soz.Core.Registry.RegistryKinds.CheckKind)
            .Select(e => (Name: ((e.Body["measure"] as JsonObject)?["name"] as JsonValue)?.GetValue<string>(), Entry: e)).Where(x => x.Name is not null).ToList();
        var found = kinds.FirstOrDefault(x => x.Name == kind);
        if (found.Name is null) throw new ArgumentException($"--kind şunlardan biri olmalı: {string.Join(", ", kinds.Select(x => x.Name))}.");
        var schema = found.Entry.Body["specSchema"] as JsonObject ?? new JsonObject();
        var props = schema["properties"] as JsonObject ?? new JsonObject();
        var spec = a.Opt("spec") is { } json
            ? (JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("--spec bir JSON nesnesi olmalı."))
            : new JsonObject();
        foreach (var (name, def) in props)
        {
            if (name == "template") continue;
            var flag = Kebab(name);
            var raw = a.Opt(flag);
            if (raw is null)
            {
                if (!spec.ContainsKey(name) && def is JsonObject d && d["default"] is { } dflt) spec[name] = dflt.DeepClone();
                continue;
            }
            var type = (def as JsonObject)?["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "string";
            spec[name] = type switch
            {
                "integer" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"--{flag} tam sayı olmalı."),
                "number" => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : throw new ArgumentException($"--{flag} sayı olmalı."),
                "boolean" => raw.Equals("true", StringComparison.OrdinalIgnoreCase) ? true : raw.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : throw new ArgumentException($"--{flag} true ya da false olmalı."),
                "array" or "object" => throw new ArgumentException($"--{flag} bu türde bayrakla verilmez. --spec '{{...}}' ile tam tanımı JSON olarak ver."),
                _ => raw,
            };
        }
        foreach (var r in (schema["required"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>() ?? Enumerable.Empty<string>())
            if (!spec.ContainsKey(r)) throw new ArgumentException($"--{Kebab(r)} gerekli.");
        return spec;
    }

    /// <summary>camelCase → kebab-case (expectExit → expect-exit).</summary>
    private static string Kebab(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsUpper(ch)) { if (sb.Length > 0) sb.Append('-'); sb.Append(char.ToLowerInvariant(ch)); }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static void PrintChecks(List<CheckDto> list)
    {
        if (list.Count == 0) { Console.WriteLine("Ölçüt yok."); return; }
        foreach (var c in list)
            Console.WriteLine($"#{c.Id,-4} [{(c.Enabled ? "AÇIK " : "kapalı")}] {c.Kind,-10} {c.Label} · {c.Spec.ToJsonString()} · {c.Origin}{(c.JobId is { } j ? $" · iş #{j}" : "")}");
    }

    // ---------------- proof ----------------
    private static int Proof(Args a)
    {
        const string usage = "Kullanım: soz proof run|show|list …";
        var sub = Sub(a, usage);
        var w = Work(a);
        switch (sub)
        {
            case "run":
            {
                var r = w.RunProof(Project(a), OptLong(a, "job"));
                if (a.Has("json")) { PrintJson(r); return 0; }
                PrintRun(r);
                return r.Verdict == "passed" ? 0 : 3;
            }
            case "show":
            {
                var r = w.Run(IdArg(a, 1, "Kullanım: soz proof show <runId>"));
                if (a.Has("json")) { PrintJson(r); return 0; }
                PrintRun(r);
                return 0;
            }
            case "list":
            {
                var pid = a.Opt("project") is null ? (long?)null : Project(a);
                var list = w.Runs(pid, OptLong(a, "job"), a.Int("limit") ?? 20);
                if (a.Has("json")) { PrintJson(list); return 0; }
                foreach (var r in list) Console.WriteLine($"#{r.Id,-4} {r.StartedAt} · {r.ProjectName} · {r.Trigger} tur {r.Attempt} · {r.Verdict ?? "sürüyor"} · {r.Summary}");
                return 0;
            }
            default: throw new ArgumentException(usage);
        }
    }

    private static void PrintRun(ProofRunDto r)
    {
        Console.WriteLine($"Kanıt çalışması #{r.Id} · {r.ProjectName}{(r.JobId is { } j ? $" · iş #{j}" : "")} · {r.Trigger} tur {r.Attempt}");
        Console.WriteLine($"Hüküm: {r.Verdict ?? "sürüyor"}. {r.Summary}");
        if (r.Claim is { } c) Console.WriteLine($"İddia: {c.Label}");
        foreach (var x in r.Results ?? new())
        {
            Console.WriteLine($"  [{x.Status}] {x.Label}{(x.ExitCode is { } e ? $" (çıkış {e})" : "")} · {x.DurationMs} ms{(x.ArtifactUrl is { } u ? " · " + u : "")}");
            if (x.Status != "passed" && !string.IsNullOrWhiteSpace(x.OutputTail))
            {
                var t = x.OutputTail!.TrimEnd();
                if (t.Length > 1200) t = "…" + t[^1200..];
                foreach (var line in t.Split('\n')) Console.WriteLine("      " + line);
            }
        }
    }

    // ---------------- gate ----------------
    private static int Gate(Args a)
    {
        var sub = Sub(a, "Kullanım: soz gate on|off --project <p>");
        if (sub is not ("on" or "off")) throw new ArgumentException("Kullanım: soz gate on|off --project <p>");
        var g = Work(a).SetGate(Project(a), sub == "on");
        if (a.Has("json")) { PrintJson(g); return 0; }
        Console.WriteLine($"Kanıt kapısı {(g.GateEnabled ? "AÇIK" : "kapalı")}: {g.Name} ({g.EnabledChecks}/{g.Checks} ölçüt açık)");
        return 0;
    }

    // ---------------- queue ----------------
    private static int Queue(Args a)
    {
        var sub = Sub(a, "Kullanım: soz queue add|list …");
        var w = Work(a);
        if (sub == "add")
        {
            var text = string.Join(' ', a.Positional.Skip(1));
            var sid = OptLong(a, "session");
            var q = w.Enqueue(sid is null ? Project(a) : null, sid, text);
            if (a.Has("json")) { PrintJson(q); return 0; }
            Console.WriteLine($"Kuyruğa eklendi #{q.Id} · {q.ProjectName}{(q.SessionId is { } s ? $" · oturum {s}" : "")}: ajanın bir sonraki duruşunda iletilecek.");
            return 0;
        }
        if (sub == "list")
        {
            var pid = a.Opt("project") is null ? (long?)null : Project(a);
            var list = w.Queue(pid, OptLong(a, "session"), a.Has("pending"));
            if (a.Has("json")) { PrintJson(list); return 0; }
            if (list.Count == 0) { Console.WriteLine("Kuyruk boş."); return 0; }
            foreach (var q in list) Console.WriteLine($"#{q.Id,-4} {(q.DeliveredAt is null ? "bekliyor" : "iletildi")} · {q.ProjectName} · {q.Text}");
            return 0;
        }
        throw new ArgumentException("Kullanım: soz queue add|list …");
    }

    // ---------------- morning / now ----------------
    private static int Morning(Args a)
    {
        DateTime? since = null;
        if (a.Opt("since") is { } s)
        {
            if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) throw new ArgumentException("--since ISO tarih olmalı.");
            since = d.UtcDateTime;
        }
        var m = Work(a).Morning(since);
        if (a.Has("json")) { PrintJson(m); return 0; }
        Console.WriteLine($"Sabah raporu ({m.Since} → {m.Until})");
        Console.WriteLine(m.Sentence);
        foreach (var p in m.Passed) Console.WriteLine($"  GEÇTİ  iş #{p.Job.Id} {p.Job.Title} · {p.Attempts} tur · {p.Summary}");
        foreach (var p in m.AcceptedByYou ?? new()) Console.WriteLine($"  SEN ONAYLADIN  iş #{p.Job.Id} {p.Job.Title} · kanıtı sen kabul ettin");
        foreach (var f in m.Failed) Console.WriteLine($"  OLMADI  iş #{f.Job.Id} {f.Job.Title} · {f.Attempts} tur · geçmeyen: {string.Join(", ", f.FailedChecks)}");
        foreach (var u in m.Unverified) Console.WriteLine($"  BELİRSİZ iş #{u.Job.Id} {u.Job.Title} · {u.Verdict} · {u.Summary}");
        foreach (var n in m.WaitingForYou) Console.WriteLine($"  BEKLİYOR oturum {n.SessionId} · {n.ProjectName} · {n.Title} · {n.Reason}");
        foreach (var n in m.Stuck) Console.WriteLine($"  TAKILDI oturum {n.SessionId} · {n.ProjectName} · {n.Title}");
        Console.WriteLine($"  bekçi durdurması: {m.GuardBlocked} · mesaj {m.Usage.Messages}, girdi {m.Usage.InputTokens}, çıktı {m.Usage.OutputTokens} belirteç");
        return 0;
    }

    private static int Now(Args a)
    {
        var n = Work(a).Now();
        if (a.Has("json")) { PrintJson(n); return 0; }
        Console.WriteLine(n.Sentence);
        Console.WriteLine($"  sürüyor {n.Counts.JobsRunning} · geçti {n.Counts.JobsPassed} · sen onayladın {n.Counts.JobsAccepted} · olmadı {n.Counts.JobsFailed} · bekleyen {n.Counts.Waiting} · takılan {n.Counts.Stalled} · kuyruk {n.Counts.QueuePending}");
        return 0;
    }
}
