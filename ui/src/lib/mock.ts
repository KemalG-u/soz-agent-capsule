// Sahte katman: bütün kişi, proje ve cümleler TAMAMEN UYDURMADIR.
// Sunucu cevap vermezse (ya da VITE_MOCK=1) arayüz bununla çalışır.
import { ApiError } from './errors'
import { mock2Handle, NOT_HANDLED } from './mock2'
import { mock3Handle } from './mock3'
import { mock5Handle } from './mock5'
import type {
  Decision,
  DecisionPatch,
  DecisionType,
  Project,
  RepeatCluster,
  Report,
  SessionInfo,
  Settings,
  Summary,
  UtteranceItem,
} from './types'

export const MIN = 60_000
export const HOUR = 60 * MIN
export const DAY = 24 * HOUR
const t0 = Date.now()
export const iso = (msAgo: number) => new Date(t0 - msAgo).toISOString()
export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

export const emptyMode = typeof location !== 'undefined' && /[?&]bos=1/.test(location.search)
let imported = !emptyMode
export const isImported = () => imported
let lastImportAt: string | null = emptyMode ? null : iso(26 * MIN)

export const projects: Project[] = [
  { id: 1, name: 'Genel', path: null, utterances: 212, proposed: 1, approved: 4, repeats: 1, lastAt: iso(40 * MIN) },
  { id: 2, name: 'Bahçe Takip', path: 'D:\\projeler\\bahce-takip', utterances: 468, proposed: 1, approved: 5, repeats: 1, lastAt: iso(25 * MIN) },
  { id: 3, name: 'Atölye Sipariş', path: 'D:\\projeler\\atolye-siparis', utterances: 391, proposed: 1, approved: 4, repeats: 1, lastAt: iso(3 * HOUR) },
  { id: 4, name: 'Aile Bütçesi', path: 'D:\\projeler\\aile-butcesi', utterances: 255, proposed: 0, approved: 3, repeats: 1, lastAt: iso(5 * HOUR) },
  { id: 5, name: 'Haftalık Bülten', path: 'D:\\projeler\\haftalik-bulten', utterances: 301, proposed: 1, approved: 2, repeats: 0, lastAt: iso(52 * MIN) },
]
projects.forEach((p) => { p.gateEnabled = p.id === 2 || p.id === 4 })
export const pname = (id: number | null) => projects.find((p) => p.id === id)?.name ?? 'Genel'

