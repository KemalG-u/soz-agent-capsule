import { sadeAgac } from './sade'
// Söz 2 uçları (FABLE-VIZYON-2 bölüm 5). Aynı istemciyi (belirteç, sahte katman, hata biçimi) kullanır.
import { qs, request } from './api'
import type {
  Check,
  CheckInput,
  CheckTemplateInput,
  CheckTemplates,
  FolderListing,
  GuardAction,
  GuardApplyRaw,
  GuardEvent,
  GuardEventsRaw,
  GuardKind,
  GuardRule,
  GuardTemplate,
  GuardTestResult,
  HooksHealth,
  Job,
  JobDetail,
  JobInput,
  Morning,
  Now,
  PhonePairing,
  ProofRun,
  Project,
  QueueItem,
  Usage,
  UsageRaw,
} from './types'

export const getNow = () => request<Now>('GET', '/api/now')
export const getMorning = async (since?: string) => sadeAgac(await request<Morning>('GET', `/api/morning${qs({ since })}`))
const DAYMS = 86_400_000
/** /api/usage gerçek biçimini (API-FARKLARI-2B) arayüz biçimine çevirir; eksik günleri sıfırla doldurur. */
export function adaptUsage(r: UsageRaw): Usage {
  const byKey = new Map(r.byDay.map((d) => [d.day, d]))
  const byDay: Usage['byDay'] = []
  const start = Date.parse(r.since + 'T00:00:00Z')
  const end = Date.parse(r.until + 'T00:00:00Z')
  for (let ms = start; ms <= end; ms += DAYMS) {
    const day = new Date(ms).toISOString().slice(0, 10)
    const d = byKey.get(day)
    const sub = d?.subagentTokens.freshTokens ?? 0
    byDay.push({ day, mainTokens: Math.max(0, (d?.tokens.freshTokens ?? 0) - sub), subTokens: sub, estimatedUsd: d?.costUsd ?? null })
  }
  return {
    days: r.days,
    priced: r.pricing.configured,
    note: r.pricing.configured && r.pricing.note ? r.pricing.note : null,
    totalTokens: r.totals.freshTokens,
    messages: r.totals.messages,
    subagentSharePct: Math.round(r.subagent.sharePercent),
    estimatedUsd: r.estimatedCostUsd,
    byDay,
    byModel: r.byModel.map((m) => ({ model: m.model, tokens: m.tokens.freshTokens, messages: m.tokens.messages, estimatedUsd: m.costUsd })),
  }
}
export const getUsage = (days = 14) => request<UsageRaw>('GET', `/api/usage${qs({ days })}`).then(adaptUsage)

/** İş listesi sayfası: sunucu varsayılan olarak en yeni 50 işi verir. envelope=1 ile toplam sayı ve "devamı var" bilgisi gelir. */
export interface JobsPage { items: Job[]; total: number; hasMore: boolean }
export async function getJobsPage(f: { projectId?: number | null; limit?: number } = {}): Promise<JobsPage> {
  const r = await request<Job[] | JobsPage>('GET', `/api/jobs${qs({ ...f, envelope: 1 })}`)
  if (Array.isArray(r)) return { items: r, total: r.length, hasMore: false }
  return { items: r.items, total: r.total, hasMore: r.hasMore }
}
export const getJob = (id: number) => request<JobDetail>('GET', `/api/jobs/${id}`)
export const createJob = (j: JobInput) => request<Job>('POST', '/api/jobs', j)
/** Yazılan iş metninde anahtar gibi bir şey var mı? Varsa tek cümlelik uyarı döner (metin kaydedilmez, yalnız kontrol edilir). */
export const checkJobText = (text: string) => request<{ secretWarning: string | null }>('POST', '/api/jobs/text-check', { text })
export const patchJobState = (id: number, state: 'verildi' | 'iptal') => request<Job>('PATCH', `/api/jobs/${id}`, { state })
export const cancelJob = (id: number) => patchJobState(id, 'iptal')

