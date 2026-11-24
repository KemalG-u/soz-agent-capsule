using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Soz.Core.Profile;

namespace Soz.Shell;

/// <summary>
/// --olc-json &lt;dosya&gt; [--olc-bekle &lt;sn&gt;] [--olc-sure &lt;sn&gt;] [--olc-panel]: gerçek açılışı yapar (canlı yenileme zamanlayıcıları dahil),
/// ısınma süresi kadar bekler, sonra ölçüm penceresi boyunca her saniye kendi süreç ağacını (kabuk, kapsül, panel açıksa WebView2 süreçleri) örnekler.
/// Yazılan değerler: çalışma kümesi, özel çalışma kümesi, özel bayt ve işlemci süresi (süreç ve grup bazında), pencere boyunca saniyelik toplam işlemci payının
/// ortalaması, 95. yüzdeliği ve en yükseği. Gerçek veri klasörüne dokunulmaz (--data verilmelidir).
/// Dikte köprüsü yoklaması ölçümde çalışmaz (kullanıcının gerçek dikte programına bağlanır); payı ayrıca ölçülür.
/// </summary>
internal static class Olcum
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PMC2
    {
        public uint cb, PageFaultCount; public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize; public ulong SharedCommitUsage;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr h, ref PMC2 c, uint cb);

    private sealed record Ornek(int Pid, string Ad, double WsMb, double PwsMb, double OzelMb, double CpuMs);

    private static Ornek? Al(Process p)
    {
        try
        {
            var c = new PMC2 { cb = (uint)Marshal.SizeOf<PMC2>() };
            if (!GetProcessMemoryInfo(p.Handle, ref c, c.cb)) return null;
            return new Ornek(p.Id, p.ProcessName, Math.Round(c.WorkingSetSize / 1048576.0, 2), Math.Round(c.PrivateWorkingSetSize / 1048576.0, 2),
                Math.Round(c.PrivateUsage / 1048576.0, 2), p.TotalProcessorTime.TotalMilliseconds);
        }
        catch { return null; }
    }

    /// <summary>Kabuk sürecinin bütün alt süreçleri (kapsül, WebView2 ve onun çocukları).</summary>
    private static List<Process> Agac(int kokPid)
    {
        var sonuc = new List<Process>();
        try
        {
            var tree = Win32ProcessInspector.SnapshotProcessTree();
            var kuyruk = new Queue<int>(); kuyruk.Enqueue(kokPid);
            var gorulen = new HashSet<int> { kokPid };
            while (kuyruk.Count > 0)
            {
                var cur = kuyruk.Dequeue();
                try { sonuc.Add(Process.GetProcessById(cur)); } catch { }
                foreach (var n in tree.Values.Where(n => n.ParentPid == cur && gorulen.Add(n.Pid))) kuyruk.Enqueue(n.Pid);
            }
        }
        catch { }
        return sonuc;
    }

    private static string Grup(Ornek o, int kabukPid, int? kapsulPid)
        => o.Pid == kabukPid ? "kabuk" : o.Pid == kapsulPid ? "kapsul" : o.Ad.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase) ? "webview2" : "diger";

    private static double Yuzdelik(List<double> l, double q)
    {
        if (l.Count == 0) return -1;
        var s = l.OrderBy(x => x).ToList();
        return Math.Round(s[Math.Min(s.Count - 1, (int)Math.Ceiling(q * s.Count) - 1)], 3);
    }

    public static async Task RunAsync(ShellContext ctx)
    {
        var o = ctx.Opt; var r = new Dictionary<string, object?>();
        try
        {
            var kabukPid = Environment.ProcessId;
            r["kapsul"] = ctx.Kapsul != null ? "yeni" : ctx.Capsule != null ? "eski" : "yok";
            r["panel"] = o.OlcPanel ? "acik" : "kapali";
            r["isinma_sn"] = o.OlcBekleSn; r["pencere_sn"] = o.OlcSureSn;
            r["mantiksal_cekirdek"] = Environment.ProcessorCount;
            r["not"] = "cpu yuzdeleri tek cekirdek uzerinden (100 = bir cekirdegin tamami); makine payi icin cekirdek sayisina bol";
            await Task.Delay(TimeSpan.FromSeconds(o.OlcBekleSn)).ConfigureAwait(true);
            if (o.OlcPanel)
            {
                ctx.OpenPanel(odakAl: false); // ölçüm: kullanıcı eylemi yok, odak alınmaz
                await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(true); // sayfa ve WebView2 süreçleri otursun
            }
            var kapsulPid = ctx.Kapsul?.Surec?.Id;
            r["kapsul_pid"] = kapsulPid;

            GetSystemTimes(out var si0, out var sk0, out var su0);
            lock (ctx.Sayac) ctx.Sayac.Clear(); // sayaçlar yalnız ölçüm penceresini saysın
            Dictionary<int, double> ib;
            using (var b0 = Process.GetCurrentProcess()) ib = b0.Threads.Cast<ProcessThread>().ToDictionary(t => t.Id, t => SafeCpu(t));
            // pencere: her saniye örnekle
            var ilk = new Dictionary<int, Ornek>(); var son = new Dictionary<int, Ornek>();
            var saniyelik = new List<double>(); var onceki = new Dictionary<int, double>();
            var enYuksekBellek = new Dictionary<int, Ornek>();
            for (var s = 0; s <= o.OlcSureSn; s++)
            {
                var toplamFark = 0.0;
                foreach (var p in Agac(kabukPid))
                {
                    using (p)
                    {
                        if (Al(p) is not { } a) continue;
                        if (!ilk.ContainsKey(a.Pid)) ilk[a.Pid] = a;
                        son[a.Pid] = a;
                        if (!enYuksekBellek.TryGetValue(a.Pid, out var m) || a.PwsMb > m.PwsMb) enYuksekBellek[a.Pid] = a;
                        if (onceki.TryGetValue(a.Pid, out var c0)) toplamFark += a.CpuMs - c0;
                        onceki[a.Pid] = a.CpuMs;
                    }
                }
                if (s > 0) saniyelik.Add(toplamFark / 1000.0 * 100); // 1 sn aralık: ms / 1000 ms * 100 = tek çekirdek yüzdesi
                if (s < o.OlcSureSn) await Task.Delay(1000).ConfigureAwait(true);
            }

            GetSystemTimes(out var si1, out var sk1, out var su1);
            var sToplam = (double)((sk1 - sk0) + (su1 - su0)); // çekirdek zamanı idle dahil
            r["makine_mesgul_yuzde_pencere"] = sToplam <= 0 ? -1 : Math.Round((sToplam - (si1 - si0)) / sToplam * 100, 1);
            var surecler = son.Values.Select(b =>
            {
                var a = ilk[b.Pid];
                return new Dictionary<string, object?>
                {
                    ["grup"] = Grup(b, kabukPid, kapsulPid), ["ad"] = b.Ad, ["pid"] = b.Pid,
                    ["calisma_kumesi_mb"] = b.WsMb, ["ozel_calisma_kumesi_mb"] = b.PwsMb, ["ozel_bayt_mb"] = b.OzelMb,
                    ["cpu_ms_pencere"] = Math.Round(b.CpuMs - a.CpuMs, 1),
                    ["cpu_yuzde_tek_cekirdek"] = Math.Round((b.CpuMs - a.CpuMs) / (o.OlcSureSn * 1000.0) * 100, 3),
                };
            }).OrderBy(x => (string)x["grup"]!).ToList();
            r["surecler"] = surecler;
            r["gruplar"] = surecler.GroupBy(x => (string)x["grup"]!).ToDictionary(g => g.Key, g => (object)new Dictionary<string, object?>
            {
                ["surec_sayisi"] = g.Count(),
                ["calisma_kumesi_mb"] = Math.Round(g.Sum(x => (double)x["calisma_kumesi_mb"]!), 2),
                ["ozel_calisma_kumesi_mb"] = Math.Round(g.Sum(x => (double)x["ozel_calisma_kumesi_mb"]!), 2),
                ["ozel_bayt_mb"] = Math.Round(g.Sum(x => (double)x["ozel_bayt_mb"]!), 2),
                ["cpu_yuzde_tek_cekirdek"] = Math.Round(g.Sum(x => (double)x["cpu_yuzde_tek_cekirdek"]!), 3),
            });
            r["saniyelik_cpu_yuzde_tek_cekirdek"] = saniyelik.Select(x => Math.Round(x, 1)).ToList();
            lock (ctx.Sayac) r["kabuk_sayaclar"] = ctx.Sayac.ToDictionary(k => k.Key, k => new { adet = k.Value.N, toplam_ms = k.Value.Ms });
            r["arayuz_is_parcacigi_kimligi"] = GetCurrentThreadId();
            r["toplam"] = new Dictionary<string, object?>
            {
                ["calisma_kumesi_mb"] = Math.Round(surecler.Sum(x => (double)x["calisma_kumesi_mb"]!), 2),
                ["ozel_calisma_kumesi_mb"] = Math.Round(surecler.Sum(x => (double)x["ozel_calisma_kumesi_mb"]!), 2),
                ["ozel_bayt_mb"] = Math.Round(surecler.Sum(x => (double)x["ozel_bayt_mb"]!), 2),
                ["cpu_ortalama_yuzde_tek_cekirdek"] = saniyelik.Count == 0 ? -1 : Math.Round(saniyelik.Average(), 3),
                ["cpu_p95_yuzde_tek_cekirdek"] = Yuzdelik(saniyelik, 0.95),
                ["cpu_en_yuksek_saniye_yuzde_tek_cekirdek"] = saniyelik.Count == 0 ? -1 : Math.Round(saniyelik.Max(), 3),
            };

            // kabuk iş parçacıkları: pencere boyunca en çok işlemci harcayanlar (adları bilinmez, kimlikleriyle)
            try
            {
                using var ben = Process.GetCurrentProcess();
                var fark = ben.Threads.Cast<ProcessThread>().Select(t => (t.Id, Ms: SafeCpu(t) - (ib.TryGetValue(t.Id, out var v) ? v : 0))).Where(x => x.Ms > 0).OrderByDescending(x => x.Ms).Take(8).ToList();
                r["kabuk_en_cok_cpu_is_parcacigi_pencere_ms"] = fark.Select(x => new { is_parcacigi = x.Id, ms = Math.Round(x.Ms, 1) }).ToList();
            }
            catch { }
        }
        catch (Exception ex) { r["hata"] = ex.Message; }
        finally
        {
            try { File.WriteAllText(o.OlcPath!, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true })); } catch { }
            ctx.ExitApp();
        }
    }

    private static double SafeCpu(ProcessThread t) { try { return t.TotalProcessorTime.TotalMilliseconds; } catch { return 0; } }
}