export type DRow = Omit<Decision, 'createdAt' | 'updatedAt' | 'projectName'> & { createdAgo: number }
let nextId = 100
export const decisions: DRow[] = [
  { id: 1, projectId: 1, type: 'rule', text: 'Bir işi bitirdim demeden önce çıktısını göster.', status: 'approved', pinned: true, repeatCount: 7, sessionCount: 5, createdAgo: 12 * DAY, origin: { tool: 'claude', ts: iso(12 * DAY), sessionId: 11 } },
  { id: 2, projectId: 1, type: 'rule', text: 'Sade Türkçe yaz. Teknik sözcüğü gerekiyorsa bir cümleyle açıkla.', status: 'approved', pinned: false, repeatCount: 4, sessionCount: 3, createdAgo: 20 * DAY, origin: { tool: 'codex', ts: iso(20 * DAY), sessionId: 12 } },
  { id: 3, projectId: 1, type: 'ban', text: 'Bana aynı soruyu iki kez sorma.', status: 'approved', pinned: false, repeatCount: 2, sessionCount: 2, createdAgo: 9 * DAY, origin: { tool: 'claude', ts: iso(9 * DAY), sessionId: 13 } },
  { id: 4, projectId: 1, type: 'preference', text: 'Cevabın ilk satırında sonucu söyle, ayrıntıyı sonra ver.', status: 'approved', pinned: false, repeatCount: 3, sessionCount: 3, createdAgo: 15 * DAY, origin: null },
  { id: 5, projectId: 2, type: 'rule', text: 'Sulama hatırlatmaları yalnız sabah sekizde gitsin.', status: 'approved', pinned: true, repeatCount: 3, sessionCount: 2, createdAgo: 6 * DAY, origin: { tool: 'claude', ts: iso(6 * DAY), sessionId: 21 } },
  { id: 6, projectId: 2, type: 'ban', text: 'Bitki fotoğraflarını silme, yalnız arşive taşı.', status: 'approved', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 14 * DAY, origin: { tool: 'claude', ts: iso(14 * DAY), sessionId: 22 } },
  { id: 7, projectId: 2, type: 'decision', text: 'Veriler yerel dosyada kalacak. Sunucu kullanmayacağız.', status: 'approved', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 22 * DAY, origin: { tool: 'codex', ts: iso(22 * DAY), sessionId: 23 } },
  { id: 8, projectId: 2, type: 'decision', text: 'Tarihler gün.ay.yıl biçiminde gösterilecek.', status: 'approved', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 19 * DAY, origin: null },
  { id: 9, projectId: 2, type: 'preference', text: 'Renkler sakin olsun. Yeşil tonları ağırlıkta.', status: 'approved', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 17 * DAY, origin: { tool: 'claude', ts: iso(17 * DAY), sessionId: 24 } },
  { id: 10, projectId: 3, type: 'rule', text: 'Sipariş numarası her yerde aynı biçimde yazılsın: SP-0001.', status: 'approved', pinned: false, repeatCount: 2, sessionCount: 2, createdAgo: 11 * DAY, origin: { tool: 'codex', ts: iso(11 * DAY), sessionId: 31 } },
  { id: 11, projectId: 3, type: 'ban', text: 'Müşteri telefonlarını günlüğe yazma.', status: 'approved', pinned: true, repeatCount: 1, sessionCount: 1, createdAgo: 18 * DAY, origin: { tool: 'claude', ts: iso(18 * DAY), sessionId: 32 } },
  { id: 12, projectId: 3, type: 'decision', text: 'Kargo takibi için ayrı bir sayfa açılacak.', status: 'approved', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 8 * DAY, origin: null },
  { id: 13, projectId: 3, type: 'preference', text: 'Tabloda en yeni sipariş en üstte dursun.', status: 'approved', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 5 * DAY, origin: { tool: 'codex', ts: iso(5 * DAY), sessionId: 33 } },
  { id: 14, projectId: 4, type: 'rule', text: 'Para tutarları her yerde virgülle ve iki ondalıkla yazılsın.', status: 'approved', pinned: false, repeatCount: 2, sessionCount: 2, createdAgo: 13 * DAY, origin: { tool: 'claude', ts: iso(13 * DAY), sessionId: 41 } },
  { id: 15, projectId: 4, type: 'ban', text: 'Banka bilgilerini hiçbir dosyaya kaydetme.', status: 'approved', pinned: true, repeatCount: 1, sessionCount: 1, createdAgo: 25 * DAY, origin: null },
  { id: 16, projectId: 4, type: 'decision', text: 'Aylık rapor her ayın ilk günü hazırlanacak.', status: 'approved', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 7 * DAY, origin: { tool: 'codex', ts: iso(7 * DAY), sessionId: 42 } },
  { id: 17, projectId: 5, type: 'rule', text: 'Bülten başlığı altı sözcüğü geçmesin.', status: 'approved', pinned: false, repeatCount: 2, sessionCount: 2, createdAgo: 10 * DAY, origin: { tool: 'claude', ts: iso(10 * DAY), sessionId: 51 } },
  { id: 18, projectId: 5, type: 'preference', text: 'Bülten sade, tek sütun ve bol boşluklu olsun.', status: 'approved', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 16 * DAY, origin: null },
  { id: 19, projectId: 3, type: 'rule', text: 'Eski sipariş şablonunu kullanma.', status: 'archived', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 40 * DAY, origin: null },
  // öneriler
  { id: 20, projectId: 2, type: 'rule', text: 'Hatırlatma saatini ayarlamadan önce bana göster.', status: 'proposed', pinned: false, repeatCount: 4, sessionCount: 3, createdAgo: 2 * HOUR, origin: { tool: 'claude', ts: iso(2 * HOUR), sessionId: 25 } },
  { id: 21, projectId: 1, type: 'ban', text: 'Cevabı gereksiz uzatma.', status: 'proposed', pinned: false, repeatCount: 5, sessionCount: 4, createdAgo: 5 * HOUR, origin: { tool: 'codex', ts: iso(5 * HOUR), sessionId: 14 } },
  { id: 22, projectId: 3, type: 'decision', text: 'Sipariş listesi tarihe göre sıralanacak.', status: 'proposed', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 8 * HOUR, origin: { tool: 'claude', ts: iso(8 * HOUR), sessionId: 34 } },
  { id: 23, projectId: 5, type: 'preference', text: 'Bülten gönderim saati cuma öğleden sonra olsun.', status: 'proposed', pinned: false, repeatCount: 2, sessionCount: 2, createdAgo: 20 * HOUR, origin: { tool: 'claude', ts: iso(20 * HOUR), sessionId: 52 } },
]

