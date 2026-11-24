// Örnek kip (?ornek=1), Dalga 2 uçları: Sıra bende, bitti'nin tanımı, koruma ve geri alma, tanışma, kayıt defteri, ayarlar.
// Cevap biçimleri GERÇEK sunucu cevaplarıyla aynıdır (bkz. _gecici/API-5.md ve mock5data.ts). Bütün kişi, klasör ve cümleler UYDURMADIR.
// Sıra bende öğeleri örnek işlerden TÜRETİLİR (sunucudaki AttentionBuilder ile aynı kural): iş cevap beklerse soru, karar beklerse kabul, kaldıysa engel, kanıtla bittiyse bitti.
// ?duzeltilmis=1: bir ayarın kayıtlı değeri sınır dışıymış gibi ayar cevabına `adjusted` eklenir (gerçek biçim).
import { ApiError } from './errors'
import { emitLive } from './live'
import { emptyMode, HOUR, iso, MIN, projects, settings as oldSettings, sleep } from './mock'
import { mock2Handle, mock2State, NOT_HANDLED } from './mock2'
import { mockLastProof } from './mock3'
import { mock6Handle } from './mock6'
import { ADJUSTED_SAMPLE, DONE_LEVELS, DONE_QUESTIONS, DONE_SUGGEST, ONBOARDING_EMPTY, REGISTRY_ENTRIES, SETTINGS_DEFAULTS } from './mock5data'
import type { Job } from './types'
import type {
  AttentionAction,
  AttentionItem,
  AttentionSnapshot,
  DoneProposal,
  OnboardingItem,
  OnboardingView,
  Protection,
  RegistryEntry,
  ServerSettings,
  SettingBody,
  UndoItem,
  UndoRecord,
  UndoReply,
} from './types5'

const wantsAdjusted = typeof location !== 'undefined' && /[?&]duzeltilmis=1/.test(location.search)
const pname = (id: number | null) => projects.find((p) => p.id === id)?.name ?? null
const clip = (s: string | null | undefined, n: number) => (s ? (s.length > n ? s.slice(0, n - 1) + '…' : s) : null)

// ---------------- kayıt defteri ve ayarlar ----------------
const registry: RegistryEntry[] = REGISTRY_ENTRIES.map((e) => JSON.parse(JSON.stringify(e)) as RegistryEntry)
// Gerçek sunucuda Hermes ve OpenCode iş yürütebilir ama KAPALI gelir (kabul edilince açılır).
for (const e of registry) {
  if (e.id === 'connector:hermes' || e.id === 'connector:opencode') {
    const bd = e.body as { depth?: string[]; capabilities?: Record<string, unknown> }
    bd.depth = ['observe', 'run']
    bd.capabilities = { headless: true, eventStream: true, resume: true, stop: e.id !== 'connector:hermes', beforeTool: 'none', turnEnd: 'outer-loop', ask: 'resume-reply', cost: 'tokens' }
  }
}
const body = (e: RegistryEntry) => e.body as SettingBody
const keyOf = (e: RegistryEntry) => body(e).legacyKey ?? e.id

// mock.ts bu dosyayı içe alır, bu dosya da mock.ts'i: eski ayar nesnesi modül yüklenirken henüz yok. İlk istekte kurulur.
const values: Record<string, unknown> = { ...SETTINGS_DEFAULTS }
let adjusted: ServerSettings['adjusted'] = []
let inited = false
function init() {
  if (inited) return
  inited = true
  for (const [k, v] of Object.entries(oldSettings as unknown as Record<string, unknown>)) if (k in values && v !== undefined) values[k] = v
  if (wantsAdjusted) {
    for (const a of ADJUSTED_SAMPLE) values[a.key] = a.applied
    adjusted = ADJUSTED_SAMPLE.map((a) => ({ ...a }))
  }
}

/** Sunucunun doğrulaması: sınır dışı değer 400 ve sade cümle (gerçek örnek: "gateMaxAttempts: 1 ile 20 arası tam sayı olmalı"). */
function validate(key: string, v: unknown): void {
  if (key === 'themeFamily') {
    if (!['cam', 'terminal', 'mat', 'ton', 'duz', 'sicak'].includes(String(v))) throw new ApiError(400, `${key}: geçersiz tema ailesi`)
    return
  }
  if (key === 'navTabs') {
    if (v !== null && (!Array.isArray(v) || !v.every((x) => typeof x === 'string'))) throw new ApiError(400, `${key}: geçerli sekme listesi olmalı`)
    return
  }
  const e = registry.find((x) => x.kind === 'setting' && (keyOf(x) === key || x.id === key))
  if (!e) throw new ApiError(400, `${key}: böyle bir ayar yok`)
  const sch = body(e).schema ?? {}
  const types = Array.isArray(sch.type) ? sch.type : sch.type ? [sch.type] : []
  if (v === null) {
    if (!types.includes('null')) throw new ApiError(400, `${key}: boş bırakılamaz`)
    return
  }
  if (types.includes('integer') || types.includes('number')) {
    const isInt = types.includes('integer')
    if (typeof v !== 'number' || !Number.isFinite(v) || (isInt && !Number.isInteger(v))) throw new ApiError(400, `${key}: ${isInt ? 'tam sayı' : 'sayı'} olmalı`)
    const lo = sch.minimum
    const hi = sch.maximum
    if ((lo !== undefined && v < lo) || (hi !== undefined && v > hi)) {
      throw new ApiError(400, lo !== undefined && hi !== undefined ? `${key}: ${lo} ile ${hi} arası ${isInt ? 'tam sayı' : 'sayı'} olmalı` : `${key}: sınır dışı`)
    }
    return
  }
  if (types.includes('boolean') && typeof v !== 'boolean') throw new ApiError(400, `${key}: açık ya da kapalı olmalı`)
  if (sch.enum && !sch.enum.some((x) => x === v)) throw new ApiError(400, `${key}: şunlardan biri olmalı: ${sch.enum.join(', ')}`)
  if (types.includes('string') && typeof v === 'string' && sch.maxLength !== undefined && v.length > sch.maxLength) throw new ApiError(400, `${key}: en çok ${sch.maxLength} karakter olabilir`)
}

