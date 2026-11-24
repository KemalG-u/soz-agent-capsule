import { hataMetni } from './hata'
// Paylaşılan profil durumu: Şu An ekranı ve üst iş verme kutusu aynı canlı profil verisini okur.
// Şu An ekranı ilk açıldığında henüz tarama yoksa arka planda kendiliğinden tarama başlatır, soru sormaz.
import { getProfile, getScanProgress, hasScan, runProfileScan, validTs, waitingRows, type Profile, type ProfProject } from './profil'
import { loadProjects, loadAgents } from './surface.svelte'
import { at, loadAttention } from './attention.svelte'
import { t, type Key } from './i18n/index.svelte'

export const pf = $state({
  data: null as Profile | null,
  loaded: false,
  loading: false,
  scanning: false,
  autoScanTried: false,
  /** İlk taramada sade ilerleme cümlesi (sunucunun /api/profile/scan-progress ucundan); tarama yokken boş. */
  progressText: '',
  error: '',
})

// Aynı döngü kalıbı (registry.svelte.ts): $effect içinden çağrılan işlev reaktif `pf.loading` okuyup yazmasın. Mantık düz değişkenle yürür.
let profileInflight = false
export async function refreshProfile(opts: { autoScan?: boolean; silent?: boolean } = {}): Promise<Profile | null> {
  if (profileInflight && !opts.autoScan) return pf.data
  profileInflight = true
  pf.loading = true
  try {
    const p = await getProfile()
    pf.data = p
    pf.error = ''
    pf.loaded = true
    if (opts.autoScan && !pf.autoScanTried && !pf.scanning && (p.scannedAt === null || !hasScan(p))) {
      pf.autoScanTried = true
      void autoScanInBackground()
    }
    return p
  } catch (e) {
    if (!opts.silent || !pf.data) pf.error = hataMetni(e)
    pf.loaded = true
    return pf.data
  } finally {
    pf.loading = false
    profileInflight = false
  }
}

/** Tarama sürerken ilerleme ucunu yoklar; hangi yardımcıların kayıtlarına bakıldığını sayar. Uç yoksa ya da hata verirse sessizce vazgeçer. */
function watchScanProgress(): () => void {
  const seen = new Set<string>()
  let stopped = false
  let busy = false
  const tick = async () => {
    if (stopped || busy) return
    busy = true
    try {
      const p = await getScanProgress()
      if (stopped) return
      if (p.tool) seen.add(p.tool)
      if (p.phase === 'kaydediliyor') pf.progressText = t('suAnScanSaving')
      else if (seen.size > 0) pf.progressText = t('suAnScanProgress', { n: seen.size, p: Math.max(0, Math.min(100, Math.round(p.percent || 0))) })
    } catch { /* uç yok ya da bağlantı gitti: sade bekleme cümlesi kalır */ } finally { busy = false }
  }
  const id = setInterval(() => { void tick() }, 1200)
  void tick()
  return () => { stopped = true; clearInterval(id); pf.progressText = '' }
}

export async function autoScanInBackground(): Promise<void> {
  if (pf.scanning) return
  pf.scanning = true
  const stopWatch = watchScanProgress()
  try {
    await runProfileScan()
    const fresh = await getProfile()
    pf.data = fresh
    pf.error = ''
    await Promise.all([loadProjects(), loadAgents(), loadAttention()])
  } catch (e) {
    pf.error = hataMetni(e)
  } finally {
    stopWatch()
    pf.scanning = false
  }
}

/** Tanınan gerçek projeleri en son etkin olana göre sıralar (eksik klasörler hariç). */
export function activeProfileProjects(): ProfProject[] {
  const list = (pf.data?.projects ?? []).filter((p) => p.path && !p.missingFolder)
  const ts = (p: ProfProject) => {
    const n = validTs(p.lastActivity)
    return Number.isFinite(n) ? n : -Infinity
  }
  return [...list].sort((a, b) => (ts(a) === ts(b) ? 0 : ts(b) > ts(a) ? 1 : -1) || a.name.localeCompare(b.name))
}

/**
 * TEK SAYAÇ (C4): "Seni bekleyenler" her yerde (Şu An başlığı, alt şerit rozeti, İşler çipi, Sıra özeti) bu işlevden okunur.
 * acil: karar, izin, soru, hata (Söz işleri ve dış oturumlar) ve profilde "seni bekliyor" diyen açık pencereler. durdu: "sırayı sana verdi". bitti: "bitti dedi, kontrol edilmedi".
 * Şu An'daki kart sayısıyla aynı kümedir; "emin değilim" türü kart olmadığı için sayılmaz.
 */
export function waitingBreakdown(): { urgent: number; turn: number; done: number; total: number } {
  let urgent = 0, turn = 0, done = 0
  for (const i of at.snap?.items ?? []) {
    if ((i.reason ?? '').startsWith('session_')) {
      if (i.reason === 'session_turn_end') turn++
      else if (i.reason === 'session_done') done++
      else if (i.kind !== 'uncertain') urgent++
    } else if (!['done', 'uncertain'].includes(i.kind)) urgent++
  }
  urgent += outsideWaitingCount()
  return { urgent, turn, done, total: urgent + turn + done }
}

const ROW_STATE: Record<string, { label: Key; warm: boolean }> = {
  session_permission: { label: 'saPermission', warm: true },
  session_question: { label: 'saQuestion', warm: true },
  session_input: { label: 'saInput', warm: true },
  session_waiting: { label: 'saWaiting', warm: true },
  session_failed: { label: 'saFailed', warm: true },
  session_turn_end: { label: 'saTurnEnd', warm: false },
  session_done: { label: 'saDoneShort', warm: false },
}
/** Aynı oturum üstte kartta "İzin bekliyor" diyorsa alttaki açık yardımcı satırında da aynı durumu söyler (C3). Bekleyen öğesi yoksa null. */
export function attnRowState(sessionId: string | number | null | undefined): { text: string; warm: boolean } | null {
  if (sessionId == null) return null
  const it = (at.snap?.items ?? []).find((i) => i.externalSessionId != null && String(i.externalSessionId) === String(sessionId) && ROW_STATE[i.reason ?? ''])
  const r = it ? ROW_STATE[it.reason] : null
  return r ? { text: t(r.label), warm: r.warm } : null
}

/** Söz dışında açık olup seni bekleyen yardımcı sayısı: "Benden Beklenen" sayacına eklenir. */
export const outsideWaitingCount = (): number => {
  const ids = new Set((at.snap?.items ?? []).filter((i) => (i.reason ?? '').startsWith('session_') && i.externalSessionId != null).map((i) => String(i.externalSessionId)))
  return waitingRows(pf.data).filter((r) => !ids.has(String(r.s.id))).length
}
