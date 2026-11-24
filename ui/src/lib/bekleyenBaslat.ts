// "Klasör seçmeden Başlat" (katalog ŞÜPHE-1): bekleyen başlatma ZAMAN AŞIMINA değil, o taslağın metnine bağlıdır.
// Kişi vazgeçerse silinir; klasör sonradan gelse bile başka bir taslak asla başlamaz. Saf mantık, node ile sınanır.
export interface BekleyenBaslat { on: boolean; raw: string }
export const bosBekleyen = (): BekleyenBaslat => ({ on: false, raw: '' })
/** Bu taslak klasör beklerken durduruldu. */
export const bekle = (p: BekleyenBaslat, draft: string): void => { p.on = true; p.raw = draft.trim() }
/** Vazgeçildi ya da başka işe geçildi: bekleyen başlatma silinir. */
export const vazgec = (p: BekleyenBaslat): void => { p.on = false; p.raw = '' }
/** Klasör hazır oldu: yalnız bekleyen taslak HÂLÂ kutudaki taslaksa başlatılır. Her durumda bekleyen tüketilir (bir kez). */
export function al(p: BekleyenBaslat, draft: string): boolean {
  const ok = p.on && p.raw !== '' && p.raw === draft.trim()
  vazgec(p)
  return ok
}
