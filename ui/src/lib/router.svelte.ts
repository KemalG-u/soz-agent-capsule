import { geriAdimiMi } from './gezinme'
import { katmanKapat } from './katman'

// Birleşik kontrol kulesi ("suan" / Şu An) varsayılan açılış ekranıdır.
// Tüm mevcut rotalar (is, projelerim, sira, sabah, neoldu, bekci, harcama, defter, ara, oneriler, ayarlar, taniyorum) tam uyumlu çalışır.
export type Route = 'suan' | 'sira' | 'is' | 'projelerim' | 'sabah' | 'neoldu' | 'bekci' | 'harcama' | 'defter' | 'ara' | 'oneriler' | 'ayarlar' | 'taniyorum'
export const routes: Route[] = ['suan', 'sira', 'is', 'projelerim', 'sabah', 'neoldu', 'bekci', 'harcama', 'defter', 'ara', 'oneriler', 'ayarlar', 'taniyorum']

const legacy: Record<string, string> = {
  bugun: 'suan',
  toparla: 'suan',
  isler: 'is',
  tekrarlar: 'defter/tekrarlar',
  karne: 'harcama/karne',
  'ne-demistim': 'ara',
}

function parse(): { route: Route; sub: string } {
  const raw = location.hash.replace(/^#\/?/, '').split('?')[0]
  const [head = '', ...rest] = raw.split('/')
  const mapped = legacy[head] ? [...legacy[head].split('/'), ...rest] : [head, ...rest]
  const [first = '', ...tail] = mapped
  const route = (routes as string[]).includes(first) ? (first as Route) : 'suan'
  return { route, sub: tail.join('/') }
}

/** "#/suan?yeni=1" veya "#/is?yeni=1" adresi: yeni iş kutusuna odaklan (kabuk bu adresle açar). */
const wantsNewJob = () => /[?&]yeni=1(&|$)/.test(location.hash)

/** "#/suan?oturum=<kimlik>" adresi: o yardımcı oturumunun satırını görünür kıl ve vurgula (kapsül satıra tıklayınca bu adresle açar). Yoksa boş. */
export function oturumOf(): string {
  const m = /[?&]oturum=([^&]+)/.exec(location.hash)
  if (!m) return ''
  try { return decodeURIComponent(m[1]!) } catch { return m[1]! }
}

const first = parse()
export const router = $state({ route: first.route, sub: first.sub, yeniTick: wantsNewJob() ? 1 : 0, oturum: oturumOf(), oturumTick: 1 })

// ---- Geçmiş damgası (O2): her geçmiş kaydına bir zaman damgası konur. Geri gezinmede hedefin damgası şimdikinden küçüktür.
// Bir pencere açıkken Geri gelirse yalnız pencere kapanır: arada gelen adres değişimi yutulur, sonra ileri gidilerek aynı sayfada kalınır.
let suankiDamga = 0
function damgala() {
  if (typeof history === 'undefined') return
  const s = (history.state ?? {}) as Record<string, unknown>
  if (typeof s.t !== 'number') {
    try { history.replaceState({ ...s, t: Date.now() }, '') } catch { /* geçmiş yazılamıyorsa damga yok */ }
  }
  const t = (history.state as { t?: number } | null)?.t
  suankiDamga = typeof t === 'number' ? t : Date.now()
}
let sonHash = typeof location !== 'undefined' ? location.hash : ''
let yutHash: string | null = null
let yutBitis = 0
let ileriDonus = false

if (typeof window !== 'undefined') {
  damgala()
  window.addEventListener('popstate', (e) => {
    if (ileriDonus) {
      ileriDonus = false
      damgala()
      return
    }
    const hedef = (e.state as { t?: unknown } | null)?.t
    if (geriAdimiMi(hedef, suankiDamga) && katmanKapat()) {
      yutHash = sonHash
      yutBitis = Date.now() + 800
      ileriDonus = true
      history.go(1)
      return
    }
    if (typeof hedef === 'number') suankiDamga = hedef
  })
  window.addEventListener('hashchange', () => {
    if (yutHash !== null) {
      if (Date.now() > yutBitis) yutHash = null
      else if (location.hash !== yutHash) return // pencere kapatılırken geçici geri adım: sayfa değişmez
      else yutHash = null
    }
    sonHash = location.hash
    damgala()
    const p = parse()
    router.route = p.route
    router.sub = p.sub
    router.oturum = oturumOf()
    if (router.oturum) router.oturumTick++ // aynı adres yeniden açılırsa (kapsül tekrar tıklandı) satır yine vurgulanır
    if ((p.route === 'suan' || p.route === 'is') && wantsNewJob()) router.yeniTick++
  })
}

export function go(r: Route, sub = '') {
  location.hash = sub ? `#/${r}/${sub}` : `#/${r}`
}
