<script lang="ts">
  import { getUsageModels } from '../api2'
  import { t } from '../i18n/index.svelte'
  import { app, toast, updateSettings } from '../store.svelte'
  import type { ModelPrice } from '../types'
  import { errText } from '../util'
  import Icon from './Icon.svelte'

  // Her satır bir model. Giriş ve çıkış kutusu birlikte boşsa "fiyat girilmedi": kaydedilmez.
  // Önbellek fiyatları (cacheRead / cacheWrite) bu formda yoktur ama kayıtlıysa aynen korunur.
  interface Row {
    model: string
    input: string
    output: string
    cacheRead?: number
    cacheWrite?: number
  }

  let rows = $state<Row[]>([])
  let loaded = $state(false)
  let loadErr = $state('')
  let bad = $state<string[]>([])
  let saving = $state(false)

  const show = (n: number | undefined) => (n === undefined ? '' : String(n))

  async function load() {
    let models: string[] = []
    try {
      models = await getUsageModels()
      loadErr = ''
    } catch (e) {
      loadErr = errText(e)
    }
    const prices = app.settings?.prices ?? {}
    const names = [...new Set([...Object.keys(prices), ...models])]
    rows = names.map((model) => {
      const p = prices[model]
      return { model, input: show(p?.input), output: show(p?.output), cacheRead: p?.cacheRead, cacheWrite: p?.cacheWrite }
    })
    bad = []
    loaded = true
  }

  $effect(() => {
    if (!app.settings || loaded) return
    void load()
  })

  const num = (v: string): number => {
    const s = v.trim().replace(',', '.')
    return s === '' ? NaN : Number(s)
  }
  const blank = (r: Row) => r.input.trim() === '' && r.output.trim() === ''
  const valid = (n: number) => Number.isFinite(n) && n >= 0

  async function save() {
    if (saving) return
    const out: Record<string, ModelPrice> = {}
    const wrong: string[] = []
    for (const r of rows) {
      if (blank(r)) continue
      const i = num(r.input)
      const o = num(r.output)
      if (!valid(i) || !valid(o)) {
        wrong.push(r.model)
        continue
      }
      const price: ModelPrice = { input: i, output: o }
      if (r.cacheRead !== undefined) price.cacheRead = r.cacheRead
      if (r.cacheWrite !== undefined) price.cacheWrite = r.cacheWrite
      out[r.model] = price
    }
    bad = wrong
    if (wrong.length > 0) return
    saving = true
    try {
      await updateSettings({ prices: Object.keys(out).length > 0 ? out : null })
      toast(t('pricesSaved'))
    } catch (e) {
      toast(errText(e))
    } finally {
      saving = false
    }
  }
</script>

<div class="pp">
  <p class="small muted">{t('pricesLead')}</p>

  {#if loadErr}<p class="small err-line" role="alert">{t('pricesLoadErr', { m: loadErr })}</p>{/if}

  {#if loaded && rows.length === 0 && !loadErr}
    <p class="small muted">{t('pricesNoModels')}</p>
  {:else if rows.length > 0}
    <ul class="rows">
      {#each rows as r, i (r.model)}
        <li class="row" class:err={bad.includes(r.model)}>
          <span class="mname mono" id={`pm-${i}`}>{r.model}</span>
          <div class="field">
            <label class="label" for={`pi-${i}`}>{t('priceInput')}</label>
            <input id={`pi-${i}`} class="input num" type="text" inputmode="decimal" autocomplete="off" bind:value={r.input} aria-describedby={`pm-${i}`} />
          </div>
          <div class="field">
            <label class="label" for={`po-${i}`}>{t('priceOutput')}</label>
            <input id={`po-${i}`} class="input num" type="text" inputmode="decimal" autocomplete="off" bind:value={r.output} aria-describedby={`pm-${i}`} />
          </div>
          {#if blank(r)}<span class="small muted none">{t('noPrice')}</span>{/if}
        </li>
      {/each}
    </ul>
    {#if bad.length > 0}<p class="small err-line" role="alert">{t('pricesBad', { m: bad.join(', ') })}</p>{/if}
    <div><button class="btn btn-primary" onclick={save} disabled={saving}><Icon name="check" size={16} />{t('pricesSave')}</button></div>
  {/if}
</div>

<style>
  .pp { display: flex; flex-direction: column; gap: 14px; }
  .rows { display: flex; flex-direction: column; border-top: 1px solid var(--line); }
  .row { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 8px 14px; padding: 12px 0; border-bottom: 1px solid var(--line); }
  .row.err .num { border-color: var(--stalled); }
  .mname { flex: 1 1 100%; font-weight: var(--fw-strong); word-break: break-all; }
  .num { width: 9.5rem; font-variant-numeric: tabular-nums; }
  .none { align-self: center; padding-top: 18px; }
</style>
