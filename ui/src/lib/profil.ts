// "Projelerim" ekranının veri katmanı: GET /api/profile ve GET /api/profile/sessions (yalnız okuma) ile onboarding taraması.
// Sunucunun verdiği alanlar aynen kullanılır; olmayan alan uydurulmaz. Gruplama ve metin eşlemeleri burada, saf işlevlerdir.
import { qs, request } from './api'
import { ApiError } from './errors'
import { t, type Key } from './i18n/index.svelte'
import { parseTs } from './util'

export interface ProfArea { path: string; sessions: number; rootSessions: number; subagentSessions: number; hasGit: boolean }
export interface ProfProject {
  name: string
  path: string
  displayHint: string
  drive: string
  missingFolder: boolean
  sessionsByTool: Record<string, number>
  /** Zamanı bilinmeyen oturumlardan oluşan proje: null. */
  firstActivity: string | null
  lastActivity: string | null
  sessions7Days: number
  sessions30Days: number
  hasGit: boolean
  hasTests: boolean
  rootSessions: number
  subagentSessions: number
  areas: ProfArea[]
  soluk: boolean
  status: string
  lastQuietDays: number | null
}
export interface ProfLiveSession {
  tool: string
  id: string
  lastActivity: string | null
  level: 'yaziyor' | 'sicak' | 'bugun' | 'acik' | string | null
  acikAdayi: boolean
  title: string | null
  pid?: number | null
  hwnd?: number | string | null
  canFocus?: boolean
  cwd?: string | null
  projectPath?: string | null
  windowTitle?: string | null
  /** Sunucunun açıklık kanıtı: 'surec' (süreç açık), 'yakin-zaman' (yakın zamanda hareket), 'yok' (kanıt yok). Eski sunucuda alan hiç yoktur. */
  evidence?: 'surec' | 'yakin-zaman' | 'yok' | string | null
  /** Sunucunun durum kararı: 'calisiyor', 'seni-bekliyor', 'bosta', 'bilinmiyor'. Eski sunucuda alan yoktur: eski türetme kullanılır. */
  state?: 'calisiyor' | 'seni-bekliyor' | 'bosta' | 'bilinmiyor' | string | null
  /** state = seni-bekliyor iken ne beklediği (ör. "izin istiyor", "girdi bekliyor"). */
  waitingFor?: string | null
}
export interface ProfRelation { tool: string; sessionId: string; otherTool: string; otherSessionId: string; relation: string }
export interface ProfLive { projectPath: string | null; sessions: ProfLiveSession[]; relations: ProfRelation[] }
export interface ProfBreakdown { count: number; rootSessions: number; subagentSessions: number; sessions7Days: number; sessions30Days: number }
export interface ProfDisk { letter: string; sizeBytes: number | null; freeBytes: number | null }
export interface ProfSource { tool: string; okunan: number; okunamayan: number; sessions: number; observedAt: string; warning?: string | null; reasons?: Record<string, number> | null }
export type ProfWarning = string | { tool?: string | null; message?: string | null; text?: string | null; detail?: string | null }
export interface Profile {
  machine: { memoryBytes: number | null; cpuName: string | null; physicalCores: number | null; logicalCores: number | null; gpuName: string | null; disks: ProfDisk[] }
  projects: ProfProject[]
  unassigned: { count: number; reasons: Record<string, number>; byTool?: Record<string, ProfBreakdown>; byReason?: Record<string, ProfBreakdown>; sessions7Days: number; sessions30Days: number }
  live: ProfLive[]
  counts: { sessions: number; projects: number; facts: number }
  scanMs: number
  sources: ProfSource[]
  /** Son taramanın UTC anı. Hiç tarama yoksa null. Eski sunucuda alan hiç yoktur (undefined). */
  scannedAt?: string | null
  /** Okunamayan ya da biçimi değişmiş kaynak uyarıları (Kapı 9: kırılınca susmaz). */
  warnings?: ProfWarning[] | null
  /** "Şu an açık" bölümünün hesaplandığı an (UTC). Eski sunucuda yok. */
  liveObservedAt?: string | null
  /** Açık yardımcılar nereden öğrenildi: 'agents-komutu' | 'kayit-dosyasi' | 'surec-tahmini' | null. */
  liveSource?: string | null
  /** Yedek yola düşüldüyse sunucunun sade açıklaması. */
  liveNote?: string | null
}
export interface ProfSession {
  tool: string
  id: string
  cwd: string | null
  gitBranch: string | null
  startedAt: string | null
  lastActivity: string | null
  parentId: string | null
  title: string | null
  sourceChannel: string | null
}
export interface ProfSessions { project: string; limit: number; total: number; count: number; sessions: ProfSession[] }

