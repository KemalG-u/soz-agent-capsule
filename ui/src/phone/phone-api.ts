// Telefon sayfasının tek ağ katmanı. Yalnız /api/phone/* uçlarını çağırır (yerel ağdan başka uca erişim yoktur).
// Kimlik: eşleştirme adresindeki kodla sunucunun koyduğu HttpOnly çerez; sayfa kodu görmez, belirteç taşımaz.
export class PhoneError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

export interface PhoneItem {
  sessionId: number
  projectId: number | null
  projectName: string | null
  tool: string
  since: string
  lastAssistantText: string | null
  asksQuestion: boolean
}

// Bilgisayardaki "Sıra bende" ile aynı sıra: sunucu priority verir (1 en önce), telefon kendi sırasını icat etmez.
export interface PhoneAttention {
  id: string
  kind: 'ask' | 'accept' | 'blocked' | 'done' | 'uncertain'
  reason: string
  priority: number
  title: string | null
  sentence: string | null
  detail: string | null
  projectName: string | null
  sessionId: number | null
  jobId: number | null
  at: string
  confidence: string
}

export interface PhoneNow {
  sentence?: string | null
  items?: PhoneAttention[]
  waiting: PhoneItem[]
  morning: {
    passed: unknown[] | number
    failed: unknown[] | number
    unchecked: number
    guardStops: number
    usage?: { estimatedUsd: number | null }
  }
}

const sample = /[?&](ornek|bos)=1/.test(location.search)
const unpaired = /[?&]eslesmemis=1/.test(location.search)
const empty = /[?&]bos=1/.test(location.search)
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms))
const ago = (m: number) => new Date(Date.now() - m * 60_000).toISOString()

const sampleSent: { sessionId: number | null; text: string }[] = []

async function mock(method: string, path: string): Promise<unknown> {
  await wait(80)
  if (unpaired) throw new PhoneError(401, 'eşleşmemiş')
  if (path === '/api/phone/now') {
    return {
      sentence: empty ? 'Şu an seni bekleyen bir şey yok.' : '1 soru, 1 kabul, 1 bitti seni bekliyor.',
      items: empty
        ? []
        : [
            { id: 'a1', kind: 'ask', reason: 'session_question', priority: 1, title: 'Bahçe Takip', sentence: 'Bahçe Takip penceresinde yardımcı sana bir soru sordu.', detail: 'Hafta sonları da gönderilsin mi?', projectName: 'Bahçe Takip', sessionId: 201, jobId: null, at: ago(25), confidence: 'firm' },
            { id: 'a2', kind: 'accept', reason: 'decision', priority: 2, title: 'Rapor', sentence: 'Rapor işinde deneme sonucu sayılamadı, kabul edip etmeyeceğine sen karar vereceksin.', detail: null, projectName: 'Atölye Sipariş', sessionId: null, jobId: 7, at: ago(60), confidence: 'firm' },
            { id: 'a3', kind: 'done', reason: 'session_done', priority: 4, title: 'Atölye Sipariş', sentence: 'Atölye Sipariş çalışması bitti.', detail: null, projectName: 'Atölye Sipariş', sessionId: 202, jobId: null, at: ago(180), confidence: 'firm' },
          ],
      waiting: empty
        ? []
        : [
            { sessionId: 201, projectId: 2, projectName: 'Bahçe Takip', tool: 'claude', since: ago(25), lastAssistantText: 'Hatırlatma saatini sabah 08:00 olarak ayarladım. Hafta sonları da gönderilsin mi, yoksa yalnız hafta içi mi?', asksQuestion: true },
            { sessionId: 202, projectId: 3, projectName: 'Atölye Sipariş', tool: 'codex', since: ago(180), lastAssistantText: 'Sipariş listesi ekranı tamamlandı, her şey hazır. İstersen kargo takibi sayfasına geçebilirim.', asksQuestion: false },
          ],
      morning: { passed: empty ? 0 : 2, failed: empty ? 0 : 1, unchecked: empty ? 0 : 1, guardStops: empty ? 0 : 3, usage: { estimatedUsd: empty ? null : 4.82 } },
    } satisfies PhoneNow
  }
  if (path === '/api/phone/queue' && method === 'POST') return { ok: true }
  throw new PhoneError(404, 'yok')
}

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  if (sample) {
    if (method === 'POST') sampleSent.push(body as { sessionId: number | null; text: string })
    return (await mock(method, path)) as T
  }
  let res: Response
  try {
    res = await fetch(path, {
      method,
      credentials: 'same-origin',
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new PhoneError(0, 'Bilgisayara ulaşılamadı')
  }
  const text = await res.text()
  let data: unknown = null
  try {
    data = text ? JSON.parse(text) : null
  } catch {
    data = null
  }
  if (!res.ok) {
    const msg = data && typeof data === 'object' ? String((data as Record<string, unknown>).error ?? res.statusText) : res.statusText
    throw new PhoneError(res.status, msg)
  }
  return data as T
}

export const phoneNow = () => call<PhoneNow>('GET', '/api/phone/now')
export const phoneQueue = (b: { sessionId: number | null; projectId: number | null; text: string }) => call<unknown>('POST', '/api/phone/queue', b)
