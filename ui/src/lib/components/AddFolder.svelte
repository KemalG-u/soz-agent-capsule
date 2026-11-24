<script lang="ts">
  import { t } from '../i18n/index.svelte'
  import { pickFolder } from '../shell'
  import { addFolder, sf } from '../surface.svelte'
  import { inShell } from '../util'
  import FolderPicker from './FolderPicker.svelte'
  import Icon from './Icon.svelte'

  let { autofocus = false, onadded }: { autofocus?: boolean; onadded?: (p?: { name?: string | null }) => void } = $props()

  let path = $state('')
  let busy = $state(false)
  let input: HTMLInputElement | undefined = $state()
  const shell = inShell()
  // Tarayıcıda klasör seçici (kabukta yerel pencere var). Son kullanılan klasörler bu tarayıcıda tutulur: yalnız kolaylık, kaybolursa bir şey bozulmaz.
  const RECENT_KEY = "soz.recentFolders"
  let browsing = $state(false)
  let recent = $state<string[]>(loadRecent())
  function loadRecent(): string[] {
    try {
      const v = JSON.parse(localStorage.getItem(RECENT_KEY) ?? "[]")
      return Array.isArray(v) ? v.filter((x) => typeof x === "string").slice(0, 5) : []
    } catch {
      return []
    }
  }
  function remember(p: string) {
    recent = [p, ...recent.filter((x) => x.toLowerCase() !== p.toLowerCase())].slice(0, 5)
    try {
      localStorage.setItem(RECENT_KEY, JSON.stringify(recent))
    } catch {
      // depolama kapalı olabilir
    }
  }

  $effect(() => {
    if (autofocus) input?.focus()
  })
  // İş yazıldı ama klasör yok (yalnız sayaç artınca): kabukta klasör seçici açılır, tarayıcıda odak yol kutusuna gelir.
  let seen = sf.focusPath
  $effect(() => {
    const n = sf.focusPath
    if (n !== seen) {
      seen = n
      if (!pickFolder()) input?.focus()
    }
  })

  async function add(value?: string) {
    if (busy) return
    const typed = (value ?? path).trim()
    if (!typed) {
      sf.folderError = t('folderEmpty') // boşken ne eksik olduğu söylenir
      input?.focus()
      return
    }
    busy = true
    const p = await addFolder(typed)
    busy = false
    if (p) {
      remember(typed)
      path = ""
      browsing = false
      onadded?.(p)
    }
  }
  /** Seçiciden gelen klasör: yol kutusuna yazılır ve eklenir (kişi zaten "Bu klasörü seç" dedi). */
  function picked(p: string) {
    path = p
    void add(p)
  }
</script>

<form class="af" onsubmit={(e) => { e.preventDefault(); void add() }}>
  <label class="sr-only" for="af-path">{t('addFolderLabel')}</label>
  <div class="row">
    <span class="ico"><Icon name="folder" size={16} /></span>
    <input id="af-path" class="path" bind:this={input} bind:value={path} oninput={() => (sf.folderError = "")} placeholder={t('addFolderPlaceholder')} autocomplete="off" spellcheck="false" data-path />
    {#if shell}
      <button type="button" class="btn btn-quiet" onclick={() => pickFolder()} data-pick>{t('addFolderPick')}</button>
    {:else}
      <button type="button" class="btn btn-quiet" onclick={() => (browsing = !browsing)} aria-expanded={browsing} data-browse>{t('addFolderBrowse')}</button>
    {/if}
    <button type="submit" class="btn btn-primary" disabled={busy} data-addfolder>{busy ? t('addFolderBusy') : t('addFolderGo')}</button>
  </div>
  {#if sf.folderError}<p class="small err-line" role="alert" data-folder-error>{sf.folderError}</p>{:else}<p class="small muted">{t('addFolderHint')}</p>{/if}
  {#if browsing && !shell}<FolderPicker {recent} onpick={picked} onclose={() => (browsing = false)} />{/if}
</form>

<style>
  .af { display: flex; flex-direction: column; gap: 6px; }
  .row { display: flex; align-items: center; gap: 8px; padding: 4px; border: 1px solid var(--line-strong); border-radius: var(--radius); background: var(--surface); }
  .row:focus-within { border-color: var(--focus); }
  .ico { display: inline-flex; padding-left: 8px; color: var(--ink-3); }
  .path { flex: 1; min-width: 0; min-height: var(--ctl); border: 0; background: none; outline: none; font-family: var(--mono); font-size: var(--fs-sm); }
  @media (max-width: 560px) {
    .row { flex-wrap: wrap; }
    .path { flex-basis: calc(100% - 40px); }
    .row .btn { flex: 1; }
  }
</style>
