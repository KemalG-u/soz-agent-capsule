// Alan adları SozServer.cs / SozService.cs kayıt tipleriyle birebir (camelCase). Gerçek uç: _gecici/API-FARKLARI.md

export type Tool = 'claude' | 'codex' | 'antigravity' | 'vscode' | 'cursor' | 'hermes' | 'opencode' | 'manual' | (string & {})
export type ThemeFamily = 'cam' | 'terminal' | 'mat' | 'ton' | 'duz' | 'sicak'
export type DecisionType = 'rule' | 'ban' | 'decision' | 'preference' | 'correction'
export type DecisionStatus = 'proposed' | 'approved' | 'archived' | 'rejected'
export type SessionState = 'working' | 'waiting' | 'stalled' | 'idle' | 'stale'
export type SessionKind = 'human' | 'automation' | 'subagent'

export interface SessionToken {
  token: string
  version: string
  dataDir: string
  /** Veri klasörünün neden bu olduğu (ör. ortam değişkeni, varsayılan); eski sunucuda yok. */
  dataDirSource?: string | null
  language: string
}

export interface SourceInfo {
  tool: string
  root: string | null
  origin?: string | null
  found: boolean
  files: number
  unrecognized: number
}

export interface Summary {
  projects: number
  sessions: number
  utterances: number
  proposed: number
  approved: number
  repeatClusters: number
  lastImportAt: string | null
  waiting: number
  working: number
  stalled: number
  sources: SourceInfo[]
}

export interface Project {
  id: number
  name: string
  path: string | null
  utterances: number
  proposed: number
  approved: number
  repeats: number
  lastAt: string | null
  gateEnabled?: boolean
}

export interface DecisionOrigin {
  tool: string
  ts: string
  sessionId: number
}

export interface Decision {
  id: number
  projectId: number
  projectName: string
  type: DecisionType
  text: string
  status: DecisionStatus
  pinned: boolean
  repeatCount: number
  sessionCount: number
  createdAt: string
  updatedAt: string
  origin: DecisionOrigin | null
}

export interface DecisionPatch {
  status?: DecisionStatus
  text?: string
  type?: DecisionType
  pinned?: boolean
  projectId?: number
}

export interface RepeatMember {
  utteranceId: number
  text: string
  ts: string
  tool: string
  sessionId: number
}

export interface RepeatCluster {
  clusterId: number
  projectId: number
  projectName: string
  canonical: string
  count: number
  sessionCount: number
  firstTs: string
  lastTs: string
  decisionId: number | null
  members: RepeatMember[]
}

export interface UtteranceItem {
  id: number
  text: string
  ts: string
  tool: string
  projectName: string | null
  sessionId: number
}

export interface UtterancePage {
  total: number
  items: UtteranceItem[]
}

export interface Packet {
  text: string
  chars: number
  decisionCount: number
}

export interface SessionInfo {
  id: number
  tool: string
  projectId: number | null
  projectName: string | null
  title: string | null
  kind: SessionKind
  state: SessionState
  since: string
  lastUserText: string | null
  lastUserTs: string | null
  lastAssistantText: string | null
  lastAssistantTs: string | null
  asksQuestion: boolean
  claimsDone: boolean
  showsEvidence: boolean
  subCount: number
}

export interface Digest {
  text: string
  waiting: number
  working: number
  idle: number
  stalled: number
}

export interface ReportRepeat {
  clusterId: number
  text: string
  count: number
  sessionCount: number
}

export interface ReportDay {
  date: string
  utterances: number
}

export interface ReportTool {
  tool: string
  sessions: number
  utterances: number
  compactions: number
}

export interface Report {
  days: number
  from: string
  to: string
  projects: number
  sessions: number
  utterances: number
  tools: ReportTool[]
  compactions: number
  candidates: number
  repeatClusters: number
  totalRepeats: number
  topRepeats: ReportRepeat[]
  daily: ReportDay[]
  automationSessions: number
  subagentSessions: number
  proposed: number
  approved: number
}

