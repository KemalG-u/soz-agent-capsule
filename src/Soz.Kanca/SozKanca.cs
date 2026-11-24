using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

[assembly: AssemblyTitle("Söz Hızlı Kanca Rölesi")]
[assembly: AssemblyDescription("Söz yerel kanca aktarıcısı")]
[assembly: AssemblyCompany("Oluk")]
[assembly: AssemblyProduct("Söz")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Oluk")]
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]

// Söz kanca rölesi (soz-kanca.exe). .NET Framework 4 (Windows'ta hazır gelir): çalışma zamanı kurulumu ve ağır .NET 10 başlangıcı yok.
// Derleme: derle.sh (Framework csc, aynı klasör). Kullanım: soz-kanca hook <olay> [--data <veri klasörü>]  (olay JSON'u stdin'den).
//
// Görevi: olayı çalışan Söz sunucusuna yetkili geri döngü isteğiyle ilet, cevabı stdout'a yaz. KULLANICININ İŞİNİ ASLA YAVAŞLATMAZ ya da DURDURMAZ:
//   - Söz kapalıysa (veri klasöründe sunucu.json yok, ya da yazan süreç ölü) hiçbir bağlantı denemeden çıkış 0.
//   - Sunucu cevap vermezse / hata dönerse / jeton reddedilirse çıkış 0, çıktı yok. (Eski sürüm burada ağır soz.exe'yi açıyordu: 200-700 ms.)
//   - Her koşulda çıkış kodu 0, hata çıktısı yok.
namespace Soz.Kanca
{
    internal static class Program
    {
        private static readonly string[] EnvKeys =
        {
            "CLAUDE_PROJECT_DIR", "SOZ_RUN_ID", "SOZ_JOB_ID",
            "USERPROFILE", "HOME", "TEMP", "TMP", "SOZ_HOOK_PULSE_ALL"
        };

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 2 && string.Equals(args[0], "hook", StringComparison.Ordinal))
                {
                    var ev = args[1];
                    if (ev != "session-start" && ev != "user-prompt-submit" && ev != "stop" && ev != "stop-failure" && ev != "notification" && ev != "pre-tool-use")
                        return 0;

                    string explicitData = null;
                    for (var i = 2; i + 1 < args.Length; i++)
                    {
                        if (args[i] == "--data") explicitData = args[++i];
                    }

                    // Claude olayı stdin'e yazıp kapatır; yazarı kırmamak için her durumda sonuna kadar okunur (veri zaten borudadır, ~1 ms).
                    string rawStdin;
                    using (var sr = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                    {
                        rawStdin = sr.ReadToEnd();
                    }

                    var dataDir = ResolveDataDir(explicitData);
                    int port;
                    if (!ServerIsUp(dataDir, out port)) return 0; // Söz kapalı: anında çık

                    string outText;
                    if (Relay.TryPost(dataDir, port, ev, rawStdin, EnvKeys, out outText))
                    {
                        if (!string.IsNullOrEmpty(outText))
                        {
                            using (var sw = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                            {
                                sw.Write(outText);
                                sw.Flush();
                            }
                        }
                        return 0;
                    }

                    // Ağır yol yalnız bilerek istenirse (deneme/ayıklama): SOZ_HOOK_INPROC=1
                    if (Environment.GetEnvironmentVariable("SOZ_HOOK_INPROC") == "1") return Fallback.RunSozCli(args, rawStdin);
                    return 0;
                }

                return Fallback.RunSozCli(args, null);
            }
            catch
            {
                return 0;
            }
        }

