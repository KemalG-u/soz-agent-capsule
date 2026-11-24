<script lang="ts">
  import { onMount } from 'svelte'
  import Icon, { type IconName } from './lib/components/Icon.svelte'
  import Palette from './lib/components/Palette.svelte'
  import BaglantiSeridi from './lib/components/BaglantiSeridi.svelte'
  import ProjelerIslerTabs from './lib/components/ProjelerIslerTabs.svelte'
  import ShortcutHelp from './lib/components/ShortcutHelp.svelte'
  import Surface from './lib/components/Surface.svelte'
  import { t, type Key } from './lib/i18n/index.svelte'
  import { onGlobalKey } from './lib/keys'
  import { katmanEkle } from './lib/katman'
  import { router, type Route } from './lib/router.svelte'
  import { initShell } from './lib/shell'
  import { app, boot, type OptionalNavRoute } from './lib/store.svelte'
  import { boot3, sf } from './lib/surface.svelte'
  import { at, loadAttention } from './lib/attention.svelte'
  import { waitingBreakdown } from './lib/profileStore.svelte'
  import Ayarlar from './pages/Ayarlar.svelte'
  import Bekci from './pages/Bekci.svelte'
  import DefterSayfa from './pages/DefterSayfa.svelte'
  import HarcamaSayfa from './pages/HarcamaSayfa.svelte'
  import NeDemistim from './pages/NeDemistim.svelte'
  import NeOldu from './pages/NeOldu.svelte'
  import Projelerim from './pages/Projelerim.svelte'
  import Oneriler from './pages/Oneriler.svelte'
  import Sabah from './pages/Sabah.svelte'
  import Sira from './pages/Sira.svelte'
  import SuAn from './pages/SuAn.svelte'
  import Tanisma from './pages/Tanisma.svelte'

  onMount(() => {
    const offShell = initShell()
    // Geri ve Esc önce bu katmanlara bakar (O2, A1): açıksa yalnız katman kapanır, sayfa değişmez. Yerel <dialog> pencereleri ayrıca kaydedilmez.
    const offKatman = [
      katmanEkle({ acik: () => sf.paletteOpen, kapat: () => (sf.paletteOpen = false) }),
      katmanEkle({ acik: () => sf.folderMenuOpen, kapat: () => (sf.folderMenuOpen = false) }),
      katmanEkle({ acik: () => sf.agentMenuOpen, kapat: () => (sf.agentMenuOpen = false) }),
    ]
    void boot().then(() => {
      if (!app.failed) {
        void boot3()
        void loadAttention()
      }
    })
    return () => {
      offShell()
      for (const f of offKatman) f()
    }
  })

  // Son ziyaret edilen "Projeler ve İşler" alt sekmesini hatırla (varsayılan: projelerim)
  let projelerSub = $state<'projelerim' | 'is'>('projelerim')
  $effect(() => {
    if (router.route === 'projelerim' || router.route === 'is') {
      projelerSub = router.route
    }
  })

  const extraNavMeta: Record<OptionalNavRoute, { icon: IconName; label: Key; activeOn: Route[] }> = {
    bekci: { icon: 'bekci', label: 'navGuard', activeOn: ['bekci'] },
    defter: { icon: 'defter', label: 'navLedger', activeOn: ['defter', 'oneriler'] },
    neoldu: { icon: 'clock', label: 'navHistory', activeOn: ['neoldu', 'sabah'] },
    harcama: { icon: 'harcama', label: 'navSpend', activeOn: ['harcama'] },
    ara: { icon: 'ara', label: 'navSearch', activeOn: ['ara'] },
  }

  // Tek gerçek sayaç: /api/attention üzerinden bekleyen karar/soru/onay sayısı
  const me = $derived(waitingBreakdown().total)
  const suanActive = $derived(router.route === 'suan' || router.route === 'sira')
  const projelerActive = $derived(router.route === 'projelerim' || router.route === 'is')
  const ayarlarActive = $derived(router.route === 'ayarlar' || router.route === 'taniyorum')
</script>

<svelte:window onkeydown={onGlobalKey} />

