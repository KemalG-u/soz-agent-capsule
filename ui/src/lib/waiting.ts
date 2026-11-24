// b7: yardımcının "seni bekliyorum" derken yazdığı ham, çoğu İngilizce metni (ör. "approve Edit: hesap.js") sade Türkçe cümleye çevirir.
// Bu dosya saf işlevdir (içe aktarma yok): sınaması `scripts/waiting-test.mjs` (Node) içinde koşar.
// Kural: bilinen kalıp sade cümle olur; komut ve tam yol küçük ikincil satıra iner; tanınmayan metin AYNEN, "yardımcının kendi sözü" diye ayrı gösterilir.
// Silme ve geri alınamaz görünen işler `danger` ile işaretlenir; arayüz bunları daha dikkat çekici çizer.

export type WaitKind = 'edit' | 'write' | 'run' | 'read' | 'search' | 'net' | 'delete' | 'agent' | 'tool' | 'permission' | 'plain' | 'other'

export interface WaitInfo {
  /** Kalıp tanındı mı (false: ham metin `quote` olarak gösterilir) */
  recognized: boolean
  kind: WaitKind
  /** Tek sade cümle, yardımcı adıyla başlar. Boş metinde null. */
  sentence: string | null
  /** Küçük ikincil satır: komut, tam dosya yolu, adres. */
  target: string | null
  /** Tanınmayan ham metin (yardımcının kendi sözü). Tanınan kalıpta null. */
  quote: string | null
  /** Silme, zorla yazma gibi geri alınamayabilecek iş. */
  danger: boolean
}

type Lang = 'tr' | 'en'

const TXT = {
  tr: {
    someone: 'Yardımcı',
    edit: (f: string) => `${f} dosyasını değiştirmek için izin istiyor`,
    editAny: 'bir dosyayı değiştirmek için izin istiyor',
    write: (f: string) => `${f} dosyasını yazmak için izin istiyor`,
    writeAny: 'bir dosya yazmak için izin istiyor',
    read: (f: string) => `${f} dosyasını okumak için izin istiyor`,
    readAny: 'bir dosyayı okumak için izin istiyor',
    search: 'dosyalarda arama yapmak için izin istiyor',
    run: 'bir komut çalıştırmak için izin istiyor',
    net: 'internete bağlanmak için izin istiyor',
    del: (f: string) => `${f} silmek için izin istiyor`,
    delAny: 'bir şeyi silmek için izin istiyor',
    agent: 'başka bir yardımcıyı çalıştırmak için izin istiyor',
    tool: 'ek bir araç kullanmak için izin istiyor',
    permission: 'bir işlem için izin istiyor',
    plain: 'senden bir cevap bekliyor',
    other: 'senden bir şey bekliyor',
    withWhat: (w: string) => `${w} için izin istiyor`,
    danger: 'Dikkat: bu geri alınamayabilir.',
    ownWords: 'Yardımcının kendi sözü',
  },
  en: {
    someone: 'The helper',
    edit: (f: string) => `wants permission to change the file ${f}`,
    editAny: 'wants permission to change a file',
    write: (f: string) => `wants permission to write the file ${f}`,
    writeAny: 'wants permission to write a file',
    read: (f: string) => `wants permission to read the file ${f}`,
    readAny: 'wants permission to read a file',
    search: 'wants permission to search your files',
    run: 'wants permission to run a command',
    net: 'wants permission to connect to the internet',
    del: (f: string) => `wants permission to delete ${f}`,
    delAny: 'wants permission to delete something',
    agent: 'wants permission to start another helper',
    tool: 'wants permission to use an extra tool',
    permission: 'wants permission to do something',
    plain: 'is waiting for your answer',
    other: 'is waiting for something from you',
    withWhat: (w: string) => `wants permission: ${w}`,
    danger: 'Careful: this may not be undoable.',
    ownWords: 'The helper’s own words',
  },
} as const

