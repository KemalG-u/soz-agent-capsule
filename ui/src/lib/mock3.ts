// Sahte katman, Söz 3 akışları: klasör ekle, iş başlat, akış satırları, cevap, durdur, Bekçi onayı, kanca kur.
// Bütün isimler ve cümleler UYDURMADIR. Ajan benzetimi zamanlayıcıyla satır basar ve job.progress / job.state olaylarını yayar.
// ?hiz=1 adımları hızlandırır (otomatik sınama için).
import { ApiError } from './errors'
import { emitLive } from './live'
import { emptyMode, HOUR, iso, MIN, projects, settings, sleep } from './mock'
import { mock2Handle, mock2State, NOT_HANDLED } from './mock2'
import type { Check, Job, JobRun, PendingApproval, ProofResult, ProofRun, TimelineItem, TimelineProof, TimelineProofResult, Verdict, WireTimelineItem } from './types'

const STEP = typeof location !== 'undefined' && /[?&]hiz=1/.test(location.search) ? 110 : 850

const tl = new Map<number, TimelineItem[]>()
const tokens = new Map<number, number>()
let seeded = false
let idn = 1
let nextProj = 100
let nextEvent = 9000
const nid = () => idn++

function shot(title: string, ok: boolean): string {
  const c = ok ? '#2f7d55' : '#b0251c'
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360" viewBox="0 0 640 360"><rect width="640" height="360" fill="#f4f5f7"/>` +
    `<rect width="640" height="40" fill="#12161c"/><rect x="20" y="12" width="110" height="14" rx="7" fill="#f4f5f7" opacity=".85"/>` +
    `<rect x="36" y="70" width="320" height="24" rx="6" fill="#12161c"/><rect x="36" y="110" width="500" height="11" rx="5" fill="#98a3b1"/>` +
    `<rect x="36" y="150" width="170" height="100" rx="8" fill="#fff" stroke="#c8cdd5"/><rect x="226" y="150" width="170" height="100" rx="8" fill="#fff" stroke="#c8cdd5"/>` +
    `<rect x="50" y="164" width="90" height="11" rx="5" fill="${c}"/><rect x="240" y="164" width="70" height="11" rx="5" fill="${c}"/>` +
    `<text x="36" y="320" font-family="Segoe UI, sans-serif" font-size="16" fill="#444d5a">${title} (örnek görüntü)</text></svg>`
  return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg)
}

const mk = (kind: TimelineItem['kind'], text: string, agoMs: number, extra: Partial<TimelineItem> = {}): TimelineItem => ({ id: nid(), ts: iso(agoMs), kind, text, ...extra })
const done = (text: string, agoMs: number) => mk('progress', text, agoMs, { state: 'done' })

function results(list: [string, 'passed' | 'failed', number, string?][]): TimelineProofResult[] {
  return list.map(([label, status, ms, tail]) => ({ label, status: status as ProofResult['status'], durationMs: ms, outputTail: tail ?? null }))
}

