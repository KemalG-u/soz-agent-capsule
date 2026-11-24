// Vuruş algılama durum makinesi (StrokeDetector) ve ekran kenarı sıkıştırma fikri Kando'dan (kando-menu/kando) uyarlanmıştır:
//   src/menu-renderer/input-methods/gesture-detector.ts  (SPDX-License-Identifier: MIT, Copyright (c) Simon Schneegans)
//   src/common/math/index.ts clampToMonitor              (SPDX-License-Identifier: MIT, Copyright (c) Simon Schneegans)
// Değişiklikler: C#'a çevrildi; zamanlayıcı yerine açık zaman damgası (Tick) kullanır, durum değişmezdir (saf fonksiyon), sekiz sabit yön ve
// histerezis eklendi. Kando'nun MIT bildirimi: https://github.com/kando-menu/kando/blob/main/LICENSES/MIT.txt

namespace Soz.Core.Discovery;

/// <summary>Ekran noktası, DIP biriminde (aygıttan bağımsız piksel). Y aşağı doğru artar.</summary>
public readonly record struct Pt(double X, double Y);

/// <summary>Çalışma alanı (görev çubuğu hariç ekran), DIP.</summary>
public readonly record struct WorkArea(double Left, double Top, double Right, double Bottom);

/// <summary>
/// Vuruş geometrisi ayarları, hepsi DIP. Değerler başlangıç önerisidir ve ölçülmedi (repo-4 bölüm 2.1: K = kodda gördüm, B = benim önerim);
/// gerçek elle denemeden sonra ayarlanır. Ayar çorbası olmasın diye kişiye açılan ayar bu işte yok.
/// </summary>
public sealed record RingOptions
{
    /// <summary>Merkez ölü bölgesi: bunun içinde yön yok (B: Kando 50, Blender 12 arası).</summary>
    public double DeadZone { get; init; } = 40;
    /// <summary>Seçili sektörden çıkmak için sınırı bu kadar derece aşmak gerekir (B, kaynakta yok).</summary>
    public double HysteresisDegrees { get; init; } = 5;
    /// <summary>Bundan küçük hareket yok sayılır (K: Kando 10).</summary>
    public double JitterThreshold { get; init; } = 10;
    /// <summary>Köşe algılamak için en kısa vuruş (K: Kando 150).</summary>
    public double MinStrokeLength { get; init; } = 150;
    /// <summary>Bundan küçük dönüş köşe sayılmaz (K: Kando 20 derece).</summary>
    public double MinStrokeAngleDegrees { get; init; } = 20;
    /// <summary>Vuruş yeterince uzunken bu kadar ms kıpırdamazsa seçim (K: Kando 100).</summary>
    public double PauseMs { get; init; } = 100;
    /// <summary>Sabit uzunluk kipi: merkez ölü bölge + bu kadar aşılınca seçim; açı ve hız yok sayılır (K: mekanizma Kando, değer B 110).</summary>
    public double FixedStrokeLength { get; init; } = 110;
    /// <summary>Ekran kenarında halka merkezi çalışma alanından en az bu kadar içeride durur (B 160). Onay yarıçapından BÜYÜK olmalı.</summary>
    public double EdgeMargin { get; init; } = 160;
    /// <summary>Kör onay yarıçapı = ölü bölge + sabit uzunluk.</summary>
    public double ConfirmRadius => DeadZone + FixedStrokeLength;
    /// <summary>Dokunuş bundan kısaysa menü açık kalır (K: Blender 0,2 sn).</summary>
    public double TapMaxSeconds { get; init; } = 0.2;
    /// <summary>Tıkla ya da sürükle ayrımı (K: Kando 15).</summary>
    public double DragThreshold { get; init; } = 15;

    /// <summary>Sorunlu ayarın açıklaması (yoksa null). En önemlisi: kenar boşluğu onay yarıçapından büyük olmalı, yoksa kenarda yön yanlış okunur.</summary>
    public string? Problem()
    {
        if (DeadZone < 0 || FixedStrokeLength <= 0 || MinStrokeLength <= 0 || JitterThreshold < 0 || PauseMs <= 0) return "Ölçüler pozitif olmalı.";
        if (HysteresisDegrees is < 0 or >= 22.5) return "Histerezis 0 ile 22,5 derece arasında olmalı (sektör yarısından küçük).";
        if (EdgeMargin <= ConfirmRadius) return "Kenar boşluğu, onay yarıçapından (ölü bölge + sabit uzunluk) büyük olmalı. Yoksa ekran kenarında yön yanlış okunur.";
        return null;
    }
}

