// İş yüzeyinin durumu: iş listesi, seçili iş, akış satırları, klasör, taslak. Tek yerde; bileşenler yalnız çizer.
import { getAgents, getProjects } from './api'
import { acceptDone, getAgentsReady, getDoneSummary, getRegistry } from './api5'
import { ApiError } from './errors'
import { isMi } from './isTani'
import { ilkIsBoottaMi } from './isSecim'
import { al } from './bekleyenBaslat'
import { acceptJob, addProject, approveJob, approveNewTests, awaitingOf, continueJob, declineNewTests, declineSuggestedCheck, errText3, getTestOffer, getTimeline, isMissing, normJob, progressFromEvent, replyJob, startJob, stateFromEvent, stopJob } from './api3'
import { cancelJob, createJob, getChecks, getJob, getJobsPage, getMorning, getNow, getProofRuns, patchCheck } from './api2'
import type { IconName } from './components/Icon.svelte'
import { t, type Key } from './i18n/index.svelte'
import { loadAttention } from './attention.svelte'
import { onLive } from './live'
import { sozKapandiMi, yaridaMetni } from './yarida'
import { go, router } from './router.svelte'
import { app, toast } from './store.svelte'
import { toolFullLabel } from './profil'
import type { AgentId, AgentInfo, Check, Job, PendingApproval, Project, ProjectAddResult, SuggestedCheck, TestOffer, TestOfferFile, TimelineItem } from './types'
import type { AgentReady, AgentsReady, DoneAcceptItem, DoneScope, DoneSummary, RegistryEntry } from './types5'
import { isGeneral, isTestProject, parseTs } from './util'
import { kurulumGerekir, seciliKlasor, zatenEkliBildirimi } from './klasorSecim'
import { dar, gecmisKarari, kisaAd } from './gezinme'

export type Bucket = 'me' | 'blocked' | 'running' | 'queued' | 'passed' | 'nochange' | 'accepted' | 'failed' | 'nowork' | 'stopped' | 'unproven'
export const buckets: Bucket[] = ['me', 'blocked', 'running', 'queued', 'passed', 'nochange', 'accepted', 'failed', 'nowork', 'stopped', 'unproven']

/** Grup başlığı, çip rengi ve simge: liste, akış başlığı ve palet aynı tablodan okur. */
export const bucketMeta: Record<Bucket, { key: Key; cls: string; icon: IconName }> = {
  me: { key: 'grpMe', cls: 'chip-waiting', icon: 'waiting' },
  blocked: { key: 'grpBlocked', cls: 'chip-neutral', icon: 'info' },
  running: { key: 'grpRunning', cls: 'chip-working', icon: 'working' },
  queued: { key: 'grpQueued', cls: 'chip-neutral', icon: 'clock' },
  passed: { key: 'grpPassed', cls: 'chip-ok', icon: 'check' },
  nochange: { key: 'grpNoChange', cls: 'chip-neutral', icon: 'idle' },
  accepted: { key: 'grpAccepted', cls: 'chip-accepted', icon: 'person' },
  failed: { key: 'grpFailed', cls: 'chip-stalled', icon: 'x' },
  // İş yapılmamış görünüyor: yeşil değil, gri de değil; uyarı rengi (sunucu hükmü unchecked, nedeni no_work).
  nowork: { key: 'grpNoWork', cls: 'chip-waiting', icon: 'alert' },
  stopped: { key: 'grpStopped', cls: 'chip-neutral', icon: 'idle' },
  unproven: { key: 'grpUnproven', cls: 'chip-unproven', icon: 'unchecked' },
}

function ls(k: string): string {
  try {
    return localStorage.getItem(k) ?? ''
  } catch {
    return ''
  }
}
function lsSet(k: string, v: string) {
  try {
    localStorage.setItem(k, v)
  } catch {
    // depolama kapalı olabilir
  }
}

const DRAFT_KEY = 'soz.draft'

export const sf = $state({
  jobs: [] as Job[],
  /** İş listesi: kaç iş istendi (50'şer artar), sunucuda toplam kaç iş var, devamı var mı. */
  jobsLimit: 50,
  jobsTotal: 0,
  jobsMore: false,
  loaded: false,
  loadError: '',
  /** Son iş listesi okuması başarısız oldu (sunucu hatası, boş ya da bozuk cevap). */
  jobsFailed: false,
  projects: [] as Project[],
  projLoaded: false,
  projectId: (ls('soz.lastProject') ? Number(ls('soz.lastProject')) : null) as number | null,
  selectedPath: (ls('soz.lastProjectPath') || null) as string | null,
  selId: null as number | null,
  /** dar panelde hangisi görünür */
  view: 'list' as 'list' | 'detail',
  timelines: {} as Record<number, TimelineItem[]>,
  tlLoading: {} as Record<number, boolean>,
  /** Başlatma ucu yoksa (bu sürümde yok) iş taslak kalır; istem kopyalanır. */
  startMissing: {} as Record<number, boolean>,
  checks: {} as Record<number, Check[]>,
  setup: null as ProjectAddResult | null,
  /** Kullanıcı işi yazıp klasör yokken Ctrl+Enter bastı: klasör onaylanınca iş kendiliğinden başlar. */
  pendingStart: false,
  /** Bekleyen başlatmanın bağlı olduğu taslak metni (bekleyenBaslat.ts): başka taslak asla başlamaz. */
  pendingRaw: '',
  /** Adresten ya da kısayoldan istenen iş listede yoksa sunucudan getirilir; getirilirken ve bulunamayınca burada durur (isSecim.ts). */
  /** Hangi işte "Durdur" onayı soruluyor (kısayol ve düğme önce sorar). */
  stopAsk: null as number | null,
  /** "Reddet" sonrası devam kutusu açık: kutunun üstünde "Neyi beğenmedin?" notu çıkar (iş numarasına göre). */
  rejectBack: {} as Record<number, boolean>,
  /** Süren tek-istek işlemleri (anahtar: eylem + iş): çift tıkta ikinci istek gitmez, düğme kilitli görünür. */
  busy: {} as Record<string, boolean>,
  jobPulling: null as number | null,
  jobMissing: null as number | null,
  draft: ls(DRAFT_KEY),
  /** "Gece işi": başında olmayacağım. Yalnız bir sonraki işe uygulanır. */
  night: false,
  /** "Bitti sayılması için" kartı açık: işin metni burada bekler, kişi ölçütleri kabul edince iş başlar. */
  doneRaw: null as string | null,
  /** Üst kutuya iş olmayan bir şey yazıldı (selam, soru): iş açılmaz, kutunun altında sade cevap çıkar. */
  notJob: null as { text: string; neden: string } | null,
  /** Üst sayaçlar: yalnız sunucunun /api/now cevabı. Bilinmiyorsa null (arayüz saymaz, göstermez). */
  counts: null as { waiting: number | null; running: number | null; queued: number | null } | null,
  /** Onay kuyruğunda bu turda cevaplanan sayısı ('2 onaydan 1' sayacı için). */
  answered: {} as Record<number, number>,
  /** 'Bu işe devam et' kutusu açık işler. */
  continuing: {} as Record<number, boolean>,
  /** Devam kutusuna konacak taslak (iş kimliğine göre; kutu bir kez alır). */
  prefill: {} as Record<number, string>,
  addFolderOpen: false,
  /** Kullanıcı bu oturumda proje seçti: sınama klasörü bile olsa seçimine dokunulmaz. */
  picked: false,
  paletteOpen: false,
  folderMenuOpen: false,
  /** Bulunan ajan araçları (GET /api/agents). Boşsa ya da yüklenmediyse arayüz ajan seçimi göstermez. */
  agents: [] as AgentInfo[],
  /** Sunucunun varsayılan ajanı. */
  defaultAgent: 'claude' as AgentId,
  /** Yeni iş kutusunda seçilen ajan (null: varsayılan). */
  agent: null as AgentId | null,
  agentMenuOpen: false,
  /** İş vermeden önce özet (GET /api/done/summary), klasör kimliğine göre. */
  summary: {} as Record<number, DoneSummary>,
  /** Ajan hazır mı (GET /api/agents/ready). Yüklenmediyse null: arayüz bir şey söylemez. */
  ready: null as AgentsReady | null,
  /** Bağlayıcı kayıtları (araç listesi sunucudan gelir). Boşsa /api/agents listesi kullanılır. */
  connectors: [] as RegistryEntry[],
  /** Kişi tek satırlık özetteki "Değiştir"e bastı: tam kart açılır. */
  forceFull: false,
  /** Sabah cevabındaki gece işi cümlesi (iş kartında bir kez gösterilir). */
  nightNote: '',

  /** Değiştirilen onaylı testlerin görülen fark özeti (iş kimliğine göre). id: fark özetinin kimliği; kabul bununla yapılır, kimlik değişirse yeniden görmek gerekir. */
  offerDiff: {} as Record<number, { id: string; files: TestOfferFile[] }>,
  /** Sonradan değiştiği söylenen eski fark, aynı kimlikle yeniden gösterilmez. */
  offerStale: {} as Record<number, string>,
  /** Test teklifinde sunucunun söylediği hata cümlesi (iş kimliğine göre), örneğin farkı görmeden kabul denemesi. */
  offerError: {} as Record<number, string>,
  /** Son kanıt kaydının nedeni (sunucu işin satırında vermez, kanıt kaydından okunur). run: o kaydın kimliği (iş yeniden çalışınca eski cevap geçersiz olur). */
  reasons: {} as Record<number, { run: number | null; code: string | null }>,
  focusReply: 0,
  focusNew: 0,
  focusList: 0,
  focusPath: 0,
  focusSetup: 0,
  checksOpen: false,
  lastMsg: '',
  folderError: '',
  /** Bu oturumda en son verilen iş (O5): Şu An'da iş kutusunun altında "Az önce verdiğin" satırı. */
  sonVerilen: null as { id: number; at: number } | null,
  /** İş listesinin son başarılı okunduğu an (O11: bağlantı şeridi "son bilgi N sn önce" der). */
  jobsOkAt: 0,
})

