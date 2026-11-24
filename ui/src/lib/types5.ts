// Dalga 2 uçlarının türleri (API-5.md, gerçek sunucu cevaplarından). Alan adları sunucuyla birebir.

// ---------------- kayıt defteri ----------------
export interface LText {
  tr: string | null
  en: string | null
}

export type SettingClass = 'K1' | 'K2' | 'K3' | 'K4' | 'K5'
export type SettingControl = 'auto' | 'toggle' | 'number' | 'slider' | 'text' | 'path-file' | 'path-folder' | 'select' | 'secret' | 'shortcut' | 'json'
export type SettingGroup = 'genel' | 'gorunum-ses' | 'kapsul-bildirim' | 'arac-ajan' | 'kural-kanit' | 'proje' | 'veri-gizlilik' | 'gelismis'

export interface RegistryContract {
  discover?: LText | null
  disable?: LText | null
  onError?: LText | null
  onNewTool?: LText | null
}

export interface RegistryEntry {
  id: string
  kind: string
  owner: 'fixed' | 'user' | 'system' | string
  state: string
  declaredState: string | null
  awaitsApproval: boolean
  title: LText
  description: LText
  keywords: string[]
  surfaces: string[]
  permissions: { cap: string; scope: string; why: LText }[]
  stability: string
  since: string
  deprecated: unknown | null
  contract: RegistryContract | null
  origin: string
  sha256: string
  problems: string[]
  body: Record<string, unknown>
}

/** Ayar kaydının `body` alanı. Hepsi kayıttan gelir; arayüz ayar adı bilmez. */
export interface SettingBody {
  schema?: { type?: string | string[]; minimum?: number; maximum?: number; exclusiveMinimum?: number; enum?: (string | number)[]; maxLength?: number; minLength?: number }
  default?: unknown
  class?: SettingClass
  scopes?: string[]
  group?: SettingGroup | null
  card?: string
  order?: number
  control?: SettingControl
  unit?: string
  restart?: 'none' | 'next-run' | 'app' | 'capsule' | string
  validators?: string[]
  legacyKey?: string
  securityGate?: { restrictive?: string }
  clampOnRead?: boolean
}

export interface RegistryReply {
  kind: string | null
  count: number
  entries: RegistryEntry[]
  problems: unknown[]
}

export interface RegistryChange {
  id: string
  kind: string
  state: string
  origin: string
  changed: boolean
}

/** GET /api/settings: düz nesne; `adjusted` sunucunun okurken düzelttiği değerlerdir (ya anahtar adı ya {key, stored, applied, reason}). */
export type ServerSettings = Record<string, unknown> & { adjusted?: (string | { key: string; stored?: unknown; applied?: unknown; reason?: string })[] }

// ---------------- bitti'nin tanımı ----------------
export interface DoneOption {
  value: string
  label: string
  hint: string
}
export interface DoneQuestion {
  id: 'kind' | 'looks' | 'irreversible' | string
  text: string
  type: 'choice' | 'text' | 'yesno' | string
  options: DoneOption[]
  hint: string
}
export interface DoneLevel {
  level: 'A' | 'B' | 'C' | 'D' | string
  label: string
  meaning: string
}
export interface DoneFill {
  field: string
  label: string
  candidates: string[]
}
export interface DoneProposal {
  id: string
  template: string
  kind: string
  sentence: string
  level: 'A' | 'B' | 'C' | 'D' | string
  levelLabel: string
  who: string
  why: string
  ready: boolean
  enabledByDefault: boolean
  values: Record<string, string>
  fill: DoneFill[]
}
export interface DoneSuggestion {
  jobKind: string
  jobKindLabel: string
  found: string[]
  proposals: DoneProposal[]
  note: string
  /** Söz'ün kendi ölçebileceği, doldurulmuş en az bir madde var mı. */
  measurable?: boolean
  /** Yalnız kod işinde ve test komutu bulunamadıysa dolu. */
  testless?: DoneTestless | null
}
export interface DoneTestless {
  nothingToTry: boolean
  message: string
  action: string
  actionLabel: string
  startField: string
}
/** GET /api/done/summary: iş vermeden önce tek uç. needsFullCard=false ise tam kart açılmaz, line gösterilir. */
export interface DoneSummary {
  projectId: number
  jobId: number | null
  jobsGiven: number
  firstJob: boolean
  permanentCount: number
  measurableCount: number
  permanent: { id: number; label: string; kind: string; level: string; levelLabel: string; who: string }[]
  needsFullCard: boolean
  reason: 'first_job' | 'no_checks' | 'has_checks' | string
  line: string
  agentSuggested: { checkId: number; label: string; cmd: string; level: string; levelLabel: string; sentence: string }[]
}
export type DoneScope = 'job' | 'project'
export interface DoneAcceptReply {
  created: DoneCreated[]
  count: number
  scope?: DoneScope
  legacy?: boolean
  sentence?: string
}
/** GET /api/agents/ready: hiçbir istem göndermez; yalnız sürüm ve giriş dosyasının var olup olmadığına bakar. */
export type AgentReadyStatus = 'ready' | 'login_unknown' | 'login_missing' | 'broken' | 'not_found'
export interface AgentReady {
  id: string
  name: string
  found: boolean
  ready: boolean
  version: string | null
  login: string
  status: AgentReadyStatus | string
  canStart: boolean
  sentence: string
  action: string | null
  detail: string | null
  isDefault: boolean
}
export interface AgentsReady {
  canStart: boolean
  sentence: string
  defaultAgent: string
  agents: AgentReady[]
}
export interface DoneAcceptItem {
  template: string
  values: Record<string, string>
  enabled: boolean
}
export interface DoneCreated {
  id: number
  projectId: number
  jobId: number | null
  kind: string
  label: string
  enabled: boolean
}