/** Boş ya da bozuk cevap (null, dizi yok) sessizce "boş ekran" olmasın: açık hata olur. */
export async function getProfile(): Promise<Profile> {
  const p = await request<Profile>('GET', '/api/profile')
  if (!p || typeof p !== 'object' || !Array.isArray(p.projects)) throw new ApiError(200, 'Boş cevap geldi')
  return p
}
export const getProfileSessions = (project: string, limit = 20) => request<ProfSessions>('GET', `/api/profile/sessions${qs({ project, limit })}`)
export interface ScanProgress { running: boolean; phase: string; done: number; total: number; tool: string | null; percent: number }
export const getScanProgress = () => request<ScanProgress>('GET', '/api/profile/scan-progress')
export async function runProfileScan(): Promise<unknown> {
  try {
    return await request<unknown>('POST', '/api/profile/scan', {})
  } catch {
    return await request<unknown>('POST', '/api/onboarding/scan', {})
  }
}
export interface LiveFocusInput {
  sessionId?: string | number | null
  tool?: string | null
  pid?: number | null
  hwnd?: number | string | null
  path?: string | null
  canFocus?: boolean
}
export async function focusLiveWindow(input: LiveFocusInput): Promise<{ ok: boolean; openedFolder?: boolean; tabSelected?: boolean; hint?: string }> {
  const hasHandle = input.canFocus || input.pid != null || (input.hwnd != null && input.hwnd !== 0 && input.hwnd !== '0')
  if (hasHandle || !input.path) {
    try {
      const res = await request<{ tabSelected?: boolean; hint?: string | null } | null>('POST', '/api/live/focus', {
        sessionId: input.sessionId ?? null,
        tool: input.tool ?? null,
        pid: input.pid ?? null,
        hwnd: input.hwnd ?? null,
        path: input.path ?? null,
      })
      return { ok: true, tabSelected: res?.tabSelected === true, hint: typeof res?.hint === 'string' && res.hint.trim() ? res.hint.trim() : undefined }
    } catch (e) {
      if (!input.path) throw e
    }
  }
  await request<unknown>('POST', '/api/live/open-folder', { path: input.path })
  return { ok: true, openedFolder: true }
}
const TERMINAL_TOOLS = new Set(['claude', 'claude-code', 'codex', 'hermes', 'opencode'])
/** Pencere öne getirilince söylenecek cümle. Sunucu sekmeyi seçtiğini söylemediyse (tabSelected) terminal araçlarında sekmeyi bilemediğimiz açıkça yazılır. */
export function focusToastText(res: { openedFolder?: boolean; tabSelected?: boolean; hint?: string | null }, tool?: string | null): string {
  if (res.openedFolder) return t('suAnFolderOpened')
  if (res.tabSelected === true) return t('suAnWinFocused') // doğru sekme seçildi: ek uyarı yok
  // Seçilemediyse sunucunun sade ipucu cümlesi (hangi proje, kaç sekme) varsa o, yoksa mevcut "hangi sekme olduğunu bilemiyorum" cümlesi.
  if (tool && TERMINAL_TOOLS.has(tool.toLowerCase())) return res.hint?.trim() || t('suAnWinFocusedTab')
  return t('suAnWinFocused')
}
export const openProjectFolder = (path: string) => request<unknown>('POST', '/api/live/open-folder', { path })

// ---- araçlar ----
export const TOOLS = ['claude', 'codex', 'antigravity', 'vscode', 'cursor', 'hermes', 'opencode'] as const
const COMPACT_LABELS: Record<string, string> = {
  claude: 'Claude',
  'claude-code': 'Claude',
  codex: 'Codex',
  antigravity: 'Antigravity',
  vscode: 'VS Code',
  'vs-code': 'VS Code',
  cursor: 'Cursor',
  hermes: 'Hermes',
  opencode: 'OpenCode',
}
const FULL_LABELS: Record<string, string> = {
  ...COMPACT_LABELS,
  claude: 'Claude Code',
  'claude-code': 'Claude Code',
}
export function toolLabel(tool: string): string {
  const k = (tool ?? '').trim().toLowerCase()
  return COMPACT_LABELS[k] ?? tool
}
export function toolFullLabel(tool: string): string {
  const k = (tool ?? '').trim().toLowerCase()
  return FULL_LABELS[k] ?? tool
}
export function toolEntries(by: Record<string, number> | undefined): [string, number][] {
  const e = Object.entries(by ?? {}).filter(([, n]) => n > 0)
  const rank = (k: string) => { const i = (TOOLS as readonly string[]).indexOf(k); return i < 0 ? 99 : i }
  return e.sort((a, b) => rank(a[0]) - rank(b[0]) || b[1] - a[1])
}

