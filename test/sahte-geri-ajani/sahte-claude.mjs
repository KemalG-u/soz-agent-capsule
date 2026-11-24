// Sahte `claude` (geri alma sınaması): ajan proje klasöründeki dosyaları BOZAR, sonra "bitti" der.
// Gerçek claude'u taklit eden yalnız bu kadar: --version, stdin istemi, stream-json olayları, çalışma klasörü = proje klasörü.
// Bozma listesi (hepsi proje klasöründe, ürün koduna dokunmaz):
//   - hesap.js içeriğini değiştirir, notlar.txt'yi SİLER, yeni.txt ve yeni-klasor\ic.txt YARATIR,
//   - src\ klasörünün adını değiştirir (src -> kaynak), bos-klasor\ boş klasörünü siler, yeni-bos\ boş klasörü yaratır,
//   - büyük.bin dosyasının sonuna bayt ekler.
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'

const argv = process.argv.slice(2)
if (argv.includes('--version')) { process.stdout.write('2.1.322 (Claude Code, sahte)\n', () => process.exit(0)); await new Promise(() => {}) }
const prompt = fs.readFileSync(0, 'utf8')
const sessionId = crypto.randomUUID()
const emit = (o) => process.stdout.write(JSON.stringify({ ...o, session_id: sessionId }) + '\n')
const say = (text) => emit({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text }] } })
let seq = 0
const tool = (name, input) => {
  const id = `toolu_geri_${++seq}`
  emit({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'tool_use', id, name, input }] } })
  emit({ type: 'user', message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: id, content: 'ok' }] } })
}
const cwd = process.cwd()
const p = (...a) => path.join(cwd, ...a)

emit({ type: 'system', subtype: 'init', cwd, tools: ['Read', 'Edit', 'Bash'], model: 'sahte', permissionMode: 'acceptEdits', claude_code_version: '0.0-sahte' })

if (/\[\[sahte:boz\]\]/.test(prompt)) {
  const f = p('hesap.js')
  if (fs.existsSync(f)) { fs.writeFileSync(f, 'function topla(a, b) { return 0 } // AJAN BOZDU\n'); tool('Edit', { file_path: f }) }
  if (fs.existsSync(p('notlar.txt'))) { fs.rmSync(p('notlar.txt')); tool('Bash', { command: 'rm notlar.txt' }) }
  fs.writeFileSync(p('yeni.txt'), 'ajan yazdi\n'); tool('Write', { file_path: p('yeni.txt') })
  fs.mkdirSync(p('yeni-klasor'), { recursive: true }); fs.writeFileSync(p('yeni-klasor', 'ic.txt'), 'ic\n'); tool('Write', { file_path: p('yeni-klasor', 'ic.txt') })
  if (fs.existsSync(p('src'))) { fs.renameSync(p('src'), p('kaynak')); tool('Bash', { command: 'mv src kaynak' }) }
  if (fs.existsSync(p('bos-klasor'))) { fs.rmdirSync(p('bos-klasor')); tool('Bash', { command: 'rmdir bos-klasor' }) }
  fs.mkdirSync(p('yeni-bos'), { recursive: true })
  if (fs.existsSync(p('buyuk.bin'))) { fs.appendFileSync(p('buyuk.bin'), Buffer.alloc(1024, 7)); tool('Edit', { file_path: p('buyuk.bin') }) }
}
say('Bitti. Her şeyi düzelttim.')
process.stdout.write(JSON.stringify({ type: 'result', subtype: 'success', is_error: false, result: 'Bitti.', num_turns: 1, total_cost_usd: 0, session_id: sessionId }) + '\n', () => process.exit(0))
await new Promise(() => {})
