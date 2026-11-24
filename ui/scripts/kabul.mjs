// Klavye-yalnız kabul testi + ölçüm. Üretim derlemesine karşı (vite preview). Yalnız örnek (uydurma) veri.
import { launch } from './pw.mjs'
import { rmSync } from 'node:fs'
const BASE = process.env.BASE ?? 'http://127.0.0.1:49412'
const { ctx, profile } = await launch()
const errors = []
const rows = []
let cur = 'başlangıç'
const ok = (name, cond, extra = '') => { cur = name; rows.push([cond ? 'GEÇTİ' : 'KALDI', name, extra]) }
async function open(url, w = 1440, h = 900) {
  const page = await ctx.newPage()
  page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') errors.push(`${m.type()} [${cur}]: ${m.text()}`) })
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message))
  await page.setViewportSize({ width: w, height: h })
  await page.emulateMedia({ colorScheme: 'light' })
  await page.goto(BASE + url); await page.reload()
  await page.waitForSelector('[data-newjob]', { timeout: 5000 }).catch(() => {})
  return { page }
}
const waitFor = (page, sel, ms = 8000) => page.waitForSelector(sel, { timeout: ms, state: 'attached' }).then(() => true, () => false)
// d2u: her yeni iş önce "Bitti sayılması için" kartını açar; iş ancak kart kabul edilince (Enter = ana düğme) başlar. Kart gelmezse false döner.
// u3: klasörde ölçüt varsa tek satır özet çıkar, Ctrl+Enter kartsız doğrudan başlatır; o zaman kabul edilecek kart yoktur.
const acceptDone = async (page) => { if (await page.evaluate(() => !!document.querySelector('[data-summary-line]'))) return false; const shown = await waitFor(page, '[data-done-accept]:not([disabled])', 6000); if (shown) await page.keyboard.press('Enter'); return shown }
const waitFn = (page, fn, arg, ms = 4000) => page.waitForFunction(fn, arg, { timeout: ms }).then(() => true, () => false)

// Sayfa içi ölçüm: keydown -> koşul sağlanınca -> iki rAF sonrası süre
async function measure(page, pred, press) {
  await page.evaluate((src) => {
    window.__m = { done: false, ms: null }
    const f = new Function('return (' + src + ')')()
    let t0 = 0
    window.addEventListener('keydown', () => { t0 = performance.now() }, { capture: true, once: true })
    const obs = new MutationObserver(() => {
      if (f()) {
        obs.disconnect()
        requestAnimationFrame(() => requestAnimationFrame(() => { window.__m = { done: true, ms: performance.now() - t0 } }))
      }
    })
    obs.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true })
  }, pred)
  await press()
  await page.waitForFunction(() => window.__m.done, null, { timeout: 4000 }).catch(() => {})
  return await page.evaluate(() => window.__m.ms)
}

