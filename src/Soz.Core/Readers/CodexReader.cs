using System.Diagnostics;
using System.Text.Json;
using Soz.Core.Data;
using Soz.Core.Text;

namespace Soz.Core.Readers;

/// <summary>
/// Codex oturum kayıtları: &lt;CODEX_HOME&gt;\sessions\YYYY\MM\DD\rollout-*.jsonl.
/// Biçim bu makinedeki gerçek dosyalarla doğrulandı: ilk satır session_meta (payload.id, cwd, source, originator, parent_thread_id);
/// insan mesajı = event_msg / item_completed / item.type=UserMessage; asistan = AgentMessage (phase final_answer|commentary);
/// tur sonu = task_complete (last_agent_message); sıkıştırma = item.type=ContextCompaction.
/// Tür: source "exec" ya da originator codex_exec → automation; source nesnesi {subagent:…} ya da thread_source=subagent → subagent; diğerleri human.
/// </summary>
public sealed class CodexReader : ITranscriptReader
{
    public string Tool => "codex";

    public ReadReport Read(IngestStore store, string root, ReadOptions options)
    {
        var sw = Stopwatch.StartNew();
        var report = new ReadReport { Tool = Tool, Root = root, Found = Directory.Exists(root) };
        if (!report.Found) return report;
        var states = store.LoadFileStates(Tool);
        string[] files;
        try { files = Directory.GetFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.FileErrors++; return report; }
        Array.Sort(files, StringComparer.Ordinal);

        using var conn = store.Db.Open();
        foreach (var file in files)
        {
            report.Files++;
            try
            {
                var fi = new FileInfo(file);
                if (options.SinceUtc is { } since && fi.LastWriteTimeUtc < since) continue;
                states.TryGetValue(file, out var st);
                if (st is not null && st.Size == fi.Length && st.Mtime == Time.Iso(fi.LastWriteTimeUtc)) { report.FilesSkippedUnchanged++; continue; }
                FileBatch batch;
                if (st is not null && st.Offset > 0 && st.Offset <= fi.Length)
                {
                    var ext = conn.Scalar("SELECT external_id FROM sessions WHERE tool='codex' AND file_path=@f", null, ("@f", file)) as string;
                    var kind = conn.Scalar("SELECT kind FROM sessions WHERE tool='codex' AND file_path=@f", null, ("@f", file)) as string;
                    if (ext is null) { st = null; }
                    batch = ReadFile(file, fi, st, ext, kind, options, report);
                }
                else batch = ReadFile(file, fi, null, null, null, options, report);
                var (u, c) = store.Apply(new[] { batch }, options.SinceUtc);
                report.NewUtterances += u; report.NewCompactions += c; report.FilesRead++;
            }
            catch (IOException) { report.FileErrors++; }
            catch (UnauthorizedAccessException) { report.FileErrors++; }
        }
        report.ElapsedMs = sw.ElapsedMilliseconds;
        return report;
    }

