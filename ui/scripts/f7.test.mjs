// f7: "kişi neye basacağını biliyor mu" düzeltmelerinin saf kararları (u1 TASARIM.md A1, O1, O2, O6, O10, O12, D8, D10).
// Çalıştır: node --test scripts/f7.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import {
  azOnceGorunur, bekleyenSayaci, darGorunum, escKarari, gecmisKarari, geriAdimiMi, geriAlGorunur, kisaAd, korumaNotuGorunur,
  olumsuzSonuc, sadeKomut, testKomutuMu, yardimciDurumu,
} from '../src/lib/gezinme.ts'

const esc = (o) => escKarari({ katmanAcik: false, durdurSorusu: false, yaziyor: false, route: 'suan', sub: '', dar: true, gorunum: 'list', kurulum: false, navTabs: [], ...o })

test('A1 Esc: açık pencere varken sayfa hiçbir şey yapmaz (pencere kendisi kapanır)', () => {
  for (const route of ['suan', 'is', 'ayarlar', 'sira', 'bekci']) assert.deepEqual(esc({ katmanAcik: true, route, sub: '5', gorunum: 'detail' }), { tur: 'yok' })
})
test('A1 Esc: en üst ekranda hiçbir şey yapmaz', () => {
  for (const route of ['suan', 'projelerim', 'ayarlar']) assert.deepEqual(esc({ route }), { tur: 'yok' })
  assert.deepEqual(esc({ route: 'ayarlar', sub: 'claude' }), { tur: 'yok' })
  assert.deepEqual(esc({ route: 'is', sub: '' }), { tur: 'yok' }) // liste en üst
  assert.deepEqual(esc({ route: 'bekci', navTabs: ['bekci'] }), { tur: 'yok' }) // şeritteki isteğe bağlı sekme de en üst
})
test('A1 Esc: alt sayfada bir üst ekrana döner', () => {
  assert.deepEqual(esc({ route: 'is', sub: '12', dar: true, gorunum: 'detail' }), { tur: 'liste' })
  assert.deepEqual(esc({ route: 'is', sub: '12', dar: false, gorunum: 'detail' }), { tur: 'listeOdak' })
  assert.deepEqual(esc({ route: 'sira' }), { tur: 'git', hedef: 'suan' })
  assert.deepEqual(esc({ route: 'taniyorum' }), { tur: 'git', hedef: 'ayarlar' })
  assert.deepEqual(esc({ route: 'sabah', navTabs: ['neoldu'] }), { tur: 'git', hedef: 'neoldu' })
  assert.deepEqual(esc({ route: 'sabah', navTabs: [] }), { tur: 'git', hedef: 'suan' }) // üst ekran şeritte yoksa Şu An
  assert.deepEqual(esc({ route: 'defter', sub: 'tekrarlar', navTabs: ['defter'] }), { tur: 'git', hedef: 'defter' })
  assert.deepEqual(esc({ route: 'bekci', navTabs: [] }), { tur: 'git', hedef: 'suan' })
})
test('A1 Esc: kutuda yazarken ya da kurulum kartı açıkken sayfa değişmez, "Durdurayım mı?" sorusu yalnız kapanır', () => {
  assert.deepEqual(esc({ yaziyor: true, route: 'sira' }), { tur: 'yok' })
  assert.deepEqual(esc({ route: 'is', sub: '3', dar: true, gorunum: 'detail', kurulum: true }), { tur: 'yok' })
  assert.deepEqual(esc({ route: 'is', sub: '3', dar: true, gorunum: 'detail', durdurSorusu: true }), { tur: 'durdurKapat' })
})

