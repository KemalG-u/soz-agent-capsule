// Karşılaştırma cümlesi ayrıştırıcısının birim sınaması (u10). Çalıştır: node --test ui/scripts/claim.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { claimCompare, withoutClaim } from '../src/lib/claim.ts'

const LEAD = 'Ajan hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu.'
test('söylenen ile bulunmayan: uyuşmazlık', () => {
  const c = claimCompare(`${LEAD} Yardımcı 2 dosya değiştirdiğini söyledi. Ben klasörde değişiklik bulamadım.`)
  assert.deepEqual(c, { said: 'Yardımcı 2 dosya değiştirdiğini söyledi.', found: 'Ben klasörde değişiklik bulamadım.', mismatch: true })
})
test('söylenen ile bulunan: uyuşma', () => {
  const c = claimCompare(null, 'Yardımcı 1 dosya değiştirdiğini söyledi. Ben de klasörde değişiklik buldum.')
  assert.equal(c?.mismatch, false)
  assert.equal(c?.found, 'Ben de klasörde değişiklik buldum.')
})
test('hiç söylemedi ama bulundu', () => {
  assert.equal(claimCompare('Yardımcı dosya değiştirdiğini söylemedi. Ben klasörde değişiklik buldum.')?.mismatch, false)
})
test('izlenen dosyalarda bulunamadı', () => {
  assert.equal(claimCompare('Yardımcı 3 dosya değiştirdiğini söyledi. Ben izlenen dosyalarda değişiklik bulamadım.')?.mismatch, true)
})
test('cümle yoksa null; ilk cümle (Ajan hiçbir dosyayı değiştirmedi) karşılaştırma sayılmaz', () => {
  assert.equal(claimCompare(LEAD), null)
  assert.equal(claimCompare('', null, undefined), null)
})
test('withoutClaim cümleyi çıkarır, geri kalanı korur', () => {
  assert.equal(withoutClaim(`${LEAD} Yardımcı 2 dosya değiştirdiğini söyledi. Ben klasörde değişiklik bulamadım.`), LEAD)
  assert.equal(withoutClaim(LEAD), LEAD)
  assert.equal(withoutClaim(null), '')
})
