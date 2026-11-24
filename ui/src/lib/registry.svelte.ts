import { hataMetni } from './hata'
// Kayıt defteri durumu: ayar sayfası, tanışma ve yetenek onayları buradan beslenir.
// Arayüz hiçbir ayarın adını bilmez; başlık, açıklama, tür, sınır ve grup kayıttan gelir. Yeni ayar eklemek arayüz kodu gerektirmez.
import { getRegistry, getServerSettings, putServerSettings } from './api5'
import { app, setLang, setTheme } from './store.svelte'
import type { RegistryEntry, ServerSettings, SettingBody, SettingGroup } from './types5'
import type { Settings } from './types'

export const rg = $state({
  entries: [] as RegistryEntry[],
  values: {} as ServerSettings,
  loaded: false,
  loading: false,
  error: '',
})

/** Sunucunun okurken düzelttiği değerler: anahtar, sunucudaki ham değer, kullanılan değer ve neden. */
export interface Adjustment {
  key: string
  stored: unknown
  applied: unknown
  reason: string
}

import { alanEtiketiKaydet } from './hata'
import { sade } from './sade'
export const body = (e: RegistryEntry): SettingBody => e.body as SettingBody
/** Değerin sunucudaki anahtarı: eski adı varsa o, yoksa kayıt kimliği (GET ve PUT aynı anahtarı kullanır). */
export const settingKey = (e: RegistryEntry): string => body(e).legacyKey ?? e.id
export const titleOf = (e: RegistryEntry): string => (app.lang === 'en' ? e.title?.en : null) ?? e.title?.tr ?? e.id
export const describeOf = (e: RegistryEntry): string => sade((app.lang === 'en' ? e.description?.en : null) ?? e.description?.tr ?? '')

// Sunucunun ayar reddindeki iç adı ekrandaki etikete çevirir (hata.ts).
alanEtiketiKaydet((k) => {
  const e = rg.entries.find((x) => x.kind === 'setting' && (settingKey(x) === k || x.id === k || body(x).legacyKey === k))
  return e ? titleOf(e) : null
})

/** Kişiye gösterilen ayarlar: grubu olanlar (iç durum K5 grupsuzdur) ve kullanımdan kalkmamış olanlar. */
export const visibleSettings = (): RegistryEntry[] =>
  rg.entries
    .filter((e) => e.kind === 'setting' && e.deprecated == null && e.state !== 'deprecated' && !!body(e).group)
    .sort((a, b) => (body(a).order ?? 0) - (body(b).order ?? 0))

/** Sabit sekiz başlık (kayıt şemasındaki grup listesi). Başlık adları şemada yok; yeni grup gelirse "Diğer" altında görünür. */
export const groupOrder: SettingGroup[] = ['genel', 'gorunum-ses', 'kapsul-bildirim', 'arac-ajan', 'kural-kanit', 'proje', 'veri-gizlilik', 'gelismis']

/** Değer varsayılandan farklı mı (kişi değiştirmiş mi). SettingRow'daki "Varsayılana dön" düğmesiyle aynı ölçüt. */
export const isChanged = (e: RegistryEntry): boolean => JSON.stringify(rg.values[settingKey(e)] ?? null) !== JSON.stringify(body(e).default ?? null)

export const adjustments = (): Adjustment[] => {
  const raw = rg.values.adjusted
  if (!Array.isArray(raw)) return []
  return raw.map((a) => (typeof a === 'string' ? { key: a, stored: undefined, applied: rg.values[a], reason: '' } : { key: a.key, stored: a.stored, applied: a.applied, reason: a.reason ?? '' }))
}

export function adjustmentOf(e: RegistryEntry): Adjustment | null {
  const k = settingKey(e)
  return adjustments().find((a) => a.key === k || a.key === e.id || a.key === body(e).legacyKey) ?? null
}

// DÖNGÜ KÖKÜ (s4, saniyede 9 ila 450 istek): Ayarlar.svelte'in $effect'i bu işlevi çağırıyordu ve işlev ilk satırda `rg.loading` OKUYUP sonra YAZIYORDU.
// Etki kendi okuduğu durumu yazınca kendini yeniden tetikler. Artık mantık reaktif durum okumaz: düz değişkenler kullanılır, `rg.loading` yalnız ekran içindir.
// Ek güvence: hata olunca artan bekleme (2, 4, 8 ... en çok 30 sn), sayfa görünmezken istek yok. Elle "Tekrar dene" (force) beklemeyi atlar.
let inflight = false
let failures = 0
let nextTry = 0
export const registryBackoffMs = (n: number): number => Math.min(30000, 1000 * 2 ** Math.max(1, n))

export async function loadRegistry(opts?: { force?: boolean }) {
  const force = opts?.force === true
  if (inflight) return
  if (!force) {
    if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return
    if (Date.now() < nextTry) return
  }
  inflight = true
  rg.loading = true
  try {
    const [r, v] = await Promise.all([getRegistry(), getServerSettings()])
    rg.entries = r.entries
    rg.values = v
    rg.error = ''
    failures = 0
    nextTry = 0
  } catch (e) {
    rg.error = hataMetni(e)
    failures++
    nextTry = Date.now() + registryBackoffMs(failures)
  } finally {
    rg.loaded = true
    rg.loading = false
    inflight = false
  }
}

/** Tek ayar kaydeder. Sunucu sınır dışı değeri 400 ile reddeder (cümlesi ApiError.message). Başarıda tam ayar nesnesi esastır. */
export async function saveSetting(e: RegistryEntry, value: unknown): Promise<void> {
  const key = settingKey(e)
  const next = await putServerSettings({ [key]: value })
  rg.values = next
  const { adjusted: _a, ...plain } = next
  if (app.settings) app.settings = { ...app.settings, ...(plain as unknown as Partial<Settings>) }
  if (key === 'language' && (next.language === 'tr' || next.language === 'en')) setLang(next.language)
  if (key === 'theme' && (next.theme === 'system' || next.theme === 'light' || next.theme === 'dark')) setTheme(next.theme)
}

/** Türkçe harf duyarsız karşılaştırma: ı/İ/i, ç, ğ, ö, ş, ü ve aksanlar aynı sayılır. */
export const norm = (s: string): string =>
  s.toLocaleLowerCase('tr').replace(/ı/g, 'i').normalize('NFD').replace(/\p{M}/gu, '')

export function matches(e: RegistryEntry, q: string): boolean {
  const terms = norm(q).split(/\s+/).filter(Boolean)
  if (terms.length === 0) return true
  const hay = norm([titleOf(e), describeOf(e), (e.keywords ?? []).join(' '), body(e).card ?? '', e.id].join(' '))
  return terms.every((t) => hay.includes(t))
}
