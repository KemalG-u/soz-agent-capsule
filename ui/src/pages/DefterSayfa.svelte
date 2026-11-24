<script lang="ts">
  import { getDecisions } from '../lib/api'
  import Proposals from '../lib/components/Proposals.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import { router } from '../lib/router.svelte'
  import { app } from '../lib/store.svelte'
  import type { Decision } from '../lib/types'
  import Defter from './Defter.svelte'
  import Tekrarlar from './Tekrarlar.svelte'

  const tab = $derived(router.sub === 'tekrarlar' ? 'tekrarlar' : 'kararlar')
  let proposals = $state<Decision[]>([])

  $effect(() => {
    void app.tick
    getDecisions({ status: 'proposed' })
      .then((p) => (proposals = p))
      .catch(() => {})
  })
</script>

<div class="page">
  <header class="page-head">
    <div>
      <h1 class="h1">{t('ledgerTitle')}</h1>
      <p class="lead">{t('ledgerLead')}</p>
    </div>
  </header>

  {#if proposals.length > 0}
    <section class="props" id="oneriler" aria-labelledby="oneriler-t">
      <h2 class="h2" id="oneriler-t">{t('proposalsTitle')} <span class="gcount">{proposals.length}</span></h2>
      <p class="lead small">{t('proposalsLead')}</p>
      <Proposals items={proposals} />
    </section>
  {/if}

  <nav class="subtabs" aria-label={t('ledgerTabs')}>
    <a href="#/defter" aria-current={tab === 'kararlar' ? 'page' : undefined}>{t('tabDecisions')}</a>
    <a href="#/defter/tekrarlar" aria-current={tab === 'tekrarlar' ? 'page' : undefined}>{t('navRepeats')}</a>
    <a href="#/oneriler">{t('navSuggest')}</a>
  </nav>
  {#if tab === 'kararlar'}
    <Defter embedded />
  {:else}
    <Tekrarlar embedded />
  {/if}
</div>

<style>
  .props { margin-bottom: clamp(24px, 4vw, 40px); }
  .props .lead { margin: 6px 0 16px; }
  .gcount { display: inline-flex; align-items: center; justify-content: center; min-width: 26px; height: 26px; padding: 0 8px; border-radius: var(--chip-r, 999px); background: var(--ok-tint); color: var(--ok); font-size: var(--fs-xs); font-weight: var(--fw-strong); vertical-align: middle; }
</style>
