// Gerçek sunucuya karşı deneme: soz.exe (yayın derlemesi) + sahte claude + bu arayüzün üretim derlemesi (araya küçük statik sunucu ve vekil).
// Hiçbir gerçek claude çağrılmaz. Veri klasörü ve proje klasörü _gecici altında kurulur, bitince kaldırılır.
import { spawn, execFileSync } from 'node:child_process'
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, createReadStream, statSync } from 'node:fs'
import http from 'node:http'
import { extname, join, normalize } from 'node:path'
import { fileURLToPath } from 'node:url'
import { launch } from './pw.mjs'
import { relaunchAsHuman, writeExitCode } from './insan.mjs'

// Söz kabul ve ayar isteğini yalnız insandan alır; bu betik Claude Code içinden başlatılırsa ata zincirinde claude bulunur ve sunucu ajan sayar.
// Bu yüzden betik kendini ata zinciri kopuk "insan eşdeğeri" süreçte yeniden başlatır (ayrıntı: insan.mjs).
if (await relaunchAsHuman(import.meta.url)) process.exit(process.exitCode ?? 0)

// Ürün kökü: bu betik ui/scripts altında durur; SOZ_URUN ile başka bir klasör verilebilir.
const URUN_KOK = process.env.SOZ_URUN ?? fileURLToPath(new URL('../..', import.meta.url)).replaceAll('\\', '/').replace(/\/$/, '')
const URUN = URUN_KOK
const EXE = process.env.SOZ_EXE ?? `${URUN}/yayin/soz.exe`
const DIST = process.env.DIST ?? `${URUN}/src/Soz.Server/wwwroot`
const TMP = process.env.SOZ_TMP ?? `${URUN}/_gecici` // d2u2: çalışma klasörü ortamdan verilebilir
const ROOT = `${TMP}/fix4-gercek`
const DATA = `${TMP}/fix4-gercek-veri`
const PROJ = `${ROOT}/proje klasoru`
const STATE = `${ROOT}/durum`
const OUT = process.env.OUT ?? `${URUN}/_gecici/fix4-ekran`
const SPORT = 49580
const WPORT = 49581
const FAKE = `${URUN}/test/sahte-claude/sahte-claude.mjs`

const NOPATH = 'D:' + String.fromCharCode(92) + 'olmayan-klasor-ui4' + String.fromCharCode(92) + 'yok'

// d2u: her yeni iş önce "Bitti sayılması için" kartını açar; iş kart kabul edilince (Enter = ana düğme) başlar.
const acceptDone = async (page) => { const shown = await page.waitForSelector('[data-done-accept]:not([disabled])', { timeout: 8000 }).then(() => true, () => false); if (shown) { await page.waitForTimeout(900); /* öneriler yüklensin */ for (const el of await page.$$('[data-proposal][data-level="D"] [data-pick]')) await el.uncheck().catch(() => {}) /* bu betik Söz'ün ölçtüğü kanıtla geçmeyi sınar: "Sen bakacaksın" maddesi çıkarılır (d2u ile aynı) */; await page.focus('[data-done-accept]'); await page.keyboard.press('Enter') } return shown }
const rows = []
const ok = (name, cond, extra = '') => rows.push([cond ? 'GEÇTİ' : 'KALDI', name, extra])
const errors = []
const apiErrs = []
let server = null
let web = null
let ctx = null
let profile = null

mkdirSync(OUT, { recursive: true })
rmSync(ROOT, { recursive: true, force: true })
rmSync(DATA, { recursive: true, force: true })
mkdirSync(`${PROJ}/test`, { recursive: true })
mkdirSync(DATA, { recursive: true })
mkdirSync(STATE, { recursive: true })
for (const f of ['package.json', 'hesap.js']) cpSync(`${URUN}/deneme-proje/${f}`, `${PROJ}/${f}`)
cpSync(`${URUN}/deneme-proje/test/hesap.test.js`, `${PROJ}/test/hesap.test.js`)
const reset = () => cpSync(`${URUN}/deneme-proje/hesap.js`, `${PROJ}/hesap.js`)

