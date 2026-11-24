using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Soz.Shell;

/// <summary>
/// Tek örnek (Single Instance) etkinleştirme kanalı (<c>Soz.Shell.Activate.v1.&lt;SessionId&gt;</c>):
/// Kullanıcı kısayola ikinci kez tıkladığında çalışan örneğe <c>"show-panel"</c> sinyali gönderir ve çalışan örnek paneli öne getirir.
/// </summary>
internal sealed class SingleInstanceChannel : IDisposable
{
    public const string ShowPanelSignal = "show-panel";
    public const string ExitSignal = "exit";
    private const byte AckByte = 1;
    private const int ServerClientBudgetMs = 400;

    private readonly string _pipeName;
    private readonly Action _onShowPanel;
    private readonly Action? _onExit;
    private readonly uint _sessionId;
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeServerStream? _currentServer;
    private readonly object _gate = new();
    private bool _disposed;

    private SingleInstanceChannel(string pipeName, Action onShowPanel, Action? onExit)
    {
        _pipeName = pipeName;
        _onShowPanel = onShowPanel;
        _onExit = onExit;
        using var proc = Process.GetCurrentProcess();
        _sessionId = (uint)proc.SessionId;
        _ = Task.Run(() => ListenLoopAsync(_stop.Token));
    }

    public static string BuildPipeName(Options opt, string dataDir)
    {
        using var proc = Process.GetCurrentProcess();
        var baseName = $"Soz.Shell.Activate.v1.{proc.SessionId}";
        return opt.TekOrnekAdi(baseName, dataDir);
    }

    public static SingleInstanceChannel StartServer(string pipeName, Action onShowPanel, Action? onExit = null)
        => new(pipeName, onShowPanel, onExit);

    public static bool TrySignalShowPanel(string pipeName, int timeoutMs = 800)
        => TrySignal(pipeName, ShowPanelSignal, grantForeground: true, timeoutMs);

    public static bool TrySignalExit(string pipeName, int timeoutMs = 1500)
        => TrySignal(pipeName, ExitSignal, grantForeground: false, timeoutMs);

    private static bool TrySignal(string pipeName, string signal, bool grantForeground, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || timeoutMs <= 0) return false;
        try
        {
            return Task.Run(() => SendSignalCoreAsync(pipeName, signal, grantForeground, timeoutMs)).GetAwaiter().GetResult();
        }
        catch { return false; }
    }

    private static async Task<bool> SendSignalCoreAsync(string pipeName, string signal, bool grantForeground, int timeoutMs)
    {
        using var deadline = new CancellationTokenSource(timeoutMs);
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Anonymous);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if (!IsTrustedServer(client)) return false;
            using var current = Process.GetCurrentProcess();
            if (!GetNamedPipeServerSessionId(client.SafePipeHandle, out var serverSession) || serverSession != (uint)current.SessionId)
                return false;
            if (grantForeground && GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid))
                _ = AllowSetForegroundWindow(serverPid);

            var payload = Encoding.UTF8.GetBytes(signal + "\n");
            await client.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);
            var ack = new byte[1];
            var read = await client.ReadAsync(ack, deadline.Token).ConfigureAwait(false);
            return read == 1 && ack[0] == AckByte;
        }
        catch { return false; }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateUserPipe(_pipeName);
                lock (_gate)
                {
                    if (_disposed) { server.Dispose(); return; }
                    _currentServer = server;
                }
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(ServerClientBudgetMs);
                try
                {
                    if (GetNamedPipeClientSessionId(server.SafePipeHandle, out var clientSession) && clientSession == _sessionId)
                    {
                        var buf = new byte[64];
                        var n = await server.ReadAsync(buf, budget.Token).ConfigureAwait(false);
                        if (n > 0)
                        {
                            var msg = Encoding.UTF8.GetString(buf, 0, n).Trim();
                            var isShow = string.Equals(msg, ShowPanelSignal, StringComparison.Ordinal);
                            var isExit = string.Equals(msg, ExitSignal, StringComparison.Ordinal) && _onExit != null;
                            var ok = isShow || isExit;
                            await server.WriteAsync(new byte[] { ok ? AckByte : (byte)0 }, budget.Token).ConfigureAwait(false);
                            await server.FlushAsync(budget.Token).ConfigureAwait(false);
                            if (isShow)
                            {
                                try { _onShowPanel(); } catch { }
                            }
                            else if (isExit)
                            {
                                try { _onExit!(); } catch { }
                            }
                            // İstemci onayı okuyana kadar boruyu anında koparma.
                            _ = await server.ReadAsync(buf.AsMemory(0, 1), budget.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested) { }
                catch (IOException) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
            catch (Exception)
            {
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { break; }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_currentServer, server)) _currentServer = null;
                }
                try { server?.Dispose(); } catch { }
            }
        }
    }

    private static NamedPipeServerStream CreateUserPipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        if (identity.User != null)
            security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64,
            64,
            security);
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

    public void Dispose()
    {
        NamedPipeServerStream? srv;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            srv = _currentServer;
            _currentServer = null;
        }
        try { _stop.Cancel(); } catch { }
        try { srv?.Dispose(); } catch { }
        _stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

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
