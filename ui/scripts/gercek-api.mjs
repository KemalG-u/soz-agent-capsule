// Yayın sunucusuyla GERÇEK API üzerinden tarayıcı gezisi (sentetik veri!).
// Kullanım: node scripts/gercek-api.mjs <soz.exe> <ornek-veri-kökü> <veri-klasörü> <ekran-klasörü>
// Sunucuyu bu betik kendisi açar ve sonunda kendisi kapatır. Gerçek kayıt klasörlerine DOKUNMAZ:
// kaynak yolları Ayarlar sayfasından sentetik klasöre çevrilir, sonra içe alınır.
import { chromium } from 'playwright-core'
import { spawn } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, existsSync } from 'node:fs'
import { dirname, join } from 'node:path'

const [exe, sample, dataDir, out] = process.argv.slice(2)
if (!exe || !sample || !dataDir || !out) throw new Error('kullanım: soz.exe ornek-veri veri-klasörü ekran-klasörü')
mkdirSync(out, { recursive: true })
mkdirSync(dataDir, { recursive: true })

const env = { ...process.env }
delete env.DOTNET_ROOT // yayın kendi çalışma zamanını taşır

// ---- sunucuyu aç ----
const server = spawn(exe, ['serve', '--port', '49300', '--data', dataDir], { env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
let base = ''
await new Promise((resolve, reject) => {
  const t = setTimeout(() => reject(new Error('sunucu 20 sn içinde açılmadı')), 20000)
  server.stdout.setEncoding('utf8')
  server.stdout.on('data', (d) => {
    const m = /(http:\/\/127\.0\.0\.1:\d+)/.exec(d)
    if (m) { base = m[1]; clearTimeout(t); resolve() }
  })
  server.on('exit', (c) => reject(new Error('sunucu çıktı: ' + c)))
})

const log = []
const ok = (name, pass, extra = '') => log.push(`${pass ? 'GEÇTİ ' : 'KALDI '} ${name}${extra ? ' ' + extra : ''}`)
const errors = []
const profile = mkdtempSync(join(dirname(out), 'pw-profil-'))
let browser
try {
  const api = async (method, path, body, token) => {
    const res = await fetch(base + path, { method, headers: { ...(body ? { 'Content-Type': 'application/json' } : {}), ...(token ? { 'X-Soz-Token': token } : {}) }, body: body ? JSON.stringify(body) : undefined })
    const text = await res.text()
    let json = null
    try { json = JSON.parse(text) } catch { /* düz metin */ }
    return { status: res.status, json, text }
  }
  const { json: sess } = await api('GET', '/api/session')
  const token = sess.token

  browser = await chromium.launchPersistentContext(profile, {
    executablePath: process.env.SOZ_EDGE ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true,
    args: ['--no-first-run', '--no-default-browser-check'],
  })
  const page = await browser.newPage()
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()) })
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message))
  page.on('response', (r) => { if (r.url().includes('/api/') && r.status() >= 400) errors.push(`HTTP ${r.status()} ${r.url()}`) })
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' })
  const shot = (name, full = true) => page.screenshot({ path: join(out, name), fullPage: full })
  const noSampleBadge = async (where) => ok(`"örnek veri" rozeti yok (${where})`, (await page.locator('.sample').count()) === 0 && (await page.locator('text=örnek veri').count()) === 0)

  // 1) Açılış: gerçek sunucuya bağlandı mı (sahteye düşmedi mi)
  await page.goto(`${base}/#/toparla`)
  await page.waitForSelector('text=Önce kayıtlarını içe al')
  await noSampleBadge('açılış')
  ok('gerçek sunucuya bağlandı (boş durum, sahte veri yok)', true)
  await shot('01-bos-toparla.png', false)

  // 2) Ayarlar: kaynak yollarını sentetik klasöre çevir (PUT /api/settings gerçek biçim)
  await page.click('nav.tabs >> text=Ayarlar')
  await page.waitForSelector('#croot')
  await page.fill('#croot', join(sample, 'claude', 'projects'))
  await page.press('#croot', 'Tab')
  await page.waitForSelector('text=Kaydedildi')
  await page.fill('#xroot', join(sample, 'codex', 'sessions'))
  await page.press('#xroot', 'Tab')
  await page.waitForTimeout(300)
  const st = (await api('GET', '/api/settings')).json
  ok('ayarlar sunucuda kaydoldu (claudeRoot/codexRoot sentetik)', String(st.claudeRoot).includes('ornek-veri') && String(st.codexRoot).includes('ornek-veri'))
  await noSampleBadge('ayarlar')

  // 3) İçe al
  await page.click('nav.tabs >> text=Toparla')
  await page.click('button:has-text("Kayıtlarını içe al")')
  await page.waitForSelector('text=İçe alma bitti', { timeout: 20000 })
  await shot('02-ice-alma-bitti.png', false)
  ok('içe alma düğmesi gerçek /api/import ile çalıştı', true)
  const sum = (await api('GET', '/api/summary')).json
  ok('özet gerçek sayılarla doldu', sum.utterances === 12 && sum.proposed > 0, `(mesaj ${sum.utterances}, öneri ${sum.proposed})`)
  await page.click('text=Toparla’ya bak')

  // 4) Altı sayfayı gez
  const tour = [['Toparla', '01', 'toparla'], ['Defter', '03', 'defter'], ['Tekrarlar', '04', 'tekrarlar'], ['Ne demiştim', '05', 'ne-demistim'], ['Karne', '06', 'karne'], ['Ayarlar', '07', 'ayarlar']]
  let visited = 0
  for (const [label, n, slug] of tour) {
    await page.click(`nav.tabs >> text=${label}`)
    await page.waitForTimeout(600)
    const h1 = await page.locator('h1').first().innerText()
    ok(`sayfa açıldı: ${label}`, h1.length > 0 && !(await page.locator('text=yüklenemedi').count()), `("${h1.slice(0, 40)}")`)
    await noSampleBadge(label)
    await shot(`${n}-${slug}.png`)
    visited++
  }

  // 5) Öneriyi onayla → Defter'de görünür; paket metni kararı içerir
  await page.click('nav.tabs >> text=Toparla')
  await page.waitForSelector('button:has-text("Onayla")')
  const propText = (await page.locator('ul.list li .text').first().innerText()).trim()
  await page.locator('button:has-text("Onayla")').first().click()
  await page.waitForSelector('text=Deftere eklendi')
  await page.click('nav.tabs >> text=Defter')
  await page.waitForTimeout(700)
  const body = await page.locator('main').innerText()
  ok('onaylanan öneri Defter\'de görünür', body.includes(propText.slice(0, 25)), `("${propText.slice(0, 30)}…")`)
  await page.click('button:has-text("Paketi göster")')
  await page.waitForSelector('pre.ptext')
  const packetText = await page.locator('pre.ptext').innerText()
  ok('paket metni onaylanan kararı içerir', packetText.includes(propText.slice(0, 25)))
  await shot('08-defter-paket.png')

  // 6) Ne demiştim araması
  await page.click('nav.tabs >> text=Ne demiştim')
  await page.fill('#q', 'Türkçe')
  await page.waitForTimeout(800)
  const marks = await page.locator('mark').count()
  ok('Ne demiştim araması sonuç verir (işaretli)', marks > 0, `(${marks} işaret)`)
  await shot('09-ne-demistim-arama.png', false)

  // 7) Karne: varsayılan yalnız sayılar
  await page.click('nav.tabs >> text=Karne')
  await page.waitForSelector('canvas')
  const checked = await page.locator('label.check input[type=checkbox]').isChecked()
  ok('Karne paylaşım kartı varsayılanı: cümle EKLENMEZ (işaret kapalı)', checked === false)
  await page.locator('canvas').screenshot({ path: join(out, '10-karne-kart-sayilar.png') })
  await shot('10-karne.png')

  // 7b) Sahte veri yalnız ?ornek=1 ile açılır ve rozetle belli olur (gerçek adreste asla)
  const demoPage = await browser.newPage()
  await demoPage.goto(`${base}/?ornek=1#/toparla`)
  await demoPage.waitForSelector('.sample', { timeout: 8000 })
  ok('?ornek=1 ile sahte katman açılır ve "örnek veri" rozeti görünür', true)
  await demoPage.close()

  // 8) SSE: karar değişince "decision" olayı akar (arayüz dışı istemciyle)
  const sseRes = await fetch(base + '/api/events/stream')
  const reader = sseRes.body.getReader()
  const dec = new TextDecoder()
  let seen = ''
  const pump = (async () => { try { for (;;) { const r = await reader.read(); if (r.done) break; seen += dec.decode(r.value) } } catch { /* iptal */ } })()
  const readSome = async (ms) => {
    const until = Date.now() + ms
    while (Date.now() < until && !seen.includes('event: decision')) await new Promise((r) => setTimeout(r, 100))
  }
  await readSome(800)
  const projects = (await api('GET', '/api/projects')).json
  const general = projects.find((p) => !p.path)
  const created = await api('POST', '/api/decisions', { projectId: general.id, type: 'rule', text: 'Her zaman sade Türkçe yaz.' }, token)
  await readSome(3000)
  await reader.cancel()
  await pump
  ok('SSE: ": bağlandı" yorumu geldi', seen.includes(': bağlandı'))
  ok('SSE: "event: decision" geldi', seen.includes('event: decision'), `(POST ${created.status})`)

  // 9) API davranışları (sözleşme): token, köken, 409/404
  ok('POST token olmadan 401', (await api('POST', '/api/decisions', { projectId: general.id, type: 'rule', text: 'x' })).status === 401)
  const copied = await api('POST', `/api/packet/copied?projectId=${general.id}`, undefined, token)
  ok('POST /api/packet/copied → {ok:true}', copied.status === 200 && copied.json?.ok === true)
  const ex = await api('POST', '/api/sessions/1/explain', undefined, token)
  ok('explain kapalıyken 409 + açıklama', ex.status === 409 && typeof ex.json?.error === 'string')
  const exp = await api('POST', '/api/export', { projectId: general.id, agents: true }, token)
  ok('export yolsuz Genel projede 409', exp.status === 409, `(${exp.json?.error?.slice(0, 50)})`)
  const bad = await api('PUT', '/api/settings', { extractThreshold: 'x' }, token)
  ok('settings yanlış tür 400', bad.status === 400)
  const sessions = (await api('GET', '/api/sessions?hours=100000&includeStale=true')).json
  const states = [...new Set(sessions.map((s) => s.state))]
  ok('sessions durumları sözleşme içinde', states.every((s) => ['working', 'waiting', 'stalled', 'idle', 'stale'].includes(s)), `(${states.join(',')})`)
  const digest = (await api('GET', '/api/digest?hours=100000')).json
  ok('digest alanları {text,waiting,working,idle,stalled}', ['text', 'waiting', 'working', 'idle', 'stalled'].every((k) => k in digest))

  log.push(`gezilen sayfa: ${visited}`)
} catch (e) {
  log.push('HATA: ' + e.message.split('\n').slice(0, 4).join(' / '))
} finally {
  try { await browser?.close() } catch { /* */ }
  try { rmSync(profile, { recursive: true, force: true }) } catch { /* */ }
  server.kill()
  await new Promise((r) => setTimeout(r, 800))
}
console.log(log.join('\n'))
console.log('Konsol hataları:', errors.length ? errors.join(' | ') : 'yok (0)')
console.log('Sunucu kapandı:', server.exitCode !== null || server.killed)