// Ayar anahtarları: bölüm 11.4 (+ workingSeconds, idleMinutes). Ayarlanmamış yollar sunucuda null gelir.
export interface Settings {
  language: 'tr' | 'en'
  theme: 'system' | 'light' | 'dark'
  themeFamily?: ThemeFamily
  navTabs?: string[] | null
  claudeRoot: string | null
  codexRoot: string | null
  extractThreshold: number
  repeatThreshold: number
  packetBudget: number
  waitingStaleHours: number
  stalledMinutes: number
  workingSeconds: number
  idleMinutes: number
  explainEnabled: boolean
  explainCommand: string
  allowRealProjectWrites: boolean
  dictationPath: string | null
  hotkeyPacket: string
  capsuleX: number | null
  capsuleY: number | null
  lanEnabled?: boolean
  /** Claude programının yeri. Boşsa Söz kendisi bulur (null). */
  claudePath?: string | null
  /** Codex programının yeri. Boşsa Söz kendisi bulur (null). */
  codexPath?: string | null
  /** Yeni işte varsayılan ajan aracı: claude ya da codex. */
  defaultAgent?: AgentId
  /** Aynı anda en çok kaç iş çalışsın (1-6). */
  maxParallelRuns?: number
  /** Hakem model kolu: ajanın son sözünü sınıflamak için model çağırır. Varsayılan kapalı. */
  judgeClaudeEnabled?: boolean
  /** Model adına göre fiyat (1 milyon token başına USD). Yoksa null. */
  prices?: Record<string, ModelPrice> | null
}

/** Ajan aracı kimliği (bağlayıcı). */
export type AgentId = string

/** GET /api/agents satırı: araç bulundu mu, sürümü, neleri yapabildiği. */
export interface AgentInfo {
  id: AgentId
  name: string
  found: boolean
  ready: boolean
  version: string | null
  path: string | null
  configuredPath: string | null
  error: string | null
  capabilities: {
    headless: boolean
    eventStream: boolean
    resume: boolean
    stop: boolean
    /** native_hook: araçtan önce yerli kanca; protocol_permission: izin protokolü; os_sandbox: işletim sistemi sınırı; none */
    guardTier: 'native_hook' | 'protocol_permission' | 'os_sandbox' | 'none'
    /** native_hook: bitişte yerli kanca; outer_loop: Söz tur bitince kendisi kapıyı koşar ve geri yollar; none */
    turnEnd: 'native_hook' | 'outer_loop' | 'none'
    ask: 'resume_reply' | 'live' | 'none'
    /** dollars | tokens | none */
    cost: 'dollars' | 'tokens' | 'none'
  }
  /** kanca | sorulur | sınırlı | yok */
  guardLabel: string
  isDefault: boolean
}
export interface AgentsReply {
  defaultAgent: AgentId
  agents: AgentInfo[]
}

export interface JobSpend {
  runs: number
  inputTokens: number | null
  cachedTokens: number | null
  outputTokens: number | null
  reasoningTokens: number | null
  costUsd: number | null
}

export interface ModelPrice {
  input: number
  output: number
  cacheRead?: number
  cacheWrite?: number
}

// POST /api/import cevabı (Soz.Core ImportSummary; yalnız arayüzün kullandığı alanlar)
export interface ImportResult {
  newUtterances: number
  newCandidates: number
  newClusters: number
  newRepeatClusters: number
  newProposals: number
  updatedProposals: number
  totalMs: number
  finishedAt: string
}

// ================= Söz 2 (FABLE-VIZYON-2 bölüm 3 ve 5) =================
// Gerçek JSON biçimleri çekirdek ajanlarının _gecici/API-FARKLARI-2*.md dosyalarından eşitlenir; eşitlenene kadar
// alanlar sözleşmeden türetilmiştir ve arayüz eksik alana dayanıklıdır (? ile).

