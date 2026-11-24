using System.Runtime.InteropServices;

namespace Soz.Shell;

internal enum PasteOutcome { NoData, Empty, CopiedOnly, Pasted, Failed }

/// <summary>
/// Paketi alır ve (a) yalnız panoya koyar ya da (b) öndeki pencereye Ctrl+V gönderir.
/// Öndeki pencere kabuğun kendisiyse yapıştırma yok, yalnız kopyalama.
/// </summary>
internal sealed class PacketPaster
{
    private readonly IApiClient _api;
    private readonly IClipboardBackend _clip;
    private readonly ClipboardKeeper _keeper;

    public PacketPaster(IApiClient api, IClipboardBackend clip, ClipboardKeeper keeper)
    {
        _api = api; _clip = clip; _keeper = keeper;
    }

    /// <summary>Paket metnini yalnız okur (pano ve tuş yok). Duman sınaması bunu kullanır.</summary>
    public async Task<PacketInfo?> FetchAsync(long? projectId, CancellationToken ct = default) =>
        await _api.GetPacketAsync(projectId, ct).ConfigureAwait(true);

    /// <summary>Panoya koyar ve orada bırakır (geri koyma yok).</summary>
    public async Task<PasteOutcome> CopyAsync(long? projectId)
    {
        var p = await FetchAsync(projectId).ConfigureAwait(true);
        if (p == null) return PasteOutcome.NoData;
        if (string.IsNullOrEmpty(p.Text)) return PasteOutcome.Empty;
        try { _clip.SetText(p.Text); return PasteOutcome.CopiedOnly; }
        catch (Exception ex) { Log.Error("pano yazma", ex); return PasteOutcome.Failed; }
    }

    /// <summary>Kısayol akışı: eski panoyu sakla, paketi koy, Ctrl+V gönder, 1,5 sn sonra koşullu geri koy.</summary>
    public async Task<PasteOutcome> PasteAsync(long? projectId)
    {
        var p = await FetchAsync(projectId).ConfigureAwait(true);
        if (p == null) return PasteOutcome.NoData;
        if (string.IsNullOrEmpty(p.Text)) return PasteOutcome.Empty;

        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || Native.IsOwnWindow(fg))
        {
            try { _clip.SetText(p.Text); return PasteOutcome.CopiedOnly; }
            catch (Exception ex) { Log.Error("pano yazma", ex); return PasteOutcome.Failed; }
        }

        try
        {
            _keeper.Place(p.Text);
            await WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(true);
            // bekleme sırasında öne kabuğun kendi penceresi geldiyse yapıştırma
            var fg2 = Native.GetForegroundWindow();
            if (fg2 == IntPtr.Zero || Native.IsOwnWindow(fg2))
            {
                _keeper.Abandon(); // paket panoda kalır, eski içerik geri konmaz
                return PasteOutcome.CopiedOnly;
            }
            var ok = SendCtrlV();
            _keeper.ScheduleRestore();
            return ok ? PasteOutcome.Pasted : PasteOutcome.Failed;
        }
        catch (Exception ex)
        {
            Log.Error("yapıştırma", ex);
            try { _keeper.RestoreNow(); } catch { }
            return PasteOutcome.Failed;
        }
    }

    internal static async Task WaitForModifiersReleasedAsync(TimeSpan max)
    {
        var deadline = DateTime.UtcNow + max;
        while (DateTime.UtcNow < deadline && AnyHeld()) await Task.Delay(20).ConfigureAwait(true);
    }

    private static bool AnyHeld()
    {
        // Ctrl, Alt (Menu), Shift, Win ve kısayolların ana tuşları (K, J, Space) dahil bütün tuşlar bırakılmış olmalı.
        foreach (var vk in new[] { 0x11, 0x12, 0x10, 0x5B, 0x5C, 0x4B, 0x4A, 0x20 })
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    internal static bool SendCtrlV()
    {
        INPUT_Helper.Build(out var inputs);
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        return sent == inputs.Length;
    }

    private static class INPUT_Helper
    {
        public static void Build(out Native.INPUT[] inputs)
        {
            Native.INPUT Key(ushort vk, bool up) => new()
            {
                type = Native.INPUT_KEYBOARD,
                U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } },
            };
            inputs = [Key(Native.VK_CONTROL, false), Key(Native.VK_V, false), Key(Native.VK_V, true), Key(Native.VK_CONTROL, true)];
        }
    }
}