function seed() {
  if (seeded) return
  seeded = true
  if (emptyMode) return
  const { jobs } = mock2State()
  const J = (id: number) => jobs.find((j) => j.id === id)
  const failTail = ' FAIL  test/siparis-liste.test.ts\n  ✕ durum süzgeci "kargoda" olanları getirir (12 ms)\n    beklenen 3 satır, gelen 0\n\nTests: 1 failed, 18 passed, 19 total'

  const j301 = J(301)!
  tl.set(301, [
    mk('user', j301.rawText, 3 * HOUR),
    done('Klasörü inceledi', 3 * HOUR - MIN),
    done('sulama.js dosyasını okudu', 3 * HOUR - 2 * MIN),
    done('2 dosya değiştirdi', 3 * HOUR - 5 * MIN),
    done('Testleri çalıştırdı', 3 * HOUR - 6 * MIN),
    mk('proof', 'Kaldı: 3 ölçütten 1’i tutmadı', 2 * HOUR + 14 * MIN, { proof: { verdict: 'failed', attempt: 1, summary: '3 ölçütten 1’i tutmadı', results: results([['Testler geçsin', 'failed', 8200, 'Tests: 1 failed, 11 passed, 12 total\n  ✕ sulama saati sabah sekizi aşmaz (3 ms)\n    beklenen 08:00, gelen 20:00'], ['Derleme hatasız bitsin', 'passed', 6100], ['Ana sayfa açılsın', 'passed', 220]]) } }),
    mk('system', 'Test kaldı, işi ajana geri yolladım (1/3)', 2 * HOUR + 13 * MIN),
    done('Akşam saatini kaldırdı', 2 * HOUR + 10 * MIN),
    done('Testleri yeniden çalıştırdı', 2 * HOUR + 5 * MIN),
    mk('proof', 'Kanıtıyla bitti: 4 ölçütün hepsi geçti', 2 * HOUR + 1 * MIN, { proof: { verdict: 'passed', attempt: 2, summary: '4 ölçütün hepsi geçti', results: [...results([['Testler geçsin', 'passed', 7900], ['Derleme hatasız bitsin', 'passed', 6000], ['Ana sayfa açılsın', 'passed', 190]]), { label: 'Ana sayfanın ekran görüntüsü alınsın', status: 'passed', durationMs: 1900, artifactUrl: shot('Bahçe Takip ana sayfa', true) }] } }),
  ])
  const j302 = J(302)!
  tl.set(302, [
    mk('user', j302.rawText, 6 * HOUR),
    done('Klasörü inceledi', 6 * HOUR - MIN),
    done('siparis-liste.ts dosyasını okudu', 6 * HOUR - 3 * MIN),
    done('3 dosya değiştirdi', 5 * HOUR + 40 * MIN),
    mk('proof', 'Kaldı: 1 ölçüt tutmadı', 5 * HOUR + 30 * MIN, { proof: { verdict: 'failed', attempt: 1, summary: 'Testler geçsin tutmadı', results: results([['Testler geçsin', 'failed', 14300, failTail]]) } }),
    mk('system', 'Test kaldı, işi ajana geri yolladım (1/3)', 5 * HOUR + 29 * MIN),
    mk('proof', 'Kaldı: 1 ölçüt tutmadı', 5 * HOUR + 12 * MIN, { proof: { verdict: 'failed', attempt: 2, summary: 'Testler geçsin tutmadı', results: results([['Testler geçsin', 'failed', 14100, failTail]]) } }),
    mk('system', 'Test kaldı, işi ajana geri yolladım (2/3)', 5 * HOUR + 11 * MIN),
    mk('proof', 'Kaldı: 3 denemede de tutmadı', 4 * HOUR + 50 * MIN, { proof: { verdict: 'failed', attempt: 3, summary: 'Testler geçsin 3 denemede de tutmadı', results: results([['Testler geçsin', 'failed', 14500, failTail]]) } }),
  ])
  const j303 = J(303)!
  tl.set(303, [
    mk('user', j303.rawText, 5 * HOUR + 20 * MIN),
    done('Toplamları hesaplayan işlevi buldu', 5 * HOUR + 18 * MIN),
    done('Market ile faturayı ayırdı', 5 * HOUR + 10 * MIN),
    done('Testleri çalıştırdı', 5 * HOUR + 4 * MIN),
    mk('proof', 'Kanıtıyla bitti: 1 ölçütün hepsi geçti', 5 * HOUR + 1 * MIN, { proof: { verdict: 'passed', attempt: 1, summary: '1 ölçütün hepsi geçti', results: results([['Testler geçsin', 'passed', 3100]]) } }),
  ])
  const j304 = J(304)!
  tl.set(304, [
    mk('user', j304.rawText, 55 * MIN),
    done('Geçen haftanın bültenini okudu', 52 * MIN),
    done('Üç başlık önerdi', 48 * MIN),
    done('Metni yazdı', 41 * MIN),
    mk('proof', 'Kanıtsız: denenecek ölçüt yok', 40 * MIN, { proof: { verdict: 'unchecked', attempt: 1, summary: 'Bu klasörde iş bitince denenecek bir ölçüt tanımlı değil', results: [] } }),
  ])
  const j305 = J(305)!
  tl.set(305, [
    mk('user', j305.rawText, 12 * MIN),
    done('Klasörü inceledi', 11 * MIN),
    done('bitki-listesi.ts dosyasını okudu', 10 * MIN),
    done('Son sulama tarihi alanını ekledi', 6 * MIN),
    mk('progress', 'Testleri çalıştırıyor', 2 * MIN, { state: 'active' }),
  ])
  const j306 = J(306)!
  tl.set(306, [mk('user', j306.rawText, 7 * HOUR), mk('system', 'Bu iş terminalden yürüyor. Söz bitince kanıtı deneyecek.', 7 * HOUR - MIN)])

  const extra = (id: number, projectId: number, raw: string, ago: number, patch: Partial<Job>): Job => {
    const pr = projects.find((p) => p.id === projectId)
    const j: Job = {
      id, projectId, projectName: pr?.name, sessionId: null, title: raw.length > 70 ? raw.slice(0, 67) + '…' : raw, rawText: raw, promptText: raw, source: 'yazi', state: 'calisiyor', night: false, restate: false,
      createdAt: iso(ago), givenAt: iso(ago - MIN), finishedAt: null, lastVerdict: null, attempts: 0, maxAttempts: 3, ...patch,
    }
    jobs.unshift(j)
    return j
  }
  const j307 = extra(307, 3, 'Eski sipariş arşivini temizle ve yıl sonu raporunu çıkar.', 9 * MIN, {
    state: 'onay_bekliyor',
    run: { status: 'waiting_approval', lastLine: 'Senin onayını bekliyor', startedAt: iso(9 * MIN) },
    pendingApprovals: [
      { eventId: 9001, summary: 'rm -rf siparis-arsiv/2025', detail: 'Bash', at: iso(4 * MIN) },
      { eventId: 9002, summary: 'rm -rf siparis-arsiv/2024', detail: 'Bash', at: iso(3 * MIN) },
    ],
  })
  tl.set(307, [
    mk('user', j307.rawText, 9 * MIN),
    done('Klasörü inceledi', 8 * MIN),
    done('Arşiv klasörünü buldu', 7 * MIN),
    mk('approval', 'Ajan eski arşiv klasörünü silmek istiyor. Silinsin mi?', 4 * MIN, { detail: 'rm -rf siparis-arsiv/2025', state: 'pending', eventId: 9001 }),
    mk('approval', 'Ajan bir önceki yılın arşivini de silmek istiyor. Silinsin mi?', 3 * MIN, { detail: 'rm -rf siparis-arsiv/2024', state: 'pending', eventId: 9002 }),
  ])
  const j308 = extra(308, 4, 'Aylık harcama özetine hafta sonu harcamalarını ekle.', 22 * MIN, {
    state: 'cevap_bekliyor', agentState: 'soru', question: 'Hafta sonu harcamalarını ayrı bir satırda mı göstereyim, yoksa hafta içiyle birlikte mi toplayayım?',
    run: { status: 'done', lastLine: 'Sana bir soru sordu', startedAt: iso(22 * MIN) },
  })
  tl.set(308, [
    mk('user', j308.rawText, 22 * MIN),
    done('Harcama dosyasını okudu', 20 * MIN),
    done('Hafta sonu günlerini ayırdı', 17 * MIN),
    mk('agent', 'Hafta sonu harcamalarını ayrı bir satırda mı göstereyim, yoksa hafta içiyle birlikte mi toplayayım?', 14 * MIN),
  ])
  // ---- Söz 4: yeni durumlar ----
  const j309 = extra(309, 2, 'Bitki listesine her bitki için bir fotoğraf alanı ekle.', 2 * MIN, { state: 'sirada', run: null })
  tl.set(309, [mk('user', j309.rawText, 2 * MIN)])

  const j310 = extra(310, 2, 'Sulama hesabındaki yuvarlama hatasını düzelt, testler geçsin.', 18 * MIN, { state: 'karar_bekliyor', lastVerdict: 'unchecked', attempts: 2, run: { status: 'done', lastLine: 'Kanıt sayılmadı', startedAt: iso(18 * MIN) },
    baseNote: null })
  tl.set(310, [
    mk('user', j310.rawText, 18 * MIN),
    done('Klasörü inceledi', 17 * MIN),
    done('sulama.js dosyasını okudu', 16 * MIN),
    done('2 dosya değiştirdi', 13 * MIN),
    done('Testleri çalıştırdı', 12 * MIN),
    mk('proof', 'Kanıt sayılmadı: test dosyası değişti', 11 * MIN, { proof: { verdict: 'unchecked', attempt: 2, summary: 'Test dosyası değişti', results: results([['Testler geçsin', 'passed', 4100, 'Tests: 12 passed, 12 total']]),
      reasonCode: 'tests_changed', reasonText: 'Ajan, geçmeyen bir testi değiştirerek işi geçmiş gösteriyor olabilir. Bu yüzden sonucu kanıt saymadım.', changedFiles: ['test/sulama.test.js'], addedTests: [], countUnknown: false } }),
  ])

  const j311 = extra(311, 3, 'Sipariş listesine arama kutusu ekle.', 6 * MIN, { state: 'baslatilamadi', startError: 'Claude programı bulunamadı', run: { status: 'failed', lastLine: 'Başlatılamadı', failReason: 'claude_missing' } })
  tl.set(311, [mk('user', j311.rawText, 6 * MIN)])

  const j312 = extra(312, 4, 'Harcama dosyasının ay adlarını Türkçe yap.', 3 * HOUR, { state: 'gecti', acceptedByUser: true, lastVerdict: 'passed', attempts: 2, finishedAt: iso(2 * HOUR + 40 * MIN), run: { status: 'done', lastLine: 'Sen onayladın', startedAt: iso(3 * HOUR) } })
  tl.set(312, [
    mk('user', j312.rawText, 3 * HOUR),
    done('Harcama dosyasını okudu', 3 * HOUR - 2 * MIN),
    done('pytest ayarını güncelledi', 3 * HOUR - 6 * MIN),
    mk('proof', 'Kanıt sayılmadı: test tanımı değişti', 2 * HOUR + 50 * MIN, { proof: { verdict: 'unchecked', attempt: 2, summary: 'Test tanımı değişti', results: results([['Testler geçsin', 'passed', 2900, '14 passed in 2.8s']]),
      reasonCode: 'definition_changed', reasonText: 'Ajan, işi denemekte kullandığım komutun tanımını değiştirdi: pytest.ini. Kanıt sayamam.', changedFiles: ['pytest.ini'], addedTests: ['test_aylar.py'], countUnknown: false } }),
    mk('system', 'Değişikliği sen kabul ettin', 2 * HOUR + 40 * MIN),
  ])

  const j313 = extra(313, 5, 'Bülten başlık şablonuna tarih ekle.', 4 * HOUR, { state: 'gecti', lastVerdict: 'passed', attempts: 1, finishedAt: iso(3 * HOUR + 50 * MIN), run: { status: 'done', lastLine: 'Kanıtıyla bitti', startedAt: iso(4 * HOUR) } })
  tl.set(313, [
    mk('user', j313.rawText, 4 * HOUR),
    done('Şablonu okudu', 4 * HOUR - 3 * MIN),
    mk('proof', 'Kanıtıyla bitti: 1 ölçütün hepsi geçti', 3 * HOUR + 52 * MIN, { proof: { verdict: 'passed', attempt: 1, summary: '1 ölçütün hepsi geçti', results: results([['Testler geçsin', 'passed', 1700, 'derleme tamam, çıktı yok']]), countUnknown: true } }),
  ])

  // ---- u4: onayladığın testlerle verilen sonuç (cümleler gerçek sunucu cevaplarından alındı) ----
  const j314 = extra(314, 2, 'Sulama hesabındaki hafta günü adlarını Türkçe yap.', 30 * MIN, { state: 'karar_bekliyor', lastVerdict: 'unchecked', lastRunId: 314, attempts: 1, run: { status: 'done', lastLine: 'Kanıt sayılmadı', startedAt: iso(30 * MIN) } })
  tl.set(314, [
    mk('user', j314.rawText, 30 * MIN),
    done('Klasörü inceledi', 29 * MIN),
    done('sulama.js dosyasını okudu', 27 * MIN),
    done('Testleri düzenledi', 24 * MIN),
    mk('proof', 'kanıt: onayladığın testler geçmiyor (tur 1)', 22 * MIN, { proof: { verdict: 'unchecked', attempt: 1, summary: null, results: results([['Testler geçsin', 'passed', 2100, 'Tests: 2 passed, 2 total']]),
      reasonCode: 'approved_failed', verdictLabel: 'onayladığın testler geçmiyor', baselineText: 'Başlamadan önce 2 testin hepsi geçiyordu. Şimdi 2’si de geçiyor.', snapshotId: 'ornek-goruntu-314',
      reasonText: 'Onayladığın 2 testten 1’i yeni kodla geçmiyor. Geçmeyen: gunAdi. Ajan bu testleri de değiştirmiş. Değişiklik gerekli olabilir (ör. bir ad değişti) ama kararı sen ver.',
      diffs: [
        { path: 'test/sulama.test.js', kind: 'test', sentence: 'Test dosyası değişti: test/sulama.test.js (1 var olan satır değişti ya da silindi).', lines: [{ op: '-', text: "test('gunAdi', () => { assert.equal(gunAdi(1), 'Monday'); });" }, { op: '+', text: "test('gunAdi', () => { assert.equal(gunAdi(1), 'Pazartesi'); });" }] },
        { path: 'onaylı testler', kind: 'approved-fail', sentence: 'Onayladığın testlerden geçmeyenler', lines: [{ op: '-', text: 'gunAdi' }] },
      ] } }),
  ])
  const j315 = extra(315, 3, 'Sipariş listesine toplam tutar sütunu ekle.', 40 * MIN, { state: 'karar_bekliyor', lastVerdict: 'unchecked', lastRunId: 315, attempts: 1, run: { status: 'done', lastLine: 'Kanıt sayılmadı', startedAt: iso(40 * MIN) } })
  tl.set(315, [
    mk('user', j315.rawText, 40 * MIN),
    done('Klasörü inceledi', 39 * MIN),
    done('Yeni bir test ekledi', 34 * MIN),
    mk('proof', 'kanıt: ajanın yeni testleri geçmiyor (tur 1)', 31 * MIN, { proof: { verdict: 'unchecked', attempt: 1, summary: null, results: results([['Testler geçsin', 'failed', 2400, 'Tests: 1 failed, 18 passed, 19 total']]),
      reasonCode: 'agent_tests_fail', verdictLabel: 'ajanın yeni testleri geçmiyor', baselineText: 'Başlamadan önce 18 testin hepsi geçiyordu. Şimdi 19 testten 1’i geçmiyor.', snapshotId: 'ornek-goruntu-315',
      reasonText: 'Onayladığın testler geçiyor ama ajanın eklediği yeni testler geçmiyor. Geçmeyen: toplam sütunu. Kararı sen ver.',
      diffs: [{ path: 'ajanın testleri', kind: 'agent-fail', sentence: 'Ajanın testlerinden geçmeyenler', lines: [{ op: '-', text: 'toplam sütunu' }] }] } }),
  ])
  const j316 = extra(316, 4, 'Bülten şablonundaki yazım hatalarına bak.', 50 * MIN, { state: 'gecti', lastVerdict: 'passed', lastRunId: 316, attempts: 1, finishedAt: iso(45 * MIN), run: { status: 'done', lastLine: 'Ajan hiçbir dosyayı değiştirmedi', startedAt: iso(50 * MIN) } })
  tl.set(316, [
    mk('user', j316.rawText, 50 * MIN),
    done('Şablonu okudu', 48 * MIN),
    mk('proof', 'kanıt: Ajan hiçbir dosyayı değiştirmedi (tur 1)', 45 * MIN, { proof: { verdict: 'passed', attempt: 1, summary: 'Ajan hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu. 1 ölçüt geçti.', results: results([['Testler geçsin', 'passed', 1500, 'Tests: 6 passed, 6 total']]),
      reasonCode: 'no_change', verdictLabel: 'Ajan hiçbir dosyayı değiştirmedi', reasonText: 'Ajan hiçbir dosyayı değiştirmedi. Testler zaten geçiyordu. 1 ölçüt geçti.', baselineText: 'Başlamadan önce 6 testin hepsi geçiyordu. Şimdi 6’sı da geçiyor.' } }),
  ])
  const j317 = extra(317, 5, 'Harcama sayfasına yeni bir toplam testi ekle.', 70 * MIN, { state: 'gecti', lastVerdict: 'passed', lastRunId: 317, attempts: 1, finishedAt: iso(60 * MIN), run: { status: 'done', lastLine: 'Kanıtıyla bitti', startedAt: iso(70 * MIN) },
    testOffer: { added: 1, edited: 0, sentence: 'Ajan 1 yeni test ekledi. Bunları da onaylı testlere katayım mı?' } })
  tl.set(317, [
    mk('user', j317.rawText, 70 * MIN),
    done('Harcama dosyasını okudu', 68 * MIN),
    done('Yeni bir test ekledi', 64 * MIN),
    mk('proof', 'kanıt: geçti (tur 1)', 60 * MIN, { proof: { verdict: 'passed', attempt: 1, summary: '1 ölçüt geçti. Ajan testleri de düzenledi. Sonuç senin onayladığın testlerle verildi. Not: Yeni test dosyası eklendi: test/toplam.test.js.', results: results([['Testler geçsin', 'passed', 1900, 'Tests: 3 passed, 3 total']]),
      baselineText: 'Başlamadan önce 2 testin hepsi geçiyordu. Şimdi 3’ü de geçiyor.', addedTests: ['test/toplam.test.js'], notes: ['Yeni test dosyası eklendi: test/toplam.test.js'] } }),
  ])
}