function viewDecision(d: DRow): Decision {
  const { createdAgo, ...rest } = d
  return { ...rest, projectName: pname(d.projectId), createdAt: iso(createdAgo), updatedAt: iso(Math.max(0, createdAgo - HOUR)) }
}

const repeatsData: Omit<RepeatCluster, 'projectName'>[] = [
  {
    clusterId: 1, projectId: 1, canonical: 'Kanıtlamadan önüme getirme.', count: 6, sessionCount: 4, firstTs: iso(18 * DAY), lastTs: iso(2 * DAY), decisionId: null,
    members: [
      { utteranceId: 901, text: 'Kanıtlamadan önüme getirme.', ts: iso(2 * DAY), tool: 'claude', sessionId: 11 },
      { utteranceId: 902, text: 'Bitti diyorsan çıktısını da göster, kanıtsız getirme.', ts: iso(5 * DAY), tool: 'codex', sessionId: 12 },
      { utteranceId: 903, text: 'Yine kanıt yok. Kanıtlamadan getirme dedim.', ts: iso(9 * DAY), tool: 'claude', sessionId: 13 },
      { utteranceId: 904, text: 'Önce çalıştırıp çıktısını göster, sonra bitti de.', ts: iso(12 * DAY), tool: 'claude', sessionId: 14 },
      { utteranceId: 905, text: 'Kanıtsız bitti deme.', ts: iso(15 * DAY), tool: 'codex', sessionId: 15 },
      { utteranceId: 906, text: 'Gösterdiğin şey kanıt değil, çıktısını ver.', ts: iso(18 * DAY), tool: 'claude', sessionId: 16 },
    ],
  },
  {
    clusterId: 2, projectId: 2, canonical: 'Hatırlatmalar akşam gitmesin.', count: 4, sessionCount: 3, firstTs: iso(10 * DAY), lastTs: iso(1 * DAY), decisionId: 5,
    members: [
      { utteranceId: 911, text: 'Hatırlatmalar akşam gitmesin.', ts: iso(1 * DAY), tool: 'claude', sessionId: 21 },
      { utteranceId: 912, text: 'Akşam mesaj gönderme, sadece sabah.', ts: iso(4 * DAY), tool: 'claude', sessionId: 26 },
      { utteranceId: 913, text: 'Sulama bildirimi akşam olmasın dedim.', ts: iso(7 * DAY), tool: 'codex', sessionId: 27 },
      { utteranceId: 914, text: 'Bildirimler sabah sekizde gelsin, akşam hiç gelmesin.', ts: iso(10 * DAY), tool: 'claude', sessionId: 28 },
    ],
  },
  {
    clusterId: 3, projectId: 3, canonical: 'Dosyaları silmeden önce sor.', count: 3, sessionCount: 2, firstTs: iso(8 * DAY), lastTs: iso(3 * DAY), decisionId: null,
    members: [
      { utteranceId: 921, text: 'Dosyaları silmeden önce sor.', ts: iso(3 * DAY), tool: 'codex', sessionId: 31 },
      { utteranceId: 922, text: 'Silmeden önce bana haber ver, kendi kendine silme.', ts: iso(5 * DAY), tool: 'codex', sessionId: 35 },
      { utteranceId: 923, text: 'Silme işi için her seferinde onayımı al.', ts: iso(8 * DAY), tool: 'claude', sessionId: 36 },
    ],
  },
  {
    clusterId: 4, projectId: 4, canonical: 'Tutarları virgülle yaz.', count: 2, sessionCount: 2, firstTs: iso(13 * DAY), lastTs: iso(6 * DAY), decisionId: 14,
    members: [
      { utteranceId: 931, text: 'Tutarları virgülle yaz.', ts: iso(6 * DAY), tool: 'claude', sessionId: 41 },
      { utteranceId: 932, text: 'Paralar 1.250,00 gibi yazılsın, noktalı olmasın.', ts: iso(13 * DAY), tool: 'claude', sessionId: 43 },
    ],
  },
]

