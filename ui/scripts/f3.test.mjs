// f3: "Yarıda kaldı" kartı. Çalıştır: node --test scripts/f3.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { yaridaMetni, ayarIlgili, hamAyir } from '../src/lib/yarida.ts'

test('yeni sunucu cümlesi "Yarıda kaldı" sayılır, eski cümleler de', () => {
  assert.equal(yaridaMetni('Yardımcı işi yarıda bırakıp durdu.'), true)
  assert.equal(yaridaMetni('Yardımcı hata ile bitti: boom'), true)
  assert.equal(yaridaMetni('Yardımcı çalışırken hata verdi'), true)
})
test('başlatılamayan iş "Yarıda kaldı" sayılmaz', () => {
  assert.equal(yaridaMetni('Claude bulunamadı.'), false)
  assert.equal(yaridaMetni(null), false)
  assert.equal(yaridaMetni(undefined), false)
})
test('Ayarlar yalnız ayarla ilgili hatada önerilir', () => {
  assert.equal(ayarIlgili('Yardımcı işi yarıda bırakıp durdu.'), false)
  assert.equal(ayarIlgili('Yardımcı işi yarıda bırakıp durdu.', 'boom: bir şey ters gitti'), false)
  assert.equal(ayarIlgili('Yardımcı işi yarıda bırakıp durdu.', 'Claude komutu bulunamadı'), true)
  assert.equal(ayarIlgili('Yardımcı işi yarıda bırakıp durdu.', 'Not logged in, please run /login'), true)
  assert.equal(ayarIlgili(null, null), false)
})
test('eski kayıtta ham hata cümleden ayrılır, yeni kayıt aynen kalır', () => {
  assert.deepEqual(hamAyir('Yardımcı hata ile bitti: boom: bir sey ters gitti', 'çıkış kodu 1'), { text: 'Yardımcı işi yarıda bırakıp durdu.', detail: 'boom: bir sey ters gitti\nçıkış kodu 1' })
  assert.deepEqual(hamAyir('Yardımcı işi yarıda bırakıp durdu.', 'çıkış kodu 1'), { text: 'Yardımcı işi yarıda bırakıp durdu.', detail: 'çıkış kodu 1' })
  assert.deepEqual(hamAyir('Yardımcı işi yarıda bırakıp durdu.', null), { text: 'Yardımcı işi yarıda bırakıp durdu.', detail: null })
})
import { notCumleleri } from '../src/lib/yarida.ts'
test('Şu An notu: sunucu cümlesi tek başına, önek yok, iki not ayrı cümle', () => {
  assert.equal(notCumleleri(["Claude'un açık pencerelerinin hepsini şu an göremiyorum."]), "Claude'un açık pencerelerinin hepsini şu an göremiyorum.")
  assert.equal(notCumleleri(['Codex kaynağı okunamadı', 'Hermes kapalı.']), 'Codex kaynağı okunamadı. Hermes kapalı.')
  assert.equal(notCumleleri(['a', 'b', 'c']), 'a. b.')
  assert.equal(notCumleleri([]), '')
  assert.ok(!notCumleleri(['x. y', 'z']).includes(';'))
})
