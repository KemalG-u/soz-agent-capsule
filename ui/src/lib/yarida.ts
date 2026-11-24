// "Yarıda kaldı" kartının saf yardımcıları (i18n içe almaz, node --test ile denenir).
/** Sunucunun cümleleri: "Yardımcı işi yarıda bırakıp durdu.", eski "Yardımcı hata ile bitti: ...", "çalışırken". */
export const YARIDA_RE = /hata ile bitti|çalışırken|yarıda/i
export const yaridaMetni = (s: string | null | undefined): boolean => YARIDA_RE.test(s ?? '')

/** Hata ayarla ilgiliyse (yardımcı bulunamadı, giriş yapılmamış, yol yanlış) Ayarlar yardım eder; başka hatada etmez. */
const AYAR_RE = /bulunamad|kurulu değil|giriş yap|oturum aç|yetkilendir|kimlik doğrula|api key|anahtar|login|not logged|unauthori[sz]ed|not found|enoent|yolu geçersiz|çalıştırılamad/i
export const ayarIlgili = (...parts: (string | null | undefined)[]): boolean => AYAR_RE.test(parts.filter(Boolean).join(' '))

/** Eski kayıtta ham hata cümlenin içindeydi ("Yardımcı hata ile bitti: boom"): sade cümle ile ham metin ayrılır. */
export function hamAyir(text: string, detail: string | null | undefined): { text: string; detail: string | null } {
  const m = /^Yardımcı hata ile bitti:\s*([\s\S]+)$/.exec(text.trim())
  if (!m) return { text, detail: detail ?? null }
  return { text: 'Yardımcı işi yarıda bırakıp durdu.', detail: [m[1], detail].filter(Boolean).join('\n') }
}

/** Şu An altındaki "bazı yardımcılar görünmüyor" notu: sunucu tam cümle söylüyor, önüne ikinci giriş eklenmez. Birden çok not ayrı kısa cümle olur (noktalı virgül yok). */
export const notCumleleri = (notlar: string[], en = 2): string =>
  notlar.map((x) => x.trim()).filter(Boolean).slice(0, en).map((x) => (/[.!?…]$/.test(x) ? x : x + '.')).join(' ')

/** Söz kapandığı için duran iş (düzgün kapanış: "Söz kapandığı için iş durduruldu."; çökme ya da zorla kapanış: "yarım kaldı"). Kişi durdurmadı. */
export const SOZ_KAPANDI_RE = /Söz kapandığı|^\s*yarım kaldı\s*$/i
export const sozKapandiMi = (lastLine: string | null | undefined): boolean => SOZ_KAPANDI_RE.test(lastLine ?? '')
/** Aynı olayın akıştaki satırı (kart zaten söylediği için akışta tekrarlanmaz). */
export const SOZ_KAPANDI_SATIRI_RE = /^Söz kapan(?:mıştı|dığı için iş durduruldu)/i