/** İşi aç: İşler sayfasında, ayrıntısıyla (O5 "İzle" bildirimi ve "Az önce verdiğin" satırı). */
export function isiIzle(id: number) {
  select(id, { detail: true })
  go('is', String(id))
}

const clip = (s: string) => (s.length > 70 ? s.slice(0, 67) + '…' : s)

// ---- seçiciler ----
export const realProjects = () => sf.projects.filter((p) => !isGeneral(p))
/** İş verme kutusunun varsayılan projesi: sınama ve geçici klasörler hariç, en son çalışılan gerçek proje. Yalnız sınama klasörü varsa boş döner. */
export function defaultProject(): Project | null {
  const good = realProjects().filter((p) => !isTestProject(p))
  const ms = (p: Project) => (Number.isFinite(parseTs(p.lastAt)) ? parseTs(p.lastAt) : 0)
  return [...good].sort((a, b) => ms(b) - ms(a))[0] ?? null
}
export const currentProject = () => seciliKlasor(realProjects(), sf.projectId, sf.picked, defaultProject())

export function bucketOf(j: Job): Bucket {
  const st = j.state
  if (st === 'baslatilamadi') return 'blocked'
  if (st === 'sirada') return 'queued'
  if (st === 'cevap_bekliyor' || st === 'onay_bekliyor' || st === 'karar_bekliyor') return 'me'
  if (j.awaiting === 'approval' || j.awaiting === 'reply' || j.awaiting === 'decision' || j.run?.status === 'waiting_approval') return 'me'
  if (st === 'taslak') return 'me'
  if (st === 'iptal' || st === 'durduruldu') return 'stopped' // durdurma "kaldı" değildir: iş başarısız olmadı, sen durdurdun
  if (st === 'gecti') return j.acceptedByUser ? 'accepted' : isNoChange(j) ? 'nochange' : 'passed'
  if (st === 'kaldi') return 'failed'
  const rs = j.run?.status
  if (rs === 'running') return 'running'
  if (rs === 'stopped') return 'stopped'
  const v = j.lastVerdict
  if (v === 'unchecked' && reasonOf(j) === 'no_work') return 'nowork'
  // süreç bitince sunucu hükmü kendisi verir (kapı geçti/kaldı, ölçüt yoksa unchecked); bu dal 'verildi' kalan, kanıtı belirsiz işleri kanıtsız gösterir
  if (rs === 'failed') return 'failed'
  if (rs === 'done') return v === 'passed' ? 'passed' : v === 'failed' || v === 'error' ? 'failed' : 'unproven'
  if (st === 'calisiyor') return 'running'
  if (v === 'passed') return 'passed'
  if (v === 'failed' || v === 'error') return 'failed'
  if (v === 'unchecked') return 'unproven'
  return 'running' // verildi: ajan terminalde çalışıyor, sonuç bekleniyor
}

/** İşin son kanıt kaydının nedeni (bilinmiyorsa null). */
export const reasonOf = (j: { id: number; lastRunId?: number | null; lastReason?: string | null }): string | null => {
  if (j.lastReason) return j.lastReason // sunucu işin satırında verir, ayrıca sorgu gerekmez
  const c = sf.reasons[j.id]
  return c && c.run === (j.lastRunId ?? null) ? c.code : null
}
/** Ajan hiçbir dosyayı değiştirmeden ya da yalnız izlenmeyen klasörlere yazarak biten iş (yeşil "Kanıtıyla bitti" değil, sakin "Değişiklik yok"). */
export const isNoChange = (j: { id: number; lastRunId?: number | null; lastReason?: string | null }): boolean => {
  const r = reasonOf(j)
  return r === 'no_change' || r === 'ignored_only'
}
const probing = new Set<number>()
type Probe = { id: number; state?: string; lastRunId?: number | null; lastReason?: string | null; acceptedByUser?: boolean }
/** Biten ve karar bekleyen işlerin son kanıt kaydına bakar (en çok 40 iş, birer birer, cevap önbelleğe girer). Liste, Sabah ve Sıra bende aynı cevabı kullanır. */
export async function resolveReasons(list: Probe[] = sf.jobs) {
  const todo = list.filter((j) => j.id > 0 && (j.state === 'gecti' || j.state === 'karar_bekliyor') && !j.acceptedByUser && !j.lastReason && sf.reasons[j.id]?.run !== (j.lastRunId ?? null) && !probing.has(j.id)).slice(0, 40)
  for (const j of todo) {
    probing.add(j.id)
    try {
      const runs = await getProofRuns({ jobId: j.id, limit: 1 })
      const last = Array.isArray(runs) ? runs[0] : undefined
      sf.reasons[j.id] = { run: j.lastRunId ?? null, code: last?.reasonCode ?? null }
    } catch {
      // okunamazsa satır eski etiketiyle kalır
    } finally {
      probing.delete(j.id)
    }
  }
}

export const isStopped = (j: Job): boolean => j.state === 'iptal' || j.state === 'durduruldu' || j.run?.status === 'stopped'
/** Süreç bitmiş, aynı oturumdan devam edilebilen iş. */
export const isFinished = (j: Job): boolean => ['gecti', 'kaldi', 'iptal', 'durduruldu'].includes(j.state)
/** Akışta ve satırda görünen çip metni (durdurulan iş "Kaldı" değil "Durduruldu" der). */
/** Başlamış, çalışırken hata verip duran iş: "Başlatılamadı" değil "Yarıda kaldı" (sunucu cümlesi "Yardımcı hata ile bitti" der). */
export const midRunFailed = (j: Pick<Job, 'startError'> & Partial<Job>, hasAgentLine = false): boolean => bucketOf(j as Job) === 'blocked' && (hasAgentLine || yaridaMetni(j.startError))
export const chipLabelKey = (j: Job): Key => (isStopped(j) ? 'rowStopped' : midRunFailed(j) ? 'rowHalf' : bucketMeta[bucketOf(j)].key)

/** Bekleyen onaylar, en eskiden başlayarak. Önce işin kendi listesi, yoksa akıştaki bekleyen satırlar, yoksa eski tek onay biçimi. */
export function pendingList(j: Job): PendingApproval[] {
  const fromJob = (j.pendingApprovals ?? []).slice().sort((a, b) => a.eventId - b.eventId)
  if (fromJob.length) return fromJob
  const fromTl = (sf.timelines[j.id] ?? []).filter((x) => x.kind === 'approval' && x.state === 'pending' && x.eventId !== null && x.eventId !== undefined)
  if (fromTl.length) return fromTl.map((x) => ({ eventId: x.eventId as number, summary: x.text, detail: x.detail ?? null, at: x.ts })).sort((a, b) => a.eventId - b.eventId)
  const one = j.run?.pendingApproval
  return one ? [{ eventId: one.eventId, summary: one.summary, detail: null }] : []
}

export function jobsIn(b: Bucket): Job[] {
  return sf.jobs.filter((j) => bucketOf(j) === b).sort((a, c) => parseTs(c.createdAt) - parseTs(a.createdAt))
}