const settingsReply = (): ServerSettings => ({ ...values, adjusted: adjusted?.map((a) => (typeof a === 'string' ? a : { ...a })) ?? [] })

// ---------------- Sıra bende ----------------
const acked = new Set<string>()
const DAILY = 3

function jobItem(j: Job, kind: string, reason: string, sentence: string, detail: string | null, at: string, firm: boolean, actions: AttentionAction[]): AttentionItem {
  const id = `${kind}:job:j${j.id}:${at}`
  const acts = actions.map((a) => (a.path === '/api/attention/ack' ? { ...a, body: { id } } : a))
  const prio = kind === 'ask' ? 1 : kind === 'accept' ? 2 : kind === 'blocked' ? (reason === 'proof_failed' ? 2 : 3) : kind === 'done' ? 4 : 5
  return { id, kind, reason, priority: prio, sentence, detail, title: clip(j.title, 60) ?? 'Adsız iş', jobUid: `ornek-is-${j.id}`, jobId: j.id, sessionId: j.sessionId, projectName: j.projectName ?? pname(j.projectId), at, confidence: firm ? 'firm' : 'unknown', signal: false, isNew: false, actions: acts }
}
const failedActions = (id: number): AttentionAction[] => [
  { id: 'continue', label: 'Devam et', method: 'POST', path: `/api/jobs/${id}/continue`, body: null },
  { id: 'seen', label: 'Gördüm', method: 'POST', path: '/api/attention/ack', body: null },
]
const ackOnly = (): AttentionAction[] => [{ id: 'seen', label: 'Gördüm', method: 'POST', path: '/api/attention/ack', body: null }]

async function sessionsNow() {
  const n = (await mock2Handle('GET', '/api/now', new URLSearchParams(), {})) as { needsYou: Record<string, unknown>[] }
  const needsYou = (n.needsYou ?? []).map((s) => ({
    ...s,
    stateSource: s.state === 'stalled' ? 'process' : 'log',
    stateConfidence: s.state === 'stalled' ? 'unknown' : 'probable',
    endClass: s.state === 'stalled' ? 'truncated' : 'awaiting_user',
  }))
  const uncertain = emptyMode ? [] : [{ sessionId: 9001, tool: 'hermes', projectId: 2, projectName: pname(2), title: 'Gece kontrol oturumu', state: 'idle', since: iso(3 * MIN + 40_000), reason: null, question: null, pendingQueue: 0, stateSource: 'process', stateConfidence: 'unknown', endClass: 'tool_call_pending' }]
  return { ...n, needsYou, uncertain }
}

