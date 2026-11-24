<script lang="ts">
  import { t } from '../i18n/index.svelte'
  import { router } from '../router.svelte'
  import { addFolderFlow, dictate } from '../shell'
  import { app, toast } from '../store.svelte'
  import { agentName, agentOptions, chosenAgent, currentProject, realProjects, selectProjectPath, setProject, sf, startBlocked, startDraft } from '../surface.svelte'
  import { activeProfileProjects } from '../profileStore.svelte'
  import { shortPath, validTs } from '../profil'
  import { inShell, isTestProject, parseTs } from '../util'
  import { checkJobText } from '../api2'
  import Icon from './Icon.svelte'
  import NotJobNote from './NotJobNote.svelte'
  import StartStrip from './StartStrip.svelte'

  let ta: HTMLTextAreaElement | undefined = $state()
  let focused = $state(false)
  let menuIdx = $state(0)
  let menuEl: HTMLElement | undefined = $state()
  let chipEl: HTMLButtonElement | undefined = $state()
  let listening = $state(false)
  const shell = inShell()

  const normPath = (p: string | null | undefined) => (p ?? '').trim().replace(/[\\/]+$/, '').toLowerCase()

  type FolderChoice = {
    key: string
    projectId: number | null
    path: string | null
    label: string
    hint: string
    lastMs: number
    isAdd?: boolean
  }

  // Söz'deki projeler ile bilgisayarda tanınan gerçek projeler tek listede birleşir, en son aktif olan en üstte durur.
  const folderChoices = $derived.by<FolderChoice[]>(() => {
    const byPath = new Map<string, FolderChoice>()
    const noPath: FolderChoice[] = []

    for (const p of realProjects()) {
      const ms = parseTs(p.lastAt)
      const lastMs = Number.isFinite(ms) ? ms : 0
      if (p.path) {
        const k = normPath(p.path)
        byPath.set(k, { key: `p-${p.id}`, projectId: p.id, path: p.path, label: p.name, hint: shortPath(p.path), lastMs })
      } else {
        noPath.push({ key: `p-${p.id}`, projectId: p.id, path: null, label: p.name, hint: '', lastMs })
      }
    }

    for (const pp of activeProfileProjects()) {
      const k = normPath(pp.path)
      const pMs = validTs(pp.lastActivity)
      const lastMs = Number.isFinite(pMs) ? pMs : 0
      const ex = byPath.get(k)
      if (ex) {
        if (lastMs > ex.lastMs) ex.lastMs = lastMs
      } else {
        byPath.set(k, {
          key: `prof-${k}`,
          projectId: null,
          path: pp.path,
          label: pp.name,
          hint: shortPath(pp.displayHint || pp.path),
          lastMs,
        })
      }
    }

    // Sınama ve geçici klasörler listenin sonuna iner, ne kadar yeni olursa olsun varsayılan olmaz.
    for (const c of [...byPath.values(), ...noPath]) if (isTestProject({ name: c.label, path: c.path })) c.lastMs = -1
    const merged = [...byPath.values(), ...noPath].sort((a, b) => b.lastMs - a.lastMs || a.label.localeCompare(b.label, 'tr'))
    // O12: hiç klasör yokken "Başka klasör seç..." değil "Klasör seç...".
    merged.push({ key: 'add', projectId: null, path: null, label: t(merged.length === 0 ? 'addFolderItemFirst' : 'addFolderItem'), hint: '', lastMs: -Infinity, isAdd: true })
    return merged
  })

  // Varsayılan olarak en son aktif gerçek projeyi otomatik seçer.
  let autoSelectedOnce = false
  $effect(() => {
    if (autoSelectedOnce) return
    const real = folderChoices.filter((x) => !x.isAdd && !isTestProject({ name: x.label, path: x.path }))
    if (real.length === 0) return
    const savedId = sf.projectId
    const savedPath = sf.selectedPath
    // Kaydedilmiş açık bir seçim varsa koru, yoksa en son aktif gerçek projeyi seç.
    let hasSaved = false
    try {
      hasSaved = !!(localStorage.getItem('soz.lastProject') || localStorage.getItem('soz.lastProjectPath'))
    } catch {
      hasSaved = false
    }
    // Kayıtlı seçim bir sınama klasörüyse yok sayılır (o seçimi kullanıcı değil, eski bir deneme bırakmıştır).
    const savedProj = savedId != null ? sf.projects.find((p) => p.id === savedId) : null
    const savedIsTest = (savedProj ? isTestProject(savedProj) : false) || (savedPath ? isTestProject({ path: savedPath }) : false)
    if (!hasSaved || (!savedId && !savedPath) || (savedIsTest && !sf.picked)) {
      const best = real[0]!
      autoSelectedOnce = true
      if (best.projectId != null) setProject(best.projectId)
      else if (best.path) selectProjectPath(best.path)
    } else {
      autoSelectedOnce = true
    }
  })

  const activeChoice = $derived.by<FolderChoice | null>(() => {
    if (sf.selectedPath && (sf.picked || !isTestProject({ path: sf.selectedPath }))) {
      const k = normPath(sf.selectedPath)
      const byP = folderChoices.find((x) => !x.isAdd && normPath(x.path) === k)
      if (byP) return byP
      const fallbackName = sf.selectedPath.split(/[\\/]/).filter(Boolean).pop() ?? sf.selectedPath
      return { key: 'sel-path', projectId: null, path: sf.selectedPath, label: fallbackName, hint: shortPath(sf.selectedPath), lastMs: 0 }
    }
    const cp = currentProject()
    if (cp) {
      return folderChoices.find((x) => x.projectId === cp.id) ?? { key: `p-${cp.id}`, projectId: cp.id, path: cp.path, label: cp.name, hint: cp.path ? shortPath(cp.path) : '', lastMs: 0 }
    }
    return folderChoices.find((x) => !x.isAdd && !isTestProject({ name: x.label, path: x.path })) ?? null
  })

  function autosize() {
    const el = ta
    if (!el) return
    el.style.height = 'auto'
    el.style.height = Math.min(el.scrollHeight, 150) + 'px'
  }

  $effect(() => {
    void sf.draft
    autosize()
  })

  // Kutudaki yazı değişince "bu iş gibi görünmüyor" cevabı kaybolur.
  $effect(() => {
    if (sf.notJob && sf.draft.trim() !== sf.notJob.text) sf.notJob = null
  })

  let secretWarning = $state<string | null>(null)
  $effect(() => {
    const text = sf.draft
    if (!text || text.trim().length < 12) { secretWarning = null; return }
    const h = setTimeout(async () => {
      try { secretWarning = (await checkJobText(text)).secretWarning ?? null } catch { secretWarning = null }
    }, 600)
    return () => clearTimeout(h)
  })

  let ilkAcilis = true
  $effect(() => {
    const istek = sf.focusNew + router.yeniTick + app.focusNewJob
    const ilk = ilkAcilis
    ilkAcilis = false
    if (ilk && istek === 0) return
    ta?.focus()
  })

  function onKey(e: KeyboardEvent) {
    if (e.isComposing) return
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault()
      void startDraft()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      ta?.blur()
      sf.focusList++
    }
  }

  function openMenu() {
    sf.folderMenuOpen = true
    menuIdx = Math.max(0, folderChoices.findIndex((x) => x.key === activeChoice?.key))
    queueMicrotask(() => menuEl?.focus())
  }
  function closeMenu(refocus = true) {
    sf.folderMenuOpen = false
    if (refocus) chipEl?.focus()
  }
  function choose(i: number) {
    const it = folderChoices[i]
    if (!it) return
    closeMenu(false)
    if (it.isAdd) addFolderFlow()
    else if (it.projectId != null) {
      setProject(it.projectId, true)
      ta?.focus()
    } else if (it.path) {
      selectProjectPath(it.path, true)
      ta?.focus()
    }
  }
  function onMenuKey(e: KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); menuIdx = (menuIdx + 1) % folderChoices.length }
    else if (e.key === 'ArrowUp') { e.preventDefault(); menuIdx = (menuIdx - 1 + folderChoices.length) % folderChoices.length }
    else if (e.key === 'Enter') { e.preventDefault(); choose(menuIdx) }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeMenu() }
    else if (e.key === 'Tab') closeMenu(false)
  }

  // ---- yardımcı menüsü (yalnız kurulu ve hazır olanlar, sabit yükseklik ile sıfır kayma) ----
  let agMenuEl: HTMLElement | undefined = $state()
  let agChipEl: HTMLButtonElement | undefined = $state()
  let agIdx = $state(0)
  const agItems = $derived(agentOptions())
  const canPickMultipleAgents = $derived(agItems.length > 1)

  function openAgent() {
    if (!canPickMultipleAgents) return
    sf.agentMenuOpen = true
    agIdx = Math.max(0, agItems.findIndex((x) => x.id === chosenAgent()))
    queueMicrotask(() => agMenuEl?.focus())
  }
  function closeAgent(refocus = true) {
    sf.agentMenuOpen = false
    if (refocus) agChipEl?.focus()
  }
  function chooseAgent(i: number) {
    const it = agItems[i]
    if (!it) return
    closeAgent(false)
    sf.agent = it.id
    ta?.focus()
  }
  function onAgentKey(e: KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); agIdx = (agIdx + 1) % agItems.length }
    else if (e.key === 'ArrowUp') { e.preventDefault(); agIdx = (agIdx - 1 + agItems.length) % agItems.length }
    else if (e.key === 'Enter') { e.preventDefault(); chooseAgent(agIdx) }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeAgent() }
    else if (e.key === 'Tab') closeAgent(false)
  }

  // Söyle (mikrofon): kabukta dikte köprüsünü çalıştırır, tarayıcıda Web Speech varsa sesten metne çevirir.
  function onSpeak() {
    if (shell) {
      dictate()
      ta?.focus()
      return
    }
    const SR = (window as unknown as { SpeechRecognition?: new () => any; webkitSpeechRecognition?: new () => any }).SpeechRecognition
      ?? (window as unknown as { webkitSpeechRecognition?: new () => any }).webkitSpeechRecognition
    if (!SR) {
      toast(t('suAnDictateUnsupported'))
      ta?.focus()
      return
    }
    if (listening) return
    try {
      const rec = new SR()
      rec.lang = app.lang === 'en' ? 'en-US' : 'tr-TR'
      rec.interimResults = false
      listening = true
      toast(t('suAnDictateListening'))
      rec.onresult = (ev: any) => {
        const transcript = String(ev?.results?.[0]?.[0]?.transcript ?? '').trim()
        if (transcript) sf.draft = sf.draft ? `${sf.draft.trimEnd()} ${transcript}` : transcript
      }
      rec.onerror = () => { listening = false }
      rec.onend = () => { listening = false; ta?.focus() }
      rec.start()
    } catch {
      listening = false
      toast(t('suAnDictateUnsupported'))
    }
  }

  function onWin(e: MouseEvent) {
    const n = e.target as Node
    if (sf.agentMenuOpen && !(agMenuEl?.contains(n) || agChipEl?.contains(n))) sf.agentMenuOpen = false
    if (!sf.folderMenuOpen) return
    if (menuEl?.contains(n) || chipEl?.contains(n)) return
    sf.folderMenuOpen = false
  }

  $effect(() => {
    if (sf.folderMenuOpen && document.activeElement !== menuEl) queueMicrotask(() => menuEl?.focus())
  })
