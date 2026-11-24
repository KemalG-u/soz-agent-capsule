<script lang="ts">
  import { onMount, tick } from 'svelte'
  import { t, type Key } from '../i18n/index.svelte'
  import { go, type Route } from '../router.svelte'
  import { addFolderFlow } from '../shell'
  import { THEME_FAMILIES, app, saveThemeChoice, toast } from '../store.svelte'
  import type { ThemeFamily } from '../types'
  import { bucketMeta, bucketOf, realProjects, select, setProject, sf, currentProject } from '../surface.svelte'
  import Icon, { type IconName } from './Icon.svelte'

  type Cmd = { id: string; group: 'palActions' | 'palJobs' | 'palPages' | 'palLook'; label: string; icon: IconName; hint?: string; keys?: string; run: () => void }

  let q = $state('')
  let idx = $state(0)
  let stage = $state<'root' | 'folders'>('root')
  let input: HTMLInputElement | undefined = $state()
  let listEl: HTMLElement | undefined = $state()
  let before: Element | null = null

  const norm = (s: string) => s.toLocaleLowerCase('tr').replace(/ı/g, 'i').normalize('NFD').replace(/\p{M}/gu, '')

  function close(restore = true) {
    sf.paletteOpen = false
    if (restore && before instanceof HTMLElement) before.focus()
  }
  const exec = (fn: () => void) => () => {
    close(false)
    fn()
  }

  function page(r: Route) {
    go(r)
  }

  async function theme(th: 'system' | 'light' | 'dark') {
    toast((await saveThemeChoice({ theme: th })) ? t('saved') : t('themeNotSaved'))
  }

  async function family(f: ThemeFamily) {
    toast((await saveThemeChoice({ themeFamily: f })) ? t('saved') : t('themeNotSaved'))
  }

  const familyCmds = $derived<Cmd[]>(
    THEME_FAMILIES.map((f) => ({
      id: `tf-${f}`,
      group: 'palLook' as const,
      label: `${t('thFamilyTitle')}: ${t(`th_${f}` as Key)}`,
      icon: 'spark' as IconName,
      hint: app.themeFamily === f ? t('palCurrent') : t(`th_${f}_desc` as Key),
      run: exec(() => void family(f)),
    })),
  )

  const base = $derived<Cmd[]>([
    { id: 'new', group: 'palActions', label: t('palNewJob'), icon: 'plus', keys: 'n', run: exec(() => { go('suan'); sf.focusNew++ }) },
    { id: 'addf', group: 'palActions', label: t('palAddFolder'), icon: 'folder', run: exec(() => { go('suan'); addFolderFlow() }) },
    { id: 'swf', group: 'palActions', label: t('palSwitchFolder'), icon: 'folder', keys: 'f', run: () => { stage = 'folders'; q = ''; idx = 0 } },
    { id: 'p-suan', group: 'palPages', label: t('navSuAn'), icon: 'toparla', run: exec(() => page('suan')) },
    { id: 'p-proje', group: 'palPages', label: t('navProjects'), icon: 'folder', run: exec(() => page('projelerim')) },
    { id: 'p-is', group: 'palPages', label: t('tabGivenJobs'), icon: 'isler', run: exec(() => page('is')) },
    { id: 'p-sira', group: 'palPages', label: t('navSira'), icon: 'inbox', run: exec(() => page('sira')) },
    { id: 'p-sabah', group: 'palPages', label: t('tabHistorySummary'), icon: 'sun', run: exec(() => page('sabah')) },
    { id: 'p-neoldu', group: 'palPages', label: t('tabHistoryEvents'), icon: 'clock', run: exec(() => page('neoldu')) },
    { id: 'p-bekci', group: 'palPages', label: t('navGuard'), icon: 'bekci', run: exec(() => page('bekci')) },
    { id: 'p-harcama', group: 'palPages', label: t('navSpend'), icon: 'harcama', run: exec(() => page('harcama')) },
    { id: 'p-defter', group: 'palPages', label: t('navLedger'), icon: 'defter', run: exec(() => page('defter')) },
    { id: 'p-ara', group: 'palPages', label: t('navSearch'), icon: 'ara', keys: '/', run: exec(() => { page('ara'); app.focusSearch++ }) },
    { id: 'p-oneri', group: 'palPages', label: t('navSuggest'), icon: 'spark', run: exec(() => page('oneriler')) },
    { id: 'p-tanis', group: 'palPages', label: t('navKnow'), icon: 'person', run: exec(() => page('taniyorum')) },
    { id: 'p-ayar', group: 'palPages', label: t('navSettings'), icon: 'ayarlar', run: exec(() => page('ayarlar')) },
    ...familyCmds,
    { id: 't-sys', group: 'palLook', label: t('palThemeSystem'), icon: 'globe', hint: app.theme === 'system' ? t('palCurrent') : undefined, run: exec(() => void theme('system')) },
    { id: 't-light', group: 'palLook', label: t('palThemeLight'), icon: 'globe', hint: app.theme === 'light' ? t('palCurrent') : undefined, run: exec(() => void theme('light')) },
    { id: 't-dark', group: 'palLook', label: t('palThemeDark'), icon: 'moon', hint: app.theme === 'dark' ? t('palCurrent') : undefined, run: exec(() => void theme('dark')) },
    { id: 'help', group: 'palActions', label: t('palHelp'), icon: 'keyboard', keys: '?', run: exec(() => (app.helpOpen = true)) },
  ])

  const jobCmds = $derived<Cmd[]>(
    sf.jobs
      .filter((j) => j.id > 0)
      .map((j) => ({
        id: `job-${j.id}`, group: 'palJobs' as const, label: j.title, icon: bucketMeta[bucketOf(j)].icon, hint: `${t(bucketMeta[bucketOf(j)].key)} · ${j.projectName ?? ''}`,
        run: exec(() => { go('is', String(j.id)); select(j.id, { detail: true }) }), // adresle aynı karar yolu (Surface isSecim)
      })),
  )

  const folderCmds = $derived<Cmd[]>(
    realProjects().map((p) => ({
      id: `f-${p.id}`, group: 'palActions' as const, label: p.name, icon: 'folder' as IconName, hint: p.id === currentProject()?.id ? t('palCurrent') : (p.path ?? ''),
      run: exec(() => { setProject(p.id, true); sf.focusNew++ }),
    })),
  )

  const results = $derived.by(() => {
    const terms = norm(q).split(/\s+/).filter(Boolean)
    const pool = stage === 'folders' ? folderCmds : [...base, ...jobCmds]
    const hit = (c: Cmd) => terms.every((w) => norm(`${c.label} ${c.hint ?? ''}`).includes(w))
    let list = terms.length ? pool.filter(hit) : stage === 'folders' ? pool : [...base, ...jobCmds.slice(0, 5)]
    const order = ['palActions', 'palJobs', 'palPages', 'palLook']
    const first = terms[0] ?? ''
    const rank = (c: Cmd) => { const l = norm(c.label); return first && l === norm(q).trim() ? 0 : first && l.startsWith(first) ? 1 : 2 }
    list = [...list].sort((a, b) => rank(a) - rank(b) || order.indexOf(a.group) - order.indexOf(b.group))
    return list.slice(0, 60)
  })

  $effect(() => {
    void q
    void stage
    idx = 0
  })

  onMount(() => {
    before = document.activeElement
    input?.focus()
  })

  async function move(d: number) {
    const n = results.length
    if (!n) return
    idx = (idx + d + n) % n
    await tick()
    document.getElementById(`pal-${idx}`)?.scrollIntoView({ block: 'nearest' })
  }

  function onKey(e: KeyboardEvent) {
    if (e.isComposing) return
    if (e.key === 'ArrowDown' || (e.ctrlKey && e.key === 'n')) { e.preventDefault(); void move(1) }
    else if (e.key === 'ArrowUp' || (e.ctrlKey && e.key === 'p')) { e.preventDefault(); void move(-1) }
    else if (e.key === 'Enter') { e.preventDefault(); results[idx]?.run() }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); if (stage === 'folders') { stage = 'root'; q = '' } else close() }
    else if (e.key === 'Backspace' && q === '' && stage === 'folders') { stage = 'root' }
    else if (e.key === 'Tab') e.preventDefault()
  }
