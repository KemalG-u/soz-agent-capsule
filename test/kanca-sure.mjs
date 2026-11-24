// Kanca süreleri: çağıran tarafın duvar saati. Zaman damgası: spawnSync'e girmeden hemen önce, süreç çıkıp çıktısı toplandıktan hemen sonra
// (process.hrtime.bigint; süreç başlatma + stdin yazma + çalışma + çıkış dahil). Boş süreç tabanı aynı yolla ölçülür.
//
// Kullanım:
//   node test/kanca-sure.mjs "<soz-kanca.exe veya soz.exe>" [--n 30] [--durum acik|kapali|ikisi] [--sunucu-exe "<soz.exe>"] [--http] [--cikti <klasör>]
//     --durum   Söz sunucusu açık mı kapalı mı: ikisi = ikisi de (varsayılan). "açık" için kendi sunucusunu 49540-49590 arası bir kapıda
//               `soz.exe serve --exit-after` ile açar, işi bitince kendi PID'iyle kapatır.
//     --http    Ek olarak Claude Code'un http kancası yolunu (POST /api/hooks/<olay>, jetonlu) süreç açmadan ölçer.
// Veri ve proje: sentetik, urun/_gecici/k6/kanca-sure-<zaman>/. Gerçek veriye dokunmaz.
import { spawnSync, spawn } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import http from 'node:http'
import { fileURLToPath } from 'node:url'

const urun = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const argv = process.argv.slice(2)
const exe = argv.find((a) => !a.startsWith('--') && fs.existsSync(a))
const opt = (name, def) => { const i = argv.indexOf('--' + name); return i >= 0 && argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[i + 1] : def }
const n = Number(opt('n', 30))
const durum = opt('durum', 'ikisi')
const wantHttp = argv.includes('--http')
if (!exe) { console.error('soz-kanca.exe ya da soz.exe yolunu ver'); process.exit(2) }
const sozExe = opt('sunucu-exe', path.join(path.dirname(exe), 'soz.exe'))
const tutucu = opt('tutucu', null) // Claude oturumundan `soz serve` açılamıyorsa: _gecici/k6/SunucuTutucu çıktısı (soz-k6-tutucu.exe)
const stamp = new Date().toISOString().replace(/[:.]/g, '-')
const root = path.join(opt('cikti', path.join(urun, '_gecici', 'k6')), 'kanca-sure-' + stamp)
const proj = path.join(root, 'proje')
fs.mkdirSync(proj, { recursive: true })
const env = { ...process.env }
if (fs.existsSync(path.join(path.dirname(exe), 'coreclr.dll'))) delete env.DOTNET_ROOT
delete env.SOZ_SERVER_URL

const inputs = {
  'session-start': { session_id: 'sure-1', cwd: proj, source: 'startup' },
  'user-prompt-submit': { session_id: 'sure-1', cwd: proj, prompt: 'deneme istemi' },
  'pre-tool-use': { session_id: 'sure-1', cwd: proj, tool_name: 'Bash', tool_input: { command: 'ls -la' } },
  notification: { session_id: 'sure-1', cwd: proj, message: 'Claude is waiting for your input', notification_type: 'idle_prompt' },
  stop: { session_id: 'sure-1', cwd: proj, stop_hook_active: false, last_assistant_message: 'Bitti.' },
  'stop-failure': { session_id: 'sure-1', cwd: proj, hook_event_name: 'StopFailure', error: 'rate_limit' },
}

const nowMs = () => Number(process.hrtime.bigint()) / 1e6
const pct = (a, p) => { const s = [...a].sort((x, y) => x - y); return s[Math.min(s.length - 1, Math.floor(s.length * p))] }
const stat = (a) => ({ n: a.length, min: +Math.min(...a).toFixed(1), medyan: +pct(a, 0.5).toFixed(1), p95: +pct(a, 0.95).toFixed(1), max: +Math.max(...a).toFixed(1) })

