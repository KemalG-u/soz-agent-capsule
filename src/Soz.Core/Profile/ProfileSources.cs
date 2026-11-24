using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Onboarding;

namespace Soz.Core.Profile;

public sealed record ProfileRoot(string Tool, string Path);

public static class ProfileSources
{
    public static IReadOnlyList<ProfileRoot> Resolve(SozSettings settings, OnboardingRoots roots)
    {
        var result = new List<ProfileRoot>();
        foreach (var (tool, fallback) in new[]
        {
            ("claude", Path.Combine(roots.ClaudeHome, "projects")),
            ("codex", Path.Combine(roots.CodexHome, "sessions")),
            ("hermes", roots.HermesHome),
            ("opencode", roots.OpenCodeData),
        })
        {
            // The same configured history roots and resolver as ingest/doctor. Never executable paths.
            var spec = Agents.AgentRegistry.Specs.First(x => x.Id == tool);
            var key = spec.RootSetting is null ? null : Agents.AgentRegistry.Registry.Get(spec.RootSetting)?.Setting?.LegacyKey;
            var value = key is null ? null : settings.Str(key);
            var path = string.IsNullOrWhiteSpace(value) ? fallback : SourceLocator.For(tool, null, value).Root;
            if (path is not null) result.Add(new(tool, path));
        }

        // Modüler ek kaynaklar: klasörü ya da veritabanı varsa kendiliğinden eklenir (sentetik 4-araç testlerini bozmaz).
        if (Directory.Exists(roots.ClaudeDesktopSessions))
            result.Add(new("claude", roots.ClaudeDesktopSessions));
        if (Directory.Exists(roots.AntigravityHome) || File.Exists(Path.Combine(roots.AntigravityHome, "conversation_summaries.db")))
            result.Add(new("antigravity", roots.AntigravityHome));
        if (!string.Equals(roots.AntigravityCliHome, roots.AntigravityHome, StringComparison.OrdinalIgnoreCase)
            && (Directory.Exists(roots.AntigravityCliHome) || File.Exists(Path.Combine(roots.AntigravityCliHome, "conversation_summaries.db"))))
            result.Add(new("antigravity", roots.AntigravityCliHome));
        if (Directory.Exists(roots.VsCodeStorage))
            result.Add(new("vscode", roots.VsCodeStorage));
        if (Directory.Exists(roots.CursorStorage))
            result.Add(new("cursor", roots.CursorStorage));

        return result;
    }

