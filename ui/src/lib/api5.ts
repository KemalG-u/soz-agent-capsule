// Dalga 2 uçları: kayıt defteri, ayarlar, bitti'nin tanımı, koruma ve geri alma, Sıra bende, tanışma, keşif (yalnız veri katmanı).
// Aynı istemciyi kullanır (belirteç, hata biçimi {error}). Hata cümlesi sunucudan sade Türkçe gelir.
import { qs, request } from './api'
import { ApiError } from './errors'
import type {
  AttentionSnapshot,
  ChecklistState,
  DiscoveryLayout,
  DiscoverySearch,
  DiscoveryShadow,
  DiscoverySuggestions,
  AgentsReady,
  DoneAcceptItem,
  DoneAcceptReply,
  DoneLevel,
  DoneQuestion,
  DoneScope,
  DoneSuggestion,
  DoneSummary,
  FeedPage,
  NowLite,
  OturumOzet,
  OnboardingAccept,
  OnboardingView,
  OnboardingWhy,
  Protection,
  RegistryChange,
  RegistryReply,
  ScanReply,
  ServerSettings,
  UndoRecord,
  UndoReply,
} from './types5'

// ---- kayıt defteri ve ayarlar ----
export const getRegistry = (kind?: string) => request<RegistryReply>('GET', `/api/registry${qs({ kind })}`)
export const approveEntry = (id: string) => request<RegistryChange>('POST', '/api/registry/approve', { id })
export const revokeEntry = (id: string) => request<RegistryChange>('POST', '/api/registry/revoke', { id })
export const getServerSettings = () => request<ServerSettings>('GET', '/api/settings')
export const putServerSettings = (patch: Record<string, unknown>) => request<ServerSettings>('PUT', '/api/settings', patch)

// ---- bitti'nin tanımı ----
export const getDoneQuestions = () => request<{ questions: DoneQuestion[] }>('GET', '/api/done/questions')
export const getDoneLevels = () => request<{ levels: DoneLevel[]; note: string }>('GET', '/api/done/levels')
export const suggestDone = (b: { projectId: number; text: string; kind?: string; irreversible?: boolean }) => request<DoneSuggestion>('POST', '/api/done/suggest', b)
/** Kapsam her zaman gönderilir: 'job' (yalnız bu iş, jobId şart; iş henüz başlamamış olmalı) ya da 'project' (bu klasörde hep). */
export const acceptDone = (projectId: number, items: DoneAcceptItem[], scope: DoneScope = 'project', jobId?: number) =>
  request<DoneAcceptReply>('POST', '/api/done/accept', { projectId, scope, ...(scope === 'job' && jobId !== undefined ? { jobId } : {}), items })
/** İş vermeden önce özet: tam kart mı, tek satır mı. */
export const getDoneSummary = (projectId: number) => request<DoneSummary>('GET', `/api/done/summary${qs({ projectId })}`)
/** Ajan hazır mı: sürüm okunur, giriş dosyasının yalnız var olup olmadığına bakılır. */
export const getAgentsReady = () => request<AgentsReady>('GET', '/api/agents/ready')
export const getChecklist = (checkId: number, jobId: number) => request<ChecklistState>('GET', `/api/checks/${checkId}/checklist${qs({ jobId })}`)
export const tickChecklist = (jobId: number, checkId: number, item: number, ticked: boolean) =>
  request<ChecklistState>('POST', `/api/jobs/${jobId}/checklist/${checkId}/tick`, { item, ticked })

// ---- koruma ve geri alma ----
export const getProtection = (jobId: number) => request<Protection>('GET', `/api/jobs/${jobId}/protection`)
export const previewUndo = (jobId: number, b: { scope?: 'all' | 'last'; paths?: string[]; runId?: number } = {}) => request<UndoReply>('POST', `/api/jobs/${jobId}/undo/preview`, b)
export const applyUndo = (jobId: number, previewUid: string, force = false) => request<UndoReply>('POST', `/api/jobs/${jobId}/undo`, force ? { previewUid, force: true } : { previewUid })
export const getUndoHistory = (jobId: number) => request<{ undo: UndoRecord[] }>('GET', `/api/jobs/${jobId}/undo`)
export const revertUndo = (uid: string, force = false) => request<UndoReply>('POST', `/api/undo/${uid}/revert`, force ? { force: true } : {})