/** u4: örnek kipte işin son kanıt satırı (Sıra bende ve kanıt kaydı cevabı için). */
export function mockLastProof(jobId: number): TimelineProof | null {
  seed()
  const l = [...(tl.get(jobId) ?? [])].reverse().find((x) => x.kind === 'proof' && x.proof)
  return l?.proof ?? null
}

function items(id: number): TimelineItem[] {
  seed()
  let l = tl.get(id)
  if (!l) {
    l = []
    tl.set(id, l)
  }
  return l
}

// ---- sunucu biçimi (JobsProofEndpoints.cs): satırlarda kimlik yok, kanıt sonucu ayrı uçtan, durum run alanında ----
const proofs = new Map<number, ProofRun>()
const proofIds = new WeakMap<TimelineItem, number>()
let nextProof = 9000

function toWire(x: TimelineItem, j: Job): WireTimelineItem {
  const base = { at: x.ts, runId: 1, id: `l${x.id}` }
  if (x.kind === 'approval') {
    const st = x.state === 'pending' ? 'pending' : x.state === 'denied' ? 'denied' : 'used'
    const pre = st === 'pending' ? 'senden onay bekliyor' : st === 'denied' ? 'reddettin' : 'onayladın'
    return { ...base, id: `a${x.eventId ?? x.id}`, kind: 'approval', text: pre + ': ' + (x.detail ?? x.text), detail: 'Bash', eventId: x.eventId ?? null, status: st }
  }
  if (x.kind === 'proof' && x.proof) {
    let id = proofIds.get(x)
    if (id === undefined) {
      id = nextProof++
      proofIds.set(x, id)
    }
    const pid = id
    const p = x.proof
    proofs.set(pid, {
      id: pid, projectId: j.projectId, jobId: j.id, sessionId: null, trigger: 'stop_hook', attempt: p.attempt, startedAt: x.ts, finishedAt: x.ts, verdict: p.verdict, summary: p.summary ?? null,
      results: p.results.map((r, i) => ({ id: pid * 10 + i, runId: pid, checkId: i + 1, label: r.label, status: r.status, exitCode: r.status === 'failed' ? 1 : 0, outputTail: r.outputTail ?? null, artifactUrl: r.artifactUrl ?? null, durationMs: r.durationMs ?? 0 })),
    })
    const results = p.results.map((r) => ({ label: r.label, status: r.status, durationMs: r.durationMs ?? 0, outputTail: r.outputTail ?? null, artifactUrl: r.artifactUrl ?? null, artifactKind: r.artifactUrl ? (r.artifactUrl.startsWith('data:image') ? ('image' as const) : ('text' as const)): null }))
    return { ...base, id: `p${pid}`, kind: 'proof', text: 'kanıt: ' + (p.verdict === 'passed' ? 'geçti' : p.verdict === 'failed' ? 'kaldı' : 'ölçüt yok') + ' (tur ' + p.attempt + ')', detail: p.summary ?? null, verdict: p.verdict, proofRunId: pid, attempt: p.attempt, results,
      reasonCode: p.reasonCode ?? null, reasonText: p.reasonText ?? null, verdictLabel: p.verdictLabel ?? null, baselineText: p.baselineText ?? null, changedFiles: p.changedFiles ?? [], addedTests: p.addedTests ?? [], countUnknown: !!p.countUnknown, snapshotId: p.snapshotId ?? null, diffs: p.diffs ?? [], notes: p.notes ?? [] }
  }
  const kind = x.kind === 'guard' || x.kind === 'system' ? 'state' : x.kind
  return { ...base, kind, text: x.text }
}

