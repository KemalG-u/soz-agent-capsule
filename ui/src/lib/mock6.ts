// Örnek kip (?ornek=1), u3 uçları: iş öncesi özet, kapsam, testsiz klasör, ajan hazırlığı, sabah (yeni alanlar), ajanın yazdığı deneme önerisi, öneriler.
// Cevap biçimleri GERÇEK sunucu cevaplarıyla aynıdır (bkz. _gecici/API-6.md). Bütün isim ve cümleler UYDURMADIR.
import { ApiError } from './errors'
import { emptyMode, HOUR, iso, projects } from './mock'
import { mock2Handle, mock2State, NOT_HANDLED } from './mock2'
import type { Check, Job, Morning } from './types'

const pname = (id: number | null | undefined) => projects.find((p) => p.id === id)?.name ?? null

/** Klasörde kalıcı (işe bağlı olmayan) açık ölçütler. */
function permanent(projectId: number): Check[] {
  return mock2State().checks.filter((c) => c.projectId === projectId && c.jobId === null && c.enabled && c.origin !== 'agent-test')
}

let dismissed = new Set<string>()
let seeded = false
/** Bir kez: bitmiş bir örnek işe "ajanın yazdığı deneme" önerisi ve onun kapalı ölçütü eklenir. */
function seed() {
  if (seeded) return
  seeded = true
  const st = mock2State()
  const job = st.jobs.find((j) => j.state === 'gecti')
  if (!job) return
  const id = 9001
  st.checks.push({ id, projectId: job.projectId, jobId: null, kind: 'command', label: 'Ajanın yazdığı deneme geçsin (npm test)', spec: { cmd: 'npm test', expectExit: 0 }, timeoutS: 120, enabled: false, origin: 'agent-test', createdAt: iso(2 * HOUR) })
  const strongNote = 'Bu deneme iş yapılmadan önce geçmiyordu, şimdi geçiyor. Bir şey kanıtlar.'
  ;(job as Job).suggestedChecks = [{ checkId: id, label: 'Ajanın yazdığı deneme geçsin (npm test)', cmd: 'npm test', level: 'C', levelLabel: 'ajan beyanı', sentence: 'Ajan bir deneme yazdı: npm test. ' + strongNote, strength: 'strong', strengthNote: strongNote, defaultAnswer: 'evet', rejected: false }]
  // zayıf ve kabul edilemez örnekler: başka iki bitmiş işte
  const more = st.jobs.filter((j) => j.state === 'gecti' && j.id !== job.id)
  const weakNote = 'Bu deneme iş yapılmadan önce de geçiyordu. Bir şey kanıtlamaz.'
  const alwaysNote = 'Bu komut hangi sonucu alırsa alsın başarılı bitiyor (|| true, ; exit 0 gibi). Bir şey kanıtlamaz, kabul edilemez.'
  if (more[0]) {
    st.checks.push({ id: 9002, projectId: more[0].projectId, jobId: null, kind: 'command', label: 'Ajanın yazdığı deneme geçsin (npm test)', spec: { cmd: 'npm test', expectExit: 0 }, timeoutS: 120, enabled: false, origin: 'agent-test', createdAt: iso(2 * HOUR) })
    ;(more[0] as Job).suggestedChecks = [{ checkId: 9002, label: 'Ajanın yazdığı deneme geçsin (npm test)', cmd: 'npm test', level: 'C', levelLabel: 'ajan beyanı', sentence: 'Ajan bir deneme yazdı: npm test. ' + weakNote, strength: 'weak', strengthNote: weakNote, defaultAnswer: 'hayir', rejected: false }]
  }
  if (more[1]) {
    st.checks.push({ id: 9003, projectId: more[1].projectId, jobId: null, kind: 'command', label: 'Ajanın yazdığı deneme geçsin (npm test || true)', spec: { cmd: 'npm test || true', expectExit: 0 }, timeoutS: 120, enabled: false, origin: 'agent-test', createdAt: iso(2 * HOUR) })
    ;(more[1] as Job).suggestedChecks = [{ checkId: 9003, label: 'Ajanın yazdığı deneme geçsin (npm test || true)', cmd: 'npm test || true', level: 'C', levelLabel: 'ajan beyanı', sentence: 'Ajan bir deneme yazdı: npm test || true. ' + alwaysNote, strength: 'always', strengthNote: alwaysNote, defaultAnswer: 'hayir', rejected: true }]
  }
}