    public static IEnumerable<string> Files(ProfileRoot root)
    {
        if (root.Tool == "codex")
        {
            var stateDb = FindLatestCodexStateDb(root.Path);
            if (stateDb is not null) yield return stateDb;
            var sessionsDir = ResolveCodexSessionsDir(root.Path);
            if (sessionsDir is not null && Directory.Exists(sessionsDir))
                foreach (var p in Walk(sessionsDir, "rollout-*.jsonl")) yield return p;
            yield break;
        }
        if (root.Tool == "antigravity")
        {
            var db = File.Exists(root.Path) ? root.Path : Path.Combine(root.Path, "conversation_summaries.db");
            if (File.Exists(db)) yield return db;
            yield break;
        }
        if (!Directory.Exists(root.Path)) yield break;
        if (root.Tool == "claude")
        {
            if (IsClaudeDesktopRoot(root.Path))
            {
                foreach (var p in EnumerateClaudeDesktopEntries(root.Path)) yield return p;
            }
            else
            {
                foreach (var p in Walk(root.Path, "*.jsonl")) yield return p;
            }
        }
        else if (root.Tool == "hermes")
        {
            var db = Path.Combine(root.Path, "state.db");
            if (File.Exists(db)) yield return db;
            var profiles = Path.Combine(root.Path, "profiles");
            // Yalnız doğrudan profil klasörleri (profiles\<ad>\state.db). Daha derindeki state.db dosyaları
            // (araştırma, önbellek, sınama düzenekleri) gerçek oturum deposu değildir.
            if (Directory.Exists(profiles))
                foreach (var dir in Directory.EnumerateDirectories(profiles, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
                {
                    var profileDb = Path.Combine(dir, "state.db");
                    if (File.Exists(profileDb)) yield return profileDb;
                }
        }
        else if (root.Tool == "opencode")
        {
            foreach (var p in Directory.EnumerateFiles(root.Path, "opencode*.db", SearchOption.TopDirectoryOnly)) yield return p;
        }
        else if (root.Tool is "vscode" or "cursor")
        {
            foreach (var p in EnumerateWorkspaceJsonFiles(root.Path)) yield return p;
        }
    }

    public static bool IsClaudeDesktopRoot(string path)
        => Path.GetFileName(path.TrimEnd('\\', '/')).Equals("local-agent-mode-sessions", StringComparison.OrdinalIgnoreCase);

    public static string? ResolveCodexHomeDir(string codexRootOrSessions)
    {
        if (string.IsNullOrWhiteSpace(codexRootOrSessions)) return null;
        var trimmed = codexRootOrSessions.TrimEnd('\\', '/');
        return Path.GetFileName(trimmed).Equals("sessions", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(trimmed)?.FullName ?? trimmed
            : trimmed;
    }

    public static string? ResolveCodexSessionsDir(string codexRootOrSessions)
    {
        if (string.IsNullOrWhiteSpace(codexRootOrSessions)) return null;
        var trimmed = codexRootOrSessions.TrimEnd('\\', '/');
        if (Path.GetFileName(trimmed).Equals("sessions", StringComparison.OrdinalIgnoreCase)) return trimmed;
        var sub = Path.Combine(trimmed, "sessions");
        return Directory.Exists(sub) ? sub : trimmed;
    }

    public static string? FindLatestCodexStateDb(string codexRootOrSessions)
    {
        var home = ResolveCodexHomeDir(codexRootOrSessions);
        if (string.IsNullOrEmpty(home) || !Directory.Exists(home)) return null;
        try
        {
            var candidates = new List<(int Version, long Ticks, string Path)>();
            foreach (var f in Directory.EnumerateFiles(home, "state_*.sqlite", SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(f); // state_5
                var numPart = stem.Length > 6 ? stem[6..] : "";
                var ver = int.TryParse(numPart, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
                var ticks = new FileInfo(f).LastWriteTimeUtc.Ticks;
                candidates.Add((ver, ticks, f));
            }
            if (candidates.Count > 0)
                return candidates.OrderByDescending(c => c.Version).ThenByDescending(c => c.Ticks).First().Path;
            var plain = Path.Combine(home, "state.sqlite");
            if (File.Exists(plain)) return plain;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static int CountCompressedCodexFiles(string codexRootOrSessions)
    {
        var sessionsDir = ResolveCodexSessionsDir(codexRootOrSessions);
        if (string.IsNullOrEmpty(sessionsDir) || !Directory.Exists(sessionsDir)) return 0;
        try { return Walk(sessionsDir, "*.jsonl.zst").Count(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static readonly System.Text.RegularExpressions.Regex GuidInName = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Codex'te okunmadan atlanan kayıtlar: özet veritabanında kimliği olmayan sıkıştırılmış rollout dosyaları (.jsonl.zst),
    /// okunan sürümden başka eski özet veritabanları (state_N.sqlite) ve arşivlenmiş oturum dosyaları.
    /// </summary>
    public static (int Compressed, int OlderStateDbs, int Archived) CountSkippedCodex(string codexRootOrSessions, IReadOnlySet<string> knownIds)
    {
        int compressed = 0, older = 0, archived = 0;
        var sessionsDir = ResolveCodexSessionsDir(codexRootOrSessions);
        try
        {
            if (!string.IsNullOrEmpty(sessionsDir) && Directory.Exists(sessionsDir))
                foreach (var f in Walk(sessionsDir, "*.jsonl.zst"))
                    if (GuidInName.Match(Path.GetFileName(f)) is not { Success: true } m || !knownIds.Contains(m.Value)) compressed++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var home = ResolveCodexHomeDir(codexRootOrSessions);
        if (string.IsNullOrEmpty(home) || !Directory.Exists(home)) return (compressed, older, archived);
        try
        {
            var latest = FindLatestCodexStateDb(codexRootOrSessions);
            older = Directory.EnumerateFiles(home, "state_*.sqlite", SearchOption.TopDirectoryOnly)
                .Count(f => !f.Equals(latest, StringComparison.OrdinalIgnoreCase));
            var archive = Path.Combine(home, "archived_sessions");
            if (Directory.Exists(archive))
                foreach (var f in Walk(archive, "*.jsonl*"))
                    if (GuidInName.Match(Path.GetFileName(f)) is not { Success: true } m || !knownIds.Contains(m.Value)) archived++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (compressed, older, archived);
    }

    private static IEnumerable<string> EnumerateWorkspaceJsonFiles(string storageRoot)
    {
        if (!Directory.Exists(storageRoot)) yield break;
        var opt = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        foreach (var dir in Directory.EnumerateDirectories(storageRoot, "*", opt))
        {
            var ws = Path.Combine(dir, "workspace.json");
            if (File.Exists(ws)) yield return ws;
        }
    }

    private static IEnumerable<string> EnumerateClaudeDesktopEntries(string desktopRoot)
    {
        if (!Directory.Exists(desktopRoot)) yield break;
        var opt = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        foreach (var f in Directory.EnumerateFiles(desktopRoot, "*.json*", opt))
        {
            var name = Path.GetFileName(f);
            if (IsSensitiveFileName(name)) continue;
            yield return f;
        }
        foreach (var dir in Directory.EnumerateDirectories(desktopRoot, "*", opt))
        {
            var foundMeta = false;
            foreach (var metaName in new[] { "session.json", "metadata.json", "manifest.json" })
            {
                var mp = Path.Combine(dir, metaName);
                if (File.Exists(mp)) { yield return mp; foundMeta = true; break; }
            }
            if (!foundMeta)
            {
                foreach (var jf in Directory.EnumerateFiles(dir, "*.json*", opt))
                {
                    if (IsSensitiveFileName(Path.GetFileName(jf))) continue;
                    yield return jf;
                    foundMeta = true;
                }
            }
            if (!foundMeta) yield return dir;
        }
    }

    private static bool IsSensitiveFileName(string name)
        => name.Contains("auth", StringComparison.OrdinalIgnoreCase)
           || name.Contains("token", StringComparison.OrdinalIgnoreCase)
           || name.Contains("cookie", StringComparison.OrdinalIgnoreCase)
           || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
           || name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Walk(string path, string pattern)
    {
        if (!Directory.Exists(path)) yield break;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        foreach (var f in Directory.EnumerateFiles(path, pattern, options)) yield return f;
    }

    public static string Fingerprint(string path, string? tool = null)
    {
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            return $"dir:{di.LastWriteTimeUtc.Ticks}";
        }
        string Part(string p) { var f = new FileInfo(p); return f.Exists ? $"{f.LastWriteTimeUtc.Ticks}:{f.Length}" : "-"; }
        var baseFp = Part(path) + ":" + Part(path + "-wal") + ":" + Part(path + "-journal");
        if (tool is "vscode" or "cursor" && Path.GetFileName(path).Equals("workspace.json", StringComparison.OrdinalIgnoreCase))
        {
            var wsDir = Path.GetDirectoryName(path);
            if (wsDir is not null)
            {
                var chatDir = Path.Combine(wsDir, "chatSessions");
                if (Directory.Exists(chatDir))
                {
                    long maxTicks = 0;
                    var count = 0;
                    try
                    {
                        foreach (var cf in Directory.EnumerateFiles(chatDir, "*.json*", SearchOption.TopDirectoryOnly))
                        {
                            count++;
                            var t = File.GetLastWriteTimeUtc(cf).Ticks;
                            if (t > maxTicks) maxTicks = t;
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    return $"{baseFp}:chat:{count}:{maxTicks}";
                }
                var vscdb = Path.Combine(wsDir, "state.vscdb");
                if (File.Exists(vscdb))
                    return $"{baseFp}:vscdb:{File.GetLastWriteTimeUtc(vscdb).Ticks}";
            }
        }
        return baseFp;
    }

    /// <summary>Uzun yol önekini (\\?\) ve eğik çizgi farklarını temizler.</summary>
    public static string? CleanPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var p = value.Trim();
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"//?/", StringComparison.OrdinalIgnoreCase))
            p = p[4..];
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            p = char.ToUpperInvariant(p[0]) + p[1..].Replace('/', '\\');
            if (p.Length == 2) p += @"\";
            else if (p.Length > 3) p = p.TrimEnd('\\');
        }
        return p.Length == 0 ? null : p;
    }

    public static bool IsDriveRoot(string? path)
    {
        var p = CleanPath(path);
        return p is not null && p.Length is 2 or 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p.Length == 2 || p[2] == '\\');
    }

    /// <summary>`file:///d%3A/tools/...` biçimindeki URI'leri gerçek Windows klasör yoluna çevirir.</summary>
    public static string? DecodeFileUri(string? uriText)
    {
        if (string.IsNullOrWhiteSpace(uriText)) return null;
        var raw = uriText.Trim().Trim('"');
        if (!raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;

        string? decoded = null;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            try
            {
                var unescaped = Uri.UnescapeDataString(uri.AbsolutePath);
                if (unescaped.Length >= 3 && unescaped[0] == '/' && char.IsAsciiLetter(unescaped[1]) && unescaped[2] == ':')
                    decoded = unescaped[1..];
                else if (!string.IsNullOrEmpty(uri.Host))
                    decoded = @"\\" + uri.Host + unescaped.Replace('/', '\\');
                else
                    decoded = uri.LocalPath;
            }
            catch (Exception) { }
        }
        if (decoded is null)
        {
            var body = Uri.UnescapeDataString(raw["file://".Length..]);
            if (body.StartsWith('/') && body.Length >= 3 && char.IsAsciiLetter(body[1]) && body[2] == ':')
                decoded = body[1..];
        }
        var clean = CleanPath(decoded);
        return clean is not null && Path.IsPathFullyQualified(clean) ? clean : null;
    }

    private static SqliteConnection OpenReadOnlySqlite(string path)
    {
        if (File.Exists(path + "-wal") && !File.Exists(path + "-shm")) throw new ProfileReadException("wal-var-shm-yok");
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        c.Open();
        c.Exec("PRAGMA query_only=1; PRAGMA busy_timeout=1;");
        return c;
    }

    public static IReadOnlyList<SessionMetadata> ReadCodexStateDb(string path, DateTime now)
    {
        using var c = OpenReadOnlySqlite(path);
        var cols = c.Query("SELECT name FROM pragma_table_info('threads')", r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!cols.Contains("id")) throw new ProfileReadException("sema-farkli");

        var parentByChild = new Dictionary<string, string>(StringComparer.Ordinal);
        var edgeCols = c.Query("SELECT name FROM pragma_table_info('thread_spawn_edges')", r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (edgeCols.Count >= 2)
        {
            var parentCol = new[] { "parent_thread_id", "source_thread_id", "from_thread_id", "parent_id" }.FirstOrDefault(edgeCols.Contains);
            var childCol = new[] { "child_thread_id", "target_thread_id", "to_thread_id", "child_id" }.FirstOrDefault(edgeCols.Contains);
            if (parentCol is not null && childCol is not null)
            {
                using var edgeCmd = c.CreateCommand();
                edgeCmd.CommandText = $"SELECT \"{parentCol}\", \"{childCol}\" FROM thread_spawn_edges";
                using var er = edgeCmd.ExecuteReader();
                while (er.Read())
                {
                    var p = er.IsDBNull(0) ? null : Convert.ToString(er.GetValue(0), CultureInfo.InvariantCulture);
                    var ch = er.IsDBNull(1) ? null : Convert.ToString(er.GetValue(1), CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(p) && !string.IsNullOrWhiteSpace(ch))
                        parentByChild[ch.Trim()] = p.Trim();
                }
            }
        }

        string Col(string name) => cols.Contains(name) ? "t.\"" + name + "\"" : "NULL";
        var modified = File.GetLastWriteTimeUtc(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT t.id,{Col("cwd")},{Col("created_at")},{Col("updated_at")},{Col("git_branch")},{Col("title")},{Col("source")},{Col("parent_thread_id")} FROM threads t";
        using var reader = cmd.ExecuteReader();
        var rows = new List<SessionMetadata>();
        string? Text(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
        DateTime? Stamp(int i) => reader.IsDBNull(i) ? null : reader.GetValue(i) switch
        {
            long integer => FromEpoch(integer),
            double real => FromEpoch(real),
            string text => ParseTimestamp(text),
            _ => null,
        };
        while (reader.Read())
        {
            var id = Text(0);
            if (string.IsNullOrWhiteSpace(id)) continue;
            id = id.Trim();
            var directory = CleanPath(Text(1));
            if (directory is not null && !Path.IsPathFullyQualified(directory)) directory = null;
            var started = Stamp(2);
            var updated = Stamp(3) ?? started;
            var directParent = Text(7);
            var parentId = !string.IsNullOrWhiteSpace(directParent) ? directParent.Trim() : parentByChild.GetValueOrDefault(id);
            rows.Add(new("codex", id, directory, Text(4), started, updated, parentId, path, modified, now, "sqlite",
                SourceChannel: SafeChannel(Text(6)), Title: SessionMetadataReader.SafeTitle(Text(5))));
        }
        return rows;
    }

    public static IReadOnlyList<SessionMetadata> ReadAntigravityDb(string path, DateTime now)
    {
        using var c = OpenReadOnlySqlite(path);
        var cols = c.Query("SELECT name FROM pragma_table_info('conversation_summaries')", r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!cols.Contains("conversation_id")) throw new ProfileReadException("sema-farkli");

        string Col(string name) => cols.Contains(name) ? "s.\"" + name + "\"" : "NULL";
        var createdExpr = cols.Contains("created_time") ? "s.\"created_time\"" : cols.Contains("created_at") ? "s.\"created_at\"" : "NULL";
        var modified = File.GetLastWriteTimeUtc(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT s.conversation_id,{Col("title")},{Col("last_modified_time")},{Col("workspace_uris")},{Col("status")},{Col("parent_conversation_id")},{Col("not_fully_idle")},{createdExpr} FROM conversation_summaries s";
        using var reader = cmd.ExecuteReader();
        string? Text(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
        DateTime? Stamp(int i) => reader.IsDBNull(i) ? null : reader.GetValue(i) switch
        {
            long integer => FromEpoch(integer),
            double real => FromEpoch(real),
            string text => ParseTimestamp(text),
            _ => null,
        };

        var rawRows = new List<(string Id, string? Title, DateTime? Last, DateTime? Started, string? Cwd, bool OnlyDriveRoot, string? ParentId, bool Active)>();
        while (reader.Read())
        {
            var id = Text(0)?.Trim();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var parentRaw = Text(5);
            var parentId = string.IsNullOrWhiteSpace(parentRaw) ? null : parentRaw.Trim();
            var (cwd, onlyDriveRoot) = ParseWorkspaceUris(Text(3));
            var notIdleVal = reader.IsDBNull(6) ? null : reader.GetValue(6);
            var active = notIdleVal switch
            {
                long l => l != 0,
                int iv => iv != 0,
                bool b => b,
                string s => s.Trim() is "1" or "true" or "TRUE",
                _ => false,
            };
            var last = Stamp(2);
            var started = Stamp(7) ?? last;
            rawRows.Add((id, SessionMetadataReader.SafeTitle(Text(1)), last, started, cwd, onlyDriveRoot, parentId, active));
        }

        // Yalnız sürücü kökü (ör. E:\) olan alt oturum varsa ve ata oturum gerçek bir proje klasöründeyse ata klasörü miras al.
        var byId = rawRows.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var rows = new List<SessionMetadata>(rawRows.Count);
        foreach (var r in rawRows)
        {
            var cwd = r.Cwd;
            // Ata zinciri boyunca (en çok 8 basamak) ilk gerçek proje klasörünü miras al
            if (cwd is null || r.OnlyDriveRoot)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { r.Id };
                var current = r;
                for (var step = 0; step < 8 && current.ParentId is not null && byId.TryGetValue(current.ParentId, out var ancestor) && seen.Add(ancestor.Id); step++)
                {
                    if (ancestor.Cwd is not null && !ancestor.OnlyDriveRoot) { cwd = ancestor.Cwd; break; }
                    current = ancestor;
                }
            }
            rows.Add(new("antigravity", r.Id, cwd, null, r.Started, r.Last, r.ParentId, path, modified, now, "sqlite",
                SourceChannel: r.Active ? "antigravity-active" : "antigravity", Title: r.Title));
        }
        return rows;
    }

    private static (string? Path, bool OnlyDriveRoot) ParseWorkspaceUris(string? jsonOrUri)
    {
        if (string.IsNullOrWhiteSpace(jsonOrUri)) return (null, false);
        var candidates = new List<string>();
        var trimmed = jsonOrUri.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String && DecodeFileUri(el.GetString()) is { } p)
                            candidates.Add(p);
                    }
                }
            }
            catch (JsonException) { }
        }
        else if (DecodeFileUri(trimmed) is { } single)
        {
            candidates.Add(single);
        }

        if (candidates.Count == 0) return (null, false);
        var nonRoot = candidates.FirstOrDefault(p => !IsDriveRoot(p));
        return nonRoot is not null ? (nonRoot, false) : (candidates[0], true);
    }

    /// <summary>VS Code ve Cursor workspaceStorage\*\workspace.json + chatSessions üst bilgisi (konuşma içeriği okunmaz!).</summary>
    public static IReadOnlyList<SessionMetadata> ReadWorkspaceStorage(string tool, string workspaceJsonPath, DateTime now)
    {
        string? folderUri = null;
        using (var fs = new FileStream(workspaceJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
        {
            if (fs.Length > 256 * 1024) throw new ProfileReadException("bozuk");
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("folder", out var fEl) && fEl.ValueKind == JsonValueKind.String)
                    folderUri = fEl.GetString();
                else if (doc.RootElement.TryGetProperty("workspace", out var wEl) && wEl.ValueKind == JsonValueKind.String)
                    folderUri = wEl.GetString();
            }
        }
        var folder = DecodeFileUri(folderUri);
        if (folder is null) return [];

        var wsInfo = new FileInfo(workspaceJsonPath);
        var wsDir = wsInfo.DirectoryName!;
        var chatDir = Path.Combine(wsDir, "chatSessions");
        var rows = new List<SessionMetadata>();
        if (Directory.Exists(chatDir))
        {
            foreach (var cf in Directory.EnumerateFiles(chatDir, "*.json*", SearchOption.TopDirectoryOnly))
            {
                var fi = new FileInfo(cf);
                var id = Path.GetFileNameWithoutExtension(fi.Name);
                if (id.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    id = Path.GetFileNameWithoutExtension(id);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var created = fi.CreationTimeUtc.Year >= 1970 ? fi.CreationTimeUtc : fi.LastWriteTimeUtc;
                var last = fi.LastWriteTimeUtc.Year >= 1970 ? fi.LastWriteTimeUtc : created;
                if (created > last) created = last;
                rows.Add(new(tool, id, folder, null, created, last, null, workspaceJsonPath, last, now, "workspace-chat", SourceChannel: tool));
            }
        }
        if (rows.Count == 0)
        {
            var last = wsInfo.LastWriteTimeUtc;
            var dirInfo = new DirectoryInfo(wsDir);
            if (dirInfo.LastWriteTimeUtc > last) last = dirInfo.LastWriteTimeUtc;
            var vscdb = new FileInfo(Path.Combine(wsDir, "state.vscdb"));
            if (vscdb.Exists && vscdb.LastWriteTimeUtc > last) last = vscdb.LastWriteTimeUtc;
            var created = wsInfo.CreationTimeUtc.Year >= 1970 && wsInfo.CreationTimeUtc <= last ? wsInfo.CreationTimeUtc : last;
            rows.Add(new(tool, "ws-" + dirInfo.Name, folder, null, created, last, null, workspaceJsonPath, last, now, "workspace", SourceChannel: tool));
        }
        return rows;
    }

    /// <summary>Claude Desktop local-agent-mode-sessions üst bilgisi (yalnız sınırlı alanlar okunur, konuşma içeriği alınmaz).</summary>
    public static IReadOnlyList<SessionMetadata> ReadClaudeDesktopEntry(string path, DateTime now)
    {
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            var last = di.LastWriteTimeUtc;
            var created = di.CreationTimeUtc.Year >= 1970 && di.CreationTimeUtc <= last ? di.CreationTimeUtc : last;
            return [new("claude", "desktop-" + di.Name, null, null, created, last, null, path, last, now, "desktop", SourceChannel: "claude-desktop")];
        }

        var fi = new FileInfo(path);
        if (!fi.Exists) return [];
        string? id = null, cwd = null, branch = null, title = null;
        DateTime? started = null, updated = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            var bufLen = (int)Math.Min(fs.Length, 32768);
            var buf = new byte[bufLen];
            var n = fs.Read(buf, 0, bufLen);
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, n).TrimStart();
            if (text.StartsWith('{'))
            {
                var lineEnd = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? text.IndexOf('\n') : -1;
                var slice = lineEnd > 0 ? text[..lineEnd] : text;
                using var doc = JsonDocument.Parse(slice);
                var r = doc.RootElement;
                if (r.ValueKind == JsonValueKind.Object)
                {
                    id = Prop(r, "sessionId") ?? Prop(r, "id") ?? Prop(r, "uuid");
                    var rawCwd = Prop(r, "cwd") ?? Prop(r, "workingDirectory") ?? Prop(r, "projectPath") ?? Prop(r, "workspacePath") ?? Prop(r, "folder");
                    cwd = rawCwd is not null && rawCwd.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                        ? DecodeFileUri(rawCwd)
                        : CleanPath(rawCwd);
                    if (cwd is not null && !Path.IsPathFullyQualified(cwd)) cwd = null;
                    branch = Prop(r, "gitBranch") ?? Prop(r, "git_branch");
                    title = SessionMetadataReader.SafeTitle(Prop(r, "title") ?? Prop(r, "name"));
                    started = ParseTimestamp(Prop(r, "createdAt") ?? Prop(r, "created_at") ?? Prop(r, "startedAt") ?? Prop(r, "timestamp"));
                    updated = ParseTimestamp(Prop(r, "updatedAt") ?? Prop(r, "updated_at") ?? Prop(r, "lastActivityAt"));
                }
            }
        }
        catch (JsonException) { }

        id = string.IsNullOrWhiteSpace(id) ? "desktop-" + Path.GetFileNameWithoutExtension(fi.Name) : id.Trim();
        var mod = fi.LastWriteTimeUtc;
        var startTs = started ?? (fi.CreationTimeUtc.Year >= 1970 && fi.CreationTimeUtc <= mod ? fi.CreationTimeUtc : mod);
        var lastTs = updated ?? mod;
        return [new("claude", id, cwd, branch, startTs, lastTs, null, path, mod, now, "desktop", SourceChannel: "claude-desktop", Title: title)];

        static string? Prop(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static IReadOnlyList<SessionMetadata> ReadDatabase(string tool, string path, DateTime now)
    {
        if (tool == "codex") return ReadCodexStateDb(path, now);
        if (tool == "antigravity") return ReadAntigravityDb(path, now);

        using var c = OpenReadOnlySqlite(path);
        var table = tool == "hermes" ? "sessions" : "session";
        var cols = c.Query($"SELECT name FROM pragma_table_info('{table}')", r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!cols.Contains("id")) throw new ProfileReadException("sema-farkli");
        string Col(string name) => cols.Contains(name) ? "s.\"" + name + "\"" : "NULL";
        var cwd = tool == "hermes" ? "cwd" : "directory";
        var parent = tool == "hermes" ? "parent_session_id" : "parent_id";
        var start = tool == "hermes" ? "started_at" : "time_created";
        var activity = tool == "hermes" ? "last_activity_at" : "time_updated";
        var messageTime = "NULL";
        var messageJoin = "";
        if (tool == "hermes")
        {
            var messages = c.Query("SELECT name FROM pragma_table_info('messages')", r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Only the aggregate timestamp is read. Message text never enters the result set.
            if (messages.Contains("session_id") && messages.Contains("timestamp"))
            {
                messageTime = "mt.last_timestamp";
                messageJoin = " LEFT JOIN (SELECT session_id,MAX(timestamp) AS last_timestamp FROM messages GROUP BY session_id) mt ON mt.session_id=s.id";
            }
        }
        var modified = File.GetLastWriteTimeUtc(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT s.id,{Col(cwd)},{Col("git_repo_root")},{Col(parent)},{Col(start)},{Col("ended_at")},{Col(activity)},{Col("git_branch")},{messageTime},{Col("source")},{Col("title")} FROM {table} s{messageJoin}";
        using var reader = cmd.ExecuteReader();
        var rows = new List<SessionMetadata>();
        // Never call ToString() on a SQLite value: a REAL becomes "1763810000,123" under tr-TR. Read by storage type instead.
        string? Text(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
        DateTime? Stamp(int i) => reader.IsDBNull(i) ? null : reader.GetValue(i) switch
        {
            long integer => FromEpoch(integer),
            double real => FromEpoch(real),
            string text => ParseTimestamp(text),
            _ => null,
        };
        while (reader.Read())
        {
            var directory = CleanPath(Text(1));
            if (directory is not null && !System.IO.Path.IsPathFullyQualified(directory)) directory = null;
            var repoRoot = CleanPath(Text(2));
            if (repoRoot is not null && !System.IO.Path.IsPathFullyQualified(repoRoot)) repoRoot = null;
            var rawParent = Text(3);
            var parentId = string.IsNullOrWhiteSpace(rawParent) ? null : rawParent.Trim();
            var last = tool == "hermes" ? Stamp(5) ?? Stamp(8) ?? Stamp(4) : Stamp(6) ?? Stamp(4);
            rows.Add(new(tool, Text(0)!, directory, Text(7), Stamp(4), last,
                parentId, path, modified, now, "sqlite", GitRepoRoot: repoRoot, SourceChannel: SafeChannel(Text(9)), Title: SessionMetadataReader.SafeTitle(Text(10))));
        }
        return rows;
    }

    private static string? SafeChannel(string? source) => !string.IsNullOrWhiteSpace(source) && source.Length <= 64
        && source.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? source.ToLowerInvariant() : null;

    /// <summary>Kayıt geçerli bir JSON ama beklenen zorunlu alan (klasör) hiç yok: yardımcının kayıt biçimi değişmiş olabilir.</summary>
    public const string ReasonUnrecognizedFormat = "bicim-taninmadi";

    public static string FailureReason(Exception error) => error switch
    {
        ProfileReadException e => e.Reason,
        SqliteException e when e.SqliteErrorCode is 5 or 6 or 14 => "kilitli",
        SqliteException e when e.SqliteErrorCode == 1 => "sema-farkli",
        UnauthorizedAccessException => "kilitli",
        System.Security.SecurityException => "kilitli",
        IOException e when (e.HResult & 0xffff) is 32 or 33 => "kilitli",
        // "Bozuk" yalnız gerçekten ayrıştırılamayan dosyaya verilir; belirsiz okuma hataları dosyayı suçlamaz (D6-17).
        SqliteException e when e.SqliteErrorCode is 11 or 26 => "bozuk",
        System.Text.Json.JsonException or InvalidDataException or FormatException => "bozuk",
        _ => "okunamadi",
    };

    /// <summary>Epoch seconds, or milliseconds when 12+ digits. Zero, negative, NaN, infinite and absurdly large values mean "no time".</summary>
    public static DateTime? FromEpoch(double number)
    {
        if (!double.IsFinite(number) || number <= 0) return null;
        if (number >= 100_000_000_000) number /= 1000;
        return number < 253402300799 ? DateTime.UnixEpoch.AddSeconds(number) : null;
    }

    /// <summary>All parsing is invariant. Unknown or unparseable text is null, never DateTime.MinValue.</summary>
    public static DateTime? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        var comma = value.IndexOf(',');
        if (comma >= 9 && value.IndexOf(',', comma + 1) < 0 && !value.Contains('.')
            && value[..comma].All(char.IsAsciiDigit) && value[(comma + 1)..].All(char.IsAsciiDigit))
            value = value[..comma] + "." + value[(comma + 1)..];
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return FromEpoch(number);
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            && d.Year >= 1970 ? d : null;
    }
}

public sealed class ProfileReadException(string reason) : IOException(reason)
{
    public string Reason { get; } = reason;
}
