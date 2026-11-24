using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Soz.Core.Security;

public enum CallerKind { Human, Agent, Unknown }

/// <summary>Çağıranın sınıfı. Pid yoksa 0. Why: kararın sade gerekçesi (olay kaydı ve sınama için).</summary>
public sealed record CallerInfo(CallerKind Kind, int Pid, string ProcessName, string Why)
{
    public bool IsAgent => Kind == CallerKind.Agent;
}

/// <summary>
/// "Kabul ve ayar yalnız insandan" (denetim 3, İlke 2). Aynı kullanıcı hesabında çalışan ajan için gizli anahtar saklamak çözüm değildir;
/// bu yüzden çağıran SÜREÇ sınıflanır:
///  (a) Söz'ün başlattığı ajan süreçleri ADLANDIRILMIŞ bir Windows iş nesnesinin (Local\SozAjan-özet) içindedir; çocuk süreçler de içinde kalır
///      (breakaway kapalı). Süreç bu nesnenin içindeyse ajan işidir.
///  (b) Yedek (en iyi çaba): süreç ya da atalarından biri claude / komut satırında claude geçen node ise ajan işidir (en çok 12 kuşak).
/// Sunucu, loopback bağlantının karşı ucundaki süreci GetExtendedTcpTable ile bulur. Süreç bulunamazsa yazma uçları için ret (Unknown).
/// Sınırı: aynı kullanıcı olarak çalışan kötü niyetli bir süreç Söz'ün dışından başlatılıp veritabanını doğrudan değiştirebilir; bu engellenemez.
/// </summary>
public static class CallerIdentity
{
    public const int MaxGenerations = 12;
    /// <summary>Ret metni ne yapılacağını söyler (denetim 4, G): ajan işinden ya da Claude oturumundan gelen istek.</summary>
    public const string HumanOnlyMessage = "Bu istek bir Claude penceresinin içinden geldi. Söz'ü Claude dışındaki bir pencereden aç.";
    /// <summary>Çağıran süreç bulunamadığında (güvenli taraf: ret).</summary>
    public const string UnknownCallerMessage = "İsteğin hangi programdan geldiği anlaşılamadı. Söz'ü kendi penceresinden ya da tarayıcıdan aç.";

    public static string MessageFor(CallerInfo who) => who.Kind == CallerKind.Unknown ? UnknownCallerMessage : HumanOnlyMessage;

    /// <summary>(b) yedeğini açar/kapatır. Varsayılan açık. YALNIZ birim testleri için (testin kendisi Claude Code içinden koşabilsin). Üründe komut satırı seçeneği yoktur.</summary>
    public static bool AncestorCheck { get; set; } = true;

    /// <summary>Veri klasörüne özgü iş nesnesi adı. Aynı veri klasörünü kullanan sunucu ve CLI aynı adı üretir.</summary>
    public static string AgentJobName(string dataDir)
    {
        var full = Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToLowerInvariant();
        var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant()[..16];
        return "Local\\SozAjan-" + h;
    }

    // ------------------------------------------------------------------ sunucu tarafı
    /// <summary>Loopback bağlantının karşı ucunu sınıflar. clientPort = istemcinin yerel portu (RemotePort), serverPort = sunucunun portu.</summary>
    public static CallerInfo ClassifyPeer(int clientPort, int serverPort, string dataDir)
    {
        if (!OperatingSystem.IsWindows()) return new CallerInfo(CallerKind.Unknown, 0, "", "Windows dışı sistemde süreç bulunamadı.");
        int pid = 0;
        for (var i = 0; i < 4 && pid == 0; i++)
        {
            pid = FindPeerPid(clientPort, serverPort) ?? 0;
            if (pid == 0) Thread.Sleep(8);
        }
        if (pid == 0) return new CallerInfo(CallerKind.Unknown, 0, "", "Bağlantının sahibi süreç bulunamadı.");
        return ClassifyPid(pid, dataDir);
    }