async function attention(): Promise<AttentionSnapshot> {
  const items: AttentionItem[] = []
  const running: NonNullable<AttentionSnapshot['running']> = []
  if (!emptyMode) {
    mockLastProof(0) // örnek işlerin tohumunu döngüden önce kur: döngü sırasında iş listesi değişirse bir iş iki kez görünürdü
    const jobs = [...mock2State().jobs]
    const cut = Date.now() - 24 * HOUR
    for (const j of jobs) {
      const at = j.finishedAt ?? j.givenAt ?? j.createdAt
      const title = clip(j.title, 60) ?? 'Adsız iş'
      if (j.state === 'cevap_bekliyor') {
        items.push(jobItem(j, 'ask', 'question', `«${title}» işinde yardımcı sana bir soru sordu.`, clip(j.question, 240), at, true, [{ id: 'reply', label: 'Cevap yaz', method: 'POST', path: `/api/jobs/${j.id}/reply`, body: { text: '' } }]))
      } else if (j.state === 'onay_bekliyor') {
        const ap = j.pendingApprovals?.[0]
        const acts: AttentionAction[] = ap
          ? [
              { id: 'allow', label: 'İzin ver', method: 'POST', path: `/api/jobs/${j.id}/approve`, body: { allow: true, eventId: ap.eventId } },
              { id: 'deny', label: 'İzin verme', method: 'POST', path: `/api/jobs/${j.id}/approve`, body: { allow: false, eventId: ap.eventId } },
            ]
          : []
        items.push(jobItem(j, 'ask', 'approval', `«${title}» işinde yardımcı bir işlem için senden izin bekliyor.`, clip(ap?.summary, 240), at, true, acts))
      } else if (j.state === 'karar_bekliyor') {
        items.push(jobItem(j, 'accept', 'decision', `«${title}» işinde deneme sonucu sayılamadı. Sonucu kabul edip etmeyeceğine sen karar ver.`, mockLastProof(j.id)?.reasonText ?? 'Yardımcı, geçmeyen bir testi değiştirerek işi geçmiş gösteriyor olabilir. Bu yüzden sonucu saymadım.', at, true, [
          { id: 'accept', label: 'Kabul ediyorum', method: 'POST', path: `/api/jobs/${j.id}/accept`, body: { accept: true, snapshotId: mockLastProof(j.id)?.snapshotId ?? `ornek-goruntu-${j.id}` } },
          { id: 'reject', label: 'Kabul etmiyorum', method: 'POST', path: `/api/jobs/${j.id}/accept`, body: { accept: false } },
        ]))
      } else if (j.state === 'kaldi') {
        items.push(jobItem(j, 'blocked', 'proof_failed', `«${title}» için deneme sonucu geçmedi.`, j.failedLabels?.length ? `${j.failedLabels.length} kontrolden ${j.failedLabels.length} tanesi kaldı: ${j.failedLabels.join(', ')}` : '1 kontrol kaldı: Testler geçsin', at, true, failedActions(j.id)))
      } else if (j.state === 'baslatilamadi') {
        items.push(jobItem(j, 'blocked', 'start_failed', `«${title}» başlatılamadı.`, clip(j.startError, 240), at, true, failedActions(j.id)))
      } else if (j.state === 'gecti' && !j.acceptedByUser && j.finishedAt && Date.parse(j.finishedAt) >= cut) {
        const lp = mockLastProof(j.id)
        const noChange = lp?.reasonCode === 'no_change'
        const acts = ackOnly()
        if (j.testOffer) acts.unshift({ id: 'approve-tests', label: 'Evet, kat', method: 'POST', path: `/api/jobs/${j.id}/approve-new-tests`, body: {} })
        const detail = [j.testOffer?.sentence, noChange ? lp?.reasonText : `${Math.max(1, j.attempts ?? 1)} kontrolün hepsi geçti.`].filter(Boolean).join(' ')
        items.push(jobItem(j, 'done', noChange ? 'no_change' : 'passed', noChange ? `«${title}» için yardımcı hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu.` : `«${title}» deneme sonucuyla bitti.`, detail, at, true, acts))
      } else if (j.state === 'calisiyor') {
        running.push({ jobUid: `ornek-is-${j.id}`, jobId: j.id, title, projectName: j.projectName ?? pname(j.projectId) })
      }
    }
    const now = await sessionsNow()
    for (const n of now.needsYou as Record<string, any>[]) {
      const title = n.title || n.projectName || 'Yardımcı'
      const base = { title, jobUid: null, jobId: null, sessionId: n.sessionId as number, projectName: n.projectName as string | null, signal: false, isNew: false, actions: [] as AttentionAction[] }
      const at = (n.since as string) ?? iso(0)
      if (n.state === 'stalled') items.push({ ...base, id: `uncertain:ses:${n.sessionId}:${at}`, kind: 'uncertain', reason: 'session_quiet', priority: 5, sentence: `«${title}» uzun süredir sessiz. Takılmış mı çalışıyor mu bilmiyorum.`, detail: null, at, confidence: 'unknown' })
      else if (n.reason === 'permission_prompt' && n.question) items.push({ ...base, id: `ask:ses:${n.sessionId}:${at}`, kind: 'ask', reason: 'session_question', priority: 1, sentence: `«${title}» yardımcısı sana bir soru sordu. Cevabı yardımcının açık olduğu pencerede yazmalısın.`, detail: clip(n.question, 240), at, confidence: 'firm' })
      else items.push({ ...base, id: `done:ses:${n.sessionId}:${at}`, kind: 'done', reason: 'session_done', priority: 4, sentence: `«${title}» yardımcısı işini bitirdi ve seni bekliyor.`, detail: null, at, confidence: 'firm' })
    }
    for (const n of now.uncertain as Record<string, any>[]) {
      const title = n.title || n.projectName || 'Ajan'
      items.push({ id: `uncertain:ses:${n.sessionId}:${n.since}`, kind: 'uncertain', reason: 'session_unsure', priority: 5, sentence: `«${title}» yardımcısının durumundan emin değilim: çalışıyor ya da senden izin bekliyor olabilir.`, detail: null, title, jobUid: null, jobId: null, sessionId: n.sessionId, projectName: n.projectName, at: n.since, confidence: 'unknown', signal: false, isNew: false, actions: [] })
    }
  }
  const kindOrder = ['ask', 'accept', 'blocked', 'done', 'uncertain']
  const list = items.filter((i) => !acked.has(i.id)).sort((a, b) => a.priority - b.priority || kindOrder.indexOf(a.kind) - kindOrder.indexOf(b.kind) || a.at.localeCompare(b.at) || a.id.localeCompare(b.id))
  list.forEach((i, k) => { i.signal = i.kind !== 'uncertain' && k < DAILY })
  const c = (k: string) => list.filter((i) => i.kind === k).length
  const counts = { ask: c('ask'), accept: c('accept'), blocked: c('blocked'), done: c('done'), uncertain: c('uncertain'), total: list.length, running: running.length }
  const parts = [counts.ask && `${counts.ask} soru`, counts.accept && `${counts.accept} kabul`, counts.blocked && `${counts.blocked} engel`, counts.done && `${counts.done} bitti`].filter(Boolean) as string[]
  const waiting = parts.length === 0 ? 'Şu an seni bekleyen bir şey yok.' : parts.join(', ') + ' seni bekliyor.'
  const tail = [counts.uncertain && `${counts.uncertain} işin durumundan emin değilim`, counts.running && `${counts.running} iş çalışıyor`].filter(Boolean) as string[]
  const t = tail.join(', ')
  const sentence = tail.length ? `${waiting} ${t.charAt(0).toLocaleUpperCase('tr')}${t.slice(1)}.` : waiting
  return { rev: 1, now: new Date().toISOString(), sentence, counts, budget: { dailyLimit: DAILY, usedToday: Math.min(DAILY, list.filter((i) => i.signal).length), suppressedToday: 0, day: new Date().toISOString().slice(0, 10) }, items: list, running }
}