// ---------------- koruma ve geri alma ----------------
export type ProtectionLevel = 'tam' | 'sinirli' | 'yok'
export interface Protection {
  jobId: number
  level: ProtectionLevel
  note: string | null
  text: string
  limits: string
  canUndo: boolean
  cannotUndoWhy: string | null
  runs: { runId: number; runUid: string; level: ProtectionLevel; note: string | null; beforeSnapshotUid: string | null; afterSnapshotUid: string | null; started: string }[]
}
export interface UndoItem {
  path: string
  action: string
  why: string
  text: string
}
export interface UndoReply {
  undoUid: string
  status: 'previewed' | 'applied' | 'reverted' | string
  summary: string
  restored: number
  removed: number
  renamed: number
  dirsCreated: number
  dirsRemoved: number
  skipped: number
  items: UndoItem[]
  conflicts: (string | { path?: string; text?: string; why?: string })[]
  unrestorable: (string | { path?: string; text?: string; why?: string })[]
  notes: string[]
}
export interface UndoRecord {
  uid: string
  jobId: number
  runId: number | null
  mode: string
  status: 'previewed' | 'applied' | 'reverted' | string
  startedAt: string
  finishedAt: string | null
  filesRestored: number
  filesRemoved: number
  filesSkipped: number
  conflicts: number
  error: string | null
}

// ---------------- Sıra bende ----------------
export interface AttentionAction {
  id: string
  label: string
  method: string
  path: string
  body: Record<string, unknown> | null
}
export interface AttentionItem {
  id: string
  kind: 'ask' | 'accept' | 'blocked' | 'done' | 'uncertain' | string
  reason: string
  priority: number
  sentence: string
  detail: string | null
  title: string
  jobUid: string | null
  jobId: number | null
  sessionId: number | null
  projectName: string | null
  at: string
  confidence: 'firm' | 'unknown' | string
  signal: boolean
  isNew: boolean
  actions: AttentionAction[]
  /** Dış oturum öğelerinde: yardımcının adı ("Claude Code"), oturumun dış kimliği, bilginin kaynağı (job, hook, log, agents, hook+agents, log+agents). */
  helper?: string | null
  externalSessionId?: string | null
  source?: string | null
  observedAt?: string | null
  ageSeconds?: number | null
  projectPath?: string | null
  /** Söz'ün kendi notu (yardımcının sözü değil). Ekranda "Söz'ün notu:" etiketiyle ayrı satırdır. */
  sozNotu?: string | null
  /** Yalnız biten oturum öğesinde dolu: false ise Söz bu işi deneyemez (klasörde kontrol yok ya da klasör kayıtlı değil). */
  canProve?: boolean | null
  canProveReason?: 'no_checks' | 'no_project' | string | null
}
export interface AttentionSnapshot {
  rev: number
  now: string
  sentence: string
  counts: { ask: number; accept: number; blocked: number; done: number; uncertain: number; total: number; running: number }
  budget: { dailyLimit: number; usedToday: number; suppressedToday: number; day: string }
  items: AttentionItem[]
  running?: { jobUid: string | null; jobId: number; title: string; projectName: string | null }[]
}
export interface NeedsYou {
  sessionId: number
  tool: string
  projectId: number | null
  projectName: string | null
  title: string | null
  state: string
  since: string | null
  reason: string | null
  question: string | null
  pendingQueue: number | null
  stateSource: 'log' | 'hook' | 'protocol' | 'process' | string | null
  stateConfidence: 'firm' | 'probable' | 'unknown' | string | null
  endClass: 'awaiting_user' | 'tool_call_pending' | 'truncated' | null
}
export interface NowLite {
  sentence?: string
  needsYou?: NeedsYou[]
  uncertain?: NeedsYou[]
}

