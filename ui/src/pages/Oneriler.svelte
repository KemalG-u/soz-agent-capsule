<script lang="ts">
  import { dismissDiscovery, getDiscoverySuggestions, recordDiscoveryUsage, restoreDiscovery, searchDiscovery } from '../lib/api5'
  import Icon from '../lib/components/Icon.svelte'
  import Skeleton from '../lib/components/Skeleton.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import { go, type Route } from '../lib/router.svelte'
  import { toast } from '../lib/store.svelte'
  import type { DiscoveryHit } from '../lib/types5'
  import { errText } from '../lib/util'

  // "Söz'ün önerdikleri": Söz'ün sık kullandığına bakıp önerdiği şeyler. Eylemler yalnız sunucunun verdikleridir:
  // "Şimdi değil" ve "Bir daha sorma" (reddet), geri alma, kullanım kaydı. "Aç" yalnız karşılığı olan sayfası bilinen şeylerde çizilir (ayar, araç).
  // Gölge kip açıksa (varsayılan) öneri yalnız bu sayfada görünür, kendiliğinden araya girmez: ekran bunu tek sakin satırla söyler.
  let loading = $state(true)
  let error = $state('')
  let shadow = $state(false)
  let items = $state<DiscoveryHit[]>([])
  let firstDay = $state<DiscoveryHit[]>([])
  let emptyText = $state<string | null>(null)
  let nearestText = $state<string | null>(null)
  /** Reddedilen satırlar: kimlik -> hangi kipte (geri almak için). */
  let gone = $state<Record<string, 'ignored' | 'never'>>({})
  let busy = $state('')
  let q = $state('')
  let found = $state<DiscoveryHit[] | null>(null)
  let searching = $state(false)
  let searchErr = $state('')

  async function load() {
    loading = true
    try {
      const r = await getDiscoverySuggestions()
      shadow = r.shadow
      items = r.items
      firstDay = r.firstDay ?? []
      emptyText = r.emptyText ?? null
      nearestText = r.nearest?.text ?? null
      error = ''
    } catch (e) {
      error = errText(e)
    } finally {
      loading = false
    }
  }
  $effect(() => {
    void load()
  })

  /** Karşılığı olan sayfa: yalnız adı bilinen türlerde. Başka türde "Aç" çizilmez. */
  function target(uid: string): Route | null {
    if (uid.startsWith('setting:')) return 'ayarlar'
    if (uid.startsWith('connector:')) return 'taniyorum'
    return null
  }
  async function open(it: DiscoveryHit) {
    const r = target(it.objectUid)
    if (!r) return
    void recordDiscoveryUsage(it.objectUid, 'oneriler').catch(() => {})
    go(r)
  }
  async function dismiss(it: DiscoveryHit, mode: 'ignored' | 'never') {
    if (busy) return // çift tık: istek sürerken ikinci gönderim yok
    busy = it.objectUid
    try {
      await dismissDiscovery(it.objectUid, mode)
      gone[it.objectUid] = mode
    } catch (e) {
      toast(errText(e))
    } finally {
      busy = ''
    }
  }
  async function undo(it: DiscoveryHit) {
    const mode = gone[it.objectUid]
    if (!mode || busy) return
    busy = it.objectUid
    try {
      await restoreDiscovery(it.objectUid, mode)
      delete gone[it.objectUid]
    } catch (e) {
      toast(errText(e))
    } finally {
      busy = ''
    }
  }
  async function search(e: Event) {
    e.preventDefault()
    const text = q.trim()
    if (!text) {
      found = null
      return
    }
    searching = true
    searchErr = ''
    try {
      found = (await searchDiscovery(text)).items
    } catch (err) {
      searchErr = errText(err)
    } finally {
      searching = false
    }
  }
</script>

