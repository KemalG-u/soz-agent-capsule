// Sahte `claude` (k1): "önce küçük bir deneme yaz" turlarını sınamak için en küçük taklit. Gerçek claude çalıştırılmaz.
// Senaryo istemdeki [[k1:ad]] işaretinden seçilir:
//   testyaz        : YENİ bir test dosyası (test/yeni.test.js) yazar ve package.json'a "test" betiğini ekler, sonunda komutu söyler
//   testyaz-config : testyaz + kökte yeni bir jest.config.js (ayar dosyası) ekler
//   varolani-boz   : VAR OLAN bir test dosyasının (test/hesap.test.js) bir satırını değiştirir (e2: kod doğruysa onaylı testler geçer, alarm değil)
//   varolani-boz-kod : varolani-boz + hesap.js'yi bozar (gerçek hile: onaylı test yeni kodla kalır, karar sende)
//   teste-ekle / test-sil / test-atla / test-tasi : bkz. aşağı (e1)
//   (işaret yok)   : hiçbir dosyaya dokunmaz
// SAHTE_LOG verilmişse istem günlüğe yazılır. `--version` model çağırmadan sürüm söyler.
import fs from 'node:fs'

const argv = process.argv.slice(2)
if (argv.includes('--version')) { process.stdout.write('2.1.999 (Claude Code, sahte k1)\n', () => process.exit(0)); await new Promise(() => {}) }
const prompt = fs.readFileSync(0, 'utf8')
if (process.env.SAHTE_LOG) fs.appendFileSync(process.env.SAHTE_LOG, JSON.stringify({ prompt, cwd: process.cwd() }) + '\n')
const sid = 'k1-' + Date.now()
const emit = (o) => process.stdout.write(JSON.stringify({ ...o, session_id: sid }) + '\n')
emit({ type: 'system', subtype: 'init', cwd: process.cwd(), tools: ['Write'], model: 'sahte', permissionMode: 'acceptEdits', claude_code_version: '0.0-sahte' })

const marker = (/\[\[k1:([\w-]+)\]\]/.exec(prompt) ?? [])[1]
if (marker === 'testyaz' || marker === 'testyaz-config') {
  fs.mkdirSync('test', { recursive: true })
  fs.writeFileSync('test/yeni.test.js', "const test = require('node:test');\nconst assert = require('node:assert/strict');\ntest('yeni deneme', () => { assert.equal(1 + 1, 2); });\n")
  const pj = JSON.parse(fs.readFileSync('package.json', 'utf8'))
  pj.scripts = { ...(pj.scripts ?? {}), test: 'node --test' }
  fs.writeFileSync('package.json', JSON.stringify(pj, null, 2))
  if (marker === 'testyaz-config') fs.writeFileSync('jest.config.js', 'module.exports = {};\n')
}
if (marker === 'varolani-boz') fs.writeFileSync('test/hesap.test.js', fs.readFileSync('test/hesap.test.js', 'utf8').replace('topla(2, 3), 5', 'topla(0, 0), 0'))
// e2: gerçek hile = testi gevşet VE kodu bozuk bırak (ajanın bıraktığı hâl yeşil görünür, onaylı test kalır)
if (marker === 'varolani-boz-kod') {
  fs.writeFileSync('test/hesap.test.js', fs.readFileSync('test/hesap.test.js', 'utf8').replace('topla(2, 3), 5', 'topla(0, 0), 0'))
  fs.writeFileSync('hesap.js', fs.readFileSync('hesap.js', 'utf8').replace('a + b', 'a - b'))
}
// e1 (denetim 5): yalnız ekleme alarm değil; silme ve atlama alarm
if (marker === 'teste-ekle') fs.appendFileSync('test/hesap.test.js', "\ntest('ek', () => { assert.equal(carp(0, 5), 0); });\n")
if (marker === 'test-sil') fs.rmSync('test/hesap.test.js')
if (marker === 'test-atla') fs.writeFileSync('test/hesap.test.js', fs.readFileSync('test/hesap.test.js', 'utf8').replace(/^test\(/gm, 'test.skip('))
if (marker === 'test-tasi') { fs.mkdirSync('test/unit', { recursive: true }); fs.renameSync('test/hesap.test.js', 'test/unit/hesap.test.js'); fs.writeFileSync('test/unit/hesap.test.js', fs.readFileSync('test/unit/hesap.test.js', 'utf8').replace("require('../hesap.js')", "require('../../hesap.js')")) }

const text = 'Deneme yazdım. Komut: npm test'
emit({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text }] } })
process.stdout.write(JSON.stringify({ type: 'result', subtype: 'success', is_error: false, result: text, num_turns: 1, total_cost_usd: 0, session_id: sid }) + '\n', () => process.exit(0))