// ---- zaman ----
/** MinValue (0001-...) gibi anlamsız zamanlar yok sayılır (NaN döner). */
export function validTs(ts: string | null | undefined): number {
  const n = parseTs(ts)
  return Number.isFinite(n) && n > Date.UTC(2000, 0, 1) ? n : NaN
}
/** "az önce", "25 dk önce", "3 sa önce", "2 gün önce", "3 hf önce", "5 ay önce". Bilinmeyen zaman: boş. */
export function agoShort(ts: string | null | undefined, now = Date.now()): string {
  const n = validTs(ts)
  if (!Number.isFinite(n)) return ''
  const min = Math.floor((now - n) / 60000)
  if (min < 1) return t('justNow')
  if (min < 60) return t('ago', { t: `${min} ${t('min')}` })
  const h = Math.floor(min / 60)
  if (h < 24) return t('ago', { t: `${h} ${t('hour')}` })
  const d = Math.floor(h / 24)
  if (d < 14) return t('ago', { t: `${d} ${t('day')}` })
  if (d < 60) return t('ago', { t: `${Math.floor(d / 7)} ${t('pjWeek')}` })
  return t('ago', { t: `${Math.floor(d / 30)} ${t('pjMonth')}` })
}

/** Son tarama zamanı: sunucunun `scannedAt` alanı. Alan hiç yoksa (eski sunucu) kaynakların gözlem zamanlarının en yenisi yedek olarak kullanılır. Tarama yoksa null. */
export function lastScanTs(p: Profile): string | null {
  if (p.scannedAt !== undefined) return validTs(p.scannedAt) > 0 ? (p.scannedAt as string) : null
  let best = NaN
  let raw: string | null = null
  for (const s of p.sources ?? []) {
    const n = validTs(s.observedAt)
    if (Number.isFinite(n) && (!Number.isFinite(best) || n > best)) { best = n; raw = s.observedAt }
  }
  return raw
}
/** Tarama yapıldı mı: `scannedAt` doluysa evet, açıkça null ise hayır. Alan yoksa (eski sunucu) kaynak listesi dolu mu diye bakılır. */
export const hasScan = (p: Profile) => (p.scannedAt !== undefined ? p.scannedAt !== null : (p.sources?.length ?? 0) > 0)

// ---- proje bölümleri ----
export type SectionId = 'etkin' | 'yasayan-sistem' | 'duraklamis' | 'yeni' | 'belirsiz' | 'eski'
export const SECTIONS: { id: SectionId; label: Key }[] = [
  { id: 'etkin', label: 'pjSecActive' },
  { id: 'yasayan-sistem', label: 'pjSecLiving' },
  { id: 'duraklamis', label: 'pjSecPaused' },
  { id: 'yeni', label: 'pjSecNew' },
  { id: 'belirsiz', label: 'pjSecUnsure' },
  { id: 'eski', label: 'pjSecOld' },
]
export function sectionOf(p: ProfProject): SectionId {
  return (SECTIONS.some((s) => s.id === p.status) ? p.status : 'belirsiz') as SectionId
}
/** Bölümlere böler; her bölümde son etkinliği yeni olan üstte. Boş bölüm döndürülmez. */
export function groupProjects(projects: ProfProject[]): { id: SectionId; label: Key; items: ProfProject[] }[] {
  const by = new Map<SectionId, ProfProject[]>()
  for (const p of projects) {
    const id = sectionOf(p)
    if (!by.has(id)) by.set(id, [])
    by.get(id)!.push(p)
  }
  // Zamanı bilinmeyen proje bölümün sonuna düşer (en eski bilinen zamandan da sonra).
  const ts = (p: ProfProject) => { const n = validTs(p.lastActivity); return Number.isFinite(n) ? n : -Infinity }
  return SECTIONS.filter((s) => by.has(s.id)).map((s) => ({ ...s, items: by.get(s.id)!.sort((a, b) => (ts(a) === ts(b) ? 0 : ts(b) > ts(a) ? 1 : -1) || a.name.localeCompare(b.name)) }))
}

