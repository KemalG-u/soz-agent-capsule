// Hareket: tek eğri (CSS --ease ile aynı: cubic-bezier(.2,.7,.2,1)), üç süre (140, 180, 220 ms).
// Yalnız durum değişimini anlatır. prefers-reduced-motion açıkken süre 0.
import type { TransitionConfig } from 'svelte/transition'

export const reduced = (): boolean => typeof matchMedia !== 'undefined' && matchMedia('(prefers-reduced-motion: reduce)').matches

function bezier(x1: number, y1: number, x2: number, y2: number): (t: number) => number {
  const cx = 3 * x1, bx = 3 * (x2 - x1) - cx, ax = 1 - cx - bx
  const cy = 3 * y1, by = 3 * (y2 - y1) - cy, ay = 1 - cy - by
  const sx = (t: number) => ((ax * t + bx) * t + cx) * t
  const sy = (t: number) => ((ay * t + by) * t + cy) * t
  const dx = (t: number) => (3 * ax * t + 2 * bx) * t + cx
  return (x: number) => {
    if (x <= 0) return 0
    if (x >= 1) return 1
    let t = x
    for (let i = 0; i < 6; i++) {
      const e = sx(t) - x
      if (Math.abs(e) < 1e-4) return sy(t)
      const d = dx(t)
      if (Math.abs(d) < 1e-6) break
      t -= e / d
    }
    let lo = 0, hi = 1
    t = x
    while (lo < hi) {
      const v = sx(t)
      if (Math.abs(v - x) < 1e-4) break
      if (x > v) lo = t
      else hi = t
      t = (hi - lo) / 2 + lo
      if (hi - lo < 1e-5) break
    }
    return sy(t)
  }
}

export const ease = bezier(0.2, 0.7, 0.2, 1)
export const DUR = { 1: 140, 2: 180, 3: 220 } as const

/** Liste satırı ve akış satırı girişi. */
export function rowIn(_node: Element, p: { y?: number } = {}): TransitionConfig {
  const y = p.y ?? 6
  return { duration: reduced() ? 0 : DUR[2], easing: ease, css: (t) => `opacity:${t};transform:translateY(${(1 - t) * y}px)` }
}

/** Liste satırı çıkışı (bir gruptan ötekine geçerken eski satır solar). */
export function rowOut(_node: Element): TransitionConfig {
  return { duration: reduced() ? 0 : DUR[1], easing: ease, css: (t) => `opacity:${t}` }
}

/** Ayrıntı paneli: seçili iş değişince içerik hafifçe kayarak gelir. */
export function paneIn(_node: Element): TransitionConfig {
  return { duration: reduced() ? 0 : DUR[2], easing: ease, css: (t) => `opacity:${0.4 + 0.6 * t};transform:translateX(${(1 - t) * 10}px)` }
}
