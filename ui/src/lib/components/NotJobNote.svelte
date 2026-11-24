<script lang="ts">
  import { t } from '../i18n/index.svelte'
  import { reopenIntro } from '../intro.svelte'
  import { sf, startDraft } from '../surface.svelte'
  import Icon from './Icon.svelte'

  // Kutuya iş olmayan bir şey yazıldı (selam, soru, tek sözcük): iş açılmadı, sahte iş kaydı oluşmadı.
  // Burada sade bir cevap, iki tıklanır örnek ve "Yine de iş olarak ver" kaçış yolu çıkar.
  let { ta }: { ta?: HTMLTextAreaElement } = $props()

  const nj = $derived(sf.notJob)
  let box: HTMLDivElement | undefined = $state()
  // Cevap çıkınca ekranda görünsün (dar ekranda kutunun altında kalmasın).
  $effect(() => {
    if (nj && box) box.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
  })
  const head = $derived(nj ? t(nj.neden === 'selam' ? 'notJobHello' : nj.neden === 'kimlik' ? 'notJobAbout' : 'notJobDefault') : '')
  const examples = $derived([t('notJobEx1'), t('notJobEx2')])

  function fill(text: string) {
    sf.draft = text
    sf.notJob = null
    ta?.focus()
  }
  function showIntro() {
    reopenIntro()
    sf.notJob = null
    document.getElementById('icerik')?.querySelector('[data-intro]')?.scrollIntoView({ block: 'start' })
  }
</script>

{#if nj}
  <div class="nj" bind:this={box} role="status" data-not-job data-not-job-reason={nj.neden}>
    <p class="hd"><Icon name="info" size={15} /><span><strong>{head}</strong> {t('notJobBody')}</span></p>
    <ul class="ex" aria-label={t('notJobExLabel')}>
      {#each examples as ex (ex)}
        <li><button type="button" class="chipbtn" onclick={() => fill(ex)} data-not-job-example>{ex}</button></li>
      {/each}
    </ul>
    <div class="acts">
      <button type="button" class="btn btn-sm" onclick={() => void startDraft({ force: true })} data-not-job-force>{t('notJobForce')}</button>
      {#if nj.neden === 'kimlik'}<button type="button" class="btn btn-sm btn-quiet" onclick={showIntro} data-not-job-intro>{t('introWhat')}</button>{/if}
    </div>
  </div>
{/if}

<style>
  .nj { display: flex; flex-direction: column; gap: 8px; max-width: 64ch; margin: 8px 4px 0; padding: 10px 12px; border: 1px solid var(--line-strong); border-radius: var(--radius-lg); background: var(--surface); }
  .hd { display: flex; align-items: flex-start; gap: 8px; margin: 0; font-size: var(--fs-sm); line-height: 1.5; color: var(--ink-2); }
  .hd :global(svg) { flex: none; margin-top: 3px; }
  .hd strong { color: var(--ink); font-weight: var(--fw-strong); }
  .ex { display: flex; flex-direction: column; gap: 6px; margin: 0; padding: 0; list-style: none; }
  .chipbtn { display: inline-flex; align-items: center; max-width: 100%; min-height: var(--ctl); padding: 4px 12px; border: 1px solid var(--line-strong); border-radius: var(--radius); background: var(--bg); color: var(--ink); font: inherit; font-size: var(--fs-sm); text-align: left; cursor: pointer; }
  .chipbtn:hover { background: var(--hover); }
  .acts { display: flex; flex-wrap: wrap; gap: 8px; }
</style>
