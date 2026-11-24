namespace Soz.Core.Profile;

/// <summary>
/// Tam taramanın ilerlemesi (ilk açılışta yarım dakika sürebilir; arayüz "x / y dosya" gösterir).
/// <c>Phase</c>: <c>bos</c> (hiç tarama olmadı), <c>sayiliyor</c> (dosyalar sayılıyor), <c>okunuyor</c>, <c>kaydediliyor</c> (projeler çıkarılıyor, yazılıyor), <c>bitti</c>.
/// <c>Percent</c> 0-100 (okuma %90'a kadar, kaydetme %95'e kadar sayılır). <c>Tool</c> şu an okunan yardımcının adı.
/// </summary>
public sealed record ScanProgress(bool Running, string Phase, int Done, int Total, string? Tool, DateTime? StartedAt, long ElapsedMs, int Percent);