</script>

<svelte:window onmousedown={onWin} />

<form class="bar" class:on={focused} onsubmit={(e) => { e.preventDefault(); void startDraft() }} aria-label={t('barLabel')}>
  <p class="hd" data-bar-head><label for="newjob">{t('barHead')}</label><span class="hint muted">{t('barHint')}</span></p>
  <div class="box">
    <!-- D7: DOM sırası ekrandaki sırayla aynı (klasör, yardımcı, yazı, seçenekler, Başlat): Tab sırası da öyle gider. -->
    <div class="folder">
      <button type="button" class="chip-btn" bind:this={chipEl} onclick={() => (sf.folderMenuOpen ? closeMenu(false) : openMenu())} aria-haspopup="listbox" aria-expanded={sf.folderMenuOpen} aria-label={activeChoice ? `${t('barFolderMenu')}: ${activeChoice.label}` : t('barFolder')} data-folder>
        <Icon name="folder" size={16} /><span class="fn">{activeChoice ? activeChoice.label : t('barFolder')}</span><Icon name="chevron" size={14} />
      </button>
      {#if sf.folderMenuOpen}
        <ul class="menu" role="listbox" tabindex="-1" bind:this={menuEl} onkeydown={onMenuKey} aria-label={t('barFolderMenu')} aria-activedescendant={`fm-${menuIdx}`}>
          {#each folderChoices as it, i (it.key)}
            <li id="fm-{i}" role="option" aria-selected={!it.isAdd && it.key === activeChoice?.key} class:cur={i === menuIdx} class:add={!!it.isAdd} onclick={() => choose(i)} onkeydown={() => {}} onmousemove={() => (menuIdx = i)}>
              {#if it.isAdd}<Icon name="plus" size={14} />{:else if it.key === activeChoice?.key}<Icon name="check" size={14} />{:else}<span class="sp"></span>{/if}
              <span class="fitem">
                <span class="fname">{it.label}</span>
                {#if it.hint}<span class="fhint">{it.hint}</span>{/if}
              </span>
            </li>
          {/each}
        </ul>
      {/if}
    </div>

    <div class="folder agent">
      {#if canPickMultipleAgents}
        <button type="button" class="chip-btn" bind:this={agChipEl} onclick={() => (sf.agentMenuOpen ? closeAgent(false) : openAgent())} aria-haspopup="listbox" aria-expanded={sf.agentMenuOpen} aria-label={`${t('barAgentMenu')}: ${agentName(chosenAgent())}`} data-agent-chip>
          <Icon name="terminal" size={16} /><span class="fn">{agentName(chosenAgent())}</span><Icon name="chevron" size={14} />
        </button>
        {#if sf.agentMenuOpen}
          <ul class="menu" role="listbox" tabindex="-1" bind:this={agMenuEl} onkeydown={onAgentKey} aria-label={t('barAgentMenu')} aria-activedescendant={`am-${agIdx}`}>
            {#each agItems as it, i (it.id)}
              <li id="am-{i}" role="option" aria-selected={it.id === chosenAgent()} class:cur={i === agIdx} onclick={() => chooseAgent(i)} onkeydown={() => {}} onmousemove={() => (agIdx = i)} data-agent-option={it.id} data-agent-open="1">
                {#if it.id === chosenAgent()}<Icon name="check" size={14} />{:else}<span class="sp"></span>{/if}
                <span>{it.name}</span>
              </li>
            {/each}
          </ul>
        {/if}
      {:else}
        <span class="chip-static" aria-label={`${t('barAgent')}: ${agentName(chosenAgent())}`} data-agent-chip>
          <Icon name="terminal" size={16} /><span class="fn">{agentName(chosenAgent())}</span>
        </span>
      {/if}
    </div>

    <textarea id="newjob" rows="1" bind:this={ta} bind:value={sf.draft} onkeydown={onKey} onfocus={() => (focused = true)} onblur={() => (focused = false)} placeholder={t('barPlaceholder')} aria-describedby="newjob-kb" data-newjob></textarea>

    <!-- O12: "Uzun sürecek iş" bir açma kapama anahtarıdır: durumu düğmenin üstünde yazar (Açık / Kapalı). -->
    <button type="button" class="btn btn-quiet ib night" class:on={sf.night} role="switch" aria-checked={sf.night} onclick={() => { sf.night = !sf.night; ta?.focus() }} title={t('barNightHelp')} aria-describedby={sf.night ? 'night-says' : undefined} data-night>
      <Icon name="moon" size={16} /><span class="nt">{t('barNightShort')}</span><span class="nstate" data-night-state>{sf.night ? t('nightOn') : t('nightOff')}</span>
    </button>
    <button type="button" class="btn btn-quiet ib speak" class:on={listening} onclick={onSpeak} aria-label={t('barDictate')} data-dictate>
      <Icon name="mic" size={16} /><span class="sp-txt">{t('barDictate')}</span>
    </button>
    <!-- D2: "Bitti sayılması için" kartı açıkken kartın kendi "İşi başlat"ı var: üstteki Başlat gizlenir. -->
    {#if sf.doneRaw === null}
      <button type="submit" class="btn btn-primary go" class:soft={startBlocked()} title={t('barStartTip')} aria-describedby={startBlocked() ? 'ready-says' : undefined} data-give>
        <span class="gt">{t('barStart')}</span><Icon name="send" size={16} />
      </button>
    {/if}
  </div>
  <!-- D1: kısayol ekranda görünür (Enter yeni satır açar). -->
  <p class="kbhint" id="newjob-kb" data-kb-hint>{t('barKbHint')}</p>
  <NotJobNote {ta} />
  {#if secretWarning}<p class="small secretnote" role="status" data-secret-warning><Icon name="alert" size={14} />{secretWarning}</p>{/if}
  {#if sf.night}<p class="small nightnote" id="night-says" role="status" data-night-says><Icon name="moon" size={14} />{t('nightOnSays')}</p>{/if}
  <StartStrip />
</form>

<style>
  /* Üst çubuk yüksekliği sabittir (min-height): ajan sürümü yüklenirken düzen kayması (CLS) olmaz. */
  /* A2: yerleşim kutunun kendi genişliğine göre seçilir (kapsayıcı sorgusu): 1000 px altında yazı alanı kendi satırına iner, ezilmez. */
  .bar { flex: none; padding: 10px 16px; border-bottom: 1px solid var(--line); background: var(--bg); min-height: 62px; container-type: inline-size; }
  .kbhint { margin: 3px 6px 0; text-align: right; font-size: var(--fs-xs); color: var(--ink-3); }
  .nstate { margin-left: 2px; padding: 0 6px; border-radius: var(--radius-sm); background: var(--bg-sunken); font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-2); }
  .night.on .nstate { background: var(--ink); color: var(--bg); }
  .hd { display: flex; flex-wrap: wrap; align-items: baseline; gap: 2px 10px; margin: 0 4px 6px; }
  .hd label { font-family: var(--display); font-size: var(--fs-sm); font-weight: var(--fw-strong); color: var(--ink); }
  .hint { font-size: var(--fs-xs); }
  .box { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 6px; min-height: 42px; padding: 4px; border: 1px solid var(--line-strong); border-radius: var(--radius-lg); background: var(--surface); transition: border-color var(--dur-1) var(--ease); }
  .bar.on .box { border-color: var(--focus); }
  .folder { position: relative; flex: none; }
  .chip-btn, .chip-static { display: inline-flex; align-items: center; gap: 6px; min-height: var(--ctl); max-width: 12.5rem; padding: 0 10px; border: 0; border-radius: var(--radius); background: var(--bg-sunken); color: var(--ink); font-weight: var(--fw-medium); font-size: var(--fs-sm); }
  .chip-btn:hover { background: var(--hover); }
  .chip-static { color: var(--ink-2); }
  .fn { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  textarea { flex: 1 1 18rem; min-width: min(18rem, 100%); resize: none; border: 0; outline: none; background: none; min-height: var(--ctl); max-height: 150px; padding: 5px 8px; line-height: 1.45; font-size: var(--fs-sm); }
  .go.soft { opacity: 0.62; }
  .fitem { display: flex; flex-direction: column; min-width: 0; padding: 3px 0; }
  .fname { font-weight: var(--fw-medium); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .fhint { font-size: var(--fs-xs); color: var(--ink-3); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .secretnote { display: flex; align-items: flex-start; gap: 8px; max-width: 64ch; margin: 8px 4px 0; color: var(--waiting); font-weight: var(--fw-medium); }
  .secretnote :global(svg) { flex: none; margin-top: 2px; }
  .nightnote { display: flex; align-items: flex-start; gap: 8px; max-width: 64ch; margin: 8px 4px 0; color: var(--ink-2); }
  .nightnote :global(svg) { flex: none; margin-top: 2px; }
  .ib { align-self: flex-start; gap: 5px; }
  .speak.on, .night.on { background: var(--sel-bg, var(--select)); color: var(--sel-fg, var(--ink)); outline: 1.5px solid var(--ink); outline-offset: -1.5px; }
  .go { flex: none; align-self: flex-start; gap: 6px; min-height: var(--ctl); }
  .menu { position: absolute; z-index: 30; top: calc(100% + 6px); left: 0; min-width: 15rem; max-width: min(22rem, calc(100vw - 24px)); max-height: 18rem; overflow: auto; padding: 4px; border: 1px solid var(--line-strong); border-radius: var(--radius-lg); background: var(--surface); box-shadow: var(--shadow); outline: none; animation: menu-in var(--dur-1) var(--ease); }
  .menu li { display: flex; align-items: center; gap: 8px; min-height: var(--ctl); padding: 2px 10px; border-radius: var(--radius); cursor: pointer; }
  .menu li.cur { background: var(--sel-bg, var(--select)); color: var(--sel-fg, var(--ink)); }
  .menu li.cur .fhint { color: var(--sel-fg2, var(--ink-2)); }
  .menu li.add { border-top: 1px solid var(--line); border-radius: 0 0 var(--radius) var(--radius); margin-top: 4px; }
  .sp { width: 14px; flex: none; }
  @keyframes menu-in { from { opacity: 0; transform: translateY(-4px); } to { opacity: 1; transform: none; } }

  @media (max-width: 759px) {
    .bar { padding: 8px 10px; min-height: 92px; }
  }
  @container (max-width: 999px) {
    .box { flex-wrap: wrap; row-gap: 6px; }
    .folder { flex: 0 1 auto; max-width: calc(50% - 4px); min-width: 0; }
    .chip-btn, .chip-static { max-width: 100%; }
    textarea { flex: 1 1 100%; min-height: 46px; font-size: var(--fs-sm); }
    .night { margin-right: auto; }
    .go { padding: 0 12px; }
  }
  @media (pointer: coarse) and (hover: none) {
    .kbhint { display: none; }
  }
  @media (pointer: coarse) and (hover: none) {
    textarea { font-size: var(--fs-md); }
  }
</style>
