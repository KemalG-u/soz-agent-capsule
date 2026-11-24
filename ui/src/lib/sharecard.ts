// Paylaşılabilir kart: canvas ile çizilir (dış kütüphane yok). Varsayılan olarak YALNIZ sayılar.
export interface CardData {
  title: string
  stats: { value: string; label: string; accent?: boolean }[]
  sentence?: string
  footer: string
}

export const CARD_W = 1200
export const CARD_H = 630

const SANS = '"Segoe UI Variable Display", "Segoe UI", system-ui, -apple-system, Roboto, sans-serif'
const SERIF = '"Palatino Linotype", "Iowan Old Style", Palatino, Cambria, Georgia, serif'

function wrap(ctx: CanvasRenderingContext2D, text: string, maxWidth: number): string[] {
  const words = text.split(/\s+/)
  const lines: string[] = []
  let cur = ''
  for (const w of words) {
    const test = cur ? `${cur} ${w}` : w
    if (ctx.measureText(test).width > maxWidth && cur) {
      lines.push(cur)
      cur = w
    } else cur = test
  }
  if (cur) lines.push(cur)
  return lines
}

export function drawCard(canvas: HTMLCanvasElement, d: CardData) {
  canvas.width = CARD_W
  canvas.height = CARD_H
  const ctx = canvas.getContext('2d')
  if (!ctx) return
  const pad = 72

  ctx.fillStyle = '#131e27'
  ctx.fillRect(0, 0, CARD_W, CARD_H)

  // Başlık
  ctx.fillStyle = '#b4c0c8'
  ctx.font = `600 34px ${SANS}`
  ctx.textBaseline = 'alphabetic'
  ctx.fillText(d.title, pad, pad + 34)

  // İnce çizgi
  ctx.fillStyle = '#29353e'
  ctx.fillRect(pad, pad + 62, CARD_W - pad * 2, 2)

  // Sayılar
  const hasSentence = !!d.sentence
  const numY = hasSentence ? 250 : 350
  const numFont = `600 ${hasSentence ? 112 : 136}px ${SANS}`
  const labelFont = `500 32px ${SANS}`
  // Sayılar yan yana, genişliğe göre dizilir (üst üste binmez).
  const widths = d.stats.map((s) => {
    ctx.font = numFont
    const a = ctx.measureText(s.value).width
    ctx.font = labelFont
    return Math.max(a, ctx.measureText(s.label).width)
  })
  const total = widths.reduce((a, b) => a + b, 0)
  const gap = d.stats.length > 1 ? Math.min(96, (CARD_W - pad * 2 - total) / (d.stats.length - 1)) : 0
  let x = pad
  d.stats.forEach((s, i) => {
    ctx.fillStyle = s.accent ? '#f0ad4e' : '#f4f5f2'
    ctx.font = numFont
    ctx.fillText(s.value, x, numY)
    ctx.fillStyle = '#b4c0c8'
    ctx.font = labelFont
    ctx.fillText(s.label, x + 4, numY + 52)
    x += widths[i] + gap
  })

  // İsteğe bağlı tek cümle
  if (d.sentence) {
    ctx.fillStyle = '#f4f5f2'
    ctx.font = `italic 44px ${SERIF}`
    const lines = wrap(ctx, `“${d.sentence}”`, CARD_W - pad * 2).slice(0, 2)
    lines.forEach((ln, i) => ctx.fillText(ln, pad, 410 + i * 58))
  }

  // Alt yazı
  ctx.fillStyle = '#98a6b0'
  ctx.font = `500 28px ${SANS}`
  ctx.fillText(d.footer, pad, CARD_H - pad + 6)
}

export function canvasToBlob(canvas: HTMLCanvasElement): Promise<Blob | null> {
  return new Promise((resolve) => canvas.toBlob((b) => resolve(b), 'image/png'))
}
