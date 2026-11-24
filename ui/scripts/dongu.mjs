// Döngü sınaması (s4 ağır bulgusu): Ayarlar açıkken /api/registry ve /api/settings saniyede onlarca kez istenmemeli (etki kendini tetikliyordu).
// Gerçek sunucu YOK: statik arayüz + sahte /api cevapları (Playwright route). 6 sn boyunca istek sayılır.
// Kullanım: node ui\scripts\dongu.mjs [www klasörü]   (varsayılan _gecici\f1\www). Tek tarayıcı, çıkışta kendi Edge'ini PID ile kapatır (temizlik.mjs).
import { createReadStream, existsSync, statSync } from 'node:fs'
import http from 'node:http'
import { extname, join, normalize } from 'node:path'
import { fileURLToPath } from 'node:url'
import { launch } from './pw.mjs'
import { kapatVeDogrula, portKaydet, temizlikKur } from './temizlik.mjs'

temizlikKur({ sure: 240 })
// Ürün kökü: bu betik ui/scripts altında durur; SOZ_URUN ile başka bir klasör verilebilir.
const URUN_KOK = process.env.SOZ_URUN ?? fileURLToPath(new URL('../..', import.meta.url)).replaceAll('\\', '/').replace(/\/$/, '')
const WWW = process.argv[2] ?? `${URUN_KOK}/_gecici/f1/www`
const PORT = Number(process.env.DONGU_PORT ?? 49631)
const SURE_MS = 6000
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' }
const sleep = (n) => new Promise((r) => setTimeout(r, n))

const srv = http.createServer((req, res) => {
  let f = join(WWW, normalize(decodeURIComponent(req.url.split('?')[0])))
  if (!existsSync(f) || statSync(f).isDirectory()) f = join(f, 'index.html')
  if (!existsSync(f)) { res.statusCode = 404; res.end(); return }
  res.setHeader('content-type', MIME[extname(f)] ?? 'application/octet-stream')
  createReadStream(f).pipe(res)
})
await new Promise((r) => srv.listen(PORT, '127.0.0.1', r))
portKaydet(PORT)

const rows = []
const ok = (ad, gecti, ayrinti = '') => { rows.push({ ad, gecti }); console.log(`${gecti ? 'GEÇTİ' : 'KALDI'} | ${ad} | ${ayrinti}`) }

const ENTRY = { id: 'attention.daily-signals', kind: 'setting', state: 'enabled', title: { tr: 'Günlük bildirim sayısı', en: null }, description: { tr: 'Bir günde en çok kaç bildirim.', en: null }, keywords: [], body: { group: 'genel', order: 1, control: 'number', legacyKey: 'attentionDailySignals', default: 5, schema: { type: 'integer', minimum: 0, maximum: 20 } } }

async function kos(ad, { registryDurum }) {
  const { ctx } = await launch()
  const sayac = { registry: 0, settings: 0, diger: 0 }
  try {
    const page = await ctx.newPage()
    await page.route('**/api/**', async (r) => {
      const u = new URL(r.request().url())
      const p = u.pathname
      const json = (b, status = 200) => r.fulfill({ status, contentType: 'application/json', body: JSON.stringify(b) })
      if (p === '/api/session') return json({ token: 't', version: 'sahte', dataDir: 'x' })
      if (p === '/api/registry') { sayac.registry++; return registryDurum === 500 ? json({ error: 'internal registry failure' }, 500) : json({ entries: [ENTRY] }) }
      if (p === '/api/settings') { sayac.settings++; return registryDurum === 500 ? json({ error: 'İç hata' }, 500) : json({ attentionDailySignals: 5, language: 'tr', theme: 'light' }) }
      sayac.diger++
      if (/\/(jobs|projects|queue|decisions|events|sessions)\b/.test(p)) return json(p.includes('jobs') ? { items: [], total: 0, hasMore: false } : [])
      return json({})
    })
    await page.goto(`http://127.0.0.1:${PORT}/#/ayarlar`)
    await sleep(SURE_MS)
    const toplam = sayac.registry + sayac.settings
    // Sağlam durumda en çok birkaç istek (açılış + olay yenilemesi), hatada artan bekleme yüzünden daha da az.
    const sinir = registryDurum === 500 ? 8 : 12
    ok(`${ad}: ${SURE_MS / 1000} sn içinde /api/registry + /api/settings isteği en çok ${sinir}`, toplam <= sinir, `registry ${sayac.registry}, settings ${sayac.settings}, diğer ${sayac.diger}`)
    if (registryDurum === 500) {
      const govde = await page.evaluate(() => document.body.innerText)
      ok(`${ad}: ekranda ham "internal registry failure" / "İç hata" yok`, !/internal registry failure|İç hata/i.test(govde), govde.slice(0, 120).replace(/\s+/g, ' '))
    }
  } finally {
    await ctx.close().catch(() => {})
  }
}

let kod = 0
try {
  await kos('sağlam sunucu', { registryDurum: 200 })
  await kos('sunucu 500 veriyor', { registryDurum: 500 })
} catch (e) {
  console.log('HATA', e?.stack ?? e)
  kod = 1
} finally {
  srv.close()
  const t = kapatVeDogrula()
  const kaldi = rows.filter((r) => !r.gecti).length
  console.log(`\ndongu: ${rows.length - kaldi} geçti, ${kaldi} kaldı`)
  process.exitCode = kod || (kaldi ? 1 : 0) || (t.kalan.length || t.dinleyen.length ? 3 : 0)
}
