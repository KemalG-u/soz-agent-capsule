// "Bu bir iş mi?" kuralının birim sınaması. Çalıştır: node --test ui/scripts/is-tani.test.mjs   (Node 24: .ts doğrudan okunur)
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { isMi } from '../src/lib/isTani.ts'

const isDegil = [
  '', '   ', 'Merhaba', 'merhaba ben kimim', 'Merhaba ben kimim', 'bu ne işe yarar', 'Bu ne işe yarar?', 'selam', 'nasılsın', 'naber',
  'ben kimim', 'sen kimsin', 'bu program ne yapar', 'yardım', 'nasıl çalışır', 'Söz nedir', 'ne yapabilirsin', 'test', 'deneme',
  'hava nasıl', 'bugün ne var', 'bu ne?', 'çalışıyor mu', 'Teşekkürler', 'tamam', 'merhaba nasılsın', 'iyi akşamlar',
  'kaç tane iş var?', 'bu nasıl kullanılır', 'neden açılmıyor?', 'burası ne işe yarıyor',
]
const isDur = [
  'giriş sayfasındaki yazım hatasını düzelt', 'Giriş sayfasındaki yazım hatasını düzelt', 'giriş sayfasındaki yazım hatasını düzeltir misin',
  'login.html dosyasındaki başlığı değiştir', 'bütün sayfaları baştan sona gözden geçir [[sahte:uzun]]', 'testleri çalıştır',
  'npm test komutunu çalıştırıp hataları söyle', 'README dosyasına kurulum bölümü ekle', 'hesap.js içindeki toplama hatasını bul',
  'merhaba, giriş sayfasını düzelt', 'ana sayfadaki logoyu büyüt ve ortala', 'fatura dışa aktarmayı yaz',
  'src/app.ts dosyasında hata var', 'sayfanın rengini maviye çevirir misin', 'yeni bir ayarlar sayfası oluşturalım',
  'bu klasördeki eski kayıtları temizle', 'giriş sayfası yazım hatası', 'Parolayı unuttum bağlantısı çalışmıyor, düzelt',
  'Kullanıcı listesinde sıralama bozuk olduğu için sıralamayı tarihe göre yeniden yap',
]

test('iş olmayan girdiler iş sayılmaz', () => {
  for (const m of isDegil) assert.equal(isMi(m).is, false, `iş sanıldı: "${m}" (${isMi(m).neden})`)
})

test('gerçek iş cümleleri iş sayılır', () => {
  for (const m of isDur) assert.equal(isMi(m).is, true, `iş değil sanıldı: "${m}" (${isMi(m).neden})`)
})

test('neden bilgisi açıklanabilir', () => {
  assert.equal(isMi('merhaba').neden, 'selam')
  assert.equal(isMi('bu ne işe yarar').neden, 'kimlik')
  assert.equal(isMi('merhaba ben kimim').neden, 'kimlik')
  assert.equal(isMi('').neden, 'bos')
  assert.equal(isMi('test').neden, 'kisa')
  assert.equal(isMi('bugün hava nasıl olacak').neden, 'soru')
  assert.equal(isMi('login.html değiştir').neden, 'dosya')
})

test('uzun cümle fiil olmasa da iştir (kuşkuda iş: yazı kaybolmaz)', () => {
  assert.equal(isMi('giriş sayfasında kullanıcı adı alanının yanındaki yardım yazısı ekranda iki kere görünüyor').is, true)
})
