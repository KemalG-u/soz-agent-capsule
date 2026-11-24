<script lang="ts">
  import { hataMetni } from '../lib/hata'
  import { islemKilidi } from '../lib/islemKilidi'
  import { onMount } from 'svelte'
  import AddFolder from '../lib/components/AddFolder.svelte'
  import Icon from '../lib/components/Icon.svelte'
  import OturumOzet from '../lib/components/OturumOzet.svelte'
  import Skeleton from '../lib/components/Skeleton.svelte'
  import { t } from '../lib/i18n/index.svelte'
  import {
    agoShort, cpuShort, diskLetter, diskLow, focusLiveWindow, focusToastText, getProfile, getProfileSessions, gbText, groupProjects, hasScan, lastScanTs, liveGroups, liveState, sessionName,
    memoryText, nestSessions, openProjectFolder, profileWarnings, reasonRows, relPath, runProfileScan, shortPath, toolEntries, toolLabel, validTs,
    type ProfLiveSession, type ProfProject, type ProfSessions, type Profile,
  } from '../lib/profil'
  import { attnRowState } from '../lib/profileStore.svelte'
  import { go } from '../lib/router.svelte'
  import { addFolderFlow, openNewJob } from '../lib/shell'
  import { cancelPendingStart, giveJobToProject, realProjects, sf } from '../lib/surface.svelte'
  import { toast } from '../lib/store.svelte'
  import { fmtDate, friendlyActionError, parseTs } from '../lib/util'

  // "Projelerim": bilgisayarın taranmış hâli. Ekran soru sormaz, bulduğunu gösterir. Veri yalnız GET /api/profile ve GET /api/profile/sessions.
  let profile = $state<Profile | null>(null)
  let loading = $state(true)
  let error = $state('')
  let scanning = $state(false)
  let scanErr = $state('')
  let opened = $state<string | null>(null)
  let dlg: HTMLDialogElement | undefined = $state()
  let oldOpen = $state(false)
  let nowTs = $state(Date.now())
  type SessState = { state: 'loading' } | { state: 'ok'; data: ProfSessions } | { state: 'err'; msg: string }
  let sess = $state<Record<string, SessState>>({})
  let root: HTMLElement | undefined = $state()

  const msgOf = (e: unknown) => (hataMetni(e))

  async function load(silent = false) {
    try {
      profile = await getProfile() // boş ya da bozuk cevapta getProfile hata verir: ekran boş kalmaz
      error = ''
    } catch (e) {
      if (!silent || !profile) error = msgOf(e)
    } finally {
      loading = false
    }
  }

  $effect(() => {
    if (sf.addFolderOpen && dlg && !dlg.open) dlg.showModal()
    if (!sf.addFolderOpen && dlg?.open) dlg.close()
  })

  /** Klasör eklendi: iş kutusunda seçili, hemen iş yazılabilir. */
  function folderAdded(p?: { name?: string | null }) {
    sf.addFolderOpen = false
    toast(p?.name ? t('folderAddedOk', { n: p.name }) + ' ' + t('pjFolderReady') : t('pjFolderAdded'))
    openNewJob() // Şu An'a geçer, iş kutusuna odaklanır (bir kez daha gecikmeyle: pencere kapanırken odağı geri almasın)
  }

  // Açık oturum satırı: tıklayınca o pencere öne gelir (Şu An ekranındaki satırla aynı eylem).
  const canOpenLive = (s: { canFocus?: boolean; pid?: number | null; hwnd?: number | string | null }, path: string | null) => !!(s.canFocus || s.pid != null || s.hwnd != null || path)
  // Pencere ya da klasör açan satırlarda çift tık ikinci istek göndermez (S3-20): işlem sürerken ikinci tık yok sayılır.
  const kilit = islemKilidi()
  const openLive = (s: Parameters<typeof openLiveNow>[0], path: string | null) => kilit.calistir(() => openLiveNow(s, path))
  async function openLiveNow(s: { id: string; tool: string; pid?: number | null; hwnd?: number | string | null; projectPath?: string | null; cwd?: string | null; canFocus?: boolean }, path: string | null) {
    try {
      const res = await focusLiveWindow({ sessionId: s.id, tool: s.tool, pid: s.pid ?? null, hwnd: s.hwnd ?? null, path: s.projectPath ?? s.cwd ?? path, canFocus: s.canFocus })
      toast(focusToastText(res, s.tool))
    } catch (e) {
      toast(friendlyActionError(e, 'window'))
    }
  }

  async function scan() {
    if (scanning) return
    scanning = true
    scanErr = ''
    try {
      await runProfileScan()
      sess = {} // eski oturum listeleri atılır
      await load()
      if (opened) void openSessions(opened, true)
      toast(t('pjScanDone'))
    } catch (e) {
      scanErr = msgOf(e)
    } finally {
      scanning = false
    }
  }

  async function openSessions(path: string, force = false) {
    if (!force && sess[path]?.state === 'ok') return
    sess[path] = { state: 'loading' }
    try {
      const data = await getProfileSessions(path, 20)
      sess[path] = { state: 'ok', data }
    } catch (e) {
      sess[path] = { state: 'err', msg: msgOf(e) }
    }
  }

  function toggle(p: ProfProject) {
    if (opened === p.path) { opened = null; return }
    opened = p.path
    void openSessions(p.path)
  }

  async function copyPath(path: string) {
    try {
      await navigator.clipboard.writeText(path)
      toast(t('pjCopied'))
    } catch {
      toast(path)
    }
  }

  const openFolder = (path: string) => kilit.calistir(() => openFolderNow(path))
  async function openFolderNow(path: string) {
    try {
      await openProjectFolder(path)
      toast(t('suAnFolderOpened'))
    } catch (e) {
      toast(friendlyActionError(e, 'folder'))
    }
  }

  // Okla satırlar arasında gezinme (Yukarı, Aşağı, Home, End). Tab ve Enter her zaman çalışır.
  function rowKey(e: KeyboardEvent) {
    const keys = ['ArrowDown', 'ArrowUp', 'Home', 'End']
    if (!keys.includes(e.key)) return
    const rows = [...(root?.querySelectorAll<HTMLElement>('.prow') ?? [])]
    const i = rows.indexOf(e.currentTarget as HTMLElement)
    if (i < 0) return
    e.preventDefault()
    const j = e.key === 'Home' ? 0 : e.key === 'End' ? rows.length - 1 : Math.min(rows.length - 1, Math.max(0, i + (e.key === 'ArrowDown' ? 1 : -1)))
    rows[j]?.focus()
  }

  onMount(() => {
    void load()
    const iv = setInterval(() => {
      nowTs = Date.now()
      if (!scanning && !document.hidden) void load(true)
    }, 30000)
    return () => clearInterval(iv)
  })

  const scanned = $derived(!!profile && hasScan(profile))
  const sections = $derived(profile ? groupProjects(profile.projects ?? []) : [])
  // O3: Söz'e eklenen klasörler (yalnız yolu olan gerçek klasörler), en son çalışılan en üstte.
  const mine = $derived([...realProjects()].sort((a, b) => (parseTs(b.lastAt) || 0) - (parseTs(a.lastAt) || 0)))
  const live = $derived(profile ? liveGroups(profile) : [])
  const warnings = $derived(profileWarnings(profile))
  const reasons = $derived(profile ? reasonRows(profile.unassigned) : [])
  const unTools = $derived(profile ? toolEntries(Object.fromEntries(Object.entries(profile.unassigned.byTool ?? {}).map(([k, v]) => [k, v.count]))) : [])
  const scanTs = $derived(profile ? lastScanTs(profile) : null)
  const noProjects = $derived(!!profile && scanned && (profile.projects?.length ?? 0) === 0 && (profile.unassigned?.count ?? 0) === 0)

  const sessTitle = (s: { title: string | null; tool: string; parentId?: string | null }, child = false) =>
    s.title?.trim() ? s.title : child ? t('pjSessionHelper') : t('pjSessionOf', { a: toolLabel(s.tool) })
  const when = (ts: string | null | undefined) => agoShort(ts, nowTs)
  // Zamanı bilinmeyen oturum ve proje (sunucu null verir): boş bırakılmaz, soluk bir cümleyle söylenir.
  const whenOr = (ts: string | null | undefined) => (validTs(ts) ? when(ts) : t('pjTimeUnknown'))
