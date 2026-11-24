// f7 (A1, O2): ekranın üstündeki katmanlar (pencere, kısayol listesi, palet, açılır menü).
// Esc ve Geri önce bunlara bakar: katman açıkken sayfa değişmez, yalnız katman kapanır.

type Katman = { acik: () => boolean; kapat: () => void }
const kayitli: Katman[] = []

/** Yerel <dialog> olmayan bir katmanı kaydeder (palet, açılır menü). Kaydı silen işlevi döner. */
export function katmanEkle(k: Katman): () => void {
  kayitli.push(k)
  return () => {
    const i = kayitli.indexOf(k)
    if (i >= 0) kayitli.splice(i, 1)
  }
}

const acikPencereler = (): HTMLDialogElement[] =>
  typeof document === 'undefined' ? [] : [...document.querySelectorAll<HTMLDialogElement>('dialog[open]')]

/** Ekranda açık bir katman var mı? */
export function katmanAcik(): boolean {
  return acikPencereler().length > 0 || kayitli.some((k) => k.acik())
}

/** En üstteki katmanı kapatır. Bir şey kapandıysa true. */
export function katmanKapat(): boolean {
  const p = acikPencereler()
  const son = p[p.length - 1]
  if (son) {
    son.close()
    return true
  }
  for (let i = kayitli.length - 1; i >= 0; i--) {
    const k = kayitli[i]!
    if (k.acik()) {
      k.kapat()
      return true
    }
  }
  return false
}