export type SRow = Omit<SessionInfo, 'projectName' | 'since' | 'lastUserTs' | 'lastAssistantTs'> & { sinceAgo: number; userAgo: number | null; asstAgo: number | null }
export const sessionsData: SRow[] = [
  { id: 201, tool: 'claude', projectId: 2, title: 'Sulama hatırlatmaları', kind: 'human', state: 'waiting', sinceAgo: 25 * MIN, userAgo: 31 * MIN, asstAgo: 25 * MIN,
    lastUserText: 'Sulama hatırlatmaları sabah sekizde gelsin, akşam hiç gelmesin.',
    lastAssistantText: 'Hatırlatma saatini sabah 08:00 olarak ayarladım. Tek bir sorum var: hafta sonları da gönderilsin mi, yoksa yalnız hafta içi mi? Cevabına göre zamanlayıcıyı bitireceğim.',
    asksQuestion: true, claimsDone: false, showsEvidence: false, subCount: 3 },
  { id: 202, tool: 'codex', projectId: 3, title: 'Sipariş listesi ekranı', kind: 'human', state: 'waiting', sinceAgo: 3 * HOUR, userAgo: 3 * HOUR + 12 * MIN, asstAgo: 3 * HOUR,
    lastUserText: 'Sipariş listesinde duruma göre süzme ve tarihe göre sıralama olsun.',
    lastAssistantText: 'Sipariş listesi ekranı tamamlandı, her şey hazır. Süzme ve sıralama çalışıyor. İstersen kargo takibi sayfasına geçebilirim.',
    asksQuestion: false, claimsDone: true, showsEvidence: false, subCount: 0 },
  { id: 203, tool: 'claude', projectId: 4, title: 'Kategori toplamları', kind: 'human', state: 'waiting', sinceAgo: 5 * HOUR, userAgo: 5 * HOUR + 20 * MIN, asstAgo: 5 * HOUR,
    lastUserText: 'Kategori toplamları yanlış çıkıyor, market ile fatura karışmış.',
    lastAssistantText: 'Karışıklığı düzelttim: market ve fatura artık ayrı toplanıyor.\n\nKontrol: 14 test çalıştırıldı, 14 geçti (test/toplamlar.test.ts).\nEkranı şuradan görebilirsin: http://localhost:5173/butce\n\nBir sonraki adım olarak aylık özet sayfasını hazırlayabilirim.',
    asksQuestion: false, claimsDone: true, showsEvidence: true, subCount: 7 },
  { id: 204, tool: 'codex', projectId: 5, title: 'PDF çıktısı', kind: 'human', state: 'stalled', sinceAgo: 52 * MIN, userAgo: 58 * MIN, asstAgo: 52 * MIN,
    lastUserText: 'Bülten şablonunu PDF olarak da verebilir misin?',
    lastAssistantText: 'Şimdi bülten şablonunu dışa aktarma adımına geçiyorum ve sayfa kenar boşluklarını ayarlıyorum.',
    asksQuestion: false, claimsDone: false, showsEvidence: false, subCount: 1 },
  { id: 205, tool: 'claude', projectId: 3, title: null, kind: 'human', state: 'stalled', sinceAgo: 6 * HOUR + 10 * MIN, userAgo: 6 * HOUR + 10 * MIN, asstAgo: null,
    lastUserText: 'Müşteri listesini dışa aktarırken telefon sütununu çıkar.',
    lastAssistantText: null,
    asksQuestion: false, claimsDone: false, showsEvidence: false, subCount: 0 },
  { id: 206, tool: 'claude', projectId: 5, title: 'Bülten metni', kind: 'human', state: 'working', sinceAgo: 2 * MIN, userAgo: 9 * MIN, asstAgo: 3 * MIN,
    lastUserText: 'Bu haftanın bülteni için üç kısa başlık öner, sonra metni yaz.',
    lastAssistantText: 'Üç başlık hazır: “Bahçede ilk filiz”, “Haftanın tarifi”, “Küçük hatırlatmalar”. Şimdi ilk metni yazıyorum.',
    asksQuestion: false, claimsDone: false, showsEvidence: false, subCount: 12 },
  { id: 207, tool: 'codex', projectId: 2, title: 'Bitki listesi', kind: 'human', state: 'working', sinceAgo: 40_000, userAgo: 4 * MIN, asstAgo: 90_000,
    lastUserText: 'Bitki listesine her bitkinin son sulama tarihini ekle.',
    lastAssistantText: 'Veri dosyasını okuyorum, tarih alanını ekleyeceğim.',
    asksQuestion: false, claimsDone: false, showsEvidence: false, subCount: 0 },
  { id: 208, tool: 'claude', projectId: 4, title: 'Aylık rapor', kind: 'human', state: 'idle', sinceAgo: 2 * HOUR + 15 * MIN, userAgo: 2 * HOUR + 30 * MIN, asstAgo: 2 * HOUR + 15 * MIN,
    lastUserText: 'Tamam, şimdilik böyle kalsın, yarın devam ederiz.',
    lastAssistantText: 'Anlaşıldı. Aylık rapor şablonu hazır. Yarın kalan iki sayfayı bitiririz.',
    asksQuestion: false, claimsDone: false, showsEvidence: false, subCount: 0 },
  { id: 209, tool: 'codex', projectId: 1, title: 'Not düzeni', kind: 'human', state: 'idle', sinceAgo: 7 * HOUR, userAgo: 7 * HOUR + 5 * MIN, asstAgo: 7 * HOUR,
    lastUserText: 'Notları tarihe göre klasörlere ayır.',
    lastAssistantText: 'Notlar klasörlere ayrıldı.',
    asksQuestion: false, claimsDone: false, showsEvidence: false, subCount: 0 },
  { id: 210, tool: 'codex', projectId: 3, title: 'Gece taraması', kind: 'automation', state: 'working', sinceAgo: MIN, userAgo: null, asstAgo: MIN,
    lastUserText: null, lastAssistantText: 'Tarama sürüyor.', asksQuestion: false, claimsDone: false, showsEvidence: false, subCount: 0 },
]

