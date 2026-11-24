using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;

namespace Soz.Shell;

/// <summary>
/// --smoke: gerçek açılışı yapar, ölçer, JSON yazar, temiz kapanır. Gerçek panoya yazmaz, tuş göndermez,
/// dikte köprüsünü yalnız --dictation-path (sahte program) verilmişse çağırır.
/// </summary>
internal static class Smoke
{
    public static async Task RunAsync(ShellContext ctx)
    {
        var opt = ctx.Opt;
        var r = new Dictionary<string, object?>();
        using var watchdog = new System.Threading.Timer(_ =>
        {
            try { r["errors"] = ctx.Errors.Append("duman zaman aşımı (watchdog)").ToArray(); Write(opt.SmokePath!, r); } catch { }
            Environment.Exit(3);
        }, null, TimeSpan.FromSeconds(25), Timeout.InfiniteTimeSpan);

        try
        {
            r["serverUrl"] = ctx.Server?.Url;
            r["fakeApi"] = opt.FakeApi;
            r["dataDir"] = ctx.DataDir;
            r["priorServerFile"] = ctx.PriorServerNote;

            // sunucu sağlığı
            var healthOk = false;
            if (ctx.Server != null)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    using var resp = await http.GetAsync(ctx.Server.Url + "/api/health").ConfigureAwait(true);
                    healthOk = resp.IsSuccessStatusCode;
                }
                catch (Exception ex) { ctx.Errors.Add("health: " + ex.Message); }
            }
            r["healthOk"] = healthOk;

            // kapsül: gösterilmeden önce / sonra öndeki pencere
            var before = Native.GetForegroundWindow();
            ctx.ShowCapsule();
            await Task.Delay(700).ConfigureAwait(true);
            var after = Native.GetForegroundWindow();
            r["foregroundBefore"] = Hex(before);
            r["foregroundAfter"] = Hex(after);

            // yeni kapsül (--kapsul yeni): alt süreç ayakta mı, çökerse bir kez yeniden başlayıp eski kapsüle düşüyor mu (--kapsul-ek ile kısa ömürlü çalıştırılır)
            if (opt.KapsulTuru != null)
            {
                await Task.Delay(opt.KapsulEk != null ? 9000 : 1500).ConfigureAwait(true);
                var y = ctx.Kapsul;
                if (y?.Calisiyor == true) { y.Flash("Duman: kapsül cevabı"); await Task.Delay(300).ConfigureAwait(true); }
                r["yeniKapsul"] = new { istenen = opt.KapsulTuru, calisiyor = y?.Calisiyor ?? false, pid = y?.Pid, yenidenBaslatma = y?.Politika.YenidenBaslatma, eskiKapsulAcik = ctx.Capsule is { IsHandleCreated: true } };
            }

            var cap = ctx.Capsule;
            r["capsuleCreated"] = cap is { IsHandleCreated: true };
            if (cap is { IsHandleCreated: true })
            {
                var ex = Native.GetExStyle(cap.Handle);
                r["capsuleExStyle"] = "0x" + ex.ToString("X8");
                r["noActivate"] = (ex & Native.WS_EX_NOACTIVATE) != 0;
                r["toolWindow"] = (ex & Native.WS_EX_TOOLWINDOW) != 0;
                r["topMost"] = (ex & Native.WS_EX_TOPMOST) != 0;
                r["layered"] = (ex & Native.WS_EX_LAYERED) != 0;
                var lp = (IntPtr)(Native.HTCLIENT | (Native.WM_LBUTTONDOWN << 16));
                r["mouseActivateResult"] = Native.SendMessage(cap.Handle, Native.WM_MOUSEACTIVATE, cap.Handle, lp).ToInt32(); // 3 = MA_NOACTIVATE
                var b = cap.NativeBounds;
                r["capsuleBounds"] = new { x = b.X, y = b.Y, width = b.Width, height = b.Height };
                r["capsuleVisible"] = cap.Visible;
                r["capsuleOnScreen"] = ScreenMath.WorkAreas().Any(w => w.Contains(cap.BodyScreenRect));
                r["capsuleScale"] = cap.CurrentScale;
                r["capsuleTooltip"] = cap.Model.Tooltip();
                var cl = cap.CurrentLayout;
                if (cl != null)
                {
                    var c = new Point((int)(cl.NewJob.X + cl.NewJob.Width / 2), (int)(cl.NewJob.Y + cl.NewJob.Height / 2));
                    r["capsuleNewJobButton"] = new { present = cl.NewJob.Width > 0 && cl.HitTest(c) == HoverPart.NewJob, rect = new { x = (int)cl.NewJob.X, y = (int)cl.NewJob.Y, width = (int)cl.NewJob.Width, height = (int)cl.NewJob.Height } };
                }
                else r["capsuleNewJobButton"] = new { present = false };
                r["capsuleNewJobTip"] = Strings.Get(ctx.NewJobInfo is { Registered: true } ? "tip.newjob" : "tip.newjob.nokey", ctx.NewJobInfo?.Display ?? "");
            }
            else r["noActivate"] = false;

