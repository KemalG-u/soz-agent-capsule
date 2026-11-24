<script lang="ts">
  import { getUsage } from '../lib/api2'
  import EmptyState from '../lib/components/EmptyState.svelte'
  import Skeleton from '../lib/components/Skeleton.svelte'
  import { go } from '../lib/router.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import { app } from '../lib/store.svelte'
  import type { Usage } from '../lib/types'
  import { errText, fmtDate, fmtTokens, fmtUsd } from '../lib/util'

  let days = $state(14)
  let usage = $state<Usage | null>(null)
  let error = $state('')

  async function load() {
    try {
      usage = await getUsage(days)
      error = ''
    } catch (e) {
      error = errText(e)
    }
  }

  $effect(() => {
    void app.tick
    void days
    load()
  })

  const maxDay = $derived(Math.max(1, ...(usage?.byDay ?? []).map((d) => d.mainTokens + d.subTokens)))
  const maxModel = $derived(Math.max(1, ...(usage?.byModel ?? []).map((m) => m.tokens)))
  const peak = $derived((usage?.byDay ?? []).reduce((a, d) => (d.mainTokens + d.subTokens > a.v ? { v: d.mainTokens + d.subTokens, day: d.day } : a), { v: 0, day: '' }))

  const BAR_W = 20
  const GAP = 5
  const H = 150
  const dayLabel = (d: string) => fmtDate(d + 'T12:00:00')
  const hasUsage = $derived(!!usage && usage.totalTokens > 0)
</script>

