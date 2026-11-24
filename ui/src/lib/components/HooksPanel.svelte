<script lang="ts">
  import { getHooksHealth } from '../api2'
  import { errText3, installHooks } from '../api3'
  import { realProjects } from '../surface.svelte'
  import { t } from '../i18n/index.svelte'
  import { app, toast } from '../store.svelte'
  import type { HooksHealth } from '../types'
  import { ago, copyText, errText, fmtDur } from '../util'
  import Icon from './Icon.svelte'

  const installCmd = 'soz hooks install --settings "C:\\projeler\\ornek\\.claude\\settings.json"'
  const removeCmd = 'soz hooks uninstall --settings "C:\\projeler\\ornek\\.claude\\settings.json"'

  /** Kanca adının sade Türkçesi (ham ad ipucunda kalır). */
  const HOOK_LABEL: Record<string, string> = { 'session-start': 'oturum başlangıcı', 'user-prompt-submit': 'istem kaydı', stop: 'kanıt denetimi', notification: 'bekleme işareti', 'pre-tool-use': 'bekçi' }
  const hookLabel = (k: string) => HOOK_LABEL[k] ?? k

  let h = $state<HooksHealth | null>(null)
  let err = $state('')
  let busy = $state(false)

  async function load() {
    busy = true
    try {
      h = await getHooksHealth()
      err = ''
    } catch (e) {
      err = errText(e)
    } finally {
      busy = false
    }
  }

  $effect(() => {
    void app.tick
    void load()
  })

  let hooking = $state<number | null>(null)
  let hookMsg = $state<Record<number, string>>({})
  async function installFor(id: number) {
    hooking = id
    try {
      await installHooks(id)
      hookMsg[id] = t('hookDone')
      toast(t('hookDone'))
      void load()
    } catch (e) {
      hookMsg[id] = errText3(e)
    } finally {
      hooking = null
    }
  }

  async function copyCmd(c: string) {
    toast((await copyText(c)) ? t('copied') : t('copyFailed'))
  }

  // Hiçbir klasörde terminal desteği kurulu değilken "sağlıklı" denmez (B-DIGER-25).
  const anyInstalled = $derived((h?.files ?? []).some((f) => f.exists && f.hooks.length > 0))
  const scope = (f: HooksHealth['files'][number]) =>
    f.scope === 'user' ? t('hkScopeUser') : f.scope === 'project' ? t('hkScopeProject', { p: f.projectName ?? '' }) : f.scope
</script>