// ---------------- koruma ve geri alma ----------------
const URAN = (path: string, action: string, why: string, text: string): UndoItem => ({ path, action, why, text })
/** Sunucunun cümleleri (ekranda sade() "Ajan" sözcüğünü "Yardımcı" yapar): "Ajan değiştirmişti, eski hâline döner." / "Ajan silmişti, geri gelir." / "Ajan oluşturmuştu, silinir." */
const CH = (p: string) => URAN(p, 'geri-yukle', 'ajan-degistirdi', 'Ajan değiştirmişti, eski hâline döner.')
const DEL = (p: string) => URAN(p, 'geri-yukle', 'ajan-sildi', 'Ajan silmişti, geri gelir.')
const NEW = (p: string) => URAN(p, 'sil', 'ajan-olusturdu', 'Ajan oluşturmuştu, silinir.')
const NEWDIR = (p: string) => URAN(p, 'klasor-sil', 'ajan-olusturdu', 'Ajan oluşturmuştu, boş klasör kaldırılır.')
const DELDIR = (p: string) => URAN(p, 'klasor-olustur', 'ajan-sildi', 'Ajan silmişti, boş klasör geri gelir.')

/** Küçük iş: 9 değişiklik. Büyük iş (302): 17 değişiklik, listede "N tane daha" açılır. */
const changes = (jobId: number): UndoItem[] =>
  jobId === 302
    ? [CH('src/siparis-liste.ts'), CH('src/durum-suzgeci.ts'), CH('test/siparis-liste.test.ts'), CH('package.json'), CH('README.md'), DEL('notlar.txt'), DEL('src/eski/kargo.ts'), DEL('src/eski/musteri.ts'), DEL('src/eski/yardimci.ts'), NEW('src/yeni/arama.ts'), NEW('src/yeni/siralama.ts'), NEW('src/yeni/tarih.ts'), NEW('yedek/siparis-2026-11-10.json'), NEW('yedek/siparis-2026-11-11.json'), NEW('log/calisma.txt'), NEWDIR('yedek'), NEWDIR('src/yeni')]
    : [DELDIR('bos-klasor'), CH('buyuk.bin'), CH('hesap.js'), NEW('kaynak/a.js'), NEW('kaynak/derin/b.js'), DEL('notlar.txt'), DEL('src/a.txt'), NEW('yeni.txt'), NEW('yeni-klasor/ic.txt')]

const undone = new Set<number>()
const undoPre = new Map<string, { jobId: number; items: UndoItem[] }>()
const undoLog = new Map<number, UndoRecord[]>()
let undoSeq = 1

