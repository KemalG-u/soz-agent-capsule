<script lang="ts">
  import { closeIntro, intro, reopenIntro } from '../intro.svelte'
  import { t } from '../i18n/index.svelte'
  import { sf } from '../surface.svelte'
  import Icon, { type IconName } from './Icon.svelte'

  // Her zaman sayfanın en üstünde: ürünün adı, tek cümlelik tanım, üç adım ve bir gerçek örnek.
  // "Anladım" kartı küçültür, "Söz nedir?" bağlantısı kalır ve kartı geri getirir. Çok adımlı tur ya da sihirbaz yok.
  const steps: { icon: IconName; lbl: 'intro1Lbl' | 'intro2Lbl' | 'intro3Lbl'; txt: 'intro1' | 'intro2' | 'intro3' }[] = [
    { icon: 'send', lbl: 'intro1Lbl', txt: 'intro1' },
    { icon: 'terminal', lbl: 'intro2Lbl', txt: 'intro2' },
    { icon: 'check', lbl: 'intro3Lbl', txt: 'intro3' },
  ]

  // kucuk: seni bekleyen varken (O8) kart yer kaplamaz, tek satır kalır. 'Söz nedir?' yine açar.
  let { kucuk = false }: { kucuk?: boolean } = $props()
  let zorla = $state(false)
  const mini = $derived(intro.closed || (kucuk && !zorla))
  function ac() { zorla = true; reopenIntro() }

  // Kutuda yazılmış bir şey varsa sormadan silinmez (K-08): önce sorulur.
  let ask = $state(false)
  function put() {
    sf.draft = t('introExampleJob')
    ask = false
    sf.focusNew++
  }
  function tryExample() {
    if (sf.draft.trim() && sf.draft.trim() !== t('introExampleJob')) { ask = true; return }
    put()
  }
</script>

{#if mini}
  <div class="mini" data-intro-mini>
    <span class="brand" aria-hidden="true">Söz</span>
    <button type="button" class="link" onclick={ac} data-intro-open>{t('introWhat')}</button>
  </div>
{:else}
  <section class="intro" data-intro aria-labelledby="intro-t">
    <div class="top">
      <h2 class="ttl" id="intro-t"><span class="brand">Söz</span><span class="sent">{t('introTitle')}</span></h2>
      <button type="button" class="btn btn-sm cls" onclick={closeIntro} aria-label={t('introCloseAria')} data-intro-close>{t('introClose')}</button>
    </div>
    <ol class="list">
      {#each steps as it, i (it.lbl)}
        <li class="it"><span class="no" aria-hidden="true">{i + 1}</span><span class="ic" aria-hidden="true"><Icon name={it.icon} size={15} /></span><span><strong>{t(it.lbl)}.</strong> {t(it.txt)}</span></li>
      {/each}
    </ol>
    <p class="ex" data-intro-example>
      <span class="exl">{t('introExampleLbl')}</span>
      <span class="exq">“{t('introExampleJob')}”</span>
      <span class="exr">{t('introExampleResult')}</span>
      <button type="button" class="btn btn-sm" onclick={tryExample} data-intro-try>{t('introTry')}</button>
    </p>
    {#if ask}
      <p class="ask" role="alert" data-intro-ask>
        <span>{t('introReplaceAsk')}</span>
        <button type="button" class="btn btn-sm btn-primary" onclick={put} data-intro-ask-yes>{t('introReplaceYes')}</button>
        <button type="button" class="btn btn-sm" onclick={() => { ask = false; sf.focusNew++ }} data-intro-ask-no>{t('introReplaceNo')}</button>
      </p>
    {/if}
  </section>
{/if}

<style>
  .intro { flex: none; padding: 12px 16px 14px; border-bottom: 1px solid var(--line); background: var(--bg-sunken); }
  .top { display: flex; align-items: flex-start; justify-content: space-between; gap: 10px; }
  .ttl { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 10px; margin: 0; font-family: var(--display); font-size: var(--fs-md); font-weight: var(--fw-strong); line-height: 1.4; color: var(--ink); min-width: 0; }
  .brand { font-size: var(--fs-lg, var(--fs-md)); font-weight: var(--fw-strong); letter-spacing: 0; }
  .sent { font-size: var(--fs-sm); font-weight: var(--fw-medium); min-width: 0; }
  .cls { flex: none; }
  .list { display: flex; flex-direction: column; gap: 4px; margin: 8px 0 0; padding: 0; list-style: none; }
  .it { display: flex; align-items: flex-start; gap: 8px; font-size: var(--fs-sm); line-height: 1.45; color: var(--ink-2); }
  .it strong { color: var(--ink); font-weight: var(--fw-strong); }
  .no { flex: none; width: 18px; height: 18px; margin-top: 1px; border-radius: 50%; background: var(--bg); color: var(--ink); font-size: var(--fs-xs); font-weight: var(--fw-strong); line-height: 18px; text-align: center; box-shadow: inset 0 0 0 1px var(--line-strong); }
  .ic { flex: none; display: inline-flex; margin-top: 2px; color: var(--ink-3); }
  .ex { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 8px; margin: 10px 0 0; font-size: var(--fs-sm); line-height: 1.5; color: var(--ink-2); }
  .ask { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; margin: 8px 0 0; padding: 8px 10px; border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); font-size: var(--fs-sm); }
  .exl { font-weight: var(--fw-strong); color: var(--ink); }
  .exq { color: var(--ink); font-weight: var(--fw-medium); }
  .exr { min-width: 0; }
  .mini { flex: none; display: flex; align-items: center; justify-content: space-between; gap: 10px; padding: 4px 16px; border-bottom: 1px solid var(--line); background: var(--bg); min-height: 32px; }
  .mini .brand { font-size: var(--fs-sm); color: var(--ink); }
  .link { display: inline-flex; align-items: center; min-height: 28px; padding: 0 4px; border: 0; background: none; color: var(--ink-2); font: inherit; font-size: var(--fs-xs); text-decoration: underline; text-underline-offset: 2px; cursor: pointer; }
  .link:hover { color: var(--ink); }
  @media (pointer: coarse) and (hover: none) { .link { min-height: 40px; } }
  @media (max-width: 759px) { .intro { padding: 10px 12px 12px; } .mini { padding: 2px 12px; } }
</style>
