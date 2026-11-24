using System.Text.Json;

namespace Soz.Shell;

internal enum TextPasteOutcome { Pasted, Copied, Failed, Rejected }

/// <summary>Saf kurallar (Win32 yok): metin kabulü, sonuç kararı ve arayüze giden sonuç iletisi.</summary>
internal static class TextPasteRules
{
    public const int MaxChars = 20000;

    public static bool Acceptable(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= MaxChars;

    /// <summary>
    /// Hedef yok / geçersiz / kendi pencerem → yalnız kopyala; ön pencere hedef olmadı → yalnız kopyala; oldu → yapıştır.
    /// <paramref name="usable"/>: hedef IsWindow ve kabuğun kendi penceresi değil.
    /// </summary>
    public static TextPasteOutcome Decide(string? text, IntPtr target, Func<IntPtr, bool> usable, bool foregroundBecameTarget)
    {
        if (!Acceptable(text)) return TextPasteOutcome.Rejected;
        if (target == IntPtr.Zero || !usable(target)) return TextPasteOutcome.Copied;
        return foregroundBecameTarget ? TextPasteOutcome.Pasted : TextPasteOutcome.Copied;
    }

    public static string OutcomeName(TextPasteOutcome o) => o switch
    {
        TextPasteOutcome.Pasted => "pasted",
        TextPasteOutcome.Copied => "copied",
        TextPasteOutcome.Rejected => "rejected",
        _ => "failed",
    };

    public static string PasteResultJson(TextPasteOutcome o) => JsonSerializer.Serialize(new { type = "paste-result", outcome = OutcomeName(o) });

    public static string DictateResultJson(bool started) => JsonSerializer.Serialize(new { type = "dictate-result", started });
}

/// <summary>
/// Arayüzden gelen metni hatırlanan hedef pencereye yapıştırır: hedefi öne alır, panoya koyar, değiştiriciler bırakılınca Ctrl+V gönderir,
/// 1,5 sn sonra koşullu geri koyar (ClipboardKeeper kuralı). Hedef yoksa ya da öne gelmezse yalnız panoya koyar (geri koyma yok).
/// </summary>
internal sealed class TextPaster
{
    private readonly IClipboardBackend _clip;
    private readonly ClipboardKeeper _keeper;

    public TextPaster(IClipboardBackend clip, ClipboardKeeper keeper) { _clip = clip; _keeper = keeper; }

    public static bool IsUsableTarget(IntPtr h) => h != IntPtr.Zero && Native.IsWindow(h) && !Native.IsOwnWindow(h);

    public async Task<TextPasteOutcome> PasteAsync(string? text, IntPtr target)
    {
        if (!TextPasteRules.Acceptable(text)) return TextPasteOutcome.Rejected;
        try
        {
            var became = false;
            if (IsUsableTarget(target))
            {
                Native.SetForegroundWindow(target);
                var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
                while (true)
                {
                    became = Native.GetForegroundWindow() == target;
                    if (became || DateTime.UtcNow >= deadline) break;
                    await Task.Delay(20).ConfigureAwait(true);
                }
            }
            var outcome = TextPasteRules.Decide(text, target, IsUsableTarget, became);
            if (outcome != TextPasteOutcome.Pasted) return CopyOnly(text!);

            _keeper.Place(text!);
            await PacketPaster.WaitForModifiersReleasedAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(true);
            // bekleme sırasında öne başka bir pencere (kabuğun kendisi dahil) geldiyse yapıştırma
            if (Native.GetForegroundWindow() != target)
            {
                _keeper.Abandon(); // metin panoda kalır, eski içerik geri konmaz
                return TextPasteOutcome.Copied;
            }
            var ok = PacketPaster.SendCtrlV();
            _keeper.ScheduleRestore();
            return ok ? TextPasteOutcome.Pasted : TextPasteOutcome.Failed;
        }
        catch (Exception ex)
        {
            Log.Error("metin yapıştırma", ex);
            try { _keeper.RestoreNow(); } catch { }
            return TextPasteOutcome.Failed;
        }
    }

    private TextPasteOutcome CopyOnly(string text)
    {
        try { _clip.SetText(text); return TextPasteOutcome.Copied; }
        catch (Exception ex) { Log.Error("pano yazma", ex); return TextPasteOutcome.Failed; }
    }
}
