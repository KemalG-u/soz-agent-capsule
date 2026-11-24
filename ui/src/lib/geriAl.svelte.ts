// Silme sonrası "Geri al" (ölçüt ve koruma kuralı aynı kalıbı kullanır): silinen şey 5 sn tutulur, geri alınırsa yeniden kurulur.
export class GeriAl<T> {
  item = $state<T | null>(null)
  private timer: ReturnType<typeof setTimeout> | undefined
  arm(x: T, ms = 5000) {
    this.item = x
    clearTimeout(this.timer)
    this.timer = setTimeout(() => (this.item = null), ms)
  }
  /** Geri alınacak şeyi verir ve bildirimi kapatır. */
  take(): T | null {
    const x = this.item
    clearTimeout(this.timer)
    this.item = null
    return x
  }
  clear() {
    clearTimeout(this.timer)
    this.item = null
  }
}
