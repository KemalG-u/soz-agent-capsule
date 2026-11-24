using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Soz.Core.Profile;

/// <summary>
/// Windows süreç üst bilgisi, PEB çalışma klasörü (CurrentDirectory), görünür ana/terminal pencere tutamacı (HWND)
/// ve pencereyi öne getirme yardımcıları. Her hata sessizce yutulur, süreç hiçbir zaman zorla kapatılmaz veya değiştirilmez.
/// </summary>
public static class Win32ProcessInspector
{
    public sealed record RawProcessNode(int Pid, int ParentPid, string Name);
    public sealed record VisibleWindowInfo(long Hwnd, int Pid, string Title);

    private static readonly HashSet<string> TerminalAndHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "cmd", "powershell", "pwsh", "conhost",
        "Code", "Cursor", "Antigravity", "Hermes", "mintty", "wezterm-gui", "alacritty",
    };

    private static readonly HashSet<string> ClaudeChildShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "node", "nodejs", "python", "python3", "pwsh", "powershell", "cmd", "git", "dotnet",
    };

    public static Dictionary<int, RawProcessNode> SnapshotProcessTree()
    {
        var map = new Dictionary<int, RawProcessNode>();
        if (!OperatingSystem.IsWindows()) return map;
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return map;
            do
            {
                var pid = (int)entry.th32ProcessID;
                var ppid = (int)entry.th32ParentProcessID;
                var exe = Path.GetFileNameWithoutExtension(entry.szExeFile ?? "");
                if (pid > 0 && exe.Length > 0)
                    map[pid] = new RawProcessNode(pid, ppid, exe);
            } while (Process32NextW(snap, ref entry));
        }
        catch (Exception) { }
        finally { CloseHandle(snap); }
        return map;
    }

    public static Dictionary<int, long> SnapshotVisibleWindows()
    {
        var map = new Dictionary<int, long>();
        foreach (var w in SnapshotAllVisibleWindows())
        {
            if (!map.ContainsKey(w.Pid))
                map[w.Pid] = w.Hwnd;
        }
        return map;
    }

    public static List<VisibleWindowInfo> SnapshotAllVisibleWindows()
    {
        var list = new List<VisibleWindowInfo>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var len = GetWindowTextLengthW(hWnd);
                if (len <= 0) return true;
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0) return true;
                var cap = Math.Min(len + 1, 512);
                var sb = new StringBuilder(cap);
                var title = GetWindowTextW(hWnd, sb, cap) > 0 ? sb.ToString().Trim() : "";
                if (title.Length == 0) return true;
                list.Add(new VisibleWindowInfo(hWnd.ToInt64(), (int)pid, title));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception) { }
        return list;
    }

    public static string? GetWindowTitle(long? hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd is not > 0) return null;
        var h = new IntPtr(hwnd.Value);
        if (!IsWindow(h)) return null;
        var len = GetWindowTextLengthW(h);
        if (len <= 0) return null;
        var cap = Math.Min(len + 1, 512);
        var sb = new StringBuilder(cap);
        return GetWindowTextW(h, sb, cap) > 0 ? sb.ToString().Trim() : null;
    }

    public static long? ResolveWindowForProcess(int pid, IReadOnlyDictionary<int, RawProcessNode> tree, IReadOnlyDictionary<int, long> windowsByPid, bool allowTerminalFallback = false)
    {
        if (windowsByPid.TryGetValue(pid, out var direct)) return direct;
        var visited = new HashSet<int> { pid };
        var cur = pid;
        var hasConsoleShellInChain = false;
        for (var depth = 0; depth < 8; depth++)
        {
            if (!tree.TryGetValue(cur, out var node)) break;
            if (node.Name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("bash", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("conhost", StringComparison.OrdinalIgnoreCase))
            {
                hasConsoleShellInChain = true;
            }
            if (node.ParentPid <= 0 || !visited.Add(node.ParentPid)) break;
            cur = node.ParentPid;
            if (windowsByPid.TryGetValue(cur, out var ancHwnd))
            {
                if (!tree.TryGetValue(cur, out var ancNode) || TerminalAndHostNames.Contains(ancNode.Name))
                    return ancHwnd;
            }
        }
        if (allowTerminalFallback || hasConsoleShellInChain)
        {
            foreach (var (wPid, wHwnd) in windowsByPid)
            {
                if (tree.TryGetValue(wPid, out var wn)
                    && (wn.Name.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
                        || wn.Name.Equals("wezterm-gui", StringComparison.OrdinalIgnoreCase)
                        || wn.Name.Equals("alacritty", StringComparison.OrdinalIgnoreCase)
                        || wn.Name.Equals("mintty", StringComparison.OrdinalIgnoreCase)))
                {
                    return wHwnd;
                }
            }
        }
        return null;
    }

    public static bool HasAncestorNamed(int pid, string ancestorName, IReadOnlyDictionary<int, RawProcessNode> tree)
    {
        var visited = new HashSet<int> { pid };
        var cur = pid;
        for (var depth = 0; depth < 8; depth++)
        {
            if (!tree.TryGetValue(cur, out var node) || node.ParentPid <= 0 || !visited.Add(node.ParentPid)) break;
            cur = node.ParentPid;
            if (tree.TryGetValue(cur, out var parent) && parent.Name.Equals(ancestorName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Aynı kullanıcıdaki 64-bit süreçten PEB CurrentDirectory ve CommandLine bilgisini güvenli okur.</summary>
    public static (string? CurrentDirectory, string? CommandLine) ReadPebStrings(int pid)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || pid <= 4) return (null, null);
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
        if (hProcess == IntPtr.Zero) return (null, null);
        try
        {
            if (IsWow64Process(hProcess, out var wow64) && wow64) return (null, null);
            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero) return (null, null);

            // x64 PEB + 0x20 -> RTL_USER_PROCESS_PARAMETERS*
            if (!TryReadIntPtr(hProcess, IntPtr.Add(pbi.PebBaseAddress, 0x20), out var procParams) || procParams == IntPtr.Zero)
                return (null, null);

            // x64 RTL_USER_PROCESS_PARAMETERS: CurrentDirectory.DosPath @ 0x38, CommandLine @ 0x70
            var cwd = ReadUnicodeString(hProcess, IntPtr.Add(procParams, 0x38), maxChars: 1024);
            var cmdLine = ReadUnicodeString(hProcess, IntPtr.Add(procParams, 0x70), maxChars: 4096);
            return (ProfileSources.CleanPath(cwd), cmdLine);
        }
        catch (Exception)
        {
            return (null, null);
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>claude.exe kendi CWD'si System32/ev/Masaüstü ise alt süreç ağacındaki bash/node/python/pwsh CWD'sini miras alır.</summary>
    public static string? InheritChildProjectDirectory(int rootPid, IReadOnlyDictionary<int, RawProcessNode> tree, Func<string, bool> isUnusableCwd)
    {
        var childrenByParent = new Dictionary<int, List<RawProcessNode>>();
        foreach (var n in tree.Values)
        {
            if (n.ParentPid <= 0) continue;
            if (!childrenByParent.TryGetValue(n.ParentPid, out var list)) childrenByParent[n.ParentPid] = list = new();
            list.Add(n);
        }

        var queue = new Queue<(int Pid, int Depth)>();
        var visited = new HashSet<int> { rootPid };
        queue.Enqueue((rootPid, 0));
        while (queue.Count > 0)
        {
            var (cur, depth) = queue.Dequeue();
            if (depth >= 4 || !childrenByParent.TryGetValue(cur, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (!visited.Add(kid.Pid)) continue;
                if (ClaudeChildShellNames.Contains(kid.Name))
                {
                    var (kidCwd, _) = ReadPebStrings(kid.Pid);
                    if (!string.IsNullOrWhiteSpace(kidCwd) && !isUnusableCwd(kidCwd))
                        return kidCwd;
                }
                queue.Enqueue((kid.Pid, depth + 1));
            }
        }
        return null;
    }

    public static bool FocusWindow(long? hwnd, int? pid, out long? usedHwnd)
    {
        usedHwnd = null;
        if (!OperatingSystem.IsWindows()) return false;
        IntPtr target = IntPtr.Zero;
        if (hwnd is > 0)
        {
            var h = new IntPtr(hwnd.Value);
            if (IsWindow(h)) target = h;
        }
        if (target == IntPtr.Zero && pid is > 0)
        {
            var tree = SnapshotProcessTree();
            var wins = SnapshotVisibleWindows();
            var resolved = ResolveWindowForProcess(pid.Value, tree, wins);
            if (resolved is > 0 && IsWindow(new IntPtr(resolved.Value)))
                target = new IntPtr(resolved.Value);
        }
        if (target == IntPtr.Zero) return false;

        try
        {
            AllowSetForegroundWindow(ASFW_ANY);
            if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
            else ShowWindow(target, SW_SHOW);
            var fg = SetForegroundWindow(target);
            BringWindowToTop(target);
            usedHwnd = target.ToInt64();
            return fg || IsWindowVisible(target);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool OpenFolderInExplorer(string folderPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(folderPath)) return false;
        var clean = ProfileSources.CleanPath(folderPath);
        if (clean is null || !Directory.Exists(clean)) return false;
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(clean));
        using var p = Process.Start(psi);
        return p is not null;
    }

    private static bool TryReadIntPtr(IntPtr hProcess, IntPtr address, out IntPtr value)
    {
        Span<byte> buf = stackalloc byte[8];
        if (!ReadProcessMemory(hProcess, address, ref MemoryMarshal.GetReference(buf), 8, out var read) || (int)read != 8)
        {
            value = IntPtr.Zero;
            return false;
        }
        value = new IntPtr(BitConverter.ToInt64(buf));
        return true;
    }

    private static string? ReadUnicodeString(IntPtr hProcess, IntPtr unicodeStringAddr, int maxChars)
    {
        Span<byte> header = stackalloc byte[16];
        if (!ReadProcessMemory(hProcess, unicodeStringAddr, ref MemoryMarshal.GetReference(header), 16, out var r1) || (int)r1 != 16)
            return null;
        var byteLen = BitConverter.ToUInt16(header[..2]);
        var bufferPtr = new IntPtr(BitConverter.ToInt64(header[8..16]));
        if (byteLen == 0 || bufferPtr == IntPtr.Zero) return null;
        var safeBytes = Math.Min((int)byteLen, maxChars * 2);
        if (safeBytes % 2 != 0) safeBytes--;
        if (safeBytes <= 0) return null;
        var data = new byte[safeBytes];
        if (!ReadProcessMemory(hProcess, bufferPtr, ref data[0], (nuint)safeBytes, out var r2) || (int)r2 <= 0)
            return null;
        return Encoding.Unicode.GetString(data, 0, (int)r2).TrimEnd('\0');
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint GW_OWNER = 4;
    private const uint ASFW_ANY = 0xFFFFFFFF;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, ref byte lpBuffer, nuint dwSize, out nuint lpNumberOfBytesRead);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
}
