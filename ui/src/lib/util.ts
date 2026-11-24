import { ApiError } from './errors'
import { hata } from './hata'
import { t } from './i18n/index.svelte'
import { app } from './store.svelte'
import type { DecisionType, Project, SessionState } from './types'

export function parseTs(ts: string | null | undefined): number {
  if (!ts) return NaN
  const n = Date.parse(ts)
  return Number.isFinite(n) ? n : NaN
}

/** "25 dk önce", "3 sa önce", "2 gün önce" */
export function ago(ts: string | null | undefined): string {
  const ms = Date.now() - parseTs(ts)
  if (!Number.isFinite(ms)) return ''
  const min = Math.floor(ms / 60000)
  if (min < 1) return t('justNow')
  if (min < 60) return t('ago', { t: `${min} ${t('min')}` })
  const h = Math.floor(min / 60)
  if (h < 24) return t('ago', { t: `${h} ${t('hour')}` })
  return t('ago', { t: `${Math.floor(h / 24)} ${t('day')}` })
}

export function fmtDate(ts: string | null | undefined, withTime = false): string {
  const n = parseTs(ts)
  if (!Number.isFinite(n)) return ''
  const opts: Intl.DateTimeFormatOptions = withTime
    ? { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }
    : { day: 'numeric', month: 'short', year: 'numeric' }
  return new Intl.DateTimeFormat(app.lang === 'en' ? 'en-GB' : 'tr-TR', opts).format(n)
}

const TOOL_NAMES: Record<string, string> = {
  claude: 'Claude Code',
  'claude-code': 'Claude Code',
  codex: 'Codex',
  antigravity: 'Antigravity',
  vscode: 'VS Code',
  'vs-code': 'VS Code',
  cursor: 'Cursor',
  hermes: 'Hermes',
  opencode: 'OpenCode',
}

export function toolName(tool: string): string {
  const k = (tool ?? '').trim().toLowerCase()
  if (k === 'manual') return app.lang === 'en' ? 'By hand' : 'Elle'
  return TOOL_NAMES[k] ?? tool
}

export function typeLabel(type: DecisionType): string {
  const m: Record<DecisionType, Parameters<typeof t>[0]> = {
    rule: 'typeRule', ban: 'typeBan', decision: 'typeDecision', preference: 'typePreference', correction: 'typeCorrection',
  }
  return t(m[type])
}

export function stateLabel(s: SessionState): string {
  const m = { waiting: 'stateWaiting', stalled: 'stateStalled', working: 'stateWorking', idle: 'stateIdle', stale: 'stateStale' } as const
  return t(m[s])
}

export function isGeneral(p: Pick<Project, 'name' | 'path'>): boolean {
  // Genel (*) proje: sunucuda yolu yoktur (null)
  return !p.path || p.name === '*' || p.path === '*'
}

// isTestProject: saf mantık olarak klasorSecim.ts içinde (node ile sınanır); burada yeniden dışa aktarılır.
export { isTestProject } from './klasorSecim'

/** Düğmenin arkasındaki uç hata verince kullanıcıya teknik ileti değil, ne yapabileceğini söyleyen sade cümle gösterilir. */
export function friendlyActionError(e: unknown, what: 'window' | 'folder' | 'general' = 'general'): string {
  if (e instanceof ApiError && e.status === 0) return t('errNoServer')
  if (what === 'window') return t('errWindowOpen')
  if (what === 'folder') return t('errFolderOpen')
  return t('errActionGeneric')
}

export function projectLabel(p: Pick<Project, 'name' | 'path'>): string {
  return isGeneral(p) ? t('generalProject') : p.name
}

/** Aranan sözcükleri metinde işaretlemek için parçalara böler. */
export function highlightParts(text: string, query: string): { text: string; hit: boolean }[] {
  const terms = query.trim().split(/\s+/).filter(Boolean)
  if (!terms.length) return [{ text, hit: false }]
  const esc = terms.map((x) => x.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
  const re = new RegExp(`(${esc.join('|')})`, 'giu')
  return text.split(re).map((s, i) => ({ text: s, hit: i % 2 === 1 })).filter((x) => x.text !== '')
}

export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text)
    return true
  } catch {
    try {
      const ta = document.createElement('textarea')
      ta.value = text
      ta.setAttribute('readonly', '')
      ta.style.position = 'fixed'
      ta.style.opacity = '0'
      document.body.appendChild(ta)
      ta.select()
      const ok = document.execCommand('copy')
      ta.remove()
      return ok
    } catch {
      return false
    }
  }
}

// ---- Söz 2 ----

/** Hata cümlesi: ham sunucu metni hiçbir zaman ekrana çıkmaz (lib/hata.ts). */
export function errText(e: unknown): string {
  return hata(e).text
}

export function fmtTokens(n: number): string {
  if (n >= 1_000_000) return (n / 1_000_000).toLocaleString(app.lang === 'en' ? 'en-GB' : 'tr-TR', { maximumFractionDigits: 1 }) + ' M'
  if (n >= 1_000) return Math.round(n / 1_000).toLocaleString(app.lang === 'en' ? 'en-GB' : 'tr-TR') + ' B'
  return String(n)
}

export function fmtUsd(n: number | null | undefined): string {
  if (n === null || n === undefined) return t('noPrice')
  return '≈ $' + n.toLocaleString(app.lang === 'en' ? 'en-GB' : 'tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

export function fmtDur(ms: number): string {
  if (ms < 1000) return `${ms} ms`
  return `${(ms / 1000).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} sn`
}

/** Ölçütün özeti (komut, yol, adres) tek satır. */
export function specSummary(kind: string, spec: Record<string, unknown>): string {
  if (kind === 'command') return String(spec.cmd ?? '')
  if (kind === 'file') return String(spec.path ?? '')
  if (kind === 'url') return String(spec.url ?? '')
  if (kind === 'screenshot') return `${spec.url ?? ''} (${spec.width ?? '?'}×${spec.height ?? '?'})`
  return JSON.stringify(spec)
}

/** Kabuk içinde mi (WebView2): "Terminale yapıştır" yalnız orada gösterilir. */
export function inShell(): boolean {
  return typeof window !== 'undefined' && !!(window as unknown as { chrome?: { webview?: unknown } }).chrome?.webview
}

export function pasteToTerminal(text: string) {
  ;(window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome.webview.postMessage({ type: 'paste', text })
}