/** j/k sırası: ekrandaki sıra. */
export const flatJobs = (): Job[] => buckets.flatMap((b) => jobsIn(b))
export const selectedJob = (): Job | null => sf.jobs.find((j) => j.id === sf.selId) ?? null

/** Liste satırının ikinci satırı: ajanın son sade sözü ya da sonuç özeti. */
export function rowLine(j: Job): string {
  const b = bucketOf(j)
  if (b === 'blocked') return midRunFailed(j) ? t('rowHalfLine') : j.startError ? t('rowBlockedWhy', { m: j.startError }) : t('rowBlocked')
  if (b === 'queued') return t('rowQueued')
  if (j.awaiting === 'decision') return t('rowDecision')
  if (j.awaiting === 'approval' || j.run?.status === 'waiting_approval') {
    const n = pendingList(j).length
    return n > 1 ? t('rowApprovalN', { n }) : t('rowApproval')
  }
  if (j.awaiting === 'reply') return j.question ? clip(j.question) : t('rowReply')
  if (b === 'me' && j.state === 'taslak') return t('rowDraft')
  if (b === 'running') return friendlyLine(j.run?.lastLine || t('rowRunning'))
  if (b === 'accepted') return j.acceptedUnverified ? t('rowAcceptedUnverified') : t('rowAccepted')
  // Sunucu kapanırken iş gerçekten durur (RunManager.StopAllAsync, nedeni satıra yazılır): sebep söylenir, kişi "Durduruldu" görüp kendi durdurdu sanmaz.
  if (b === 'stopped') return sozKapandiMi(j.run?.lastLine) ? t('rowStoppedShutdown') : t('rowStopped')
  if (b === 'nowork') return t('rowNoWork')
  if (b === 'nochange') return reasonOf(j) === 'ignored_only' ? t('rowNoSourceChange') : t('rowNoChange')
  if (b === 'passed') return (j.attempts ?? 1) > 1 ? t('rowPassedN', { n: j.attempts ?? 1 }) : t('rowPassed')
  if (b === 'failed') {
    if (isStopped(j)) return sozKapandiMi(j.run?.lastLine) ? t('rowStoppedShutdown') : t('rowStopped')
    return j.failedLabels?.length ? t('rowFailedOn', { l: j.failedLabels.join(', ') }) : t('rowFailed')
  }
  return t('rowUnproven')
}

/** Sunucunun anlamsız bıraktığı kısa satırlar kullanıcıya ne olduğunu söyleyen cümleye çevrilir. */
const WEAK: Record<string, Key> = { 'bir komut hata verdi': 'lineCmdFailed', 'düşünüyor': 'lineThinking' }
export function friendlyLine(text: string): string {
  const k = WEAK[text.trim().toLocaleLowerCase('tr').replace(/[.!]+$/, '')]
  return k ? t(k) : text
}

// ---- ajan araçları ----
/** İş verme ekranındaki araç satırı: ad, açık mı, hazır mı ve dürüst sınırları. Liste sunucudan gelir (sabit liste yok). */
export interface AgentOption {
  id: string
  name: string
  /** Kapalı araç soluk görünür ve seçilemez; Tanışma sayfasında onaylanınca açılır. */
  open: boolean
  ready: AgentReady | null
  /** Seçilince tek satırla söylenen sınır: "Bu araçta Söz komutları tek tek denetleyemez." */
  limit: string | null
  /** "Durdur dersen {ad} işi arka planda sürdürebilir." (Durdur yalnız izlemeyi kesiyorsa) */
  stopNote: string | null
}
const connectorId = (e: RegistryEntry) => e.id.replace(/^connector:/, '')
function limitsOf(name: string, cap: { beforeTool?: string; stop?: boolean } | null | undefined): { limit: string | null; stopNote: string | null } {
  if (!cap) return { limit: null, stopNote: null }
  return {
    limit: cap.beforeTool && cap.beforeTool !== 'native-hook' && cap.beforeTool !== 'native_hook' ? t('agLimitNoCheck') : null,
    stopNote: cap.stop === false ? t('agLimitStop', { a: name.split(' ')[0] ?? name }) : null,
  }
}
export function agentOptions(): AgentOption[] {
  const readyBy = new Map((sf.ready?.agents ?? []).map((a) => [a.id, a]))
  const out: AgentOption[] = []
  const seen = new Set<string>()
  // Yalnız kurulu ve hazır olan yardımcılar (found: true) ana kutuda görünür; kurulu olmayan araçlar (OpenCode vb.) kutuyu kirletmez.
  for (const r of sf.ready?.agents ?? []) {
    if (!r.found) continue
    const info = sf.agents.find((a) => a.id === r.id)
    const conn = sf.connectors.find((e) => connectorId(e) === r.id)
    if (conn) {
      const depth = (conn.body as { depth?: string[] }).depth ?? []
      if (depth.length > 0 && !depth.includes('run')) continue
    }
    const open = conn ? conn.state === 'enabled' : r.canStart || r.ready
    if (!open) continue
    const name = info?.name ? toolFullLabel(info.id) : toolFullLabel(r.id)
    const cap = info
      ? { beforeTool: info.capabilities.guardTier === 'native_hook' ? 'native-hook' : info.capabilities.guardTier, stop: info.capabilities.stop }
      : ((conn?.body as { capabilities?: { beforeTool?: string; stop?: boolean } } | undefined)?.capabilities ?? null)
    out.push({ id: r.id, name, open: true, ready: r, ...limitsOf(name, cap) })
    seen.add(r.id)
  }
  for (const a of sf.agents) {
    if (seen.has(a.id) || !a.found) continue
    const conn = sf.connectors.find((e) => connectorId(e) === a.id)
    if (conn && conn.state !== 'enabled') continue
    const r = readyBy.get(a.id) ?? null
    if (r && !r.found) continue
    const name = a.name === 'Claude' ? 'Claude Code' : a.name || toolFullLabel(a.id)
    out.push({
      id: a.id,
      name,
      open: true,
      ready: r,
      ...limitsOf(name, { beforeTool: a.capabilities.guardTier === 'native_hook' ? 'native-hook' : a.capabilities.guardTier, stop: a.capabilities.stop }),
    })
    seen.add(a.id)
  }
  return out
}
export const chosenOption = (): AgentOption | null => agentOptions().find((x) => x.id === chosenAgent()) ?? null
/** Seçili araç hazır değilse Başlat soluk görünür (basılabilir kalır: uyarır, engellemez). */
export const startBlocked = (): boolean => {
  const r = chosenOption()?.ready
  return !!r && !r.canStart
}
/** Birden çok kurulu yardımcı varsa seçim görünür; yüklenirken de düzen kaymaması için çip yüksekliği sabittir. */
export const agentChoiceVisible = () => agentOptions().length >= 1
export const chosenAgent = (): AgentId => {
  const a = sf.agent
  return a && agentOptions().some((x) => x.id === a && x.open) ? a : sf.defaultAgent
}
export const agentName = (id: string | undefined | null): string => {
  if (!id || id === 'claude') return 'Claude Code'
  const fromAgent = sf.agents.find((a) => a.id === id)?.name
  if (fromAgent && fromAgent !== 'Claude') return fromAgent
  return toolFullLabel(id)
}
/** İş başlığındaki bekçi etiketi (yalnız yerli kancası olmayan araçta): "Bekçi: sınırlı (Codex)". */
export function guardTag(j: Job): string | null {
  const id = j.agent ?? 'claude'
  const info = sf.agents.find((a) => a.id === id)
  const label = info?.guardLabel ?? (id === 'codex' ? 'sınırlı' : 'kanca')
  if (label === 'kanca') return null
  return t('guardTagFor', { l: label === 'sınırlı' ? t('guardLimited') : label === 'sorulur' ? t('guardAsks') : t('guardNone'), a: agentName(id) })
}
export async function loadAgents() {
  try {
    const r = await getAgents()
    sf.agents = Array.isArray(r?.agents) ? r.agents : []
    sf.defaultAgent = r.defaultAgent
  } catch {
    // eski sunucu ya da bağlantı yok: ajan seçimi görünmez, işler eskisi gibi başlar
  }
  await Promise.all([loadReady(), loadConnectors()])
}
/** Ajan hazır mı: hiçbir istem göndermez. Uç yoksa (eski sunucu) satır çizilmez. */
/** Gece işi cümlesi: sunucunun sabah cevabındaki `night.note`. Bir kez okunur. */
export async function loadNightNote() {
  if (sf.nightNote || nightNoteLoading) return
  nightNoteLoading = true
  try {
    sf.nightNote = (await getMorning()).night?.note ?? ''
  } catch {
    // cümle gelmezse gösterilmez
  } finally {
    nightNoteLoading = false
  }
}
let nightNoteLoading = false
export async function loadReady() {
  try {
    sf.ready = await getAgentsReady()
  } catch {
    sf.ready = null
  }
}
export async function loadConnectors() {
  try {
    sf.connectors = (await getRegistry('connector')).entries
  } catch {
    sf.connectors = []
  }
}
/** İş vermeden önce özet. Uç yoksa kayıt yok sayılır: kart eskisi gibi açılır. */
export async function loadSummary(projectId: number): Promise<DoneSummary | null> {
  try {
    const r = await getDoneSummary(projectId)
    sf.summary[projectId] = r
    return r
  } catch {
    delete sf.summary[projectId]
    return null
  }
}