    public static FileBatch ReadFile(string file, FileInfo fi, FileStateRow? st, string? knownExternalId, string? knownKind, ReadOptions options, ReadReport report)
    {
        long offset = st?.Offset ?? 0, lineNo = st?.LineNo ?? 0;
        var size = fi.Length;
        var tailStart = Math.Max(offset, size - options.StateTailBytes);
        var acceptTail = (Time.UtcNow() - fi.LastWriteTimeUtc).TotalSeconds > 60;
        SessionUpdate? s = knownExternalId is null ? null : new SessionUpdate { Tool = "codex", ExternalId = knownExternalId, Kind = knownKind };
        var batch = new FileBatch { Path = file, Tool = "codex", Session = s };
        var unrecognized = false;
        var humanSession = knownKind is null or SessionKinds.Human;

        void Handle(ReadOnlySpan<byte> line, long lineOff, long lno)
        {
            if (unrecognized) return;
            if (s is null)
            {
                // İlk satır dosyanın kimliğidir.
                using var d0 = JsonlScanner.TryParse(line);
                if (d0 is null || d0.RootElement.StrN("type") != "session_meta") { unrecognized = true; return; }
                var p = d0.RootElement.Obj("payload");
                var id = p.StrN("id") ?? p.StrN("session_id");
                if (id is null) { unrecognized = true; return; }
                s = new SessionUpdate { Tool = "codex", ExternalId = id, Cwd = Profile.ProfileSources.CleanPath(p.StrN("cwd")) };
                var src = p.Obj("source");
                string kind;
                if (src.ValueKind == JsonValueKind.Object || p.StrN("thread_source") == "subagent") kind = SessionKinds.Subagent;
                else if (src.ValueKind == JsonValueKind.String && src.GetString() == "exec" || p.StrN("originator") == "codex_exec") kind = SessionKinds.Automation;
                else kind = SessionKinds.Human;
                s.Kind = kind;
                humanSession = kind == SessionKinds.Human;
                s.ParentExternalId = p.StrN("parent_thread_id");
                s.Touch(Time.NormalizeIso(p.StrN("timestamp")) ?? Time.NormalizeIso(d0.RootElement.StrN("timestamp")));
                batch.Session = s;
                return;
            }
            if (!JsonlScanner.Contains(line, "\"type\":\"event_msg\""u8, 200))
            {
                // Bozuk satırı say (yalnız JSON değilse).
                if (line[0] != (byte)'{') report.BadLines++;
                return;
            }
            var inTail = lineOff >= tailStart;
            var important = JsonlScanner.Contains(line, "\"UserMessage\""u8, 800) || JsonlScanner.Contains(line, "\"ContextCompaction\""u8, 800)
                            || JsonlScanner.Contains(line, "\"task_complete\""u8, 300) || JsonlScanner.Contains(line, "\"task_started\""u8, 300)
                            || JsonlScanner.Contains(line, "\"turn_aborted\""u8, 300);
            if (!important && !inTail) return;
            using var doc = JsonlScanner.TryParse(line);
            if (doc is null) { report.BadLines++; return; }
            var r = doc.RootElement;
            var ts = Time.NormalizeIso(r.StrN("timestamp"));
            var pl = r.Obj("payload");
            switch (pl.StrN("type"))
            {
                case "item_completed":
                {
                    var it = pl.Obj("item");
                    switch (it.StrN("type"))
                    {
                        case "UserMessage":
                        {
                            var text = TextNormalizer.CleanUserText(JsonlScanner.ContentText(it.Obj("content")));
                            if (text.Length == 0 || ts is null || TextNormalizer.IsSlashCommand(text)) return;
                            if (humanSession)
                            {
                                batch.Utterances.Add(new ParsedUtterance(ts, text, lno));
                                s.SawHumanInput = true;
                            }
                            else s.SawAutomationInput = true;
                            s.LastUserText = text; s.LastUserTs = ts;
                            s.Event(EventKinds.User, ts, false);
                            return;
                        }
                        case "ContextCompaction":
                            if (ts is not null) batch.CompactionTs.Add(ts);
                            return;
                        case "AgentMessage":
                        {
                            var text = JsonlScanner.ContentText(it.Obj("content"));
                            if (string.IsNullOrWhiteSpace(text)) return;
                            s.LastAssistantText = text.Length > options.MaxAssistantChars ? text[..options.MaxAssistantChars] : text;
                            s.LastAssistantTs = ts;
                            s.Event(EventKinds.AssistantText, ts, null);
                            return;
                        }
                        case "Reasoning":
                            s.Event(EventKinds.Other, ts, false);
                            return;
                        case null:
                            return;
                        default:
                            s.Event(EventKinds.Tool, ts, false);
                            return;
                    }
                }
                case "task_complete":
                {
                    var last = pl.StrN("last_agent_message");
                    if (!string.IsNullOrWhiteSpace(last))
                    {
                        s.LastAssistantText = last.Length > options.MaxAssistantChars ? last[..options.MaxAssistantChars] : last;
                        s.LastAssistantTs = ts;
                        s.Event(EventKinds.AssistantText, ts, true);
                    }
                    else { s.TurnEnded = true; s.Touch(ts); }
                    return;
                }
                case "task_started":
                    s.Event(EventKinds.Other, ts, false);
                    return;
                case "turn_aborted":
                    s.Event(EventKinds.Other, ts, true);
                    return;
            }
        }

        batch.NewOffset = JsonlScanner.Scan(file, offset, lineNo, Handle, out var endLine, out var len, acceptTail);
        batch.NewLineNo = endLine;
        batch.Size = len;
        batch.Mtime = Time.Iso(fi.LastWriteTimeUtc);
        report.BytesRead += Math.Max(0, batch.NewOffset - offset);
        if (unrecognized)
        {
            report.Unrecognized++;
            batch.Session = null;
            batch.Utterances.Clear();
        }
        return batch;
    }
}