// ---- yol ----
/** Yol kısa gösterilir: sürücü ve son iki klasör, ortası "…" ile kırpılır. Tam yol ipucunda durur. */
export function shortPath(p: string): string {
  const parts = p.split(/[\\/]+/).filter(Boolean)
  if (parts.length <= 3) return p.replace(/[\\/]+$/, '')
  return `${parts[0]}\\…\\${parts.slice(-2).join('\\')}`
}
/** Proje klasörüne göre göreli yol. Dışındaysa tam yol. */
export function relPath(root: string, p: string): string {
  const r = root.replace(/[\\/]+$/, '').toLowerCase()
  const q = p.toLowerCase()
  return q.startsWith(r + '\\') || q.startsWith(r + '/') ? p.slice(r.length + 1) : p
}

// ---- bu bilgisayar ----
const GIB = 1024 ** 3
export const gbText = (n: number) => (n >= 100 * GIB ? `${Math.round(n / GIB)}` : `${Math.round((n / GIB) * 10) / 10}`).replace('.', ',') + ' GB'
export const memoryText = (bytes: number | null | undefined) => (bytes ? `${Math.round(bytes / GIB)} GB` : '')
export function cpuShort(name: string | null | undefined): string {
  return (name ?? '').replace(/\((R|TM|tm)\)/g, '').replace(/\b\d+(st|nd|rd|th) Gen\b/i, '').replace(/\s+CPU\b.*$/i, '').replace(/\s+/g, ' ').trim()
}
export const diskLetter = (l: string) => l.replace(/[\\/]+$/, '')
/** Boş yer yüzde 10'un altındaysa uyarı. Boyut bilinmiyorsa uyarı yok. */
export const diskLow = (d: ProfDisk) => !!d.sizeBytes && d.freeBytes != null && d.freeBytes / d.sizeBytes < 0.1

// ---- projeye bağlanmayan oturumlar ----
const REASON_KEYS: Record<string, Key> = {
  'klasor-bilinmiyor': 'pjRsUnknown',
  'tek-seferlik': 'pjRsOneOff',
  'kap-klasor': 'pjRsContainer',
  'soz-verisi-ve-denemesi': 'pjRsSoz',
  'arac-kayit-klasoru': 'pjRsToolRecords',
  'arac-profil-klasoru': 'pjRsToolFolder',
  'kullanici-evi': 'pjRsHome',
  'ozel-kap-klasoru': 'pjRsDesktop',
  'surucu-koku': 'pjRsDriveRoot',
  'sistem-klasoru': 'pjRsSystem',
  'gecici-klasor': 'pjRsTemp',
}
export function reasonText(reason: string): string {
  if (reason.startsWith('hermes-source:')) {
    const ch = reason.slice('hermes-source:'.length)
    return ch === 'cron' ? t('pjRsCron') : t('pjRsChat', { k: ch })
  }
  const k = REASON_KEYS[reason]
  return k ? t(k) : t('pjRsOther')
}
export function reasonRows(u: Profile['unassigned']): { reason: string; text: string; count: number }[] {
  return Object.entries(u.reasons ?? {})
    .map(([reason, count]) => ({ reason, text: reasonText(reason), count }))
    .sort((a, b) => b.count - a.count || a.text.localeCompare(b.text))
}

// ---- şu an açık ----
export interface LiveGroup { path: string | null; name: string; hint: string; sessions: { s: ProfLiveSession; child: boolean }[] }
/** Yardımcının şu anki durumu: çalışıyor, seni bekliyor (senden cevap ya da izin), boşta (açık ama yazmıyor), durdu (açık görünmüyor ama az önce çalışmış). */
export type LiveState = 'working' | 'you' | 'idle' | 'unknown' | 'stopped'
const DAY_MS = 24 * 60 * 60 * 1000
const levelRank = (s: ProfLiveSession) => (s.level === 'yaziyor' ? 0 : s.canFocus || s.pid != null || s.acikAdayi || s.level === 'acik' ? 1 : s.level === 'sicak' ? 2 : 3)
const skey = (s: { tool: string; id: string }) => `${s.tool}\u0000${s.id}`
const tsOf = (s: ProfLiveSession) => { const n = validTs(s.lastActivity); return Number.isFinite(n) ? n : 0 }

