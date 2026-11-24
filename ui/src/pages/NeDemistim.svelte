<script lang="ts">
  import { getProjects, searchUtterances } from '../lib/api'
  import EmptyState from '../lib/components/EmptyState.svelte'
  import Icon from '../lib/components/Icon.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import { go } from '../lib/router.svelte'
  import { app } from '../lib/store.svelte'
  import { autoScanInBackground, pf } from '../lib/profileStore.svelte'
  import type { Project, UtterancePage } from '../lib/types'
  import { fmtDate, highlightParts, projectLabel, toolName } from '../lib/util'

  const LIMIT = 50
  let q = $state('')
  let term = $state('') // gecikmeli (debounce) arama
  let projectSel = $state('')
  let offset = $state(0)
  let projects = $state<Project[]>([])
  let page = $state<UtterancePage | null>(null)
  let failed = $state(false)
  let input: HTMLInputElement | undefined = $state()

  // "/" ile gelince arama kutusu odakta.
  $effect(() => {
    void app.focusSearch
    input?.focus()
  })

  let timer: ReturnType<typeof setTimeout> | undefined
  function onInput() {
    clearTimeout(timer)
    timer = setTimeout(() => {
      term = q.trim()
      offset = 0
    }, 250)
  }

  async function load() {
    try {
      const [pr, pg] = await Promise.all([
        getProjects(),
        searchUtterances({ q: term, projectId: projectSel === '' ? null : Number(projectSel), limit: LIMIT, offset }),
      ])
      projects = pr
      page = pg
      failed = false
    } catch {
      failed = true
    }
  }

  $effect(() => {
    void app.tick
    void term
    void projectSel
    void offset
    void pf.scanning // tarama bitince liste yenilenir
    load()
  })

  $effect(() => {
    void projectSel
    offset = 0
  })

  const from = $derived(page && page.total > 0 ? offset + 1 : 0)
  const to = $derived(page ? Math.min(offset + LIMIT, page.total) : 0)
</script>

<div class="page">
  <header class="page-head">
    <div>
      <h1 class="h1">{t('searchTitle')}</h1>
      <p class="lead">{t('searchLead')}</p>
    </div>
  </header>

  <form class="bar" role="search" onsubmit={(e) => { e.preventDefault(); clearTimeout(timer); term = q.trim(); offset = 0 }}>
    <div class="field grow">
      <label class="sr-only" for="q">{t('searchLabel')}</label>
      <div class="search">
        <Icon name="ara" size={20} />
        <input id="q" class="input" type="search" placeholder={t('searchPlaceholder')} bind:this={input} bind:value={q} oninput={onInput} autocomplete="off" />
      </div>
    </div>
    <div class="field proj">
      <label class="sr-only" for="sproj">{t('project')}</label>
      <select id="sproj" class="select" bind:value={projectSel}>
        <option value="">{t('everyProject')}</option>
        {#each projects as p (p.id)}
          <option value={String(p.id)}>{projectLabel(p)}</option>
        {/each}
      </select>
    </div>
  </form>

  {#if failed && page === null}
    <p class="muted">{t('loadError')} <button class="btn btn-sm" onclick={load}>{t('retry')}</button></p>
  {:else if page}
    <p class="small muted count" aria-live="polite">{term ? t('searchResults', { n: page.total }) : t('searchRecent')}</p>

    {#if page.items.length === 0 && term === '' && projectSel === ''}
      <!-- Hiç arama yapılmadı ve hiç kayıt yok: "eşleşen yok" denmez, kayıtların henüz okunmadığı söylenir (B-DIGER-20). -->
      <EmptyState id="no-records" text={t('searchNoRecords')} action={pf.scanning ? (pf.progressText || t('searchReading')) : t('searchReadNow')} onaction={() => { if (!pf.scanning) void autoScanInBackground() }} />
    {:else if page.items.length === 0}
      <EmptyState id="no-results" text={t('searchNone')} action={term ? t('emptyClear') : t('emptyGoJobs')} onaction={() => { if (term) { q = ''; term = ''; offset = 0 } else go('is') }} />
    {:else}
      <ul class="results">
        {#each page.items as u (u.id)}
          <li class="res">
            <p class="voice text">
              {#each highlightParts(u.text, term) as part}{#if part.hit}<mark>{part.text}</mark>{:else}{part.text}{/if}{/each}
            </p>
            <p class="small muted meta">
              <time datetime={u.ts}>{fmtDate(u.ts, true)}</time>
              <span class="tool">{toolName(u.tool)}</span>
              <span>{u.projectName}</span>
            </p>
          </li>
        {/each}
      </ul>

      {#if page.total > LIMIT}
        <nav class="pager" aria-label={t('searchResults', { n: page.total })}>
          <button class="btn btn-sm" disabled={offset === 0} onclick={() => (offset = Math.max(0, offset - LIMIT))}>{t('prev')}</button>
          <span class="small muted">{t('pageRange', { a: from, b: to, total: page.total })}</span>
          <button class="btn btn-sm" disabled={offset + LIMIT >= page.total} onclick={() => (offset += LIMIT)}>{t('next')}</button>
        </nav>
      {/if}
    {/if}
  {/if}
</div>

<style>
  .bar { display: flex; flex-wrap: wrap; gap: 12px; margin-bottom: 20px; }
  .grow { flex: 1 1 18rem; }
  .proj { flex: 0 1 16rem; min-width: 0; }
  .search { position: relative; display: flex; align-items: center; }
  .search :global(svg) { position: absolute; left: 14px; color: var(--ink-3); pointer-events: none; }
  .search .input { padding-left: 44px; min-height: var(--row-m); font-size: var(--fs-md); border-radius: 999px; }
  .proj .select { min-height: var(--row-m); border-radius: 999px; padding-left: 16px; }
  .count { margin-bottom: 4px; }

  .results { display: flex; flex-direction: column; border-bottom: 1px solid var(--line); }
  .res { padding: 18px 0; border-top: 1px solid var(--line); }
  .text { max-width: 62ch; }
  .meta { display: flex; flex-wrap: wrap; gap: 2px 14px; margin-top: 6px; }
  .tool { font-weight: var(--fw-strong); color: var(--ink-2); }
  mark {
    background: var(--waiting-tint);
    color: inherit;
    border-radius: 3px;
    padding: 0 2px;
    box-shadow: inset 0 -2px 0 var(--waiting);
  }

  .pager { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 20px 0; }
</style>
