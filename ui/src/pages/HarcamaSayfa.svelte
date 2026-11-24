<script lang="ts">
  import { t } from '../lib/i18n/index.svelte'
  import { router } from '../lib/router.svelte'
  import Harcama from './Harcama.svelte'
  import Karne from './Karne.svelte'

  const tab = $derived(router.sub === 'karne' ? 'karne' : 'harcama')
</script>

<div class="page">
  <header class="page-head">
    <h1 class="h1">{t('navSpend')}</h1>
  </header>
  <nav class="subtabs" aria-label={t('spendTabs')}>
    <a href="#/harcama" aria-current={tab === 'harcama' ? 'page' : undefined}>{t('tabSpend')}</a>
    <a href="#/harcama/karne" aria-current={tab === 'karne' ? 'page' : undefined}>{t('navReport')}</a>
  </nav>
  {#if tab === 'harcama'}
    <Harcama />
  {:else}
    <Karne embedded />
  {/if}
</div>
