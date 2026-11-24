<script lang="ts">
  import type { Snippet } from 'svelte'
  import { t, type Key } from '../i18n/index.svelte'
  import { selectedJob, sf } from '../surface.svelte'
  import { claimCompare, withoutClaim } from '../claim'
  import { firstFailure, parseCounts } from '../testsummary'
  import type { FileDiff, ReasonCode, TimelineItem } from '../types'
  import { fmtDur, parseTs } from '../util'
  import { kirp, tekBosluk } from '../kirp'
  import { sadeKomut } from '../gezinme'
  import Icon from './Icon.svelte'

  let { item, actions, decision = false, accepted = null }: { item: TimelineItem; actions?: Snippet; decision?: boolean; accepted?: { via: string | null; process: string | null; panel: boolean } | null } = $props()

  // Ekran görüntüsü resimdir; komut çıktısı kaydı gibi diğer dosyalar bağlantı olur.
  const isImage = (u: string) => u.startsWith('data:image') || /\.(png|jpe?g|webp|gif|svg)(\?|$)/i.test(u)
  const p = $derived(item.proof!)
  const total = $derived(p.results.length)
  const passed = $derived(p.results.filter((r) => r.status === 'passed').length)

  // Kanıtsızlığın nedeni: sunucunun sade cümlesi esastır, yoksa kod karşılığı.
  const reasonKey: Record<ReasonCode, Key> = {
    no_checks: 'reasonNoChecks',
    no_tests: 'reasonNoTests',
    definition_changed: 'reasonDefinition',
    tests_changed: 'reasonTests',
    count_dropped: 'reasonDropped',
    checks_changed: 'reasonChecks',
    interrupted: 'reasonInterrupted',
    not_tracked: 'reasonNotTracked',
    deps_changed: 'reasonDeps',
    no_base: 'reasonNoBase',
    approved_failed: 'pcHeadApprovedFailed',
    agent_tests_fail: 'pcHeadAgentTests',
    verdict_unavailable: 'pcUncheckedBody',
    no_change: 'pcNoChangeBody',
    no_work: 'pcNoWorkBody',
    ignored_only: 'pcNoSourceBody',
  }
  const code = $derived<ReasonCode | null>(p.reasonCode ?? (p.verdict === 'unchecked' && total === 0 ? 'no_checks' : null))
  // Yardımcının "dosya değiştirdim" sözü ile Söz'ün klasörde bulduğu: tek cümle çifti, özetten ayrılıp kendi kutusunda vurgulu gösterilir.
  const claim = $derived(claimCompare(p.summary, p.reasonText))
  // Deneme olmadı ama yardımcı "bitti" demişti: kişi "bitti" sözüne değil denemeye bakarak karar versin (K-17/18). Sunucunun dosya karşılaştırması yoksa,
  // yardımcının bu denemeden önceki son sözü ile Söz'ün denemesinin sonucu yan yana gösterilir. Yardımcının sözü yoksa kutu çizilmez.
  const said = $derived.by(() => {
    if (p.verdict !== 'failed') return ''
    const list = selectedJob() ? (sf.timelines[selectedJob()!.id] ?? []) : []
    const at = list.findIndex((x) => x.id === item.id)
    const before = at >= 0 ? list.slice(0, at) : list
    const last = [...before].reverse().find((x) => x.kind === 'agent' && x.text.trim())
    return kirp(tekBosluk(last?.text), 180)
  })
  const foundTry = $derived.by(() => {
    const bad = p.results.find((r) => r.status !== 'passed' && r.status !== 'skipped')
    const line = bad ? sentence(bad) : null
    return line ? t('pcFoundTry', { s: line }) : t('pcFoundTryAll', { a: total, b: passed })
  })
  const reason = $derived(withoutClaim(p.reasonText) || (code ? t(reasonKey[code]) : t('pcUncheckedBody')))
  const changed = $derived(p.changedFiles ?? [])
  const added = $derived(p.addedTests ?? [])
  const tamper = $derived(code === 'definition_changed' || code === 'tests_changed' || code === 'count_dropped' || code === 'deps_changed')
  const diffs = $derived(p.diffs ?? [])
  // Ajanın yazdığı denemeyi kabul ettiysen, onu çalıştırmak için package.json değişmiştir: kart bunu açıkça söyler (kapı gevşetilmez, kart bir kez çıkar).
  const agentTestNote = $derived.by(() => {
    if (code !== 'definition_changed') return false
    if (!changed.some((f) => /(^|[\/])package.json$/i.test(f))) return false
    const pid = selectedJob()?.projectId
    return pid !== undefined && (sf.checks[pid] ?? []).some((c) => c.origin === 'agent-test' && c.enabled)
  })
  const notes = $derived(p.notes ?? [])

  // Onayladığın testlerle verilen sonuç: dört dal (sunucu cümlesi aynen gösterilir, burada yalnız yerleşim var).
  // Ajan hiçbir dosyayı değiştirmedi (no_change) ya da yalnız izlenmeyen klasörlere yazdı (ignored_only): sakin kart, cümle sunucudan.
  const ignoredOnly = $derived(code === 'ignored_only' && p.verdict === 'passed')
  const noChange = $derived((code === 'no_change' || code === 'ignored_only') && p.verdict === 'passed')
  // Klasörde hiçbir şey değişmedi ama iş değişiklik istiyordu (no_work, hüküm unchecked): uyarı rengi, sunucunun cümlesi görünür. Gri "Hiçbir dosya değişmedi" yalnız no_change içindir.
  const noWork = $derived(code === 'no_work' && p.verdict === 'unchecked')
  const approvedFailed = $derived(code === 'approved_failed')
  const agentFail = $derived(code === 'agent_tests_fail')
  const approvedFailDiff = $derived(diffs.find((d) => d.kind === 'approved-fail') ?? null)
  const agentFailDiff = $derived(diffs.find((d) => d.kind === 'agent-fail') ?? null)
  // Bilgi türleri alarm değildir: ajanın test dosyasında ne değiştirdiği, eklediği sınama ayarı.
  const infoDiffs = $derived(diffs.filter((d) => d.kind === 'test' || d.kind === 'config-info'))
  const mainDiffs = $derived(diffs.filter((d) => !['test', 'config-info', 'approved-fail', 'agent-fail'].includes(d.kind)))
  const testsEdited = $derived(p.verdict === 'passed' && !noChange && (infoDiffs.length > 0 || added.length > 0))

  // "Geçti" ama sayı okunamadı ya da bazı testler atlanmış: tam yeşil değil, sarı-yeşil ve kendi cümlesiyle (denetim 3, Ö-1).
  const soft = $derived(p.verdict === 'passed' && !!p.countUnknown)
  const head = $derived(
    noChange ? t('grpNoChange')
    : noWork ? t('pcNoWork')
    : approvedFailed ? t('pcHeadApprovedFailed')
    : agentFail ? t('pcHeadAgentTests')
    : soft ? (p.verdictLabel || t('pcPassed'))
    : p.verdict === 'passed' ? t('pcPassed')
    : p.verdict === 'failed' ? t('pcFailed')
    : p.verdict === 'error' ? t('pcError')
    : code === 'not_tracked' ? t('pcHeadNotTracked')
    : code === 'no_base' ? t('pcHeadNoBase')
    : tamper ? t('pcHeadDecision')
    : code === 'interrupted' ? t('pcHeadInterrupted')
    : code === 'checks_changed' ? t('pcHeadChecks')
    : t('pcUnchecked'),
  )
  const sub = $derived(
    noChange ? (withoutClaim(p.reasonText) || withoutClaim(p.summary) || t(ignoredOnly ? 'pcNoSourceBody' : 'pcNoChangeBody'))
    : noWork ? (withoutClaim(p.summary) || withoutClaim(p.reasonText) || t('pcNoWorkBody'))
    : p.verdict === 'unchecked' ? reason : p.verdict === 'passed' ? t('pcAll', { n: total }) : total > 0 ? t('pcOf', { a: total, b: passed }) : withoutClaim(p.summary),
  )
  // Yalnız az önce gelen kartta "geçti" anı oynar; eski kayıtlar sessiz açılır.
  const fresh = $derived(Date.now() - parseTs(item.ts) < 12_000)

  /** Her sonuç için tek cümle: kaç test geçti, kaç tane kaldı, kaç tane atlandı. */
  function sentence(r: { outputTail?: string | null; testsTotal?: number | null; testsPassed?: number | null; testsSkipped?: number | null }): string | null {
    // Önce sunucunun okuduğu sayı; yoksa çıktı metninden. Sayı biliniyorsa "okunamadı" yazılmaz (denetim 4, G).
    const c = r.testsTotal != null
      ? { passed: r.testsPassed ?? 0, skipped: r.testsSkipped ?? 0, failed: Math.max(0, r.testsTotal - (r.testsPassed ?? 0) - (r.testsSkipped ?? 0)) }
      : parseCounts(r.outputTail)
    if (!c) return null
    const parts: string[] = []
    if (c.passed) parts.push(t('pcSumPassed', { n: c.passed }))
    if (c.failed) parts.push(t('pcSumFailed', { n: c.failed }))
    if (c.skipped) parts.push(t('pcSumSkipped', { n: c.skipped }))
    return parts.length ? parts.join(', ') : null
  }
  let openOut = $state<Record<number, boolean>>({})
