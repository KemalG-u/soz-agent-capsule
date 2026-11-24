// Sahte `hermes`: Söz'ün Hermes bağlayıcısını sınamak için `hermes chat --format stream-json` çıktısını taklit eder.
// GERÇEK hermes çalıştırılmaz (kullanıcının kotasını harcar, ortak arka plan hizmetine iş bırakırdı). Olay biçimi upstream hermes_cli/stream_json.py (v0.23.1) kaynağından (_gecici\h1\ARAYUZ.md):
//   {"type":"system","subtype":"init","session_id":"…"}  (yeni oturumda kimlik BOŞ olabilir)
//   {"type":"text","text":"parça"}  (metin parçalarıdır; art arda gelir)
//   {"type":"tool_use","tool_call_id","name","input":{…}} · {"type":"tool_result","tool_call_id","name","output","is_error"}
//   {"type":"result","session_id","text","exit_code","error?","tokens":{input,output,cache_read,…}}  (yeni oturumun kimliği ilk kez burada gelir)
// Bayrak denetimi gerçek `hermes chat --help` ile aynı mantık: bilinmeyen bayrak -> argparse hatası (çıkış 2); `--format stream-json` ve `--query-file -` zorunlu.
// Senaryo, istemdeki [[sahte:ad]] işaretinden seçilir; resume turunda işaret yoktur, senaryo oturum durum dosyasından okunur.
//   boz    : işi bozar (hesap.js bozuk, notlar.txt silinir, yeni.txt eklenir): geri alma sınaması
//   oku    : okur, dosya değiştirmez, "Bitti" der (ölçütler sahte işe bağlı)
//   gecer  : hesap.js'i ilk turda patch aracıyla düzeltir, testler geçer
//   duzelt : ilk turda düzeltmez; Söz geri yollayınca (--resume) düzeltir
//   bozuk  : hiç düzeltmez (3 kapı turunda kalır)
//   hile   : her turda testlerden birini test.skip yapar (kanıt kapısı "test dosyası değişti" demeli)
//   soru   : "Hangisini seçeyim?" diye sorar; cevap (resume) gelince bitirir
//   uzun   : torun süreç başlatıp uzun süre çalışır (durdurma sınaması)
//   hata   : stderr'e yazıp 1 ile çıkar
//   giris  : result.error "No inference provider configured" ve çıkış 1
//   limit  : result.error "usage limit" ve çıkış 1
//   girdi  : "input required" der ve 3 ile çıkar (başsız çalışmada onay istenemez)
// SAHTE_HERMES_LOG: argv, cwd, istem, resume, ortam kaydı dosyası. SAHTE_STATE_DIR: oturum durum dosyaları.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawn } from 'node:child_process'

const argv = process.argv.slice(2)
if (argv.includes('--version')) { process.stdout.write('Hermes Agent v0.0.0-sahte\n', () => process.exit(0)); await new Promise(() => {}) }

const VALUE_FLAGS = new Set(['--format', '--query-file', '--in', '-m', '--model', '--resume', '-r', '--provider', '--source'])
const KNOWN_BOOL = new Set(['--yolo', '--accept-hooks', '-q', '--quiet'])
if (argv[0] !== 'chat') { process.stderr.write(`sahte-hermes: yalnız "chat" desteklenir (gelen: ${argv[0]})\n`); process.exit(2) }
const flags = {}
for (let i = 1; i < argv.length; i++) {
  const t = argv[i]
  if (VALUE_FLAGS.has(t)) { flags[t] = argv[++i]; continue }
  if (KNOWN_BOOL.has(t)) { flags[t] = true; continue }
  process.stderr.write(`hermes chat: error: unrecognized arguments: ${t}\n`); process.exit(2)
}
if (flags['--format'] !== 'stream-json') { process.stderr.write('hermes chat: error: --format yalnız stream-json ile sınanıyor\n'); process.exit(2) }
if (flags['--query-file'] !== '-') { process.stderr.write('hermes chat: error: --query-file - bekleniyordu (istem standart girdiden)\n'); process.exit(2) }
const prompt = fs.readFileSync(0, 'utf8')
const resumeId = flags['--resume'] ?? null
const isResume = resumeId !== null
if (isResume && flags['-m']) { process.stderr.write('hermes chat: error: devam eden oturumda -m değiştirilemez\n'); process.exit(2) }
const sessionId = resumeId ?? 'sahte-oturum-' + crypto.randomUUID()
const stateDir = process.env.SAHTE_STATE_DIR ?? os.tmpdir()
const stateFile = path.join(stateDir, `sahte-hermes-${sessionId}.json`)

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

if (process.env.SAHTE_HERMES_LOG) {
  fs.appendFileSync(process.env.SAHTE_HERMES_LOG, JSON.stringify({
    argv, cwd: process.cwd(), prompt, resume: resumeId, isResume, flags, pid: process.pid, scenario, turn: turns,
    env: {
      SOZ_RUN_ID: process.env.SOZ_RUN_ID ?? null, SOZ_JOB_ID: process.env.SOZ_JOB_ID ?? null,
      PYTHONUTF8: process.env.PYTHONUTF8 ?? null, PYTHONIOENCODING: process.env.PYTHONIOENCODING ?? null,
      HERMES_HOME: process.env.HERMES_HOME ?? null, ANTHROPIC_API_KEY: process.env.ANTHROPIC_API_KEY ?? null, OPENAI_API_KEY: process.env.OPENAI_API_KEY ?? null,
    },
  }) + '\n')
}