/// <summary>Seçim: vuruşun bittiği nokta (Kando'da "selection" olayı).</summary>
public readonly record struct StrokeSelection(Pt At, string Reason);

/// <summary>Kenar sıkıştırma sonucu: yeni merkez ve imlecin ışınlanacağı fark.</summary>
public readonly record struct ClampResult(Pt Center, Pt CursorWarp, bool Clamped);

/// <summary>Saf geometri fonksiyonları. Yön 0 yukarıdır, saat yönünde artar (sekiz sabit yön, 45 derecelik sektör, merkezler 0/45/90/...).</summary>
public static class RingGeometry
{
    public const double SectorDegrees = 45;

    public static double Distance(Pt a, Pt b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// <summary>Yukarıdan saat yönünde derece, 0 ile 360 arası (360 hariç). Aynı noktaysa 0.</summary>
    public static double AngleDegrees(Pt from, Pt to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var a = Math.Atan2(dx, -dy) * 180 / Math.PI; // ekranda y aşağı: yukarı = -dy
        if (a < 0) a += 360;
        return a >= 360 ? 0 : a;
    }

    public static double SectorCenter(int dir) => dir * SectorDegrees;

    /// <summary>
    /// <paramref name="origin"/>'dan <paramref name="to"/> noktasının yönü (0..7); ölü bölge içindeyse null.
    /// <paramref name="current"/> verilirse histerezis uygulanır: seçili sektörden çıkmak için sektör sınırını <see cref="RingOptions.HysteresisDegrees"/> kadar aşmak gerekir.
    /// </summary>
    public static int? Direction(Pt origin, Pt to, RingOptions o, int? current = null)
    {
        if (Distance(origin, to) < o.DeadZone) return null;
        var a = AngleDegrees(origin, to);
        if (current is { } cur && Directions.IsValid(cur))
        {
            var diff = Math.Abs(((a - SectorCenter(cur)) % 360 + 540) % 360 - 180); // 0..180 dairesel fark
            if (diff <= SectorDegrees / 2 + o.HysteresisDegrees) return cur;
        }
        return (int)Math.Floor((a + SectorDegrees / 2) % 360 / SectorDegrees);
    }

    /// <summary>İki vuruşlu akış: 1. vuruş tür yönü, 2. vuruş yuva yönü. İkisinin de yönü kendi başlangıç noktasından ölçülür (mutlak sekiz yön).</summary>
    public static SlotAddress? Address(int? kindDir, int? slotDir)
        => kindDir is { } k && slotDir is { } s && Directions.IsValid(k) && Directions.IsValid(s) ? new SlotAddress(k, s) : null;

    /// <summary>
    /// Sabit uzunluk (kör seçim) kipi: başlangıçtan uzaklık ölü bölge + sabit uzunluğu aşınca seçim olur; seçim noktası ideal noktaya (tam o mesafeye) oturtulur.
    /// Açıya ve hıza bakılmaz. Kando gesture-detector.ts:102-116 ile aynı mantık.
    /// </summary>
    public static (bool Selected, Pt Ideal) FixedLength(Pt start, Pt strokeEnd, RingOptions o)
    {
        var len = Distance(start, strokeEnd);
        var need = o.ConfirmRadius;
        if (len <= need) return (false, strokeEnd);
        var f = need / len;
        return (true, new Pt(start.X + (strokeEnd.X - start.X) * f, start.Y + (strokeEnd.Y - start.Y) * f));
    }

    /// <summary>
    /// Halka merkezini çalışma alanı içine sıkıştırır (kenar boşluğu kadar içeri), tam sayıya aşağı yuvarlar. İmleç <see cref="ClampResult.CursorWarp"/> kadar ışınlanmalı,
    /// vuruş başlangıcı yeni merkeze sıfırlanmalı (Kando menu.ts:757-787). Çalışma alanı iki kenar boşluğundan küçükse merkez ortaya konur.
    /// Mutlak yön korunur: halka çevrilmez, kayıp yoktur.
    /// </summary>
    public static ClampResult ClampCenter(Pt center, WorkArea area, double margin)
    {
        double One(double v, double lo, double hi)
        {
            var min = lo + margin;
            var max = hi - margin;
            if (min > max) return Math.Floor((lo + hi) / 2);
            return Math.Floor(Math.Clamp(v, min, max));
        }
        var nx = One(center.X, area.Left, area.Right);
        var ny = One(center.Y, area.Top, area.Bottom);
        var moved = nx != center.X || ny != center.Y;
        return new ClampResult(new Pt(nx, ny), new Pt(nx - center.X, ny - center.Y), moved);
    }

    /// <summary>Dokunuş mu (kısa ve az hareketli), sürükleme mi? Dokunuşta menü açık kalır, sürüklemede bırakınca seçilir.</summary>
    public static bool IsTap(double pressSeconds, double movedDistance, RingOptions o) => pressSeconds < o.TapMaxSeconds && movedDistance < o.DragThreshold;
}

/// <summary>Vuruş algılayıcının durumu (değişmez).</summary>
public readonly record struct StrokeState(Pt? Start, Pt? End, double? PauseSinceMs, Pt PausePoint)
{
    public static readonly StrokeState Idle = new(null, null, null, default);
}

/// <summary>
/// İşaretleme kipi vuruş algılayıcısı (Kando GestureDetector'ın saf hali). Hareket olaylarını <see cref="Step"/> ile, geçen zamanı <see cref="Tick"/> ile besle;
/// sert dönüşte (köşe), duraklamada ya da (sabit uzunluk kipinde) mesafe aşılınca seçim döner. Fare düğmesi bırakılınca <see cref="StrokeState.Idle"/>'a dön.
/// </summary>
public static class StrokeDetector
{
    public static (StrokeState State, StrokeSelection? Selection) Step(StrokeState s, Pt p, double nowMs, RingOptions o, bool fixedLength)
    {
        if (s.Start is not { } start || s.End is not { } end) return (new StrokeState(p, p, null, default), null);

        var dir = new Pt(end.X - start.X, end.Y - start.Y);
        var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);