// ---- yükleme ----
export async function loadProjects() {
  try {
    const list = await getProjects()
    if (!Array.isArray(list)) throw new Error('boş cevap')
    sf.projects = list
    sf.projLoaded = true
    const real = realProjects()
    const cur = real.find((p) => p.id === sf.projectId)
    if (!cur || (isTestProject(cur) && !sf.picked)) {
      sf.projectId = defaultProject()?.id ?? null
      if (!sf.picked && sf.selectedPath && isTestProject({ path: sf.selectedPath })) sf.selectedPath = null
    }
  } catch {
    sf.projLoaded = true
  }
}

// Yerel değişiklik sayacı: liste çağrısı sürerken araya giren değişiklik (yeni iş, canlı olay) bayat cevapla ezilmesin.
let mutations = 0
// Bu oturumda açılan işler: sunucu listesi henüz görmemiş olsa bile (yarış) birkaç saniye listede kalır.
const mine = new Map<number, number>()

export async function loadJobs() {
  try {
    let fresh: Job[] = []
    for (let i = 0; i < 3; i++) {
      const start = mutations
      const page = await getJobsPage({ limit: sf.jobsLimit })
      if (!page || !Array.isArray(page.items)) throw new Error('bozuk cevap')
      fresh = page.items.map(normJob)
      sf.jobsTotal = page.total
      sf.jobsMore = page.hasMore
      if (start === mutations) break
    }
    // iyimser (negatif kimlikli) satırlar sunucu cevabına kadar korunur
    const now = Date.now()
    const kept = sf.jobs.filter((j) => j.id < 0 || (pinned.has(j.id) && !fresh.some((f) => f.id === j.id)) || (mine.has(j.id) && now - (mine.get(j.id) ?? 0) < 30000 && !fresh.some((f) => f.id === j.id)))
    sf.jobs = [...kept, ...fresh]
    sf.loadError = ''
    sf.jobsFailed = false
    sf.jobsOkAt = Date.now()
    void resolveReasons()
  } catch (e) {
    // İş listesi okunamadıysa "Henüz iş yok" denmez (C6): ekran açık hata gösterir, eldeki liste bayat olabilir.
    sf.jobsFailed = true
    sf.loadError = errText3(e)
  } finally {
    sf.loaded = true
  }
}

/** "Daha fazla göster": liste 50 iş daha ister (sunucu en çok 500 verir). */
export async function loadMoreJobs() {
  sf.jobsLimit = Math.min(500, sf.jobsLimit + 50)
  await loadJobs()
}

export async function loadCounts() {
  try {
    const c = (await getNow()).counts as { waiting?: number; jobsRunning?: number; queued?: number } | undefined
    if (!c) return
    const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null)
    sf.counts = { waiting: num(c.waiting), running: num(c.jobsRunning), queued: num(c.queued) }
  } catch {
    // sayaç gelmezse gösterilmez
  }
}

export async function loadChecks(projectId: number): Promise<Check[]> {
  try {
    sf.checks[projectId] = await getChecks(projectId)
  } catch {
    sf.checks[projectId] ??= []
  }
  return sf.checks[projectId]
}

export async function ensureTimeline(id: number, force = false) {
  if (id < 0) return
  if (sf.timelines[id] && !force) return
  sf.tlLoading[id] = true
  try {
    const r = await getTimeline(id)
    sf.timelines[id] = mergeKeepLocal(sf.timelines[id] ?? [], r.items)
    // Sunucunun bildirdiği iş durumu (çalışıyor mu, ajan soru mu sordu) listedeki satırı da günceller
    const j = sf.jobs.find((x) => x.id === id)
    if (j && r.state) {
      const run = r.run ?? null
      applyState(id, { ...(r.extra ?? {}), state: r.state as Job['state'], agentState: r.agentState ?? null, run, awaiting: awaitingOf(r.state, run, r.agentState) })
    }
  } catch (e) {
    if (!sf.timelines[id]) sf.timelines[id] = []
    if (!isMissing(e)) sf.lastMsg = errText3(e)
  } finally {
    sf.tlLoading[id] = false
  }
}

/** Sunucudan gelen akış esastır; yalnız henüz sunucuya ulaşmamış yerel satırlar (kimliği "yerel-" ile başlar) sona eklenir. */
function mergeKeepLocal(local: TimelineItem[], server: TimelineItem[]): TimelineItem[] {
  if (server.length === 0) return local
  const pending = local.filter((x) => String(x.id).startsWith('yerel-r') && !server.some((s) => s.kind === x.kind && s.text === x.text))
  return [...server, ...pending]
}

// Adresle ya da kısayolla açılan ama listede yüklü olmayan iş (50'den eski, süzgeç dışı) sunucudan getirilir; bulunamazsa jobMissing set edilir.
// Listede kalması için pinned tutulur (loadJobs onu atmaz). ASLA başka işi gösterme.
const pinned = new Map<number, Job>()
export async function ensureJob(id: number): Promise<boolean> {
  if (id < 0) return false
  if (sf.jobs.some((j) => j.id === id)) return true
  sf.jobPulling = id
  try {
    const d = await getJob(id)
    if (!d || typeof d !== 'object' || Number(d.id) !== id) throw new Error('bozuk cevap')
    const j = normJob(d as Job)
    pinned.set(id, j)
    if (!sf.jobs.some((x) => x.id === id)) sf.jobs = [...sf.jobs, j]
    if (sf.jobMissing === id) sf.jobMissing = null
    void ensureTimeline(id)
    return true
  } catch {
    sf.jobMissing = id
    if (sf.selId === id) sf.selId = null
    return false
  } finally {
    if (sf.jobPulling === id) sf.jobPulling = null
  }
}

/** Adresi değiştirmeden seçer (açılışta kendiliğinden ilk iş: adres kişinin yazdığı gibi kalır). */
function selectQuiet(id: number) {
  sf.selId = id
  sf.jobMissing = null
  void ensureTimeline(id)
}

export function select(id: number | null, opts: { detail?: boolean; oto?: boolean } = {}) {
  sf.selId = id
  sf.jobMissing = null
  // Seçilen iş adreste görünür (#/is/<no>): yenileme, geri tuşu ve bağlantı aynı işi açar (A-IS-07). Yalnız İşler sayfasındayken.
  // Listeden ayrıntıya ilk geçiş geçmişe yazılır (Geri listeye döner), ayrıntıdan ayrıntıya geçiş yerinde değişir (O1, gezinme.ts).
  const k = gecmisKarari({ route: router.route, sub: router.sub, id, oto: !!opts.oto, dar: dar() })
  if (k === 'push') {
    location.hash = '#/is/' + id
    try { history.replaceState({ ...(history.state ?? {}), sozListeden: true, t: Date.now() }, '') } catch { /* geçmiş yazılamıyorsa Geri yine çalışır */ }
  } else if (k === 'replace') {
    const st = history.state
    location.replace('#/is/' + id)
    try { history.replaceState(st, '') } catch { /* yok */ }
  }
  if (id !== null && !sf.jobs.some((j) => j.id === id)) void ensureJob(id)
  if (id !== null) {
    void ensureTimeline(id)
    if (opts.detail) {
      sf.view = 'detail'
      // İşi açmak ya da "Kanıtı gör" satırı düşürmez: sunucuya "görüldü" gönderilmez. Satır yalnız "Gördüm", kabul/ret ya da uygulanan geri almayla düşer.
    }
  }
}

/** Dar pencerede ayrıntıdan listeye dön ("← Liste" ve Esc): listeden gelindiyse geçmişte bir geri, değilse adres listeye çevrilir. */
export function listeyeDon() {
  sf.view = 'list'
  if (router.route !== 'is' || !/^\d+$/.test(router.sub)) return
  if ((history.state as { sozListeden?: boolean } | null)?.sozListeden) history.back()
  else location.replace('#/is')
}