function emitState(j: Job) {
  const pa = j.run?.pendingApproval ?? (j.pendingApprovals?.[0] ? { ...j.pendingApprovals[0], tool: 'Bash' } : null)
  emitLive('job.state', {
    jobId: j.id, runId: 1, status: j.run?.status ?? 'running', jobState: j.state, lastLine: j.run?.lastLine ?? null, failReason: null, exitCode: null,
    approval: pa ? { eventId: pa.eventId, tool: pa.tool ?? 'Bash', summary: pa.summary } : null,
    acceptedByUser: !!j.acceptedByUser, startError: j.startError ?? null, question: j.question ?? null, pendingApprovals: j.pendingApprovals ?? [],
  })
}

// Aynı anda çalışan iş sayısı ayardaki sınırı (maxParallelRuns) aşarsa iş "sirada" kalır, biri bitince kendiliğinden başlar.
const waitq: number[] = []
const runningCount = () => mock2State().jobs.filter((x) => x.state === 'calisiyor' && x.run?.status === 'running').length
const limit = () => Math.max(1, Math.min(6, Number(settings.maxParallelRuns ?? 3)))
function drain() {
  while (waitq.length > 0 && runningCount() < limit()) {
    const id = waitq.shift()!
    const q = mock2State().jobs.find((x) => x.id === id)
    if (q && q.state === 'sirada') void run(q, 'start')
  }
}

