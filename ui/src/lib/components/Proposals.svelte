<script lang="ts">
  import { hataMetni } from '../hata'
  import { patchDecision } from '../api'
  import { t } from '../i18n/index.svelte'
  import { refresh, toast } from '../store.svelte'
  import type { Decision } from '../types'
  import { fmtDate, toolName, typeLabel } from '../util'
  import Icon from './Icon.svelte'

  let { items }: { items: Decision[] } = $props()

  let editing = $state<number | null>(null)
  let draft = $state('')
  let busy = $state<number | null>(null)

  async function act(d: Decision, status: 'approved' | 'rejected', text?: string) {
    busy = d.id
    try {
      await patchDecision(d.id, text !== undefined ? { status, text } : { status })
      toast(status === 'approved' ? t('approveSaved') : t('rejectDone'))
      editing = null
      await refresh()
    } catch (e) {
      toast(t('errGeneric', { m: hataMetni(e) }))
    } finally {
      busy = null
    }
  }

  function startEdit(d: Decision) {
    editing = d.id
    draft = d.text
  }
</script>

<ul class="list">
  {#each items as d (d.id)}
    <li class="item">
      <div class="head">
        <span class="chip chip-neutral">{typeLabel(d.type)}</span>
        <span class="small muted">{d.projectName}</span>
      </div>

      {#if editing === d.id}
        <div class="field">
          <label class="sr-only" for={`prop-${d.id}`}>{t('proposalText')}</label>
          <textarea id={`prop-${d.id}`} class="textarea" bind:value={draft} rows="2"></textarea>
        </div>
        <div class="actions">
          <button class="btn btn-primary btn-sm" disabled={busy === d.id || !draft.trim()} onclick={() => act(d, 'approved', draft.trim())}>
            <Icon name="check" size={16} />{t('saveAndApprove')}
          </button>
          <button class="btn btn-quiet btn-sm" onclick={() => (editing = null)}>{t('cancel')}</button>
        </div>
      {:else}
        <p class="voice text">{d.text}</p>
        <p class="small muted said">
          {#if d.repeatCount > 1}
            {t('saidTimes', { n: d.repeatCount })}{#if d.sessionCount > 1}, {t('saidInSessions', { m: d.sessionCount })}{/if}
          {:else}
            {t('saidOnce')}
          {/if}
          {#if d.origin}
            <span class="origin">({toolName(d.origin.tool)}, {fmtDate(d.origin.ts)})</span>
          {/if}
        </p>
        <div class="actions">
          <button class="btn btn-primary btn-sm" disabled={busy === d.id} onclick={() => act(d, 'approved')}>
            <Icon name="check" size={16} />{t('approve')}
          </button>
          <button class="btn btn-sm" onclick={() => startEdit(d)}>
            <Icon name="edit" size={16} />{t('edit')}
          </button>
          <button class="btn btn-quiet btn-sm" disabled={busy === d.id} onclick={() => act(d, 'rejected')}>
            <Icon name="x" size={16} />{t('reject')}
          </button>
        </div>
      {/if}
    </li>
  {/each}
</ul>

<style>
  .list { display: flex; flex-direction: column; }
  .item {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 18px 0;
    border-top: 1px solid var(--line);
  }
  .head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .text { max-width: 60ch; font-size: var(--fs-md); }
  .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 4px; }
  .origin { white-space: nowrap; }
</style>