export type Verdict = 'passed' | 'failed' | 'unchecked' | 'error'
export type JobState = 'taslak' | 'verildi' | 'sirada' | 'calisiyor' | 'cevap_bekliyor' | 'onay_bekliyor' | 'karar_bekliyor' | 'gecti' | 'kaldi' | 'iptal' | 'durduruldu' | 'baslatilamadi'
/** Kanıt hükmünün nedeni (sunucu: reasonCode). */
export type ReasonCode = 'no_checks' | 'no_tests' | 'definition_changed' | 'tests_changed' | 'count_dropped' | 'checks_changed' | 'interrupted' | 'not_tracked' | 'deps_changed' | 'no_base' | 'approved_failed' | 'agent_tests_fail' | 'verdict_unavailable' | 'no_change' | 'no_work' | 'ignored_only'
/** Ölçüt türleri kayıt defterinden gelir (açık küme): bilinen dördü adlıdır, yenisi gelince arayüz kodu gerekmez. */
export type CheckKind = 'command' | 'file' | 'url' | 'screenshot' | (string & {})
export type ResultStatus = 'passed' | 'failed' | 'skipped' | 'error'
export type GuardKind = 'scope_only' | 'path_protect' | 'command_block' | 'ask_before'
export type GuardAction = 'deny' | 'ask'

export interface Check {
  id: number
  projectId: number
  jobId: number | null
  kind: CheckKind
  label: string
  spec: Record<string, unknown>
  timeoutS: number
  enabled: boolean
  /** auto: Söz önerdi; user: sen ekledin; agent-test: ajanın yazdığı deneme (sen kabul edene kadar kapalı). */
  origin: 'auto' | 'user' | 'agent-test' | string
  createdAt: string
}

export interface CheckInput {
  projectId: number
  jobId?: number | null
  kind: CheckKind
  label: string
  spec: Record<string, unknown>
  timeoutS?: number
  enabled?: boolean
}

export interface ProofResult {
  id: number
  runId: number
  checkId: number
  label: string
  status: ResultStatus
  exitCode: number | null
  outputTail: string | null
  artifactPath?: string | null
  artifactUrl?: string | null
  artifactKind?: 'image' | 'text' | 'file' | null
  kind?: CheckKind
  durationMs: number
}

export interface ProofRun {
  id: number
  projectId: number
  jobId: number | null
  sessionId: number | null
  trigger: 'stop_hook' | 'manual'
  attempt: number
  startedAt: string
  finishedAt: string | null
  verdict: Verdict | null
  summary: string | null
  /** Hükmün nedeni (no_change gibi). */
  reasonCode?: string | null
  projectName?: string
  jobTitle?: string
  counts?: { passed: number; failed: number; skipped: number; error: number }
  claim?: { claimsDone: boolean; showsEvidence: boolean; label: string } | null
  results?: ProofResult[]
}

export interface Job {
  id: number
  projectId: number
  projectName?: string
  sessionId: number | null
  title: string
  rawText: string
  promptText: string
  promptHash?: string
  source: 'dikte' | 'yazi'
  state: JobState
  night: boolean
  restate: boolean
  createdAt: string
  givenAt: string | null
  finishedAt: string | null
  /** Bu işi hangi ajan aracı yürütüyor. Eski işlerde yok: claude. */
  agent?: AgentId
  /** Koşuların toplam harcaması (Claude dolar, Codex token). */
  spend?: JobSpend | null
  lastVerdict?: Verdict | null
  attempts?: number
  maxAttempts?: number
  failedLabels?: string[]
  lastRunId?: number | null
  /** Söz'ün yürüttüğü işte süreç durumu (API-BEKLENTI-3). Yoksa iş terminalden yürüyor demektir. */
  run?: JobRun | null
  /** İş senden bir şey bekliyor: Bekçi onayı ya da ajanın sorusu. */
  awaiting?: 'approval' | 'reply' | 'decision' | null
  /** Hakem sınıfı: soru, bitti, kanit, takildi. */
  agentState?: string | null
  /** Kullanıcı 'Değişikliği kabul et' dedi: etiket 'Sen onayladın'. */
  acceptedByUser?: boolean
  /** Kabulün hangi yoldan geldiği: 'arayüz' ya da 'komut satırı'. */
  acceptedVia?: string | null
  /** Kabul kaydı bulunamadı (veritabanı elle değişmiş olabilir): iş 'kanıtsız kabul' diye gösterilir. */
  acceptedUnverified?: boolean
  /** Kabulü yapan sürecin adı (komut satırı ya da başka süreç): panelden gelmediğinde küçük yazıyla gösterilir. */
  acceptedProcess?: string | null
  /** Başlatılamayan işin sade nedeni. */
  startError?: string | null
  /** Ajanın sorusu (cevap_bekliyor). */
  question?: string | null
  /** Bekleyen Bekçi onayları, en eskiden başlayarak. */
  pendingApprovals?: PendingApproval[] | null
  /** 'Bu klasörde test tanımı son onaydan beri değişmiş' gibi baştan uyarı. */
  baseNote?: string | null
  /** İş "önce küçük bir deneme yaz" diye başlatıldı. */
  writeCheckFirst?: boolean
  /** Ajanın yazdığı, sen kabul edene kadar kapalı duran deneme önerileri. */
  suggestedChecks?: SuggestedCheck[] | null
  /** Ajanın eklediği ya da değiştirdiği testler için bekleyen iki ayrı teklif (yoksa null). */
  testOffer?: TestOffer | null
  /** Son sonucun neden kodu (no_change, ignored_only, approved_failed gibi), yoksa null. */
  lastReason?: string | null
}