// Geri alınamayabilecek işler. Komutta ve tanınmayan metinde aranır; dosya adında aranmaz.
const DANGER_RE = new RegExp(
  [
    String.raw`\brm\b`, String.raw`\brmdir\b`, String.raw`\bdel\b`, String.raw`\berase\b`, String.raw`remove-item`, String.raw`\bunlink\b`,
    String.raw`\bdelete\b`, String.raw`\bremove\b`, String.raw`\bdestroy\b`, String.raw`--force\b`, String.raw`\s-f\b`, String.raw`--hard\b`, String.raw`\bforce\b`,
    String.raw`\bdrop\s+(table|database|schema)\b`, String.raw`\btruncate\b`, String.raw`\bformat(?:-volume)?\s+[a-z]:`, String.raw`\bmkfs\b`, String.raw`\bshutdown\b`, String.raw`\bwipe\b`,
    String.raw`\bsil(mek|me|ecek|in|iyor)?\b`, String.raw`silinecek`, String.raw`kalıcı olarak`,
  ].join('|'),
  'i',
)
export const looksDestructive = (s: string | null | undefined): boolean => !!s && DANGER_RE.test(s)

const KINDS: Record<string, WaitKind> = {
  edit: 'edit', multiedit: 'edit', notebookedit: 'edit', replace: 'edit', patch: 'edit', apply_patch: 'edit', str_replace: 'edit', strreplace: 'edit',
  write: 'write', create: 'write', writefile: 'write', createfile: 'write',
  bash: 'run', shell: 'run', powershell: 'run', pwsh: 'run', cmd: 'run', command: 'run', exec: 'run', terminal: 'run', run: 'run', sh: 'run',
  read: 'read', view: 'read', cat: 'read', open: 'read', readfile: 'read',
  glob: 'search', grep: 'search', ls: 'search', list: 'search', search: 'search', find: 'search',
  webfetch: 'net', websearch: 'net', fetch: 'net', http: 'net', curl: 'net', browser: 'net', web: 'net',
  delete: 'delete', remove: 'delete', rm: 'delete', unlink: 'delete', deletefile: 'delete',
  task: 'agent', agent: 'agent', subagent: 'agent',
}

const clean = (s: string | null | undefined): string => (s ?? '').replace(/[\u0000-\u001f\u007f]+/g, ' ').replace(/\s+/g, ' ').trim()
const baseName = (p: string): string => {
  const parts = p.split(/[\\/]/).filter(Boolean)
  return parts.length ? parts[parts.length - 1]! : p
}
const looksPath = (s: string): boolean => /[\\/]/.test(s) && !/^https?:\/\//i.test(s)

// "approve Edit: hesap.js", "Approve Bash: npm test", "allow Read hesap.js", "permission Edit - hesap.js", "Claude needs your permission to use Bash"
const VERB = String.raw`(?:approve|approval|allow|permit|permission(?:\s+to(?:\s+use)?)?|needs?\s+(?:your\s+)?(?:permission|approval)(?:\s+to\s+use)?)`
const RE_TOOL = new RegExp(String.raw`^\s*(?:[\w.-]+\s+)?${VERB}\s*(?:for\s+|to\s+use\s+)?([A-Za-z_][\w.-]*)\s*(?:[:\-–—]\s*|\(\s*)?(.*?)\)?\s*\??\s*$`, 'i')
const RE_TR_PERM = /^\s*(?:[iİ]zin\s+istiyor|[iİ]zin\s+bekliyor|onay\s+istiyor|onay\s+bekliyor)\s*[:\-–—]\s*(.+)$/i
const RE_TR_PLAIN = /^\s*(?:senden|sizden)\s+(?:bir\s+)?(?:cevap|yanıt|şey|bilgi|karar)(?:\s+ya\s+da\s+(?:izin|onay))?\s+bekliyor\.?\s*$|^\s*(?:waiting\s+(?:for|on)\s+(?:you|input|your\s+(?:answer|input|reply))|needs?\s+(?:your\s+)?(?:input|answer|reply))\.?\s*$/i

