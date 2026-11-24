// Küçük QR üretici (kütüphane yok): bayt kipi, hata düzeltme düzeyi L, sürüm 1-10 (en çok 271 bayt).
// Telefon eşleştirme adresi (~70 karakter) için yeterlidir. Çıktı: true = koyu modül.

const ECC_PER_BLOCK = [0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18]
const NUM_BLOCKS = [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4]

function rawModules(v: number): number {
  let r = (16 * v + 128) * v + 64
  if (v >= 2) {
    const a = Math.floor(v / 7) + 2
    r -= (25 * a - 10) * a - 55
    if (v >= 7) r -= 36
  }
  return r
}

const dataCodewords = (v: number) => Math.floor(rawModules(v) / 8) - ECC_PER_BLOCK[v] * NUM_BLOCKS[v]

function gfMul(x: number, y: number): number {
  let z = 0
  for (let i = 7; i >= 0; i--) {
    z = (z << 1) ^ ((z >>> 7) * 0x11d)
    z ^= ((y >>> i) & 1) * x
  }
  return z
}

function rsDivisor(degree: number): number[] {
  const r: number[] = new Array(degree).fill(0)
  r[degree - 1] = 1
  let root = 1
  for (let i = 0; i < degree; i++) {
    for (let j = 0; j < r.length; j++) {
      r[j] = gfMul(r[j], root)
      if (j + 1 < r.length) r[j] ^= r[j + 1]
    }
    root = gfMul(root, 2)
  }
  return r
}

function rsRemainder(data: number[], divisor: number[]): number[] {
  const r: number[] = new Array(divisor.length).fill(0)
  for (const b of data) {
    const f = b ^ (r.shift() as number)
    r.push(0)
    divisor.forEach((c, i) => (r[i] ^= gfMul(c, f)))
  }
  return r
}

function alignPositions(v: number): number[] {
  if (v === 1) return []
  const n = Math.floor(v / 7) + 2
  const size = v * 4 + 17
  const step = Math.ceil((v * 4 + 4) / (n * 2 - 2)) * 2
  const r = [6]
  for (let pos = size - 7; r.length < n; pos -= step) r.splice(1, 0, pos)
  return r
}

