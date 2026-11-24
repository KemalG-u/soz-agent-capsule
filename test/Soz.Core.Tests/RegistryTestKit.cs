using System.Text.Json;
using System.Text.Json.Nodes;
using Soz.Core.Registry;

namespace Soz.Core.Tests;

/// <summary>Kayıt defteri testlerinin ortak araçları: geçici kişi klasörü ve küçük JSON yardımcıları.</summary>
internal sealed class RegistryUserDir : IDisposable
{
    public string Path { get; }

    public RegistryUserDir()
    {
        Path = System.IO.Path.Combine(TestEnv.RepoRoot, "_gecici", "testler", "d1b-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Write(string name, string text)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class RegistryTestKit
{
    public static string ExamplesDir => System.IO.Path.Combine(TestEnv.RepoRoot, "test", "kayit-ornekleri");

    public static RegistrySource Source(string name, string text) => RegistrySource.FromText(name, "file:" + name, text);

    public static RegistryLoadResult LoadUser(string text, IRegistryApprovals? approvals = null, string name = "kullanici.soz.json")
        => RegistryLoader.Load(Array.Empty<RegistrySource>(), new[] { Source(name, text) }, approvals);

    /// <summary>Tek ayar kaydı içeren kişi dosyası metni; over, gövde alanlarını ezer.</summary>
    public static string SettingFile(string id, string? bodyPatchJson = null, string? entryPatchJson = null)
    {
        var body = JsonNode.Parse("""{"schema":{"type":"boolean"},"default":false,"class":"K1","group":"genel","card":"Örnek","order":1,"control":"toggle"}""")!.AsObject();
        if (bodyPatchJson is not null) foreach (var (k, v) in JsonNode.Parse(bodyPatchJson)!.AsObject().ToList()) body[k] = v?.DeepClone();
        var entry = new JsonObject { ["id"] = id, ["kind"] = "setting", ["owner"] = "user", ["title"] = "Örnek", ["body"] = body };
        if (entryPatchJson is not null) foreach (var (k, v) in JsonNode.Parse(entryPatchJson)!.AsObject().ToList()) entry[k] = v?.DeepClone();
        return new JsonObject { ["schemaVersion"] = 1, ["entries"] = new JsonArray(entry) }.ToJsonString();
    }

    public static string EmbeddedText(string fileName)
        => System.Text.Encoding.UTF8.GetString(RegistryLoader.EmbeddedSources().First(s => s.Name == fileName).Bytes).TrimStart('﻿');

    public static JsonNode? J(string json) => JsonNode.Parse(json);

    public static string Q(string s) => JsonSerializer.Serialize(s);
}
