<script lang="ts">
  import { untrack } from 'svelte'
  import { getDecisions, getProjects } from '../lib/api'
  import { applyGuardTemplate, createGuardRule, deleteGuardRule, getGuardEvents, getGuardRules, getGuardTemplates, patchGuardRule, testGuard } from '../lib/api2'
  import EmptyState from '../lib/components/EmptyState.svelte'
  import Icon from '../lib/components/Icon.svelte'
  import { go } from '../lib/router.svelte'
  import { addFolderFlow } from '../lib/shell'
  import Skeleton from '../lib/components/Skeleton.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import { app, toast } from '../lib/store.svelte'
  import type { Decision, GuardAction, GuardEvent, GuardKind, GuardRule, GuardTemplate, GuardTestResult, Project } from '../lib/types'
  import { addProject } from '../lib/api3'
  import { pf, refreshProfile } from '../lib/profileStore.svelte'
  import { ago, errText, isGeneral, isTestProject, projectLabel } from '../lib/util'
  import { sade } from '../lib/sade'
  import { ApiError } from '../lib/errors'
  import { sadeMi } from '../lib/hata'
  import { GeriAl } from '../lib/geriAl.svelte'
  import GeriAlSatiri from '../lib/components/GeriAlSatiri.svelte'
  import { islemKilidi } from '../lib/islemKilidi'

  /** Araç adı sade Türkçe (A-KOR-06): bilinmeyen ad olduğu gibi kalır. */
  const toolLabel = (n: string): string => {
    switch (n) {
      case 'Bash': return t('tToolBash')
      case 'Write': return t('tToolWrite')
      case 'Edit': return t('tToolEdit')
      case 'Read': return t('tToolRead')
      case 'NotebookEdit': return t('tToolNotebook')
      default: return n
    }
  }

  let projects = $state<Project[]>([])
  let projectSel = $state('')
  let rules = $state<GuardRule[] | null>(null)
  let events = $state<GuardEvent[] | null>(null)
  let bans = $state<Decision[]>([])
  let error = $state('')

  const pid = $derived(projectSel === '' || projectSel.startsWith('path:') ? null : Number(projectSel))

  // Söz'de henüz kayıtlı olmayan ama bilgisayarında tanıdığım projeler: seçici boş kalmasın, seçince sessizce eklenir.
  const normPath = (p: string | null | undefined) => (p ?? '').trim().replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase()
  const recognized = $derived(
    (pf.data?.projects ?? [])
      .filter((p) => !isTestProject(p) && !projects.some((sp) => normPath(sp.path) === normPath(p.path)))
      .slice(0, 40),
  )
  async function adoptRecognized(value: string) {
    if (!value.startsWith('path:')) return
    error = ''
    try {
      const r = await addProject(value.slice(5))
      const ps = (await getProjects()).filter((p) => !isGeneral(p))
      projects = ps
      projectSel = String(r.id)
    } catch (e) {
      error = t('guardAdoptFail')
      projectSel = projects[0] ? String(projects[0].id) : ''
    }
  }
  $effect(() => { untrack(() => { void refreshProfile({ silent: true }) }) })

  async function loadProjects() {
    try {
      const ps = (await getProjects()).filter((p) => !isGeneral(p))
      projects = ps
      if (!ps.some((p) => String(p.id) === projectSel)) projectSel = ps[0] ? String(ps[0].id) : ''
    } catch (e) {
      error = errText(e)
    }
  }

  async function load() {
    if (pid === null) return
    try {
      const [r, ev, b] = await Promise.all([getGuardRules(pid), getGuardEvents(7), getDecisions({ status: 'approved', type: 'ban' })])
      rules = r
      events = ev
      bans = b
      error = ''
    } catch (e) {
      error = errText(e)
    }
  }

  $effect(() => {
    void app.tick
    loadProjects()
  })
  $effect(() => {
    void pid
    void app.tick
    load()
  })

  const kindLabel = (k: GuardKind) => t(({ scope_only: 'gkScope', path_protect: 'gkPath', command_block: 'gkBlock', ask_before: 'gkAsk' } as const)[k])
  const myEvents = $derived((events ?? []).filter((e) => e.projectId === null || e.projectId === pid))
  const unlinkedBans = $derived(bans.filter((d) => (d.projectId === pid || d.projectName === 'Genel') && !(rules ?? []).some((r) => r.decisionId === d.id)))

  // ---- Şablon seçici ----
  let templates = $state<GuardTemplate[] | null>(null)
  let tplErr = $state('')
  let applying = $state<string | null>(null)

  async function loadTemplates() {
    try {
      templates = await getGuardTemplates()
      tplErr = ''
    } catch (e) {
      tplErr = errText(e)
    }
  }
  $effect(() => {
    void loadTemplates() // şablon listesi proje değişince değişmez; açılışta bir kez
  })

  /** Şablonun bütün kuralları bu projede açık olarak varsa "uygulanmış" sayılır. */
  const isApplied = (tpl: GuardTemplate) => tpl.rules.length > 0 && tpl.rules.every((x) => (rules ?? []).some((r) => r.label === x.label && r.enabled))

  async function applyTemplate(tpl: GuardTemplate) {
    if (pid === null || applying || isApplied(tpl)) return
    applying = tpl.id
    error = ''
    const before = rules ?? []
    // önce çiz: şablonun kuralları geçici satır olarak hemen görünür
    const temp: GuardRule[] = tpl.rules
      .filter((x) => !before.some((r) => r.label === x.label))
      .map((x, i) => ({ id: -(i + 1), projectId: pid, spec: {}, enabled: true, decisionId: null, origin: 'template' as const, createdAt: new Date().toISOString(), ...x }) as GuardRule)
    const labels = tpl.rules.map((x) => x.label)
    rules = [...before.filter((r) => !temp.some((x) => x.label === r.label)).map((r) => ({ ...r, enabled: labels.includes(r.label) ? true : r.enabled })), ...temp]
    try {
      await applyGuardTemplate(pid, tpl.id)
      toast(t('templateApplied', { name: tpl.label }))
    } catch (e) {
      rules = before
      error = errText(e)
      toast(t('undone'))
    } finally {
      applying = null
      await load()
    }
  }

  // Sunucu kural değişikliğini reddedince cümlesini {error} alanında sade Türkçe söyler (404, 400, 409): sade ise olduğu gibi gösterilir.
  const guardErr = (e: unknown): string => (e instanceof ApiError && e.status >= 400 && e.status < 500 && sadeMi(e.message) ? e.message.trim() : errText(e))

  // ---- kural düzenleme ----
  // Çift tıkta iki istek gitmesin: kural için bir istek sürerken o kuralın düğmeleri kilitli (S3-20).
  let ruleBusy = $state<Record<number, boolean>>({})
  async function setEnabled(r: GuardRule) {
    if (r.id < 0 || ruleBusy[r.id]) return
    ruleBusy[r.id] = true
    try { await setEnabledNow(r) } finally { ruleBusy[r.id] = false }
  }
  async function setEnabledNow(r: GuardRule) {
    const prev = r.enabled
    const want = !prev
    r.enabled = want
    try {
      const res = await patchGuardRule(r.id, { enabled: want })
      // Sunucunun söylediği gerçek değer esastır: istenenle dönen farklıysa sessiz kalınmaz (A-KOR-03).
      if (res && typeof res.enabled === 'boolean' && res.enabled !== want) {
        r.enabled = res.enabled
        toast(t('guardRuleNoChange'))
      }
    } catch (e) {
      r.enabled = prev
      error = guardErr(e)
      toast(t('undone'))
    }
  }

  async function setAction(r: GuardRule, a: GuardAction) {
    if (r.id < 0 || ruleBusy[r.id]) return
    ruleBusy[r.id] = true
    try { await setActionNow(r, a) } finally { ruleBusy[r.id] = false }
  }
  async function setActionNow(r: GuardRule, a: GuardAction) {
    if (r.action === a) return
    const prev = r.action
    r.action = a
    try {
      const res = await patchGuardRule(r.id, { action: a })
      if (res && res.action && res.action !== a) {
        r.action = res.action
        error = t('guardRuleNoChange')
        toast(t('guardRuleNoChange'))
      }
    } catch (e) {
      r.action = prev
      error = guardErr(e)
      toast(t('undone'))
    }
  }

  // Silinen kural 5 sn "Geri al" ile geri getirilebilir (A-KOR-05): aynı kural yeniden kurulur.
  const undoDel = new GeriAl<GuardRule>()
  async function undoRemove() {
    const r = undoDel.take()
    if (!r) return
    try {
      const real = await createGuardRule({ projectId: r.projectId ?? pid ?? 0, kind: r.kind, label: r.label, spec: r.spec, action: r.action, decisionId: r.decisionId ?? undefined, origin: r.origin === 'defter' ? 'defter' : 'user' })
      rules = [...(rules ?? []), real]
    } catch (e) {
      error = errText(e)
    }
  }
  async function remove(r: GuardRule) {
    if (r.id < 0) return
    const before = rules ?? []
    rules = before.filter((x) => x.id !== r.id)
    try {
      await deleteGuardRule(r.id)
      undoDel.arm(r)
    } catch (e) {
      rules = before
      error = errText(e)
      toast(t('undone'))
    }
  }

  // ---- Dene ----
  let tool = $state('Bash')
  let target = $state('')
  let testing = $state(false)
  let result = $state<GuardTestResult | null>(null)
  let tested = $state('')
  let testErr = $state('')
  let emptyTry = $state(false)

  async function dene(e?: Event) {
    e?.preventDefault()
    const v = target.trim()
    if (!v) {
      // Boşken eski sonuç ekranda kalmaz, ne eksik olduğu söylenir (A-KOR-07).
      result = null
      tested = ''
      testErr = ''
      emptyTry = true
      document.getElementById('g-target')?.focus()
      return
    }
    emptyTry = false
    if (pid === null || testing) return
    testing = true
    testErr = ''
    result = null
    tested = `${toolLabel(tool)}: ${v}` // önce çiz: "bakılıyor" satırı hemen
    try {
      result = await testGuard(pid, tool, tool === 'Bash' ? { command: v } : { file_path: v })
    } catch (er) {
      testErr = errText(er)
    } finally {
      testing = false
    }
  }

  function example(tl: string, v: string) {
    tool = tl
    target = v
    void dene()
  }

  // ---- Defter'den bağla ----
  type Tpl = { id: string; kind: GuardKind; label: string; patterns?: string[] }
  const tpls: Tpl[] = [
    { id: 'bulk', kind: 'command_block', label: 'tplBulk', patterns: ['rm -rf', 'Remove-Item -Recurse', 'del /s'] },
    { id: 'force', kind: 'command_block', label: 'tplForce', patterns: ['git push --force', 'git reset --hard'] },
    { id: 'kill', kind: 'command_block', label: 'tplKill', patterns: ['taskkill', 'Stop-Process', 'pm2 delete', 'pm2 kill'] },
    { id: 'global', kind: 'command_block', label: 'tplGlobal', patterns: ['npm i -g', 'npm install -g', 'winget install', 'choco install'] },
    { id: 'disk', kind: 'command_block', label: 'tplDisk', patterns: ['format ', 'diskpart'] },
    { id: 'publish', kind: 'command_block', label: 'tplPublish', patterns: ['npm publish', 'gh release', 'vercel --prod'] },
    { id: 'scope', kind: 'scope_only', label: 'tplScope' },
  ]
  let pick = $state<Record<number, string>>({})
  let linkBusy = $state<number | null>(null)

  async function bind(d: Decision) {
    const tp = tpls.find((x) => x.id === (pick[d.id] ?? ''))
    if (!tp || pid === null) return
    linkBusy = d.id
    const before = rules ?? []
    const action: GuardAction = 'deny'
    const temp: GuardRule = { id: -Date.now(), projectId: pid, kind: tp.kind, label: d.text, spec: { patterns: tp.patterns ?? [] }, action, enabled: true, decisionId: d.id, origin: 'defter', createdAt: new Date().toISOString() }
    rules = [...before, temp]
    try {
      const real = await createGuardRule({ projectId: pid, kind: tp.kind, label: d.text, spec: tp.patterns ? { patterns: tp.patterns } : {}, action, decisionId: d.id, origin: 'defter' })
      rules = (rules ?? []).map((x) => (x.id === temp.id ? real : x))
      toast(t('linked'))
    } catch (e) {
      rules = before
      error = errText(e)
      toast(t('undone'))
    } finally {
      linkBusy = null
    }
  }

  const decisionText = (r: GuardRule) => (r.decisionId ? bans.find((d) => d.id === r.decisionId)?.text : null)