{#snippet card(it: DiscoveryHit, kind: string)}
  {@const away = gone[it.objectUid]}
  <li class="r" data-suggest-row={kind} data-object={it.objectUid}>
    {#if away}
      <p class="small muted" role="status" data-suggest-gone>{away === 'never' ? t('dsGoneNever', { t: it.title }) : t('dsGoneLater', { t: it.title })} <button type="button" class="linkbtn" onclick={() => undo(it)} disabled={busy === it.objectUid} data-suggest-undo>{t('dsUndo')}</button></p>
    {:else}
      <p class="sent"><span class="nm">{it.title}</span>{#if it.reason}<span class="why">{it.reason}</span>{:else if it.description}<span class="why">{it.description}</span>{/if}</p>
      <div class="acts" role="group" aria-label={t('dsActions', { t: it.title })}>
        {#if target(it.objectUid)}<button type="button" class="btn btn-primary btn-sm" onclick={() => open(it)} data-suggest-open>{t('dsOpen')}</button>{/if}
        {#if kind === 'suggest'}
          <button type="button" class="btn btn-sm" onclick={() => dismiss(it, 'ignored')} disabled={busy === it.objectUid} data-suggest-later>{t('dsLater')}</button>
          <button type="button" class="btn btn-quiet btn-sm" onclick={() => dismiss(it, 'never')} disabled={busy === it.objectUid} data-suggest-never>{t('dsNever')}</button>
        {/if}
      </div>
    {/if}
  </li>
{/snippet}

<div class="page" data-oneriler>
  <nav class="subtabs" aria-label={t('ledgerTabs')}>
    <a href="#/defter">{t('tabDecisions')}</a>
    <a href="#/defter/tekrarlar">{t('navRepeats')}</a>
    <a href="#/oneriler" aria-current="page">{t('navSuggest')}</a>
  </nav>

  <header class="page-head">
    <div>
      <h1 class="h1">{t('navSuggest')}</h1>
      <p class="lead">{t('dsLead')}</p>
    </div>
  </header>

  {#if loading}
    <Skeleton rows={3} />
  {:else if error}
    <p class="err-line" role="alert" data-suggest-error>{error}</p>
    <p class="retry-row"><button class="btn" onclick={load}>{t('retry')}</button></p>
  {:else}
    <!-- Gölge kip yalnız KENDİLİĞİNDEN çıkan öneriyi (bildirim, kapsül, iş ekranı) susturur. Bu sayfa kişinin kendi açtığı yerdir: dayanağı olan öneri her zaman burada. -->
    {#if shadow}<p class="small muted note" data-suggest-shadow>{t('dsShadow')}</p>{/if}
    {#if firstDay.length > 0}
      <ul class="rows" aria-label={t('navSuggest')} data-suggest-firstday>
        {#each firstDay as it (it.objectUid)}{@render card(it, 'suggest')}{/each}
      </ul>
    {/if}
    {#if items.length > 0}
      <ul class="rows" aria-label={t('navSuggest')} data-suggest-list>
        {#each items as it (it.objectUid)}{@render card(it, 'suggest')}{/each}
      </ul>
    {:else if firstDay.length === 0}
      <div class="empty-state" data-empty="no-suggest">
        <p>{emptyText ?? t('dsNone')}</p>
        {#if nearestText}<p class="small muted" data-suggest-nearest>{nearestText}</p>{/if}
      </div>
    {/if}
  {/if}

  <section class="blk" aria-labelledby="ds-find">
    <h2 class="h2s" id="ds-find"><Icon name="ara" size={16} />{t('dsFind')}</h2>
    <form class="find" onsubmit={search} role="search">
      <label class="sr-only" for="ds-q">{t('dsFind')}</label>
      <input id="ds-q" class="input" type="search" bind:value={q} placeholder={t('dsFindHint')} autocomplete="off" data-suggest-q />
      <button type="submit" class="btn btn-sm" disabled={searching || !q.trim()}>{t('dsSearch')}</button>
    </form>
    {#if searchErr}<p class="small err-line" role="alert">{searchErr}</p>{/if}
    {#if found !== null}
      {#if found.length === 0}
        <p class="small muted" data-suggest-nofind>{t('dsNoFind')}</p>
      {:else}
        <ul class="rows" data-suggest-found>{#each found as it (it.objectUid)}{@render card(it, 'find')}{/each}</ul>
      {/if}
    {/if}
  </section>
</div>

<style>
  .note { margin-top: 4px; max-width: 64ch; }
  .rows { display: flex; flex-direction: column; margin-top: 8px; border-bottom: 1px solid var(--line); }
  .r { display: flex; flex-direction: column; gap: 6px; padding: 14px 0; border-top: 1px solid var(--line); }
  .sent { max-width: 64ch; line-height: 1.45; }
  .nm { display: block; font-weight: var(--fw-medium); }
  .why { display: block; margin-top: 2px; color: var(--ink-2); }
  .acts { display: flex; flex-wrap: wrap; gap: 6px; }
  .blk { margin-top: 32px; }
  .h2s { display: flex; align-items: center; gap: 8px; font-size: var(--fs-md); font-weight: var(--fw-strong); }
  .find { display: flex; gap: 8px; max-width: 32rem; margin-top: 10px; }
  .find .input { flex: 1; min-width: 0; }
  .linkbtn { display: inline-flex; align-items: center; min-height: 32px; padding: 0 6px; border: 0; background: none; color: var(--ink); font: inherit; text-decoration: underline; text-underline-offset: 2px; cursor: pointer; }
  @media (pointer: coarse) and (hover: none) { .linkbtn { min-height: 40px; } }
</style>