function protection(jobId: number): Protection {
  const j = mock2State().jobs.find((x) => x.id === jobId)
  if (!j) throw new ApiError(404, 'İş bulunamadı.')
  const limits = 'Geri alınamayanlar: paket kurulumları, ağ istekleri, gönderilmiş e-postalar, çalışan sunucular ve bu klasörün dışına yazılanlar. Söz bir kasa değil, özen katmanıdır.'
  const fromTerminal = jobId === 306 // terminalden yürüyen iş: Söz başlamadan önceki hâli saklamadı
  if (fromTerminal || j.state === 'sirada' || j.state === 'taslak' || j.state === 'baslatilamadi') {
    return { jobId, level: 'yok', note: fromTerminal ? 'Bu iş terminalden yürüdüğü için başlamadan önceki hâli saklanmadı.' : 'İş henüz klasöre dokunmadı. Geri alınacak bir şey yok.', text: 'Bu işin dosya değişiklikleri geri alınamaz.', limits, canUndo: false, cannotUndoWhy: fromTerminal ? 'Başlamadan önceki hâl saklanmadı.' : 'Henüz değişiklik yok.', runs: [] }
  }
  const limited = jobId === 304
  return {
    jobId, level: limited ? 'sinirli' : 'tam', note: limited ? 'Klasör çok büyük: yalnız kod dosyalarının önceki hâli saklandı.' : null,
    text: limited ? 'Bu işin kod dosyalarındaki değişiklikler geri alınabilir.' : 'Bu işin klasördeki dosya değişiklikleri geri alınabilir.', limits, canUndo: true, cannotUndoWhy: null,
    runs: [{ runId: 1, runUid: `ornek-kosu-${jobId}`, level: limited ? 'sinirli' : 'tam', note: null, beforeSnapshotUid: `ornek-once-${jobId}`, afterSnapshotUid: `ornek-sonra-${jobId}`, started: iso(HOUR) }],
  }
}

function undoReply(uid: string, status: string, items: UndoItem[], summary: string): UndoReply {
  const n = (a: string) => items.filter((i) => i.action === a).length
  return { undoUid: uid, status, summary, restored: n('geri-yukle'), removed: n('sil'), renamed: 0, dirsCreated: n('klasor-olustur'), dirsRemoved: n('klasor-sil'), skipped: 0, items, conflicts: [], unrestorable: [], notes: [] }
}

// ---------------- tanışma ----------------
// iso() mock.ts'ten gelir (döngüsel içe alma): liste ilk kullanımda kurulur.
let scanCache: OnboardingItem[] | null = null
const scanItems = (): OnboardingItem[] => (scanCache ??= makeScanItems())
const makeScanItems = (): OnboardingItem[] => [
  { id: 'tool:ornek-arac', group: 'tools', title: 'Örnek Araç (örnek)', detail: 'Bilgisayarında çalıştırılabilir dosyası bulundu.', count: null, confidence: 'firm', confidenceText: 'kesin', state: 'suggested', stateText: 'önerildi', present: true, userValue: null, owner: 'system', evidenceCount: 1, firstSeen: iso(0) },
  { id: 'tool:hermes', group: 'tools', title: 'Hermes (örnek)', detail: 'Bu araca ait bir kayıt klasörü var.', count: null, confidence: 'probable', confidenceText: '', state: 'suggested', stateText: 'önerildi', present: true, userValue: null, owner: 'system', evidenceCount: 2, firstSeen: iso(0) },
  { id: 'browser:ornek', group: 'browsers', title: 'Örnek Tarayıcı', detail: 'Kurulu görünüyor.', count: null, confidence: 'firm', confidenceText: 'kesin', state: 'suggested', stateText: 'önerildi', present: true, userValue: null, owner: 'system', evidenceCount: 1, firstSeen: iso(0) },
]
let scanned = false
const taken: Record<string, string | undefined> = {}
const STATE_TEXT: Record<string, string> = { accepted: 'kabul ettin', dismissed: 'istemedin', suggested: 'önerildi' }
function onboardingView(): OnboardingView {
  if (!scanned) return JSON.parse(JSON.stringify(ONBOARDING_EMPTY)) as OnboardingView
  const mapped = scanItems().map((i) => ({ ...i, state: taken[i.id] ?? i.state, stateText: STATE_TEXT[taken[i.id] ?? 'suggested'] }))
  const groups = [
    { key: 'tools', title: 'Yapay zekâ araçların' },
    { key: 'browsers', title: 'Tarayıcıların' },
  ].map((g) => {
    const its = mapped.filter((i) => i.group === g.key)
    return { key: g.key, title: g.title, count: its.length, items: its }
  })
  return { notice: ONBOARDING_EMPTY.notice, total: mapped.length, pending: mapped.filter((i) => i.state === 'suggested').length, lastScan: iso(0), groups }
}

