// f1: iş seçimi (olmayan iş yerine ilk iş ASLA açılmaz) ve klasörsüz Başlat bekletmesi (başka taslak ASLA başlamaz).
// Çalıştır: node --test ui/scripts/f1.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { secimKarari, ilkIsBoottaMi } from '../src/lib/isSecim.ts'
import { bekle, vazgec, al, bosBekleyen } from '../src/lib/bekleyenBaslat.ts'

const b = (o) => ({ jobIds: [5, 7, 9], selId: null, routeId: null, pulling: null, missing: null, ...o })

test('şüphe 2: listede olmayan iş istenirse getirilir, ilk iş seçilmez', () => {
  assert.equal(secimKarari(b({ routeId: 4242 })), 'fetch-route')
})
test('şüphe 2: getirilirken bekler, bulunamayınca "Bu işi bulamadım"', () => {
  assert.equal(secimKarari(b({ routeId: 4242, pulling: 4242 })), 'wait')
  assert.equal(secimKarari(b({ routeId: 4242, missing: 4242 })), 'missing')
})
test('şüphe 2: adres başka işi gösterirken hiçbir durumda first dönmez', () => {
  for (const selId of [null, 5, 4242]) for (const pulling of [null, 4242]) for (const missing of [null, 4242]) {
    const k = secimKarari(b({ routeId: 4242, selId, pulling, missing }))
    assert.notEqual(k, 'first')
    assert.notEqual(k, 'keep')
  }
})
test('listede olan iş: seçilir, zaten seçiliyse dokunulmaz', () => {
  assert.equal(secimKarari(b({ routeId: 7 })), 'select-route')
  assert.equal(secimKarari(b({ routeId: 7, selId: 7 })), 'keep')
})
test('hiçbir şey istenmemişse ilk iş seçilir, liste boşsa hiçbiri', () => {
  assert.equal(secimKarari(b({})), 'first')
  assert.equal(secimKarari(b({ jobIds: [] })), 'none')
})
test('kısayolla seçilen iş listede yoksa getirilir, bulunamazsa söylenir', () => {
  assert.equal(secimKarari(b({ selId: 4242 })), 'fetch-sel')
  assert.equal(secimKarari(b({ selId: 4242, missing: 4242 })), 'missing')
})

// f2 (A-GEN-13, S2-04, S2-05, S3-01): açılışta (boot3) adreste iş numarası varken ilk iş ASLA seçilmez.
const bo = (o) => ({ route: 'is', routeSub: '', selId: null, missing: null, pulling: null, ...o })
test('f2 boot: olmayan numara (987654) iken ilk iş seçilmez, bulunamadıktan sonra da seçilmez', () => {
  assert.equal(ilkIsBoottaMi(bo({ routeSub: '987654' })), false)
  assert.equal(ilkIsBoottaMi(bo({ routeSub: '987654', missing: 987654 })), false)
  assert.equal(ilkIsBoottaMi(bo({ route: 'suan', routeSub: '987654', missing: 987654 })), false)
})
test('f2 boot: ilk 50 dışındaki var olan numara getirilirken ilk iş seçilmez', () => {
  assert.equal(ilkIsBoottaMi(bo({ routeSub: '130', pulling: 130 })), false)
  assert.equal(ilkIsBoottaMi(bo({ route: 'suan', routeSub: '130' })), false)
})
test('f2 boot: İşler sayfasında ilk işi Surface seçer, boot seçmez; başka sayfada hiçbir şey seçili değilse seçer', () => {
  assert.equal(ilkIsBoottaMi(bo({})), false)
  assert.equal(ilkIsBoottaMi(bo({ route: 'suan' })), true)
  assert.equal(ilkIsBoottaMi(bo({ route: 'suan', selId: 5 })), false)
  assert.equal(ilkIsBoottaMi(bo({ route: 'suan', missing: 9 })), false)
})
test('f2 yüklenme sırası: istenen iş getirilir, gelince tutulur, gelmezse "bulamadım", hiçbir adımda first yok', () => {
  const adimlar = [
    b({ routeId: 130 }), // yüklendi, iş listede yok: getir
    b({ routeId: 130, selId: 130, pulling: 130 }), // getiriliyor: bekle
    b({ routeId: 130, selId: 130, jobIds: [5, 7, 9, 130] }), // geldi: dokunma
    b({ routeId: 987654, selId: 987654, pulling: 987654 }),
    b({ routeId: 987654, selId: null, missing: 987654 }), // bulunamadı
  ]
  assert.deepEqual(adimlar.map(secimKarari), ['fetch-route', 'wait', 'keep', 'wait', 'missing'])
})
test('f2 geri tuşu: adres listedeki başka işe dönünce o iş seçilir', () => {
  assert.equal(secimKarari(b({ routeId: 5, selId: 130 })), 'select-route')
})

