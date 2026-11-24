using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core;
using Soz.Core.Hooks;
using Soz.Core.Report;

namespace Soz.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string Usage = """
        Söz: yapay zekâ ajanlarının ustabaşısı

        Kullanım: soz <komut> [seçenekler] [--data <veri klasörü>]
          import [--claude <kök>] [--codex <kök>] [--since 30d] [--no-claude] [--no-codex] [--json]   (kayıt defterindeki öteki gözle araçları için --<araç> <kök> ve --no-<araç>, kapalı gelenler Seni tanıyorum sayfasında kabul edilince açılır)
          projects [--json]
          decisions --project <ad|yol|id|*> [--status proposed|approved|archived|rejected] [--type t] [--json]
          approve <id> | reject <id> | archive <id> | pin <id> | unpin <id>
          add --project <p> --type rule|ban|decision|preference "<metin>"
          repeats [--project <p>] [--min 2] [--json]
          search "<sorgu>" [--project <p>] [--limit 20] [--json]
          packet --project <p|yol> [--budget 2000]
          report [--days 30] [--json]
          status [--hours 24] [--json]
          digest [--hours 24]
          explain <oturumId>
          guard list|templates|apply|test|events (bekçi) · usage [--days 30] [--refresh] (harcama)
          undo list <iş-no> [--json]   (geri alma durumu, yalnız okur)
          hook session-start | user-prompt-submit | stop | stop-failure | notification | pre-tool-use   (stdin: Claude Code kanca JSON'u)
          hooks install|uninstall --settings <settings.json> [--exe <soz.exe>] [--only stop,notification,…]
          job new --project <p|yol> [--night] [--restate] [--check <id,id>] [--title t] [--agent claude|codex] "<metin>" | job list [--project p] [--state s] | job show <id> | job give <id> | job cancel <id>
          job start <n> [--agent claude|codex] [--model m] | job reply <n> "<metin>" | job stop <n>   (Söz ajanı arka planda penceresiz çalışır, start/reply bitene kadar ilerlemeyi yazar)
          agents [--json]   (bulunan ajan araçları, sürüm, yetenekler, hazır mı)
          registry check [--dir <kayıt klasörü>] | registry list [--kind <tür>] [--dir <klasör>] [--json]   (tek kayıt defteri: ayarlar, bağlayıcılar, ölçüt türleri)
          project add <yol> [--json] | project list | project relocate <no> <yeni yol>
          checks suggest --project <p> | checks list --project <p> [--job id] | checks enable <id> | checks disable <id>
          checks add --project <p> [--job id] --kind command|file|url|screenshot --label "<etiket>" (--cmd "…" | --path … | --url …) [--timeout 120] [--enable]
          proof run --project <p> [--job id] | proof show <runId> | proof list [--project p] [--job id]
          gate on|off --project <p>
          queue add (--project <p> | --session <id>) "<mesaj>" | queue list [--project p] [--pending]
          morning [--since <ISO>] | now
          export --project-dir <klasör> [--agents] [--claude]
          serve [--port 49300] [--open] [--exit-after <sn>] [--exit-with-parent <pid>]
          doctor [--json]
          --version
        Çıkış kodu: 0 başarı, 2 kullanım hatası, 1 iç hata.
        """;

    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); Console.InputEncoding = new UTF8Encoding(false); } catch (IOException) { }
        if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
        var cmd = args[0];
        if (cmd is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
        if (cmd is "--version" or "-v") { Console.WriteLine($"soz {SozInfo.Version}"); return 0; }

        // Kanca yolu: en kısa yol, hiçbir koşulda oturumu bozmaz.
        if (cmd == "hook") return RunHook(args);

        Args a;
        try { a = Args.Parse(args[1..], BoolFlags(cmd)); }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }

        // Denetim 3, İlke 2: ajan işinden ya da claude soyundan gelen YAZAN komutlar reddedilir.
        try
        {
            if (CliGuard.Check(cmd, a, SourceLocator.ResolveDataDir(a.Opt("data"))) is { } denied) { Console.Error.WriteLine(denied); return 1; }
        }
        catch (Exception) { /* kimlik bakılamazsa komut yine çalışır: bu katman en iyi çabadır */ }
        try
        {
            return cmd switch
            {
                "serve" => await ServeAsync(a),
                "import" => Import(a),
                "projects" => Projects(a),
                "decisions" => Decisions(a),
                "approve" or "reject" or "archive" or "pin" or "unpin" => SetStatus(cmd, a),
                "add" => Add(a),
                "repeats" => Repeats(a),
                "search" => Search(a),
                "packet" => Packet(a),
                "report" => Report(a),
                "status" => Status(a),
                "digest" => Digest(a),
                "explain" => await Explain(a),
                "hooks" => Hooks(a),
                "export" => Export(a),
                "doctor" => Doctor(a),
                "agents" => Agents(a),
                "registry" => Commands.RegistryCommands.Run(a, Console.Out, Console.Error),
                "undo" => Commands.UndoCommands.Run(a), // geri alma durumu, yalnız okur (d2m)
                "guard" => Commands.GuardUsageCommands.Guard(a, Svc(a)), // B
                "usage" => Commands.GuardUsageCommands.UsageCmd(a, Svc(a)), // B
                "job" or "checks" or "proof" or "queue" or "morning" or "now" or "gate" or "project" => Soz.Cli.Commands.JobProofCommands.Run(cmd, a),
                _ => Fail($"Bilinmeyen komut: {cmd}\n\n{Usage}", 2),
            };
        }
        catch (SozException e) { Console.Error.WriteLine(e.Message); return e.Status is 400 or 404 ? 2 : 1; }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        // Veri klasörü sorunları kendi sade cümlesini taşır: tür adı ya da "iç hata" başlığı yazılmaz.
        catch (Exception e) when (e is Soz.Core.Data.SozDataAccessException or Soz.Core.Data.SozSchemaTooNewException or Soz.Core.Data.SozForeignDatabaseException or Soz.Core.Data.SozBackupFailedException)
        { Console.Error.WriteLine(e.Message); if (Environment.GetEnvironmentVariable("SOZ_DEBUG") == "1") Console.Error.WriteLine(e); return 1; }
        catch (Exception e) { Console.Error.WriteLine($"Beklenmeyen bir hata oldu: {e.Message}"); if (Environment.GetEnvironmentVariable("SOZ_DEBUG") == "1") Console.Error.WriteLine(e); return 1; }
    }

    private static int Fail(string msg, int code) { Console.Error.WriteLine(msg); return code; }

    private static HashSet<string> BoolFlags(string cmd) => cmd switch
    {
        "export" => new() { "agents", "claude", "json" },
        "job" or "checks" or "proof" or "queue" or "morning" or "now" or "gate" or "project" => Soz.Cli.Commands.JobProofCommands.BoolFlags,
        _ => new HashSet<string> { "json", "open", "no-claude", "no-codex", "include-stale", "refresh" }.Concat(ObserveNoFlags()).ToHashSet(),
    };

    /// <summary>Kayıt defterindeki gözle araçlarının `--no-<araç>` bayrakları (değer almaz). Kayıt defteri okunamazsa boş döner, komut kendi hatasını verir.</summary>
    private static IEnumerable<string> ObserveNoFlags()
    {
        try { return Soz.Core.Agents.AgentRegistry.Specs.Where(x => x.CanObserve).Select(x => "no-" + x.Id).ToList(); }
        catch (SozException) { return Array.Empty<string>(); }
        catch (InvalidOperationException) { return Array.Empty<string>(); }
    }

    private static SozService Svc(Args a) => SozService.Open(SourceLocator.ResolveDataDir(a.Opt("data")));
    private static void PrintJson(object o) => Console.WriteLine(JsonSerializer.Serialize(o, JsonOut));

    // ---------------- hook ----------------
    private static int RunHook(string[] args)
    {
        // Kanca ajanı asla kilitlemez: her hata (yol çözme, stdin, çıktı) yutulur, çıkış 0.
        try { return RunHookCore(args); }
        catch (Exception) { return 0; }
    }

    private static int RunHookCore(string[] args)
    {
        Soz.Core.RegexSpeed.ShortLived = true; // kanca her çağrıda yeni süreç: düzenli ifadeler derlenmez (ifade başına ~2,6 ms), eşleşme sonucu aynı
        string? ev = args.Length > 1 ? args[1] : null;
        string? data = null;
        for (var i = 2; i < args.Length; i++) if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
        var dir = SourceLocator.ResolveDataDir(data);
        if (ev is not ("session-start" or "user-prompt-submit" or "stop" or "stop-failure" or "notification" or "pre-tool-use")) return 0; // bilinmeyen kanca: sessizce geç
        using var stdinReader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var rawStdin = stdinReader.ReadToEnd();
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        if (TryRelayHookToLocalServer(dir, ev, rawStdin, stdout))
            return 0;

        return HookHandler.Run(ev, new StringReader(rawStdin), stdout, dir);
    }

    private static bool TryRelayHookToLocalServer(string dataDir, string eventName, string rawStdin, TextWriter stdout)
    {
        try
        {
            var baseUrl = Environment.GetEnvironmentVariable("SOZ_SERVER_URL");
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                var sf = ServerFile.PathFor(dataDir);
                if (!File.Exists(sf)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(sf));
                if (!doc.RootElement.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) return false;
                baseUrl = urlEl.GetString();
            }
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var uri)) return false;
            if (!uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return false;

            var envObj = new JsonObject();
            foreach (var key in new[] { "CLAUDE_PROJECT_DIR", "SOZ_RUN_ID", "SOZ_JOB_ID", "USERPROFILE", "HOME", "TEMP", "TMP", "SOZ_HOOK_PULSE_ALL" })
            {
                var v = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(v)) envObj[key] = v;
            }
            var payloadBytes = Encoding.UTF8.GetBytes(new JsonObject { ["stdin"] = rawStdin, ["env"] = envObj }.ToJsonString());
            var headerBytes = Encoding.ASCII.GetBytes(
                $"POST /api/hooks/{eventName} HTTP/1.1\r\nHost: 127.0.0.1:{uri.Port}\r\nContent-Type: application/json; charset=utf-8\r\n{HookTokenHeaderLine(dataDir)}Content-Length: {payloadBytes.Length}\r\nConnection: close\r\n\r\n");

            using var sock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            var ar = sock.BeginConnect(System.Net.IPAddress.Loopback, uri.Port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(80)) return false;
            sock.EndConnect(ar);
            sock.SendTimeout = 2000;
            sock.ReceiveTimeout = eventName == "stop" ? 330_000 : 3000;
            sock.Send(headerBytes);
            if (payloadBytes.Length > 0) sock.Send(payloadBytes);

            using var ms = new MemoryStream();
            var buf = new byte[4096];
            int r;
            while ((r = sock.Receive(buf)) > 0) ms.Write(buf, 0, r);
            var raw = ms.ToArray();
            var sep = raw.AsSpan().IndexOf("\r\n\r\n"u8);
            if (sep < 0) return false;
            var head = Encoding.ASCII.GetString(raw, 0, sep);
            var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
            var statusLine = lineEnd >= 0 ? head[..lineEnd] : head;
            if (!statusLine.Contains(" 200") && !statusLine.Contains(" 204")) return false;
            var bodySpan = raw.AsSpan(sep + 4);
            if (bodySpan.Length > 0)
            {
                var outText = head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)
                    ? DecodeChunkedUtf8(bodySpan)
                    : Encoding.UTF8.GetString(bodySpan);
                if (!string.IsNullOrEmpty(outText))
                {
                    stdout.Write(outText);
                    stdout.Flush();
                }
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Kanca jetonu başlık satırı (jeton dosyası yoksa boş): sunucu jetonsuz kanca isteğini 401 ile reddeder.</summary>
    private static string HookTokenHeaderLine(string dataDir)
        => Soz.Core.Hooks.HookToken.Read(dataDir) is { } t ? $"{Soz.Core.Hooks.HookInstaller.TokenHeader}: {t}\r\n" : "";

    private static string DecodeChunkedUtf8(ReadOnlySpan<byte> span)
    {
        using var outMs = new MemoryStream();
        var pos = 0;
        while (pos < span.Length)
        {
            var rel = span[pos..].IndexOf("\r\n"u8);
            if (rel < 0) break;
            var hex = Encoding.ASCII.GetString(span.Slice(pos, rel)).Split(';')[0].Trim();
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var len) || len <= 0) break;
            pos += rel + 2;
            var take = Math.Min(len, span.Length - pos);
            outMs.Write(span.Slice(pos, take));
            pos += take + 2;
        }
        return Encoding.UTF8.GetString(outMs.ToArray());
    }

    // ---------------- serve ----------------
    private static async Task<int> ServeAsync(Args a)
    {
        var port = a.Int("port") ?? Soz.Server.SozServer.DefaultPort;
        var data = SourceLocator.ResolveDataDir(a.Opt("data"));
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await using var handle = await Soz.Server.SozServer.StartAsync(data, port);
        Console.WriteLine($"Söz çalışıyor: {handle.Url}  (Ctrl+C ile kapanır, veri: {data})");
        if (handle.Phone.Listening) Console.WriteLine($"Telefon (yerel ağ) açık: {string.Join(", ", handle.Phone.Urls)}");
        if (a.Has("open")) { try { Process.Start(new ProcessStartInfo(handle.Url) { UseShellExecute = true }); } catch (Exception) { } }
        // Sınama bayrağı: --exit-after <sn> verilen süre dolunca kendiliğinden kapanır (yetim sunucu kalmasın diye).
        if (a.Int("exit-after") is { } secs and > 0) cts.CancelAfter(TimeSpan.FromSeconds(secs));
        // Sınama bayrağı: --exit-with-parent <pid> o süreç bitince kapanır.
        if (a.Int("exit-with-parent") is { } ppid and > 0)
            _ = Task.Run(async () =>
            {
                try { using var pp = Process.GetProcessById(ppid); await pp.WaitForExitAsync(cts.Token); } catch (Exception) { }
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            });
        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }
        return 0;
    }

    // ---------------- import ----------------
    private static int Import(Args a)
    {
        int? since = null;
        if (a.Opt("since") is { } sinceArg)
        {
            var t = sinceArg.TrimEnd('d', 'D', 'g', 'G');
            if (!int.TryParse(t, out var d) || d <= 0) return Fail("--since biçimi: 30d", 2);
            since = d;
        }
        var svc = Svc(a);
        var opts = new ImportOptions
        {
            ClaudeRoot = a.Opt("claude"), CodexRoot = a.Opt("codex"), SinceDays = since,
            SkipClaude = a.Has("no-claude"), SkipCodex = a.Has("no-codex"),
        };
        // Öteki gözle araçlarının bayrakları kayıt defterinden gelir (--<araç> <kök>, --no-<araç>); araç adı bu dosyada yazılı değildir.
        // Kapalı gelen araç (Hermes, OpenCode) bayrakla açılmaz: bağlayıcısı "Seni tanıyorum" sayfasında kabul edilmemişse okunmaz ve not düşülür.
        foreach (var spec in Soz.Core.Agents.AgentRegistry.Specs.Where(x => x.CanObserve))
        {
            if (spec.Id is "claude" or "codex") continue;
            if (a.Opt(spec.Id) is { } root) opts.ToolRoots[spec.Id] = root;
            if (a.Has("no-" + spec.Id)) opts.SkipTools.Add(spec.Id);
        }
        var sum = svc.Import(opts);
        if (a.Has("json")) { PrintJson(sum); return 0; }
        Console.WriteLine($"İçe alma bitti ({sum.TotalMs / 1000.0:0.0} sn, okuma {sum.ReadMs / 1000.0:0.0}, ayıklama {sum.ExtractMs / 1000.0:0.0}, kümeleme {sum.ClusterMs / 1000.0:0.0}).");
        foreach (var s in sum.Sources)
        {
            if (!s.Found) { Console.WriteLine($"  {s.Tool}: kök bulunamadı ({s.Root})"); continue; }
            Console.WriteLine($"  {s.Tool}: {s.Files} dosya ({s.FilesRead} okundu, {s.FilesSkippedUnchanged} değişmemiş{(s.SubagentFiles > 0 ? $", {s.SubagentFiles} alt ajan dosyası" : "")}), " +
                              $"{s.BytesRead / 1e6:0.0} MB, {s.NewUtterances} yeni mesaj, {s.NewCompactions} sıkıştırma, okuyamadım: {s.Unrecognized} dosya, bozuk satır: {s.BadLines}, dosya hatası: {s.FileErrors}");
        }
        foreach (var note in sum.ObserveNotes) Console.WriteLine($"  not: {note}");
        Console.WriteLine($"  ayıklanan mesaj {sum.Extracted}, yeni aday {sum.NewCandidates}, yeni küme {sum.NewClusters}, yeni tekrar kümesi {sum.NewRepeatClusters}, genel üst küme {sum.ParentClusters}");
        Console.WriteLine($"  yeni öneri {sum.NewProposals}, güncellenen {sum.UpdatedProposals}, geri çekilen {sum.WithdrawnProposals}");
        return 0;
    }

    // ---------------- projects / decisions ----------------
    private static int Projects(Args a)
    {
        var list = Svc(a).Projects();
        if (a.Has("json")) { PrintJson(list); return 0; }
        Console.WriteLine($"{"id",4}  {"ad",-28} {"mesaj",6} {"öneri",6} {"onaylı",6} {"tekrar",6}  son");
        foreach (var p in list)
            Console.WriteLine($"{p.Id,4}  {Cut(p.Name, 28),-28} {p.Utterances,6} {p.Proposed,6} {p.Approved,6} {p.Repeats,6}  {Local(p.LastAt)}");
        return 0;
    }

    private static long? Project(SozService svc, Args a, bool required)
    {
        var spec = a.Opt("project");
        if (spec is null) { if (required) throw new ArgumentException("--project gerekli (ad, yol, id ya da *)."); return null; }
        return svc.ResolveProject(spec) ?? throw new SozException(404, $"Proje bulunamadı: {spec}");
    }

    private static int Decisions(Args a)
    {
        var svc = Svc(a);
        var pid = Project(svc, a, required: false);
        var list = svc.Decisions(pid, a.Opt("status"), a.Opt("type"));
        if (a.Has("json")) { PrintJson(list); return 0; }
        if (list.Count == 0) { Console.WriteLine("Kayıt yok."); return 0; }
        foreach (var d in list)
            Console.WriteLine($"#{d.Id,-5} [{d.Status}] {d.Type,-10} {(d.Pinned ? "📌 " : "")}{d.Text}  · {d.ProjectName} · {d.RepeatCount} kez / {d.SessionCount} oturum");
        return 0;
    }

    private static int SetStatus(string cmd, Args a)
    {
        if (a.Positional.Count < 1 || !long.TryParse(a.Positional[0], out var id)) return Fail($"Kullanım: soz {cmd} <id>", 2);
        var svc = Svc(a);
        var d = cmd switch
        {
            "approve" => svc.PatchDecision(id, status: "approved"),
            "reject" => svc.PatchDecision(id, status: "rejected"),
            "archive" => svc.PatchDecision(id, status: "archived"),
            "pin" => svc.PatchDecision(id, pinned: true),
            _ => svc.PatchDecision(id, pinned: false),
        };
        Console.WriteLine($"#{d.Id} [{d.Status}]{(d.Pinned ? " 📌" : "")} {d.Text}");
        return 0;
    }

    private static int Add(Args a)
    {
        var svc = Svc(a);
        var pid = Project(svc, a, required: true)!.Value;
        var text = string.Join(' ', a.Positional);
        if (string.IsNullOrWhiteSpace(text)) return Fail("Metin gerekli: soz add --project <p> --type rule \"…\"", 2);
        var d = svc.CreateDecision(pid, a.Opt("type") ?? "rule", text);
        Console.WriteLine($"Eklendi: #{d.Id} [{d.Status}] {d.Type} {d.Text}");
        return 0;
    }

    private static int Repeats(Args a)
    {
        var svc = Svc(a);
        var list = svc.Repeats(Project(svc, a, false), a.Int("min") ?? 2, a.Has("json") ? 50 : 3);
        if (a.Has("json")) { PrintJson(list); return 0; }
        if (list.Count == 0) { Console.WriteLine("Tekrar kümesi yok."); return 0; }
        foreach (var r in list)
            Console.WriteLine($"küme {r.ClusterId,-5} {r.Count,3} kez / {r.SessionCount,2} oturum · {r.ProjectName} · {Cut(r.Canonical, 110)}{(r.DecisionId is { } did ? $"  (karar #{did})" : "")}");
        return 0;
    }

    private static int Search(Args a)
    {
        var svc = Svc(a);
        var q = string.Join(' ', a.Positional);
        var page = svc.Utterances(q, Project(svc, a, false), a.Int("limit") ?? 20, a.Int("offset") ?? 0);
        if (a.Has("json")) { PrintJson(page); return 0; }
        Console.WriteLine($"{page.Total} sonuç.");
        foreach (var u in page.Items) Console.WriteLine($"{Local(u.Ts)} · {u.Tool} · {u.ProjectName} · {Cut(u.Text.Replace('\n', ' '), 160)}");
        return 0;
    }

    private static int Packet(Args a)
    {
        var svc = Svc(a);
        var spec = a.Opt("project") ?? throw new ArgumentException("--project gerekli.");
        var pid = svc.ResolveProject(spec);
        if (pid is null && spec != "*" && !Directory.Exists(spec)) throw new SozException(404, $"Proje bulunamadı: {spec}");
        var p = svc.Packet(pid, a.Int("budget"));
        if (a.Has("json")) { PrintJson(p); return 0; }
        Console.WriteLine(p.Text);
        return 0;
    }

    // ---------------- report / status / digest / explain ----------------
    private static int Report(Args a)
    {
        var r = Svc(a).Report(a.Int("days") ?? 30);
        if (a.Has("json")) { PrintJson(r); return 0; }
        Console.WriteLine($"Karne · son {r.Days} gün");
        Console.WriteLine($"  proje {r.Projects} · insan oturumu {r.Sessions} · mesaj {r.Utterances} · sıkıştırma {r.Compactions}");
        foreach (var t in r.Tools) Console.WriteLine($"  {t.Tool}: {t.Sessions} oturum, {t.Utterances} mesaj, {t.Compactions} sıkıştırma");
        Console.WriteLine($"  otomasyon oturumu {r.AutomationSessions} · alt ajan çalışması {r.SubagentSessions}");
        Console.WriteLine($"  aday {r.Candidates} · tekrar kümesi {r.RepeatClusters} · toplam tekrar {r.TotalRepeats} · bekleyen öneri {r.Proposed} · onaylı {r.Approved}");
        if (r.TopRepeats.Count > 0)
        {
            Console.WriteLine("  En çok tekrar edilenler:");
            foreach (var t in r.TopRepeats) Console.WriteLine($"    {t.Count} kez / {t.SessionCount} oturum · {Cut(t.Text, 100)}");
        }
        if (r.Daily.Count > 0)
            Console.WriteLine($"  günlük: en az {r.Daily.Min(d => d.Utterances)}, en çok {r.Daily.Max(d => d.Utterances)} ({r.Daily.MaxBy(d => d.Utterances)!.Date}), {r.Daily.Count} etkin gün");
        return 0;
    }

    private static int Status(Args a)
    {
        var list = Svc(a).Sessions(a.Dbl("hours") ?? 24, a.Has("include-stale"));
        if (a.Has("json")) { PrintJson(list); return 0; }
        if (list.Count == 0) { Console.WriteLine("Bu sürede etkin insan oturumu yok."); return 0; }
        foreach (var g in list.GroupBy(x => x.State).OrderBy(g => Array.IndexOf(new[] { "waiting", "stalled", Soz.Core.Sessions.SessionStates.Uncertain, "working", "idle", "stale" }, g.Key)))
        {
            Console.WriteLine($"{Strings.Get("tr", "state." + g.Key)} ({g.Count()})");
            foreach (var s in g)
            {
                var flags = new List<string>();
                if (s.AsksQuestion) flags.Add("soru");
                if (s.ClaimsDone) flags.Add(s.ShowsEvidence ? "bitti+kanıt" : "bitti, kanıt yok");
                Console.WriteLine($"  #{s.Id} {s.Tool} · {s.ProjectName} · {Cut(string.IsNullOrWhiteSpace(s.Title) ? string.IsNullOrWhiteSpace(s.LastUserText) ? "(başlıksız)" : "«" + Soz.Core.Sessions.DigestBuilder.Display(s.LastUserText ?? "").ReplaceLineEndings(" ") + "»" : s.Title, 50)} · {Local(s.Since)}{(s.SubCount > 0 ? $" · {s.SubCount} alt iş" : "")}{(flags.Count > 0 ? " [" + string.Join(", ", flags) + "]" : "")}");
            }
        }
        return 0;
    }

    private static int Digest(Args a)
    {
        var d = Svc(a).Digest(a.Dbl("hours") ?? 24);
        if (a.Has("json")) { PrintJson(d); return 0; }
        Console.WriteLine(d.Text);
        return 0;
    }

    private static async Task<int> Explain(Args a)
    {
        if (a.Positional.Count < 1 || !long.TryParse(a.Positional[0], out var id)) return Fail("Kullanım: soz explain <oturumId>", 2);
        var r = await Svc(a).ExplainAsync(id);
        if (!r.Ok) return Fail(r.Text, 1);
        Console.WriteLine(r.Text);
        return 0;
    }

    // ---------------- hooks / export / doctor ----------------
    private static int Hooks(Args a)
    {
        var sub = a.Positional.FirstOrDefault();
        var settings = a.Opt("settings");
        if (sub is not ("install" or "uninstall") || settings is null)
            return Fail("Kullanım: soz hooks install|uninstall --settings <settings.json yolu> [--exe <soz.exe>] [--only stop,notification,…] [--transport command|http]", 2);
        if (sub == "install")
        {
            var defaultSoz = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "soz.exe");
            var siblingKanca = Path.Combine(Path.GetDirectoryName(defaultSoz) ?? "", "soz-kanca.exe");
            var exe = a.Opt("exe") ?? (File.Exists(siblingKanca) ? siblingKanca : defaultSoz);
            var data = a.Opt("data") is { } d ? Path.GetFullPath(d) : null;
            var selected = HookInstaller.Select(a.Opt("only"));
            var st = Svc(a).Settings();
            var budget = Soz.Core.Jobs.WorkService.GateBudgetSeconds(st);
            // Varsayılan komut yolu. http (hızlı kip) yalnız --transport http, SOZ_HOOK_TRANSPORT=http ya da Ayarlar'daki hooksFastMode ile; --transport command her zaman komutu zorlar.
            var tr = a.Opt("transport");
            var http = string.Equals(tr, "command", StringComparison.OrdinalIgnoreCase) ? null
                : HookTransportPlanner.Plan(SourceLocator.ResolveDataDir(a.Opt("data")), st.ClaudePath, fastMode: string.Equals(tr, "http", StringComparison.OrdinalIgnoreCase) || st.Bool("hooksFastMode"));
            var res = HookInstaller.InstallDetailed(settings, exe, data, a.Opt("dotnet-root") ?? HookInstaller.DotnetRootFor(exe), a.Opt("only"), budget, http);
            var viaHttp = res.Transport.Where(t => t.Value == "http").Select(t => t.Key).ToList();
            Console.WriteLine($"Terminal desteği kuruldu: {settings} ({string.Join(", ", selected.Select(h => h.Event))}, Stop zaman aşımı {budget + 30} sn)."
                + (viaHttp.Count > 0 ? $" Süreç açmayan yol (http): {string.Join(", ", viaHttp)}." : " Komut yolu (soz-kanca).")
                + (res.BackupPath is null ? (res.Changed ? "" : " Dosya zaten güncel, değişmedi.") : $" Önceki hâlin yedeği: {res.BackupPath}"));
        }
        else
        {
            var removed = HookInstaller.Uninstall(settings);
            Console.WriteLine(removed ? $"Söz'ün terminal desteği kaldırıldı: {settings}" : "Kaldırılacak terminal desteği yok.");
        }
        return 0;
    }

    private static int Export(Args a)
    {
        var dir = a.Opt("project-dir") ?? throw new ArgumentException("--project-dir gerekli.");
        var r = Svc(a).Export(Path.GetFullPath(dir), a.Has("agents"), a.Has("claude"));
        Console.WriteLine(r.Message);
        foreach (var f in r.Files) Console.WriteLine("  " + f);
        return 0;
    }

    /// <summary>Bulunan ajan araçları: sürüm, hazır mı, bekçi kademesi. Model çağırmaz, yalnız `--version` sorar.</summary>
    private static int Agents(Args a)
    {
        var st = SozSettings.Load(Svc(a).Db);
        var list = Soz.Core.Agents.AgentProbe.All(st, true, Soz.Core.Registry.RegistryHost.Load(Svc(a).Db));
        if (a.Has("json")) { PrintJson(new { defaultAgent = st.DefaultAgent, agents = list }); return 0; }
        foreach (var x in list)
            Console.WriteLine($"  {x.Id,-7} {x.Name,-12} {(x.Ready ? "hazır" : x.Found ? "bulundu, sürüm okunamadı" : "bulunamadı")}{(x.Version is { } v ? " · " + v : "")}{(x.IsDefault ? " · varsayılan" : "")} · bekçi: {x.GuardLabel}{(x.Error is { } er && !x.Ready ? " · " + er : "")}");
        return 0;
    }

    private static int Doctor(Args a)
    {
        var health = Soz.Core.Registry.DoctorHealth.Collect(Svc(a).Db); // d1f: ayar kaydı, olay defteri zinciri, düzeltilen ayarlar
        if (health.RegistryError is not null)
        {
            if (a.Has("json")) Console.WriteLine(health.ToJson().ToJsonString(JsonOut));
            else foreach (var l in health.Lines()) Console.Error.WriteLine(l);
            return 1;
        }
        var o = Svc(a).Doctor();
        if (a.Has("json")) { foreach (var (k, v) in health.ToJson()) o[k] = v?.DeepClone(); Console.WriteLine(o.ToJsonString(JsonOut)); return 0; }
        Console.WriteLine($"Söz {o["version"]} · veri: {o["dataDir"]} · şema v{o["dbSchemaVersion"]} · {((long?)o["dbSizeBytes"] ?? 0) / 1e6:0.0} MB");
        Console.WriteLine($"Veri klasörü neden bu: {o["dataDirSource"]} · exe: {o["exeDir"]}");
        Console.WriteLine($"Son içe alma: {Local((string?)o["lastImportAt"])} · deneme projesi: {o["demoProjectDir"]}");
        foreach (var s in o["sources"]!.AsArray())
        {
            Console.WriteLine($"  {s!["tool"]}: {s["root"]} ({s["origin"]}): {((bool)s["exists"]! ? "var" : "YOK")}");
            if ((bool)s["exists"]!)
                Console.WriteLine($"     {s["files"]} dosya{(s["subagentFiles"] is { } sf ? $" ({sf} alt ajan)" : "")}, {s["totalMB"]} MB, okunabilir: {((bool?)s["readable"] == true ? "evet" : "HAYIR")}, içe alınmış dosya: {s["importedFiles"]}");
        }
        var cnt = o["counts"]!;
        Console.WriteLine($"  sayılar: proje {cnt["projects"]}, insan oturumu {cnt["humanSessions"]}, otomasyon {cnt["automationSessions"]}, alt ajan {cnt["subagentSessions"]}, mesaj {cnt["utterances"]}, aday {cnt["candidates"]}, küme {cnt["clusters"]}, öneri {cnt["proposed"]}, onaylı {cnt["approved"]}, sıkıştırma {cnt["compactions"]}");
        if (o["hookHealth"] is JsonObject hh)
        {
            var files = hh["files"]!.AsArray().Where(f => f!["hooks"]!.AsArray().Count > 0).ToList();
            Console.WriteLine($"  terminal desteği: {files.Count} ayar dosyasında kurulu · kanıt kapısı: {(hh["gateProblem"] is { } gp ? "ÇALIŞMIYOR: " + gp : "sorun yok")}");
            foreach (var f in files)
                Console.WriteLine($"     {f!["projectName"] ?? f["scope"]}: {string.Join(", ", f["hooks"]!.AsArray().Select(x => (string?)x))} · exe {((bool?)f["exeExists"] switch { true => "var", false => "YOK", _ => "?" })}" +
                                  $"{((bool?)f["dataMatches"] == false ? " · BAŞKA veri klasörü" : "")}");
            foreach (var s in hh["stats"]!.AsArray())
                Console.WriteLine($"     {s!["name"],-19} 24 sa: {s["runs24h"]} çalışma, {s["errors24h"]} hata, {s["timeouts24h"]} zaman aşımı{(s["maxMs"] is { } mx ? $", en uzun {mx} ms" : "")}{(s["lastError"] is { } le ? " · son hata: " + le : "")}");
            foreach (var w in hh["warnings"]!.AsArray()) Console.WriteLine($"  UYARI: {w}");
        }
        Console.WriteLine($"  terminal desteği günlüğü: {o["hookLogLines"]} satır · sunucu: {(o["serverFile"] is null ? "kapalı" : (bool)o["serverFile"]!["alive"]! ? o["serverFile"]!["url"] : "kapalı (bayat sunucu.json: süreç yok)")}");
        foreach (var l in health.Lines()) Console.WriteLine("  " + l);
        return 0;
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    private static string Local(string? iso)
    {
        var t = Soz.Core.Data.Time.Parse(iso);
        return t is null ? "-" : t.Value.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
    }
}

/// <summary>Basit argüman ayrıştırıcı: --ad değer, --bayrak, konumsal.</summary>
public sealed class Args
{
    public List<string> Positional { get; } = new();
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args, HashSet<string> boolFlags)
    {
        var a = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            var t = args[i];
            if (t.StartsWith("--") && t.Length > 2)
            {
                var name = t[2..];
                string? val = null;
                var eq = name.IndexOf('=');
                if (eq > 0) { val = name[(eq + 1)..]; name = name[..eq]; }
                else if (!boolFlags.Contains(name))
                {
                    if (i + 1 >= args.Length) throw new ArgumentException($"--{name} için değer gerekli.");
                    val = args[++i];
                }
                a._opts[name] = val;
            }
            else a.Positional.Add(t);
        }
        return a;
    }

    public bool Has(string n) => _opts.ContainsKey(n);
    public string? Opt(string n) => _opts.TryGetValue(n, out var v) ? v : null;
    public int? Int(string n) => Opt(n) is { } s ? (int.TryParse(s, out var i) ? i : throw new ArgumentException($"--{n} sayı olmalı.")) : null;
    public double? Dbl(string n) => Opt(n) is { } s ? (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new ArgumentException($"--{n} sayı olmalı.")) : null;
}