        private static string ResolveDataDir(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return Path.GetFullPath(explicitPath);
            var env = Environment.GetEnvironmentVariable("SOZ_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env))
                return Path.GetFullPath(env);

            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            var parent = Directory.GetParent(baseDir);
            if (parent != null && File.Exists(Path.Combine(parent.FullName, "Update.exe")))
            {
                var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localApp, "Soz", "veri");
            }

            var cur = new DirectoryInfo(baseDir);
            for (var i = 0; i < 5 && cur != null; i++, cur = cur.Parent)
            {
                var candidate = Path.Combine(cur.FullName, "veri");
                if (File.Exists(Path.Combine(candidate, "soz.db")) || File.Exists(Path.Combine(candidate, "sunucu.json")))
                    return candidate;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soz", "veri");
        }

        /// <summary>Söz sunucusu şu an açık mı: sunucu.json var, adresi 127.0.0.1/localhost ve (yazılı) süreç yaşıyor. Dosya okunur, soket açılmaz.</summary>
        private static bool ServerIsUp(string dataDir, out int port)
        {
            port = -1;
            var url = Environment.GetEnvironmentVariable("SOZ_SERVER_URL");
            var pid = 0;
            if (string.IsNullOrWhiteSpace(url))
            {
                var sf = Path.Combine(dataDir, "sunucu.json");
                if (!File.Exists(sf)) return false;
                var json = File.ReadAllText(sf, Encoding.UTF8);
                url = JsonString(json, "url");
                var pidText = JsonNumber(json, "pid");
                if (pidText != null) int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
            }
            if (string.IsNullOrWhiteSpace(url)) return false;
            url = url.Trim().TrimEnd('/');
            string rest = null;
            const string p1 = "http://127.0.0.1:";
            const string p2 = "http://localhost:";
            if (url.StartsWith(p1, StringComparison.OrdinalIgnoreCase)) rest = url.Substring(p1.Length);
            else if (url.StartsWith(p2, StringComparison.OrdinalIgnoreCase)) rest = url.Substring(p2.Length);
            if (rest == null) return false;
            var slash = rest.IndexOf('/');
            if (slash >= 0) rest = rest.Substring(0, slash);
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port <= 0 || port > 65535) return false;
            if (pid > 0 && !Native.ProcessAlive(pid)) return false; // bayat işaret (çökme sonrası kalır)
            return true;
        }

