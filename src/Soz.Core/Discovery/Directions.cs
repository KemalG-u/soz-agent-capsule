namespace Soz.Core.Discovery;

/// <summary>Halkanın sekiz sabit yönünün sekiz sabit türü (derin-3 sonuç 1). Yön 0 yukarıdadır, saat yönünde artar. Bu tablo kişiye göre değişmez.</summary>
public sealed record DirectionKind(int Dir, string Code, string Title, string Compass);

public static class Directions
{
    public const int Count = 8;
    public const int SlotsPerKind = 8;
    public const int TotalSlots = Count * SlotsPerKind;

    public static readonly IReadOnlyList<DirectionKind> Kinds = new[]
    {
        new DirectionKind(0, "jobs", "İşler", "kuzey"),
        new DirectionKind(1, "agent", "Araç ve yardımcı", "kuzeydoğu"),
        new DirectionKind(2, "skill", "Beceri", "doğu"),
        new DirectionKind(3, "pattern", "Kalıp", "güneydoğu"),
        new DirectionKind(4, "target", "Hedef pencere", "güney"),
        new DirectionKind(5, "flow", "Akış", "güneybatı"),
        new DirectionKind(6, "project", "Proje", "batı"),
        new DirectionKind(7, "soz", "Söz", "kuzeybatı"),
    };

    public static bool IsValid(int dir) => dir is >= 0 and < Count;
    public static DirectionKind Kind(int dir) => IsValid(dir) ? Kinds[dir] : throw new ArgumentOutOfRangeException(nameof(dir), "Yön 0 ile 7 arasında olmalı.");
    public static DirectionKind? ByCode(string? code) => Kinds.FirstOrDefault(k => string.Equals(k.Code, code, StringComparison.Ordinal));
}

/// <summary>Bir yuvanın adresi: tür yönü (1. vuruş) ve yuva yönü (2. vuruş), ikisi de 0..7.</summary>
public readonly record struct SlotAddress(int KindDir, int SlotDir)
{
    public bool IsValid => Directions.IsValid(KindDir) && Directions.IsValid(SlotDir);
    /// <summary>0..63 sıra numarası (tür yönü x 8 + yuva yönü).</summary>
    public int Index => KindDir * Directions.SlotsPerKind + SlotDir;
    public override string ToString() => $"{KindDir}.{SlotDir}";
}
