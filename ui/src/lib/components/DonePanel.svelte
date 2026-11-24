<script lang="ts">
  import { onMount, tick, untrack } from 'svelte'
  import { acceptDone, getDoneLevels, getDoneQuestions, suggestDone } from '../api5'
  import { ApiError } from '../errors'
  import { sadeKomut } from '../gezinme'
  import { hataMetni } from '../hata'
  import { t } from '../i18n/index.svelte'
  import { rowIn } from '../motion'
  import { toast } from '../store.svelte'
  import { currentProject, finishDone, loadChecks, sf } from '../surface.svelte'
  import type { DoneLevel, DoneProposal, DoneQuestion, DoneSuggestion } from '../types5'
  import ChecksPanel from './ChecksPanel.svelte'
  import Icon from './Icon.svelte'

  // "Bitti sayılması için": iş verilirken sistemin önerdiği ölçütler görünür; kişi kabul eder, çıkarır ya da ekler.
  // Öneriler taslaktır, kabul edilmeden hiçbir ölçüt eklenmez. Ölçüt türleri ve düzey adları sunucudan (kayıttan) gelir; arayüzde sabit liste yok.
  let { raw }: { raw: string } = $props()

  // Telefon genişliğinde kart kısa tutulur: gerekçe ve açıklama notları kapalı başlar, ana düğme altta sabit durur.
  // Pencere sonradan daralıp genişleyebilir (döndürme, yeniden boyutlama): kart buna uyar.
  const mq = typeof matchMedia !== 'undefined' ? matchMedia('(max-width: 559px)') : null
  let narrow = $state(mq?.matches ?? false)
  $effect(() => {
    if (!mq) return
    const on = () => (narrow = mq.matches)
    mq.addEventListener('change', on)
    return () => mq.removeEventListener('change', on)
  })

  const proj = $derived(currentProject())
  let loading = $state(true)
  let error = $state('')
  let sug = $state<DoneSuggestion | null>(null)
  let questions = $state<DoneQuestion[]>([])
  let levels = $state<DoneLevel[]>([])
  let levelNote = $state('')
  let kind = $state('')
  let irreversible = $state(false)
  let looks = $state('')
  let picked = $state<Record<string, boolean>>({})
  let vals = $state<Record<string, Record<string, string>>>({})
  /** "Bu klasörde hep böyle olsun": işaretliyse kapsam klasör, değilse yalnız bu iş. */
  let always = $state(false)
  /** 'soz': Söz hazır denemeyi kendisi çalıştırır. 'ben': kişi bakıp karar verir, hiçbir kontrol eklenmez. */
  let mode = $state<'soz' | 'ben'>('soz')
  let savedPick: Record<string, boolean> = {}
  let busy = $state(false)
  let root: HTMLElement | undefined = $state()
  let primary: HTMLButtonElement | undefined = $state()
  let cancelBtn: HTMLButtonElement | undefined = $state()

  const kindQ = $derived(questions.find((q) => q.id === 'kind'))
  const irrQ = $derived(questions.find((q) => q.id === 'irreversible'))
  const looksQ = $derived(questions.find((q) => q.id === 'looks'))

  /** Belirsiz öneri: hangi dosya ya da adres olduğunu söylemeyen ("Şu dosya oluşsun ve boş olmasın") deneme işle ilgisiz olabilir; kendiliğinden seçilmez, kişi isterse işaretler. */
  const vague = (p: DoneProposal): boolean => (/^şu\s/i.test(p.sentence.trim()) && !shownValues(p)) || (p.fill.length > 0 && Object.values(p.values).every((v) => !v.trim()))
  /** Önerinin baktığı dosya ya da adres (değerlerin ilk satırı). Cümle "Şu dosya oluşsun" der, hangi dosya olduğu burada yazılır (S2-10). */
  function shownValues(p: DoneProposal): string {
    const v = ['path', 'url', 'file', 'heading'].map((k) => (p.values[k] ?? '').split('\n')[0]?.trim()).filter(Boolean)
    return v.join(', ')
  }
  const named = (p: DoneProposal): string => (shownValues(p) ? `${p.sentence} (${shownValues(p)})` : p.sentence)
  /** İş metni bu dosyadan söz etmiyorsa o deneme işle ilgisiz olabilir (iş öncesi de geçer): kendiliğinden seçilmez. */
  const unrelated = (p: DoneProposal): boolean => {
    const v = shownValues(p)
    if (!v) return false
    const r = raw.toLowerCase()
    return !v.split(', ').some((x) => r.includes(x.toLowerCase()) || r.includes((x.split(/[\\/]/).pop() ?? x).toLowerCase()))
  }
  const sure = (p: DoneProposal): boolean => p.ready && p.enabledByDefault && !vague(p) && !unrelated(p)

  /** Aynı cümleyle ve aynı değerle bu klasörde zaten açık bir ölçüt var mı (aynı ölçütü her işte yeniden eklememek için). */
  function have(p: DoneProposal): boolean {
    const list = sf.checks[proj?.id ?? -1] ?? []
    const first = Object.values(p.values).map((v) => v.split('\n')[0]).filter(Boolean)
    return list.some((c) => c.enabled && c.label.startsWith(p.sentence) && first.every((v) => c.label.includes(v)))
  }

  async function ask() {
    if (!proj) return
    loading = true
    error = ''
    try {
      const text = looks.trim() ? `${raw}\n${looks.trim()}` : raw
      const r = await suggestDone({ projectId: proj.id, text, ...(kind ? { kind } : {}), ...(irreversible ? { irreversible: true } : {}) })
      sug = r
      kind = r.jobKind
      const pk: Record<string, boolean> = {}
      const pv: Record<string, Record<string, string>> = {}
      for (const p of r.proposals) {
        pk[p.id] = sure(p) && !have(p)
        pv[p.id] = { ...p.values }
      }
      picked = pk
      vals = pv
      mode = r.proposals.some(sure) ? 'soz' : 'ben'
      savedPick = {}
    } catch (e) {
      // Bu sunucuda öneri ucu yoksa (eski sürüm) iş eskisi gibi başlar; başka hatada kişi sade cümleyi görür.
      if (e instanceof ApiError && e.status === 404) {
        void finishDone(true)
        return
      }
      sug = null
      error = hataMetni(e)
    } finally {
      loading = false
      await tick()
      untrack(focusAction)
    }
  }

  onMount(() => {
    void getDoneQuestions().then((r) => (questions = r.questions)).catch(() => {})
    void getDoneLevels().then((r) => { levels = r.levels; levelNote = r.note }).catch(() => {})
    void (async () => {
      if (proj) await loadChecks(proj.id)
      await ask()
    })()
  })

  function focusAction() {
    const el = primary && !primary.disabled ? primary : cancelBtn
    el?.focus()
  }

  /** Söz'ün kendi çalıştırabileceği hazır denemeler (sunucunun varsayılan işaretlediği, eksiksiz olanlar). Yoksa duvar çıkmaz: "ben bakarım" tek yol olur. */
  const autoProps = $derived(sug ? sug.proposals.filter(sure) : [])
  const hasAuto = $derived(autoProps.length > 0)
  // O12: "(npm test)" gibi komut kişiye "Klasörün kendi testleri" diye söylenir.
  const autoText = $derived(sadeKomut(autoProps.map((p) => named(p)).join(', '), t('testCmdPlain')))

  function setMode(m: 'soz' | 'ben') {
    if (!sug || m === mode) return
    const none = Object.fromEntries(sug.proposals.map((p) => [p.id, false]))
    if (m === 'ben') {
      savedPick = { ...picked }
      picked = none
    } else {
      picked = { ...none, ...savedPick }
    }
    mode = m
  }

  /** Kabul: "Yalnız bu iş" ise ölçütler iş oluşunca işe bağlanır, "Bu klasörde hep" ise şimdi klasöre eklenir. Arayüz kapsamı HER ZAMAN söyler. */
  async function accept(writeCheckFirst = false) {
    if (!proj || !sug || busy) return
    busy = true
    error = ''
    try {
      const items = sug.proposals.filter((p) => picked[p.id]).map((p) => ({ template: p.template, values: vals[p.id] ?? {}, enabled: true }))
      const opts = writeCheckFirst ? { writeCheckFirst: true } : {}
      if (items.length > 0 && always) {
        const r = await acceptDone(proj.id, items, 'project')
        await loadChecks(proj.id)
        toast(r.sentence ?? t('doneAddedN', { n: r.count }))
        await finishDone(true, opts)
      } else if (items.length > 0) {
        await finishDone(true, { ...opts, jobItems: items })
      } else {
        await finishDone(true, opts)
      }
    } catch (e) {
      error = hataMetni(e)
    } finally {
      busy = false
    }
  }

  function onKey(e: KeyboardEvent) {
    if (e.key === 'Escape' && !e.isComposing) {
      e.preventDefault()
      e.stopPropagation()
      void finishDone(false)
    }
  }

  const fieldLabel = (p: DoneProposal, name: string): string => p.fill.find((f) => f.field === name)?.label ?? (name === 'items' ? t('doneFieldItems') : name)
  const multi = (p: DoneProposal, name: string): boolean => name === 'items' || (p.values[name] ?? '').includes('\n')
  const levelCls = (l: string) => (l === 'A' ? 'chip-ok' : l === 'B' ? 'chip-working' : l === 'C' ? 'chip-waiting' : 'chip-unproven')
