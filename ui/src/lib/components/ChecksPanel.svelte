<script lang="ts">
  import { hataMetni } from '../hata'
  import { createCheck, createCheckFromTemplate, deleteCheck, getCheckTemplates, getChecks, getProject, patchCheck, patchProject, suggestChecks } from '../api2'
  import { ApiError } from '../errors'
  import { t } from '../i18n/index.svelte'
  import type { Key } from '../i18n/tr'
  import { app, toast } from '../store.svelte'
  import type { Check, CheckKind, CheckTemplate, CheckTemplateField } from '../types'
  import { errText, specSummary } from '../util'
  import { GeriAl } from '../geriAl.svelte'
  import { sadeKomut } from '../gezinme'
  import GeriAlSatiri from './GeriAlSatiri.svelte'
  import Icon from './Icon.svelte'

  // Projenin kalıcı ölçütleri (iş kimliği yok). Önerilenler KAPALI gelir; kullanıcı açmadan hiçbir komut çalışmaz.
  let { projectId, jobId = null, onchange }: { projectId: number; jobId?: number | null; onchange?: () => void } = $props()

  let checks = $state<Check[] | null>(null)
  let gate = $state(false)
  let suggesting = $state(false)
  let suggestMsg = $state('')
  let freshIds = $state<number[]>([])
  let error = $state('')

  let kind = $state<CheckKind>('command')
  let label = $state('')
  let cmd = $state('')
  let path = $state('')
  let url = $state('')
  let width = $state(1280)
  let height = $state(800)

  async function load() {
    try {
      const [c, pr] = await Promise.all([getChecks(projectId), getProject(projectId).catch(() => null)])
      checks = c
      gate = !!pr?.gateEnabled
      error = ''
    } catch (e) {
      error = errText(e)
    }
  }

  $effect(() => {
    void projectId
    void app.tick
    checks = checks && checks.length && checks[0].projectId !== projectId ? null : checks
    load()
  })

  const sorted = $derived([...(checks ?? [])].sort((a, b) => Math.abs(a.id) - Math.abs(b.id)))
  const hasOffAuto = $derived((checks ?? []).some((c) => !c.enabled && c.origin === 'auto'))
  const enabledCount = $derived((checks ?? []).filter((c) => c.enabled).length)

  async function toggle(c: Check) {
    const prev = c.enabled
    c.enabled = !prev // önce çiz
    error = ''
    try {
      await patchCheck(c.id, { enabled: c.enabled })
      onchange?.()
    } catch (e) {
      c.enabled = prev
      error = errText(e)
      toast(t('undone'))
    }
  }

  // Silinen ölçüt 5 sn boyunca geri alınabilir (A-OLC-03): geri alınca aynı ölçüt yeniden kurulur.
  const undoDel = new GeriAl<Check>()
  async function undoRemove() {
    const c = undoDel.take()
    if (!c) return
    try {
      const real = await createCheck({ projectId, kind: c.kind, label: c.label, spec: c.spec, timeoutS: c.timeoutS, enabled: c.enabled })
      checks = [...(checks ?? []), real]
      onchange?.()
    } catch (e) {
      error = hataMetni(e)
    }
  }

  async function remove(c: Check) {
    const before = checks ?? []
    checks = before.filter((x) => x.id !== c.id)
    try {
      await deleteCheck(c.id)
      undoDel.arm(c)
      onchange?.()
    } catch (e) {
      checks = before
      error = errText(e)
      toast(t('undone'))
    }
  }

  async function suggest() {
    if (suggesting) return
    suggesting = true
    suggestMsg = ''
    error = ''
    try {
      const before = new Set((checks ?? []).map((x) => x.id))
      const res = await suggestChecks(projectId)
      const all = await getChecks(projectId) // tam liste sunucudan; yeni gelenler işaretlenir
      checks = all
      freshIds = all.filter((x) => !before.has(x.id)).map((x) => x.id)
      suggestMsg = res.added > 0 || freshIds.length > 0 ? t('suggestedN', { n: Math.max(res.added, freshIds.length) }) : t('suggestedNone')
      void loadTemplates()
      onchange?.()
    } catch (e) {
      error = errText(e)
    } finally {
      suggesting = false
    }
  }

  async function toggleGate() {
    const prev = gate
    gate = !prev
    try {
      await patchProject(projectId, { gateEnabled: gate })
    } catch (e) {
      gate = prev
      error = errText(e)
      toast(t('undone'))
    }
  }