/** Listeye aday olan kayıtlar: yazıyor, az önce çalışmış, açık olabilir ya da penceresi/süreci bilinen. Sıradan geçmiş kayıtlar hiçbir yerde gösterilmez. */
const isCandidate = (s: ProfLiveSession) => s.level === 'yaziyor' || s.level === 'sicak' || s.level === 'acik' || s.acikAdayi || !!s.canFocus || s.pid != null

/** Gerçekten açık ya da az önce çalışmış mı. Sunucu `evidence` verdiyse ona uyulur. Vermediyse (eski sunucu) son hareketi 24 saatten eski "açık olabilir" kayıtlar açık sayılmaz. */
export function isLiveNow(s: ProfLiveSession, now = Date.now()): boolean {
  if (s.evidence != null && s.evidence !== '') return s.evidence === 'surec' || s.evidence === 'yakin-zaman'
  if (s.level === 'yaziyor' || s.level === 'sicak' || s.pid != null || s.canFocus) return true
  const ts = validTs(s.lastActivity)
  return Number.isFinite(ts) && now - ts <= DAY_MS
}

export function liveState(s: ProfLiveSession): LiveState {
  // Sunucu kendi kararını verdiyse ona uyulur (eski sunucuda alan yok: aşağıdaki türetme).
  if (s.state === 'seni-bekliyor') return 'you'
  if (s.state === 'calisiyor') return 'working'
  if (s.state === 'bosta') return 'idle'
  if (s.state === 'bilinmiyor') return 'unknown' // Hermes, Antigravity ve komut bilgisi olmayan kayıtlar: "çalışıyor" denmez
  if (s.level === 'yaziyor') return 'working'
  if (s.evidence === 'surec' || s.pid != null || s.canFocus) return 'idle'
  return 'stopped'
}

export interface LiveRow { s: ProfLiveSession; child: boolean; projectName: string; path: string | null; hint: string; state: LiveState }

/** Açık işleri iki parçaya böler: gerçekten açık olanlar ve "eskiden açık kalmış olabilir" kayıtları (hiçbiri kaybolmaz). */
export function liveSplit(p: Profile, now = Date.now()): { groups: LiveGroup[]; rows: LiveRow[]; old: LiveRow[] } {
  const byPath = new Map(p.projects.map((x) => [x.path.toLowerCase(), x]))
  const groups: LiveGroup[] = []
  const old: LiveRow[] = []
  for (const g of p.live ?? []) {
    const cands = (g.sessions ?? []).filter(isCandidate)
    if (cands.length === 0) continue
    const proj = g.projectPath ? byPath.get(g.projectPath.toLowerCase()) : undefined
    const name = g.projectPath ? (proj?.name ?? g.projectPath.split(/[\\/]/).filter(Boolean).pop() ?? g.projectPath) : t('pjLiveNoProject')
    const hint = g.projectPath ? shortPath(proj?.displayHint ?? g.projectPath) : ''
    const list = cands.filter((s) => isLiveNow(s, now))
    for (const s of cands) if (!list.includes(s)) old.push({ s, child: false, projectName: name, path: g.projectPath, hint, state: 'stopped' })
    if (list.length === 0) continue
    const present = new Set(list.map(skey))
    const parentOf = new Map<string, string>()
    for (const r of g.relations ?? []) {
      // "ana ve yardımcı": ilk taraf ana, ikinci taraf yardımcı.
      const a = skey({ tool: r.tool, id: r.sessionId })
      const b = skey({ tool: r.otherTool, id: r.otherSessionId })
      if (r.relation === 'ana ve yardımcı' && present.has(a) && present.has(b)) parentOf.set(b, a)
    }
    const sorted = [...list].sort((a, b) => levelRank(a) - levelRank(b) || tsOf(b) - tsOf(a))
    const ordered: { s: ProfLiveSession; child: boolean }[] = []
    const used = new Set<string>()
    for (const s of sorted) {
      if (parentOf.has(skey(s)) || used.has(skey(s))) continue
      ordered.push({ s, child: false }); used.add(skey(s))
      for (const c of sorted) if (parentOf.get(skey(c)) === skey(s) && !used.has(skey(c))) { ordered.push({ s: c, child: true }); used.add(skey(c)) }
    }
    for (const s of sorted) if (!used.has(skey(s))) { ordered.push({ s, child: false }); used.add(skey(s)) }
    groups.push({ path: g.projectPath, name, hint, sessions: ordered })
  }
  const best = (g: LiveGroup) => Math.min(...g.sessions.map((x) => levelRank(x.s)))
  const newest = (g: LiveGroup) => Math.max(...g.sessions.map((x) => tsOf(x.s)))
  groups.sort((a, b) => (a.path ? 0 : 1) - (b.path ? 0 : 1) || best(a) - best(b) || newest(b) - newest(a) || a.name.localeCompare(b.name, 'tr'))
  const rows: LiveRow[] = groups.flatMap((g) => g.sessions.map((x) => ({ s: x.s, child: x.child, projectName: g.name, path: g.path, hint: g.hint, state: liveState(x.s) })))
  old.sort((a, b) => tsOf(b.s) - tsOf(a.s))
  return { groups, rows, old }
}
/** Senden cevap ya da izin bekleyen, Söz dışında açık yardımcılar (sunucu state = seni-bekliyor dedi). */
export const waitingRows = (p: Profile | null | undefined, now = Date.now()): LiveRow[] => (p ? liveSplit(p, now).rows.filter((r) => r.state === 'you') : [])
/** "Son bakış: 12 sn önce". Sunucu liveObservedAt vermediyse boş döner (eski sunucu). */
export function liveSeenText(p: Profile | null | undefined, now = Date.now()): string {
  const ts = validTs(p?.liveObservedAt)
  if (!Number.isFinite(ts)) return ''
  const sec = Math.max(0, Math.floor((now - ts) / 1000))
  return t('liveSeen', { a: sec < 60 ? t('liveSeenSec', { n: sec }) : agoShort(p?.liveObservedAt, now) })
}
export const liveGroups = (p: Profile, now = Date.now()): LiveGroup[] => liveSplit(p, now).groups