// ---------------- tanışma ----------------
export interface OnboardingItem {
  id: string
  group: string
  title: string
  detail: string | null
  count: number | null
  confidence: string | null
  confidenceText: string | null
  state: 'suggested' | 'accepted' | 'changed' | 'dismissed' | string
  stateText: string | null
  present: boolean
  userValue: string | null
  owner: string
  evidenceCount: number
  firstSeen: string | null
  facts?: unknown
}
export interface OnboardingGroup {
  key: string
  title: string
  count: number
  items: OnboardingItem[]
}
export interface OnboardingView {
  notice: string
  total: number
  pending: number
  lastScan: string | null
  groups: OnboardingGroup[]
}
export interface OnboardingWhy {
  id: string
  title: string
  confidenceText: string | null
  answer: string
  evidence: { what: string; where: string | null; when: string | null }[]
}
export interface OnboardingAccept extends OnboardingItem {
  connector?: { id: string; state: string | null }
}
export interface ScanReply {
  outcome: { found: number; new: number; updated: number; unchanged: number; gone: number; keptDismissed: number; droppedAsSecret: number; unreadable: number; couldNotRead: string[]; ms: number }
  view: OnboardingView
}

// ---------------- keşif (yalnız veri katmanı) ----------------
export interface DiscoverySlot {
  slot: number
  objectUid: string | null
  title: string | null
  source: string | null
  placedAt: string | null
}
export interface DiscoveryDirection {
  dir: number
  code: string
  title: string
  compass: string
  slots: DiscoverySlot[]
}
export interface DiscoveryLayout {
  installed: boolean
  total: number
  filled: number
  directions: DiscoveryDirection[]
}
export interface DiscoveryHit {
  objectUid: string
  title: string
  kindDir: number | string
  description?: string | null
  match?: string
  weight?: number
  /** Öneri cevabında: tek cümlelik neden ve puan. */
  reason?: string | null
  reasonCode?: string | null
  score?: number
}
export interface DiscoverySearch {
  installed: boolean
  query: string
  items: DiscoveryHit[]
}
export interface DiscoverySuggestions {
  batchId: string | number
  shadow: boolean
  items: DiscoveryHit[]
  /** İlk gün: kullanım birikmeden de gerçek dayanağı olan öneriler (ör. tanışmada bulunmuş ama kapalı araç). Gölge kipten bağımsız. */
  firstDay?: DiscoveryHit[]
  /** Öneri yokken nedeni (sade cümle). */
  emptyText?: string | null
  /** Öneriye en yakın şey: kaç kez kullanıldı, kaç kez daha kullanılınca önerilir. */
  nearest?: { objectUid: string; title: string; count: number; needed: number; text: string } | null
}
export interface DiscoveryShadow {
  days: number
  batches: number
  hits: number
  hitRate: number | null
  rank1Rate: number | null
  shown: number
  note: string | null
}

// ---------------- kişi onay listesi ----------------
export interface ChecklistState {
  checkId: number
  label: string
  jobId: number | null
  items: { index: number; text: string; ticked: boolean }[]
  waiting: number
  allTicked: boolean
}

// ---------------- Ne oldu (olay defterinin okunan hali) ----------------
export type FeedGroup = 'job' | 'guard' | 'setting' | 'system'
export interface FeedLine {
  id: string
  at: string
  group: FeedGroup
  groupLabel: string
  kind: string
  sentence: string
  jobId: number | null
  jobTitle: string | null
  projectId: number | null
  projectName: string | null
  folder: string | null
}
export interface FeedPage {
  items: FeedLine[]
  hasMore: boolean
  next: string | null
  limit: number
  group: FeedGroup | null
  groups: { id: FeedGroup; label: string }[]
}

// ---------------- oturum özeti (oz1) ----------------
/** GET /api/sessions/{id}/ozet ve GET /api/sessions/ozet?tool=&sessionId= gövdesi. Üç cümle sunucuda kurulur; ekran yalnız gösterir. */
export interface OturumOzet {
  neYapti: string
  denediMi: string
  sendenBeklenen: string
  bittiDedi?: boolean
  bittiSonrasiDeneme?: boolean
  sonDeneme?: { komutTuru?: string | null; sonuc?: 'gecti' | 'kaldi' | 'bilinmiyor' | string; an?: string | null; cikisKodu?: number | null } | null
  bekleyen?: 'izin' | 'soru' | 'inceleme' | 'yok' | string
  bekleyenKesin?: boolean
  guven?: 'yuksek' | 'dusuk' | string
  guvenNedeni?: string | null
}
