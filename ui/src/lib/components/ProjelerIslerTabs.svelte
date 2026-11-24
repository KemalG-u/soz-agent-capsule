<script lang="ts">
  import Icon from './Icon.svelte'
  import { t } from '../i18n/index.svelte'
  import { sf } from '../surface.svelte'
  import { sekmeIsSayisi } from '../sayac'

  let { active }: { active: 'projelerim' | 'is' } = $props()

  // Liste yalnız en yeni işleri yükler; sekme sunucunun verdiği toplamı gösterir (60 iş varken 50 yazmaz).
  const jobCount = $derived(sekmeIsSayisi(sf.jobs.filter((j) => j.id > 0).length, sf.jobsTotal, sf.jobsMore))
</script>

<div class="pi-wrap" data-projeler-isler-tabs>
  <nav class="pi-tabs" aria-label={t('navProjelerIsler')}>
    <a href="#/projelerim" class="pi-tab" class:on={active === 'projelerim'} aria-current={active === 'projelerim' ? 'page' : undefined} data-pi-tab="projelerim">
      <Icon name="folder" size={15} strokeWidth={active === 'projelerim' ? 2 : 1.7} />
      <span>{t('tabMyProjects')}</span>
    </a>
    <a href="#/is" class="pi-tab" class:on={active === 'is'} aria-current={active === 'is' ? 'page' : undefined} data-pi-tab="is">
      <Icon name="isler" size={15} strokeWidth={active === 'is' ? 2 : 1.7} />
      <span>{t('tabGivenJobs')}</span>
      {#if jobCount > 0}<span class="pi-cnt">{jobCount}</span>{/if}
    </a>
  </nav>
</div>

<style>
  .pi-wrap {
    flex: none;
    display: flex;
    align-items: center;
    padding: 6px clamp(12px, 3vw, 24px) 0;
    border-bottom: 1px solid var(--line);
    background: var(--bg);
  }
  .pi-tabs {
    display: flex;
    flex-wrap: wrap; /* A3: %200 yakınlaştırmada iki sekme yan yana sığmazsa alt satıra iner, sayfa yana taşmaz */
    align-items: stretch;
    gap: 0 4px;
    min-width: 0;
  }
  .pi-tab {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    min-height: 34px;
    padding: 0 10px;
    border-bottom: 2px solid transparent;
    margin-bottom: -1px;
    color: var(--ink-2);
    font-size: var(--fs-sm);
    font-weight: var(--fw-medium);
    text-decoration: none;
    white-space: nowrap;
    transition: color var(--dur-1) var(--ease), border-color var(--dur-1) var(--ease);
  }
  .pi-tab:hover {
    color: var(--ink);
  }
  .pi-tab.on {
    color: var(--ink);
    font-weight: var(--fw-strong);
    border-bottom-color: var(--primary);
  }
  .pi-cnt {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 18px;
    height: 18px;
    padding: 0 5px;
    border-radius: var(--chip-r, 999px);
    background: var(--bg-sunken);
    color: var(--ink-2);
    font-size: var(--fs-xs);
    font-variant-numeric: tabular-nums;
  }
  @media (pointer: coarse) and (hover: none) {
    .pi-tab {
      min-height: 44px;
      padding: 0 12px;
    }
  }
</style>
