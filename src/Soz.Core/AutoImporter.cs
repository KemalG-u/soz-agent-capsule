using System.Diagnostics;
using Soz.Core.Data;

namespace Soz.Core;

/// <summary>
/// Oturumları kendiliğinden içe alır (n6): sunucu açılınca bir kez, sonra her <see cref="Interval"/> aralığında. Artımlıdır (yalnız değişen kayıt dosyası okunur, hiçbir şey
/// değişmediyse ayıklama ve kümeleme çalışmaz), düşük öncelikli iş parçacığında koşar ve düğmeyle başlatılan içe almayı beklemez (o sürerken tur atlanır).
/// Böylece "Benden Beklenen" kutusu, kişi "Yenile" demeden de diskteki oturumları görür. Ölçü: <see cref="LastMs"/>, <see cref="MaxMs"/>, <see cref="Runs"/>, <see cref="ChangedRuns"/>.
/// </summary>
public sealed class AutoImporter : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    private readonly SozDb _db;
    private readonly Func<ImportOptions> _options;
    private readonly Action<ImportSummary>? _onChanged;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Sunucuda çalışan örnek (durum ucu için). Birden çok sunucu aynı süreçteyse sonuncusu.</summary>
    public static AutoImporter? Current { get; private set; }

    public TimeSpan Interval { get; }
    public int Runs => Volatile.Read(ref _runs);
    public int ChangedRuns => Volatile.Read(ref _changed);
    public int SkippedBusy => Volatile.Read(ref _busy);
    public long LastMs => Interlocked.Read(ref _lastMs);
    public long MaxMs => Interlocked.Read(ref _maxMs);
    public string? LastError { get; private set; }
    private int _runs, _changed, _busy;
    private long _lastMs, _maxMs;

    public AutoImporter(SozDb db, Func<ImportOptions>? options = null, TimeSpan? interval = null, Action<ImportSummary>? onChanged = null)
    {
        _db = db;
        _options = options ?? (() => new ImportOptions());
        Interval = interval ?? DefaultInterval;
        _onChanged = onChanged;
    }

    /// <summary>Bir tur: içe al, süreyi ölç. Başka içe alma sürüyorsa null.</summary>
    public ImportSummary? RunOnce()
    {
        var o = _options();
        o.SkipWhenIdle = true;
        var sw = Stopwatch.StartNew();
        var sum = Importer.TryRun(_db, o);
        if (sum is null) { Interlocked.Increment(ref _busy); return null; }
        var ms = sw.ElapsedMilliseconds;
        Interlocked.Increment(ref _runs);
        Interlocked.Exchange(ref _lastMs, ms);
        if (ms > Interlocked.Read(ref _maxMs)) Interlocked.Exchange(ref _maxMs, ms);
        if (sum.Changed) { Interlocked.Increment(ref _changed); try { _onChanged?.Invoke(sum); } catch (Exception) { /* haber verme hatası içe almayı bozmaz */ } }
        return sum;
    }

    public void Start(TimeSpan? firstDelay = null)
    {
        if (_loop is not null) return;
        Current = this;
        var stop = _cts.Token;
        _loop = Task.Factory.StartNew(() =>
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
            try
            {
                if (stop.WaitHandle.WaitOne(firstDelay ?? TimeSpan.FromSeconds(1))) return;
                while (!stop.IsCancellationRequested)
                {
                    try { RunOnce(); LastError = null; }
                    catch (Exception e) when (e is not OperationCanceledException) { LastError = e.GetType().Name; /* tur hatası sunucuyu düşürmez, sonraki tur yeniden dener */ }
                    if (stop.WaitHandle.WaitOne(Interval)) return;
                }
            }
            catch (ObjectDisposedException) { }
        }, stop, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _cts.Dispose();
    }
}