// ---- Sıra bende ----
import { sadeAgac } from './sade'
export async function getAttention(): Promise<AttentionSnapshot> {
  const s = await request<AttentionSnapshot>('GET', '/api/attention')
  if (!s || typeof s !== 'object' || !Array.isArray(s.items)) throw new ApiError(200, 'Boş cevap geldi')
  return sadeAgac(s)
}
export const getNowSessions = () => request<NowLite>('GET', '/api/now')
/** Sunucunun öğeye verdiği eylemi aynen çalıştırır (yön ve gövde sunucudan gelir). */
export const runAttentionAction = (a: { method: string; path: string; body: Record<string, unknown> | null }, extra?: Record<string, unknown>) =>
  request<unknown>(a.method, a.path, a.body || extra ? { ...(a.body ?? {}), ...(extra ?? {}) } : undefined)

// ---- tanışma ----
export const getOnboarding = () => request<OnboardingView>('GET', '/api/onboarding')
export const scanOnboarding = () => request<ScanReply>('POST', '/api/onboarding/scan', {})
export const whyOnboarding = (id: string) => request<OnboardingWhy>('GET', `/api/onboarding/why${qs({ id })}`)
export const acceptOnboarding = (id: string) => request<OnboardingAccept>('POST', '/api/onboarding/accept', { id })
export const dismissOnboarding = (id: string) => request<OnboardingAccept>('POST', '/api/onboarding/dismiss', { id })
export const restoreOnboarding = (id: string) => request<OnboardingAccept>('POST', '/api/onboarding/restore', { id })

// ---- keşif: bu işte yalnız veri katmanı (görünümü ayrı tasarım turunda) ----
export const getDiscoveryLayout = () => request<DiscoveryLayout>('GET', '/api/discovery/layout')
export const searchDiscovery = (q: string) => request<DiscoverySearch>('GET', `/api/discovery/search${qs({ q })}`)
export const placeDiscoverySlot = (b: { kindDir: number; slotDir: number; objectUid: string; replace?: boolean }) =>
  request<{ ok: boolean; kindDir: number; slotDir: number; objectUid: string; replaced: boolean }>('PUT', '/api/discovery/layout/slot', b)
export const clearDiscoverySlot = (kindDir: number, slotDir: number) => request<unknown>('DELETE', `/api/discovery/layout/slot${qs({ kindDir, slotDir })}`)
export const recordDiscoveryUsage = (objectUid: string, source: string) => request<{ ok: boolean }>('POST', '/api/discovery/usage', { objectUid, source })
export const getDiscoverySuggestions = () => request<DiscoverySuggestions>('POST', '/api/discovery/suggestions', { surface: 'page' })
export const getDiscoveryShadow = (days = 7) => request<DiscoveryShadow>('GET', `/api/discovery/shadow${qs({ days })}`)
export const dismissDiscovery = (objectUid: string, mode: "ignored" | "never-here" | "never" = "ignored") => request<unknown>("POST", "/api/discovery/dismiss", { objectUid, mode })
export const restoreDiscovery = (objectUid: string, mode: "ignored" | "never-here" | "never" = "ignored") => request<unknown>("POST", "/api/discovery/restore", { objectUid, mode })

// ---- oturum özeti (üç satır): Söz oturum kimliğiyle ya da yardımcının kendi kimliğiyle ----
export const getOzetById = (id: number | string) => request<OturumOzet | null>('GET', `/api/sessions/${id}/ozet`)
export const getOzetByTool = (tool: string, sessionId: string) => request<OturumOzet | null>('GET', `/api/sessions/ozet${qs({ tool, sessionId })}`)

// ---- Ne oldu: olay defteri, yalnız okuma ----
export const getEvents = (p: { limit?: number; before?: string | null; kind?: string | null } = {}) =>
  request<FeedPage>('GET', `/api/events${qs({ limit: p.limit, before: p.before ?? undefined, kind: p.kind ?? undefined })}`)