</script>

<div class="scrim" role="presentation" onmousedown={(e) => { if (e.target === e.currentTarget) close() }}>
  <div class="pal" role="dialog" aria-modal="true" aria-label={t('palTitle')} data-palette>
    <div class="q">
      <Icon name={stage === 'folders' ? 'folder' : 'command'} size={18} />
      <input bind:this={input} bind:value={q} onkeydown={onKey} role="combobox" aria-expanded="true" aria-controls="pal-list" aria-activedescendant={results.length ? `pal-${idx}` : undefined} autocomplete="off" spellcheck="false"
        placeholder={stage === 'folders' ? t('palFolders') : t('palPlaceholder')} aria-label={t('palTitle')} data-palette-input />
      <kbd>Esc</kbd>
    </div>
    <ul class="res" id="pal-list" role="listbox" bind:this={listEl}>
      {#each results as c, i (c.id)}
        {#if i === 0 || results[i - 1].group !== c.group}
          <li class="gh" role="presentation">{t(c.group)}</li>
        {/if}
        <li id="pal-{i}" role="option" aria-selected={i === idx} class:cur={i === idx} onclick={() => c.run()} onkeydown={() => {}} onmousemove={() => (idx = i)} data-cmd={c.id}>
          <span class="ci"><Icon name={c.icon} size={16} /></span>
          <span class="cl">{c.label}</span>
          {#if c.hint}<span class="ch">{c.hint}</span>{/if}
          {#if c.keys}<kbd>{c.keys}</kbd>{/if}
        </li>
      {/each}
      {#if results.length === 0}<li class="none" role="presentation">{t('palNone')}</li>{/if}
    </ul>
  </div>
</div>

<style>
  .scrim { position: fixed; inset: 0; z-index: 60; display: flex; justify-content: center; align-items: flex-start; padding: min(14vh, 120px) 12px 12px; background: var(--scrim); animation: fade var(--dur-1) var(--ease); }
  .pal { width: min(36rem, 100%); max-height: min(70vh, 30rem); display: flex; flex-direction: column; border: 1px solid var(--line-strong); border-radius: var(--radius-lg); background: var(--surface); box-shadow: var(--shadow); overflow: hidden; animation: pop var(--dur-2) var(--ease); }
  .q { display: flex; align-items: center; gap: 10px; padding: 0 14px; border-bottom: 1px solid var(--line); color: var(--ink-3); }
  .q input { flex: 1; min-width: 0; min-height: var(--row-m); border: 0; outline: none; background: none; color: var(--ink); font-size: var(--fs-md); }
  .res { overflow-y: auto; padding: 4px 6px 8px; }
  .gh { padding: 8px 10px 4px; font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-3); }
  .res li[role='option'] { display: flex; align-items: center; gap: 10px; min-height: 36px; padding: 4px 10px; border-radius: var(--radius); cursor: pointer; }
  .res li.cur { background: var(--sel-bg, var(--select)); color: var(--sel-fg, var(--ink)); }
  .res li.cur .ci, .res li.cur .ch { color: var(--sel-fg2, var(--ink-2)); }
  .res li.cur kbd { background: var(--surface); color: var(--ink); }
  .ci { flex: none; display: inline-flex; color: var(--ink-3); }
  .cl { flex: 0 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .ch { flex: 1; min-width: 0; text-align: right; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--fs-xs); color: var(--ink-3); }
  li[role='option'] kbd { margin-left: auto; }
  .ch + kbd { margin-left: 0; }
  .none { padding: 16px 12px; color: var(--ink-3); }
  @keyframes fade { from { opacity: 0; } to { opacity: 1; } }
  @keyframes pop { from { opacity: 0; transform: translateY(-6px) scale(0.985); } to { opacity: 1; transform: none; } }
  @media (max-width: 759px) {
    .scrim { padding-top: 8vh; }
  }
  @media (pointer: coarse) and (hover: none) {
    .res li[role='option'] { min-height: 44px; }
  }
</style>
