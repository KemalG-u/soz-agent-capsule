// f4: klasörsüz Başlat sonrası kayıtlı klasör (A-YENI-21) ve çift sözcük (Kusur 2). Çalıştır: node --test scripts/f4.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { isTestProject, seciliKlasor, kurulumGerekir, zatenEkliBildirimi } from '../src/lib/klasorSecim.ts'
import { sade } from '../src/lib/sade.ts'

const cicek = { id: 7, name: 'Çiçek', path: 'E:\\Sample Work\\urun\\_gecici\\f4\\sentetik\\Çiçek' }
const gercek = { id: 8, name: 'Bahçe', path: 'D:\\projeler\\bahce' }

test('sınama klasörü seçilmediyse varsayılana düşer (eski davranış korunur)', () => {
  assert.equal(isTestProject(cicek), true)
  assert.equal(seciliKlasor([cicek], 7, false, null), null)
  assert.equal(seciliKlasor([cicek, gercek], 7, false, gercek), gercek)
})
test('kişi sınama gibi görünen klasörü kendisi seçtiyse geçerli klasör odur (pencere geri dönmez)', () => {
  assert.equal(seciliKlasor([cicek], 7, true, null), cicek)
  assert.equal(seciliKlasor([cicek, gercek], 7, true, gercek), cicek)
})
test('sıradan klasör zaten seçilir; listede olmayan id varsayılana düşer', () => {
  assert.equal(isTestProject(gercek), false)
  assert.equal(seciliKlasor([gercek], 8, false, null), gercek)
  assert.equal(seciliKlasor([gercek], 99, true, null), null)
})
test('kurulum kartı: yeni klasör, kapalı kapı ya da kapalı kontrol varsa; her şey açıksa yok', () => {
  assert.equal(kurulumGerekir({ created: true, gateEnabled: true, suggested: [{ enabled: true }] }), true)
  assert.equal(kurulumGerekir({ created: false, gateEnabled: false, suggested: [] }), true)
  assert.equal(kurulumGerekir({ created: false, gateEnabled: true, suggested: [{ enabled: true }, { enabled: false }] }), true)
  assert.equal(kurulumGerekir({ created: false, gateEnabled: true, suggested: [{ enabled: true }] }), false)
})
test('"Bu klasör zaten ekli." yalnız elle eklemede; iş başlatma akışında gösterilmez', () => {
  assert.equal(zatenEkliBildirimi(false, false), true)
  assert.equal(zatenEkliBildirimi(false, true), false)
  assert.equal(zatenEkliBildirimi(true, false), false)
})

test('Kusur 2: "kanıt kontrolü" çift sözcük üretmez', () => {
  assert.equal(sade('iş bittiğinde kanıt kontrolü otomatik çalışır'), 'iş bittiğinde kontrol otomatik çalışır')
  assert.equal(sade('Geri alma noktası ve iş sonu kanıt kontrolü Söz tarafında otomatik çalışır.'), 'Geri alma noktası ve iş sonu kontrolü Söz tarafında otomatik çalışır.')
  assert.equal(sade('"Kanıt kontrolü" kartında bulunur'), '"Kontrol" kartında bulunur')
  assert.equal(sade('Kanıt kontrolü'), 'Kontrol')
})
test('sade çıktısında ardışık yinelenen sözcük kalmaz, doğru yinelemelere dokunulmaz', () => {
  assert.equal(sade('kanıt kanıt kontrolü'), 'kontrol')
  assert.equal(sade('deneme sonucu sonucu'), 'deneme sonucu')
  assert.equal(sade('komut komut ön onay yoktur'), 'komut komut ön onay yoktur')
  assert.equal(sade('ayrı ayrı kabul edilir'), 'ayrı ayrı kabul edilir')
  const o = sade('Ajan kanıt kontrolü yapar. Ajan ajan değil.')
  assert.equal(sade(o), o)
})