</script>

{#snippet diffBlock(d: FileDiff)}
  <div class="diff">
    <p class="small dpath"><span class="mono">{d.path}</span>{#if d.sentence}<span class="muted">{' · '}{d.sentence}</span>{/if}</p>
    {#if d.lines.length > 0}
      <pre class="calm dl" data-diff>{#each d.lines as l, i (i)}<span class="ln op-{l.op === '+' ? 'add' : l.op === '-' ? 'del' : 'ctx'}"><span class="sr-only">{l.op === '+' ? t('pcDiffAdded') : l.op === '-' ? t('pcDiffRemoved') : ''}{' '}</span><span aria-hidden="true">{l.op === '+' || l.op === '-' ? l.op : ' '}{' '}</span>{l.text}</span>{/each}</pre>
    {/if}
  </div>
{/snippet}

<article class="proof v-{p.verdict}" class:fresh class:tamper class:decision class:soft class:nochange={noChange} class:nowork={noWork} aria-label={head} data-proof={p.verdict} data-reason={code ?? undefined}>
  <header>
    <span class="seal" aria-hidden="true">
      {#if noChange}
        <Icon name="idle" size={20} />
      {:else if noWork}
        <Icon name="alert" size={20} strokeWidth={2.2} />
      {:else if p.verdict === 'passed'}
        <svg viewBox="0 0 24 24" width="22" height="22"><path class="draw" d="m5.5 12.5 4.2 4.2L18.5 7.8" /></svg>
      {:else if p.verdict === 'failed' || p.verdict === 'error'}
        <Icon name="x" size={20} strokeWidth={2.2} />
      {:else}
        <Icon name="unchecked" size={22} />
      {/if}
    </span>
    <div class="hd">
      <h4 class="head">{head}</h4>
      <p class="sub">{sub}</p>
    </div>
    <span class="att small">{t('attemptN', { n: p.attempt })}</span>
  </header>

  {#if claim}
    <div class="claim" class:diff={claim.mismatch} data-claim data-claim-mismatch={claim.mismatch ? '1' : '0'}>
      <Icon name={claim.mismatch ? 'alert' : 'info'} size={16} strokeWidth={2} />
      <div>
        <p class="claim-h">{claim.mismatch ? t('pcClaimDiff') : t('pcClaimSame')}</p>
        <p class="claim-t" data-claim-said>{claim.said}</p>
        <p class="claim-t strong" data-claim-found>{claim.found}</p>
      </div>
    </div>
  {/if}
  {#if !claim && said}
    <div class="claim diff" data-claim data-claim-failed data-claim-mismatch="1">
      <Icon name="alert" size={16} strokeWidth={2} />
      <div>
        <p class="claim-h">{t('pcClaimDiff')}</p>
        <p class="claim-t" data-claim-said>{t('pcSaid', { s: said })}</p>
        <p class="claim-t strong" data-claim-found>{foundTry}</p>
      </div>
    </div>
  {/if}
  {#if soft}
    <p class="note small" data-soft><Icon name="info" size={14} />{t('pcCountUnknown')}</p>
  {/if}
  {#if p.baselineText && !approvedFailed}
    <p class="note small" data-baseline><Icon name="info" size={14} />{p.baselineText}</p>
  {/if}

  {#if approvedFailDiff}
    <div class="failbox" data-approved-fail>
      <p class="small"><strong>{approvedFailDiff.sentence || t('pcApprovedFailTitle')}</strong></p>
      <ul class="names">{#each approvedFailDiff.lines as l, i (i)}<li class="mono">{l.text}</li>{/each}</ul>
    </div>
  {/if}
  {#if agentFailDiff}
    <div class="failbox" data-agent-fail>
      <p class="small"><strong>{agentFailDiff.sentence || t('pcAgentFailTitle')}</strong></p>
      <ul class="names">{#each agentFailDiff.lines as l, i (i)}<li class="mono">{l.text}</li>{/each}</ul>
    </div>
  {/if}
  {#if approvedFailed && infoDiffs.length > 0}
    <div class="diffs" data-agent-change>
      <span class="small muted">{t('pcAgentChangeTitle')}</span>
      {#each infoDiffs as d, i (`${i}:${d.kind}:${d.path}`)}{@render diffBlock(d)}{/each}
    </div>
  {/if}
  {#if mainDiffs.length > 0}
    <div class="diffs" data-diffs>
      <span class="small muted">{t('pcDiffTitle')}</span>
      {#each mainDiffs as d, i (`${i}:${d.kind}:${d.path}`)}{@render diffBlock(d)}{/each}
    </div>
  {/if}
  {#if changed.length > 0 && mainDiffs.length === 0 && !approvedFailDiff && !agentFailDiff}
    <div class="files" data-changed>
      <span class="small muted">{t('pcChanged')}</span>
      <ul>
        {#each changed as f (f)}<li class="mono">{f}</li>{/each}
      </ul>
    </div>
  {/if}
  {#if notes.length > 0}
    <div class="notes" data-notes>
      <span class="small muted">{t('pcNotesTitle')}</span>
      <ul>{#each notes as n (n)}<li class="small">{n}</li>{/each}</ul>
    </div>
  {/if}
  {#if added.length > 0}
    <p class="note small"><Icon name="info" size={14} />{t('pcAdded')}: <span class="mono">{added.join(', ')}</span></p>
  {/if}
  {#if testsEdited}
    <div class="note small edited" data-tests-edited>
      <Icon name="info" size={14} />
      <div>
        <p>{t('pcTestsEdited')}</p>
        {#if infoDiffs.length > 0}
          <details class="more-det">
            <summary class="small">{t('pcTestsEditedMore')}</summary>
            <div class="diffs inner">{#each infoDiffs as d, i (`${i}:${d.kind}:${d.path}`)}{@render diffBlock(d)}{/each}</div>
          </details>
        {/if}
      </div>
    </div>
  {/if}
  {#if agentTestNote}
    <p class="note small" data-agent-test-note><Icon name="info" size={14} />{t('pcAgentTestChanged')}</p>
  {/if}

  {#if total > 0 && approvedFailed}
    <details class="more-det own" data-own-run>
      <summary class="small">{t('pcAgentOwnRun')}</summary>
      <p class="small muted ownnote">{t('pcAgentOwnRunNote')}</p>
      {@render resultRows()}
    </details>
  {:else if total > 0}
    {@render resultRows()}
  {/if}

{#snippet resultRows()}
    <ul class="rows">
      {#each p.results as r, i (i)}
        {@const line = sentence(r)}
        {@const bad = r.status !== 'passed' && r.status !== 'skipped' ? firstFailure(r.outputTail) : null}
        <li class="row s-{r.status}">
          <span class="ri" aria-hidden="true"><Icon name={r.status === 'passed' ? 'check' : r.status === 'skipped' ? 'idle' : r.status === 'error' ? 'alert' : 'x'} size={14} strokeWidth={2.2} /></span>
          <span class="lab" title={r.label}>{sadeKomut(r.label, t('testCmdPlain'))}</span>
          {#if r.durationMs !== undefined}<span class="dur mono">{fmtDur(r.durationMs)}</span>{/if}
          {#if r.outputTail && (line || r.status !== 'passed')}
            <p class="sum small" data-summary>{line ?? t('pcNoCount')}</p>
          {/if}
          {#if r.outputTail}
            <div class="out">
              <button class="more small" aria-expanded={!!openOut[i]} onclick={() => (openOut[i] = !openOut[i])} data-detail-btn>{openOut[i] ? t('hideDetail') : t('showDetail')}</button>
              {#if openOut[i]}{#if bad}<p class="small bad" data-first-fail>{t('pcFirstFail', { l: bad })}</p>{/if}<pre class="calm" data-detail>{r.outputTail}</pre>{/if}
            </div>
          {/if}
          {#if r.artifactUrl && (r.artifactKind ? r.artifactKind === 'image' : isImage(r.artifactUrl))}
            <a class="shotlink" href={r.artifactUrl} target="_blank" rel="noopener"><img class="shot" src={r.artifactUrl} alt={t('pcShot', { l: r.label })} loading="lazy" /></a>
          {:else if r.artifactUrl}
            <a class="small outlink" href={r.artifactUrl} target="_blank" rel="noopener">{t('pcFullOutput')}</a>
          {/if}
        </li>
      {/each}
    </ul>
{/snippet}

  {#if accepted}
    {#if accepted.panel}
      <p class="small muted via" data-via>{t('pcAcceptVia', { v: accepted.via ?? '' })}</p>
    {:else}
      <p class="warnbox small" data-not-panel role="note"><Icon name="alert" size={14} /><span><strong>{t('pcNotPanel')}</strong> {t('pcNotPanelBy', { p: accepted.process || accepted.via || '?' })}</span></p>
    {/if}
  {/if}

  {#if actions}
    <div class="act">{@render actions()}</div>
  {:else if p.verdict === 'unchecked' && (code === 'no_checks' || code === 'no_tests')}
    <div class="act"><button class="btn btn-sm" onclick={() => (sf.checksOpen = true)}>{t('pcAddChecks')}</button></div>
  {/if}
</article>

<style>
  .proof { --c: var(--idle); --tint: var(--idle-tint); border: 1px solid var(--line); border-left: 3px solid var(--c); border-radius: var(--radius-lg); background: var(--surface); overflow: clip; max-width: 44rem; }
  .proof.v-passed { --c: var(--ok); --tint: var(--ok-tint); }
  .proof.v-passed.soft { --c: color-mix(in srgb, var(--ok) 55%, var(--waiting)); --tint: color-mix(in srgb, var(--ok-tint) 55%, var(--waiting-tint)); }
  .proof.v-passed.nochange { --c: var(--idle); --tint: var(--idle-tint); }
  .proof.v-unchecked.nowork { border-style: solid; }
  .proof.nowork .head { color: var(--waiting); }
  .proof.v-failed, .proof.v-error { --c: var(--stalled); --tint: var(--stalled-tint); }
  .proof.v-unchecked { --c: var(--waiting); --tint: var(--waiting-tint); border-left-style: dashed; border-top-style: dashed; border-right-style: dashed; border-bottom-style: dashed; border-color: var(--waiting); }
  .proof.decision { border-style: solid; border-width: 1px 1px 1px 3px; box-shadow: var(--shadow); }
  header { display: flex; align-items: flex-start; gap: 12px; padding: 12px 14px; background: var(--tint); }
  .seal { flex: none; display: inline-flex; align-items: center; justify-content: center; width: 34px; height: 34px; border-radius: 50%; border: 2px solid var(--c); color: var(--c); background: var(--surface); }
  .seal svg { fill: none; stroke: currentColor; stroke-width: 2.4; stroke-linecap: round; stroke-linejoin: round; }
  .draw { stroke-dasharray: 18; stroke-dashoffset: 0; }
  .fresh.v-passed:not(.nochange) .seal { animation: seal-pop var(--dur-2) var(--ease); }
  .fresh.v-passed:not(.nochange) .draw { animation: seal-draw var(--dur-3) var(--ease) both; }
  .fresh.v-passed:not(.nochange) header { animation: tint-in var(--dur-3) var(--ease); }
  @keyframes seal-pop { from { transform: scale(0.78); } to { transform: scale(1); } }
  @keyframes seal-draw { from { stroke-dashoffset: 18; } to { stroke-dashoffset: 0; } }
  @keyframes tint-in { from { background: var(--surface); } to { background: var(--tint); } }
  .hd { flex: 1; min-width: 0; }
  .head { font-size: var(--fs-md); font-weight: var(--fw-strong); color: var(--c); line-height: 1.3; }
  .sub { font-size: var(--fs-sm); color: var(--ink-2); line-height: 1.45; max-width: 60ch; }
  .att { color: var(--ink-3); white-space: nowrap; padding-top: 2px; }
  .claim { display: flex; align-items: flex-start; gap: 10px; margin: 0; padding: 10px 14px; border-top: 1px solid var(--line); background: var(--bg-sunken); color: var(--ink); }
  .claim :global(svg) { flex: none; margin-top: 2px; color: var(--ink-2); }
  .claim.diff { background: var(--waiting-tint); border-top-color: var(--waiting); }
  .claim.diff :global(svg) { color: var(--waiting); }
  .claim p { margin: 0; }
  .claim-h { font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-2); letter-spacing: 0.01em; margin-bottom: 2px !important; }
  .claim-t { font-size: var(--fs-sm); line-height: 1.45; color: var(--ink); overflow-wrap: anywhere; }
  .claim-t.strong { font-weight: var(--fw-strong); }
  .note { display: flex; align-items: flex-start; gap: 8px; margin: 0; padding: 8px 14px; border-top: 1px solid var(--line); color: var(--ink-2); line-height: 1.45; }
  .note :global(svg) { flex: none; margin-top: 2px; color: var(--ink-3); }
  .files { display: flex; flex-direction: column; gap: 6px; padding: 10px 14px; border-top: 1px solid var(--line); }
  .files ul { display: flex; flex-wrap: wrap; gap: 6px; }
  .files li { padding: 2px 8px; border-radius: var(--radius-sm); background: var(--bg-sunken); color: var(--ink); overflow-wrap: anywhere; }
  .failbox { display: flex; flex-direction: column; gap: 6px; padding: 10px 14px; border-top: 1px solid var(--line); background: var(--waiting-tint); }
  .failbox p { margin: 0; }
  .names { display: flex; flex-wrap: wrap; gap: 6px; }
  .names li { padding: 2px 8px; border-radius: var(--radius-sm); background: var(--surface); border: 1px solid var(--waiting); overflow-wrap: anywhere; }
  .edited p { margin: 0; }
  .more-det { padding: 0 14px 8px; }
  .more-det > summary { cursor: pointer; min-height: 32px; display: flex; align-items: center; color: var(--ink-2); }
  .edited .more-det { padding: 0; }
  .own { border-top: 1px solid var(--line); padding-top: 4px; }
  .ownnote { margin: 0 0 6px; }
  .diffs.inner { padding: 6px 0 0; border-top: 0; }
  .diffs, .notes { display: flex; flex-direction: column; gap: 6px; padding: 10px 14px; border-top: 1px solid var(--line); }
  .diff { display: flex; flex-direction: column; gap: 4px; }
  .dpath { margin: 0; overflow-wrap: anywhere; }
  .dl { max-height: calc(13 * 1.55em); }
  .ln { display: block; }
  .op-add { color: var(--ok); }
  .op-del { color: var(--stalled); }
  .notes ul { display: flex; flex-direction: column; gap: 2px; color: var(--ink-2); }
  .via { margin: 0; padding: 6px 14px 10px; }
  .warnbox { display: flex; align-items: flex-start; gap: 8px; margin: 0; padding: 10px 14px; border-top: 1px solid var(--waiting); background: var(--waiting-tint); color: var(--ink); line-height: 1.45; }
  .warnbox :global(svg) { flex: none; margin-top: 2px; color: var(--waiting); }
  .sr-only { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
  .rows { display: flex; flex-direction: column; }
  .row { display: grid; grid-template-columns: 20px minmax(0, 1fr) auto; align-items: center; gap: 4px 10px; padding: 8px 14px; border-top: 1px solid var(--line); }
  .ri { display: inline-flex; align-items: center; justify-content: center; width: 20px; height: 20px; border-radius: 50%; }
  .s-passed .ri { background: var(--ok-tint); color: var(--ok); }
  /* Kanıt sayılmadı kartında yeşil işaret yok: ölçüt komutu geçse de kart "geçti" demiyor (iki mesaj verilmesin). */
  .v-unchecked .s-passed .ri { background: var(--idle-tint); color: var(--idle); }
  .s-failed .ri, .s-error .ri { background: var(--stalled-tint); color: var(--stalled); }
  .s-skipped .ri { background: var(--idle-tint); color: var(--idle); }
  .lab { font-weight: var(--fw-medium); }
  .dur { color: var(--ink-3); }
  .sum, .out, .shotlink, .outlink { grid-column: 2 / -1; }
  .sum { margin: 0; color: var(--ink-2); }
  .sum .bad { color: var(--stalled); }
  .outlink { display: inline-flex; align-items: center; width: fit-content; min-height: 32px; }
  .more { border: 0; background: none; padding: 0 4px; margin-left: -4px; min-height: 32px; color: var(--ink-3); text-decoration: underline; text-underline-offset: 0.18em; }
  .more:hover { color: var(--ink); }
  /* Sakin çıktı bloğu: satır sarmalı, en çok 14 satır yüksekliğinde kaydırılır. */
  .calm { margin: 0 0 4px; padding: 10px 12px; border-radius: var(--radius); background: var(--bg-sunken); font-family: var(--mono); font-size: var(--fs-xs); line-height: 1.55; white-space: pre-wrap; overflow-wrap: anywhere; max-height: calc(14 * 1.55em); overflow-y: auto; color: var(--ink-2); animation: calm-in var(--dur-1) var(--ease); }
  @keyframes calm-in { from { opacity: 0; } to { opacity: 1; } }
  .shotlink { display: block; width: fit-content; max-width: 100%; line-height: 0; border: 1px solid var(--line-strong); border-radius: var(--radius); overflow: hidden; }
  .shot { display: block; max-width: min(100%, 22rem); height: auto; }
  .act { display: flex; flex-wrap: wrap; gap: 8px; padding: 10px 14px 12px; border-top: 1px solid var(--line); }
  .proof:not(.decision) .act { border-top: 0; padding-top: 0; }
  @media (pointer: coarse) and (hover: none) {
    .more, .outlink { min-height: 40px; }
  }
  @media (max-width: 559px) {
    header { padding: 12px; }
    .act :global(.btn) { flex: 1 1 100%; }
    /* Karar kartında düğmeler kartın altına yapışabilsin diye eylem kutusu araya girmez (yapışma kartın bütününe göre çalışır). */
    .proof.decision .act { display: contents; }
  }
</style>