            // bir kez daha: rozet güncellemesi ve vurgu odak çalmamalı
            if (cap != null) { var cm = cap.Model; cap.SetModel(cm with { Waiting = cm.Waiting + 1 }); }
            cap?.Flash("Duman sınaması", 400);
            await Task.Delay(500).ConfigureAwait(true);
            var after2 = Native.GetForegroundWindow();
            r["foregroundAfterUpdates"] = Hex(after2);
            r["foregroundIsOwnProcess"] = Native.IsOwnWindow(after2);
            r["focusStolen"] = before != after || after != after2 || Native.IsOwnWindow(after2);

            r["hotkeyRegistered"] = ctx.Hotkey.IsRegistered;
            r["hotkey"] = ctx.Hotkey.Display;
            if (!ctx.Hotkey.IsRegistered) r["hotkeyFailureReason"] = ctx.Hotkey.FailureReason;
            r["trayCreated"] = ctx.TrayCreated;

            // iki genel kısayol: kayıt/düşme sonucu ve AltGr (Ctrl+Alt = AltGr) ölçümü
            var nj = ctx.NewJobInfo;
            r["newJobHotkey"] = nj == null ? null : new { requested = nj.Requested, registered = nj.Registered, display = nj.Display, fellBack = nj.FellBack, reason = nj.Reason, failureReason = nj.FailureReason };
            var pm = ctx.PacketMeasure;
            r["packetHotkey"] = new
            {
                requested = ctx.PacketRequested, registered = ctx.Hotkey.IsRegistered, display = ctx.Hotkey.Display, fellBack = false,
                reason = pm?.ConflictReason ?? (ctx.Hotkey.IsRegistered ? null : ctx.Hotkey.FailureReason),
                layoutConflict = pm?.Conflict ?? false,
            };
            var probe = ctx.LayoutProbe;
            var jm = HotkeyPolicy.Measure("Ctrl+Alt+J", probe);
            var km = HotkeyPolicy.Measure("Ctrl+Alt+K", probe);
            var qm = HotkeyPolicy.Measure("Ctrl+Alt+Q", probe); // ölçümün çalıştığını göstermek için kontrol tuşu (Türkçe Q: AltGr+Q = @)
            static object Entry(HotkeyMeasure m, int i) => m.Hits.Count <= i ? new { producesChar = false, codePoint = (string?)null } :
                new { producesChar = m.Hits[i].ProducesText, codePoint = m.Hits[i].CodePoint == 0 ? null : HotkeyMeasure.CodePointText(m.Hits[i].CodePoint) };
            r["layoutProbe"] = probe.Layouts.Select((l, i) => new { hkl = l.Hkl, klid = l.Klid, name = l.Name, culture = l.Culture, ctrlAltJ = Entry(jm, i), ctrlAltK = Entry(km, i), ctrlAltQ_control = Entry(qm, i) }).ToArray();

            // etkin proje + paket metni (pano ve tuş yok)
            var ap = ctx.ResolveActiveNow();
            r["activeProject"] = ap == null ? null : new { id = ap.Id, name = ap.Name, source = ap.Source };
            try
            {
                var pk = await ctx.Paster.FetchAsync(ap?.Id).ConfigureAwait(true);
                r["packetCopy"] = pk == null
                    ? new { ok = false, note = "veri yok (API cevap vermedi)", chars = 0, decisionCount = 0 }
                    : new { ok = true, note = pk.Text.Length == 0 ? "paket boş" : "metin alındı", chars = pk.Text.Length, decisionCount = pk.DecisionCount };
            }
            catch (Exception ex) { ctx.Errors.Add("paket: " + ex.Message); r["packetCopy"] = new { ok = false, note = ex.Message, chars = 0, decisionCount = 0 }; }

