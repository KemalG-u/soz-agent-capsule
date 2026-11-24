<script lang="ts">
  import { browseFolders } from '../api2'
  import { t } from '../i18n/index.svelte'
  import type { FolderListing } from '../types'
  import { errText } from '../util'
  import Icon from './Icon.svelte'

  // Tarayıcıda klasör seçici: tam yol yazmak yerine sürücüden başlayıp alt klasörlere inilir.
  // Sunucu yalnız klasör adlarını verir (gizli ve sistem klasörleri yok). Masaüstü kabukta bu bileşen kullanılmaz, yerel pencere açılır.
  let { recent = [], onpick, onclose }: { recent?: string[]; onpick: (path: string) => void; onclose?: () => void } = $props()

  let data = $state<FolderListing | null>(null)
  let loading = $state(true)
  let error = $state('')
  let listEl: HTMLElement | undefined = $state()

  async function open(path: string | null) {
    loading = true
    try {
      data = await browseFolders(path)
      error = ''
    } catch (e) {
      error = errText(e)
    } finally {
      loading = false
    }
    queueMicrotask(() => listEl?.scrollTo({ top: 0 }))
  }
  $effect(() => {
    void open(recent[0] ? parentOf(recent[0]) : null)
  })

  /** Son kullanılan klasörün üst klasöründen başla: komşu klasörler hemen görünür. */
  function parentOf(p: string): string | null {
    const i = Math.max(p.lastIndexOf('\\'), p.lastIndexOf('/'))
    if (i <= 2) return null
    return p.slice(0, i)
  }
  /** Yol çubuğu: tıklanabilir üst klasörler. */
  const crumbs = $derived.by(() => {
    if (!data?.path) return [] as { name: string; path: string }[]
    const parts = data.path.replace(/[\\/]+$/, '').split(/[\\/]/)
    const out: { name: string; path: string }[] = []
    let acc = ''
    for (const [i, part] of parts.entries()) {
      acc = i === 0 ? part + '\\' : acc.replace(/[\\/]+$/, '') + '\\' + part
      out.push({ name: part, path: acc })
    }
    return out
  })
</script>

<div class="fp" data-folder-picker>
  <div class="bar">
    <button type="button" class="btn btn-quiet btn-sm" onclick={() => open(data?.parent ?? null)} disabled={loading || !data?.path} data-fp-up><Icon name="back" size={14} />{data?.parent ? t('fpUp') : t('fpDrives')}</button>
    <nav class="crumbs small" aria-label={t('fpPath')}>
      {#if data?.path}
        {#each crumbs as c, i (c.path)}
          {#if i > 0}<span class="sep" aria-hidden="true">›</span>{/if}
          <button type="button" class="crumb" onclick={() => open(c.path)} aria-current={i === crumbs.length - 1 ? 'location' : undefined}>{c.name}</button>
        {/each}
      {:else}
        <span class="muted">{t('fpDrivesTitle')}</span>
      {/if}
    </nav>
  </div>

  {#if recent.length > 0 && !data?.path}
    <div class="recent" data-fp-recent>
      <p class="small muted">{t('fpRecent')}</p>
      <ul class="rl">
        {#each recent as r (r)}<li><button type="button" class="btn btn-sm" onclick={() => onpick(r)} title={r}>{r}</button></li>{/each}
      </ul>
    </div>
  {/if}

  {#if error}
    <p class="small err-line" role="alert" data-fp-error>{error}</p>
  {:else if loading && !data}
    <p class="small muted" role="status">{t('fpLoading')}</p>
  {:else if data}
    <ul class="list" bind:this={listEl} aria-label={t('fpFolders')} data-fp-list>
      {#each data.folders as f (f.path)}
        <li><button type="button" class="item" onclick={() => open(f.path)} data-fp-item><Icon name="folder" size={16} /><span>{f.name}</span></button></li>
      {:else}
        <li class="small muted empty">{data.path ? t('fpEmpty') : t('fpNoDrives')}</li>
      {/each}
    </ul>
    {#if data.truncated}<p class="small muted">{t('fpTruncated')}</p>{/if}
    {#if data.path}
      <div class="foot">
        <button type="button" class="btn btn-primary" disabled={!data.selectable} onclick={() => data?.path && onpick(data.path)} data-fp-pick>{t('fpPick')}</button>
        {#if !data.selectable && data.reason}<p class="small muted" data-fp-reason>{data.reason}</p>{/if}
      </div>
    {/if}
  {/if}
  {#if onclose}<p class="close"><button type="button" class="btn btn-quiet btn-sm" onclick={onclose} data-fp-close>{t('fpClose')}</button></p>{/if}
</div>

<style>
  .fp { display: flex; flex-direction: column; gap: 10px; padding: 12px; border: 1px solid var(--line); border-radius: var(--radius); background: var(--surface); }
  .bar { display: flex; align-items: center; gap: 8px; min-width: 0; }
  .crumbs { display: flex; align-items: center; flex-wrap: wrap; gap: 2px; min-width: 0; }
  .crumb { min-height: 28px; padding: 0 6px; border: 0; border-radius: 6px; background: none; color: var(--ink-2); font: inherit; cursor: pointer; }
  .crumb:hover { background: var(--hover); color: var(--ink); }
  .crumb[aria-current='location'] { color: var(--ink); font-weight: var(--fw-medium); }
  .sep { color: var(--ink-3); }
  .recent .rl { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; margin-top: 4px; }
  .recent .btn { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-family: var(--mono); font-size: var(--fs-xs); }
  .list { display: flex; flex-direction: column; max-height: min(40vh, 18rem); overflow-y: auto; border: 1px solid var(--line); border-radius: var(--radius); }
  .list li + li { border-top: 1px solid var(--line); }
  .item { display: flex; align-items: center; gap: 8px; width: 100%; min-height: var(--row-s); padding: 0 12px; border: 0; background: none; color: var(--ink); font: inherit; text-align: left; cursor: pointer; }
  .item:hover { background: var(--hover); }
  .item span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .empty { padding: 12px; }
  .foot { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; }
  .close { margin: 0; }
</style>