export interface SuggestedCheck {
  checkId: number
  label: string
  cmd: string
  level: string
  levelLabel: string
  sentence: string
  /** strong: iş öncesi kalıyordu, şimdi geçiyor. weak: iş öncesi de geçiyordu. always: her sonuçta başarılı bitiyor (kabul edilemez). unknown: ölçülemedi. */
  strength?: 'strong' | 'weak' | 'unknown' | 'always' | string | null
  /** Sunucunun kişiye cümlesi (güçlü, zayıf ya da kabul edilemez için). */
  strengthNote?: string | null
  /** Varsayılan cevap: "evet" ya da "hayir". */
  defaultAnswer?: 'evet' | 'hayir' | string | null
  /** Her zaman başarılı komut: kabul düğmesi yok. */
  rejected?: boolean
}

/** Ajanın değiştirdiği onaylı dosyanın fark özeti. Satırlar yalnız GET /api/jobs/{id}/test-offer cevabında dolu gelir. */
export interface TestOfferFile {
  path: string
  sentence: string
  lines?: { op: string; text: string }[] | null
}

/** Ajanın testleri için iki ayrı teklif (JobDto.testOffer).
 *  Eklenenler ("katayım mı?") tek tıkla katılır. Değiştirilen onaylı testler varsayılan Hayır: farkı görmeden kabul edilemez. */
export interface TestOffer {
  added: number
  edited: number
  /** İkisinin birleşimi (eski alan). */
  sentence: string
  addedSentence?: string | null
  editedSentence?: string | null
  /** Fark özetinin kimliği: değişikliği kabul ederken gönderilir. */
  editedId?: string | null
  editedFiles?: TestOfferFile[] | null
}

export interface PendingApproval {
  eventId: number
  summary: string
  detail?: string | null
  at?: string | null
}

export type RunStatus = 'running' | 'waiting_approval' | 'stopped' | 'done' | 'failed'

export interface JobRun {
  status: RunStatus
  /** Ajanın son sade ilerleme satırı. */
  lastLine?: string | null
  startedAt?: string | null
  runId?: number | null
  failReason?: string | null
  /** Bekleyen Bekçi onayı (sunucu: run.pendingApproval, eski biçim). */
  pendingApproval?: { eventId: number; summary: string; tool?: string | null } | null
}

export type TimelineKind = 'user' | 'agent' | 'progress' | 'guard' | 'proof' | 'approval' | 'system'

export interface TimelineProofResult {
  label: string
  status: ResultStatus
  durationMs?: number
  outputTail?: string | null
  artifactUrl?: string | null
  /** Sunucunun işaretlediği tür: image (ekran görüntüsü), text (komut çıktısı), file. Yoksa adrese bakılır. */
  artifactKind?: 'image' | 'text' | 'file' | null
  /** Sunucunun okuduğu test sayıları: çıktı metninden okunamasa da sayı biliniyorsa "okunamadı" yazılmaz. */
  testsTotal?: number | null
  testsPassed?: number | null
  testsSkipped?: number | null
}

