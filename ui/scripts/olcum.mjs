// Yazı ölçeği (derlenmiş sayfada gerçekten kullanılan boyut, kalınlık, aile) ve etkileşim süreleri. Üretim derlemesine karşı, örnek veri.
import { launch } from './pw.mjs'
import { rmSync } from 'node:fs'
const BASE = process.env.BASE ?? 'http://127.0.0.1:49412'
const { ctx, profile } = await launch()
const sizes = new Map(), weights = new Map(), fams = new Map()
const bump = (m, k, n = 1) => m.set(k, (m.get(k) ?? 0) + n)
const timings = []
try {
  const routes = [['is', 1440], ['bekci', 1440], ['harcama', 1440], ['defter', 1440], ['ara', 1440], ['ayarlar', 1440], ['is', 420], ['ayarlar', 420]]
  for (const [r, w] of routes) {
    const page = await ctx.newPage()
    await page.setViewportSize({ width: w, height: w === 420 ? 860 : 900 })
    await page.goto(`${BASE}/?ornek=1#/${r}`); await page.reload(); await page.waitForTimeout(700)
    if (r === 'is') { await page.keyboard.press('Control+k'); await page.waitForTimeout(250); await page.keyboard.press('Escape') }
    const res = await page.evaluate(() => {
      const out = []
      for (const e of document.querySelectorAll('body *')) {
        if (![...e.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim())) continue
        const s = getComputedStyle(e)
        if (s.display === 'none' || s.visibility === 'hidden' || e.closest('svg, script, style, .sr-only')) continue
        out.push([s.fontSize, s.fontWeight, s.fontFamily.split(',')[0].replace(/["']/g, '').trim()])
      }
      return out
    })
    for (const [a, b, c] of res) { bump(sizes, a); bump(weights, b); bump(fams, c) }
    await page.close()
  }
  // yeni durum kartları (Söz 4): her iş ekranı iki genişlikte
  for (const w of [1440, 420]) {
    const page = await ctx.newPage()
    await page.setViewportSize({ width: w, height: w === 420 ? 860 : 900 })
    await page.goto(`${BASE}/?ornek=1#/is`); await page.evaluate(() => { try { localStorage.removeItem('soz.draft') } catch {} }); await page.reload(); await page.waitForTimeout(600)
    for (const id of [301, 302, 303, 305, 307, 308, 309, 310, 311, 312, 313]) {
      await page.goto(`${BASE}/?ornek=1#/is`); await page.reload(); await page.waitForSelector(`[data-job="${id}"]`)
      await page.click(`[data-job="${id}"]`); await page.waitForTimeout(350)
      const det = page.locator('[data-detail-btn]').first()
      if (await det.count()) await det.click().catch(() => {})
      const res = await page.evaluate(() => {
        const out = []
        for (const e of document.querySelectorAll('body *')) {
          if (![...e.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim())) continue
          const s = getComputedStyle(e)
          if (s.display === 'none' || s.visibility === 'hidden' || e.closest('svg, script, style, .sr-only')) continue
          out.push([s.fontSize, s.fontWeight, s.fontFamily.split(',')[0].replace(/["']/g, '').trim()])
        }
        return out
      })
      for (const [a, b, c] of res) { bump(sizes, a); bump(weights, b); bump(fams, c) }
    }
    await page.close()
  }
  // telefon sayfası
  { const page = await ctx.newPage(); await page.setViewportSize({ width: 420, height: 860 }); await page.goto(`${BASE}/t/index.html?ornek=1`); await page.waitForTimeout(700)
    const res = await page.evaluate(() => [...document.querySelectorAll('body *')].filter((e) => [...e.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim())).map((e) => { const s = getComputedStyle(e); return [s.fontSize, s.fontWeight, s.fontFamily.split(',')[0].replace(/["']/g, '').trim()] }))
    for (const [a, b, c] of res) { bump(sizes, a); bump(weights, b); bump(fams, c) }
    await page.close() }

  // etkileşim süreleri: 7 tekrar, ortanca ve en yavaş
  async function measure(page, pred, press) {
    await page.evaluate((src) => {
      window.__m = { done: false, ms: null }
      const f = new Function('return (' + src + ')')()
      let t0 = 0
      window.addEventListener('keydown', () => { t0 = performance.now() }, { capture: true, once: true })
      const obs = new MutationObserver(() => { if (f()) { obs.disconnect(); requestAnimationFrame(() => requestAnimationFrame(() => { window.__m = { done: true, ms: performance.now() - t0 } })) } })
      obs.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true })
    }, pred)
    await press()
    await page.waitForFunction(() => window.__m.done, null, { timeout: 4000 }).catch(() => {})
    return await page.evaluate(() => window.__m.ms)
  }
  const stat = (name, v) => { const a = v.filter((x) => x !== null).sort((x, y) => x - y); timings.push([name, a.length, a[Math.floor(a.length / 2)]?.toFixed(1), a[a.length - 1]?.toFixed(1)]) }
  const page = await ctx.newPage()
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto(`${BASE}/?ornek=1#/is`); await page.reload(); await page.waitForSelector('[data-newjob]'); await page.waitForTimeout(800)
  const nav = await page.evaluate(() => { const n = performance.getEntriesByType('navigation')[0]; return [n.domContentLoadedEventEnd, performance.getEntriesByName('first-contentful-paint')[0]?.startTime] })
  timings.push(['ilk içerik: DOM hazır / ilk boyama (ms)', 1, nav[0].toFixed(0), nav[1]?.toFixed(0)])
  const A = [], B = [], C = [], D = []
  for (let i = 0; i < 7; i++) {
    await page.keyboard.press('Escape')
    A.push(await measure(page, "() => document.activeElement && document.activeElement.matches('[data-newjob]')", () => page.keyboard.press('n')))
    await page.keyboard.press('Escape')
    C.push(await measure(page, "() => !!document.querySelector('[data-palette]')", () => page.keyboard.press('Control+k')))
    await page.keyboard.press('Escape')
    const before = await page.evaluate(() => document.querySelector('[data-job-title]')?.textContent)
    B.push(await measure(page, `() => document.querySelector('[data-job-title]')?.textContent !== ${JSON.stringify(before)}`, () => page.keyboard.press(i % 2 ? 'k' : 'j')))
  }
  stat('n → yeni iş kutusuna odak', A); stat('Ctrl+K → palet açık', C); stat('j/k → ayrıntı paneli yeni işi gösteriyor', B)
  await page.close()
  const p2 = await ctx.newPage()
  await p2.setViewportSize({ width: 420, height: 860 })
  await p2.goto(`${BASE}/?ornek=1#/is`); await p2.reload(); await p2.waitForSelector('[data-newjob]'); await p2.waitForTimeout(800)
  for (let i = 0; i < 7; i++) {
    await p2.keyboard.press('Escape'); await p2.keyboard.press('Escape')
    await p2.waitForTimeout(350)
    D.push(await measure(p2, "() => document.querySelector('[data-panes]')?.getAttribute('data-view') === 'detail'", () => p2.keyboard.press('Enter')))
  }
  stat('dar ekran: Enter → ayrıntı paneli geçişi başladı', D)
} finally {
  const fmt = (m) => [...m.entries()].sort((a, b) => parseFloat(a[0]) - parseFloat(b[0])).map(([k, v]) => `${k} (${v})`).join(', ')
  console.log('YAZI BOYUTLARI:', fmt(sizes))
  console.log('KALINLIKLAR:', fmt(weights))
  console.log('AİLELER:', fmt(fams))
  console.log('SÜRELER (ad | tekrar | ortanca ms | en yavaş ms):')
  for (const t of timings) console.log('  ', t.join(' | '))
  await ctx.close(); try { rmSync(profile, { recursive: true, force: true }) } catch {}
}