export const getChecks = (projectId: number, jobId?: number | null) => request<Check[]>('GET', `/api/projects/${projectId}/checks${qs({ jobId })}`)
export const suggestChecks = (projectId: number) => request<{ added: number; checks: Check[] }>('POST', `/api/projects/${projectId}/checks/suggest`, {})
export const createCheck = (c: CheckInput) => request<Check>('POST', '/api/checks', c)
/** Düz cümleyle ölçüt: hangi cümleler kullanılabilir (ve neden değil) ile test olup olmadığı. */
export const getCheckTemplates = (projectId: number) => request<CheckTemplates>('GET', `/api/projects/${projectId}/checks/templates`)
/** Cümleden ölçüt kurar; etiketi sunucu yazar. 400/409 hatasında ApiError.message sunucunun açıklamasıdır. */
export const createCheckFromTemplate = (c: CheckTemplateInput) => request<Check>('POST', '/api/checks', c)
export const patchCheck = (id: number, p: { enabled?: boolean; label?: string }) => request<Check>('PATCH', `/api/checks/${id}`, p)
export const deleteCheck = (id: number) => request<{ ok: boolean }>('DELETE', `/api/checks/${id}`)
export const getProject = (id: number) => request<Project>('GET', `/api/projects/${id}`)
export const patchProject = (id: number, p: { gateEnabled?: boolean }) => request<Project>('PATCH', `/api/projects/${id}`, p)

export const runProof = (projectId: number, jobId?: number | null) =>
  request<{ runId: number }>('POST', `/api/projects/${projectId}/proof/run`, jobId ? { jobId } : {})
export const getProofRuns = (f: { projectId?: number | null; jobId?: number | null; limit?: number }) =>
  request<ProofRun[]>('GET', `/api/proof/runs${qs(f)}`)
export const artifactUrl = (runId: number, name: string) => `/api/proof/artifact/${runId}/${encodeURIComponent(name)}`

export const getGuardRules = (projectId?: number | null) => request<GuardRule[]>('GET', `/api/guard/rules${qs({ projectId })}`)
export const createGuardRule = (r: {
  projectId: number | null
  kind: GuardKind
  label: string
  spec: Record<string, unknown>
  action: GuardAction
  decisionId?: number | null
  origin?: 'user' | 'defter'
}) => request<GuardRule>('POST', '/api/guard/rules', r)
export const patchGuardRule = (id: number, p: { enabled?: boolean; action?: GuardAction; label?: string }) =>
  request<GuardRule>('PATCH', `/api/guard/rules/${id}`, p)
export const deleteGuardRule = (id: number) => request<{ ok: boolean }>('DELETE', `/api/guard/rules/${id}`)
export const getGuardTemplates = () => request<GuardTemplate[]>('GET', '/api/guard/templates')
export const applyGuardTemplate = (projectId: number, template: string) =>
  request<GuardApplyRaw>('POST', '/api/guard/templates/apply', { projectId, template }).then((r) => r.created)
export const getGuardEvents = (days = 7) =>
  request<GuardEventsRaw>('GET', `/api/guard/events${qs({ days })}`).then((r) =>
    r.items.map<GuardEvent>((e) => ({ id: e.id, at: e.at, projectId: e.projectId, projectName: e.projectName, toolSessionId: e.sessionId, tool: e.tool, targetSummary: e.target, ruleId: e.ruleId, ruleLabel: e.ruleLabel, action: e.action, reason: e.reason })),
  )
export const testGuard = (projectId: number, toolName: string, toolInput: Record<string, unknown>) =>
  request<GuardTestResult>('POST', '/api/guard/test', { projectId, toolName, toolInput })

export const getQueue = (f: { projectId?: number | null; sessionId?: number | null; pending?: boolean } = {}) =>
  request<QueueItem[]>('GET', `/api/queue${qs(f)}`)

export const getHooksHealth = () => request<HooksHealth>('GET', '/api/hooks/health')

/** Fiyat satırları için model adları: harcamada görünenler ile fiyatı girilmeyenler. Sıra sabit, tekrarsız. */
export const getUsageModels = () =>
  request<UsageRaw>('GET', `/api/usage${qs({ days: 90 })}`).then((r) => [...new Set([...r.byModel.map((m) => m.model), ...(r.pricing?.unpricedModels ?? [])])])

export const getPhonePairing = () => request<PhonePairing>('GET', '/api/phone/pairing')
export const renewPhonePairing = () => request<PhonePairing>('POST', '/api/phone/pairing/renew', {})

/** Tarayıcıda klasör seçici: sürücü listesi (path yok) ya da bir klasörün alt klasörleri. Yalnız insan çağırabilir. */
export const browseFolders = (path?: string | null) => request<FolderListing>('GET', `/api/fs/browse${qs({ path: path ?? undefined })}`)
