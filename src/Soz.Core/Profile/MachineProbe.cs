using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Soz.Core.Profile;

public interface IMachineProbe { MachineProfile Read(DateTime now); }

/// <summary>Local OS metadata only. No process query, shell, network, elevation or new dependency.</summary>
public sealed class MachineProbe : IMachineProbe
{
    // Test seam and source order. A provider failure must still allow the next source.
    public IReadOnlyList<Func<IEnumerable<string>>> GpuSources { get; init; } = [RegistryGpuNames,DisplayGpuNames];
    public string? ReadGpuName()
    {
        foreach(var source in GpuSources)
        {
            try{var name=CleanGpuNames(source());if(name is not null)return name;}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception){}
        }
        return null;
    }
    private static string? CleanGpuNames(IEnumerable<string> names)
    {
        var result=names.Where(n=>!string.IsNullOrWhiteSpace(n)&&n.Length<256).Select(n=>n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return result.Length==0?null:string.Join(", ",result);
    }
    private static IEnumerable<string> RegistryGpuNames()
    {
        if(!OperatingSystem.IsWindows())return [];
        using var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        var result=new List<string>();
        if(key is not null)foreach(var name in key.GetSubKeyNames())
        {using var d=key.OpenSubKey(name);if(d?.GetValue("DriverDesc") is string value)result.Add(value);}
        return result;
    }
    private static IEnumerable<string> DisplayGpuNames()
    {
        if(!OperatingSystem.IsWindows())return [];
        var result=new List<string>();
        for(uint i=0;i<32;i++)
        {
            var device=new DisplayDevice{Size=Marshal.SizeOf<DisplayDevice>()};
            if(!EnumDisplayDevices(null,i,ref device,0))break;
            if((device.Flags&8)==0&&!string.IsNullOrWhiteSpace(device.Description))result.Add(device.Description);
        }
        return result;
    }
    public MachineProfile Read(DateTime now)
    {
        // Native/registry calls normally return immediately. A slow device cannot hold the scan open.
        var source = new ProfileEvidence("machine", "Windows API / registry", null, now);
        long? memory = null; string? cpu = null, gpu = null; int? cores = null, logical = null;
        var disks = new List<DiskProfile>();
        var sync = new object();
        var jobs = new[] {
            Task.Run(() => { long? m = null; if (OperatingSystem.IsWindows()) { var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() }; if (GlobalMemoryStatusEx(ref s)) m = (long)s.TotalPhysical; } lock(sync) memory = m; }),
            Task.Run(() => { string? c = null; int? count = null; if (OperatingSystem.IsWindows()) {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                c = (k?.GetValue("ProcessorNameString") as string)?.Trim(); count = PhysicalCores(); }
                lock(sync) { cpu = c; cores = count; logical = Environment.ProcessorCount; } }),
            Task.Run(() => { var name=ReadGpuName();lock(sync)gpu=name; }),
            Task.Run(() => { foreach(var d in DriveInfo.GetDrives()) { try { if(d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var row = new DiskProfile(d.Name, d.TotalSize, d.AvailableFreeSpace, source, now); lock(sync) disks.Add(row); }
                catch(Exception e) when(e is IOException or UnauthorizedAccessException) { } } })
        };
        try { Task.WaitAll(jobs, TimeSpan.FromMilliseconds(1700)); } catch (AggregateException) { /* partial results remain useful */ }
        lock (sync)
        {
            var facts = new[] { new ProfileFact("memoryBytes", memory ?? (object)"bilinmiyor", source, now, "gozlem"),
                new("cpuName", cpu ?? "bilinmiyor", source, now, "gozlem"), new("physicalCores", cores ?? (object)"bilinmiyor", source, now, "gozlem"),
                new("logicalCores", logical ?? (object)"bilinmiyor", source, now, "gozlem"), new("gpuName", gpu ?? "bilinmiyor", source, now, "gozlem") };
            return new(memory, cpu, cores, logical, gpu, disks.ToArray(), facts);
        }
    }

    private static int? PhysicalCores()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        if (length == 0 || length > 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref length)) return null;
            var count = 0;
            for (var offset = 0; offset + 8 <= length;)
            {
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || offset + size > length) return null;
                if (Marshal.ReadInt32(buffer, offset) == 0) count++;
                offset += size;
            }
            return count;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string? Name;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string? Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string? Id;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string? Key;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device,uint number,ref DisplayDevice display,uint flags);
}