export function setProject(id: number, explicit = false) {
  if (explicit) sf.picked = true
  sf.projectId = id
  const found = realProjects().find((p) => p.id === id)
  sf.selectedPath = found?.path ?? null
  lsSet('soz.lastProject', String(id))
  if (sf.selectedPath) lsSet('soz.lastProjectPath', sf.selectedPath)
  void loadChecks(id)
  void loadSummary(id)
}

const normPath = (p: string | null | undefined) => (p ?? '').trim().replace(/[\\/]+$/, '').toLowerCase()

/** Tanınan gerçek projeyi (profile.projects veya Söz projesi) seçer. Söz'de henüz kayıtlı değilse yolunu tutar, iş başlatırken sessizce kaydeder. */
export function selectProjectPath(path: string | null, explicit = false) {
  if (!path) return
  if (explicit) sf.picked = true
  const wanted = normPath(path)
  const existing = realProjects().find((p) => normPath(p.path) === wanted)
  if (existing) {
    setProject(existing.id, explicit)
    return
  }
  sf.selectedPath = path.trim()
  lsSet('soz.lastProjectPath', sf.selectedPath)
}

/** Canlı oturumdan ya da projeden "İş Ver": ilgili projeyi ve kuruluysa o yardımcıyı üst kutuda seçip metin alanına odaklanır. */
export function giveJobToProject(path: string | null, tool?: string | null) {
  if (router.route !== 'suan' && router.route !== 'is') go('suan')
  if (path) selectProjectPath(path, true)
  if (tool) {
    const t = tool.trim().toLowerCase()
    const mapped = t === 'claude-code' ? 'claude' : t
    if (agentOptions().some((a) => a.id === mapped && a.open)) {
      sf.agent = mapped
    }
  }
  sf.view = 'list'
  sf.focusNew++
}

// ---- canlı olaylar ----
function upsertItem(list: TimelineItem[], item: TimelineItem): TimelineItem[] {
  const i = list.findIndex((x) => x.id === item.id)
  if (i >= 0) {
    const next = list.slice()
    next[i] = item
    return next
  }
  return [...list, item]
}

export function applyProgress(jobId: number, item: TimelineItem) {
  mutations++
  const cur = sf.timelines[jobId]
  if (!cur) return // henüz açılmamış iş: açılınca sunucudan okunur
  // önceki "active" satırlar yenisi gelince biter
  const done = item.kind !== 'agent' && item.kind !== 'user' ? cur.map((x) => (x.kind === 'progress' && x.state === 'active' && x.id !== item.id ? { ...x, state: 'done' as const } : x)) : cur
  sf.timelines[jobId] = upsertItem(done, item)
  const j = sf.jobs.find((x) => x.id === jobId)
  if (j && item.kind === 'progress') j.run = { ...(j.run ?? { status: 'running' }), lastLine: item.text }
}

export function applyState(jobId: number, patch: Partial<Job>) {
  mutations++
  const j = sf.jobs.find((x) => x.id === jobId)
  if (j) Object.assign(j, patch)
  else void loadJobs()
}

let jobsTimer: ReturnType<typeof setTimeout> | undefined
function scheduleJobs() {
  clearTimeout(jobsTimer)
  // Durum değişince üst sayaçlar da sunucudan yenilenir (yoksa bir sonraki yoklamaya kadar eski sayıyı gösterirdi).
  jobsTimer = setTimeout(() => { void loadJobs(); void loadCounts() }, 250)
}

onLive((name, data) => {
  const d = (data ?? {}) as Record<string, unknown>
  if (name === 'job.progress') {
    const id = Number(d.jobId)
    const item = progressFromEvent(d)
    if (Number.isFinite(id) && item) applyProgress(id, item)
  } else if (name === 'job.state') {
    const id = Number(d.jobId ?? d.id)
    if (Number.isFinite(id)) {
      applyState(id, stateFromEvent(d))
      if (sf.timelines[id]) void ensureTimeline(id, true)
    }
    scheduleJobs()
  } else if (name === 'job' || name === 'proof') {
    scheduleJobs()
    const id = Number(d.id ?? d.jobId)
    if (Number.isFinite(id) && sf.timelines[id]) void ensureTimeline(id, true)
  }
})

// ---- eylemler ----

