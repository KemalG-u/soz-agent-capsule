// Söz 3 uçları (JobsProofEndpoints.cs, gerçek biçim). Sunucu satırlara kalıcı kimlik verir, kanıt sonuçlarını satıra gömer ve iş durumunu
// run/agentState ile bildirir: bu katman onları arayüzün iç modeline (TimelineItem, Job.awaiting) çevirir. Uç yoksa (boş gövdeli 404, 405, 501) arayüz çökmez:
// ApiError(404, NOT_IN_VERSION) fırlatılır ve errText3 sade "bu sürümde yok" cümlesine çevirir.
import { request } from './api'
import { ApiError } from './errors'
import { hata } from './hata'
import { hamAyir } from './yarida'
import { t } from './i18n/index.svelte'
import type { Job, JobRun, ProjectAddResult, ReasonCode, TestOffer, TimelineItem, TimelineKind, Verdict, WireTimelineItem } from './types'

export const NOT_IN_VERSION = '__bu-surumde-yok__'

async function soft<T>(p: Promise<T>): Promise<T> {
  try {
    return await p
  } catch (e) {
    // Yönlendirme bulunamadı: gövde yok, ileti yalnız durum metni. Sunucunun kendi cümleli 404'ü ("İş bulunamadı") bu değildir.
    if (e instanceof ApiError && (e.status === 404 || e.status === 405 || e.status === 501) && /^(Not Found|Method Not Allowed|Not Implemented|)$/i.test(e.message)) {
      throw new ApiError(404, NOT_IN_VERSION)
    }
    throw e
  }
}

export const isMissing = (e: unknown): boolean => e instanceof ApiError && e.message === NOT_IN_VERSION

/** Yeni uçların hata cümlesi: "bu sürümde yok" sade söylenir, ham durum kodu gösterilmez. */
export function errText3(e: unknown): string {
  return hata(e).text
}

// ---------------- sunucu biçimi → iç model ----------------

const FINAL = ['gecti', 'kaldi', 'iptal', 'durduruldu', 'baslatilamadi']

/** "Sende bekleyen" bilgisi sunucuda yok: bekleyen onay ya da ajanın sorusundan türetilir. */
export function awaitingOf(state: string | undefined, run: JobRun | null | undefined, agentState: string | null | undefined): 'approval' | 'reply' | 'decision' | null {
  if (state === 'karar_bekliyor') return 'decision'
  if (state === 'onay_bekliyor' || run?.pendingApproval || run?.status === 'waiting_approval') return 'approval'
  if (state === 'cevap_bekliyor') return 'reply'
  if (agentState === 'soru' && !FINAL.includes(state ?? '') && run?.status !== 'running') return 'reply'
  return null
}

type WireRun = Partial<JobRun> & { started?: string | null; ended?: string | null }

export function normRun(r: WireRun | null | undefined): JobRun | null {
  if (!r) return null
  const { started, ended: _e, ...rest } = r
  return { ...(rest as JobRun), startedAt: r.startedAt ?? started ?? null }
}

/** İş satırı: run alanını düzleştirir, awaiting türetir. Zaten awaiting taşıyan (eski örnek) veri bozulmaz. */
export function normJob<T extends Job>(j: T): T {
  const run = normRun(j.run as WireRun | null | undefined)
  const pa = Array.isArray(j.pendingApprovals) ? j.pendingApprovals.filter((x) => x && x.eventId !== undefined && x.eventId !== null) : null
  return { ...j, run, pendingApprovals: pa, awaiting: awaitingOf(j.state, run, j.agentState) }
}

const KIND: Record<string, TimelineKind> = { user: 'user', agent: 'agent', progress: 'progress', proof: 'proof', approval: 'approval', state: 'system', error: 'system' }
const APPROVAL_PREFIX = /^(senden onay bekliyor|onayladın|reddettin): /