export interface TimelineProof {
  verdict: Verdict
  attempt: number
  summary?: string | null
  results: TimelineProofResult[]
  reasonCode?: ReasonCode | null
  reasonText?: string | null
  verdictLabel?: string | null
  changedFiles?: string[]
  addedTests?: string[]
  countUnknown?: boolean
  /** Başlamadan önceki durumla karşılaştırma: "Başlamadan önce 2 testten 1'i kalıyordu. Şimdi 2'si de geçiyor." */
  baselineText?: string | null
  /** Kartın dayandığı klasör görüntüsünün kimliği: kabul bunu taşır (denetim 4, E). */
  snapshotId?: string | null
  /** Değişen dosyaların kısa önce/sonra farkı (dosya başına en çok 12 satır). */
  diffs?: FileDiff[]
  /** Bilgi notları: kilitli bağımlılık eklendi gibi, kanıtı bozmayan değişiklikler. */
  notes?: string[]
}

export interface FileDiff {
  path: string
  kind: string
  sentence: string
  lines: { op: string; text: string }[]
}

/** İş akışı satırı (GET /api/jobs/{id}/timeline ve SSE job.progress). */
export interface TimelineItem {
  id: number | string
  ts: string
  kind: TimelineKind
  text: string
  /** "ayrıntı" ile açılan ham bilgi (komut, yol); varsayılan gizli. */
  detail?: string | null
  /** progress: active | done. approval: pending | allowed | denied. */
  state?: 'active' | 'done' | 'pending' | 'allowed' | 'denied' | 'error' | null
  /** approval satırında /approve çağrısına verilen kimlik. */
  eventId?: number | null
  proof?: TimelineProof | null
}

export interface JobDetail extends Job {
  runs?: ProofRun[]
  checks?: Check[]
}

export interface JobInput {
  projectId: number
  rawText: string
  source: 'dikte' | 'yazi'
  restate: boolean
  night: boolean
  checkIds: number[]
  /** Boşsa sunucu varsayılan ajanı kullanır. */
  agent?: AgentId
  /** Testsiz klasörde: ajan işe başlamadan önce küçük bir deneme yazsın. */
  writeCheckFirst?: boolean
}

export interface GuardRule {
  id: number
  projectId: number | null
  kind: GuardKind
  label: string
  spec: Record<string, unknown>
  action: GuardAction
  enabled: boolean
  decisionId: number | null
  origin: 'template' | 'user' | 'defter'
  createdAt: string
  projectName?: string | null
  /** Sunucunun ürettiği düz cümle (API-FARKLARI-2B). */
  sentence?: string
}

export interface GuardEvent {
  id: number
  at: string
  projectId: number | null
  projectName?: string | null
  toolSessionId?: string | null
  tool: string
  targetSummary: string
  ruleId: number | null
  ruleLabel?: string | null
  action: GuardAction
  reason: string
}

export interface GuardTestResult {
  verdict: 'allow' | 'deny' | 'ask'
  ruleId: number | null
  ruleLabel: string | null
  ruleKind?: GuardKind | null
  reason: string | null
  unknown: boolean
  targets: string[]
  note: string
}

/** Sunucunun /api/guard/events cevabı (API-FARKLARI-2B); arayüz items'ı GuardEvent'e çevirir. */
export interface GuardEventsRaw {
  days: number
  total: number
  deny: number
  ask: number
  items: { id: number; at: string; projectId: number | null; projectName?: string | null; sessionId?: string | null; tool: string; target: string; ruleId: number | null; ruleLabel?: string | null; action: GuardAction; reason: string }[]
}

export interface GuardApplyRaw {
  template: string
  created: GuardRule[]
  existing: GuardRule[]
}

export interface GuardTemplateRule {
  kind: GuardKind
  label: string
  action: GuardAction
}

export interface GuardTemplate {
  id: string
  label: string
  description: string
  rules: GuardTemplateRule[]
}

export interface QueueItem {
  id: number
  projectId: number | null
  projectName?: string | null
  sessionId: number | null
  text: string
  createdAt: string
  deliveredAt: string | null
  origin: 'panel' | 'telefon'
}

export interface NeedsYou {
  sessionId: number
  tool: string
  projectId: number | null
  projectName: string | null
  title: string | null
  state: 'waiting' | 'stalled'
  since: string
  reason: string | null
  question: string | null
  pendingQueue: number
}