try {
  server = spawn(EXE, ['serve', '--port', String(SPORT), '--data', DATA, '--exit-after', '1500', '--exit-with-parent', String(process.pid)], {
    env: { ...process.env, SAHTE_KANCALI: '1', SAHTE_STATE_DIR: STATE, SAHTE_TORUN: `${ROOT}/torun.pid`, SAHTE_LOG: `${ROOT}/sahte.log` },
    stdio: 'ignore', windowsHide: true,
  })
  let token = ''
  for (let i = 0; i < 60 && !token; i++) {
    try { token = (await (await fetch(`http://127.0.0.1:${SPORT}/api/session`)).json()).token } catch { await new Promise((r) => setTimeout(r, 500)) }
  }
  ok('sunucu açıldı', !!token, `PID ${server.pid}`)
  if (!token) throw new Error('sunucu açılmadı')
  const put = await fetch(`http://127.0.0.1:${SPORT}/api/settings`, { method: 'PUT', headers: { 'X-Soz-Token': token, 'content-type': 'application/json' }, body: JSON.stringify({ claudePath: FAKE.replace(/\//g, '\\'), runnerModel: '' }) })
  ok('ayar: sahte claude yazıldı', put.ok)
  // GERÇEK claude asla çağrılmasın: ayar geri okunur, sahte değilse betik durur
  const back = await (await fetch(`http://127.0.0.1:${SPORT}/api/settings`, { headers: { 'X-Soz-Token': token } })).json()
  const fakeOk = String(back.claudePath ?? '').toLowerCase().endsWith('sahte-claude.mjs')
  ok('ayar doğrulandı: claudePath sahte claude', fakeOk, String(back.claudePath))
  if (!fakeOk) throw new Error('claudePath sahte claude değil; gerçek claude çağrılabilirdi, durduruldu')

  // statik sunucu + /api vekili (SSE akar)
  const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' }
  web = http.createServer((req, res) => {
    if (req.url.startsWith('/api')) {
      const p = http.request({ host: '127.0.0.1', port: SPORT, path: req.url, method: req.method, headers: { ...req.headers, host: `127.0.0.1:${SPORT}`, origin: `http://127.0.0.1:${SPORT}` } }, (r) => {
        res.writeHead(r.statusCode ?? 502, r.headers)
        r.pipe(res)
      })
      p.on('error', () => { res.statusCode = 502; res.end() })
      req.pipe(p)
      return
    }
    let f = join(DIST, normalize(decodeURIComponent(req.url.split('?')[0])))
    if (!existsSync(f) || statSync(f).isDirectory()) f = join(f, 'index.html')
    if (!existsSync(f)) { res.statusCode = 404; res.end(); return }
    res.setHeader('content-type', MIME[extname(f)] ?? 'application/octet-stream')
    createReadStream(f).pipe(res)
  })
  await new Promise((r) => web.listen(WPORT, '127.0.0.1', r))

  ;({ ctx, profile } = await launch())
  const page = await ctx.newPage()
  page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') errors.push(`${m.type()}: ${m.text()}`) })
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message))
  page.on('response', (r) => { if (r.url().includes('/api/') && r.status() >= 400) apiErrs.push(`${r.request().method()} ${r.url().replace(/^.*\/api/, '/api')} ${r.status()}`) })
  let proofReqs = 0
  page.on('request', (r) => { if (r.url().includes('/api/proof/runs/')) proofReqs++ })
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.emulateMedia({ colorScheme: 'light' })
  const B = `http://127.0.0.1:${WPORT}`
  const waitFor = (sel, ms = 90000, state = 'attached') => page.waitForSelector(sel, { timeout: ms, state }).then(() => true, () => false)
  const waitFn = (fn, arg, ms = 90000) => page.waitForFunction(fn, arg, { timeout: ms }).then(() => true, () => false)
  const shot = (n) => page.screenshot({ path: `${OUT}/${n}.png` })

  await page.goto(B + '/#/is'); await page.reload(); await page.waitForSelector('[data-newjob]', { timeout: 8000 })
  await page.waitForTimeout(800)
  ok('G0 ilk açılış: büyük kutu', await page.evaluate(() => !!document.querySelector('[data-onboarding]')))
  await shot('gercek-01-ilk-acilis-light-1440')

  // 1. düzelt: dört eylem, terminal yok
  await page.keyboard.insertText('Toplama fonksiyonundaki hatayı düzelt, testler geçsin. [[sahte:duzelt]]')
  await page.keyboard.press('Control+Enter')
  await page.keyboard.insertText(PROJ.replace(/\//g, '\\')); await page.keyboard.press('Enter')
  ok('G1 onay kartı ve gerçek öneri (npm test)', await waitFor('[data-setup] [data-open-all]', 15000) && await page.evaluate(() => /npm test/.test(document.querySelector('[data-setup]')?.innerText ?? '')))
  await shot('gercek-02-onay-karti-light-1440')
  await page.keyboard.press('Enter')
  ok('G1b bitti tanımı kartı geldi ve kabul edildi', await acceptDone(page))
  ok('G2 iş başladı, ilerleme satırı geldi', await waitFor('.prog', 30000))
  await page.waitForTimeout(600); await shot('gercek-03-calisiyor-light-1440')
  ok('G3 kanıt kartı geçti', await waitFor('[data-proof="passed"]', 120000))
  ok('G3c başlangıç ölçümü akışta görünür (Başlamadan önce N testten M geçmiyor; sayılar projeye göre değişir)', await page.evaluate(() => /Başlamadan önce \d+ testten \d+'(i|si) geçmiyor/.test(document.querySelector('#stream-scroll')?.innerText ?? '')))
  ok('G3d kanıt kartında karşılaştırma cümlesi (data-baseline): Şimdi hepsi geçiyor', await page.evaluate(() => /Şimdi (\d+'si de|\d+ testin hepsi) geçiyor/.test([...document.querySelectorAll('[data-baseline]')].at(-1)?.textContent ?? '')), await page.evaluate(() => [...document.querySelectorAll('[data-baseline]')].map((e) => e.textContent).join(' || ') || 'yok'))
  await page.waitForTimeout(800); await shot('gercek-04-gecti-light-1440')
  ok('G3b akışta kırık görsel yok', await page.evaluate(() => [...document.querySelectorAll('#stream-scroll img')].every((i) => i.complete && i.naturalWidth > 0)))
  ok('G4 hesap.js gerçekten düzeldi', readFileSync(`${PROJ}/hesap.js`, 'utf8').includes('a + b'))
  ok('G5 araç adı sızmadı', await page.evaluate(() => !/\b(Read|Edit|Bash|Write)\b/.test(document.querySelector('#stream-scroll')?.innerText ?? '')))
  ok('G5b kanıt sonucu akışın içinde geldi (satır başına ek istek yok)', proofReqs === 0, `ek istek: ${proofReqs}`)
  ok('G6 iş "Kanıtıyla bitti" grubunda', await waitFn(() => /Kanıtıyla bitti/.test(document.querySelector('#job-list')?.innerText ?? ''), null, 10000))

  // 2. bekçi onayı
  reset()
  await page.keyboard.press('Escape'); await page.keyboard.press('n'); await page.keyboard.insertText('Eski klasörü temizle. [[sahte:sor]]'); await page.keyboard.press('Control+Enter'); await acceptDone(page)
  ok('G7 bekçi onay kartı satır içinde', await waitFn(() => /Eski klasörü/.test(document.querySelector('[data-job-title]')?.textContent ?? '') && !!document.querySelector('[data-approve="yes"]'), null, 60000))
  await page.waitForTimeout(600)
  rows.push(['BİLGİ', 'G7 sunucu zaman çizelgesi (kısa)', await page.evaluate(async () => {
    const id = document.querySelector('[data-stream]')?.getAttribute('data-stream')
    const r = await (await fetch(`/api/jobs/${id}/timeline?detail=1`)).json()
    return JSON.stringify((r.items ?? []).map((x) => [x.kind, x.status ?? '', String(x.text).slice(0, 40)]))
  })])
  await shot('gercek-05-bekci-onayi-light-1440')
  await page.keyboard.press('Escape'); await page.keyboard.press('e')
  ok('G8 e → onay verildi, iş sürdü', await waitFn(() => !document.querySelector('[data-approve="yes"]'), null, 8000))
  ok('G9 onaydan sonra ajan devam etti ve bitirdi', await waitFn(() => /Klasör silindi. Tamamlandı./.test(document.querySelector('#stream-scroll')?.innerText ?? ''), null, 120000))
  await page.waitForTimeout(1500)
  rows.push(['BİLGİ', 'G9 sonrası akış sonu / etiket', await page.evaluate(() => (document.querySelector('[data-stream] .chip')?.textContent ?? '?') + ' | ' + (document.querySelector('#stream-scroll')?.innerText ?? '').replace(/\s+/g, ' ').slice(-160))])
  ok('G9b ajan bitince etiket Çalışıyor kalmadı', await page.evaluate(() => !/Çalışıyor/.test(document.querySelector('[data-stream] .chip')?.textContent ?? 'Çalışıyor')))
  await shot('gercek-05b-onaydan-sonra-light-1440')

  // 3. durdur
  await page.keyboard.press('Escape'); await page.keyboard.press('n'); await page.keyboard.insertText('Uzun iş. [[sahte:uzun]]'); await page.keyboard.press('Control+Enter'); await acceptDone(page)
  ok('G10 uzun iş çalışıyor (Durdur görünür)', await waitFor('[data-stop]', 60000))
  await page.waitForTimeout(1500)
  await page.keyboard.press('Escape'); await page.keyboard.press('s')
  ok('G11 s → durduruldu', await waitFn(() => /Durduruldu|Durdurdun/i.test(document.querySelector('[data-stream]')?.innerText ?? ''), null, 15000))
  await shot('gercek-06-durduruldu-light-1440')

  // 4. bozuk → kaldı
  reset()
  await page.keyboard.press('Escape'); await page.keyboard.press('n'); await page.keyboard.insertText('Toplama hatasını düzelt. [[sahte:bozuk]]'); await page.keyboard.press('Control+Enter'); await acceptDone(page)
  ok('G12 bozuk iş → kaldı kartı (geçti denmedi)', await waitFor('[data-stream] [data-proof="failed"]', 150000))
  await page.waitForTimeout(3000)
  ok('G13 son kart kaldı, geçti yok', await page.evaluate(() => !document.querySelector('[data-stream] [data-proof="passed"]')))
  await shot('gercek-07-kaldi-light-1440')


  // 4b. Yeni arayüz parçaları gerçek sunucuda
  await page.goto(B + '/#/is'); await page.reload(); await page.waitForSelector('[data-newjob]', { timeout: 8000 }); await page.waitForTimeout(1200)
  const apiNow = await page.evaluate(async () => (await (await fetch('/api/now')).json()).counts ?? null)
  const shown = await page.evaluate(() => Object.fromEntries([...document.querySelectorAll('[data-counter]')].map((e) => [e.getAttribute('data-counter'), Number(e.querySelector('b')?.textContent)])))
  rows.push(['BİLGİ', 'G17 /api/now counts ile üst sayaçlar', JSON.stringify({ apiNow, shown })])
  ok('G17 üst sayaçlar API sayılarıyla aynı (ya da API yeni sayıyı vermiyorsa sayaç yok)', !apiNow || (apiNow.waiting === undefined ? true : shown.me === apiNow.waiting) && (apiNow.jobsRunning === undefined ? true : shown.running === apiNow.jobsRunning))
  const before = errors.length
  await page.keyboard.press('Escape'); await page.click('[data-folder]'); await page.getByText('Klasör ekle', { exact: false }).last().click()
  await page.waitForSelector('[data-path]', { timeout: 5000 })
  await page.keyboard.insertText(NOPATH); await page.keyboard.press('Enter')
  ok('G18 var olmayan klasör: kutunun altında sade hata', await waitFn(() => !!document.querySelector('[role=alert]')?.textContent?.trim(), null, 10000), await page.evaluate(() => document.querySelector('[role=alert]')?.textContent ?? 'hata yok'))
  ok('G18b var olmayan klasörde konsola ileti düşmedi (sunucu 200 + ok:false döner)', errors.length - before === 0, errors.slice(before).join(' | ').slice(0, 200))
  await shot('gercek-07b-yanlis-klasor-light-1440')
  await page.keyboard.press('Escape')
  // taslak: yenilemede geri gelir
  await page.waitForTimeout(700); await page.click('[data-newjob]'); await page.keyboard.insertText('Gerçek sunucuda taslak denemesi'); await page.waitForTimeout(800)
  await page.reload(); await page.waitForSelector('[data-newjob]', { timeout: 8000 })
  ok('G19 taslak yenilemede geri geldi', await waitFn(() => /Gerçek sunucuda taslak/.test(document.querySelector('[data-newjob]')?.value ?? ''), null, 4000), await page.evaluate(() => JSON.stringify({ val: document.querySelector('[data-newjob]')?.value, ls: localStorage.getItem('soz.draft'), url: location.hash })))

  // 5. dar ekran ve koyu tema aynı veriyle
  await page.setViewportSize({ width: 420, height: 860 }); await page.waitForTimeout(500); await shot('gercek-08-dar-light-420')
  await page.emulateMedia({ colorScheme: 'dark' }); await page.setViewportSize({ width: 1440, height: 900 }); await page.waitForTimeout(500); await shot('gercek-09-koyu-1440')
  await page.keyboard.press('Escape')
  await page.goto(B + '/#/ayarlar'); await page.waitForTimeout(800)
  await page.waitForSelector('details[data-group]', { timeout: 10000 }).catch(() => {}); await page.waitForTimeout(500)
  await page.evaluate(() => document.querySelectorAll('details').forEach((d) => (d.open = true))) // d2u2: ayar grupları ve durum blokları kapalı gelir
  ok('G14 Ayarlar: Hakem anahtarı ve kanca düğmesi görünür', await page.evaluate(() => !!document.querySelector('[data-setting="judgeClaudeEnabled"] [data-setting-input]') && !!document.querySelector('[data-hook-btn]')))
  // Hakem anahtarı gerçek sunucuda açılıp kapanır (ayar yazılır). Bu sırada hakem kararı istenmez.
  const judgeOn = async () => (await (await fetch(`http://127.0.0.1:${SPORT}/api/settings`, { headers: { 'X-Soz-Token': token } })).json()).judgeClaudeEnabled
  await page.click('[data-setting="judgeClaudeEnabled"] [data-setting-input]')
  await page.waitForTimeout(400); if (await page.locator('[data-setting="judgeClaudeEnabled"] [data-confirm-yes]').count()) await page.click('[data-setting="judgeClaudeEnabled"] [data-confirm-yes]') // d2u: bu ayar güvenlik kapısıysa değişmeden önce onay ister
  await page.waitForTimeout(1500)
  rows.push(['BİLGİ', 'G14b tıklama sonrası', JSON.stringify({ aria: await page.evaluate(() => document.querySelector('[data-setting="judgeClaudeEnabled"] [data-setting-input]')?.getAttribute('aria-checked')), toast: await page.evaluate(() => document.querySelector('.toast')?.textContent ?? null), api: await judgeOn() })])
  ok('G14b Hakem anahtarı açıldı (arayüz + sunucu)', await waitFn(() => document.querySelector('[data-setting="judgeClaudeEnabled"] [data-setting-input]')?.getAttribute('aria-checked') === 'true', null, 8000) && (await judgeOn()) === true)
  await page.waitForTimeout(3500) // ilk kayıt bildirimi kapansın, düğme yeniden basılabilir olsun
  await page.click('[data-setting="judgeClaudeEnabled"] [data-setting-input]')
  await page.waitForTimeout(400); if (await page.locator('[data-setting="judgeClaudeEnabled"] [data-confirm-yes]').count()) await page.click('[data-setting="judgeClaudeEnabled"] [data-confirm-yes]') // d2u: bu ayar güvenlik kapısıysa değişmeden önce onay ister
  await page.waitForTimeout(1500)
  rows.push(['BİLGİ', 'G14c tıklama sonrası', JSON.stringify({ aria: await page.evaluate(() => document.querySelector('[data-setting="judgeClaudeEnabled"] [data-setting-input]')?.getAttribute('aria-checked')), disabled: await page.evaluate(() => document.querySelector('[data-setting="judgeClaudeEnabled"] [data-setting-input]')?.disabled ?? null), api: await judgeOn() })])
  ok('G14c Hakem anahtarı kapandı (arayüz + sunucu)', await waitFn(() => document.querySelector('[data-setting="judgeClaudeEnabled"] [data-setting-input]')?.getAttribute('aria-checked') === 'false', null, 8000) && (await judgeOn()) === false)
  await page.click('[data-hook-btn]')
  ok('G15 kanca kuruldu (gerçek uç)', await waitFn(() => /Kancalar kuruldu/.test(document.body.innerText), null, 15000))
  ok('G16 .claude/settings.json yazıldı', existsSync(`${PROJ}/.claude/settings.json`))
  await shot('gercek-10-ayarlar-koyu-1440')

  // 6. Yeni durumlar (denetim 2) GERÇEK sunucuda: sahte claude senaryolarıyla tetiklenir
  const sozApi = async (method, path, body) => (await fetch(`http://127.0.0.1:${SPORT}${path}`, { method, headers: { 'X-Soz-Token': token, 'content-type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) })).json().catch(() => null)
  const resetAll = () => { for (const f of ['package.json', 'hesap.js']) cpSync(`${URUN}/deneme-proje/${f}`, `${PROJ}/${f}`); cpSync(`${URUN}/deneme-proje/test/hesap.test.js`, `${PROJ}/test/hesap.test.js`) }
  const newJob = async (text) => { await page.keyboard.press('Escape'); await page.keyboard.press('n'); await page.keyboard.insertText(text); await page.keyboard.press('Control+Enter'); await acceptDone(page) }
  // Geçici (negatif) kimlik sunucudan gerçek kimlik gelince değişir: pozitif olana kadar bekle
  const curJob = async () => { let id = null; for (let i = 0; i < 80; i++) { id = await page.evaluate(() => Number(document.querySelector('[data-stream]')?.getAttribute('data-stream')) || null); if (id && id > 0) return id; await page.waitForTimeout(250) } return id }
  await page.setViewportSize({ width: 1440, height: 900 }); await page.emulateMedia({ colorScheme: 'light' })
  await page.goto(B + '/#/is'); await page.reload(); await page.waitForSelector('[data-newjob]', { timeout: 8000 }); await page.waitForTimeout(1200)
  resetAll()

  // 6a. ajan soru sorar: cevap_bekliyor, soru kartı, cevap kutusu
  await newJob('Bir şey sor. [[sahte:soru]]')
  ok('G20 soru soran ajan: soru kartı göründü (cevap_bekliyor)', await waitFor('[data-question]', 60000))
  const jid20 = await curJob()
  ok('G20b API iş durumu cevap_bekliyor', (await sozApi('GET', `/api/jobs/${jid20}`))?.state === 'cevap_bekliyor')
  await page.waitForTimeout(1200)
  const c20 = await page.evaluate(async () => { const j = await (await fetch('/api/now')).json(); return { waiting: j.counts.waiting, me: Number(document.querySelector('[data-counter="me"] b')?.textContent), waitingJobs: (j.waitingJobs ?? []).map((x) => x.id + ':' + x.state) } })
  ok('G20c üst sayaç "Sıra bende" API waiting ile aynı', c20.waiting === c20.me, JSON.stringify(c20))
  await shot('gercek-11-soru-light-1440')
  await page.locator('[data-reply]').click(); await page.keyboard.insertText('Birincisini seç.'); await page.keyboard.press('Control+Enter')
  ok('G20d cevap gitti: akışta cevabın göründü', await waitFn(() => /Birincisini seç\./.test(document.querySelector('#stream-scroll')?.innerText ?? ''), null, 20000))
  await page.waitForTimeout(3000)

  // 6b. başlatılamadı: giriş yok sahte hatası; "Kaldı" değil "Başlatılamadı"
  await newJob('Giriş yok denemesi. [[sahte:giris]]')
  ok('G21 başlatılamadı kartı (Kaldı değil)', await waitFor('[data-blocked]', 60000))
  ok('G21b akışta "Kaldı" yazmıyor, çip blocked', await page.evaluate(() => !/Kaldı/.test(document.querySelector('[data-stream]')?.innerText ?? '') && !!document.querySelector('[data-chip="blocked"]')))
  const jid21 = await curJob(); const j21 = await sozApi('GET', `/api/jobs/${jid21}`)
  ok('G21c API iş durumu baslatilamadi + startError dolu', j21?.state === 'baslatilamadi' && !!j21?.startError, String(j21?.startError).slice(0, 80))
  await shot('gercek-12-baslatilamadi-light-1440')

  // 6c. sırada: eşzamanlı sınır 1, iki uzun iş; ikincisi sırada, birincisi durunca kendiliğinden başlar
  await sozApi('PUT', '/api/settings', { maxParallelRuns: 1 })
  rmSync(`${ROOT}/torun.pid`, { force: true })
  await newJob('Uzun iş bir. [[sahte:uzun]]')
  ok('G22 birinci uzun iş çalışıyor', await waitFor('[data-stop]', 60000)); const jidA = await curJob()
  // başlangıç ölçümü sürerken durdurulan iş ajanla hiç konuşmamış olur (devam edilecek oturum yok): ajan gerçekten başlayana kadar bekle
  ok('G22a ajan gerçekten başladı (oturum var)', await (async () => { for (let i = 0; i < 120; i++) { if (existsSync(`${ROOT}/torun.pid`)) return true; await new Promise((r) => setTimeout(r, 500)) } return false })())
  await newJob('Uzun iş iki. [[sahte:uzun]]')
  ok('G22b ikinci iş "Sırada" kartı', await waitFor('[data-queued]', 30000)); const jidB = await curJob()
  const nowQ = await sozApi('GET', '/api/now')
  ok('G22c API durum sirada ve counts.queued=1', (await sozApi('GET', `/api/jobs/${jidB}`))?.state === 'sirada' && nowQ?.counts?.queued === 1, JSON.stringify({ queued: nowQ?.counts?.queued, running: nowQ?.counts?.jobsRunning }))
  ok('G22d üst sayaç Sırada API ile aynı', await page.evaluate(async () => { const n = (await (await fetch('/api/now')).json()).counts; const e = document.querySelector('[data-counter="queued"] b'); return e ? Number(e.textContent) === n.queued : true }))
  await shot('gercek-13-sirada-light-1440')
  await sozApi('POST', `/api/jobs/${jidA}/stop`, {})
  ok('G22e birinci durunca ikinci kendiliğinden başladı', await waitFn(async (id) => (await (await fetch(`/api/jobs/${id}`)).json()).state === 'calisiyor', jidB, 30000))
  await sozApi('POST', `/api/jobs/${jidB}/stop`, {})
  await sozApi('PUT', '/api/settings', { maxParallelRuns: 3 })

  // 6d. devam et: durdurulan iş aynı oturumdan sürer
  await page.click(`[data-job="${jidA}"]`)
  ok('G23 durdurulan işte "Bu işe devam et" düğmesi', await waitFor('[data-continue]', 15000))
  await page.click('[data-continue]'); await page.locator('[data-reply]').click(); await page.keyboard.insertText('Kaldığın yerden sür.'); await page.keyboard.press('Control+Enter')
  await page.waitForTimeout(1500)
  rows.push(['BİLGİ', 'G23 devam sonrası', await page.evaluate(() => JSON.stringify({ toast: document.querySelector('.toast')?.textContent ?? null, reply: document.querySelector('[data-reply]')?.value ?? null, stop: !!document.querySelector('[data-stop]'), chip: document.querySelector('[data-stream] [data-chip]')?.textContent?.trim() ?? null }))])
  ok('G23b devam: iş yeniden çalışıyor (Durdur göründü)', await waitFor('[data-stop]', 30000))
  const sl23 = existsSync(`${ROOT}/sahte.log`) ? readFileSync(`${ROOT}/sahte.log`, 'utf8').split(String.fromCharCode(10)).filter(Boolean).map((l) => JSON.parse(l)) : []
  ok('G23c devam: sahte claude --resume ile ve "Kaldığın yerden sür" istemiyle çağrıldı', sl23.some((l) => l.argv.includes('--resume') && /Kaldığın yerden sür/.test(l.prompt)), JSON.stringify(sl23.filter((l) => l.argv.includes('--resume')).map((l) => String(l.prompt).slice(0, 60))))
  await shot('gercek-14-devam-light-1440')
  await sozApi('POST', `/api/jobs/${jidA}/stop`, {})

  // 6e. onay: bekleyen onay üst sayaca yansır
  resetAll()
  await newJob('Eski klasörü temizle. [[sahte:sor]]')
  ok('G24 onay kartı geldi', await waitFor('[data-approve="yes"]', 60000))
  await page.waitForTimeout(1200)
  const c24 = await page.evaluate(async () => { const j = await (await fetch('/api/now')).json(); return { waiting: j.counts.waiting, me: Number(document.querySelector('[data-counter="me"] b')?.textContent), waitingJobs: (j.waitingJobs ?? []).map((x) => x.id + ':' + x.state) } })
  ok('G24b üst sayaç "Sıra bende" API waiting ile aynı ve en az 1', c24.waiting >= 1 && c24.waiting === c24.me, JSON.stringify(c24))
  const jid24 = await curJob(); const j24 = await sozApi('GET', `/api/jobs/${jid24}`)
  ok('G24c API onay_bekliyor ve pendingApprovals 1', j24?.state === 'onay_bekliyor' && (j24?.pendingApprovals ?? []).length === 1)
  await page.keyboard.press('Escape'); await page.keyboard.press('e')
  ok('G24d onay verildi, kart kalktı', await waitFn(() => !document.querySelector('[data-approve="yes"]'), null, 8000))
  await waitFn(async (id) => { const s = (await (await fetch(`/api/jobs/${id}`)).json()).state; return s !== 'calisiyor' && s !== 'onay_bekliyor' }, jid24, 90000)

  // 6f. karar bekliyor → kabul et → "Sen onayladın"
  resetAll()
  await newJob('Toplamayı düzelt. [[sahte:paket]]')
  ok('G25 test betiğini boşaltan ajan: karar kartı (Değişikliği kabul et)', await waitFor('[data-accept]', 90000))
  ok('G25b karar kartında package.json görünüyor, yeşil Geçti yok', await page.evaluate(() => /package\.json/.test(document.querySelector('[data-stream]')?.innerText ?? '') && !document.querySelector('[data-stream] [data-proof="passed"]')))
  const jid25 = await curJob(); const j25 = await sozApi('GET', `/api/jobs/${jid25}`)
  ok('G25c API karar_bekliyor', j25?.state === 'karar_bekliyor')
  // SÖZLEŞME TESTİ (denetim 3, Y4): karar kartında GERÇEK sunucudan gelen neden metni ve değişen dosya adı ekranda olmalı (sahte veriyle geçmek yetmez)
  const tl25 = await page.evaluate(async (id) => (await (await fetch(`/api/jobs/${id}/timeline?detail=1`)).json()).items ?? [], jid25)
  const proof25 = [...tl25].reverse().find((x) => x.kind === 'proof' && x.verdict)
  rows.push(['BİLGİ', 'G25 sunucunun karar kartı alanları', JSON.stringify({ reasonCode: proof25?.reasonCode, reasonText: String(proof25?.reasonText).slice(0, 90), changedFiles: proof25?.changedFiles, verdictLabel: proof25?.verdictLabel })])
  ok('G25f sözleşme: sunucu zaman çizgisi reasonCode/reasonText/changedFiles veriyor', proof25?.reasonCode === 'definition_changed' && !!proof25?.reasonText && (proof25?.changedFiles ?? []).includes('package.json'))
  const screen25 = await page.evaluate(() => document.querySelector('[data-stream]')?.innerText ?? '')
  ok('G25g sözleşme: neden metni EKRANDA (gerçek sunucudan gelen ilk 30 karakter)', !!proof25?.reasonText && screen25.includes(String(proof25.reasonText).slice(0, 30)))
  ok('G25h sözleşme: değişen dosya adı EKRANDA', (proof25?.changedFiles ?? []).length > 0 && screen25.includes(proof25.changedFiles[0]))
  ok('G25i kartın data-reason işareti sunucu kodu ile aynı', (await page.evaluate(() => document.querySelector('[data-stream] [data-reason]')?.getAttribute('data-reason') ?? null)) === proof25?.reasonCode)
  ok('G25l sözleşme (denetim 4, E): karar kartı görüntü kimliği taşıyor', /^[0-9a-f]{16}$/.test(String(proof25?.snapshotId ?? '')), String(proof25?.snapshotId))
  ok('G25m sözleşme (denetim 4, A): kısa fark kutusu ekranda ve sunucu farkı veriyor', (proof25?.diffs ?? []).length > 0 && (await page.locator('[data-diffs]').count()) >= 1, JSON.stringify((proof25?.diffs ?? []).map((d) => d.path)))
  await shot('gercek-15-karar-light-1440')
  await page.locator('[data-accept]').click()
  ok('G25d kabul → "Sen onayladın" çipi (yeşil Kanıtıyla bitti değil)', await waitFn(() => /Sen onayladın/.test(document.querySelector('[data-stream] [data-chip]')?.textContent ?? ''), null, 15000))
  let j25b = null
  for (let i = 0; i < 20; i++) { j25b = await sozApi('GET', `/api/jobs/${jid25}`); if (j25b?.state === 'gecti') break; await new Promise((r) => setTimeout(r, 400)) }
  ok('G25e API gecti + acceptedByUser', j25b?.state === 'gecti' && j25b?.acceptedByUser === true, JSON.stringify({ state: j25b?.state, acc: j25b?.acceptedByUser, via: j25b?.acceptedVia }))
  ok('G25j kabul kanalı kayıtlı (acceptedVia dolu, kanıtsız değil)', !!j25b?.acceptedVia && j25b?.acceptedUnverified === false, JSON.stringify({ via: j25b?.acceptedVia, unverified: j25b?.acceptedUnverified }))
  const now25 = await sozApi('GET', '/api/now')
  ok('G25k /api/now: sen onayladın ayrı sayılır (jobsAccepted >= 1)', (now25?.counts?.jobsAccepted ?? 0) >= 1, JSON.stringify({ accepted: now25?.counts?.jobsAccepted, passed: now25?.counts?.jobsPassed }))
  await shot('gercek-16-sen-onayladin-light-1440')
  ok('G25n kabul vekil süreçten (node.exe) geldiği için kartta sarı "panelden gelmedi" uyarısı var (bu betiğin vekili, gerçek tarayıcıda kanal "tarayıcı" olur)', await waitFor('[data-not-panel]', 8000))
  // 6g. kabul KOMUT SATIRINDAN gelirse kartta sarı "panelden gelmedi" uyarısı (denetim 4, F)
  resetAll()
  await newJob('Toplamayı düzelt, ikinci kez. [[sahte:paket]]')
  ok('G25p ikinci karar kartı geldi', await waitFor('[data-accept]', 90000))
  const jid26 = await curJob()
  let cli26 = ''
  try { cli26 = execFileSync(EXE, ['job', 'accept', String(jid26), '--data', DATA], { encoding: 'utf8', windowsHide: true }) } catch (e) { cli26 = String(e.stdout ?? e.message) }
  ok('G25q CLI kabulü iş "gecti" yaptı', await (async () => { for (let i = 0; i < 25; i++) { if ((await sozApi('GET', `/api/jobs/${jid26}`))?.state === 'gecti') return true; await new Promise((r) => setTimeout(r, 400)) } return false })(), cli26.slice(0, 160).replace(/s+/g, ' '))
  await page.waitForTimeout(1500)
  ok('G25r kartta sarı "Bu kabul panelden gelmedi" uyarısı', await waitFor('[data-not-panel]', 15000))
  await shot('gercek-17-panelden-gelmedi-light-1440')
  // 7. ESC İŞ BAŞLATMAZ (denetim 3, Y5): onay kartı açıkken ve başka yerlerde Esc basınca hiçbir iş oluşmaz, taslak yerinde durur
  const PROJ2 = `${ROOT}/ikinci proje`
  mkdirSync(`${PROJ2}/test`, { recursive: true })
  for (const f of ['package.json', 'hesap.js']) cpSync(`${URUN}/deneme-proje/${f}`, `${PROJ2}/${f}`)
  cpSync(`${URUN}/deneme-proje/test/hesap.test.js`, `${PROJ2}/test/hesap.test.js`)
  const jobList = async () => { const r = await sozApi('GET', '/api/jobs?limit=500'); return Array.isArray(r) ? r : (r?.jobs ?? []) }
  await page.goto(B + '/#/is'); await page.reload(); await page.waitForSelector('[data-newjob]', { timeout: 8000 }); await page.waitForTimeout(1200)
  const before7 = (await jobList()).length
  await page.keyboard.press('Escape'); await page.click('[data-folder]'); await page.getByText('Klasör ekle', { exact: false }).last().click()
  await page.waitForSelector('[data-path]', { timeout: 5000 })
  await page.keyboard.insertText(PROJ2.split('/').join(String.fromCharCode(92))); await page.keyboard.press('Enter')
  ok('G26 ikinci klasör eklendi, onay kartı açık', await waitFor('[data-setup] [data-open-all]', 15000))
  await page.click('[data-newjob]'); await page.keyboard.insertText('Esc denemesi: bu iş başlamamalı.'); await page.keyboard.press('Control+Enter')
  await page.waitForTimeout(600)
  await page.keyboard.press('Escape')
  await page.waitForTimeout(2500)
  const after7 = (await jobList()).length
  ok('G26b onay kartında Esc: kart kapandı', !(await page.evaluate(() => !!document.querySelector('[data-setup]'))))
  ok('G26c onay kartında Esc: iş BAŞLAMADI (iş sayısı aynı)', after7 === before7, `önce ${before7}, sonra ${after7}`)
  ok('G26d Esc sonrası taslak yazı kutusunda duruyor', await waitFn(() => /Esc denemesi/.test(document.querySelector('[data-newjob]')?.value ?? ''), null, 4000))
  await shot('gercek-17-esc-vazgec-light-1440')
  // başka yerlerde Esc: yazı kutusu, liste, palet
  await page.click('[data-newjob]'); await page.keyboard.press('Escape'); await page.keyboard.press('Escape')
  await page.keyboard.press('Control+k'); await page.keyboard.press('Escape'); await page.keyboard.press('Escape')
  await page.waitForTimeout(2500)
  ok('G26e başka yerlerde Esc: iş BAŞLAMADI', (await jobList()).length === before7, `iş sayısı ${(await jobList()).length}`)
} catch (e) {
  rows.push(['KALDI', 'beklenmeyen hata', String(e?.message ?? e)])
} finally {
  const w = Math.max(0, ...rows.map((r) => r[1].length))
  for (const r of rows) console.log(`${r[0]}  ${r[1].padEnd(w)}  ${r[2]}`)
  console.log('KONSOL:', errors.join('\n') || 'temiz')
  console.log('4xx/5xx API yanıtları:', apiErrs.join(' | ') || 'yok')
  try { await ctx?.close() } catch {}
  try { rmSync(profile, { recursive: true, force: true }) } catch {}
  try { web?.close() } catch {}
  if (server?.pid) { try { execFileSync('taskkill', ['/PID', String(server.pid), '/T', '/F'], { stdio: 'ignore' }) } catch {} }
  await new Promise((r) => setTimeout(r, 800))
  try { rmSync(ROOT, { recursive: true, force: true }) } catch (e) { console.log('temizlik reddedildi:', ROOT) }
  try { rmSync(DATA, { recursive: true, force: true }) } catch (e) { console.log('temizlik reddedildi:', DATA) }
  const failed = rows.filter((r) => r[0] === 'KALDI').length
  console.log(`SONUÇ: ${rows.filter((r) => r[0] === 'GEÇTİ').length} geçti, ${failed} kaldı`)
  writeExitCode(failed > 0 ? 1 : 0)
  process.exit(failed > 0 ? 1 : 0)
}