// ---------------- bitti'nin tanımı ----------------
function suggest(b: Record<string, unknown>) {
  const base = JSON.parse(JSON.stringify(DONE_SUGGEST)) as typeof DONE_SUGGEST
  const kind = String(b.kind ?? '')
  const kindQ = DONE_QUESTIONS.find((q) => q.id === 'kind')
  const label = kindQ?.options.find((o) => o.value === kind)?.label
  if (kind && kind !== 'kod') {
    base.jobKind = kind
    base.jobKindLabel = label ?? kind
    base.found = []
    base.proposals = base.proposals.filter((p) => p.template === 'human-list')
    return base
  }
  const diff: DoneProposal = {
    id: 'kod:diff-only:3', template: 'diff-only', kind: 'diff-summary', sentence: 'Yalnız şu dosyalar değişsin', level: 'B', levelLabel: 'dolaylı', who: 'Söz ölçer',
    why: 'Yardımcı yalnız kod klasöründe değişiklik yapsın. Başka yere dokunursa görürüm.', ready: true, enabledByDefault: true, values: { allowed: 'src/**' }, fill: [{ field: 'allowed', label: 'değişebilecek dosyalar', candidates: ['src/**', 'test/**'] }],
  }
  base.proposals = [base.proposals[0]!, diff, ...base.proposals.slice(1)]
  base.found = [...base.found, '2 belge dosyası']
  // Sunucu gibi: Söz'ün ölçtükleri işaretli, "Ben şunlara bakıp onaylayacağım" (düzey D) işaretsiz gelir.
  base.proposals = base.proposals.map((p) => ({ ...p, enabledByDefault: p.level !== 'D' }))
  const pid = Number(b.projectId)
  const hasTest = Number.isFinite(pid) && mock2State().checks.some((c) => c.projectId === pid && c.kind === 'command' && c.enabled)
  const out = base as typeof base & { measurable?: boolean; testless?: unknown }
  out.measurable = base.proposals.some((p) => p.level === 'A' || p.level === 'B')
  if (Number.isFinite(pid) && pid >= 100 && !hasTest) {
    base.proposals = base.proposals.filter((p) => p.template !== 'tests')
    base.found = []
    out.measurable = base.proposals.some((p) => p.level === 'B')
    out.testless = { nothingToTry: false, message: 'Bu klasörde işin doğruluğunu sınayan bir test yok.', action: 'write-check-first', actionLabel: 'Yardımcıya önce küçük bir deneme yazdır', startField: 'writeCheckFirst' }
  }
  if (b.irreversible === true) base.proposals = base.proposals.map((p) => (p.template === 'human-list' ? { ...p, values: { items: p.values.items + '\nGeri alamayacağım bir şey (para, e-posta, silme, yayın) olmadı' } } : p))
  return base
}

