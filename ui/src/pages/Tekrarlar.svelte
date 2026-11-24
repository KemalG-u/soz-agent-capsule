<script lang="ts">
  import { hataMetni } from '../lib/hata'
  let { embedded = false }: { embedded?: boolean } = $props()
  import { getProjects, getRepeats, promoteRepeat } from '../lib/api'
  import EmptyState from '../lib/components/EmptyState.svelte'
  import Icon from '../lib/components/Icon.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import { app, refresh, toast } from '../lib/store.svelte'
  import { go } from '../lib/router.svelte'
  import type { DecisionType, Project, RepeatCluster } from '../lib/types'
  import { fmtDate, projectLabel, toolName } from '../lib/util'

  let projects = $state<Project[]>([])
  let projectSel = $state('')
  let clusters = $state<RepeatCluster[] | null>(null)
  let failed = $state(false)
  let open = $state<Record<number, boolean>>({})
  let types = $state<Record<number, DecisionType>>({})
  let busy = $state<number | null>(null)

  async function load() {
    try {
      const [pr, cl] = await Promise.all([getProjects(), getRepeats(projectSel === '' ? null : Number(projectSel))])
      projects = pr
      clusters = cl
      failed = false
    } catch {
      failed = true
    }
  }

  $effect(() => {
    void app.tick
    void projectSel
    load()
  })

  async function promote(c: RepeatCluster) {
    if (busy !== null) return // çift tık: istek sürerken ikinci gönderim yok
    busy = c.clusterId
    try {
      await promoteRepeat(c.clusterId, types[c.clusterId] ?? 'rule')
      toast(t('promoted'))
      await refresh()
    } catch (e) {
      toast(t('errGeneric', { m: hataMetni(e) }))
    } finally {
      busy = null
    }
  }
</script>

<div class="page" class:embedded>
  <header class="page-head">
    <div>
      {#if embedded}<h2 class="h2">{t('repeatsTitle')}</h2>{:else}<h1 class="h1">{t('repeatsTitle')}</h1>{/if}
      <p class="lead">{t('repeatsLead')}</p>
    </div>
    <div class="field tools">
      <label class="label" for="rproj">{t('project')}</label>
      <select id="rproj" class="select" bind:value={projectSel}>
        <option value="">{t('everyProject')}</option>
        {#each projects as p (p.id)}
          <option value={String(p.id)}>{projectLabel(p)}</option>
        {/each}
      </select>
    </div>
  </header>

  {#if failed && clusters === null}
    <p class="muted">{t('loadError')} <button class="btn btn-sm" onclick={load}>{t('retry')}</button></p>
  {:else if clusters !== null && clusters.length === 0}
    <EmptyState id="no-repeats" text={t('repeatsNone')} action={t('emptyGoJobs')} onaction={() => go('is')} />
  {:else if clusters}
    <ul class="list">
      {#each clusters as c (c.clusterId)}
        <li class="cl">
          <div class="num" aria-hidden="true">
            <span class="big">{c.count}</span>
            <span class="x">×</span>
          </div>
          <div class="main">
            <p class="voice canon">{c.canonical}</p>
            <p class="small count-line">
              <strong>{t('repeatCountLine', { n: c.count, m: c.sessionCount })}</strong>
              <span class="muted">{c.projectName}</span>
            </p>
            <p class="small muted">{t('firstLast', { a: fmtDate(c.firstTs), b: fmtDate(c.lastTs) })}</p>

            <details class="members" bind:open={open[c.clusterId]}>
              <summary>
                <span class="chev"><Icon name="chevron" size={16} /></span>
                {t('openMembers', { n: c.members.length })}
              </summary>
              <ul class="mlist">
                {#each c.members as m (m.utteranceId)}
                  <li>
                    <p class="voice mtext">{m.text}</p>
                    <p class="small muted">{fmtDate(m.ts)}, {toolName(m.tool)}</p>
                  </li>
                {/each}
              </ul>
            </details>
          </div>

          <div class="act">
            {#if c.decisionId !== null}
              <button class="btn btn-quiet" onclick={() => go('defter')}>
                <span class="chip chip-ok"><Icon name="check" size={15} />{t('inLedger')}</span>
              </button>
            {:else}
              <div class="field">
                <label class="sr-only" for={`pt-${c.clusterId}`}>{t('promoteType')}</label>
                <select id={`pt-${c.clusterId}`} class="select small-select" bind:value={types[c.clusterId]}>
                  <option value="rule" selected>{t('typeRule')}</option>
                  <option value="ban">{t('typeBan')}</option>
                  <option value="decision">{t('typeDecision')}</option>
                  <option value="preference">{t('typePreference')}</option>
                </select>
              </div>
              <button class="btn btn-primary" disabled={busy === c.clusterId} onclick={() => promote(c)}>{t('promote')}</button>
            {/if}
          </div>
        </li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  .tools { min-width: min(18rem, 100%); }
  .list { display: flex; flex-direction: column; border-bottom: 1px solid var(--line); }
  .cl {
    display: grid;
    grid-template-columns: 5.5rem minmax(0, 1fr) auto;
    gap: 8px 20px;
    padding: 24px 0;
    border-top: 1px solid var(--line);
    align-items: start;
  }
  .num { display: flex; align-items: baseline; gap: 2px; color: var(--ink); line-height: 1; }
  .big {
    font-family: var(--sans);
    font-weight: var(--fw-strong);
    font-size: var(--fs-xl);
    letter-spacing: -0.04em;
    font-variant-numeric: tabular-nums;
  }
  .x { font-size: var(--fs-lg); color: var(--ink-3); }
  .canon { font-size: var(--fs-md); line-height: 1.4; max-width: 44ch; }
  .count-line { display: flex; flex-wrap: wrap; gap: 4px 14px; margin-top: 8px; }
  .main > p.muted { margin-top: 2px; }
  .act { display: flex; flex-direction: column; align-items: stretch; gap: 8px; min-width: 9rem; }
  .small-select { min-height: 36px; padding-block: 4px; font-size: var(--fs-sm); border-radius: 999px; }

  .members { margin-top: 12px; }
  .members summary {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    cursor: pointer;
    font-size: var(--fs-sm);
    font-weight: var(--fw-medium);
    color: var(--ink-2);
    list-style: none;
    padding: 6px 0;
  }
  .members summary::-webkit-details-marker { display: none; }
  .members summary:hover { color: var(--ink); }
  .chev { display: inline-flex; transition: transform 0.12s var(--ease); }
  .members[open] .chev { transform: rotate(180deg); }
  .mlist { display: flex; flex-direction: column; gap: 14px; margin-top: 8px; padding-left: 16px; border-left: 3px solid var(--line); }
  .mtext { font-size: var(--fs-md); }


  @media (max-width: 700px) {
    .cl { grid-template-columns: 4rem minmax(0, 1fr); }
    .big { font-size: var(--fs-xl); }
    .act { grid-column: 1 / -1; flex-direction: row; align-items: center; flex-wrap: wrap; }
    .act .field { flex: 0 0 8rem; }
  }
</style>
