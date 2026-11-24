using System.Text;
using System.Text.Json;

namespace Soz.Core.Profile;

/// <summary>Only whitelisted metadata. No conversation, command, title or tool arguments.</summary>
public sealed record SessionMetadata(string Tool, string Id, string? Cwd, string? GitBranch,
    DateTime? StartedAt, DateTime? LastActivity, string? ParentId, string RecordPath,
    DateTime RecordModifiedAt, DateTime ObservedAt, string SourceType, string Kind = "gozlem", string? GitRepoRoot = null,
    string? SourceChannel = null, string? Title = null);

public static class SessionMetadataReader
{
    public const int MaxBytes = 131072;
    public const int MaxLines = 64;
    public const int FallbackMaxBytes = 4 * 1024 * 1024;

    /// <summary>Reads a bounded prefix, never constructs strings for message fields. mtime is activity evidence.</summary>
    public static SessionMetadata? Read(string tool, string path, DateTime observedAt)
    {
        var info = new FileInfo(path);
        var modified = info.LastWriteTimeUtc;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        using var buffered = new BufferedStream(stream, 16384);
        var prefix = new MetadataPrefix(buffered, FallbackMaxBytes);
        string? id = null, cwd = null, branch = null, parent = null, agent = null, title = null;
        DateTime? started = null;
        var valid = false;
        bool cwdKeySeen = false, turnSeen = false, untypedIdSeen = false, codexMetaSeen = false;
        for (var line = 0; !prefix.End; line++)
        {
            if (cwd is not null && (line >= MaxLines || prefix.Consumed >= MaxBytes)) break;
            try
            {
                var fields = prefix.Read(tool == "codex");
                if (fields is null) break;
                valid = true;
                if (fields.ContainsKey("cwd") || fields.ContainsKey("_cwdKey")) cwdKeySeen = true;
                if (fields.Count > 0) codexMetaSeen = true;
                if (fields.TryGetValue("_type", out var recordType))
                { if (recordType is "user" or "assistant" or "system") turnSeen = true; }
                else if (fields.ContainsKey("sessionId") || fields.ContainsKey("id")) untypedIdSeen = true;
                if (fields.TryGetValue("cwd", out var c) && ProfileSources.CleanPath(c) is { } cleanCwd && Path.IsPathFullyQualified(cleanCwd)) cwd ??= cleanCwd;
                if (fields.TryGetValue("sessionId", out var s) || fields.TryGetValue("id", out s)) id ??= s;
                if (fields.TryGetValue("gitBranch", out var b)) branch ??= b;
                if (fields.TryGetValue("parent_session_id", out var p) || fields.TryGetValue("parentSessionId", out p)) parent ??= p;
                if (fields.TryGetValue("agentId", out var a)) agent ??= a;
                title = TitleFrom(fields) ?? title;
                // Invariant culture, zone-less text is UTC; unparseable text leaves the time unknown (never 0001).
                if (started is null && fields.TryGetValue("timestamp", out var t)) started = ProfileSources.ParseTimestamp(t);
            }
            catch (JsonException) { prefix.NextLine(); /* no raw content in logs */ }
        }
        if (!valid) return null;
        // Zorunlu alan (klasör) hiç görülmedi ama kayıt oturum gibi duruyorsa biçim değişmiş demektir: sessizce oturum sayılmaz.
        if (!cwdKeySeen && cwd is null)
        {
            if (tool == "claude" && (turnSeen || untypedIdSeen)) throw new ProfileReadException(ProfileSources.ReasonUnrecognizedFormat);
            if (tool == "codex" && (!codexMetaSeen || id is null)) throw new ProfileReadException(ProfileSources.ReasonUnrecognizedFormat);
        }
        // Renames can be appended. Read a bounded tail, skip a partial first line without allocating it.
        if(info.Length>MaxBytes)
        {
            using var tail=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,16384);
            tail.Seek(Math.Max(0,info.Length-MaxBytes),SeekOrigin.Begin);
            using var buffer=new BufferedStream(tail,16384);
            var projection=new MetadataPrefix(buffer,MaxBytes);
            projection.NextLine();
            while(!projection.End)
            {
                try{var fields=projection.Read(tool=="codex");if(fields is null)break;title=TitleFrom(fields)??title;}
                catch(JsonException){projection.NextLine();}
            }
        }
        if (tool == "claude" && new DirectoryInfo(Path.GetDirectoryName(path)!).Name.Equals("subagents", StringComparison.OrdinalIgnoreCase))
        {
            parent ??= Directory.GetParent(Path.GetDirectoryName(path)!)?.Name;
            id = agent ?? Path.GetFileNameWithoutExtension(path);
        }
        id ??= Path.GetFileNameWithoutExtension(path);
        return new(tool, id, cwd, branch, started, modified, parent, Path.GetFullPath(path), modified, observedAt, "jsonl",Title:title);
    }

    private static string? TitleFrom(Dictionary<string,string> fields) =>
        SafeTitle(fields.GetValueOrDefault("customTitle")??fields.GetValueOrDefault("title"));
    public static string? SafeTitle(string? title) => string.IsNullOrWhiteSpace(title)?null:
        string.Concat(title.Trim().EnumerateRunes().Take(120).Select(r=>Rune.IsControl(r)?" ":r.ToString()));

}
