<script lang="ts">
  import { hataMetni } from '../lib/hata'
  import { onMount } from 'svelte'
  import { acceptOnboarding, approveEntry, dismissOnboarding, getOnboarding, restoreOnboarding, revokeEntry, scanOnboarding, whyOnboarding } from '../lib/api5'
  import Icon from '../lib/components/Icon.svelte'
  import Skeleton from '../lib/components/Skeleton.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import { describeOf, loadRegistry, rg, titleOf } from '../lib/registry.svelte'
  import { sade } from '../lib/sade'
  import { toast } from '../lib/store.svelte'
  import type { OnboardingItem, OnboardingView, OnboardingWhy, RegistryEntry, ScanReply } from '../lib/types5'
  import { ago } from '../lib/util'

  // "Seni tanıyorum": bilgisayarında bulunan araçlar ve öneriler. Hiçbir şey kabul etmeden etkinleşmez. Yalnız adlara, sürümlere ve yerlere bakılır.
  let view = $state<OnboardingView | null>(null)
  let loading = $state(true)
  let error = $state('')
  let scanning = $state(false)
  let scanErr = $state('')
  let outcome = $state<ScanReply['outcome'] | null>(null)
  let why = $state<Record<string, OnboardingWhy | 'loading' | string>>({})
  let busy = $state<Record<string, boolean>>({})
  let rowErr = $state<Record<string, string>>({})
  let connectorOf = $state<Record<string, { id: string; state: string | null }>>({})

  async function load() {
    try {
      view = await getOnboarding()
      error = ''
    } catch (e) {
      error = hataMetni(e)
    } finally {
      loading = false
    }
  }
  onMount(() => {
    void load()
    void loadRegistry()
  })

  async function scan() {
    if (scanning) return
    scanning = true
    scanErr = ''
    try {
      const r = await scanOnboarding()
      view = r.view
      outcome = r.outcome
    } catch (e) {
      scanErr = hataMetni(e)
    } finally {
      scanning = false
    }
  }

  async function act(it: OnboardingItem, kind: 'accept' | 'dismiss' | 'restore') {
    if (busy[it.id]) return
    busy[it.id] = true
    rowErr[it.id] = ''
    try {
      const r = kind === 'accept' ? await acceptOnboarding(it.id) : kind === 'dismiss' ? await dismissOnboarding(it.id) : await restoreOnboarding(it.id)
      if (r.connector) connectorOf[it.id] = r.connector
      await load()
      await loadRegistry() // kabul bir bağlayıcıyı açmış olabilir
      toast(kind === 'accept' ? t('knAccepted') : kind === 'dismiss' ? t('knDismissed') : t('knRestored'))
    } catch (e) {
      rowErr[it.id] = hataMetni(e)
    } finally {
      busy[it.id] = false
    }
  }

  async function toggleWhy(it: OnboardingItem) {
    if (why[it.id]) {
      delete why[it.id]
      return
    }
    why[it.id] = 'loading'
    try {
      why[it.id] = await whyOnboarding(it.id)
    } catch (e) {
      why[it.id] = hataMetni(e)
    }
  }

  // ---- yetenekler: kişi "Kullan" diyene kadar kapalı gelenler (Hermes, OpenCode, kendi dosyaların) ----
  const caps = $derived(rg.entries.filter((e) => e.kind === 'connector' || ((e.state === 'suggested' || e.awaitsApproval) && e.kind !== 'setting')))
  let capBusy = $state<Record<string, boolean>>({})
  let capErr = $state<Record<string, string>>({})

  async function setCap(e: RegistryEntry, on: boolean) {
    if (capBusy[e.id]) return
    capBusy[e.id] = true
    capErr[e.id] = ''
    try {
      if (on) await approveEntry(e.id)
      else await revokeEntry(e.id)
      await loadRegistry()
      toast(on ? t('knCapOn', { n: titleOf(e) }) : t('knCapOff', { n: titleOf(e) }))
    } catch (err) {
      capErr[e.id] = hataMetni(err)
    } finally {
      capBusy[e.id] = false
    }
  }
  const canRevoke = (e: RegistryEntry) => e.declaredState === 'suggested' || e.owner === 'user'
  const stateText = (e: RegistryEntry) => (e.state === 'enabled' ? t('knCapEnabled') : e.state === 'suggested' ? t('knCapSuggested') : t('knCapOff2'))

  const empty = $derived(!!view && view.total === 0)
