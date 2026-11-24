// Uzun cümleyi sözcük sınırından kısaltır. Ekran metnini bozmaması için ayrı ve testli (f6: ProofCard ve OturumOzet'te
// düzenli ifadelerde ters bölü kaybolmuştu, "s" harfi boşluğa dönüyordu).

/** Ardışık boşlukları (satır sonları dahil) tek boşluğa indirir, baş ve sondaki boşluğu atar. */
export const tekBosluk = (s: string | null | undefined): string => (s ?? '').replace(/\s+/g, ' ').trim()

/** `en` karakterden uzun metni, yarım kalan son sözcüğü atarak keser ve '…' ekler. Kısa metne dokunmaz. */
export function kirp(s: string, en: number): string {
  if (s.length <= en) return s
  const kes = s.slice(0, en)
  // Kesim sözcüğün tam sonuna denk geldiyse sözcük yarım değildir; değilse yarım sözcüğü at (hiç boşluk yoksa olduğu gibi bırak).
  const yarim = /\S/.test(s[en] ?? ' ') && /\s/.test(kes)
  return (yarim ? kes.replace(/\s+\S*$/, '') : kes).trimEnd() + '…'
}
