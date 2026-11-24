// "Bu bir iş mi?": üst kutuya yazılan cümlenin yapay zekâya verilecek bir iş emri mi, yoksa selam ya da soru mu olduğunu ayırır.
// Dil modeli yok: kısa, sırası belli, açıklanabilir bir kural listesi. Yanlış alarm olursa kişi "Yine de iş olarak ver" der.
// Kural sırası (ilk eşleşen kazanır):
//  1. Boş yazı iş değil.
//  2. Dosya adı, yol, kod izi ya da emir fiili varsa iş. (Fiil "düzeltir misin" gibi soru kalıbında da iştir.)
//  3. Söz hakkında ya da kendin hakkında soru (ben kimim, bu ne işe yarar, nasıl çalışır, yardım) iş değil.
//  4. Selamlaşma (merhaba, selam, nasılsın...) kısa ise iş değil.
//  5. Tek ya da iki sözcük, fiilsiz: iş değil.
//  6. Soru işaretiyle biten kısa cümle ya da soru sözcüklü kısa cümle: iş değil.
//  7. Geri kalan her şey iş sayılır (kuşkuda iş: kişi yazdığını kaybetmesin).

export type IsNedeni = 'bos' | 'selam' | 'kimlik' | 'kisa' | 'soru' | 'dosya' | 'fiil' | 'uzun' | 'varsayilan'
export interface IsKarari { is: boolean; neden: IsNedeni }

const norm = (s: string): string =>
  s.toLocaleLowerCase('tr-TR').replace(/[“”"'`’]/g, ' ').replace(/\s+/g, ' ').trim()

const sozcukler = (s: string): string[] => s.split(/[\s,;:!?.…()]+/).filter(Boolean)

// Emir köküne ek alan biçimler de eşleşir (düzelt, düzeltir misin, düzeltmek, düzeltelim). "kurallar" gibi başka sözcükler eşleşmez.
const KOKLER = [
  'düzelt', 'ekle', 'sil', 'yaz', 'kur', 'değiştir', 'güncelle', 'oluştur', 'kaldır', 'taşı', 'çalıştır', 'incele', 'hazırla',
  'yap', 'çevir', 'tamamla', 'birleştir', 'ayır', 'azalt', 'temizle', 'derle', 'yayınla', 'listele', 'adlandır', 'düzenle',
  'sırala', 'indir', 'yükle', 'aç', 'kapat', 'onar', 'iyileştir', 'hızlandır', 'bul', 'kontrol et', 'gözden geçir', 'test et',
  'çöz', 'uygula', 'entegre et', 'optimize et', 'tasarla', 'çiz', 'dönüştür', 'kopyala', 'yeniden adlandır', 'refactor', 'fixle',
  'geliştir', 'arttır', 'artır', 'sadeleştir', 'ayarla', 'bağla', 'bağlantı kur', 'göster', 'gizle', 'çıkar', 'ara', 'say', 'özetle',
  'fix', 'add', 'remove', 'delete', 'update', 'create', 'write', 'build', 'run', 'rename', 'refactor',
]
const EKLER = '(?:t|r|y)?(?:mak|mek|ir|ır|ur|ür|ip|ıp|up|üp|in|ın|un|ün|sin|sın|sun|sün|eyim|ayım|elim|alım|meli|malı|ecek|acak|er|ar|iyor|ıyor|uyor|üyor|miş|mış|se|sa)?(?:\\s?(?:mi|mı|mu|mü|misin|mısın|musun|müsün|misiniz|mısınız))?'
const FIIL = new RegExp('^(?:' + KOKLER.map((k) => k.replace(/ /g, '\\s')).join('|') + ')' + EKLER + '$', 'iu')

const DOSYA = /(?:[\w-]+\.(?:html?|css|scss|js|jsx|ts|tsx|mjs|py|cs|csproj|sln|md|json|svelte|vue|txt|yml|yaml|xml|sql|sh|ps1|bat|go|rs|java|kt|php|rb|cpp|h|ini|toml|env|log|csv)\b)|[\\/][\w.-]+[\\/]|`|\[\[|\b[a-z]:\\|\bnpm\b|\bgit\b|\bdotnet\b|\bsrc\b/iu

const SELAM = /^(?:merhaba|merhabalar|selam|selamlar|selamün aleyküm|günaydın|iyi günler|iyi akşamlar|iyi geceler|hey|hi|hello|naber|nabıyon|nasılsın|nasılsınız|ne haber|teşekkürler|teşekkür ederim|sağ ol|sağol|eyvallah|tamam|ok|evet|hayır|peki)\b/u

const KIMLIK = /\b(?:ben kimim|sen kimsin|bu kim|sen neysin|bu ne|bu nedir|bu neyi|bu program|bu uygulama|söz ne|söz nedir|ne işe yarar|ne işe yarıyor|ne yapar|ne yapıyor|ne yapabilir|ne yapabilirsin|ne yaparsın|nasıl çalışır|nasıl çalışıyor|nasıl kullanılır|nasıl kullanırım|ne demek|yardım|help|ne var ne yok)\b/u

const SORU_SOZ = /\b(?:ne|neden|niçin|niye|nasıl|kim|kimin|kimi|kaç|kaçta|nerede|nereye|nereden|hangi|hangisi|ne zaman|ne kadar|hani)\b/u
const SORU_EK = /\b(?:mi|mı|mu|mü|misin|mısın|musun|müsün|miyim|mıyım|muyum|müyüm|miyiz|mıyız|midir|mıdır)\s*$/u

/** Yazılan cümle iş mi, değil mi; neden öyle dediğini de söyler (arayüz ve test için). */
export function isMi(metin: string): IsKarari {
  const raw = metin.trim()
  if (!raw) return { is: false, neden: 'bos' }
  const s = norm(raw)
  const kelimeler = sozcukler(s)
  const n = kelimeler.length
  const soruIsareti = /[?？]\s*$/.test(raw)

  // 2. Açık iş izi: dosya, yol, kod ya da emir fiili. Söz hakkındaki soru kalıpları ("ne yapar") fiil sayılmaz.
  if (DOSYA.test(raw)) return { is: true, neden: 'dosya' }
  const kv = sozcukler(s.replace(new RegExp(KIMLIK.source, 'gu'), ' '))
  const fiil = kv.some((w, i) => FIIL.test(w) || (i < kv.length - 1 && FIIL.test(w + ' ' + kv[i + 1]!)))
  if (fiil) return { is: true, neden: 'fiil' }

  // 3. Söz hakkında ya da kendin hakkında soru
  if (n <= 9 && KIMLIK.test(s)) return { is: false, neden: 'kimlik' }
  // 4. Selamlaşma
  if (n <= 6 && SELAM.test(s)) return { is: false, neden: 'selam' }
  // 5. Çok kısa ve fiilsiz
  if (n <= 2) return { is: false, neden: 'kisa' }
  // 6. Kısa soru
  if (n <= 8 && (soruIsareti || SORU_EK.test(s) || (SORU_SOZ.test(s) && n <= 6))) return { is: false, neden: 'soru' }
  // 7. Uzun cümle ya da varsayılan: iş
  return { is: true, neden: n >= 9 ? 'uzun' : 'varsayilan' }
}
