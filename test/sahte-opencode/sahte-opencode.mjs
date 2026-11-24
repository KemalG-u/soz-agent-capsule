// Sahte `opencode`: Söz'ün OpenCode bağlayıcısını sınamak için `opencode run --format json` çıktısını taklit eder.
// GERÇEK opencode çalıştırılmaz (kullanıcının kotasını harcar). Olay biçimi `opencode run --format json` kaynağından (_gecici\h1\ARAYUZ.md):
//   Her satır {"type","timestamp","sessionID","part"|"error"}: step_start · text{part.text} · tool_use{part.tool, part.state{status,input,output,metadata}} (araç BİTİNCE gelir)
//   · step_finish{part.reason,"tokens":{input,output,reasoning,cache:{read,write}},"cost"} · error{error:{message}} (2.x) ya da {error:{name,data:{message}}} (1.x)
// SAHTE_OPENCODE_SURUM: `--version` çıktısı (varsayılan 2.0.0-sahte). Ana sürüm 1 iken `--standalone` BİLİNMEYEN bayraktır (gerçek 1.x gibi): hata ve çıkış 1.
// Senaryo, istemdeki [[sahte:ad]] işaretinden seçilir; `--session` ile devamda senaryo oturum durum dosyasından okunur.
//   oku, gecer, duzelt, bozuk, boz, hile, soru, uzun, hata: sahte-hermes ile aynı anlam
//   giris  : error olayı "not logged in ... opencode auth login", çıkış 1
//   limit  : error olayı "usage limit", çıkış 1
//   ucret  : step_finish cost 0.0123 (dolar gösterimi sınaması)
// SAHTE_OPENCODE_LOG: argv, cwd, istem, session, ortam kaydı dosyası. SAHTE_STATE_DIR: oturum durum dosyaları.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawn } from 'node:child_process'

const argv = process.argv.slice(2)
const version = process.env.SAHTE_OPENCODE_SURUM ?? '2.0.0-sahte'
if (argv.includes('--version')) { process.stdout.write(version + '\n', () => process.exit(0)); await new Promise(() => {}) }
const major = parseInt(version.split('.')[0], 10)

const VALUE_FLAGS = new Set(['--format', '--agent', '--session', '-s', '--model', '-m'])
const KNOWN_BOOL = new Set(['--standalone', '--continue', '-c', '--print-logs'])
if (argv[0] !== 'run') { process.stderr.write(`sahte-opencode: yalnız "run" desteklenir (gelen: ${argv[0]})\n`); process.exit(2) }
const flags = {}
for (let i = 1; i < argv.length; i++) {
  const t = argv[i]
  if (t === '--standalone' && major < 2) { process.stderr.write('Unknown argument: standalone\n'); process.exit(1) }
  if (VALUE_FLAGS.has(t)) { flags[t] = argv[++i]; continue }
  if (KNOWN_BOOL.has(t)) { flags[t] = true; continue }
  if (t.startsWith('-')) { process.stderr.write(`Unknown argument: ${t}\n`); process.exit(1) }
  process.stderr.write(`sahte-opencode: istem standart girdiden bekleniyor, argüman geldi: ${t}\n`); process.exit(1)
}
if (flags['--format'] !== 'json') { process.stderr.write('sahte-opencode: --format json bekleniyordu\n'); process.exit(1) }
const prompt = fs.readFileSync(0, 'utf8')
const sessionArg = flags['--session'] ?? null
const isResume = sessionArg !== null
const sessionId = sessionArg ?? 'ses_sahte' + crypto.randomUUID().replace(/-/g, '').slice(0, 16)
const stateDir = process.env.SAHTE_STATE_DIR ?? os.tmpdir()
const stateFile = path.join(stateDir, `sahte-opencode-${sessionId}.json`)

let scenario = (/\[\[sahte:(\w+)\]\]/.exec(prompt) ?? [])[1]
let turns = 0
if (isResume && fs.existsSync(stateFile)) {
  const st = JSON.parse(fs.readFileSync(stateFile, 'utf8'))
  scenario ??= st.scenario
  turns = st.turns ?? 0
}
scenario ??= 'oku'
turns += 1
try { fs.writeFileSync(stateFile, JSON.stringify({ scenario, turns })) } catch { /* yoksay */ }

if (process.env.SAHTE_OPENCODE_LOG) {
  fs.appendFileSync(process.env.SAHTE_OPENCODE_LOG, JSON.stringify({
    argv, cwd: process.cwd(), prompt, session: sessionArg, isResume, flags, pid: process.pid, scenario, turn: turns,
    env: { SOZ_RUN_ID: process.env.SOZ_RUN_ID ?? null, SOZ_JOB_ID: process.env.SOZ_JOB_ID ?? null, OPENAI_API_KEY: process.env.OPENAI_API_KEY ?? null, ANTHROPIC_API_KEY: process.env.ANTHROPIC_API_KEY ?? null },
  }) + '\n')
}