export function viewSession(s: SRow): SessionInfo {
  const { sinceAgo, userAgo, asstAgo, ...rest } = s
  return { ...rest, projectName: pname(s.projectId), since: iso(sinceAgo), lastUserTs: userAgo === null ? null : iso(userAgo), lastAssistantTs: asstAgo === null ? null : iso(asstAgo) }
}

// ---- cümle havuzu (arama için) ----
const pool: { text: string; project: number; tool: string }[] = [
  { text: 'Bahçe takip uygulamasında her bitkinin bir fotoğrafı olsun.', project: 2, tool: 'claude' },
  { text: 'Sulama hatırlatmaları sabah sekizde gelsin, akşam hiç gelmesin.', project: 2, tool: 'claude' },
  { text: 'Bitki listesine son sulama tarihini ekle.', project: 2, tool: 'codex' },
  { text: 'Hasat takvimi ayrı bir sayfada dursun.', project: 2, tool: 'claude' },
  { text: 'Sipariş listesinde duruma göre süzme ve tarihe göre sıralama olsun.', project: 3, tool: 'codex' },
  { text: 'Siparişi iptal edince müşteriye bir mesaj gitsin mi, sence?', project: 3, tool: 'claude' },
  { text: 'Dosyaları silmeden önce sor.', project: 3, tool: 'codex' },
  { text: 'Müşteri telefonlarını günlüğe yazma.', project: 3, tool: 'claude' },
  { text: 'Kargo takibi için ayrı bir sayfa açalım.', project: 3, tool: 'codex' },
  { text: 'Kategori toplamları yanlış çıkıyor, market ile fatura karışmış.', project: 4, tool: 'claude' },
  { text: 'Paralar 1.250,00 gibi yazılsın, noktalı olmasın.', project: 4, tool: 'claude' },
  { text: 'Aylık rapor her ayın ilk günü hazırlansın.', project: 4, tool: 'codex' },
  { text: 'Banka bilgilerini hiçbir dosyaya kaydetme.', project: 4, tool: 'claude' },
  { text: 'Bu haftanın bülteni için üç kısa başlık öner.', project: 5, tool: 'claude' },
  { text: 'Bülten sade, tek sütun ve bol boşluklu olsun.', project: 5, tool: 'claude' },
  { text: 'Bülten şablonunu PDF olarak da verebilir misin?', project: 5, tool: 'codex' },
  { text: 'Bülten gönderim saati cuma öğleden sonra olsun.', project: 5, tool: 'claude' },
  { text: 'Kanıtlamadan önüme getirme.', project: 1, tool: 'claude' },
  { text: 'Bitti diyorsan çıktısını da göster, kanıtsız getirme.', project: 1, tool: 'codex' },
  { text: 'Sade Türkçe yaz. Teknik sözcüğü gerekiyorsa bir cümleyle açıkla.', project: 1, tool: 'codex' },
  { text: 'Cevabın ilk satırında sonucu söyle, ayrıntıyı sonra ver.', project: 1, tool: 'claude' },
  { text: 'Bana aynı soruyu iki kez sorma.', project: 1, tool: 'claude' },
  { text: 'Cevabı gereksiz uzatma, kısa tut.', project: 1, tool: 'codex' },
]
const utterancesAll: (UtteranceItem & { projectId: number })[] = Array.from({ length: 118 }, (_, i) => {
  const p = pool[(i * 7) % pool.length]
  return { id: 1000 + i, text: p.text, ts: iso(i * 5 * HOUR + ((i * 37) % 90) * MIN), tool: p.tool, projectName: pname(p.project), projectId: p.project, sessionId: 300 + (i % 40) }
})

