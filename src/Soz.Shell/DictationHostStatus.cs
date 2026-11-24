namespace Soz.Shell;

/// <summary>KarıncaAI köprüsünün bildirdiği canlı dikte durumu (boru yanıt baytı).</summary>
internal enum DictationHostStatus : byte
{
    Unavailable = 0,
    Idle = 10,
    Recording = 11,
    Processing = 12,
    Delivered = 13,
    ClipboardOnly = 14,
}