// ---- Düz cümleyle ölçüt ----
  let tpls = $state<CheckTemplate[]>([])
  let tplErr = $state('')
  let picked = $state('')
  let vals = $state<Record<string, Record<string, string>>>({})
  let formErr = $state('')
  let adding = $state(false)

  const defaults = (tpl: CheckTemplate): Record<string, string> => Object.fromEntries(tpl.fields.map((f) => [f.name, f.default ?? '']))

  async function loadTemplates() {
    const forId = projectId
    try {
      const r = await getCheckTemplates(forId)
      if (forId !== projectId) return
      tpls = r.templates
      const next = { ...vals }
      for (const tpl of r.templates) next[tpl.id] = { ...defaults(tpl), ...(next[tpl.id] ?? {}) }
      vals = next
      if (picked && !r.templates.some((x) => x.id === picked && x.available)) picked = ''
      tplErr = ''
    } catch (e) {
      tplErr = serverMsg(e)
    }
  }

  $effect(() => {
    void projectId
    picked = ''
    formErr = ''
    vals = {}
    void loadTemplates()
  })

  /** Sunucunun Türkçe hata açıklaması ({error}); sunucuya hiç ulaşılamadıysa genel metin. */
  function serverMsg(e: unknown): string {
    return hataMetni(e)
  }

  function pick(id: string) {
    picked = id
    formErr = ''
  }

  const current = $derived(tpls.find((x) => x.id === picked) ?? null)
  const canAddSentence = $derived(!!current && current.available && current.fields.every((f) => (vals[current.id]?.[f.name] ?? '').trim() !== ''))

  /** Cümle metni: sunucu {ad} yer tutucusu koyduysa boşluk cümlenin içinde; koymadıysa boşluklar cümlenin sonuna gelir. */
  function parts(tpl: CheckTemplate): { text?: string; field?: CheckTemplateField }[] {
    const out: { text?: string; field?: CheckTemplateField }[] = []
    const used = new Set<string>()
    const re = /\{(\w+)\}/g
    let last = 0
    let m: RegExpExecArray | null
    while ((m = re.exec(tpl.sentence))) {
      const f = tpl.fields.find((x) => x.name === m![1])
      if (!f) continue
      if (m.index > last) out.push({ text: tpl.sentence.slice(last, m.index) })
      out.push({ field: f })
      used.add(f.name)
      last = m.index + m[0].length
    }
    if (last < tpl.sentence.length) out.push({ text: tpl.sentence.slice(last) })
    const rest = tpl.fields.filter((f) => !used.has(f.name))
    if (used.size === 0 && rest.length > 0 && out.length > 0 && !/[:：]\s*$/.test(tpl.sentence)) out[out.length - 1].text += ':'
    for (const f of rest) out.push({ field: f })
    return out
  }
  const plain = (tpl: CheckTemplate) => tpl.sentence.replace(/\{\w+\}/g, '…').replace(/[:：]\s*$/, '')

  async function addSentence(e: Event) {
    e.preventDefault()
    if (!current || !canAddSentence || adding) return
    formErr = ''
    adding = true
    const values: Record<string, string> = {}
    for (const f of current.fields) values[f.name] = (vals[current.id]?.[f.name] ?? '').trim()
    try {
      await createCheckFromTemplate({ projectId, jobId: jobId ?? undefined, template: current.id, values })
      vals = { ...vals, [current.id]: defaults(current) }
      await load()
      void loadTemplates()
      onchange?.()
      toast(t('checkAdded'))
    } catch (err) {
      formErr = serverMsg(err)
    } finally {
      adding = false
    }
  }

  const canAdd = $derived(
    kind === 'command' ? cmd.trim() !== '' : kind === 'file' ? path.trim() !== '' : url.trim() !== '',
  )

  async function add(e: Event) {
    e.preventDefault()
    if (!canAdd) return
    const spec: Record<string, unknown> =
      kind === 'command' ? { cmd: cmd.trim(), expectExit: 0 }
      : kind === 'file' ? { path: path.trim(), exists: true }
      : kind === 'url' ? { url: url.trim(), expectStatus: 200 }
      : { url: url.trim(), width, height }
    const text = label.trim() || (kind === 'command' ? cmd.trim() : kind === 'file' ? path.trim() : url.trim())
    // Önce çiz: geçici satır, sonra gerçeği koy.
    const temp: Check = { id: -Date.now(), projectId, jobId: null, kind, label: text, spec, timeoutS: 120, enabled: true, origin: 'user', createdAt: new Date().toISOString() }
    const before = checks ?? []
    checks = [...before, temp]
    const keep = { cmd, path, url, label }
    cmd = path = url = label = ''
    try {
      const real = await createCheck({ projectId, kind, label: text, spec, enabled: true })
      checks = (checks ?? []).map((x) => (x.id === temp.id ? real : x))
      onchange?.()
      toast(t('checkAdded'))
    } catch (err) {
      checks = before
      ;({ cmd, path, url, label } = keep)
      error = errText(err)
    }
  }

  const kindKeys: Record<string, Key> = { command: 'ckCommand', file: 'ckFile', url: 'ckUrl', screenshot: 'ckShot', 'human-checklist': 'ckHuman' }
  // Yeni ölçüt türleri kayıt defterinden gelir; bilinmeyenin adı olduğu gibi gösterilir.
  const kindLabel = (k: CheckKind) => (kindKeys[k] ? t(kindKeys[k]) : k)
