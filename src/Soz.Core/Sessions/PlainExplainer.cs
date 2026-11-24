using System.Diagnostics;
using System.Text;

namespace Soz.Core.Sessions;

public sealed record ExplainResult(bool Ok, string Text, bool Disabled = false);

/// <summary>
/// "Sade anlat" (sözleşme 10): kullanıcının kendi aboneliğindeki modelle, `claude -p --model haiku` çocuk süreci.
/// Çocuk ortamından API anahtarı ve faturayı başka yere yönlendiren değişkenler silinir. Varsayılan KAPALI.
/// </summary>
public sealed class PlainExplainer
{
    public static readonly string[] StrippedEnvExact =
    {
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_VERTEX_PROJECT_ID",
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "AWS_BEARER_TOKEN_BEDROCK", "CLAUDE_API_KEY",
    };

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public static bool ShouldStrip(string name)
        => name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
           || StrippedEnvExact.Contains(name, StringComparer.OrdinalIgnoreCase);

    public async Task<ExplainResult> ExplainAsync(SozSettings settings, string? assistantText, CancellationToken ct = default)
    {
        var lang = settings.Language;
        if (!settings.ExplainEnabled) return new ExplainResult(false, Strings.Get(lang, "explain.disabled"), Disabled: true);
        if (string.IsNullOrWhiteSpace(assistantText)) return new ExplainResult(false, Strings.Get(lang, "explain.nothing"));
        var prompt = Strings.Get(lang, "explain.prompt") + "\n\n---\n" + assistantText;
        try
        {
            var psi = BuildStartInfo(settings.ExplainCommand);
            using var p = new Process { StartInfo = psi };
            p.Start();
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new ExplainResult(false, Strings.Get(lang, "explain.timeout"));
            }
            var output = (await stdoutTask).Trim();
            if (p.ExitCode != 0 || output.Length == 0)
            {
                var err = (await stderrTask).Trim();
                return new ExplainResult(false, Strings.Get(lang, "explain.failed").Replace("{error}", Truncate(err.Length > 0 ? err : $"çıkış kodu {p.ExitCode}", 300)));
            }
            return new ExplainResult(true, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new ExplainResult(false, Strings.Get(lang, "explain.failed").Replace("{error}", e.Message));
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Komutu çözer (.cmd/.bat → cmd.exe /c), argümanları ve temizlenmiş ortamı kurar.</summary>
    public static ProcessStartInfo BuildStartInfo(string command)
    {
        var resolved = Resolve(command);
        var args = new[] { "-p", "--model", "haiku" };
        ProcessStartInfo psi;
        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            psi = new ProcessStartInfo("cmd.exe");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(resolved);
        }
        else if (ext == ".ps1")
        {
            psi = new ProcessStartInfo("powershell.exe");
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", resolved }) psi.ArgumentList.Add(a);
        }
        else psi = new ProcessStartInfo(resolved);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var key in psi.Environment.Keys.ToList())
            if (ShouldStrip(key)) psi.Environment.Remove(key);
        return psi;
    }

    public static string Resolve(string command)
    {
        if (Path.IsPathRooted(command) || command.Contains('\\') || command.Contains('/'))
            return command;
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (Path.HasExtension(command) && File.Exists(Path.Combine(dir, command))) return Path.Combine(dir, command);
                foreach (var e in exts)
                {
                    var p = Path.Combine(dir, command + e.ToLowerInvariant());
                    if (File.Exists(p)) return p;
                }
            }
            catch (ArgumentException) { }
        }
        return command;
    }
}
