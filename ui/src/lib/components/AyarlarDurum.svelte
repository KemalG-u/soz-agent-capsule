<script lang="ts">
  import { getPhonePairing, renewPhonePairing } from '../api2'
  import { t } from '../i18n/index.svelte'
  import { qrMatrix, qrPath } from '../qr'
  import { app, toast } from '../store.svelte'
  import { loadAgents, sf } from '../surface.svelte'
  import { yardimciDurumu } from '../gezinme'
  import type { PhonePairing } from '../types'
  import { ago, copyText, errText, toolName } from '../util'
  import HooksPanel from './HooksPanel.svelte'
  import Icon from './Icon.svelte'
  import ImportPanel from './ImportPanel.svelte'
  import PricesPanel from './PricesPanel.svelte'

  // Ayar olmayan, durum gösteren parçalar: bulunan araçlar, kayıt klasörleri, telefon eşleştirme, fiyat tablosu, kanca kurulumu, veri klasörü, gizlilik.
  // Ayarın kendisi (açma kapama, yol, sayı) kayıt defterinden çizilen listededir; burada yalnız durum ve eşleştirme vardır.
  $effect(() => {
    void app.tick
    void loadAgents()
  })

  let pairing = $state<PhonePairing | null>(null)
  let pairErr = $state('')
  let renewing = $state(false)
  let confirmRenew = $state(false)
  const lan = $derived(!!app.settings?.lanEnabled)
  const qr = $derived(pairing?.url ? qrMatrix(pairing.url) : null)
  const qrSvg = $derived(qr ? qrPath(qr) : null)

  async function loadPairing() {
    try {
      pairing = await getPhonePairing()
      pairErr = ''
    } catch (e) {
      pairErr = errText(e)
    }
  }
  $effect(() => {
    void app.tick
    void lan
    if (lan) void loadPairing()
    else pairing = null
  })

  async function renew() {
    if (!confirmRenew) {
      confirmRenew = true
      setTimeout(() => (confirmRenew = false), 5000)
      return
    }
    confirmRenew = false
    renewing = true
    try {
      pairing = await renewPhonePairing()
      toast(t('phoneRenewed'))
    } catch (e) {
      pairErr = errText(e)
    } finally {
      renewing = false
    }
  }
  async function copyUrl() {
    if (pairing?.url) toast((await copyText(pairing.url)) ? t('copied') : t('copyFailed'))
  }
</script>

