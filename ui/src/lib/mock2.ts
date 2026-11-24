// Söz 2 sahte katmanı: iş, ölçüt, kanıt, bekçi, kuyruk, bugün, sabah, harcama, telefon.
// Bütün isimler, komutlar ve çıktılar TAMAMEN UYDURMADIR. Yalnız npm run dev / VITE_MOCK=1 / ?ornek=1 / ?bos=1 ile çalışır.
import { ApiError } from './errors'
import { emitLive } from './live'
import { DAY, decisions, emptyMode, isImported, HOUR, iso, MIN, pname, projects, sessionsData, settings, sleep, viewSession } from './mock'
import type {
  Check,
  CheckTemplate,
  CheckTemplates,
  GuardEvent,
  GuardRule,
  Job,
  HooksHealth,
  JobDetail,
  JobState,
  Morning,
  Now,
  PhonePairing,
  ProofResult,
  ProofRun,
  QueueItem,
  GuardTestResult,
  TokenSet,
  UsageRaw,
  Verdict,
} from './types'

export const NOT_HANDLED = Symbol('mock2-not-handled')

const unpriced = typeof location !== 'undefined' && /[?&]fiyatsiz=1/.test(location.search)
// ?kancasorun=1: kanca sağlığı bozuk görünür (kanıt kapısı çalışmıyor uyarısını denemek için).
const hooksBroken = typeof location !== 'undefined' && /[?&]kancasorun=1/.test(location.search)

