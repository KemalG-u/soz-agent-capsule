// Klasör seçimi: saf mantık (node ile sınanır, scripts/f4.test.mjs).
// Kişinin yazıp ya da seçip eklediği klasör, adı ne olursa olsun SEÇİLİ sayılır. Sınama klasörleri yalnız "kendiliğinden varsayılan" olmaktan çıkarılır
// (ör. yolunda _gecici geçen klasör); kişi onu kendisi seçtiyse iş o klasörde başlar ve klasör penceresi ona geri dönmez.

/** Sınama ve geçici klasörler: iş verme kutusunda asla varsayılan seçilmez. Yalnız açıkça sınama olan adlar sayılır (_gecici, canli-proje, sentetik) ya da Windows'un geçici klasörü.
 * "temp" ya da "deneme" gibi genel sözcük bir üst klasör adında geçiyorsa (D:\temp\proje) gerçek proje olabilir, elenmez. */
const TEST_SEGMENTS = new Set(['_gecici', 'canli-proje', 'sentetik', 'canli-deneme'])
export function isTestProject(p: { name?: string | null; path?: string | null }): boolean {
  const path = (p.path ?? '').trim().toLowerCase().replaceAll('/', '\\')
  const segs = path.split('\\').filter(Boolean)
  const name = (p.name ?? '').trim().toLowerCase()
  if (segs.some((s) => TEST_SEGMENTS.has(s) || s.endsWith('-sentetik') || s.startsWith('canli-proje'))) return true
  if (TEST_SEGMENTS.has(name) || name.startsWith('canli-proje') || name === 'temp' || name === 'tmp') return true
  return path.includes('\\appdata\\local\\temp\\') || path.endsWith('\\appdata\\local\\temp') || path.includes('\\windows\\temp\\')
}

/** Geçerli klasör: seçili id gerçek listedeyse ve (kişi seçtiyse ya da sınama klasörü değilse) o; yoksa varsayılan. */
export function seciliKlasor<P extends { id: number; name?: string | null; path?: string | null }>(gercek: P[], projectId: number | null, picked: boolean, varsayilan: P | null): P | null {
  const cur = gercek.find((p) => p.id === projectId)
  if (cur && (picked || !isTestProject(cur))) return cur
  return varsayilan
}

/** Klasör eklenince tek onay kartı gerekir mi: yeni klasör, kapalı kapı ya da hâlâ açılmamış kontrol varsa. Her şey zaten açıksa kart gösterilmez. */
export function kurulumGerekir(r: { created: boolean; gateEnabled: boolean; suggested: { enabled: boolean }[] }): boolean {
  return r.created || !r.gateEnabled || r.suggested.some((c) => !c.enabled)
}

/** "Bu klasör zaten ekli." bildirimi yalnız klasör eklerken anlamlıdır. İş başlatma akışında kişi klasör eklemiyor, seçiyor: orada gösterilmez. */
export function zatenEkliBildirimi(created: boolean, baslatmaAkisinda: boolean): boolean {
  return !created && !baslatmaAkisinda
}
