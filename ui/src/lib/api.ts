// Tek API istemcisi (gerçek biçim: SozServer.cs). Sahte veri katmanı yalnız geliştirmede, VITE_MOCK=1 ile ya da adreste ?ornek=1 / ?bos=1 varken
// kullanılır; yayın sürümünde sunucu cevap vermezse sahteye DÜŞÜLMEZ, hata gösterilir.
import { ApiError } from './errors'
import type {
  AgentsReply,
  Decision,
  DecisionPatch,
  DecisionStatus,
  DecisionType,
  ImportResult,
  Packet,
  Project,
  RepeatCluster,
  Report,
  SessionToken,
  Settings,
  Summary,
  UtterancePage,
} from './types'

export { ApiError }

let token = ''
let useMock = false

export function isMock(): boolean {
  return useMock
}

export function qs(params: Record<string, string | number | boolean | null | undefined>): string {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue
    p.set(k, String(v))
  }
  const s = p.toString()
  return s ? `?${s}` : ''
}

export async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  if (useMock) return (await (await import('./mock')).mockHandle(method, path, body)) as T
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (method !== 'GET' && token) headers['X-Soz-Token'] = token
  let res: Response
  try {
    res = await fetch(path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      // Okumalarda 20 sn içinde cevap gelmezse bağlantı yok sayılır (C-41): sunucu bağlantıyı açık tutup susarsa şerit çıkar. Yazma ve uzun işler (tarama, özet) süre sınırı almaz.
      signal: method === 'GET' ? AbortSignal.timeout(20000) : undefined,
    })
  } catch {
    throw new ApiError(0, 'Söz sunucusuna ulaşılamadı')
  }
  const text = await res.text()
  let data: unknown = null
  if (text) {
    try {
      data = JSON.parse(text)
    } catch {
      data = text
    }
  }
  if (!res.ok) {
    let msg = res.statusText
    if (data && typeof data === 'object') {
      const o = data as Record<string, unknown>
      msg = String(o.error ?? o.message ?? o.detail ?? msg)
    } else if (typeof data === 'string' && data) {
      msg = data
    }
    throw new ApiError(res.status, msg)
  }
  return data as T
}

/** Örnek (sahte) veri istendi mi: geliştirme sunucusu, VITE_MOCK=1 ya da adreste ?ornek=1 / ?bos=1. */
function mockWanted(): boolean {
  if (import.meta.env.VITE_MOCK === '1') return true
  return /[?&](ornek|bos)=1/.test(location.search)
}

/** Açılışta belirteci alır. Yayın sürümünde sunucu yoksa hata fırlatır (arayüz "yüklenemedi" gösterir). */
export async function initApi(): Promise<SessionToken> {
  if (!mockWanted()) {
    // Sunucu bağlantıyı kimin açtığını kısa süre bulamazsa 403 verebilir (denetim 3, ilke 2): 3 kez, aralıkla yeniden dene.
    let lastDenied = ''
    for (let i = 0; i < 3; i++) {
      try {
        const res = await fetch('/api/session', { signal: AbortSignal.timeout(4000) })
        const type = res.headers.get('content-type') ?? ''
        if (res.ok && type.includes('json')) {
          const s = (await res.json()) as SessionToken
          token = s.token
          useMock = false
          return s
        }
        if (res.status === 403 && type.includes('json')) {
          lastDenied = String(((await res.json()) as { error?: string }).error ?? '')
          await new Promise((r) => setTimeout(r, 350 * (i + 1)))
          continue
        }
        break
      } catch {
        break // aşağıda karar verilir
      }
    }
    if (lastDenied && !import.meta.env.DEV) throw new Error(lastDenied)
    // Geliştirme sunucusunda (npm run dev) API yoksa sahte veriyle çalışmaya izin ver.
    if (!import.meta.env.DEV) throw new Error('Söz sunucusuna ulaşılamadı')
  }
  useMock = true
  return (await (await import('./mock')).mockHandle('GET', '/api/session')) as SessionToken
}

// ---- Okuma uçları ----
export const getSummary = () => request<Summary>('GET', '/api/summary')
export const getProjects = () => request<Project[]>('GET', '/api/projects')

export const explainSession = (id: number | string) =>
  request<{ text: string }>('POST', `/api/sessions/${id}/explain`)

export const getDecisions = (f: { projectId?: number | null; status?: DecisionStatus; type?: DecisionType } = {}) =>
  request<Decision[]>('GET', `/api/decisions${qs(f)}`)

export const createDecision = (d: { projectId: number; type: DecisionType; text: string }) =>
  request<Decision>('POST', '/api/decisions', d)

export const patchDecision = (id: number, p: DecisionPatch) =>
  request<Decision>('PATCH', `/api/decisions/${id}`, p)

export const getRepeats = (projectId?: number | null, min = 2) =>
  request<RepeatCluster[]>('GET', `/api/repeats${qs({ projectId, min })}`)

export const promoteRepeat = (clusterId: number, type?: DecisionType) =>
  request<Decision>('POST', `/api/repeats/${clusterId}/promote`, type ? { type } : {})

export const searchUtterances = (f: { q?: string; projectId?: number | null; limit?: number; offset?: number }) =>
  request<UtterancePage>('GET', `/api/utterances${qs(f)}`)

export const getPacket = (projectId?: number | null, budget?: number) =>
  request<Packet>('GET', `/api/packet${qs({ projectId, budget })}`)

export const runImport = (sinceDays?: number) =>
  request<ImportResult>('POST', '/api/import', sinceDays ? { sinceDays } : {})

export const getReport = (days = 30) => request<Report>('GET', `/api/report${qs({ days })}`)

export const getSettings = () => request<Settings>('GET', '/api/settings')

/** Kısmi güncelleme; sunucu bilinmeyen anahtarı ve yanlış türü 400 ile reddeder. Güncel ayarları döndürür. */
export const getAgents = () => request<AgentsReply>('GET', '/api/agents')
export const putSettings = (s: Partial<Settings>) => request<Settings>('PUT', '/api/settings', s)

/** Paket panoya kopyalanınca yerel sayaç için (karne ölçümü). */
export const markPacketCopied = (projectId?: number | null) =>
  request<{ ok: boolean }>('POST', `/api/packet/copied${qs({ projectId })}`)