function setJob(j: Job, patch: Partial<Job>) {
  Object.assign(j, patch)
  emitState(j)
  if (patch.state && patch.state !== 'calisiyor') queueMicrotask(drain)
}

function push(j: Job, kind: TimelineItem['kind'], text: string, extra: Partial<TimelineItem> = {}): TimelineItem {
  const l = items(j.id)
  for (const x of l) if (kind !== 'user' && kind !== 'agent' && x.kind === 'progress' && x.state === 'active') x.state = 'done'
  const it: TimelineItem = { id: nid(), ts: new Date().toISOString(), kind, text, ...extra }
  l.push(it)
  if (kind === 'progress') j.run = { ...(j.run ?? { status: 'running' }), lastLine: text }
  // Sunucuda yalnız ajan satırları job.progress olur. Kanıt 'proof' olayıyla, onay job.state ile (arayüz akışı yeniden okur).
  if (kind === 'proof') emitLive('proof', { jobId: j.id })
  else if (kind !== 'approval') emitLive('job.progress', { jobId: j.id, runId: 1, lineId: it.id, ...toWire(it, j) })
  return it
}

const say = (j: Job, text: string) => push(j, 'progress', text, { state: 'active' })

const alive = (j: Job, tok: number) => tokens.get(j.id) === tok

