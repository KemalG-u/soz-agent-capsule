// Kabuk (WebView2) köprüsü. Kabuk dışında hiçbir şey yapmaz, hata vermez.
// Kabuk -> sayfa: dize "soz:new-job", nesneler {type:'paste-result', outcome}, {type:'dictate-result', started}, {type:'folder-picked', path}.
// Sayfa -> kabuk: {type:'paste', text}, {type:'dictate'}, {type:'pick-folder'}, {type:'theme-changed', family, mode}, {type:'reset-panel-place'}, {type:'get-shell-prefs'}, {type:'set-shell-pref', key, value, confirmed?}.
// Kabuk -> sayfa (tercihler): {type:'shell-prefs', bildirimler, kaldirirkenVeriyiSil, acilistaBasla}.
import { t } from './i18n/index.svelte'
import { go, router } from './router.svelte'
import { app, toast } from './store.svelte'
import { applyShellPrefs } from './shellPrefs.svelte'
import { addFolder, sf } from './surface.svelte'
import { inShell } from './util'

interface WebView {
  addEventListener(type: 'message', h: (e: MessageEvent) => void): void
  removeEventListener(type: 'message', h: (e: MessageEvent) => void): void
  postMessage(m: unknown): void
}

function webview(): WebView | null {
  if (!inShell()) return null
  return (window as unknown as { chrome: { webview: WebView } }).chrome.webview
}

export function postShellMessage(msg: unknown): boolean {
  const wv = webview()
  if (!wv) return false
  wv.postMessage(msg)
  return true
}

/** Masaüstü kabuğunda panel yerini ve boyutunu varsayılana sıfırlar. */
export function resetPanelPlace(): boolean {
  return postShellMessage({ type: 'reset-panel-place' })
}

/** Kabuk tercihlerini ister (cevap {type:'shell-prefs'} ile gelir). Kabuk dışında false döner ve hiçbir şey olmaz. */
export function requestShellPrefs(): boolean {
  return postShellMessage({ type: 'get-shell-prefs' })
}

/** Tek kabuk tercihini yazar. Veriyi silmeyi açmak için confirmed:true gerekir (arayüz önce kişiye sorar). Cevap yine {type:'shell-prefs'} olarak gelir. */
export function setShellPref(key: 'bildirimler' | 'kaldirirkenVeriyiSil' | 'acilistaBasla', value: boolean, confirmed = false): boolean {
  return postShellMessage(confirmed ? { type: 'set-shell-pref', key, value, confirmed: true } : { type: 'set-shell-pref', key, value })
}

/** Yeni iş kutusuna odaklan (Ctrl+Alt+J, kapsül kalemi). Şu An ve İşler ekranlarında kutu doğrudan üsttedir. */
export function openNewJob() {
  if (router.route !== 'suan' && router.route !== 'is') go('suan')
  sf.view = 'list'
  sf.focusNew++
  app.focusNewJob++
  setTimeout(() => sf.focusNew++, 150)
}

/** Kabukta klasör seçici (Windows). Kabuk dışında false döner: arayüz yol kutusunu gösterir. */
export function pickFolder(): boolean {
  return postShellMessage({ type: 'pick-folder' })
}

/** Başka klasör seç: kabukta yerel klasör seçici penceresi açılır, tarayıcıda yol kutusu penceresi. */
export function addFolderFlow() {
  if (!pickFolder()) sf.addFolderOpen = true
}

export function dictate() {
  postShellMessage({ type: 'dictate' })
}

const pasteText = {
  pasted: 'pasteDone',
  copied: 'pasteCopied',
  failed: 'pasteFailed',
  rejected: 'pasteRejected',
} as const

function onMessage(e: MessageEvent) {
  let d: unknown = e.data
  if (typeof d === 'string') {
    if (d === 'soz:new-job') return openNewJob()
    try {
      d = JSON.parse(d)
    } catch {
      return
    }
  }
  if (!d || typeof d !== 'object') return
  const m = d as { type?: unknown; outcome?: unknown; started?: unknown; path?: unknown }
  if (m.type === 'folder-picked') {
    if (typeof m.path === 'string' && m.path) {
      // Tarayıcı yolundaki gibi (Projelerim.folderAdded): bildirim, Şu An'a geçiş ve iş kutusuna odak. Sayfa "0 proje" diye eski listede kalmaz.
      void addFolder(m.path).then((p) => {
        if (!p && sf.folderError && !sf.addFolderOpen) toast(sf.folderError)
        else if (p) {
          toast(t('folderAddedOk', { n: p.name ?? (m.path as string) }) + ' ' + t('pjFolderReady'))
          openNewJob()
        }
      })
    }
    return
  }
  if (m.type === 'shell-prefs') {
    applyShellPrefs(d as { bildirimler?: unknown; kaldirirkenVeriyiSil?: unknown; acilistaBasla?: unknown })
    return
  }
  if (m.type === 'paste-result' &&typeof m.outcome === 'string' && m.outcome in pasteText) {
    toast(t(pasteText[m.outcome as keyof typeof pasteText]))
  } else if (m.type === 'dictate-result' && m.started === false) {
    toast(t('dictateFailed'))
  }
}

/** İleti dinleyicisi tek yerde (App.svelte) kurulur. Dönen işlev dinleyiciyi kaldırır. */
export function initShell(): () => void {
  const wv = webview()
  if (!wv || typeof wv.addEventListener !== 'function') return () => {}
  wv.addEventListener('message', onMessage)
  return () => wv.removeEventListener?.('message', onMessage)
}
