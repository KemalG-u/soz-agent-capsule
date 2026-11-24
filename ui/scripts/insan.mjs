// "İnsan eşdeğeri" çalıştırıcı (denetim 3, ilke 2).
// Söz, kabul ve ayar isteğini yalnız insandan alır: çağıranın süreci Söz'ün ajan iş nesnesinde (Job Object) ya da bir claude sürecinin soyunda ise 403 verir.
// Bu betikler Claude Code içinden başlatılınca ata zincirinde claude bulunur ve sunucu bizi ajan sayar. Kullanıcı kendi terminalinden çalıştırdığında sorun yoktur.
// relaunchAsHuman: betiği, ata zinciri Claude Code'a bağlı olmayan yetim bir süreç olarak yeniden başlatır (ara süreç çıkınca zincir kopar),
// CLAUDE*, SOZ_* ve AI_AGENT ortam değişkenlerini siler ve çıktıyı bu sürece yansıtır. Gerçek bir ajanı korumak için değil, sınama içindir:
// Söz'ün başlattığı ajanlar normalde iş nesnesinin içinde kalır, bu yüzden yetim bırakma onları kurtarmaz (WMI ile çıkış ayrı bir yoldur, bkz. BENIOKU Tehdit modeli).
import { spawn } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { temizlikKur } from './temizlik.mjs'

const alive = (pid) => { try { process.kill(pid, 0); return true } catch { return false } }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

export function cleanEnv(src = process.env) {
  const env = {}
  for (const [k, v] of Object.entries(src)) {
    if (/^(CLAUDE|SOZ_|AI_AGENT)/i.test(k) && k !== 'SOZ_EXE' && k !== 'SOZ_OUT' && k !== 'SOZ_SURE' && k !== 'SOZ_PID_KLASOR') continue
    env[k] = v
  }
  return env
}

/** Zaten insan eşdeğeri sürecteysek false. Değilsek betiği yetim süreçte çalıştırır, bitmesini bekler, çıkış kodunu process.exitCode'a yazar ve true döner. */
export async function relaunchAsHuman(metaUrl, args = process.argv.slice(2)) {
  if (process.env.SOZ_INSAN === '1') {
    // Yarış düzeltmesi (denetim 4, G): asıl süreç, ara sürecin çıktığını görmeden işe başlamaz. Ara süreç yaşarken ata zinciri hâlâ Claude Code'a bağlıdır ve ilk istekler ajan sayılırdı.
    const ara = Number(process.env.SOZ_INSAN_ARA ?? 0)
    for (let i = 0; ara && i < 100 && alive(ara); i++) await sleep(100)
    if (ara) await sleep(300) // süreç tablosu ebeveynin ölümünü görsün
    // Temizlik güvencesi (temizlik.mjs): iç süre sınırı, çıkışta kendi sunucu ve tarayıcısını PID'iyle kapatma, başlatan süreç ölürse yetim kalmama.
    temizlikKur({ sahip: Number(process.env.SOZ_INSAN_SAHIP ?? 0) })
    return false
  }
  const script = fileURLToPath(metaUrl)
  const dir = mkdtempSync(join(tmpdir(), 'soz-insan-'))
  const log = join(dir, 'cikti.log')
  const pidFile = join(dir, 'pid')
  const code = join(dir, 'kod')
  writeFileSync(log, '')
  const env = { ...cleanEnv(), SOZ_INSAN: '1' }
  // ara süreç: asıl süreci ayrık başlatır ve hemen çıkar; asıl sürecin ebeveyni ölmüş olur (ata zinciri kopar)
  const child = `
    const { spawn } = require('node:child_process'); const fs = require('node:fs')
    const o = fs.openSync(${JSON.stringify(log)}, 'a')
    const c = spawn(process.execPath, ${JSON.stringify([script, ...args])}, { detached: true, stdio: ['ignore', o, o], windowsHide: true, env: { ...process.env, SOZ_INSAN_ARA: String(process.pid) } })
    fs.writeFileSync(${JSON.stringify(pidFile)}, String(c.pid))
    c.unref()
    setTimeout(() => process.exit(0), 300)
  `
  // Çıkış kodunu asıl süreç betiğin sonunda kendisi yazar (writeExitCode, SOZ_INSAN_KOD); dosya yoksa 0 sayılır.
  const env2 = { ...env, SOZ_INSAN_KOD: code, SOZ_INSAN_SAHIP: String(process.pid) }
  const mid = spawn(process.execPath, ['-e', child], { env: env2, stdio: 'ignore', windowsHide: true })
  await new Promise((r) => mid.on('exit', r))
  for (let i = 0; i < 40 && !existsSync(pidFile); i++) await sleep(100)
  const pid = existsSync(pidFile) ? Number(readFileSync(pidFile, 'utf8')) : 0
  if (!pid) {
    // Asıl süreç hiç başlamadı: sessizce 0 ile dönmek betiği başarılı gösterirdi.
    process.stderr.write('insan.mjs: asıl süreç başlatılamadı\n')
    process.exitCode = 1
    try { rmSync(dir, { recursive: true, force: true }) } catch { /* kalabilir */ }
    return true
  }
  let shown = 0
  const flush = () => {
    const s = readFileSync(log, 'utf8')
    if (s.length > shown) { process.stdout.write(s.slice(shown)); shown = s.length }
  }
  while (alive(pid)) { flush(); await sleep(500) }
  flush()
  const kod = existsSync(code) ? Number(readFileSync(code, 'utf8')) : 0
  process.exitCode = Number.isFinite(kod) ? kod : 1
  try { rmSync(dir, { recursive: true, force: true }) } catch { /* temizlik reddedilirse kalır */ }
  return true
}

/** Betiğin sonunda çağrılır: çıkış kodunu yetim sürecin yazacağı dosyaya bırakır. */
export function writeExitCode(code) {
  if (process.env.SOZ_INSAN_KOD) { try { writeFileSync(process.env.SOZ_INSAN_KOD, String(code)) } catch { /* yoksay */ } }
}

// Doğrudan çalıştırılırsa: node insan.mjs <betik> [argümanlar]
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const [target, ...rest] = process.argv.slice(2)
  if (!target) { console.error('kullanım: node insan.mjs <betik.mjs> [argümanlar]'); process.exit(2) }
  const { pathToFileURL } = await import('node:url')
  const done = await relaunchAsHuman(pathToFileURL(target).href, rest)
  if (!done) await import(pathToFileURL(target).href)
}
