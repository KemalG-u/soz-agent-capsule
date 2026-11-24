<script lang="ts">
  import { onMount, tick, untrack } from 'svelte'
  import AyarlarDurum from '../lib/components/AyarlarDurum.svelte'
  import Icon from '../lib/components/Icon.svelte'
  import SettingRow from '../lib/components/SettingRow.svelte'
  import { t, type Key } from '../lib/i18n/index.svelte'
  import { intro, reopenIntro } from '../lib/intro.svelte'
  import { toolFullLabel } from '../lib/profil'
  import { putServerSettings } from '../lib/api5'
  import { body, groupOrder, isChanged, loadRegistry, matches, rg, titleOf, visibleSettings } from '../lib/registry.svelte'
  import { router } from '../lib/router.svelte'
  import { requestShellPrefs, resetPanelPlace, setShellPref } from '../lib/shell'
  import { sp } from '../lib/shellPrefs.svelte'
  import { OPTIONAL_NAV_ROUTES, THEME_FAMILIES, app, saveNavTabs, saveThemeChoice, setLang, toast, updateSettings, type OptionalNavRoute } from '../lib/store.svelte'
  import { loadAgents, sf } from '../lib/surface.svelte'
  import { yardimciDurumu } from '../lib/gezinme'
  import type { ThemeFamily } from '../lib/types'
  import type { RegistryEntry } from '../lib/types5'
  import { errText } from '../lib/util'

  let q = $state('')
  let onlyChanged = $state(false)
  let advOpen = $state(false)
  let open = $state<Record<string, boolean>>({ genel: true, 'gorunum-ses': true })
  let searchEl: HTMLInputElement | undefined = $state()
  let prefersDark = $state(false)

  // Kabuk tercihleri: yalnız masaüstü uygulamasında cevap gelir, tarayıcıda bu bölüm hiç görünmez.
  let confirmDelete = $state(false)

  onMount(() => {
    requestShellPrefs()
    void loadRegistry()
    void loadAgents()
    if (typeof window !== 'undefined' && window.matchMedia) {
      const mq = window.matchMedia('(prefers-color-scheme: dark)')
      prefersDark = mq.matches
      const fn = (e: MediaQueryListEvent) => (prefersDark = e.matches)
      mq.addEventListener?.('change', fn)
      return () => mq.removeEventListener?.('change', fn)
    }
  })

  // untrack: yükleme işlevinin içindeki durum okumaları bu etkiyi izlemesin (kendini tetikleyen döngü olmasın).
  $effect(() => {
    void app.tick
    untrack(() => { if (rg.loaded) void loadRegistry() })
  })

  const effectiveDark = $derived(app.theme === 'dark' || (app.theme === 'system' && prefersDark))

  const SWATCHES: Record<ThemeFamily, { light: { bg: string; card: string; accent: string; ink: string }; dark: { bg: string; card: string; accent: string; ink: string }; r: string }> = {
    cam: { light: { bg: '#f4f7fc', card: '#ffffff', accent: '#0b6cc4', ink: '#12161d' }, dark: { bg: '#141823', card: '#1c2230', accent: '#6cc0ff', ink: '#f2f5fb' }, r: '10px' },
    terminal: { light: { bg: '#f6f6f3', card: '#ffffff', accent: '#0d5c46', ink: '#111314' }, dark: { bg: '#0f1213', card: '#15191b', accent: '#5fe0b4', ink: '#e8ecec' }, r: '2px' },
    mat: { light: { bg: '#eeeff2', card: '#f8f9fb', accent: '#243047', ink: '#16181c' }, dark: { bg: '#17191d', card: '#1f2228', accent: '#d8e0ef', ink: '#eceff4' }, r: '8px' },
    ton: { light: { bg: '#f4f1fa', card: '#fdfbff', accent: '#5641a8', ink: '#1a1625' }, dark: { bg: '#15121e', card: '#1f1b2c', accent: '#cbbdff', ink: '#f2eefb' }, r: '14px' },
    duz: { light: { bg: '#f5f5f0', card: '#ffffff', accent: '#c9ea3b', ink: '#0f100e' }, dark: { bg: '#11120f', card: '#191b16', accent: '#d5f24d', ink: '#f4f5f0' }, r: '0px' },
    sicak: { light: { bg: '#f6f1ea', card: '#fffcf7', accent: '#8a2c52', ink: '#221c17' }, dark: { bg: '#1a1613', card: '#231e1a', accent: '#f29ab8', ink: '#f5ede4' }, r: '8px' },
  }

  async function pickFamily(f: ThemeFamily) {
    if (await saveThemeChoice({ themeFamily: f })) {
      rg.values = { ...rg.values, themeFamily: f }
      toast(t('saved'))
    } else toast(t('themeNotSaved'))
  }

  async function pickMode(th: 'light' | 'dark' | 'system') {
    if (await saveThemeChoice({ theme: th })) {
      rg.values = { ...rg.values, theme: th }
      toast(t('saved'))
    } else toast(t('themeNotSaved'))
  }

  async function pickLang(l: 'tr' | 'en') {
    const prevLang = app.lang
    setLang(l)
    rg.values = { ...rg.values, language: l }
    try {
      if (app.settings) await updateSettings({ language: l })
      else rg.values = await putServerSettings({ language: l })
      toast(t('saved'))
    } catch {
      // Sunucu kaydetmedi: dil eski hâline döner, "Kaydedildi" denmez.
      setLang(prevLang)
      rg.values = { ...rg.values, language: prevLang }
      toast(t('themeNotSaved'))
    }
  }

  const navTabLabels: Record<OptionalNavRoute, Key> = {
    bekci: 'navGuard',
    defter: 'navLedger',
    neoldu: 'navHistory',
    harcama: 'navSpend',
    ara: 'navSearch',
  }

  const orderedTabChoices = $derived.by<OptionalNavRoute[]>(() => {
    const active = app.navTabs
    const rest = OPTIONAL_NAV_ROUTES.filter((r) => !active.includes(r))
    return [...active, ...rest]
  })

  async function toggleNavTab(tab: OptionalNavRoute) {
    const cur = app.navTabs
    const next = cur.includes(tab) ? cur.filter((x) => x !== tab) : [...cur, tab]
    toast((await saveNavTabs(next)) ? t('saved') : t('navSaveFail'))
  }

  async function moveNavTab(tab: OptionalNavRoute, dir: -1 | 1) {
    const cur = [...app.navTabs]
    const i = cur.indexOf(tab)
    const j = i + dir
    if (i < 0 || j < 0 || j >= cur.length) return
    ;[cur[i], cur[j]] = [cur[j], cur[i]]
    toast((await saveNavTabs(cur)) ? t('saved') : t('navSaveFail'))
  }

  function handleResetPanel() {
    // Tarayıcıda sıfırlanacak panel yok: "sıfırlandı" denmez (A-AYR-06).
    if (resetPanelPlace()) toast(t('panelResetDone'))
    else toast(t('panelResetBrowser'))
  }

  // Temel ayarlar: varsayılan yardımcı, paralel sınır, kurulu yardımcılar ve dikte durumu
  const currentDefaultAgent = $derived(String(rg.values.defaultAgent ?? app.settings?.defaultAgent ?? 'claude'))
  const currentMaxParallel = $derived(Number(rg.values.maxParallelRuns ?? app.settings?.maxParallelRuns ?? 2))
  const currentDictationPath = $derived(String(rg.values.dictationPath ?? app.settings?.dictationPath ?? '').trim())

  // Kurulu olmayan yardımcı kuruluymuş gibi gösterilmez (A-AYR-12): hiçbiri kurulu değilse liste yalnız şu anki seçimi "kurulu değil" diye taşır.
  const installedIds = $derived(new Set(sf.agents.filter((a) => a.found).map((a) => a.id as string)))
  const noneInstalled = $derived(sf.agents.length > 0 && installedIds.size === 0)
  const readyHelperChoices = $derived.by(() => {
    const base = [...installedIds]
    if (currentDefaultAgent && !base.includes(currentDefaultAgent)) return [...base, currentDefaultAgent]
    return base
  })

  // Sunucu kaydetmezse seçim kutusu eski değerine döner ve bunu söyler (sayı alanındaki gibi).
  async function changeDefaultAgent(v: string, el?: HTMLSelectElement) {
    try {
      const next = await putServerSettings({ defaultAgent: v })
      rg.values = next
      if (app.settings) app.settings = { ...app.settings, defaultAgent: v }
      toast(t('saved'))
    } catch (e) {
      if (el) el.value = currentDefaultAgent
      toast(errText(e) + ' ' + t('stReverted'))
    }
  }

  async function changeMaxParallel(v: number, el?: HTMLSelectElement) {
    try {
      const next = await putServerSettings({ maxParallelRuns: v })
      rg.values = next
      if (app.settings) app.settings = { ...app.settings, maxParallelRuns: v }
      toast(t('saved'))
    } catch (e) {
      if (el) el.value = String(currentMaxParallel)
      toast(errText(e) + ' ' + t('stReverted'))
    }
  }

  // Gelişmiş ayarlar (kayıt defterindeki tüm ayar grupları)
  const all = $derived(visibleSettings())
  const changedAll = $derived(all.filter(isChanged))
  const pool = $derived(onlyChanged ? changedAll : all)
  const found = $derived(q.trim() ? pool.filter((e) => matches(e, q)) : pool)
  const searching = $derived(q.trim().length > 0)
  const forceOpen = $derived(searching || onlyChanged)

  const groupKey = (e: RegistryEntry): string => (groupOrder as string[]).includes(body(e).group ?? '') ? (body(e).group as string) : 'other'
  const groups = $derived.by(() => {
    const order = [...groupOrder, 'other']
    return order
      .map((g) => {
        const items = found.filter((e) => groupKey(e) === g)
        return { key: g, items, changed: items.filter(isChanged).length, names: items.map(titleOf).join(' · ') }
      })
      .filter((g) => g.items.length > 0)
  })

  function split(items: RegistryEntry[]) {
    const normal = items.filter((e) => body(e).class !== 'K4')
    const gates = items.filter((e) => body(e).class === 'K4')
    const cards: { name: string; items: RegistryEntry[] }[] = []
    for (const e of normal) {
      const name = body(e).card ?? ''
      let c = cards.find((x) => x.name === name)
      if (!c) cards.push((c = { name, items: [] }))
      c.items.push(e)
    }
    return { cards, gates }
  }

  const gname = (g: string) => t(`grp_${g}` as Key)

  // Hedefi yalnız sayfanın kendi kaydırma alanında gösterir. scrollIntoView pencerenin kendisini de kaydırıp tüm uygulamayı yukarı iter (A-GEN-22).
  function paneKaydir(el: HTMLElement, where: 'start' | 'center') {
    let p: HTMLElement | null = el.parentElement
    while (p && !/(auto|scroll)/.test(getComputedStyle(p).overflowY)) p = p.parentElement
    if (!p) return
    const r = el.getBoundingClientRect()
    const pr = p.getBoundingClientRect()
    const hedef = where === 'center' ? r.top - pr.top - (pr.height - r.height) / 2 : r.top - pr.top - 12
    p.scrollTo({ top: Math.max(0, p.scrollTop + hedef) })
  }

  let done = ''
  $effect(() => {
    const sub = router.sub
    if (!sub || done === sub || !rg.loaded) return
    done = sub
    if (sub === 'claude') {
      advOpen = true
      open['arac-ajan'] = true
      queueMicrotask(() => {
        const el = document.querySelector<HTMLElement>('[data-setting="claudePath"] [data-setting-input]')
        if (el) paneKaydir(el, 'center')
        el?.focus({ preventScroll: true })
      })
    } else if (sub === 'paralel') {
      advOpen = true
      open['arac-ajan'] = true
      queueMicrotask(() => {
        const el = document.querySelector<HTMLElement>('[data-setting="maxParallelRuns"] [data-setting-input]')
        if (el) paneKaydir(el, 'center')
        el?.focus({ preventScroll: true })
      })
    } else if (sub === 'kancalar') {
      advOpen = true
      // Gelişmiş bölümü açıldıktan SONRA kaydırılır (A-GEN-22): önce çizim beklenir, düzen oturunca bir kez daha kaydırılır.
      void tick().then(() => requestAnimationFrame(() => {
        const d = document.getElementById('s-hook') as HTMLDetailsElement | null
        if (!d) return
        d.open = true
        paneKaydir(d, 'start')
        d.querySelector<HTMLElement>('summary')?.focus({ preventScroll: true })
        setTimeout(() => paneKaydir(d, 'start'), 250)
      }))
    }
  })
