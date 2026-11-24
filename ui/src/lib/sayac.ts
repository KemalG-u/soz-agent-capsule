// İş sayaçları (üst çipler ve sekme). Sayı ile liste aynı kaynaktan gelir ve kişi yanılmaz (G-DÜŞÜK-4).
// Liste en yeni N işi yükler (`sf.jobsLimit`); sunucu yalnız TÜM işlerin toplamını verir, durum başına toplamı vermez.

/** "Verdiğim İşler" sekmesi: yüklenmemiş iş varsa sunucunun toplamı, yoksa yüklenen satır sayısı. */
export function sekmeIsSayisi(yuklenen: number, sunucuToplami: number, dahaVar: boolean): number {
  return dahaVar && Number.isFinite(sunucuToplami) ? Math.max(yuklenen, sunucuToplami) : yuklenen
}

/** Duruma göre sayaç ("Seni bekleyenler"): yüklenmemiş iş varsa gerçek sayı bilinmez, "50+" denir. */
export function durumSayaci(yuklenen: number, dahaVar: boolean): string {
  return dahaVar && yuklenen > 0 ? `${yuklenen}+` : String(yuklenen)
}
