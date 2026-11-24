// b7: yoklama kuralı. Sunucu tarafında her canlı hesap gerçek yardımcı komutlarını çalıştırır (pahalı), bu yüzden arayüz:
//   1) sekme/pencere GÖRÜNÜR DEĞİLKEN hiçbir şey istemez,
//   2) görünürken en çok 20 saniyede bir ister,
//   3) kullanıcı ekrana dönünce (görünürlük değişimi ya da odak) hemen bir kez ister,
//   4) görünmezken gelen "yenile" istekleri tutulur ve dönüşte tek seferde yapılır.
export const POLL_MS = 20000
/** Dönüşte art arda gelen iki olay (görünürlük + odak) tek istek sayılsın. */
const RETURN_GAP_MS = 1500

export const isVisible = (): boolean => typeof document === 'undefined' || document.visibilityState !== 'hidden'

type Fn = () => void
const returnHandlers = new Set<Fn>()
let wired = false
let lastReturn = 0

function fireReturn() {
  if (!isVisible()) return
  const now = Date.now()
  if (now - lastReturn < RETURN_GAP_MS) return
  lastReturn = now
  for (const h of [...returnHandlers]) {
    try { h() } catch { /* bir dinleyicinin hatası ötekini bozmasın */ }
  }
}

function wire() {
  if (wired || typeof document === 'undefined') return
  wired = true
  document.addEventListener('visibilitychange', fireReturn)
  window.addEventListener('focus', fireReturn)
}

/** Kullanıcı ekrana döndüğünde (görünür olunca ya da odak gelince) bir kez çağrılır. Dönüş: kaldırıcı. */
export function onReturn(fn: Fn): () => void {
  wire()
  returnHandlers.add(fn)
  return () => { returnHandlers.delete(fn) }
}

/** Görünürken `ms` aralıkla, dönüşte hemen bir kez. Görünmezken hiç çağrılmaz. Dönüş: durdurucu. */
export function pollWhileVisible(fn: Fn, ms: number = POLL_MS): () => void {
  const start = () => setInterval(() => { if (isVisible()) fn() }, ms)
  let iv = start()
  // Dönüşte hemen bir kez bakılır ve 20 sn sayacı yeniden başlar (dönüşten hemen sonra ikinci bir istek gelmesin).
  const off = onReturn(() => { fn(); clearInterval(iv); iv = start() })
  return () => { clearInterval(iv); off() }
}

/** Şimdi görünürse çağırır; değilse dönüşte tek seferde çağrılır (art arda gelen istekler birleşir). */
export function whenVisible(fn: Fn): void {
  if (isVisible()) { fn(); return }
  pending.add(fn)
  wire()
  if (!pendingWired) {
    pendingWired = true
    returnHandlers.add(flushPending)
  }
}
const pending = new Set<Fn>()
let pendingWired = false
function flushPending() {
  const list = [...pending]
  pending.clear()
  for (const f of list) {
    try { f() } catch { /* sessiz */ }
  }
}
