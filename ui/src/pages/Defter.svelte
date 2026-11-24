<script lang="ts">
  import { islemKilidi } from '../lib/islemKilidi'
  import { hataMetni } from '../lib/hata'
  let { embedded = false }: { embedded?: boolean } = $props()
  import { createDecision, getDecisions, getPacket, getProjects, markPacketCopied, patchDecision } from '../lib/api'
  import EmptyState from '../lib/components/EmptyState.svelte'
  import Icon from '../lib/components/Icon.svelte'
  import { t, type Key } from '../lib/i18n/index.svelte'
  import { app, refresh, toast } from '../lib/store.svelte'
  import type { Decision, DecisionType, Packet, Project } from '../lib/types'
  import { copyText, fmtDate, isGeneral, projectLabel, toolName } from '../lib/util'

  type Tab = 'rule' | 'ban' | 'decision' | 'preference'
  const tabs: { id: Tab; label: Key }[] = [
    { id: 'rule', label: 'typeRules' },
    { id: 'ban', label: 'typeBans' },
    { id: 'decision', label: 'typeDecisions' },
    { id: 'preference', label: 'typePreferences' },
  ]

  let projects = $state<Project[]>([])
  let projectSel = $state('') // '' = her proje
  let tab = $state<Tab>('rule')
  let showArchived = $state(false)
  let items = $state<Decision[] | null>(null)
  let failed = $state(false)

  let editing = $state<number | null>(null)
  let draft = $state('')

  let packetOpen = $state(false)
  let packet = $state<Packet | null>(null)

  let addType = $state<DecisionType>('rule')
  let addProject = $state('')
  let addText = $state('')

  const general = $derived(projects.find(isGeneral))
  const projectId = $derived(projectSel === '' ? null : Number(projectSel))

  async function load() {
    try {
      const [pr, ap, ar] = await Promise.all([
        getProjects(),
        getDecisions({ projectId, status: 'approved' }),
        showArchived ? getDecisions({ projectId, status: 'archived' }) : Promise.resolve([] as Decision[]),
      ])
      projects = pr
      items = [...ap, ...ar]
      failed = false
    } catch {
      failed = true
    }
  }

  async function loadPacket() {
    try {
      packet = await getPacket(projectId ?? general?.id ?? null)
    } catch {
      packet = null
    }
  }

  $effect(() => {
    void app.tick
    void projectId
    void showArchived
    load()
  })

  $effect(() => {
    void app.tick
    void projectId
    if (packetOpen) loadPacket()
  })

  const counts = $derived.by(() => {
    const c: Record<string, number> = { rule: 0, ban: 0, decision: 0, preference: 0 }
    for (const d of items ?? []) if (d.status === 'approved' && d.type in c) c[d.type]++
    return c
  })

  const visible = $derived(
    (items ?? [])
      .filter((d) => d.type === tab)
      .sort((a, b) => Number(b.pinned) - Number(a.pinned) || Number(a.status === 'archived') - Number(b.status === 'archived') || Date.parse(b.updatedAt) - Date.parse(a.updatedAt)),
  )

  // Çift tıkta tek istek: işlem sürerken ikinci istek yok sayılır, düğmeler kilitlenir (islemKilidi.ts).
  let patching = $state(false)
  const patchLock = islemKilidi((b) => (patching = b))
  async function patch(d: Decision, p: Parameters<typeof patchDecision>[1], msg?: string) {
    await patchLock.calistir(async () => {
      try {
        await patchDecision(d.id, p)
        if (msg) toast(msg)
        editing = null
        await refresh()
      } catch (e) {
        toast(t('errGeneric', { m: hataMetni(e) }))
      }
    })
  }

  function onTabKey(e: KeyboardEvent) {
    const i = tabs.findIndex((x) => x.id === tab)
    let n = i
    if (e.key === 'ArrowRight') n = (i + 1) % tabs.length
    else if (e.key === 'ArrowLeft') n = (i - 1 + tabs.length) % tabs.length
    else if (e.key === 'Home') n = 0
    else if (e.key === 'End') n = tabs.length - 1
    else return
    e.preventDefault()
    tab = tabs[n].id
    document.getElementById(`tab-${tab}`)?.focus()
  }

  // Çift tıkta tek kayıt: istek sürerken ikinci gönderim yok sayılır (islemKilidi.ts).
  let adding = $state(false)
  const addLock = islemKilidi((b) => (adding = b))
  async function add(e: Event) {
    e.preventDefault()
    const pid = addProject !== '' ? Number(addProject) : (projectId ?? general?.id)
    if (!addText.trim() || pid === undefined) return
    await addLock.calistir(async () => {
      try {
        await createDecision({ projectId: pid, type: addType, text: addText.trim() })
        addText = ''
        tab = addType === 'correction' ? 'rule' : addType
        toast(t('added'))
        await refresh()
      } catch (err) {
        toast(t('errGeneric', { m: hataMetni(err) }))
      }
    })
  }

  async function copyPacket() {
    if (!packet) return
    const ok = await copyText(packet.text)
    toast(ok ? t('copied') : t('copyFailed'))
    if (ok) void markPacketCopied(projectId ?? general?.id ?? null).catch(() => {}) // yalnız yerel sayaç
  }

  function scopeText(d: Decision): string {
    return general && d.projectId === general.id ? t('scopeAll') : t('scopeOnly', { p: d.projectName })
  }
