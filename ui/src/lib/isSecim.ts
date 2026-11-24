// İş sayfasında hangi işin açılacağına karar verir (saf mantık, node ile sınanır: scripts/f1.test.mjs).
// Kural (katalog ŞÜPHE-4): listede yüklü olmayan bir işe gidilirse o iş sunucudan getirilir, bulunamazsa "Bu işi bulamadım" denir.
// Hiçbir koşulda İSTENEN işin yerine listedeki başka (ilk) iş gösterilmez.
export type SecimKarari = 'keep' | 'select-route' | 'fetch-route' | 'fetch-sel' | 'wait' | 'missing' | 'first' | 'none'

export function secimKarari(o: { jobIds: number[]; selId: number | null; routeId: number | null; pulling: number | null; missing: number | null }): SecimKarari {
  const has = (id: number) => o.jobIds.includes(id)
  if (o.routeId !== null) {
    if (has(o.routeId)) return o.selId === o.routeId ? 'keep' : 'select-route'
    if (o.missing === o.routeId) return 'missing'
    if (o.pulling === o.routeId) return 'wait'
    return 'fetch-route'
  }
  if (o.selId !== null) {
    if (has(o.selId)) return 'keep'
    if (o.missing === o.selId) return 'missing'
    if (o.pulling === o.selId) return 'wait'
    return 'fetch-sel'
  }
  if (o.missing !== null) return 'missing'
  if (o.pulling !== null) return 'wait'
  return o.jobIds.length > 0 ? 'first' : 'none'
}

/**
 * Açılışta (boot3) kendiliğinden ilk iş seçilsin mi?
 * Yalnız İşler sayfası DIŞINDA, hiçbir iş seçili değilken, adreste iş numarası yokken ve bir iş getirilirken ya da bulunamamışken değil.
 * İşler sayfasında ilk işi Surface.svelte (secimKarari) seçer; adresteki iş numarası her zaman önce gelir.
 */
export function ilkIsBoottaMi(o: { route: string; routeSub: string; selId: number | null; missing: number | null; pulling: number | null }): boolean {
  if (o.route === 'is') return false
  if (/^\d+$/.test(o.routeSub)) return false
  return o.selId === null && o.missing === null && o.pulling === null
}