async function finish(j: Job, tok: number) {
  const { checks } = mock2State()
  const list = checks.filter((c) => c.projectId === j.projectId && c.enabled && (c.jobId === null || c.jobId === j.id))
  const bad = /bozuk/i.test(j.rawText)
  let attempt = (j.attempts ?? 0) + 1
  const finalAttempt = Math.max(3, attempt)
  // Örnek tetikleyici: işte "test dosyasını değiştir" geçerse ajan testi değiştirmiş sayılır, karar Kemal'e kalır.
  if (list.length > 0 && /test dosyas[ıi]n[ıi] değiştir/i.test(j.rawText)) {
    await sleep(STEP)
    if (!alive(j, tok)) return
    push(j, 'proof', 'Kanıt sayılmadı: test dosyası değişti', { proof: { verdict: 'unchecked', attempt, summary: 'Test dosyası değişti', results: list.map((c, i) => ({ label: c.label, status: 'passed' as const, durationMs: 300 + i * 410, outputTail: null, artifactUrl: null })),
      reasonCode: 'tests_changed', reasonText: 'Ajan, geçmeyen bir testi değiştirerek işi geçmiş gösteriyor olabilir. Bu yüzden sonucu kanıt saymadım.', changedFiles: ['test/hesap.test.js'], addedTests: [], countUnknown: false, snapshotId: 'a1b2c3d4e5f60718',
      diffs: [{ path: 'test/hesap.test.js', kind: 'test', sentence: 'Bir test atlandı.', lines: [{ op: '-', text: "test('toplama iki sayiyi toplar', () => {" }, { op: '+', text: "test.skip('toplama iki sayiyi toplar', () => {" }] }] } })
    setJob(j, { state: 'karar_bekliyor', lastVerdict: 'unchecked', attempts: attempt, finishedAt: new Date().toISOString(), run: { status: 'done', lastLine: 'Kanıt sayılmadı' } })
    return
  }
  if (list.length === 0) {
    await sleep(STEP)
    if (!alive(j, tok)) return
    push(j, 'proof', 'Kanıtsız: denenecek ölçüt yok', { proof: { verdict: 'unchecked', attempt, summary: 'Ajan bitti dedi ama bu klasörde denenecek bir ölçüt yok. Söz bir şey kanıtlayamadı.', results: [], reasonCode: 'no_checks', reasonText: null } })
    setJob(j, { state: 'verildi', lastVerdict: 'unchecked', attempts: attempt, failedLabels: [], finishedAt: new Date().toISOString(), run: { status: 'done', lastLine: 'Kanıtsız bitti' } })
    return
  }
  for (;;) {
    await sleep(STEP)
    if (!alive(j, tok)) return
    const failed = bad
    const res: TimelineProofResult[] = list.map((c, i) => {
      const isTest = c.kind === 'command' && /test/.test(String(c.spec.cmd ?? ''))
      const f = failed && isTest
      return { label: c.label, status: f ? 'failed' : 'passed', durationMs: 300 + i * 410, outputTail: f ? 'Tests: 1 failed, 18 passed, 19 total\n  ✕ toplam doğru hesaplanır (4 ms)' : null, artifactUrl: c.kind === 'screenshot' ? shot(j.projectName ?? 'Sayfa', !f) : null }
    })
    const nFail = res.filter((r) => r.status === 'failed').length
    const verdict: Verdict = nFail ? 'failed' : 'passed'
    push(j, 'proof', verdict === 'passed' ? `Kanıtıyla bitti: ${res.length} ölçütün hepsi geçti` : `Kaldı: ${nFail} ölçüt tutmadı`, {
      proof: { verdict, attempt, summary: verdict === 'passed' ? `${res.length} ölçütün hepsi geçti` : `${nFail} ölçütten ${nFail}’i tutmadı`, results: res },
    })
    const failedLabels = res.filter((r) => r.status === 'failed').map((r) => r.label)
    if (verdict === 'passed') {
      setJob(j, { state: 'gecti', lastVerdict: 'passed', attempts: attempt, failedLabels: [], finishedAt: new Date().toISOString(), run: { status: 'done', lastLine: 'Kanıtıyla bitti' } })
      return
    }
    if (attempt >= finalAttempt) {
      setJob(j, { state: 'kaldi', lastVerdict: 'failed', attempts: attempt, failedLabels, finishedAt: new Date().toISOString(), run: { status: 'failed', lastLine: 'Kaldı' } })
      return
    }
    push(j, 'system', `Test kaldı, işi ajana geri yolladım (${attempt}/3)`)
    attempt++
    setJob(j, { attempts: attempt - 1 })
    await sleep(STEP)
    if (!alive(j, tok)) return
    say(j, 'Hatayı düzeltiyor')
    await sleep(STEP)
    if (!alive(j, tok)) return
    say(j, 'Testleri yeniden çalıştırıyor')
  }
}

