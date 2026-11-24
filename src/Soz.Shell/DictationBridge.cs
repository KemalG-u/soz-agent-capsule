using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Soz.Shell;


internal sealed record DictationResult(bool Started, string? Path, string Message, Process? Process);

/// <summary>
/// KarıncaAI tek-ürün dikte ve ev sahibi köprüsü:
/// - Çalışan KarıncaAI örneğine adlandırılmış borudan (<c>WisperKemal.Activation.v1.&lt;SessionId&gt;.&lt;SHA256(SID)&gt;</c>) bağlanır.
/// - <c>4</c> komutuyla dikteyi 0 ms süreç açmadan başlatır/durdurur.
/// - <c>7</c> (HostAttach) kalp atışıyla KarıncaAI'nin ikinci tepsi simgesini ve dinlenme çakılını gizleyip tek ürün gibi çalışmasını sağlar ve canlı durum baytını okur.
/// - <c>6</c> (StatusQuery) ile dikte sırasında canlı fazı (<c>dinliyor</c>, <c>ceviriyor</c>, <c>yazdi</c>, <c>panoda</c>, <c>kapali</c>) Söz kapsülüne aktarır.
/// - <c>8</c> (HostDetach) ile Söz kapanırken KarıncaAI kendi tepsi simgesini ve çakılını anında geri açar.
/// </summary>
internal sealed class DictationBridge
{
    public const byte DictatePipeCommand = 4;
    public const byte StatusQueryPipeCommand = 6;
    public const byte HostAttachPipeCommand = 7;
    public const byte HostDetachPipeCommand = 8;
    public const byte PipeAcceptedReply = 1;
    public const int PipeBudgetMs = 350;

    private readonly Func<string?> _configuredPath;
    private readonly string _localAppData;
    private readonly Func<string?>? _pipeNameOverride;
    private readonly Func<string, Process?>? _processStarter;
    private DateTime _legacyServerBackoffUntilUtc = DateTime.MinValue;
    private int _legacyRejectCount;

    public bool HostProtocolSupported { get; private set; }
    public bool IsHostAttached { get; private set; }

    public DictationBridge(
        Func<string?> configuredPath,
        string? localAppData = null,
        Func<string?>? pipeNameProvider = null,
        Func<string, Process?>? processStarter = null)
    {
        _configuredPath = configuredPath;
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _pipeNameOverride = pipeNameProvider;
        _processStarter = processStarter;
    }

