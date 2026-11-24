namespace Soz.Shell;

/// <summary>veri\soz-shell.log; hata yutmaz ama kabuğu da düşürmez.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, "soz-shell.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > 512 * 1024) File.Delete(_path);
        }
        catch { _path = null; }
    }

    public static void Write(string message)
    {
        var p = _path;
        if (p == null) return;
        try
        {
            lock (Gate) File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* günlük yazılamıyorsa sessiz */ }
    }

    public static void Error(string where, Exception ex) => Write($"[HATA] {where}: {ex}");
}