/** Sunucu satırı → iç satır. Kimlik sunucudan gelir (SSE job.progress lineId ile aynı: l{lineId}). */
export function fromWire(w: WireTimelineItem): TimelineItem | null {
  const kind = KIND[w.kind] ?? 'progress'
  // Zaman çizelgesinde onay yalnız durumlu satırdır. SSE job.progress durumsuz bir onay kopyası taşır; onun yerine job.state ile akış yeniden okunur.
  if (w.kind === 'approval' && !w.status) return null
  const id = w.id ?? `${w.at}|${w.kind}|${w.text}`
  if (kind === 'approval') {
    const state = w.status === 'pending' ? 'pending' : w.status === 'denied' ? 'denied' : 'allowed'
    return { id, ts: w.at, kind, text: w.text.replace(APPROVAL_PREFIX, ''), detail: w.detail ?? null, state, eventId: w.eventId ?? null }
  }
  if (w.kind === 'proof' && !w.verdict) return { id, ts: w.at, kind: 'progress', text: t('streamProofRunning'), state: 'active' }
  if (w.kind === 'proof') {
    const proof: TimelineItem['proof'] = {
      verdict: w.verdict as Verdict,
      attempt: w.attempt ?? 1,
      summary: w.detail ?? null,
      results: (w.results ?? []).map((x) => ({ label: x.label, status: x.status, durationMs: x.durationMs, outputTail: x.outputTail, artifactUrl: x.artifactUrl, artifactKind: x.artifactKind, testsTotal: x.testsTotal ?? null, testsPassed: x.testsPassed ?? null, testsSkipped: x.testsSkipped ?? null })),
      reasonCode: (w.reasonCode as ReasonCode | null | undefined) ?? null,
      reasonText: w.reasonText ?? null,
      verdictLabel: w.verdictLabel ?? null,
      changedFiles: w.changedFiles ?? [],
      addedTests: w.addedTests ?? [],
      countUnknown: !!w.countUnknown,
      baselineText: w.baselineText ?? null,
      snapshotId: w.snapshotId ?? null,
      diffs: w.diffs ?? [],
      notes: w.notes ?? [],
    }
    return { id, ts: w.at, kind, text: w.text, detail: null, state: null, proof }
  }
  if (kind === 'progress') return { id, ts: w.at, kind, text: w.text, detail: w.detail ?? null, state: 'done' }
  // Ham hata cümlenin içinde gelirse (eski kayıt) sade cümle akışta kalır, ham metin yalnız "Teknik ayrıntı" altına gider.
  const sp = w.kind === 'error' ? hamAyir(w.text, w.detail) : { text: w.text, detail: w.detail ?? null }
  return { id, ts: w.at, kind, text: sp.text, detail: sp.detail, state: w.kind === 'error' ? 'error' : null }
}

/** SSE job.progress: {jobId, runId, lineId, at, kind, text}. */
export function progressFromEvent(d: Record<string, unknown>): TimelineItem | null {
  if (typeof d.kind !== 'string' || typeof d.text !== 'string') return null
  const w: WireTimelineItem = { id: d.lineId !== undefined && d.lineId !== null ? `l${d.lineId}` : null, at: String(d.at ?? new Date().toISOString()), kind: d.kind, text: d.text, runId: d.runId as number | undefined }
  const it = fromWire(w)
  if (!it) return null
  return it.kind === 'progress' ? { ...it, state: 'active' } : it
}

/** SSE job.state: {jobId, runId, status, jobState, lastLine, failReason, exitCode, approval}. */
export function stateFromEvent(d: Record<string, unknown>): Partial<Job> {
  const a = d.approval as { eventId: number; tool?: string; summary: string } | null | undefined
  const run: JobRun = {
    runId: d.runId as number | undefined,
    status: (d.status as JobRun['status']) ?? 'running',
    lastLine: (d.lastLine as string | null | undefined) ?? null,
    failReason: (d.failReason as string | null | undefined) ?? null,
    pendingApproval: a ? { eventId: a.eventId, summary: a.summary, tool: a.tool ?? null } : null,
  }
  const patch: Partial<Job> = { run }
  if (typeof d.jobState === 'string') patch.state = d.jobState as Job['state']
  else if (typeof d.state === 'string') patch.state = d.state as Job['state']
  if (typeof d.acceptedByUser === 'boolean') patch.acceptedByUser = d.acceptedByUser
  if (typeof d.startError === 'string' || d.startError === null) patch.startError = d.startError as string | null
  if (typeof d.question === 'string' || d.question === null) patch.question = d.question as string | null
  if (Array.isArray(d.pendingApprovals)) patch.pendingApprovals = d.pendingApprovals as Job['pendingApprovals']
  patch.awaiting = awaitingOf(patch.state, run, undefined)
  return patch
}

// ---------------- uçlar ----------------

/** Klasör ekle. Cevap: proje, öneri ölçütleri ve onay kartının cümlesi. */
export const addProject = async (path: string): Promise<ProjectAddResult> => {
  // Kullanıcı girdisi hatası (klasör yok, reddedilen yer) sunucudan 200 + {ok:false, error, code} gelir: tarayıcı konsoluna hata düşmez.
  const r = await soft(request<ProjectAddResult & { ok?: boolean; error?: string; code?: string }>('POST', '/api/projects', { path }))
  if (r.ok === false) throw new Error(r.error || t('folderBadPath'))
  return r
}

/** Tek onay: önerilen ölçütleri açar, kapıyı açar, istenirse Bekçi şablonunu uygular. */
export const approveSetup = (projectId: number, body: { checkIds?: number[]; gate?: boolean; guardTemplate?: string | boolean }) =>
  soft(request<{ guardTemplate: string | null; guardRulesCreated: number }>('POST', `/api/projects/${projectId}/approve-setup`, body))

type RunReply = { run?: unknown; job?: Job }
const job_ = (r: RunReply) => (r.job ? normJob(r.job) : null)