export function qrMatrix(text: string): boolean[][] | null {
  const bytes = Array.from(new TextEncoder().encode(text))
  let v = 1
  for (; v <= 10; v++) {
    const countBits = v <= 9 ? 8 : 16
    if (4 + countBits + bytes.length * 8 <= dataCodewords(v) * 8) break
  }
  if (v > 10) return null
  const size = v * 4 + 17

  // 1) bit akışı
  const bits: number[] = []
  const push = (val: number, len: number) => {
    for (let i = len - 1; i >= 0; i--) bits.push((val >>> i) & 1)
  }
  push(0b0100, 4)
  push(bytes.length, v <= 9 ? 8 : 16)
  bytes.forEach((b) => push(b, 8))
  const cap = dataCodewords(v) * 8
  push(0, Math.min(4, cap - bits.length))
  while (bits.length % 8) bits.push(0)
  for (let pad = 0xec; bits.length < cap; pad ^= 0xec ^ 0x11) push(pad, 8)
  const data: number[] = []
  for (let i = 0; i < bits.length; i += 8) data.push(parseInt(bits.slice(i, i + 8).join(''), 2))

  // 2) bloklar + hata düzeltme + sıralı birleştirme
  const nb = NUM_BLOCKS[v]
  const eccLen = ECC_PER_BLOCK[v]
  const raw = Math.floor(rawModules(v) / 8)
  const shortCount = nb - (raw % nb)
  const shortLen = Math.floor(raw / nb)
  const div = rsDivisor(eccLen)
  const blocks: number[][] = []
  for (let i = 0, k = 0; i < nb; i++) {
    const dat = data.slice(k, k + shortLen - eccLen + (i < shortCount ? 0 : 1))
    k += dat.length
    const ecc = rsRemainder(dat, div)
    if (i < shortCount) dat.push(0)
    blocks.push(dat.concat(ecc))
  }
  const all: number[] = []
  for (let i = 0; i < blocks[0].length; i++) {
    blocks.forEach((b, j) => {
      if (i !== shortLen - eccLen || j >= shortCount) all.push(b[i])
    })
  }

  // 3) çizim
  const m: boolean[][] = Array.from({ length: size }, () => new Array(size).fill(false))
  const fn: boolean[][] = Array.from({ length: size }, () => new Array(size).fill(false))
  const setF = (x: number, y: number, dark: boolean) => {
    m[y][x] = dark
    fn[y][x] = true
  }
  for (let i = 0; i < size; i++) {
    setF(6, i, i % 2 === 0)
    setF(i, 6, i % 2 === 0)
  }
  const finder = (cx: number, cy: number) => {
    for (let dy = -4; dy <= 4; dy++)
      for (let dx = -4; dx <= 4; dx++) {
        const d = Math.max(Math.abs(dx), Math.abs(dy))
        const x = cx + dx
        const y = cy + dy
        if (x >= 0 && x < size && y >= 0 && y < size) setF(x, y, d !== 2 && d !== 4)
      }
  }
  finder(3, 3)
  finder(size - 4, 3)
  finder(3, size - 4)
  const ap = alignPositions(v)
  const last = ap.length - 1
  ap.forEach((cy, i) =>
    ap.forEach((cx, j) => {
      if ((i === 0 && j === 0) || (i === 0 && j === last) || (i === last && j === 0)) return
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) setF(cx + dx, cy + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1)
    }),
  )

  const drawFormat = (mask: number) => {
    const d = (1 << 3) | mask // L = 01
    let r = d
    for (let i = 0; i < 10; i++) r = (r << 1) ^ ((r >>> 9) * 0x537)
    const b = ((d << 10) | r) ^ 0x5412
    const bit = (i: number) => ((b >>> i) & 1) !== 0
    for (let i = 0; i <= 5; i++) setF(8, i, bit(i))
    setF(8, 7, bit(6))
    setF(8, 8, bit(7))
    setF(7, 8, bit(8))
    for (let i = 9; i < 15; i++) setF(14 - i, 8, bit(i))
    for (let i = 0; i < 8; i++) setF(size - 1 - i, 8, bit(i))
    for (let i = 8; i < 15; i++) setF(8, size - 15 + i, bit(i))
    setF(8, size - 8, true)
  }
  drawFormat(0) // alanı işaretler; gerçek maske sonra yazılır
  if (v >= 7) {
    let r = v
    for (let i = 0; i < 12; i++) r = (r << 1) ^ ((r >>> 11) * 0x1f25)
    const b = (v << 12) | r
    for (let i = 0; i < 18; i++) {
      const dark = ((b >>> i) & 1) !== 0
      const a = size - 11 + (i % 3)
      const c = Math.floor(i / 3)
      setF(a, c, dark)
      setF(c, a, dark)
    }
  }

  // 4) veri yerleştirme (zikzak)
  let bi = 0
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5
    for (let vert = 0; vert < size; vert++)
      for (let j = 0; j < 2; j++) {
        const x = right - j
        const upward = ((right + 1) & 2) === 0
        const y = upward ? size - 1 - vert : vert
        if (!fn[y][x] && bi < all.length * 8) {
          m[y][x] = ((all[bi >>> 3] >>> (7 - (bi & 7))) & 1) !== 0
          bi++
        }
      }
  }

  // 5) maske seçimi
  const inv = (mask: number, x: number, y: number): boolean => {
    switch (mask) {
      case 0: return (x + y) % 2 === 0
      case 1: return y % 2 === 0
      case 2: return x % 3 === 0
      case 3: return (x + y) % 3 === 0
      case 4: return (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0
      case 5: return ((x * y) % 2) + ((x * y) % 3) === 0
      case 6: return ((((x * y) % 2) + ((x * y) % 3)) % 2) === 0
      default: return ((((x + y) % 2) + ((x * y) % 3)) % 2) === 0
    }
  }
  const apply = (mask: number) => {
    for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (!fn[y][x] && inv(mask, x, y)) m[y][x] = !m[y][x]
  }
  const penalty = (): number => {
    let p = 0
    const lines = [m, Array.from({ length: size }, (_, x) => m.map((row) => row[x]))]
    for (const grid of lines)
      for (const row of grid) {
        let run = 1
        for (let i = 1; i <= size; i++) {
          if (i < size && row[i] === row[i - 1]) run++
          else {
            if (run >= 5) p += 3 + (run - 5)
            run = 1
          }
        }
        const s = row.map((c) => (c ? '1' : '0')).join('')
        for (const pat of ['10111010000', '00001011101']) {
          let at = s.indexOf(pat)
          while (at >= 0) {
            p += 40
            at = s.indexOf(pat, at + 1)
          }
        }
      }
    for (let y = 0; y < size - 1; y++)
      for (let x = 0; x < size - 1; x++) if (m[y][x] === m[y][x + 1] && m[y][x] === m[y + 1][x] && m[y][x] === m[y + 1][x + 1]) p += 3
    const dark = m.flat().filter(Boolean).length
    p += Math.floor(Math.abs((dark * 20) - size * size * 10) / (size * size)) * 10
    return p
  }
  let best = 0
  let bestP = Infinity
  for (let mask = 0; mask < 8; mask++) {
    apply(mask)
    drawFormat(mask)
    const p = penalty()
    if (p < bestP) {
      bestP = p
      best = mask
    }
    apply(mask) // geri al
  }
  apply(best)
  drawFormat(best)
  return m
}

/** SVG yol verisi (tek path): koyu modüller 1x1 kare; sessiz bölge 4 modül. */
export function qrPath(matrix: boolean[][]): { d: string; size: number } {
  const n = matrix.length
  let d = ''
  for (let y = 0; y < n; y++)
    for (let x = 0; x < n; x++) if (matrix[y][x]) d += `M${x + 4} ${y + 4}h1v1h-1z`
  return { d, size: n + 8 }
}