        private static string JsonString(string json, string key)
        {
            var idx = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx + key.Length + 2);
            if (colon < 0) return null;
            var q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            var q2 = json.IndexOf('"', q1 + 1);
            if (q2 <= q1) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static string JsonNumber(string json, string key)
        {
            var idx = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx + key.Length + 2);
            if (colon < 0) return null;
            var i = colon + 1;
            while (i < json.Length && json[i] == ' ') i++;
            var s = i;
            while (i < json.Length && json[i] >= '0' && json[i] <= '9') i++;
            return i > s ? json.Substring(s, i - s) : null;
        }
    }

    /// <summary>Windows süreç yoklaması (OpenProcess + WaitForSingleObject): System.Diagnostics yüklenmeden, mikrosaniyeler içinde.</summary>
    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static bool ProcessAlive(int pid)
        {
            try
            {
                const uint SYNCHRONIZE = 0x00100000;
                var h = OpenProcess(SYNCHRONIZE, false, pid);
                if (h == IntPtr.Zero)
                {
                    // 5 = erişim reddedildi: süreç var ama bizim değil (yaşıyor sayılır); 87 = böyle bir süreç yok.
                    return Marshal.GetLastWin32Error() == 5;
                }
                var r = WaitForSingleObject(h, 0);
                CloseHandle(h);
                return r == 0x102; // WAIT_TIMEOUT: hâlâ çalışıyor
            }
            catch
            {
                return true; // yoklayamadıysak bağlantıyı dene (kapalıysa anında reddedilir)
            }
        }
    }

    /// <summary>
    /// Geri döngü isteği, doğrudan Winsock ile (ws2_32.dll). System.dll / System.Net.Sockets hiç yüklenmez: .NET Framework'te bu yükleme ve
    /// ilk bağlantı istisnası tek başına 30-300 ms tutuyordu. Bağlantı bloklamasızdır (en çok 250 ms), alım zaman aşımı ayarlıdır; hata = false.
    /// </summary>
    internal static class Relay
    {
        [DllImport("ws2_32.dll")] private static extern int WSAStartup(ushort version, byte[] data);
        [DllImport("ws2_32.dll")] private static extern IntPtr socket(int af, int type, int proto);
        [DllImport("ws2_32.dll")] private static extern int connect(IntPtr s, byte[] addr, int len);
        [DllImport("ws2_32.dll")] private static extern int send(IntPtr s, byte[] buf, int len, int flags);
        [DllImport("ws2_32.dll")] private static extern int recv(IntPtr s, byte[] buf, int len, int flags);
        [DllImport("ws2_32.dll")] private static extern int closesocket(IntPtr s);
        [DllImport("ws2_32.dll")] private static extern int ioctlsocket(IntPtr s, int cmd, ref uint argp);
        [DllImport("ws2_32.dll")] private static extern int setsockopt(IntPtr s, int level, int name, ref int val, int len);
        [DllImport("ws2_32.dll")] private static extern int select(int nfds, byte[] read, byte[] write, byte[] except, ref TimeVal timeout);
        [DllImport("ws2_32.dll")] private static extern int WSAGetLastError();
        [DllImport("ws2_32.dll")] private static extern int WSAIoctl(IntPtr s, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr overlapped, IntPtr completion);

        [StructLayout(LayoutKind.Sequential)] private struct TimeVal { public int Sec; public int Usec; }

        private const int AF_INET = 2, SOCK_STREAM = 1, IPPROTO_TCP = 6, SOL_SOCKET = 0xFFFF, SO_RCVTIMEO = 0x1006, SO_SNDTIMEO = 0x1005;
        private const int TCP_NODELAY = 1, FIONBIO = unchecked((int)0x8004667E), WSAEWOULDBLOCK = 10035;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryPost(string dataDir, int port, string eventName, string rawStdin, string[] envKeys, out string responseBody)
        {
            responseBody = null;
            var payloadBytes = Encoding.UTF8.GetBytes(BuildPayloadJson(rawStdin, envKeys));
            var token = ReadToken(dataDir);
            var header = "POST /api/hooks/" + eventName + " HTTP/1.1\r\n"
                + "Host: 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + (token == null ? "" : "X-Soz-Hook-Token: " + token + "\r\n")
                + "Content-Length: " + payloadBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);

            if (WSAStartup(0x0202, new byte[512]) != 0) return false;
            var s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
            if (s == new IntPtr(-1)) return false;
            try
            {
                // Windows kapalı bir kapıya bağlanırken SYN'i yeniden dener ve reddi ~2 sn sonra bildirir. SIO_TCP_INITIAL_RTO (libuv/Node'un yaptığı gibi)
                // yeniden denemeyi kapatır: geri döngüde kapalı kapı ~2 ms'de "reddedildi" olur. Başarısız olursa (eski Windows) bağlantı zaman aşımı yine korur.
                int ioctlBytes;
                WSAIoctl(s, 0x98000011, new byte[] { 0xFF, 0xFF, 0xFE, 0 }, 4, null, 0, out ioctlBytes, IntPtr.Zero, IntPtr.Zero);
                var one = 1;
                setsockopt(s, IPPROTO_TCP, TCP_NODELAY, ref one, 4);
                // bloklamasız bağlan: kapalı kapı anında reddedilir (hata kodu, istisna yok); sessiz bırakan bir kapıda en çok 250 ms beklenir
                uint nb = 1;
                ioctlsocket(s, FIONBIO, ref nb);
                var addr = new byte[16];
                addr[0] = AF_INET; // sin_family (küçük bayt)
                addr[2] = (byte)(port >> 8); addr[3] = (byte)(port & 0xFF); // sin_port ağ sırası
                addr[4] = 127; addr[7] = 1; // 127.0.0.1
                if (connect(s, addr, 16) != 0)
                {
                    if (WSAGetLastError() != WSAEWOULDBLOCK) return false;
                    var wr = NewFdSet(s); var ex = NewFdSet(s);
                    var tv = new TimeVal { Sec = 0, Usec = 250000 };
                    var n = select(0, null, wr, ex, ref tv);
                    if (n <= 0 || FdCount(wr) == 0) return false; // zaman aşımı ya da bağlantı reddedildi
                }
                nb = 0;
                ioctlsocket(s, FIONBIO, ref nb);
                var rcv = eventName == "stop" ? 330000 : 3000;
                var snd = 2000;
                setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, ref rcv, 4);
                setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, ref snd, 4);

                if (!SendAll(s, headerBytes)) return false;
                if (payloadBytes.Length > 0 && !SendAll(s, payloadBytes)) return false;

                using (var ms = new MemoryStream())
                {
                    var buf = new byte[4096];
                    while (true)
                    {
                        var read = recv(s, buf, buf.Length, 0);
                        if (read <= 0) break; // 0 = kapandı, <0 = hata/zaman aşımı
                        ms.Write(buf, 0, read);
                    }
                    return ParseHttpResponse(ms.ToArray(), out responseBody);
                }
            }
            finally { closesocket(s); }
        }

        private static byte[] NewFdSet(IntPtr s)
        {
            // Windows fd_set: u_int fd_count; SOCKET fd_array[64] (dizi işaretçi boyutuna hizalı)
            var off = IntPtr.Size;
            var b = new byte[off + IntPtr.Size * 64];
            b[0] = 1;
            var sb = BitConverter.GetBytes(s.ToInt64());
            Buffer.BlockCopy(sb, 0, b, off, IntPtr.Size);
            return b;
        }

        private static int FdCount(byte[] set) { return BitConverter.ToInt32(set, 0); }

        private static bool SendAll(IntPtr s, byte[] data)
        {
            var sent = 0;
            while (sent < data.Length)
            {
                var chunk = new byte[Math.Min(65536, data.Length - sent)];
                Buffer.BlockCopy(data, sent, chunk, 0, chunk.Length);
                var n = send(s, chunk, chunk.Length, 0);
                if (n <= 0) return false;
                sent += n;
            }
            return true;
        }

        private static string ReadToken(string dataDir)
        {
            try
            {
                var p = Path.Combine(dataDir, "kanca-jeton");
                if (!File.Exists(p)) return null;
                var t = File.ReadAllText(p, Encoding.UTF8).Trim();
                if (t.Length < 16) return null;
                foreach (var c in t) if (c <= ' ' || c > '~') return null; // başlığa yalnız düz ASCII
                return t;
            }
            catch { return null; }
        }

        private static bool ParseHttpResponse(byte[] raw, out string body)
        {
            body = null;
            if (raw == null || raw.Length < 12) return false;
            var sep = FindHeaderEnd(raw);
            if (sep < 0) return false;
            var head = Encoding.ASCII.GetString(raw, 0, sep);
            var firstLineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
            var statusLine = firstLineEnd >= 0 ? head.Substring(0, firstLineEnd) : head;
            if (!statusLine.Contains(" 200") && !statusLine.Contains(" 204")) return false;

            var bodyOffset = sep + 4;
            var bodyLen = raw.Length - bodyOffset;
            if (bodyLen <= 0)
            {
                body = "";
                return true;
            }
            if (head.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                body = DecodeChunked(raw, bodyOffset, bodyLen);
                return true;
            }
            body = Encoding.UTF8.GetString(raw, bodyOffset, bodyLen);
            return true;
        }

        private static int FindHeaderEnd(byte[] b)
        {
            for (var i = 0; i + 3 < b.Length; i++)
            {
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        private static string DecodeChunked(byte[] b, int offset, int length)
        {
            using (var outMs = new MemoryStream())
            {
                var pos = offset;
                var end = offset + length;
                while (pos < end)
                {
                    var lineEnd = -1;
                    for (var i = pos; i + 1 < end; i++)
                    {
                        if (b[i] == 13 && b[i + 1] == 10) { lineEnd = i; break; }
                    }
                    if (lineEnd < 0) break;
                    var hex = Encoding.ASCII.GetString(b, pos, lineEnd - pos).Trim();
                    var semi = hex.IndexOf(';');
                    if (semi >= 0) hex = hex.Substring(0, semi);
                    int chunkSize;
                    if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out chunkSize) || chunkSize <= 0)
                        break;
                    pos = lineEnd + 2;
                    if (pos + chunkSize > end) chunkSize = end - pos;
                    outMs.Write(b, pos, chunkSize);
                    pos += chunkSize + 2;
                }
                return Encoding.UTF8.GetString(outMs.ToArray());
            }
        }

        private static string BuildPayloadJson(string rawStdin, string[] envKeys)
        {
            var sb = new StringBuilder(rawStdin.Length + 256);
            sb.Append("{\"stdin\":");
            AppendJsonString(sb, rawStdin ?? "");
            sb.Append(",\"env\":{");
            var first = true;
            foreach (var k in envKeys)
            {
                var v = Environment.GetEnvironmentVariable(k);
                if (string.IsNullOrEmpty(v)) continue;
                if (!first) sb.Append(',');
                first = false;
                AppendJsonString(sb, k);
                sb.Append(':');
                AppendJsonString(sb, v);
            }
            sb.Append("}}");
            return sb.ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>Yalnız bilerek istendiğinde (SOZ_HOOK_INPROC=1) ya da kanca dışı komutta: ağır soz.exe. Ayrı sınıf: System.Diagnostics yalnız burada yüklenir.</summary>
    internal static class Fallback
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int RunSozCli(string[] args, string alreadyReadStdin)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var sozExe = Path.Combine(baseDir, "soz.exe");
            if (!File.Exists(sozExe)) return 0;

            var psi = new System.Diagnostics.ProcessStartInfo(sozExe, QuoteArgs(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = alreadyReadStdin != null,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                if (proc == null) return 0;
                if (alreadyReadStdin != null)
                {
                    using (var sw = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
                    {
                        sw.Write(alreadyReadStdin);
                        sw.Flush();
                    }
                }
                var outText = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                if (!string.IsNullOrEmpty(outText))
                {
                    using (var outSw = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                    {
                        outSw.Write(outText);
                        outSw.Flush();
                    }
                }
                return proc.ExitCode;
            }
        }

        private static string QuoteArgs(string[] args)
        {
            var parts = new List<string>(args.Length);
            foreach (var a in args)
            {
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                {
                    parts.Add(a);
                }
                else
                {
                    parts.Add("\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                }
            }
            return string.Join(" ", parts);
        }
    }
}
