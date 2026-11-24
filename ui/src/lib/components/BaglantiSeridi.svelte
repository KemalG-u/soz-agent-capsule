<script lang="ts">
  import { onMount } from 'svelte'
  import { t } from '../i18n/index.svelte'
  import { loadAttention } from '../attention.svelte'
  import { refreshSurface, sf } from '../surface.svelte'
  import { isVisible } from '../visible'
  import Icon from './Icon.svelte'

  // O11: İşler sayfasında ve iş ayrıntısında da Şu An'daki "Söz'e ulaşamıyorum" şeridi çıkar. Kaynak iş listesinin son okuması.
  // Sayfa açıkken 10 sn'de bir iş listesi yeniden okunur: sunucu kesilince şerit en geç yaklaşık 20 sn içinde görünür, gelince kendiliğinden kalkar.
  let nowTs = $state(Date.now())
  let bakiyor = $state(false)
  onMount(() => {
    const tk = setInterval(() => { if (isVisible()) nowTs = Date.now() }, 1000)
    const yk = setInterval(() => { if (isVisible()) refreshSurface() }, 10000)
    return () => { clearInterval(tk); clearInterval(yk) }
  })
  const kopuk = $derived(sf.jobsFailed && sf.jobsOkAt > 0 && nowTs - sf.jobsOkAt > 8000)
  const yas = $derived.by(() => {
    const s = Math.max(0, Math.round((nowTs - sf.jobsOkAt) / 1000))
    return s < 60 ? t('linkSecs', { n: s }) : t('linkMins', { n: Math.round(s / 60) })
  })
  async function yeniden() {
    if (bakiyor) return
    bakiyor = true
    try {
      refreshSurface()
      await loadAttention()
    } finally {
      bakiyor = false
    }
  }
</script>

{#if kopuk}
  <div class="link-lost" role="alert" data-link-lost>
    <Icon name="alert" size={16} strokeWidth={2} /><span>{t('linkLost', { s: yas })}</span>
    <button type="button" class="btn btn-sm" onclick={yeniden} disabled={bakiyor} data-link-retry>{t('retry')}</button>
  </div>
{/if}

<style>
  .link-lost { flex: none; display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; margin: 8px var(--gutter, 16px) 0; padding: 8px 12px; border: 1px solid var(--waiting); border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); font-size: var(--fs-sm); }
  .link-lost :global(svg) { flex: none; color: var(--waiting); }
  .link-lost span { flex: 1 1 14rem; min-width: 0; }
</style>
