<script lang="ts">
  import { t } from '../i18n/index.svelte'
  import { at } from '../attention.svelte'
  import { waitingBreakdown } from '../profileStore.svelte'
  import { rowIn, rowOut } from '../motion'
  import { agentChoiceVisible, agentName, bucketMeta, buckets, flatJobs, jobsIn, loadMoreJobs, midRunFailed, refreshSurface, rowLine, select, sf, type Bucket } from '../surface.svelte'
  import type { Key } from '../i18n/index.svelte'
  import { ago } from '../util'
  import { bekleyenSayaci } from '../gezinme'
  import Icon from './Icon.svelte'

  let listEl: HTMLElement | undefined = $state()

  // Liste odaklanma isteği (Esc ile geri dönüş).
  $effect(() => {
    if (sf.focusList > 0) listEl?.focus()
  })

  // Seçili satır görünür kalsın (j/k ile gezerken).
  $effect(() => {
    const id = sf.selId
    if (id === null) return
    queueMicrotask(() => document.getElementById(`jr-${id}`)?.scrollIntoView({ block: 'nearest' }))
  })

  const total = $derived(flatJobs().length)

  // "Başlatılamadı" ile "Yarıda kaldı" ayrı gruplardır: çalışırken hata veren iş hiç başlamamış gibi anlatılmaz.
  type Grup = { id: string; b: Bucket; key: Key; rows: ReturnType<typeof jobsIn> }
  const groups = $derived<Grup[]>(
    buckets.flatMap((b): Grup[] => {
      const rows = jobsIn(b)
      if (b !== 'blocked') return [{ id: b as string, b, key: bucketMeta[b].key as Key, rows }]
      return [
        { id: 'blocked', b, key: bucketMeta[b].key as Key, rows: rows.filter((j) => !midRunFailed(j)) },
        { id: 'half', b, key: 'grpHalf' as Key, rows: rows.filter((j) => midRunFailed(j)) },
      ]
    }),
  )

  // Sayı ile altındaki liste AYNI kaynaktan gelir (K-11): çip bu listedeki "Seni bekleyenler" grubunun satır sayısını söyler. Şu An'da ayrıca bekleyen başka kartlar varsa
  // (Olmadı ya da başlatılamayan işler, terminaldeki yardımcılar) bunlar gizlenmez: "N tane daha Şu An sayfasında" diye açıkça söylenir ve oraya götürür.
  const counters = $derived(
    sf.counts
      ? ([
          { id: 'me' as Bucket, n: jobsIn('me').length, key: 'grpMe' as const },
          { id: 'running' as Bucket, n: sf.counts.running, key: 'grpRunning' as const },
          { id: 'queued' as Bucket, n: sf.counts.queued, key: 'grpQueued' as const },
        ].filter((c) => c.n !== null && (c.n > 0 || c.id !== 'queued')))
      : [],
  )
  // D8: "Seni bekleyenler" sayısı alt şerit ve Şu An ile aynıdır (gezinme.ts bekleyenSayaci). Listede görünmeyenler "N tane daha Şu An sayfasında" der.
  const bekleyen = $derived(bekleyenSayaci({ bekleyenYuklendi: at.loaded && !!at.snap, bekleyenToplam: waitingBreakdown().total, listedeki: jobsIn('me').length, dahaVar: sf.jobsMore }))
  const moreWaiting = $derived(bekleyen.fazla)
  function jump(b: Bucket) {
    const first = jobsIn(b)[0]
    if (first) select(first.id, { oto: true })
    listEl?.focus()
  }
</script>

