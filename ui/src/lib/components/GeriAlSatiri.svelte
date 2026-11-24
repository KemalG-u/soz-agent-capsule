<script lang="ts">
  import { t } from '../i18n/index.svelte'
  let { label, onundo }: { label: string; onundo: () => void } = $props()
</script>

<p class="small undo" role="status" data-undo>
  {t('ckRemoved', { l: label })}
  <button type="button" class="btn btn-quiet btn-sm" onclick={onundo} data-undo-btn>{t('undoBtn')}</button>
</p>

<style>
  .undo { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; margin: 8px 0 0; color: var(--ink-2); }
</style>
