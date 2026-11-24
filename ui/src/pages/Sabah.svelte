<script lang="ts">
  import { getMorning } from '../lib/api2'
  import EmptyState from '../lib/components/EmptyState.svelte'
  import Icon from '../lib/components/Icon.svelte'
  import Skeleton from '../lib/components/Skeleton.svelte'
  import { t, type Key } from '../lib/i18n/index.svelte'
  import { go } from '../lib/router.svelte'
  import { sabahAralikOku, sabahAralikYaz } from '../lib/sabahAralik'
  import { isNoChange, resolveReasons } from '../lib/surface.svelte'
  import { app } from '../lib/store.svelte'
  import type { Morning, MorningEntry, MorningJob } from '../lib/types'
  import { ago, errText, fmtTokens, fmtUsd } from '../lib/util'

  // "Sabah": sabah raporunun ekranı. Bölüm sırası: başlık cümlesi, geceden biten işler, seni bekleyenler, kalanlar, bekçi neyi durdurdu, harcama, gece işleri.
  // Boş bölüm çizilmez. Pencerede hiç iş yoksa tek cümle: "Bu aralıkta iş yok". Metinlerin çoğu sunucudan sade gelir; arayüz kod adı göstermez.
  const HOURS = 3600_000
  let windowH = $state<number>(sabahAralikOku()) // 0: sunucunun varsayılanı (son 16 saat); seçim işe gidip Geri gelince korunur
  let m = $state<Morning | null>(null)
  let error = $state('')

  async function load() {
    try {
      m = await getMorning(windowH > 0 ? new Date(Date.now() - windowH * HOURS).toISOString() : undefined)
      error = ''
      // Ajanın hiçbir dosyayı değiştirmediği işler "kanıtıyla bitti" gibi yeşil sayılmaz: son kanıt kaydından okunur.
      void resolveReasons((m.passed ?? []).map((e) => ({ id: e.job.id, state: 'gecti', lastRunId: e.runId ?? null, lastReason: e.reasonCode ?? null })))
    } catch (e) {
      error = errText(e)
    }
  }
  $effect(() => {
    void app.tick
    sabahAralikYaz(windowH)
    void load()
  })

  const stateKey: Record<string, Key> = {
    cevap_bekliyor: 'mrStAsk', onay_bekliyor: 'mrStApprove', karar_bekliyor: 'mrStDecide', kaldi: 'mrStFailed', baslatilamadi: 'mrStNoStart', durduruldu: 'mrStStopped',
    gecti: 'mrStPassed', calisiyor: 'mrStRunning', verildi: 'mrStGiven', sirada: 'mrStQueued', taslak: 'mrStDraft', iptal: 'mrStCancel',
  }
  // Sunucunun iş durumu ajan bitse de "verildi" kalır; kanıtsız biten işi her yerde aynı etiketle ("Kanıtsız") gösteririz.
  const unprovenIds = $derived(new Set((m?.unverified ?? []).map((e) => e.job.id)))
  const stateText = (s: string | null | undefined, id?: number) =>
    s === 'verildi' && id !== undefined && unprovenIds.has(id) ? t('grpUnproven') : s && stateKey[s] ? t(stateKey[s]) : ''

  const passed = $derived(m?.passed ?? [])
  const failed = $derived(m?.failed ?? [])
  const failedIds = $derived(new Set(failed.map((e) => e.job.id)))
  const waiting = $derived((m?.waitingJobs ?? []).filter((j) => !failedIds.has(j.id)))
  const unverified = $derived(m?.unverified ?? [])
  const stops = $derived(m?.guardStops ?? [])
  const spend = $derived(m?.spend && (m.spend.runs > 0 || m.spend.inputTokens > 0 || m.spend.outputTokens > 0) ? m.spend : null)
  const night = $derived(m?.night && (m.night.jobs.length > 0 || m.night.open > 0) ? m.night : null)
  const nothing = $derived(!!m && (m.jobsInWindow ?? 0) === 0 && waiting.length === 0 && stops.length === 0 && passed.length === 0 && failed.length === 0 && unverified.length === 0)

  const open = (id: number) => go('is', String(id))
  const tries = (e: MorningEntry) => ((e.attempts ?? 1) > 1 ? t('mrTries', { n: e.attempts ?? 1 }) : '')
  const noChangeOf = (e: MorningEntry) => isNoChange({ id: e.job.id, lastRunId: e.runId ?? null, lastReason: e.reasonCode ?? null })
  const passedExtra = (e: MorningEntry) => [noChangeOf(e) ? t('grpNoChange') : '', tries(e)].filter(Boolean).join(' · ')
