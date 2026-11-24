// Sahte `claude`: Söz yürütücüsünü sınamak için `claude -p --output-format stream-json --verbose` çıktısını taklit eder.
// Gerçek olay biçimi claude 2.1.322 ile elde edilen çıktıdan alındı (system/init, assistant, user, result).
// Senaryo, istemdeki [[sahte:ad]] işaretinden seçilir; --resume ile gelen istemde işaret yoktur, senaryo oturum durum dosyasından okunur.
//   oku    : okur, test komutu çalıştırır, 2 dosyayı değiştirir, "Bitti. Testler geçti: 2/2" der (varsayılan)
//   duzelt : kanca açıksa Stop kancasından geri yollanınca hesap.js'i gerçekten düzeltir
//   bozuk  : kanca açıksa geri yollanır ama hiç düzeltmez (3 turda kalır)
//   sor    : PreToolUse kancasına "rm -rf eski-klasor" sorar; reddedilirse durur, onay gelince (resume) devam eder
//   soru   : "Hangisini seçeyim?" diye sorar
//   uzun   : torun süreç başlatıp uzun süre çalışır (durdurma sınaması)
//   hata   : stderr'e yazıp 1 ile çıkar
//   giris  : "Please run /login" ile hata sonucu verir
//   paket  : package.json'daki test betiğini boşaltır (kanıt kapısı "test tanımı değişti" demeli), "Testler geçti" der (K-1)
//   atla   : kalan testlerden birini test.skip yapar, "Testler geçti" der (K-1)
//   sonrabozdu : önce düzeltir (kapı geçer), sonra aynı oturumda kodu yeniden bozar (K-2: nihai hüküm kaldı)
//   suren  : geçtikten sonra da çalışmaya devam eder, bozmaz (K-2: kapı yine koşar, iş geçti kalır)
//   altajan: bir yardımcı (alt) ajan olayı yayar; alt ajanın sözü ve araçları ana işi etkilememeli (K-8)
// HER araçtan önce PreToolUse kancası çağrılır (gerçek claude gibi); reddedilirse araç hiç çalışmaz.
// SAHTE_KANCALI=1 ise --settings dosyasındaki kancalar gerçekten çalıştırılır (bash -c). SAHTE_LOG: argv/ortam kaydı dosyası.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawn, spawnSync } from 'node:child_process'

const BSLASH = String.fromCharCode(92)
const argv = process.argv.slice(2)
// Sürüm sorusu (Söz ajan araçları listesi): model çağrısı yok, senaryo koşmaz, günlüğe yazmaz.
if (argv.includes('--version')) { process.stdout.write('2.1.322 (Claude Code, sahte)\n', () => process.exit(0)); await new Promise(() => {}) }
const opt = (n) => { const i = argv.indexOf(n); return i >= 0 && i + 1 < argv.length ? argv[i + 1] : null }
const settingsPath = opt('--settings')
const resume = opt('--resume')
const prompt = fs.readFileSync(0, 'utf8')
const sessionId = resume ?? crypto.randomUUID()
const stateDir = process.env.SAHTE_STATE_DIR ?? os.tmpdir()
const stateFile = path.join(stateDir, `sahte-${sessionId}.json`)

let scenario = (/\[\[sahte:(\w+)\]\]/.exec(prompt) ?? [])[1]
if (!scenario && resume && fs.existsSync(stateFile)) scenario = JSON.parse(fs.readFileSync(stateFile, 'utf8')).scenario
scenario ??= 'oku'
try { fs.writeFileSync(stateFile, JSON.stringify({ scenario })) } catch { /* yoksay */ }

if (process.env.SAHTE_LOG) {
  fs.appendFileSync(process.env.SAHTE_LOG, JSON.stringify({
    argv, cwd: process.cwd(), prompt, resume, pid: process.pid, scenario,
    env: { SOZ_RUN_ID: process.env.SOZ_RUN_ID ?? null, SOZ_JOB_ID: process.env.SOZ_JOB_ID ?? null, ANTHROPIC_API_KEY: process.env.ANTHROPIC_API_KEY ?? null },
  }) + '\n')
}

