// Yardımcıya henüz ulaşmamış mesajlar (GET /api/queue?pending=true). `queue` olayında yenilenir.
// Kendi yoklaması yok: Şu An sayfasının mevcut yoklaması loadPendingQueue'yu çağırır (C-15).
import { getQueue } from './api2'
import { onLive } from './live'
import type { QueueItem } from './types'
import { whenVisible } from './visible'

export const qp = $state<{ items: QueueItem[] }>({ items: [] })
let inFlight: Promise<void> | null = null

export function loadPendingQueue(): Promise<void> {
  inFlight ??= getQueue({ pending: true })
    .then((r) => { qp.items = Array.isArray(r) ? r.filter((x) => x.deliveredAt == null) : [] })
    .catch(() => { /* okunamazsa eski liste kalır */ })
    .finally(() => { inFlight = null })
  return inFlight
}

let timer: ReturnType<typeof setTimeout> | undefined
onLive((name) => {
  if (name !== 'queue') return
  clearTimeout(timer)
  timer = setTimeout(() => whenVisible(() => void loadPendingQueue()), 250)
})
