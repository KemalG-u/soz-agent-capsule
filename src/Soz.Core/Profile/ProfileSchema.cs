using Microsoft.Data.Sqlite;
using Soz.Core.Data;

namespace Soz.Core.Profile;

public static class ProfileSchema
{
    public static void ApplyV16(SqliteConnection c, SqliteTransaction tx)
    {
        var columns=c.Query("SELECT name FROM pragma_table_info('profile_session')",r=>r.GetString(0),tx).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var name in new[]{"source_channel","project_path","unassigned_reason","title"})
            if(!columns.Contains(name))c.Exec($"ALTER TABLE profile_session ADD COLUMN {name} TEXT",tx);
        c.Exec("CREATE INDEX IF NOT EXISTS ix_profile_session_project ON profile_session(project_path,present,last_activity); UPDATE profile_file SET ok=0;",tx);
    }

    public static void Apply(SqliteConnection c, SqliteTransaction tx) => c.Exec("""
        CREATE TABLE IF NOT EXISTS profile_session(
          tool TEXT NOT NULL, session_id TEXT NOT NULL, cwd TEXT, git_branch TEXT, git_repo_root TEXT,
          started_at TEXT, last_activity TEXT NOT NULL, parent_id TEXT, record_path TEXT NOT NULL,
          record_modified_at TEXT NOT NULL, observed_at TEXT NOT NULL, source_type TEXT NOT NULL,
          kind TEXT NOT NULL DEFAULT 'gozlem', present INTEGER NOT NULL DEFAULT 1,
          PRIMARY KEY(tool,record_path,session_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_profile_session_cwd ON profile_session(cwd);
        CREATE TABLE IF NOT EXISTS profile_file(
          tool TEXT NOT NULL, path TEXT NOT NULL, fingerprint TEXT NOT NULL, ok INTEGER NOT NULL,
          PRIMARY KEY(tool,path)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS profile_snapshot(id INTEGER PRIMARY KEY CHECK(id=1), body TEXT NOT NULL);
        """, tx);
}
