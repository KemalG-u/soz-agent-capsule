// WCAG 2.x karşıtlık hesabı: app.css tabanını ve themes.css içindeki 6 onaylı temanın (cam, terminal, mat, ton, duz, sicak) açık/koyu kiplerini denetler.
// Kullanım: node scripts/karsitlik.mjs [çıktı.md]
import { readFileSync, writeFileSync } from 'node:fs'

const appCss = readFileSync(new URL('../src/app.css', import.meta.url), 'utf8')
const themesCss = readFileSync(new URL('../src/themes.css', import.meta.url), 'utf8')

const grab = (block) => Object.fromEntries([...block.matchAll(/--([a-z0-9-]+):\s*(#[0-9a-f]{6})\b/gi)].map((m) => [m[1], m[2]]))

const baseLight = grab(appCss.match(/:root \{[\s\S]*?\n\}/)[0])
const baseDark = grab(appCss.match(/:root\[data-theme='dark'\] \{[\s\S]*?\n\}/)[0])

const FAMILIES = ['cam', 'terminal', 'mat', 'ton', 'duz', 'sicak']

function grabTheme(family, mode) {
  const re = mode === 'light'
    ? new RegExp(`:root\\[data-theme-family='${family}'\\]\\[data-theme='light'\\]\\s*\\{([\\s\\S]*?)\\n\\}`)
    : new RegExp(`:root\\[data-theme-family='${family}'\\]\\[data-theme='dark'\\]\\s*\\{([\\s\\S]*?)\\n\\}`)
  const m = themesCss.match(re)
  if (!m) throw new Error(`Tema bloğu bulunamadı: ${family} (${mode})`)
  return { ...(mode === 'light' ? baseLight : baseDark), ...grab(m[1]) }
}

const lum = (h) => {
  const c = [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16) / 255).map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4))
  return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]
}
const ratio = (a, b) => {
  const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p)
  return (x + 0.05) / (y + 0.05)
}

const pairs = [
  ['ink', 'bg', 'ana metin / zemin', 4.5], ['ink', 'surface', 'ana metin / kart', 4.5], ['ink', 'bg-sunken', 'ana metin / çukur zemin', 4.5],
  ['ink-2', 'bg', 'ikincil metin / zemin', 4.5], ['ink-2', 'surface', 'ikincil metin / kart', 4.5], ['ink-2', 'bg-sunken', 'ikincil metin / çukur zemin', 4.5],
  ['ink-3', 'bg', 'soluk metin / zemin', 4.5], ['ink-3', 'surface', 'soluk metin / kart', 4.5], ['ink-3', 'bg-sunken', 'soluk metin / çukur zemin', 4.5],
  ['on-primary', 'primary', 'ana düğme yazısı', 4.5],
  ['sel-fg', 'sel-bg', 'seçili satır metni', 4.5], ['sel-fg2', 'sel-bg', 'seçili satır ikincil metni', 4.5],
  ['ok', 'ok-tint', 'yeşil (geçti) çip', 4.5], ['ok', 'surface', 'yeşil / kart', 4.5], ['ok', 'bg', 'yeşil / zemin', 4.5],
  ['stalled', 'stalled-tint', 'kırmızı (kaldı) çip', 4.5], ['stalled', 'surface', 'kırmızı / kart', 4.5], ['stalled', 'bg', 'kırmızı / zemin', 4.5],
  ['waiting', 'waiting-tint', 'sarı (bekliyor) çip', 4.5], ['waiting', 'surface', 'sarı / kart', 4.5], ['waiting', 'bg', 'sarı / zemin', 4.5],
  ['working', 'working-tint', 'mavi (çalışıyor) çip', 4.5], ['working', 'surface', 'mavi / kart', 4.5], ['working', 'bg', 'mavi / zemin', 4.5],
  ['idle', 'idle-tint', 'gri çip', 4.5], ['idle', 'bg', 'gri / zemin', 4.5],
  ['ink', 'waiting-tint', 'ana metin / sarı karar kartı başlığı', 4.5], ['ink-2', 'waiting-tint', 'ikincil metin / sarı karar kartı', 4.5],
  ['ink', 'ok-tint', 'ana metin / yeşil kanıt başlığı', 4.5], ['ink-2', 'ok-tint', 'ikincil metin / yeşil kanıt başlığı', 4.5],
  ['ink', 'stalled-tint', 'ana metin / kırmızı kanıt başlığı', 4.5], ['ink-2', 'stalled-tint', 'ikincil metin / kırmızı kanıt başlığı', 4.5],
  ['ink', 'working-tint', 'ana metin / mavi zemin', 4.5], ['ink-2', 'idle-tint', 'ikincil metin / gri zemin', 4.5],
  ['line-strong', 'bg', 'çerçeve (arayüz) / zemin', 3], ['line-strong', 'surface', 'çerçeve (arayüz) / kart', 3], ['focus', 'bg', 'odak halkası / zemin', 3], ['focus', 'surface', 'odak halkası / kart', 3],
]

const variants = [
  ['TABAN AÇIK', baseLight],
  ['TABAN KOYU', baseDark],
  ...FAMILIES.flatMap((f) => [
    [`${f.toUpperCase()} AÇIK`, grabTheme(f, 'light')],
    [`${f.toUpperCase()} KOYU`, grabTheme(f, 'dark')],
  ]),
]

let out = '## Tema Karşıtlık Özeti (WCAG 2.2 AA)\n\n| Tema / Kip | Denetlenen Çift | En Düşük Metin | En Düşük Sınır/Odak | Sonuç |\n|---|---|---|---|---|\n'
let failures = []

for (const [name, th] of variants) {
  let minText = [99, '']
  let minUi = [99, '']
  let bad = 0
  for (const [f, b, what, need] of pairs) {
    if (!th[f] || !th[b]) {
      bad++
      failures.push(`${name}: eksik değişken ${f} (${th[f]}) / ${b} (${th[b]})`)
      continue
    }
    const r = ratio(th[f], th[b])
    if (need >= 4.5 && r < minText[0]) minText = [r, `${f}/${b}`]
    if (need < 4.5 && r < minUi[0]) minUi = [r, `${f}/${b}`]
    if (r < need) {
      bad++
      failures.push(`${name}: ${f} (${th[f]}) / ${b} (${th[b]}) [${what}] = ${r.toFixed(2)}:1 < ${need}:1`)
    }
  }
  out += `| ${name} | ${pairs.length} | ${minText[0].toFixed(2)}:1 (${minText[1]}) | ${minUi[0].toFixed(2)}:1 (${minUi[1]}) | ${bad === 0 ? 'GEÇTİ' : `${bad} KALDI`} |\n`
}

if (failures.length > 0) {
  out += '\n### Kalan Çiftler\n' + failures.map((x) => `- ${x}`).join('\n') + '\n'
  process.exitCode = 1
} else {
  out += `\nTüm ${variants.length} tema varyantında (${variants.length * pairs.length} renk çifti) 0 hata ile WCAG AA karşıtlık eşiği korundu.\n`
}

console.log(out)
if (process.argv[2]) writeFileSync(process.argv[2], out)