async function run(j: Job, mode: 'start' | 'reply' | 'resume' | 'denied') {
  const tok = (tokens.get(j.id) ?? 0) + 1
  tokens.set(j.id, tok)
  const startRun: JobRun = { status: 'running', lastLine: 'Başlıyor', startedAt: new Date().toISOString() }
  setJob(j, { state: 'calisiyor', agentState: null, question: null, startError: null, pendingApprovals: null, acceptedByUser: false, run: startRun, givenAt: j.givenAt ?? new Date().toISOString() })
  const steps =
    mode === 'start' ? ['Klasörü inceliyor', 'İlgili dosyaları okuyor', 'Değişikliği yazıyor', '2 dosya değiştirdi', 'Testleri çalıştırıyor']
    : mode === 'reply' ? ['Cevabını okuyor', 'Değişikliği düzeltiyor', 'Testleri çalıştırıyor']
    : mode === 'resume' ? ['Onayını aldı, silme işlemini yaptı', 'Raporu hazırlıyor', 'Testleri çalıştırıyor']
    : ['Komutu çalıştırmadı, başka yol arıyor', 'Raporu hazırlıyor', 'Testleri çalıştırıyor']
  for (let i = 0; i < steps.length; i++) {
    await sleep(STEP)
    if (!alive(j, tok)) return
    say(j, steps[i])
    if (mode === 'start' && i === 2 && /\b(sil|temizle|sileyim)/i.test(j.rawText)) {
      await sleep(STEP)
      if (!alive(j, tok)) return
      const ap = push(j, 'approval', 'Ajan bir klasörü silmek istiyor. Silinsin mi?', { detail: 'rm -rf eski-yedekler/2025', state: 'pending', eventId: nextEvent++ })
      const list: PendingApproval[] = [{ eventId: ap.eventId!, summary: ap.detail ?? '', detail: 'Bash', at: ap.ts }]
      if (/iki onay/i.test(j.rawText)) {
        const ap2 = push(j, 'approval', 'Ajan bir klasörü daha silmek istiyor. Silinsin mi?', { detail: 'rm -rf eski-yedekler/2024', state: 'pending', eventId: nextEvent++ })
        list.push({ eventId: ap2.eventId!, summary: ap2.detail ?? '', detail: 'Bash', at: ap2.ts })
      }
      setJob(j, { state: 'onay_bekliyor', pendingApprovals: list, run: { status: 'waiting_approval', lastLine: 'Senin onayını bekliyor' } })
      return
    }
  }
  await finish(j, tok)
}

function jobOf(id: number): Job {
  seed()
  const j = mock2State().jobs.find((x) => x.id === id)
  if (!j) throw new ApiError(404, 'İş bulunamadı.')
  return j
}

