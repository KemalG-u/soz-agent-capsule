<script lang="ts">
  import { t } from '../i18n/index.svelte'
  import { app, startImport } from '../store.svelte'
  import { go } from '../router.svelte'

  let { variant = 'hero' }: { variant?: 'hero' | 'inline' } = $props()

  const sum = $derived(app.summary)

  function dismiss() {
    app.importResult = null
    go('is')
  }
</script>

{#if variant === 'hero'}
  <section class="hero" aria-live="polite">
    {#if app.importing}
      <h2 class="h1">{t('importing')}</h2>
      <p class="lead">{t('importingHint')}</p>
      <div class="bar" role="progressbar" aria-label={t('importing')}><span></span></div>
    {:else if app.importResult && sum}
      <h2 class="h1">{t('importDone')}</h2>
      <dl class="nums">
        <div><dd>{sum.sessions}</dd><dt>{t('importResultSessions')}</dt></div>
        <div><dd>{sum.utterances}</dd><dt>{t('importResultUtterances')}</dt></div>
        <div><dd>{sum.proposed}</dd><dt>{t('importResultProposed')}</dt></div>
        <div><dd>{sum.repeatClusters}</dd><dt>{t('importResultRepeats')}</dt></div>
      </dl>
      <button class="btn btn-primary btn-lg" onclick={dismiss}>{t('importSeeToday')}</button>
    {:else}
      <h2 class="h1">{t('emptyTitle')}</h2>
      <p class="lead">{t('emptyBody')}</p>
      {#if app.importError}<p class="err" role="alert">{t('importFailed')}: {app.importError}</p>{/if}
      <button class="btn btn-primary btn-lg" onclick={startImport}>{t('importAction')}</button>
    {/if}
  </section>
{:else}
  <div class="inline" aria-live="polite">
    <button class="btn" onclick={startImport} disabled={app.importing}>{app.importing ? t('importing') : t('importAgain')}</button>
    {#if app.importing}
      <div class="bar" role="progressbar" aria-label={t('importing')}><span></span></div>
    {:else if app.importResult && sum}
      <p class="small muted">
        {t('importDone')}: {sum.sessions} {t('importResultSessions')}, {sum.utterances} {t('importResultUtterances')}, {sum.proposed} {t('importResultProposed')}
      </p>
    {:else if app.importError}
      <p class="small err" role="alert">{t('importFailed')}: {app.importError}</p>
    {/if}
  </div>
{/if}

<style>
  .hero {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 22px;
    padding: clamp(28px, 8vw, 96px) 0;
    max-width: 40rem;
  }
  .hero .lead { margin: 0; font-size: var(--fs-md); max-width: 36rem; }
  .nums {
    display: grid;
    grid-template-columns: repeat(4, auto);
    gap: 12px clamp(18px, 5vw, 44px);
    margin: 0;
  }
  .nums div { display: flex; flex-direction: column; }
  .nums dd {
    margin: 0;
    font-family: var(--sans);
    font-weight: var(--fw-strong);
    font-size: var(--fs-xl);
    line-height: 1;
    letter-spacing: -0.03em;
    font-variant-numeric: tabular-nums;
  }
  .nums dt { color: var(--ink-3); font-size: var(--fs-sm); margin-top: 4px; }
  .err { color: var(--stalled); }

  .bar {
    position: relative;
    width: min(100%, 26rem);
    height: 6px;
    border-radius: 999px;
    background: var(--bg-sunken);
    overflow: hidden;
  }
  .bar span {
    position: absolute;
    inset: 0 auto 0 0;
    width: 38%;
    border-radius: inherit;
    background: var(--ink);
    animation: slide 1.4s var(--ease) infinite;
  }
  @keyframes slide {
    from { transform: translateX(-100%); }
    to { transform: translateX(270%); }
  }
  @media (prefers-reduced-motion: reduce) {
    .bar span { animation: none; width: 100%; opacity: 0.45; }
  }

  .inline { display: flex; flex-direction: column; align-items: flex-start; gap: 10px; }
  @media (max-width: 480px) {
    .nums { grid-template-columns: repeat(2, auto); }
  }
</style>
