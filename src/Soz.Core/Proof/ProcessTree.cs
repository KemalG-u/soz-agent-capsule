using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Soz.Core.Proof;

/// <summary>
/// Kendi başlattığımız sürecin ağacını tek parça yönetir: Windows Job Object (KILL_ON_JOB_CLOSE) içine alınır,
/// zaman aşımında ya da işin sonunda yalnız bu ağaç kapatılır. Kullanıcının diğer süreçlerine dokunulmaz.
/// Bilinen sınır: Process.Start dönüşü ile iş nesnesine atama arasında çok kısa bir an vardır; bu anda doğan
/// torun süreç (pratikte cmd.exe o kadar hızlı değildir) iş nesnesine girmez. Yedek olarak Kill(entireProcessTree) de çağrılır.
/// </summary>
public sealed class ProcessTree : IDisposable
{
    private readonly IntPtr _job;
    private readonly Process _proc;
    private readonly StringBuilder _out = new();
    private readonly object _lock = new();
    private readonly TaskCompletionSource _stdoutDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stderrDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const int MaxCapture = 256 * 1024;
    private bool _truncated;

    public bool InJob { get; }

    private IntPtr _agentJob;

    /// <summary>Süreç Söz'ün adlandırılmış ajan iş nesnesinin (Local\SozAjan-özet) içine alındı mı (denetim 3, İlke 2).</summary>
    public bool InAgentJob { get; }

