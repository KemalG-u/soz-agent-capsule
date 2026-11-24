import { hataMetni } from './hata'
import { emitLive, onLive, type LiveName } from './live'
import { POLL_MS, pollWhileVisible, whenVisible } from './visible'
import { getSettings, getSummary, initApi, isMock, putSettings, runImport } from './api'
import type { ImportResult, Settings, Summary, ThemeFamily } from './types'
import { inShell } from './util'

type Lang = 'tr' | 'en'
type Theme = 'system' | 'light' | 'dark'

export const THEME_FAMILIES: ThemeFamily[] = ['cam', 'terminal', 'mat', 'ton', 'duz', 'sicak']
export const OPTIONAL_NAV_ROUTES = ['bekci', 'defter', 'neoldu', 'harcama', 'ara'] as const
export type OptionalNavRoute = (typeof OPTIONAL_NAV_ROUTES)[number]

function lsGet(k: string): string | null {
  try {
    return localStorage.getItem(k)
  } catch {
    return null
  }
}
function lsSet(k: string, v: string) {
  try {
    localStorage.setItem(k, v)
  } catch {
    // depolama kapalı olabilir
  }
}

function validFamily(v: unknown): ThemeFamily {
  return THEME_FAMILIES.includes(v as ThemeFamily) ? (v as ThemeFamily) : 'cam'
}
function parseNavTabs(raw: unknown): OptionalNavRoute[] {
  let arr: unknown[] = []
  if (Array.isArray(raw)) arr = raw
  else if (typeof raw === 'string' && raw.trim()) {
    try {
      const p = JSON.parse(raw)
      if (Array.isArray(p)) arr = p
    } catch {
      arr = raw.split(',').map((s) => s.trim())
    }
  }
  const out: OptionalNavRoute[] = []
  for (const x of arr) {
    if ((OPTIONAL_NAV_ROUTES as readonly string[]).includes(String(x)) && !out.includes(x as OptionalNavRoute)) {
      out.push(x as OptionalNavRoute)
    }
  }
  return out
}

function postThemeToShell(family: ThemeFamily, mode: Theme) {
  if (!inShell()) return
  try {
    ;(window as unknown as { chrome?: { webview?: { postMessage?: (m: unknown) => void } } }).chrome?.webview?.postMessage?.({
      type: 'theme-changed',
      family,
      mode,
    })
  } catch {
    // kabuk mesajı gönderilemezse sayfa teması yine çalışır
  }
}

const savedLang = lsGet('soz.lang')
const savedTheme = lsGet('soz.theme')
const savedFamily = lsGet('soz.themeFamily')
const savedNavTabs = lsGet('soz.navTabs')

export const app = $state({
  ready: false,
  failed: false,
  mock: false,
  lang: (savedLang === 'en' ? 'en' : 'tr') as Lang,
  theme: (savedTheme === 'light' || savedTheme === 'dark' ? savedTheme : 'system') as Theme,
  themeFamily: validFamily(savedFamily),
  navTabs: parseNavTabs(savedNavTabs),
  tick: 0,
  summary: null as Summary | null,
  settings: null as Settings | null,
  dataDir: '',
  dataDirSource: '' as string,
  version: '',
  toastText: '',
  toastId: 0,
  /** Bildirimin yanındaki tek eylem (ör. 'İzle'): yoksa null. */
  toastAction: null as { label: string; run: () => void } | null,
  importing: false,
  importResult: null as ImportResult | null,
  importError: '',
  proofTick: 0,
  proofLast: null as unknown,
  jobTick: 0,
  helpOpen: false,
  focusNewJob: 0,
  focusSearch: 0,
})

export function applyTheme() {
  if (typeof document === 'undefined') return
  const el = document.documentElement
  el.setAttribute('data-theme-family', app.themeFamily)
  if (app.theme === 'system') el.removeAttribute('data-theme')
  else el.setAttribute('data-theme', app.theme)
  el.lang = app.lang
}