/**
 * @param raw yardımcının ham metni (waitingFor ya da soru/izin ayrıntısı)
 * @param helper yardımcının adı ("Claude Code"); verilmezse "Yardımcı"
 */
export function describeWaiting(raw: string | null | undefined, helper?: string | null, lang: Lang = 'tr'): WaitInfo {
  const L = TXT[lang]
  const text = clean(raw)
  const who = clean(helper) || L.someone
  const say = (rest: string) => `${who} ${rest}.`
  if (!text) return { recognized: false, kind: 'other', sentence: null, target: null, quote: null, danger: false }

  if (RE_TR_PLAIN.test(text)) return { recognized: true, kind: 'plain', sentence: say(L.plain), target: null, quote: null, danger: false }

  const trPerm = RE_TR_PERM.exec(text)
  if (trPerm) {
    const what = trPerm[1]!.trim().replace(/\.$/, '')
    const danger = looksDestructive(what)
    if (/(mek|mak)$/i.test(what)) return { recognized: true, kind: danger ? 'delete' : 'permission', sentence: say(L.withWhat(what)), target: null, quote: null, danger }
    return { recognized: true, kind: danger ? 'delete' : 'permission', sentence: say(L.permission), target: what, quote: null, danger }
  }

  const m = RE_TOOL.exec(text)
  if (m) {
    const tool = m[1]!
    const arg = (m[2] ?? '').trim()
    const key = tool.toLowerCase().replace(/[^a-z_]/g, '')
    const kind: WaitKind | undefined = KINDS[key] ?? (tool.toLowerCase().startsWith('mcp__') ? 'tool' : undefined)
    if (!kind) {
      // "approve" kalıbı tanındı ama araç adı bilinmiyor: sade "izin istiyor", ham metin yardımcının sözü olarak durur.
      return { recognized: false, kind: 'permission', sentence: say(L.permission), target: null, quote: text, danger: looksDestructive(arg) || looksDestructive(text) }
    }
    const file = arg ? (looksPath(arg) ? baseName(arg) : arg) : ''
    const fullPath = arg && looksPath(arg) ? arg : null
    switch (kind) {
      case 'edit': return { recognized: true, kind, sentence: say(file ? L.edit(file) : L.editAny), target: fullPath, quote: null, danger: false }
      case 'write': return { recognized: true, kind, sentence: say(file ? L.write(file) : L.writeAny), target: fullPath, quote: null, danger: false }
      case 'read': return { recognized: true, kind, sentence: say(file ? L.read(file) : L.readAny), target: fullPath, quote: null, danger: false }
      case 'search': return { recognized: true, kind, sentence: say(L.search), target: arg || null, quote: null, danger: false }
      case 'run': return { recognized: true, kind, sentence: say(L.run), target: arg || null, quote: null, danger: looksDestructive(arg) }
      case 'net': return { recognized: true, kind, sentence: say(L.net), target: arg || null, quote: null, danger: false }
      case 'delete': return { recognized: true, kind, sentence: say(file ? L.del(file) : L.delAny), target: fullPath, quote: null, danger: true }
      case 'agent': return { recognized: true, kind, sentence: say(L.agent), target: arg || null, quote: null, danger: false }
      default: return { recognized: true, kind: 'tool', sentence: say(L.tool), target: arg || null, quote: null, danger: looksDestructive(arg) }
    }
  }

  // Tanınmayan metin: olduğu gibi, yardımcının kendi sözü olarak. Yıkıcı görünüyorsa yine uyarılır.
  return { recognized: false, kind: 'other', sentence: null, target: null, quote: text.length > 240 ? text.slice(0, 237) + '...' : text, danger: looksDestructive(text) }
}