<div class="sr-only" role="status" aria-live="polite" aria-atomic="true" data-announce>{#key at.announceId}{at.announce}{/key}</div>

<a class="skip" href="#icerik" onclick={(e) => { e.preventDefault(); document.getElementById('icerik')?.focus() }}>{t('skipToContent')}</a>

<div class="shell">
  <div class="main">
    <main id="icerik" tabindex="-1">
      {#if !app.ready}
        <div class="pad"><p class="muted" role="status">{t('loading')}</p></div>
      {:else if app.failed}
        <div class="page">
          <p class="err-line" role="alert">{t('loadError')}</p>
          <p class="retry-row"><button class="btn" onclick={() => location.reload()}>{t('retry')}</button></p>
        </div>
      {:else if router.route === 'suan'}
        <div class="pagescroll" data-page="suan"><SuAn /></div>
      {:else if router.route === 'is'}
        <ProjelerIslerTabs active="is" />
        <BaglantiSeridi />
        <Surface />
      {:else if router.route === 'projelerim'}
        <ProjelerIslerTabs active="projelerim" />
        <div class="pagescroll" data-page="projelerim"><Projelerim /></div>
      {:else if router.route === 'sira'}
        <div class="pagescroll" data-page="sira"><Sira /></div>
      {:else if router.route === 'taniyorum'}
        <div class="pagescroll" data-page="taniyorum"><Tanisma /></div>
      {:else}
        <div class="pagescroll" data-page={router.route}>
          {#if router.route === 'sabah'}
            <Sabah />
          {:else if router.route === 'neoldu'}
            <NeOldu />
          {:else if router.route === 'oneriler'}
            <Oneriler />
          {:else if router.route === 'bekci'}
            <Bekci />
          {:else if router.route === 'defter'}
            <DefterSayfa />
          {:else if router.route === 'harcama'}
            <HarcamaSayfa />
          {:else if router.route === 'ara'}
            <NeDemistim />
          {:else}
            <Ayarlar />
          {/if}
        </div>
      {/if}
    </main>

    {#if app.toastText || app.mock}
      <footer class="status">
        <div class="st-l" role="status" aria-live="polite">
          {#if app.toastText}{#key app.toastId}<span class="toast">{app.toastText}</span>{#if app.toastAction}{@const act = app.toastAction}<button type="button" class="toast-act" onclick={() => { act.run(); app.toastText = ''; app.toastAction = null }} data-toast-action>{act.label}</button>{/if}{/key}{/if}
        </div>
        <div class="st-r">
          {#if app.mock}<span class="sample" title={t('sampleDataTitle')}>{t('sampleData')}</span>{/if}
        </div>
      </footer>
    {/if}
  </div>

  <!-- Alt gezinme şeridi: varsayılan yalnız 3 net sekme (Şu An, Projeler ve İşler, Ayarlar) + Ayarlar -> Panel Düzeni içinden açılan ek sekmeler -->
  <nav class="rail" aria-label={t('railLabel')}>
    <div class="items">
      <a href="#/suan" class="ri" class:on={suanActive} aria-current={suanActive ? 'page' : undefined} aria-label={t('navSuAn')} title={t('navSuAn')} data-nav="suan">
        <span class="ic">
          <Icon name="toparla" size={18} strokeWidth={suanActive ? 2.1 : 1.7} />
          {#if me > 0}<span class="cnt" aria-label={`${me}`} data-nav-badge>{me}</span>{/if}
        </span>
        <span class="lb">{t('navSuAn')}</span>
      </a>

      <a href={`#/${projelerSub}`} class="ri" class:on={projelerActive} aria-current={projelerActive ? 'page' : undefined} aria-label={t('navProjelerIsler')} title={t('navProjelerIsler')} data-nav="projelerim">
        <span class="ic"><Icon name="folder" size={18} strokeWidth={projelerActive ? 2.1 : 1.7} /></span>
        <span class="lb">{t('navProjelerIsler')}</span>
      </a>

      {#each app.navTabs as tab (tab)}
        {@const meta = extraNavMeta[tab]}
        {#if meta}
          {@const active = meta.activeOn.includes(router.route)}
          <a href={`#/${tab}`} class="ri" class:on={active} aria-current={active ? 'page' : undefined} aria-label={t(meta.label)} title={t(meta.label)} data-nav={tab}>
            <span class="ic"><Icon name={meta.icon} size={18} strokeWidth={active ? 2.1 : 1.7} /></span>
            <span class="lb">{t(meta.label)}</span>
          </a>
        {/if}
      {/each}

      <a href="#/ayarlar" class="ri" class:on={ayarlarActive} aria-current={ayarlarActive ? 'page' : undefined} aria-label={t('navSettings')} title={t('navSettings')} data-nav="ayarlar">
        <span class="ic"><Icon name="ayarlar" size={18} strokeWidth={ayarlarActive ? 2.1 : 1.7} /></span>
        <span class="lb">{t('navSettings')}</span>
      </a>
    </div>
  </nav>
</div>

{#if sf.paletteOpen}
  <Palette />
{/if}
{#if app.helpOpen}
  <ShortcutHelp onclose={() => (app.helpOpen = false)} />
{/if}

<style>
  .skip { position: fixed; left: 12px; top: -60px; z-index: 80; background: var(--primary); color: var(--on-primary); padding: 8px 14px; border-radius: var(--radius); transition: top var(--dur-1) var(--ease); }
  .skip:focus { top: 12px; }

  .shell {
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: minmax(0, 1fr) auto;
    height: 100dvh;
    overflow: hidden;
  }

  .main { grid-column: 1; grid-row: 1; display: flex; flex-direction: column; min-width: 0; min-height: 0; }
  main { flex: 1; min-height: 0; display: flex; flex-direction: column; outline: none; }
  /* position: relative: içerideki ekran okuyucu etiketleri (.sr-only, absolute) sayfanın dışına taşıp belgeyi uzatmasın (Ayarlar'da belge 1394 px oluyordu, tekerlek bütün paneli kaydırıyordu). */
  .pagescroll { position: relative; flex: 1; min-height: 0; overflow-y: auto; }
  .pad { padding: 24px; }

  .status {
    flex: none;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    min-height: 28px;
    padding: 0 12px;
    border-top: 1px solid var(--line);
    background: var(--bg);
    font-size: var(--fs-xs);
    color: var(--ink-2);
  }
  .st-l { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .toast { display: inline-block; font-weight: var(--fw-medium); color: var(--ink); animation: toast-in var(--dur-1) var(--ease); }
  @keyframes toast-in { from { opacity: 0; transform: translateY(4px); } to { opacity: 1; transform: none; } }
  .st-l { display: flex; align-items: center; gap: 8px; }
  .toast { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
  .toast-act { flex: none; display: inline-flex; align-items: center; min-height: 24px; padding: 0 8px; border: 1px solid var(--line-strong); border-radius: var(--radius-sm); background: var(--surface); color: var(--ink); font: inherit; font-weight: var(--fw-strong); cursor: pointer; }
  .toast-act:hover { background: var(--hover); }
  .st-r { flex: none; display: flex; align-items: center; gap: 4px; }
  .sample { padding: 0 8px; border-radius: var(--radius-sm); border: 1px dashed var(--line-strong); color: var(--ink-2); font-weight: var(--fw-medium); white-space: nowrap; }

  .rail {
    grid-column: 1;
    grid-row: 2;
    display: flex;
    align-items: stretch;
    padding: 0 0 env(safe-area-inset-bottom);
    border-top: 1px solid var(--line);
    background: var(--surface);
  }
  .items {
    flex: 1;
    display: flex;
    flex-direction: row;
    justify-content: space-around;
    align-items: stretch;
    gap: 0;
    min-width: 0;
    /* D9: geniş ekranda sekmeler 1920 px'e yayılmaz, en çok yaklaşık 480 px ortada durur. */
    max-width: 30rem;
    margin-inline: auto;
  }
  .ri {
    position: relative;
    flex: 1 1 0;
    min-width: 0;
    height: var(--rail-h);
    display: inline-flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 2px;
    padding: 0 4px;
    border: 0;
    border-radius: 0;
    background: none;
    color: var(--ink-3);
    text-decoration: none;
    transition: background-color var(--dur-1) var(--ease), color var(--dur-1) var(--ease);
  }
  .ri:hover { background: var(--hover); color: var(--ink); }
  .ri.on {
    color: var(--ink);
    box-shadow: inset 0 2px 0 var(--primary);
  }
  .ic { position: relative; display: inline-flex; }
  .lb {
    display: block;
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: var(--fs-xs);
    line-height: 1.2;
    font-weight: var(--fw-medium);
  }
  .ri.on .lb { font-weight: var(--fw-strong); }
  /* A3: %200 yakınlaştırmada (250 CSS px) uzun sekme adı kesilmez, iki satıra iner. */
  @media (max-width: 299px) {
    .ri { gap: 0; }
    .lb { white-space: normal; text-align: center; overflow-wrap: anywhere; font-size: 0.6875rem; line-height: 1.05; }
  }
  .cnt {
    position: absolute;
    top: -6px;
    right: -10px;
    min-width: 16px;
    height: 16px;
    padding: 0 4px;
    border-radius: 8px;
    background: var(--waiting);
    color: var(--surface);
    font-size: var(--fs-xs);
    font-weight: var(--fw-strong);
    line-height: 16px;
    text-align: center;
    font-variant-numeric: tabular-nums;
  }
</style>
