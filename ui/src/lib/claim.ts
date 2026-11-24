// "Yardımcı N dosya değiştirdiğini söyledi. Ben klasörde değişiklik bulamadım." gibi karşılaştırma cümlesi (sunucu: ChangeClaim.Sentence).
// Sunucu bu iki cümleyi sonuç özetinin (summary / reasonText) içine koyar; ekran onu ayırıp görünür ve vurgulu gösterir, özetin geri kalanı aynen durur.
const RX = /(?:Yardımcı|Ajan) (?:\d+ dosya|dosya) değiştirdiğini (?:söyledi|söylemedi)\. Ben (?:de )?(?:klasörde|izlenen dosyalarda) değişiklik (?:buldum|bulamadım)\./

export type ClaimCompare = { said: string; found: string; mismatch: boolean }

/** Metinlerin ilkinde karşılaştırma cümlesi varsa iki parçaya ayırıp verir; yoksa null. mismatch: Söz yardımcının söylediğini klasörde bulamadı. */
export function claimCompare(...texts: (string | null | undefined)[]): ClaimCompare | null {
  for (const s of texts) {
    const m = s ? RX.exec(s) : null
    if (!m) continue
    const [said, found] = m[0].split(/(?<=\.) (?=Ben )/)
    if (!said || !found) continue
    return { said, found, mismatch: /bulamadım/.test(found) }
  }
  return null
}

/** Karşılaştırma cümlesini metinden çıkarır (aynı cümle iki yerde görünmesin). */
export const withoutClaim = (s: string | null | undefined): string => (s ?? '').replace(RX, '').replace(/\s{2,}/g, ' ').trim()