const SUGGESTIONS = [
  { objectUid: 'setting:ui.theme', title: 'Tema', kindDir: 7, score: 0.62, reasonCode: 'project-usage', reason: 'Bu klasörde bunu sık açtın.' },
  { objectUid: 'connector:hermes', title: 'Hermes Agent', kindDir: 1, score: 0.41, reasonCode: 'recent', reason: 'Bilgisayarında bu araca ait bir kayıt klasörü var.' },
]

export async function mock6Handle(method: string, p: string, q: URLSearchParams, b0: unknown): Promise<unknown> {
  const b = (b0 ?? {}) as Record<string, unknown>
  seed()

  if (p === '/api/done/summary' && method === 'GET') {
    const pid = Number(q.get('projectId'))
    if (!Number.isFinite(pid)) throw new ApiError(400, 'projectId gerekli.')
    const perm = permanent(pid)
    const given = mock2State().jobs.filter((j) => j.projectId === pid).length
    const first = given === 0
    const needsFullCard = first || perm.length === 0
    const names = perm.map((c) => c.label.replace(/ \(.*\)$/, ''))
    return {
      projectId: pid, jobId: null, jobsGiven: given, firstJob: first, permanentCount: perm.length, measurableCount: perm.filter((c) => c.kind === 'command' || c.kind === 'url').length,
      permanent: perm.map((c) => ({ id: c.id, label: c.label, kind: c.kind, level: 'A', levelLabel: 'ölçüldü', who: 'Söz ölçer' })),
      needsFullCard, reason: first ? 'first_job' : perm.length === 0 ? 'no_checks' : 'has_checks',
      line: first ? 'Bu klasörde ilk işin: ajan "bitti" derse neye bakacağımızı birlikte seçelim.' : perm.length === 0 ? 'Bu klasörde henüz sabit bir ölçüt yok.' : `${perm.length} ölçütle denenecek: ${names.join(', ')}.`,
      agentSuggested: mock2State().checks.filter((c) => c.projectId === pid && c.origin === 'agent-test' && !c.enabled).map((c) => ({ checkId: c.id, label: c.label, cmd: String(c.spec.cmd ?? ''), level: 'C', levelLabel: 'ajan beyanı', sentence: 'Ajan bir deneme yazdı.' })),
    }
  }

  if (p === '/api/done/accept' && method === 'POST') {
    const scope = b.scope === 'job' || b.scope === 'project' ? (b.scope as 'job' | 'project') : null
    if (scope === 'job' && (b.jobId === undefined || b.jobId === null)) throw new ApiError(400, 'scope "job" için jobId gerekli.')
    const items = (Array.isArray(b.items) ? b.items : []) as { template: string; values: Record<string, string>; enabled: boolean }[]
    const created: unknown[] = []
    for (const it of items) {
      const kind = it.template === 'tests' ? 'command' : it.template === 'diff-only' ? 'diff-summary' : 'human-checklist'
      const label = it.template === 'tests' ? 'Testler geçsin (npm test)' : it.template === 'diff-only' ? `Yalnız şu dosyalar değişsin: ${it.values.allowed ?? '**'}` : 'Ben şunlara bakıp onaylayacağım'
      const spec = it.template === 'tests' ? { cmd: 'npm test', expectExit: 0 } : { ...it.values }
      const c = (await mock2Handle('POST', '/api/checks', new URLSearchParams(), { projectId: Number(b.projectId), jobId: scope === 'job' ? Number(b.jobId) : null, kind, label, spec, enabled: it.enabled })) as Record<string, unknown>
      created.push({ ...c, origin: 'user', uid: `ornek-olcut-${c.id}` })
    }
    return {
      created, count: created.length, scope: scope ?? 'project', legacy: scope === null,
      sentence: scope === 'job' ? 'Bu iş için eklendi, sonraki işlere yapışmaz.' : 'Bu klasörde kalıcı olarak eklendi, sonraki işlerde de geçerli.',
    }
  }

  if (p === '/api/agents/ready' && method === 'GET') {
    const agents = [
      { id: 'claude', name: 'Claude Code', found: true, ready: true, version: '2.1.322 (örnek)', login: 'yes', status: 'ready', canStart: true, sentence: 'Claude Code hazır (sürüm 2.1.322 (örnek)). Giriş yapılmış görünüyor.', action: null, detail: null, isDefault: true },
      { id: 'codex', name: 'Codex', found: true, ready: true, version: 'codex-cli 0.167.0 (örnek)', login: 'unknown', status: 'login_unknown', canStart: true, sentence: 'Codex kurulu (sürüm codex-cli 0.167.0 (örnek)). Giriş durumu ilk işte anlaşılır.', action: null, detail: null, isDefault: false },
      { id: 'hermes', name: 'Hermes Agent', found: false, ready: false, version: null, login: 'unknown', status: 'closed', canStart: false, sentence: 'Tanışma sayfasında onaylayınca açılır.', action: null, detail: null, isDefault: false },
      { id: 'opencode', name: 'OpenCode', found: false, ready: false, version: null, login: 'unknown', status: 'closed', canStart: false, sentence: 'Tanışma sayfasında onaylayınca açılır.', action: null, detail: null, isDefault: false },
    ]
    return { canStart: true, sentence: agents[0]!.sentence, defaultAgent: 'claude', agents }
  }

  if (p === '/api/events' && method === 'GET') {
    const kind = q.get('kind')
    if (kind && !['job', 'guard', 'setting', 'system'].includes(kind)) throw new ApiError(400, 'Tür şunlardan biri olmalı: job, guard, setting, system.')
    const L = (n: number, h: number, group: 'job' | 'guard' | 'setting' | 'system', sentence: string, jobId: number | null = null) => {
      const job = jobId ? mock2State().jobs.find((j) => j.id === jobId) : null
      return { id: 'ornek-' + n, at: iso(h * HOUR), group, groupLabel: { job: 'İş', guard: 'Koruma', setting: 'Ayar', system: 'Sistem' }[group], kind: 'ornek', sentence, jobId, jobTitle: job?.title ?? null, projectId: job?.projectId ?? null, projectName: pname(job?.projectId), folder: null }
    }
    const firstJob = mock2State().jobs[0]?.id ?? null
    const secondJob = mock2State().jobs[1]?.id ?? null
    const all = emptyMode ? [] : [
      L(1, 0.2, 'job', 'İş kanıtıyla bitti: ölçütlerin hepsi geçti.', firstJob),
      L(2, 0.5, 'job', 'İş çalışmaya başladı.', secondJob),
      L(3, 3, 'guard', 'Bekçi bir işlemi durdurdu: proje dışına silme denemesi.'),
      L(4, 5, 'setting', 'Hermes Agent kaydını onayladın.'),
      L(5, 9, 'job', 'Ölçütler kontrol edildi, bir tanesi geçmedi.', secondJob),
      L(6, 27, 'system', 'Veri dosyası yeni sürüme güncellendi.'),
      L(7, 30, 'job', 'İş kanıtıyla bitti: ölçütlerin hepsi geçti.', firstJob),
      L(8, 52, 'system', 'Veri dosyasının yedeği alındı.'),
    ]
    const rows = kind ? all.filter((x) => x.group === kind) : all
    const limit = Math.min(200, Math.max(1, Number(q.get('limit') ?? 50)))
    const before = q.get('before')
    const older = before ? rows.filter((x) => Date.parse(x.at) < Date.parse(before)) : rows
    const items = older.slice(0, limit)
    const hasMore = older.length > items.length
    return { items, hasMore, next: hasMore ? items[items.length - 1]!.at : null, limit, group: kind, groups: [{ id: 'job', label: 'İş' }, { id: 'guard', label: 'Koruma' }, { id: 'setting', label: 'Ayar' }, { id: 'system', label: 'Sistem' }] }
  }

  if (p === '/api/morning' && method === 'GET') {
    const base = (await mock2Handle('GET', p, q, b)) as Morning
    const st = mock2State()
    const none = emptyMode
    const waiting = none ? [] : st.jobs.filter((j) => ['cevap_bekliyor', 'onay_bekliyor', 'karar_bekliyor', 'kaldi'].includes(j.state)).map((j) => ({ id: j.id, title: j.title, projectName: pname(j.projectId), projectId: j.projectId, state: j.state, night: j.night, finishedAt: j.finishedAt, question: j.question ?? null }))
    const nightJobs = none ? [] : st.jobs.filter((j) => j.night).map((j) => ({ id: j.id, title: j.title, projectName: pname(j.projectId), projectId: j.projectId, state: j.state, night: true, finishedAt: j.finishedAt, question: null }))
    const total = none ? 0 : base.passed.length + base.failed.length + base.unverified.length + waiting.length
    return {
      ...base,
      sentence: none ? 'Bu aralıkta biten iş ya da seni bekleyen ajan yok.' : `${base.failed.length} iş geçemedi, ${waiting.length} iş seni bekliyor.`,
      hours: 16, waitingJobs: waiting,
      guardStops: none ? [] : [
        { at: iso(5 * HOUR), projectName: pname(2), tool: 'Bash', summary: 'rm -rf ../baska-klasor', action: 'deny', reason: 'Proje dışına silme.' },
        { at: iso(3 * HOUR), projectName: pname(2), tool: 'Bash', summary: 'git push origin main', action: 'ask', reason: 'Yayın öncesi sorulur.' },
      ],
      spend: none ? { runs: 0, inputTokens: 0, cachedTokens: 0, outputTokens: 0, reasoningTokens: 0, costUsd: null, note: 'Bu aralıkta Söz ajan başlatmadı.' }
        : { runs: 4, inputTokens: 182_000, cachedTokens: 640_000, outputTokens: 41_000, reasoningTokens: 0, costUsd: 1.37, note: 'Dolar tutarı aracın kendi bildirdiği toplamdır (tahmin değil).' },
      night: { passed: nightJobs.filter((j) => j.state === 'gecti').length, failed: nightJobs.filter((j) => j.state === 'kaldi').length, waiting: 0, open: nightJobs.filter((j) => ['calisiyor', 'verildi', 'sirada'].includes(j.state)).length, jobs: nightJobs,
        note: 'Uzun iş belli bir saatte başlamaz ya da durmaz. Tek farkı: o klasörün bitti denetimi iş bitene kadar açık kalır. Bilgisayar açık kalmalı, seni kimse uyandırmaz.' },
      jobsInWindow: total,
    }
  }

  if (p === '/api/discovery/suggestions' && method === 'POST') {
    return { batchId: 'ornek-grup', shadow: false, items: emptyMode ? [] : SUGGESTIONS.filter((s) => !dismissed.has(s.objectUid)) }
  }
  if ((p === '/api/discovery/dismiss' || p === '/api/discovery/restore') && method === 'POST') {
    const uid = String(b.objectUid ?? '')
    if (!uid) throw new ApiError(400, 'objectUid gerekli.')
    if (p.endsWith('dismiss')) dismissed.add(uid)
    else dismissed = new Set([...dismissed].filter((x) => x !== uid))
    return { ok: true }
  }
  if (p === '/api/fs/browse' && method === 'GET') {
    // Örnek klasör ağacı (uydurma). Sürücünün kendisi gezilir ama seçilemez.
    const tree: Record<string, string[]> = { 'C:\\': ['Users', 'Projeler'], 'C:\\Projeler': ['bahce-takip', 'fatura-sayfasi'], 'D:\\': ['Belgeler'] }
    const at = q.get('path')
    if (!at) return { path: null, parent: null, selectable: false, reason: null, truncated: false, folders: [{ name: 'C:', path: 'C:\\' }, { name: 'D:', path: 'D:\\' }] }
    const isRoot = /^[A-Za-z]:[\\/]?$/.test(at)
    const key = isRoot ? at.slice(0, 2) + '\\' : at.replace(/[\\/]+$/, '')
    const cut = key.lastIndexOf('\\')
    const parent = isRoot ? null : cut <= 2 ? key.slice(0, 2) + '\\' : key.slice(0, cut)
    return {
      path: key,
      parent,
      selectable: !isRoot,
      reason: isRoot ? 'Bir sürücünün tamamı proje olarak eklenemez. İçindeki bir klasörü seç.' : null,
      truncated: false,
      folders: (tree[key] ?? []).map((n) => ({ name: n, path: (isRoot ? key : key + '\\') + n })),
    }
  }
  if (p === '/api/discovery/usage' && method === 'POST') return { ok: true }
  if (p === '/api/discovery/search' && method === 'GET') {
    const text = (q.get('q') ?? '').toLocaleLowerCase('tr')
    const all = [
      { objectUid: 'setting:ui.theme', title: 'Tema', kindDir: 7, description: 'Açık, koyu ya da sistemi izle.', match: 'TitleExact', weight: 0.5 },
      { objectUid: 'connector:hermes', title: 'Hermes Agent', kindDir: 1, description: 'Hermes ile iş yürütür.', match: 'Prefix', weight: 0.2 },
    ]
    return { installed: true, query: q.get('q') ?? '', items: all.filter((x) => x.title.toLocaleLowerCase('tr').includes(text) || (x.description ?? '').toLocaleLowerCase('tr').includes(text)) }
  }
  return NOT_HANDLED
}
