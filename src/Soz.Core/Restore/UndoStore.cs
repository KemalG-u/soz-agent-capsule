using System.Text.Json;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Restore;

/// <summary>Geri alma kaydı (soz.db, undo tablosu). Status: previewed | applied | failed | reverted. Mode: all | last | partial.</summary>
public sealed record UndoDto(string Uid, long JobId, long? RunId, string TargetSnapshotUid, string? PreSnapshotUid, string? PostSnapshotUid, string Mode, string Status,
    string StartedAt, string? FinishedAt, int? FilesRestored, int? FilesRemoved, int? FilesSkipped, List<Conflict>? Conflicts, string? Error);

public static class UndoStore
{
    private const string Cols = "uid, job_id, run_id, target_snapshot_uid, pre_snapshot_uid, post_snapshot_uid, mode, status, started_at, finished_at, files_restored, files_removed, files_skipped, conflicts_json, error";

    private static UndoDto Map(SqliteDataReader r) => new(r.GetString(0), r.GetInt64(1), r.LongN(2), r.GetString(3), r.Str(4), r.Str(5), r.GetString(6), r.GetString(7), r.GetString(8), r.Str(9),
        r.LongN(10) is { } a ? (int)a : null, r.LongN(11) is { } b ? (int)b : null, r.LongN(12) is { } c ? (int)c : null, ParseConflicts(r.Str(13)), r.Str(14));

    private static List<Conflict>? ParseConflicts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<Conflict>>(json, Json); } catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Insert(SqliteConnection c, long jobId, long? runId, string targetSnapshotUid, string mode, string status)
    {
        var uid = Uid.New();
        c.Exec("INSERT INTO undo(uid, job_id, run_id, target_snapshot_uid, mode, status, started_at) VALUES(@u,@j,@r,@t,@m,@s,@n)", null,
            ("@u", uid), ("@j", jobId), ("@r", runId), ("@t", targetSnapshotUid), ("@m", mode), ("@s", status), ("@n", Time.NowIso()));
        return uid;
    }

    public static UndoDto? Get(SqliteConnection c, string uid) => c.Query($"SELECT {Cols} FROM undo WHERE uid=@u", Map, null, ("@u", uid)).FirstOrDefault();

    public static List<UndoDto> ForJob(SqliteConnection c, long jobId) => c.Query($"SELECT {Cols} FROM undo WHERE job_id=@j ORDER BY id DESC", Map, null, ("@j", jobId));

    /// <summary>Sonucu yazar. Çakışma listesi en çok 500 satır tutulur (yol ve neden; dosya içeriği hiçbir zaman yazılmaz).</summary>
    public static void Finish(SqliteConnection c, string uid, string status, RestoreOutcome? o, string? error)
    {
        var conflicts = o is null ? null : JsonSerializer.Serialize(o.Conflicts.Concat(o.Unrestorable).Take(500).ToList(), Json);
        c.Exec("""
            UPDATE undo SET status=@s, finished_at=@n, pre_snapshot_uid=COALESCE(@p,pre_snapshot_uid), post_snapshot_uid=COALESCE(@q,post_snapshot_uid),
              files_restored=@fr, files_removed=@fd, files_skipped=@fs, conflicts_json=@cj, error=@e WHERE uid=@u
            """, null, ("@s", status), ("@n", Time.NowIso()), ("@p", o?.PreUid), ("@q", o?.PostUid),
            ("@fr", o is null ? null : o.Restored + o.Renamed + o.DirsCreated), ("@fd", o is null ? null : o.Removed + o.DirsRemoved),
            ("@fs", o is null ? null : o.Conflicts.Count + o.Unrestorable.Count), ("@cj", conflicts), ("@e", error), ("@u", uid));
    }

    public static void SetStatus(SqliteConnection c, string uid, string status) => c.Exec("UPDATE undo SET status=@s WHERE uid=@u", null, ("@s", status), ("@u", uid));
}