<div class="page embedded">
  <header class="page-head">
    <p class="lead small">{t('spendLead', { n: days })}</p>
    <div class="field">
      <label class="label" for="u-days">{t('reportDays')}</label>
      <select id="u-days" class="select" bind:value={days}>
        <option value={7}>7</option>
        <option value={14}>14</option>
        <option value={30}>30</option>
      </select>
    </div>
  </header>

  {#if error && !usage}
    <p class="err-line" role="alert">{error}</p>
    <p><button class="btn btn-sm" onclick={load}>{t('retry')}</button></p>
  {:else if !usage}
    <Skeleton rows={3} />
  {:else if !hasUsage}
    <EmptyState id="no-spend" text={t('spendNone')} action={t('emptyGoJobs')} onaction={() => go('is')} />
  {:else}
    <dl class="stats">
      <div class="s big"><dd>{fmtTokens(usage.totalTokens)}</dd><dt>{t('spendTokens')}</dt></div>
      <div class="s"><dd>{usage.messages.toLocaleString('tr-TR')}</dd><dt>{t('spendMessages')}</dt></div>
      <div class="s"><dd class:noprice={usage.estimatedUsd === null}>{fmtUsd(usage.estimatedUsd)}</dd><dt>{t('spendEstimate')}</dt></div>
      <div class="s"><dd>%{usage.subagentSharePct}</dd><dt>{t('spendSubShare')}</dt></div>
    </dl>
    <p class="note small"><span class="muted">{t('spendNote')} {t('spendFreshNote')}{usage.note ? ' ' + usage.note + '.' : ''}</span></p>

    <section class="blk" aria-labelledby="gun-t">
      <div class="sec-head">
        <h3 class="h3" id="gun-t">{t('spendDaily')}</h3>
        <ul class="legend small" aria-hidden="true">
          <li><i class="sw main"></i>{t('spendMain')}</li>
          <li><i class="sw sub"></i>{t('spendSub')}</li>
        </ul>
      </div>
      <svg class="bars" viewBox={`0 0 ${usage.byDay.length * (BAR_W + GAP)} ${H}`} preserveAspectRatio="none" role="img" aria-label={t('spendDailyAlt', { max: fmtTokens(maxDay), day: peak.day ? dayLabel(peak.day) : '' })} data-bars>
        <defs>
          <pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <rect width="6" height="6" fill="var(--working-tint)" />
            <rect width="3" height="6" fill="var(--working)" />
          </pattern>
        </defs>
        <line x1="0" y1={H - 0.5} x2={usage.byDay.length * (BAR_W + GAP)} y2={H - 0.5} stroke="var(--line-strong)" stroke-width="1" vector-effect="non-scaling-stroke" />
        {#each usage.byDay as d, i (d.day)}
          {@const hm = (d.mainTokens / maxDay) * (H - 8)}
          {@const hs = (d.subTokens / maxDay) * (H - 8)}
          <g>
            <title>{dayLabel(d.day)}: {fmtTokens(d.mainTokens + d.subTokens)} ({t('spendMain')} {fmtTokens(d.mainTokens)}, {t('spendSub')} {fmtTokens(d.subTokens)}){d.estimatedUsd !== null ? ', ' + fmtUsd(d.estimatedUsd) : ''}</title>
            <rect x={i * (BAR_W + GAP)} y={H - 1 - hm} width={BAR_W} height={hm} rx="2" fill="var(--working)" />
            <rect x={i * (BAR_W + GAP)} y={H - 1 - hm - hs} width={BAR_W} height={hs} rx="2" fill="url(#hatch)" stroke="var(--working)" stroke-width="1" vector-effect="non-scaling-stroke" />
          </g>
        {/each}
      </svg>
      <div class="axis small muted" aria-hidden="true">
        <span>{dayLabel(usage.byDay[0].day)}</span>
        <span>{dayLabel(usage.byDay[usage.byDay.length - 1].day)}</span>
      </div>
      <table class="sr-only">
        <caption>{t('spendDaily')}</caption>
        <thead><tr><th>{t('spendDay')}</th><th>{t('spendMain')}</th><th>{t('spendSub')}</th></tr></thead>
        <tbody>
          {#each usage.byDay as d (d.day)}<tr><td>{d.day}</td><td>{d.mainTokens}</td><td>{d.subTokens}</td></tr>{/each}
        </tbody>
      </table>
      {#if peak.day}<p class="small muted">{t('spendPeak', { day: dayLabel(peak.day), v: fmtTokens(peak.v) })}</p>{/if}
    </section>

    <section class="blk" aria-labelledby="alt-t">
      <div class="sec-head">
        <h3 class="h3" id="alt-t">{t('spendAgentSplit')}</h3>
      </div>
      <div class="split" role="img" aria-label={t('spendSubAlt', { n: usage.subagentSharePct })}>
        <span class="pm" style="width: {100 - usage.subagentSharePct}%"></span>
        <span class="ps" style="width: {usage.subagentSharePct}%"></span>
      </div>
      <p class="small"><strong>%{100 - usage.subagentSharePct}</strong> {t('spendMain')} · <strong>%{usage.subagentSharePct}</strong> {t('spendSub')}</p>
    </section>

    <section class="blk" aria-labelledby="model-t">
      <div class="sec-head"><h3 class="h3" id="model-t">{t('spendByModel')}</h3></div>
      <ul class="models" data-models>
        {#each [...usage.byModel].sort((a, b) => b.tokens - a.tokens) as m (m.model)}
          <li class="mrow">
            <span class="mname">{m.model}</span>
            <span class="mbar" aria-hidden="true"><i style="width: {(m.tokens / maxModel) * 100}%"></i></span>
            <span class="mval">{fmtTokens(m.tokens)}</span>
            <span class="mval usd" class:noprice={m.estimatedUsd === null}>{fmtUsd(m.estimatedUsd)}</span>
          </li>
        {/each}
      </ul>
    </section>
  {/if}
</div>

<style>
  .h3 { font-family: var(--sans); font-weight: var(--fw-strong); font-size: var(--fs-md); margin: 0; }
  .stats { display: flex; flex-wrap: wrap; gap: 12px clamp(20px, 5vw, 56px); margin: 0 0 12px; }
  .s { display: flex; flex-direction: column-reverse; min-width: 6rem; }
  .s dd { margin: 0; font-family: var(--sans); font-weight: var(--fw-strong); font-size: var(--fs-xl); line-height: 1.05; letter-spacing: -0.03em; font-variant-numeric: tabular-nums; }
  .s.big dd { font-size: var(--fs-xl); }
  .s dt { font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-3); }
  .s dd.noprice,
  .mval.noprice { font-family: var(--sans); font-size: var(--fs-md); letter-spacing: 0; font-weight: var(--fw-strong); color: var(--ink-2); padding-top: 0.5rem; }
  .note { margin-bottom: clamp(24px, 4vw, 40px); max-width: 60ch; }

  .blk { margin-bottom: clamp(26px, 4vw, 44px); }
  .legend { display: flex; gap: 14px; }
  .legend li { display: inline-flex; align-items: center; gap: 6px; color: var(--ink-2); }
  .sw { display: inline-block; width: 14px; height: 14px; border-radius: 3px; border: 1px solid var(--working); }
  .sw.main { background: var(--working); }
  .sw.sub { background: repeating-linear-gradient(45deg, var(--working) 0 2px, var(--working-tint) 2px 5px); }
  .bars { display: block; width: 100%; height: 11rem; margin-top: 4px; }
  .axis { display: flex; justify-content: space-between; margin-top: 4px; }

  .split { display: flex; height: 22px; border-radius: 6px; overflow: hidden; border: 1px solid var(--line-strong); margin-bottom: 8px; }
  .split .pm { background: var(--working); }
  .split .ps { background: repeating-linear-gradient(45deg, var(--working) 0 3px, var(--working-tint) 3px 7px); }

  .models { display: flex; flex-direction: column; }
  .mrow { display: grid; grid-template-columns: 8rem minmax(0, 1fr) 4.5rem 6.5rem; gap: 12px; align-items: center; padding: 10px 0; border-top: 1px solid var(--line); }
  .mname { font-weight: var(--fw-strong); }
  .mbar { display: block; height: 12px; background: var(--bg-sunken); border-radius: 6px; overflow: hidden; }
  .mbar i { display: block; height: 100%; background: var(--working); border-radius: 6px; }
  .mval { text-align: right; font-variant-numeric: tabular-nums; }

  @media (max-width: 560px) {
    .mrow { grid-template-columns: minmax(0, 1fr) auto auto; }
    .mbar { grid-column: 1 / -1; grid-row: 2; }
  }
</style>
