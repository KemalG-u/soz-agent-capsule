<script lang="ts">
  import { onMount } from 'svelte'
  import { t, type Key } from '../i18n/index.svelte'

  let { onclose }: { onclose: () => void } = $props()
  let dlg: HTMLDialogElement | undefined = $state()

  onMount(() => {
    dlg?.showModal()
  })

  const rows: { keys: string[]; text: Key }[] = [
    { keys: ['Ctrl', 'K'], text: 'kbPalette' },
    { keys: ['n'], text: 'kbNew' },
    { keys: ['Ctrl', 'Enter'], text: 'kbGive' },
    { keys: ['j', 'k'], text: 'kbMove' },
    { keys: ['Enter'], text: 'kbOpen' },
    { keys: ['r'], text: 'kbReply' },
    { keys: ['s'], text: 'kbStop' },
    { keys: ['e'], text: 'kbYes' },
    { keys: ['h'], text: 'kbNo' },
    { keys: ['f'], text: 'kbFolder' },
    { keys: ['/'], text: 'kbFind' },
    { keys: ['Esc'], text: 'kbBack' },
    { keys: ['?'], text: 'kbHelp' },
  ]
</script>

<dialog class="sheet" bind:this={dlg} aria-labelledby="kb-t" onclose={onclose} onclick={(e) => { if (e.target === dlg) dlg?.close() }} data-help>
  <div class="in">
    <div class="hd">
      <h2 class="h2" id="kb-t">{t('shortcuts')}</h2>
      <button class="btn btn-quiet btn-sm" onclick={() => dlg?.close()} data-close>{t('close')} <kbd>Esc</kbd></button>
    </div>
    <dl class="kl">
      {#each rows as r (r.text)}
        <div class="kr">
          <dt>{#each r.keys as k, i (i)}<kbd>{k}</kbd>{' '}{/each}</dt>
          <dd>{t(r.text)}</dd>
        </div>
      {/each}
    </dl>
    <p class="small muted">{t('kbdNote')}</p>
  </div>
</dialog>

<style>
  .in { padding: 16px 20px 18px; display: flex; flex-direction: column; gap: 10px; }
  .hd { display: flex; justify-content: space-between; align-items: center; gap: 12px; }
  .kl { display: flex; flex-direction: column; }
  .kr { display: grid; grid-template-columns: 8.5rem minmax(0, 1fr); gap: 12px; align-items: center; min-height: 34px; border-top: 1px solid var(--line); }
  .kr dt { display: flex; gap: 4px; flex-wrap: wrap; }
  .kr dd { margin: 0; }
</style>