// ---- yardımcılar ----
function shotSvg(title: string, tone: 'ok' | 'bad'): string {
  const c = tone === 'ok' ? '#2f7d55' : '#b0251c'
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="640" height="400" viewBox="0 0 640 400">` +
    `<rect width="640" height="400" fill="#f4f5f2"/><rect x="0" y="0" width="640" height="44" fill="#131e27"/>` +
    `<rect x="24" y="14" width="120" height="16" rx="8" fill="#f4f5f2" opacity=".85"/>` +
    `<rect x="40" y="76" width="360" height="26" rx="6" fill="#131e27"/>` +
    `<rect x="40" y="118" width="520" height="12" rx="6" fill="#98a6b0"/><rect x="40" y="142" width="470" height="12" rx="6" fill="#98a6b0"/>` +
    `<rect x="40" y="190" width="160" height="110" rx="10" fill="#fff" stroke="#c8cec9"/><rect x="224" y="190" width="160" height="110" rx="10" fill="#fff" stroke="#c8cec9"/>` +
    `<rect x="408" y="190" width="160" height="110" rx="10" fill="#fff" stroke="#c8cec9"/>` +
    `<rect x="56" y="206" width="90" height="12" rx="6" fill="${c}"/><rect x="240" y="206" width="70" height="12" rx="6" fill="${c}"/>` +
    `<text x="40" y="360" font-family="Segoe UI, sans-serif" font-size="18" fill="#3f4d58">${title} (örnek görüntü)</text></svg>`
  return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg)
}

// ---- durum (ilk çağrıda kurulur) ----
let ready = false
let nextId = 1000
let checks: Check[] = []
let jobs: Job[] = []
let runs: ProofRun[] = []
let rules: GuardRule[] = []
let events: GuardEvent[] = []
let queue: QueueItem[] = []
let pairing: PhonePairing = { enabled: false, listening: false, url: null, addresses: [], error: null }
const running = new Set<number>() // proje kimlikleri

function chk(id: number, projectId: number, kind: Check['kind'], label: string, spec: Record<string, unknown>, enabled: boolean, origin: Check['origin'] = 'auto'): Check {
  return { id, projectId, jobId: null, kind, label, spec, timeoutS: 120, enabled, origin, createdAt: iso(3 * DAY) }
}

function res(runId: number, n: number, checkId: number, label: string, status: ProofResult['status'], ms: number, tail: string | null, exit: number | null, art: string | null = null): ProofResult {
  return { id: runId * 10 + n, runId, checkId, label, status, exitCode: exit, outputTail: tail, artifactPath: art ? `ekran-${runId}.png` : null, artifactUrl: art, durationMs: ms }
}

function init() {
  if (ready) return
  ready = true
  if (emptyMode) {
    pairing = { enabled: false, listening: false, url: null, addresses: [], error: null }
    return
  }
  checks = [
    chk(1, 2, 'command', 'Testler geçsin', { cmd: 'npm test', expectExit: 0 }, true),
    chk(2, 2, 'command', 'Derleme hatasız bitsin', { cmd: 'npm run build', expectExit: 0 }, true),
    chk(3, 2, 'url', 'Ana sayfa açılsın', { url: 'http://localhost:5173/', expectStatus: 200 }, true, 'user'),
    chk(4, 2, 'screenshot', 'Ana sayfanın ekran görüntüsü alınsın', { url: 'http://localhost:5173/', width: 1280, height: 800 }, true, 'user'),
    chk(5, 3, 'command', 'Testler geçsin', { cmd: 'dotnet test', expectExit: 0 }, true),
    chk(6, 3, 'command', 'Derleme hatasız bitsin', { cmd: 'dotnet build', expectExit: 0 }, false),
    chk(7, 4, 'command', 'Testler geçsin', { cmd: 'pytest -q', expectExit: 0 }, true),
  ]
  const failTail =
    ' FAIL  test/siparis-liste.test.ts\n  ✕ durum süzgeci "kargoda" olanları getirir (12 ms)\n    beklenen 3 satır, gelen 0\n  ✕ tarihe göre sıralama en yeniyi üste koyar (4 ms)\n\nTests: 2 failed, 17 passed, 19 total'
  const failTail2 = ' FAIL  test/siparis-liste.test.ts\n  ✕ durum süzgeci "kargoda" olanları getirir (11 ms)\n    beklenen 3 satır, gelen 0\n\nTests: 1 failed, 18 passed, 19 total'
  runs = [
    // iş 301: 1. deneme kaldı, 2. deneme geçti
    { id: 11, projectId: 2, jobId: 301, sessionId: 201, trigger: 'stop_hook', attempt: 1, startedAt: iso(2 * HOUR + 14 * MIN), finishedAt: iso(2 * HOUR + 12 * MIN), verdict: 'failed', summary: '3 ölçütten 1’i kaldı: Testler geçsin',
      results: [
        res(11, 1, 1, 'Testler geçsin', 'failed', 8200, 'Tests: 1 failed, 11 passed, 12 total\n  ✕ sulama saati sabah sekizi aşmaz (3 ms)\n    beklenen 08:00, gelen 20:00', 1),
        res(11, 2, 2, 'Derleme hatasız bitsin', 'passed', 6100, 'built in 5.9s', 0),
        res(11, 3, 3, 'Ana sayfa açılsın', 'passed', 220, 'HTTP 200', null),
      ] },
    { id: 12, projectId: 2, jobId: 301, sessionId: 201, trigger: 'stop_hook', attempt: 2, startedAt: iso(2 * HOUR + 3 * MIN), finishedAt: iso(2 * HOUR + 1 * MIN), verdict: 'passed', summary: '4 ölçütün hepsi geçti',
      results: [
        res(12, 1, 1, 'Testler geçsin', 'passed', 7900, 'Tests: 12 passed, 12 total', 0),
        res(12, 2, 2, 'Derleme hatasız bitsin', 'passed', 6000, 'built in 5.8s', 0),
        res(12, 3, 3, 'Ana sayfa açılsın', 'passed', 190, 'HTTP 200', null),
        res(12, 4, 4, 'Ana sayfanın ekran görüntüsü alınsın', 'passed', 1900, 'sayfa açıldı: 1280x800', null, shotSvg('Bahçe Takip ana sayfa', 'ok')),
      ] },
    // iş 302: üç turda da kaldı
    { id: 21, projectId: 3, jobId: 302, sessionId: 202, trigger: 'stop_hook', attempt: 1, startedAt: iso(5 * HOUR + 30 * MIN), finishedAt: iso(5 * HOUR + 28 * MIN), verdict: 'failed', summary: '1 ölçütten 1’i kaldı: Testler geçsin',
      results: [res(21, 1, 5, 'Testler geçsin', 'failed', 14300, failTail, 1)] },
    { id: 22, projectId: 3, jobId: 302, sessionId: 202, trigger: 'stop_hook', attempt: 2, startedAt: iso(5 * HOUR + 12 * MIN), finishedAt: iso(5 * HOUR + 10 * MIN), verdict: 'failed', summary: '1 ölçütten 1’i kaldı: Testler geçsin',
      results: [res(22, 1, 5, 'Testler geçsin', 'failed', 14100, failTail2, 1)] },
    { id: 23, projectId: 3, jobId: 302, sessionId: 202, trigger: 'stop_hook', attempt: 3, startedAt: iso(4 * HOUR + 50 * MIN), finishedAt: iso(4 * HOUR + 48 * MIN), verdict: 'failed', summary: '1 ölçütten 1’i kaldı: Testler geçsin',
      results: [res(23, 1, 5, 'Testler geçsin', 'failed', 14500, failTail2, 1)] },
    // iş 303: ilk denemede geçti
    { id: 31, projectId: 4, jobId: 303, sessionId: 203, trigger: 'stop_hook', attempt: 1, startedAt: iso(5 * HOUR + 2 * MIN), finishedAt: iso(5 * HOUR + 1 * MIN), verdict: 'passed', summary: '1 ölçütün hepsi geçti',
      results: [res(31, 1, 7, 'Testler geçsin', 'passed', 3100, '14 passed in 2.8s', 0)] },
    // iş 304: ölçüt yok
    { id: 41, projectId: 5, jobId: 304, sessionId: 206, trigger: 'stop_hook', attempt: 1, startedAt: iso(40 * MIN), finishedAt: iso(40 * MIN), verdict: 'unchecked', summary: 'Kontrol edilecek ölçüt tanımlı değil',
      results: [] },
  ]
  const j = (id: number, projectId: number, state: JobState, raw: string, ago: number, extra: Partial<Job> = {}): Job => ({
    id, projectId, projectName: pname(projectId), sessionId: null, title: raw.length > 70 ? raw.slice(0, 67) + '…' : raw, rawText: raw,
    promptText: `${raw}\n\nBitti ölçütleri (Söz bu ölçütleri kendisi çalıştıracak):\n- Testler geçsin`, source: 'yazi', state, night: false, restate: false,
    createdAt: iso(ago), givenAt: state === 'taslak' ? null : iso(ago - 2 * MIN), finishedAt: null, ...extra,
  })
  jobs = [
    j(301, 2, 'gecti', 'Sulama hatırlatmaları sabah sekizde gelsin, akşam hiç gelmesin. Ana sayfa da açılsın.', 3 * HOUR, { sessionId: 201, lastRunId: 12, finishedAt: iso(2 * HOUR), lastVerdict: 'passed', attempts: 2, maxAttempts: 3 }),
    j(302, 3, 'kaldi', 'Sipariş listesinde duruma göre süzme ve tarihe göre sıralama olsun.', 6 * HOUR, { sessionId: 202, lastRunId: 23, finishedAt: iso(4 * HOUR + 48 * MIN), lastVerdict: 'failed', attempts: 3, maxAttempts: 3, failedLabels: ['Testler geçsin'], night: true }),
    j(303, 4, 'gecti', 'Kategori toplamları yanlış çıkıyor, market ile fatura ayrı toplansın.', 5 * HOUR + 20 * MIN, { sessionId: 203, lastRunId: 31, finishedAt: iso(5 * HOUR), lastVerdict: 'passed', attempts: 1, maxAttempts: 3, restate: true }),
    j(304, 5, 'verildi', 'Bu haftanın bülteni için üç kısa başlık öner, sonra metni yaz.', 55 * MIN, { sessionId: 206, lastRunId: 41, lastVerdict: 'unchecked', attempts: 1, maxAttempts: 3 }),
    j(305, 2, 'calisiyor', 'Bitki listesine her bitkinin son sulama tarihini ekle.', 12 * MIN, { sessionId: 207, lastVerdict: null, attempts: 0, maxAttempts: 3 }),
    j(306, 3, 'verildi', 'Müşteri listesini dışa aktarırken telefon sütununu çıkar.', 7 * HOUR, { sessionId: 205, night: true, lastVerdict: null, attempts: 0, maxAttempts: 3, source: 'dikte' }),
  ]
  rules = [
    { id: 1, projectId: 3, kind: 'scope_only', label: 'Proje klasörünün dışına hiçbir şey yazma', spec: {}, action: 'deny', enabled: true, decisionId: null, origin: 'template', createdAt: iso(4 * DAY) },
    { id: 2, projectId: 3, kind: 'ask_before', label: 'Toplu silmeden önce sor', spec: { patterns: ['rm -rf', 'Remove-Item -Recurse', 'del /s'] }, action: 'ask', enabled: true, decisionId: null, origin: 'template', createdAt: iso(4 * DAY) },
    { id: 3, projectId: 3, kind: 'ask_before', label: 'Eski kaydın üstüne yazmadan önce sor', spec: { patterns: ['git push --force', 'git reset --hard'] }, action: 'ask', enabled: true, decisionId: null, origin: 'template', createdAt: iso(4 * DAY) },
    { id: 4, projectId: 3, kind: 'ask_before', label: 'Yayın yapmadan önce sor', spec: { patterns: ['npm publish', 'gh release', 'vercel --prod'] }, action: 'ask', enabled: false, decisionId: null, origin: 'template', createdAt: iso(4 * DAY) },
    { id: 5, projectId: 3, kind: 'path_protect', label: 'Müşteri telefon dosyalarına dokunma', spec: { globs: ['**/musteri-telefon*'] }, action: 'deny', enabled: true, decisionId: 11, origin: 'defter', createdAt: iso(2 * DAY) },
  ]
  events = [
    { id: 1, at: iso(26 * MIN), projectId: 3, projectName: 'Atölye Sipariş', tool: 'Bash', targetSummary: 'rm -rf siparis-arsiv/2025', ruleId: 2, ruleLabel: 'Toplu silmeden önce sor', action: 'ask', reason: 'Toplu silme komutu: önce senin onayın gerekir.' },
    { id: 2, at: iso(3 * HOUR), projectId: 3, projectName: 'Atölye Sipariş', tool: 'Write', targetSummary: 'D:\\projeler\\baska-proje\\notlar.txt', ruleId: 1, ruleLabel: 'Proje klasörünün dışına hiçbir şey yazma', action: 'deny', reason: 'Hedef proje klasörünün dışında.' },
    { id: 3, at: iso(9 * HOUR), projectId: 3, projectName: 'Atölye Sipariş', tool: 'Edit', targetSummary: 'veri\\musteri-telefon.csv', ruleId: 5, ruleLabel: 'Müşteri telefon dosyalarına dokunma', action: 'deny', reason: 'Korunan yol: Müşteri telefonlarını günlüğe yazma.' },
    { id: 4, at: iso(1 * DAY + 2 * HOUR), projectId: 3, projectName: 'Atölye Sipariş', tool: 'Bash', targetSummary: 'git push --force origin main', ruleId: 3, ruleLabel: 'Eski kaydın üstüne yazmadan önce sor', action: 'ask', reason: 'Eski kaydın üstüne yazma: geçmişi bozabilir.' },
  ]
  queue = [
    { id: 1, projectId: 2, sessionId: 201, text: 'Hafta sonu da gönderilsin, ama yalnız sabah sekizde.', createdAt: iso(8 * MIN), deliveredAt: null, origin: 'panel' },
  ]
}

// ---- yardımcı hesaplar ----
function enabledChecks(projectId: number, jobId: number | null): Check[] {
  return checks.filter((c) => c.projectId === projectId && c.enabled && (c.jobId === null || c.jobId === jobId))
}

const summaryOf = (r: ProofRun): ProofRun => {
  const { results, ...rest } = r
  const rs = results ?? []
  return {
    ...rest,
    projectName: pname(r.projectId),
    jobTitle: jobs.find((j) => j.id === r.jobId)?.title,
    counts: { passed: rs.filter((x) => x.status === 'passed').length, failed: rs.filter((x) => x.status === 'failed').length, skipped: rs.filter((x) => x.status === 'skipped').length, error: rs.filter((x) => x.status === 'error').length },
    claim: r.trigger === 'stop_hook' ? { claimsDone: true, showsEvidence: false, label: `bitti dedi / deneme sonucu: ${r.verdict === 'passed' ? 'geçti' : r.verdict === 'failed' ? 'olmadı' : 'kontrol yok'}` } : null,
  }
}

const empty = () => emptyMode && !isImported()

function now(): Now {
  const human = empty() ? [] : sessionsData.filter((s) => s.kind === 'human')
  const needsYou = human
    .filter((s) => s.state === 'waiting' || s.state === 'stalled')
    .map((s) => ({
      sessionId: s.id, tool: s.tool, projectId: s.projectId, projectName: pname(s.projectId), title: s.title, state: s.state as 'waiting' | 'stalled', since: iso(s.sinceAgo),
      reason: s.asksQuestion ? 'permission_prompt' : null, question: s.lastAssistantText ? s.lastAssistantText.slice(0, 200) : null, pendingQueue: queue.filter((q) => q.sessionId === s.id && !q.deliveredAt).length,
    }))
  const finished = runs.filter((r) => r.finishedAt).sort((a, b) => Date.parse(b.finishedAt!) - Date.parse(a.finishedAt!))
  return {
    sentence: '',
    needsYou,
    runningJobs: jobs.filter((j) => j.state === 'calisiyor' || (j.state === 'verildi' && !runs.some((r) => r.jobId === j.id))),
    recentVerdicts: finished.map(summaryOf),
    counts: {
      jobsPassed: jobs.filter((j) => j.lastVerdict === 'passed').length, jobsFailed: jobs.filter((j) => j.lastVerdict === 'failed').length, jobsRunning: jobs.filter((j) => (j.state === 'calisiyor' && (!j.run || j.run.status === 'running')) || (j.state === 'verildi' && !j.run && !j.lastVerdict)).length,
      waiting: jobs.filter((j) => ['cevap_bekliyor', 'karar_bekliyor', 'onay_bekliyor'].includes(j.state)).length, queued: jobs.filter((j) => j.state === 'sirada').length, working: human.filter((s) => s.state === 'working').length, stalled: needsYou.filter((s) => s.state === 'stalled').length,
      queuePending: queue.filter((q) => !q.deliveredAt).length, guardBlocked: events.length,
    },
  }
}

function morning(): Morning {
  const z = empty()
  const ent = (j: Job) => ({ job: { id: j.id, title: j.title, projectName: pname(j.projectId) }, runId: j.lastRunId ?? undefined, attempts: j.attempts ?? 1, failedChecks: j.failedLabels ?? [], summary: undefined })
  const human = z ? [] : sessionsData.filter((s) => s.kind === 'human')
  return {
    since: iso(16 * HOUR), until: iso(0), sentence: '',
    passed: jobs.filter((j) => j.lastVerdict === 'passed').map(ent),
    failed: jobs.filter((j) => j.lastVerdict === 'failed').map(ent),
    unverified: jobs.filter((j) => j.lastVerdict === 'unchecked').map((j) => ({ ...ent(j), verdict: 'unchecked' as const })),
    waitingForYou: human.filter((s) => s.state === 'waiting').map((s) => ({ sessionId: s.id, projectName: pname(s.projectId), title: s.title, since: iso(s.sinceAgo), reason: null })),
    stuck: human.filter((s) => s.state === 'stalled').map((s) => ({ sessionId: s.id, projectName: pname(s.projectId), title: s.title, since: iso(s.sinceAgo) })),
    guardBlocked: events.length,
    usage: z ? { messages: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0 } : { messages: 214, inputTokens: 1_240_000, outputTokens: 310_000, cacheReadTokens: 1_930_000, cacheWriteTokens: 0 },
  }
}

function tokenSet(fresh: number, msgs: number): TokenSet {
  const out = Math.round(fresh * 0.1)
  const cw = Math.round(fresh * 0.25)
  const inp = fresh - out - cw
  return { messages: msgs, inputTokens: inp, outputTokens: out, cacheReadTokens: fresh * 6, cacheWriteTokens: cw, totalTokens: fresh * 7, freshTokens: fresh }
}

/** Gerçek /api/usage biçimi (API-FARKLARI-2B). */
function usage(days: number): UsageRaw {
  const now = Date.now()
  const rows = Array.from({ length: days }, (_, i) => {
    const d = new Date(now - (days - 1 - i) * DAY)
    const wave = 0.55 + 0.45 * Math.abs(Math.sin(i * 1.7 + 0.6))
    const main = empty() ? 0 : Math.round(420_000 * wave * (i === days - 1 ? 0.7 : 1))
    const sub = Math.round(main * (0.25 + 0.2 * Math.abs(Math.cos(i * 0.9))))
    return { day: d.toISOString().slice(0, 10), main, sub }
  })
  const fresh = rows.reduce((a, d) => a + d.main + d.sub, 0)
  const subFresh = rows.reduce((a, d) => a + d.sub, 0)
  const msgs = Math.round(fresh / 11_000)
  const parts = [['claude', 'claude-opus-5-5', 0.46, 5.2], ['claude', 'claude-sonnet-5-5', 0.34, 2.7], ['claude', 'claude-haiku-5-5', 0.08, 0.4], ['codex', 'gpt-5-codex', 0.12, 2.1]] as const
  const cost = (f: number, price: number) => (unpriced ? null : +((f / 1_000_000) * price).toFixed(2))
  return {
    days, since: rows[0].day, until: rows[rows.length - 1].day, tool: null, dayBasis: 'UTC',
    totals: tokenSet(fresh, msgs),
    subagent: { tokens: tokenSet(subFresh, Math.round(msgs / 3)), sharePercent: +((subFresh / Math.max(1, fresh)) * 100).toFixed(1), sharePercentOfAllTokens: 6.8, sharePercentOfMessages: 33.3, basis: 'sharePercent = yeni işlenen token içindeki pay' },
    estimatedCostUsd: unpriced ? null : +rows.reduce((a, d) => a + ((d.main + d.sub) / 1_000_000) * 3.1, 0).toFixed(2),
    pricing: unpriced
      ? { configured: false, note: 'fiyat girilmedi', unpricedModels: parts.map((p) => p[1]), disclaimer: 'API eşdeğeri tahmini. Abonelikte gerçek fatura DEĞİLDİR.' }
      : { configured: true, note: '', unpricedModels: [], disclaimer: 'API eşdeğeri tahmini. Abonelikte gerçek fatura DEĞİLDİR.' },
    byDay: rows.map((d) => ({ day: d.day, tokens: tokenSet(d.main + d.sub, Math.round((d.main + d.sub) / 11_000)), subagentTokens: tokenSet(d.sub, Math.round(d.sub / 11_000)), costUsd: unpriced ? null : +(((d.main + d.sub) / 1_000_000) * 3.1).toFixed(2) })),
    byModel: parts.map(([tool, model, share, price]) => ({ tool, model, tokens: tokenSet(Math.round(fresh * share), Math.round(msgs * share)), sharePercent: +(share * 100).toFixed(1), subagentSharePercent: 20, costUsd: cost(fresh * share, price), priced: !unpriced })),
  }
}

// ---- bekçi değerlendirici (kuru deneme) ----
function globRe(g: string): RegExp {
  const e = g.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*\*/g, '§§').replace(/\*/g, '[^\\\\/]*').replace(/§§/g, '.*')
  return new RegExp(e, 'i')
}

function guardTest(projectId: number, tool: string, input: Record<string, unknown> | string): GuardTestResult {
  const proj = projects.find((p) => p.id === projectId)
  const rs = rules.filter((r) => r.enabled && (r.projectId === projectId || r.projectId === null))
  const o = typeof input === 'string' ? { command: input, file_path: input } : input
  const cmd = String(o.command ?? '')
  const path = String(o.file_path ?? o.path ?? '')
  const res = (r: GuardRule, reason: string, targets: string[]): GuardTestResult => ({ verdict: r.action, ruleId: r.id, ruleLabel: r.label, ruleKind: r.kind, reason, unknown: false, targets, note: '' })
  for (const r of rs) {
    if (r.kind === 'scope_only' && path && proj?.path && !path.toLowerCase().startsWith(proj.path.toLowerCase())) {
      return res(r, 'Bekçi durdurdu: «' + r.label + '» kuralı. «' + path + '» proje klasörünün (' + proj.path + ') dışında.', [path])
    }
    if (r.kind === 'path_protect' && path && ((r.spec.globs as string[]) ?? []).some((g) => globRe(g).test(path))) {
      return res(r, 'Bekçi durdurdu: «' + r.label + '» kuralı. Korunan bir yola yazılıyor.', [path])
    }
    if ((r.kind === 'command_block' || r.kind === 'ask_before') && cmd && ((r.spec.patterns as string[]) ?? []).some((p) => cmd.toLowerCase().includes(p.split('|')[0].toLowerCase()))) {
      return res(r, r.action === 'deny' ? 'Bekçi durdurdu: «' + r.label + '» kuralı.' : 'Bekçi soruyor: «' + r.label + '» kuralı. Devam edilsin mi?', [])
    }
  }
  return { verdict: 'allow', ruleId: null, ruleLabel: null, ruleKind: null, reason: null, unknown: false, targets: [], note: rs.length ? 'Hiçbir kurala uymadı.' : 'Bu proje için etkin kural yok.' }
}

const TEMPLATES: Record<string, Omit<GuardRule, 'id' | 'projectId' | 'createdAt'>[]> = {
  temkinli: [
    { kind: 'scope_only', label: 'Proje klasörünün dışına hiçbir şey yazma', spec: {}, action: 'deny', enabled: true, decisionId: null, origin: 'template' },
    { kind: 'ask_before', label: 'Toplu silmeden önce sor', spec: { patterns: ['rm -rf', 'Remove-Item -Recurse', 'del /s'] }, action: 'ask', enabled: true, decisionId: null, origin: 'template' },
    { kind: 'ask_before', label: 'Eski kaydın üstüne yazmadan önce sor', spec: { patterns: ['git push --force', 'git reset --hard'] }, action: 'ask', enabled: true, decisionId: null, origin: 'template' },
    { kind: 'ask_before', label: 'Yayın yapmadan önce sor', spec: { patterns: ['npm publish', 'gh release', 'vercel --prod'] }, action: 'ask', enabled: true, decisionId: null, origin: 'template' },
  ],
}

const GUARD_TEMPLATE_INFO: Record<string, { label: string; description: string }> = {
  temkinli: { label: 'Temkinli başlangıç', description: 'Proje dışına yazmayı durdurur. Toplu silme, eski kaydın üstüne yazma ve yayın öncesi sorar.' },
  'korunan-yollar': { label: 'Gizli dosyaları koru', description: 'Gizli anahtar ve ortam dosyalarına yazmayı durdurur. Bağımlılık kurmadan önce sorar.' },
}
TEMPLATES['korunan-yollar'] = [
  { kind: 'path_protect', label: 'Gizli anahtar dosyalarına yazma', spec: { globs: ['**/.env', '**/*.pem'] }, action: 'deny', enabled: true, decisionId: null, origin: 'template' },
  { kind: 'ask_before', label: 'Bağımlılık kurmadan önce sor', spec: { patterns: ['npm install', 'pip install'] }, action: 'ask', enabled: true, decisionId: null, origin: 'template' },
]

// ---- düz cümleyle ölçüt şablonları (sunucu biçimi) ----
function checkTemplates(pid: number): CheckTemplates {
  const find = (re: RegExp) => checks.find((c) => c.projectId === pid && c.kind === 'command' && re.test(String(c.spec.cmd ?? '')))
  const test = find(/test|pytest/)
  const build = find(/build/)
  const f = (name: string, label: string, placeholder: string, def = '') => ({ name, label, placeholder, default: def })
  const url = f('url', 'Adres', 'http://localhost:3000', 'http://localhost:5173/')
  const list: CheckTemplate[] = [
    { id: 'tests', sentence: 'Testler geçsin', kind: 'command', fields: [], available: !!test, reason: test ? null : 'Bu projede test komutu bulamadım.', detectedCmd: test ? String(test.spec.cmd) : null },
    { id: 'build', sentence: 'Proje derlensin', kind: 'command', fields: [], available: !!build, reason: build ? null : 'Bu projede derleme komutu bulamadım.', detectedCmd: build ? String(build.spec.cmd) : null },
    { id: 'url-opens', sentence: 'Şu adres açılsın:', kind: 'url', fields: [url], available: true, reason: null, detectedCmd: null },
    { id: 'page-text', sentence: 'Sayfada şu yazı görünsün:', kind: 'url', fields: [url, f('text', 'Yazı', 'Hoş geldin')], available: true, reason: null, detectedCmd: null },
    { id: 'file-exists', sentence: 'Şu dosya oluşsun:', kind: 'file', fields: [f('path', 'Dosya yolu', 'dist\\index.html')], available: true, reason: null, detectedCmd: null },
    { id: 'file-text', sentence: 'Dosyada şu yazı geçsin:', kind: 'file', fields: [f('path', 'Dosya yolu', 'dist\\index.html'), f('text', 'Yazı', 'Sürüm 1')], available: true, reason: null, detectedCmd: null },
    { id: 'screenshot', sentence: 'Sayfanın ekran görüntüsünü al:', kind: 'screenshot', fields: [url], available: pid !== 4, reason: pid === 4 ? 'Bu projede ekran görüntüsü alacak tarayıcı bulunamadı.' : null, detectedCmd: null },
  ]
  return { templates: list, hasTests: !!test, hint: test ? null : "Bu projede otomatik test bulamadım. En azından 'şu adres açılsın' ya da 'ekran görüntüsü al' ölçütü ekle. Yoksa işler 'kanıtlanmadı' kalır." }
}

function checkFromTemplate(b: Record<string, unknown>): Check {
  const pid = Number(b.projectId)
  const tpl = checkTemplates(pid).templates.find((x) => x.id === b.template)
  if (!tpl) throw new ApiError(400, 'Bilinmeyen ölçüt cümlesi.')
  if (!tpl.available) throw new ApiError(409, tpl.reason ?? 'Bu cümle bu projede kullanılamaz.')
  const v = (b.values ?? {}) as Record<string, unknown>
  const get = (k: string) => String(v[k] ?? '').trim()
  for (const fl of tpl.fields) if (!get(fl.name)) throw new ApiError(400, `${fl.label} boş olamaz.`)
  const url = get('url'), text = get('text'), path = get('path')
  if ((tpl.id === 'url-opens' || tpl.id === 'page-text' || tpl.id === 'screenshot') && !/^https?:\/\//i.test(url)) throw new ApiError(400, 'Adres http:// ya da https:// ile başlamalı.')
  const specs: Record<string, Record<string, unknown>> = {
    tests: { cmd: tpl.detectedCmd, expectExit: 0 }, build: { cmd: tpl.detectedCmd, expectExit: 0 },
    'url-opens': { url, expectStatus: 200 }, 'page-text': { url, expectStatus: 200, text },
    'file-exists': { path, exists: true }, 'file-text': { path, exists: true, text }, screenshot: { url, width: 1280, height: 800 },
  }
  const labels: Record<string, string> = {
    tests: 'Testler geçsin', build: 'Proje derlensin', 'url-opens': `${url} açılsın`, 'page-text': `${url} sayfasında “${text}” yazısı görünsün`,
    'file-exists': `${path} dosyası oluşsun`, 'file-text': `${path} dosyasında “${text}” geçsin`, screenshot: `${url} sayfasının ekran görüntüsü alınsın`,
  }
  return { id: nextId++, projectId: pid, jobId: (b.jobId as number | null) ?? null, kind: tpl.kind, label: labels[tpl.id], spec: specs[tpl.id], timeoutS: 120, enabled: b.enabled !== false, origin: 'user', createdAt: new Date().toISOString() }
}

// ---- kanca sağlığı ----
function hooksHealth(): HooksHealth {
  const ago = (min: number) => iso(min * MIN)
  const exe = 'C:\\Program Files\\Soz\\soz.exe'
  const files: HooksHealth['files'] = [
    { scope: 'user', projectId: null, projectName: null, path: 'C:\\Users\\ornek\\.claude\\settings.json', exists: true, readError: null, hooks: ['SessionStart', 'UserPromptSubmit', 'PreToolUse', 'Stop'], exePath: exe, exeExists: true, dataDir: 'E:\\Ornek\\Soz\\veri', dataMatches: true },
    { scope: 'project', projectId: 2, projectName: pname(2), path: 'D:\\ornek\\bahce-takip\\.claude\\settings.json', exists: true, readError: null, hooks: ['Stop'], exePath: hooksBroken ? 'D:\\eski\\soz.exe' : exe, exeExists: !hooksBroken, dataDir: hooksBroken ? 'D:\\eski\\veri' : 'E:\\Ornek\\Soz\\veri', dataMatches: !hooksBroken },
  ]
  const stats: HooksHealth['stats'] = [
    { name: 'Stop', runs24h: 14, errors24h: hooksBroken ? 14 : 0, timeouts24h: 0, lastRunAt: ago(35), lastResult: hooksBroken ? 'hata' : 'tamam', lastErrorAt: hooksBroken ? ago(35) : null, lastError: hooksBroken ? 'soz.exe bulunamadı: D:\\eski\\soz.exe' : null, maxMs: 4120 },
    { name: 'PreToolUse', runs24h: 212, errors24h: 0, timeouts24h: 1, lastRunAt: ago(2), lastResult: 'tamam', lastErrorAt: null, lastError: null, maxMs: 960 },
    { name: 'UserPromptSubmit', runs24h: 63, errors24h: 0, timeouts24h: 0, lastRunAt: ago(18), lastResult: 'tamam', lastErrorAt: null, lastError: null, maxMs: 310 },
  ]
  return {
    checkedAt: new Date().toISOString(),
    ok: !hooksBroken,
    gateProblem: hooksBroken ? 'Stop kancası soz.exe dosyasını bulamıyor' : null,
    warnings: hooksBroken ? ['Bahçe Takip projesindeki kanca başka bir veri klasörüne yazıyor.'] : [],
    files,
    stats,
  }
}

// ---- canlı kanıt çalıştırma benzetimi ----
async function simulateRun(run: ProofRun, list: Check[]) {
  const total = list.length
  emitLive('proof', { runId: run.id, jobId: run.jobId, projectId: run.projectId, phase: 'started', total })
  let anyFailed = false
  for (let i = 0; i < list.length; i++) {
    const c = list[i]
    await sleep(520)
    const fails = run.projectId === 3 && c.kind === 'command' && /test/.test(String(c.spec.cmd))
    anyFailed = anyFailed || fails
    run.results = [
      ...(run.results ?? []),
      res(run.id, i + 1, c.id, c.label, fails ? 'failed' : 'passed', 300 + i * 410, fails ? 'Tests: 1 failed, 18 passed, 19 total' : c.kind === 'command' ? 'tamam' : c.kind === 'url' ? 'HTTP 200' : 'sayfa açıldı', fails ? 1 : c.kind === 'command' ? 0 : null,
        c.kind === 'screenshot' ? shotSvg('Elle çalıştırma', 'ok') : null),
    ]
    emitLive('proof', { runId: run.id, jobId: run.jobId, projectId: run.projectId, phase: 'check', index: i + 1, total, label: c.label, status: fails ? 'failed' : 'passed' })
  }
  await sleep(200)
  run.finishedAt = new Date().toISOString()
  run.verdict = total === 0 ? 'unchecked' : anyFailed ? 'failed' : 'passed'
  run.summary = total === 0 ? 'Kontrol edilecek ölçüt tanımlı değil' : anyFailed ? 'Bir ölçüt kaldı' : `${total} ölçütün hepsi geçti`
  const job = jobs.find((j) => j.id === run.jobId)
  if (job) {
    job.lastVerdict = run.verdict
    job.lastRunId = run.id
    job.attempts = Math.max(job.attempts ?? 0, run.attempt)
    job.failedLabels = (run.results ?? []).filter((r) => r.status === 'failed').map((r) => r.label)
    if (run.verdict === 'passed') { job.state = 'gecti'; job.finishedAt = run.finishedAt }
    else if (run.verdict === 'failed') job.state = 'kaldi'
  }
  running.delete(run.projectId)
  emitLive('proof', { runId: run.id, jobId: run.jobId, projectId: run.projectId, phase: 'finished', verdict: run.verdict })
  emitLive('job', { id: run.jobId })
}

const rulesText = () => {
  const r = decisions.filter((d) => d.status === 'approved' && (d.type === 'rule' || d.type === 'ban')).slice(0, 4)
  return r.length ? '\n\nGeçerli kurallar:\n' + r.map((d) => `- ${d.text}`).join('\n') : ''
}

const detail = (j: Job): JobDetail => ({
  ...j,
  runs: runs.filter((r) => r.jobId === j.id).sort((a, b) => b.attempt - a.attempt).map(summaryOf),
})

const SUGGEST_POOL: Record<number, { kind: Check['kind']; label: string; spec: Record<string, unknown> }[]> = {
  2: [
    { kind: 'command', label: 'Tür denetimi temiz çıksın', spec: { cmd: 'npm run check', expectExit: 0 } },
    { kind: 'command', label: 'Lint temiz çıksın', spec: { cmd: 'npm run lint', expectExit: 0 } },
  ],
  3: [{ kind: 'command', label: 'Çözüm hatasız derlensin', spec: { cmd: 'dotnet build --nologo', expectExit: 0 } }],
  4: [],
  5: [],
}

// Yeni eklenen klasör (kimlik 100 ve üstü) için öneri; yolu 'testsiz' ile bitenler test bulunmayan klasörü anlatır.
const DEFAULT_SUGGEST: { kind: Check['kind']; label: string; spec: Record<string, unknown> }[] = [
  { kind: 'command', label: 'Testler geçsin', spec: { cmd: 'npm test', expectExit: 0 } },
  { kind: 'command', label: 'Derleme hatasız bitsin', spec: { cmd: 'npm run build', expectExit: 0 } },
]

/** mock3 (yeni akışlar) bu durumu paylaşır. */
export function mock2State() {
  init()
  return { jobs, checks, runs, nextId: () => nextId++ }
}

// ---- yönlendirici ----
export async function mock2Handle(method: string, p: string, q: URLSearchParams, body: unknown): Promise<unknown> {
  init()
  const b = (body ?? {}) as Record<string, unknown>
  const projId = q.get('projectId') ? Number(q.get('projectId')) : null
  let m: RegExpMatchArray | null

  if (p === '/api/now') return now()
  if (p === '/api/morning') return morning()
  if (p === '/api/usage') return usage(Number(q.get('days') ?? 14))

  if (p === '/api/jobs') {
    if (method === 'POST') {
      const pid = Number(b.projectId)
      const raw = String(b.rawText ?? '').trim()
      if (!raw) throw new ApiError(400, 'Önce işi yaz.')
      const ids = (b.checkIds as number[] | undefined) ?? []
      const picked = checks.filter((c) => ids.includes(c.id) && c.enabled)
      let text = raw
      text += picked.length ? `\n\nBitti ölçütleri (Söz bu ölçütleri kendisi çalıştıracak):\n${picked.map((c) => `- ${c.label}`).join('\n')}` : '\n\nBitti ölçütleri: tanımlı ölçüt yok. Söz bu işi kanıtlayamayacak.'
      text += rulesText()
      if (b.restate) text += '\n\nBaşlamadan önce ne anladığını en çok 5 maddeyle yaz ve onayımı bekle.'
      const job: Job = {
        id: nextId++, projectId: pid, projectName: pname(pid), sessionId: null, title: raw.length > 70 ? raw.slice(0, 67) + '…' : raw, rawText: raw,
        promptText: text, promptHash: Math.random().toString(16).slice(2, 10), source: b.source === 'dikte' ? 'dikte' : 'yazi', state: 'taslak', night: !!b.night, restate: !!b.restate,
        createdAt: new Date().toISOString(), givenAt: null, finishedAt: null, lastVerdict: null, attempts: 0, maxAttempts: 3,
        agent: b.agent === 'codex' ? 'codex' : 'claude',
      }
      jobs.unshift(job)
      emitLive('job', { id: job.id })
      return job
    }
    return jobs.filter((j) => projId === null || j.projectId === projId).sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))
  }
  m = p.match(/^\/api\/jobs\/(\d+)$/)
  if (m) {
    const job = jobs.find((j) => j.id === Number(m![1]))
    if (!job) throw new ApiError(404, 'İş bulunamadı.')
    if (method === 'PATCH') {
      if (b.state === 'iptal') {
        if (job.state === 'gecti') throw new ApiError(409, 'Biten iş iptal edilemez.')
        job.state = 'iptal'
      }
      emitLive('job', { id: job.id })
    }
    return detail(job)
  }

  m = p.match(/^\/api\/projects\/(\d+)\/checks$/)
  if (m) {
    const jid = q.get('jobId') ? Number(q.get('jobId')) : null
    return checks.filter((c) => c.projectId === Number(m![1]) && (c.jobId === null || (jid !== null && c.jobId === jid)))
  }
  m = p.match(/^\/api\/projects\/(\d+)\/checks\/templates$/)
  if (m) return checkTemplates(Number(m[1]))
  m = p.match(/^\/api\/projects\/(\d+)\/checks\/suggest$/)
  if (m) {
    const pid = Number(m[1])
    await sleep(380)
    const proj = projects.find((x) => x.id === pid)
    const pool = SUGGEST_POOL[pid] ?? (/testsiz/i.test(proj?.path ?? '') ? [] : DEFAULT_SUGGEST)
    const fresh = pool.filter((s) => !checks.some((c) => c.projectId === pid && c.label === s.label))
    const made = fresh.map((s) => ({ id: nextId++, projectId: pid, jobId: null, kind: s.kind, label: s.label, spec: s.spec, timeoutS: 120, enabled: false, origin: 'auto' as const, createdAt: new Date().toISOString() }))
    checks.push(...made)
    return { added: made.length, checks: checks.filter((c) => c.projectId === pid && c.jobId === null) }
  }
  if (p === '/api/checks' && method === 'POST' && b.template) {
    const c = checkFromTemplate(b)
    checks.push(c)
    return c
  }
  if (p === '/api/checks' && method === 'POST') {
    const c: Check = {
      id: nextId++, projectId: Number(b.projectId), jobId: (b.jobId as number | null) ?? null, kind: b.kind as Check['kind'], label: String(b.label || 'Ölçüt'),
      spec: (b.spec as Record<string, unknown>) ?? {}, timeoutS: Number(b.timeoutS ?? 120), enabled: b.enabled !== false, origin: 'user', createdAt: new Date().toISOString(),
    }
    checks.push(c)
    return c
  }
  m = p.match(/^\/api\/checks\/(\d+)$/)
  if (m) {
    const i = checks.findIndex((c) => c.id === Number(m![1]))
    if (i < 0) throw new ApiError(404, 'Ölçüt bulunamadı.')
    if (method === 'DELETE') { checks.splice(i, 1); return { ok: true } }
    Object.assign(checks[i], b)
    return checks[i]
  }
  m = p.match(/^\/api\/projects\/(\d+)$/)
  if (m && method === 'GET') {
    const pr = projects.find((x) => x.id === Number(m![1]))
    if (!pr) throw new ApiError(404, 'Proje bulunamadı.')
    return { ...pr, checks: checks.filter((c) => c.projectId === pr.id && !c.jobId).length, enabledChecks: enabledChecks(pr.id, null).length }
  }
  if (m && method === 'PATCH') {
    const pr = projects.find((x) => x.id === Number(m![1]))
    if (!pr) throw new ApiError(404, 'Proje bulunamadı.')
    Object.assign(pr, b)
    return pr
  }

  m = p.match(/^\/api\/projects\/(\d+)\/proof\/run$/)
  if (m && method === 'POST') {
    const pid = Number(m[1])
    if (running.has(pid)) throw new ApiError(409, 'Bu proje için kanıt zaten çalışıyor.')
    const jobId = (b.jobId as number | undefined) ?? null
    const list = enabledChecks(pid, jobId)
    const attempt = runs.filter((r) => r.jobId === jobId && r.projectId === pid).length + 1
    const run: ProofRun = { id: nextId++, projectId: pid, jobId, sessionId: null, trigger: 'manual', attempt, startedAt: new Date().toISOString(), finishedAt: null, verdict: null, summary: null, results: [] }
    runs.push(run)
    running.add(pid)
    const job = jobs.find((j) => j.id === jobId)
    if (job && (job.state === 'taslak' || job.state === 'verildi')) job.state = 'calisiyor'
    void simulateRun(run, list)
    return { runId: run.id }
  }
  if (p === '/api/proof/runs') {
    const jobId = q.get('jobId') ? Number(q.get('jobId')) : null
    return runs.filter((r) => (projId === null || r.projectId === projId) && (jobId === null || r.jobId === jobId)).sort((a, b) => b.id - a.id).map(summaryOf)
  }
  m = p.match(/^\/api\/proof\/runs\/(\d+)$/)
  if (m) {
    const r = runs.find((x) => x.id === Number(m![1]))
    if (!r) throw new ApiError(404, 'Çalıştırma bulunamadı.')
    return { ...summaryOf(r), results: r.results ?? [] }
  }

  if (p === '/api/guard/rules') {
    if (method === 'POST') {
      const r: GuardRule = {
        id: nextId++, projectId: (b.projectId as number | null) ?? null, kind: b.kind as GuardRule['kind'], label: String(b.label), spec: (b.spec as Record<string, unknown>) ?? {},
        action: (b.action as GuardRule['action']) ?? 'ask', enabled: true, decisionId: (b.decisionId as number | null) ?? null, origin: (b.origin as GuardRule['origin']) ?? 'user', createdAt: new Date().toISOString(),
      }
      rules.push(r)
      emitLive('guard', null)
      return r
    }
    return rules.filter((r) => projId === null || r.projectId === projId || r.projectId === null)
  }
  m = p.match(/^\/api\/guard\/rules\/(\d+)$/)
  if (m) {
    const i = rules.findIndex((r) => r.id === Number(m![1]))
    if (i < 0) throw new ApiError(404, 'Kural bulunamadı.')
    if (method === 'DELETE') { rules.splice(i, 1); return { ok: true } }
    Object.assign(rules[i], b)
    return rules[i]
  }
  if (p === '/api/guard/templates') return Object.entries(GUARD_TEMPLATE_INFO).map(([id, i]) => ({ id, ...i, rules: TEMPLATES[id].map((r) => ({ kind: r.kind, label: r.label, action: r.action })) }))
  if (p === '/api/hooks/health') return hooksHealth()
  if (p === '/api/guard/templates/apply' && method === 'POST') {
    const pid = Number(b.projectId)
    const tpl = TEMPLATES[String(b.template)]
    if (!tpl) throw new ApiError(400, 'Bilinmeyen şablon.')
    await sleep(140)
    const made = tpl
      .filter((t) => !rules.some((r) => r.projectId === pid && r.label === t.label))
      .map((t) => ({ ...t, id: nextId++, projectId: pid, createdAt: new Date().toISOString() }))
    rules.push(...made)
    emitLive('guard', null)
    return { template: String(b.template), created: made, existing: [] }
  }
  if (p === '/api/guard/events') {
    const items = events.slice().sort((a, b) => Date.parse(b.at) - Date.parse(a.at)).map((e) => ({ id: e.id, at: e.at, projectId: e.projectId, projectName: e.projectName ?? null, sessionId: e.toolSessionId ?? null, tool: e.tool, target: e.targetSummary, ruleId: e.ruleId, ruleLabel: e.ruleLabel ?? null, action: e.action, reason: e.reason }))
    return { days: Number(q.get('days') ?? 7), total: items.length, deny: items.filter((x) => x.action === 'deny').length, ask: items.filter((x) => x.action === 'ask').length, items }
  }
  if (p === '/api/guard/test' && method === 'POST') {
    await sleep(70)
    return guardTest(Number(b.projectId), String(b.toolName), (b.toolInput as Record<string, unknown>) ?? {})
  }

  if (p === '/api/queue') {
    if (method === 'POST') {
      const text = String(b.text ?? '').trim()
      if (!text) throw new ApiError(400, 'Mesaj boş.')
      const it: QueueItem = { id: nextId++, projectId: Number(b.projectId ?? 0), sessionId: (b.sessionId as number | null) ?? null, text, createdAt: new Date().toISOString(), deliveredAt: null, origin: 'panel' }
      queue.push(it)
      emitLive('queue', null)
      return it
    }
    const sid = q.get('sessionId') ? Number(q.get('sessionId')) : null
    return queue.filter((x) => (projId === null || x.projectId === projId) && (sid === null || x.sessionId === sid) && (q.get('pending') !== 'true' || x.deliveredAt === null))
  }

  if (p === '/api/phone/pairing' || p === '/api/phone/pairing/renew') {
    const code = () => Math.random().toString(16).slice(2, 10) + Math.random().toString(16).slice(2, 10)
    if (!settings.lanEnabled) return { enabled: false, listening: false, url: null, addresses: [], error: null } satisfies PhonePairing
    if (method === 'POST' || !pairing.url) {
      pairing = { enabled: true, listening: true, url: `http://192.168.1.23:49301/t/?k=${method === 'POST' ? code() : '9f2c41d07a6be385c0d1e7a24b8f5963'}`, addresses: ['http://192.168.1.23:49301/t/', 'http://172.20.10.4:49301/t/'], error: null }
    }
    return pairing
  }
  return NOT_HANDLED
}