const cwd = process.cwd()
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
let seq = 0
const base = (type, extra) => ({ type, timestamp: Date.now(), sessionID: sessionId, ...extra })
const emit = (o) => process.stdout.write(JSON.stringify(o) + '\n')
const step = () => emit(base('step_start', { part: { id: `prt_${seq++}`, type: 'step-start' } }))
const say = (text) => emit(base('text', { part: { id: `prt_${seq++}`, type: 'text', text } }))
const stepEnd = (reason, cost = 0) => emit(base('step_finish', { part: { id: `prt_${seq++}`, type: 'step-finish', reason, cost, tokens: { input: 1000 * turns, output: 100 * turns, reasoning: 20 * turns, cache: { read: 500 * turns, write: 0 } } } }))
// tool_use araç BİTİNCE gelir (state.status completed | error).
const tool = (name, input, output, { exit = 0, error = null, effect = null } = {}) => {
  if (effect) effect()
  const state = error ? { status: 'error', input, error } : { status: 'completed', input, output, metadata: { exit } }
  emit(base('tool_use', { part: { id: `prt_${seq++}`, type: 'tool', tool: name, callID: `call_${seq}`, state } }))
}
const fail = (message, v1 = false) => new Promise(() => {
  const err = v1 ? { name: 'APIError', data: { message } } : { name: 'APIError', message }
  process.stdout.write(JSON.stringify(base('error', { error: err })) + '\n', () => process.exit(1))
})
const done = () => new Promise(() => { process.stdout.write('', () => process.exit(0)) })

if (scenario === 'hata') { process.stderr.write('boom: sahte opencode bir sey ters gitti\n', () => process.exit(1)); await new Promise(() => {}) }
if (scenario === 'giris') { step(); await fail('Not logged in. Run `opencode auth login` to add a provider credential.', major < 2) }
if (scenario === 'limit') { step(); await fail("You've hit your usage limit. Try again later.", major < 2) }

step()
if (scenario === 'uzun') {
  const torun = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 120000)'], { stdio: 'ignore' })
  if (process.env.SAHTE_TORUN) fs.writeFileSync(process.env.SAHTE_TORUN, String(torun.pid))
  for (let i = 0; i < 400; i++) { say(`calisiyorum ${i}`); tool('todowrite', {}, 'ok'); await sleep(300) }
  stepEnd('stop'); await done()
}
if (scenario === 'soru') {
  if (!isResume) { say('İki yol var. Hangisini seçeyim?'); stepEnd('stop'); await done() }
  say('Tamam, seçtiğin yolla bitirdim. Testler geçti: 2/2'); stepEnd('stop'); await done()
}

const hesap = path.join(cwd, 'hesap.js')
const fix = () => { if (fs.existsSync(hesap)) fs.writeFileSync(hesap, fs.readFileSync(hesap, 'utf8').replace('a - b', 'a + b')) }
if (scenario === 'boz') {
  // ajan işi bozar: hesap.js'i bozuk yazar, notlar.txt'yi siler, yeni.txt ekler (geri alma sınaması); her turda aynı
  tool('write', { filePath: hesap, content: '...' }, 'ok', { effect: () => {
    fs.writeFileSync(hesap, 'AJAN BOZDU {{{\n')
    try { fs.unlinkSync(path.join(cwd, 'notlar.txt')) } catch { /* yoksay */ }
    fs.writeFileSync(path.join(cwd, 'yeni.txt'), 'ajanin dosyasi\n')
  } })
  say('Bitti.'); stepEnd('stop'); await done()
}
tool('read', { filePath: hesap }, 'function topla(a, b) { return a - b }')
tool('bash', { command: 'npm test', description: 'testleri çalıştır' }, 'tests 2\npass 1\nfail 1', { exit: 1 })
stepEnd('tool-calls')
step()

let text = 'Bitti. Testler geçti: 2/2 (hesap.js)'
if (scenario === 'gecer') {
  tool('edit', { filePath: hesap, oldString: 'a - b', newString: 'a + b' }, 'ok', { effect: fix })
  tool('bash', { command: 'npm test' }, 'tests 2\npass 2')
  text = 'Düzelttim. Testler geçti: 2/2'
} else if (scenario === 'duzelt' && isResume) {
  tool('edit', { filePath: hesap, oldString: 'a - b', newString: 'a + b' }, 'ok', { effect: fix })
  tool('bash', { command: 'npm test' }, 'tests 2\npass 2')
  text = 'Düzelttim. Testler geçti: 2/2'
} else if (scenario === 'hile') {
  const f = path.join(cwd, 'test', 'hesap.test.js')
  tool('write', { filePath: f, content: '...' }, 'ok', { effect: () => {
    if (fs.existsSync(f)) { const t = fs.readFileSync(f, 'utf8'); if (!t.includes('test.skip(')) fs.writeFileSync(f, t.replace('test(', 'test.skip(')) }
  } })
  text = 'Bitti. Testler geçti: 0 kalan.'
} else if (scenario === 'bozuk' && isResume) {
  text = 'Düzeltemedim, hâlâ aynı.'
}
say(text)
stepEnd('stop', scenario === 'ucret' ? 0.0123 : 0)
await done()
