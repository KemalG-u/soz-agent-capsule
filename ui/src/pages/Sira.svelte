<script lang="ts">
  import { onMount } from 'svelte'
  import AttentionRow from '../lib/components/AttentionRow.svelte'
  import Icon from '../lib/components/Icon.svelte'
  import LostReplies from '../lib/components/LostReplies.svelte'
  import Skeleton from '../lib/components/Skeleton.svelte'
  import { at, loadAttention } from '../lib/attention.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import { waitingBreakdown, outsideWaitingCount } from '../lib/profileStore.svelte'
  import { go } from '../lib/router.svelte'
  import { app } from '../lib/store.svelte'
  import { sf } from '../lib/surface.svelte'

  // "Sıra bende": kişinin kararını bekleyenler tek listede. Sıra SUNUCUNUN verdiği priority sırasıdır (arayüz kendi sırasını kurmaz).
  // "Emin değilim" ayrı ve sessiz bir bölümdedir: kimseyi uyandırmaz.
  onMount(() => void loadAttention())
  $effect(() => {
    void app.tick
    void loadAttention()
  })

  const snap = $derived(at.snap)
  // "Sırayı sana verdi" kartı sunucuda 'emin değilim' türünde gelir ama sayıda ve Şu An'da kart olarak sayılır: listede de ANA bölümde durur (K-09, sayı = satır).
  const isTurn = (i: { reason?: string }) => i.reason === 'session_turn_end'
  const main = $derived((snap?.items ?? []).filter((i) => i.kind !== 'uncertain' || isTurn(i)))
  const quiet = $derived((snap?.items ?? []).filter((i) => i.kind === 'uncertain' && !isTurn(i)))
  const outside = $derived(outsideWaitingCount())
  // Özet cümlesi türleriyle yazılır ve toplamı Şu An başlığı, alt şerit rozeti ve İşler çipiyle aynıdır (C4): "Seni bekleyenler 6: 1 izin, 1 soru, 1 sırayı sana verdi, 3 bitti".
  const sentence = $derived.by(() => {
    if (!snap) return ''
    const c = { perm: 0, ask: 0, accept: 0, blocked: 0, turn: 0, done: 0 }
    for (const i of snap.items) {
      const r = i.reason ?? ''
      if (r.startsWith('session_')) {
        if (r === 'session_turn_end') c.turn++
        else if (r === 'session_done') c.done++
        else if (r === 'session_permission') c.perm++
        else if (r === 'session_failed') c.blocked++
        else if (i.kind !== 'uncertain') c.ask++
      } else if (i.kind === 'ask') (r === 'approval' ? c.perm++ : c.ask++)
      else if (i.kind === 'accept') c.accept++
      else if (i.kind === 'blocked') c.blocked++
    }
    const out: string[] = []
    if (c.perm) out.push(t('cnPerm', { n: c.perm }))
    if (c.ask) out.push(t('cnAsk', { n: c.ask }))
    if (c.accept) out.push(t('cnAccept', { n: c.accept }))
    if (c.blocked) out.push(t('cnBlocked', { n: c.blocked }))
    const outside = outsideWaitingCount()
    if (outside) out.push(t('cnOutside', { n: outside }))
    if (c.turn) out.push(t('cnTurn', { n: c.turn }))
    if (c.done) out.push(t('cnDone', { n: c.done }))
    return out.length ? t('cnLead', { n: waitingBreakdown().total, s: out.join(', ') }) : snap.sentence
  })
</script>

<div class="page" data-sira>
  <header class="page-head">
    <div>
      <h1 class="h1">{t('attTitle')}</h1>
      {#if snap}<p class="lead" data-attention-sentence>{sentence}</p>{/if}
    </div>
  </header>

  {#if !at.loaded}
    <Skeleton rows={3} />
  {:else if at.error && !snap}
    <div role="alert" data-attention-load-error>
      <p class="err-line">{t('dataError')}</p>
      <p class="retry-row"><button class="btn" onclick={() => loadAttention()}>{t('retry')}</button></p>
    </div>
  {:else if snap}
    {#if at.error}<p class="small err-line" role="alert">{t('dataError')}</p>{/if}

    {#if main.length === 0 && outside === 0}
      <div class="empty-state" data-empty="attention">
        <p>{t('attEmpty')}</p>
        <button class="btn btn-sm" onclick={() => { go('is'); sf.focusNew++ }}>{t('streamNoneBtn')} <kbd>n</kbd></button>
      </div>
    {:else}
      {#if main.length > 0}
        <ol class="list" aria-label={t('attTitle')} data-attention-list>
          {#each main as it (it.id)}<AttentionRow item={it} />{/each}
        </ol>
      {/if}
      {#if outside > 0}
        <p class="small outside" data-sira-outside>{t('siraOutsideNote', { n: outside })} <a href="#/suan" data-sira-outside-link>{t('siraOutsideLink')}</a></p>
      {/if}
    {/if}
    <LostReplies />

    {#if (snap.running?.length ?? 0) > 0}
      <p class="small muted run"><span class="spin" aria-hidden="true"></span>{t('attRunning', { n: snap.running?.length ?? 0 })} <a href="#/is">{t('attSeeJobs')}</a></p>
    {/if}

    {#if quiet.length > 0}
      <details class="quiet" data-uncertain>
        <summary class="h2s"><span class="h3">{t('attUncertainTitle')}</span><span class="small muted n">{quiet.length}</span></summary>
        <p class="small muted">{t('attUncertainLead')}</p>
        <ol class="list">{#each quiet as it (it.id)}<AttentionRow item={it} quiet />{/each}</ol>
      </details>
    {/if}

    <p class="small muted budget" data-attention-budget><Icon name="info" size={14} />{t('attBudget', { used: snap.budget.usedToday, limit: snap.budget.dailyLimit })}{#if snap.budget.suppressedToday > 0}{t('attBudgetQuiet', { n: snap.budget.suppressedToday })}{/if}{#if snap.budget.dailyLimit > 0 && snap.budget.usedToday >= snap.budget.dailyLimit}{t('attBudgetFull')}{/if}</p>
  {/if}
</div>

<style>
  .list { display: flex; flex-direction: column; border-bottom: 1px solid var(--line); }
  .run { display: flex; align-items: center; gap: 8px; margin-top: 16px; }
  .run a { display: inline-flex; align-items: center; min-height: 24px; }
  .quiet { margin-top: 24px; }
  .quiet > summary { cursor: pointer; min-height: var(--row-s); display: flex; align-items: center; gap: 10px; }
  .quiet .n { padding: 0 8px; border-radius: var(--radius-sm); background: var(--bg-sunken); cursor: default; }
  .outside { margin: 12px 0 0; color: var(--ink-2); }
  .budget { display: flex; align-items: center; gap: 8px; margin-top: 28px; }
</style>