</script>

{#snippet jobRow(j: { id: number; title: string; projectName: string | null; night?: boolean }, extra: string, sub: string | null, kind: string)}
  <li class="r" data-morning-row={kind}>
    <div class="rt">
      <a href={`#/is/${j.id}`} class="jt" onclick={(e) => { e.preventDefault(); open(j.id) }}>{j.title}</a>
      <span class="small muted">{j.projectName ?? ''}{#if extra}{j.projectName ? ' · ' : ''}{extra}{/if}</span>
    </div>
    {#if sub}<p class="small sub">{sub}</p>{/if}
  </li>
{/snippet}

<div class="page" data-sabah>
  <nav class="subtabs" aria-label={t('navHistory')}>
    <a href="#/sabah" aria-current="page">{t('tabHistorySummary')}</a>
    <a href="#/neoldu">{t('tabHistoryEvents')}</a>
  </nav>

  <header class="page-head">
    <div>
      <h1 class="h1">{t('tabHistorySummary')}</h1>
      {#if m}<p class="lead" data-morning-sentence>{m.sentence}</p>{/if}
    </div>
    <div class="field win">
      <label class="label" for="mr-win">{t('mrWindow')}</label>
      <select id="mr-win" class="select" bind:value={windowH} data-morning-window>
        <option value={0}>{t('mrLast16')}</option>
        <option value={24}>{t('mrLast24')}</option>
        <option value={72}>{t('mrLast72')}</option>
      </select>
    </div>
  </header>

  {#if error && !m}
    <p class="err-line" role="alert" data-morning-error>{error}</p>
    <p class="retry-row"><button class="btn" onclick={load}>{t('retry')}</button></p>
  {:else if !m}
    <Skeleton rows={4} />
  {:else if nothing}
    <EmptyState id="no-morning" text={t('mrNone')} action={t('emptyGoJobs')} onaction={() => go('is')} />
  {:else}
    {#if passed.length > 0}
      <section class="blk" aria-labelledby="mr-passed" data-morning-section="passed">
        <h2 class="h2s" id="mr-passed"><Icon name="check" size={16} strokeWidth={2} />{t('mrPassed')} <span class="small muted n">{passed.length}</span></h2>
        <ul class="rows">{#each passed as e (e.job.id)}{@render jobRow(e.job, passedExtra(e), e.summary ?? null, noChangeOf(e) ? 'nochange' : 'passed')}{/each}</ul>
      </section>
    {/if}

    {#if waiting.length > 0}
      <section class="blk" aria-labelledby="mr-wait" data-morning-section="waiting">
        <h2 class="h2s" id="mr-wait"><Icon name="waiting" size={16} />{t('mrWaiting')} <span class="small muted n">{waiting.length}</span></h2>
        <ul class="rows">{#each waiting as j (j.id)}{@render jobRow(j, stateText(j.state, j.id), (j as MorningJob).question ?? null, 'waiting')}{/each}</ul>
      </section>
    {/if}

    {#if failed.length > 0}
      <section class="blk" aria-labelledby="mr-fail" data-morning-section="failed">
        <h2 class="h2s" id="mr-fail"><Icon name="x" size={16} strokeWidth={2} />{t('mrFailed')} <span class="small muted n">{failed.length}</span></h2>
        <ul class="rows">{#each failed as e (e.job.id)}{@render jobRow(e.job, tries(e), e.failedChecks && e.failedChecks.length ? t('mrFailedOn', { l: e.failedChecks.join(', ') }) : null, 'failed')}{/each}</ul>
      </section>
    {/if}

    {#if unverified.length > 0}
      <section class="blk" aria-labelledby="mr-unv" data-morning-section="unverified">
        <h2 class="h2s" id="mr-unv"><Icon name="unchecked" size={16} />{t('mrUnverified')} <span class="small muted n">{unverified.length}</span></h2>
        <ul class="rows">{#each unverified as e (e.job.id)}{@render jobRow(e.job, '', e.summary ?? null, 'unverified')}{/each}</ul>
      </section>
    {/if}

    {#if stops.length > 0}
      <section class="blk" aria-labelledby="mr-guard" data-morning-section="guard">
        <h2 class="h2s" id="mr-guard"><Icon name="bekci" size={16} />{t('mrGuard')} <span class="small muted n">{stops.length}</span></h2>
        <ul class="rows">
          {#each stops as g, i (i)}
            <li class="r" data-morning-row="guard">
              <div class="rt">
                <span class="chip {g.action === 'deny' ? 'chip-stalled' : 'chip-waiting'}">{g.action === 'deny' ? t('mrGuardDeny') : t('mrGuardAsk')}</span>
                <span class="small muted">{g.projectName ?? ''} · <time datetime={g.at}>{ago(g.at)}</time></span>
              </div>
              <p class="small mono sum">{g.summary}</p>
              {#if g.reason}<p class="small muted">{g.reason}</p>{/if}
            </li>
          {/each}
        </ul>
      </section>
    {/if}

    {#if spend}
      <section class="blk" aria-labelledby="mr-spend" data-morning-section="spend">
        <h2 class="h2s" id="mr-spend"><Icon name="harcama" size={16} />{t('mrSpend')}</h2>
        <p class="small facts" data-morning-spend>
          <span>{t('mrRuns', { n: spend.runs })}</span>
          <span>{t('mrIn', { n: fmtTokens(spend.inputTokens) })}</span>
          <span>{t('mrOut', { n: fmtTokens(spend.outputTokens) })}</span>
          {#if spend.cachedTokens > 0}<span>{t('mrCached', { n: fmtTokens(spend.cachedTokens) })}</span>{/if}
          {#if spend.costUsd !== null}<span class="cost" data-morning-cost>{fmtUsd(spend.costUsd)}</span>{/if}
        </p>
        <p class="small muted" data-morning-spend-note>{spend.note}</p>
      </section>
    {/if}

    {#if night}
      <section class="blk" aria-labelledby="mr-night" data-morning-section="night">
        <h2 class="h2s" id="mr-night"><Icon name="moon" size={16} />{t('mrNight')}</h2>
        <p class="small facts" data-morning-night>
          <span>{t('mrNightPassed', { n: night.passed })}</span>
          <span>{t('mrNightFailed', { n: night.failed })}</span>
          <span>{t('mrNightWaiting', { n: night.waiting })}</span>
          <span>{t('mrNightOpen', { n: night.open })}</span>
        </p>
        {#if night.jobs.length > 0}
          <ul class="rows">{#each night.jobs as j (j.id)}{@render jobRow(j, stateText(j.state, j.id), null, 'night')}{/each}</ul>
        {/if}
        <p class="small muted" data-morning-night-note>{night.note}</p>
      </section>
    {/if}
  {/if}
</div>

<style>
  .win { min-width: 11rem; }
  .blk { margin-top: 26px; }
  .h2s { display: flex; align-items: center; gap: 8px; font-size: var(--fs-md); font-weight: var(--fw-strong); }
  .n { padding: 0 8px; border-radius: var(--radius-sm); background: var(--bg-sunken); cursor: default; font-weight: var(--fw-regular); }
  .rows { display: flex; flex-direction: column; margin-top: 8px; border-bottom: 1px solid var(--line); }
  .r { display: flex; flex-direction: column; gap: 3px; padding: 11px 0; border-top: 1px solid var(--line); }
  .rt { display: flex; flex-wrap: wrap; align-items: baseline; gap: 2px 12px; }
  .jt { display: inline-flex; align-items: center; min-height: 24px; font-weight: var(--fw-medium); color: var(--ink); text-decoration: none; }
  .jt:hover { text-decoration: underline; text-underline-offset: 2px; }
  .sub { color: var(--ink-2); max-width: 64ch; }
  .sum { color: var(--ink); max-width: 64ch; overflow-wrap: anywhere; }
  .facts { display: flex; flex-wrap: wrap; gap: 2px 18px; margin-top: 8px; color: var(--ink-2); }
  .cost { font-weight: var(--fw-strong); color: var(--ink); }
</style>