    /// <summary>Verilen süreç ajan işinde mi (iş nesnesi) ya da ajan soyundan mı (yedek)?</summary>
    public static CallerInfo ClassifyPid(int pid, string dataDir, bool? ancestorCheck = null)
    {
        var name = ProcessName(pid);
        if (IsInAgentJob(pid, dataDir)) return new CallerInfo(CallerKind.Agent, pid, name, "Süreç Söz'ün yardımcı iş nesnesinin içinde.");
        if (ancestorCheck ?? AncestorCheck)
        {
            var anc = AgentAncestor(pid);
            if (anc is not null) return new CallerInfo(CallerKind.Agent, pid, name, "Süreç bir yardımcı sürecinin soyundan: " + anc);
        }
        return new CallerInfo(CallerKind.Human, pid, name, "Yardımcı işinde değil.");
    }

    /// <summary>CLI için: kendi sürecim ajan işinde mi, soyunda ajan var mı, ajan ortam değişkeni var mı?</summary>
    public static CallerInfo ClassifySelf(string dataDir, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var pid = Environment.ProcessId;
        var info = ClassifyPid(pid, dataDir);
        if (info.IsAgent) return info;
        foreach (var v in new[] { "SOZ_JOB_ID", "SOZ_RUN_ID", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CODEX_SANDBOX", "CODEX_CI", "OPENCODE" })
            if (!string.IsNullOrEmpty(env(v))) return new CallerInfo(CallerKind.Agent, pid, info.ProcessName, $"Yardımcı ortam değişkeni var ({v}).");
        return info;
    }

    // ------------------------------------------------------------------ iş nesnesi
    public static bool IsInAgentJob(int pid, string dataDir)
    {
        if (!OperatingSystem.IsWindows()) return false;
        IntPtr hJob = IntPtr.Zero, hProc = IntPtr.Zero;
        try
        {
            hJob = OpenJobObject(JOB_OBJECT_QUERY, false, AgentJobName(dataDir));
            if (hJob == IntPtr.Zero) return false; // hiç ajan işi yok
            hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (hProc == IntPtr.Zero) return false;
            return IsProcessInJob(hProc, hJob, out var inJob) && inJob;
        }
        catch (Exception) { return false; }
        finally { if (hProc != IntPtr.Zero) CloseHandle(hProc); if (hJob != IntPtr.Zero) CloseHandle(hJob); }
    }

    /// <summary>Adlandırılmış ajan iş nesnesini açar ya da oluşturur (ProcessTree kullanır). Dönen tutamak çağıranındır.</summary>
    internal static IntPtr CreateOrOpenAgentJob(string dataDir)
    {
        try { return CreateJobObject(IntPtr.Zero, AgentJobName(dataDir)); }
        catch (Exception) { return IntPtr.Zero; }
    }

    internal static bool Assign(IntPtr job, IntPtr process)
    {
        try { return AssignProcessToJobObject(job, process); }
        catch (Exception) { return false; }
    }

    internal static void Close(IntPtr h) { if (h != IntPtr.Zero) { try { CloseHandle(h); } catch (Exception) { } } }

    // ------------------------------------------------------------------ ata zinciri
    private sealed record ProcRow(int Pid, int Ppid, string Exe);

    private static Dictionary<int, ProcRow> Snapshot()
    {
        var map = new Dictionary<int, ProcRow>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref e)) return map;
            do { map[(int)e.th32ProcessID] = new ProcRow((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile ?? ""); }
            while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>Süreç ya da atalarından biri claude ise gerekçe cümlesi, yoksa null. PID yeniden kullanımına karşı ata, çocuktan sonra doğmuşsa zincir biter.</summary>
    public static string? AgentAncestor(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var map = Snapshot();
            var cur = pid;
            DateTime? curStart = StartTime(cur);
            for (var gen = 0; gen <= MaxGenerations; gen++)
            {
                if (!map.TryGetValue(cur, out var row)) return null;
                if (IsAgentProcess(row)) return $"{row.Exe} ({row.Pid})";
                var parent = row.Ppid;
                if (parent <= 4 || parent == cur || !map.TryGetValue(parent, out var prow)) return null; // ata yok (çıkmış) ya da sistem
                var pStart = StartTime(parent);
                if (curStart is { } cs && pStart is { } ps && ps > cs) return null; // aynı numarayı sonradan başka süreç almış
                cur = parent; curStart = pStart;
            }
        }
        catch (Exception) { }
        return null;
    }

    public static bool IsAgentExecutableName(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        var stem = Path.GetFileNameWithoutExtension(exe.Trim());
        return Assistants.AssistantCatalog.CliAgentProcessNames.Contains(stem);
    }

    private static bool IsAgentProcess(ProcRow row)
    {
        var stem = Path.GetFileNameWithoutExtension(row.Exe ?? "").ToLowerInvariant();
        if (stem == "claude")
        {
            // Claude Desktop (masaüstü uygulaması) da claude.exe adını taşır; ajan sayılmaz. Claude Code'un kendi çalıştırılabiliri sayılır.
            var path = ImagePath(row.Pid);
            return path is null || !IsDesktopAppPath(path);
        }
        if (stem is "codex" or "hermes" or "opencode" or "agy")
        {
            var path = ImagePath(row.Pid);
            if (path is not null && IsDesktopAppPath(path)) return false;
            return true;
        }
        if (stem is "node" or "nodejs" or "bun")
        {
            var cl = CommandLine(row.Pid);
            return cl is not null && IsClaudeCodeCommandLine(cl);
        }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex ClaudeCodeCli = new(
        @"@anthropic-ai[\\/]claude-code(?:[\\/]|\b)|[\\/]claude-code[\\/](?:cli\.(?:m?js)|bin[\\/]claude)|[\\/]\.claude[\\/]local[\\/]|[\\/]node_modules[\\/]\.bin[\\/](?:claude|codex|opencode|agy)\b|@openai[\\/]codex(?:[\\/]|\b)|opencode-ai(?:[\\/]|\b)|antigravity-cli(?:[\\/]|\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>node/bun komut satırı Claude Code'un kendi cli betiğini mi çalıştırıyor? Yol ya da argümanda yalnız "claude" geçen sıradan betik hayır.</summary>
    public static bool IsClaudeCodeCommandLine(string commandLine) => ClaudeCodeCli.IsMatch(commandLine);

    /// <summary>Masaüstü uygulaması yolu (AnthropicClaude, WindowsApps, Programs\Claude, app-x.y.z klasörü).</summary>
    public static bool IsDesktopAppPath(string path)
    {
        var p = path.Replace('/', '\\').ToLowerInvariant();
        return p.Contains("\\anthropicclaude\\") || p.Contains("\\windowsapps\\") || p.Contains("\\programs\\claude\\") || p.Contains("\\claude\\app-") || p.Contains("\\squirrel");
    }

    /// <summary>Sürecin çalıştırılabilir dosya yolu; okunamazsa null.</summary>
    public static string? ImagePath(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero) return null;
            var sb = new StringBuilder(1024);
            var n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        catch (Exception) { return null; }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    /// <summary>
    /// Çağıran sürecin kanalı (kabul olayı ve kart için): "kabuk penceresi" (Söz masaüstü), "tarayıcı", "komut satırı" (soz.exe, node, powershell, curl...) ya da "bilinmeyen süreç".
    /// Tarayıcının ağ süreci ana tarayıcı sürecinin çocuğudur; ata zincirinde (en çok 4 kuşak) aranır.
    /// </summary>
    public static string Channel(int pid, string? processName = null)
    {
        static string? ByName(string n)
        {
            n = n.ToLowerInvariant();
            if (n is "soz-masaustu.exe") return "kabuk penceresi";
            if (n is "msedge.exe" or "chrome.exe" or "firefox.exe" or "brave.exe" or "opera.exe" or "vivaldi.exe" or "arc.exe") return "tarayıcı";
            if (n is "soz.exe" or "node.exe" or "nodejs.exe" or "bun.exe" or "powershell.exe" or "pwsh.exe" or "cmd.exe" or "curl.exe" or "wget.exe" or "python.exe" or "pythonw.exe" or "py.exe" or "bash.exe" or "wsl.exe" or "claude.exe") return "komut satırı";
            return null;
        }
        try
        {
            var name = string.IsNullOrEmpty(processName) ? ProcessName(pid) : processName!;
            if (ByName(name) is { } direct) return direct;
            var map = Snapshot();
            var cur = pid;
            for (var gen = 0; gen < 5 && map.TryGetValue(cur, out var row); gen++)
            {
                if (ByName(row.Exe) is { } b) return b;
                if (row.Ppid <= 4 || row.Ppid == cur) break;
                cur = row.Ppid;
            }
            return "bilinmeyen süreç";
        }
        catch (Exception) { return "bilinmeyen süreç"; }
    }
    /// <summary>Kabul panelden (Söz penceresi ya da tarayıcı) mi geldi?</summary>
    public static bool IsPanelChannel(string? channel) => channel is "kabuk penceresi" or "tarayıcı" or "arayüz";

    public static string ProcessName(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName + ".exe"; }
        catch (Exception) { return ""; }
    }

    private static DateTime? StartTime(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero) return null;
            return GetProcessTimes(h, out var c, out _, out _, out _) ? DateTime.FromFileTimeUtc(c) : null;
        }
        catch (Exception) { return null; }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    /// <summary>Sürecin komut satırı (PEB okuması, aynı kullanıcı ve aynı bit sayısı; olmazsa null).</summary>
    public static string? CommandLine(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
            if (h == IntPtr.Zero) return null;
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;
            if (!ReadPtr(h, pbi.PebBaseAddress + 0x20, out var parms)) return null;           // PEB.ProcessParameters
            var us = new byte[16];
            if (!ReadProcessMemory(h, parms + 0x70, us, 16, out _)) return null;               // CommandLine UNICODE_STRING
            var len = BitConverter.ToUInt16(us, 0);
            var buf = new IntPtr(BitConverter.ToInt64(us, 8));
            if (len == 0 || buf == IntPtr.Zero || len > 32768) return null;
            var data = new byte[len];
            if (!ReadProcessMemory(h, buf, data, len, out _)) return null;
            return Encoding.Unicode.GetString(data);
        }
        catch (Exception) { return null; }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    private static bool ReadPtr(IntPtr h, IntPtr addr, out IntPtr value)
    {
        var b = new byte[8];
        var ok = ReadProcessMemory(h, addr, b, 8, out _);
        value = ok ? new IntPtr(BitConverter.ToInt64(b, 0)) : IntPtr.Zero;
        return ok && value != IntPtr.Zero;
    }

    // ------------------------------------------------------------------ TCP tablosu
    /// <summary>İstemci portu clientPort, sunucu portu serverPort olan loopback bağlantının istemci tarafındaki sürecin numarası.</summary>
    public static int? FindPeerPid(int clientPort, int serverPort)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            size += 4096;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (rc == 122) continue; // ERROR_INSUFFICIENT_BUFFER: tablo büyüdü
                if (rc != 0) return null;
                var count = Marshal.ReadInt32(buf);
                var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                    if (Port(row.localPort) == clientPort && Port(row.remotePort) == serverPort && row.owningPid != 0 && (row.localAddr & 0xFF) == 127)
                        return (int)row.owningPid;
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return null;
    }

    private static int Port(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));

    // ------------------------------------------------------------------ Win32
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010, JOB_OBJECT_QUERY = 0x0004, TH32CS_SNAPPROCESS = 2;
    private const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID { public uint state, localAddr, localPort, remoteAddr, remotePort, owningPid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int cls, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attrs, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION info, int size, out int ret);
}
