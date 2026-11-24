using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Soz.Core.Data;
using Soz.Core.Jobs;

namespace Soz.Core.Proof;

/// <summary>Ölçüt kayıtları. Kural: kullanıcı açmadan (enabled=1) hiçbir ölçüt çalışmaz.</summary>
public static class CheckStore
{
    public const int DefaultTimeoutS = 120;
    /// <summary>Ajanın yazdığı denemeden Söz'ün bulup önerdiği kapalı ölçütün kökeni (kişi kabul edene kadar çalışmaz).</summary>
    public const string AgentTestOrigin = "agent-test";

    private const string Select = "SELECT id, project_id, job_id, kind, label, spec_json, timeout_s, enabled, origin, created_at, uid FROM checks";

    /// <summary>uid sütunu (10) seçilmemişse (eski seçim listeleri) null kalır.</summary>
    public static CheckDto Map(SqliteDataReader r) => new(r.GetInt64(0), r.Long(1), r.LongN(2), r.Str(3) ?? "command", r.Str(4) ?? "",
        ParseSpec(r.Str(5)), (int)(r.LongN(6) ?? DefaultTimeoutS), r.Long(7) == 1, r.Str(8) ?? "user", r.Str(9), r.FieldCount > 10 ? r.Str(10) : null);

    public static JsonObject ParseSpec(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); } catch (JsonException) { return new JsonObject(); }
    }

    public static CheckDto? Get(SqliteConnection c, long id) => c.Query(Select + " WHERE id=@id", Map, null, ("@id", id)).FirstOrDefault();

    /// <summary>jobId null → projenin kalıcı ölçütleri; dolu → kalıcılar + o işin ölçütleri.</summary>
    public static List<CheckDto> List(SqliteConnection c, long projectId, long? jobId = null)
        => c.Query(Select + " WHERE project_id=@p AND (job_id IS NULL OR (@j IS NOT NULL AND job_id=@j)) ORDER BY job_id IS NOT NULL, id", Map, null, ("@p", projectId), ("@j", jobId));

    /// <summary>Kapının çalıştıracağı ölçütler: YALNIZ enabled=1; kalıcılar + işin kendi ölçütleri.</summary>
    public static List<CheckDto> EnabledFor(SqliteConnection c, long projectId, long? jobId)
        => List(c, projectId, jobId).Where(x => x.Enabled).ToList();

    /// <summary>
    /// Bir işe uygulanacak (açık) ölçüt tanımlarının özeti: id, tür, etiket, süre, tanım. İş verilirken kaydedilir;
    /// hüküm anında farklıysa (ölçüt değişti, kapandı, silindi ya da eklendi) hüküm "geçti" olamaz.
    /// </summary>
    public static string DefinitionHash(IEnumerable<CheckDto> enabledChecks)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var x in enabledChecks.OrderBy(x => x.Id))
            sb.Append(x.Id).Append('\u001f').Append(x.Kind).Append('\u001f').Append(x.Label).Append('\u001f').Append(x.TimeoutS).Append('\u001f').Append(x.Spec.ToJsonString()).Append('\n');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    public static string DefinitionHash(SqliteConnection c, long projectId, long? jobId) => DefinitionHash(EnabledFor(c, projectId, jobId));

    /// <summary>Ölçüt tanımı geçerli mi? Tür ölçücü kaydından bulunur; her ölçücü kendi tanımını doğrular (eski dört türün iletileri aynen).</summary>
    public static string? Validate(string kind, JsonObject spec)
    {
        var m = MeasurerRegistry.Find(kind);
        if (m is null) return $"Bilinmeyen kontrol türü. Şunlardan biri olmalı: {string.Join(", ", CheckKinds.All)}.";
        return m.Validate(spec);
    }

    public static long Insert(SqliteConnection c, SqliteTransaction? tx, long projectId, long? jobId, string kind, string label, JsonObject spec, int timeoutS, bool enabled, string origin)
    {
        c.Exec("""
            INSERT INTO checks(project_id, job_id, kind, label, spec_json, timeout_s, enabled, origin, created_at, uid)
            VALUES(@p,@j,@k,@l,@s,@t,@e,@o,@n,@u)
            """, tx, ("@u", Uid.New()), ("@p", projectId), ("@j", jobId), ("@k", kind), ("@l", label.Trim()), ("@s", spec.ToJsonString()), ("@t", Math.Clamp(timeoutS, 1, 3600)),
            ("@e", enabled ? 1 : 0), ("@o", origin), ("@n", Time.NowIso()));
        return c.ScalarLong("SELECT last_insert_rowid()", tx);
    }

    // ---------- öneri ----------
    public sealed record Suggestion(string Label, string Cmd);

    /// <summary>Proje kökünü tarar; HİÇBİR KOMUT ÇALIŞTIRMAZ (yalnız dosya okur).</summary>
    public static List<Suggestion> Scan(string root)
    {
        var list = new List<Suggestion>();
        if (!Directory.Exists(root)) return list;
        string F(string n) => Path.Combine(root, n);

        // package.json scripts
        if (File.Exists(F("package.json")))
        {
            try
            {
                var pkg = JsonNode.Parse(File.ReadAllText(F("package.json"))) as JsonObject;
                if (pkg?["scripts"] is JsonObject scripts)
                {
                    foreach (var (name, label) in new[] { ("test", "Testler geçsin"), ("build", "Derleme başarılı olsun"), ("lint", "Lint temiz olsun"), ("typecheck", "Tür denetimi geçsin") })
                    {
                        if (scripts[name] is not JsonValue v || !v.TryGetValue<string>(out var body) || string.IsNullOrWhiteSpace(body)) continue;
                        if (name == "test" && body.Contains("no test specified", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(new Suggestion(label, name == "test" ? "npm test" : $"npm run {name}"));
                    }
                }
            }
            catch (Exception e) when (e is JsonException or IOException) { /* okunamayan package.json: öneri yok */ }
        }

        // .NET
        var sln = SafeFiles(root, "*.sln").Concat(SafeFiles(root, "*.slnx")).FirstOrDefault();
        var csproj = SafeFiles(root, "*.csproj").FirstOrDefault();
        if (sln is not null || csproj is not null)
        {
            var target = Path.GetFileName(sln ?? csproj!);
            list.Add(new Suggestion("dotnet derlemesi başarılı olsun", $"dotnet build \"{target}\""));
            list.Add(new Suggestion("dotnet testleri geçsin", $"dotnet test \"{target}\""));
        }

        // Python
        if (File.Exists(F("pytest.ini")) || File.Exists(F("pyproject.toml")))
            list.Add(new Suggestion("pytest testleri geçsin", "python -m pytest -q"));

        if (File.Exists(F("Cargo.toml"))) list.Add(new Suggestion("cargo testleri geçsin", "cargo test"));
        if (File.Exists(F("go.mod"))) list.Add(new Suggestion("go testleri geçsin", "go test ./..."));
        if (File.Exists(F("Makefile")) && System.Text.RegularExpressions.Regex.IsMatch(SafeRead(F("Makefile")), @"(?m)^test\s*:"))
            list.Add(new Suggestion("make test geçsin", "make test"));
        return list;
    }

    private static IEnumerable<string> SafeFiles(string root, string pattern)
    {
        try { return Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Önerileri enabled=0, origin='auto' yazar; aynı komut zaten varsa eklemez.</summary>
    public static SuggestResult Suggest(SqliteConnection c, long projectId, string root)
    {
        var existing = List(c, projectId).Where(x => x.Kind == CheckKinds.Command && x.JobId is null)
            .Select(x => x.Spec["cmd"] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var s in Scan(root))
        {
            if (existing.Contains(s.Cmd)) continue;
            Insert(c, null, projectId, null, CheckKinds.Command, s.Label, new JsonObject { ["cmd"] = s.Cmd, ["expectExit"] = 0 }, DefaultTimeoutS, enabled: false, origin: "auto");
            existing.Add(s.Cmd);
            added++;
        }
        return new SuggestResult(added, List(c, projectId));
    }
}