</script>

<div class="page">
  <header class="page-head">
    <div class="titles">
      <h1 class="h1">{t('navGuard')}</h1>
      <p class="lead">{t('guardLead')}</p>
    </div>
    <div class="field">
      <label class="label" for="g-proj">{t('project')}</label>
      <select id="g-proj" class="select" bind:value={projectSel} onchange={() => void adoptRecognized(projectSel)} data-guard-project>
        {#if projects.length === 0}<option value="">{t('guardPickProject')}</option>{/if}
        {#each projects as p (p.id)}
          <option value={String(p.id)}>{projectLabel(p)}</option>
        {/each}
        {#if recognized.length > 0}
          <optgroup label={t('guardRecognized')}>
            {#each recognized as p (p.path)}
              <option value={'path:' + p.path} data-recognized>{p.name}</option>
            {/each}
          </optgroup>
        {/if}
      </select>
    </div>
  </header>

  {#if error}<p class="err-line" role="alert">{error}</p>{/if}

  {#if projects.length === 0 && rules === null}
    {#if recognized.length > 0}
      <EmptyState id="no-project" text={t('guardPickFirst')} />
    {:else}
      <EmptyState id="no-project" text={t('guardNoProject')} action={t('emptyAddFolder')} onaction={() => { go('is'); addFolderFlow() }} />
    {/if}
  {:else if rules === null}
    <Skeleton rows={3} />
  {:else}
    <section class="blk" aria-labelledby="sablon-t">
      <div class="sec-head">
        <h2 class="h2" id="sablon-t">{t('guardTemplates')}</h2>
        <p class="small muted">{t('guardTemplatesHint')}</p>
      </div>
      {#if tplErr && templates === null}
        <p class="small err-line" role="alert">{t('guardTemplatesErr', { m: tplErr })}</p>
      {:else if templates === null}
        <Skeleton rows={2} />
      {:else}
        <ul class="tcards" data-templates>
          {#each templates as tpl (tpl.id)}
            {@const on = isApplied(tpl)}
            <li class="tcard" class:on data-template={tpl.id}>
              <div class="thead">
                <h3 class="tname">{tpl.label}</h3>
                <span class="small muted">{t('tplRuleN', { n: tpl.rules.length })}</span>
              </div>
              <p class="small tdesc">{sade(tpl.description)}</p>
              <ul class="trules">
                {#each tpl.rules as r (r.label)}
                  <li class="small">
                    <span class="chip" class:chip-stalled={r.action === 'deny'} class:chip-waiting={r.action === 'ask'}>{r.action === 'deny' ? t('actDeny') : t('actAsk')}</span>
                    <span>{r.label}</span>
                    <span class="muted kind">{kindLabel(r.kind)}</span>
                  </li>
                {/each}
              </ul>
              <div class="tact">
                {#if on}
                  <span class="chip chip-ok"><Icon name="check" size={15} />{t('templateOn')}</span>
                {:else}
                  <button class="btn btn-primary btn-sm" aria-label={t('applyToProjectNamed', { n: tpl.label })} onclick={() => applyTemplate(tpl)} disabled={applying !== null} data-temkinli={tpl.id === 'temkinli' ? '' : undefined} data-apply={tpl.id}>
                    <Icon name="bekci" size={16} />{t('applyToProject')}
                  </button>
                {/if}
              </div>
            </li>
          {/each}
        </ul>
      {/if}
    </section>

    <section class="blk" aria-labelledby="kural-t">
      <div class="sec-head">
        <h2 class="h2" id="kural-t">{t('guardRules')}</h2>
      </div>

      {#if undoDel.item}<GeriAlSatiri label={undoDel.item.label} onundo={undoRemove} />{/if}
      {#if rules.length === 0}
        <EmptyState id="no-rules" text={t('guardRulesNone')} action={t('emptyApplyTemplate')} onaction={() => { const tpl = templates?.find((x) => x.id === 'temkinli') ?? templates?.[0]; if (tpl) void applyTemplate(tpl) }} />
      {:else}
        <ul class="spines" data-rules>
          {#each rules as r (r.id)}
            <li class="spine rrow" class:off={!r.enabled} class:v-failed={r.enabled && r.action === 'deny'} class:v-waiting={r.enabled && r.action === 'ask'} class:v-neutral={!r.enabled} data-rule={r.id}>
              <div class="rmain">
                <p class="rlabel">{r.label}</p>
                <p class="small muted">
                  {kindLabel(r.kind)}
                  · {r.origin === 'template' ? t('originTemplate') : r.origin === 'defter' ? t('originLedger') : t('originYou')}
                  {#if r.projectId === null} · {t('everyProject')}{/if}
                </p>
                {#if decisionText(r)}<p class="small muted">{t('fromLedger')}: <span class="voice">{decisionText(r)}</span></p>{/if}
              </div>
              <div class="ract">
                <div class="seg" role="group" aria-label={`${r.label}: ${t('guardAction')}`}>
                  <button disabled={r.id < 0 || ruleBusy[r.id]} aria-pressed={r.action === 'deny'} title={t('actDenyTip')} onclick={() => setAction(r, 'deny')}>{t('actDeny')}</button>
                  <button disabled={r.id < 0 || ruleBusy[r.id]} aria-pressed={r.action === 'ask'} title={t('actAskTip')} onclick={() => setAction(r, 'ask')}>{t('actAsk')}</button>
                </div>
                <button class="switch" disabled={r.id < 0 || ruleBusy[r.id]} role="switch" aria-checked={r.enabled} aria-label={`${r.label}: ${r.enabled ? t('on') : t('off')}`} title={r.enabled ? t('ckSwitchOnTip') : t('ckSwitchOffTip')} onclick={() => setEnabled(r)}></button>
                {#if r.origin !== 'template'}
                  <button class="btn btn-quiet btn-sm" disabled={r.id < 0 || ruleBusy[r.id]} aria-label={`${t('remove')}: ${r.label}`} title={`${t('remove')}: ${r.label}`} onclick={() => remove(r)}><Icon name="trash" size={16} /></button>
                {/if}
              </div>
            </li>
          {/each}
        </ul>
      {/if}
    </section>

    <section class="blk" aria-labelledby="dene-t">
      <div class="sec-head">
        <h2 class="h2" id="dene-t">{t('guardTry')}</h2>
        <p class="small muted">{t('guardTryHint')}</p>
      </div>
      <form class="try" onsubmit={dene}>
        <div class="field tool">
          <label class="label" for="g-tool">{t('guardTool')}</label>
          <select id="g-tool" class="select" bind:value={tool}>
            <option value="Bash">{toolLabel('Bash')}</option>
            <option value="Write">{toolLabel('Write')}</option>
            <option value="Edit">{toolLabel('Edit')}</option>
            <option value="NotebookEdit">{toolLabel('NotebookEdit')}</option>
          </select>
        </div>
        <div class="field grow">
          <label class="label" for="g-target">{tool === 'Bash' ? t('guardCommand') : t('guardPath')}</label>
          <input id="g-target" class="input mono" bind:value={target} placeholder={tool === 'Bash' ? 'rm -rf build' : 'D:\\baska-klasor\\dosya.txt'} autocomplete="off" spellcheck="false" />
        </div>
        <div class="field">
          <span class="label" aria-hidden="true">&nbsp;</span>
          <button class="btn btn-primary" type="submit" disabled={testing} data-dene><Icon name="play" size={16} />{t('guardTryBtn')}</button>
        </div>
      </form>
      <p class="small examples">
        <span class="muted">{t('examples')}:</span>
        <button class="btn btn-quiet btn-sm mono" onclick={() => example('Bash', 'rm -rf build')}>rm -rf build</button>
        <button class="btn btn-quiet btn-sm mono" onclick={() => example('Bash', 'git push --force origin main')}>git push --force</button>
        <button class="btn btn-quiet btn-sm mono" onclick={() => example('Write', 'D:\\baska-klasor\\notlar.txt')}>{t('exOutside')}</button>
        <button class="btn btn-quiet btn-sm mono" onclick={() => example('Bash', 'npm test')}>npm test</button>
      </p>

      {#if emptyTry && !tested}<p class="small err-line" role="alert" data-try-empty>{tool === 'Bash' ? t('guardTryNeedCmd') : t('guardTryNeedPath')}</p>{/if}
      {#if tested}
        <div class="verdict" class:deny={result?.verdict === 'deny'} class:ask={result?.verdict === 'ask'} class:allow={result?.verdict === 'allow'} role="status" aria-live="polite" data-result>
          <p class="mono small tgt">{tested}</p>
          {#if testing}
            <p class="vtxt muted">{t('guardThinking')}</p>
          {:else if testErr}
            <p class="err-line">{testErr}</p>
          {:else if result}
            <p class="vtxt">
              {#if result.verdict === 'deny'}<Icon name="x" size={22} />{t('wouldDeny')}
              {:else if result.verdict === 'ask'}<Icon name="question" size={22} />{t('wouldAsk')}
              {:else}<Icon name="check" size={22} />{t('wouldAllow')}{/if}
            </p>
            {#if result.reason}<p class="small">{result.reason}</p>{:else if result.note}<p class="small muted">{result.note}</p>{/if}
            {#if result.ruleLabel}<p class="small muted">{t('byRule')}: {result.ruleLabel}</p>{/if}
          {/if}
        </div>
      {/if}
    </section>

    <section class="blk" aria-labelledby="defter-t">
      <div class="sec-head">
        <h2 class="h2" id="defter-t">{t('guardFromLedger')}</h2>
        <p class="small muted">{t('guardFromLedgerHint')}</p>
      </div>
      {#if unlinkedBans.length === 0}
        <EmptyState id="no-bans" text={t('guardFromLedgerNone')} action={t('emptyOpenLedger')} onaction={() => go('defter')} />
      {:else}
        <ul class="spines">
          {#each unlinkedBans as d (d.id)}
            <li class="spine v-neutral lrow" data-ban={d.id}>
              <p class="voice lt">{d.text}</p>
              <div class="lact">
                <label class="sr-only" for={`tp-${d.id}`}>{t('pickTemplate')}</label>
                <select id={`tp-${d.id}`} class="select" value={pick[d.id] ?? ''} onchange={(e) => (pick[d.id] = e.currentTarget.value)}>
                  <option value="">{t('pickTemplate')}</option>
                  {#each tpls as tp (tp.id)}<option value={tp.id}>{t(tp.label as never)}</option>{/each}
                </select>
                <button class="btn btn-sm" disabled={linkBusy === d.id} onclick={() => (pick[d.id] ? bind(d) : document.getElementById(`tp-${d.id}`)?.focus())}><Icon name="bekci" size={16} />{t('linkGuard')}</button>
              </div>
            </li>
          {/each}
        </ul>
      {/if}
    </section>

    <section class="blk" aria-labelledby="olay-t">
      <div class="sec-head">
        <h2 class="h2" id="olay-t">{t('guardEvents')}</h2>
        <p class="small muted">{t('guardEventsHint')}</p>
      </div>
      {#if events === null}
        <Skeleton rows={2} />
      {:else if myEvents.length === 0}
        <EmptyState id="no-events" text={t('guardEventsNone')} action={t('emptyTryCmd')} onaction={() => document.getElementById('g-target')?.focus()} />
      {:else}
        <ul class="spines" data-events>
          {#each myEvents as ev (ev.id)}
            <li class="spine {ev.action === 'deny' ? 'v-failed' : 'v-waiting'} erow">
              <div class="etop">
                <span class="chip" class:chip-stalled={ev.action === 'deny'} class:chip-waiting={ev.action === 'ask'}>
                  <Icon name={ev.action === 'deny' ? 'x' : 'question'} size={14} />{ev.action === 'deny' ? t('evDenied') : t('evAsked')}
                </span>
                <span class="small muted">{toolLabel(ev.tool)}</span>
                <time class="small muted" datetime={ev.at}>{ago(ev.at)}</time>
              </div>
              {#if ev.targetSummary}<p class="mono etgt" data-ev-target>{ev.targetSummary}</p>{/if}
              <p class="small muted">{ev.ruleLabel ? `${ev.ruleLabel}. ` : ''}{ev.reason}</p>
            </li>
          {/each}
        </ul>
      {/if}
    </section>
  {/if}
</div>

<style>
  .titles { flex: 1 1 22rem; min-width: 0; }
  .blk { margin-bottom: clamp(28px, 4vw, 48px); }
  .tcards { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 20rem), 1fr)); gap: 14px; }
  .tcard { display: flex; flex-direction: column; gap: 8px; padding: 16px 18px; border: 1px solid var(--line-strong); border-radius: var(--radius); }
  .tcard.on { border-color: var(--ok); background: var(--ok-tint); }
  .thead { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 4px 12px; }
  .tname { margin: 0; font-family: var(--sans); font-weight: var(--fw-strong); font-size: var(--fs-md); }
  .tdesc { margin: 0; color: var(--ink-2); }
  .trules { display: flex; flex-direction: column; gap: 6px; margin: 4px 0; }
  .trules li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 8px; }
  .kind { font-size: var(--fs-xs); }
  .tact { margin-top: auto; padding-top: 6px; }

  .rrow { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 10px 20px; }
  .rrow.off .rlabel { color: var(--ink-3); }
  .rmain { min-width: 0; flex: 1 1 18rem; }
  .rlabel { margin: 0 0 2px; font-weight: var(--fw-strong); font-size: var(--fs-md); }
  .ract { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
  .seg { display: inline-flex; border: 1px solid var(--line-strong); border-radius: 999px; overflow: hidden; }
  .seg button { min-height: 34px; padding: 0 14px; border: 0; background: none; font-size: var(--fs-sm); font-weight: var(--fw-medium); color: var(--ink-2); }
  .seg button[aria-pressed='true'] { background: var(--primary); color: var(--on-primary); }

  .try { display: flex; flex-wrap: wrap; gap: 12px; align-items: flex-end; }
  .try .tool { width: 11rem; }
  .try .grow { flex: 1 1 16rem; }
  .examples { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; margin: 10px 0 14px; }
  .verdict { display: flex; flex-direction: column; gap: 6px; padding: 14px 18px; border-left: 4px solid var(--idle); background: var(--bg-sunken); border-radius: 0 var(--radius) var(--radius) 0; max-width: 46rem; }
  .verdict.deny { border-left-color: var(--stalled); background: var(--stalled-tint); }
  .verdict.ask { border-left-color: var(--waiting); background: var(--waiting-tint); }
  .verdict.allow { border-left-color: var(--ok); background: var(--ok-tint); }
  .vtxt { display: flex; align-items: center; gap: 10px; margin: 0; font-family: var(--sans); font-weight: var(--fw-strong); font-size: var(--fs-lg); }
  .tgt { margin: 0; word-break: break-all; color: var(--ink-2); }

  .lrow { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 10px 20px; }
  .lt { margin: 0; flex: 1 1 16rem; }
  .lact { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .lact .select { width: 14rem; min-height: 36px; }

  .etop { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 12px; }
  .etgt { margin: 6px 0 2px; word-break: break-all; }
</style>