            // gerçek API: yoklama yolu (özet, projeler, oturumlar), ayar yazma (kısmi PUT) ve SSE akışı
            try
            {
                var sum = await ctx.Api.GetSummaryAsync().ConfigureAwait(true);
                var prj = await ctx.Api.GetProjectsAsync().ConfigureAwait(true);
                var ses = await ctx.Api.GetSessionsAsync(24).ConfigureAwait(true);
                r["poll"] = new
                {
                    summaryOk = sum != null, projects = prj.Count, sessions = ses.Count,
                    waiting = sum?.Waiting, stalled = sum?.Stalled, working = sum?.Working, proposed = sum?.Proposed,
                    states = ses.Select(s => s.State).Distinct().ToArray(),
                };

                // /api/now: kapsül rozetinin kaynağı
                var nowInfo = await ctx.Api.GetNowAsync().ConfigureAwait(true);
                r["now"] = new { ok = nowInfo != null, waiting = nowInfo?.Waiting, stalled = nowInfo?.Stalled, jobsFailed = nowInfo?.JobsFailed };

                // ayar: konumu yaz, geri oku, eski değeri geri koy (yalnız iki anahtar, sayı türünde)
                var setBefore = await ctx.Api.GetSettingsAsync().ConfigureAwait(true);
                var putOk = await ctx.Api.PutSettingsAsync(new Dictionary<string, object?> { ["capsuleX"] = 123, ["capsuleY"] = 45 }).ConfigureAwait(true);
                var setAfter = await ctx.Api.GetSettingsAsync().ConfigureAwait(true);
                var roundTrip = setAfter.GetValueOrDefault("capsuleX") == "123" && setAfter.GetValueOrDefault("capsuleY") == "45";
                await ctx.Api.PutSettingsAsync(new Dictionary<string, object?>
                {
                    ["capsuleX"] = int.TryParse(setBefore.GetValueOrDefault("capsuleX"), out var bx) ? bx : null,
                    ["capsuleY"] = int.TryParse(setBefore.GetValueOrDefault("capsuleY"), out var by) ? by : null,
                }).ConfigureAwait(true);
                r["settingsPut"] = new { ok = putOk, roundTrip, otherSettingsKept = setAfter.GetValueOrDefault("packetBudget") == setBefore.GetValueOrDefault("packetBudget") };

                // SSE: akışa bağlan, sunucudan bir olay yayınla, kabuğun ayrıştırıcısı yakalasın
                var connected = false; string? got = null;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var stream = ctx.Api.StreamEventsAsync(k => got ??= k, () => connected = true, cts.Token);
                for (var i = 0; i < 40 && !connected; i++) await Task.Delay(100).ConfigureAwait(true);
                ctx.Server?.Hub.Publish("session", new { waiting = 0 });
                for (var i = 0; i < 40 && got == null; i++) await Task.Delay(100).ConfigureAwait(true);
                var subs = ctx.Server?.Hub.SubscriberCount;
                cts.Cancel();
                try { await stream.ConfigureAwait(true); } catch (OperationCanceledException) { } catch (Exception) { }
                r["sse"] = new { connected, firstEvent = got, subscribersWhileOpen = subs };
            }
            catch (Exception ex) { ctx.Errors.Add("api yolu: " + ex.Message); }

            // dikte köprüsü yalnız sahte yol verildiyse
            if (opt.DictationPathOverride != null)
            {
                var dr = ctx.Dictation.Toggle();
                var exit = (int?)null;
                if (dr.Process != null)
                {
                    try { if (dr.Process.WaitForExit(3000)) exit = dr.Process.ExitCode; } catch { }
                }
                r["dictationBridge"] = new { invoked = dr.Started, path = dr.Path, message = dr.Message, exitCode = exit };
            }
            else r["dictationBridge"] = new { invoked = (bool?)null, note = "atlandı: sahte yol (--dictation-path) verilmedi; gerçek program çalıştırılmaz" };

            // varsayılan dikte yolu bulma mantığı: yalnız File.Exists
            r["dictationDefaultPathExists"] = new DictationBridge(() => null).ResolvePath() != null;

            // panel (isteğe bağlı): WebView2 başlat, adrese git; pencere GÖSTERİLMEZ
            if (opt.SmokePanel && ctx.Server != null)
            {
                using var panel = new PanelForm(ctx.Server.Url + "/", Path.Combine(ctx.DataDir, "webview2"), ctx.Pal);
                var pr = await panel.ProbeAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(true);
                r["panelProbe"] = new { ok = pr.Ok, title = pr.Title, error = pr.Error };

                // soğuk başlangıç: /#/isler?yeni=1 (panel yine gösterilmez); iş kutusu odağı arayüz hazırsa dolar, değilse "" (hata değil)
                using var panel2 = new PanelForm(ctx.Server.Url + "/", Path.Combine(ctx.DataDir, "webview2"), ctx.Pal);
                var nf = await panel2.ProbeNewJobFocusAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(true);
                r["panelNewJobFocus"] = nf.Focus;
                r["panelNewJobProbe"] = new { ok = nf.Ok, error = nf.Error };
            }

            await Task.Delay(300).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ctx.Errors.Add("duman: " + ex);
            Log.Error("duman", ex);
        }
        finally
        {
            r["errors"] = ctx.Errors.ToArray();
            try { Write(opt.SmokePath!, r); } catch (Exception ex) { Log.Error("duman yazma", ex); }
            ctx.ExitApp();
        }
    }

    private static string Hex(IntPtr h) => "0x" + h.ToInt64().ToString("X");

    private static void Write(string path, Dictionary<string, object?> data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