const firstContent = []
try {
  // ---- A. İlk açılış, klavye yalnız ----
  {
    const { page } = await open('/?bos=1&hiz=1#/is')
    const nav = await page.evaluate(() => { const n = performance.getEntriesByType('navigation')[0]; return { dcl: n.domContentLoadedEventEnd, load: n.loadEventEnd } })
    const fcp = await page.evaluate(() => performance.getEntriesByName('first-contentful-paint')[0]?.startTime ?? null)
    firstContent.push({ dcl: Math.round(nav.dcl), load: Math.round(nav.load), fcp: fcp && Math.round(fcp) })
    let actions = 0, keys = 0
    await page.keyboard.insertText('Hesap makinesine yüzde düğmesi ekle'); actions++; keys++
    await page.keyboard.press('Control+Enter'); actions++; keys++
    ok('A1 Ctrl+Enter sonrası odak klasör alanında', await page.evaluate(() => /INPUT|TEXTAREA/.test(document.activeElement?.tagName ?? '') && document.activeElement?.id !== 'ob-text'))
    await page.keyboard.insertText('D:/projeler/hesap-makinesi'); await page.keyboard.press('Enter'); actions++; keys += 2
    ok('A2 onay kartı geldi', await waitFor(page, '[data-setup] [data-open-all]'))
    ok('A3 onay vermeden hiçbir iş çalışmıyor', await page.evaluate(() => !document.querySelector('.prog, [data-proof]')))
    await page.keyboard.press('Enter'); actions++; keys++
    ok('A3b bitti tanımı kartı geldi, kabul edilmeden iş başlamadı', await waitFor(page, '[data-done-accept]:not([disabled])', 6000) && await page.evaluate(() => !document.querySelector('.prog, [data-proof]')))
    await page.keyboard.press('Enter'); actions++; keys++
    ok('A4 Hepsini aç → iş kendiliğinden başladı', await waitFor(page, '.prog', 4000))
    const got = await waitFor(page, '[data-proof]', 12000)
    ok('A5 kanıt kartı geldi', got, await page.evaluate(() => document.querySelector('[data-proof]')?.getAttribute('data-proof') ?? 'kart yok'))
    ok('A6 eylem sayısı <= 5', actions <= 5, `${actions} eylem (yaz, gönder, klasörü yaz ve Enter, onayla, bitti tanımını kabul et), tuş düzeyinde ${keys}, 0 komut, 0 terminal`)
    await page.close()
  }
  // ---- B. Günlük kullanım ----
  {
    const { page } = await open('/?ornek=1&hiz=1#/is')
    await page.keyboard.press('Escape')
    const n = await measure(page, "() => document.activeElement && document.activeElement.matches('[data-newjob]')", () => page.keyboard.press('n'))
    ok('B1 n → yeni iş kutusuna odak', n !== null, `${n?.toFixed(1)} ms`)
    await page.keyboard.insertText('Yüzde hesabına negatif sayı denetimi ekle')
    await page.keyboard.press('Control+Enter')
    ok('B1b Ctrl+Enter → bitti tanımı kartı (klasörde ölçüt varsa tek satır özetle kartsız başlar)', (await waitFor(page, '[data-done-accept]:not([disabled])', 2500)) || (await page.evaluate(() => !!document.querySelector('[data-summary-line]'))))
    const c = await measure(page, "() => /negatif sayı denetimi/.test(document.querySelector('#job-list')?.innerText ?? '')", () => page.keyboard.press('Enter'))
    ok('B2 kartı kabul et → iş listede (iyimser)', c !== null, `${c?.toFixed(1)} ms`)
    ok('B3 iş kanıtla bitti', await waitFor(page, '[data-proof]', 12000))
    await page.close()
  }
  // ---- C. Bekçi onayı ----
  {
    const { page } = await open('/?ornek=1&hiz=1#/is')
    await page.keyboard.press('Escape'); await page.keyboard.press('n'); await page.keyboard.insertText('Eski derleme klasörünü sil'); await page.keyboard.press('Control+Enter'); await acceptDone(page)
    ok('C1 bekçi onay kartı satır içinde (yeni işin kendi kartı)', await waitFn(page, () => /Eski derleme/.test(document.querySelector('[data-job-title]')?.textContent ?? '') && !!document.querySelector('[data-approve="yes"]'), null, 8000))
    await page.keyboard.press('Escape')
    await page.keyboard.press('e')
    ok('C2 e → onay verildi', await waitFn(page, () => !document.querySelector('[data-approve="yes"]'), null, 4000))
    ok('C3 onaydan sonra iş kanıt verdi', await waitFor(page, '[data-proof]', 12000))
    await page.close()
  }
  // ---- D. Kaldı ----
  {
    const { page } = await open('/?ornek=1&hiz=1#/is')
    await page.keyboard.press('Escape'); await page.keyboard.press('n'); await page.keyboard.insertText('Bozuk giriş testini düzelt'); await page.keyboard.press('Control+Enter'); await acceptDone(page)
    const hit = await waitFor(page, '[data-proof="failed"]', 14000)
    ok('D1 bozuk iş → kaldı kartı', hit, await page.evaluate(() => [...document.querySelectorAll('[data-proof]')].map((e) => e.getAttribute('data-proof')).join(',') || 'kart yok'))
    await page.close()
  }
  // ---- E. Yanıt ve durdur ----
  {
    const { page } = await open('/?ornek=1#/is')
    await page.keyboard.press('Escape')
    const idx = await page.evaluate(() => [...document.querySelectorAll('#job-list [role=option]')].findIndex((e) => e.getAttribute('data-job') === '308'))
    ok('E0 yanıt bekleyen iş listede', idx >= 0, String(idx))
    const cur0 = await page.evaluate(() => [...document.querySelectorAll('#job-list [role=option]')].findIndex((e) => e.id === document.querySelector('#job-list')?.getAttribute('aria-activedescendant')))
    for (let i = cur0; i < idx; i++) await page.keyboard.press('j')
    await page.keyboard.press('r')
    ok('E1 r → yanıt kutusuna odak', await page.evaluate(() => !!document.activeElement?.matches?.('[data-reply]')))
    await page.keyboard.insertText('Hafta içi yeter, hafta sonu gönderme.'); await page.keyboard.press('Enter')
    ok('E2 yanıt akışta göründü', await waitFn(page, () => /Hafta içi yeter/.test(document.querySelector('#stream-scroll')?.innerText ?? ''), null, 4000))
    await page.keyboard.press('Escape'); await page.keyboard.press('Escape')
    await page.keyboard.press('n'); await page.keyboard.insertText('Uzun bir iş dene'); await page.keyboard.press('Control+Enter'); await acceptDone(page)
    await waitFor(page, '[data-stop]', 5000)
    await page.keyboard.press('Escape'); await page.keyboard.press('s')
    ok('E3 s → iş durduruldu', await waitFn(page, () => /Durduruldu|Durdurdun/i.test(document.querySelector('[data-stream]')?.innerText ?? ''), null, 4000))
    await page.close()
  }
  // ---- F. Palet ve j/k ----
  {
    const { page } = await open('/?ornek=1#/is')
    await page.keyboard.press('Escape')
    const p = await measure(page, "() => !!document.querySelector('[data-palette]')", () => page.keyboard.press('Control+k'))
    ok('F1 Ctrl+K palet açıldı', p !== null, `${p?.toFixed(1)} ms`)
    await page.keyboard.insertText('bekçi'); await page.keyboard.press('Enter')
    ok('F2 palet → Bekçi', await waitFn(page, () => location.hash.startsWith('#/bekci'), null, 3000))
    await page.keyboard.press('Control+k'); await page.keyboard.insertText('harcama'); await page.keyboard.press('Enter')
    ok('F3 palet → Harcama', await waitFn(page, () => location.hash.startsWith('#/harcama'), null, 3000))
    await page.keyboard.press('Escape')
    ok('F4 Esc → iş yüzeyi', await waitFn(page, () => location.hash.startsWith('#/is'), null, 3000))
    await page.keyboard.press('Escape')
    const before = await page.evaluate(() => document.querySelector('#job-list')?.getAttribute('aria-activedescendant'))
    const j = await measure(page, `() => document.querySelector('#job-list')?.getAttribute('aria-activedescendant') !== ${JSON.stringify(before)}`, () => page.keyboard.press('j'))
    ok('F5 j → seçim aşağı', j !== null, `${j?.toFixed(1)} ms`)
    const t2 = await page.evaluate(() => document.querySelector('[data-job-title]')?.textContent)
    await page.keyboard.press('k')
    ok('F6 k → seçim yukarı', await waitFn(page, (x) => document.querySelector('[data-job-title]')?.textContent !== x, t2, 2000))
    await page.keyboard.press('?')
    ok('F7 ? → kısayol yardımı', await waitFor(page, 'dialog[open]', 2000))
    await page.keyboard.press('Escape')
    await page.close()
  }
  // ---- G. Dar ekran ----
  {
    const { page } = await open('/?ornek=1#/is', 420, 860)
    await page.keyboard.press('Escape'); await page.keyboard.press('j')
    const d = await measure(page, "() => document.querySelector('[data-panes]')?.getAttribute('data-view') === 'detail'", () => page.keyboard.press('Enter'))
    ok('G1 dar: Enter → ayrıntı paneli', d !== null, `${d?.toFixed(1)} ms`)
    await page.waitForTimeout(300)
    await page.keyboard.press('Escape')
    ok('G2 dar: Esc → liste', await waitFn(page, () => document.querySelector('[data-panes]')?.getAttribute('data-view') === 'list', null, 2000))
    await page.close()
  }
  // ---- I. Yeni durumlar (Söz 4) ----
  const openJob = async (id, url = '/?ornek=1#/is', w = 1440, h = 900) => {
    const { page } = await open(url, w, h)
    await page.waitForSelector(`[data-job="${id}"]`, { timeout: 8000 })
    await page.click(`[data-job="${id}"]`)
    await page.waitForSelector(`[data-stream="${id}"]`, { timeout: 5000 })
    await page.waitForTimeout(300)
    return page
  }
  const txt = (page, sel) => page.evaluate((s) => document.querySelector(s)?.innerText ?? null, sel)
  {
    // I1 onaylar tek tek, en eskiden; sayaç "2 onaydan 1."; üst sayaç API'den
    const page = await openJob(307)
    const c1 = await txt(page, '[data-approval-counter]')
    ok('I1 onay sayacı "2 onaydan 1."', /2 onaydan 1\./.test(c1 ?? ''), String(c1))
    const first = await page.evaluate(() => document.querySelector('[data-approval-what]')?.textContent)
    ok('I1b yalnız bir onay kartı görünüyor', (await page.locator('[data-approval]').count()) === 1)
    const me0 = Number(await txt(page, '[data-counter="me"] b'))
    await page.keyboard.press('Escape'); await page.keyboard.press('e')
    ok('I2 e → ikinci onay geldi ("2 onaydan 2.")', await waitFn(page, () => /2 onaydan 2\./.test(document.querySelector('[data-approval-counter]')?.innerText ?? ''), null, 4000))
    const second = await page.evaluate(() => document.querySelector('[data-approval-what]')?.textContent)
    ok('I2b ikinci onay farklı komut (en eskiden sırayla)', !!first && !!second && first !== second, `${first} -> ${second}`)
    ok('I2c ilk onay verilince tek onay kartı kaldı', (await page.locator('[data-approve="yes"]').count()) === 1)
    await page.keyboard.press('e')
    ok('I3 ikinci onaydan sonra onay kartı kalktı', await waitFn(page, () => !document.querySelector('[data-approval]'), null, 4000))
    await waitFn(page, (n) => Number(document.querySelector('[data-counter="me"] b')?.textContent) !== n, me0, 4000)
    const me1 = Number((await txt(page, '[data-counter="me"] b')) ?? 0)
    ok('I4 üst sayaç "Sıra bende" API sayısıyla azaldı', me1 === me0 - 1, `${me0} -> ${me1}`)
    await page.close()
  }
  {
    // I5 Durdur onay bekleyen işte görünür ve çalışır
    const page = await openJob(307)
    ok('I5 onay bekleyen işte Durdur görünür', (await page.locator('[data-stop]').count()) === 1)
    await page.locator('[data-stop]').click()
    ok('I5b Durdur çalıştı', await waitFn(page, () => /Durduruldu|Durdurdun/i.test(document.querySelector('[data-stream]')?.innerText ?? ''), null, 4000))
    ok('I5c durdurulan iş için "Bu işe devam et" çıktı', await waitFor(page, '[data-continue]', 3000))
    await page.close()
  }
  {
    // I6 cevap bekliyor: soru kartta, cevap kutusu odakta
    const page = await openJob(308)
    ok('I6 soru kartı var', await waitFor(page, '[data-question]', 2000))
    ok('I6b cevap kutusu odakta', await waitFn(page, () => !!document.activeElement?.matches?.('[data-reply]'), null, 2000))
    ok('I6c çip "Sıra bende" (sarı), yeşil değil', (await page.locator('[data-chip="me"]').count()) === 1)
    await page.close()
  }
  {
    // I7 sırada
    const page = await openJob(309)
    ok('I7 sırada kartı var', await waitFor(page, '[data-queued]', 2000), String(await txt(page, '[data-queued]')))
    await page.locator('[data-queued] [data-open-settings]').click()
    // d2u2: ayar sayfası kayıt defterinden çizilir; alan artık [data-setting="maxParallelRuns"] satırındaki girdidir ve düğme ona odaklar.
    ok('I7b sırada kartındaki düğme Ayarlar > "aynı anda en çok kaç iş" ayarına götürdü', await waitFn(page, () => location.hash.startsWith('#/ayarlar/paralel') && !!document.querySelector('[data-setting="maxParallelRuns"] [data-setting-input]'), null, 4000))
    ok('I7c ayar alanı ekranda görünür ve odakta', await page.evaluate(() => { const e = document.querySelector('[data-setting="maxParallelRuns"] [data-setting-input]'); const r = e?.getBoundingClientRect(); return !!r && r.top >= 0 && r.bottom <= innerHeight && document.activeElement === e }))
    await page.close()
  }
  {
    // I8 başlatılamadı: nötr uyarı, "Kaldı" değil
    const page = await openJob(311)
    const b = await txt(page, '[data-blocked]')
    ok('I8 "Başlatılamadı: <neden>" yazıyor', /Başlatılamadı: Claude programı bulunamadı/.test(b ?? ''), String(b).slice(0, 80))
    const all = await txt(page, '[data-stream]')
    ok('I8b "Kaldı" yazmıyor', !/Kaldı/.test(all ?? ''))
    ok('I8c çip nötr (blocked)', (await page.locator('[data-chip="blocked"]').count()) === 1)
    await page.locator('[data-blocked] [data-open-settings]').click()
    ok('I8d "Ayarlar\'ı aç" → Claude programının yeri alanı', await waitFn(page, () => location.hash.startsWith('#/ayarlar/claude') && !!document.querySelector('[data-setting="claudePath"] [data-setting-input]'), null, 4000))
    await page.waitForTimeout(600)
    ok('I8e alan ekranda görünür', await page.evaluate(() => { const e = document.querySelector('[data-setting="claudePath"] [data-setting-input]'); const r = e?.getBoundingClientRect(); return !!r && r.top >= 0 && r.bottom <= innerHeight }))
    await page.close()
  }
  {
    // I9 karar bekliyor: neden + dosyalar + iki düğme; kabul / ret
    const page = await openJob(310)
    const all = (await txt(page, '[data-stream]')) ?? ''
    ok('I9 karar kartı: değişen dosya görünüyor', /sulama\.test\.js/.test(all) && (await page.locator('[data-accept]').count()) === 1)
    ok('I9b iki düğme: kabul et / kabul etme', (await page.locator('[data-reject]').count()) === 1, `${await page.locator('[data-accept]').innerText()} | ${await page.locator('[data-reject]').innerText()}`)
    await page.locator('[data-accept]').click()
    ok('I9c kabul → "Sen onayladın" çipi', await waitFn(page, () => /Sen onayladın/.test(document.querySelector('[data-chip]')?.textContent ?? ''), null, 4000))
    await page.close()
    const p2 = await openJob(310)
    await p2.locator('[data-reject]').click()
    ok('I9d kabul etme → iş kaldı', await waitFn(p2, () => document.querySelector('[data-chip]')?.getAttribute('data-chip') === 'failed', null, 4000))
    await p2.close()
  }
  {
    // I10 "Sen onayladın" yeşil "Geçti"den görsel olarak ayrı
    const page = await openJob(312)
    const a = await page.evaluate(() => { const e = document.querySelector('[data-chip]'); const s = getComputedStyle(e); return { cls: e.className, txt: e.textContent.trim(), bg: s.backgroundColor, bw: s.borderTopWidth } })
    ok('I10 "Sen onayladın" yazıyor, chip-ok sınıfı yok', /Sen onayladın/.test(a.txt) && !/chip-ok/.test(a.cls), JSON.stringify(a))
    await page.close()
    const { page: p4 } = await open('/?ornek=1#/is')
    const green = await p4.evaluate(() => {
      const j = [...document.querySelectorAll('[data-group="passed"] [data-job]')][0]
      if (!j) return null
      j.click(); return true
    })
    await p4.waitForTimeout(400)
    const g = await p4.evaluate(() => { const e = document.querySelector('[data-chip="passed"]'); if (!e) return null; const s = getComputedStyle(e); return { cls: e.className, bg: s.backgroundColor, bw: s.borderTopWidth } })
    ok('I10b yeşil "Geçti" çipinden zemin ya da çerçeveyle ayrı', !!g && (g.bg !== a.bg || g.bw !== a.bw) && g.cls !== a.cls, JSON.stringify({ g, green }))
    await p4.close()
  }
  {
    // I11 kanıt kartı: ham pre yok; "ayrıntı" ile açılır, satır sarmalı, sınırlı
    const page = await openJob(301)
    const pre0 = await page.evaluate(() => document.querySelectorAll('[data-proof] pre').length)
    ok('I11 kanıt kartında ham çıktı kapalı (pre yok)', pre0 === 0, String(pre0))
    const hasBtn = (await page.locator('[data-detail-btn]').count()) > 0
    if (hasBtn) {
      await page.locator('[data-detail-btn]').first().click()
      const det = await page.evaluate(() => { const e = document.querySelector('[data-detail]'); if (!e) return null; const s = getComputedStyle(e); return { wrap: s.whiteSpace, maxH: s.maxHeight, ov: s.overflowY } })
      ok('I11b ayrıntı: satır sarmalı, sınırlı yükseklik, kaydırılabilir', !!det && /wrap/.test(det.wrap) && det.maxH !== 'none' && /auto|scroll/.test(det.ov), JSON.stringify(det))
    } else ok('I11b ayrıntı düğmesi', false, 'düğme yok')
    const p2 = await openJob(313)
    const cu = await txt(p2, '[data-proof]')
    ok('I11c sayı okunamadı notu', /okunamad|sayı/i.test(cu ?? ''), String(cu).slice(0, 100))
    await p2.close()
    await page.close()
  }
  {
    // I12 zaman çizgisi zaman sırasında; ham komut yok
    for (const id of [301, 302, 303]) {
      const page = await openJob(id)
      const order = await page.evaluate(() => [...document.querySelectorAll('.tl > li')].map((l) => /k-(\w+)/.exec(l.className)?.[1]))
      const lastProg = order.lastIndexOf('progress'), lastProof = order.lastIndexOf('proof')
      ok(`I12 iş ${id}: kanıt kartı ilerleme satırlarının altında`, lastProof > lastProg && lastProof >= 0, order.join(','))
      const raw = await page.evaluate(() => /(^|[^a-zA-Z])(rm -rf|npm run|dotnet test|python -m|git commit)/.test(document.querySelector('.tl')?.innerText ?? ''))
      ok(`I12b iş ${id}: akışta ham komut görünmüyor`, !raw)
      await page.close()
    }
  }
  {
    // I13 "Bu işe devam et": biten işte
    const page = await openJob(303)
    ok('I13 biten işte "Bu işe devam et" düğmesi', await waitFor(page, '[data-continue]', 2000))
    await page.locator('[data-continue]').click()
    ok('I13b devam kutusu açıldı ve odakta', await waitFn(page, () => !!document.activeElement?.matches?.('[data-reply]'), null, 2000))
    await page.keyboard.insertText('Bir de boş sayfa durumunu düzelt.'); await page.keyboard.press('Enter')
    ok('I13c devam yazısı akışta, iş yeniden çalışıyor', await waitFn(page, () => /boş sayfa durumunu/.test(document.querySelector('#stream-scroll')?.innerText ?? '') && /Çalışıyor/.test(document.querySelector('[data-chip]')?.textContent ?? ''), null, 5000))
    await page.close()
  }
  // ---- J. Testsiz klasör kartı, klavye ----
  {
    const { page } = await open('/?bos=1&hiz=1#/is')
    await page.keyboard.insertText('Bahçe listesine arama ekle'); await page.keyboard.press('Control+Enter')
    await page.keyboard.insertText('D:/projeler/testsiz-bahce'); await page.keyboard.press('Enter')
    ok('J1 testsiz klasörde şablon kartı geldi', await waitFor(page, '[data-setup]'))
    await waitFn(page, () => !document.querySelector('[data-setup] .skel'), null, 4000)
    await page.waitForTimeout(200)
    const f = await page.evaluate(() => { const a = document.activeElement; return { later: a?.matches?.('[data-later]') ?? false, tag: a?.tagName, openAllDisabled: document.querySelector('[data-open-all]')?.disabled ?? null } })
    ok('J2 ana düğme pasifse odak "Kanıtsız devam et"te', f.later, JSON.stringify(f))
    ok('J2b kartta ölü (pasif) düğme yok ya da yalnız ana düğme pasif', await page.evaluate(() => [...document.querySelectorAll('[data-setup] button')].every((b) => !b.disabled || b.matches('[data-open-all]'))))
    await page.keyboard.press('Enter')
    ok('J3 Enter → kanıtsız devam, kart kapandı', await waitFn(page, () => !document.querySelector('[data-setup]'), null, 4000))
    await page.close()
    const { page: p2 } = await open('/?bos=1&hiz=1#/is')
    await p2.keyboard.insertText('Bahçe listesine arama ekle'); await p2.keyboard.press('Control+Enter')
    await p2.keyboard.insertText('D:/projeler/testsiz-bahce2'); await p2.keyboard.press('Enter')
    await waitFor(p2, '[data-setup]'); await waitFn(p2, () => !document.querySelector('[data-setup] .skel'), null, 4000)
    await p2.keyboard.press('Escape')
    ok('J4 Esc → kart kapandı (pencere düzeyinde)', await waitFn(p2, () => !document.querySelector('[data-setup]'), null, 4000))
    await p2.close()
  }
  // ---- K. Taslak kalıcı ----
  {
    const { page } = await open('/?ornek=1#/is')
    // Taslak localStorage'da kalıcıdır ve bu betikteki bütün sayfalar aynı profili paylaşır: önceki bölümlerde yazılıp başlatılmayan metin burada karışmasın.
    await page.evaluate(() => { try { localStorage.removeItem('soz.draft') } catch {} })
    await page.reload(); await page.waitForSelector('[data-newjob]')
    await page.keyboard.press('Escape'); await page.keyboard.press('n')
    await page.keyboard.insertText('Yarım kalan taslak cümlesi')
    await page.waitForTimeout(300)
    await page.reload(); await page.waitForSelector('[data-newjob]')
    ok('K1 yenilemede taslak geri geldi', await waitFn(page, () => document.querySelector('[data-newjob]')?.value === 'Yarım kalan taslak cümlesi', null, 3000), String(await page.evaluate(() => document.querySelector('[data-newjob]')?.value)))
    await page.keyboard.press('Control+Enter'); await acceptDone(page)
    await page.waitForTimeout(600)
    const left = await page.evaluate(() => { try { return localStorage.getItem('soz.draft') } catch { return 'x' } })
    ok('K2 iş oluşunca taslak silindi', !left, String(left))
    await page.reload(); await page.waitForSelector('[data-newjob]')
    ok('K3 yenilemede kutu boş', await page.evaluate(() => (document.querySelector('[data-newjob]')?.value ?? '') === ''))
    await page.close()
  }
  // ---- L. Yanlış klasör yolu ----
  {
    const { page } = await open('/?bos=1&hiz=1#/is')
    const before = errors.length
    await page.keyboard.insertText('Bir şey yap'); await page.keyboard.press('Control+Enter')
    await page.keyboard.insertText('bahce'); await page.keyboard.press('Enter')
    ok('L1 yanlış yol: kutunun altında sade hata', await waitFn(page, () => /klasör yolu/i.test(document.querySelector('[role=alert]')?.textContent ?? ''), null, 3000), String(await page.evaluate(() => document.querySelector('[role=alert]')?.textContent)))
    ok('L2 yanlış yol: konsola hata düşmedi', errors.length === before, errors.slice(before).join(' | '))
    await page.close()
  }
  // ---- M. Yalnız klavye: klasör ekle, iş ver, onayla (2 onay), kabul et ----
  {
    const { page } = await open('/?bos=1&hiz=1#/is')
    let mouse = 0
    await page.exposeFunction('__mouse', () => { mouse++ })
    await page.evaluate(() => addEventListener('mousedown', () => window.__mouse(), true))
    let eylem = 0
    await page.keyboard.insertText('Eski dosyaları temizle, iki onay iste, test dosyasını değiştir'); eylem++
    await page.keyboard.press('Control+Enter'); eylem++
    await page.keyboard.insertText('D:/projeler/hesap-makinesi'); await page.keyboard.press('Enter'); eylem++
    ok('M1 onay kartı', await waitFor(page, '[data-setup] [data-open-all]'))
    await page.keyboard.press('Enter'); eylem++
    await acceptDone(page); eylem++
    ok('M2 iş başladı ve onay istedi', await waitFor(page, '[data-approval]', 15000))
    ok('M2b sayaç "2 onaydan 1."', /2 onaydan 1\./.test((await txt(page, '[data-approval-counter]')) ?? ''))
    await page.keyboard.press('Escape'); await page.keyboard.press('e'); eylem++
    await waitFn(page, () => /2 onaydan 2\./.test(document.querySelector('[data-approval-counter]')?.innerText ?? ''), null, 4000)
    await page.keyboard.press('e'); eylem++
    ok('M3 karar kartı geldi (test dosyası değişti)', await waitFor(page, '[data-accept]', 15000))
    let hit = false
    for (let i = 0; i < 60 && !hit; i++) { await page.keyboard.press('Tab'); hit = await page.evaluate(() => !!document.activeElement?.matches?.('[data-accept]')) }
    ok('M4 Tab ile "Değişikliği kabul et" düğmesine ulaşıldı', hit)
    await page.keyboard.press('Enter'); eylem++
    ok('M5 kabul → "Sen onayladın"', await waitFn(page, () => /Sen onayladın/.test(document.querySelector('[data-chip]')?.textContent ?? ''), null, 5000))
    ok('M6 fare hiç kullanılmadı', mouse === 0, `${mouse} tıklama; ${eylem} eylem`)
    await page.close()
  }
  // ---- H. Metin kuralları ve boyutlar ----
  for (const [w, h] of [[1440, 900], [420, 860]]) {
    for (const route of ['is', 'bekci', 'harcama', 'defter', 'ara', 'ayarlar']) {
      const { page } = await open(`/?ornek=1#/${route}`, w, h)
      await page.waitForTimeout(500)
      const r = await page.evaluate(() => {
        const c = document.body.cloneNode(true)
        c.querySelectorAll('pre, code, kbd, script, style, .mono').forEach((e) => e.remove())
        // d2u2: ayar açıklamaları kayıt defterinden (sunucudan) gelir; arayüzün kendi yazısı değildir. Onları ayrı sayıp raporlarız (arka uç metni), arayüz yazısı denetimi katı kalır.
        const serverSemi = [...c.querySelectorAll('[data-server-text]')].filter((e) => /;/.test(e.textContent || '')).length
        c.querySelectorAll('[data-server-text]').forEach((e) => e.remove())
        const txt = c.textContent || ''
        const small = [...document.querySelectorAll('body *')].filter((e) => {
          const s = getComputedStyle(e)
          return [...e.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim()) && parseFloat(s.fontSize) < 12 && s.display !== 'none' && s.visibility !== 'hidden' && !e.closest('svg')
        }).length
        const need = innerWidth <= 759 ? 40 : 32
        const tiny = [...document.querySelectorAll('button, [role=option], [role=switch], select, input, textarea')].filter((e) => {
          const b = e.getBoundingClientRect(); const s = getComputedStyle(e)
          if (!(b.width > 0 && b.height > 0) || s.visibility === 'hidden' || b.bottom + 8 > innerHeight || !e.contains(document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2)) || e.closest('.sr-only') || e.type === 'checkbox' || e.type === 'radio') return false
          if (b.top < 0 || b.bottom > innerHeight) return false
          // etkin dokunma alanı: öğenin kendisi ya da onu örten sahte öğe dikey olarak yeterli mi
          const pad = Math.max(0, (need - b.height) / 2) - 0.5
          if (pad > 0) { const cx = b.left + b.width / 2; const a = document.elementFromPoint(cx, b.top - pad); const z = document.elementFromPoint(cx, b.bottom + pad); if (e.contains(a) && (z === null || e.contains(z))) return false }
          return b.height < need
        }).map((e) => (e.getAttribute('aria-label') || e.innerText || e.tagName).slice(0, 16).replace(/s+/g, ' ') + '.' + String(e.className).slice(0, 14) + ':' + Math.round(e.getBoundingClientRect().height))
        return { toparla: /toparla/i.test(txt), dash: /—/.test(txt), semi: /;/.test(txt), small, tiny: tiny.slice(0, 4), sunucuMetniNoktalivirgul: serverSemi }
      })
      ok(`H ${route} ${w}: Toparla/—/; yok, yazı >= 12px, hedef >= 32/40px`, !r.toparla && !r.dash && !r.semi && r.small === 0 && r.tiny.length === 0, JSON.stringify(r))
      await page.close()
    }
  }
} finally {
  const w = Math.max(...rows.map((r) => r[1].length))
  for (const r of rows) console.log(`${r[0]}  ${r[1].padEnd(w)}  ${r[2]}`)
  console.log('ilk içerik:', JSON.stringify(firstContent))
  console.log('GEÇTİ', rows.filter((r) => r[0] === 'GEÇTİ').length, 'KALDI', rows.filter((r) => r[0] === 'KALDI').length)
  console.log('KONSOL:', errors.join('\n') || 'temiz')
  await ctx.close(); try { rmSync(profile, { recursive: true, force: true }) } catch {}
}
