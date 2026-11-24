// Hata sözleşmesi (f1): ekrana ham sunucu ya da sistem metni asla çıkmaz. Aynı hata her ekranda aynı sade cümleyi verir;
// ham metin yalnız "Teknik ayrıntı" düğmesinin altında durur (HataSatiri.svelte).
import { ApiError } from './errors'
const NOT_IN_VERSION = '__bu-surumde-yok__' // api3.ts ile aynı değer (döngüsel içe aktarma olmasın)
import { t } from './i18n/index.svelte'

export type HataBaglam = 'genel' | 'pencere' | 'klasor' | 'ozet'
export interface Hata {
  text: string
  /** Ham metin (yalnız "Teknik ayrıntı" altında gösterilir). Boşsa düğme çıkmaz. */
  detail: string
  status: number
  /** Kart ya da liste bayat: yeniden okunmalı (409 zaten yapılmış, 404). */
  refresh: boolean
  kind: 'ag' | 'oturum' | 'yok' | 'zaten' | 'cakisma' | 'sunucu' | 'yardimci' | 'diger'
}

// Sunucunun kendi yazdığı sade Türkçe cümle mi, yoksa sızan teknik metin mi?
const TEKNIK = /exception|stack ?trace|\bat\s+[\w.$]+\(|\bat\s+[\w$]+\.[\w$]+|enoent|eacces|econn|spawn\b|internal server|object reference|nullreference|undefined|\bnull\b|system\.|\.cs[:\s)]|sqlite|json|unexpected token|syntaxerror|typeerror|referenceerror|cannot read|failed to|not found|bad request|forbidden|conflict|\bat process\b|\bwin32\b|hresult|0x[0-9a-f]{4,}/i
const TURKCE = /[çğıöşüÇĞİÖŞÜ]|\b(bu|bir|ve|için|değil|yok|zaten|iş|işi|klasör|proje|yardımcı|bulun|açık|kapalı|oturum|izin|gerek|seç|yaz|yap|ekle|sil)\w*/i

export function sadeMi(msg: string): boolean {
  const m = msg.trim()
  if (!m || m.length > 220 || /[\r\n]/.test(m)) return false
  if (TEKNIK.test(m)) return false
  return TURKCE.test(m)
}

// Ayar reddinde sunucu iç adı söyler ("attentionDailySignals: 0 ile 20 arası tam sayı olmalı"). Arayüz o adı ekrandaki etiketle değiştirir;
// etiket bulunamazsa genel cümle çıkar, ham metin yalnız "Teknik ayrıntı" altında kalır. Etiketleyiciyi registry.svelte.ts kaydeder.
let alanEtiketi: ((anahtar: string) => string | null) | null = null
export const alanEtiketiKaydet = (f: (anahtar: string) => string | null): void => { alanEtiketi = f }
const IC_AD = /^(?:setting:)?((?:[a-z]+(?:[A-Z][a-z0-9]*)+)|(?:[a-z0-9]+(?:[.-][a-z0-9]+)+)):\s*(.+)$/s
function alanCumlesi(raw: string): string | null | undefined {
  const m = IC_AD.exec(raw.trim())
  if (!m) return undefined // iç ad yok
  const lab = alanEtiketi?.(m[1] ?? '')
  if (!lab) return null // iç ad var ama etiket yok: genel cümle
  const rest = (m[2] ?? '').trim()
  const r = /^(-?\d+(?:[.,]\d+)?) ile (-?\d+(?:[.,]\d+)?) arası (?:tam )?sayı olmalı\.?$/i.exec(rest)
  if (r) return t('errFieldRange', { f: lab, a: r[1] ?? '', b: r[2] ?? '' })
  return `${lab}: ${rest.replace(/\.?$/, '.')}`
}

export function hata(e: unknown, baglam: HataBaglam = 'genel'): Hata {
  const raw = e instanceof Error ? (e.message ?? '') : String(e ?? '')
  const status = e instanceof ApiError ? e.status : -1
  const mk = (text: string, kind: Hata['kind'], refresh = false): Hata => ({ text, detail: raw && raw !== text && raw !== NOT_IN_VERSION ? `${status > 0 ? status + ': ' : ''}${raw}` : '', status, refresh, kind })
  if (!(e instanceof ApiError)) return sadeMi(raw) ? mk(raw.trim(), 'diger') : mk(baglam === 'pencere' ? t('errWindowOpen') : baglam === 'klasor' ? t('errFolderOpen') : t('errActionGeneric'), 'diger')
  if (raw === NOT_IN_VERSION) return mk(t('errNotInVersion'), 'yok')
  if (status === 0) return mk(t('errNoServer'), 'ag')
  if (status === 401 || status === 403) return mk(t('errToken'), 'oturum')
  if (/ENOENT|spawn\s+\w+/i.test(raw) || /kurulu değil|kurulu degil/i.test(raw)) return mk(t('errHelperMissing'), 'yardimci')
  if (status === 404) return mk(baglam === 'ozet' ? t('ozGone') : t('errNotFound'), 'yok', true)
  if (status === 409) {
    if (/zaten/i.test(raw)) return mk(t('errAlreadyDone'), 'zaten', true)
    return mk(sadeMi(raw) ? raw.trim() : t('errConflictGeneric'), 'cakisma', true)
  }
  if (status >= 500) return mk(t('errServerFail'), 'sunucu')
  // 400 ve benzerleri: sunucunun kişiye söylenmiş sade cümlesi olduğu gibi, değilse genel cümle.
  const alan = alanCumlesi(raw)
  if (typeof alan === 'string') return mk(alan, 'diger')
  if (alan === null) return mk(t('errSettingRejected'), 'diger')
  if (sadeMi(raw)) return mk(raw.trim(), 'diger')
  return mk(baglam === 'pencere' ? t('errWindowOpen') : baglam === 'klasor' ? t('errFolderOpen') : t('errActionGeneric'), 'diger')
}

export const hataMetni = (e: unknown, baglam: HataBaglam = 'genel'): string => hata(e, baglam).text

/** Arayüzün kendi sade cümlesinden hata satırı (sunucu cevabı yok). */
export const kendiHata = (text: string): Hata => ({ text, detail: '', status: -1, refresh: false, kind: 'diger' })