export function setLang(l: Lang) {
  app.lang = l
  lsSet('soz.lang', l)
  applyTheme()
}

export function setTheme(th: Theme, notifyShell = true) {
  app.theme = th
  lsSet('soz.theme', th)
  applyTheme()
  if (notifyShell) postThemeToShell(app.themeFamily, app.theme)
}

export function setThemeFamily(f: ThemeFamily, notifyShell = true) {
  app.themeFamily = validFamily(f)
  lsSet('soz.themeFamily', app.themeFamily)
  applyTheme()
  if (notifyShell) postThemeToShell(app.themeFamily, app.theme)
}

/** Tema ailesini ve kipini aynı saniye uygular, kabuğa bildirir ve sunucuya kaydeder. */
export async function saveThemeChoice(patch: { themeFamily?: ThemeFamily; theme?: Theme }): Promise<boolean> {
  const before = { family: app.themeFamily, theme: app.theme, settings: app.settings }
  if (patch.themeFamily) setThemeFamily(patch.themeFamily, false)
  if (patch.theme) setTheme(patch.theme, false)
  postThemeToShell(app.themeFamily, app.theme)
  if (app.settings) {
    app.settings = { ...app.settings, themeFamily: app.themeFamily, theme: app.theme }
  }
  try {
    const saved = await putSettings({ themeFamily: app.themeFamily, theme: app.theme })
    app.settings = { ...saved, themeFamily: saved.themeFamily ?? app.themeFamily }
    return true
  } catch {
    // Sunucu henüz themeFamily alanını tanımıyorsa theme kipini yine kaydet.
    if (patch.theme) {
      try {
        const saved = await putSettings({ theme: app.theme })
        app.settings = { ...saved, themeFamily: app.themeFamily }
        return true
      } catch {
        // aşağıda geri alınır
      }
    }
  }
  // Sunucu kaydetmedi: "Kaydedildi" denmez, ekran eski temaya döner (A-AYR-03).
  setThemeFamily(before.family, false)
  setTheme(before.theme, false)
  postThemeToShell(app.themeFamily, app.theme)
  app.settings = before.settings
  return false
}

/** Alt gezinme şeridi ek sekmelerini kaydeder (yerel anında + sunucu ayarı). Sunucu kabul ettiyse true; reddettiyse seçim yalnız bu tarayıcıda kalır ve false döner (C13). */
export async function saveNavTabs(tabs: OptionalNavRoute[]): Promise<boolean> {
  const clean = parseNavTabs(tabs)
  app.navTabs = clean
  lsSet('soz.navTabs', JSON.stringify(clean))
  if (app.settings) app.settings = { ...app.settings, navTabs: clean }
  try {
    // Sunucu bu ayarı virgüllü metin olarak bekler (en çok 200 karakter).
    await putSettings({ navTabs: clean.join(',') as unknown as string[] })
    return true
  } catch {
    // seçim yerel depolamada kalır, ama "kaydedildi" denmez
    return false
  }
}

let toastTimer: ReturnType<typeof setTimeout> | undefined
export function toast(text: string, action: { label: string; run: () => void } | null = null) {
  app.toastText = text
  app.toastAction = action
  app.toastId++
  clearTimeout(toastTimer)
  // Süre cümlenin uzunluğuna göre: en az 4 sn, uzun cümlede karakter başına 50 ms eklenir, en çok 12 sn (K-12).
  toastTimer = setTimeout(() => { app.toastText = ''; app.toastAction = null }, Math.min(12000, Math.max(action ? 8000 : 4000, 2000 + text.length * 50)))
}

export async function loadSummary() {
  try {
    app.summary = await getSummary()
  } catch {
    // sessizce geç, bir sonraki yoklamada yeniden denenir
  }
}

/** Sayıları ve açık sayfaları yeniler. */
export async function refresh() {
  await loadSummary()
  app.tick++
}

