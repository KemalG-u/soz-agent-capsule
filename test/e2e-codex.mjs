// Uçtan uca (node eşdeğeri): yayın exe'si + SAHTE codex + SAHTE claude. Gerçek codex ve gerçek claude ASLA çağrılmaz.
// Kapsam: ajan listesi, ayarlarda iki aracın sahteye çevrilip geri okunması, Codex işi (geçen, kalan + geri yollama, 3 tur sonra kaldı, hile -> karar bekliyor,
// soru + cevap, durdurma), Codex'in kendi klasörüne yazılmaması, API ve komut satırında agent alanı.
// Kullanım: node test\e2e-codex.mjs [soz.exe yolu]. Söz yazma uçlarını yalnız insandan alır; betik kendini insan eşdeğeri süreçte yeniden başlatır (ui\scripts\insan.mjs).
import { spawn, execFileSync } from 'node:child_process'
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { relaunchAsHuman, writeExitCode } from '../ui/scripts/insan.mjs'

if (await relaunchAsHuman(import.meta.url)) process.exit(process.exitCode ?? 0)

const URUN = 'E:/Sample Work/urun'
const EXE = process.argv[2] ?? `${URUN}/yayin/soz.exe`
const ROOT = `${URUN}/_gecici/bag-e2e`
const DATA = `${URUN}/_gecici/bag-e2e-veri`
const PROJ = `${ROOT}/proje klasoru`
const STATE = `${ROOT}/durum`
const CODEX_HOME = `${ROOT}/codex-home`
const PORT = 49610
const FAKE_CODEX = `${URUN}/test/sahte-codex/sahte-codex.mjs`.replace(/\//g, '\\')
const FAKE_CLAUDE = `${URUN}/test/sahte-claude/sahte-claude.mjs`.replace(/\//g, '\\')
const BASE = `http://127.0.0.1:${PORT}`

const rows = []
const ok = (name, cond, extra = '') => { rows.push([cond ? 'GEÇTİ' : 'KALDI', name, extra]); console.log(`${cond ? 'GEÇTİ' : 'KALDI'}  ${name}${extra ? '  [' + extra + ']' : ''}`) }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
let server = null
let token = ''

rmSync(ROOT, { recursive: true, force: true })
rmSync(DATA, { recursive: true, force: true })
mkdirSync(`${PROJ}/test`, { recursive: true })
mkdirSync(DATA, { recursive: true })
mkdirSync(STATE, { recursive: true })
mkdirSync(CODEX_HOME, { recursive: true })
const resetProject = () => {
  for (const f of ['package.json', 'hesap.js']) cpSync(`${URUN}/deneme-proje/${f}`, `${PROJ}/${f}`)
  cpSync(`${URUN}/deneme-proje/test/hesap.test.js`, `${PROJ}/test/hesap.test.js`)
}
resetProject()

async function api(method, path, body) {
  const r = await fetch(BASE + path, { method, headers: { 'X-Soz-Token': token, 'content-type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) })
  const text = await r.text()
  let json = null
  try { json = text ? JSON.parse(text) : null } catch { /* düz metin */ }
  return { status: r.status, json }
}
async function waitState(id, states, sec = 120) {
  const end = Date.now() + sec * 1000
  let j = null
  while (Date.now() < end) {
    j = (await api('GET', `/api/jobs/${id}`)).json
    if (j && states.includes(j.state) && j.run?.status !== 'running') return j
    await sleep(400)
  }
  return j
}
const codexLog = () => (existsSync(`${ROOT}/codex.log`) ? readFileSync(`${ROOT}/codex.log`, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)) : [])
const newJob = async (text, agent = 'codex') => (await api('POST', '/api/jobs', { projectId: pid, rawText: text, agent })).json
let pid = 0

try {
  server = spawn(EXE, ['serve', '--port', String(PORT), '--data', DATA, '--exit-after', '1500', '--exit-with-parent', String(process.pid)], {
    env: {
      ...process.env, SAHTE_KANCALI: '1', SAHTE_STATE_DIR: STATE, SAHTE_TORUN: `${ROOT}/torun.pid`, SAHTE_LOG: `${ROOT}/claude.log`, SAHTE_CODEX_LOG: `${ROOT}/codex.log`, SAHTE_ELEVATED: '1',
      CODEX_HOME, OPENAI_API_KEY: 'sk-test-gizli-openai-0000', CODEX_API_KEY: 'sk-test-gizli-codex-0000',
    },
    stdio: 'ignore', windowsHide: true,
  })
  for (let i = 0; i < 60 && !token; i++) {
    try { token = (await (await fetch(`${BASE}/api/session`)).json()).token } catch { await sleep(500) }
  }
  ok('sunucu açıldı', !!token, `PID ${server.pid}`)
  if (!token) throw new Error('sunucu açılmadı')

  // GERÇEK araç asla çağrılmasın: iki yol da sahteye çevrilir ve GERİ OKUNUR
  const put = await api('PUT', '/api/settings', { claudePath: FAKE_CLAUDE, codexPath: FAKE_CODEX, runnerModel: '' })
  ok('ayar yazıldı', put.status === 200, String(put.status))
  const back = (await api('GET', '/api/settings')).json
  const sahte = String(back.claudePath ?? '').toLowerCase().endsWith('sahte-claude.mjs') && String(back.codexPath ?? '').toLowerCase().endsWith('sahte-codex.mjs')
  ok('ayar doğrulandı: claudePath ve codexPath sahte', sahte, `${back.claudePath} | ${back.codexPath}`)
  if (!sahte) throw new Error('yollar sahte değil; gerçek araç çağrılabilirdi, durduruldu')
  ok('varsayılan ajan claude', back.defaultAgent === 'claude')
  ok('geçersiz varsayılan ajan 400', (await api('PUT', '/api/settings', { defaultAgent: 'gemini' })).status === 400)

  // ajan listesi
  const ag = (await api('GET', '/api/agents')).json
  const codex = ag.agents.find((a) => a.id === 'codex')
  ok('GET /api/agents: iki araç, ikisi hazır', ag.agents.length === 2 && ag.agents.every((a) => a.ready), ag.agents.map((a) => `${a.id}:${a.version}`).join(' '))
  ok('codex yetenekleri dürüst: işletim sistemi sınırı + dış döngü + token', codex.capabilities.guardTier === 'os_sandbox' && codex.capabilities.turnEnd === 'outer_loop' && codex.capabilities.cost === 'tokens' && codex.guardLabel === 'sınırlı')
  const cli = execFileSync(EXE, ['agents', '--json', '--data', DATA], { encoding: 'utf8', windowsHide: true, env: { ...process.env, CODEX_HOME } })
  ok('komut satırı: soz agents --json', /"codex"/.test(cli) && /sahte/.test(cli))

  // proje + ölçüt
  const add = (await api('POST', '/api/projects', { path: PROJ.replace(/\//g, '\\') })).json
  pid = add.id
  const ap = (await api('POST', `/api/projects/${pid}/approve-setup`, {})).json
  ok('proje eklendi, ölçüt açık', !!pid && ap.checks?.[0]?.enabled === true)

  // 1. geçen iş
  let j = await newJob('Toplamayı düzelt [[sahte:gecer]]')
  ok('iş codex ile oluştu (agent alanı)', j.agent === 'codex')
  const st = (await api('POST', `/api/jobs/${j.id}/start`, { agent: 'codex' })).json
  ok('başlat cevabında koşu ajanı codex', st.run?.agent === 'codex')
  j = await waitState(j.id, ['gecti', 'kaldi', 'karar_bekliyor', 'baslatilamadi'])
  ok('1 geçen iş: gecti', j.state === 'gecti', j.state)
  ok('1 hesap.js düzeldi', readFileSync(`${PROJ}/hesap.js`, 'utf8').includes('a + b'))
  const proofs = (await api('GET', `/api/proof/runs?jobId=${j.id}`)).json
  ok('1 kanıt dış kapıdan', proofs.some((p) => p.trigger === 'outer_gate' && p.verdict === 'passed'), proofs.map((p) => `${p.trigger}/${p.verdict}`).join(','))
  ok('1 harcama token olarak işe yazıldı', (j.spend?.inputTokens ?? 0) > 0 && j.spend.costUsd == null, JSON.stringify(j.spend))
  const log1 = codexLog()
  ok('1 faturalı anahtarlar codex sürecine geçmedi', log1.length === 1 && log1[0].env.OPENAI_API_KEY === null && log1[0].env.CODEX_API_KEY === null)
  ok('1 unelevated bayrağı çift tırnaklı TOML dizgisi olarak ulaştı (SAHTE_ELEVATED=1 iken komutlar yalnız bu sayede koştu)', log1[0].unelevated === true && log1[0].sandboxBroken === false && log1[0].argv.includes('windows.sandbox="unelevated"'))
  ok('1 komut satırı: exec --json, sınır, istem stdin', log1[0].argv.includes('--json') && log1[0].argv.includes('workspace-write') && log1[0].argv.at(-1) === '-')
  ok('1 Codex ayar klasörüne hiçbir şey yazılmadı', readdirSync(CODEX_HOME).length === 0)
  ok('1 projede .codex ya da .claude kancası yok', !existsSync(`${PROJ}/.codex`) && !existsSync(`${PROJ}/.claude/settings.json`))
  const tl = (await api('GET', `/api/jobs/${j.id}/timeline`)).json
  const texts = (tl.items ?? tl).map((i) => i.text).join(' | ')
  ok('1 zaman çizelgesinde araç adı yok', !/powershell|bash -lc|command_execution/i.test(texts), texts.slice(0, 160))

  // 2. kalan iş + geri yollama (resume)
  resetProject()
  let j2 = await newJob('Toplamayı düzelt [[sahte:duzelt]]')
  await api('POST', `/api/jobs/${j2.id}/start`, {})
  j2 = await waitState(j2.id, ['gecti', 'kaldi', 'karar_bekliyor', 'baslatilamadi'])
  ok('2 kalan iş geri yollandı ve geçti', j2.state === 'gecti', j2.state)
  const l2 = codexLog().filter((l) => l.scenario === 'duzelt')
  ok('2 ikinci tur `exec resume` ile aynı oturumda', l2.length === 2 && l2[1].isResume === true && l2[1].argv.includes('resume') && !l2[1].argv.includes('-s') && !l2[1].argv.includes('-C'))
  ok('2 geri yollama metni Söz kanıt kapısı', /Söz kanıt kapısı/.test(l2[1]?.prompt ?? ''))

  // 3. hiç düzelmeyen: 3 kapı turu sonra kaldı
  resetProject()
  let j3 = await newJob('Toplamayı düzelt [[sahte:bozuk]]')
  await api('POST', `/api/jobs/${j3.id}/start`, {})
  j3 = await waitState(j3.id, ['gecti', 'kaldi', 'karar_bekliyor', 'baslatilamadi'])
  ok('3 bozuk iş 3 turdan sonra kaldı', j3.state === 'kaldi' && codexLog().filter((l) => l.scenario === 'bozuk').length === 3, `${j3.state} tur=${codexLog().filter((l) => l.scenario === 'bozuk').length}`)

  // 4. hile: test dosyasını değiştirme
  resetProject()
  writeFileSync(`${PROJ}/hesap.js`, readFileSync(`${URUN}/deneme-proje/hesap.js`, 'utf8').replace('a - b', 'a + b')) // kod doğru: yalnız test dosyası değişirse yeşil görünürdü
  let j4 = await newJob('Toplamayı düzelt [[sahte:hile]]')
  await api('POST', `/api/jobs/${j4.id}/start`, {})
  j4 = await waitState(j4.id, ['gecti', 'kaldi', 'karar_bekliyor', 'baslatilamadi'])
  ok('4 hile: iş "geçti" olmadı, karar bekliyor', j4.state === 'karar_bekliyor', j4.state)

  // 5. soru + cevap
  resetProject()
  let j5 = await newJob('Bir şey yap [[sahte:soru]]')
  await api('POST', `/api/jobs/${j5.id}/start`, {})
  j5 = await waitState(j5.id, ['cevap_bekliyor', 'gecti', 'kaldi'])
  ok('5 ajan sordu: cevap bekliyor', j5.state === 'cevap_bekliyor', j5.state)
  const rep = await api('POST', `/api/jobs/${j5.id}/reply`, { text: 'Birinciyi seç.' })
  ok('5 cevap kabul edildi', rep.status === 200 || rep.status === 202, String(rep.status))
  await sleep(1500)
  const l5 = codexLog().filter((l) => l.scenario === 'soru')
  ok('5 cevap `exec resume` ile aynı oturuma gitti', l5.length >= 2 && l5[1].isResume === true && !!l5[1].resume && l5.slice(1).every((l) => l.resume === l5[1].resume) && l5[1].prompt.trim() === 'Birinciyi seç.', `${l5.length} çağrı (ilki başlatma, sonrakiler aynı oturumda resume)`)
  await waitState(j5.id, ['gecti', 'kaldi', 'karar_bekliyor'], 90)

  // 6. durdurma: torun süreç de kapanmalı
  let j6 = await newJob('Uzun iş [[sahte:uzun]]')
  await api('POST', `/api/jobs/${j6.id}/start`, {})
  let torun = 0
  for (let i = 0; i < 60 && !torun; i++) { try { torun = Number(readFileSync(`${ROOT}/torun.pid`, 'utf8')) } catch { await sleep(300) } }
  await sleep(600)
  const stop = await api('POST', `/api/jobs/${j6.id}/stop`, {})
  ok('6 durdur çağrısı', stop.status === 200 || stop.status === 202, String(stop.status))
  j6 = await waitState(j6.id, ['durduruldu'], 30)
  ok('6 iş durduruldu', j6.state === 'durduruldu', j6.state)
  let alive = true
  for (let i = 0; i < 40 && alive; i++) { try { process.kill(torun, 0); await sleep(250) } catch { alive = false } }
  ok('6 alt süreç (torun) da kapandı', !alive, `pid ${torun}`)

  // 6b. "yazdım dedi ama yazmadı": dosya ölçütü ekle, hiçbir senaryo yeşil vermemeli
  await api('POST', '/api/checks', { projectId: pid, kind: 'file', label: 'not.txt oluşsun', spec: { path: 'not.txt', exists: true }, timeoutS: 60, enabled: true })
  resetProject()
  let j8 = await newJob('not.txt yaz [[sahte:yazdim]]')
  await api('POST', `/api/jobs/${j8.id}/start`, {})
  j8 = await waitState(j8.id, ['gecti', 'kaldi', 'karar_bekliyor', 'baslatilamadi'])
  ok('8 kum havuzu hatası: iş yeşil DEĞİL, sade nedenle başlatılamadı', j8.state === 'baslatilamadi' && /Windows kum havuzu kurulumu eksik/.test(j8.startError ?? ''), `${j8.state} | ${j8.startError}`)
  ok('8 not.txt gerçekten yok', !existsSync(`${PROJ}/not.txt`))
  let j9 = await newJob('not.txt yaz [[sahte:yazdim2]]')
  await api('POST', `/api/jobs/${j9.id}/start`, {})
  j9 = await waitState(j9.id, ['gecti', 'kaldi', 'karar_bekliyor', 'baslatilamadi'])
  ok('9 "yazdım" dedi ama dosya yok: kapı 3 turda kaldı, asla gecti değil', j9.state === 'kaldi' && !existsSync(`${PROJ}/not.txt`) && codexLog().filter((l) => l.scenario === 'yazdim2').length === 3, `${j9.state}`)
  let j10 = await newJob('bir şey yap [[sahte:erken]]')
  await api('POST', `/api/jobs/${j10.id}/start`, {})
  j10 = await waitState(j10.id, ['gecti', 'kaldi', 'karar_bekliyor', 'baslatilamadi'], 60)
  ok('10 turn.completed gelmeden çıkış: asılı kalmadı, baslatilamadi ve neden yazılı', j10.state === 'baslatilamadi' && /code-mode host closed its stdout/.test(j10.startError ?? ''), `${j10.state} | ${j10.startError}`)
  const tl10 = (await api('GET', `/api/jobs/${j10.id}/timeline`)).json
  ok('10 JSON olmayan günlük satırı akışa sızmadı', !/sahte gürültü|ERROR codex_core/.test(JSON.stringify(tl10)))
  // yazdim senaryosunun ölçütü sonraki sınamaları bozmasın
  const chk = (await api('GET', `/api/projects/${pid}/checks`)).json.find((c) => c.label === 'not.txt oluşsun')
  if (chk) await api('PATCH', `/api/checks/${chk.id}`, { enabled: false })

  // 7. geriye uyum: agent verilmeyen iş varsayılan ajanla (claude) gider
  const j7 = (await api('POST', '/api/jobs', { projectId: pid, rawText: 'Eski biçim' })).json
  ok('7 agent verilmezse claude', j7.agent === 'claude')
  ok('7 bilinmeyen ajan 400', (await api('POST', '/api/jobs', { projectId: pid, rawText: 'x', agent: 'gemini' })).status === 400)
  const list = (await api('GET', '/api/jobs')).json
  ok('7 iş listesinde agent alanı', list.every((x) => x.agent === 'codex' || x.agent === 'claude'))
  ok('7 Codex ayar klasörü hâlâ boş', readdirSync(CODEX_HOME).length === 0)
} catch (e) {
  ok('betik hatasız bitti', false, String(e?.message ?? e))
} finally {
  try { server?.kill() } catch { /* kapanmış olabilir */ }
}

const bad = rows.filter((r) => r[0] === 'KALDI').length
console.log(`\nSONUÇ: ${rows.length - bad}/${rows.length} geçti, ${bad} kaldı`)
writeExitCode(bad ? 1 : 0)
process.exitCode = bad ? 1 : 0