    public static string? DefaultPipeName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (string.IsNullOrEmpty(sid)) return null;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
            using var proc = Process.GetCurrentProcess();
            return $"WisperKemal.Activation.v1.{proc.SessionId}.{hash}";
        }
        catch { return null; }
    }

    public string? ResolvePipeName()
    {
        if (_pipeNameOverride != null) return _pipeNameOverride();
        // Özel yol verilmişse (ör. --dictation-path denemesi) sistemdeki gerçek KarıncaAI borusuna dokunulmaz.
        if (!string.IsNullOrWhiteSpace(_configuredPath())) return null;
        return DefaultPipeName();
    }

    public string? ResolvePath()
    {
        var cfg = _configuredPath();
        if (!string.IsNullOrWhiteSpace(cfg))
        {
            var expanded = Environment.ExpandEnvironmentVariables(cfg.Trim().Trim('"'));
            return File.Exists(expanded) ? expanded : null;
        }
        foreach (var candidate in new[]
        {
            Path.Combine(_localAppData, "KarincaAI", "current", "KarincaAI.exe"),
            Path.Combine(_localAppData, "KarincaAI", "KarincaAI.exe"),
        })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public static string? ToCapsulePhase(DictationHostStatus status) => status switch
    {
        DictationHostStatus.Recording => "dinliyor",
        DictationHostStatus.Processing => "ceviriyor",
        DictationHostStatus.Delivered => "yazdi",
        DictationHostStatus.ClipboardOnly => "panoda",
        DictationHostStatus.Idle => "kapali",
        _ => null,
    };

    public DictationResult Toggle()
    {
        var pipeName = ResolvePipeName();
        if (!string.IsNullOrWhiteSpace(pipeName) && TryTriggerViaPipe(pipeName, PipeBudgetMs))
            return new DictationResult(true, "pipe:" + pipeName, "Kanal üzerinden tetiklendi", null);

        var path = ResolvePath();
        if (path == null) return new DictationResult(false, null, "Dikte programı bulunamadı", null);
        try
        {
            Process? p;
            if (_processStarter != null)
            {
                p = _processStarter(path);
            }
            else
            {
                var psi = new ProcessStartInfo(path, "--dictate")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                };
                p = Process.Start(psi);
            }
            // Yeni süreç başladıysa eski sürüm geri çekilmesini sıfırla ki yeni boru açılınca hemen bağlanabilsin.
            _legacyServerBackoffUntilUtc = DateTime.MinValue;
            _legacyRejectCount = 0;
            return new DictationResult(p != null, path, p != null ? "Başlatıldı" : "Süreç başlamadı", p);
        }
        catch (Exception ex)
        {
            Log.Error("dikte köprüsü", ex);
            return new DictationResult(false, path, ex.Message, null);
        }
    }

    public DictationHostStatus AttachAndQueryStatus(int budgetMs = 150)
    {
        var pipeName = ResolvePipeName();
        if (string.IsNullOrWhiteSpace(pipeName)) return DictationHostStatus.Unavailable;
        if (DateTime.UtcNow < _legacyServerBackoffUntilUtc) return DictationHostStatus.Unavailable;

        var reply = SendPipeCommand(pipeName, HostAttachPipeCommand, grantForeground: false, budgetMs);
        if (reply is null)
        {
            IsHostAttached = false;
            return DictationHostStatus.Unavailable;
        }
        if (reply.Value is >= (byte)DictationHostStatus.Idle and <= (byte)DictationHostStatus.ClipboardOnly)
        {
            _legacyRejectCount = 0;
            HostProtocolSupported = true;
            IsHostAttached = true;
            return (DictationHostStatus)reply.Value;
        }
        // Çalışan KarıncaAI eski sürümse (komut 7 için 0 döndürür) yormamak için 45 sn geri çekil.
        IsHostAttached = false;
        HostProtocolSupported = false;
        if (++_legacyRejectCount >= 2)
            _legacyServerBackoffUntilUtc = DateTime.UtcNow.AddSeconds(45);
        return DictationHostStatus.Unavailable;
    }

    public DictationHostStatus QueryStatus(int budgetMs = 120)
    {
        var pipeName = ResolvePipeName();
        if (string.IsNullOrWhiteSpace(pipeName) || !HostProtocolSupported) return DictationHostStatus.Unavailable;
        var reply = SendPipeCommand(pipeName, StatusQueryPipeCommand, grantForeground: false, budgetMs);
        if (reply is { } b && b is >= (byte)DictationHostStatus.Idle and <= (byte)DictationHostStatus.ClipboardOnly)
            return (DictationHostStatus)b;
        return DictationHostStatus.Unavailable;
    }

    public bool DetachHost(int budgetMs = 200)
    {
        var pipeName = ResolvePipeName();
        IsHostAttached = false;
        if (string.IsNullOrWhiteSpace(pipeName) || !HostProtocolSupported) return false;
        var reply = SendPipeCommand(pipeName, HostDetachPipeCommand, grantForeground: false, budgetMs);
        return reply == PipeAcceptedReply;
    }

    internal static bool TryTriggerViaPipe(string pipeName, int budgetMs = PipeBudgetMs)
        => SendPipeCommand(pipeName, DictatePipeCommand, grantForeground: true, budgetMs) == PipeAcceptedReply;

    internal static byte? SendPipeCommand(string pipeName, byte command, bool grantForeground, int budgetMs)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || budgetMs <= 0) return null;
        // Boru hiç oluşturulmamışsa (program kapalıysa) ERROR_FILE_NOT_FOUND (2) ile 0 ms içinde döner.
        if (!WaitNamedPipeW(@"\\.\pipe\" + pipeName, 30))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 3) return null;
            if (err == 2)
            {
                Thread.Sleep(10);
                if (!WaitNamedPipeW(@"\\.\pipe\" + pipeName, 30) && Marshal.GetLastWin32Error() is 2 or 3)
                    return null;
            }
        }
        try
        {
            return Task.Run(() => SendPipeCoreAsync(pipeName, command, grantForeground, budgetMs)).GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    private static async Task<byte?> SendPipeCoreAsync(string pipeName, byte command, bool grantForeground, int budgetMs)
    {
        using var deadline = new CancellationTokenSource(budgetMs);
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Anonymous);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if (!IsTrustedServer(client)) return null;
            using var current = Process.GetCurrentProcess();
            if (!GetNamedPipeServerSessionId(client.SafePipeHandle, out var serverSession) || serverSession != (uint)current.SessionId)
                return null;
            if (grantForeground && GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid))
                _ = AllowSetForegroundWindow(serverPid);
            await client.WriteAsync(new byte[] { command }, deadline.Token).ConfigureAwait(false);
            var reply = new byte[1];
            var read = await client.ReadAsync(reply, deadline.Token).ConfigureAwait(false);
            return read == 1 ? reply[0] : null;
        }
        catch { return null; }
    }

    private static bool IsTrustedServer(NamedPipeClientStream client)
    {
        try
        {
            if (client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
                return false;
            using var identity = WindowsIdentity.GetCurrent();
            return owner == identity.User || owner == identity.Owner ||
                   owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string lpNamedPipeName, uint nTimeOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
