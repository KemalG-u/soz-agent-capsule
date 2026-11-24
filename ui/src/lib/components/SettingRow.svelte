<script lang="ts">
  import { tick } from 'svelte'
  import { t, type Key } from '../i18n/index.svelte'
  import { adjustmentOf, body, describeOf, rg, saveSetting, settingKey, titleOf } from '../registry.svelte'
  import { toast } from '../store.svelte'
  import { sf } from '../surface.svelte'
  import type { RegistryEntry } from '../types5'
  import Icon from './Icon.svelte'
  import { hata, kendiHata, type Hata } from '../hata'
  import HataSatiri from './HataSatiri.svelte'

  // Tek ayar satırı: bütün bilgi kayıttan gelir (başlık, açıklama, tür, sınır, birim, sınıf, kaç yerde geçerli, ne zaman uygulanır).
  let { entry, showCard = false }: { entry: RegistryEntry; showCard?: boolean } = $props()

  const b = $derived(body(entry))
  const key = $derived(settingKey(entry))
  const cur = $derived(rg.values[key])
  const control = $derived(b.control ?? 'text')
  const sch = $derived(b.schema ?? {})
  const nullable = $derived(Array.isArray(sch.type) ? sch.type.includes('null') : false)
  const isInt = $derived(Array.isArray(sch.type) ? sch.type.includes('integer') : sch.type === 'integer')
  const isGate = $derived(b.class === 'K4')
  const adj = $derived(adjustmentOf(entry))
  const id = $derived('st-' + entry.id.replace(/[^a-z0-9]+/gi, '-'))
  const differs = $derived(JSON.stringify(cur ?? null) !== JSON.stringify(b.default ?? null))

  let busy = $state(false)
  let error = $state<Hata | null>(null)
  let more = $state(false)
  // Güvenlik kapısı değişikliği iki adımlıdır: önce değer beklemeye alınır, kişi onaylayınca yazılır.
  let pending = $state<{ value: unknown } | null>(null)
  let box: HTMLElement | undefined = $state()

  const restartKey = $derived(b.restart === 'next-run' ? 'stRestartRun' : b.restart === 'app' ? 'stRestartApp' : b.restart === 'capsule' ? 'stRestartCapsule' : null)

  async function write(value: unknown, el?: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement) {
    busy = true
    error = null
    try {
      await saveSetting(entry, value)
      toast(t('saved'))
    } catch (e) {
      error = hata(e)
      // Sunucu reddetti: ekrandaki değer eski haline döner.
      if (el) el.value = display(cur)
    } finally {
      busy = false
    }
  }

  function commit(value: unknown, el?: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement) {
    error = null
    if (JSON.stringify(value ?? null) === JSON.stringify(cur ?? null)) return
    if (isGate) {
      pending = { value }
      if (el) el.value = display(cur)
      queueMicrotask(() => box?.querySelector<HTMLElement>('[data-confirm-yes]')?.focus())
      return
    }
    void write(value, el)
  }

  // Onay kutusundan vazgeçince (Esc ya da Vazgeç) odak ayarın kendi düğmesine döner; Esc sayfadan çıkmaz.
  function cancelGate() {
    pending = null
    void tick().then(() => box?.querySelector<HTMLElement>('[data-setting-input]')?.focus())
  }

  async function confirmGate() {
    const p = pending
    pending = null
    if (p) await write(p.value)
  }

  // Kişiye gösterilen cümlelerde true/false yerine Açık/Kapalı.
  const say = (v: unknown): string => (v === true ? t('stOn') : v === false ? t('stOff') : display(v))
  const display = (v: unknown): string => (v === null || v === undefined ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v))

  function onText(e: Event) {
    const el = e.currentTarget as HTMLInputElement
    const v = el.value.trim()
    commit(v === '' ? (nullable ? null : '') : v, el)
  }
  // "abc" gibi sayı olmayan yazı: tarayıcı alanı boş gösterir ama `badInput` der. Yazarken alan altında "Buraya sayı yaz." çıkar ve kaydedilmez.
  function onNumberInput(e: Event) {
    const el = e.currentTarget as HTMLInputElement
    if (el.validity?.badInput) error = kendiHata(t('stNotNumber'))
    else if (error?.text === t('stNotNumber')) error = null
  }
  // Tarayıcı sayı alanında saf harfi hiç yazmaz ve input olayı vermez: tuş basışında söyleriz ki alan neden boş kaldığı anlaşılsın (C-13).
  function onNumberKey(e: KeyboardEvent) {
    if (e.isComposing || e.ctrlKey || e.metaKey || e.altKey || e.key.length !== 1) return
    if (!/[0-9.,+\-eE]/.test(e.key)) error = kendiHata(t('stNotNumber'))
  }
  function onNumber(e: Event) {
    const el = e.currentTarget as HTMLInputElement
    if (el.validity?.badInput) {
      error = kendiHata(t('stNotNumber'))
      return
    }
    if (el.value.trim() === '') {
      if (nullable) commit(null, el)
      else el.value = display(cur)
      return
    }
    const n = Number(el.value.replace(',', '.'))
    if (!Number.isFinite(n)) {
      error = kendiHata(t('stNotNumber'))
      el.value = display(cur)
      return
    }
    commit(n, el)
  }
  function onSelect(e: Event) {
    const el = e.currentTarget as HTMLSelectElement
    const raw = el.value
    const asNum = (sch.enum ?? []).find((x) => String(x) === raw)
    commit(asNum !== undefined ? asNum : raw, el)
  }
  function onJson(e: Event) {
    const el = e.currentTarget as HTMLTextAreaElement
    const txt = el.value.trim()
    if (txt === '') return commit(null, el)
    try {
      commit(JSON.parse(txt), el)
    } catch {
      error = kendiHata(t('stNotJson'))
      el.value = display(cur)
    }
  }

  const enumLabel = (v: string | number): string => {
    const k = `stEnum_${v}` as Key
    const lab = t(k)
    return lab === k ? (b.unit ? `${v} ${b.unit}` : String(v)) : lab
  }
  // Seçenekleri kayıtta sayılmamış ama doğrulayıcısı ajan kimliği olan ayar: bulunan ajan araçlarından seçilir.
  const agentChoices = $derived((b.validators ?? []).includes('agent.id') ? sf.agents : [])
  const step = $derived(isInt ? '1' : (sch.maximum ?? 1000) <= 1 ? '0.05' : '0.1')
  const unit = $derived(b.unit ?? '')