// ---------------- yönlendirici ----------------
export async function mock5Handle(method: string, p: string, q: URLSearchParams, b0: unknown): Promise<unknown> {
  init()
  const b = (b0 ?? {}) as Record<string, unknown>
  let m: RegExpMatchArray | null

  // u3: özet, kapsam, ajan hazırlığı, sabah, öneriler
  const r6 = await mock6Handle(method, p, q, b0)
  if (r6 !== NOT_HANDLED) return r6

  // ayarlar
  if (p === '/api/settings') {
    if (method === 'PUT') {
      for (const [k, v] of Object.entries(b)) validate(k, v)
      for (const [k, v] of Object.entries(b)) {
        values[k] = v
        ;(oldSettings as unknown as Record<string, unknown>)[k] = v
        adjusted = (adjusted ?? []).filter((a) => (typeof a === 'string' ? a : a.key) !== k)
      }
      emitLive('attention')
    }
    return settingsReply()
  }
  if (p === '/api/registry' && method === 'GET') {
    const kind = q.get('kind')
    if (kind && !registry.some((e) => e.kind === kind)) {
      if (!['setting', 'connector', 'check-kind', 'guard-template', 'theme', 'capsule-behavior', 'notify-channel', 'shortcut', 'palette-command', 'extractor', 'reader', 'job-kind', 'mode'].includes(kind)) throw new ApiError(400, 'Bilinmeyen tür.')
    }
    const entries = kind ? registry.filter((e) => e.kind === kind) : registry
    return { kind: kind ?? null, count: entries.length, entries, problems: [] }
  }
  if ((p === '/api/registry/approve' || p === '/api/registry/revoke') && method === 'POST') {
    const e = registry.find((x) => x.id === b.id)
    if (!e) throw new ApiError(404, 'Böyle bir kayıt yok.')
    const to = p.endsWith('approve') ? 'enabled' : 'suggested'
    const changed = e.state !== to
    e.state = to
    e.awaitsApproval = false
    return { id: e.id, kind: e.kind, state: e.state, origin: 'file', changed }
  }

  // Şimdi (oturum alanları)
  if (p === '/api/now') return sessionsNow().then(async (n) => ({ ...((await mock2Handle('GET', p, q, b)) as object), needsYou: n.needsYou, uncertain: n.uncertain }))

  // Sıra bende
  if (p === '/api/attention' && method === 'GET') return attention()
  if (p === '/api/attention/ack' && method === 'POST') {
    const snap = await attention()
    const it = snap.items.find((i) => i.id === b.id)
    if (!it) throw new ApiError(404, 'Bu öğe artık listede değil. Liste kendiliğinden güncellenmiş olabilir.')
    if (it.jobId == null) throw new ApiError(409, 'Bu bildirim ajanın açık olduğu pencerede kapanır. Buradan "gördüm" denemez.')
    if (!['passed', 'proof_failed', 'start_failed', 'unproven'].includes(it.reason)) throw new ApiError(409, it.kind === 'ask' || it.kind === 'accept' ? 'Bu iş senden bir cevap ya da karar bekliyor. Önce onu ver.' : 'Bu öğe "gördüm" ile kapanmaz. Durum değişince kendiliğinden kapanır.')
    acked.add(String(b.id))
    emitLive('attention')
    return { ok: true }
  }

  // bitti'nin tanımı
  if (p === '/api/done/questions') return { questions: DONE_QUESTIONS }
  if (p === '/api/done/levels') return DONE_LEVELS
  if (p === '/api/done/suggest' && method === 'POST') {
    await sleep(220)
    return suggest(b)
  }
  if (p === '/api/done/accept' && method === 'POST') {
    const projectId = Number(b.projectId)
    const list = (Array.isArray(b.items) ? b.items : []) as { template: string; values: Record<string, string>; enabled: boolean }[]
    const created: unknown[] = []
    for (const it of list) {
      const sug = suggest({}).proposals.find((x) => x.template === it.template)
      const label = sug ? (it.values.allowed ? `${sug.sentence}: ${it.values.allowed}` : sug.sentence) : it.template
      const kind = it.template === 'tests' ? 'command' : it.template === 'diff-only' ? 'diff-summary' : 'human-checklist'
      const spec = it.template === 'tests' ? { cmd: 'npm test', expectExit: 0 } : { ...it.values }
      const c = (await mock2Handle('POST', '/api/checks', new URLSearchParams(), { projectId, kind, label, spec, enabled: it.enabled })) as Record<string, unknown>
      created.push({ ...c, origin: 'user', uid: `ornek-olcut-${c.id}` })
    }
    return { created, count: created.length }
  }

  // koruma ve geri alma
  m = p.match(/^\/api\/jobs\/(\d+)\/protection$/)
  if (m && method === 'GET') return protection(Number(m[1]))
  m = p.match(/^\/api\/jobs\/(\d+)\/undo\/preview$/)
  if (m && method === 'POST') {
    const id = Number(m[1])
    if (!protection(id).canUndo) throw new ApiError(409, 'Bu işin geri alınabilir bir değişikliği yok.')
    await sleep(160)
    const items = undone.has(id) ? [] : changes(id)
    const uid = `ornek-geri-${undoSeq++}`
    undoPre.set(uid, { jobId: id, items })
    return undoReply(uid, 'previewed', items, `Geri alınırsa ${items.length} değişiklik yapılır.`)
  }
  m = p.match(/^\/api\/jobs\/(\d+)\/undo$/)
  if (m && method === 'POST') {
    const id = Number(m[1])
    const pre = undoPre.get(String(b.previewUid))
    if (!pre || pre.jobId !== id) throw new ApiError(404, 'Bu önizleme artık geçerli değil. Yeniden önizle.')
    await sleep(300)
    undone.add(id)
    const rec: UndoRecord = { uid: pre.items.length ? `ornek-kayit-${undoSeq++}` : 'ornek-bos', jobId: id, runId: 1, mode: 'all', status: 'applied', startedAt: iso(0), finishedAt: iso(0), filesRestored: pre.items.filter((i) => i.action === 'geri-yukle').length, filesRemoved: pre.items.filter((i) => i.action === 'sil').length, filesSkipped: 0, conflicts: 0, error: null }
    undoLog.set(id, [rec, ...(undoLog.get(id) ?? [])])
    undoPre.set(rec.uid, pre)
    emitLive('job', { id })
    return undoReply(rec.uid, 'applied', pre.items, `Geri alındı: ${pre.items.length} değişiklik yapıldı.`)
  }
  m = p.match(/^\/api\/jobs\/(\d+)\/undo$/)
  if (m && method === 'GET') return { undo: undoLog.get(Number(m[1])) ?? [] }
  m = p.match(/^\/api\/undo\/([^/]+)\/revert$/)
  if (m && method === 'POST') {
    const pre = undoPre.get(m[1]!)
    if (!pre) throw new ApiError(404, 'Böyle bir geri alma kaydı yok.')
    await sleep(300)
    undone.delete(pre.jobId)
    const list = undoLog.get(pre.jobId) ?? []
    const rec = list.find((r) => r.uid === m![1])
    if (rec) rec.status = 'reverted'
    emitLive('job', { id: pre.jobId })
    return undoReply(m[1]!, 'reverted', pre.items, `Geri alma geri alındı: ${pre.items.length} değişiklik yapıldı.`)
  }

  // tanışma
  if (p === '/api/onboarding' && method === 'GET') return onboardingView()
  if (p === '/api/onboarding/scan' && method === 'POST') {
    await sleep(700)
    scanned = true
    return { outcome: { found: scanItems().length, new: scanItems().length, updated: 0, unchanged: 0, gone: 0, keptDismissed: 0, droppedAsSecret: 0, unreadable: 1, couldNotRead: ['örnek klasör'], ms: 12 }, view: onboardingView() }
  }
  if (p === '/api/onboarding/why' && method === 'GET') {
    const id = q.get('id')
    if (!id) throw new ApiError(400, 'Hangi madde olduğu (id) gerekli.')
    const it = scanItems().find((i) => i.id === id)
    return { id, title: it?.title ?? id, confidenceText: it?.confidenceText ?? null, answer: it?.group === 'tools' ? 'Bilgisayarında bu aracın çalıştırılabilir dosyası ya da kayıt klasörü bulundu.' : 'Bu program bilgisayarında kurulu görünüyor.', evidence: [{ what: 'dosya adı', where: 'C:\\Ornek\\yol', when: null }] }
  }
  if ((p === '/api/onboarding/accept' || p === '/api/onboarding/dismiss' || p === '/api/onboarding/restore') && method === 'POST') {
    const id = String(b.id)
    const it = scanItems().find((i) => i.id === id)
    if (!it) throw new ApiError(404, 'Böyle bir madde yok.')
    taken[id] = p.endsWith('accept') ? 'accepted' : p.endsWith('dismiss') ? 'dismissed' : undefined
    const hermes = p.endsWith('accept') && id === 'tool:hermes'
    if (hermes) {
      const c = registry.find((e) => e.id === 'connector:hermes')
      if (c) c.state = 'enabled'
    }
    return { ...it, state: taken[id] ?? 'suggested', stateText: STATE_TEXT[taken[id] ?? 'suggested'], ...(hermes ? { connector: { id: 'connector:hermes', state: 'enabled' } } : {}) }
  }
  if (p === '/api/profile/scan' && method === 'POST') {
    await sleep(300)
    scanned = true
    return { ok: true, scannedAt: iso(0) }
  }
  if (p === '/api/profile' && method === 'GET') {
    const real = projects.filter((x) => x.path)
    return {
      machine: { memoryBytes: 34_359_738_368, cpuName: 'AMD Ryzen 7 7840HS', physicalCores: 8, logicalCores: 16, gpuName: null, disks: [{ letter: 'C:\\', sizeBytes: 512_000_000_000, freeBytes: 184_000_000_000 }, { letter: 'D:\\', sizeBytes: 1_000_000_000_000, freeBytes: 420_000_000_000 }] },
      projects: emptyMode ? [] : real.map((pr, idx) => ({
        name: pr.name,
        path: pr.path!,
        displayHint: pr.path!,
        drive: pr.path!.slice(0, 2),
        missingFolder: false,
        sessionsByTool: idx === 0 ? { claude: 8, antigravity: 3, codex: 2 } : { claude: 4, vscode: 2 },
        firstActivity: iso(20 * HOUR * (idx + 2)),
        lastActivity: pr.lastAt,
        sessions7Days: 6 - idx,
        sessions30Days: 14 - idx * 2,
        hasGit: true,
        hasTests: idx < 2,
        rootSessions: 8,
        subagentSessions: 2,
        areas: [],
        soluk: false,
        status: idx < 2 ? 'etkin' : 'yasayan-sistem',
        lastQuietDays: idx,
      })),
      unassigned: { count: 0, reasons: {}, sessions7Days: 0, sessions30Days: 0 },
      live: emptyMode ? [] : [
        {
          projectPath: 'D:\\projeler\\bahce-takip',
          sessions: [
            { tool: 'claude', id: 'ornek-canli-1', lastActivity: iso(MIN), level: 'yaziyor', acikAdayi: true, title: 'Bitki listesi son sulama tarihi', canFocus: true, pid: 1001 },
            { tool: 'antigravity', id: 'ornek-canli-2', lastActivity: iso(4 * MIN), level: 'sicak', acikAdayi: true, title: 'Bahçe arayüzü düzeni', canFocus: true, pid: 1002 },
          ],
          relations: [],
        },
        {
          projectPath: 'D:\\projeler\\atolye-siparis',
          sessions: [
            { tool: 'vscode', id: 'ornek-canli-3', lastActivity: iso(12 * MIN), level: 'sicak', acikAdayi: true, title: 'Sipariş tablosu', canFocus: true, pid: 1003 },
          ],
          relations: [],
        },
      ],
      counts: { sessions: emptyMode ? 0 : 28, projects: emptyMode ? 0 : real.length, facts: 6 },
      scanMs: 42,
      sources: [
        { tool: 'claude', okunan: 18, okunamayan: 0, sessions: 18, observedAt: iso(MIN) },
        { tool: 'codex', okunan: 6, okunamayan: 0, sessions: 6, observedAt: iso(MIN) },
        { tool: 'antigravity', okunan: 4, okunamayan: 0, sessions: 4, observedAt: iso(MIN) },
      ],
      scannedAt: iso(MIN),
      warnings: [],
    }
  }
  if (p === '/api/profile/sessions' && method === 'GET') {
    const project = q.get('project') ?? ''
    return {
      project,
      limit: Number(q.get('limit') ?? 20),
      total: 2,
      count: 2,
      sessions: [
        { tool: 'claude', id: 's-1', cwd: project, gitBranch: 'main', startedAt: iso(2 * HOUR), lastActivity: iso(15 * MIN), parentId: null, title: 'Son düzenlemeler', sourceChannel: 'cli' },
        { tool: 'antigravity', id: 's-2', cwd: project, gitBranch: 'main', startedAt: iso(5 * HOUR), lastActivity: iso(3 * HOUR), parentId: null, title: 'Arayüz kontrolü', sourceChannel: 'ide' },
      ],
    }
  }
  if ((p === '/api/live/focus' || p === '/api/live/open-folder') && method === 'POST') {
    return { ok: true }
  }
  return NOT_HANDLED
}