/** Başlık yoksa saatle ayırt edilir: boş ad ("Claude oturumu") üretilmez. Proje ve yardımcı satırda ayrıca durur. */
export function sessionName(s: ProfLiveSession, child = false): string {
  const own = s.title?.trim() || s.windowTitle?.trim()
  if (own) return own
  const ts = validTs(s.lastActivity)
  const c = Number.isFinite(ts) ? new Date(ts).toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' }) : ''
  return t(child ? 'liveUntitledChild' : 'liveUntitled', { c })
}

// ---- açılan projenin oturum listesi ----
export function nestSessions(list: ProfSession[]): { s: ProfSession; child: boolean }[] {
  const ids = new Set(list.map(skey))
  // Zamanı bilinmeyen oturumlar sona; bilinenlerin sunucudaki sırası korunur.
  const known = (s: ProfSession) => Number.isFinite(validTs(s.lastActivity))
  const roots = list.filter((s) => !s.parentId || !ids.has(skey({ tool: s.tool, id: s.parentId }))).sort((a, b) => Number(known(b)) - Number(known(a)))
  const out: { s: ProfSession; child: boolean }[] = []
  for (const r of roots) {
    out.push({ s: r, child: !!r.parentId })
    for (const c of list) if (c.parentId === r.id && c.tool === r.tool) out.push({ s: c, child: true })
  }
  return out
}

/** Kapı 9: Okunamayan ya da biçimi değişmiş kaynak uyarılarını sade satırlara çevirir. */
export function profileWarnings(p: Profile | null | undefined): string[] {
  if (!p) return []
  const lines: string[] = []
  for (const w of p.warnings ?? []) {
    if (typeof w === 'string' && w.trim()) lines.push(w.trim())
    else if (w && typeof w === 'object') {
      const msg = (w.message ?? w.text ?? w.detail ?? '').trim()
      if (msg) lines.push(w.tool ? `${toolFullLabel(w.tool)}: ${msg}` : msg)
    }
  }
  for (const s of p.sources ?? []) {
    if (s.warning?.trim()) {
      const line = `${toolFullLabel(s.tool)}: ${s.warning.trim()}`
      if (!lines.includes(line)) lines.push(line)
    } else if (s.okunamayan > 0 && lines.length === 0) {
      lines.push(t('pjSourceUnreadable', { tool: toolFullLabel(s.tool), n: s.okunamayan }))
    }
    const fmt = s.reasons?.['bicim-taninmadi'] ?? 0
    if (fmt > 0) {
      const tl = toolFullLabel(s.tool)
      // Sunucunun kendi cümlesi bu kaynağı zaten anlatıyorsa tekrar yazılmaz.
      if (!lines.some((l) => l.includes(tl) && /biçim/i.test(l))) lines.push(t('pjFormatUnknown', { tool: tl, n: fmt }))
    }
  }
  return lines
}