test('O1 geçmiş: listeden ayrıntıya ilk geçiş geçmişe yazılır, ayrıntıdan ayrıntıya yerinde değişir', () => {
  assert.equal(gecmisKarari({ route: 'is', sub: '', id: 7, oto: false, dar: true }), 'push')
  assert.equal(gecmisKarari({ route: 'is', sub: '', id: 7, oto: false, dar: false }), 'push')
  assert.equal(gecmisKarari({ route: 'is', sub: '3', id: 7, oto: false, dar: true }), 'replace')
  assert.equal(gecmisKarari({ route: 'is', sub: '7', id: 7, oto: false, dar: true }), 'yok')
})
test('O1 geçmiş: kendiliğinden seçim geçmişe yazılmaz (dar pencerede adres hiç değişmez), İşler dışında adres değişmez', () => {
  assert.equal(gecmisKarari({ route: 'is', sub: '', id: 7, oto: true, dar: true }), 'yok')
  assert.equal(gecmisKarari({ route: 'is', sub: '', id: 7, oto: true, dar: false }), 'replace')
  assert.equal(gecmisKarari({ route: 'suan', sub: '', id: 7, oto: false, dar: true }), 'yok')
  assert.equal(gecmisKarari({ route: 'is', sub: '', id: -5, oto: false, dar: true }), 'yok') // iyimser (henüz sunucuda olmayan) iş
  assert.equal(gecmisKarari({ route: 'is', sub: '', id: null, oto: false, dar: true }), 'yok')
})
test('O1 dar pencerede görünüm adresten: #/is liste, #/is/<no> ayrıntı', () => {
  assert.equal(darGorunum(''), 'list')
  assert.equal(darGorunum('42'), 'detail')
  assert.equal(darGorunum('abc'), 'list')
})
test('O2 Geri: yalnız damgası küçük kayda gidiş geri adımıdır (yeni gezinmede damga yok)', () => {
  assert.equal(geriAdimiMi(100, 200), true)
  assert.equal(geriAdimiMi(300, 200), false)
  assert.equal(geriAdimiMi(undefined, 200), false)
  assert.equal(geriAdimiMi(null, 200), false)
  assert.equal(geriAdimiMi('100', 200), false)
})

test('O6 ve D10: "Geri al" yalnız değişiklik varken ve iş sürmüyorken görünür', () => {
  assert.equal(geriAlGorunur({ canUndo: true, calisiyor: false, degisiklikYok: false }), true)
  assert.equal(geriAlGorunur({ canUndo: true, calisiyor: true, degisiklikYok: false }), false)
  assert.equal(geriAlGorunur({ canUndo: true, calisiyor: false, degisiklikYok: true }), false)
  assert.equal(geriAlGorunur({ canUndo: false, calisiyor: false, degisiklikYok: false }), false)
  assert.equal(korumaNotuGorunur({ level: 'tam', degisiklikYok: true }), false) // yeşil not yok
  assert.equal(korumaNotuGorunur({ level: 'tam', degisiklikYok: false }), true)
  assert.equal(korumaNotuGorunur({ level: 'yok', degisiklikYok: true }), true) // "geri alma yok" uyarısı kalır
})
test('O6: olumsuz sonuç yalnız "iş yapılmamış" ve "olmadı"', () => {
  assert.equal(olumsuzSonuc('nowork'), true)
  assert.equal(olumsuzSonuc('failed'), true)
  for (const b of ['passed', 'accepted', 'nochange', 'unproven', 'stopped', 'running', null]) assert.equal(olumsuzSonuc(b), false)
})

test('D8: sayaç alt şerit ve Şu An ile aynı sayıyı söyler, liste dışındakiler "N tane daha" olur', () => {
  assert.deepEqual(bekleyenSayaci({ bekleyenYuklendi: true, bekleyenToplam: 16, listedeki: 7, dahaVar: true }), { metin: '16', sayi: 16, fazla: 9 })
  assert.deepEqual(bekleyenSayaci({ bekleyenYuklendi: true, bekleyenToplam: 3, listedeki: 5, dahaVar: false }), { metin: '3', sayi: 3, fazla: 0 })
  assert.deepEqual(bekleyenSayaci({ bekleyenYuklendi: false, bekleyenToplam: 0, listedeki: 7, dahaVar: true }), { metin: '7+', sayi: 7, fazla: 0 })
})