</script>

<div class="page" data-settings-page>
  <header class="page-head">
    <div>
      <h1 class="h1">{t('settingsTitle')}</h1>
      <p class="lead small">{t('stEssentialLead')}</p>
    </div>
    <a class="btn btn-sm" href="#/taniyorum"><Icon name="person" size={15} />{t('navKnow')}</a>
  </header>

  <!-- 1. Onaylı 6 Görsel Tema Kartı + Renk Kipi -->
  <section class="ess-sec" aria-labelledby="th-h" data-theme-section>
    <div class="sec-head">
      <div>
        <h2 class="h2" id="th-h">{t('thFamilyTitle')}</h2>
        <p class="small muted">{t('thFamilyLead')}</p>
      </div>
      <div class="mode-seg" role="group" aria-label={t('thModeTitle')} data-theme-mode>
        <button type="button" class="btn btn-sm" class:btn-primary={app.theme === 'light'} aria-pressed={app.theme === 'light'} onclick={() => void pickMode('light')} data-mode="light">{t('themeLight')}</button>
        <button type="button" class="btn btn-sm" class:btn-primary={app.theme === 'dark'} aria-pressed={app.theme === 'dark'} onclick={() => void pickMode('dark')} data-mode="dark">{t('themeDark')}</button>
        <button type="button" class="btn btn-sm" class:btn-primary={app.theme === 'system'} aria-pressed={app.theme === 'system'} onclick={() => void pickMode('system')} data-mode="system">{t('themeSystem')}</button>
      </div>
    </div>

    <div class="theme-grid" role="radiogroup" aria-label={t('thFamilyTitle')}>
      {#each THEME_FAMILIES as fam (fam)}
        {@const sw = effectiveDark ? SWATCHES[fam].dark : SWATCHES[fam].light}
        {@const selected = app.themeFamily === fam}
        <button
          type="button"
          role="radio"
          aria-checked={selected}
          class="tcard"
          class:on={selected}
          onclick={() => void pickFamily(fam)}
          data-theme-card={fam}
        >
          <span class="tprev" style:background={sw.bg} style:border-radius={SWATCHES[fam].r} aria-hidden="true">
            <span class="tp-card" style:background={sw.card} style:border-radius={SWATCHES[fam].r}>
              <span class="tp-dot" style:background={sw.accent} style:border-radius={SWATCHES[fam].r}></span>
              <span class="tp-line" style:background={sw.ink}></span>
            </span>
          </span>
          <span class="tmeta">
            <span class="tname">
              <span>{t(`th_${fam}` as Key)}</span>
              {#if selected}<Icon name="check" size={14} strokeWidth={2.2} />{/if}
            </span>
            <span class="tdesc">{t(`th_${fam}_desc` as Key)}</span>
          </span>
        </button>
      {/each}
    </div>
  </section>

  <!-- 2. Panel Düzeni (Alt menü bölümleri aç/kapat + sırala + Panel yerini sıfırla) -->
  <section class="ess-sec" aria-labelledby="pl-h" data-panel-layout>
    <h2 class="h2" id="pl-h">{t('panelLayoutTitle')}</h2>
    <p class="small muted">{t('panelLayoutLead')}</p>

    <ul class="navopt-list" aria-label={t('panelExtraTabs')}>
      {#each orderedTabChoices as tab (tab)}
        {@const enabled = app.navTabs.includes(tab)}
        {@const idx = app.navTabs.indexOf(tab)}
        <li class="tab-row" data-nav-opt={tab}>
          <button
            type="button"
            role="switch"
            class="switch"
            aria-checked={enabled}
            aria-label={t(navTabLabels[tab])}
            onclick={() => void toggleNavTab(tab)}
            data-nav-toggle={tab}
          ></button>
          <span class="tab-lbl">{t(navTabLabels[tab])}</span>
          {#if enabled && app.navTabs.length > 1}
            <div class="tab-ord">
              <button type="button" class="btn btn-quiet btn-sm ord-btn" disabled={idx <= 0} aria-label={`${t(navTabLabels[tab])} ${t('panelMoveUp')}`} title={t('panelMoveUp')} onclick={() => void moveNavTab(tab, -1)} data-nav-up={tab}>↑</button>
              <button type="button" class="btn btn-quiet btn-sm ord-btn" disabled={idx < 0 || idx >= app.navTabs.length - 1} aria-label={`${t(navTabLabels[tab])} ${t('panelMoveDown')}`} title={t('panelMoveDown')} onclick={() => void moveNavTab(tab, 1)} data-nav-down={tab}>↓</button>
            </div>
          {/if}
        </li>
      {/each}
    </ul>

    <div class="ess-row">
      <div class="ess-txt">
        <span class="ess-lbl">{t('panelResetLabel')}</span>
        <span class="small muted">{t('panelResetHelp')}</span>
      </div>
      <button type="button" class="btn btn-sm" onclick={handleResetPanel} data-reset-panel>{t('panelResetBtn')}</button>
    </div>

    <div class="ess-row">
      <div class="ess-txt">
        <span class="ess-lbl">{t('introSettingTitle')}</span>
        <span class="small muted">{t('introSettingHelp')}</span>
        {#if !intro.closed}<span class="small muted" id="intro-open-why" data-intro-open-why>{t('introSettingOpen')}</span>{/if}
      </div>
      <button type="button" class="btn btn-sm" onclick={() => { reopenIntro(); toast(t('introSettingShown')) }} disabled={!intro.closed} aria-describedby={!intro.closed ? 'intro-open-why' : undefined} data-intro-setting>{t('introSettingShow')}</button>
    </div>
  </section>

  {#if sp.known}
    <section class="ess-sec" aria-labelledby="sh-h" data-shell-prefs>
      <h2 class="h2" id="sh-h">{t('shellPrefsTitle')}</h2>
      <div class="ess-row">
        <div class="ess-txt">
          <span class="ess-lbl">{t('shellPrefNotifyLabel')}</span>
          <span class="small muted">{t('shellPrefNotifyHelp')}</span>
        </div>
        <button type="button" role="switch" class="switch" aria-checked={sp.bildirimler} aria-label={t('shellPrefNotifyLabel')} onclick={() => setShellPref('bildirimler', !sp.bildirimler)} data-shell-pref="bildirimler"></button>
      </div>
      <div class="ess-row">
        <div class="ess-txt">
          <span class="ess-lbl">{t('shellPrefStartLabel')}</span>
          <span class="small muted">{t('shellPrefStartHelp')}</span>
        </div>
        <button type="button" role="switch" class="switch" aria-checked={sp.acilistaBasla} aria-label={t('shellPrefStartLabel')} onclick={() => setShellPref('acilistaBasla', !sp.acilistaBasla)} data-shell-pref="acilistaBasla"></button>
      </div>
      <div class="ess-row">
        <div class="ess-txt">
          <span class="ess-lbl">{t('shellPrefDeleteLabel')}</span>
          <span class="small muted">{t('shellPrefDeleteHelp')}</span>
          {#if confirmDelete && !sp.kaldirirkenVeriyiSil}
            <span class="small" role="alert" data-shell-delete-warn>{t('shellPrefDeleteWarn')}</span>
            <span class="tab-ord">
              <button type="button" class="btn btn-sm" onclick={() => { confirmDelete = false; setShellPref('kaldirirkenVeriyiSil', true, true) }} data-shell-delete-yes>{t('shellPrefDeleteYes')}</button>
              <button type="button" class="btn btn-quiet btn-sm" onclick={() => (confirmDelete = false)} data-shell-delete-no>{t('shellPrefDeleteNo')}</button>
            </span>
          {/if}
        </div>
        <button
          type="button"
          role="switch"
          class="switch"
          aria-checked={sp.kaldirirkenVeriyiSil}
          aria-label={t('shellPrefDeleteLabel')}
          onclick={() => { if (sp.kaldirirkenVeriyiSil) setShellPref('kaldirirkenVeriyiSil', false); else confirmDelete = !confirmDelete }}
          data-shell-pref="kaldirirkenVeriyiSil"
        ></button>
      </div>
    </section>
  {/if}

  <!-- 3. Temel Çalışma Ayarları (~10 sade ayar tamamlaması) -->
  <section class="ess-sec" aria-label={t('settingsTitle')} data-essential-settings>
    <div class="ess-row">
      <div class="ess-txt">
        <label class="ess-lbl" for="ess-agent">{t('stDefaultAgentLabel')}</label>
        <span class="small muted">{t('stDefaultAgentHelp')}</span>
      </div>
      <select id="ess-agent" class="select ess-ctl" value={currentDefaultAgent} onchange={(e) => { const el = e.currentTarget as HTMLSelectElement; void changeDefaultAgent(el.value, el) }} data-essential-agent>
        {#each readyHelperChoices as ag (ag)}
          <option value={ag}>{toolFullLabel(ag)}{installedIds.has(ag) || sf.agents.length === 0 ? '' : ` (${t('notInstalledShort')})`}</option>
        {/each}
      </select>
      {#if noneInstalled}<p class="small err-line ess-none" data-none-installed>{t('noHelperInstalled')}</p>{/if}
    </div>

    <div class="ess-row">
      <div class="ess-txt">
        <label class="ess-lbl" for="ess-par">{t('stMaxParallelLabel')}</label>
        <span class="small muted">{t('stMaxParallelHelp')}</span>
      </div>
      <select id="ess-par" class="select ess-num" value={currentMaxParallel} onchange={(e) => { const el = e.currentTarget as HTMLSelectElement; void changeMaxParallel(Number(el.value), el) }} data-essential-parallel>
        {#each [1, 2, 3, 4, 5, 6] as n (n)}
          <option value={n}>{n}</option>
        {/each}
      </select>
    </div>

    <div class="ess-row">
      <div class="ess-txt">
        <span class="ess-lbl">{t('language')}</span>
        <span class="small muted" data-lang-partial>{t('langPartial')}</span>
      </div>
      <div class="mode-seg" role="group" aria-label={t('language')}>
        <button type="button" class="btn btn-sm" class:btn-primary={app.lang === 'tr'} aria-pressed={app.lang === 'tr'} onclick={() => void pickLang('tr')}>Türkçe</button>
        <button type="button" class="btn btn-sm" class:btn-primary={app.lang === 'en'} aria-pressed={app.lang === 'en'} onclick={() => void pickLang('en')} data-lang-en>English <span class="small muted">({t('langPartialShort')})</span></button>
      </div>
    </div>

    <div class="ess-row">
      <div class="ess-txt">
        <span class="ess-lbl">{t('setAgentsTitle')}</span>
        <div class="ag-chips">
          {#if sf.agents.length === 0}
            <span class="small muted">{t('stNoAgents')}</span>
          {:else}
            {#each sf.agents as a (a.id)}
              {@const yd = yardimciDurumu(a.found, sf.ready?.agents.find((r) => r.id === a.id))}
              <span class="chip {yd === 'hazir' ? 'chip-ok' : yd === 'hazirDegil' ? 'chip-waiting' : 'chip-neutral'}" title={yd === 'hazirDegil' ? (sf.ready?.agents.find((r) => r.id === a.id)?.sentence ?? '') : undefined} data-agent-state={yd}>{toolFullLabel(a.id)} · {yd === 'hazir' ? t('agentReady') : yd === 'hazirDegil' ? t('agentNotReady') : t('agentNotFound')}</span>
            {/each}
          {/if}
        </div>
      </div>
    </div>

    <div class="ess-row">
      <div class="ess-txt">
        <span class="ess-lbl">{t('stDictationTitle')}</span>
        <span class="small muted">{currentDictationPath ? t('stDictationReady', { p: currentDictationPath }) : t('stDictationAuto')}</span>
      </div>
    </div>

    <div class="ess-row">
      <div class="ess-txt">
        <span class="ess-lbl">{t('stShortcutsLabel')}</span>
        <span class="small muted">{t('stShortcutsHelp')}</span>
      </div>
      <button type="button" class="btn btn-sm" onclick={() => (app.helpOpen = true)} data-open-shortcuts><Icon name="keyboard" size={15} />{t('stShortcutsOpen')}</button>
    </div>
  </section>

  <!-- 4. Gelişmiş Ayarlar: varsayılan kapalı, arandığında ya da açıldığında tüm kayıt defteri ayarlarını ve güvenlik kapılarını gösterir -->
  {#if !rg.loaded}
    <p class="muted small adv-load" role="status">{t('loading')}</p>
  {:else if rg.error && rg.entries.length === 0}
    <div role="alert" data-settings-error>
      <p class="err-line">{rg.error}</p>
      <p class="retry-row"><button class="btn" onclick={() => loadRegistry({ force: true })}>{t('retry')}</button></p>
    </div>
  {:else}
    <div class="adv-search-wrap">
      <div class="searchbar">
        <label class="sr-only" for="set-search">{t('stSearch')}</label>
        <Icon name="ara" size={16} />
        <input
          id="set-search"
          class="sinput"
          type="search"
          bind:value={q}
          bind:this={searchEl}
          placeholder={t('stSearchPh')}
          autocomplete="off"
          spellcheck="false"
          data-settings-search
          onkeydown={(e) => { if (e.key === 'Escape' && q) { e.preventDefault(); e.stopPropagation(); q = '' } }}
        />
        {#if searching || onlyChanged}
          <span class="small muted" aria-live="polite" data-search-count>{t('stFoundN', { n: found.length })}</span>
        {/if}
      </div>
    </div>

    <details
      class="adv-wrap"
      open={advOpen || forceOpen}
      ontoggle={(e) => { if (!forceOpen) advOpen = (e.currentTarget as HTMLDetailsElement).open }}
      data-advanced-settings
    >
      <summary class="adv-sum" data-advanced-summary>
        <span class="adv-title">{t('stAdvancedTitle')}</span>
        <span class="small muted adv-lead">{t('stAdvancedLead')}</span>
      </summary>

      <div class="adv-body">
        <label class="only small">
          <input type="checkbox" bind:checked={onlyChanged} data-only-changed />
          <span>{t('stOnlyChanged')}</span>
          <span class="muted" data-changed-total>{changedAll.length > 0 ? `(${changedAll.length})` : ''}</span>
        </label>

        {#if rg.error}<p class="small err-line" role="alert">{rg.error}</p>{/if}

        {#if found.length === 0}
          <div class="empty-state" data-empty="settings">
            <p>{searching ? t('stNone', { q: q.trim() }) : t('stNoneChanged')}</p>
            <button class="btn btn-sm" onclick={() => { q = ''; onlyChanged = false; searchEl?.focus() }}>{t('stClear')}</button>
          </div>
        {/if}

        {#each groups as g (g.key)}
          {@const s = split(g.items)}
          <details
            class="grp"
            open={forceOpen || !!open[g.key]}
            ontoggle={(e) => {
              const isOpen = (e.currentTarget as HTMLDetailsElement).open
              // Başlangıçta açık görünen iç gruplar Gelişmiş katını kendiliğinden açmasın (C14): yalnız dış kat zaten açıksa.
              if (isOpen && (e.currentTarget as HTMLElement).closest<HTMLDetailsElement>('.adv-wrap')?.open) advOpen = true
              if (!forceOpen) open[g.key] = isOpen
            }}
            data-group={g.key}
          >
            <summary class="gsum">
              <span class="gline">
                <span class="gh">{gname(g.key)}</span>
                <span class="small muted gn">{t('stCountN', { n: g.items.length })}</span>
                {#if g.changed > 0}<span class="chip chip-working gc" data-group-changed={g.changed}>{t('stChangedN', { n: g.changed })}</span>{/if}
              </span>
              {#if !forceOpen}<span class="small muted gnames" title={g.names} data-group-names><span class="sr-only">{t('stGroupNamesLabel')}: </span>{g.names}</span>{/if}
            </summary>
            <p class="small muted gl">{t(`grpHelp_${g.key}` as Key)}</p>
            {#each s.cards as c (c.name)}
              {#if c.name}<h3 class="h3 card">{c.name}</h3>{/if}
              {#each c.items as e (e.id)}<SettingRow entry={e} showCard={searching} />{/each}
            {/each}
            {#if s.gates.length > 0}
              <div class="gates" data-gates>
                <h3 class="h3 card"><Icon name="shield" size={16} />{t('stGatesTitle')}</h3>
                <p class="small muted">{t('stGatesLead')}</p>
                {#each s.gates as e (e.id)}<SettingRow entry={e} showCard={searching} />{/each}
              </div>
            {/if}
          </details>
        {/each}

        {#if !forceOpen}
          <h2 class="h2 statush">{t('stStatusTitle')}</h2>
          <p class="small muted">{t('stStatusLead')}</p>
          <AyarlarDurum />
        {/if}
      </div>
    </details>
  {/if}
</div>

<style>
  .ess-sec {
    padding: 14px 0;
    border-top: 1px solid var(--line);
  }
  .sec-head {
    display: flex;
    flex-wrap: wrap;
    align-items: flex-start;
    justify-content: space-between;
    gap: 10px 16px;
    margin-bottom: 12px;
  }
  .mode-seg {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
  }

  .theme-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(11.5rem, 1fr));
    gap: 8px;
  }
  .tcard {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
    min-height: 60px;
    padding: 8px 10px;
    border: 1px solid var(--line);
    border-radius: var(--radius-lg);
    background: var(--surface);
    color: var(--ink);
    text-align: left;
    cursor: pointer;
    transition: border-color var(--dur-1) var(--ease), background-color var(--dur-1) var(--ease);
  }
  .tcard:hover {
    border-color: var(--line-strong);
    background: var(--hover);
  }
  .tcard.on {
    border-color: var(--primary);
    box-shadow: inset 0 0 0 1px var(--primary);
  }
  .tprev {
    flex: none;
    width: 40px;
    height: 36px;
    padding: 5px;
    border: 1px solid var(--line);
    display: flex;
    align-items: stretch;
  }
  .tp-card {
    flex: 1;
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    padding: 4px;
    border: 1px solid rgba(128, 128, 128, 0.2);
  }
  .tp-dot {
    width: 14px;
    height: 6px;
  }
  .tp-line {
    width: 82%;
    height: 2px;
    opacity: 0.7;
  }
  .tmeta {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .tname {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 4px;
    font-size: var(--fs-sm);
    font-weight: var(--fw-strong);
  }
  .tdesc {
    font-size: var(--fs-xs);
    color: var(--ink-2);
    line-height: 1.25;
    display: -webkit-box;
    line-clamp: 2;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .navopt-list {
    display: flex;
    flex-direction: column;
    margin: 10px 0 6px;
    border-top: 1px solid var(--line);
  }
  .tab-row {
    display: flex;
    align-items: center;
    gap: 10px;
    min-height: 38px;
    padding: 4px 0;
    border-bottom: 1px solid var(--line);
  }
  .tab-lbl {
    flex: 1;
    min-width: 0;
    font-size: var(--fs-sm);
    font-weight: var(--fw-medium);
  }
  .tab-ord {
    display: inline-flex;
    align-items: center;
    gap: 4px;
  }
  .ord-btn {
    padding: 0 8px;
    min-width: 28px;
  }

  .ess-row {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: space-between;
    gap: 8px 16px;
    padding: 10px 0;
    border-bottom: 1px solid var(--line);
  }
  .ess-sec > .ess-row:last-child {
    border-bottom: 0;
  }
  .ess-txt {
    flex: 1 1 13rem;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .ess-lbl {
    font-size: var(--fs-sm);
    font-weight: var(--fw-medium);
    color: var(--ink);
  }
  .ess-ctl {
    width: auto;
    min-width: 9.5rem;
    max-width: 100%;
  }
  .ess-num {
    width: 5.5rem;
  }
  .ag-chips {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    margin-top: 4px;
  }

  .adv-load { margin-top: 16px; }
  .adv-search-wrap {
    margin-top: 14px;
    padding-top: 14px;
    border-top: 1px solid var(--line-strong);
  }
  .searchbar {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 2px 12px;
    margin-bottom: 10px;
    border: 1px solid var(--line);
    border-radius: var(--radius-lg);
    background: var(--surface);
  }
  .searchbar:focus-within { border-color: var(--focus); }
  .sinput { flex: 1; min-width: 0; border: 0; outline: none; background: none; min-height: var(--ctl); font-size: var(--fs-sm); color: var(--ink); }

  .adv-wrap {
    border: 1px solid var(--line);
    border-radius: var(--radius-lg);
    background: var(--surface);
    padding: 0 12px;
  }
  .adv-sum {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 4px 12px;
    min-height: var(--row-m);
    padding: 10px 0;
    cursor: pointer;
    list-style: none;
  }
  .adv-sum::-webkit-details-marker { display: none; }
  .adv-sum::before {
    content: '';
    flex: none;
    align-self: center;
    width: 7px;
    height: 7px;
    border-right: 2px solid var(--ink-3);
    border-bottom: 2px solid var(--ink-3);
    transform: rotate(-45deg);
    transition: transform var(--dur-1) var(--ease);
  }
  .adv-wrap[open] > .adv-sum::before { transform: rotate(45deg); }
  .adv-title { font-size: var(--fs-md); font-weight: var(--fw-strong); }
  .adv-lead { flex: 1 1 14rem; }
  .adv-body { padding: 4px 0 12px; }

  .grp { border-top: 1px solid var(--line); padding-bottom: 6px; }
  .gsum { display: grid; grid-template-columns: auto minmax(0, 1fr); align-items: center; column-gap: 12px; row-gap: 0; min-height: var(--row-m); padding: 10px 0 4px; cursor: pointer; list-style: none; }
  .gline { display: flex; flex-wrap: wrap; align-items: baseline; gap: 2px 12px; min-width: 0; }
  .gnames { grid-column: 2; display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; margin-top: 2px; }
  .grp[open] > .gsum .gnames { display: none; }
  .gc { align-self: center; }
  .only { display: inline-flex; align-items: center; gap: 8px; min-height: var(--ctl); margin: 0 0 8px 2px; cursor: pointer; }
  .only input { width: 24px; height: 24px; accent-color: var(--ink); }
  .gsum::-webkit-details-marker { display: none; }
  .gsum::before { content: ''; flex: none; align-self: center; width: 7px; height: 7px; border-right: 2px solid var(--ink-3); border-bottom: 2px solid var(--ink-3); transform: rotate(-45deg); transition: transform var(--dur-1) var(--ease); }
  .grp[open] > .gsum::before { transform: rotate(45deg); }
  .gh { font-size: var(--fs-md); font-weight: var(--fw-strong); }
  .gl { margin: 0 0 6px 19px; }
  .card { margin-top: 14px; display: flex; align-items: center; gap: 8px; color: var(--ink-2); }
  .gates { margin-top: 18px; padding: 4px 14px 8px; border: 1px solid var(--waiting); border-radius: var(--radius-lg); background: color-mix(in srgb, var(--waiting-tint) 35%, transparent); }
  .gates .card { color: var(--waiting); }
  .statush { margin-top: 28px; }

  @media (max-width: 439px) {
    .theme-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .tprev { width: 32px; height: 30px; padding: 4px; }
  }
  @media (pointer: coarse) and (hover: none) {
    .tab-row { min-height: 48px; }
    .ord-btn { min-width: 40px; }
  }
  @media (prefers-reduced-motion: reduce) {
    .gsum::before, .adv-sum::before { transition: none; }
  }
</style>