</script>

<section class="cp" aria-labelledby={`cp-t-${projectId}`}>
  <div class="head">
    <h3 class="h3" id={`cp-t-${projectId}`}>{t('checksTitle')} <span class="muted small">{t('checksEnabledN', { n: enabledCount })}</span></h3>
    <button class="btn btn-sm" onclick={suggest} disabled={suggesting} data-suggest>
      <Icon name="refresh" size={16} />{suggesting ? t('suggesting') : t('suggest')}
    </button>
  </div>

  <p class="notice" role="note"><Icon name="shield" size={18} /><span>{t('checksWarn')}</span></p>

  {#if suggestMsg}<p class="small" role="status">{suggestMsg}</p>{/if}
  {#if error}<p class="small err-line" role="alert">{error}</p>{/if}
  {#if undoDel.item}<GeriAlSatiri label={undoDel.item.label} onundo={undoRemove} />{/if}

  {#if checks === null}
    <p class="muted small" role="status">{t('loading')}</p>
  {:else if sorted.length === 0}
    <p class="muted empty">{t('checksNone')}</p>
  {:else}
    <ul class="list">
      {#each sorted as c (c.id)}
        <li class="crow" class:off={!c.enabled} data-check={c.id}>
          <button
            class="switch"
            role="switch"
            aria-checked={c.enabled}
            aria-label={`${c.label}: ${c.enabled ? t('on') : t('off')}`}
            title={c.enabled ? t('ckSwitchOnTip') : t('ckSwitchOffTip')}
            onclick={() => toggle(c)}
          ></button>
          <div class="cbody">
            <p class="clabel">{sadeKomut(c.label, t('testCmdPlain'))}</p>
            <p class="mono cspec"><span class="ckind">{kindLabel(c.kind)}</span> {specSummary(c.kind, c.spec)}</p>
          </div>
          <div class="cmeta">
            {#if c.origin === 'auto'}<span class="chip" class:chip-working={freshIds.includes(c.id)} class:chip-neutral={!freshIds.includes(c.id)}>{freshIds.includes(c.id) ? t('originNew') : t('originAuto')}</span>{/if}
            {#if !c.enabled}<span class="small muted">{t('offNote')}</span>{/if}
            <button class="btn btn-quiet btn-sm" aria-label={`${t('remove')}: ${c.label}`} title={`${t('remove')}: ${c.label}`} onclick={() => remove(c)}><Icon name="trash" size={16} /></button>
          </div>
        </li>
      {/each}
    </ul>
  {/if}

  <details class="add" open>
    <summary>{t('checkAddTitle')}</summary>
    <p class="small muted lead">{t('ckSentenceLead')}</p>
    {#if tplErr}<p class="small err-line" role="alert">{t('ckTemplatesErr', { m: tplErr })}</p>{/if}

    <form class="sform" onsubmit={addSentence}>
      <ul class="sents" role="radiogroup" aria-label={t('checkAddTitle')}>
        {#each tpls as tpl (tpl.id)}
          {@const rid = `ck-s-${projectId}-${tpl.id}`}
          <li class="sent" class:off={!tpl.available} class:sel={picked === tpl.id} data-sentence={tpl.id}>
            <div class="srow">
              <input type="radio" id={rid} name={`ck-sent-${projectId}`} aria-label={plain(tpl)} checked={picked === tpl.id} disabled={!tpl.available} onchange={() => pick(tpl.id)} />
              <div class="stxt">
                {#each parts(tpl) as part, i (i)}
                  {#if part.field}
                    {#if picked === tpl.id}
                      <input class="input fld mono" aria-label={part.field.label} placeholder={part.field.placeholder} autocomplete="off" spellcheck="false" bind:value={vals[tpl.id][part.field.name]} />
                    {:else}
                      <label class="blank" for={rid}>{part.field.label}</label>
                    {/if}
                  {:else}
                    <label class="tp" for={rid}>{part.text}</label>
                  {/if}
                {/each}
                {#if tpl.weak}<span class="chip chip-working small" data-weak-proof>{t('ckWeak')}</span>{/if}
                {#if tpl.detectedCmd}<code class="small muted det" title={t('ckDetected', { c: tpl.detectedCmd })}>{tpl.detectedCmd}</code>{/if}
              </div>
            </div>
            {#if !tpl.available && tpl.reason}<p class="small muted why">{tpl.reason}</p>{/if}
            {#if tpl.weak && tpl.note}<p class="small muted why" data-weak-note>{tpl.note}</p>{/if}
          </li>
        {/each}
      </ul>
      {#if formErr}<p class="small err-line" role="alert" data-form-error>{formErr}</p>{/if}
      <div class="sadd">
        <button class="btn btn-primary" type="submit" disabled={!canAddSentence || adding}><Icon name="plus" size={16} />{t('checkAddBtn')}</button>
        {#if picked && !canAddSentence}<span class="small muted">{t('ckFieldsMissing')}</span>{/if}
      </div>
    </form>

    <details class="adv">
      <summary>{t('ckCustom')}</summary>
      <form class="form" onsubmit={add}>
        <div class="field">
          <label class="label" for={`ck-kind-${projectId}`}>{t('ckKind')}</label>
          <select id={`ck-kind-${projectId}`} class="select" bind:value={kind}>
            <option value="command">{t('ckCommand')}</option>
            <option value="file">{t('ckFile')}</option>
            <option value="url">{t('ckUrl')}</option>
            <option value="screenshot">{t('ckShot')}</option>
          </select>
        </div>
        {#if kind === 'command'}
          <div class="field grow">
            <label class="label" for={`ck-cmd-${projectId}`}>{t('ckCmdLabel')}</label>
            <input id={`ck-cmd-${projectId}`} class="input mono" bind:value={cmd} placeholder="npm test" autocomplete="off" spellcheck="false" />
          </div>
        {:else if kind === 'file'}
          <div class="field grow">
            <label class="label" for={`ck-path-${projectId}`}>{t('ckPathLabel')}</label>
            <input id={`ck-path-${projectId}`} class="input mono" bind:value={path} placeholder="dist\index.html" autocomplete="off" spellcheck="false" />
          </div>
        {:else}
          <div class="field grow">
            <label class="label" for={`ck-url-${projectId}`}>{t('ckUrlLabel')}</label>
            <input id={`ck-url-${projectId}`} class="input mono" bind:value={url} placeholder="http://localhost:5173/" autocomplete="off" spellcheck="false" />
          </div>
        {/if}
        {#if kind === 'screenshot'}
          <div class="field dim">
            <label class="label" for={`ck-w-${projectId}`}>{t('ckWidth')}</label>
            <input id={`ck-w-${projectId}`} class="input" type="number" min="200" max="3840" bind:value={width} />
          </div>
          <div class="field dim">
            <label class="label" for={`ck-h-${projectId}`}>{t('ckHeight')}</label>
            <input id={`ck-h-${projectId}`} class="input" type="number" min="200" max="3840" bind:value={height} />
          </div>
        {/if}
        <div class="field grow">
          <label class="label" for={`ck-label-${projectId}`}>{t('ckName')}</label>
          <input id={`ck-label-${projectId}`} class="input" bind:value={label} placeholder={t('ckNamePh')} autocomplete="off" />
        </div>
        <div class="field">
          <span class="label" aria-hidden="true">&nbsp;</span>
          <button class="btn btn-primary" type="submit" disabled={!canAdd} aria-describedby={`ck-why-${projectId}`}><Icon name="plus" size={16} />{t('checkAddBtn')}</button>
          <span class="small muted" id={`ck-why-${projectId}`} data-check-why>{#if !canAdd}{t(kind === 'command' ? 'ckNeedCmd' : kind === 'file' ? 'ckNeedPath' : 'ckNeedUrl')}{/if}</span>
        </div>
      </form>
    </details>
  </details>

  <div class="gate">
    <button class="switch" role="switch" aria-checked={gate} title={gate ? t('ckGateOnTip') : t('ckGateOffTip')} aria-labelledby={`gate-l-${projectId}`} aria-describedby={`gate-d-${projectId}`} onclick={toggleGate}></button>
    <div>
      <p class="clabel" id={`gate-l-${projectId}`}>{t('gateLabel')}</p>
      <p class="small muted" id={`gate-d-${projectId}`}>{t('gateHelp')}</p>
    </div>
  </div>
  {#if hasOffAuto}<p class="small muted">{t('autoOffHint')}</p>{/if}
</section>

<style>
  .cp { display: flex; flex-direction: column; gap: 12px; }
  .head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px 16px; }
  .h3 { font-family: var(--sans); font-weight: var(--fw-strong); font-size: var(--fs-md); margin: 0; }
  .empty { padding: 8px 0; }
  .list { border-top: 1px solid var(--line); }
  .crow { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: 8px 14px; align-items: center; padding: 12px 0; border-bottom: 1px solid var(--line); }
  .crow.off .clabel { color: var(--ink-2); }
  .clabel { margin: 0; font-weight: var(--fw-medium); }
  .cspec { margin: 2px 0 0; color: var(--ink-2); word-break: break-all; }
  .ckind { font-family: var(--sans); font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-3); padding-right: 6px; }
  .cmeta { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; justify-content: flex-end; }
  .add { border-top: 1px solid var(--line); padding-top: 10px; }
  .lead { margin: 0 0 8px; }
  .sents { display: flex; flex-direction: column; border-top: 1px solid var(--line); }
  .sent { padding: 8px 0; border-bottom: 1px solid var(--line); }
  .sent.sel { background: color-mix(in srgb, var(--ok-tint) 55%, transparent); }
  .srow { display: flex; align-items: flex-start; gap: 10px; padding-inline: 4px; }
  .srow input[type='radio'] { flex: none; width: 24px; height: 24px; margin: 6px 0 0; accent-color: var(--primary); }
  .stxt { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; min-width: 0; min-height: 32px; }
  .tp { font-weight: var(--fw-medium); cursor: pointer; }
  .sent.off .tp { color: var(--ink-3); font-weight: var(--fw-medium); cursor: default; }
  .blank { display: inline-block; min-width: 5.5rem; padding: 2px 10px; border-bottom: 2px dotted var(--line-strong); color: var(--ink-3); font-size: var(--fs-sm); cursor: pointer; }
  .sent.off .blank { display: none; }
  .fld { width: min(100%, 17rem); min-height: 34px; padding-block: 4px; font-size: var(--fs-sm); }
  .det { font-family: var(--mono); }
  .why { margin: 2px 0 0 34px; }
  .sadd { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 14px; padding-top: 12px; }
  .sform { display: flex; flex-direction: column; gap: 4px; }
  .adv { margin-top: 12px; padding-top: 8px; border-top: 1px dashed var(--line); }
  .adv > summary { cursor: pointer; font-weight: var(--fw-strong); min-height: 32px; display: flex; align-items: center; color: var(--ink-2); }
  .add > summary { cursor: pointer; font-weight: var(--fw-strong); min-height: 32px; display: flex; align-items: center; }
  .form { display: flex; flex-wrap: wrap; gap: 12px; align-items: flex-end; padding: 12px 0 4px; }
  .form .grow { flex: 1 1 14rem; }
  .form .dim { width: 7rem; }
  .form .field:first-child { width: 13rem; }
  .gate { display: flex; gap: 14px; align-items: flex-start; padding-top: 12px; border-top: 1px solid var(--line); }

  @media (max-width: 560px) {
    .crow { grid-template-columns: auto minmax(0, 1fr); }
    .cmeta { grid-column: 2; justify-content: flex-start; }
    .form .field:first-child { width: 100%; }
  }
</style>
