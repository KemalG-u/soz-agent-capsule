using System.Drawing;
using System.Windows.Forms;

namespace Soz.Shell;

/// <summary>Panonun ne yaptığını soyutlar: gerçek (WinForms) ve sınama (bellekte) uygulamaları var.</summary>
internal interface IClipboardBackend
{
    /// <summary>Panonun şimdiki içeriğinin geri konabilir kopyası (boşsa null olabilir).</summary>
    object? Capture();
    void SetText(string text);
    string? GetText();
    /// <summary>Pano her değiştiğinde artan sayı.</summary>
    long Sequence { get; }
    void Restore(object? snapshot);
}

/// <summary>Zamanlayıcıyı soyutlar; testte sahte saat verilir.</summary>
internal interface IScheduler
{
    IDisposable After(TimeSpan delay, Action action);
}

/// <summary>
/// Paketi panoya koyar, eski içeriği saklar, <see cref="ScheduleRestore"/> çağrılınca gecikme sonunda eski içeriği
/// geri koyar. Kural: yalnız pano hâlâ bizim koyduğumuz metinse ve bizim yazımızdan sonra değişmediyse geri koyar.
/// Art arda yerleştirmede asıl eski içerik korunur (kendi paketimizi "eski pano" saymayız).
/// </summary>
internal sealed class ClipboardKeeper
{
    public static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(1500);

    private readonly IClipboardBackend _clip;
    private readonly IScheduler _scheduler;
    private object? _original;
    private bool _holdingOriginal;
    private string? _mine;
    private long _mineSeq;
    private IDisposable? _timer;

    public ClipboardKeeper(IClipboardBackend clip, IScheduler scheduler)
    {
        _clip = clip;
        _scheduler = scheduler;
    }

    public bool RestorePending => _timer != null;
    public bool LastRestoreDone { get; private set; }

    /// <summary>Eski içeriği sakla, paketi koy.</summary>
    public void Place(string text)
    {
        _timer?.Dispose(); _timer = null;
        if (!_holdingOriginal) { _original = _clip.Capture(); _holdingOriginal = true; }
        LastRestoreDone = false;
        _clip.SetText(text);
        _mine = text;
        _mineSeq = _clip.Sequence;
    }

    /// <summary>Yapıştırma gönderildikten sonra çağrılır; 1,5 sn sonra koşullu geri koyma.</summary>
    public void ScheduleRestore()
    {
        _timer?.Dispose();
        _timer = _scheduler.After(RestoreDelay, () => RestoreNow());
    }

    /// <summary>Geri koymayı bırakır; pano olduğu gibi kalır.</summary>
    public void Abandon()
    {
        _timer?.Dispose(); _timer = null;
        _original = null; _holdingOriginal = false; _mine = null;
    }

    /// <summary>true: geri konuldu; false: pano bizim değildi, dokunulmadı.</summary>
    public bool RestoreNow()
    {
        _timer?.Dispose(); _timer = null;
        var ok = false;
        try
        {
            var stillMine = _mine != null && _clip.Sequence == _mineSeq && _clip.GetText() == _mine;
            if (stillMine) { _clip.Restore(_original); ok = true; }
        }
        finally
        {
            _original = null; _holdingOriginal = false; _mine = null;
            LastRestoreDone = ok;
        }
        return ok;
    }
}

/// <summary>Gerçek pano: metin, dosya listesi, bitmap ve düz bayt/metin biçimlerini saklar.</summary>
internal sealed class WinClipboardBackend : IClipboardBackend
{
    private sealed record Snapshot(List<(string Format, object Data)> Items);

    public long Sequence => Native.GetClipboardSequenceNumber();

    public string? GetText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : null; }
        catch { return null; }
    }

    public void SetText(string text)
    {
        var obj = new DataObject();
        obj.SetText(text, TextDataFormat.UnicodeText);
        Clipboard.SetDataObject(obj, true, 10, 50);
    }

    public object? Capture()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return null;
            var items = new List<(string, object)>();
            foreach (var f in data.GetFormats(false))
            {
                object? v;
                try { v = data.GetData(f, false); } catch { continue; }
                switch (v)
                {
                    case string s: items.Add((f, s)); break;
                    case string[] arr: items.Add((f, arr.Clone())); break;
                    case byte[] b: items.Add((f, b.Clone())); break;
                    case MemoryStream ms: items.Add((f, ms.ToArray())); break;
                    case Bitmap bmp: items.Add((f, new Bitmap(bmp))); break;
                }
            }
            return items.Count == 0 ? null : new Snapshot(items);
        }
        catch (Exception ex) { Log.Error("pano yakalama", ex); return null; }
    }

    public void Restore(object? snapshot)
    {
        try
        {
            if (snapshot is not Snapshot snap) { Clipboard.Clear(); return; }
            var obj = new DataObject();
            foreach (var (f, d) in snap.Items)
            {
                if (d is byte[] bytes) obj.SetData(f, false, new MemoryStream(bytes));
                else obj.SetData(f, false, d);
            }
            Clipboard.SetDataObject(obj, true, 10, 50);
        }
        catch (Exception ex) { Log.Error("pano geri koyma", ex); }
    }
}

/// <summary>Gerçek zamanlayıcı: UI iş parçacığında çalışır (WinForms Timer).</summary>
internal sealed class UiScheduler : IScheduler
{
    public IDisposable After(TimeSpan delay, Action action)
    {
        var t = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)delay.TotalMilliseconds) };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); try { action(); } catch (Exception ex) { Log.Error("zamanlanmış iş", ex); } };
        t.Start();
        return new TimerHandle(t);
    }

    private sealed class TimerHandle(System.Windows.Forms.Timer t) : IDisposable
    {
        public void Dispose() { try { t.Stop(); t.Dispose(); } catch { } }
    }
}
