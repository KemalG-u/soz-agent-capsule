// f7: gezinme, Esc ve görünürlük kararları. Saf işlevler: DOM ve Svelte bilmez, scripts/f7.test.mjs ile sınanır.
// Kaynak bulgular: _gecici/u1/TASARIM.md A1, O1, O2, O6, D8, D10, O12.

/** Pencere dar mı (liste ve ayrıntı tek sütunda)? Sunucu tarafında (testte) false. */
export const DAR_SORGU = '(max-width: 759px)'
export const dar = (): boolean => typeof matchMedia !== 'undefined' && matchMedia(DAR_SORGU).matches

/** Rail'deki sabit en üst ekranlar: burada Esc hiçbir şey yapmaz. */
const UST_EKRAN = ['suan', 'projelerim', 'ayarlar']
/** Ayarlar > Panel Düzeni ile alt şeride eklenebilen sekmeler: şeritteyse en üst ekran sayılır. */
const ISTEGE_BAGLI = ['bekci', 'defter', 'neoldu', 'harcama', 'ara']
/** Alt sayfa ile bir üst ekranı. */
const UST: Record<string, string> = { sira: 'suan', taniyorum: 'ayarlar', sabah: 'neoldu', oneriler: 'defter' }

export type EscKarari =
  | { tur: 'yok' } // en üst ekran ya da başka biri (pencere, kutu) zaten ilgileniyor
  | { tur: 'durdurKapat' } // "Durdurayım mı?" sorusu açık: yalnız soru kapanır
  | { tur: 'liste' } // dar pencerede ayrıntıdan listeye
  | { tur: 'listeOdak' } // geniş pencerede liste ile ayrıntı yan yana: odak listeye
  | { tur: 'git'; hedef: string } // bir üst ekrana

export interface EscDurum {
  /** Açık pencere (dialog), kısayol listesi, komut paleti ya da açılır menü var. */
  katmanAcik: boolean
  /** "Durdurayım mı?" sorusu açık. */
  durdurSorusu: boolean
  /** Odak bir metin kutusunda: kutuların kendi Esc davranışı var. */
  yaziyor: boolean
  route: string
  sub: string
  dar: boolean
  gorunum: 'list' | 'detail'
  /** Klasör kurulum kartı açık (dar pencerede ayrıntı alanında durur). */
  kurulum: boolean
  /** Alt şeritte görünen isteğe bağlı sekmeler. */
  navTabs: readonly string[]
}

/**
 * Esc ne yapar: açık pencere varken yalnız o kapanır (pencere kendisi yapar, burası karışmaz),
 * alt sayfada boştayken bir üst ekrana döner, en üst ekranda hiçbir şey yapmaz.
 */
export function escKarari(d: EscDurum): EscKarari {
  if (d.katmanAcik) return { tur: 'yok' }
  if (d.durdurSorusu) return { tur: 'durdurKapat' }
  if (d.yaziyor) return { tur: 'yok' }
  const r = d.route
  if (r === 'is') {
    if (!/^\d+$/.test(d.sub)) return { tur: 'yok' }
    if (d.dar) return d.gorunum === 'detail' && !d.kurulum ? { tur: 'liste' } : { tur: 'yok' }
    return { tur: 'listeOdak' }
  }
  if (UST_EKRAN.includes(r)) return { tur: 'yok' }
  const sekmede = (x: string) => d.navTabs.includes(x)
  if (ISTEGE_BAGLI.includes(r)) {
    if (d.sub) return { tur: 'git', hedef: sekmede(r) ? r : 'suan' }
    return sekmede(r) ? { tur: 'yok' } : { tur: 'git', hedef: 'suan' }
  }
  const ust = UST[r]
  if (ust) return { tur: 'git', hedef: ISTEGE_BAGLI.includes(ust) && !sekmede(ust) ? 'suan' : ust }
  return { tur: 'git', hedef: 'suan' }
}

export type GecmisKarari = 'push' | 'replace' | 'yok'

/**
 * İş seçilince adres nasıl değişir (O1): listeden ayrıntıya ilk geçiş geçmişe yazılır (Geri listeye döner),
 * ayrıntıdan ayrıntıya geçiş yerinde değişir (her seçim Geri'ye yeni satır eklemez).
 * Kendiliğinden seçim (sayfa açılınca ilk iş) geçmişe yazılmaz: dar pencerede adres hiç değişmez, genişte yerinde değişir.
 */
export function gecmisKarari(d: { route: string; sub: string; id: number | null; oto: boolean; dar: boolean }): GecmisKarari {
  if (d.route !== 'is' || d.id === null || !(d.id > 0)) return 'yok'
  if (d.sub === String(d.id)) return 'yok'
  if (d.oto) return d.dar ? 'yok' : 'replace'
  return /^\d+$/.test(d.sub) ? 'replace' : 'push'
}

