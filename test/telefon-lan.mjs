// Telefon (M11) düşmanca sınaması, makinenin GERÇEK yerel ağ adresine karşı.
// Kullanım: node test/telefon-lan.mjs "<soz.exe yolu>" [port]
// Sunucuyu bu betik başlatır (--exit-after ve --exit-with-parent ile), betik bitince sunucu da biter.
// Veri: sentetik, urun/_gecici/i2-tel/veri-<zaman> (gerçek veriye dokunmaz).
import { spawn } from 'node:child_process'
import net from 'node:net'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const urun = path.resolve(here, '..')
const exe = process.argv[2]
const port = Number(process.argv[3] ?? 49510)
if (!exe || !fs.existsSync(exe)) { console.error('soz.exe yolu ver'); process.exit(2) }
const data = path.join(urun, '_gecici', 'i2-tel', 'veri-' + Date.now())
fs.mkdirSync(data, { recursive: true })

const results = []
const ok = (name, cond, detail = '') => { results.push({ name, ok: !!cond, detail }); console.log(`${cond ? 'GEÇTİ' : 'KALDI'}  ${name}${detail ? '  · ' + detail : ''}`) }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

/** Ham HTTP/1.1 isteği (yol normalleştirilmeden). */
function raw(host, p, { method = 'GET', target = '/', hostHeader, cookie, body, extra = '' } = {}) {
  return new Promise((resolve) => {
    const s = net.connect({ host, port: p })
    const chunks = []
    s.setTimeout(8000, () => { s.destroy(); resolve({ status: -2, head: '', body: 'zaman aşımı' }) })
    s.on('error', (e) => resolve({ status: -1, head: '', body: e.code }))
    s.on('data', (d) => chunks.push(d))
    s.on('end', () => {
      const t = Buffer.concat(chunks).toString('utf8')
      const i = t.indexOf('\r\n\r\n')
      const head = i < 0 ? t : t.slice(0, i)
      let b = i < 0 ? '' : t.slice(i + 4)
      if (/transfer-encoding: chunked/i.test(head)) {
        let out = '', rest = Buffer.from(b, 'utf8')
        while (rest.length) {
          const nl = rest.indexOf('\r\n'); if (nl < 0) break
          const n = parseInt(rest.subarray(0, nl).toString(), 16); if (!n) break
          out += rest.subarray(nl + 2, nl + 2 + n).toString('utf8'); rest = rest.subarray(nl + 2 + n + 2)
        }
        b = out
      }
      resolve({ status: Number(head.split(' ')[1]), head, body: b })
    })
    s.on('connect', () => {
      const buf = body === undefined ? Buffer.alloc(0) : Buffer.from(body, 'utf8')
      let h = `${method} ${target} HTTP/1.1\r\nHost: ${hostHeader ?? host + ':' + p}\r\nConnection: close\r\n`
      if (cookie) h += `Cookie: ${cookie}\r\n`
      if (body !== undefined) h += `Content-Type: application/json\r\nContent-Length: ${buf.length}\r\n`
      s.write(h + extra + '\r\n')
      if (buf.length) s.write(buf, () => {})
    })
  })
}
const canConnect = (host, p) => new Promise((r) => { const s = net.connect({ host, port: p }); s.setTimeout(2000, () => { s.destroy(); r(false) }); s.on('connect', () => { s.destroy(); r(true) }); s.on('error', () => r(false)) })

const srv = spawn(exe, ['serve', '--data', data, '--port', String(port), '--exit-after', '150', '--exit-with-parent', String(process.pid)], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true })
let out = ''
srv.stdout.on('data', (d) => (out += d))
srv.stderr.on('data', (d) => (out += d))
const kill = () => { try { srv.kill() } catch {} }
process.on('exit', kill)

