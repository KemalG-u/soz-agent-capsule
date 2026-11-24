// Oturum kartının ek alanları (stateSource, stateConfidence, endClass) için sade etiketler. İç teknik gevezelik yüzeye çıkmaz.
import { t } from './i18n/index.svelte'

export function sourceLabel(s: string | null | undefined): string | null {
  if (s === 'log' || s === 'process') return t('srcLog')
  if (s === 'hook') return t('srcHook')
  if (s === 'protocol') return t('srcProtocol')
  return null
}

export function confidenceLabel(c: string | null | undefined): { text: string; soft: boolean } | null {
  if (c === 'unknown') return { text: t('confUnknown'), soft: true }
  return null
}

export function endLabel(e: string | null | undefined): string | null {
  if (e === 'awaiting_user') return t('endAwaiting')
  if (e === 'tool_call_pending') return t('endToolPending')
  if (e === 'truncated') return t('endTruncated')
  return null
}