/** Gezgin'in "Yol olarak kopyala"sı yolu tırnaklı verir: baştaki ve sondaki tırnak ve boşluk kırpılır (A-KLAS-05). */
export const cleanPath = (s: string): string => s.trim().replace(/^["'“”‘’\s]+|["'“”‘’\s]+$/g, '')

export async function addFolder(path: string, opts: { quiet?: boolean } = {}): Promise<Project | null> {
  const p = cleanPath(path)
  if (!p) return null
  sf.folderError = ''
  // Ağ klasörü (\sunucu\paylaşım) geçerli bir Windows yoludur ama Söz henüz ağ klasörünü eklemiyor: doğru nedeni söyle, "yol gibi görünmüyor" deme.
  if (/^\\/.test(p)) {
    sf.folderError = t('folderUnc')
    return null
  }
  // Biçimi baştan bozuk yolu sunucuya göndermeyiz: hata kutunun altında sade cümleyle çıkar, ağ hatası da oluşmaz.
  if (!/^[A-Za-z]:[\\/]/.test(p) && !p.startsWith('/') && !p.startsWith('~')) {
    sf.folderError = t('folderBadPath')
    return null
  }
  try {
    const r = await addProject(p)
    await loadProjects()
    // Kişi bu klasörü kendisi yazdı ya da seçti: sınama klasörü gibi görünse bile SEÇİLİ sayılır (açıkça seçilmiş). Aksi hâlde geçerli klasör boş kalır,
    // kurulum kartı ya da bekleyen başlatma sonrası klasör penceresi yeniden açılırdı (A-YENI-21@klasorsuz).
    setProject(r.id, true)
    // İş başlatma akışında (bekleyen başlatma ya da sessiz kayıt) kişi klasör eklemiyor, seçiyor: "zaten ekli" demek anlamsız. Klasörler sayfasından elle eklemede söylenir.
    if (zatenEkliBildirimi(r.created, opts.quiet === true || sf.pendingStart)) toast(t('folderAlready')) // aynı klasör ikinci kez eklendi: sessiz kalma
    // yeni klasör ya da hâlâ açılmamış ölçüt varsa tek onay kartı; her şey zaten açıksa kart gösterilmez
    const needs = kurulumGerekir(r)
    sf.setup = needs ? r : null
    sf.addFolderOpen = false
    // Klasör eklenince kurulum kartı gerekmiyorsa bekleyen iş şimdi başlar (eskiden başlamıyordu). Yalnız o bekleyen taslak, başka taslak değil.
    if (!needs && sf.pendingStart && takePending()) {
      toast(t('startedInFolder', { f: r.name ?? p }))
      void startDraft()
    }
    return sf.projects.find((x) => x.id === r.id) ?? null
  } catch (e) {
    sf.folderError = errText3(e)
    return null
  }
}

export interface SubmitOpts {
  /** Testsiz klasör: ajan işe başlamadan önce küçük bir deneme yazsın. */
  writeCheckFirst?: boolean
  /** "Yalnız bu iş" kapsamlı ölçütler: iş taslakken eklenir (sunucu iş başlamadan önce ister), sonra iş başlar. */
  jobItems?: DoneAcceptItem[]
}

/** Araç kapalıyken başlatma 409 verir: sunucunun uzun cümlesi yerine sade cümle. */
export function jobErrText(e: unknown, agent?: string): string {
  if (e instanceof ApiError && e.status === 409 && /henüz açık değil/i.test(e.message)) return t('agNotOpen', { a: agentName(agent) })
  return errText3(e)
}

/** Yeni iş: önce çiz, sonra oluştur ve başlat. Klasör yoksa false döner (arayüz klasör ister). */
export async function submitJob(rawIn: string, opts: SubmitOpts = {}): Promise<boolean> {
  mutations++
  const raw = rawIn.trim()
  if (!raw) return false
  const proj = currentProject()
  if (!proj) return false
  const checks = sf.checks[proj.id] ?? (await loadChecks(proj.id))
  const ids = checks.filter((c) => c.enabled).map((c) => c.id)
  const tempId = -Date.now()
  const temp: Job = {
    id: tempId, projectId: proj.id, projectName: proj.name, sessionId: null, title: clip(raw), rawText: raw, promptText: '', source: 'yazi', state: 'calisiyor',
    night: sf.night, restate: false, createdAt: new Date().toISOString(), givenAt: null, finishedAt: null, lastVerdict: null, attempts: 0,
    run: { status: 'running', lastLine: t('rowStarting') },
  }
  sf.jobs = [temp, ...sf.jobs]
  sf.timelines[tempId] = [
    { id: 'yerel-u', ts: temp.createdAt, kind: 'user', text: raw },
    { id: 'yerel-p', ts: temp.createdAt, kind: 'progress', text: t('rowStarting'), state: 'active' },
  ]
  select(tempId, { detail: true })
  try {
    const nightNow = sf.night
    sf.night = false
    const real = normJob(await createJob({ projectId: proj.id, rawText: raw, source: 'yazi', restate: false, night: nightNow, checkIds: ids, ...(agentChoiceVisible() ? { agent: chosenAgent() } : {}), ...(opts.writeCheckFirst ? { writeCheckFirst: true } : {}) }))
    // "Yalnız bu iş": ölçütler işe, iş taslakken bağlanır. Bağlanamazsa iş başlamaz (kişinin istediği kapsam çiğnenmez).
    if (opts.jobItems && opts.jobItems.length > 0) {
      try {
        const r = await acceptDone(proj.id, opts.jobItems, 'job', real.id)
        toast(r.sentence ?? t('doneAddedN', { n: r.count }))
      } catch (e) {
        try { await cancelJob(real.id) } catch { /* iptal edilemezse taslak olarak kalır */ }
        sf.jobs = sf.jobs.filter((j) => j.id !== tempId && j.id !== real.id)
        delete sf.timelines[tempId]
        if (sf.selId === tempId) sf.selId = null
        toast(errText3(e))
        return false
      }
    }
    const carried = sf.timelines[tempId]
    delete sf.timelines[tempId]
    sf.timelines[real.id] = carried
    mutations++
    mine.set(real.id, Date.now())
    sf.jobs = [{ ...real, state: 'calisiyor', run: { status: 'running', lastLine: t('rowStarting') } }, ...sf.jobs.filter((j) => j.id !== tempId && j.id !== real.id)]
    if (sf.selId === tempId) sf.selId = real.id
    try {
      const started = await startJob(real.id, !!opts.writeCheckFirst)
      if (started) applyState(real.id, { state: started.state, run: started.run, agentState: started.agentState, awaiting: started.awaiting })
      // Görünür onay (O5): bildirim ve Şu An'da "Az önce verdiğin" satırı. "İzle" o işi açar.
      sf.sonVerilen = { id: real.id, at: Date.now() }
      const rid = real.id
      toast(t('jobStartedToast', { n: kisaAd(real.title || raw) }), { label: t('jobStartedWatch'), run: () => isiIzle(rid) })
    } catch (e) {
      if (isMissing(e)) {
        sf.startMissing[real.id] = true
        applyState(real.id, { state: 'taslak', run: null })
        sf.timelines[real.id] = [{ id: 'yerel-u', ts: real.createdAt, kind: 'user', text: raw }, { id: 'yerel-s', ts: new Date().toISOString(), kind: 'system', text: t('errNotInVersion') }]
      } else if (e instanceof ApiError && e.status === 409 && /henüz açık değil/i.test(e.message)) {
        // Araç kapalı: bu iş o araçla hiç başlamaz. Açıkta ölü bir taslak bırakma; sade cümleyle söyle, işi iptal et.
        try { await cancelJob(real.id) } catch { /* iptal edilemezse taslak olarak kalır */ }
        sf.jobs = sf.jobs.filter((j) => j.id !== real.id)
        delete sf.timelines[real.id]
        if (sf.selId === real.id) sf.selId = null
        toast(jobErrText(e, real.agent))
        return false
      } else {
        applyState(real.id, { state: 'taslak', run: null })
        toast(jobErrText(e, real.agent))
      }
    }
    void ensureTimeline(real.id, true)
    void loadSummary(proj.id)
    return true
  } catch (e) {
    sf.jobs = sf.jobs.filter((j) => j.id !== tempId)
    delete sf.timelines[tempId]
    if (sf.selId === tempId) sf.selId = null
    toast(jobErrText(e, chosenAgent()))
    return false
  }
}

/** Taslağı iş olarak başlatır. Seçili klasör tanınan bir profil projesiyse önce Söz'e sessizce bağlar; klasör yoksa seçtirir. */
export async function startDraft(opts: { force?: boolean } = {}): Promise<void> {
  const raw = sf.draft.trim()
  if (!raw) {
    sf.notJob = null
    sf.focusNew++ // boşken Başlat'a basmak ölü değildir: yazı kutusuna götürür ve ne yapılacağını söyler
    toast(t('startEmpty'))
    return
  }
  // "Bu bir iş mi?" (lib/isTani.ts): selam ve soru iş olmaz, sahte iş kaydı da açılmaz. "Yine de iş olarak ver" bu kapıyı bir kez geçer.
  if (opts.force) forcedRaw = raw
  if (forcedRaw !== raw) {
    const k = isMi(raw)
    if (!k.is) {
      sf.notJob = { text: raw, neden: k.neden }
      return
    }
  }
  sf.notJob = null
  // Kullanıcı profildeki bir projeyi seçtiyse ama Söz'de henüz Project satırı yoksa sessizce kaydet.
  if (sf.selectedPath && normPath(currentProject()?.path) !== normPath(sf.selectedPath)) {
    const added = await addFolder(sf.selectedPath, { quiet: true })
    if (!added && sf.folderError) {
      toast(sf.folderError)
      return
    }
  }
  if (!currentProject()) {
    sf.pendingStart = true
    sf.pendingRaw = raw
    sf.addFolderOpen = true
    sf.focusPath++
    return
  }
  if (sf.setup) {
    sf.pendingStart = true
    sf.pendingRaw = raw
    sf.focusSetup++
    return
  }
  // Ajan "bitti" demeden önce: bitti sayılması için ölçütler görünür. Klasörde zaten ölçüt varsa tam kart açılmaz, tek satırlık özet yeter
  // (sunucu söyler: needsFullCard). Kişi "Değiştir" derse tam kart yine açılır.
  if (startingDraft) return
  startingDraft = true
  try {
    const full = sf.forceFull
    sf.forceFull = false
    const proj = currentProject()
    const sum = proj ? await loadSummary(proj.id) : null
    if (sum && !sum.needsFullCard && !full) {
      sf.draft = ''
      const ok = await submitJob(raw)
      if (!ok) sf.draft = raw
    } else {
      sf.doneRaw = raw
    }
  } finally {
    startingDraft = false
  }
}
let startingDraft = false
/** Kişi "yine de iş olarak ver" dediği cümle: klasör ya da kurulum adımından dönünce yeniden elenmez. */
let forcedRaw = ''

/** "Bitti sayılması için" kartı kapandı. start=true: iş başlar (kabul edilen ölçütler zaten eklendi). start=false: vazgeçildi, taslak yerinde durur. */
export async function finishDone(start: boolean, opts: SubmitOpts = {}) {
  const raw = sf.doneRaw
  sf.doneRaw = null
  if (!start || !raw) {
    sf.focusNew++
    return
  }
  sf.draft = ''
  const ok = await submitJob(raw, opts)
  if (!ok) sf.draft = raw
}

/** Ajanın yazdığı denemeyi bu klasörde hep çalıştır (Evet) ya da bırak (Hayır). Öneri kapalı bir ölçüttür; Evet onu açar. */
export async function answerSuggested(j: Job, s: SuggestedCheck, yes: boolean): Promise<boolean> {
  try {
    if (!yes) {
      const back = await declineSuggestedCheck(s.checkId)
      if (back) applyState(j.id, back)
      return true
    }
    await patchCheck(s.checkId, { enabled: true })
    await loadChecks(j.projectId)
    void loadSummary(j.projectId)
    toast(t('sugAccepted'))
    return true
  } catch (e) {
    toast(errText3(e))
    return false
  }
}
/** Ajanın testleri için bekleyen teklif. "Hayır"ı sunucu kendisi kaydeder: cevaplanan kısım bir daha gelmez. */
export function visibleOffer(j: Job): TestOffer | null {
  const o = j.testOffer
  return o && (o.added > 0 || o.edited > 0) ? o : null
}
/** Teklife verilen cevaptan sonra işi ve Sıra bende listesini tazeler. */
function offerDone(j: Job, back: Job | null) {
  applyState(j.id, back ? { ...back, testOffer: back.testOffer ?? null } : { testOffer: null })
  delete sf.offerError[j.id]
  void loadAttention()
}
/** Eklenen yeni testler: "Evet" onayladığın testlere katar (sunucuda yalnız insan çağırabilir), "Hayır" sunucuda kayıtlı reddeder. */
export async function answerOffer(j: Job, yes: boolean): Promise<boolean> {
  try {
    const back = yes ? await approveNewTests(j.id, 'added') : await declineNewTests(j.id, 'added')
    offerDone(j, back)
    toast(t(yes ? 'offerAdded' : 'offerDeclined'))
    return true
  } catch (e) {
    sf.offerError[j.id] = errText3(e)
    return false
  }
}
/** "Farkı gör": ajanın değiştirdiği onaylı testlerin satırlarını getirir. Sunucu bunu "gördü" diye yazar, kabul ancak ondan sonra mümkün. */
export async function viewOfferEdit(j: Job): Promise<boolean> {
  try {
    const o = await getTestOffer(j.id)
    if (!o.editedId) throw new Error(t('offerNoDiff'))
    if (sf.offerStale[j.id] === o.editedId) return false
    delete sf.offerStale[j.id]
    sf.offerDiff[j.id] = { id: o.editedId, files: o.editedFiles ?? [] }
    delete sf.offerError[j.id]
    return true
  } catch (e) {
    sf.offerError[j.id] = errText3(e)
    return false
  }
}
/** Fark görüldüyse kabul düğmesi etkin olur. */
export const offerEditSeen = (j: Job): boolean => !!j.testOffer?.editedId && sf.offerDiff[j.id]?.id === j.testOffer.editedId
export const offerEditViewable = (j: Job): boolean => {
  const files = sf.offerDiff[j.id]?.files ?? []
  return offerEditSeen(j) && files.length > 0 && files.every((f) => (f.lines ?? []).some((l) => l.op === '+' || l.op === '-'))
}
/** Değiştirilen onaylı testleri kabul et (fark özetinin kimliğiyle) ya da kabul etme. Sunucu 409 verirse cümlesi kartta görünür. */
export async function answerOfferEdit(j: Job, yes: boolean): Promise<boolean> {
  const id = j.testOffer?.editedId
  try {
    if (yes && !offerEditViewable(j)) throw new Error(t('offerSeeFirst'))
    const back = yes ? await approveNewTests(j.id, 'edited', id) : await declineNewTests(j.id, 'edited')
    offerDone(j, back)
    delete sf.offerDiff[j.id]
    delete sf.offerStale[j.id]
    toast(t(yes ? 'offerEditAccepted' : 'offerEditDeclined'))
    return true
  } catch (e) {
    const message = errText3(e)
    if (yes && e instanceof ApiError && e.status === 409) {
      delete sf.offerDiff[j.id]
      if (/yeniden değişti/i.test(message) && id) sf.offerStale[j.id] = id
      try {
        const fresh = normJob(await getJob(j.id))
        applyState(j.id, fresh)
        const offer = await getTestOffer(j.id)
        if (offer.editedId && (!/yeniden değişti/i.test(message) || offer.editedId !== id)) {
          sf.offerDiff[j.id] = { id: offer.editedId, files: offer.editedFiles ?? [] }
        }
      } catch { /* Asıl sunucu hatası korunur. */ }
    }
    sf.offerError[j.id] = message
    return false
  }
}

/** Bu iş için gösterilecek deneme önerileri: kabul edilmemiş ve "Hayır" denmemiş olanlar. */
export function visibleSuggestions(j: Job): SuggestedCheck[] {
  const list = sf.checks[j.projectId] ?? []
  return (j.suggestedChecks ?? []).filter((s) => !list.some((c) => c.id === s.checkId && c.enabled))
}

/**
 * Onay kartı kapandı. start=true (ölçütler açıldı ya da "Kanıtsız devam et" düğmesine bilerek basıldı): bekleyen iş başlar.
 * start=false (Esc, vazgeçti): HİÇBİR iş başlamaz, yazı kutusundaki taslak durur (denetim 3, Y5: Esc iş başlatmaz).
 */
export function closeSetup(start = true) {
  sf.setup = null
  if (sf.pendingStart) {
    const ok = takePending()
    if (start && ok) void startDraft()
  }
}

/** Bekleyen başlatmayı tüketir; yalnız kutudaki taslak hâlâ o taslaksa true (bekleyenBaslat.ts). */
function takePending(): boolean {
  const p = { on: sf.pendingStart, raw: sf.pendingRaw }
  const ok = al(p, sf.draft)
  sf.pendingStart = false
  sf.pendingRaw = ''
  return ok
}

/** Klasör penceresinden vazgeçildi (klasör hâlâ yok): bekleyen başlatma silinir, taslak kutuda durur. */
export function cancelPendingStart() {
  if (!sf.pendingStart) return
  if (sf.setup) return // kurulum kartı bu başlatmayı bekliyor
  sf.pendingStart = false
  sf.pendingRaw = ''
  toast(t('startNeedsFolder')) // vazgeçince sessiz kalma: iş başlamadı, yazı kutuda duruyor
}

/** Taslak kalan işi yeniden başlatmayı dener. */
export async function retryStart(id: number) {
  const j = sf.jobs.find((x) => x.id === id)
  if (!j) return
  applyState(id, { state: 'calisiyor', run: { status: 'running', lastLine: t('rowStarting') } })
  try {
    const started = await startJob(id)
    if (started) applyState(id, { state: started.state, run: started.run, awaiting: started.awaiting })
    delete sf.startMissing[id]
    sf.sonVerilen = { id, at: Date.now() }
    toast(t('jobStartedToast', { n: kisaAd(j.title || j.rawText) }), { label: t('jobStartedWatch'), run: () => isiIzle(id) })
    void ensureTimeline(id, true)
  } catch (e) {
    applyState(id, { state: 'taslak', run: null })
    toast(errText3(e))
  }
}

/** Başlamamış (taslak) işten vazgeç (A4): sunucuda iş iptal edilir, listede "Durduruldu" olur. Silme ucu yok. */
export async function vazgecTaslak(id: number): Promise<boolean> {
  return (await once('cancel:' + id, async () => {
    try {
      await cancelJob(id)
      applyState(id, { state: 'iptal', run: null, awaiting: null })
      toast(t('draftCancelled'))
      void loadJobs()
      return true
    } catch (e) {
      toast(errText3(e))
      return false
    }
  })) ?? false
}

export async function sendReply(id: number, text: string): Promise<boolean> {
  const msg = text.trim()
  if (!msg) return false
  const j = sf.jobs.find((x) => x.id === id)
  const prev = j ? { awaiting: j.awaiting, run: j.run, state: j.state, lastVerdict: j.lastVerdict } : null
  const item: TimelineItem = { id: `yerel-r${Date.now()}`, ts: new Date().toISOString(), kind: 'user', text: msg }
  sf.timelines[id] = [...(sf.timelines[id] ?? []), item]
  if (j) applyState(id, { awaiting: null, state: 'calisiyor', run: { status: 'running', lastLine: t('rowStarting') } })
  try {
    const back = await replyJob(id, msg)
    if (back) applyState(id, { state: back.state, run: back.run, agentState: back.agentState, awaiting: back.awaiting })
    void ensureTimeline(id, true)
    return true
  } catch (e) {
    sf.timelines[id] = (sf.timelines[id] ?? []).filter((x) => x.id !== item.id)
    if (prev) applyState(id, prev)
    toast(errText3(e))
    return false
  }
}

// Tek istek koruması: aynı anahtarla bir işlem sürerken ikincisi hiç gönderilmez (çift tık, tuş tekrarı). Sürerken sf.busy[anahtar] true olur.
const inflight = new Set<string>()
export async function once<T>(key: string, fn: () => Promise<T>): Promise<T | undefined> {
  if (inflight.has(key)) return undefined
  inflight.add(key)
  sf.busy[key] = true
  try {
    return await fn()
  } finally {
    inflight.delete(key)
    delete sf.busy[key]
  }
}

/** "Durdur" ya da s tuşu: önce sorar, onaylanınca stopCurrent çalışır. */
export function askStop(id: number) {
  const j = sf.jobs.find((x) => x.id === id)
  if (!j) return
  if (bucketOf(j) !== 'running' && !(bucketOf(j) === 'me' && j.awaiting === 'approval')) {
    toast(t('stopNotRunning'))
    return
  }
  sf.stopAsk = id
}

export function stopCurrent(id: number): Promise<void> {
  sf.stopAsk = null
  return once('stop:' + id, () => stopNow(id)).then(() => undefined)
}

async function stopNow(id: number) {
  const j = sf.jobs.find((x) => x.id === id)
  if (!j) return
  if (bucketOf(j) !== 'running' && !(bucketOf(j) === 'me' && j.awaiting === 'approval')) {
    toast(t('stopNotRunning'))
    return
  }
  const prev = { run: j.run, state: j.state, awaiting: j.awaiting, pendingApprovals: j.pendingApprovals }
  applyState(id, { run: { status: 'stopped', lastLine: t('rowStopped') }, awaiting: null, pendingApprovals: null, state: 'durduruldu' })
  const item: TimelineItem = { id: `yerel-s${Date.now()}`, ts: new Date().toISOString(), kind: 'system', text: t('stoppedLine') }
  sf.timelines[id] = [...(sf.timelines[id] ?? []).map((x) => (x.kind === 'progress' && x.state === 'active' ? { ...x, state: 'done' as const } : x)), item]
  try {
    const back = await stopJob(id)
    if (back) applyState(id, { state: back.state, run: back.run, awaiting: back.awaiting })
    toast(t('stoppedToast'))
    void ensureTimeline(id, true)
  } catch (e) {
    applyState(id, prev)
    sf.timelines[id] = (sf.timelines[id] ?? []).filter((x) => x.id !== item.id)
    toast(errText3(e))
  }
}

/** Bekçi onayı: en eski bekleyen tek tek cevaplanır. Hepsi cevaplanınca iş çalışmaya döner. */
export function decide(id: number, eventId: number, allow: boolean): Promise<void> {
  return once('decide:' + id + ':' + eventId, () => decideNow(id, eventId, allow)).then(() => undefined)
}
async function decideNow(id: number, eventId: number, allow: boolean) {
  const j = sf.jobs.find((x) => x.id === id)
  const before = j ? pendingList(j) : []
  const prevJob = j ? { awaiting: j.awaiting, run: j.run, state: j.state, pendingApprovals: j.pendingApprovals } : null
  const prevAnswered = sf.answered[id] ?? 0
  const rest = before.filter((x) => x.eventId !== eventId)
  sf.answered[id] = prevAnswered + 1
  sf.timelines[id] = (sf.timelines[id] ?? []).map((x) => (x.kind === 'approval' && x.eventId === eventId ? { ...x, state: allow ? ('allowed' as const) : ('denied' as const) } : x))
  if (j) {
    if (rest.length > 0) applyState(id, { pendingApprovals: rest })
    else applyState(id, { awaiting: null, pendingApprovals: null, state: 'calisiyor', run: { status: 'running', lastLine: allow ? t('rowAllowed') : t('rowDenied') } })
  }
  try {
    const back = await approveJob(id, eventId, allow)
    if (back) applyState(id, { pendingApprovals: null, ...back, awaiting: back.awaiting })
    if (!back || (back.pendingApprovals ?? []).length === 0) sf.answered[id] = 0
    toast(allow ? t('approveToast') : t('denyToast')) // ne olduğu tek cümleyle söylenir
    void ensureTimeline(id, true)
  } catch (e) {
    sf.answered[id] = prevAnswered
    sf.timelines[id] = (sf.timelines[id] ?? []).map((x) => (x.kind === 'approval' && x.eventId === eventId ? { ...x, state: 'pending' as const } : x))
    if (prevJob) applyState(id, prevJob)
    toast(errText3(e))
  }
}

/** Bekleyen ilk onay (en eski): klavye kısayolu bunu cevaplar. */
export const pendingApproval = (id: number): PendingApproval | null => {
  const j = sf.jobs.find((x) => x.id === id)
  return j ? (pendingList(j)[0] ?? null) : null
}

/** "Değişikliği kabul et" ya da "Kabul etme". */
export async function decideChange(id: number, accept: boolean): Promise<boolean> {
  return (await once('change:' + id, () => decideChangeNow(id, accept))) ?? false
}
async function decideChangeNow(id: number, accept: boolean): Promise<boolean> {
  const j = sf.jobs.find((x) => x.id === id)
  if (!j) return false
  const prev = { state: j.state, awaiting: j.awaiting, acceptedByUser: j.acceptedByUser, lastVerdict: j.lastVerdict }
  // Kabul, ekranda gösterilen kartın görüntü kimliğini taşır: klasör kart çizildikten sonra değiştiyse sunucu 409 verir (denetim 4, E).
  const lastProof = [...(sf.timelines[id] ?? [])].reverse().find((x) => x.kind === 'proof' && x.proof)
  const snapshotId = lastProof?.proof?.snapshotId ?? null
  applyState(id, accept ? { state: 'gecti', awaiting: null, acceptedByUser: true, lastVerdict: 'passed' } : { state: 'kaldi', awaiting: null, lastVerdict: 'failed' })
  try {
    const back = await acceptJob(id, accept, snapshotId)
    if (back) applyState(id, { ...back, awaiting: back.awaiting })
    toast(accept ? t('acceptedToast') : t('rejectedToast'))
    void ensureTimeline(id, true)
    return true
  } catch (e) {
    applyState(id, prev)
    toast(errText3(e))
    void ensureTimeline(id, true) // eski kartı yenisiyle değiştir: kullanıcı güncel farkı görüp yeniden karar versin
    return false
  }
}

/** "Reddet, ajana geri gönder": iş "Kaldı" olur ve devam kutusu açılır; kutuya kişinin düzenleyebileceği bir taslak konur. */
export async function rejectAndSendBack(id: number, draft: string) {
  if (!(await decideChange(id, false))) return
  if (draft) sf.prefill[id] = draft
  sf.rejectBack[id] = true
  sf.continuing[id] = true
  sf.focusReply++
}

/** Biten işe aynı oturumdan devam: iş çalışmaya döner. */
export async function continueFinished(id: number, text: string): Promise<boolean> {
  const msg = text.trim()
  if (!msg) return false
  const j = sf.jobs.find((x) => x.id === id)
  const prev = j ? { state: j.state, run: j.run, awaiting: j.awaiting, acceptedByUser: j.acceptedByUser } : null
  const item: TimelineItem = { id: `yerel-r${Date.now()}`, ts: new Date().toISOString(), kind: 'user', text: msg }
  sf.timelines[id] = [...(sf.timelines[id] ?? []), item]
  applyState(id, { state: 'calisiyor', awaiting: null, acceptedByUser: false, run: { status: 'running', lastLine: t('rowStarting') } })
  sf.continuing[id] = false
  try {
    const back = await continueJob(id, msg)
    if (back) applyState(id, { ...back, awaiting: back.awaiting })
    void ensureTimeline(id, true)
    return true
  } catch (e) {
    sf.timelines[id] = (sf.timelines[id] ?? []).filter((x) => x.id !== item.id)
    if (prev) applyState(id, prev)
    sf.continuing[id] = true
    toast(errText3(e))
    return false
  }
}

export async function boot3() {
  await Promise.all([loadProjects(), loadJobs(), loadCounts(), loadAgents()])
  if (sf.projectId !== null) {
    void loadChecks(sf.projectId)
    void loadSummary(sf.projectId)
  }
  // İlk işi seçmek Surface.svelte'in işidir (isSecim.ts): adreste bir iş numarası varsa ya da o iş bulunamadıysa buradan ASLA başka iş seçilmez.
  // Burası yalnız Surface hiç çizilmeyen sayfalarda (Şu An) bir iş seçili olsun diye çalışır ve adrese bakmadan seçim yapmaz.
  const ilk = ilkIsBoottaMi({ route: router.route, routeSub: router.sub, selId: sf.selId, missing: sf.jobMissing, pulling: sf.jobPulling })
  const first = flatJobs()[0]
  if (first && ilk) selectQuiet(first.id)
}

// Sayaçlar değişince (15 sn yoklama ve SSE) listeyi tazele.
export function refreshSurface() {
  void loadJobs()
  void loadProjects()
  void loadCounts()
}

// Yeni iş taslağı: yazdıkça saklanır (depolama kapalıysa sessizce geçilir), iş oluşunca (taslak boşalınca) silinir.
$effect.root(() => {
  $effect(() => {
    const v = sf.draft
    try {
      if (v.trim()) localStorage.setItem(DRAFT_KEY, v)
      else localStorage.removeItem(DRAFT_KEY)
    } catch {
      // depolama yoksa taslak yalnız bu oturumda yaşar
    }
  })
})