        if (fixedLength)
        {
            var (selected, ideal) = RingGeometry.FixedLength(start, end, o);
            if (selected) return (new StrokeState(ideal, p, null, default), new StrokeSelection(ideal, "distance"));
            return (s with { End = p }, null);
        }

        if (len > o.MinStrokeLength)
        {
            var tip = new Pt(p.X - end.X, p.Y - end.Y);
            var tipLen = Math.Sqrt(tip.X * tip.X + tip.Y * tip.Y);
            var pauseSince = s.PauseSinceMs;
            var pausePoint = s.PausePoint;
            var newEnd = end;
            if (tipLen > o.JitterThreshold)
            {
                pauseSince = null; // kıpırdadı: duraklama sayacı iptal
                var cos = (tip.X / tipLen * dir.X + tip.Y / tipLen * dir.Y) / len;
                var angle = Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
                if (angle > o.MinStrokeAngleDegrees) return (new StrokeState(end, end, null, default), new StrokeSelection(end, "corner"));
                newEnd = p;
            }
            if (pauseSince is null) { pauseSince = nowMs; pausePoint = p; } // Kando: zamanlayıcı yoksa kur
            return (new StrokeState(start, newEnd, pauseSince, pausePoint), null);
        }
        return (s with { End = p }, null);
    }

    /// <summary>Zaman geçti: duraklama süresi dolduysa seçim.</summary>
    public static (StrokeState State, StrokeSelection? Selection) Tick(StrokeState s, double nowMs, RingOptions o)
    {
        if (s.PauseSinceMs is { } since && nowMs - since >= o.PauseMs)
            return (new StrokeState(s.PausePoint, s.PausePoint, null, default), new StrokeSelection(s.PausePoint, "pause"));
        return (s, null);
    }
}
