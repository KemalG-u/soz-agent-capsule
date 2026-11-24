<script lang="ts">
  let { embedded = false }: { embedded?: boolean } = $props()
  import { getReport } from '../lib/api'
  import Icon from '../lib/components/Icon.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import { app, toast } from '../lib/store.svelte'
  import type { Report } from '../lib/types'
  import { canvasToBlob, CARD_H, CARD_W, drawCard } from '../lib/sharecard'
  import { copyText, fmtDate, toolName } from '../lib/util'

  let days = $state(30)
  let report = $state<Report | null>(null)
  let failed = $state(false)
  let addSentence = $state(false)
  let sentenceIdx = $state(0)
  let canvas = $state<HTMLCanvasElement>()

  async function load() {
    try {
      report = await getReport(days)
      failed = false
    } catch {
      failed = true
    }
  }

  $effect(() => {
    void app.tick
    void days
    load()
  })

  const nf = $derived(new Intl.NumberFormat(app.lang === 'en' ? 'en-GB' : 'tr-TR'))
  const f = (n: number) => nf.format(n)

  const maxDay = $derived(report ? Math.max(1, ...report.daily.map((d) => d.utterances)) : 1)
  const toolTotal = $derived(report ? report.tools.reduce((a, b) => a + b.utterances, 0) : 0)

  // Grafik ölçüleri (viewBox 600 x 150)
  const W = 600
  const H = 150
  const bars = $derived(
    report
      ? report.daily.map((d, i) => {
          const n = report!.daily.length
          const slot = W / n
          const bw = Math.max(2, slot * 0.68)
          const h = Math.max(d.utterances > 0 ? 2 : 0, (d.utterances / maxDay) * (H - 6))
          return { x: i * slot + (slot - bw) / 2, y: H - h, w: bw, h, d }
        })
      : [],
  )

  const sentence = $derived(report && addSentence ? report.topRepeats[sentenceIdx]?.text : undefined)

  const cardLine = $derived(
    report
      ? t('shareLine', { n: report.days, u: f(report.utterances), s: f(report.sessions), p: f(report.projects) }) +
          (report.totalRepeats > 0 ? ' ' + t('shareLineRepeat', { n: f(report.totalRepeats) }) : '')
      : '',
  )

  $effect(() => {
    if (!canvas || !report) return
    // Yazı tipleri hazır olunca çiz (sistem yazı tipleri hemen hazırdır; yine de bekle).
    const r = report
    const draw = () =>
      drawCard(canvas!, {
        title: t("shareCardTitle", { n: r.days }),
        stats: [
          { value: f(r.utterances), label: t('repMessages') },
          { value: f(r.sessions), label: t('repSessions') },
          { value: f(r.totalRepeats), label: t('repRepeats'), accent: true },
        ],
        sentence,
        footer: t('shareFooter'),
      })
    draw()
    void document.fonts?.ready.then(draw)
  })

  async function download() {
    if (!canvas) return
    const blob = await canvasToBlob(canvas)
    if (!blob) return toast(t('copyFailed'))
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `soz-karne-${report?.days ?? 30}-gun.png`
    document.body.appendChild(a)
    a.click()
    a.remove()
    setTimeout(() => URL.revokeObjectURL(a.href), 4000)
    toast(t('reportSaved', { f: a.download }))
  }

  async function copyCard() {
    const text = cardLine + (sentence ? `\n“${sentence}”` : '')
    toast((await copyText(text)) ? t('copied') : t('copyFailed'))
  }
</script>

