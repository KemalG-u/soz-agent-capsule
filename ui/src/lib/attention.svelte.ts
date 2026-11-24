// "Sıra bende": kişinin kararını bekleyenler tek listede. Sıra sunucudadır (priority); arayüz kendi sırasını kurmaz.
import { getAttention, getNowSessions } from './api5'
import { t } from './i18n/index.svelte'
import { onLive } from './live'
import type { AttentionSnapshot, NeedsYou } from './types5'
import { whenVisible } from './visible'

export const at = $state({
  snap: null as AttentionSnapshot | null,
  /** Oturum kimliğine göre oturum kartı alanları (kaynak, güven, bitiş sınıfı). */
  sessions: {} as Record<number, NeedsYou>,
  loaded: false,
  error: '',
  /** Ekran okuyucuya söylenecek cümle: yeni bir "sıra bende" öğesi geldiğinde (uyandırmayan "emin değilim" öğeleri söylenmez). */
  announce: '',
  announceId: 0,
  /** Son başarılı okumanın zamanı (ms) ve art arda başarısız okuma sayısı: bağlantı koptuysa ekran bunu söyler (C5). */
  lastOk: 0,
  fails: 0,
})

/** Cevap yazılırken kart başka yerden kalkarsa (soru başka yerde cevaplandı) yazılan metin kaybolmaz: kart not düşülerek yerinde kalır (K-07). */
export interface LostReply { id: string; projectName: string; sentence: string; text: string; gone: boolean }
export const lost = $state<{ list: LostReply[] }>({ list: [] })
export function loseReply(item: { id: string; projectName?: string | null; sentence: string }, text: string, gone = true) {
  const tx = text.trim()
  if (!tx || lost.list.some((x) => x.id === item.id)) return
  lost.list = [...lost.list, { id: item.id, projectName: item.projectName ?? '', sentence: item.sentence, text: tx, gone }]
}
export const dropLost = (id: string) => { lost.list = lost.list.filter((x) => x.id !== id) }
export const stillThere = (id: string): boolean => (at.snap?.items ?? []).some((i) => i.id === id)

let known: Set<string> | null = null

let loading = false
let again = false

export async function loadAttention() {
  if (loading) {
    again = true
    return
  }
  loading = true
  try {
    const [snap, now] = await Promise.all([getAttention(), getNowSessions().catch(() => null)])
    at.snap = snap
    // İlk yüklemede söylenecek yeni bir şey yoktur; sonraki yüklemelerde listeye giren her öğe bir kez duyurulur.
    // Yalnız Şu An'da gerçekten kart olarak görünen öğeler duyurulur (C12): iş bitti kartı, "emin değilim" ve kapanmış işler duyurulmaz.
    const shown = (i: { kind: string; reason: string }) => ((i.reason ?? '').startsWith('session_') ? i.kind !== 'uncertain' || i.reason === 'session_turn_end' : !['done', 'uncertain'].includes(i.kind))
    const fresh = snap.items.filter((i) => shown(i) && !(known?.has(i.id) ?? true))
    known = new Set(snap.items.map((i) => i.id))
    if (fresh.length > 0) {
      at.announce = fresh.length === 1 ? t('attAnnounceOne', { s: fresh[0]!.sentence }) : t('attAnnounceMany', { n: fresh.length, s: fresh[0]!.sentence })
      at.announceId++
    }
    const map: Record<number, NeedsYou> = {}
    for (const n of [...(now?.needsYou ?? []), ...(now?.uncertain ?? [])]) map[n.sessionId] = n
    at.sessions = map
    at.error = ''
    at.lastOk = Date.now()
    at.fails = 0
  } catch (e) {
    at.error = e instanceof Error ? e.message : String(e)
    at.fails++
  } finally {
    at.loaded = true
    loading = false
    if (again) {
      again = false
      void loadAttention()
    }
  }
}

let timer: ReturnType<typeof setTimeout> | undefined
const soon = () => {
  clearTimeout(timer)
  timer = setTimeout(() => whenVisible(() => void loadAttention()), 250) // görünmezken istek yok, dönüşte tek seferde
}
onLive((name) => {
  if (name === 'attention' || name === 'job' || name === 'job.state' || name === 'proof' || name === 'session') soon()
})