export async function updateSettings(patch: Partial<Settings>) {
  if (!app.settings) return
  const prev = app.settings
  app.settings = { ...app.settings, ...patch }
  if (patch.language) setLang(patch.language)
  if (patch.theme) setTheme(patch.theme)
  if (patch.themeFamily) setThemeFamily(patch.themeFamily)
  try {
    app.settings = await putSettings(patch)
  } catch (e) {
    app.settings = prev
    setLang(prev.language)
    setTheme(prev.theme, false)
    if (prev.themeFamily) setThemeFamily(prev.themeFamily, false)
    throw e
  }
}

export async function startImport() {
  if (app.importing) return
  app.importing = true
  app.importResult = null
  app.importError = ''
  try {
    const r = await runImport()
    await loadSummary()
    app.importResult = r
  } catch (e) {
    app.importError = hataMetni(e)
  } finally {
    app.importing = false
    await refresh()
  }
}

export async function boot() {
  applyTheme()
  try {
    const s = await initApi()
    app.mock = isMock()
    app.dataDir = s.dataDir
    app.dataDirSource = s.dataDirSource ?? ''
    app.version = s.version
    await Promise.all([
      loadSummary(),
      getSettings()
        .then((st) => {
          app.settings = st
          app.lang = st.language
          app.theme = st.theme
          if (st.themeFamily) {
            app.themeFamily = validFamily(st.themeFamily)
            lsSet('soz.themeFamily', app.themeFamily)
          }
          if (st.navTabs !== undefined && st.navTabs !== null) {
            app.navTabs = parseNavTabs(st.navTabs)
            lsSet('soz.navTabs', JSON.stringify(app.navTabs))
          }
          lsSet('soz.lang', st.language)
          lsSet('soz.theme', st.theme)
          applyTheme()
          postThemeToShell(app.themeFamily, app.theme)
        })
        .catch(() => {}),
    ])
    app.ready = true
    startLive()
  } catch {
    app.failed = true
    app.ready = true
  }
}

// ---- Canlılık: SSE + görünürken 20 sn yoklama ----
let live = false
let es: EventSource | null = null
let sseFails = 0
let refreshTimer: ReturnType<typeof setTimeout> | undefined

function scheduleRefresh() {
  clearTimeout(refreshTimer)
  // Görünmezken istek yok: yenileme tutulur, kullanıcı dönünce tek seferde yapılır.
  refreshTimer = setTimeout(() => whenVisible(() => void refresh()), 350)
}

function handleLive(name: LiveName, data: unknown) {
  if (name === 'proof') {
    app.proofLast = data
    app.proofTick++
  }
  if (name === 'job' || name === 'proof' || name === 'job.state') app.jobTick++
  if (name === 'attention') return
  if (name === 'job.progress') return
  scheduleRefresh()
}

onLive(handleLive)

function connect() {
  if (app.mock) return
  try {
    es?.close()
    es = new EventSource('/api/events/stream')
    for (const name of ['import', 'proposal', 'repeat', 'session', 'decision', 'job', 'proof', 'guard', 'queue', 'job.progress', 'job.state', 'attention'] as LiveName[]) {
      es.addEventListener(name, (ev) => {
        let data: unknown = null
        try {
          data = JSON.parse((ev as MessageEvent).data)
        } catch {
          // veri JSON değilse olay yine de sayılır
        }
        emitLive(name, data)
      })
    }
    es.onopen = () => { sseFails = 0 }
    es.onerror = () => {
      es?.close()
      es = null
      if (sseFails === 0) scheduleRefresh() // bağlantı kopunca hemen bir kez bakılır: bağlantı şeridi en geç 10 sn içinde çıkar
      sseFails++
      setTimeout(connect, Math.min(15000, 3000 * sseFails)) // artan bekleme: sunucu kapalıyken boşuna istek yağmaz
    }
  } catch {
    setTimeout(connect, 3000)
  }
}

function startLive() {
  if (live) return
  live = true
  connect()
  // Görünürken 20 sn'de bir, ekrana dönünce hemen bir kez; görünmezken hiç (lib/visible.ts).
  pollWhileVisible(() => { if (!app.importing) void refresh() }, POLL_MS)
}
