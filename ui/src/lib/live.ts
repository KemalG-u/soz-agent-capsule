// Küçük olay ayrıştırıcı: gerçek SSE (store) ve sahte katman (mock2) aynı kanaldan konuşur.
// Başka hiçbir modülü içe almaz; döngüsel bağımlılık çıkmaz.
export type LiveName = 'import' | 'proposal' | 'repeat' | 'session' | 'decision' | 'job' | 'proof' | 'guard' | 'queue' | 'job.progress' | 'job.state' | 'attention'
export type LiveHandler = (name: LiveName, data: unknown) => void

const handlers = new Set<LiveHandler>()

export function onLive(h: LiveHandler): () => void {
  handlers.add(h)
  return () => handlers.delete(h)
}

export function emitLive(name: LiveName, data: unknown = null) {
  for (const h of handlers) {
    try {
      h(name, data)
    } catch {
      // bir dinleyicinin hatası ötekini bozmasın
    }
  }
}