// GET /api/now (API-FARKLARI-2): tek çağrı; ayrıntı arayüzde birleştirilir.
export interface Now {
  sentence?: string
  needsYou: NeedsYou[]
  runningJobs: Job[]
  recentVerdicts: ProofRun[]
  counts: { jobsPassed: number; jobsFailed: number; jobsRunning: number; waiting: number; working: number; stalled: number; queuePending: number; guardBlocked: number; queued?: number }
  /** Seni bekleyen işler (sunucu ek alanı). Liste ekranı bunu işlerin kendi durumundan türetir; alan yalnız okunur. */
  waitingJobs?: Job[]
}

export interface MorningEntry {
  job: { id: number; title: string; projectName: string | null; state?: string | null; night?: boolean; finishedAt?: string | null; question?: string | null }
  runId?: number
  attempts?: number
  summary?: string
  failedChecks?: string[]
  verdict?: Verdict
  /** Son sonucun neden kodu (no_change, ignored_only, approved_failed ...). */
  reasonCode?: string | null
}

export interface Morning {
  since: string
  until?: string
  sentence?: string
  passed: MorningEntry[]
  failed: MorningEntry[]
  unverified: MorningEntry[]
  waitingForYou: { sessionId: number; projectName: string | null; title: string | null; since: string; reason: string | null }[]
  stuck: { sessionId: number; projectName: string | null; title: string | null; since: string }[]
  guardBlocked: number
  usage: { messages: number; inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheWriteTokens: number }
  /** Pencere saat sayısı (varsayılan 16). */
  hours?: number
  /** Seni bekleyen, kalan işler (cevap, karar, onay bekleyen ve görülmemiş kaldı). */
  waitingJobs?: MorningJob[]
  /** Bekçinin son durdurduğu ya da sana sorduğu işlemler (sır maskeli). */
  guardStops?: MorningGuardStop[]
  spend?: MorningSpend
  night?: MorningNight
  /** Pencerede ilgili toplam iş; 0 ise ekran boş durum çizer. */
  jobsInWindow?: number
}

export interface MorningJob {
  id: number
  title: string
  projectName: string | null
  projectId?: number
  state?: string | null
  night?: boolean
  finishedAt?: string | null
  question?: string | null
}
export interface MorningGuardStop { at: string; projectName: string | null; tool: string; summary: string; action: string; reason: string | null }
export interface MorningSpend { runs: number; inputTokens: number; cachedTokens: number; outputTokens: number; reasoningTokens: number; costUsd: number | null; note: string }
export interface MorningNight { passed: number; failed: number; waiting: number; open: number; jobs: MorningJob[]; note: string }

export interface UsageDay {
  day: string
  mainTokens: number
  subTokens: number
  estimatedUsd: number | null
}

export interface UsageModel {
  model: string
  tokens: number
  messages: number
  estimatedUsd: number | null
}

/** Sunucunun /api/usage cevabı (API-FARKLARI-2B), arayüzün kullandığı alanlarla. */
export interface TokenSet {
  messages: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  totalTokens: number
  freshTokens: number
}
export interface UsageRaw {
  days: number
  since: string
  until: string
  tool: string | null
  dayBasis: string
  totals: TokenSet
  subagent: { tokens: TokenSet; sharePercent: number; sharePercentOfAllTokens: number; sharePercentOfMessages: number; basis: string }
  estimatedCostUsd: number | null
  pricing: { configured: boolean; note: string; unpricedModels: string[]; disclaimer: string; howTo?: string }
  byDay: { day: string; tokens: TokenSet; subagentTokens: TokenSet; costUsd: number | null }[]
  byModel: { tool: string; model: string; tokens: TokenSet; sharePercent: number; subagentSharePercent: number; costUsd: number | null; priced: boolean }[]
}

export interface Usage {
  days: number
  priced: boolean
  /** Fiyat notu (örn. "Fiyatı girilmeyen modeller toplama katılmadı: ..."); yoksa null. */
  note: string | null
  /** Sayılar "yeni işlenen" token: giriş + çıkış + önbellek yazma; önbellekten okunan hariç. */
  totalTokens: number
  messages: number
  subagentSharePct: number
  estimatedUsd: number | null
  byDay: UsageDay[]
  byModel: UsageModel[]
}