<div class="page" class:embedded>
  <header class="page-head">
    <div>
      {#if embedded}<h2 class="h2">{t('reportTitle')}</h2>{:else}<h1 class="h1">{t('reportTitle')}</h1>{/if}
      <p class="lead">{t('reportLead', { n: report?.days ?? days })}</p>
    </div>
    <div class="field">
      <label class="label" for="days">{t('reportDays')}</label>
      <select id="days" class="select" bind:value={days}>
        <option value={7}>7</option>
        <option value={30}>30</option>
        <option value={90}>90</option>
      </select>
    </div>
  </header>

  {#if failed && !report}
    <p class="muted">{t('loadError')} <button class="btn btn-sm" onclick={load}>{t('retry')}</button></p>
  {:else if report}
    <dl class="stats">
      <div class="s big"><dd>{f(report.utterances)}</dd><dt>{t('repMessages')}</dt></div>
      <div class="s big"><dd>{f(report.sessions)}</dd><dt>{t('repSessions')}</dt></div>
      <div class="s big rep"><dd>{f(report.totalRepeats)}</dd><dt>{t('repRepeats')}</dt></div>
      <div class="s"><dd>{f(report.projects)}</dd><dt>{t('repProjects')}</dt></div>
      <div class="s"><dd>{f(report.compactions)}</dd><dt>{t('repCompactions')}</dt></div>
      <div class="s"><dd>{f(report.candidates)}</dd><dt>{t('repCandidates')}</dt></div>
      <div class="s"><dd>{f(report.repeatClusters)}</dd><dt>{t('repClusters')}</dt></div>
    </dl>

    <section class="block" aria-labelledby="daily-t">
      <h2 class="h2" id="daily-t">{t('repDaily')}</h2>
      <p class="small muted peakline">{t('repPeak', { n: f(maxDay) })}</p>
      <figure class="chart">
        <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={t('repDailyAlt', { max: f(maxDay) })} preserveAspectRatio="xMidYMid meet">
          <line x1="0" y1={H - 0.5} x2={W} y2={H - 0.5} class="axis" />
          <line x1="0" y1={H / 2} x2={W} y2={H / 2} class="grid" />
          <line x1="0" y1="0.5" x2={W} y2="0.5" class="grid" />
          {#each bars as b (b.d.date)}
            <rect x={b.x} y={b.y} width={b.w} height={b.h} rx={Math.min(2.5, b.w / 3)} class="bar" class:peak={b.d.utterances === maxDay}>
              <title>{fmtDate(b.d.date)}: {b.d.utterances}</title>
            </rect>
          {/each}
        </svg>
        <figcaption class="axis-labels small muted">
          <span>{fmtDate(report.daily[0]?.date)}</span>
          <span>{fmtDate(report.daily[report.daily.length - 1]?.date)}</span>
        </figcaption>
      </figure>
    </section>

    {#if report.tools.length > 0 && toolTotal > 0}
      <section class="block" aria-labelledby="tool-t">
        <h2 class="h2" id="tool-t">{t('repByTool')}</h2>
        <div class="split" role="img" aria-label={report.tools.map((x) => `${toolName(x.tool)} ${Math.round((x.utterances / toolTotal) * 100)}%`).join(', ')}>
          {#each report.tools as x, i (x.tool)}
            <span class="seg s{i}" style={`flex: ${x.utterances} 1 0`}></span>
          {/each}
        </div>
        <ul class="legend">
          {#each report.tools as x, i (x.tool)}
            <li><span class="key s{i}" aria-hidden="true"></span><strong>{toolName(x.tool)}</strong> {Math.round((x.utterances / toolTotal) * 100)}% <span class="muted">({f(x.utterances)})</span></li>
          {/each}
        </ul>
      </section>
    {/if}

    <section class="block" aria-labelledby="top-t">
      <h2 class="h2" id="top-t">{t('repTopTitle')}</h2>
      {#if report.topRepeats.length === 0}
        <p class="muted">{t('repTopNone')}</p>
      {:else}
        <ol class="top">
          {#each report.topRepeats.slice(0, 5) as r, i (i)}
            <li>
              <span class="tn">{r.count}<span class="tx">×</span></span>
              <span class="voice">{r.text}</span>
            </li>
          {/each}
        </ol>
      {/if}
    </section>

    <section class="block share" aria-labelledby="share-t">
      <h2 class="h2" id="share-t">{t('shareTitle')}</h2>
      <p class="lead small">{t('shareLead')}</p>
      <div class="sharegrid">
        <div class="cardwrap" role="img" aria-label={`${t('shareCardAlt')}: ${cardLine}`}>
          <canvas bind:this={canvas} class="card" width={CARD_W} height={CARD_H} aria-hidden="true"></canvas>
        </div>
        <div class="controls">
          {#if report.topRepeats.length > 0}
            <label class="check">
              <input type="checkbox" bind:checked={addSentence} />
              <span>{t('shareAddSentence')}</span>
            </label>
            {#if addSentence}
              <div class="field">
                <label class="label" for="sent">{t('shareSentence')}</label>
                <select id="sent" class="select" bind:value={sentenceIdx}>
                  {#each report.topRepeats.slice(0, 5) as r, i (i)}
                    <option value={i}>{r.text}</option>
                  {/each}
                </select>
              </div>
            {/if}
          {/if}
          <div class="btns">
            <button class="btn btn-primary" onclick={download}><Icon name="download" size={17} />{t('shareDownload')}</button>
            <button class="btn" onclick={copyCard}><Icon name="copy" size={17} />{t('shareCopyText')}</button>
          </div>
        </div>
      </div>
    </section>
  {/if}
</div>

<style>
  .stats {
    display: grid;
    grid-template-columns: repeat(12, minmax(0, 1fr));
    gap: 22px clamp(14px, 3vw, 36px);
    margin: 0 0 clamp(32px, 5vw, 56px);
    padding-bottom: clamp(24px, 4vw, 40px);
    border-bottom: 1px solid var(--line);
  }
  .s { grid-column: span 3; display: flex; flex-direction: column; }
  .s.big { grid-column: span 4; }
  .s dd {
    margin: 0;
    font-family: var(--sans);
    font-weight: var(--fw-strong);
    font-size: var(--fs-xl);
    line-height: 1;
    letter-spacing: -0.03em;
    font-variant-numeric: tabular-nums;
  }
  .s.big dd { font-size: var(--fs-xl); letter-spacing: -0.04em; }
  .s.rep dd { color: var(--waiting); }
  .s dt { color: var(--ink-3); font-size: var(--fs-sm); margin-top: 6px; font-weight: var(--fw-medium); }

  .block { margin-bottom: clamp(32px, 5vw, 52px); }
  .block > .h2 { margin-bottom: 14px; }

  .chart { margin: 0; }
  .chart svg { display: block; width: 100%; height: auto; max-height: 14rem; overflow: visible; }
  .axis { stroke: var(--line-strong); stroke-width: 1; }
  .grid { stroke: var(--line); stroke-width: 1; stroke-dasharray: 3 4; }
  .bar { fill: var(--ink-3); }
  .bar.peak { fill: var(--ink); }
  .axis-labels { display: flex; justify-content: space-between; margin-top: 8px; }
  .peakline { margin: -8px 0 12px; }

  .split { display: flex; height: 14px; gap: 3px; max-width: 36rem; }
  .seg { border-radius: 999px; }
  .s0 { background: var(--ink); }
  .s1 { background: var(--line-strong); }
  .s2 { background: var(--waiting); }
  .legend { display: flex; flex-wrap: wrap; gap: 6px 22px; margin-top: 12px; font-size: var(--fs-sm); }
  .legend li { display: inline-flex; align-items: center; gap: 8px; }
  .key { width: 12px; height: 12px; border-radius: 4px; }

  .top { display: flex; flex-direction: column; max-width: 48rem; }
  .top li { display: grid; grid-template-columns: 4.5rem minmax(0, 1fr); gap: 12px; align-items: baseline; padding: 12px 0; border-top: 1px solid var(--line); }
  .tn { font-family: var(--sans); font-weight: var(--fw-strong); font-size: var(--fs-xl); letter-spacing: -0.03em; font-variant-numeric: tabular-nums; }
  .tx { font-size: var(--fs-md); color: var(--ink-3); margin-left: 2px; }
  .top .voice { font-size: var(--fs-md); }

  .sharegrid { display: grid; grid-template-columns: minmax(0, 1.5fr) minmax(0, 1fr); gap: 24px 32px; align-items: start; margin-top: 18px; }
  .card { width: 100%; height: auto; aspect-ratio: 1200 / 630; border-radius: 14px; border: 1px solid var(--line); background: #131e27; }
  .controls { display: flex; flex-direction: column; gap: 16px; }
  .btns { display: flex; flex-wrap: wrap; gap: 10px; }
  .check { display: inline-flex; align-items: center; gap: 10px; cursor: pointer; }
  .check input { width: 24px; height: 24px; accent-color: var(--ink); }

  @media (max-width: 760px) {
    .sharegrid { grid-template-columns: minmax(0, 1fr); }
    .s, .s.big { grid-column: span 6; }
    .s.big:first-child { grid-column: span 12; }
  }
</style>