</script>

<div class="page" data-tanisma>
  <header class="page-head">
    <div>
      <h1 class="h1">{t('knTitle')}</h1>
      <p class="lead">{t('knLead')}</p>
    </div>
    <button class="btn btn-primary" onclick={scan} disabled={scanning} data-scan>
      {#if scanning}<span class="spin" aria-hidden="true"></span>{t('knScanning')}{:else}<Icon name="refresh" size={16} />{view?.lastScan ? t('knScanAgain') : t('knScan')}{/if}
    </button>
  </header>

  {#if view}
    <p class="notice" data-notice><Icon name="lock" size={16} /><span>{view.notice}</span></p>
    {#if view.lastScan}<p class="small muted last">{t('knLastScan', { when: ago(view.lastScan) })}</p>{/if}
  {/if}
  {#if scanErr}<p class="err-line" role="alert" data-scan-error>{scanErr}</p>{/if}
  {#if outcome}
    <p class="small okline" role="status" data-scan-outcome>{t('knOutcome', { found: outcome.found, fresh: outcome.new })}{#if outcome.couldNotRead.length}{' '}{t('knCouldNot', { n: outcome.couldNotRead.length })}{/if}</p>
  {/if}

  {#if loading}
    <Skeleton rows={3} />
  {:else if error && !view}
    <div role="alert" data-onboarding-error>
      <p class="err-line">{error}</p>
      <p class="retry-row"><button class="btn" onclick={() => { loading = true; void load() }}>{t('retry')}</button></p>
    </div>
  {:else if view}
    {#if empty}
      <div class="empty-state" data-empty="onboarding">
        <p>{t('knEmpty')}</p>
      </div>
    {/if}

    {#each view.groups.filter((g) => g.items.length > 0) as g (g.key)}
      <section class="grp" aria-labelledby="kg-{g.key}" data-kgroup={g.key}>
        <h2 class="h2" id="kg-{g.key}">{g.title} <span class="small muted">{g.count}</span></h2>
        {#if g.key === 'records'}<p class="small muted">{t('knRecordsLead')}</p>{/if}
        <ul class="items">
          {#each g.items as it (it.id)}
            <li class="it st-{it.state}" data-onb-item={it.id} data-state={it.state}>
              <div class="main">
                <div class="tl">
                  <strong>{it.title}</strong>
                  {#if it.stateText}<span class="chip {it.state === 'accepted' || it.state === 'changed' ? 'chip-ok' : it.state === 'dismissed' ? 'chip-neutral' : 'chip-waiting'}">{it.stateText}</span>{/if}
                  {#if it.confidenceText}<span class="small muted soft">{it.confidenceText}</span>{/if}
                </div>
                {#if it.detail}<p class="small muted">{sade(it.detail)}</p>{/if}
                {#if connectorOf[it.id]}<p class="small okline"><Icon name="check" size={14} strokeWidth={2.2} />{t('knConnector', { s: connectorOf[it.id].state === 'enabled' ? t('knCapEnabled') : (connectorOf[it.id].state ?? '') })}</p>{/if}
                {#if why[it.id]}
                  {@const w = why[it.id]}
                  <div class="why" data-why>
                    {#if w === 'loading'}<span class="small muted">{t('loading')}</span>
                    {:else if typeof w === 'string'}<p class="small err-line">{w}</p>
                    {:else}
                      <p class="small">{w.answer}</p>
                      {#if w.confidenceText}<p class="small muted">{w.confidenceText}</p>{/if}
                      <ul class="ev small muted">{#each w.evidence as ev, i (i)}<li>{ev.what}{#if ev.where}<span class="mono">{' '}{ev.where}</span>{/if}{#if ev.when}<span>{' '}· {ago(ev.when)}</span>{/if}</li>{/each}</ul>
                    {/if}
                  </div>
                {/if}
                {#if rowErr[it.id]}<p class="small err-line" role="alert">{rowErr[it.id]}</p>{/if}
              </div>
              <div class="acts">
                {#if it.state === 'dismissed'}
                  <button class="btn btn-sm" onclick={() => act(it, 'restore')} disabled={busy[it.id]} data-onb-restore>{t('knRestore')}</button>
                {:else}
                  {#if it.state !== 'accepted'}<button class="btn btn-primary btn-sm" onclick={() => act(it, 'accept')} disabled={busy[it.id]} data-onb-accept>{t('knAccept')}</button>{/if}
                  <button class="btn btn-quiet btn-sm" onclick={() => act(it, 'dismiss')} disabled={busy[it.id]} data-onb-dismiss>{t('knDismiss')}</button>
                {/if}
                <button class="btn btn-quiet btn-sm" aria-expanded={!!why[it.id]} onclick={() => toggleWhy(it)} data-onb-why>{t('knWhy')}</button>
              </div>
            </li>
          {/each}
        </ul>
      </section>
    {/each}
  {/if}

  <section class="grp" aria-labelledby="kg-caps" data-capabilities>
    <h2 class="h2" id="kg-caps">{t('knCapsTitle')}</h2>
    <p class="small muted">{t('knCapsLead')}</p>
    {#if caps.length === 0}
      <p class="small muted">{rg.loaded ? t('knCapsNone') : t('loading')}</p>
    {/if}
    <ul class="items">
      {#each caps as e (e.id)}
        <li class="it" data-cap={e.id} data-state={e.state}>
          <div class="main">
            <div class="tl">
              <strong>{titleOf(e)}</strong>
              <span class="chip {e.state === 'enabled' ? 'chip-ok' : e.state === 'suggested' ? 'chip-waiting' : 'chip-neutral'}">{stateText(e)}</span>
              {#if e.state === 'suggested' && e.declaredState === 'suggested'}<span class="small muted soft">{t('knCapClosed')}</span>{/if}
            </div>
            {#if describeOf(e)}<p class="small muted">{describeOf(e)}</p>{/if}
            {#if e.permissions?.length}
              <ul class="perm small muted">{#each e.permissions as p, i (i)}<li>{sade(p.why?.tr ?? `${p.cap}: ${p.scope}`)}</li>{/each}</ul>
            {/if}
            {#if e.problems?.length}<p class="small err-line">{e.problems.join(' ')}</p>{/if}
            {#if capErr[e.id]}<p class="small err-line" role="alert">{capErr[e.id]}</p>{/if}
          </div>
          <div class="acts">
            {#if e.state !== 'enabled'}
              <button class="btn btn-primary btn-sm" onclick={() => setCap(e, true)} disabled={capBusy[e.id]} data-cap-on>{t('knUse')}</button>
            {:else if canRevoke(e)}
              <button class="btn btn-sm" onclick={() => setCap(e, false)} disabled={capBusy[e.id]} data-cap-off>{t('knCapClose')}</button>
            {/if}
          </div>
        </li>
      {/each}
    </ul>
  </section>
</div>

<style>
  .last { margin-top: 6px; }
  .okline { display: flex; align-items: center; gap: 6px; color: var(--ok); font-weight: var(--fw-medium); margin: 10px 0; }
  .grp { margin-top: 30px; }
  .grp > .h2 { display: flex; align-items: baseline; gap: 10px; margin-bottom: 4px; }
  .items { display: flex; flex-direction: column; margin-top: 10px; border-bottom: 1px solid var(--line); }
  .it { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 8px 16px; padding: 12px 0; border-top: 1px solid var(--line); }
  .it.st-dismissed .tl strong { color: var(--ink-3); text-decoration: line-through; text-decoration-thickness: 1px; }
  .main { flex: 1 1 22rem; min-width: 0; display: flex; flex-direction: column; gap: 4px; }
  .tl { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; }
  .soft { opacity: 1; }
  .acts { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 6px; }
  .why { display: flex; flex-direction: column; gap: 4px; padding: 8px 12px; border-radius: var(--radius); background: var(--bg-sunken); }
  .ev { display: flex; flex-direction: column; gap: 2px; }
  .perm { display: flex; flex-direction: column; gap: 2px; padding-left: 14px; list-style: disc; }
  .notice { margin-top: -8px; }
</style>