</script>

<div class="page" class:embedded>
  <header class="page-head">
    <div>
      {#if embedded}<h2 class="h2">{t('tabDecisions')}</h2>{:else}<h1 class="h1">{t('ledgerTitle')}</h1>{/if}
      {#if !embedded}<p class="lead">{t('ledgerLead')}</p>{/if}
    </div>
    <div class="tools">
      <div class="field">
        <label class="label" for="dproj">{t('project')}</label>
        <select id="dproj" class="select" bind:value={projectSel}>
          <option value="">{t('everyProject')}</option>
          {#each projects as p (p.id)}
            <option value={String(p.id)}>{projectLabel(p)}</option>
          {/each}
        </select>
      </div>
    </div>
  </header>

  <div class="tablist" role="tablist" aria-label={t('typeTabs')} tabindex="-1" onkeydown={onTabKey}>
    {#each tabs as x (x.id)}
      <button
        id={`tab-${x.id}`}
        role="tab"
        class="tab"
        class:on={tab === x.id}
        aria-selected={tab === x.id}
        aria-controls="panel"
        tabindex={tab === x.id ? 0 : -1}
        onclick={() => (tab = x.id)}
      >
        {t(x.label)}<span class="cnt">{counts[x.id]}</span>
      </button>
    {/each}
  </div>

  <div id="panel" role="tabpanel" aria-labelledby={`tab-${tab}`}>
    {#if failed && items === null}
      <p class="muted">{t('loadError')} <button class="btn btn-sm" onclick={load}>{t('retry')}</button></p>
    {:else if items !== null && visible.length === 0}
      <EmptyState id="no-ledger" text={t('ledgerEmpty')} action={t('emptyAddEntry')} onaction={() => { const d = document.querySelector('details.add') as HTMLDetailsElement | null; if (d) d.open = true; document.getElementById('addtext')?.focus() }} />
    {:else}
      <ul class="entries">
        {#each visible as d (d.id)}
          <li class="entry" class:archived={d.status === 'archived'}>
            <div class="main">
              {#if editing === d.id}
                <label class="sr-only" for={`e-${d.id}`}>{t('edit')}</label>
                <textarea id={`e-${d.id}`} class="textarea" rows="2" bind:value={draft}></textarea>
                <div class="row-acts">
                  <button class="btn btn-primary btn-sm" disabled={!draft.trim() || patching} onclick={() => patch(d, { text: draft.trim() }, t('saved'))}>{t('save')}</button>
                  <button class="btn btn-quiet btn-sm" onclick={() => (editing = null)}>{t('cancel')}</button>
                </div>
              {:else}
                <p class="voice text">{d.text}</p>
                <p class="small muted meta">
                  {#if d.pinned}<span class="pinned"><Icon name="pin" size={14} />{t('pinned')}</span>{/if}
                  {#if d.status === 'archived'}<span class="chip chip-neutral">{t('archivedLabel')}</span>{/if}
                  <span class="scope"><Icon name="globe" size={14} />{scopeText(d)}</span>
                  {#if d.origin}<span>{t('fromTool', { tool: toolName(d.origin.tool), date: fmtDate(d.origin.ts) })}</span>{/if}
                </p>
              {/if}
            </div>

            {#if editing !== d.id}
              <div class="acts">
                <button class="btn btn-quiet btn-sm pinbtn" disabled={patching} aria-pressed={d.pinned} onclick={() => patch(d, { pinned: !d.pinned })}>
                  <Icon name="pin" size={16} />{d.pinned ? t('unpin') : t('pin')}
                </button>
                <button class="btn btn-quiet btn-sm" onclick={() => { editing = d.id; draft = d.text }}>
                  <Icon name="edit" size={16} />{t('edit')}
                </button>
                {#if d.status === 'archived'}
                  <button class="btn btn-quiet btn-sm" disabled={patching} onclick={() => patch(d, { status: 'approved' })}><Icon name="undo" size={16} />{t('restore')}</button>
                {:else}
                  <button class="btn btn-quiet btn-sm" disabled={patching} onclick={() => patch(d, { status: 'archived' })}><Icon name="archive" size={16} />{t('archive')}</button>
                {/if}
                {#if general}
                  {#if d.projectId !== general.id}
                    <button class="btn btn-quiet btn-sm wide" disabled={patching} onclick={() => patch(d, { projectId: general.id })}><Icon name="globe" size={16} />{t('scopeToAll')}</button>
                  {:else}
                    <label class="sr-only" for={`s-${d.id}`}>{t('scopeToOne')}</label>
                    <select
                      id={`s-${d.id}`}
                      class="select scope-select"
                      disabled={patching}
                      onchange={(e) => {
                        const v = e.currentTarget.value
                        if (v) patch(d, { projectId: Number(v) })
                      }}
                    >
                      <option value="">{t('scopeToOne')}</option>
                      {#each projects.filter((p) => !isGeneral(p)) as p (p.id)}
                        <option value={String(p.id)}>{p.name}</option>
                      {/each}
                    </select>
                  {/if}
                {/if}
              </div>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}

    <label class="check">
      <input type="checkbox" bind:checked={showArchived} />
      <span>{t('showArchived')}</span>
    </label>
  </div>

  <details class="add">
    <summary><Icon name="plus" size={18} />{t('addByHand')}</summary>
    <form class="addform" onsubmit={add}>
      <div class="addrow">
        <div class="field">
          <label class="label" for="addtype">{t('addType')}</label>
          <select id="addtype" class="select" bind:value={addType}>
            <option value="rule">{t('typeRule')}</option>
            <option value="ban">{t('typeBan')}</option>
            <option value="decision">{t('typeDecision')}</option>
            <option value="preference">{t('typePreference')}</option>
          </select>
        </div>
        <div class="field">
          <label class="label" for="addproj">{t('addProject')}</label>
          <select id="addproj" class="select" bind:value={addProject}>
            <option value="">{projectId === null ? t('generalProject') : (projects.find((p) => p.id === projectId)?.name ?? '')}</option>
            {#each projects as p (p.id)}
              {#if p.id !== (projectId ?? general?.id)}<option value={String(p.id)}>{projectLabel(p)}</option>{/if}
            {/each}
          </select>
        </div>
      </div>
      <div class="field">
        <label class="label" for="addtext">{t('addText')}</label>
        <textarea id="addtext" class="textarea" rows="2" placeholder={t('addTextPlaceholder')} bind:value={addText}></textarea>
      </div>
      <div><button class="btn btn-primary" type="submit" disabled={!addText.trim() || adding} aria-label={t('addSubmit')} title={t('addSubmit')} data-def-add>{t('addSubmit')}</button></div>
    </form>
  </details>

  <section class="packet" aria-labelledby="packet-t">
    <div class="phead">
      <h2 class="h2" id="packet-t">{t('packetTitle')}</h2>
      <button class="btn" aria-expanded={packetOpen} onclick={() => { packetOpen = !packetOpen }}>
        <Icon name="layers" size={17} />{packetOpen ? t('hidePacket') : t('showPacket')}
      </button>
    </div>
    {#if packetOpen}
      <div class="pbody">
        {#if packet && packet.text}
          <pre class="ptext">{packet.text}</pre>
          <div class="pfoot">
            <span class="small muted">{t('packetChars', { n: packet.chars })}, {t('packetCount', { n: packet.decisionCount })}</span>
            <button class="btn btn-sm" onclick={copyPacket}><Icon name="copy" size={16} />{t('copyPacket')}</button>
          </div>
          <p class="small muted">{t('packetNote')}</p>
        {:else}
          <p class="muted">{t('packetEmpty')}</p>
        {/if}
      </div>
    {/if}
  </section>
</div>

<style>
  .tools { display: flex; gap: 12px; }
  .tools .field { min-width: min(18rem, 100%); }

  .tablist {
    display: flex;
    gap: clamp(2px, 1.5vw, 12px);
    border-bottom: 1px solid var(--line);
    margin-bottom: 8px;
    overflow-x: auto;
    scrollbar-width: none;
  }
  .tab {
    display: inline-flex;
    align-items: baseline;
    gap: 8px;
    padding: 12px clamp(8px, 2vw, 16px) 11px;
    margin-bottom: -1px;
    border: 0;
    border-bottom: 3px solid transparent;
    background: none;
    font-weight: var(--fw-strong);
    color: var(--ink-2);
    white-space: nowrap;
  }
  .tab:hover { color: var(--ink); }
  .tab.on { color: var(--ink); border-bottom-color: var(--ink); }
  .cnt {
    font-size: var(--fs-xs);
    font-variant-numeric: tabular-nums;
    color: var(--ink-3);
    font-weight: var(--fw-strong);
  }

  .entries { display: flex; flex-direction: column; }
  .entry {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    align-items: start;
    gap: 6px 24px;
    padding: 18px 0;
    border-bottom: 1px solid var(--line);
  }
  .entry.archived .text { color: var(--ink-3); text-decoration: line-through; text-decoration-color: var(--line-strong); }
  .text { font-size: var(--fs-md); max-width: 60ch; }
  .meta { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 14px; margin-top: 6px; }
  .meta :global(svg) { vertical-align: -2px; }
  .pinned, .scope { display: inline-flex; align-items: center; gap: 5px; }
  .pinned { color: var(--ink); font-weight: var(--fw-strong); }
  .acts { display: grid; grid-template-columns: repeat(3, auto); justify-content: end; align-items: center; gap: 2px 4px; }
  .acts :global(.scope-select), .acts .wide { grid-column: 1 / -1; justify-self: end; }
  .pinbtn { min-width: 8.4rem; justify-content: flex-start; }
  .row-acts { display: flex; gap: 8px; margin-top: 8px; }
  .scope-select { width: auto; min-height: 34px; padding: 4px 30px 4px 12px; font-size: var(--fs-sm); border-radius: 999px; border-color: transparent; font-weight: var(--fw-medium); background-color: transparent; }
  .scope-select:hover { background-color: var(--bg-sunken); }

  .check { display: inline-flex; align-items: center; gap: 10px; margin-top: 16px; font-size: var(--fs-sm); color: var(--ink-2); cursor: pointer; }
  .check input { width: 24px; height: 24px; accent-color: var(--ink); }

  .add { margin-top: clamp(28px, 5vw, 44px); }
  .add summary {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 10px 16px;
    border: 1px solid var(--line-strong);
    border-radius: 999px;
    font-weight: var(--fw-medium);
    cursor: pointer;
    list-style: none;
  }
  .add summary::-webkit-details-marker { display: none; }
  .add summary:hover { background: var(--bg-sunken); }
  .addform { display: flex; flex-direction: column; gap: 14px; margin-top: 18px; max-width: 40rem; }
  .addrow { display: grid; grid-template-columns: 1fr 2fr; gap: 14px; }

  .packet { margin-top: clamp(32px, 6vw, 60px); padding-top: 24px; border-top: 1px solid var(--line); }
  .phead { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 12px; }
  .pbody { display: flex; flex-direction: column; gap: 10px; margin-top: 16px; }
  .ptext {
    margin: 0;
    padding: 16px 18px;
    background: var(--bg-sunken);
    border-radius: var(--radius);
    font-family: var(--mono);
    font-size: var(--fs-sm);
    line-height: 1.6;
    white-space: pre-wrap;
    max-height: 22rem;
    overflow: auto;
  }
  .pfoot { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; }

  @media (max-width: 380px) {
    .tab { padding-inline: 6px; font-size: var(--fs-sm); gap: 4px; }
    .cnt { display: none; }
  }

  @media (max-width: 700px) {
    .entry { grid-template-columns: minmax(0, 1fr); }
    .acts { display: flex; flex-wrap: wrap; margin-left: -10px; }
    .pinbtn { min-width: 0; }
    .acts :global(.scope-select), .acts .wide { justify-self: start; }
    .addrow { grid-template-columns: 1fr; }
    .tools { width: 100%; }
  }
</style>
