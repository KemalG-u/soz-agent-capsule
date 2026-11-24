namespace Soz.Capsule;

/// <summary>Yaklaşma ("manyetik alan") ve opaklık işlevleri. P1: opaklık = taban + (1 - taban) * (1 - d/R)^2, d &lt; R için.</summary>
internal static class Yaklasma
{
    public static double Opaklik(double taban, double dDp, double rDp)
    {
        if (rDp <= 0 || dDp >= rDp) return taban;
        var u = 1 - Math.Max(0, dDp) / rDp;
        return taban + (1 - taban) * u * u;
    }

    /// <summary>Uyku opaklığı: koyu ve açık zemin değeri, zemin parlaklığına (0 koyu, 1 açık) göre karışır. Taban altına inmez.</summary>
    public static double UykuOpaklik(Ayar a, double zeminParlaklik)
    {
        var t = Math.Clamp(zeminParlaklik, 0, 1);
        return Math.Max(a.SleepFloor, a.SleepD + (a.SleepL - a.SleepD) * t);
    }

    /// <summary>İmleç ile dikdörtgenin merkezi arası uzaklık (dp ya da px, birim aynı olmalı).</summary>
    public static double Uzaklik(double imlecX, double imlecY, double merkezX, double merkezY)
    {
        var dx = imlecX - merkezX; var dy = imlecY - merkezY;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// Sönümlü yay: x'' = -k (x - hedef) - c x', c = 2 zeta sqrt(k). 2 ms alt adımlarla yarı örtük Euler.
/// Windows.UI.Composition yaylarında "Period" = 1 / sqrt(k) (2π / sqrt(k) DEĞİL; kabuk-7 bulgu 1).
/// </summary>
internal struct Yay
{
    public double X, V, T, K, Z;
    public Yay(double x, double k, double z) { X = x; T = x; V = 0; K = k; Z = z; }

    public void Adim(double dt)
    {
        var c = 2 * Z * Math.Sqrt(K);
        var n = Math.Max(1, (int)Math.Ceiling(dt / 0.002));
        var h = dt / n;
        for (var i = 0; i < n; i++)
        {
            var a = -K * (X - T) - c * V;
            V += a * h;
            X += V * h;
        }
    }

    public readonly bool Durgun(double eps, double veps) => Math.Abs(X - T) < eps && Math.Abs(V) < veps;
    public void Oturt() { X = T; V = 0; }

    /// <summary>Composition yayının Period değeri (saniye) verilen sertlik için.</summary>
    public static double CompositionPeriyodu(double k) => 1.0 / Math.Sqrt(k);

    /// <summary>İstenen "yaklaşık oturma süresi" (ms) için sertlik, sönüm 1: kritik sönümlü yay %2 bandına ~5,83/ω sürede girer (deneme sayfasındaki kFromMs).</summary>
    public static double SertlikSureden(double ms)
    {
        var w = 5.83 / (Math.Max(1, ms) / 1000.0);
        return w * w;
    }
}
