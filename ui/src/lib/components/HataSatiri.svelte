<script lang="ts">
  import type { Hata } from '../hata'
  import { t } from '../i18n/index.svelte'

  // Hata satırı: sade cümle her zaman görünür, ham sunucu metni yalnız "Teknik ayrıntı" düğmesinin altındadır.
  let { h, small = true, ...rest }: { h: Hata | null; small?: boolean; [k: string]: unknown } = $props()
  let open = $state(false)
  $effect(() => { void h; open = false })
</script>

{#if h}
  <div class="hs" {...rest}>
    <p class="err-line" class:small role="alert" data-hata-text>{h.text}</p>
    {#if h.detail}
      <button type="button" class="hs-tog" aria-expanded={open} onclick={() => (open = !open)} data-hata-toggle>{open ? t('hideDetail') : t('showDetail')}</button>
      {#if open}<p class="hs-det small muted" data-hata-detail>{h.detail}</p>{/if}
    {/if}
  </div>
{/if}

<style>
  .hs { display: flex; flex-wrap: wrap; align-items: baseline; gap: 2px 8px; min-width: 0; }
  .hs p { margin: 0; }
  .hs-tog { border: 0; background: none; padding: 2px 4px; color: var(--ink-3); font: inherit; font-size: var(--fs-xs); text-decoration: underline; cursor: pointer; }
  .hs-tog:hover { color: var(--ink); }
  .hs-det { flex: 1 1 100%; overflow-wrap: anywhere; padding: 4px 8px; border-radius: var(--radius-sm); background: var(--bg-sunken); user-select: text; }
</style>