/** Dar pencerede adres görünümü söyler: #/is liste, #/is/<no> ayrıntı. */
export const darGorunum = (sub: string): 'list' | 'detail' => (/^\d+$/.test(sub) ? 'detail' : 'list')

/**
 * Geri gezinme (tarayıcı Geri, fare yan tuşu, Alt+Sol) bir pencere açıkken gelirse yalnız pencere kapanır (O2).
 * Hedef kaydın damgası şimdikinden küçükse bu bir geri adımıdır.
 */
export function geriAdimiMi(hedefDamga: unknown, suankiDamga: number): boolean {
  return typeof hedefDamga === 'number' && Number.isFinite(hedefDamga) && hedefDamga < suankiDamga
}

/**
 * "Geri al" düğmesi görünür mü (O6, D10): koruma geri almaya izin verir, iş şu an çalışmıyor ve klasörde geri alınacak değişiklik var.
 */
export function geriAlGorunur(d: { canUndo: boolean; calisiyor: boolean; degisiklikYok: boolean }): boolean {
  return d.canUndo && !d.calisiyor && !d.degisiklikYok
}

/** Yeşil "Geri alma noktası var" notu: değişiklik yoksa söylenmez (kişi geri alacak bir şey var sanmasın). */
export function korumaNotuGorunur(d: { level: string; degisiklikYok: boolean }): boolean {
  return !(d.degisiklikYok && d.level !== 'yok')
}

/**
 * "Seni bekleyenler" sayısı (D8): alt şerit, Şu An ve İşler listesi aynı sayıyı söyler (sunucunun bekleyenler listesi).
 * Liste daha az satır gösteriyorsa farkı "N tane daha Şu An sayfasında" der. Bekleyenler henüz okunmadıysa listedeki sayı.
 */
export function bekleyenSayaci(d: { bekleyenYuklendi: boolean; bekleyenToplam: number; listedeki: number; dahaVar: boolean }): { metin: string; sayi: number; fazla: number } {
  if (!d.bekleyenYuklendi) {
    const metin = d.dahaVar && d.listedeki > 0 ? `${d.listedeki}+` : String(d.listedeki)
    return { metin, sayi: d.listedeki, fazla: 0 }
  }
  const sayi = Math.max(d.bekleyenToplam, 0)
  return { metin: String(sayi), sayi, fazla: Math.max(0, sayi - d.listedeki) }
}

/** İş adını bildirim için kısaltır. */
export function kisaAd(s: string, en = 40): string {
  const t = s.replace(/\s+/g, ' ').trim()
  return t.length > en ? t.slice(0, en - 1).trimEnd() + '…' : t
}

/** "Az önce verdiğin" satırı ne kadar görünür: iş sürüyorsa hep, bittiyse verildikten sonra 30 dakika. */
export function azOnceGorunur(d: { verildi: number; simdi: number; suruyor: boolean }): boolean {
  if (!Number.isFinite(d.verildi)) return false
  return d.suruyor || d.simdi - d.verildi < 30 * 60 * 1000
}

/** Kod sözcüğü yerine sade ad (O12): "(npm test)" gibi test komutları kişinin diliyle söylenir. */
const TEST_KOMUTU = /\s*\((?:npm|pnpm|yarn|bun)(?: run)? test[^)]*\)|\s*\((?:dotnet test|pytest|go test|cargo test|mvn test|gradle test|python -m pytest)[^)]*\)/gi
export function sadeKomut(s: string, ifade: string): string {
  return s.replace(TEST_KOMUTU, ` (${ifade})`).replace(/\b(?:npm|pnpm|yarn) test\b/gi, ifade)
}

/**
 * Yardımcının durumu tek kaynaktan (O10): Ayarlar ve Şu An aynı soruya aynı cevabı verir.
 * Hazırlık bilgisi (GET /api/agents/ready) varsa o esastır, yoksa yalnız "bulundu mu".
 */
export function yardimciDurumu(found: boolean, hazirlik: { found?: boolean; canStart: boolean } | null | undefined): 'hazir' | 'hazirDegil' | 'yok' {
  if (hazirlik) {
    if (hazirlik.found === false && !found) return 'yok'
    return hazirlik.canStart ? 'hazir' : 'hazirDegil'
  }
  return found ? 'hazir' : 'yok'
}

/** Komut bir klasörün kendi test komutu mu (O12: ekranda "Klasörün kendi testleri" yazılır, komut ipucunda kalır). */
export const testKomutuMu = (s: string): boolean => /^\s*(?:(?:npm|pnpm|yarn|bun)(?: run)? test|dotnet test|pytest|python -m pytest|go test|cargo test|mvn test|gradle test)\b/i.test(s)

/** Olumsuz sonuç mu (O6): kişiye "Yardımcıya yeniden yaptır" önerilir. */
export const olumsuzSonuc = (bucket: string | null): boolean => bucket === 'nowork' || bucket === 'failed'