export const settings: Settings = {
  language: 'tr', theme: 'system', claudeRoot: null, codexRoot: null, workingSeconds: 90, idleMinutes: 30,
  extractThreshold: 0.6, repeatThreshold: 0.55, packetBudget: 2000, waitingStaleHours: 24, stalledMinutes: 5,
  explainEnabled: false, explainCommand: 'claude', allowRealProjectWrites: false, judgeClaudeEnabled: false,
  dictationPath: 'C:\\Program Files\\Ornek Dikte\\dikte.exe', hotkeyPacket: 'Ctrl+Alt+K', capsuleX: null, capsuleY: null, lanEnabled: false, claudePath: null, codexPath: null, defaultAgent: 'claude', maxParallelRuns: 3,
}

function summary(): Summary {
  if (!imported) {
    return { projects: 0, sessions: 0, utterances: 0, proposed: 0, approved: 0, repeatClusters: 0, lastImportAt: null, waiting: 0, working: 0, stalled: 0,
      sources: [{ tool: 'claude', root: 'C:\\Users\\ornek\\.claude\\projects', found: true, files: 0, unrecognized: 0 }, { tool: 'codex', root: 'C:\\Users\\ornek\\.codex\\sessions', found: true, files: 0, unrecognized: 0 }] }
  }
  const human = sessionsData.filter((s) => s.kind === 'human')
  return {
    projects: projects.length, sessions: 612, utterances: utterancesAll.length * 15, proposed: decisions.filter((d) => d.status === 'proposed').length,
    approved: decisions.filter((d) => d.status === 'approved').length, repeatClusters: repeatsData.length, lastImportAt,
    waiting: human.filter((s) => s.state === 'waiting').length, working: human.filter((s) => s.state === 'working').length, stalled: human.filter((s) => s.state === 'stalled').length,
    sources: [
      { tool: 'claude', root: 'C:\\Users\\ornek\\.claude\\projects', found: true, files: 128, unrecognized: 3 },
      { tool: 'codex', root: 'D:\\ornek\\.codex-home\\sessions', found: true, files: 41, unrecognized: 0 },
    ],
  }
}

function report(days: number): Report {
  const daily = Array.from({ length: days }, (_, i) => {
    const d = new Date(t0 - (days - 1 - i) * DAY)
    const wave = 34 + Math.round(26 * Math.sin(i * 0.9)) + ((i * 53) % 17)
    return { date: d.toISOString().slice(0, 10), utterances: d.getDay() === 0 ? Math.round(wave * 0.35) : wave }
  })
  const total = daily.reduce((a, b) => a + b.utterances, 0)
  return {
    days, projects: 5, sessions: Math.round(days * 5.8), utterances: total,
    from: new Date(t0 - days * DAY).toISOString(), to: new Date(t0).toISOString(),
    tools: [{ tool: 'claude', sessions: 70, utterances: Math.round(total * 0.68), compactions: 6 }, { tool: 'codex', sessions: 30, utterances: total - Math.round(total * 0.68), compactions: 2 }],
    compactions: Math.round(days * 1.4), candidates: 63, repeatClusters: 4, totalRepeats: 15, automationSessions: 12, subagentSessions: 40, proposed: 4, approved: 18,
    topRepeats: [
      { clusterId: 1, text: 'Kanıtlamadan önüme getirme.', count: 6, sessionCount: 4 },
      { clusterId: 2, text: 'Hatırlatmalar akşam gitmesin.', count: 4, sessionCount: 3 },
      { clusterId: 3, text: 'Dosyaları silmeden önce sor.', count: 3, sessionCount: 2 },
      { clusterId: 4, text: 'Tutarları virgülle yaz.', count: 2, sessionCount: 2 },
      { clusterId: 5, text: 'Cevabı gereksiz uzatma.', count: 2, sessionCount: 2 },
    ],
    daily,
  }
}