try {
  let base = null
  for (let i = 0; i < 100 && !base; i++) { await sleep(150); base = /(http:\/\/127\.0\.0\.1:\d+)/.exec(out)?.[1] ?? null }
  if (!base) throw new Error('sunucu açılmadı: ' + out)
  const mainPort = Number(new URL(base).port)
  const session = await (await fetch(base + '/api/session')).json()
  const H = { 'X-Soz-Token': session.token, 'Content-Type': 'application/json' }
  const put = (b) => fetch(base + '/api/settings', { method: 'PUT', headers: H, body: JSON.stringify(b) })

  // ---- varsayılan kapalı
  const p0 = await (await fetch(base + '/api/phone/pairing')).json()
  ok('varsayılan kapalı: enabled=false, url yok, dinleme yok', p0.enabled === false && !p0.url && p0.listening === false)
  ok('kapalıyken sunucu çıktısında telefon satırı yok', !/Telefon \(yerel ağ\)/.test(out))

  // ---- aç
  ok('belirteçsiz açma reddedilir (401)', (await fetch(base + '/api/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: '{"lanEnabled":true}' })).status === 401)
  ok('belirteçle açılır', (await put({ lanEnabled: true })).ok)
  const p1 = await (await fetch(base + '/api/phone/pairing')).json()
  ok('açık: listening=true, url var', p1.listening === true && !!p1.url, p1.error ?? '')
  const u = new URL(p1.url)
  const lanIp = u.hostname, lanPort = Number(u.port), code = u.searchParams.get('k')
  ok('adres özel ağ IPv4 (makinenin kendi yerel ağ adresi)', /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(lanIp), `${lanIp}:${lanPort} · tüm adresler: ${p1.addresses.join(', ')}`)
  ok('kod 128 bit (32 onaltılık)', /^[0-9a-f]{32}$/.test(code))
  if (mainPort !== lanPort) ok('ana uygulama yerel ağ adresinde DİNLEMİYOR', !(await canConnect(lanIp, mainPort)), `${lanIp}:${mainPort}`)
  else {
    // Aynı port numarası: 127.0.0.1'de ana uygulama, yerel ağ adresinde YALNIZ telefon uygulaması. Yerel ağdan ana uygulamaya ait uç cevap vermemeli.
    const r = await raw(lanIp, lanPort, { target: '/api/session' })
    ok('ana uygulama yerel ağ adresinde yok (aynı port numarasında yalnız telefon uygulaması cevap verir)', r.status === 404 && !/token/.test(r.body), `${lanIp}:${lanPort} /api/session → ${r.status}`)
  }

  // ---- eşleştirme
  ok('kodsuz /api/phone/now → 401', (await raw(lanIp, lanPort, { target: '/api/phone/now' })).status === 401)
  ok('uydurma çerez → 403', (await raw(lanIp, lanPort, { target: '/api/phone/now', cookie: 'soz_t=' + 'a'.repeat(64) })).status === 403)
  const bad = await raw(lanIp, lanPort, { target: '/t/?k=' + '0'.repeat(32) })
  ok('yanlış kod → 403 ve çerez yok', bad.status === 403 && !/set-cookie/i.test(bad.head))
  const good = await raw(lanIp, lanPort, { target: '/t/?k=' + code })
  const sc = good.head.split('\r\n').find((l) => /^set-cookie:/i.test(l)) ?? ''
  ok('doğru kod → yönlendirme + HttpOnly + SameSite=Strict çerez', good.status >= 301 && good.status <= 303 && /httponly/i.test(sc) && /samesite=strict/i.test(sc), sc.replace(/=([0-9a-f]{8})[0-9a-f]+/, '=$1…'))
  const cookie = sc.slice(sc.indexOf(':') + 1).trim().split(';')[0]
  const now = await raw(lanIp, lanPort, { target: '/api/phone/now', cookie })
  ok('çerezle /api/phone/now → 200', now.status === 200 && JSON.parse(now.body).waiting !== undefined)
  const page = await raw(lanIp, lanPort, { target: '/t/', cookie })
  ok('telefon sayfası /t/ → 200', page.status === 200, `${page.status} ` + (page.body.match(/(src|href)="[^"]+"/g) ?? []).join(' '))
  const refs = [...page.body.matchAll(/(?:src|href)="([^"]+)"/g)].map((m) => m[1]).filter((r) => r.startsWith('/'))
  for (const r of refs) ok(`sayfanın dosyası yerel ağdan yüklenir: ${r}`, (await raw(lanIp, lanPort, { target: r, cookie })).status === 200)

  // ---- yasak uçlar (geçerli çerezle bile)
  const forbidden = ['/api/settings', '/api/session', '/api/decisions', '/api/utterances?q=a', '/api/import', '/api/proof/artifact/1/cikti-1.txt', '/api/events/stream',
    '/api/phone/pairing', '/api/guard/rules', '/api/jobs', '/api/now', '/api/usage', '/api/doctor', '/api/hooks/health', '/api/packet', '/', '/index.html']
  for (const f of forbidden) { const r = await raw(lanIp, lanPort, { target: f, cookie }); ok(`yerel ağdan ${f} → 404`, r.status === 404, String(r.status)) }
  for (const f of ['/api/import', '/api/phone/pairing/renew', '/api/settings']) { const r = await raw(lanIp, lanPort, { method: 'POST', target: f, cookie, body: '{}' }); ok(`yerel ağdan POST ${f} → 404`, r.status === 404, String(r.status)) }

  // ---- yol kaçışı
  for (const t of ['/t/../api/settings', '/t/..%2fapi/settings', '/t/%2e%2e/api/settings', '/t/..\\api\\settings', '/t/%2e%2e/%2e%2e/veri/soz.db', '/t/index.html%00.txt']) {
    const r = await raw(lanIp, lanPort, { target: t, cookie }); ok(`yol kaçışı ${t} → 400/404`, (r.status === 400 || r.status === 404) && !/lanEnabled/.test(r.body), String(r.status))
  }
  // ---- Host oyunları
  for (const hh of ['evil.example', `127.0.0.1:${mainPort}`, `localhost:${lanPort}`, `${lanIp}.nip.io:${lanPort}`]) {
    const r = await raw(lanIp, lanPort, { target: '/api/phone/now', cookie, hostHeader: hh }); ok(`Host "${hh}" → 400`, r.status === 400, String(r.status))
  }
  // ---- gövde ve kuyruk
  const proj = (await (await fetch(base + '/api/projects')).json())[0]
  const pid = proj?.id ?? 1
  const q = (b, extra) => raw(lanIp, lanPort, { method: 'POST', target: '/api/phone/queue', cookie, body: JSON.stringify(b), extra })
  ok('çok uzun gövde (40 KB) → 413', (await q({ projectId: pid, text: 'y'.repeat(40000) })).status === 413)
  ok('1001 karakter metin → 400', (await q({ projectId: pid, text: 'x'.repeat(1001) })).status === 400)
  ok('boş metin → 400', (await q({ projectId: pid, text: '  ' })).status === 400)
  ok('başka köken (Origin) → 403', (await q({ projectId: pid, text: 'a' }, 'Origin: http://evil.example\r\n')).status === 403)
  const statuses = []
  for (let i = 0; i < 13; i++) statuses.push((await q({ projectId: pid, text: 'm' + i })).status)
  ok('dakikada 10 sınırı → 429 görülür', statuses.includes(429), statuses.join(','))

  // ---- kod yenileme
  const ren = await (await fetch(base + '/api/phone/pairing/renew', { method: 'POST', headers: H, body: '{}' })).json()
  const code2 = new URL(ren.url).searchParams.get('k')
  ok('yenileme yeni kod verir', code2 && code2 !== code)
  ok('yenilemeden sonra eski çerez → 403', (await raw(lanIp, lanPort, { target: '/api/phone/now', cookie })).status === 403)
  ok('yenilemeden sonra eski kod → 403', (await raw(lanIp, lanPort, { target: '/t/?k=' + code })).status === 403)

  // ---- kapat
  ok('kapatılır', (await put({ lanEnabled: false })).ok)
  await sleep(300)
  ok('kapalıyken yerel ağ portu bağlantı kabul etmez', !(await canConnect(lanIp, lanPort)), `${lanIp}:${lanPort}`)
} catch (e) {
  ok('betik hatası', false, String(e?.stack ?? e))
} finally {
  kill()
  const pass = results.filter((r) => r.ok).length
  console.log(`\nTOPLAM ${results.length} · GEÇTİ ${pass} · KALDI ${results.length - pass}`)
  fs.writeFileSync(path.join(urun, '_gecici', 'i2-tel', 'sonuc.json'), JSON.stringify(results, null, 2))
  process.exitCode = pass === results.length ? 0 : 1
}