const emit = (o) => process.stdout.write(JSON.stringify({ ...o, session_id: sessionId }) + '\n')
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
let toolSeq = 0
let lastDeny = null
const toolUse = (name, input) => {
  lastDeny = null
  const hk = runHook('PreToolUse', { tool_name: name, tool_input: input })?.hookSpecificOutput
  if (hk?.permissionDecision === 'deny') { lastDeny = hk.permissionDecisionReason ?? 'engellendi'; return null }
  const id = `toolu_sahte_${++toolSeq}`
  emit({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'tool_use', id, name, input }] } })
  return id
}
const toolResult = (id, content, isError = false) => id == null ? null : emit({ type: 'user', message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: id, content, ...(isError ? { is_error: true } : {}) }] } })
const say = (text) => emit({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text }] } })
const think = () => emit({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'thinking', thinking: '', signature: 'x' }] } })
const finish = (text, ok = true) => new Promise(() => {
  // Çıktı boşalınca çık; söz hiç çözülmez (sonraki kod çalışmasın)
  process.stdout.write(JSON.stringify({ type: 'result', subtype: ok ? 'success' : 'error_during_execution', is_error: !ok, result: text, num_turns: 1, total_cost_usd: 0, session_id: sessionId }) + '\n', () => process.exit(ok ? 0 : 1))
})

function runHook(name, input) {
  if (!settingsPath || !process.env.SAHTE_KANCALI) return null
  const s = JSON.parse(fs.readFileSync(settingsPath, 'utf8'))
  const cmd = s.hooks?.[name]?.[0]?.hooks?.[0]?.command
  if (!cmd) return null
  const r = spawnSync('bash', ['-c', cmd], {
    input: JSON.stringify({ session_id: sessionId, cwd: process.cwd(), hook_event_name: name, transcript_path: 't.jsonl', ...input }),
    encoding: 'utf8', env: { ...process.env, CLAUDE_PROJECT_DIR: process.cwd() },
  })
  try { return JSON.parse(r.stdout) } catch { return null }
}

emit({ type: 'system', subtype: 'init', cwd: process.cwd(), tools: ['Read', 'Edit', 'Bash'], model: 'sahte', permissionMode: 'acceptEdits', claude_code_version: '0.0-sahte' })
emit({ type: 'system', subtype: 'hook_started', hook_id: 'x', hook_name: 'SessionStart:startup', hook_event: 'SessionStart' })
emit({ type: 'rate_limit_event', rate_limit_info: { status: 'allowed' } })
runHook('SessionStart', { source: resume ? 'resume' : 'startup' })
runHook('UserPromptSubmit', { prompt })

if (scenario === 'hata') { process.stderr.write('boom: sahte claude bir sey ters gitti\n', () => process.exit(1)); await new Promise(() => {}) }
if (scenario === 'giris') await finish('Invalid API key · Please run /login', false)

if (scenario === 'uzun') {
  const torun = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 120000)'], { stdio: 'ignore' })
  if (process.env.SAHTE_TORUN) fs.writeFileSync(process.env.SAHTE_TORUN, String(torun.pid))
  for (let i = 0; i < 400; i++) { think(); say(`calisiyorum ${i}`); await sleep(300) }
  await finish('uzun is bitti')
}

if (scenario === 'soru') {
  think()
  say('İki yol var. Hangisini seçeyim?')
  runHook('Stop', { stop_hook_active: false, last_assistant_message: 'İki yol var. Hangisini seçeyim?' }) // kapı koşmamalı (Ö-3)
  await finish('İki yol var. Hangisini seçeyim?')
}

if (scenario === 'sor') {
  const cmd = 'rm -rf eski-klasor'
  think()
  const id = toolUse('Bash', { command: cmd }) // PreToolUse kancası toolUse içinde çağrılır
  if (lastDeny !== null) {
    const t = lastDeny.includes('istemedi') ? 'Reddedildi, başka yol yok. Durdum.' : 'Engellendi: onay bekliyorum.'
    say(t)
    await finish(t)
  }
  toolResult(id, '')
  const t = 'Klasör silindi. Tamamlandı.'
  say(t)
  await finish(t)
}

