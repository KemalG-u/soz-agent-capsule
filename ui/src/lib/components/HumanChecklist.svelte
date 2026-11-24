<script lang="ts">
  import { hataMetni } from '../hata'
  import { runProof } from '../api2'
  import { getChecklist, tickChecklist } from '../api5'
  import { t } from '../i18n/index.svelte'
  import { app, toast } from '../store.svelte'
  import { sf } from '../surface.svelte'
  import type { ChecklistState } from '../types5'

  // "Ben şunlara bakıp onaylayacağım": kişinin kendi işaretlediği maddeler. Bu ölçüm değil senin onayındır; ölçülen kanıtla karışmaz.
  let { jobId, projectId }: { jobId: number; projectId: number } = $props()

  const checks = $derived((sf.checks[projectId] ?? []).filter((c) => c.kind === 'human-checklist' && c.enabled && (c.jobId == null || c.jobId === jobId)))
  let lists = $state<Record<number, ChecklistState>>({})
  let error = $state('')
  let busy = $state(false)
  let dirty = $state(false)

  $effect(() => {
    void app.jobTick
    for (const c of checks) {
      getChecklist(c.id, jobId)
        .then((s) => (lists[c.id] = s))
        .catch((e) => (error = hataMetni(e)))
    }
  })

  async function tick(checkId: number, item: number, ticked: boolean) {
    error = ''
    try {
      lists[checkId] = await tickChecklist(jobId, checkId, item, ticked)
      dirty = true
    } catch (e) {
      error = hataMetni(e)
    }
  }

  async function remeasure() {
    if (busy) return
    busy = true
    try {
      await runProof(projectId, jobId)
      dirty = false
      toast(t('chkRemeasure'))
    } catch (e) {
      error = hataMetni(e)
    } finally {
      busy = false
    }
  }
</script>

{#if checks.length > 0}
  <section class="hc" aria-label={t('chkTitle')} data-human-checklist>
    <h3 class="h3">{t('chkTitle')}</h3>
    <p class="small muted">{t('chkLead')}</p>
    {#each checks as c (c.id)}
      {@const s = lists[c.id]}
      {#if s}
        <ul class="its">
          {#each s.items as it (it.index)}
            <li><label><input type="checkbox" checked={it.ticked} onchange={(e) => tick(c.id, it.index, e.currentTarget.checked)} data-tick /> <span>{it.text}</span></label></li>
          {/each}
        </ul>
        <p class="small muted">{s.allTicked ? t('chkAll') : t('chkWaiting', { n: s.waiting })}</p>
      {/if}
    {/each}
    {#if dirty}<button class="btn btn-sm" onclick={remeasure} disabled={busy} data-remeasure>{t('chkRemeasureBtn')}</button>{/if}
    {#if error}<p class="small err-line" role="alert">{error}</p>{/if}
  </section>
{/if}

<style>
  .hc { display: flex; flex-direction: column; gap: 8px; margin-top: 14px; max-width: 40rem; padding: 12px 14px; border: 1px solid var(--line-strong); border-radius: var(--radius-lg); background: var(--surface); }
  .its { display: flex; flex-direction: column; gap: 6px; }
  .its label { display: flex; gap: 10px; align-items: flex-start; cursor: pointer; min-height: var(--ctl); }
  .its input { flex: none; width: 24px; height: 24px; margin-top: 0; accent-color: var(--ink); }
</style>