function timeSpawn(file, args, input) {
  const t = nowMs()
  const r = spawnSync(file, args, { input, env, windowsHide: true })
  return { ms: nowMs() - t, status: r.status, out: r.stdout?.length ?? 0 }
}

// Boş süreç tabanı: aynı çalışma zamanı ailesinden boş exe (csc ile derlenir; yoksa cmd /c exit).
function emptyBaselines() {
  const list = [{ ad: 'cmd /c exit (yerel taban)', file: process.env.ComSpec ?? 'C:\\Windows\\System32\\cmd.exe', args: ['/c', 'exit', '0'] }]
  const csc = 'C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe'
  if (fs.existsSync(csc)) {
    const src = path.join(root, 'bos.cs'); const out = path.join(root, 'bos-fw.exe')
    fs.writeFileSync(src, 'internal static class P { private static int Main() { return 0; } }')
    const r = spawnSync(csc, ['-nologo', '-optimize+', '-out:' + out, src], { windowsHide: true })
    if (r.status === 0) list.push({ ad: 'boş .NET Framework exe (soz-kanca ile aynı çalışma zamanı)', file: out, args: [] })
  }
  return list
}

async function startServer() {
  const dataDir = path.join(root, 'veri-acik')
  const port = 49540 + Math.floor(Math.random() * 40)
  const stopFile = path.join(root, 'dur-' + port)
  const child = tutucu
    ? spawn(tutucu, [dataDir, String(port), stopFile, '900'], { env: { ...env, DOTNET_ROOT: process.env.SOZ_DOTNET_ROOT ?? process.env.DOTNET_ROOT }, windowsHide: true, stdio: 'ignore' })
    : spawn(sozExe, ['serve', '--port', String(port), '--exit-after', '900', '--data', dataDir], { env, windowsHide: true, stdio: 'ignore' })
  const sf = path.join(dataDir, 'sunucu.json')
  for (let i = 0; i < 100 && !fs.existsSync(sf); i++) await new Promise((r) => setTimeout(r, 200))
  if (!fs.existsSync(sf)) { try { child.kill() } catch {} ; throw new Error('deneme sunucusu açılmadı: ' + sozExe) }
  const url = JSON.parse(fs.readFileSync(sf, 'utf8')).url
  return { child, dataDir, url, stopFile }
}

function measureCommand(dataDir, label) {
  const rows = []
  const first = timeSpawn(exe, ['hook', 'session-start', '--data', dataDir], JSON.stringify(inputs['session-start']))
  console.log(`  [${label}] ilk çalıştırma (veritabanı/ısınma): ${first.ms.toFixed(0)} ms`)
  for (const [name, inp] of Object.entries(inputs)) {
    const ms = []; let bad = 0
    for (let i = 0; i < n; i++) { const r = timeSpawn(exe, ['hook', name, '--data', dataDir], JSON.stringify(inp)); ms.push(r.ms); if (r.status !== 0) bad++ }
    rows.push({ olay: name, ...stat(ms), cikis0degil: bad })
  }
  return rows
}

function post(url, ev, token, body) {
  return new Promise((resolve) => {
    const u = new URL(url)
    const t = nowMs()
    const req = http.request({ host: u.hostname, port: u.port, path: '/api/hooks/' + ev, method: 'POST', headers: { 'content-type': 'application/json', 'x-soz-hook-token': token ?? '' } }, (res) => {
      res.resume(); res.on('end', () => resolve({ ms: nowMs() - t, status: res.statusCode }))
    })
    req.on('error', () => resolve({ ms: nowMs() - t, status: 0 }))
    req.end(JSON.stringify(body))
  })
}

