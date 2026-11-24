// Sunucudan gelen cümlelerdeki teknik sözcükleri ekranda sade söyler (sunucu metni ve testleri değişmez).
// Kural: "ajan" yerine "yardımcı", "kanıt" yerine "kontrol", "ölçüt" yerine "kontrol", "bekçi" yerine "koruma".
const KURAL: [RegExp, string][] = [
  [/--?allowedTools bayrağına/g, 'izin verilen araç listesine'],
  [/--?allowedTools/g, 'izin verilen araç listesi'],
  [/kanıtlayamadı/g, 'deneyemedi'],
  [/kanıtlanamadı/g, 'kontrol edilemedi'],
  [/kanıtlanmadı/g, 'denenmedi'],
  [/Kanıtlanmadı/g, 'Denenmedi'],
  [/kanıtsız/g, 'denenmeden'],
  [/Kanıtsız/g, 'Denenmedi'],
  // "kanıt kontrolü" zaten kontrol demektir: genel kanıt→kontrol değişimi "kontrol kontrolü" üretmesin (f4, Kusur 2).
  [/iş sonu kanıt kontrolü/g, 'iş sonu kontrolü'],
  [/kanıt kontrolü/g, 'kontrol'],
  [/Kanıt kontrolü/g, 'Kontrol'],
  [/kanıtıyla/g, 'kontrolle'],
  [/kanıtı/g, 'kontrolü'],
  [/Kanıtı/g, 'Kontrolü'],
  [/kanıt/g, 'kontrol'],
  [/Kanıt/g, 'Kontrol'],
  [/ölçütü/g, 'kontrolü'],
  [/ölçüt/g, 'kontrol'],
  [/ajanının/g, 'yardımcısının'],
  [/Ajanı /g, 'Yardımcıyı '],
  [/ajanı/g, 'yardımcısı'],
  [/ajanın/g, 'yardımcının'],
  [/ajana/g, 'yardımcıya'],
  [/ajanlar/g, 'yardımcılar'],
  [/ajan/g, 'yardımcı'],
  [/Ajan/g, 'Yardımcı'],
  [/bekçi/g, 'koruma'],
  [/Bekçi/g, 'Koruma'],
  [/turunu bitirdi/g, 'işini bitirdi'],
  // Kod okumayan kişi "oturum", "komut satırı", "agy" sözcüklerini bilmez (S3-18).
  [/Antigravity komut satırı aracını \(agy\)/g, 'Antigravity’yi'],
  [/Antigravity komut satırı aracını/g, 'Antigravity’yi'],
  [/komut satırı aracını/g, 'aracı'],
  [/ \(agy\)/g, ''],
  [/oturum kayıtlarını/g, 'çalışma kayıtlarını'],
  [/oturum özetlerini/g, 'çalışma özetlerini'],
  [/oturum özetini/g, 'çalışma özetini'],
  [/oturum kaydı/g, 'çalışma kaydı'],
  [/oturumları/g, 'çalışmaları'],
  [/oturumu/g, 'çalışmayı'],
  [/oturum/g, 'çalışma'],
  [/Oturumları/g, 'Çalışmaları'],
  [/Oturum özet/g, 'Çalışma özet'],
  [/Oturum/g, 'Çalışma'],
  // f6: başarısız anlamındaki "kaldı / kalıyor" yerine "geçmedi / geçmiyor" (eski kayıtlarda duran cümleler için). "Yarıda kaldı", "kaldığın yerden" gibi başka anlamlara dokunmaz.
  [/(kontrolden \d+ tanesi) kaldı/g, '$1 geçmedi'],
  [/(testten \d+['’]\p{L}+) kalıyordu/gu, '$1 geçmiyordu'],
  [/(testten \d+['’]\p{L}+) kalıyor(?![\p{L}])/gu, '$1 geçmiyor'],
  [/(Başlamadan önce kontroller|Şimdi kontroller) kalıyordu/g, '$1 geçmiyordu'],
  [/(Başlamadan önce kontroller|Şimdi kontroller) kalıyor(?![\p{L}])/gu, '$1 geçmiyor'],
  [/iş yapılmadan önce kalıyordu/g, 'iş yapılmadan önce geçmiyordu'],
  [/\[Söz\] Kaldı:/g, '[Söz] Geçmedi:'],
]
/** Değişimlerin ürettiği sözcük ardışık iki kez çıkarsa ("kontrol kontrolü", "deneme sonucu sonucu") tek bırakır. Yalnız bu sözcükler: "komut komut" gibi doğru yinelemelere dokunmaz. */
const CIFT = /(?<![\p{L}])(kontrol|yardımcı|koruma|çalışma|sonucu)( \1\p{L}{0,3})(?![\p{L}])/giu
export function sade(s: string | null | undefined): string {
  if (!s) return s ?? ''
  let o = s
  for (const [rx, to] of KURAL) o = o.replace(rx, to)
  return o.replace(CIFT, '$1')
}
const ANAHTAR = new Set(['sentence', 'detail', 'note', 'summary', 'sozNotu'])
/** Nesne ağacında yalnız cümle alanlarını (sentence, detail, note, summary) sadeleştirir; yerinde değiştirir. */
export function sadeAgac<T>(x: T, derin = 0): T {
  if (!x || typeof x !== 'object' || derin > 6) return x
  if (Array.isArray(x)) { for (const y of x) sadeAgac(y, derin + 1); return x }
  const o = x as Record<string, unknown>
  for (const k of Object.keys(o)) {
    const v = o[k]
    if (typeof v === 'string' && ANAHTAR.has(k)) o[k] = sade(v)
    else if (v && typeof v === 'object') sadeAgac(v, derin + 1)
  }
  return x
}