export const startJob = (id: number, writeCheckFirst = false) => soft(request<RunReply>('POST', `/api/jobs/${id}/start`, writeCheckFirst ? { writeCheckFirst: true } : {})).then(job_)
export const replyJob = (id: number, text: string) => soft(request<RunReply>('POST', `/api/jobs/${id}/reply`, { text })).then(job_)
export const stopJob = (id: number) => soft(request<RunReply & { stopped?: boolean }>('POST', `/api/jobs/${id}/stop`, {})).then(job_)
export const approveJob = (id: number, eventId: number, allow: boolean) =>
  soft(request<RunReply>('POST', `/api/jobs/${id}/approve`, { eventId, allow })).then(job_)
// "Görüldü" çağrısı kaldırıldı (u3): işe bakmak satırı düşürmez. Satır yalnız "Gördüm", kabul/ret ya da uygulanan geri almayla düşer.
/** "Değişikliği kabul et" ya da "Kabul etme". */
export const acceptJob = (id: number, accept: boolean, snapshotId?: string | null) =>
  soft(request<RunReply>('POST', `/api/jobs/${id}/accept`, snapshotId ? { accept, snapshotId } : { accept })).then(job_)
/** Ajanın testlerini onayladığın testlere kat (yalnız insan çağırabilir). Gerçek cevap: { job }.
 *  scope "added": eklenen yeni testler, tek tıkla. scope "edited": değiştirilen onaylı testler, fark özetinin kimliğiyle (önce fark görülmüş olmalı, yoksa sunucu 409 verir). */
export const approveNewTests = (id: number, scope: 'added' | 'edited' = 'added', diffId?: string | null) =>
  soft(request<RunReply>('POST', `/api/jobs/${id}/approve-new-tests`, scope === 'edited' ? { scope, diffId } : { scope })).then(job_)
/** Teklife "Hayır": sunucuda kayıtlı, aynı teklif geri gelmez. Gerçek cevap: { job }. */
export const declineNewTests = (id: number, scope: 'added' | 'edited' | 'all' = 'all') =>
  soft(request<RunReply>('POST', `/api/jobs/${id}/decline-new-tests`, { scope })).then(job_)
export const declineSuggestedCheck = (id: number) =>
  soft(request<RunReply>('POST', `/api/checks/${id}/decline`)).then(job_)
/** Değiştirilen onaylı testlerin fark özeti (satırlarıyla). Okumak sunucuda "gördü" kaydı yazar. Teklif yoksa 404. */
export const getTestOffer = (id: number) => soft(request<TestOffer>('GET', `/api/jobs/${id}/test-offer`))
/** Biten işe aynı oturumdan devam. */
export const continueJob = (id: number, text?: string) => soft(request<RunReply>('POST', `/api/jobs/${id}/continue`, text ? { text } : {})).then(job_)

export interface TimelineReply {
  items: TimelineItem[]
  state?: string
  agentState?: string | null
  run?: JobRun | null
  /** Zaman çizelgesi cevabı iş alanlarını da taşıyabilir. */
  extra?: Partial<Job>
}

/** Akış: sunucu satırları (kanıt sonuçları satırın içinde). Ayrıntı alanı (komut) için detail=1. */
export async function getTimeline(id: number): Promise<TimelineReply> {
  const r = await soft(request<{ items?: WireTimelineItem[]; state?: string; agentState?: string | null; run?: WireRun | null } | WireTimelineItem[]>('GET', `/api/jobs/${id}/timeline?detail=1`))
  const wire = Array.isArray(r) ? r : (r.items ?? [])
  const head = Array.isArray(r) ? {} : r
  const items = wire.map(fromWire).filter((x): x is TimelineItem => x !== null)
  // yalnız son ilerleme satırı canlı sayılır (ve yalnız ajan çalışırken)
  const running = (head as { run?: WireRun | null }).run?.status === 'running'
  let last = -1
  items.forEach((x, i) => { if (x.kind === 'progress') last = i })
  if (last >= 0) items[last] = { ...items[last], state: running ? 'active' : 'done' }
  const h = head as Partial<Job>
  const extra: Partial<Job> = {}
  if (typeof h.acceptedByUser === 'boolean') extra.acceptedByUser = h.acceptedByUser
  if (h.acceptedVia !== undefined) extra.acceptedVia = h.acceptedVia
  if (typeof h.acceptedUnverified === 'boolean') extra.acceptedUnverified = h.acceptedUnverified
  if (h.acceptedProcess !== undefined) extra.acceptedProcess = h.acceptedProcess
  if (h.startError !== undefined) extra.startError = h.startError
  if (h.question !== undefined) extra.question = h.question
  if (h.baseNote !== undefined) extra.baseNote = h.baseNote
  if (Array.isArray(h.pendingApprovals)) extra.pendingApprovals = h.pendingApprovals
  return { items, state: head.state, agentState: head.agentState, run: normRun((head as { run?: WireRun | null }).run), extra }
}

/** "Bu klasörde terminalden de çalışacağım": kancaları bu projenin klasörüne kurar (tek düğme). */
export const installHooks = (projectId: number) =>
  soft(request<{ settingsPath?: string; installed?: boolean; hooks?: string[] }>('POST', `/api/projects/${projectId}/hooks/install`, {}))