const report = { zaman: new Date().toISOString(), exe, n, zamanDamgasi: 'çağıran taraf: spawnSync girişinden çıkışa (süreç başlatma + stdin + çalışma + çıkış)', taban: [], kapali: null, acik: null, http: null }
console.log(`kanca-sure: ${exe} · n=${n}`)
for (const b of emptyBaselines()) {
  const ms = []; for (let i = 0; i < n; i++) ms.push(timeSpawn(b.file, b.args, '{}').ms)
  const s = stat(ms); report.taban.push({ ad: b.ad, ...s }); console.log(`taban · ${b.ad}: medyan ${s.medyan} ms · p95 ${s.p95} ms`)
}

if (durum === 'kapali' || durum === 'ikisi') {
  console.log('SUNUCU KAPALI (sunucu.json yok):')
  report.kapali = measureCommand(path.join(root, 'veri-kapali'), 'kapalı')
  for (const r of report.kapali) console.log(`  ${r.olay.padEnd(19)} medyan ${r.medyan} ms · p95 ${r.p95} ms · en çok ${r.max} ms · çıkış≠0: ${r.cikis0degil}`)
  // Bayat işaret: sunucu.json var ama süreç ölü, kapı kapalı (çökme sonrası)
  const stale = path.join(root, 'veri-bayat'); fs.mkdirSync(stale, { recursive: true })
  fs.writeFileSync(path.join(stale, 'sunucu.json'), JSON.stringify({ url: 'http://127.0.0.1:49599', pid: 999999, startedAt: new Date().toISOString() }))
  console.log('SUNUCU BAYAT İŞARET (sunucu.json var, süreç yok):')
  report.bayat = measureCommand(stale, 'bayat')
  for (const r of report.bayat) console.log(`  ${r.olay.padEnd(19)} medyan ${r.medyan} ms · p95 ${r.p95} ms · en çok ${r.max} ms · çıkış≠0: ${r.cikis0degil}`)
}

if (durum === 'acik' || durum === 'ikisi') {
  let srv
  try {
    srv = await startServer()
    console.log(`SUNUCU AÇIK (${srv.url}):`)
    report.acik = measureCommand(srv.dataDir, 'açık')
    for (const r of report.acik) console.log(`  ${r.olay.padEnd(19)} medyan ${r.medyan} ms · p95 ${r.p95} ms · en çok ${r.max} ms · çıkış≠0: ${r.cikis0degil}`)
    if (wantHttp) {
      const tokFile = path.join(srv.dataDir, 'kanca-jeton')
      const token = fs.existsSync(tokFile) ? fs.readFileSync(tokFile, 'utf8').trim() : ''
      report.http = []
      console.log('HTTP KANCA YOLU (süreç açmadan, Claude Code http kancası gibi):')
      for (const [name, inp] of Object.entries(inputs)) {
        if (name === 'session-start' || name === 'stop') continue
        const ms = []; let bad = 0
        for (let i = 0; i < n; i++) { const r = await post(srv.url, name, token, inp); ms.push(r.ms); if (r.status !== 200) bad++ }
        const s = stat(ms); report.http.push({ olay: name, ...s, durum200degil: bad })
        console.log(`  ${name.padEnd(19)} medyan ${s.medyan} ms · p95 ${s.p95} ms · 200 olmayan: ${bad}`)
      }
    }
    const doc = spawnSync(exe.replace(/soz-kanca\.exe$/i, 'soz.exe'), ['doctor', '--json', '--data', srv.dataDir], { env, windowsHide: true, encoding: 'utf8' })
    try { const st = JSON.parse(doc.stdout).hookHealth.stats; console.log('nabız (son 24 sa): ' + st.map((s) => `${s.name}=${s.runs24h}/${s.errors24h} hata`).join(', ')) } catch { console.log('doktor çıktısı okunamadı') }
  } finally {
    if (srv) { try { fs.writeFileSync(srv.stopFile, '1') } catch {} ; await new Promise((r) => setTimeout(r, 600)); try { srv.child.kill() } catch {} }
  }
}
fs.writeFileSync(path.join(root, 'sonuc.json'), JSON.stringify(report, null, 2))
console.log('ayrıntı: ' + path.join(root, 'sonuc.json'))
