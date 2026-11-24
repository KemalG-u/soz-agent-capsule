<script lang="ts">
  import { onMount, untrack } from 'svelte'
  import { createCheckFromTemplate, getCheckTemplates, getChecks } from '../api2'
  import { approveSetup } from '../api3'
  import { t } from '../i18n/index.svelte'
  import { rowIn } from '../motion'
  import { app, toast } from '../store.svelte'
  import { closeSetup, sf } from '../surface.svelte'
  import type { Check, CheckTemplate, ProjectAddResult } from '../types'
  import { errText, specSummary } from '../util'
  import Icon from './Icon.svelte'
  import { sadeKomut, testKomutuMu } from '../gezinme'

  let { setup }: { setup: ProjectAddResult } = $props()
  const project = $derived(setup)

  let phase = $state<'loading' | 'suggest' | 'templates' | 'error'>('loading')
  let suggested = $state<Check[]>([])
  let templates = $state<CheckTemplate[]>([])
  let picked = $state<Record<string, boolean>>({})
  let values = $state<Record<string, Record<string, string>>>({})
  let busy = $state(false)
  let error = $state('')
  let primary: HTMLButtonElement | undefined = $state()
  let laterBtn: HTMLButtonElement | undefined = $state()
  let root: HTMLElement | undefined = $state()

  onMount(async () => {
    try {
      let list = setup.suggested.filter((c) => !c.enabled)
      if (list.length === 0) list = setup.suggested
      if (list.length > 0) {
        suggested = list
        phase = 'suggest'
        return
      }
      const tp = await getCheckTemplates(project.id)
      templates = tp.templates.filter((x) => x.available)
      for (const x of templates) values[x.id] = Object.fromEntries(x.fields.map((f) => [f.name, f.default]))
      phase = 'templates'
    } catch (e) {
      error = errText(e)
      phase = 'error'
    }
  })

  const anyPicked = $derived(templates.some((x) => picked[x.id]))
  // Ana düğme pasifse (hiçbir cümle seçilmedi) odak "Kanıtsız devam et"e gider; klavye hiçbir durumda boşta kalmaz.
  const showPrimary = $derived(phase === 'suggest' || (phase === 'templates' && templates.length > 0))
  const primaryDisabled = $derived(busy || (phase === 'templates' && !anyPicked))
  function focusAction() {
    const el = primary && !primary.disabled ? primary : laterBtn
    el?.focus()
  }
  // Kart hazır olunca odak: Enter ile onaylanır (ana düğme pasifse "Kanıtsız devam et").
  $effect(() => {
    if (phase !== 'loading') queueMicrotask(() => untrack(focusAction))
  })
  let seenFocus = sf.focusSetup
  $effect(() => {
    const n = sf.focusSetup
    if (n === seenFocus) return
    seenFocus = n
    untrack(focusAction)
  })

  async function openAll() {
    if (busy) return
    busy = true
    error = ''
    try {
      if (phase === 'suggest') {
        await approveSetup(project.id, { checkIds: suggested.map((c) => c.id), gate: true, guardTemplate: 'temkinli' })
      } else {
        for (const x of templates.filter((x) => picked[x.id])) {
          await createCheckFromTemplate({ projectId: project.id, template: x.id, values: values[x.id] ?? {}, enabled: true })
        }
      }
      if (phase !== 'suggest') await approveSetup(project.id, { gate: true, guardTemplate: 'temkinli' })
      sf.checks[project.id] = await getChecks(project.id)
      toast(t('setupDone'))
      closeSetup()
    } catch (e) {
      error = errText(e)
    } finally {
      busy = false
    }
  }

  function later() {
    toast(t('setupSkipped'))
    closeSetup(true)
  }

  /** Esc ya da Vazgeç: kart kapanır, iş BAŞLAMAZ, yazı kutusundaki taslak durur. */
  function cancel() {
    toast(t('setupCancelled'))
    closeSetup(false)
  }

  // Enter ve Esc pencere düzeyinde çalışır: odak neredeyse orada. Düğme, bağlantı ve yazı kutusundaki Enter kendi işini yapar.
  function onWinKey(e: KeyboardEvent) {
    if (e.isComposing || phase === 'loading' || sf.paletteOpen || app.helpOpen || sf.folderMenuOpen || sf.addFolderOpen) return
    if (e.key === 'Escape') {
      e.preventDefault()
      e.stopPropagation()
      cancel()
      return
    }
    if (e.key !== 'Enter' || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return
    const el = e.target
    if (el instanceof HTMLElement) {
      const tag = el.tagName
      if (tag === 'BUTTON' || tag === 'A' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable || (tag === 'INPUT' && (el as HTMLInputElement).type !== 'checkbox')) return
    }
    e.preventDefault()
    e.stopPropagation()
    if (showPrimary && !primaryDisabled) void openAll()
    else later()
  }
  onMount(() => {
    window.addEventListener('keydown', onWinKey, true)
    return () => window.removeEventListener('keydown', onWinKey, true)
  })

</script>

<section class="card" bind:this={root} role="group" aria-labelledby="sc-t" in:rowIn data-setup>
  <p class="added small muted"><Icon name="folder" size={14} />{t('setupAdded', { name: project.name })}</p>

  {#if phase === 'loading'}
    <h2 class="ttl" id="sc-t">{t('setupLooking')}</h2>
    <p class="skel" style="width: 60%; height: 1em"></p>
  {:else if phase === 'suggest'}
    <h2 class="ttl" id="sc-t">{t('setupTitle')}</h2>
    <ul class="cmds">
      {#each suggested as c (c.id)}
        {@const komut = specSummary(c.kind, c.spec)}
        <!-- O12: test komutu kod sözcüğüyle değil "Klasörün kendi testleri" diye söylenir, komut ipucunda kalır. -->
        <li><span class="ok"><Icon name="check" size={14} /></span>{#if testKomutuMu(komut)}<span class="plain" title={komut} data-test-cmd>{t('testCmdPlain')}</span>{:else}<code class="mono">{komut}</code>{/if}<span class="lbl muted small">{sadeKomut(c.label, t('testCmdPlain'))}</span></li>
      {/each}
    </ul>
    <p class="ask muted">{t('setupAsk')}</p>
    <p class="ask muted">{t('setupGuard')}</p>
  {:else if phase === 'templates'}
    <h2 class="ttl" id="sc-t">{t('setupNoneTitle')}</h2>
    {#if templates.length > 0}
      <p class="ask muted">{t('setupPick')}</p>
      <ul class="tpls">
        {#each templates as x (x.id)}
          <li>
            <label class="tpl"><input type="checkbox" bind:checked={picked[x.id]} /><span>{x.sentence}</span></label>
            {#if picked[x.id] && x.fields.length > 0}
              <div class="flds">
                {#each x.fields as f (f.name)}
                  <input class="input" aria-label={f.label} placeholder={f.placeholder} bind:value={values[x.id][f.name]} />
                {/each}
              </div>
            {/if}
          </li>
        {/each}
      </ul>
    {:else}
      <p class="ask muted">{t('setupNoneBody')}</p>
    {/if}
  {:else}
    <h2 class="ttl" id="sc-t">{t('setupLooking')}</h2>
  {/if}

  {#if error}<p class="err-line small" role="alert">{error}</p>{/if}

  {#if phase !== 'loading'}
    <div class="act">
      {#if showPrimary}
        <button class="btn btn-primary btn-lg" bind:this={primary} onclick={openAll} disabled={primaryDisabled} data-open-all>
          {phase === 'suggest' ? t('setupOpenAll') : t('setupOpenPicked')}<kbd>Enter</kbd>
        </button>
        <button class="btn btn-lg" bind:this={laterBtn} onclick={later} data-later>{t('setupLater')}</button>
      {:else}
        <button class="btn btn-primary btn-lg" bind:this={laterBtn} onclick={later} data-later>{t('setupLater')}<kbd>Enter</kbd></button>
      {/if}
      <button class="btn btn-quiet btn-lg" onclick={cancel} data-cancel>{t('setupCancel')}<kbd>Esc</kbd></button>
    </div>
    <p class="small muted">{t('setupLaterNote')}</p>
  {/if}
</section>

<style>
  .card { display: flex; flex-direction: column; gap: 12px; padding: 18px 20px 16px; border: 1px solid var(--line-strong); border-left: 3px solid var(--working); border-radius: var(--radius-lg); background: var(--surface); box-shadow: var(--shadow); max-width: 40rem; }
  .added { display: inline-flex; align-items: center; gap: 6px; }
  .ttl { font-size: var(--fs-md); font-weight: var(--fw-strong); line-height: 1.35; }
  .ask { font-size: var(--fs-sm); }
  .cmds { display: flex; flex-direction: column; border: 1px solid var(--line); border-radius: var(--radius); background: var(--bg); }
  .cmds li { display: grid; grid-template-columns: 20px minmax(0, auto) 1fr; align-items: center; gap: 8px; padding: 8px 12px; }
  .cmds li + li { border-top: 1px solid var(--line); }
  .cmds code { font-size: var(--fs-sm); }
  .cmds .plain { font-size: var(--fs-sm); font-weight: var(--fw-medium); }
  .ok { color: var(--ok); display: inline-flex; }
  .lbl { text-align: right; }
  .tpls { display: flex; flex-direction: column; gap: 4px; }
  .tpl { display: flex; align-items: center; gap: 10px; min-height: var(--ctl); cursor: pointer; }
  .tpl input { width: 24px; height: 24px; flex: none; }
  .flds { display: flex; flex-direction: column; gap: 6px; margin: 2px 0 8px 28px; }
  .act { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 4px; }
  .act kbd { margin-left: 4px; background: transparent; border-color: currentColor; color: inherit; opacity: 0.75; }
  @media (max-width: 560px) {
    .cmds li { grid-template-columns: 20px 1fr; }
    .lbl { grid-column: 2; text-align: left; }
    .act .btn { flex: 1 1 100%; }
  }
</style>
