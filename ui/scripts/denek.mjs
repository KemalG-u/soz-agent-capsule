// denek: "programı hiç tanımayan biri ilk kez açınca ne yaşar" yürüyüşü (denek-1, 23 Kas 2026). u9 sürümü: gözlem yerine GEÇTİ/KALDI sınaması (K1 tanıtım üstte, K2 selam iş olmaz, K5 ölü tıklama 0, K6 yalan güvence yok).
// Gerçek sunucu (yayin\soz.exe ya da DENEK_EXE), YALNIZ sentetik kayıt kökleri ve sentetik proje klasörleri. Dört araç yolu sahteye çevrilir ve geri okunur.
// Çıktı: _gecici\u9\goruntu\*.png, _gecici\u9\sonuc.json (DENEK_BASE ile değişir)
// Kullanım: node ui\scripts\denek.mjs [--only S1,S2,...]     (DENEK_EXE: sunucu exe, DENEK_WWW: farklı bir wwwroot, DENEK_PORT: 49571)
// Senaryolar: S1 bomboş ilk açılış, S2 "Merhaba ben kimim", S3 gerçek iş, S4 dolu veri ve bekleyen kart, S5 ölü tıklama taraması, S6 boş/hata/yükleniyor, S7 oturum adresi ve satır ayrıntısı, S8 (u10) sırayı sana verdi, denenemeyen iş, tek sayaç, karşılaştırma cümlesi, bağlantı ve hata durumları
// Tema denemesi: DENEK_TEMA=dark ya da light (görüntü adına -koyu / -acik eklenir)
import { spawn } from 'node:child_process'
import { createReadStream, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync, utimesSync } from 'node:fs'
import http from 'node:http'
import net from 'node:net'
import path, { join, normalize, extname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { launch } from './pw.mjs'
import { relaunchAsHuman, writeExitCode } from './insan.mjs'
import { kapatVeDogrula, portKaydet, surecKaydet } from './temizlik.mjs'

if (await relaunchAsHuman(import.meta.url)) process.exit(process.exitCode ?? 1)

// Ürün kökü: bu betik ui/scripts altında durur; SOZ_URUN ile başka bir klasör verilebilir.
const URUN_KOK = process.env.SOZ_URUN ?? fileURLToPath(new URL('../..', import.meta.url)).replaceAll('\\', '/').replace(/\/$/, '')
const U = URUN_KOK
const BASE = process.env.DENEK_BASE ?? `${U}/_gecici/u9`
const ETIKET = process.env.DENEK_ETIKET ?? ''
const TEMA = process.env.DENEK_TEMA ?? ''
const OUT = `${BASE}/goruntu${ETIKET}`
const EXE = process.env.DENEK_EXE ?? `${U}/yayin/soz.exe`
const DLL = process.env.DENEK_DLL ?? null // verilirse sunucu 'dotnet <dll> serve' ile açılır (en yeni derleme)
const DOTNET = process.env.SOZ_DOTNET ?? 'dotnet'
const WWW = process.env.DENEK_WWW ?? null // verilirse arayüz bu klasörden sunulur, api sunucuya aktarılır
const PORT0 = Number(process.env.DENEK_PORT ?? 49571)
const ORNEK = (process.env.DENEK_ORNEK ?? path.resolve(U, '..', 'calisma', 'denek-1', 'ornek').replaceAll('\\', '/')) + ETIKET // sentetik ama gerçekten var olan proje klasörleri ("_gecici" geçmez: sınama klasörü sayılmasın)
const only = (process.argv.includes('--only') ? process.argv[process.argv.indexOf('--only') + 1] : '').split(',').filter(Boolean)
const want = (s) => only.length === 0 || only.includes(s)
const sleep = (n) => new Promise((r) => setTimeout(r, n))
const win = (p) => p.replaceAll('/', '\\')
const iso = (ms) => new Date(ms).toISOString()
const MIN = 60000
const rows = []
const ok = (name, cond, extra = '') => { rows.push({ durum: cond ? 'GEÇTİ' : 'KALDI', ad: name, ek: String(extra).slice(0, 400) }); console.log([cond ? 'GEÇTİ' : 'KALDI', name, String(extra).slice(0, 300)].join(' | ')) }
const bulgu = []
const gozlem = (s, ad, ne) => { bulgu.push({ s, ad, ne }); console.log(`GÖZLEM ${s} | ${ad} | ${String(ne).slice(0, 300)}`) }
mkdirSync(OUT, { recursive: true })

// ---------- sentetik dünya ----------
const enc = (cwd) => cwd.replace(/[:\\/]/g, '-')
function writeFile(f, s) { mkdirSync(path.dirname(f), { recursive: true }); writeFileSync(f, s) }
function projeKur(ad, dosyalar) {
  const d = `${ORNEK}/${ad}`
  for (const [f, s] of Object.entries(dosyalar)) writeFile(`${d}/${f}`, s)
  return win(d)
}
function dunyaKur(SYN, dolu) {
  for (const d of ['ev', 'claude/projects', 'codex/sessions/2026/11', 'hermes', 'opencode']) mkdirSync(`${SYN}/${d}`, { recursive: true })
  const P = {}
  if (!dolu) return P
  const test = JSON.stringify({ version: '1.0.0', scripts: { test: 'node test.js' } }, null, 2)
  P.web = projeKur('Web Sitesi', { 'package.json': test, 'test.js': "console.log('ok'); process.exit(0)\n", 'giris.html': '<html><body><h1>Giriş yap</h1><p>Kullanıcı adını yazın ve giriş yapın</p><p>Parolayı unuttunuz mu?</p><p>Giris butonuna basın</p></body></html>\n', '.git/HEAD': 'ref: refs/heads/main\n' })
  P.muh = projeKur('Muhasebe Aracı', { 'package.json': test, 'test.js': "console.log('ok'); process.exit(0)\n", 'hesap.js': 'module.exports = (a, b) => a + b\n', '.git/HEAD': 'ref: refs/heads/main\n' })
  P.ders = projeKur('Ders Notları', { 'notlar.md': '# Notlar\n\nBugünkü konu: sınav özeti\n', '.git/HEAD': 'ref: refs/heads/main\n' })
  const now = Date.now()
  const claude = (cwd, id, startedAgoMin, lastAgoMin, title) => {
    const f = `${SYN}/claude/projects/${enc(cwd)}/${id}.jsonl`
    const lines = [JSON.stringify({ type: 'user', cwd, sessionId: id, timestamp: iso(now - startedAgoMin * MIN) })]
    if (title) lines.push(JSON.stringify({ type: 'custom-title', customTitle: title, sessionId: id }))
    writeFile(f, lines.join('\n') + '\n')
    const t = new Date(now - lastAgoMin * MIN); utimesSync(f, t, t)
  }
  for (let i = 0; i < 6; i++) claude(P.web, `web-eski-${i}`, 60 * 24 * (6 - i), 60 * 24 * (3 - i / 3) + 30)
  claude(P.web, 'web-ana', 40, 0.2, 'Giriş sayfasını düzelt')
  for (let i = 0; i < 4; i++) claude(P.muh, `muh-${i}`, 60 * 24 * (9 - i), 60 * 20 * (i + 1))
  claude(P.muh, 'muh-bekler', 120, 2, 'Fatura dışa aktarma')
  for (let i = 0; i < 3; i++) claude(P.ders, `ders-${i}`, 60 * 24 * (12 - i), 60 * 24 * (4 + i))
  return P
}

// ---------- sunucu ----------
const synEnv = (SYN) => ({ USERPROFILE: win(`${SYN}/ev`), HOME: win(`${SYN}/ev`), APPDATA: win(`${SYN}/ev/AppData/Roaming`), LOCALAPPDATA: win(`${SYN}/ev/AppData/Local`), CLAUDE_CONFIG_DIR: win(`${SYN}/claude`), CODEX_HOME: win(`${SYN}/codex`), HERMES_HOME: win(`${SYN}/hermes`), OPENCODE_DATA_DIR: win(`${SYN}/opencode`), ANTIGRAVITY_HOME: win(`${SYN}/antigravity`), ANTIGRAVITY_CLI_HOME: win(`${SYN}/antigravity-cli`) })
const fakes = { claudePath: `${U}/test/sahte-claude/sahte-claude.mjs`, codexPath: `${U}/test/sahte-codex/sahte-codex.mjs`, hermesPath: `${U}/test/sahte-hermes/sahte-hermes.mjs`, opencodePath: `${U}/test/sahte-opencode/sahte-opencode.mjs` }
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' }
const hepsi = [] // açılan süreçler: kendi PID'imizle kapatılır
const portBos = (p) => new Promise((r) => { const s = net.createServer(); s.once('error', () => r(false)); s.listen(p, '127.0.0.1', () => s.close(() => r(true))) })

async function dunyaAc(ad, { dolu, port }) {
  const SYN = `${BASE}/sentetik/${ad}${ETIKET}`, DATA = `${BASE}/veri/${ad}${ETIKET}`
  rmSync(SYN, { recursive: true, force: true }); rmSync(DATA, { recursive: true, force: true })
  mkdirSync(DATA, { recursive: true })
  const P = dunyaKur(SYN, dolu)
  if (!(await portBos(port))) throw new Error(`${port} portu dolu`)
  const env = { SystemRoot: process.env.SystemRoot, TEMP: process.env.TEMP, TMP: process.env.TMP, PATH: `${path.dirname(process.execPath)};${process.env.SystemRoot}\\System32`, DOTNET_NOLOGO: '1', ...(DLL ? { DOTNET_ROOT: path.dirname(DOTNET) } : {}), ...synEnv(SYN) }
  const proc = spawn(DLL ? DOTNET : EXE, [...(DLL ? [DLL] : []), 'serve', '--port', String(port), '--data', DATA, '--exit-after', '3400', '--exit-with-parent', String(process.pid)], { env, stdio: 'ignore', windowsHide: true })
  hepsi.push(proc)
  surecKaydet(proc.pid); portKaydet(port) // temizlik.mjs: çıkışta kendi PID'iyle kapatılır ve doğrulanır
  const API = `http://127.0.0.1:${port}`
  let token = ''
  for (let i = 0; i < 80 && !token; i++) { try { token = (await (await fetch(`${API}/api/session`)).json()).token } catch { await sleep(500) } }
  if (!token) throw new Error('Sunucu açılmadı: ' + ad)
  const call = async (method, p, body) => { const r = await fetch(API + p, { method, headers: { 'X-Soz-Token': token, 'content-type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) }); let b; try { b = await r.json() } catch { b = null } return { status: r.status, body: b } }
  const set = await call('PUT', '/api/settings', Object.fromEntries(Object.entries(fakes).map(([k, v]) => [k, win(v)])))
  const back = (await call('GET', '/api/settings')).body ?? {}
  const safe = set.status === 200 && Object.keys(fakes).every((k) => String(back[k] ?? '').toLowerCase().includes('sahte-'))
  ok(`[${ad}] sunucu ${port} portunda açıldı, dört araç yolu sahteye çevrildi ve geri okundu`, safe, `pid ${proc.pid}`)
  if (!safe) throw new Error('Sahte yollar doğrulanamadı')
  let web = null, base = API
  if (WWW) {
    const wp = port + 20
    web = http.createServer((req, res) => {
      if (req.url.startsWith('/api')) { const chunks = []; req.on('data', (c) => chunks.push(c)); req.on('end', () => { const p = http.request({ host: '127.0.0.1', port, path: req.url, method: req.method, headers: { ...req.headers, host: `127.0.0.1:${port}`, origin: API } }, (r) => { res.writeHead(r.statusCode ?? 502, r.headers); r.pipe(res) }); p.on('error', () => { res.statusCode = 502; res.end() }); p.end(Buffer.concat(chunks)) }); return }
      let f = join(WWW, normalize(decodeURIComponent(req.url.split('?')[0]))); if (!existsSync(f) || statSync(f).isDirectory()) f = join(f, 'index.html'); if (!existsSync(f)) { res.statusCode = 404; res.end(); return }
      res.setHeader('content-type', MIME[extname(f)] ?? 'application/octet-stream'); createReadStream(f).pipe(res)
    })
    await new Promise((r) => web.listen(wp, '127.0.0.1', r)); base = `http://127.0.0.1:${wp}`; portKaydet(wp)
  }
  return { ad, API, base, call, P, SYN, DATA, port, proc, web, token, kapat: () => { try { proc.kill() } catch {} ; try { web?.close() } catch {} } }
}

// ---------- tarayıcı ----------
const shotN = []
const goruntu = async (page, ad) => { await page.screenshot({ path: `${OUT}/${ad}${TEMA ? (TEMA === 'dark' ? '-koyu' : '-acik') : ''}.png`, fullPage: false }); shotN.push(ad) }
const govde = (page) => page.evaluate(() => (document.querySelector('main#icerik') ?? document.body).innerText)
const engel = [] // masaüstünde pencere açacak uçlar engellenir ve burada sayılır
// Makine sızıntısı (bulgu): yayinsoz.exe Antigravity ve Hermes kaynağını ortam değişkenine bakmadan gerçek makineden okur. Denek kendi dünyasında kalsın diye
// profil cevabından yalnız bu iki araç ile örnek klasörlerin DIŞINDaki projeler çıkarılır; geri kalan her şey sunucunun gerçek cevabıdır. İstenirse canlı satır eklenir (sunucu süreç göremez).
const ORNEK_W = win(ORNEK).toLowerCase()
const sess = (tool, id, agoMs, level, o = {}) => ({ tool, id, lastActivity: iso(Date.now() - agoMs), level, acikAdayi: level === 'bugun', title: o.title ?? null, pid: o.pid ?? null, canFocus: !!o.pid, cwd: null, projectPath: null, windowTitle: null, evidence: o.ev ?? 'surec', state: o.state ?? 'bosta', ...(o.waiting ? { waitingFor: o.waiting } : {}) })
// ---------- u10: oturum özeti (oz1 sözleşmesi) ----------
// Sentetik oturumun kaydı olmadığı için özet uçlarının cevabı BETİKTE kurulur; cümleler oz1 raporundaki örneklerdir. Sunucunun gerçek cümlesi değişse de sınama
// cümle metnine değil yapıya (üç başlık, uyarı işareti, hazırlanıyor/ulaşamıyorum) bakar; cümle denetimleri esnek (yalnız anahtar sözcük).
const OZ = {
  bittiDenemedi: { oturumId: 3001, arac: 'claude', neYapti: '4 dosyayı değiştirdi (liste.js, odeme.js ve 2 dosya daha), 7 komut çalıştırdı.', denediMi: 'Bitti dedi ama sonrasında hiçbir deneme çalıştırmadı.', sendenBeklenen: 'Bitti, bakmanı bekliyor.', degisenDosyaSayisi: 4, bittiDedi: true, bittiSonrasiDeneme: false, sonDeneme: null, bekleyen: 'inceleme', bekleyenKesin: true, guven: 'yuksek', guvenNedeni: null },
  gecti: { arac: 'claude', neYapti: '1 dosyayı değiştirdi (odeme.js), 2 komut çalıştırdı.', denediMi: 'Bitti dedi. Son denemesi geçti (npm test, 12 dk önce).', sendenBeklenen: 'Bitti, bakmanı bekliyor.', bittiDedi: true, bittiSonrasiDeneme: true, sonDeneme: { komutTuru: 'npm test', sonuc: 'gecti', an: iso(Date.now() - 12 * MIN), cikisKodu: 0 }, bekleyen: 'inceleme', bekleyenKesin: true, guven: 'yuksek', guvenNedeni: null },
  kaldi: { arac: 'claude', neYapti: '1 dosyayı değiştirdi (giris.js), 1 komut çalıştırdı.', denediMi: 'Henüz bitti demedi. Son denemesi hata verdi (npm test, 6 dk önce).', sendenBeklenen: 'Bir şey beklemiyor, çalışıyor.', bittiDedi: false, bittiSonrasiDeneme: false, sonDeneme: { komutTuru: 'npm test', sonuc: 'kaldi', an: iso(Date.now() - 6 * MIN), cikisKodu: 1 }, bekleyen: 'yok', bekleyenKesin: true, guven: 'yuksek', guvenNedeni: null },
  izin: { arac: 'claude', neYapti: 'Henüz dosya değiştirmedi, komut da çalıştırmadı.', denediMi: 'Henüz bitti demedi.', sendenBeklenen: 'İzin bekliyor: bir komut çalıştırmak istiyor.', bittiDedi: false, bittiSonrasiDeneme: false, sonDeneme: null, bekleyen: 'izin', bekleyenKesin: true, guven: 'yuksek', guvenNedeni: null },
  // kanca izin diyor ama kayıt dosyası yok: sunucu "Bir şey beklemiyor" der; ekran kartla çelişen bu cümleyi göstermemeli
  izinKayitsiz: { arac: 'claude', neYapti: 'Henüz dosya değiştirmedi, komut da çalıştırmadı.', denediMi: 'Henüz bitti demedi.', sendenBeklenen: 'Bir şey beklemiyor, çalışıyor.', bittiDedi: false, bittiSonrasiDeneme: false, sonDeneme: null, bekleyen: 'yok', bekleyenKesin: true, guven: 'yuksek', guvenNedeni: null },
  dusuk: { arac: 'hermes', neYapti: 'Bu kaydı okuyamadım.', denediMi: 'Bu kaydı okuyamadım.', sendenBeklenen: 'Bu kaydı okuyamadım.', bittiDedi: false, bittiSonrasiDeneme: false, sonDeneme: null, bekleyen: 'yok', bekleyenKesin: false, guven: 'dusuk', guvenNedeni: 'Bu yardımcının kayıt biçimini okuyamıyorum.' },
}
// Özet uçlarını sahteler. istek: kimlik -> kaç kez soruldu (yeniden sorma sınırı için). Bilinmeyen kimlik "geçti" cevabı alır (gerçek Projelerim oturumları).
function ozetKur(page, istek) {
  const json = (b, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(b) })
  const cevap = (kim) => {
    istek[kim] = (istek[kim] ?? 0) + 1
    if (kim === '3001') return json(OZ.bittiDenemedi)
    if (kim === 'oz-gecti') return json(OZ.gecti)
    if (kim === 'oz-kaldi') return json(OZ.kaldi)
    if (kim === 'oz-izin') return json(OZ.izin)
    if (kim === 'oz-izin-kayitsiz') return json(OZ.izinKayitsiz)
    if (kim === 'oz-dusuk') return json(OZ.dusuk)
    if (kim === 'oz-hazir') return istek[kim] <= 2 ? json(null) : json(OZ.gecti)
    if (kim === 'oz-hep-bos') return json(null)
    if (kim === 'oz-hata') return json({ error: 'İç hata' }, 500)
    return json(OZ.gecti)
  }
  return Promise.all([
    page.route(/\/api\/sessions\/ozet\?/, (r) => r.fulfill(cevap(new URL(r.request().url()).searchParams.get('sessionId') ?? '?'))),
    page.route(/\/api\/sessions\/(\d+)\/ozet/, (r) => r.fulfill(cevap(/sessions\/(\d+)\/ozet/.exec(r.request().url())[1]))),
  ])
}
const profilSuz = (pr, ekle) => {
  const ici = (p) => !!p && p.toLowerCase().startsWith(ORNEK_W)
  pr.projects = (pr.projects ?? []).filter((p) => ici(p.path))
  pr.live = (pr.live ?? []).map((g) => ({ ...g, sessions: (g.sessions ?? []).filter((x) => x.tool !== 'antigravity' && x.tool !== 'hermes') })).filter((g) => g.sessions.length > 0 && ici(g.projectPath))
  for (const [yol, oturumlar] of ekle ?? []) {
    const g = pr.live.find((x) => (x.projectPath ?? '').toLowerCase() === yol.toLowerCase())
    if (g) { g.sessions = [...g.sessions.filter((x) => !oturumlar.some((o) => o.id === x.id)), ...oturumlar] } else pr.live.push({ projectPath: yol, sessions: oturumlar, relations: [] })
  }
  pr.unassigned = { count: 0, reasons: {}, sessions7Days: 0, sessions30Days: 0 }
  pr.counts = { ...(pr.counts ?? {}), projects: pr.projects.length, sessions: pr.projects.reduce((n, p) => n + (p.rootSessions ?? 0), 0) }
  return pr
}
async function sayfaAc(ctx, dunya, w, route = '', { h = 900, kur } = {}) {
  const page = await ctx.newPage()
  await page.setViewportSize({ width: w, height: h })
  if (TEMA) await page.emulateMedia({ colorScheme: TEMA })
  page.on('pageerror', (e) => gozlem('genel', 'sayfa hatası', String(e)))
  const tamam = (b) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(b) })
  await page.route('**/api/live/focus', (r) => { engel.push('live/focus'); return r.fulfill(tamam({ ok: true, message: 'Pencere öne getirildi.', focused: true })) })
  await page.route('**/api/live/open-folder', (r) => { engel.push('live/open-folder'); return r.fulfill(tamam({ ok: true })) })
  await page.route('**/api/sessions/*/focus', (r) => { engel.push('sessions/focus'); return r.fulfill(tamam({ ok: true, message: 'Pencere öne getirildi.', focused: true })) })
  if (dunya.suz !== false) await page.route('**/api/profile', async (r) => { if (r.request().method() !== 'GET') return r.fallback(); const res = await r.fetch(); let j; try { j = await res.json() } catch { return r.fulfill({ response: res }) } return r.fulfill({ response: res, json: profilSuz(j, dunya.ekle?.()) }) })
  if (kur) await kur(page)
  await page.goto(`${dunya.base}/#/${route}`)
  await page.waitForSelector('main#icerik', { timeout: 20000 })
  await page.waitForFunction(() => document.querySelector('.rail') && !document.querySelector('.sk'), null, { timeout: 15000 }).catch(() => {})
  await sleep(1800)
  return page
}

// Ekrandaki görünür düğme ve bağlantı yazıları (denek ne seçebileceğini böyle görür).
const secenekler = (page) => page.evaluate(() => {
  const gor = (e) => { const r = e.getBoundingClientRect(); const st = getComputedStyle(e); return r.width > 0 && r.height > 0 && st.visibility !== 'hidden' && st.display !== 'none' && !e.closest('.sr-only') }
  return [...document.querySelectorAll('button, a[href], [role=button], [role=tab], summary, input[type=checkbox], select')].filter(gor).map((e) => (e.innerText || e.getAttribute('aria-label') || e.title || e.tagName).trim().split(/[\n\t ]+/).join(' ').slice(0, 60))
})
const tikla = async (page, rx, ad) => {
  const loc = page.locator('button, a[href], [role=button], [role=tab], summary, label').filter({ hasText: rx }).first()
  if ((await loc.count()) === 0) { gozlem('genel', 'düğme bulunamadı', String(ad ?? rx)); return false }
  try { await loc.click({ timeout: 4000 }) } catch (e) { gozlem('genel', 'tıklanamadı', String(ad ?? rx) + ' ' + String(e).slice(0, 100)); return false }
  return true
}
const adimlar = []
const adim = async (page, S, w, no, ne, o = {}) => {
  const ad = `${S}-${w}-${String(no).padStart(2, '0')}-${o.slug ?? 'adim'}`
  await goruntu(page, ad)
  const metin = (await govde(page)).replace(/\n{2,}/g, '\n').slice(0, 1800)
  adimlar.push({ S, w, no, ne, goruntu: ad + '.png', metin, hash: await page.evaluate(() => location.hash) })
  console.log(`ADIM ${S} ${w} #${no} ${ne} -> ${metin.replace(/\n/g, ' / ').slice(0, 240)}`)
}
// ---------- ölü tıklama taraması ----------
// Tıklanabilir görünen her öğeyi (düğme, bağlantı, sekme, özet, onay kutusu, seçim, imleci "el" olan her şey, düğme gibi görünen her şey) sırayla tıklar.
// Tepki sayılanlar: sayfa metni/yapısı değişti, adres değişti, pencere açıldı, ağa yazma ya da okuma isteği gitti, odak girdi alanına geçti. Hiçbiri yoksa ÖLÜ.
const aday = (page, kapsam) => page.evaluate((kapsam) => {
  const kok = kapsam ? document.querySelector(kapsam) : document.body
  if (!kok) return []
  for (const x of document.querySelectorAll('[data-satir-aday]')) x.removeAttribute('data-satir-aday')
  const SEM = 'button, a[href], [role=button], [role=tab], [role=option], [role=switch], summary, input, select, textarea, label[for]'
  const gor = (e) => { const r = e.getBoundingClientRect(); const st = getComputedStyle(e); if (r.width <= 1 || r.height <= 1 || st.visibility === 'hidden' || st.display === 'none' || e.closest('.sr-only') || e.closest('[hidden]')) return false; for (let p = e.parentElement; p; p = p.parentElement) if (p.tagName === 'DETAILS' && !p.open && !(e.tagName === 'SUMMARY' && e.parentElement === p)) return false; return true }
  const out = []
  const goster = (e) => (e.innerText || e.getAttribute('aria-label') || e.title || e.value || e.placeholder || '').trim().split(/[\n\t ]+/).join(' ').slice(0, 50)
  const hepsi = [...kok.querySelectorAll('*')]
  for (let ix = 0; ix < hepsi.length; ix++) {
    const e = hepsi[ix]
    if (!gor(e)) continue
    const sem = e.matches(SEM)
    const st = getComputedStyle(e)
    const el = st.cursor === 'pointer'
    const gibi = !sem && st.cursor !== 'default' && parseFloat(st.borderTopWidth) > 0 && /(^|[ _-])(btn|button|chip|pill|tab)([ _-]|$)/i.test(String(e.className?.baseVal ?? e.className ?? '')) && e.children.length < 6
    const cls = String(e.className?.baseVal ?? e.className ?? '')
    const rc = e.getBoundingClientRect()
    const satir = !sem && !el && !gibi && (e.tagName === 'LI' || e.tagName === 'TR' || /(^|[ _-])(row|card|item|entry|wc)([ _-]|$)/i.test(cls)) && rc.width > 200 && rc.height >= 24 && rc.height <= 170 && (e.innerText ?? '').trim().length >= 4 && !e.parentElement?.closest('[data-satir-aday]')
    if (satir) e.setAttribute('data-satir-aday', '1')
    if (!sem && !el && !gibi && !satir) continue
    if (!sem && !satir && e.parentElement?.closest(SEM)) continue
    if (e.tagName === 'INPUT' && e.type === 'hidden') continue
    const r = e.getBoundingClientRect()
    out.push({ ix, tag: e.tagName.toLowerCase(), ad: goster(e), tur: sem ? 'anlamli' : el ? 'imlec-el' : satir ? 'satir' : 'dugme-gibi', cur: e.getAttribute('aria-current') ?? '', devreDisi: !!(e.disabled || e.getAttribute('aria-disabled') === 'true'), href: e.getAttribute('href') ?? '', x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height), ctype: e.type ?? '', secili: ['aria-selected', 'aria-pressed', 'aria-checked'].some((k) => e.getAttribute(k) === 'true') || e.getAttribute('aria-current') === 'page' || e.getAttribute('aria-current') === 'true' || (e.tagName === 'INPUT' && (e.type === 'radio' || e.type === 'checkbox') && e.checked && e.type === 'radio') })
  }
  return out
}, kapsam)
const anlik = (page) => page.evaluate(() => {
  const n = (x) => x.replace(/[0-9]+/g, '#')
  const a = document.activeElement
  return { hash: location.hash, dom: n(document.body.innerHTML), dlg: document.querySelectorAll('dialog[open]').length, odak: (a?.tagName ?? '') + '.' + (a?.id ?? '') }
})
const SAKINCALI = /hooks\/(install|uninstall)|\/shell\/|\/update/ // gerçek makinede kalıcı iz bırakabilecek uçlar: engellenir, "ağ isteği" sayılır
async function tara(ctx, D, route, w, { kapsam = null, sec = '', ac = false, azami = ['ayarlar', 'neoldu', 'is', 'bekci'].includes(route) ? 160 : 80, sure = 500 } = {}) {
  const etiket = `${route || 'suan'}@${w}${sec}`
  const sonucTara = { etiket, adaySayisi: 0, olu: [], bilgi: [], tiklanamadi: [], canli: [], devreDisi: [], supheli: [] }
  const page = await ctx.newPage()
  await page.setViewportSize({ width: w, height: 900 })
  const istek = []
  let acilan = 0 // yeni sekme/pencere açan bağlantılar (target=_blank) tepki sayılır; açılan sekme hemen kapatılır
  page.on('popup', (p) => { acilan++; p.close().catch(() => {}) })
  page.on('pageerror', (e) => gozlem(etiket, 'sayfa hatası', String(e).slice(0, 120)))
  page.on('request', (r) => { if (r.url().includes('/api/')) istek.push(r.method() + ' ' + new URL(r.url()).pathname) })
  const tamam = (b) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(b) })
  await page.route('**/*', (r) => { const u = r.request().url(); if (/^https?:\/\/(127\.0\.0\.1|localhost)/.test(u) || u.startsWith('data:') || u.startsWith('about:')) { if (SAKINCALI.test(u) && r.request().method() !== 'GET') { engel.push('engellendi:' + new URL(u).pathname); return r.fulfill(tamam({ ok: true })) } return r.fallback() } engel.push('dis-ag:' + u.slice(0, 60)); return r.abort() })
  await page.route('**/api/live/focus', (r) => { engel.push('live/focus'); return r.fulfill(tamam({ ok: true, message: 'Pencere öne getirildi.', focused: true })) })
  await page.route('**/api/live/open-folder', (r) => { engel.push('live/open-folder'); return r.fulfill(tamam({ ok: true })) })
  await page.route('**/api/sessions/*/focus', (r) => { engel.push('sessions/focus'); return r.fulfill(tamam({ ok: true, message: 'Pencere öne getirildi.', focused: true })) })
  await page.route('**/api/attention/focus', (r) => { engel.push('attention/focus'); return r.fulfill(tamam({ ok: true, message: 'Pencere öne getirildi.', focused: true })) })
  if (D.suz !== false) await page.route('**/api/profile', async (r) => { if (r.request().method() !== 'GET') return r.fallback(); const res = await r.fetch(); let j; try { j = await res.json() } catch { return r.fulfill({ response: res }) } return r.fulfill({ response: res, json: profilSuz(j, D.ekle?.()) }) })
  let ilkGiris = true
  const git = async () => { if (ilkGiris) { ilkGiris = false; await page.goto(`${D.base}/#/${route}`) } else { await page.evaluate((r) => { try { localStorage.clear(); sessionStorage.clear() } catch {} ; location.hash = '#/' + r; location.reload() }, route) } await page.waitForSelector('main#icerik', { timeout: 20000 }); await page.waitForFunction(() => document.querySelector('.rail') && !document.querySelector('.sk'), null, { timeout: 15000 }).catch(() => {}); await sleep(1000); if (ac) { await page.evaluate(() => document.querySelectorAll('details').forEach((d) => { d.open = true })); await sleep(400) } }
  await git()
  const liste = (await aday(page, kapsam)).slice(0, azami)
  sonucTara.adaySayisi = liste.length
  for (let i = 0; i < liste.length; i++) {
    const a = liste[i]
    if (a.devreDisi) { sonucTara.devreDisi.push(a); continue }
    if (a.y + a.h < 0) { sonucTara.supheli.push({ ...a, not: 'ekran dışı (atlama bağlantısı), tıklanmadı' }); continue }
    const yeniBul = async () => (await aday(page, kapsam)).find((x) => x.tur === a.tur && x.tag === a.tag && x.ad.replace(/[0-9]+/g, "#") === a.ad.replace(/[0-9]+/g, "#") && x.href === a.href && Math.abs(x.y - a.y) < 600) ?? null
    const hedef = await yeniBul()
    if (!hedef) { sonucTara.supheli.push({ ...a, not: 'tıklamadan önce bulunamadı' }); continue }
    const once = await anlik(page)
    const n0 = istek.length, p0 = acilan
    let hata = ''
    try {
      const h = await page.evaluateHandle(([k, ix]) => (k ? document.querySelector(k) : document.body).querySelectorAll('*')[ix], [kapsam, hedef.ix])
      if (a.tur === 'satir') { await h.asElement().scrollIntoViewIfNeeded(); const r = await h.asElement().boundingBox(); await page.mouse.click(r.x + Math.min(24, r.width / 4), r.y + r.height / 2) } else await h.asElement().click({ timeout: 3000 })
    } catch (e) { hata = String(e).slice(0, 90) }
    await sleep(sure)
    const sonra = await anlik(page)
    const yeniIstek = istek.slice(n0).filter((x) => !/^GET \/api\/(profile|attention|session|events|jobs|projects|now|summary)(\/|$)/.test(x))
    const tepki = []
    if (once.hash !== sonra.hash) tepki.push('adres')
    if (once.dom !== sonra.dom) tepki.push('ekran')
    if (once.dlg !== sonra.dlg) tepki.push('pencere')
    if (acilan > p0) tepki.push('yeni sekme')
    if (once.odak !== sonra.odak && /^(INPUT|TEXTAREA|SELECT)/.test(sonra.odak)) tepki.push('odak')
    if (yeniIstek.length) tepki.push('ağ:' + yeniIstek.slice(0, 2).join('+'))
    const kayit = { ...a, tepki: tepki.join(',') }
    if (hata) kayit.hata = hata
    if (hata) sonucTara.tiklanamadi.push(kayit)
    else if (tepki.length === 0 && (a.secili || a.cur === 'page' || (a.tag === 'a' && a.href === `#/${route}`))) sonucTara.devreDisi.push({ ...kayit, not: 'zaten seçili ya da açık' })
    else if (tepki.length === 0 && (a.tag === 'select' || a.tag === 'input' || a.tag === 'textarea')) sonucTara.devreDisi.push({ ...kayit, not: 'girdi alanı: tıklamak değişiklik beklemez' })
    else if (tepki.length === 0 && a.tur === 'satir') sonucTara.bilgi.push(kayit)
    else if (tepki.length === 0) {
      sonucTara.olu.push(kayit)
      if (sonucTara.olu.length <= 4) await goruntu(page, `S5-${etiket.replace(/[^a-z0-9@]/gi, '_')}-olu-${sonucTara.olu.length}`)
    } else sonucTara.canli.push(kayit)
    if (tepki.length) await git()
    else if ((await page.evaluate(() => location.hash)) !== `#/${route}`) await git()
  }
  await page.close()
  console.log(`TARAMA ${etiket}: ${sonucTara.adaySayisi} aday, ${sonucTara.canli.length} tepki verdi, ${sonucTara.olu.length} ÖLÜ, ${sonucTara.bilgi.length} bilgi satırı, ${sonucTara.tiklanamadi.length} tıklanamadı, ${sonucTara.devreDisi.length} devre dışı, ${sonucTara.supheli.length} belirsiz`)
  for (const o of sonucTara.olu) console.log(`  ÖLÜ ${o.tur} <${o.tag}> "${o.ad}" (${o.x},${o.y}) ${o.w}x${o.h}`)
  return sonucTara
}
const taramalar = []
const oluOzet = (liste) => liste.map((t) => `${t.etiket}: ${t.olu.map((o) => `${o.tur} <${o.tag}> "${o.ad}"`).join(' | ')}`)
let DD = null // dolu dünya (isSayisi için)
const isSayisi = async () => { const l = (await DD.call('GET', '/api/jobs')).body; return Array.isArray(l) ? l.length : (l?.items?.length ?? -1) }
const YASAK = /ölçüt|kanıt|\bajan|kanca|bekçi|Söz ölçer/i
const ctxler = []
async function main() {
  const { ctx } = await launch()
  ctxler.push(ctx)
  const sonuc = { zaman: new Date().toISOString(), exe: EXE, www: WWW }

  // ===== S1: bomboş veri, ilk açılış: tanıtım en üstte ve ne olduğunu söylüyor =====
  if (want('S1')) {
    const d = await dunyaAc('s1', { dolu: false, port: PORT0 })
    for (const w of [440, 1280]) {
      const page = await sayfaAc(ctx, d, w)
      await goruntu(page, `S1-${w}-01-ilk-acilis`)
      const metin = await govde(page)
      sonuc[`S1_${w}_metin`] = metin
      const yer = await page.evaluate(() => {
        const top = (q) => document.querySelector(q)?.getBoundingClientRect().top ?? null
        return { intro: top('[data-intro]'), kutu: top('[data-newjob]'), bekleyen: top('[data-suan-expect]'), acik: top('[data-suan-live]'), introMetin: document.querySelector('[data-intro]')?.innerText ?? '' }
      })
      ok(`S1@${w} tanıtım görünüyor ve sayfanın en üstünde (iş kutusundan, bekleyenlerden ve açık işlerden önce)`, yer.intro !== null && yer.kutu !== null && yer.intro < yer.kutu && (yer.bekleyen === null || yer.intro < yer.bekleyen) && (yer.acik === null || yer.intro < yer.acik), JSON.stringify(yer))
      ok(`S1@${w} ürün adı "Söz" ve tek cümlelik tanım ("oldu ya da olmadı") ilk ekranda`, /Söz/.test(yer.introMetin) && /oldu ya da olmadı/.test(yer.introMetin), yer.introMetin.slice(0, 160))
      ok(`S1@${w} üç adım (iş ver, yardımcı yapar, Söz dener) ve bir gerçek örnek var`, (await page.locator('[data-intro] li').count()) === 3 && /giriş sayfasındaki yazım hatasını düzelt/.test(yer.introMetin) && /İş ver/.test(yer.introMetin) && /Yardımcı yapar/.test(yer.introMetin) && /Söz dener/.test(yer.introMetin), '')
      ok(`S1@${w} iş kutusu iş emri olduğunu söylüyor (yer tutucu örnekli, başlık "iş ver")`, /Örnek:/.test((await page.getAttribute('#newjob', 'placeholder')) ?? '') && /iş ver/i.test(await page.locator('[data-bar-head]').innerText()), (await page.getAttribute('#newjob', 'placeholder')) ?? '')
      ok(`S1@${w} sayfada yasak sözcük yok (ölçüt, kanıt, ajan, kanca, bekçi, oturum)`, !/ölçüt|kanıt|\bajan|kanca|bekçi|oturum/i.test(metin), (metin.match(/ölçüt|kanıt|\bajan|kanca|bekçi|oturum/i) ?? [''])[0])
      // örnek düğmesi kutuyu doldurur
      await page.click('[data-intro-try]'); await sleep(300)
      ok(`S1@${w} "Bu örneği dene" kutuyu örnek işle doldurur`, (await page.inputValue('#newjob')) === 'giriş sayfasındaki yazım hatasını düzelt', await page.inputValue('#newjob'))
      await page.fill('#newjob', '')
      // kapat: küçük bağlantı kalır, tıklanınca geri gelir
      await page.click('[data-intro-close]'); await sleep(400)
      await goruntu(page, `S1-${w}-02-tanitim-kapali`)
      ok(`S1@${w} "Anladım" sonrası tanıtım küçülür, "Söz nedir?" bağlantısı kalır`, (await page.locator('[data-intro]').count()) === 0 && (await page.locator('[data-intro-open]').count()) === 1 && /Söz nedir\?/.test(await page.locator('[data-intro-open]').innerText()), '')
      await page.click('[data-intro-open]'); await sleep(400)
      ok(`S1@${w} "Söz nedir?" tıklanınca tanıtım geri gelir`, (await page.locator('[data-intro]').count()) === 1, '')
      await sleep(5000)
      await goruntu(page, `S1-${w}-03-10sn-sonra`)
      await page.close()
    }
    d.kapat()
  }

  // ===== dolu dünya: S2..S5 =====
  let D = null
  if (['S2', 'S3', 'S4', 'S5', 'S7'].some(want)) {
    D = await dunyaAc('dolu', { dolu: true, port: PORT0 + 1 })
    D.ekle = () => [[D.P.web, [sess('claude', 'web-ana', 20000, 'sicak', { title: 'Giriş sayfasını düzelt', pid: 91001, state: 'calisiyor' })]], [D.P.muh, [sess('claude', 'muh-bekler', 2 * MIN, 'sicak', { title: 'Fatura dışa aktarma', pid: 91002, state: 'seni-bekliyor', waiting: 'İzin istiyor: faturalar klasöründeki dosyaları silmek' })]]]
    DD = D
    for (const p of Object.values(D.P)) { const r = await D.call('POST', '/api/projects', { path: p }); console.log('proje ekle', r.status) }
  }

  // ===== S2: "Merhaba ben kimim", sonra "bu ne işe yarar": iş OLUŞMAZ, sade cevap çıkar =====
  if (want('S2')) {
    for (const w of [440, 1280]) {
      const page = await sayfaAc(ctx, D, w)
      let n = 1
      await adim(page, 'S2', w, n++, 'ana ekran, dolu veri', { slug: 'ana' })
      const once = await isSayisi()
      for (const [yazi, neden] of [['Merhaba ben kimim', 'kimlik'], ['bu ne işe yarar', 'kimlik'], ['selam', 'selam'], ['nasılsın?', 'soru']]) {
        await page.fill('#newjob', yazi)
        await page.click('[data-give]')
        await sleep(1800)
        await adim(page, 'S2', w, n++, `${yazi} yazıp Başlat`, { slug: 'cevap-' + neden })
        const panel = await page.locator('[data-done]').count()
        const cevap = await page.locator('[data-not-job]').count()
        const metin = cevap ? await page.locator('[data-not-job]').innerText() : ''
        ok(`S2@${w} "${yazi}": "bitti sayılması" paneli AÇILMIYOR`, panel === 0, 'panel=' + panel)
        ok(`S2@${w} "${yazi}": sade cevap çıkıyor (iş kutusu olduğu, iki örnek, "yine de iş olarak ver")`, cevap === 1 && /iş kutusu|iş verdiğin|iş gibi görünmüyor/.test(metin) && (await page.locator('[data-not-job-example]').count()) === 2 && (await page.locator('[data-not-job-force]').count()) === 1, metin.replace(/\n+/g, ' / ').slice(0, 200))
      }
      ok(`S2@${w} dört selam ve soru sonunda SAHTE İŞ KAYDI oluşmadı`, (await isSayisi()) === once, `önce ${once}, sonra ${await isSayisi()}`)
      ok(`S2@${w} cevapta yasak sözcük yok`, !/ölçüt|kanıt|\bajan|kanca|bekçi/i.test(await page.locator('[data-not-job]').innerText()), '')
      // örnek tıklanır: kutuyu doldurur, cevap kaybolur
      await page.fill('#newjob', 'merhaba'); await page.click('[data-give]'); await sleep(1200)
      await page.locator('[data-not-job-example]').first().click(); await sleep(400)
      ok(`S2@${w} örneğe tıklayınca kutu örnek işle dolar ve cevap kapanır`, (await page.inputValue('#newjob')).length > 10 && (await page.locator('[data-not-job]').count()) === 0, await page.inputValue('#newjob'))
      // yanlış alarm için kaçış: yine de iş olarak ver
      await page.fill('#newjob', 'hava durumu'); await page.click('[data-give]'); await sleep(1200)
      ok(`S2@${w} "hava durumu" iş sayılmadı (cevap çıktı)`, (await page.locator('[data-not-job]').count()) === 1 && (await page.locator('[data-done]').count()) === 0, '')
      await page.locator('[data-not-job-force]').click(); await sleep(2500)
      await adim(page, 'S2', w, n++, 'Yine de iş olarak ver', { slug: 'yine-de' })
      ok(`S2@${w} "Yine de iş olarak ver" işi normal akışa sokuyor (panel açıldı ya da iş başladı)`, (await page.locator('[data-done]').count()) === 1 || (await isSayisi()) > once, '')
      if (await page.locator('[data-done-cancel]').count()) await page.click('[data-done-cancel]')
      await page.close()
    }
  }

  // ===== S3: gerçek iş: "İş bitince neye bakayım?" paneli sade, klasörde deneme yoksa duvar çıkmaz =====
  if (want('S3')) {
    const klasorSec = async (page, ad) => {
      await page.click('[data-folder]'); await sleep(500)
      const opt = page.locator('[role=option]').filter({ hasText: ad }).first()
      if (await opt.count()) await opt.click(); else gozlem('S3', ad + ' klasör listesinde yok', '')
      await sleep(600)
    }
    for (const w of [440, 1280]) {
      const page = await sayfaAc(ctx, D, w)
      let n = 1
      await adim(page, 'S3', w, n++, 'ana ekran', { slug: 'ana' })
      await page.click('[data-folder]'); await sleep(500)
      await adim(page, 'S3', w, n++, 'klasör kutusu açıldı', { slug: 'klasor-menu' })
      await page.keyboard.press('Escape'); await sleep(200)
      await klasorSec(page, 'Web Sitesi')
      await page.fill('#newjob', 'giriş sayfasındaki yazım hatasını düzelt')
      await adim(page, 'S3', w, n++, 'iş yazıldı, klasör Web Sitesi', { slug: 'yazildi' })
      await page.click('[data-give]')
      await page.waitForSelector('[data-done]', { timeout: 15000 }).catch(() => {})
      await sleep(1800)
      await adim(page, 'S3', w, n++, 'Başlat tıklandı: panel', { slug: 'panel' })
      const pm = await page.locator('[data-done]').innerText().catch(() => '')
      sonuc[`S3_${w}_panelMetin`] = pm
      ok(`S3@${w} panelin başlığı tek soru: "İş bitince neye bakayım?"`, /İş bitince neye bakayım\?/.test(pm), pm.split('\n')[0])
      ok(`S3@${w} panelde jargon yok (ölçüt, kanıt, ajan, "Söz ölçer", bekçi)`, !YASAK.test(pm), (pm.match(YASAK) ?? [''])[0])
      ok(`S3@${w} en çok iki seçenek: "Söz kendisi denesin" ve "Ben bakıp karar veririm"`, (await page.locator('[data-done-mode]').count()) === 2 && /Söz kendisi denesin/.test(pm) && /Ben bakıp karar veririm/.test(pm), '')
      ok(`S3@${w} hazır deneme adı seçeneğin altında yazıyor ("Hazır deneme buldum: ... npm test")`, /Hazır deneme buldum: .*npm test/.test(pm), pm.match(/Hazır deneme buldum[^\n]*/)?.[0] ?? '')
      ok(`S3@${w} ayrıntılar tek "Daha fazla" altında kapalı`, (await page.locator('[data-done-more]').count()) === 1 && !(await page.locator('[data-done-more]').evaluate((e) => e.open)) && /Daha fazla/.test(pm), '')
      const ekran = await page.evaluate(() => { const b = document.querySelector('[data-done-accept]')?.getBoundingClientRect(); return b ? { alt: Math.round(b.bottom), ic: innerHeight } : null })
      ok(`S3@${w} ana düğme "İşi başlat" ve kaydırmadan görünüyor (panel kısa)`, !!ekran && ekran.alt <= ekran.ic && /İşi başlat/.test(await page.locator('[data-done-accept]').innerText()), JSON.stringify(ekran))
      await page.locator('[data-done-more] > summary').click(); await sleep(500)
      await adim(page, 'S3', w, n++, 'Daha fazla açıldı', { slug: 'daha-fazla' })
      await page.locator('[data-done-more] > summary').click()
      const once3 = await isSayisi()
      await page.click('[data-done-accept]')
      await sleep(3500)
      await adim(page, 'S3', w, n++, 'İşi başlat', { slug: 'baslat' })
      ok(`S3@${w} "İşi başlat" gerçek iş oluşturdu (sunucuda iş sayısı arttı)`, (await isSayisi()) > once3, `${once3} -> ${await isSayisi()}`)
      await sleep(12000)
      await adim(page, 'S3', w, n++, '12 sn sonra', { slug: 'sonra' })
      const sat = page.locator('li, button, a').filter({ hasText: /Hiçbir dosya değişmedi|iş yapılmamış görünüyor|Denenmedi/i }).first()
      if (await sat.count()) {
        await sat.click({ timeout: 4000 }).catch((e) => gozlem('S3', 'biten iş satırı tıklanamadı', String(e).slice(0, 80)))
        await sleep(2000)
        await adim(page, 'S3', w, n++, 'biten işe tıklandı (iş sayfası)', { slug: 'is-sayfasi' })
        sonuc[`S3_${w}_isSayfasi`] = await govde(page)
      } else gozlem('S3', 'biten iş satırı bulunamadı', 'Hiçbir dosya değişmedi ya da İş yapılmamış görünüyor')
      // klasörde hazır deneme yok (Ders Notları): duvar çıkmaz, iş yine başlar
      await page.goto(`${D.base}/#/suan`); await sleep(1500)
      await klasorSec(page, 'Ders Notları')
      await page.fill('#newjob', 'notlardaki başlığı düzelt')
      await page.click('[data-give]')
      await page.waitForSelector('[data-done]', { timeout: 15000 }).catch(() => {})
      await sleep(1800)
      await adim(page, 'S3', w, n++, 'Ders Notları: klasörde hazır deneme yok', { slug: 'panel-denemesiz' })
      const pd = await page.locator('[data-done]').innerText().catch(() => '')
      ok(`S3@${w} hazır deneme yoksa tek cümle: "Bu klasörde hazır bir deneme bulamadım. İş bitince sen bakarsın..."`, /Bu klasörde hazır bir deneme bulamadım\. İş bitince sen bakarsın, ya da bana neye bakacağını söylersin\./.test(pd), pd.replace(/\n+/g, ' / ').slice(0, 240))
      ok(`S3@${w} denemesiz klasörde duvar yok: jargon yok ve iş başlatılabilir (ana düğme açık)`, !YASAK.test(pd) && !(await page.locator('[data-done-accept]').isDisabled()), (pd.match(YASAK) ?? [''])[0])
      const once4 = await isSayisi()
      await page.click('[data-done-accept]'); await sleep(3500)
      ok(`S3@${w} denemesiz klasörde de iş başladı (sunucuda iş sayısı arttı)`, (await isSayisi()) > once4, `${once4} -> ${await isSayisi()}`)
      // uzun süren iş: "Ben bakıp karar veririm" yoluyla
      await page.goto(`${D.base}/#/suan`); await sleep(1500)
      await klasorSec(page, 'Web Sitesi')
      await page.fill('#newjob', 'bütün sayfaları baştan sona gözden geçir [[sahte:uzun]]')
      await page.click('[data-give]')
      await page.waitForSelector('[data-done]', { timeout: 15000 }).catch(() => {})
      await sleep(1500)
      if (await page.locator('[data-done-mode="ben"]').count()) {
        await page.locator('[data-done-mode="ben"]').check(); await sleep(300)
        await adim(page, 'S3', w, n++, 'Ben bakıp karar veririm seçildi', { slug: 'ben-bakarim' })
      }
      await page.click('[data-done-accept]'); await sleep(4000)
      await adim(page, 'S3', w, n++, 'uzun iş çalışıyor (ana ekran)', { slug: 'uzun-calisiyor' })
      ok(`S3@${w} çalışan iş satırı ana ekranda ("Çalışıyor" ve ne zamandır)`, (await page.locator('[data-running-job]').count()) >= 1 && /Çalışıyor/.test(await page.locator('[data-running-job]').first().innerText()), await page.locator('[data-running-job]').first().innerText().catch(() => ''))
      const uz = page.locator('[data-running-job]').first()
      if (await uz.count()) { await uz.locator('button').first().click().catch(() => {}); await sleep(2000); await adim(page, 'S3', w, n++, 'çalışan işe tıklandı', { slug: 'uzun-detay' }) }
      const liste = (await D.call('GET', '/api/jobs')).body
      for (const j of Array.isArray(liste) ? liste : (liste?.items ?? [])) if (String(j.title ?? j.rawText ?? '').includes('[[sahte:uzun]]')) await D.call('POST', `/api/jobs/${j.id}/stop`, {})
      await page.close()
    }
  }

  // ===== S4: seni bekleyen oturum, dolu veri =====
  let bekleyenHazir = false
  const bekleyenKur = async () => {
    if (bekleyenHazir) return
    bekleyenHazir = true
    const jeton = existsSync(`${D.DATA}/kanca-jeton`) ? readFileSync(`${D.DATA}/kanca-jeton`, 'utf8').trim() : ''
    const kanca = async (olay, govde) => { const r = await fetch(`${D.API}/api/hooks/${olay}`, { method: 'POST', headers: { 'content-type': 'application/json', 'X-Soz-Hook-Token': jeton }, body: JSON.stringify(govde) }); return r.status }
    const k1 = await kanca('session-start', { session_id: 'muh-bekler', cwd: D.P.muh, hook_event_name: 'SessionStart', source: 'startup' })
    const k2 = await kanca('notification', { session_id: 'muh-bekler', cwd: D.P.muh, hook_event_name: 'Notification', notification_type: 'permission_prompt', message: 'Claude needs your permission to use Bash' })
    const k3 = await kanca('session-start', { session_id: 'web-bitti', cwd: D.P.web, hook_event_name: 'SessionStart', source: 'startup' })
    const k4 = await kanca('stop', { session_id: 'web-bitti', cwd: D.P.web, hook_event_name: 'Stop', last_assistant_message: 'Giriş sayfasındaki yazım hatasını düzelttim.' })
    ok('kanca olayları sunucuya iletildi (gerçek yol: izin bekleyen oturum ve biten oturum)', [k1, k2, k3, k4].every((x) => x === 200 || x === 204), [k1, k2, k3, k4].join(','))
    await sleep(1500)
    const att = (await D.call('GET', '/api/attention')).body
    sonuc.S4_attention = (att?.items ?? []).map((i) => ({ kind: i.kind, reason: i.reason, sentence: i.sentence, actions: (i.actions ?? []).map((x) => x.label) }))
    console.log('S4 attention', JSON.stringify(sonuc.S4_attention))
  }
  if (want('S4')) {
    await bekleyenKur()
    for (const w of [440, 1280]) {
      const page = await sayfaAc(ctx, D, w)
      await adim(page, 'S4', w, 1, 'ana ekran: bekleyen kartlar', { slug: 'ana' })
      console.log('SEÇENEKLER S4:', JSON.stringify(await secenekler(page)))
      sonuc[`S4_${w}_metin`] = await govde(page)
      const yer4 = await page.evaluate(() => { const top = (q) => document.querySelector(q)?.getBoundingClientRect().top ?? null; return { intro: top('[data-intro]'), bekleyen: top('[data-attention]'), kutu: top('[data-newjob]') } })
      ok(`S4@${w} dolu veride de tanıtım en üstte (bekleyen karttan ve iş kutusundan önce)`, yer4.intro !== null && yer4.bekleyen !== null && yer4.intro < yer4.bekleyen && yer4.intro < (yer4.kutu ?? 1e9), JSON.stringify(yer4))
      ok(`S4@${w} izin bekleyen oturum kart olarak görünüyor ("izin istiyor" cümlesi, "O pencereye git" düğmesi)`, (await page.locator('[data-attention]').count()) >= 1 && /izin/i.test(sonuc[`S4_${w}_metin`]) && /O pencereye git/.test(sonuc[`S4_${w}_metin`]), '')
      const say = await page.evaluate(() => ({ sayi: Number(document.querySelector('[data-expect-count]')?.textContent ?? -1), kart: document.querySelectorAll('[data-suan-expect] li[data-attention], [data-suan-expect] li[data-wait-session]').length }))
      const say2 = await page.evaluate(() => ({ sayi: Number(document.querySelector('[data-live-count]')?.textContent ?? -1), satir: document.querySelectorAll('[data-suan-live] li.live-row, [data-suan-live] [data-running-job]').length }))
      ok(`S4@${w} "Çalışan yardımcılar" sayısı listedeki satır sayısıyla aynı`, say2.sayi === say2.satir && say2.satir > 0, JSON.stringify(say2))
      ok(`S4@${w} "Seni bekleyenler" sayısı ekrandaki kart sayısıyla aynı`, say.sayi === say.kart && say.kart > 0, JSON.stringify(say))
      await page.close()
      taramalar.push(await tara(ctx, D, 'suan', w, { sec: '-S4-bekleyen' }))
    }
  }

  // ===== S5: her sekme, kart, satır, ayar: ölü tıklama taraması =====
  if (want('S5')) {
    await bekleyenKur()
    // Ayarlar ve Seni tanıyorum en sonda: orada tıklanan anahtarlar (dil, tema) sunucuya yazılır; her taramadan sonra ilk hâle döndürülür.
    const ROTALAR = process.env.DENEK_ROTA ? process.env.DENEK_ROTA.split(',') : ['suan', 'projelerim', 'is', 'sira', 'sabah', 'neoldu', 'bekci', 'harcama', 'defter', 'ara', 'oneriler', 'taniyorum', 'ayarlar'] // DENEK_ROTA=is,bekci yalnız bu rotaları tarar
    const ayarIlk = { ...(await D.call('GET', '/api/settings')).body }; delete ayarIlk.adjusted
    for (const rota of ROTALAR) {
      for (const w of (['suan', 'projelerim', 'is', 'ayarlar'].includes(rota) ? [1280, 440] : [1280])) {
        try { taramalar.push(await tara(ctx, D, rota, w)) } catch (e) { gozlem('S5', 'tarama çöktü ' + rota + '@' + w, String(e).slice(0, 150)) }
        if (rota === 'ayarlar' || rota === 'taniyorum') { const g = await D.call('PUT', '/api/settings', ayarIlk); if (g.status !== 200) gozlem('S5', 'ayarlar ilk hâle döndürülemedi', String(g.status) + ' ' + JSON.stringify(g.body ?? '').slice(0, 160)) }
      }
    }
    if (ROTALAR.includes('ayarlar')) for (const w of [1280, 440]) { try { taramalar.push(await tara(ctx, D, 'ayarlar', w, { sec: '-acik', ac: true, azami: 260 })) } catch (e) { gozlem('S5', 'tarama çöktü ayarlar-acik@' + w, String(e).slice(0, 150)) } const g = await D.call('PUT', '/api/settings', ayarIlk); if (g.status !== 200) gozlem('S5', 'ayarlar ilk hâle döndürülemedi', String(g.status)) }
    sonuc.S5 = taramalar
    const toplamOlu = taramalar.reduce((n, t) => n + t.olu.length, 0)
    ok(`S5 gerçek ölü tıklama sayısı 0 (${taramalar.length} tarama, ${taramalar.reduce((n, t) => n + t.adaySayisi, 0)} aday)`, toplamOlu === 0, toplamOlu ? oluOzet(taramalar.filter((t) => t.olu.length)).join(' ; ').slice(0, 500) : '')
    for (const t of taramalar) if (t.tiklanamadi.length) gozlem('S5', 'tıklanamayan öğe ' + t.etiket, t.tiklanamadi.slice(0, 3).map((o) => o.ad).join(' | '))
  }

  // ===== S7: oturum adresi (#/suan?oturum=<kimlik>) ve açık oturum satırında ayrıntı =====
  if (want('S7')) {
    await bekleyenKur()
    const att7 = (await D.call('GET', '/api/attention')).body?.items ?? []
    const sid = att7.find((x) => x.externalSessionId != null)?.externalSessionId
    ok('S7 sunucuda oturumu olan bir bekleyen kayıt var (adres sınaması için)', sid != null, String(sid))
    for (const w of [440, 1280]) {
      let n = 1
      const page = await sayfaAc(ctx, D, w, 'suan?oturum=' + encodeURIComponent(String(sid)))
      await sleep(1500)
      await adim(page, 'S7', w, n++, 'oturum adresiyle açıldı', { slug: 'adres-var' })
      const vurgu = await page.evaluate(() => { const e = document.querySelector('[data-hit]'); return e ? { var: true, ad: e.className, metin: (e.innerText || '').slice(0, 60) } : { var: false, hash: location.hash, kartlar: [...document.querySelectorAll('[data-attention],[data-wait-card]')].map((x) => x.getAttributeNames().join(',') + '=' + (x.getAttribute('data-attention-session') ?? x.getAttribute('data-wait-session'))), satirlar: [...document.querySelectorAll('[data-attention-session],[data-wait-session],[data-live-session]')].map((x) => x.getAttribute('data-attention-session') ?? x.getAttribute('data-wait-session') ?? x.getAttribute('data-live-session')) } })
      ok(`S7@${w} oturum adresiyle açılınca ilgili satır vurgulanıyor`, vurgu.var === true, JSON.stringify(vurgu))
      await page.close()
      const p2 = await sayfaAc(ctx, D, w, 'suan?oturum=yok-boyle-bir-oturum')
      await sleep(1500)
      await adim(p2, 'S7', w, n++, 'olmayan oturum adresi', { slug: 'adres-yok' })
      const m2 = await govde(p2)
      ok(`S7@${w} olmayan oturum adresinde sade cümle ("Bu çalışma şu an listede yok") ve çökme yok`, /listede yok/.test(m2) || (await p2.locator('[role=status], [data-toast], .toast').count()) >= 1, '')
      await p2.close()
      const p3 = await sayfaAc(ctx, D, w, 'suan')
      const satirlar = await p3.locator('[data-live-session]').evaluateAll((l) => l.map((e) => (e.innerText || '').replace(/\s+/g, ' ').trim()))
      sonuc[`S7_${w}_satirlar`] = satirlar
      await adim(p3, 'S7', w, n++, 'açık oturum satırları', { slug: 'satirlar' })
      ok(`S7@${w} açık oturum satırı var`, satirlar.length >= 1, String(satirlar.length))
      if (satirlar.length) {
        const r0 = satirlar[0]
        ok(`S7@${w} satırda yardımcı adı, proje, durum ve süre görünüyor`, /Claude|Codex|Hermes|OpenCode/i.test(r0) && /(az önce|\d+ ?(sn|dk|dakika|saat))/i.test(r0), r0.slice(0, 160))
      }
      await p3.close()
    }
  }

  // ===== S8 (u10): sırayı sana verdi, denenemeyen iş, tek sayaç, karşılaştırma cümlesi, bağlantı ve hata durumları =====
  // Sunucu gerçek (yayin\soz.exe ya da DLL); /api/attention cevabı e1 sözleşmesine göre BETİKTE kurulur (eski exe yeni nedenleri bilmez). İş sayfası gerçek bir işten, karşılaştırma cümlesi eklenerek.
  if (want('S8')) {
    const D8 = await dunyaAc('s8', { dolu: true, port: PORT0 + 3 })
    for (const p of Object.values(D8.P)) { const r = await D8.call('POST', '/api/projects', { path: p }); console.log('S8 proje ekle', r.status) }
    const json = (b, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(b) })
    const GO = (n) => ({ id: 'go', label: 'O pencereye git', method: 'POST', path: '/api/attention/focus', body: { sessionId: n } })
    const mk = (o) => ({ id: o.id, kind: o.kind, reason: o.reason, priority: o.priority ?? 5, sentence: o.sentence ?? '', detail: o.detail ?? null, title: o.title ?? '', jobUid: null, jobId: null, sessionId: o.sid, projectName: o.proj, at: iso(Date.now() - 3 * MIN), confidence: o.kind === 'uncertain' ? 'unknown' : 'firm', signal: o.kind !== 'uncertain', isNew: false, actions: o.actions, helper: 'Claude Code', externalSessionId: o.ext, source: 'hook', projectPath: o.path, sozNotu: o.sozNotu ?? null, canProve: o.canProve ?? null, canProveReason: o.canProveReason ?? null })
    const items = () => [
      mk({ id: 'a-perm', kind: 'ask', reason: 'session_permission', priority: 1, sentence: 'Claude Code bir işlem için izin istiyor.', sid: 2001, ext: 's8-izin', proj: 'Muhasebe Aracı', path: D8.P.muh, actions: [GO(2001)] }),
      mk({ id: 'a-turn', kind: 'uncertain', reason: 'session_turn_end', priority: 5, sentence: 'Fatura dışa aktarma durdu ve sırayı sana verdi. Bitirdi mi, senden bir şey mi bekliyor, bilmiyorum.', sid: 2002, ext: 's8-tur', proj: 'Web Sitesi', path: D8.P.web, actions: [GO(2002)] }),
      mk({ id: 'a-done-no', kind: 'done', reason: 'session_done', priority: 8, sentence: 'Claude Code işini bitirdi, Söz henüz denemedi.', sid: 2003, ext: 's8-bitti-yok', proj: 'Ders Notları', path: D8.P.ders, sozNotu: 'Bu projede neyle sınayacağımı bilmiyorum.', canProve: false, canProveReason: 'no_checks', actions: [GO(2003), { id: 'prove', label: 'Bu işi sına', method: 'POST', path: '/api/attention/prove', body: {} }] }),
      mk({ id: 'a-done-yes', kind: 'done', reason: 'session_done', priority: 8, sentence: 'Claude Code işini bitirdi, Söz henüz denemedi.', detail: 'Giriş sayfasındaki yazım hatasını düzelttim, testler geçti.', sid: 2004, ext: 's8-bitti-var', proj: 'Muhasebe Aracı', path: D8.P.muh, canProve: true, actions: [{ id: 'prove', label: 'Bu işi sına', method: 'POST', path: '/api/attention/prove', body: {} }, GO(2004)] }),
    ]
    const snapshot = () => { const it = items(); const n = (k) => it.filter((x) => x.kind === k).length; return { rev: 1, now: iso(Date.now()), sentence: 'x', counts: { ask: n('ask'), accept: 0, blocked: 0, done: n('done'), uncertain: n('uncertain'), total: it.length, running: 0 }, budget: { dailyLimit: 5, usedToday: 0, suppressedToday: 0, day: '2026-11-23' }, items: it, running: [] } }
    let focusHits = 0, down = false
    const kur = async (page) => {
      await ozetKur(page, {})
      await page.route(/\/api\/attention\/focus/, (r) => { focusHits++; engel.push('attention/focus'); return r.fulfill(json({ ok: true, message: 'Pencere öne getirildi.', tabSelected: true })) })
      await page.route(/\/api\/attention(\?.*)?$/, (r) => (r.request().method() !== 'GET' ? r.fallback() : down ? r.abort('connectionrefused') : r.fulfill(json(snapshot()))))
    }
    const sfx = TEMA ? (TEMA === 'dark' ? '-koyu' : '-acik') : ''
    const kart = async (loc, ad) => { await loc.scrollIntoViewIfNeeded(); await loc.screenshot({ path: `${OUT}/${ad}${sfx}.png` }); shotN.push(ad) }

    // gerçek bir iş (Web Sitesi): karşılaştırma cümlesi sınaması için
    {
      const page = await sayfaAc(ctx, D8, 1280, 'suan', { kur })
      await page.click('[data-folder]'); await sleep(500)
      const opt = page.locator('[role=option]').filter({ hasText: 'Web Sitesi' }).first()
      if (await opt.count()) await opt.click(); await sleep(500)
      await page.fill('#newjob', 'giriş sayfasındaki yazım hatasını düzelt')
      await page.click('[data-give]')
      await page.waitForSelector('[data-done]', { timeout: 15000 }).catch(() => {})
      await sleep(1200)
      await page.click('[data-done-accept]').catch(() => {})
      await sleep(16000)
      await page.close()
    }
    const jl = (await D8.call('GET', '/api/jobs')).body
    const job = (Array.isArray(jl) ? jl : (jl?.items ?? [])).find((j) => /yazım hatasını/.test(String(j.title ?? j.rawText ?? '')))
    ok('S8 karşılaştırma sınaması için gerçek iş oluştu', !!job, job ? 'iş ' + job.id : '')

    for (const w of [440, 1280]) {
      const hatalar = []
      const page = await sayfaAc(ctx, D8, w, 'suan', { kur })
      page.on('pageerror', (e) => hatalar.push(String(e)))
      await sleep(1200)
      const turn = page.locator('[data-attention="a-turn"]'), perm = page.locator('[data-attention="a-perm"]'), no = page.locator('[data-attention="a-done-no"]'), yes = page.locator('[data-attention="a-done-yes"]')
      const top = (l) => l.evaluate((e) => e.getBoundingClientRect().top + scrollY)
      await adim(page, 'S8', w, 1, 'dört bekleyen: izin, sırayı sana verdi, denenemeyen bitti, denenebilen bitti', { slug: 'liste' })

      // --- sırayı sana verdi ---
      const tm = await turn.innerText()
      ok(`S8@${w} "sırayı sana verdi" kartı var ve etiketi "Sırayı sana verdi"`, (await turn.count()) === 1 && /Sırayı sana verdi/.test(await turn.locator('[data-wc-label]').innerText()), tm.replace(/\n+/g, ' / ').slice(0, 160))
      ok(`S8@${w} kart "bitti" DEMİYOR: yeşil tik ve "Bitti" etiketi yok`, !/Bitti/.test(await turn.locator('[data-wc-label]').innerText()) && (await turn.locator('.chip-ok').count()) === 0 && (await turn.locator('.chip-unproven').count()) === 1, '')
      ok(`S8@${w} tek düğme "O pencereye git" (başka eylem yok)`, (await turn.locator('button').count()) === 1 && /O pencereye git/.test(await turn.locator('button').first().innerText()), String(await turn.locator('button').count()))
      const [tp, pp, np, yp] = [await top(turn), await top(perm), await top(no), await top(yes)]
      ok(`S8@${w} sıra: izin en üstte, "sırayı sana verdi" onun altında, "bitti" kartlarından önce`, pp < tp && tp < np && tp < yp, JSON.stringify({ pp: Math.round(pp), tp: Math.round(tp), np: Math.round(np), yp: Math.round(yp) }))
      ok(`S8@${w} "sırayı sana verdi" grubunun kendi başlığı ve sayısı var; "bitti dediler" ayrı başlıkta`, (await page.locator('[data-turn-head]').count()) === 1 && (await page.locator('[data-turn-count]').innerText()) === '1' && (await page.locator('[data-done-head]').count()) === 1, '')
      await kart(turn, `S8-${w}-turn-sirayi-sana-verdi`)

      // --- izin: ne için olduğu bilinmiyor ---
      ok(`S8@${w} izin kartı neye izin istediğini bilmiyorsa bunu söylüyor ("pencerede yazıyor")`, (await perm.locator('[data-sa-perm-unknown]').count()) === 1 && /pencerede yazıyor/.test(await perm.locator('[data-sa-perm-unknown]').innerText()), '')

      // --- denenemeyen bitti ---
      const nm = await no.innerText()
      ok(`S8@${w} canProve=false kartında "Bu işi dene" düğmesi YOK (sunucu listesinde prove olsa bile)`, (await no.locator('[data-action="prove"]').count()) === 0 && !/Bu işi (dene|sına)/.test(nm), nm.replace(/\n+/g, ' / ').slice(0, 200))
      const sn = no.locator('[data-wc-soznotu]')
      ok(`S8@${w} Söz'ün notu ayrı satırda ve "Söz’ün notu:" etiketiyle; "Yardımcının kendi sözü" DENMİYOR`, (await sn.count()) === 1 && /^Söz’ün notu:/.test((await sn.innerText()).trim()) && /neyle sınayacağımı bilmiyorum/.test(await sn.innerText()) && !/Yardımcının kendi sözü/.test(nm), (await sn.innerText().catch(() => '')).slice(0, 100))
      ok(`S8@${w} yerine "Nasıl denerim?" düğmesi var`, (await no.locator('[data-action="how"]').count()) === 1 && /Nasıl denerim\?/.test(await no.locator('[data-action="how"]').innerText()), '')
      await kart(no, `S8-${w}-bitti-denenemeyen`)
      await no.locator('[data-action="how"]').click(); await sleep(1200)
      ok(`S8@${w} "Nasıl denerim?" tıklanınca kontrol ekleme paneli kartın içinde açılıyor (ölü tıklama değil)`, (await no.locator('[data-sa-how]').count()) === 1 && (await no.locator('[data-sa-how] button, [data-sa-how] select, [data-sa-how] input').count()) > 0, '')
      await adim(page, 'S8', w, 2, 'Nasıl denerim? açıldı', { slug: 'nasil-denerim' })

      // --- denenebilen bitti ---
      const ym = await yes.innerText()
      ok(`S8@${w} canProve=true kartında "Bu işi dene" var, Söz'ün notu yok, yardımcının sözü doğru etiketle`, (await yes.locator('[data-action="prove"]').count()) === 1 && /Bu işi dene/.test(ym) && (await yes.locator('[data-wc-soznotu]').count()) === 0 && /Yardımcının kendi sözü:/.test(ym) && /yazım hatasını düzelttim/.test(ym), ym.replace(/\n+/g, ' / ').slice(0, 220))
      await kart(yes, `S8-${w}-bitti-denenebilen`)

      // --- kartın gövdesine tıklamak ölü değil ---
      const f0 = focusHits
      await perm.locator('[data-wc-sentence]').click(); await sleep(700)
      ok(`S8@${w} kartın gövdesine tıklamak "O pencereye git" ile aynı işi yapıyor`, focusHits === f0 + 1, `istek ${focusHits - f0}`)

      // --- tek sayaç ---
      const bad = await page.evaluate(() => ({ baslik: Number(document.querySelector('[data-expect-count]')?.textContent ?? -1), kart: document.querySelectorAll('[data-suan-expect] li[data-attention], [data-suan-expect] li[data-wait-session]').length, rozet: Number(document.querySelector('[data-nav-badge]')?.textContent ?? -1) }))
      ok(`S8@${w} Şu An başlığı = ekrandaki kart sayısı = alt şerit rozeti`, bad.baslik === bad.kart && bad.kart === bad.rozet && bad.kart === 4, JSON.stringify(bad))
      await page.goto(`${D8.base}/#/sira`); await sleep(1500)
      const sira = await page.locator('[data-attention-sentence]').innerText().catch(() => '')
      ok(`S8@${w} Sıra özeti aynı sayıyı ve türleri söylüyor ("Seni bekleyenler 4: 1 izin, 1 sırayı sana verdi, 2 bitti dedi")`, /Seni bekleyenler 4: 1 izin, 1 sırayı sana verdi, 2 bitti dedi/.test(sira), sira)
      await page.goto(`${D8.base}/#/is`); await sleep(1800)
      const chip = await page.locator('[data-counter="me"] b').innerText().catch(() => '')
      // f1 K-11 kararı: çip altındaki listeyle AYNI kaynaktan sayar (listedeki işler: 0). Terminaldeki 4 bekleyen gizlenmez: "4 tane daha Şu An sayfasında bekliyor" notu ve bağlantı.
      const more = await page.locator('[data-counter-more]').innerText().catch(() => '')
      ok(`S8@${w} İşler sayfasında çip = listedeki satır sayısı (0) ve geri kalan "4 tane daha Şu An sayfasında" diye söyleniyor`, chip.trim() === '0' && /4 tane daha/.test(more), `çip ${chip}, not "${more}"`)
      await page.goto(`${D8.base}/#/suan`); await sleep(1200)
      ok(`S8@${w} bölüm başlığı "Açık yardımcılar" (çalışan sayısı gibi görünmüyor)`, /Açık yardımcılar/.test(await page.locator('[data-suan-live]').innerText()), '')
      ok(`S8@${w} sayfa hatası yok`, hatalar.length === 0, hatalar[0] ?? '')
      await page.close()

      // --- Sade anlat: ayar kapalıyken hiçbir ekranda düğme yok ---
      const pe = await sayfaAc(ctx, D8, w, 'sira', { kur })
      await sleep(1000)
      await pe.locator('[data-uncertain] summary').click().catch(() => {})
      await sleep(400)
      ok(`S8@${w} "Sade anlat" ayar kapalıyken Sıra ekranında GÖRÜNMÜYOR`, (await pe.locator('[data-explain]').count()) === 0 && !/Sade anlat/.test(await govde(pe)), '')
      await pe.close()
    }

    // --- Sade anlat açıkken görünür; sonra eski haline ---
    {
      const ayar0 = (await D8.call('GET', '/api/settings')).body?.explainEnabled
      const put = await D8.call('PUT', '/api/settings', { explainEnabled: true })
      const geri = (await D8.call('GET', '/api/settings')).body?.explainEnabled
      // Sade anlat yalnız "emin olunamayan" (sessiz) satırlarda ve iş akışında var; Sıra'daki oturum kartlarında yerini "Özet" aldı. Sessiz bir satır ekle.
      const kurSade = async (page) => {
        await kur(page)
        await page.route(/\/api\/attention(\?.*)?$/, (r) => {
          if (r.request().method() !== 'GET') return r.fallback()
          const sn = snapshot(); sn.items = [...sn.items, mk({ id: 'a-sessiz', kind: 'uncertain', reason: 'session_idle', priority: 6, sentence: 'Bir yardımcı bir süredir sessiz, ne yaptığını bilmiyorum.', sid: 2005, ext: 's8-sessiz', proj: 'Web Sitesi', path: D8.P.web, actions: [GO(2005)] })]
          return r.fulfill(json(sn))
        })
      }
      const pe = await sayfaAc(ctx, D8, 1280, 'sira', { kur: kurSade })
      await sleep(1000)
      await pe.locator('[data-uncertain] summary').click().catch(() => {})
      await sleep(500)
      ok('S8 "Sade anlat" ayar AÇIKKEN Sıra ekranında görünüyor', (await pe.locator('[data-explain-btn]').count()) >= 1, `ayar önceki: ${ayar0}, PUT ${put.status}, geri okunan ${geri}, kutu ${await pe.locator('[data-explain]').count()}`)
      await pe.close()
      await D8.call('PUT', '/api/settings', { explainEnabled: !!ayar0 })
    }

    // --- iş sayfası: karşılaştırma cümlesi görünür ve vurgulu ---
    if (job) {
      const SAID = 'Yardımcı 2 dosya değiştirdiğini söyledi.', FOUND = 'Ben klasörde değişiklik bulamadım.'
      const yama = (o) => { if (!o || typeof o !== 'object') return; if (Array.isArray(o)) { o.forEach(yama); return } if ('verdict' in o && ('results' in o || 'summary' in o || 'reasonCode' in o)) { o.summary = `Ajan hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu. ${SAID} ${FOUND}`; if ('reasonText' in o) o.reasonText = o.summary } for (const v of Object.values(o)) yama(v) }
      for (const w of [440, 1280]) {
        const page = await sayfaAc(ctx, D8, w, 'is/' + job.id, { kur: async (p) => { await kur(p); await p.route(/\/api\/jobs\/\d+\/timeline/, async (r) => { const res = await r.fetch(); let j; try { j = await res.json() } catch { return r.fulfill({ response: res }) } yama(j); return r.fulfill({ response: res, json: j }) }) } })
        await sleep(2500)
        const kutu = page.locator('[data-claim]').first()
        const n = await kutu.count()
        ok(`S8@${w} iş sayfasında karşılaştırma kutusu var`, n === 1, 'bulunan ' + n)
        if (n) {
          await kutu.scrollIntoViewIfNeeded()
          const g = await kutu.evaluate((e) => { const r = e.getBoundingClientRect(); const said = e.querySelector('[data-claim-said]'), found = e.querySelector('[data-claim-found]'); return { gorunur: r.width > 100 && r.height > 20, tasma: e.scrollWidth > e.clientWidth + 1, said: said?.innerText ?? '', found: found?.innerText ?? '', kalin: found ? Number(getComputedStyle(found).fontWeight) : 0, kesik: [said, found].some((x) => x && x.scrollHeight > x.clientHeight + 1), mismatch: e.getAttribute('data-claim-mismatch') } })
          ok(`S8@${w} iki cümle tam ve görünür, kesilmiyor, taşmıyor; "bulamadım" cümlesi kalın, kutu uyarı renginde`, g.gorunur && !g.tasma && !g.kesik && g.said === SAID && g.found === FOUND && g.kalin >= 600 && g.mismatch === '1', JSON.stringify(g))
          ok(`S8@${w} aynı cümle özet başlığında ikinci kez tekrarlanmıyor`, ((await page.locator('[data-proof]').first().innerText()).match(/değiştirdiğini söyledi/g) ?? []).length === 1, '')
          await page.evaluate(() => scrollBy(0, -80))
          await goruntu(page, `S8-${w}-is-sayfasi-karsilastirma`)
        }
        await page.close()
      }
    }

    // --- C5 bağlantı koptu: şerit ve soluk kartlar, sonra geri gelince kalkar ---
    {
      const page = await sayfaAc(ctx, D8, 1280, 'suan', { kur })
      await sleep(800)
      ok('S8 C5 sunucu varken bağlantı şeridi yok', (await page.locator('[data-link-lost]').count()) === 0, '')
      down = true
      await sleep(26000)
      const lost = await page.locator('[data-link-lost]').count()
      const op = await page.evaluate(() => Number(getComputedStyle(document.querySelector('.suan-body.stale') ?? document.body).opacity))
      await goruntu(page, 'S8-1280-C5-baglanti-koptu')
      ok('S8 C5 bağlantı kopunca 26 sn içinde "Söz’e ulaşamıyorum, gördüklerin eski olabilir (son bilgi: N sn önce)" şeridi çıkıyor ve kartlar soluk', lost === 1 && /Söz’e ulaşamıyorum/.test(await page.locator('[data-link-lost]').innerText()) && /son bilgi: \d+ (sn|dk) önce/.test(await page.locator('[data-link-lost]').innerText()) && op < 0.9, `şerit ${lost}, opaklık ${op}`)
      down = false
      await sleep(11000)
      ok('S8 C5 bağlantı geri gelince şerit kendiliğinden kalkıyor', (await page.locator('[data-link-lost]').count()) === 0, '')
      await page.close()
    }

    // --- C6 iş listesi okunamayınca "Henüz iş yok" DENMEZ; C16 {} cevabında sayfa hatası yok ---
    for (const [ad, yanit] of [['jobs500', (r) => r.fulfill(json({ error: 'İç hata' }, 500))], ['jobsBosNesne', (r) => r.fulfill(json({}))]]) {
      for (const rota of ['is', 'suan']) {
        const hatalar = []
        const kur6 = async (p) => { await kur(p); await p.route(/\/api\/jobs(\?.*)?$/, (r) => (r.request().method() === 'GET' ? yanit(r) : r.fallback())); await p.route(/\/api\/agents(\?.*)?$/, (r) => (r.request().method() === 'GET' && ad === 'jobsBosNesne' ? r.fulfill(json({})) : r.fallback())) }
        const page = await sayfaAc(ctx, D8, 1280, rota, { kur: kur6 })
        page.on('pageerror', (e) => hatalar.push(String(e)))
        await page.reload(); await sleep(3500)
        const m = await govde(page)
        if (rota === 'is') ok(`S8 C6 ${ad} @is: "Henüz iş yok" DENMİYOR, "bilgiye ulaşamıyorum" deniyor`, !/Henüz iş yok/.test(m) && (await page.locator('[data-jobs-error]').count()) === 1 && /bilgiye ulaşamıyorum/.test(m), m.replace(/\n+/g, ' / ').slice(0, 160))
        else ok(`S8 C6 ${ad} @suan: "Son Bitenler" sessizce kaybolmuyor, hata cümlesi var`, (await page.locator('[data-recent-error]').count()) === 1, '')
        ok(`S8 C16 ${ad} @${rota}: sayfa hatası (çökme) yok`, hatalar.length === 0, hatalar[0] ?? '')
        if (rota === 'is' && ad === 'jobs500') await goruntu(page, 'S8-1280-C6-is-listesi-okunamadi')
        await page.close()
      }
    }
    {
      const page = await sayfaAc(ctx, D8, 1280, 'sira', { kur: async (p) => { await p.route(/\/api\/attention(\?.*)?$/, (r) => (r.request().method() === 'GET' ? r.fulfill(json({ error: 'İç hata' }, 500)) : r.fallback())) } })
      await page.reload(); await sleep(2500)
      const m = await govde(page)
      ok('S8 C16 Sıra ekranı hatada ham "İç hata" yazmıyor, sade cümle kullanıyor', !/İç hata/.test(m) && /bilgiye ulaşamıyorum/.test(m), m.replace(/\n+/g, ' / ').slice(0, 160))
      await page.close()
    }

    D8.kapat()
  }

  // ===== S8b (u10): Ayarlar, C13 panel düzeni kaydı, C14 Gelişmiş kapalı, C15 İngilizce yarım notu =====
  if (want('S8b') || want('S8')) {
    const D8 = await dunyaAc('s8b', { dolu: false, port: PORT0 + 2 })
    const json = (b, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(b) })
    // --- C13 panel düzeni 400 dönerse "Kaydedildi" DENMEZ; C14 Gelişmiş kapalı; C15 İngilizce yarım notu ---
    {
      const page = await sayfaAc(ctx, D8, 1280, 'ayarlar', { kur: async (p) => { await p.route(/\/api\/settings(\?.*)?$/, (r) => (r.request().method() === 'PUT' && /navTabs/.test(r.request().postData() ?? '') ? r.fulfill(json({ error: 'navTabs: en çok 200 karakterlik metin olmalı' }, 400)) : r.fallback())) } })
      await sleep(800)
      const adv = await page.locator('[data-advanced-settings]').evaluate((e) => ({ acik: e.open, ad: e.tagName, attr: e.getAttribute('open') }))
      await goruntu(page, 'S8-1280-C14-ayarlar')
      ok('S8 C14 Ayarlar açılınca "Gelişmiş" katı kapalı', adv.acik === false, JSON.stringify(adv))
      ok('S8 C15 İngilizce seçeneği "yarım" diye açıkça işaretli', /yarım/.test(await page.locator('[data-lang-en]').innerText()) && (await page.locator('[data-lang-partial]').count()) === 1, '')
      await page.locator('[data-nav-toggle="ara"]').click(); await sleep(900)
      const toast = (await page.locator('.toast').innerText().catch(() => '')).trim()
      ok('S8 C13 sunucu 400 dönünce toast "Kaydedildi" DEMİYOR, "Kaydedilemedi" diyor', /Kaydedilemedi/.test(toast) && !/^Kaydedildi/.test(toast), toast)
      await page.close()
    }
    D8.kapat()
  }

  // ===== S9 (u10): oturum özeti, üç satır: Ne yaptı / Denedi mi / Senden ne bekleniyor =====
  // Dört durum: bitti dedi denemedi, denemesi geçti, denemesi kaldı, izin bekliyor. Ayrıca hazırlanıyor (sınırlı yeniden sorma), ulaşamıyorum, düşük güven, Projelerim.
  if (want('S9')) {
    const D9 = await dunyaAc('s9', { dolu: true, port: PORT0 + 3 })
    D9.P.rapor = projeKur('Rapor Klasörü', { 'notlar.md': '# Rapor\n' }) // ayrı proje: 4 oturumlu proje satırları katlar (SWARM_MIN)
    for (const p of Object.values(D9.P)) { const r = await D9.call('POST', '/api/projects', { path: p }); console.log('S9 proje ekle', r.status) }
    D9.ekle = () => [
      [D9.P.web, [sess('claude', 'oz-gecti', 30000, 'sicak', { title: 'Ödeme sayfası', pid: 92001, state: 'calisiyor' }), sess('claude', 'oz-kaldi', 40000, 'sicak', { title: 'Giriş denemesi', pid: 92002, state: 'calisiyor' })]],
      [D9.P.muh, [sess('claude', 'oz-izin', 2 * MIN, 'sicak', { title: 'Fatura dışa aktarma', pid: 92010, state: 'seni-bekliyor', waiting: 'İzin istiyor: bir komut çalıştırmak' }), sess('claude', 'oz-dusuk', 50000, 'sicak', { title: 'Eski araçtaki iş', pid: 92003, state: 'calisiyor' })]],
      [D9.P.rapor, [sess('claude', 'oz-izin-kayitsiz', 3 * MIN, 'sicak', { title: 'Rapor çıkarma', pid: 92011, state: 'seni-bekliyor', waiting: 'İzin istiyor: bir dosyayı silmek' })]],
      [D9.P.ders, [sess('claude', 'oz-hazir', 60000, 'sicak', { title: 'Sınav özeti', pid: 92004, state: 'calisiyor' }), sess('claude', 'oz-hata', 70000, 'sicak', { title: 'Not düzeni', pid: 92005, state: 'calisiyor' }), sess('claude', 'oz-hep-bos', 80000, 'sicak', { title: 'Ders planı', pid: 92006, state: 'calisiyor' })]],
    ]
    const json = (b, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(b) })
    const item = { id: 'a-bitti', kind: 'done', reason: 'session_done', priority: 8, sentence: 'Claude Code işini bitirdi, Söz henüz denemedi.', detail: null, title: '', jobUid: null, jobId: null, sessionId: 3001, projectName: 'Web Sitesi', at: iso(Date.now() - 3 * MIN), confidence: 'firm', signal: true, isNew: false, helper: 'Claude Code', externalSessionId: 's9-bitti', source: 'hook', projectPath: D9.P.web, sozNotu: null, canProve: true, canProveReason: null, actions: [{ id: 'go', label: 'O pencereye git', method: 'POST', path: '/api/attention/focus', body: { sessionId: 3001 } }, { id: 'prove', label: 'Bu işi sına', method: 'POST', path: '/api/attention/prove', body: {} }] }
    const snapshot = () => ({ rev: 1, now: iso(Date.now()), sentence: 'x', counts: { ask: 0, accept: 0, blocked: 0, done: 1, uncertain: 0, total: 1, running: 0 }, budget: { dailyLimit: 5, usedToday: 0, suppressedToday: 0, day: '2026-11-23' }, items: [item], running: [] })
    const sfx = TEMA ? (TEMA === 'dark' ? '-koyu' : '-acik') : ''
    const kart = async (loc, ad) => { await loc.scrollIntoViewIfNeeded(); await loc.screenshot({ path: `${OUT}/${ad}${sfx}.png` }); shotN.push(ad) }

    for (const w of [440, 1280]) {
      const hatalar = [], istek = {}
      const page = await sayfaAc(ctx, D9, w, 'suan', { kur: async (pg) => {
        await ozetKur(pg, istek)
        await pg.route(/\/api\/attention(\?.*)?$/, (r) => (r.request().method() !== 'GET' ? r.fallback() : r.fulfill(json(snapshot()))))
      } })
      page.on('pageerror', (e) => hatalar.push(String(e)))
      await sleep(1500)
      const etiketler = async (kok) => (await kok.locator('.oz-k').allInnerTexts()).map((x) => x.replace(/^Dikkat:\s*/, '').trim())
      const tasma = (kok) => kok.evaluate((e) => ({ tasma: e.scrollWidth > e.clientWidth + 1, sayfa: document.documentElement.scrollWidth > innerWidth + 1 }))
      const arka = (loc) => loc.evaluate((e) => getComputedStyle(e).backgroundColor)

      // 1) bekleyen kart: özet açılmadan görünür; "bitti dedi ama denemedi" uyarı görünümünde
      const kartA = page.locator('[data-attention="a-bitti"]')
      const ozA = kartA.locator('[data-oz]')
      ok(`S9@${w} bekleyen kartta özet tıklamadan görünüyor`, (await ozA.count()) === 1 && (await ozA.getAttribute('data-oz-state')) === 'ok' && (await kartA.locator('[data-oz-toggle]').count()) === 0, '')
      ok(`S9@${w} üç sabit başlık: Ne yaptı / Denedi mi / Senden ne bekleniyor`, JSON.stringify(await etiketler(ozA)) === JSON.stringify(['Ne yaptı', 'Denedi mi', 'Senden ne bekleniyor']), JSON.stringify(await etiketler(ozA)))
      const triedA = ozA.locator('[data-oz-row="tried"]')
      ok(`S9@${w} bitti dedi ama denemedi: "Denedi mi" satırı uyarı görünümünde ve cümle açık`, (await triedA.getAttribute('data-oz-warn')) === '1' && /hiçbir deneme/.test(await triedA.innerText()) && (await ozA.locator('[data-oz-row="did"]').getAttribute('data-oz-warn')) === '0' && (await arka(triedA)) !== (await arka(ozA.locator('[data-oz-row="did"]'))), await triedA.innerText())
      const tA = await tasma(ozA)
      ok(`S9@${w} bekleyen kart özeti taşmıyor`, !tA.tasma && !tA.sayfa, JSON.stringify(tA))
      await kart(kartA, `S9-${w}-ozet-bitti-dedi-denemedi`)

      // 2) izin bekliyor (profil bekleyen kartı): özet açılmadan görünür, uyarı yok
      const kartI = page.locator('[data-wait-session="oz-izin"]')
      const ozI = kartI.locator('[data-oz]')
      ok(`S9@${w} izin bekliyor: kartta özet var, "İzin bekliyor" diyor, uyarı görünümü yok`, (await ozI.count()) === 1 && /İzin bekliyor/.test(await ozI.locator('[data-oz-row="expect"]').innerText()) && (await ozI.locator('[data-oz-warn="1"]').count()) === 0, await ozI.innerText().catch(() => '-'))
      await kart(kartI, `S9-${w}-ozet-izin-bekliyor`)
      // 2b) kanca izin diyor, özet kayıtsız "Bir şey beklemiyor" diyor: kartla çelişen cümle yerine kartın kendi sözü
      const kartK = page.locator('[data-wait-session="oz-izin-kayitsiz"]')
      const expK = kartK.locator('[data-oz-row="expect"]')
      ok(`S9@${w} kart "izin bekliyor" derken özetin "Bir şey beklemiyor" cümlesi GÖSTERİLMİYOR, yerine kartın kendi sözü var`, (await expK.count()) === 1 && (await expK.getAttribute('data-oz-own')) === '1' && /İzin bekliyor/.test(await expK.innerText()) && !/Bir şey beklemiyor/.test(await kartK.innerText()), (await expK.innerText().catch(() => '-')).replace(/\n/g, ' / '))

      // 3) çalışan yardımcı satırları: tıklamadan özet yok, "Özet" düğmesiyle açılır
      const satir = (id) => page.locator(`[data-live-session="${id}"]`)
      const gecti = satir('oz-gecti'), kaldi = satir('oz-kaldi')
      ok(`S9@${w} çalışan satırda "Özet" düğmesi var, tıklamadan özet görünmüyor`, (await gecti.locator('[data-oz-toggle]').count()) === 1 && (await gecti.locator('[data-oz]').count()) === 0, '')
      const n0 = istek['oz-gecti'] ?? 0
      ok(`S9@${w} açılmayan satır için sunucuya özet sorulmuyor`, n0 === 0, 'istek ' + n0)
      await gecti.locator('[data-oz-toggle]').click(); await sleep(900)
      const ozG = gecti.locator('[data-oz]')
      ok(`S9@${w} denemesi geçti: üç satır, uyarı görünümü yok, "Son denemesi geçti"`, (await ozG.locator('[data-oz-row]').count()) === 3 && (await ozG.locator('[data-oz-warn="1"]').count()) === 0 && /geçti/.test(await ozG.locator('[data-oz-row="tried"]').innerText()), await ozG.innerText().catch(() => '-'))
      ok(`S9@${w} "Özet" düğmesi aria-expanded=true ve satırın "o pencereye git" alanı yerinde (açma düğmesi ayrı)`, (await gecti.locator('[data-oz-toggle]').getAttribute('aria-expanded')) === 'true' && (await gecti.locator('[data-live-focus]').count()) === 1, '')
      const tG = await tasma(ozG)
      ok(`S9@${w} açık satır özeti taşmıyor`, !tG.tasma && !tG.sayfa, JSON.stringify(tG))
      await kart(gecti, `S9-${w}-ozet-denemesi-gecti`)
      await gecti.locator('[data-oz-toggle]').click(); await sleep(300)
      ok(`S9@${w} "Özeti kapat" özeti gizliyor`, (await gecti.locator('[data-oz]').count()) === 0, '')

      await kaldi.locator('[data-oz-toggle]').click(); await sleep(900)
      const ozK = kaldi.locator('[data-oz]')
      ok(`S9@${w} denemesi kaldı: "Denedi mi" satırı uyarı görünümünde, "hata verdi" yazıyor`, (await ozK.locator('[data-oz-row="tried"]').getAttribute('data-oz-warn')) === '1' && /hata verdi/.test(await ozK.locator('[data-oz-row="tried"]').innerText()), await ozK.innerText().catch(() => '-'))
      await kart(kaldi, `S9-${w}-ozet-denemesi-kaldi`)

      // 4) düşük güven: cümle olduğu gibi, süssüz; uyarı yok
      const dusuk = satir('oz-dusuk')
      await dusuk.locator('[data-oz-toggle]').click(); await sleep(900)
      const ozD = dusuk.locator('[data-oz]')
      // f1 K-01 kararı: hiç okunamadıysa üç boş satır yerine TEK cümle ("Bu oturumun kaydını okuyamadım, ...").
      ok(`S9@${w} düşük güven: hiç okunamadıysa tek cümle, üç boş satır yok`, (await ozD.getAttribute('data-oz-low')) === '1' && /kaydını okuyamadım/.test(await ozD.innerText().catch(() => '')) && (await ozD.locator('.oz-v').count()) <= 1 && (await ozD.locator('[data-oz-warn="1"]').count()) === 0, await ozD.innerText().catch(() => '-'))

      // 5) hazırlanıyor: sunucu iki kez "henüz hazır değil (null)" der, sonra özet gelir
      const hazir = satir('oz-hazir')
      await hazir.locator('[data-oz-toggle]').click(); await sleep(700)
      ok(`S9@${w} özet null ise "Hazırlanıyor" yazıyor`, (await hazir.locator('[data-oz-prep]').count()) === 1 && /Hazırlanıyor/.test(await hazir.locator('[data-oz]').innerText()), await hazir.locator('[data-oz]').innerText().catch(() => '-'))
      await page.waitForFunction(() => document.querySelector('[data-live-session="oz-hazir"] [data-oz-row]'), null, { timeout: 15000 }).catch(() => {})
      ok(`S9@${w} birkaç saniye sonra yeniden sorup özeti getiriyor (istek ${istek['oz-hazir']})`, (await hazir.locator('[data-oz-row]').count()) === 3 && (istek['oz-hazir'] ?? 0) === 3, 'istek ' + istek['oz-hazir'])

      // 6) uç hata verirse: "Şu an bilgiye ulaşamıyorum" ve elle "Tekrar dene"
      const hata = satir('oz-hata')
      await hata.locator('[data-oz-toggle]').click(); await sleep(900)
      ok(`S9@${w} uç hata verince "Şu an bilgiye ulaşamıyorum" deniyor, ham hata yazılmıyor`, (await hata.locator('[data-oz-fail]').count()) === 1 && /Şu an bilgiye ulaşamıyorum/.test(await hata.locator('[data-oz]').innerText()) && !/İç hata/.test(await hata.locator('[data-oz]').innerText()), await hata.locator('[data-oz]').innerText().catch(() => '-'))
      const h0 = istek['oz-hata'] ?? 0
      await hata.locator('[data-oz-retry]').click(); await sleep(900)
      ok(`S9@${w} "Tekrar dene" yeniden soruyor (ölü tıklama değil)`, (istek['oz-hata'] ?? 0) === h0 + 1, `önce ${h0}, sonra ${istek['oz-hata']}`)

      // 7) sonsuz döngü yok (yalnız geniş ekranda: 24 sn sürer)
      if (w === 1280) {
        const bos = satir('oz-hep-bos')
        if ((await bos.count()) === 0) gozlem('S9', 'oz-hep-bos satırı bulunamadı', 'katlı blokta olabilir')
        else {
          await bos.locator('[data-oz-toggle]').click()
          await sleep(17000)
          const a = istek['oz-hep-bos'] ?? 0
          await sleep(7000)
          const b = istek['oz-hep-bos'] ?? 0
          ok('S9 hep "hazırlanıyor" diyen uçta yeniden sorma sınırlı (en çok 5 istek) ve sonunda "ulaşamıyorum" deniyor', a >= 2 && a <= 5 && b === a && (await bos.locator('[data-oz-fail]').count()) === 1, `17 sn: ${a}, 24 sn: ${b}`)
        }
      }
      ok(`S9@${w} sayfa hatası yok`, hatalar.length === 0, hatalar[0] ?? '')
      await page.close()

      // 8) Projelerim: canlı satır "Özet" ile açılır
      const hp = []
      const pp = await sayfaAc(ctx, D9, w, 'projelerim', { kur: async (pg) => { await ozetKur(pg, istek); pg.on('request', (rq) => { if (/\/api\/sessions\/ozet\?/.test(rq.url())) hp.push(rq.url()) }) } })
      const pl = pp.locator('[data-pj-live-session="oz-gecti"]')
      ok(`S9@${w} Projelerim canlı satırında "Özet" düğmesi var`, (await pl.count()) === 1 && (await pl.locator('[data-oz-toggle]').count()) === 1, '')
      await pl.locator('[data-oz-toggle]').click(); await sleep(900)
      ok(`S9@${w} Projelerim: tıklayınca aynı üç satır geliyor ve yardımcının kendi kimliğiyle soruluyor`, (await pl.locator('[data-oz-row]').count()) === 3 && hp.some((u) => /tool=claude/.test(u) && /sessionId=oz-gecti/.test(u)), hp.slice(-1)[0] ?? 'istek yok')
      const tp = await tasma(pl.locator('[data-oz]'))
      ok(`S9@${w} Projelerim özeti taşmıyor`, !tp.tasma && !tp.sayfa, JSON.stringify(tp))
      await goruntu(pp, `S9-${w}-projelerim-ozet`)
      await pp.close()
    }
    D9.kapat()
  }

  // ===== S10 (u10): "iş yapılmamış görünüyor" (no_work, uyarı rengi), başlatılamayan iş (Söz'ün notu, Devam et / Geri al yok), çift çeviri yok =====
  // Sunucu cevapları (iş listesi, zaman çizelgesi, bekleyenler) e2 sözleşmesine göre BETİKTE kurulur; sunucu gerçek, iş gerçek (Web Sitesi), yalnız hüküm alanları yamalanır.
  if (want('S10')) {
    const D10 = await dunyaAc('s10', { dolu: true, port: PORT0 + 3 })
    for (const p of Object.values(D10.P)) { const r = await D10.call('POST', '/api/projects', { path: p }); console.log('S10 proje ekle', r.status) }
    const json = (b, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(b) })
    {
      const page = await sayfaAc(ctx, D10, 1280, 'suan')
      await page.click('[data-folder]'); await sleep(500)
      const opt = page.locator('[role=option]').filter({ hasText: 'Web Sitesi' }).first()
      if (await opt.count()) await opt.click(); await sleep(500)
      await page.fill('#newjob', 'giriş sayfasındaki yazım hatasını düzelt')
      await page.click('[data-give]')
      await page.waitForSelector('[data-done]', { timeout: 15000 }).catch(() => {})
      await sleep(1200)
      await page.click('[data-done-accept]').catch(() => {})
      await sleep(16000)
      await page.close()
    }
    const jl = (await D10.call('GET', '/api/jobs')).body
    const job = (Array.isArray(jl) ? jl : (jl?.items ?? [])).find((j) => /yazım hatasını/.test(String(j.title ?? j.rawText ?? '')))
    ok('S10 sınama için gerçek iş oluştu', !!job, job ? 'iş ' + job.id : '')
    const SUM = 'Yardımcı iş yaptığını söyledi ama klasörde hiçbir değişiklik yok. Bu iş yapılmamış görünüyor. Kontroller geçiyor ama bunlar işten önce de geçiyordu. Bu bir "geçti" değildir.'
    const NOTE = "Claude bu bilgisayarda bulunamadı. Ayarlar'dan yolunu kontrol et."
    const RAW = 'System.ComponentModel.Win32Exception (2): The system cannot find the file specified at Soz.Core.Run.RunManager.Start'
    const gez = (o, f, d = 0) => { if (!o || typeof o !== 'object' || d > 8) return; if (Array.isArray(o)) { for (const x of o) gez(x, f, d + 1); return } f(o); for (const k of Object.keys(o)) if (o[k] && typeof o[k] === 'object') gez(o[k], f, d + 1) }
    const hukum = (kod) => (o) => {
      if ('verdict' in o && ('results' in o || 'summary' in o || 'reasonCode' in o)) {
        if (kod === 'no_work') { o.verdict = 'unchecked'; o.reasonCode = 'no_work'; o.summary = SUM; o.reasonText = SUM; o.verdictLabel = 'iş yapılmamış görünüyor' }
        else { o.verdict = 'passed'; o.reasonCode = 'no_change'; o.summary = 'Yardımcı hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu. Bu iş değişiklik istemiyordu.'; o.reasonText = o.summary; o.verdictLabel = 'değişiklik yok' }
      }
    }
    const isYama = (kod) => (o) => { if (o && typeof o === 'object' && o.id === job?.id && ('state' in o)) { if (kod === 'no_work') { o.state = 'verildi'; o.lastVerdict = 'unchecked'; o.lastReason = 'no_work'; o.acceptedByUser = false; o.run = null } else if (kod === 'baslatilamadi') { o.state = 'baslatilamadi'; o.startError = NOTE; o.run = null; o.lastReason = null; o.lastVerdict = null } } }
    const patchRoute = (page, re, f) => page.route(re, async (r) => { if (r.request().method() !== 'GET') return r.fallback(); const res = await r.fetch(); let j; try { j = await res.json() } catch { return r.fulfill({ response: res }) } gez(j, f); return r.fulfill({ response: res, json: j }) })
    const hex = (c) => c
    const renkler = (page, sel) => page.evaluate((sel) => {
      const e = document.querySelector(sel); if (!e) return null
      const p = document.createElement('i'); p.style.color = 'var(--waiting)'; document.body.appendChild(p); const waiting = getComputedStyle(p).color
      p.style.color = 'var(--idle)'; const idle = getComputedStyle(p).color; p.remove()
      const h = e.querySelector('.head'); return { head: h ? getComputedStyle(h).color : null, seal: getComputedStyle(e.querySelector('.seal')).color, waiting, idle }
    }, sel)

    for (const w of [440, 1280]) {
      const hatalar = []
      // --- A) iş yapılmamış görünüyor: iş sayfası ---
      {
        const page = await sayfaAc(ctx, D10, w, 'is/' + job.id, { kur: async (p) => { await patchRoute(p, /\/api\/jobs\/\d+\/timeline/, hukum('no_work')); await patchRoute(p, /\/api\/jobs(\/\d+)?(\?.*)?$/, isYama('no_work')) } })
        page.on('pageerror', (e) => hatalar.push(String(e)))
        await sleep(2500)
        const kart = page.locator('[data-proof="unchecked"][data-reason="no_work"]').first()
        const n = await kart.count()
        ok(`S10@${w} iş sayfasında "yapılmamış görünüyor" kartı var (hüküm unchecked, neden no_work)`, n === 1, 'bulunan ' + n)
        if (n) {
          const m = await kart.innerText()
          ok(`S10@${w} kart "İş yapılmamış görünüyor" diyor, sunucu cümlesi görünür ve kesilmiyor`, /İş yapılmamış görünüyor/.test(m) && /Yardımcı iş yaptığını söyledi ama klasörde hiçbir değişiklik yok/.test(m) && /Bu bir "geçti" değildir/.test(m), m.replace(/\n/g, ' / ').slice(0, 220))
          ok(`S10@${w} kart gri "Hiçbir dosya değişmedi" ya da yeşil "Denendi, oldu" DEMİYOR`, !/Hiçbir dosya değişmedi/.test(m) && !/Denendi, oldu/.test(m), '')
          const c = await renkler(page, '[data-proof="unchecked"][data-reason="no_work"]')
          ok(`S10@${w} kart UYARI renginde (başlık ve mühür bekleyen/uyarı rengi, gri değil)`, !!c && c.head === c.waiting && c.seal === c.waiting && c.head !== c.idle, JSON.stringify(c))
          const tk = await kart.evaluate((e) => ({ tasma: e.scrollWidth > e.clientWidth + 1, sayfa: document.documentElement.scrollWidth > innerWidth + 1 }))
          ok(`S10@${w} kart taşmıyor`, !tk.tasma && !tk.sayfa, JSON.stringify(tk))
          await kart.scrollIntoViewIfNeeded(); await kart.screenshot({ path: `${OUT}/S10-${w}-is-yapilmamis-gorunuyor${TEMA ? (TEMA === 'dark' ? '-koyu' : '-acik') : ''}.png` }); shotN.push(`S10-${w}-is-yapilmamis-gorunuyor`)
        }
        await page.close()
      }
      // --- A2) salt okuma işi (no_change) hâlâ gri "Hiçbir dosya değişmedi" ---
      {
        const page = await sayfaAc(ctx, D10, w, 'is/' + job.id, { kur: async (p) => { await patchRoute(p, /\/api\/jobs\/\d+\/timeline/, hukum('no_change')) } })
        await sleep(2500)
        const kart = page.locator('[data-proof="passed"][data-reason="no_change"]').first()
        const m = (await kart.count()) ? await kart.innerText() : ''
        ok(`S10@${w} no_change (salt okuma işi) gri "Hiçbir dosya değişmedi" olarak kalıyor, uyarı rengi yok`, /Hiçbir dosya değişmedi/.test(m) && !/İş yapılmamış görünüyor/.test(m) && (await kart.evaluate((e) => e.classList.contains('nochange') && !e.classList.contains('nowork')).catch(() => false)), m.replace(/\n/g, ' / ').slice(0, 160))
        await page.close()
      }
      // --- B) iş listesi: grup başlığı ve satır ---
      {
        const page = await sayfaAc(ctx, D10, w, 'is', { kur: async (p) => { await patchRoute(p, /\/api\/jobs(\?.*)?$/, isYama('no_work')); await patchRoute(p, /\/api\/jobs\/\d+\/timeline/, hukum('no_work')) } })
        await sleep(2500)
        const g = page.locator('#g-nowork')
        const satir = page.locator(`[data-job="${job.id}"], [data-job-id="${job.id}"], #job-${job.id}`).first()
        const govde = await page.locator('main#icerik').innerText()
        ok(`S10@${w} iş listesinde "İş yapılmamış görünüyor" grubu var ve iş "Denendi, oldu" grubunda DEĞİL`, (await g.count()) === 1 && /İş yapılmamış görünüyor/.test(await g.innerText()), (await g.count()) ? await g.innerText() : govde.slice(0, 160))
        const rowTxt = govde.match(/Klasörde hiçbir değişiklik yok: iş yapılmamış görünüyor/)
        ok(`S10@${w} liste satırı "klasörde hiçbir değişiklik yok: iş yapılmamış görünüyor" diyor`, !!rowTxt, govde.slice(0, 160).replace(/\n/g, ' / '))
        await goruntu(page, `S10-${w}-is-listesi-yapilmamis`)
        await page.close()
      }
      // --- C) başlatılamayan iş: Söz'ün notu, Devam et / Geri al YOK ---
      {
        const item = { id: 'a-sf', kind: 'blocked', reason: 'start_failed', priority: 3, sentence: '«giriş sayfasındaki yazım hatasını düzelt» başlatılamadı.', detail: null, title: 'giriş sayfasındaki yazım hatasını düzelt', jobUid: null, jobId: job.id, sessionId: null, projectName: 'Web Sitesi', at: iso(Date.now() - 2 * MIN), confidence: 'firm', signal: true, isNew: false, helper: null, externalSessionId: null, source: 'job', projectPath: D10.P.web, sozNotu: NOTE, canProve: null, canProveReason: null, actions: [{ id: 'continue', label: 'Devam et', method: 'POST', path: `/api/jobs/${job.id}/continue` }, { id: 'seen', label: 'Gördüm', method: 'POST', path: '/api/attention/ack' }] }
        const snap = () => ({ rev: 1, now: iso(Date.now()), sentence: 'x', counts: { ask: 0, accept: 0, blocked: 1, done: 0, uncertain: 0, total: 1, running: 0 }, budget: { dailyLimit: 5, usedToday: 0, suppressedToday: 0, day: '2026-11-23' }, items: [item], running: [] })
        const kurC = async (p) => {
          await p.route(/\/api\/attention(\?.*)?$/, (r) => (r.request().method() !== 'GET' ? r.fallback() : r.fulfill(json(snap()))))
          // sunucu "geri alınacak bir şey var" dese bile başlamamış işte Geri al çıkmamalı
          await p.route(/\/api\/jobs\/\d+\/protection/, (r) => r.fulfill(json({ jobId: job.id, level: 'tam', note: null, text: 'Geri alınabilir.', limits: '', canUndo: true, cannotUndoWhy: null, runs: [] })))
          await patchRoute(p, /\/api\/jobs(\/\d+)?(\?.*)?$/, isYama('baslatilamadi'))
          await p.route(/\/api\/jobs\/\d+\/timeline/, async (r) => { const res = await r.fetch(); let j; try { j = await res.json() } catch { return r.fulfill({ response: res }) } const x = { id: 'l9999', at: iso(Date.now() - MIN), kind: 'progress', text: 'Yardımcı başlatılamadı.', detail: RAW }; const ayikla = (a) => a.filter((i) => i.kind !== 'proof'); if (Array.isArray(j)) j = [...ayikla(j), x]; else if (j && Array.isArray(j.items)) { j.items = [...ayikla(j.items), x]; j.state = 'baslatilamadi'; j.startError = NOTE; j.run = null; j.agentState = null } return r.fulfill({ response: res, json: j }) })
        }
        for (const [rota, ad] of [['sira', 'Sıra'], ['suan', 'Şu An']]) {
          const page = await sayfaAc(ctx, D10, w, rota, { kur: kurC })
          page.on('pageerror', (e) => hatalar.push(String(e)))
          await sleep(2000)
          const kart = rota === 'sira' ? page.locator('[data-attention="a-sf"]').first() : page.locator('[data-hero-card="a-sf"], [data-attention="a-sf"]').first()
          const n = await kart.count()
          ok(`S10@${w} ${ad}: başlatılamayan iş kartı var`, n === 1, 'bulunan ' + n)
          if (!n) { await page.close(); continue }
          const m = await kart.innerText()
          ok(`S10@${w} ${ad}: "Söz’ün notu:" etiketiyle sade cümle görünüyor, "Yardımcının kendi sözü" DENMİYOR`, /Söz’ün notu:/.test(m) && m.includes(NOTE) && !/Yardımcının kendi sözü/.test(m), m.replace(/\n/g, ' / ').slice(0, 200))
          ok(`S10@${w} ${ad}: "Devam et" ve "Geri al" düğmeleri YOK (başlamamış iş)`, (await kart.locator('button').filter({ hasText: /^\s*Devam et\s*$/ }).count()) === 0 && (await kart.locator('[data-action="continue"], [data-hero-action="continue"]').count()) === 0 && (await kart.locator('[data-undo-from-attention], [data-hero-undo]').count()) === 0 && !/Geri al/.test(m), m.replace(/\n/g, ' / ').slice(0, 200))
          ok(`S10@${w} ${ad}: işi açma ya da "Gördüm" gibi eylem düğmesi var (ölü kart değil)`, (await kart.locator('button').count()) >= 1, String(await kart.locator('button').count()))
          ok(`S10@${w} ${ad}: ham teknik hata kartta görünmüyor`, !/Win32Exception|RunManager/.test(m), '')
          await kart.scrollIntoViewIfNeeded(); await kart.screenshot({ path: `${OUT}/S10-${w}-baslatilamadi-${rota}${TEMA ? (TEMA === 'dark' ? '-koyu' : '-acik') : ''}.png` }); shotN.push(`S10-${w}-baslatilamadi-${rota}`)
          await page.close()
        }
        // iş sayfası: Geri al çubuğu yok; teknik ayrıntı yalnız "Teknik ayrıntı" altında
        const page = await sayfaAc(ctx, D10, w, 'is/' + job.id, { kur: kurC })
        await sleep(2500)
        const govde = await page.locator('main#icerik').innerText()
        ok(`S10@${w} iş sayfası (başlamamış iş): "Geri al" çubuğu yok`, (await page.locator('[data-protection]').count()) === 0 && !/Geri al\b/.test(govde), (govde.match(/.{0,50}Geri al.{0,50}/) ?? [''])[0].replace(/\n/g, ' / ') + ' | protection=' + (await page.locator('[data-protection]').count()))
        const tek = page.locator('button.more').filter({ hasText: /Teknik ayrıntı/ }).first()
        const tn = await tek.count()
        ok(`S10@${w} ham teknik hata kapalı ve düğmenin adı "Teknik ayrıntı"`, tn >= 1 && !(await page.locator('main#icerik').innerText()).includes('Win32Exception'), 'düğme ' + tn)
        if (tn) { for (let deneme = 0; deneme < 4 && !(await page.locator('pre.det').allInnerTexts()).some((x) => x.includes('Win32Exception')); deneme++) { const satirB = page.locator('.prog').filter({ hasText: 'Yardımcı başlatılamadı.' }).first(); const dg = satirB.locator('button.more').filter({ hasText: /Teknik ayrıntı/ }).first(); if (await dg.count()) await dg.click().catch(() => {}); await sleep(600) } await sleep(300); ok(`S10@${w} "Teknik ayrıntı" açılınca ham hata orada görünüyor`, (await page.locator('pre.det').allInnerTexts()).some((x) => x.includes('Win32Exception')), String(await page.locator('pre.det').count()) + ' ayrıntı') }
        await goruntu(page, `S10-${w}-is-sayfasi-baslatilamadi`)
        await page.close()
      }
      ok(`S10@${w} sayfa hatası yok`, hatalar.length === 0, hatalar[0] ?? '')
    }
    D10.kapat()
  }

  // ===== S6: boş, hata ve yükleniyor durumları =====
  if (want('S6')) {
    const E = await dunyaAc('s6bos', { dolu: false, port: PORT0 + 2 })
    const ROTA = ['suan', 'projelerim', 'is', 'ayarlar', 'taniyorum', 'sira', 'sabah', 'neoldu', 'bekci', 'harcama', 'defter', 'ara', 'oneriler']
    // boş: her ekran, bomboş veriyle
    for (const rota of ROTA) {
      for (const w of (['suan', 'projelerim', 'is', 'ayarlar'].includes(rota) ? [440, 1280] : [1280])) {
        const page = await sayfaAc(ctx, E, w, rota)
        await adim(page, 'S6', w, 0, 'boş veri: ' + rota, { slug: 'bos-' + rota })
        const mBos = (sonuc[`S6_bos_${rota}_${w}`] = await govde(page))
        if (rota === 'projelerim') ok(`S6 boş projelerim@${w}: "Klasör ekle" düğmesi var ve tıklanabilir`, /Klasör ekle/.test(mBos) && (await page.getByRole('button', { name: /Klasör ekle/ }).first().isEnabled().catch(() => false)), mBos.slice(0, 120).replace(/\n+/g, ' / '))
        await page.close()
      }
    }
    // hata: sunucu 500 döner / hiç cevap vermez / oturum ucu yok
    const hatalar = {
      'sunucu500': (page) => page.route('**/api/**', (r) => r.request().method() === 'GET' && !r.request().url().includes('/api/session') ? r.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'İç hata' }) }) : r.fallback()),
      'sunucuYok': (page) => page.route('**/api/**', (r) => r.abort('connectionrefused')),
      'oturumYok': (page) => page.route('**/api/session', (r) => r.abort('connectionrefused')),
      'iskaBos': (page) => page.route('**/api/**', (r) => r.request().method() === 'GET' && !r.request().url().includes('/api/session') ? r.fulfill({ status: 200, contentType: 'application/json', body: 'null' }) : r.fallback()),
    }
    for (const [ad, kur] of Object.entries(hatalar)) {
      for (const w of [440, 1280]) {
        for (const rota of ['suan', 'projelerim', 'ayarlar']) {
          if (w === 440 && rota !== 'suan') continue
          const page = await ctx.newPage()
          await page.setViewportSize({ width: w, height: 900 })
          const hatalarS6 = []
          page.on('pageerror', (e) => { hatalarS6.push(String(e).slice(0, 120)); gozlem('S6-' + ad, 'sayfa hatası', String(e).slice(0, 120)) })
          await kur(page)
          await page.goto(`${E.base}/#/${rota}`)
          await sleep(4500)
          await adim(page, 'S6', w, 0, `hata durumu ${ad}: ${rota}`, { slug: `hata-${ad}-${rota}` })
          const mS6 = (sonuc[`S6_hata_${ad}_${rota}_${w}`] = await govde(page))
          ok(`S6 ${ad} ${rota}@${w}: sayfa hatası (çökme) yok`, hatalarS6.length === 0, hatalarS6[0] ?? '')
          if (ad !== 'oturumYok') {
            if (rota === 'suan') {
              ok(`S6 ${ad} suan@${w}: "Şu an seni bekleyen yok" DENMİYOR (bilgi alınamadı)`, !/seni bekleyen yok/i.test(mS6), (mS6.match(/seni bekleyen yok[^\n]*/i) ?? [''])[0])
              ok(`S6 ${ad} suan@${w}: "Şu an bilgiye ulaşamıyorum. Tekrar dene." deniyor`, /Şu an bilgiye ulaşamıyorum/.test(mS6) && /Tekrar dene/.test(mS6), '')
            }
            if (rota === 'projelerim') ok(`S6 ${ad} projelerim@${w}: çökme yerine anlaşılır hata ya da boş durum`, !/reading 'filter'|undefined|TypeError/.test(mS6) && mS6.length > 40, mS6.slice(0, 120).replace(/\n+/g, ' / '))
          }
          console.log('SEÇENEKLER S6', ad, rota, JSON.stringify(await secenekler(page)))
          await page.close()
        }
      }
    }
    // yükleniyor: yavaş cevap, ilk 500 ms ve 2,5 sn sonrası
    for (const w of [440, 1280]) {
      const page = await ctx.newPage()
      await page.setViewportSize({ width: w, height: 900 })
      await page.route('**/api/**', async (r) => { if (r.request().url().includes('/api/session')) return r.fallback(); await sleep(5000); return r.fallback() })
      await page.goto(`${E.base}/#/suan`)
      await sleep(500)
      await adim(page, 'S6', w, 0, 'yükleniyor: 0,5 sn', { slug: 'yukleniyor-0' })
      const y0 = await govde(page)
      ok(`S6 yükleniyor@${w}: ilk yarım saniyede "seni bekleyen yok" DENMİYOR`, !/seni bekleyen yok/i.test(y0), (y0.match(/seni bekleyen yok[^\n]*/i) ?? [''])[0])
      await sleep(2500)
      await adim(page, 'S6', w, 0, 'yükleniyor: 3 sn', { slug: 'yukleniyor-3' })
      await sleep(5000)
      await adim(page, 'S6', w, 0, 'yükleniyor: 8 sn (cevaplar gelmiş olmalı)', { slug: 'yukleniyor-8' })
      await page.close()
    }
    // ilk tarama yavaş: Taramayı başlatan uç 8 sn beklesin
    {
      const page = await ctx.newPage()
      await page.setViewportSize({ width: 440, height: 900 })
      await page.route('**/api/profile', async (r) => { if (r.request().method() !== 'GET') return r.fallback(); const res = await r.fetch(); let j; try { j = await res.json() } catch { return r.fulfill({ response: res }) } return r.fulfill({ response: res, json: profilSuz(j, E.ekle?.()) }) })
      await page.route('**/api/profile/scan', async (r) => { await sleep(8000); return r.fallback() })
      await page.route('**/api/onboarding/scan', async (r) => { await sleep(8000); return r.fallback() })
      await page.goto(`${E.base}/#/suan`)
      await sleep(1500); await adim(page, 'S6', 440, 0, 'ilk tarama yavaş: 1,5 sn', { slug: 'tarama-yavas-1' })
      await sleep(4000); await adim(page, 'S6', 440, 0, 'ilk tarama yavaş: 5,5 sn', { slug: 'tarama-yavas-5' })
      await page.close()
    }
    E.kapat()
  }

  D?.kapat()
  writeFileSync(`${BASE}/sonuc${ETIKET}.json`, JSON.stringify({ ...sonuc, rows, bulgu, adimlar, engel: [...new Set(engel)] }, null, 2))
}

let kod = 0
try { await main() } catch (e) { console.log('HATA', e?.stack ?? e); kod = 1 } finally {
  for (const c of ctxler) { try { await c.close() } catch {} }
  for (const p of hepsi) { try { p.kill() } catch {} }
  const temiz = kapatVeDogrula() // sunucu + tarayıcı PID ile kapatıldı mı, kendi portlarında dinleyen kaldı mı
  if (temiz.kalan.length || temiz.dinleyen.length) kod = kod || 3
  const kaldi = rows.filter((r) => r.durum === 'KALDI').length
  console.log(`\nÖZET: ${rows.length - kaldi} geçti, ${kaldi} kaldı, ${bulgu.length} gözlem, ${shotN.length} görüntü`)
  if (kaldi) kod = kod || 1
  writeExitCode(kod)
  process.exitCode = kod
  setTimeout(() => process.exit(kod), 300)
}