    private ProcessTree(ProcessStartInfo psi, string? stdinText = null, Action<string>? onStdout = null, Action<string>? onStderr = null, string? agentJobDataDir = null)
    {
        _job = OperatingSystem.IsWindows() ? CreateKillOnCloseJob() : IntPtr.Zero;
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => { if (e.Data is null) _stdoutDone.TrySetResult(); else { Append(e.Data); try { onStdout?.Invoke(e.Data); } catch (Exception) { } } };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data is null) _stderrDone.TrySetResult(); else { Append(e.Data); try { onStderr?.Invoke(e.Data); } catch (Exception) { } } };
        _proc.Start();
        if (agentJobDataDir is not null && OperatingSystem.IsWindows())
        {
            // Önce adlandırılmış dış iş nesnesi (çağıran kimliği işareti), sonra koşuya özel iç iş nesnesi (öldürme). Soy ikisinin de içinde kalır.
            _agentJob = Soz.Core.Security.CallerIdentity.CreateOrOpenAgentJob(agentJobDataDir);
            if (_agentJob != IntPtr.Zero) InAgentJob = Soz.Core.Security.CallerIdentity.Assign(_agentJob, _proc.Handle);
        }
        if (_job != IntPtr.Zero)
        {
            try { InJob = AssignProcessToJobObject(_job, _proc.Handle); }
            catch (InvalidOperationException) { InJob = false; }
        }
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        try
        {
            if (stdinText is not null) _proc.StandardInput.Write(stdinText);
            _proc.StandardInput.Close(); // girdi beklemesin
        }
        catch (Exception) { /* süreç girdiyi okumadan çıkmış olabilir */ }
    }

    /// <summary>
    /// Süreci başlatır. stdout ve stderr birleşik yakalanır (en çok 256 KB, sondan). agentJobDataDir verilirse süreç Söz'ün adlandırılmış ajan iş nesnesine de alınır (denetim 4, B):
    /// proje kodunu çalıştıran her süreç (başlangıç ölçümü, kapı, kanca ölçütleri) ajan kodudur; bunlardan gelen kabul ve ayar isteği 403 alır.
    /// </summary>
    public static ProcessTree Start(string fileName, string arguments, string workingDir, IDictionary<string, string?>? extraEnv = null, string? agentJobDataDir = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (extraEnv is not null) foreach (var (k, v) in extraEnv) psi.Environment[k] = v;
        return new ProcessTree(psi, null, null, null, agentJobDataDir);
    }

    /// <summary>
    /// Uzun süren, satır satır okunan süreç (Söz ajanı). Argümanlar ArgumentList ile verilir (boşluklu yollar için tek dizgi birleştirme yok);
    /// istem stdin'den yazılır. stdout satırları onStdout ile (okuyucu iş parçacığında) iletilir. Pencere açılmaz.
    /// </summary>
    public static ProcessTree StartStreaming(string fileName, IEnumerable<string> args, string workingDir, IDictionary<string, string?>? env,
        string? stdinText, Action<string> onStdout, Action<string>? onStderr = null, IEnumerable<string>? removeEnv = null, string? agentJobDataDir = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (removeEnv is not null) foreach (var k in removeEnv) psi.Environment.Remove(k);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        return new ProcessTree(psi, stdinText ?? "", onStdout, onStderr, agentJobDataDir);
    }

    private void Append(string line)
    {
        lock (_lock)
        {
            _out.Append(line).Append('\n');
            if (_out.Length > MaxCapture) { _out.Remove(0, _out.Length - MaxCapture); _truncated = true; }
        }
    }

    public string Output { get { lock (_lock) return (_truncated ? "…(baş kısmı kesildi)\n" : "") + _out.ToString(); } }
    public int Pid => _proc.Id;
    public bool HasExited { get { try { return _proc.HasExited; } catch (InvalidOperationException) { return true; } } }
    public DateTime StartTimeUtc { get { try { return _proc.StartTime.ToUniversalTime(); } catch (Exception) { return DateTime.UtcNow; } } }

    /// <summary>Süreç bitene kadar bekler (zaman aşımı yok); çıktı borularının boşalmasını da (en çok 3 sn) bekler. Çıkış kodunu döndürür.</summary>
    public async Task<int> WaitExitAsync(CancellationToken ct = default)
    {
        await _proc.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAny(Task.WhenAll(_stdoutDone.Task, _stderrDone.Task), Task.Delay(3000, CancellationToken.None)).ConfigureAwait(false);
        return _proc.ExitCode;
    }

    /// <summary>Başka bir süreçte yazılmış bir kaydın pid'ini güvenle kapatır: süreç yaşıyorsa ve başlangıç zamanı kayıttakiyle uyuşuyorsa (pid yeniden kullanımına karşı).</summary>
    public static bool KillByPid(int pid, DateTime? expectedStartUtc, TimeSpan? tolerance = null)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (expectedStartUtc is { } exp)
            {
                var diff = (p.StartTime.ToUniversalTime() - exp).Duration();
                if (diff > (tolerance ?? TimeSpan.FromSeconds(5))) return false; // başka bir süreç aynı pid'i almış
            }
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>pid yaşıyor mu ve başlangıç zamanı uyuşuyor mu (yetim kayıt temizliği için).</summary>
    public static bool IsAlive(int pid, DateTime? expectedStartUtc, TimeSpan? tolerance = null)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (expectedStartUtc is { } exp && (p.StartTime.ToUniversalTime() - exp).Duration() > (tolerance ?? TimeSpan.FromSeconds(5))) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Bitmesini bekler. Zaman aşımında ağacı kapatır ve false döner.</summary>
    public bool WaitForExit(TimeSpan timeout, out int? exitCode)
    {
        var ms = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        if (_proc.WaitForExit(ms))
        {
            // Çıktı borusu: torun süreç borunun ucunu tutuyorsa sonsuza dek bekleme (2 sn).
            Task.WaitAll(new Task[] { _stdoutDone.Task, _stderrDone.Task }, TimeSpan.FromSeconds(2));
            exitCode = _proc.ExitCode;
            KillTree(); // kalan torunlar (yalnız bizim ağacımız)
            return true;
        }
        KillTree();
        exitCode = null;
        return false;
    }

    public void KillTree()
    {
        if (_job != IntPtr.Zero) { try { TerminateJobObject(_job, 1); } catch (Exception) { } }
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    public void Dispose()
    {
        KillTree();
        if (_job != IntPtr.Zero) { try { CloseHandle(_job); } catch (Exception) { } }
        Soz.Core.Security.CallerIdentity.Close(_agentJob); _agentJob = IntPtr.Zero;
        _proc.Dispose();
    }

    // ---------- Win32 ----------
    private static IntPtr CreateKillOnCloseJob()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len)) { CloseHandle(job); return IntPtr.Zero; }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }
        catch (Exception) { return IntPtr.Zero; }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
