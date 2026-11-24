// b7: ham yardımcı metnini sade Türkçeye çeviren katmanın (src/lib/waiting.ts) sınaması. Tarayıcısız, Node ile.
// Tek başına: node ui\scripts\waiting-test.mjs      (npm test içinde de koşar)
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import ts from 'typescript'

const HERE = dirname(fileURLToPath(import.meta.url))

export async function loadWaiting() {
  const src = readFileSync(join(HERE, '..', 'src', 'lib', 'waiting.ts'), 'utf8')
  const js = ts.transpileModule(src, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 } }).outputText
  return import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'))
}

export async function runWaitingTests(ok) {
  const { describeWaiting, looksDestructive } = await loadWaiting()
  const d = (raw, helper = 'Claude Code') => describeWaiting(raw, helper)
  const W = 'çeviri'

  let r = d('approve Edit: hesap.js')
  ok(`${W}: dosya değiştirme izni sade cümle`, r.recognized && r.kind === 'edit' && r.sentence === 'Claude Code hesap.js dosyasını değiştirmek için izin istiyor.' && !r.danger && r.quote === null, JSON.stringify(r))
  r = d('approve Edit: E:\\proje\\src\\hesap.js')
  ok(`${W}: uzun yolda dosya adı cümlede, tam yol küçük satırda`, /hesap\.js dosyasını değiştirmek/.test(r.sentence) && r.target === 'E:\\proje\\src\\hesap.js', JSON.stringify(r))
  r = d('approve Bash: npm test')
  ok(`${W}: komut izni "bir komut çalıştırmak", komut ikincil satırda, tehlikesiz`, r.kind === 'run' && r.sentence === 'Claude Code bir komut çalıştırmak için izin istiyor.' && r.target === 'npm test' && !r.danger, JSON.stringify(r))
  r = d('approve Bash: rm -rf node_modules')
  ok(`${W}: rm -rf tehlikeli`, r.kind === 'run' && r.danger && r.target === 'rm -rf node_modules', JSON.stringify(r))
  r = d('approve Bash: git push --force origin main')
  ok(`${W}: git push --force tehlikeli`, r.danger, JSON.stringify(r))
  r = d('approve Bash: git reset --hard HEAD~3')
  ok(`${W}: git reset --hard tehlikeli`, r.danger, JSON.stringify(r))
  r = d('approve Bash: psql -c "DROP TABLE users"')
  ok(`${W}: drop table tehlikeli`, r.danger, JSON.stringify(r))
  r = d('approve Bash: ls -la')
  ok(`${W}: ls -la tehlikesiz (-la, -f değil)`, !r.danger, JSON.stringify(r))
  r = d('approve Bash: git status --format=short')
  ok(`${W}: "--format=short" gibi seçenek tehlikeli sayılmıyor`, !r.danger, JSON.stringify(r))
  r = d('approve Read: notlar.txt')
  ok(`${W}: okuma izni`, r.kind === 'read' && /notlar\.txt dosyasını okumak için izin istiyor/.test(r.sentence), JSON.stringify(r))
  r = d('approve Write: yeni.md')
  ok(`${W}: yazma izni`, r.kind === 'write' && /yeni\.md dosyasını yazmak/.test(r.sentence), JSON.stringify(r))
  r = d('approve WebFetch: https://example.com/api')
  ok(`${W}: ağ erişimi izni, adres ikincil satırda`, r.kind === 'net' && /internete bağlanmak/.test(r.sentence) && r.target === 'https://example.com/api', JSON.stringify(r))
  r = d('approve Delete: eski-kayit.db')
  ok(`${W}: silme izni tehlikeli ve dosya adı cümlede`, r.kind === 'delete' && r.danger && /eski-kayit\.db silmek için izin/.test(r.sentence), JSON.stringify(r))
  r = d('approve Grep: TODO')
  ok(`${W}: arama izni`, r.kind === 'search' && /arama yapmak/.test(r.sentence), JSON.stringify(r))
  r = d('approve mcp__github__create_issue: başlık')
  ok(`${W}: ek araç izni`, r.kind === 'tool' && /ek bir araç/.test(r.sentence), JSON.stringify(r))
  r = d('İzin istiyor: faturalar klasöründeki dosyaları silmek')
  ok(`${W}: Türkçe sunucu cümlesi ("silmek") tehlikeli ve cümleye dönüyor`, r.danger && /faturalar klasöründeki dosyaları silmek için izin istiyor/.test(r.sentence), JSON.stringify(r))
  r = d('Senden bir cevap bekliyor', 'Codex')
  ok(`${W}: "Senden bir cevap bekliyor" sade cümle`, r.kind === 'plain' && r.sentence === 'Codex senden bir cevap bekliyor.' && r.quote === null, JSON.stringify(r))
  r = d('Hangisini tercih edersin, A mı B mi?')
  ok(`${W}: tanınmayan metin aynen "yardımcının kendi sözü"`, !r.recognized && r.sentence === null && r.quote === 'Hangisini tercih edersin, A mı B mi?' && !r.danger, JSON.stringify(r))
  r = d('Which database should I use?')
  ok(`${W}: İngilizce serbest soru aynen kalıyor`, !r.recognized && r.quote === 'Which database should I use?', JSON.stringify(r))
  r = d('Do you allow me to proceed?')
  ok(`${W}: "allow" geçen serbest cümle izin kalıbı sanılmıyor`, !r.recognized && r.quote === 'Do you allow me to proceed?', JSON.stringify(r))
  r = d('approve Frobnicate: abc')
  ok(`${W}: bilinmeyen araç adı: sade "bir işlem için izin", ham metin yardımcının sözü`, !r.recognized && r.kind === 'permission' && /bir işlem için izin istiyor/.test(r.sentence) && r.quote === 'approve Frobnicate: abc', JSON.stringify(r))
  r = d('I will now delete all the files in the folder, ok?')
  ok(`${W}: tanınmayan metinde silme geçerse yine dikkat işareti`, !r.recognized && r.danger, JSON.stringify(r))
  r = d('Claude needs your permission to use Bash')
  ok(`${W}: "needs your permission to use Bash" kalıbı`, r.kind === 'run' && /komut çalıştırmak/.test(r.sentence), JSON.stringify(r))
  r = d('')
  ok(`${W}: boş metin çökmüyor`, !r.recognized && r.sentence === null && r.quote === null)
  r = d(null, null)
  ok(`${W}: null çökmüyor`, r.sentence === null)
  r = d('approve Edit: dosya\u0000adı\n.js', null)
  ok(`${W}: denetim karakterleri temizleniyor, yardımcı adı yoksa "Yardımcı"`, /^Yardımcı /.test(r.sentence) && !/[\u0000\n]/.test(r.sentence), JSON.stringify(r))
  ok(`${W}: sil ile başlayan sıradan kelimeler yanlış alarm vermiyor ("silah" değil)`, !looksDestructive('silahlı tasarım') && looksDestructive('dosyaları sil'))
  r = describeWaiting('approve Edit: hesap.js', 'Claude Code', 'en')
  ok(`${W}: İngilizce dilde de cümle`, /wants permission to change the file hesap\.js/.test(r.sentence), r.sentence)
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const rows = []
  await runWaitingTests((name, cond, extra = '') => { rows.push(cond); console.log(`${cond ? 'GEÇTİ' : 'KALDI'} | ${name}${cond ? '' : ' | ' + String(extra).slice(0, 300)}`) })
  const pass = rows.filter(Boolean).length
  console.log(`\nwaiting-test: ${pass} geçti, ${rows.length - pass} kaldı`)
  process.exitCode = pass === rows.length ? 0 : 1
}
