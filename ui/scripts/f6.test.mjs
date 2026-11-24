// f6: "testler geçti" ekranda "te tler geçti" oluyordu (düzenli ifadede ters bölü kaybolmuştu: /s+/g). Çalıştır: node --test scripts/f6.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { kirp, tekBosluk } from '../src/lib/kirp.ts'
import { sade } from '../src/lib/sade.ts'

const said = (m) => kirp(tekBosluk(m), 180)

test('yardımcının sözü harf kaybetmeden gelir', () => {
  assert.equal(said('testler geçti'), 'testler geçti')
  assert.equal(said('Testler geçti.'), 'Testler geçti.')
  assert.equal(said('Sistem hazır, dosyalar değişti'), 'Sistem hazır, dosyalar değişti')
  assert.equal(said('Tüm sınamalar sorunsuz sürdü'), 'Tüm sınamalar sorunsuz sürdü')
  assert.equal(said('ss'), 'ss')
  assert.equal(said('yes sir'), 'yes sir')
})

test('boşluklar tek boşluğa iner, satır sonu da', () => {
  assert.equal(tekBosluk('  bir   iki\n\nüç\tdört  '), 'bir iki üç dört')
  assert.equal(tekBosluk(null), '')
  assert.equal(tekBosluk(undefined), '')
})

test('uzun söz sözcük sınırından kesilir, sözcük yarım kalmaz, s harfi yenmez', () => {
  const uzun = 'sonuçlar sistem ' + 'sessiz '.repeat(40)
  const c = said(uzun)
  assert.ok(c.endsWith('…'))
  assert.ok(c.length <= 181)
  const govde = c.slice(0, -1)
  assert.ok(uzun.startsWith(govde))
  assert.match(uzun[govde.length], /\s/)
  assert.ok(c.startsWith('sonuçlar sistem sessiz'))
})

test('kesim tam sözcük sonuna denk gelirse sözcük atılmaz; boşluksuz uzun dize olduğu gibi kesilir', () => {
  const a = 'abcd '.repeat(10).trim() // 49 karakter
  assert.equal(kirp(a, 49), a)
  assert.equal(kirp(a, 44), 'abcd abcd abcd abcd abcd abcd abcd abcd abcd…')
  assert.equal(kirp('x'.repeat(30), 10), 'x'.repeat(10) + '…')
  assert.equal(kirp('kısa', 10), 'kısa')
})

test('sade() harf yemez: uzunluk yalnız kural değişimiyle değişir', () => {
  for (const s of ['testler geçti', 'sistem sessiz sürdü', 'dosyalar sonuç', 'seçim sırası']) assert.equal(sade(s), s)
})

// Kaynak koruması: ters bölüsü kaybolmuş düzenli ifade kalıpları ve ham denetim karakterleri bir daha girmesin.
const kok = new URL('../src/', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')
const dosyalar = []
const gez = (d) => { for (const f of readdirSync(d)) { const p = d + '/' + f; if (statSync(p).isDirectory()) gez(p); else if (/\.(ts|svelte)$/.test(f)) dosyalar.push(p) } }
gez(decodeURIComponent(kok))
test('arayüz kaynağında ters bölüsüz /s+/ /S*/ /d+/ biçimli düzenli ifade yok', () => {
  const kotu = /(?:replace|replaceAll|split|match|test|search)\(\/[^/\n]*(?<![\\\w])(?:s\+|S\*|S\+|d\+|d\{\d)/
  const bulgu = []
  for (const p of dosyalar) readFileSync(p, 'utf8').split('\n').forEach((l, i) => { if (kotu.test(l)) bulgu.push(`${p}:${i + 1}`) })
  assert.deepEqual(bulgu, [])
})
test('arayüz kaynağında ham denetim karakteri yok (\\b gibi kaçışın bozulma izi)', () => {
  const bulgu = []
  for (const p of dosyalar) { const m = readFileSync(p, 'utf8'); if (/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(m)) bulgu.push(p) }
  assert.deepEqual(bulgu, [])
})

// Kusur 2: "kaldı" başarısız anlamında ekranda "geçmedi / geçmiyor" olur; başka anlamdaki "kal" sözlerine dokunulmaz.
test('eski kayıttaki "kaldı / kalıyor" cümleleri geçmedi / geçmiyor olur', () => {
  assert.equal(sade('1 kontrolden 1 tanesi kaldı: Testler geçsin'), '1 kontrolden 1 tanesi geçmedi: Testler geçsin')
  assert.equal(sade("Başlamadan önce 1 testten 1'i kalıyor."), "Başlamadan önce 1 testten 1'i geçmiyor.")
  assert.equal(sade('Başlamadan önce 2 testten 1’i kalıyordu. Şimdi 2’si de geçiyor.'), 'Başlamadan önce 2 testten 1’i geçmiyordu. Şimdi 2’si de geçiyor.')
  assert.equal(sade('Başlamadan önce kontroller kalıyordu.'), 'Başlamadan önce kontroller geçmiyordu.')
  assert.equal(sade('Şimdi kontroller kalıyor.'), 'Şimdi kontroller geçmiyor.')
  assert.equal(sade('Bu deneme iş yapılmadan önce kalıyordu, şimdi geçiyor.'), 'Bu deneme iş yapılmadan önce geçmiyordu, şimdi geçiyor.')
})
test('başka anlamdaki "kal" sözlerine dokunulmaz', () => {
  for (const s of ['Yarıda kaldı', 'Söz kapandığı için iş yarıda kaldı.', 'Kaldığın yerden devam et', 'Bitti demek senin bakışına kalıyor.', 'Az yer kaldı', 'Bilgisayar açık kalmalı.', 'Eski değer kalır.', 'Kaldır']) assert.equal(sade(s), s)
})