test('şüphe 1: bekleyen taslak aynıysa bir kez başlar', () => {
  const p = bosBekleyen()
  bekle(p, '  Girişi düzelt ')
  assert.equal(al(p, 'Girişi düzelt'), true)
  assert.equal(al(p, 'Girişi düzelt'), false)
})
test('şüphe 1: kutudaki taslak değiştiyse başka taslak başlamaz', () => {
  const p = bosBekleyen()
  bekle(p, 'Girişi düzelt')
  assert.equal(al(p, 'Başka bir iş'), false)
  assert.equal(p.on, false)
})
test('şüphe 1: vazgeçilince klasör gelse de başlamaz', () => {
  const p = bosBekleyen()
  bekle(p, 'Girişi düzelt')
  vazgec(p)
  assert.equal(al(p, 'Girişi düzelt'), false)
})
test('şüphe 1: boş taslak asla başlamaz', () => {
  const p = bosBekleyen()
  bekle(p, '   ')
  assert.equal(al(p, ''), false)
})

// ---- üçüncü parti: Sabah / Ne oldu / komut paleti bağlantıları aynı karara bağlı (A-SAB-07, A-SAB-10, C-02) ----
// Sabah ve Ne oldu bağlantısı go('is', no) ile #/is/<no> adresine gider; komut paleti de aynı adresi kullanır. Üçü de Surface'te routeId olarak secimKarari'na girer.
const yuz30 = Array.from({ length: 50 }, (_, i) => 130 - i) // liste yalnız en yeni 50 işi taşır: 130..81
test('Sabah bağlantısı: listede olmayan iş 1, seçili en yeni iş 130 iken iş 1 getirilir, 130 gösterilmez', () => {
  const k = secimKarari({ jobIds: yuz30, selId: 130, routeId: 1, pulling: null, missing: null })
  assert.equal(k, 'fetch-route')
})
test('Ne oldu bağlantısı: getirilirken beklenir, bulunamazsa "Bu işi bulamadım", hiçbir yolda ilk iş açılmaz', () => {
  assert.equal(secimKarari({ jobIds: yuz30, selId: 1, routeId: 1, pulling: 1, missing: null }), 'wait')
  assert.equal(secimKarari({ jobIds: yuz30, selId: 1, routeId: 1, pulling: null, missing: 1 }), 'missing')
})
test('komut paleti: listedeki iş adresle aynı karar yolundan seçilir', () => {
  assert.equal(secimKarari({ jobIds: yuz30, selId: 130, routeId: 100, pulling: null, missing: null }), 'select-route')
})

import { islemKilidi } from '../src/lib/islemKilidi.ts'
test('ekle/kaydet kilidi: çift tıkta ikinci istek gitmez, iş bitince yeniden açılır', async () => {
  const gorulen = []
  const kilit = islemKilidi((b) => gorulen.push(b))
  let sayac = 0
  const is = async () => { sayac++; await new Promise((r) => setTimeout(r, 20)); return sayac }
  const [a, b] = await Promise.all([kilit.calistir(is), kilit.calistir(is)])
  assert.equal(sayac, 1)
  assert.equal(a, 1)
  assert.equal(b, undefined)
  assert.equal(kilit.mesgul, false)
  assert.deepEqual(gorulen, [true, false])
  await kilit.calistir(is)
  assert.equal(sayac, 2)
})
