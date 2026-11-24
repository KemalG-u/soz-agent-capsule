using System.Diagnostics;
using System.Text.Json;
using Soz.Core.Data;
using Soz.Core.Text;

namespace Soz.Core.Readers;

/// <summary>
/// Claude Code oturum kayıtları: &lt;kök&gt;\&lt;slug&gt;\&lt;oturum&gt;.jsonl (ana), &lt;kök&gt;\&lt;slug&gt;\&lt;oturum&gt;\subagents\**\*.jsonl (alt ajan).
/// Biçim bu makinedeki gerçek dosyaların alan adlarıyla doğrulandı (Claude Code 2.1.3xx):
/// insan mesajı = type:user, düz metin içerik, isMeta/isCompactSummary/isSidechain değil, origin yok ya da origin.kind=human,
/// entrypoint sdk-cli değil, promptSource sdk/system değil; ya da attachment.type=queued_command + commandMode=prompt + origin.kind=human.
/// </summary>
public sealed class ClaudeCodeReader : ITranscriptReader
{
    public string Tool => "claude";

    private static readonly string[] NotHumanPrefixes =
    {
        "Another Claude", "This session is being continued", "Caveat:", "[Request interrupted",
        "[SYSTEM NOTIFICATION",
    };
    // Not: başı bu öneklerden biri olan mesaj tümüyle atılır. Etiketli sistem blokları (<task-notification>, <system-reminder>,
    // <local-command-*>, <command-*>, <bash-*>, <agent-message>, ...) ise mesajın başında da ortasında da olsa
    // TextNormalizer.CleanUserText ile (kancayla aynı işlev) çıkarılır; yalnız bloklardan oluşan mesaj boş kalır ve sayılmaz,
    // karışıksa kalan metin sayılır.

