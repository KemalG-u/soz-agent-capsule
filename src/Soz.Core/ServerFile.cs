using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soz.Core;

/// <summary>veri\sunucu.json = {url, pid, startedAt}. Süreç ölmüşse dosya bayattır (çökme sonrası kalır).</summary>
public sealed record ServerFileInfo(string? Url, int Pid, string? StartedAt, bool Alive);

public static class ServerFile
{
    public static string PathFor(string dataDir) => Path.Combine(dataDir, "sunucu.json");

    public static ServerFileInfo? Read(string dataDir)
    {
        var f = PathFor(dataDir);
        if (!File.Exists(f)) return null;
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(f)) as JsonObject;
            if (o is null) return null;
            var pid = o["pid"] is JsonValue v && v.TryGetValue<int>(out var p) ? p : 0;
            return new ServerFileInfo((string?)o["url"], pid, (string?)o["startedAt"], IsSozProcess(pid));
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>pid canlı ve bir Söz süreci mi (pid başka programa geçmiş olabilir).</summary>
    public static bool IsSozProcess(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            var n = p.ProcessName;
            return n.StartsWith("soz", StringComparison.OrdinalIgnoreCase) || n.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