export interface PhonePairing {
  enabled: boolean
  /** Yerel ağda gerçekten dinliyor mu; enabled=true iken false ise error nedeni söyler. */
  listening?: boolean
  url: string | null
  addresses?: string[]
  error?: string | null
  code?: string | null
}

// ---- Düz cümleyle ölçüt (GET /api/projects/{id}/checks/templates) ----
export interface CheckTemplateField {
  name: string
  label: string
  placeholder: string
  default: string
}

export interface CheckTemplate {
  id: string
  sentence: string
  kind: CheckKind
  fields: CheckTemplateField[]
  available: boolean
  reason: string | null
  detectedCmd: string | null
  /** Zayıf kanıt: ajan bu ölçütü kendi yazdığı dosyayla da doldurabilir. */
  weak?: boolean
  note?: string | null
}

export interface CheckTemplates {
  templates: CheckTemplate[]
  hasTests: boolean
  /** Test yoksa öneri metni; varsa null. */
  hint: string | null
}

export interface CheckTemplateInput {
  projectId: number
  jobId?: number | null
  template: string
  values: Record<string, string>
  enabled?: boolean
}

// ---- Kanca sağlığı (GET /api/hooks/health) ----
export interface HookFile {
  scope: string
  projectId: number | null
  projectName: string | null
  path: string
  exists: boolean
  readError: string | null
  hooks: string[]
  exePath: string | null
  exeExists: boolean | null
  dataDir: string | null
  dataMatches: boolean | null
}

export interface HookStat {
  name: string
  /** Sade Türkçe ad (ör. "kanıt denetimi"). Eski sunucuda yoksa ad gösterilir. */
  label?: string
  runs24h: number
  errors24h: number
  timeouts24h: number
  lastRunAt: string | null
  lastResult: string | null
  lastErrorAt: string | null
  lastError: string | null
  maxMs: number
}

export interface HooksHealth {
  checkedAt: string
  ok: boolean
  gateProblem: string | null
  warnings: string[]
  files: HookFile[]
  stats: HookStat[]
}

/** Sunucunun zaman çizelgesi satırı (GET /api/jobs/{id}/timeline?detail=1). id kalıcıdır ve SSE ile aynıdır: u, l{lineId}, a{eventId}, p{proofRunId}.
 *  Biten kanıt satırında attempt ve results gömülüdür (detail alanı kanıt özetidir). */
export interface WireTimelineItem {
  id?: string | null
  lineId?: number | null
  attempt?: number | null
  results?: WireProofResult[] | null
  at: string
  kind: string
  text: string
  detail?: string | null
  runId?: number | null
  eventId?: number | null
  status?: string | null
  verdict?: string | null
  proofRunId?: number | null
  reasonCode?: string | null
  reasonText?: string | null
  verdictLabel?: string | null
  changedFiles?: string[] | null
  addedTests?: string[] | null
  countUnknown?: boolean | null
  baselineText?: string | null
  snapshotId?: string | null
  diffs?: FileDiff[] | null
  notes?: string[] | null
}

/** Proje ekleme cevabı (POST /api/projects). */
export interface ProjectAddResult {
  id: number
  name: string
  path: string
  created: boolean
  gateEnabled: boolean
  suggested: Check[]
  card: string
}

export interface WireProofResult {
  label: string
  status: ResultStatus
  durationMs?: number
  outputTail?: string | null
  artifactUrl?: string | null
  artifactKind?: 'image' | 'text' | 'file' | null
  testsTotal?: number | null
  testsPassed?: number | null
  testsSkipped?: number | null
}

/** GET /api/fs/browse: klasör gezme (yalnız klasör adları). path yoksa sürücü listesi. */
export interface FolderEntry {
  name: string
  path: string
}
export interface FolderListing {
  path: string | null
  parent: string | null
  /** Bu klasör proje olarak eklenebilir mi (sürücünün kendisi, Windows klasörleri vb. eklenemez). */
  selectable: boolean
  reason: string | null
  truncated: boolean
  folders: FolderEntry[]
}
