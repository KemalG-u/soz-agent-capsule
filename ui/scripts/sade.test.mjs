// sade.ts (eski sunucu sözcüklerini ekranda sadeleştiren çeviri) yeni sunucu cümleleriyle çift çeviri yapıp bozmasın (u10). Çalıştır: node --test ui/scripts/sade.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { sade, sadeAgac } from '../src/lib/sade.ts'

// Sunucunun artık sade söylediği cümleler (src\Soz.Core: ChangeExpectation, StartFailure, ProofStore, AttentionBuilder). sade() bunlara DOKUNMAMALI.
const YENI = [
  'Yardımcı iş yaptığını söyledi ama klasörde hiçbir değişiklik yok. Bu iş yapılmamış görünüyor.',
  'Klasörde hiçbir değişiklik yok. İş yapılmamış görünüyor.',
  'Klasörde hiçbir değişiklik yok. İşin klasörde değişiklik isteyip istemediğini bilemedim, bu yüzden "geçti" demiyorum.',
  'Yardımcı iş yaptığını söyledi ama klasörde hiçbir değişiklik yok. Bu iş yapılmamış görünüyor. Kontroller geçiyor ama bunlar işten önce de geçiyordu. Bu bir "geçti" değildir.',
  'Claude bu bilgisayarda bulunamadı. Ayarlar\'dan yolunu kontrol et.',
  '«Giriş sayfası» işinde yardımcı sana bir soru sordu.',
  '«Giriş sayfası» için deneme sonucu geçmedi.',
  '«Giriş sayfası» deneme sonucuyla bitti.',
  '«Giriş sayfası» işinde deneme sonucu sayılamadı. Sonucu kabul edip etmeyeceğine sen karar ver.',
  'Deneme sonucu sayılamadı, yardımcıya geri yollandı (tur 1/2).',
  'Denenmedi', 'Kontrol yok', 'Bitti, denenmedi', 'Yardımcı hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu.',
]
for (const s of YENI) {
  test(`yeni cümle olduğu gibi kalır: ${s.slice(0, 50)}`, () => {
    assert.equal(sade(s), s)
    assert.equal(sade(sade(s)), sade(s)) // çift çeviri ikinci kez değiştirmez
  })
}

// Eski cümleler hâlâ sadeleşir (henüz yeni söze geçmemiş sunucu yolları için köprü).
test('eski sözcükler yeni sözcüğe çevrilir', () => {
  assert.equal(sade('Ajan bu testleri de değiştirmiş.'), 'Yardımcı bu testleri de değiştirmiş.')
  assert.equal(sade('Kanıtsız bitti'), 'Denenmedi bitti')
  assert.equal(sade('1 ölçüt kaldı'), '1 kontrol kaldı')
})

// Çeviri bir kez uygulanmalı: sonuç, tekrar çevrilince değişmemeli (idempotent), hiçbir eski sözcük kalmamalı.
test('çevrilmiş sonuç yeniden çevrilince değişmez ve eski sözcük kalmaz', () => {
  const eski = ['Ajan bu testleri de değiştirmiş', 'kanıtıyla bitti', 'Kanıt sayamam', 'ajanın çalıştığı terminalde', 'ölçütlerin hepsi geçti', 'Kanıtsız', 'kanıtlanmadı']
  for (const s of eski) {
    const bir = sade(s)
    assert.equal(sade(bir), bir, s)
    assert.ok(!/[Aa]jan|kanıt|Kanıt|ölçüt/.test(bir), `${s} -> ${bir}`)
  }
})

// Ağaç çevirisi yalnız cümle alanlarına dokunur; "sozNotu" yeni sade cümleyi bozmaz.
test('sadeAgac: sozNotu ve summary yeni cümlede aynı kalır, başka alanlar bozulmaz', () => {
  const o = { reason: 'start_failed', detail: null, sozNotu: 'Claude bu bilgisayarda bulunamadı. Ayarlar\'dan yolunu kontrol et.', summary: YENI[0], ajanAdi: 'ajan' }
  const k = sadeAgac(structuredClone(o))
  assert.equal(k.sozNotu, o.sozNotu)
  assert.equal(k.summary, o.summary)
  assert.equal(k.ajanAdi, 'ajan')
})

// f2 (S3-18): oturum, komut satırı, agy sözcükleri kod okumayan kişiye söylenmez.
test('f2: oturum ve komut satırı sözcükleri sade söylenir', () => {
  assert.equal(sade('4 oturum takılmış olabilir'), '4 çalışma takılmış olabilir')
  assert.equal(sade('Son 30 günde 0 oturum kaydı, toplam 0'), 'Son 30 günde 0 çalışma kaydı, toplam 0')
  const a = sade('Antigravity: Söz işi Antigravity komut satırı aracını (agy) arka planda başlatır ve Antigravity oturum özetlerini salt okunur izler.')
  assert.doesNotMatch(a, /oturum|komut satırı|agy/i)
  assert.equal(sade(a), a)
})