</script>

<div class="srow" class:gate={isGate} data-setting={key} data-setting-id={entry.id} bind:this={box}>
  <div class="sl">
    <div class="st">
      <label class="stitle" for={id}>{titleOf(entry)}</label>
      {#if isGate}<span class="chip chip-waiting" title={t('stGateHelp')} data-gate-chip><Icon name="shield" size={13} />{t('stGate')}</span>{/if}
      {#if showCard && b.card}<span class="small muted">{b.card}</span>{/if}
    </div>
    <p class="small muted sdesc" id="{id}-d" data-server-text>{describeOf(entry)}</p>
    {#if restartKey}<p class="small muted">{t(restartKey)}</p>{/if}
    {#if adj}
      <p class="small adjusted" role="status" data-adjusted={key}><Icon name="info" size={14} />{adj.reason || t('stAdjusted', { stored: say(adj.stored), applied: say(adj.applied) })}</p>
    {/if}
    <HataSatiri h={error} data-setting-error />
  </div>

  <div class="sc">
    {#if control === 'toggle'}
      <button type="button" {id} class="switch" role="switch" aria-checked={!!cur} aria-label={titleOf(entry)} aria-describedby="{id}-d" disabled={busy} onclick={() => commit(!cur)} data-setting-input></button>
    {:else if control === 'select' && (sch.enum?.length ?? 0) > 0}
      <select {id} class="select sel" value={display(cur)} aria-describedby="{id}-d" disabled={busy} onchange={onSelect} data-setting-input>
        {#each sch.enum ?? [] as v (v)}<option value={String(v)}>{enumLabel(v)}</option>{/each}
      </select>
    {:else if control === 'select' && agentChoices.length > 0}
      <select {id} class="select sel" value={display(cur)} aria-describedby="{id}-d" disabled={busy} onchange={onSelect} data-setting-input>
        {#each agentChoices as a (a.id)}<option value={a.id}>{a.name}</option>{/each}
      </select>
    {:else if control === 'slider'}
      <div class="slide">
        <input {id} type="range" min={sch.minimum ?? 0} max={sch.maximum ?? 1} {step} value={Number(cur ?? 0)} aria-describedby="{id}-d" disabled={busy} onchange={onNumber} data-setting-input />
        <output class="mono" for={id}>{display(cur)}</output>
      </div>
    {:else if control === 'number' || control === 'auto' && typeof cur === 'number'}
      <div class="numwrap">
        <input {id} class="input num" type="number" inputmode={isInt ? 'numeric' : 'decimal'} min={sch.minimum} max={sch.maximum} {step} value={display(cur)} aria-describedby="{id}-d" disabled={busy} oninput={onNumberInput} onkeydown={onNumberKey} onchange={onNumber} data-setting-input />
        {#if unit}<span class="small muted">{unit}</span>{/if}
      </div>
    {:else if control === 'json'}
      <textarea {id} class="textarea js" rows="4" spellcheck="false" aria-describedby="{id}-d" disabled={busy} onchange={onJson} data-setting-input>{display(cur)}</textarea>
    {:else}
      <input {id} class="input wide" type="text" value={display(cur)} placeholder={b.default === null || b.default === undefined ? t('stAutoHint') : ''} spellcheck="false" aria-describedby="{id}-d" disabled={busy} onchange={onText} data-setting-input />
    {/if}
    <div class="sa">
      {#if differs && !pending}<button type="button" class="btn btn-quiet btn-sm" onclick={() => commit(b.default ?? null)} disabled={busy} data-setting-reset>{t('stReset')}</button>{/if}
      {#if entry.contract}<button type="button" class="btn btn-quiet btn-sm" aria-expanded={more} aria-controls="{id}-more" onclick={() => (more = !more)}>{more ? t('stLess') : t('stMore')}</button>{/if}
    </div>
  </div>

  {#if pending}
    <div class="confirm" role="alertdialog" tabindex="-1" aria-label={t('stGate')} data-confirm onkeydown={(e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancelGate() } }}>
      <Icon name="alert" size={18} />
      <div>
        <p>{t('stConfirmText', { v: say(pending.value) || t('stEmpty') })}</p>
        <div class="cbtns">
          <button type="button" class="btn btn-primary btn-sm" onclick={confirmGate} data-confirm-yes>{t('stConfirmYes')}</button>
          <button type="button" class="btn btn-quiet btn-sm" onclick={cancelGate}>{t('cancel')}</button>
        </div>
      </div>
    </div>
  {/if}

  {#if more && entry.contract}
    {@const c = entry.contract}
    <dl class="contract" id="{id}-more">
      {#if c.discover?.tr}<div><dt>{t('stWhere')}</dt><dd>{c.discover.tr}</dd></div>{/if}
      {#if c.disable?.tr}<div><dt>{t('stOff')}</dt><dd>{c.disable.tr}</dd></div>{/if}
      {#if c.onError?.tr}<div><dt>{t('stFail')}</dt><dd>{c.onError.tr}</dd></div>{/if}
      {#if c.onNewTool?.tr}<div><dt>{t('stNew')}</dt><dd>{c.onNewTool.tr}</dd></div>{/if}
    </dl>
  {/if}
</div>

<style>
  .srow { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 16rem); gap: 6px 24px; padding: 14px 0; border-top: 1px solid var(--line); }
  .sl { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
  .st { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; }
  .stitle { font-weight: var(--fw-strong); }
  .sdesc { max-width: 60ch; }
  .sc { display: flex; flex-direction: column; align-items: flex-end; gap: 4px; min-width: 0; }
  .sel, .wide { width: 100%; max-width: 16rem; }
  .numwrap { display: flex; align-items: center; gap: 8px; }
  .num { width: 8rem; font-variant-numeric: tabular-nums; }
  .slide { display: flex; align-items: center; gap: 10px; width: 100%; max-width: 16rem; }
  .slide input { flex: 1; min-width: 0; accent-color: var(--ink); min-height: var(--ctl); }
  .js { width: 100%; font-family: var(--mono); font-size: var(--fs-xs); }
  .sa { display: flex; gap: 2px; }
  .adjusted { display: flex; align-items: flex-start; gap: 6px; padding: 6px 10px; border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); }
  .adjusted :global(svg) { flex: none; margin-top: 2px; color: var(--waiting); }
  .confirm { grid-column: 1 / -1; display: flex; gap: 10px; padding: 12px 14px; border-radius: var(--radius); border-left: 3px solid var(--waiting); background: var(--waiting-tint); }
  .confirm :global(svg) { flex: none; margin-top: 2px; color: var(--waiting); }
  .cbtns { display: flex; gap: 8px; margin-top: 8px; }
  .contract { grid-column: 1 / -1; display: grid; gap: 8px; padding: 10px 12px; border-radius: var(--radius); background: var(--bg-sunken); font-size: var(--fs-xs); }
  .contract dt { font-weight: var(--fw-strong); color: var(--ink-2); }
  .contract dd { margin: 0; color: var(--ink-2); line-height: 1.5; }
  @media (max-width: 640px) {
    .srow { grid-template-columns: minmax(0, 1fr); }
    .sc { align-items: stretch; }
    .sel, .wide, .slide { max-width: none; }
  }
</style>