<div class="durum" data-status-panels>
  <details class="blk" id="s-agents" data-agents>
    <summary class="h2s">{t('setAgentsTitle')}</summary>
    <div class="body">
      <p class="small muted">{t('setAgentsLead')}</p>
      <ul class="agents">
        {#each sf.agents as a (a.id)}
          {@const hz = sf.ready?.agents.find((r) => r.id === a.id)}
          {@const yd = yardimciDurumu(a.found, hz)}
          <li data-agent-row={a.id}>
            <div class="sname">
              <strong>{a.name}</strong>
              {#if yd === 'hazir'}<span class="chip chip-ok"><Icon name="check" size={14} />{t('agentReady')}</span>{:else if yd === 'hazirDegil'}<span class="chip chip-waiting" title={hz?.sentence ?? ''} data-agent-state="hazirDegil"><Icon name="alert" size={14} />{t('agentNotReady')}</span>{:else}<span class="chip chip-stalled"><Icon name="alert" size={14} />{t('agentNotFound')}</span>{/if}
              {#if a.version}<span class="small muted">{t('agentVersion', { v: a.version })}</span>{/if}
            </div>
            {#if a.path}<p class="path small muted">{a.path}</p>{/if}
            {#if !a.found && a.error}<p class="small muted">{a.error}</p>{/if}
            <ul class="caps small muted">
              <li>{t(`agGuard_${a.capabilities.guardTier}` as never)}</li>
              <li>{t(`agEnd_${a.capabilities.turnEnd}` as never)}</li>
              <li>{t(`agCost_${a.capabilities.cost}` as never)}</li>
            </ul>
          </li>
        {/each}
        {#if sf.agents.length === 0}<li class="small muted">{t('stNoAgents')}</li>{/if}
      </ul>
    </div>
  </details>

  <details class="blk" id="s-src">
    <summary class="h2s">{t('setSources')}</summary>
    <div class="body">
      <p class="small muted">{t('setSourcesLead')}</p>
      <ul class="agents">
        {#each app.summary?.sources ?? [] as src (src.tool)}
          <li>
            <div class="sname">
              <strong>{toolName(src.tool)}</strong>
              {#if src.found}<span class="chip chip-ok"><Icon name="check" size={14} />{t('srcFound')}</span>{:else}<span class="chip chip-stalled"><Icon name="alert" size={14} />{t('srcMissing')}</span>{/if}
              {#if src.found}<span class="small muted">{t('srcFiles', { n: src.files })}</span>{/if}
              {#if src.unrecognized > 0}<span class="chip chip-waiting" title={t('srcUnrecognizedTitle')}><Icon name="alert" size={14} />{t('srcUnrecognized', { n: src.unrecognized })}</span>{/if}
            </div>
            <p class="path small muted">{src.root}</p>
          </li>
        {/each}
      </ul>
      <div class="importrow">
        <ImportPanel variant="inline" />
        <p class="small muted">{t('srcLastImport')}: {app.summary?.lastImportAt ? ago(app.summary.lastImportAt) : t('srcNever')}</p>
      </div>
    </div>
  </details>

  <details class="blk" id="s-phone">
    <summary class="h2s">{t('setPhone')}</summary>
    <div class="body">
      <p class="small phonewarn" data-phone-warn><Icon name="alert" size={14} />{t('phoneWarn')}</p>
      {#if !lan}
        <p class="small muted">{t('phoneOff')}</p>
      {:else}
        {#if pairErr}<p class="small err-line" role="alert">{pairErr}</p>{/if}
        {#if pairing && pairing.enabled && pairing.listening === false}
          <p class="small err-line" role="alert">{pairing.error || t('phoneNotListening')}</p>
        {:else if pairing?.error}
          <p class="small err-line" role="alert">{pairing.error}</p>
        {/if}
        {#if pairing?.url}
          <div class="pair">
            {#if qrSvg}
              <svg class="qr" viewBox={`0 0 ${qrSvg.size} ${qrSvg.size}`} role="img" aria-label={t('phoneQrAlt')} shape-rendering="crispEdges">
                <rect width={qrSvg.size} height={qrSvg.size} fill="#ffffff" />
                <path d={qrSvg.d} fill="#000000" />
              </svg>
            {/if}
            <div class="pinfo">
              <span class="label">{t('phoneAddress')}</span>
              <div class="code"><code>{pairing.url}</code><button class="btn btn-quiet btn-sm" aria-label={t('copy')} onclick={copyUrl}><Icon name="copy" size={16} /></button></div>
              <p class="small muted">{t('phoneScan')}</p>
              <div class="pbtn"><button class="btn btn-sm" class:danger={confirmRenew} onclick={renew} disabled={renewing}><Icon name="refresh" size={16} />{confirmRenew ? t('phoneRenewConfirm') : t('phoneRenew')}</button></div>
            </div>
          </div>
        {:else if !pairErr && !pairing?.error}
          <p class="small muted">{t('phoneNoAddress')}</p>
        {/if}
        <p class="small muted">{t('phoneLimits')}</p>
      {/if}
    </div>
  </details>

  <details class="blk" id="s-price">
    <summary class="h2s">{t('setPrices')}</summary>
    <div class="body"><PricesPanel /></div>
  </details>

  <details class="blk" id="s-hook">
    <summary class="h2s">{t('setHooks')}</summary>
    <div class="body"><HooksPanel /></div>
  </details>

  <details class="blk" id="s-data">
    <summary class="h2s">{t('setData')}</summary>
    <div class="body">
      <p class="small muted">{t('dataDirLead')}</p>
      <div class="code"><code>{app.dataDir}</code><button class="btn btn-quiet btn-sm" aria-label={t('copy')} onclick={async () => toast((await copyText(app.dataDir)) ? t('copied') : t('copyFailed'))}><Icon name="copy" size={16} /></button></div>
      <p class="small muted">{app.dataDirSource ? t('dataDirWhy', { m: app.dataDirSource }) : t('dataDirWhyUnknown')}</p>
    </div>
  </details>

  <details class="blk" id="s-priv">
    <summary class="h2s">{t('setPrivacy')}</summary>
    <div class="body">
      <p class="privmain"><Icon name="lock" size={22} />{t('privacyMain')}</p>
      <p class="muted">{t('privacyBody', { dir: app.dataDir })}</p>
      {#if app.version}<p class="small muted">{t('version', { v: app.version })}</p>{/if}
    </div>
  </details>
</div>

<style>
  .phonewarn { display: flex; align-items: flex-start; gap: 8px; margin-bottom: 8px; color: var(--ink); font-weight: var(--fw-medium); max-width: 60ch; }
  .phonewarn :global(svg) { flex: none; margin-top: 2px; color: var(--waiting); }
  .durum { display: flex; flex-direction: column; }
  .blk { border-top: 1px solid var(--line); }
  .blk > summary { cursor: pointer; min-height: var(--row-s); display: flex; align-items: center; font-weight: var(--fw-strong); }
  .body { display: flex; flex-direction: column; gap: 14px; padding: 4px 0 20px 16px; max-width: 42rem; }
  .agents { display: flex; flex-direction: column; gap: 14px; }
  .caps { display: flex; flex-direction: column; gap: 2px; margin-top: 6px; }
  .sname { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 12px; }
  .path { margin-top: 2px; font-family: var(--mono); font-size: var(--fs-xs); }
  .importrow { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 12px 20px; }
  .code { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 8px 8px 8px 14px; border-radius: var(--radius); background: var(--bg-sunken); }
  .code code { font-family: var(--mono); font-size: var(--fs-xs); overflow-wrap: anywhere; line-height: 1.5; }
  .pair { display: flex; flex-wrap: wrap; gap: 18px 24px; align-items: flex-start; }
  .qr { width: 11rem; height: 11rem; flex: none; border: 1px solid var(--line-strong); border-radius: 8px; background: #fff; }
  .pinfo { display: flex; flex-direction: column; gap: 8px; flex: 1 1 15rem; min-width: 0; }
  .pbtn { display: flex; gap: 8px; }
  .danger { border-color: var(--stalled); color: var(--stalled); font-weight: var(--fw-strong); }
  .privmain { display: flex; align-items: center; gap: 10px; font-weight: var(--fw-strong); font-size: var(--fs-lg); letter-spacing: -0.01em; }
</style>
