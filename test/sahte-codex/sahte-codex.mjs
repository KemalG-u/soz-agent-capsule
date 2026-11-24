// Sahte `codex`: Söz'ün Codex bağlayıcısını sınamak için `codex exec --json` ve `codex exec resume --json` çıktısını taklit eder.
// GERÇEK codex çalıştırılmaz (kullanıcının kotasını harcar). Olay biçimi GERÇEK Codex 0.167.0 duman denemesinden (11 Kas 2026):
//   {"type":"thread.started","thread_id":"…"} (exec resume de AYNI kimlikle yeniden basar) · {"type":"turn.started"}
//   {"type":"item.started|item.completed","item":{id,type,...}}; command_execution{command,aggregated_output,exit_code (çalışırken null),status in_progress|completed|failed}
//   agent_message{text}; dosyayı Set-Content ile yazan komut için ayrı file_change olayı YOKTUR (dosya değişikliği diskten ölçülür); file_change yalnız apply_patch gibi araçlarda gelir
//   {"type":"turn.completed","usage":{input_tokens,cached_input_tokens,cache_write_input_tokens,output_tokens,reasoning_output_tokens}} · {"type":"turn.failed","error":{"message":"…"}}
//   stdout'a JSON olmayan günlük satırları da karışır: "2026-11-11T12:32:10.701972Z ERROR codex_core::tools::router: error=..."
// Windows kum havuzu taklidi: SAHTE_ELEVATED=1 iken ve komut satırında `-c windows.sandbox="unelevated"` YOKSA her komut gerçek makinedeki hatayla düşer
//   (exit_code -1, status failed, "orchestrator_helper_launch_canceled ... 1223") ve komutun etkisi (dosya yazma) olmaz. Bayrak varsa komutlar çalışır.
// Bayrak denetimi gerçek `codex exec --help` (0.167.0) ile aynıdır: `exec resume` -s ve -C bayraklarını KABUL ETMEZ (hata verir), istem "-" ile standart girdiden gelir.
// Senaryo, istemdeki [[sahte:ad]] işaretinden seçilir; resume turunda işaret yoktur, senaryo oturum durum dosyasından okunur.
//   oku    : okur, dosya değiştirmez, "Bitti" der (ölçütler sahte işe bağlı)
//   gecer  : hesap.js'i ilk turda KOMUTLA (Set-Content gibi, file_change olayı yok) düzeltir, testler geçer
//   duzelt : ilk turda düzeltmez; Söz geri yollayınca (resume) apply_patch benzeri file_change ile düzeltir
//   bozuk  : hiç düzeltmez (3 kapı turunda kalır)
//   hile   : her turda testlerden birini test.skip yapar (kanıt kapısı "test dosyası değişti" demeli)
//   soru   : "Hangisini seçeyim?" diye sorar; cevap (resume) gelince bitirir
//   uzun   : torun süreç başlatıp uzun süre çalışır (durdurma sınaması)
//   yazdim : komut kum havuzu hatasıyla düşer (gerçek makine hatası), ajan yine de "not.txt dosyasına yazdım" der ve turn.completed gelir; dosya YOK
//   yazdim2: komut sıradan bir hatayla düşer (exit 1), ajan yine de "yazdım" der ve turn.completed gelir; dosya YOK
//   erken  : turn.completed GELMEDEN süreç 0 ile biter ("code-mode host closed its stdout"); erken1: aynısı 1 ile
//   hata   : stderr'e yazıp 1 ile çıkar
//   giris  : turn.failed "Not logged in" ve 1 ile çıkar
//   limit  : turn.failed "usage limit" ve 1 ile çıkar
// SAHTE_CODEX_LOG: argv, cwd, istem, resume, ortam kaydı dosyası. SAHTE_STATE_DIR: oturum durum dosyaları.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawn } from 'node:child_process'

const argv = process.argv.slice(2)
if (argv.includes('--version')) { process.stdout.write('codex-cli 0.0.0-sahte\n', () => process.exit(0)); await new Promise(() => {}) }

const VALUE_FLAGS = new Set(['-c', '--config', '-m', '--model', '-s', '--sandbox', '-C', '--cd', '-i', '--image', '--add-dir', '-p', '--profile', '-o', '--output-last-message', '--color', '--enable', '--disable', '--thread-source', '--output-schema', '--local-provider', '--cyber-access-program'])
const KNOWN_BOOL = new Set(['--json', '--skip-git-repo-check', '--ephemeral', '--ignore-user-config', '--ignore-rules', '--strict-config', '--oss', '--last', '--all', '--approve-for-me', '--worktree', '--full-auto', '--dangerously-bypass-approvals-and-sandbox', '--dangerously-bypass-hook-trust'])
const RESUME_REJECTS = new Set(['-s', '--sandbox', '-C', '--cd', '--add-dir'])