// oku / duzelt / bozuk
think()
const r1 = toolUse('Read', { file_path: path.join(process.cwd(), 'hesap.js') })
toolResult(r1, '1\tfunction topla(a, b) { return a - b }')
const e1 = toolUse('Edit', { file_path: path.join(process.cwd(), 'hesap.js'), old_string: 'a - b', new_string: 'a + b' })
toolResult(e1, 'ok')
const e2 = toolUse('Write', { file_path: path.join(process.cwd(), 'notlar.txt'), content: 'x' })
toolResult(e2, 'ok')
const b1 = toolUse('Bash', { command: 'npm test' })
toolResult(b1, 'tests 2\npass 2', false)
if (scenario === 'altajan') {
  // Alt ajan (parent_tool_use_id taşıyan) olayları: ana işin durumunu, sözünü ve ilerleme satırlarını etkilememeli.
  const sub = (message) => emit({ type: 'assistant', parent_tool_use_id: 'toolu_sahte_ust', message: { role: 'assistant', content: [message] } })
  const t1 = toolUse('Task', { description: 'yardımcı iş', prompt: 'bak' })
  toolResult(t1, 'tamam')
  const t2 = toolUse('Task', { description: 'ikinci yardımcı iş', prompt: 'bak' })
  sub({ type: 'text', text: 'Alt ajan: hangisini seçeyim?' })
  sub({ type: 'tool_use', id: 'toolu_alt_1', name: 'Bash', input: { command: 'rm -rf ' + BSLASH + 'alt' } })
  emit({ type: 'user', parent_tool_use_id: 'toolu_sahte_ust', message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: 'toolu_alt_1', content: 'hata', is_error: true }] } })
  toolResult(t2, 'tamam')
}
const cwd = process.cwd()
const writeFix = () => { const f = path.join(cwd, 'hesap.js'); if (fs.existsSync(f)) fs.writeFileSync(f, fs.readFileSync(f, 'utf8').replace('a - b', 'a + b')) }
const writeBreak = () => { const f = path.join(cwd, 'hesap.js'); if (fs.existsSync(f)) fs.writeFileSync(f, fs.readFileSync(f, 'utf8').replace('a + b', 'a - b')) }
if (scenario === 'paket') {
  const f = path.join(cwd, 'package.json')
  const p = JSON.parse(fs.readFileSync(f, 'utf8'))
  p.scripts = { ...(p.scripts ?? {}), test: 'node -e 0' }
  fs.writeFileSync(f, JSON.stringify(p, null, 2))
  toolResult(toolUse('Edit', { file_path: f, old_string: 'x', new_string: 'y' }), 'ok')
}
if (scenario === 'atla') {
  const f = path.join(cwd, 'test', 'hesap.test.js')
  if (fs.existsSync(f)) fs.writeFileSync(f, fs.readFileSync(f, 'utf8').replace('test(', 'test.skip('))
  toolResult(toolUse('Edit', { file_path: f, old_string: 'x', new_string: 'y' }), 'ok')
}
if (scenario === 'sonrabozdu' || scenario === 'suren') { writeFix(); toolResult(toolUse('Edit', { file_path: path.join(cwd, 'hesap.js'), old_string: 'a - b', new_string: 'a + b' }), 'ok') }
let text = 'Bitti. Testler geçti: 2/2 (hesap.js)'
say(text)

let active = false
let after = false
for (let n = 0; n < 6; n++) {
  const out = runHook('Stop', { stop_hook_active: active, last_assistant_message: text })
  if (out?.decision === 'block') {
    say('Kapı geri yolladı, bakıyorum.')
    if (scenario === 'duzelt') {
      const f = path.join(process.cwd(), 'hesap.js')
      fs.writeFileSync(f, fs.readFileSync(f, 'utf8').replace('a - b', 'a + b'))
      const e = toolUse('Edit', { file_path: f, old_string: 'a - b', new_string: 'a + b' })
      toolResult(e, 'ok')
    }
    text = scenario === 'duzelt' ? 'Düzelttim. Testler geçti: 2/2' : 'Düzeltemedim, hâlâ aynı.'
    say(text)
    active = true
    continue
  }
  // K-2: kapı geçti ama ajan çalışmaya devam ediyor (bir kez); sonrabozdu kodu yeniden bozar, suren bozmaz.
  if ((scenario === 'sonrabozdu' || scenario === 'suren') && !after) {
    after = true
    if (scenario === 'sonrabozdu') writeBreak()
    toolResult(toolUse('Edit', { file_path: path.join(cwd, 'hesap.js'), old_string: 'a + b', new_string: scenario === 'sonrabozdu' ? 'a - b' : 'a + b' }), 'ok')
    text = 'Bir şey daha yaptım. Bitti.'
    say(text)
    active = false
    continue
  }
  break
}
await finish(text)
