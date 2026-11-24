// f5: Sabah aralığı korunur (G-DÜŞÜK-5), sayaçlar yanıltmaz (G-DÜŞÜK-4), Söz kapandığı için duran iş (Gözlem), söz ile düğme adı aynı (G-DÜŞÜK-2).
// Çalıştır: node --test scripts/f5.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { sabahAralikCoz, sabahAralikOku, sabahAralikYaz, SABAH_ARALIK_KEY } from '../src/lib/sabahAralik.ts'
import { sekmeIsSayisi, durumSayaci } from '../src/lib/sayac.ts'
import { sozKapandiMi, SOZ_KAPANDI_SATIRI_RE } from '../src/lib/yarida.ts'

const sahteDepo = () => { const m = new Map(); return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => void m.set(k, String(v)), m } }

test('Sabah aralığı: yalnız 0, 24, 72 geçerli; bilinmeyen değer varsayılana (0) düşer', () => {
  assert.equal(sabahAralikCoz('24'), 24)
  assert.equal(sabahAralikCoz(72), 72)
  assert.equal(sabahAralikCoz('0'), 0)
  for (const k of ['', 'abc', '5', '-24', null, undefined, NaN, {}]) assert.equal(sabahAralikCoz(k), 0)
})
test('Sabah aralığı: seçim yazılır ve sayfa yeniden açılınca (yeni okuma) aynen gelir', () => {
  const depo = sahteDepo()
  assert.equal(sabahAralikOku(depo), 0) // hiç seçilmemiş
  sabahAralikYaz(24, depo)
  assert.equal(depo.m.get(SABAH_ARALIK_KEY), '24')
  assert.equal(sabahAralikOku(depo), 24) // işe gidip Geri gelince
  sabahAralikYaz(0, depo)
  assert.equal(sabahAralikOku(depo), 0)
})
test('Sabah aralığı: depolama kapalı ya da bozuksa sayfa yine açılır', () => {
  const bozuk = { getItem() { throw new Error('kapalı') }, setItem() { throw new Error('kapalı') } }
  assert.equal(sabahAralikOku(bozuk), 0)
  assert.doesNotThrow(() => sabahAralikYaz(24, bozuk))
  assert.equal(sabahAralikOku(null), 0)
  const kirli = sahteDepo(); kirli.m.set(SABAH_ARALIK_KEY, 'çöp')
  assert.equal(sabahAralikOku(kirli), 0)
})

test('Sekme sayısı: 60 iş varken 50 değil 60 yazar; liste tamsa yüklenen sayıdır', () => {
  assert.equal(sekmeIsSayisi(50, 60, true), 60)
  assert.equal(sekmeIsSayisi(60, 60, false), 60)
  assert.equal(sekmeIsSayisi(3, 3, false), 3)
  assert.equal(sekmeIsSayisi(50, 0, true), 50) // toplam gelmediyse yüklenen sayının altına inmez
  assert.equal(sekmeIsSayisi(50, Number.NaN, true), 50)
})
test('Durum sayacı: yüklenmemiş iş varsa "50+" der, yoksa kesin sayı', () => {
  assert.equal(durumSayaci(50, true), '50+')
  assert.equal(durumSayaci(7, true), '7+')
  assert.equal(durumSayaci(0, true), '0')
  assert.equal(durumSayaci(50, false), '50')
  assert.equal(durumSayaci(0, false), '0')
})

test('Söz kapandığı için duran iş: düzgün kapanış ve zorla kapanış tanınır, kişinin durdurması tanınmaz', () => {
  assert.equal(sozKapandiMi('Söz kapandığı için iş durduruldu.'), true)
  assert.equal(sozKapandiMi('yarım kaldı'), true)
  assert.equal(sozKapandiMi(' Yarım kaldı '), true)
  for (const k of ['durduruldu', 'Durduruldu', 'İş durduruldu.', '', null, undefined, 'Yardımcı işi yarım bıraktı']) assert.equal(sozKapandiMi(k), false)
})
test('Akıştaki "Söz kapanmıştı" satırı tanınır (kart aynı şeyi söylediği için tekrarlanmaz)', () => {
  assert.equal(SOZ_KAPANDI_SATIRI_RE.test('Söz kapanmıştı, iş yarım kaldı. Sürdürmek için "Aynı işe yeni bir şey ekle" düğmesini kullanabilirsin.'), true)
  assert.equal(SOZ_KAPANDI_SATIRI_RE.test('Söz kapanmıştı, iş yarım kaldı. İstersen "Bu işe devam et" diyebilirsin.'), true) // eski kayıt
  assert.equal(SOZ_KAPANDI_SATIRI_RE.test('Söz kapandığı için iş durduruldu.'), true)
  assert.equal(SOZ_KAPANDI_SATIRI_RE.test('Durdurdun. Yardımcının süreci kapatıldı.'), false)
})

// G-DÜŞÜK-2 ve Gözlem: ekrandaki söz, ekrandaki düğmenin adıyla aynı olmalı. Düğme adı tr9'da (tr3'ü ezer).
const kaynak = (d) => readFileSync(new URL(`../src/lib/i18n/${d}.ts`, import.meta.url), 'utf8')
const anahtar = (dosya, ad) => new RegExp(`^\\s*${ad}:\\s*'((?:[^'\\\\]|\\\\.)*)'`, 'm').exec(kaynak(dosya))?.[1] ?? ''
test('Durdurma sorusu ve "Söz kapandığı" kartı gerçek düğme adını söyler', () => {
  const dugme = anahtar('tr9', 'continueBtn')
  assert.equal(dugme, 'Aynı işe yeni bir şey ekle')
  for (const k of ['stopAsk', 'shutdownBody']) assert.ok(anahtar('tr10', k).includes(`"${dugme}"`), `${k} düğme adını söylemeli: ${anahtar('tr10', k)}`)
  assert.ok(!/Devam et/.test(anahtar('tr10', 'stopAsk')))
  assert.ok(!/Devam et|devam et/.test(anahtar('tr10', 'rowStoppedShutdown')))
})
