// Sabah özetinde seçilen "Aralık" (saat). 0: sunucunun varsayılanı (son 16 saat).
// İşe gidip Geri gelince seçim kaybolmasın diye tarayıcı depolamasında tutulur (uygulamadaki öteki tercihlerle aynı düzen: `soz.` önekli anahtar).
export const SABAH_ARALIK_KEY = 'soz.sabahAralik'
export const SABAH_ARALIKLAR = [0, 24, 72] as const
export type SabahAralik = (typeof SABAH_ARALIKLAR)[number]

type Depo = Pick<Storage, 'getItem' | 'setItem'>

/** Depodan ya da seçim kutusundan gelen değeri üç geçerli seçenekten birine çevirir; bilinmeyen değer varsayılana (0) düşer. */
export function sabahAralikCoz(ham: unknown): SabahAralik {
  const n = typeof ham === 'string' ? Number(ham.trim()) : typeof ham === 'number' ? ham : NaN
  return (SABAH_ARALIKLAR as readonly number[]).includes(n) ? (n as SabahAralik) : 0
}

function varsayilanDepo(): Depo | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

export function sabahAralikOku(depo: Depo | null = varsayilanDepo()): SabahAralik {
  try {
    return sabahAralikCoz(depo?.getItem(SABAH_ARALIK_KEY))
  } catch {
    return 0
  }
}

export function sabahAralikYaz(saat: number, depo: Depo | null = varsayilanDepo()): void {
  try {
    depo?.setItem(SABAH_ARALIK_KEY, String(sabahAralikCoz(saat)))
  } catch {
    // depolama kapalı olabilir: seçim yalnız bu oturumda yaşar
  }
}
