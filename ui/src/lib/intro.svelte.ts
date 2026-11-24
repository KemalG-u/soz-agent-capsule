// İlk bakış tanıtımı: ilk açılışlarda Şu An ekranının üstünde görünür. Kapatılınca bir daha çıkmaz, Ayarlar'dan geri açılır.
// Tercih yalnız bu tarayıcıda (WebView) tutulur: kişisel bir görünüm tercihidir, sunucuya yazılmaz.
const KEY = 'soz.intro.kapali'

function read(): boolean {
  try {
    return localStorage.getItem(KEY) === '1'
  } catch {
    return false
  }
}

export const intro = $state({ closed: read() })

export function closeIntro() {
  intro.closed = true
  try {
    localStorage.setItem(KEY, '1')
  } catch {
    // depolama kapalıysa bu oturumda kapalı kalır
  }
}

export function reopenIntro() {
  intro.closed = false
  try {
    localStorage.removeItem(KEY)
  } catch {
    // depolama kapalı olabilir
  }
}
