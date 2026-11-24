// Masaüstü kabuğunun tercihleri (Windows bildirimi, açılışta başlama, kaldırırken veriyi sil). Kabuk yokken (tarayıcı) hiç dolmaz: anahtarlar görünmez.
export const sp = $state({ known: false, bildirimler: true, kaldirirkenVeriyiSil: false, acilistaBasla: true })

export function applyShellPrefs(m: { bildirimler?: unknown; kaldirirkenVeriyiSil?: unknown; acilistaBasla?: unknown }) {
  if (typeof m.bildirimler === 'boolean') sp.bildirimler = m.bildirimler
  if (typeof m.kaldirirkenVeriyiSil === 'boolean') sp.kaldirirkenVeriyiSil = m.kaldirirkenVeriyiSil
  if (typeof m.acilistaBasla === 'boolean') sp.acilistaBasla = m.acilistaBasla
  sp.known = true
}