if (argv[0] !== 'exec') { process.stderr.write(`sahte-codex: yalnız "exec" desteklenir (gelen: ${argv[0]})\n`); process.exit(2) }
const isResume = argv[1] === 'resume'
const rest = argv.slice(isResume ? 2 : 1)
const flags = {}
const positional = []
for (let i = 0; i < rest.length; i++) {
  const t = rest[i]
  if (t === '-') { positional.push('-'); continue }
  if (t.startsWith('-')) {
    if (isResume && RESUME_REJECTS.has(t)) { process.stderr.write(`error: unexpected argument '${t}' found\n\nUsage: codex exec resume [OPTIONS] [SESSION_ID] [PROMPT]\n`); process.exit(2) }
    if (VALUE_FLAGS.has(t)) { (flags[t] ??= []).push(rest[++i]); continue }
    if (KNOWN_BOOL.has(t)) { flags[t] = true; continue }
    process.stderr.write(`error: unexpected argument '${t}' found\n`); process.exit(2)
  } else positional.push(t)
}
const sessionArg = isResume ? positional[0] : null
const promptArg = positional[positional.length - 1]
if (isResume && positional.length < 2) { process.stderr.write('error: resume needs SESSION_ID and PROMPT\n'); process.exit(2) }
const prompt = promptArg === '-' ? fs.readFileSync(0, 'utf8') : (promptArg ?? '')
const sessionId = sessionArg ?? crypto.randomUUID()
const stateDir = process.env.SAHTE_STATE_DIR ?? os.tmpdir()
const stateFile = path.join(stateDir, `sahte-codex-${sessionId}.json`)

// `-c` değerleri TOML dizgisi olarak ulaşmalı: windows.sandbox="unelevated" (çift tırnaklı)
const configs = flags['-c'] ?? []
const unelevated = configs.includes('windows.sandbox="unelevated"')
const sandboxBroken = process.env.SAHTE_ELEVATED === '1' && !unelevated

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

if (process.env.SAHTE_CODEX_LOG) {
  fs.appendFileSync(process.env.SAHTE_CODEX_LOG, JSON.stringify({
    argv, cwd: process.cwd(), prompt, resume: sessionArg, isResume, flags, positional, pid: process.pid, scenario, turn: turns, unelevated, sandboxBroken,
    env: {
      SOZ_RUN_ID: process.env.SOZ_RUN_ID ?? null, SOZ_JOB_ID: process.env.SOZ_JOB_ID ?? null,
      OPENAI_API_KEY: process.env.OPENAI_API_KEY ?? null, CODEX_API_KEY: process.env.CODEX_API_KEY ?? null, OPENAI_BASE_URL: process.env.OPENAI_BASE_URL ?? null,
      ANTHROPIC_API_KEY: process.env.ANTHROPIC_API_KEY ?? null, CODEX_HOME: process.env.CODEX_HOME ?? null, SAHTE_SARMAL: process.env.SAHTE_SARMAL ?? null,
    },
  }) + '\n')
}

const cwd = process.cwd()
const emit = (o) => process.stdout.write(JSON.stringify(o) + '\n')
const noise = () => process.stdout.write('2026-11-11T12:32:10.701972Z ERROR codex_core::tools::router: error=sahte gürültü satırı (JSON değil)\n')
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
let itemSeq = 0
const nid = () => `item_${itemSeq++}`
const reasoning = () => emit({ type: 'item.completed', item: { id: nid(), type: 'reasoning', text: 'dusunuyorum' } })
const say = (text) => emit({ type: 'item.completed', item: { id: nid(), type: 'agent_message', text } })
const PS = (cmd) => `"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -Command "${cmd}"`
const HELPER_ERR = 'Failed to create unified exec process: orchestrator_helper_launch_canceled: ShellExecuteExW failed to launch setup helper: 1223'
// Gerçek biçim: önce item.started (exit_code null, in_progress), sonra item.completed. `effect` komutun gerçek etkisidir; kum havuzu bozuksa çalışmaz.
const runCommand = (cmd, output, exit = 0, effect = null, forceBroken = false) => {
  const id = nid()
  emit({ type: 'item.started', item: { id, type: 'command_execution', command: PS(cmd), aggregated_output: '', exit_code: null, status: 'in_progress' } })
  if (sandboxBroken || forceBroken) {
    emit({ type: 'item.completed', item: { id, type: 'command_execution', command: PS(cmd), aggregated_output: HELPER_ERR, exit_code: -1, status: 'failed' } })
    return false
  }
  if (effect) effect()
  emit({ type: 'item.completed', item: { id, type: 'command_execution', command: PS(cmd), aggregated_output: output, exit_code: exit, status: exit === 0 ? 'completed' : 'failed' } })
  return exit === 0
}
const fileChange = (file) => emit({ type: 'item.completed', item: { id: nid(), type: 'file_change', changes: [{ path: file, kind: 'update' }], status: 'completed' } })
const finish = (ok = true, message = '') => new Promise(() => {
  const line = ok
    ? { type: 'turn.completed', usage: { input_tokens: 1200 * turns, cached_input_tokens: 800 * turns, cache_write_input_tokens: 0, output_tokens: 150 * turns, reasoning_output_tokens: 40 * turns } }
    : { type: 'turn.failed', error: { message } }
  process.stdout.write(JSON.stringify(line) + '\n', () => process.exit(ok ? 0 : 1))
})