export async function mock3Handle(method: string, p: string, _q: URLSearchParams, body: unknown): Promise<unknown> {
  const b = (body ?? {}) as Record<string, unknown>
  let m: RegExpMatchArray | null
  if (p.startsWith('/api/jobs') || p === '/api/now' || p === '/api/morning') seed()

  if (p === '/api/projects' && method === 'POST') {
    const path = String(b.path ?? '').trim()
    if (!path) throw new ApiError(400, 'Klasör yolunu yaz.')
    if (!/^[a-z]:[\\/]|^[\\/]|^~/i.test(path)) throw new ApiError(400, 'Bu bir klasör yolu gibi görünmüyor. Örnek: D:\\projeler\\bahce')
    await sleep(80)
    let pr = projects.find((x) => x.path && x.path.toLowerCase() === path.toLowerCase())
    const created = !pr
    if (!pr) {
      const name = path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path
      pr = { id: nextProj++, name, path, utterances: 0, proposed: 0, approved: 0, repeats: 0, lastAt: new Date().toISOString(), gateEnabled: false }
      projects.push(pr)
    }
    const sug = (await mock2Handle('POST', '/api/projects/' + pr.id + '/checks/suggest', new URLSearchParams(), {})) as { checks: Check[] }
    const suggested = sug.checks.filter((c) => c.kind === 'command' && c.jobId === null)
    const cmds = suggested.map((c) => '`' + String(c.spec.cmd ?? c.label) + '`')
    const card = cmds.length === 0 ? 'Bu klasörde otomatik bir test bulamadım. İş bitince neyi deneyeyim?' : 'Bu klasörde iş bitince şunu çalıştırıp deneyeceğim: ' + cmds.join(', ') + '. Olur mu?'
    return { id: pr.id, name: pr.name, path: pr.path, created, gateEnabled: !!pr.gateEnabled, suggested, card }
  }

  m = p.match(/^\/api\/projects\/(\d+)\/approve-setup$/)
  if (m && method === 'POST') {
    const pr = projects.find((x) => x.id === Number(m![1]))
    if (!pr) throw new ApiError(404, 'Proje bulunamadı.')
    await sleep(120)
    const ids = Array.isArray(b.checkIds) ? (b.checkIds as number[]) : null
    for (const c of mock2State().checks) if (c.projectId === pr.id && c.jobId === null && (ids ? ids.includes(c.id) : !c.enabled)) c.enabled = true
    if (b.gate !== false) pr.gateEnabled = true
    return { gate: pr, checks: mock2State().checks.filter((c) => c.projectId === pr.id && c.jobId === null), guardTemplate: b.guardTemplate ? 'temkinli' : null, guardRulesCreated: b.guardTemplate ? 4 : 0 }
  }

  m = p.match(/^\/api\/proof\/runs\/(\d+)$/)
  if (m && proofs.has(Number(m[1]))) return proofs.get(Number(m[1]))
  // iş başına son kanıt kaydı (neden kodu dahil): Liste, Sabah ve Sıra bende "Değişiklik yok" etiketini buradan okur
  if (p === '/api/proof/runs' && method === 'GET' && _q.get('jobId')) {
    seed()
    const jid = Number(_q.get('jobId'))
    const pr = mockLastProof(jid)
    const jb = mock2State().jobs.find((x) => x.id === jid)
    if (pr && jb) return [{ id: jb.lastRunId ?? 0, projectId: jb.projectId, jobId: jid, sessionId: null, trigger: 'stop_hook', attempt: pr.attempt, startedAt: jb.createdAt, finishedAt: jb.finishedAt ?? null, verdict: pr.verdict, summary: pr.summary ?? null, reasonCode: pr.reasonCode ?? null }]
  }

  m = p.match(/^\/api\/jobs\/(\d+)\/(start|reply|stop|approve|timeline|accept|continue|seen|approve-new-tests)$/)
  if (m) {
    const id = Number(m[1])
    const act = m[2]
    const j = jobOf(id)
    if (act === 'timeline') return { jobId: id, state: j.state, agentState: j.agentState ?? null, run: j.run ?? null, acceptedByUser: !!j.acceptedByUser, startError: j.startError ?? null, question: j.question ?? null, pendingApprovals: j.pendingApprovals ?? [], baseNote: j.baseNote ?? null, items: items(id).filter((x) => x.kind !== 'guard').map((x) => toWire(x, j)) }
    if (act === 'seen') return { ok: true }
    if (act === 'approve-new-tests') {
      if (!j.testOffer) throw new ApiError(409, 'Bu işte onay bekleyen yeni test teklifi yok.')
      setJob(j, { testOffer: null })
      return { job: { ...j } }
    }
    if (act === 'start') {
      if (j.state === 'calisiyor' && j.run?.status === 'running') throw new ApiError(409, 'Bu iş zaten çalışıyor.')
      if (items(id).length === 0) items(id).push({ id: nid(), ts: j.createdAt, kind: 'user', text: j.rawText })
      if (runningCount() >= limit()) {
        waitq.push(id)
        setJob(j, { state: 'sirada', run: null, startError: null })
        return { run: null, job: { ...j } }
      }
      void run(j, 'start')
      return { run: { id: 1, status: 'running' }, job: { ...j } }
    }
    if (act === 'reply') {
      const text = String(b.text ?? '').trim()
      if (!text) throw new ApiError(400, 'Mesaj boş.')
      items(id).push({ id: nid(), ts: new Date().toISOString(), kind: 'user', text })
      if (j.state === 'gecti' || j.state === 'iptal') throw new ApiError(409, 'Bu iş bitmiş. Cevap yazılamaz.')
      void run(j, 'reply')
      return { run: { id: 1, status: 'running' }, job: { ...j } }
    }
    if (act === 'stop') {
      tokens.set(id, (tokens.get(id) ?? 0) + 1)
      for (const x of items(id)) if (x.kind === 'progress' && x.state === 'active') x.state = 'done'
      push(j, 'system', 'Durdurdun. Ajanın süreci kapatıldı.')
      const qi = waitq.indexOf(id)
      if (qi >= 0) waitq.splice(qi, 1)
      setJob(j, { state: 'durduruldu', pendingApprovals: null, run: { status: 'stopped', lastLine: 'Durduruldu' } })
      return { stopped: true, job: { ...j } }
    }
    if (act === 'approve') {
      const ev = Number(b.eventId)
      const it = items(id).find((x) => x.kind === 'approval' && x.eventId === ev)
      if (!it) throw new ApiError(404, 'Bu işte böyle bir onay isteği yok.')
      if (it.state !== 'pending') throw new ApiError(409, 'Bu onay isteğine zaten cevap verilmiş.')
      const allow = b.allow === true
      it.state = allow ? 'allowed' : 'denied'
      const rest = (j.pendingApprovals ?? []).filter((x) => x.eventId !== ev)
      if (rest.length > 0) {
        setJob(j, { pendingApprovals: rest })
        return { run: { id: 1, status: 'waiting_approval' }, job: { ...j } }
      }
      setJob(j, { pendingApprovals: null, run: { status: 'running', lastLine: allow ? 'Onayını aldı' : 'Reddettin' } })
      void run(j, allow ? 'resume' : 'denied')
      return { run: { id: 1, status: 'running' }, job: { ...j } }
    }
    if (act === 'accept') {
      if (j.state !== 'karar_bekliyor') throw new ApiError(409, 'Bu iş senin kararını beklemiyor.')
      if (!b.snapshotId) throw new ApiError(400, 'Kabul, ekrandaki kartın kimliğini taşımalı.')
      if (b.accept === true) {
        push(j, 'system', 'Değişikliği sen kabul ettin')
        setJob(j, { state: 'gecti', acceptedByUser: true, lastVerdict: 'passed', finishedAt: new Date().toISOString(), run: { status: 'done', lastLine: 'Sen onayladın' } })
      } else {
        push(j, 'system', 'Değişikliği kabul etmedin')
        setJob(j, { state: 'kaldi', lastVerdict: 'failed', finishedAt: new Date().toISOString(), run: { status: 'failed', lastLine: 'Kaldı' } })
      }
      return { job: { ...j } }
    }
    if (act === 'continue') {
      if (!['gecti', 'kaldi', 'iptal', 'durduruldu'].includes(j.state)) throw new ApiError(409, 'Bu iş zaten çalışıyor ya da senden bir şey bekliyor.')
      const text = String(b.text ?? '').trim()
      if (text) items(id).push({ id: nid(), ts: new Date().toISOString(), kind: 'user', text })
      void run(j, 'reply')
      return { run: { id: 1, status: 'running' }, job: { ...j } }
    }
  }

  m = p.match(/^\/api\/projects\/(\d+)\/hooks\/install$/)
  if (m && method === 'POST') {
    const pr = projects.find((x) => x.id === Number(m![1]))
    if (!pr) throw new ApiError(404, 'Proje bulunamadı.')
    await sleep(120)
    return { settingsPath: `${pr.path ?? ''}\\.claude\\settings.json`, exe: 'soz.exe', installed: true, hooks: ['PreToolUse', 'Stop'] }
  }
  return NOT_HANDLED
}
