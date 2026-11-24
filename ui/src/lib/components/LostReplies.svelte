<script lang="ts">
  import { dropLost, lost } from '../attention.svelte'
  import { t } from '../i18n/index.svelte'
  import { toast } from '../store.svelte'
  import { copyText } from '../util'
  import Icon from './Icon.svelte'

  // Cevap yazarken kart başka yerden kalktıysa (soru başka yerde cevaplandı) yazılan metin SESSİZCE kaybolmaz:
  // kart "Bu soru artık beklemiyor" notuyla yerinde durur, metin kopyalanabilir, kişi isterse kapatır (K-07).
  async function copy(text: string) {
    toast((await copyText(text)) ? t('copied') : t('copyFailed'))
  }
</script>

{#if lost.list.length > 0}
  <ul class="wc-list lost" data-lost-list aria-label={t('waitNoLongerWaiting')}>
    {#each lost.list as l (l.id)}
      <li class="wc k-uncertain" data-lost-reply data-lost-for={l.id}>
        <div class="wc-top">
          <span class="chip chip-neutral"><Icon name="info" size={14} strokeWidth={2} />{l.gone ? t('waitNoLongerWaiting') : t('lostReplyMovedChip')}</span>
          {#if l.projectName}<span class="wc-proj">{l.projectName}</span>{/if}
        </div>
        <p class="wc-sent">{l.sentence}</p>
        <p class="small muted" role="status">{l.gone ? t('lostReplyNote') : t('lostReplyMoved')}</p>
        <label class="sr-only" for="lost-{l.id}">{t('lostReplyLabel')}</label>
        <textarea id="lost-{l.id}" class="textarea" rows="2" bind:value={l.text} data-lost-text></textarea>
        <div class="wc-acts">
          <button type="button" class="btn btn-sm btn-primary" onclick={() => copy(l.text)} data-lost-copy><Icon name="copy" size={14} />{t('copy')}</button>
          <button type="button" class="btn btn-sm" onclick={() => dropLost(l.id)} data-lost-close>{t('lostReplyClose')}</button>
        </div>
      </li>
    {/each}
  </ul>
{/if}

<style>
  .lost { margin-top: 0; }
  .lost textarea { width: 100%; max-width: 44rem; resize: vertical; }
</style>
