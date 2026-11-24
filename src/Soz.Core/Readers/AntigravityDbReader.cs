using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Profile;

namespace Soz.Core.Readers;

/// <summary>
/// Antigravity oturum özetleri (`conversation_summaries.db`), salt okunur.
/// Yalnız özet tablosu (`conversation_summaries`) okunur, konuşma içeriği açılmaz.
/// </summary>
public sealed class AntigravityDbReader : ITranscriptReader
{
    public string Tool => "antigravity";

    public ReadReport Read(IngestStore store, string root, ReadOptions options)
    {
        var sw = Stopwatch.StartNew();
        var dbPath = File.Exists(root) ? root : Path.Combine(root, "conversation_summaries.db");
        var report = new ReadReport { Tool = Tool, Root = root, Found = Directory.Exists(root) || File.Exists(dbPath) };
        if (!File.Exists(dbPath))
        {
            report.ElapsedMs = sw.ElapsedMilliseconds;
            return report;
        }

        report.Files = 1;
        var states = store.LoadFileStates(Tool);
        try
        {
            var rows = ProfileSources.ReadAntigravityDb(dbPath, Time.UtcNow());
            var batches = new List<FileBatch>();
            foreach (var r in rows)
            {
                if (options.SinceUtc is { } since && r.LastActivity is { } last && last < since) continue;
                var key = dbPath + "#" + r.Id;
                var mtime = r.LastActivity is { } la ? Time.Iso(la) : Time.Iso(r.RecordModifiedAt);
                var size = r.LastActivity?.Ticks ?? r.RecordModifiedAt.Ticks;
                if (states.TryGetValue(key, out var st) && st.Size == size && st.Mtime == mtime)
                {
                    report.FilesSkippedUnchanged++;
                    continue;
                }
                var upd = new SessionUpdate
                {
                    Tool = Tool,
                    ExternalId = r.Id,
                    Cwd = r.Cwd,
                    Title = r.Title,
                    StartedAt = r.StartedAt is { } sa ? Time.Iso(sa) : mtime,
                    ParentExternalId = r.ParentId,
                    Kind = r.ParentId is not null ? SessionKinds.Subagent : SessionKinds.Human,
                };
                upd.Touch(mtime);
                upd.Event(EventKinds.Other, mtime, r.SourceChannel != "antigravity-active");
                batches.Add(new FileBatch { Path = key, Tool = Tool, Session = upd, Size = size, Mtime = mtime, NewOffset = 0 });
                report.FilesRead++;
            }
            if (batches.Count > 0)
            {
                var (u, comp) = store.Apply(batches, options.SinceUtc);
                report.NewUtterances += u;
                report.NewCompactions += comp;
            }
        }
        catch (ProfileReadException ex)
        {
            if (ex.Reason == "sema-farkli")
            {
                report.Unrecognized++;
                report.Note = "conversation_summaries.db: tanımadığım sürüm, okumadım.";
            }
            else
            {
                report.FileErrors++;
                report.Note = "conversation_summaries.db: o anda kilitli ya da okunamadı.";
            }
        }
        catch (SqliteException ex)
        {
            report.FileErrors++;
            report.Note = ex.SqliteErrorCode is 5 or 6
                ? "conversation_summaries.db: o anda kilitli, okuyamadım."
                : "conversation_summaries.db: okunamadı.";
        }
        report.ElapsedMs = sw.ElapsedMilliseconds;
        return report;
    }
}