const trLower = (s: string) => s.toLocaleLowerCase('tr')

export async function mockHandle(method: string, path: string, body?: unknown): Promise<unknown> {
  await sleep(60)
  const url = new URL(path, 'http://x')
  const p = url.pathname
  const q = url.searchParams
  const projId = q.get('projectId') ? Number(q.get('projectId')) : null

  if (p === '/api/session') return { token: 'ornek-belirtec', version: '0.1.0-ornek', dataDir: 'E:\\Ornek\\Soz\\veri', dataDirSource: 'SOZ_DATA_DIR ortam değişkeni (örnek)', language: 'tr' }
  if (p === '/api/summary') return summary()
  if (p === '/api/projects' && method === 'GET') return imported ? projects : projects.filter((x) => x.id >= 100)
  const r5 = await mock5Handle(method, p, q, body)
  if (r5 !== NOT_HANDLED) return r5
  if (p === '/api/settings') {
    if (method === 'PUT') { Object.assign(settings, body as Partial<Settings>); return settings }
    return { ...settings }
  }
  if (p === '/api/agents') {
    const caps = (kind: 'claude' | 'codex') => kind === 'claude'
      ? { headless: true, eventStream: true, resume: true, stop: true, guardTier: 'native_hook' as const, turnEnd: 'native_hook' as const, ask: 'live' as const, cost: 'dollars' as const }
      : { headless: true, eventStream: true, resume: true, stop: true, guardTier: 'os_sandbox' as const, turnEnd: 'outer_loop' as const, ask: 'resume_reply' as const, cost: 'tokens' as const }
    return {
      defaultAgent: settings.defaultAgent ?? 'claude',
      agents: [
        { id: 'claude', name: 'Claude', found: true, ready: true, version: '2.1.322 (örnek)', path: 'C:\Ornek\claude.exe', configuredPath: null, error: null, capabilities: caps('claude'), guardLabel: 'kanca', isDefault: (settings.defaultAgent ?? 'claude') === 'claude' },
        { id: 'codex', name: 'Codex', found: true, ready: true, version: 'codex-cli 0.167.0 (örnek)', path: 'C:\Ornek\codex.cmd', configuredPath: null, error: null, capabilities: caps('codex'), guardLabel: 'sınırlı', isDefault: settings.defaultAgent === 'codex' },
      ],
    }
  }
  if (p === '/api/sessions') {
    if (!imported) return []
    const hours = Number(q.get('hours') ?? 24)
    return sessionsData.filter((s) => s.sinceAgo <= hours * HOUR).map(viewSession)
  }
  if (p === '/api/digest') {
    const human = sessionsData.filter((s) => s.kind === 'human')
    const lines = human.filter((s) => s.state === 'waiting' || s.state === 'stalled').map(
      (s) => `${s.tool === 'claude' ? 'Claude' : 'Codex'} · ${pname(s.projectId)} · Sen en son: «${s.lastUserText ?? ''}» · Ajanın son sözü: «${(s.lastAssistantText ?? '').slice(0, 120)}»`)
    return { text: 'Seni bekleyenler ve takılanlar (örnek veri)\n\n' + lines.join('\n'), waiting: 3, working: 2, idle: 2, stalled: 2 }
  }
  let m = p.match(/^\/api\/sessions\/(\d+)\/explain$/)
  if (m) {
    if (!settings.explainEnabled) throw new ApiError(409, 'Sade anlat kapalı')
    await sleep(900)
    return { text: 'Sonuç: Ajan hatırlatma saatini sabah sekiz yaptı ama bir şeyi senden öğrenmek istiyor.\n\nÖrnek: Bir tarla düşün. Sulama sabah 08:00 olacak. Hafta sonu da sulanacak mı, yoksa yalnız Pazartesi ile Cuma arası mı?\n\nSenden beklenen: “Hafta sonu da olsun” ya da “yalnız hafta içi” demen.\n\nEmin olunmayan nokta: Zamanlayıcının gerçekten çalıştığı henüz gösterilmedi.' }
  }
  if (p === '/api/decisions') {
    if (method === 'POST') {
      const b = body as { projectId: number; type: DecisionType; text: string }
      const row: DRow = { id: nextId++, projectId: b.projectId, type: b.type, text: b.text, status: 'approved', pinned: false, repeatCount: 1, sessionCount: 1, createdAgo: 0, origin: null }
      decisions.push(row)
      return viewDecision(row)
    }
    const st = q.get('status'); const ty = q.get('type')
    return decisions.filter((d) => (!st || d.status === st) && (!ty || d.type === ty) && (projId === null || d.projectId === projId)).map(viewDecision)
  }
  m = p.match(/^\/api\/decisions\/(\d+)$/)
  if (m && method === 'PATCH') {
    const d = decisions.find((x) => x.id === Number(m![1]))
    if (!d) throw new ApiError(404, 'Bulunamadı')
    Object.assign(d, body as DecisionPatch)
    return viewDecision(d)
  }
  if (p === '/api/repeats') {
    const min = Number(q.get('min') ?? 2)
    return repeatsData.filter((r) => r.sessionCount >= min && (projId === null || r.projectId === projId)).map((r) => ({ ...r, projectName: pname(r.projectId) }))
  }
  m = p.match(/^\/api\/repeats\/(\d+)\/promote$/)
  if (m) {
    const r = repeatsData.find((x) => x.clusterId === Number(m![1]))
    if (!r) throw new ApiError(404, 'Bulunamadı')
    const type = ((body as { type?: DecisionType } | undefined)?.type ?? 'rule') as DecisionType
    const row: DRow = { id: nextId++, projectId: r.projectId, type, text: r.canonical, status: 'approved', pinned: false, repeatCount: r.count, sessionCount: r.sessionCount, createdAgo: 0, origin: null }
    decisions.push(row)
    r.decisionId = row.id
    return viewDecision(row)
  }
  if (p === '/api/utterances') {
    const term = trLower(q.get('q') ?? '')
    const limit = Number(q.get('limit') ?? 50); const offset = Number(q.get('offset') ?? 0)
    const all = utterancesAll.filter((u) => (projId === null || u.projectId === projId) && (!term || trLower(u.text).includes(term)))
    return { total: all.length, items: all.slice(offset, offset + limit).map(({ projectId: _x, ...u }) => u) }
  }
  if (p === '/api/packet') {
    const rows = decisions.filter((d) => d.status === 'approved' && (projId === null || d.projectId === projId || d.projectId === 1))
    const pn = projId === null ? 'Genel' : pname(projId)
    const by = (t: DecisionType) => rows.filter((d) => d.type === t).sort((a, b) => Number(b.pinned) - Number(a.pinned) || a.projectId - b.projectId)
    const sec = (title: string, t: DecisionType) => { const r = by(t); return r.length ? `${title}:\n` + r.map((d) => `- ${d.text}`).join('\n') : '' }
    const parts = [sec('Kurallar', 'rule'), sec('Yasaklar', 'ban'), sec('Kararlar', 'decision'), sec('Tercihler', 'preference')].filter(Boolean)
    const text = parts.length ? `[Söz defteri · ${pn}] Kullanıcının daha önce verdiği, hâlâ geçerli kararlar. Bunlara uy. Çelişen istek gelirse önce sor.\n` + parts.join('\n') : ''
    return { text, chars: text.length, decisionCount: rows.length }
  }
  if (p === '/api/import') {
    await sleep(1800)
    imported = true
    lastImportAt = new Date().toISOString()
    return { newUtterances: utterancesAll.length * 15, newCandidates: 63, newClusters: 40, newRepeatClusters: 4, newProposals: 4, updatedProposals: 0, totalMs: 1800, finishedAt: lastImportAt }
  }
  if (p === '/api/packet/copied') return { ok: true }
  if (p === '/api/report') return report(Number(q.get('days') ?? 30))
  const r3 = await mock3Handle(method, p, q, body)
  if (r3 !== NOT_HANDLED) return r3
  const r2 = await mock2Handle(method, p, q, body)
  if (r2 !== NOT_HANDLED) return r2
  throw new ApiError(404, 'Örnek veri katmanında yok: ' + p)
}