</script>

{#snippet liveBody(x: { s: ProfLiveSession; child: boolean })}
  <span class="dot" class:typing={liveState(x.s) === 'working'} class:warm={liveState(x.s) === 'you' || !!attnRowState(x.s.id)?.warm} aria-hidden="true"></span>
  <span class="pj-lt" title={sessionName(x.s, x.child)}>{sessionName(x.s, x.child)}</span>
  <span class="pj-lst" class:typing={x.s.level === 'yaziyor'}>
    {attnRowState(x.s.id)?.text ?? (liveState(x.s) === 'working' ? t('liveWorking') : liveState(x.s) === 'you' ? t('liveYou') : liveState(x.s) === 'idle' ? t('liveIdle') : liveState(x.s) === 'unknown' ? t('liveUnknown') : t('liveStopped'))}
  </span>
  <span class="pill">{toolLabel(x.s.tool)}</span>
  <span class="pj-time" class:unk={!validTs(x.s.lastActivity)} data-pj-ltime>{whenOr(x.s.lastActivity)}</span>
{/snippet}

<div class="page pj" data-projelerim bind:this={root}>
  <header class="page-head">
    <div class="pj-head">
      <h1 class="h1">{t('pjTitle')}</h1>
      <p class="small muted pj-lead" data-pj-lead>{t('pjLead')}</p>
      {#if profile && scanned}
        <p class="pj-sum muted" data-pj-summary>
          <span>{t('pjSumProjects', { n: profile.counts.projects })}</span><span aria-hidden="true">·</span>
          <span>{t('pjSumSessions', { n: profile.counts.sessions })}</span><span aria-hidden="true">·</span>
          {#if scanTs}<span title={fmtDate(scanTs, true)} data-pj-scan-time>{t('pjSumScan', { t: when(scanTs) })}</span>{:else}<span>{t('pjSumScanUnknown')}</span>{/if}
        </p>
      {/if}
    </div>
    {#if profile && scanned}
      <button class="btn" onclick={scan} disabled={scanning} data-pj-scan>
        {#if scanning}<span class="spin" aria-hidden="true"></span>{t('pjScanning')}{:else}<Icon name="refresh" size={16} />{t('pjScanAgain')}{/if}
      </button>
    {/if}
  </header>

  <!-- O3: önce Söz'e eklediğin klasörler (sunucunun kendi klasör listesi), sonra bilgisayarın taranmış hâli. Kaldırma ucu sunucuda yok, düğmesi de yok. -->
  <section class="pj-sec pj-mine" aria-labelledby="pj-mine-h" data-pj-mine>
    <h2 class="pj-h" id="pj-mine-h">{t('pjMineTitle')}<span class="pj-n">{mine.length}</span></h2>
    {#if mine.length === 0}
      <p class="small muted" data-pj-mine-none>{sf.projLoaded ? t('pjMineNone') : t('loading')}</p>
    {:else}
      <ul class="pj-list" aria-label={t('pjMineTitle')}>
        {#each mine as p (p.id)}
          {@const n = sf.jobs.filter((j) => j.projectId === p.id).length}
          <li class="mine-row" data-pj-mine-row={p.id}>
            <span class="pj-name" title={p.name}>{p.name}</span>
            {#if p.path}<span class="pj-path" title={p.path}>{shortPath(p.path)}</span>{/if}
            <span class="mine-n" data-pj-mine-jobs>{sf.jobsMore && n > 0 ? t('pjMineJobsMore', { n }) : t('pjMineJobs', { n })}</span>
            {#if p.path}<button type="button" class="btn btn-sm" onclick={() => giveJobToProject(p.path)} data-pj-mine-give>{t('pjMineGive')}</button>{/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  {#if scanning}
    <div class="pj-prog" role="status" data-pj-progress>
      <span class="pj-bar" aria-hidden="true"></span>
      <p class="small muted">{t('pjScanningNote')}</p>
    </div>
  {/if}
  {#if scanErr}
    <p class="err-line" role="alert" data-pj-scan-error>{t('pjScanFailed', { m: scanErr })}</p>
    <p class="retry-row"><button class="btn" onclick={scan} disabled={scanning} data-pj-scan-retry>{t('pjRetry')}</button></p>
  {/if}

  {#if loading}
    <Skeleton />
  {:else if error && !profile}
    <div data-pj-error>
      <p class="err-line" role="alert" title={error}>{t('dataError')}</p>
      <p class="retry-row"><button class="btn" onclick={() => { loading = true; void load() }} data-pj-retry>{t('pjRetry')}</button></p>
    </div>
  {:else if profile && !scanned}
    <div class="empty-state" data-pj-empty>
      <p>{t('pjEmpty')}</p>
      <button class="btn btn-primary" onclick={scan} disabled={scanning} data-pj-scan>
        {#if scanning}<span class="spin" aria-hidden="true"></span>{t('pjScanning')}{:else}{t('pjEmptyAction')}{/if}
      </button>
      <button class="btn" onclick={addFolderFlow} data-pj-add-folder><Icon name="plus" size={16} />{t('emptyAddFolder')}</button>
    </div>
  {:else if profile}
    {#if noProjects}
      <div class="empty-state" data-pj-none>
        <p>{t('pjNoneFound')}</p>
        <button class="btn btn-primary" onclick={addFolderFlow} data-pj-add-folder><Icon name="plus" size={16} />{t('emptyAddFolder')}</button>
      </div>
    {/if}

    {#if live.length}
      <section class="pj-sec" aria-labelledby="pj-live-h" data-pj-live>
        <h2 class="pj-h" id="pj-live-h">{t('pjLiveTitle')}</h2>
        <ul class="pj-live">
          {#each live as g (g.path ?? '(yok)')}
            <li class="pj-lg" data-pj-live-group={g.path ?? ''}>
              <p class="pj-lgh"><span class="pj-lgn">{g.name}</span>{#if g.hint}<span class="pj-path" title={g.path ?? ''}>{g.hint}</span>{/if}</p>
              <ul>
                {#each g.sessions as x (x.s.tool + x.s.id)}
                  <li class="pj-ls" class:child={x.child} data-pj-live-session={x.s.id} data-level={x.s.level ?? ''} data-candidate={x.s.acikAdayi ? '1' : '0'}>
                    {#if canOpenLive(x.s, g.path)}
                      <button type="button" class="pj-lsb" onclick={() => openLive(x.s, g.path)} title={t('liveRowHint')} aria-label={t('liveOpenAria', { p: g.name, n: sessionName(x.s, x.child) })} data-pj-live-open>
                        {@render liveBody(x)}
                      </button>
                    {:else}
                      <div class="pj-lsb static">{@render liveBody(x)}</div>
                    {/if}
                    <OturumOzet tool={x.s.tool} extId={x.s.id} toggle />
                  </li>
                {/each}
              </ul>
            </li>
          {/each}
        </ul>
        <p class="small muted pj-note">{t('pjLiveNote')}</p>
      </section>
    {/if}

    {#each sections as sec (sec.id)}
      {@const isOld = sec.id === 'eski'}
      <section class="pj-sec" aria-labelledby={`pj-h-${sec.id}`} data-pj-section={sec.id}>
        {#if isOld}
          <h2 class="pj-h" id={`pj-h-${sec.id}`}>
            <button class="pj-hbtn" aria-expanded={oldOpen} aria-controls="pj-list-eski" onclick={() => (oldOpen = !oldOpen)} data-pj-old-toggle>
              <span class="chev" class:open={oldOpen}><Icon name="chevron" size={14} /></span>{t(sec.label)}<span class="pj-n">{sec.items.length}</span>
            </button>
          </h2>
        {:else}
          <h2 class="pj-h" id={`pj-h-${sec.id}`}>{t(sec.label)}<span class="pj-n">{sec.items.length}</span></h2>
        {/if}
        {#if !isOld || oldOpen}
          <ul class="pj-list" class:soft={isOld} id={`pj-list-${sec.id}`} aria-label={t('pjListLabel')}>
            {#each sec.items as p, pi (p.path)}
              {@const isOpen = opened === p.path}
              {@const sv = sess[p.path]}
              <li class="pj-item" class:open={isOpen}>
                <button class="prow" aria-expanded={isOpen} aria-controls={`pd-${sec.id}-${pi}`} aria-label={t('pjOpenRow', { n: p.name })} onclick={() => toggle(p)} onkeydown={rowKey} data-pj-row={p.path} data-pj-name={p.name}>
                  <span class="chev" class:open={isOpen} aria-hidden="true"><Icon name="chevron" size={14} /></span>
                  <span class="pj-name" title={p.name}>{p.name}</span>
                  <span class="pj-path" title={p.path}>{shortPath(p.displayHint || p.path)}{#if p.missingFolder}<span class="pj-miss">{' · ' + t('pjMissing')}</span>{/if}</span>
                  <span class="pj-tools">
                    {#each toolEntries(p.sessionsByTool) as [tool, n] (tool)}<span class="pill" data-pj-tool={tool}>{toolLabel(tool)} <b>{n}</b></span>{/each}
                  </span>
                  <span class="pj-time" class:unk={!validTs(p.lastActivity)} data-pj-last>{whenOr(p.lastActivity)}</span>
                  <span class="pj-wk" data-pj-week>{p.sessions7Days > 0 ? t('pjWeekCount', { n: p.sessions7Days }) : ''}</span>
                </button>
                {#if isOpen}
                  <div class="pdet" id={`pd-${sec.id}-${pi}`} role="region" aria-label={p.name} data-pj-detail>
                    <div class="pd-meta">
                      <span class="pd-path" title={p.path}>{p.path}</span>
                      {#if !p.missingFolder}
                        <button class="btn btn-primary btn-sm" onclick={() => giveJobToProject(p.path)} data-pj-give-job><Icon name="send" size={14} />{t('pjGiveJob')}</button>
                        <button class="btn btn-sm" onclick={() => openFolder(p.path)} data-pj-open-folder><Icon name="folder" size={14} />{t('suAnOpenFolder')}</button>
                      {/if}
                      <button class="btn btn-sm" onclick={() => copyPath(p.path)} data-pj-copy><Icon name="copy" size={14} />{t('pjCopyPath')}</button>
                      {#if p.hasGit}<span class="pill">{t('pjHasGit')}</span>{/if}
                      {#if p.hasTests}<span class="pill">{t('pjHasTests')}</span>{/if}
                    </div>

                    {#if p.areas?.length}
                      <h3 class="pd-h">{t('pjAreas')}</h3>
                      <ul class="pd-areas" data-pj-areas>
                        {#each p.areas.slice(0, 12) as a (a.path)}
                          <li><span class="pd-ap" title={a.path}>{relPath(p.path, a.path)}</span><span class="muted">{t('pjAreaCount', { n: a.sessions })}</span></li>
                        {/each}
                      </ul>
                    {/if}

                    <h3 class="pd-h">{t('pjSessions')}</h3>
                    {#if !sv || sv.state === 'loading'}
                      <p class="small muted" role="status">{t('pjSessionsLoading')}</p>
                    {:else if sv.state === 'err'}
                      <p class="err-line small" role="alert">{t('pjSessionsError', { m: sv.msg })}</p>
                      <p><button class="btn btn-sm" onclick={() => openSessions(p.path, true)}>{t('pjRetry')}</button></p>
                    {:else if sv.data.sessions.length === 0}
                      <p class="small muted">{t('pjSessionsNone')}</p>
                    {:else}
                      <ul class="pd-sess" data-pj-sessions>
                        {#each nestSessions(sv.data.sessions) as x (x.s.tool + x.s.id)}
                          <li class="pd-s" class:child={x.child} data-pj-session={x.s.id}>
                            <span class="pd-st">{sessTitle(x.s, x.child)}</span>
                            {#if x.s.gitBranch}<span class="pd-br muted">{t('pjBranch', { b: x.s.gitBranch })}</span>{/if}
                            <span class="pill">{toolLabel(x.s.tool)}</span>
                            <span class="pj-time" class:unk={!validTs(x.s.lastActivity)}>{whenOr(x.s.lastActivity)}</span>
                            <OturumOzet tool={x.s.tool} extId={x.s.id} toggle />
                          </li>
                        {/each}
                      </ul>
                      {#if sv.data.total > sv.data.count}<p class="small muted">{t('pjSessionsMore', { n: sv.data.count, m: sv.data.total })}</p>{/if}
                    {/if}
                  </div>
                {/if}
              </li>
            {/each}
          </ul>
        {/if}
      </section>
    {/each}

    {#if (profile.unassigned?.count ?? 0) > 0}
      <details class="pj-un" data-pj-unassigned>
        <summary class="pj-h pj-sum2"><span class="chev"><Icon name="chevron" size={14} /></span><span>{t('pjUnTitle')}</span><span class="pj-n">{profile.unassigned.count}</span></summary>
        <p class="small muted">{t('pjUnLead')}</p>
        <ul class="pj-rs">
          {#each reasons as r (r.reason)}
            <li data-pj-reason={r.reason}><span>{r.text}</span><b>{r.count}</b></li>
          {/each}
        </ul>
        {#if unTools.length}
          <p class="pj-ut"><span class="muted small">{t('pjUnByTool')}</span>
            {#each unTools as [tool, n] (tool)}<span class="pill" data-pj-untool={tool}>{toolLabel(tool)} <b>{n}</b></span>{/each}
          </p>
        {/if}
      </details>
    {/if}

    {#if warnings.length > 0}
      <ul class="pj-warns" data-pj-warnings>
        {#each warnings as w, wi (wi)}
          <li class="small muted"><Icon name="info" size={14} /><span>{w}</span></li>
        {/each}
      </ul>
    {/if}

    {#if profile.machine && (profile.machine.memoryBytes || profile.machine.disks?.length)}
      <footer class="pj-mach small muted" data-pj-machine aria-label={t('pjMachine')}>
        <span class="pj-mh">{t('pjMachine')}</span>
        {#if profile.machine.memoryBytes}<span>{t('pjMemory', { v: memoryText(profile.machine.memoryBytes) })}</span>{/if}
        {#if profile.machine.cpuName}<span title={profile.machine.cpuName}>{cpuShort(profile.machine.cpuName)}</span>{/if}
        {#each profile.machine.disks ?? [] as d (d.letter)}
          {#if d.freeBytes != null && d.sizeBytes}
            <span class="pj-disk" class:low={diskLow(d)} data-pj-disk={diskLetter(d.letter)} data-low={diskLow(d) ? '1' : '0'}>
              {#if diskLow(d)}<Icon name="alert" size={13} />{/if}{t('pjDiskFree', { l: diskLetter(d.letter), f: gbText(d.freeBytes), s: gbText(d.sizeBytes) })}{#if diskLow(d)}{' (' + t('pjDiskLow') + ')'}{/if}
            </span>
          {/if}
        {/each}
      </footer>
    {/if}
  {/if}
</div>

<dialog class="sheet" bind:this={dlg} aria-labelledby="pj-afd-t" onclose={() => { sf.addFolderOpen = false; cancelPendingStart() }} onclick={(e) => { if (e.target === dlg) dlg?.close() }}>
  <div class="afd">
    <h2 class="h2" id="pj-afd-t">{t('addFolderTitle')}</h2>
    {#if sf.addFolderOpen}<AddFolder autofocus onadded={folderAdded} />{/if}
    <p class="afd-x"><button type="button" class="btn btn-quiet btn-sm" onclick={() => dlg?.close()} data-afd-cancel>{t('cancel')}</button></p>
  </div>
</dialog>

<style>
  /* Ölçüler: satır var(--row-s) (fareli 36, dokunmatik 44), yazı 14 ve 12 piksel, boşluklar 4'ün katı. Dar genişlik yalnız düzeni tek sütuna indirir. */
  .pj { max-width: 56rem; margin-inline: 0; }
  .pj-head { min-width: 0; }
  .pj-lead { margin: 2px 0 0; max-width: 64ch; }
  .pj-sum { display: flex; flex-wrap: wrap; gap: 0 8px; margin-top: 4px; font-variant-numeric: tabular-nums; }
  .pj-prog { margin: -8px 0 16px; }
  .pj-bar { display: block; height: 2px; border-radius: 1px; background: var(--bg-sunken); overflow: hidden; position: relative; margin-bottom: 8px; }
  .pj-bar::after { content: ''; position: absolute; inset: 0 auto 0 0; width: 30%; background: var(--working); animation: pj-slide 1.3s var(--ease) infinite; }
  @keyframes pj-slide { from { transform: translateX(-100%); } to { transform: translateX(340%); } }

  .pj-sec { margin-top: 24px; }
  .pj-mine { margin-top: 16px; }
  .mine-row { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 12px; min-height: var(--row-s, 40px); padding: 6px 8px; border-bottom: 1px solid var(--line); }
  .mine-row .pj-name { max-width: none; flex: 0 1 auto; }
  .mine-row .pj-path { flex: 1 1 8rem; min-width: 0; }
  .mine-n { font-size: var(--fs-xs); color: var(--ink-2); font-variant-numeric: tabular-nums; white-space: nowrap; }
  .pj-h { display: flex; align-items: baseline; gap: 8px; margin: 0 0 4px; font-size: var(--fs-xs); font-weight: var(--fw-medium); color: var(--ink-2); letter-spacing: 0; }
  .pj-n { color: var(--ink-3); font-variant-numeric: tabular-nums; font-weight: var(--fw-regular); }
  .pj-hbtn { display: inline-flex; align-items: center; gap: 8px; min-height: 28px; padding: 0 4px 0 0; border: 0; background: none; font: inherit; color: inherit; border-radius: var(--radius-sm); }
  .pj-hbtn:hover { color: var(--ink); }
  .chev { display: inline-flex; flex: none; color: var(--ink-3); transform: rotate(-90deg); transition: transform var(--dur-1) var(--ease); }
  .chev.open { transform: none; }

  /* Etiket: düz yazı gibi (çerçeve ve yuvarlak köşe yok, imleç ok). Tıklanmaz, düğme sanılmaz. */
  .pill { display: inline-flex; align-items: center; gap: 4px; flex: none; min-height: 20px; padding: 0 6px; border-radius: var(--radius-sm); background: var(--bg-sunken); font-size: var(--fs-xs); line-height: 1; color: var(--ink-2); white-space: nowrap; cursor: default; }
  .pill b { color: var(--ink); font-variant-numeric: tabular-nums; font-weight: var(--fw-medium); }

  /* şu an açık */
  .pj-live { display: grid; grid-template-columns: minmax(0, 1fr); gap: 8px; }
  .pj-lg { min-width: 0; border-top: 1px solid var(--line); padding-top: 4px; }
  .pj-lgh { display: flex; align-items: baseline; gap: 8px; min-height: 28px; min-width: 0; }
  .pj-lgn { font-weight: var(--fw-medium); font-size: var(--fs-sm); white-space: nowrap; }
  .pj-ls { display: flex; flex-wrap: wrap; align-items: center; min-width: 0; border-radius: var(--radius-sm); }
  .pj-lsb { flex: 1 1 0; display: flex; align-items: center; gap: 8px; min-width: 0; min-height: 32px; padding: 0 4px; border: 0; border-radius: var(--radius-sm); background: none; color: inherit; font: inherit; text-align: left; }
  button.pj-lsb { cursor: pointer; }
  button.pj-lsb:hover { background: var(--hover); }
  .pj-ls.child .pj-lsb { padding-left: 24px; }
  .afd { padding: 18px 20px 20px; display: flex; flex-direction: column; gap: 12px; }
  .afd-x { margin: 0; display: flex; justify-content: flex-end; }
  .pj-lt { flex: 1 1 0; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .pj-lst { flex: none; font-size: var(--fs-xs); color: var(--ink-2); }
  .pj-lst.typing { color: var(--working); font-weight: var(--fw-medium); }
  .dot { flex: none; width: 8px; height: 8px; border-radius: 50%; border: 1.5px solid var(--line-strong); }
  .dot.warm { border-color: var(--waiting); background: var(--waiting); }
  .dot.typing { border-color: var(--working); background: var(--working); animation: pj-pulse 1.6s var(--ease) infinite; }
  @keyframes pj-pulse { 0%, 100% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--working) 45%, transparent); } 50% { box-shadow: 0 0 0 4px transparent; } }
  .pj-note { margin-top: 8px; }

  /* proje listesi: kenar çubuğu yoğunluğunda düz liste */
  .pj-list { border-top: 1px solid var(--line); }
  .pj-item { border-bottom: 1px solid var(--line); }
  .pj-item.open { background: var(--hover); }
  .prow { display: flex; align-items: center; gap: 8px; width: 100%; min-height: var(--row-s); padding: 0 8px 0 4px; border: 0; background: none; text-align: left; font: inherit; color: var(--ink); border-radius: var(--radius-sm); }
  .prow:hover { background: var(--hover); }
  .pj-name { flex: 0 1 auto; max-width: 40%; min-width: 4rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--fs-sm); font-weight: var(--fw-medium); }
  .pj-path { flex: 1 1 0; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--fs-xs); color: var(--ink-3); }
  .pj-lgh .pj-path { flex: 0 1 auto; }
  .pj-miss { color: var(--ink-3); }
  .pj-tools { display: flex; flex: none; gap: 4px; }
  .pj-time { flex: none; min-width: 5.75rem; text-align: right; white-space: nowrap; font-size: var(--fs-xs); color: var(--ink-2); font-variant-numeric: tabular-nums; }
  .pj-time.unk { color: var(--ink-3); }
  .pj-ls .pj-time, .pd-s .pj-time { min-width: 0; }
  .pj-wk { flex: none; width: 5rem; text-align: right; font-size: var(--fs-xs); color: var(--ink-3); font-variant-numeric: tabular-nums; }
  .pj-list.soft .pj-name { font-weight: var(--fw-regular); color: var(--ink-2); }

  .pdet { padding: 4px 12px 16px 28px; display: grid; gap: 8px; }
  .pd-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
  .pd-path { flex: 1 1 12rem; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--fs-xs); color: var(--ink-2); font-family: var(--mono); }
  .pd-h { margin-top: 8px; font-size: var(--fs-xs); font-weight: var(--fw-medium); color: var(--ink-2); }
  .pd-areas li { display: flex; justify-content: space-between; gap: 12px; min-height: 28px; align-items: center; font-size: var(--fs-xs); }
  .pd-ap { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-family: var(--mono); color: var(--ink-2); }
  .pd-s { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; min-height: 32px; min-width: 0; }
  .pd-s.child { padding-left: 20px; }
  .pd-st { flex: 1 1 0; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .pd-br { flex: none; font-size: var(--fs-xs); max-width: 10rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

  /* projeye bağlanmayan oturumlar */
  .pj-un { margin-top: 28px; border-top: 1px solid var(--line); padding-top: 4px; }
  .pj-sum2 { cursor: pointer; min-height: 32px; align-items: center; list-style: none; margin: 0; }
  .pj-sum2::-webkit-details-marker { display: none; }
  .pj-un[open] > .pj-sum2 .chev { transform: none; }
  .pj-rs { margin: 8px 0; }
  .pj-rs li { display: flex; justify-content: space-between; gap: 12px; min-height: 32px; align-items: center; border-bottom: 1px solid var(--line); }
  .pj-rs b { font-variant-numeric: tabular-nums; font-weight: var(--fw-medium); }
  .pj-ut { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-top: 8px; }

  /* bu bilgisayar */
  .pj-mach { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 16px; margin-top: 32px; padding-top: 12px; border-top: 1px solid var(--line); }
  .pj-mh { font-weight: var(--fw-medium); color: var(--ink-2); }
  .pj-disk { display: inline-flex; align-items: center; gap: 4px; font-variant-numeric: tabular-nums; }
  .pj-disk.low { color: var(--waiting); font-weight: var(--fw-medium); }

  /* 440px-520px masaüstü panelinde kısa klasör ipucu (.pj-path) ve yardımcı rozetleri (.pj-tools, .pj-ls .pill) GİZLENMEZ! */
  .pj-warns { display: grid; gap: 4px; margin-top: 20px; }
  .pj-warns li { display: flex; align-items: center; gap: 6px; }
  @media (max-width: 760px) {
    .pj-wk { display: none; }
    .pj-name { max-width: 34%; min-width: 3.5rem; }
    .pj-tools { overflow: hidden; max-width: 38%; }
    .pj-time { min-width: 4.2rem; }
  }
  @media (max-width: 420px) {
    .prow .pj-path { display: none; }
    .pj-name { flex: 1 1 0; max-width: none; }
    .pj-tools .pill:nth-child(n + 3) { display: none; }
    /* İş adı çiplerin yüzünden birkaç harfe inmesin (A-PRJ-01): ad iki satıra sarar, çipler alt satıra iner. */
    .pj-lsb { flex-wrap: wrap; row-gap: 2px; padding-block: 4px; }
    .pj-lt { flex: 1 1 9rem; white-space: normal; overflow-wrap: anywhere; display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
  }
  /* Dokunmatik: oturum ve başlık satırları dokunma boyutuna çıkar (proje satırı --row-s ile 44 olur). */
  @media (pointer: coarse) and (hover: none) {
    .pj-lsb, .pd-s, .pj-rs li, .pj-sum2 { min-height: 44px; }
    .pj-lgh, .pj-hbtn, .pd-areas li { min-height: 36px; }
  }
  @media (prefers-reduced-motion: reduce) {
    .dot.typing { animation: none; }
    .pj-bar::after { animation: none; width: 100%; opacity: 0.5; }
  }
</style>