test('O5: bildirim için kısa ad ve "Az önce verdiğin" satırının süresi', () => {
  assert.equal(kisaAd('  Fatura   PDF  '), 'Fatura PDF')
  const uzun = 'a'.repeat(60)
  assert.equal(kisaAd(uzun).length, 40)
  assert.ok(kisaAd(uzun).endsWith('…'))
  const simdi = 10_000_000
  assert.equal(azOnceGorunur({ verildi: simdi - 60_000, simdi, suruyor: false }), true)
  assert.equal(azOnceGorunur({ verildi: simdi - 31 * 60_000, simdi, suruyor: false }), false)
  assert.equal(azOnceGorunur({ verildi: simdi - 31 * 60_000, simdi, suruyor: true }), true)
  assert.equal(azOnceGorunur({ verildi: NaN, simdi, suruyor: true }), false)
})

test('O10: yardımcı durumu hazırlık bilgisinden, yoksa "bulundu mu"dan', () => {
  assert.equal(yardimciDurumu(true, { found: true, canStart: false }), 'hazirDegil') // "giriş yapılmamış" iken Ayarlar "hazır" demez
  assert.equal(yardimciDurumu(true, { found: true, canStart: true }), 'hazir')
  assert.equal(yardimciDurumu(true, null), 'hazir')
  assert.equal(yardimciDurumu(false, null), 'yok')
  assert.equal(yardimciDurumu(false, { found: false, canStart: false }), 'yok')
})

test('O12: test komutu kişinin diliyle söylenir', () => {
  assert.equal(testKomutuMu('npm test'), true)
  assert.equal(testKomutuMu('npm run test -- --watch=false'), true)
  assert.equal(testKomutuMu('dotnet test Soz.sln'), true)
  assert.equal(testKomutuMu('node build.js'), false)
  assert.equal(sadeKomut('Testler geçsin (npm test)', 'Klasörün kendi testleri'), 'Testler geçsin (Klasörün kendi testleri)')
  assert.equal(sadeKomut('Testler geçsin', 'X'), 'Testler geçsin')
})

// Sözlük: yeni ve değişen cümleler sade kurala uyar (uzun tire ve noktalı virgül yok), eski sözcükler kalmadı.
const kaynak = (p) => readFileSync(new URL(p, import.meta.url), 'utf8')
test('Sözlük: tr11 cümlelerinde uzun tire ve noktalı virgül yok, İngilizce karşılığı eksiksiz', async () => {
  const s = kaynak('../src/lib/i18n/tr11.ts')
  assert.ok(!/[—–;]/.test(s.replace(/^\s*\/\/.*$/gm, '')), 'uzun tire ya da noktalı virgül var')
  const { tr11, en11 } = await import('../src/lib/i18n/tr11.ts')
  for (const k of Object.keys(tr11)) assert.ok(k in en11, `İngilizcesi yok: ${k}`)
})
test('Sözlük: O7 ve O12 sözleri yerinde', () => {
  const tr9 = kaynak('../src/lib/i18n/tr9.ts')
  const tr3 = kaynak('../src/lib/i18n/tr3.ts')
  assert.match(tr9, /protLimitsBtn: 'Neler geri alınamaz\?'/)
  assert.match(tr9, /barHead: 'Yardımcıya iş ver'/)
  assert.match(tr3, /setupOpenAll: 'Denemeyi ve korumayı aç'/)
  assert.ok(!/yapay zekâ/i.test(tr9.replace(/^\s*\/\/.*$/gm, '')), 'tr9 içinde "yapay zekâ" kaldı')
})
test('Kaynakta ters bölüsüz düzenli ifade yok (DERSLER: heredoc ters bölü kaybı)', () => {
  for (const p of ['../src/lib/gezinme.ts', '../src/lib/katman.ts', '../src/lib/router.svelte.ts']) {
    const s = kaynak(p)
    assert.ok(!/\/[sdwSDW][+*]/.test(s), `${p}: ters bölüsüz \\s, \\d ya da \\w`)
  }
})