emit({ type: 'thread.started', thread_id: sessionId }) // resume'da da yeniden basılır (aynı kimlik)
emit({ type: 'turn.started' })
noise()

if (scenario === 'hata') { process.stderr.write('boom: sahte codex bir sey ters gitti\n', () => process.exit(1)); await new Promise(() => {}) }
if (scenario === 'giris') await finish(false, 'Not logged in. Please run `codex login`.')
if (scenario === 'limit') await finish(false, "You've hit your usage limit. Try again later.")

if (scenario === 'erken' || scenario === 'erken1') {
  reasoning()
  say('Başlıyorum.')
  process.stderr.write('code-mode host closed its stdout\n', () => process.exit(scenario === 'erken' ? 0 : 1))
  await new Promise(() => {})
}

if (scenario === 'uzun') {
  const torun = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 120000)'], { stdio: 'ignore' })
  if (process.env.SAHTE_TORUN) fs.writeFileSync(process.env.SAHTE_TORUN, String(torun.pid))
  for (let i = 0; i < 400; i++) { reasoning(); say(`calisiyorum ${i}`); await sleep(300) }
  await finish()
}

if (scenario === 'soru') {
  reasoning()
  if (!isResume) { say('İki yol var. Hangisini seçeyim?'); await finish() }
  say('Tamam, seçtiğin yolla bitirdim. Testler geçti: 2/2')
  await finish()
}

if (scenario === 'yazdim') {
  reasoning()
  runCommand('Set-Content not.txt merhaba', '', 0, null, true) // gerçek makine hatası: komut hiç koşmadı
  say('not.txt dosyasına yazdım.')
  await finish()
}
if (scenario === 'yazdim2') {
  reasoning()
  runCommand('Set-Content not.txt merhaba', 'Set-Content : Access to the path is denied.', 1)
  say('not.txt dosyasına yazdım.')
  await finish()
}

const hesap = path.join(cwd, 'hesap.js')
const fix = () => { if (fs.existsSync(hesap)) fs.writeFileSync(hesap, fs.readFileSync(hesap, 'utf8').replace('a - b', 'a + b')) }
reasoning()
runCommand('Get-Content hesap.js', 'function topla(a, b) { return a - b }')
runCommand('npm test', 'tests 2\npass 1\nfail 1', 1)

let text = 'Bitti. Testler geçti: 2/2 (hesap.js)'
if (scenario === 'gecer') {
  // gerçek Codex dosyayı Set-Content ile yazınca file_change olayı göndermez
  runCommand('(Get-Content hesap.js) -replace \'a - b\', \'a + b\' | Set-Content hesap.js', '', 0, fix)
  runCommand('npm test', 'tests 2\npass 2', 0)
  text = 'Düzelttim. Testler geçti: 2/2'
} else if (scenario === 'duzelt' && isResume) {
  fix(); fileChange(hesap) // apply_patch benzeri araç: file_change olayı gelir, kum havuzundan bağımsız
  runCommand('npm test', 'tests 2\npass 2', 0)
  text = 'Düzelttim. Testler geçti: 2/2'
} else if (scenario === 'hile') {
  const f = path.join(cwd, 'test', 'hesap.test.js')
  if (fs.existsSync(f)) { const t = fs.readFileSync(f, 'utf8'); if (!t.includes('test.skip(')) fs.writeFileSync(f, t.replace('test(', 'test.skip(')) }
  fileChange(f)
  text = 'Bitti. Testler geçti: 0 kalan.'
} else if (scenario === 'bozuk' && isResume) {
  text = 'Düzeltemedim, hâlâ aynı.'
}
say(text)
await finish()