    public ReadReport Read(IngestStore store, string root, ReadOptions options)
    {
        var sw = Stopwatch.StartNew();
        var report = new ReadReport { Tool = Tool, Root = root, Found = Directory.Exists(root) };
        if (!report.Found) return report;
        var states = store.LoadFileStates(Tool);
        var subBatches = new List<FileBatch>();

        foreach (var projDir in SafeDirs(root))
        {
            foreach (var file in SafeFiles(projDir, SearchOption.TopDirectoryOnly))
            {
                report.Files++;
                try
                {
                    var fi = new FileInfo(file);
                    if (options.SinceUtc is { } since && fi.LastWriteTimeUtc < since) continue;
                    states.TryGetValue(file, out var st);
                    if (st is not null && st.Size == fi.Length && st.Mtime == Time.Iso(fi.LastWriteTimeUtc)) { report.FilesSkippedUnchanged++; continue; }
                    var batch = ReadMain(file, fi, st, options, report);
                    var (u, c) = store.Apply(new[] { batch }, options.SinceUtc);
                    report.NewUtterances += u; report.NewCompactions += c; report.FilesRead++;
                }
                catch (IOException) { report.FileErrors++; }
                catch (UnauthorizedAccessException) { report.FileErrors++; }
            }

            foreach (var sessDir in SafeDirs(projDir))
            {
                var parent = Path.GetFileName(sessDir);
                foreach (var file in SafeFiles(sessDir, SearchOption.AllDirectories))
                {
                    report.SubagentFiles++;
                    try
                    {
                        var fi = new FileInfo(file);
                        if (options.SinceUtc is { } since && fi.LastWriteTimeUtc < since) continue;
                        states.TryGetValue(file, out var st);
                        if (st is not null && st.Size == fi.Length && st.Mtime == Time.Iso(fi.LastWriteTimeUtc)) continue;
                        subBatches.Add(RegisterSubagent(file, fi, parent));
                        if (subBatches.Count >= 300) { store.Apply(subBatches, null); subBatches.Clear(); }
                    }
                    catch (IOException) { report.FileErrors++; }
                    catch (UnauthorizedAccessException) { report.FileErrors++; }
                }
            }
        }
        if (subBatches.Count > 0) store.Apply(subBatches, null);
        report.ElapsedMs = sw.ElapsedMilliseconds;
        return report;
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch (IOException) { return Array.Empty<string>(); } catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string dir, SearchOption opt)
    {
        try { return Directory.GetFiles(dir, "*.jsonl", opt); } catch (IOException) { return Array.Empty<string>(); } catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static FileBatch RegisterSubagent(string file, FileInfo fi, string parentSessionId)
    {
        var s = new SessionUpdate { Tool = "claude", ExternalId = Path.GetFileNameWithoutExtension(file), Kind = SessionKinds.Subagent, ParentExternalId = parentSessionId };
        var first = JsonlScanner.ReadFirstLine(file, 256 * 1024);
        if (first is not null)
        {
            using var doc = JsonlScanner.TryParse(first);
            if (doc is not null)
            {
                s.Cwd = doc.RootElement.StrN("cwd");
                s.Touch(Time.NormalizeIso(doc.RootElement.StrN("timestamp")));
            }
        }
        var m = Time.Iso(fi.LastWriteTimeUtc);
        s.Touch(m);
        s.LastEventTs = m;
        return new FileBatch { Path = file, Tool = "claude", Session = s, NewOffset = fi.Length, Size = fi.Length, Mtime = m };
    }

    /// <summary>Tek bir ana oturum dosyasını kaldığı yerden okur.</summary>
    public static FileBatch ReadMain(string file, FileInfo fi, FileStateRow? st, ReadOptions options, ReadReport report)
    {
        var s = new SessionUpdate { Tool = "claude", ExternalId = Path.GetFileNameWithoutExtension(file) };
        var batch = new FileBatch { Path = file, Tool = "claude", Session = s };
        long offset = st?.Offset ?? 0, lineNo = st?.LineNo ?? 0;
        var size = fi.Length;
        if (offset > size) { offset = 0; lineNo = 0; }
        var tailStart = Math.Max(offset, size - options.StateTailBytes);
        var acceptTail = (Time.UtcNow() - fi.LastWriteTimeUtc).TotalSeconds > 60;
        string? customTitle = null, aiTitle = null;
        int recognized = 0, lines = 0;

        void Handle(ReadOnlySpan<byte> line, long lineOff, long lno)
        {
            lines++;
            var type = JsonlScanner.RootType(line, out var bad);
            if (bad) { report.BadLines++; return; }
            var inTail = lineOff >= tailStart;
            switch (type)
            {
                case "user":
                {
                    recognized++;
                    var isToolResult = JsonlScanner.Contains(line, "\"toolUseResult\":"u8);
                    if (isToolResult && !inTail) return;
                    using var doc = JsonlScanner.TryParse(line);
                    if (doc is null) { report.BadLines++; return; }
                    var r = doc.RootElement;
                    var ts = Time.NormalizeIso(r.StrN("timestamp"));
                    s.Cwd ??= r.StrN("cwd");
                    if (isToolResult || IsToolResultContent(r)) { s.Event(EventKinds.Tool, ts, false); return; }
                    HandleUser(r, ts, lno, inTail, s, batch, options);
                    return;
                }
                case "attachment":
                {
                    recognized++;
                    if (!JsonlScanner.Contains(line, "\"queued_command\""u8)) return;
                    using var doc = JsonlScanner.TryParse(line);
                    if (doc is null) { report.BadLines++; return; }
                    HandleQueued(doc.RootElement, lno, s, batch);
                    return;
                }
                case "system":
                {
                    recognized++;
                    var isCompact = JsonlScanner.Contains(line, "\"compact_boundary\""u8);
                    var isTurnEnd = inTail && JsonlScanner.Contains(line, "\"turn_duration\""u8);
                    if (!isCompact && !isTurnEnd) return;
                    using var doc = JsonlScanner.TryParse(line);
                    if (doc is null) { report.BadLines++; return; }
                    var r = doc.RootElement;
                    var ts = Time.NormalizeIso(r.StrN("timestamp"));
                    var sub = r.StrN("subtype");
                    if (sub == "compact_boundary" && ts is not null) batch.CompactionTs.Add(ts);
                    else if (sub == "turn_duration") { s.TurnEnded = true; s.Touch(ts); }
                    return;
                }
                case "assistant":
                {
                    recognized++;
                    if (!inTail) return;
                    using var doc = JsonlScanner.TryParse(line);
                    if (doc is null) { report.BadLines++; return; }
                    HandleAssistant(doc.RootElement, s, options);
                    return;
                }
                case "custom-title":
                case "ai-title":
                {
                    using var doc = JsonlScanner.TryParse(line);
                    if (doc is null) return;
                    if (type == "custom-title") customTitle = doc.RootElement.StrN("customTitle") ?? customTitle;
                    else aiTitle = doc.RootElement.StrN("aiTitle") ?? aiTitle;
                    return;
                }
                default:
                    if (type is "summary" or "queue-operation" or "last-prompt" or "file-history-snapshot" or "mode" or "permission-mode") recognized++;
                    return;
            }
        }

        batch.NewOffset = JsonlScanner.Scan(file, offset, lineNo, Handle, out var endLine, out var len, acceptTail);
        batch.NewLineNo = endLine;
        batch.Size = len;
        batch.Mtime = Time.Iso(fi.LastWriteTimeUtc);
        report.BytesRead += Math.Max(0, batch.NewOffset - offset);
        if (lines > 0 && recognized == 0) report.Unrecognized++;
        s.Title = customTitle ?? aiTitle;
        return batch;
    }

    private static bool IsToolResultContent(JsonElement r)
    {
        var c = r.Obj("message").Obj("content");
        if (c.ValueKind != JsonValueKind.Array) return false;
        foreach (var x in c.EnumerateArray())
            if (x.ValueKind == JsonValueKind.Object && x.Str("type") == "tool_result") return true;
        return false;
    }

    private static void HandleUser(JsonElement r, string? ts, long lno, bool inTail, SessionUpdate s, FileBatch batch, ReadOptions options)
    {
        if (r.Bool("isSidechain") || r.Bool("isCompactSummary") || r.Bool("isMeta")) return;
        var origin = r.Obj("origin");
        var originKind = origin.ValueKind == JsonValueKind.Object ? origin.StrN("kind") : null;
        if (originKind is not null && originKind != "human")
        {
            s.SawAutomationInput = true;
            s.Event(EventKinds.Other, ts, false);
            return;
        }
        if (originKind is null)
        {
            var promptSource = r.StrN("promptSource");
            if (r.StrN("entrypoint") == "sdk-cli" || promptSource is "sdk" or "system" || r.Has("scheduledTaskId"))
            {
                s.SawAutomationInput = true;
                s.Event(EventKinds.Other, ts, false);
                return;
            }
        }
        var text = JsonlScanner.ContentText(r.Obj("message").Obj("content"));
        var head = text.TrimStart();
        if (head.StartsWith("[Request interrupted", StringComparison.Ordinal)) { s.Event(EventKinds.Other, ts, true); return; }
        foreach (var p in NotHumanPrefixes)
        {
            if (head.StartsWith(p, StringComparison.Ordinal))
            {
                if (p == "Another Claude") { s.SawAutomationInput = true; s.Event(EventKinds.Other, ts, false); }
                return;
            }
        }
        AddHuman(text, ts, lno, s, batch);
    }

    private static void HandleQueued(JsonElement r, long lno, SessionUpdate s, FileBatch batch)
    {
        if (r.Bool("isSidechain")) return;
        var a = r.Obj("attachment");
        if (a.Str("type") != "queued_command") return;
        var originKind = a.Obj("origin").StrN("kind");
        s.Cwd ??= r.StrN("cwd");
        var ts = Time.NormalizeIso(a.StrN("timestamp")) ?? Time.NormalizeIso(r.StrN("timestamp"));
        if (originKind != "human" || a.StrN("commandMode") != "prompt") { s.SawAutomationInput |= originKind is not null && originKind != "human"; return; }
        var p = a.Obj("prompt");
        var text = p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : JsonlScanner.ContentText(p);
        var head = text.TrimStart();
        foreach (var pre in NotHumanPrefixes) if (head.StartsWith(pre, StringComparison.Ordinal)) return;
        AddHuman(text, ts, lno, s, batch);
    }

    private static void AddHuman(string text, string? ts, long lno, SessionUpdate s, FileBatch batch)
    {
        var clean = TextNormalizer.CleanUserText(text);
        if (clean.Length == 0 || ts is null) return;
        if (TextNormalizer.IsSlashCommand(clean)) { s.Event(EventKinds.Other, ts, null); return; }
        s.SawHumanInput = true;
        batch.Utterances.Add(new ParsedUtterance(ts, clean, lno));
        s.LastUserText = clean;
        s.LastUserTs = ts;
        s.Event(EventKinds.User, ts, false);
    }

    private static void HandleAssistant(JsonElement r, SessionUpdate s, ReadOptions options)
    {
        if (r.Bool("isSidechain")) return;
        var ts = Time.NormalizeIso(r.StrN("timestamp"));
        s.Cwd ??= r.StrN("cwd");
        if (r.Bool("isApiErrorMessage")) { s.Event(EventKinds.Other, ts, true); return; }
        var msg = r.Obj("message");
        var stop = msg.StrN("stop_reason");
        var content = msg.Obj("content");
        string? text = null; bool hasTool = false, hasThinking = false;
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in content.EnumerateArray())
            {
                switch (b.Str("type"))
                {
                    case "text":
                        var t = b.Str("text");
                        if (!string.IsNullOrWhiteSpace(t)) text = text is null ? t : text + "\n" + t;
                        break;
                    case "tool_use": hasTool = true; break;
                    case "thinking": case "redacted_thinking": hasThinking = true; break;
                }
            }
        }
        else if (content.ValueKind == JsonValueKind.String) text = content.GetString();

        if (text is not null)
        {
            s.LastAssistantText = text.Length > options.MaxAssistantChars ? text[..options.MaxAssistantChars] : text;
            s.LastAssistantTs = ts;
        }
        if (hasTool || stop == "tool_use") s.Event(text is not null && !hasTool ? EventKinds.AssistantText : EventKinds.Tool, ts, false);
        else if (text is not null) s.Event(EventKinds.AssistantText, ts, stop is "end_turn" or "stop_sequence" or "max_tokens");
        else if (hasThinking) s.Event(EventKinds.Other, ts, false);
    }
}