</script>

<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<section class="done" onkeydown={onKey} bind:this={root} aria-labelledby="done-t" data-done in:rowIn>
  <h2 class="h2" id="done-t">{t('doneTitle')}</h2>
  <blockquote class="voice raw" data-done-raw>{raw}</blockquote>

  {#if loading}
    <p class="muted" role="status"><span class="spin" aria-hidden="true"></span> {t('doneLoading')}</p>
  {:else if sug}
    {#if hasAuto}
      <fieldset class="opts" data-done-options>
        <legend class="sr-only">{t('doneTitle')}</legend>
        <label class="opt" class:on={mode === 'soz'}>
          <input type="radio" name="done-mode" value="soz" checked={mode === 'soz'} onchange={() => setMode('soz')} data-done-mode="soz" />
          <span class="otxt">
            <span class="ot">{t('doneOptSoz')}</span>
            <span class="small muted">{t('doneOptSozHint', { l: autoText })}</span>
          </span>
        </label>
        <label class="opt" class:on={mode === 'ben'}>
          <input type="radio" name="done-mode" value="ben" checked={mode === 'ben'} onchange={() => setMode('ben')} data-done-mode="ben" />
          <span class="otxt">
            <span class="ot">{t('doneOptMe')}</span>
            <span class="small muted">{t('doneOptMeHint')}</span>
          </span>
        </label>
      </fieldset>
    {:else}
      <p class="nowall" role="note" data-testless data-done-none><Icon name="info" size={16} /><span>{t('doneNothingFound')}</span></p>
    {/if}

    <details class="more" data-done-more>
      <summary class="small">{t('doneMore')}</summary>
      <div class="moreBody">
        {#if sug.testless}
          <div class="field">
            <button class="btn btn-sm" onclick={() => accept(true)} disabled={busy} data-testless-action={sug.testless.action}>{t('doneWriteCheck')}</button>
            <span class="small muted">{t('doneWriteCheckHint')}</span>
          </div>
        {/if}

        {#if sug.proposals.length > 0}
          <h3 class="mh">{t('doneListTitle')}</h3>
          <ul class="props" data-proposals>
            {#each sug.proposals as p (p.id)}
              {@const dup = have(p)}
              <li class="prop" class:off={!picked[p.id]} data-proposal={p.template} data-level={p.level}>
                <label class="pick">
                  <input type="checkbox" bind:checked={picked[p.id]} disabled={!p.ready} data-pick />
                  <span class="ptxt">
                    <span class="ps">{named(p)}</span>
                    <span class="pm">
                      {#if dup}<span class="small muted">{t('doneHave')}</span>{/if}
                      {#if !p.ready}<span class="small muted">{t('doneNotReady')}</span>{/if}
                      {#if p.ready && vague(p)}<span class="small muted" data-vague>{t('doneVague')}</span>{:else if p.ready && p.enabledByDefault && unrelated(p)}<span class="small muted" data-unrelated>{t('doneUnrelated')}</span>{/if}
                    </span>
                    {#if !narrow}<span class="small muted">{p.why}</span>{/if}
                  </span>
                </label>
                {#if narrow}<details class="why"><summary class="small muted">{t('doneWhy')}</summary><p class="small muted">{p.why}</p></details>{/if}
                {#if picked[p.id] && vals[p.id]}
                  <div class="vals">
                    {#each Object.keys(vals[p.id]) as name (name)}
                      {@const f = p.fill.find((x) => x.field === name)}
                      <div class="field">
                        <label class="label" for="dv-{p.id}-{name}">{fieldLabel(p, name)}</label>
                        {#if multi(p, name)}
                          <textarea id="dv-{p.id}-{name}" class="textarea" rows="3" bind:value={vals[p.id][name]}></textarea>
                        {:else}
                          <input id="dv-{p.id}-{name}" class="input" type="text" bind:value={vals[p.id][name]} list={f ? `dl-${p.id}-${name}` : undefined} spellcheck="false" />
                          {#if f}<datalist id="dl-{p.id}-{name}">{#each f.candidates as c (c)}<option value={c}></option>{/each}</datalist>{/if}
                        {/if}
                      </div>
                    {/each}
                  </div>
                {/if}
              </li>
            {/each}
          </ul>
        {/if}

        {#if proj}
          <details class="lv">
            <summary class="small">{t('doneOwn')}</summary>
            <ChecksPanel projectId={proj.id} onchange={() => void loadChecks(proj.id)} />
          </details>
        {/if}

        <div class="scope" data-scope data-scope-value={always ? 'project' : 'job'}>
          <label class="always"><input type="checkbox" bind:checked={always} data-scope-project /> {t('doneAlways')}</label>
          <span class="small muted" data-scope-chip>{always ? t('doneScopeProject') : t('doneScopeJob')}</span>
        </div>

        <details class="fix">
          <summary class="small">{t('doneFix')}</summary>
          <p class="small kind" data-done-kind>{t('doneKindIs')} <strong>{sug.jobKindLabel}</strong>{#if sug.found.length}<span class="muted">{' '}· {t('doneFound', { l: sug.found.join(', ') })}</span>{/if}</p>
          <div class="qs">
            {#if kindQ}
              <div class="field">
                <label class="label" for="dq-kind">{kindQ.text}</label>
                <select id="dq-kind" class="select" bind:value={kind} onchange={ask} data-done-kind-select>
                  {#each kindQ.options as o (o.value)}<option value={o.value}>{o.label}</option>{/each}
                </select>
                {#if kindQ.hint}<span class="small muted">{kindQ.hint}</span>{/if}
              </div>
            {/if}
            {#if irrQ}
              <div class="field">
                <span class="label" id="dq-irr">{irrQ.text}</span>
                <div class="switchrow">
                  <button type="button" class="switch" role="switch" aria-checked={irreversible} aria-labelledby="dq-irr" onclick={() => { irreversible = !irreversible; void ask() }} data-done-irreversible></button>
                  <span class="small muted">{irreversible ? t('doneYes') : t('doneNo')}</span>
                </div>
                {#if irrQ.hint}<span class="small muted">{irrQ.hint}</span>{/if}
              </div>
            {/if}
            {#if looksQ}
              <div class="field">
                <label class="label" for="dq-looks">{looksQ.text}</label>
                <textarea id="dq-looks" class="textarea" rows="2" bind:value={looks} onchange={ask}></textarea>
                {#if looksQ.hint}<span class="small muted">{looksQ.hint}</span>{/if}
              </div>
            {/if}
          </div>
        </details>

        <details class="lv">
          <summary class="small">{t('doneLevels')}</summary>
          <p class="small muted">{t('doneKeepNote')}</p>
          <ul class="small">{#each levels as l (l.level)}<li><span class="chip {levelCls(l.level)}">{l.label}</span> {l.meaning}</li>{/each}</ul>
          {#if levelNote}<p class="small muted">{levelNote}</p>{/if}
        </details>
      </div>
    </details>
  {/if}

  {#if error}<p class="err-line" role="alert" data-done-error>{error}</p>{/if}

  <div class="act">
    <button class="btn btn-primary" bind:this={primary} onclick={() => accept()} disabled={busy || loading || !sug} data-done-accept>{t('doneStart')}</button>
    <button class="btn btn-quiet" bind:this={cancelBtn} onclick={() => finishDone(false)} data-done-cancel>{t('cancel')}</button>
  </div>
</section>

<style>
  .done { display: flex; flex-direction: column; gap: 12px; max-width: 42rem; padding: 18px 20px; border: 1px solid var(--line-strong); border-radius: var(--radius-lg); background: var(--surface); box-shadow: var(--shadow); }
  .opts { display: flex; flex-direction: column; gap: 8px; margin: 0; padding: 0; border: 0; min-width: 0; }
  .opt { display: flex; align-items: flex-start; gap: 12px; padding: 12px 14px; border: 1px solid var(--line-strong); border-radius: var(--radius-lg); background: var(--surface); cursor: pointer; }
  .opt.on { border-color: var(--ink); box-shadow: inset 0 0 0 1px var(--ink); }
  .opt input { flex: none; width: 22px; height: 22px; margin-top: 1px; accent-color: var(--ink); }
  .otxt { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
  .ot { font-weight: var(--fw-strong); }
  .nowall { display: flex; align-items: flex-start; gap: 8px; margin: 0; padding: 12px 14px; border: 1px solid var(--line-strong); border-radius: var(--radius-lg); background: var(--bg); font-weight: var(--fw-medium); }
  .nowall :global(svg) { flex: none; margin-top: 3px; }
  .more > summary { cursor: pointer; min-height: 32px; display: flex; align-items: center; color: var(--ink-2); text-decoration: underline; text-underline-offset: 3px; }
  .more > summary::after { content: "\25BE"; margin-left: 6px; font-size: 0.8em; }
  .more[open] > summary::after { content: "\25B4"; }
  .moreBody { display: flex; flex-direction: column; gap: 12px; padding: 8px 0 4px; }
  .mh { margin: 4px 0 0; font-size: var(--fs-xs); font-weight: var(--fw-medium); color: var(--ink-2); }
  .raw { margin: 0; padding: 4px 0 4px 14px; border-left: 2px solid var(--ink); color: var(--ink-2); max-height: 6rem; overflow: auto; }
  .fix > summary, .lv > summary { cursor: pointer; min-height: 32px; display: flex; align-items: center; color: var(--ink-2); }
  .qs { display: flex; flex-direction: column; gap: 14px; padding: 8px 0 4px; }
  .switchrow { display: flex; align-items: center; gap: 10px; }
  .props { display: flex; flex-direction: column; border: 1px solid var(--line); border-radius: var(--radius-lg); overflow: hidden; }
  .prop { padding: 10px 14px; border-top: 1px solid var(--line); background: var(--surface); }
  .prop:first-child { border-top: 0; }
  .prop.off { background: var(--bg); }
  .pick { display: flex; gap: 12px; align-items: flex-start; cursor: pointer; }
  .pick input { flex: none; width: 24px; height: 24px; margin-top: 2px; accent-color: var(--ink); }
  .ptxt { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
  .ps { font-weight: var(--fw-medium); }
  .pm { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; }
  .vals { display: flex; flex-direction: column; gap: 10px; margin: 10px 0 4px 32px; }
  .lv ul { display: flex; flex-direction: column; gap: 6px; padding: 6px 0; }
  .scope { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 14px; }
  .always { display: inline-flex; align-items: center; gap: 8px; min-height: 32px; cursor: pointer; }
  .always input { width: 24px; height: 24px; accent-color: var(--ink); }
  .act { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 4px; }
  .why { margin: 2px 0 0 32px; }
  .why > summary { cursor: pointer; min-height: 32px; display: flex; align-items: center; }
  .why p { padding: 0 0 6px; }
  @media (max-width: 559px) {
    .done { padding: 14px 14px 0; gap: 10px; }
    .raw { max-height: none; display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
    .vals { margin-left: 0; }
    .vals :global(textarea) { min-height: 4.5rem; }
    /* ana düğme kaydırmadan görünür: kartın altında sabit durur */
    .act { position: sticky; bottom: -24px; z-index: 2; margin: 4px -14px 0; padding: 10px 14px calc(10px + env(safe-area-inset-bottom)); border-top: 1px solid var(--line); background: var(--surface); }
    .act :global(.btn-primary) { flex: 1 1 100%; }
    .act :global(.btn:not(.btn-primary)) { flex: 1 1 40%; }
  }
</style>