<div class="hp">
  <p class="hooklead">{t('hooksLead')}</p>

  <div class="proj" data-hooks-install>
    <p class="small muted">{t('hookLead')}</p>
    {#if realProjects().length === 0}
      <p class="small muted">{t('hookNoProjects')}</p>
    {:else}
      <ul class="plist">
        {#each realProjects() as p (p.id)}
          <li>
            <span class="pn"><Icon name="folder" size={16} />{p.name}</span>
            <button class="btn btn-sm" onclick={() => installFor(p.id)} disabled={hooking === p.id} data-hook-btn>{hooking === p.id ? t('hookSeting') : t('hookProject')}</button>
            {#if hookMsg[p.id]}<span class="small muted" role="status">{hookMsg[p.id]}</span>{/if}
          </li>
        {/each}
      </ul>
    {/if}
  </div>

  {#if err && !h}
    <p class="small err-line" role="alert">{t('hooksLoadErr', { m: err })}</p>
  {/if}

  {#if h}
    <div class="status" role="status">
      {#if h.ok && !anyInstalled}
        <span class="chip chip-neutral" data-hooks-none><Icon name="question" size={14} />{t('hooksNotInstalled')}</span>
      {:else if h.ok}
        <span class="chip chip-ok"><Icon name="check" size={14} />{t('hooksHealthy')}</span>
      {:else}
        <span class="chip chip-stalled"><Icon name="alert" size={14} />{t('hooksProblem')}</span>
      {/if}
      <span class="small muted">{t('hooksCheckedAt', { t: ago(h.checkedAt) })}</span>
      <button class="btn btn-quiet btn-sm" onclick={load} disabled={busy}><Icon name="refresh" size={16} />{t('hooksRecheck')}</button>
    </div>

    {#if h.gateProblem}<p class="err-line" role="alert">{t('hooksGate', { m: h.gateProblem })}</p>{/if}

    {#if h.warnings.length > 0}
      <div class="warns">
        <span class="label">{t('hooksWarnings')}</span>
        <ul>
          {#each h.warnings as w (w)}<li class="small">{w}</li>{/each}
        </ul>
      </div>
    {/if}

    <details class="more" open={!h.ok}>
      <summary>{t('hooksDetails')}</summary>

      <h3 class="h3">{t('hooksFiles')}</h3>
      {#if h.files.length === 0}
        <p class="small muted">{t('hooksNoFiles')}</p>
      {:else}
        <ul class="files">
          {#each h.files as f (f.path)}
            <li class="file">
              <p class="fscope"><strong>{scope(f)}</strong>{#if !f.exists}<span class="chip chip-stalled"><Icon name="alert" size={14} />{t('hkNoFile')}</span>{/if}</p>
              <p class="mono fpath small">{f.path}</p>
              {#if f.readError}<p class="small err-line">{t('hkReadErr', { m: f.readError })}</p>{/if}
              {#if f.hooks.length > 0}
                <p class="small"><span class="muted">{t('hkInstalled')}:</span> {#each f.hooks as k (k)}<code class="hk" title={k}>{hookLabel(k)}</code>{/each}</p>
              {/if}
              <ul class="checks small">
                <li class:bad={f.exeExists === false} class:unk={f.exeExists === null}>
                  <Icon name={f.exeExists === true ? 'check' : f.exeExists === false ? 'x' : 'question'} size={14} />
                  {f.exeExists === true ? t('hkExeOk') : f.exeExists === false ? t('hkExeMissing') : t('hkExeUnknown')}
                  {#if f.exePath}<span class="mono muted">{f.exePath}</span>{/if}
                </li>
                <li class:bad={f.dataMatches === false} class:unk={f.dataMatches === null}>
                  <Icon name={f.dataMatches === true ? 'check' : f.dataMatches === false ? 'x' : 'question'} size={14} />
                  {f.dataMatches === true ? t('hkDataOk') : f.dataMatches === false ? t('hkDataDiff', { d: f.dataDir ?? '?' }) : t('hkDataUnknown')}
                </li>
              </ul>
            </li>
          {/each}
        </ul>
      {/if}

      <h3 class="h3">{t('hkStats')}</h3>
      {#if h.stats.length === 0}
        <p class="small muted">{t('hkNoStats')}</p>
      {:else}
        <ul class="stats">
          {#each h.stats as s (s.name)}
            <li class="stat">
              <p class="sname"><code class="hk" title={s.name}>{s.label ?? hookLabel(s.name)}</code>
                <span class="small">{t('hkRuns', { n: s.runs24h })}</span>
                <span class="small" class:badn={s.errors24h > 0}>{t('hkErrors', { n: s.errors24h })}</span>
                <span class="small" class:badn={s.timeouts24h > 0}>{t('hkTimeouts', { n: s.timeouts24h })}</span>
                {#if s.maxMs > 0}<span class="small muted">{t('hkMax', { ms: fmtDur(s.maxMs) })}</span>{/if}
              </p>
              {#if s.lastRunAt}<p class="small muted">{t('hkLastRun', { t: ago(s.lastRunAt) })}{#if s.lastResult} · {s.lastResult}{/if}</p>{/if}
              {#if s.lastError}<p class="small err-line">{t('hkLastErr', { t: s.lastErrorAt ? ago(s.lastErrorAt) : '?', m: s.lastError })}</p>{/if}
            </li>
          {/each}
        </ul>
      {/if}
    </details>
  {/if}

  <details class="more">
    <summary>{t('hooksInstallTitle')}</summary>
    <p class="small muted" data-hooks-cmd-why>{t('hooksCmdWhy')}</p>
    <div class="cmd">
      <span class="label">{t('hooksInstall')}</span>
      <div class="code"><code>{installCmd}</code><button class="btn btn-quiet btn-sm" aria-label={`${t('copy')}: ${t('hooksInstall')}`} onclick={() => copyCmd(installCmd)}><Icon name="copy" size={16} /></button></div>
    </div>
    <div class="cmd">
      <span class="label">{t('hooksUninstall')}</span>
      <div class="code"><code>{removeCmd}</code><button class="btn btn-quiet btn-sm" aria-label={`${t('copy')}: ${t('hooksUninstall')}`} onclick={() => copyCmd(removeCmd)}><Icon name="copy" size={16} /></button></div>
    </div>
    <p class="small muted">{t('hooksPathNote')}</p>
  </details>
</div>

<style>
  .hp { display: flex; flex-direction: column; gap: 14px; }
  .proj { display: flex; flex-direction: column; gap: 8px; }
  .plist { display: flex; flex-direction: column; border-top: 1px solid var(--line); }
  .plist li { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; padding: 8px 0; border-bottom: 1px solid var(--line); }
  .pn { display: inline-flex; align-items: center; gap: 8px; font-weight: var(--fw-medium); min-width: 8rem; }
  .hooklead { max-width: 38rem; text-wrap: pretty; }
  .status { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 14px; }
  .warns ul { margin: 4px 0 0; padding-left: 1.1rem; list-style: disc; }
  .more > summary { cursor: pointer; font-weight: var(--fw-strong); min-height: 36px; display: flex; align-items: center; }
  .more[open] > summary { margin-bottom: 6px; }
  .h3 { font-family: var(--sans); font-weight: var(--fw-strong); font-size: var(--fs-md); margin: 14px 0 6px; }
  .files, .stats { display: flex; flex-direction: column; border-top: 1px solid var(--line); }
  .file, .stat { padding: 12px 0; border-bottom: 1px solid var(--line); display: flex; flex-direction: column; gap: 4px; min-width: 0; }
  .fscope { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 0; }
  .fpath { margin: 0; word-break: break-all; color: var(--ink-2); font-size: var(--fs-xs); }
  .hk { font-family: var(--mono); font-size: var(--fs-xs); padding: 1px 7px; margin-right: 6px; border-radius: 6px; background: var(--bg-sunken); }
  .checks { display: flex; flex-direction: column; gap: 3px; margin: 2px 0 0; }
  .checks li { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; color: var(--ink-2); }
  .checks li :global(svg) { color: var(--ok); flex: none; }
  .checks li.bad { color: var(--stalled); font-weight: var(--fw-strong); }
  .checks li.bad :global(svg) { color: var(--stalled); }
  .checks li.unk :global(svg) { color: var(--ink-3); }
  .sname { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 12px; margin: 0; }
  .badn { color: var(--stalled); font-weight: var(--fw-strong); }
  .cmd { display: flex; flex-direction: column; gap: 6px; margin-bottom: 10px; }
  .code {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 8px 8px 8px 14px;
    border-radius: var(--radius);
    background: var(--bg-sunken);
  }
  .code code { font-family: var(--mono); font-size: var(--fs-xs); overflow-wrap: anywhere; line-height: 1.5; }
</style>
