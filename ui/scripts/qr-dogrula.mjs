// QR üretecinin kendi çözücüsüyle sınanması: biçim bitleri (BCH), RS sendromları sıfır mı, metin geri okunuyor mu.
// Dış tarayıcıyla çözmek değildir; yerleşim/maske/birleştirme tutarlılığını doğrular.
import { qrMatrix } from '../src/lib/qr.ts'

const ECC = [0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18]
const NB = [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4]
const raw = (v) => {
  let r = (16 * v + 128) * v + 64
  if (v >= 2) {
    const a = Math.floor(v / 7) + 2
    r -= (25 * a - 10) * a - 55
    if (v >= 7) r -= 36
  }
  return r
}
const mul = (x, y) => { let z = 0; for (let i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >>> 7) * 0x11d); z ^= ((y >>> i) & 1) * x } return z }
const inv = (mask, x, y) => [(x + y) % 2 === 0, y % 2 === 0, x % 3 === 0, (x + y) % 3 === 0, (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0, ((x * y) % 2) + ((x * y) % 3) === 0, ((((x * y) % 2) + ((x * y) % 3)) % 2) === 0, ((((x + y) % 2) + ((x * y) % 3)) % 2) === 0][mask]

function decode(m) {
  const size = m.length
  const v = (size - 17) / 4
  // biçim bilgisi (sol üst kopya)
  let f = 0
  const pos = [[8, 0], [8, 1], [8, 2], [8, 3], [8, 4], [8, 5], [8, 7], [8, 8], [7, 8], [5, 8], [4, 8], [3, 8], [2, 8], [1, 8], [0, 8]]
  pos.forEach(([x, y], i) => { if (m[y][x]) f |= 1 << i })
  f ^= 0x5412
  const data5 = f >>> 10
  let r = data5
  for (let i = 0; i < 10; i++) r = (r << 1) ^ ((r >>> 9) * 0x537)
  if (((data5 << 10) | r) !== f) return { ok: false, why: 'biçim BCH tutmuyor' }
  const mask = data5 & 7
  if (data5 >>> 3 !== 1) return { ok: false, why: 'ECC düzeyi L değil' }
  // işlev modülleri: yeniden kurmak yerine üreteçle aynı mantık (aynı koordinat kuralları) kullanılır
  const fn = Array.from({ length: size }, () => new Array(size).fill(false))
  const mark = (x, y) => { if (x >= 0 && y >= 0 && x < size && y < size) fn[y][x] = true }
  for (let i = 0; i < size; i++) { mark(6, i); mark(i, 6) }
  for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]]) for (let dy = -4; dy <= 4; dy++) for (let dx = -4; dx <= 4; dx++) mark(cx + dx, cy + dy)
  const n = v === 1 ? 0 : Math.floor(v / 7) + 2
  const step = v === 1 ? 0 : Math.ceil((v * 4 + 4) / (n * 2 - 2)) * 2
  const ap = v === 1 ? [] : [6]
  if (v > 1) for (let p = size - 7; ap.length < n; p -= step) ap.splice(1, 0, p)
  ap.forEach((cy, i) => ap.forEach((cx, j) => {
    if ((i === 0 && j === 0) || (i === 0 && j === ap.length - 1) || (i === ap.length - 1 && j === 0)) return
    for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) mark(cx + dx, cy + dy)
  }))
  for (let i = 0; i < 9; i++) { mark(8, i); mark(i, 8) }
  for (let i = 0; i < 8; i++) { mark(size - 1 - i, 8); mark(8, size - 1 - i) }
  if (v >= 7) for (let i = 0; i < 6; i++) for (let j = 0; j < 3; j++) { mark(size - 11 + j, i); mark(i, size - 11 + j) }
  // veri bitleri
  const bits = []
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5
    for (let vert = 0; vert < size; vert++) for (let j = 0; j < 2; j++) {
      const x = right - j
      const y = ((right + 1) & 2) === 0 ? size - 1 - vert : vert
      if (!fn[y][x]) bits.push(m[y][x] !== inv(mask, x, y) ? 1 : 0)
    }
  }
  const total = Math.floor(raw(v) / 8)
  const cw = []
  for (let i = 0; i < total; i++) cw.push(parseInt(bits.slice(i * 8, i * 8 + 8).join(''), 2))
  // ayrıştır
  const nb = NB[v], e = ECC[v]
  const shortCount = nb - (total % nb)
  const shortLen = Math.floor(total / nb)
  const blocks = Array.from({ length: nb }, () => [])
  let k = 0
  for (let i = 0; i < shortLen + 1; i++) for (let j = 0; j < nb; j++) {
    if (i === shortLen - e && j < shortCount) { /* kısa bloklarda bu sıra yok: ECC'ye kayar */ }
    const blockLen = shortLen + (j < shortCount ? 0 : 1)
    const dataLen = blockLen - e
    if (i < dataLen) blocks[j].push(cw[k++])
  }
  for (let i = 0; i < e; i++) for (let j = 0; j < nb; j++) blocks[j].push(cw[k++])
  // sendromlar
  for (const b of blocks) {
    for (let s = 0, a = 1; s < e; s++, a = mul(a, 2)) {
      let acc = 0
      for (const c of b) acc = mul(acc, a) ^ c
      if (acc !== 0) return { ok: false, why: `RS sendromu sıfır değil (blok uzunluğu ${b.length})` }
    }
  }
  const dataBytes = blocks.flatMap((b) => b.slice(0, b.length - e))
  const stream = dataBytes.map((x) => x.toString(2).padStart(8, '0')).join('')
  if (stream.slice(0, 4) !== '0100') return { ok: false, why: 'kip bayt değil' }
  const cb = v <= 9 ? 8 : 16
  const len = parseInt(stream.slice(4, 4 + cb), 2)
  const out = []
  for (let i = 0; i < len; i++) out.push(parseInt(stream.slice(4 + cb + i * 8, 12 + cb + i * 8), 2))
  return { ok: true, version: v, mask, text: new TextDecoder().decode(new Uint8Array(out)) }
}

const samples = [
  'a',
  'http://192.168.1.23:49301/t/?k=9f2c41d07a6be385c0d1e7a24b8f5963',
  'http://192.168.100.200:49301/t/?k=9f2c41d07a6be385c0d1e7a24b8f5963abcdef0123456789abcdef0123456789',
  'Söz telefon eşleştirme: ' + 'x'.repeat(120),
  'y'.repeat(200),
  'z'.repeat(271),
]
let fail = 0
for (const s of samples) {
  const m = qrMatrix(s)
  if (!m) { console.log('ÜRETİLEMEDİ', s.length); fail++; continue }
  const d = decode(m)
  const pass = d.ok && d.text === s
  if (!pass) fail++
  console.log(`${pass ? 'GEÇTİ' : 'KALDI'} ${s.length} bayt → sürüm ${d.version ?? '?'} maske ${d.mask ?? '?'} ${d.ok ? '' : d.why}`)
}
console.log('272 bayt (sınır dışı) →', qrMatrix('w'.repeat(272)) === null ? 'null (beklenen)' : 'ÜRETTİ (hata)')
console.log(fail === 0 ? 'HEPSİ GEÇTİ' : `${fail} KALDI`)
process.exit(fail ? 1 : 0)