{#if counters.length > 0}
  <div class="counters" role="group" aria-label={t('countersLabel')} data-counters>
    {#each counters as c (c.id)}
      <button class="cn" class:zero={c.n === 0} onclick={() => jump(c.id)} data-counter={c.id} disabled={jobsIn(c.id).length === 0}><b>{c.id === 'me' ? bekleyen.metin : c.n}</b><span>{t(c.key)}</span></button>
    {/each}
    {#if moreWaiting > 0}<a class="cn-more small" href="#/suan" data-counter-more>{t('listWaitMore', { n: moreWaiting })}</a>{/if}
  </div>
{/if}

<div class="list" bind:this={listEl} role="listbox" tabindex="0" aria-label={t('listLabel')} aria-activedescendant={sf.selId !== null ? `jr-${sf.selId}` : undefined} id="job-list" data-joblist>
  {#if sf.jobsFailed}
    <div class="jerr" role="alert" data-jobs-error>
      <Icon name="alert" size={16} /><span class="jerr-t">{t('dataError')}</span>
      <button type="button" class="btn btn-sm" onclick={() => refreshSurface()} data-jobs-retry>{t('retry')}</button>
    </div>
  {/if}
  {#each groups as g (g.id)}
    {@const rows = g.rows}
    {@const b = g.b}
    {#if rows.length > 0}
      <div class="grp" role="group" aria-labelledby="g-{g.id}" data-group={g.id}>
        <h3 class="gh" id="g-{g.id}"><span class="gn">{t(g.key)}</span></h3>
        {#each rows as j (j.id)}
          <div
            class="row b-{b}"
            class:sel={sf.selId === j.id}
            id="jr-{j.id}"
            role="option"
            aria-selected={sf.selId === j.id}
            tabindex="-1"
            data-job={j.id}
            onclick={() => { select(j.id, { detail: true }); if (j.awaiting === 'reply') sf.focusReply++ }}
            onkeydown={() => {}}
            onmousedown={(e) => { if (e.detail > 1) e.preventDefault() }}
            in:rowIn
            out:rowOut
          >
            <span class="st" aria-hidden="true">
              {#if b === 'running'}<i class="spin"></i>
              {:else}<Icon name={bucketMeta[b].icon} size={16} strokeWidth={b === 'passed' || b === 'failed' ? 2.2 : 1.7} />{/if}
            </span>
            <span class="tx">
              <span class="ti">{j.title}</span>
              <span class="mt"><span class="ln">{rowLine(j)}</span><span class="pj">{j.projectName}</span>{#if agentChoiceVisible() || (j.agent ?? 'claude') !== 'claude'}<span class="ag" title={t('agentMark', { a: agentName(j.agent) })} data-agent-mark={j.agent ?? 'claude'}>{agentName(j.agent)}</span>{/if}<time datetime={j.createdAt}>{ago(j.createdAt)}</time></span>
            </span>
          </div>
        {/each}
      </div>
    {/if}
  {/each}

  {#if sf.jobsMore}
    <div class="more" data-jobs-more>
      <p class="small muted">{sf.jobsLimit >= 500 ? t('listMaxed', { n: sf.jobsLimit }) : t('listMoreInfo', { n: sf.jobsLimit, total: sf.jobsTotal })}</p>
      {#if sf.jobsLimit < 500}<button type="button" class="btn btn-sm" onclick={() => loadMoreJobs()} data-jobs-more-btn>{t('listMore')}</button>{/if}
    </div>
  {/if}

  {#if sf.loaded && total === 0 && !sf.jobsFailed}
    <div class="none">
      <p class="muted">{t('listEmpty')}</p>
      <button class="btn" onclick={() => sf.focusNew++}>{t('listEmptyBtn')} <kbd>n</kbd></button>
    </div>
  {/if}
</div>

<style>
  .jerr { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; margin: 4px 0 8px; padding: 10px 12px; border: 1px solid var(--waiting); border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); font-size: var(--fs-sm); }
  .jerr :global(svg) { flex: none; color: var(--waiting); }
  .jerr-t { flex: 1 1 10rem; min-width: 0; }
  .counters { flex: none; display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: 8px 8px 0; }
  .cn-more { color: var(--ink-2); text-decoration: underline; text-underline-offset: 2px; padding: 2px 4px; }
  .cn { display: inline-flex; align-items: center; gap: 6px; min-height: var(--ctl); padding: 0 10px; border: 1px solid var(--line); border-radius: var(--radius); background: var(--surface); font-size: var(--fs-xs); color: var(--ink-2); transition: background-color var(--dur-1) var(--ease); }
  .cn b { font-size: var(--fs-sm); font-weight: var(--fw-strong); color: var(--ink); font-variant-numeric: tabular-nums; }
  .cn:hover:not([disabled]) { background: var(--hover); }
  .cn[disabled] { cursor: default; }
  .cn.zero b { color: var(--ink-3); }
  .list { flex: 1; min-height: 0; overflow-y: auto; padding: 6px 8px 12px; outline: none; }
  .list:focus-visible { outline: 2px solid var(--focus); outline-offset: -2px; border-radius: 0; }
  .grp { margin-top: 8px; }
  .gh { display: flex; align-items: center; gap: 8px; padding: 6px 10px 4px; font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-2); }
  .row { --c: var(--idle); position: relative; display: flex; align-items: flex-start; gap: 8px; min-height: 44px; padding: 6px 8px; border-radius: var(--radius); cursor: pointer; }
  .row:hover { background: var(--hover); }
  .row.sel { background: var(--sel-bg, var(--select)); color: var(--sel-fg, var(--ink)); }
  .row.sel .ln, .row.sel .mt, .row.sel .ag { color: var(--sel-fg2, var(--ink-2)); }
  .row.sel::before { content: ''; position: absolute; left: 0; top: 10px; bottom: 10px; width: 2px; border-radius: 2px; background: currentColor; }
  .b-me { --c: var(--waiting); }
  .b-blocked, .b-queued { --c: var(--ink-2); }
  .b-accepted { --c: var(--ink); }
  .b-running { --c: var(--working); }
  .b-passed { --c: var(--ok); }
  .b-failed { --c: var(--stalled); }
  .b-stopped { --c: var(--ink-3); }
  .b-unproven { --c: var(--waiting); }
  .st { flex: none; display: inline-flex; align-items: center; justify-content: center; width: 20px; height: 20px; margin-top: 1px; color: var(--c); }
  .st .spin { width: 14px; height: 14px; }
  .tx { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1; }
  .ti { font-size: var(--fs-sm); font-weight: var(--fw-medium); line-height: 1.35; display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
  .mt { display: flex; align-items: baseline; gap: 6px; font-size: var(--fs-xs); color: var(--ink-3); min-width: 0; white-space: nowrap; user-select: none; }
  .ln { color: var(--ink-2); overflow: hidden; text-overflow: ellipsis; min-width: 0; flex: 0 1 auto; }
  .pj { overflow: hidden; text-overflow: ellipsis; flex: 0 100 auto; min-width: 0; }
  .pj::before { content: '·'; margin-right: 6px; }
  .ag { flex: none; padding: 0 6px; border-radius: var(--radius-sm); background: var(--bg-sunken); cursor: default; font-size: var(--fs-xs); line-height: 1.5; color: var(--ink-2); }
  time { margin-left: auto; flex: none; font-variant-numeric: tabular-nums; }
  .more { display: flex; flex-direction: column; align-items: flex-start; gap: 8px; padding: 14px 12px 6px; }
  .none { display: flex; flex-direction: column; align-items: flex-start; gap: 10px; padding: 20px 12px; }
  @media (max-width: 759px) {
    .counters { padding: 8px 12px 0; }
    .row { padding: 6px 12px; }
    .gh { padding-top: 8px; }
  }
  @media (pointer: coarse) and (hover: none) {
    .row { min-height: 64px; padding: 10px 12px; }
    .ti { font-size: var(--fs-md); }
    .gh { padding-top: 10px; }
  }
</style>