const cwd = flags['--in'] ?? process.cwd()
const emit = (o) => process.stdout.write(JSON.stringify(o) + '\n')
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
let seq = 0
const say = (text) => { const m = Math.max(1, Math.floor(text.length / 2)); emit({ type: 'text', text: text.slice(0, m) }); emit({ type: 'text', text: text.slice(m) }) } // iki parça: çevirici birleştirmeli
// Gerçek biçim: tool_use sonra tool_result. `effect` aracın gerçek etkisidir.
const tool = (name, input, output, isError = false, effect = null) => {
  const id = `call_${seq++}`
  emit({ type: 'tool_use', tool_call_id: id, name, input })
  if (effect) effect()
  emit({ type: 'tool_result', tool_call_id: id, name, output, is_error: isError })
  return !isError
}
const finish = (text, exitCode = 0, error = null) => new Promise(() => {
  const line = { type: 'result', session_id: sessionId, text, exit_code: exitCode, tokens: { input: 1000 * turns, output: 100 * turns, cache_read: 500 * turns } }
  if (error) line.error = error
  process.stdout.write(JSON.stringify(line) + '\n', () => process.exit(exitCode))
})

emit({ type: 'system', subtype: 'init', session_id: isResume ? sessionId : '' }) // yeni oturumda kimlik henüz yok

if (scenario === 'hata') { process.stderr.write('boom: sahte hermes bir sey ters gitti\n', () => process.exit(1)); await new Promise(() => {}) }
if (scenario === 'giris') await finish('', 1, 'No inference provider configured. Run `hermes setup` or `hermes model`.')
if (scenario === 'limit') await finish('', 1, "You've hit your usage limit. Try again later.")
if (scenario === 'girdi') { process.stderr.write('input required: dangerous command needs approval; detached without cancelling\n', () => process.exit(3)); await new Promise(() => {}) }

if (scenario === 'uzun') {
  const torun = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 120000)'], { stdio: 'ignore' })
  if (process.env.SAHTE_TORUN) fs.writeFileSync(process.env.SAHTE_TORUN, String(torun.pid))
  for (let i = 0; i < 400; i++) { say(`calisiyorum ${i}`); tool('todo', {}, 'ok'); await sleep(300) }
  await finish('bitti')
}

if (scenario === 'soru') {
  if (!isResume) { say('İki yol var. Hangisini seçeyim?'); await finish('İki yol var. Hangisini seçeyim?') }
  say('Tamam, seçtiğin yolla bitirdim. Testler geçti: 2/2')
  await finish('Tamam, seçtiğin yolla bitirdim. Testler geçti: 2/2')
}

const hesap = path.join(cwd, 'hesap.js')
const fix = () => { if (fs.existsSync(hesap)) fs.writeFileSync(hesap, fs.readFileSync(hesap, 'utf8').replace('a - b', 'a + b')) }
if (scenario === 'boz') {
  // ajan işi bozar: hesap.js'i bozuk yazar, notlar.txt'yi siler, yeni.txt ekler (geri alma sınaması); her turda aynı
  tool('write_file', { path: hesap, content: '...' }, 'ok', false, () => {
    fs.writeFileSync(hesap, 'AJAN BOZDU {{{\n')
    try { fs.unlinkSync(path.join(cwd, 'notlar.txt')) } catch { /* yoksay */ }
    fs.writeFileSync(path.join(cwd, 'yeni.txt'), 'ajanin dosyasi\n')
  })
  say('Bitti.')
  await finish('Bitti.')
}
tool('read_file', { path: hesap }, 'function topla(a, b) { return a - b }')
tool('terminal', { command: 'npm test' }, 'tests 2\npass 1\nfail 1', true)

let text = 'Bitti. Testler geçti: 2/2 (hesap.js)'
if (scenario === 'gecer') {
  tool('patch', { path: hesap, old_string: 'a - b', new_string: 'a + b' }, 'ok', false, fix)
  tool('terminal', { command: 'npm test' }, 'tests 2\npass 2')
  text = 'Düzelttim. Testler geçti: 2/2'
} else if (scenario === 'duzelt' && isResume) {
  tool('patch', { path: hesap, old_string: 'a - b', new_string: 'a + b' }, 'ok', false, fix)
  tool('terminal', { command: 'npm test' }, 'tests 2\npass 2')
  text = 'Düzelttim. Testler geçti: 2/2'
} else if (scenario === 'hile') {
  const f = path.join(cwd, 'test', 'hesap.test.js')
  tool('write_file', { path: f, content: '...' }, 'ok', false, () => {
    if (fs.existsSync(f)) { const t = fs.readFileSync(f, 'utf8'); if (!t.includes('test.skip(')) fs.writeFileSync(f, t.replace('test(', 'test.skip(')) }
  })
  text = 'Bitti. Testler geçti: 0 kalan.'
} else if (scenario === 'bozuk' && isResume) {
  text = 'Düzeltemedim, hâlâ aynı.'
}
say(text)
await finish(text)
