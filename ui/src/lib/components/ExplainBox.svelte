<script lang="ts">
  import { explainSession } from '../api'
  import { t } from '../i18n/index.svelte'
  import { go } from '../router.svelte'
  import { app } from '../store.svelte'
  import { hata, type Hata } from '../hata'
  import HataSatiri from './HataSatiri.svelte'
  import Icon from './Icon.svelte'

  // "Sade anlat": ajanın son sözünü kişinin kendi Claude aboneliğiyle sade Türkçeye çevirir. Ayarlardan açılır (varsayılan kapalı).
  let { sessionId }: { sessionId: number } = $props()

  let text = $state('')
  let err = $state<Hata | null>(null)
  let busy = $state(false)
  let shown = $state(false)

  async function run() {
    if (busy) return
    busy = true
    err = null
    shown = true
    try {
      text = (await explainSession(sessionId)).text
    } catch (e) {
      text = ''
      err = hata(e)
      if (err.kind === 'sunucu') err = { ...err, text: t('explainFailed') } // sade anlat kendi sözüyle söylesin
    } finally {
      busy = false
    }
  }
  const off = $derived(!!err && /kapalı/i.test(err.text))
</script>

{#if app.settings?.explainEnabled}
<div class="ex" data-explain>
  <button type="button" class="btn btn-quiet btn-sm" onclick={run} disabled={busy} aria-expanded={shown} data-explain-btn><Icon name="explain" size={14} />{busy ? t('explaining') : t('explain')}</button>
  <span class="cost small muted" data-explain-cost>{t('explainCost')}</span>
  {#if shown && (text || err || busy)}
    <div class="out" role="status" aria-live="polite">
      {#if busy}<span class="small muted">{t('explaining')}</span>
      {:else if text}<p data-explain-text>{text}</p>
      {:else}
        <HataSatiri h={err} data-explain-error />
        {#if off}<button type="button" class="btn btn-sm" onclick={() => go('ayarlar')}>{t('explainOpenSettings')}</button>{/if}
      {/if}
    </div>
  {/if}
</div>
{/if}

<style>
  .ex { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; }
  .cost { line-height: 1.3; }
  .out { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; padding: 8px 12px; border-radius: var(--radius); background: var(--bg-sunken); max-width: 60ch; line-height: 1.5; }
</style>
