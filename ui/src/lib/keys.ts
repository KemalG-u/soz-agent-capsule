// Genel klavye: Ctrl+K palet; j/k ve oklar liste; Enter aç; r cevapla; s durdur (önce sorar); e/h izin (yalnız izin kartında odak varken); n yeni iş; f klasör; / ara; ? yardım; Esc geri.
// Metin kutusundayken harf kısayolları kapalıdır (yazarken karışmaz). Fare olmadan her akış tamamlanır.
import { tick } from 'svelte'
import { go, router } from './router.svelte'
import { app } from './store.svelte'
import { askStop, decide, flatJobs, listeyeDon, pendingApproval, select, selectedJob, sf } from './surface.svelte'
import { dar, escKarari } from './gezinme'
import { katmanAcik } from './katman'

export const isNarrow = dar

function typing(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return false
  const tag = el.tagName
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable
}

/** Odak boşta mı (gövde, liste ya da akış alanı)? Düğme ya da bağlantı odaktaysa Enter onların işidir. */
function idle(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return true
  return el === document.body || el.id === 'job-list' || el.id === 'stream-scroll' || el.tagName === 'MAIN'
}

function moveSel(d: number) {
  const list = flatJobs()
  if (!list.length) return
  const i = list.findIndex((j) => j.id === sf.selId)
  const n = Math.min(list.length - 1, Math.max(0, (i < 0 ? (d > 0 ? -1 : list.length) : i) + d))
  select(list[n].id, { oto: true }) // j/k yalnız seçer: dar pencerede ayrıntı açılmaz, geçmişe yazılmaz
}

export async function openSelected() {
  const j = selectedJob()
  if (!j) return
  select(j.id, { detail: true }) // listeden ayrıntıya: geçmişe yazılır (O1)
  if (isNarrow()) sf.view = 'detail'
  await tick()
  if (j.awaiting === 'reply') sf.focusReply++
  else document.getElementById('stream-scroll')?.focus()
}

export function onGlobalKey(e: KeyboardEvent) {
  if (e.isComposing) return
  const mod = e.ctrlKey || e.metaKey
  if (mod && !e.altKey && !e.shiftKey && (e.key === 'k' || e.key === 'K')) {
    e.preventDefault()
    sf.paletteOpen = !sf.paletteOpen
    return
  }
  // Açık katman (pencere, kısayol listesi, palet, menü) varken genel kısayol yok: Esc'i ve tuşları katman kendisi yönetir (A1, gezinme.ts).
  if (sf.paletteOpen || app.helpOpen || sf.folderMenuOpen || sf.agentMenuOpen || katmanAcik()) return
  if (mod || e.altKey) return
  const k = e.key

  if (k === 'Escape') {
    if (e.defaultPrevented) return // bir kutu ya da kart Esc'i zaten kullandı
    const karar = escKarari({
      katmanAcik: false, durdurSorusu: sf.stopAsk !== null, yaziyor: typing(e.target), route: router.route, sub: router.sub,
      dar: isNarrow(), gorunum: sf.view, kurulum: !!sf.setup, navTabs: app.navTabs,
    })
    if (karar.tur === 'yok') return
    e.preventDefault()
    if (karar.tur === 'durdurKapat') sf.stopAsk = null
    else if (karar.tur === 'liste') listeyeDon()
    else if (karar.tur === 'listeOdak') sf.focusList++
    else go(karar.hedef as Parameters<typeof go>[0])
    return
  }

  if (typing(e.target)) return

  if (k === '?') {
    e.preventDefault()
    app.helpOpen = true
    return
  }
  if (k === '/') {
    e.preventDefault()
    go('ara')
    app.focusSearch++
    return
  }
  if (k === 'n' || k === 'N') {
    e.preventDefault()
    if (router.route !== 'suan' && router.route !== 'is') go('suan')
    if (isNarrow()) sf.view = 'list'
    sf.focusNew++
    return
  }
  if (k === 'f' || k === 'F') {
    if (router.route === 'suan' || router.route === 'is') {
      e.preventDefault()
      sf.folderMenuOpen = true
      return
    }
  }
  if (router.route !== 'is') return
  if (k === 'j' || (k === 'ArrowDown' && idle(e.target))) {
    e.preventDefault()
    moveSel(1)
  } else if (k === 'k' || (k === 'ArrowUp' && idle(e.target))) {
    e.preventDefault()
    moveSel(-1)
  } else if (k === 'Enter' && idle(e.target)) {
    e.preventDefault()
    void openSelected()
  } else if (k === 'r' || k === 'R') {
    const rj = selectedJob()
    if (!rj) return
    e.preventDefault()
    select(rj.id, { detail: true })
    if (isNarrow()) sf.view = 'detail'
    void tick().then(() => sf.focusReply++)
  } else if (k === 's' || k === 'S') {
    const j = selectedJob()
    if (!j) return
    e.preventDefault()
    askStop(j.id) // durdurmadan önce sorar (kazara s tuşu işi yarıda bırakmasın)
  } else if (k === 'e' || k === 'E' || k === 'h' || k === 'H') {
    // İzin tek harfle yalnız izin kartının İÇİNDE odak varken verilir ya da reddedilir (başka yerde yanlışlıkla basılan harf izin vermesin).
    if (!(e.target instanceof HTMLElement) || !e.target.closest('[data-approval]')) return
    const j = selectedJob()
    const a = j ? pendingApproval(j.id) : null
    if (!j || !a) return
    e.preventDefault()
    void decide(j.id, a.eventId, k === 'e' || k === 'E')
  }
}
