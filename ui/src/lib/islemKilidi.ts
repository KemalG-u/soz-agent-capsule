// Ortak istek kilidi: ekle / kaydet / gönder düğmeleri çift tıklamada iki istek göndermesin (A-DEF-11, A-X-18).
// Kullanım: const kilit = islemKilidi(); onclick={() => kilit.calistir(async () => {...})}; düğme disabled={kilit.mesgul}.
// `mesgul` düz alan olduğu için Svelte bileşeninde `let mesgul = $state(false)` ile birlikte kullanmak yerine `bekle` geri çağrısı verilir.
export function islemKilidi(onchange?: (busy: boolean) => void) {
  let busy = false
  return {
    get mesgul() { return busy },
    /** Bir iş sürerken ikinci çağrı sessizce yok sayılır (undefined döner). */
    async calistir<T>(fn: () => Promise<T>): Promise<T | undefined> {
      if (busy) return undefined
      busy = true
      onchange?.(true)
      try {
        return await fn()
      } finally {
        busy = false
        onchange?.(false)
      }
    },
  }
}
