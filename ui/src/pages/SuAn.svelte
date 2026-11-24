<script lang="ts">
  import { onMount, tick } from 'svelte'
  import { loadPendingQueue, qp } from '../lib/kuyruk.svelte'
  import { notCumleleri } from '../lib/yarida'
  import { at, loadAttention } from '../lib/attention.svelte'
  import { sadeMi } from '../lib/hata'
  import LostReplies from '../lib/components/LostReplies.svelte'
  import AddFolder from '../lib/components/AddFolder.svelte'
  import AttentionRow from '../lib/components/AttentionRow.svelte'
  import DonePanel from '../lib/components/DonePanel.svelte'
  import Icon from '../lib/components/Icon.svelte'
  import OturumOzet from '../lib/components/OturumOzet.svelte'
  import IntroCard from '../lib/components/IntroCard.svelte'
  import { isSessionItem } from '../lib/components/SessionAttention.svelte'
  import NewJobBar from '../lib/components/NewJobBar.svelte'
  import WaitCard from '../lib/components/WaitCard.svelte'
  import SetupCard from '../lib/components/SetupCard.svelte'
  import { t } from '../lib/i18n/index.svelte'
    import { focusLiveWindow, focusToastText, liveSeenText, liveSplit, openProjectFolder, profileWarnings, sessionName, toolFullLabel, toolLabel, validTs, agoShort, type LiveRow, type LiveState } from '../lib/profil'
  import { attnRowState, autoScanInBackground, pf, refreshProfile } from '../lib/profileStore.svelte'
  import { go, router } from '../lib/router.svelte'
  import { POLL_MS, isVisible, pollWhileVisible } from '../lib/visible'
  import { app, toast } from '../lib/store.svelte'
  import { agentName, cancelPendingStart, bucketMeta, bucketOf, chipLabelKey, giveJobToProject, isiIzle, jobsIn, refreshSurface, rowLine, select, sf } from '../lib/surface.svelte'
  import { azOnceGorunur } from '../lib/gezinme'
  import type { AttentionItem } from '../lib/types5'
  import { describeWaiting } from '../lib/waiting'
  import { ago, friendlyActionError, parseTs } from '../lib/util'

  let dlg: HTMLDialogElement | undefined = $state()
  let nowTs = $state(Date.now())

  onMount(() => {
    void refreshProfile({ autoScan: true })
    void loadAttention()
    void loadPendingQueue()
    refreshSurface()
    // Kapanan pencere listeden kendiliğinden düşsün. Sunucu her bakışta gerçek yardımcı komutlarını çalıştırır (pahalı):
    // görünmezken hiç istenmez, görünürken 20 sn'de bir, ekrana dönünce hemen bir kez (lib/visible.ts).
    const stop = pollWhileVisible(() => { void loadPendingQueue(); if (!pf.scanning) void refreshProfile({ silent: true }) }, POLL_MS)
    const tk = setInterval(() => { if (isVisible()) nowTs = Date.now() }, 1000) // şerit en geç 10 sn içinde çıksın (B-GEN-02)
    // Bağlantı koptuysa 8 sn'de bir yeniden bakılır: gelince şerit kendiliğinden kalkar (C5).
    const rt = setInterval(() => { if (isVisible() && at.error) void loadAttention() }, 8000)
    return () => { stop(); clearInterval(tk); clearInterval(rt) }
  })

  $effect(() => {
    void app.tick
    void loadAttention()
    refreshSurface()
  })

  $effect(() => {
    if (sf.addFolderOpen && dlg && !dlg.open) dlg.showModal()
    if (!sf.addFolderOpen && dlg?.open) dlg.close()
  })

  // Bağlantı koptu mu (C5): son okuma başarısız ve ekrandaki bilgi 10 sn'den eski. Kartlar soluk gösterilir, şerit "son bilgi N sn önce" der.
  const linkLost = $derived((!!at.error && at.snap !== null && at.lastOk > 0 && nowTs - at.lastOk > 8000) || (!!pf.error && pf.data !== null && at.fails > 0))
  const linkAge = $derived.by(() => {
    const s = Math.max(0, Math.round((nowTs - at.lastOk) / 1000))
    return s < 60 ? t('linkSecs', { n: s }) : t('linkMins', { n: Math.round(s / 60) })
  })
  // Sunucu yeni bir tür gönderirse (ör. izin bekleyen, hata verip duran) çökmeden genel satırla gösterilir.
  const KNOWN_KINDS = ['ask', 'accept', 'blocked']
  const IGNORED_KINDS = ['done', 'uncertain']
  // Terminalde ya da başka pencerede açık yardımcılardan gelenler (session_*) kendi sade satırıyla, ayrı listelerde gösterilir.
  const allItems = $derived(at.snap?.items ?? [])
  // "Emin değilim" türü sessiz kalır, yalnız "sırayı sana verdi" (durdu, bitti denmedi) kart olarak görünür.
  const sessionItems = $derived(allItems.filter((i) => isSessionItem(i) && (i.kind !== 'uncertain' || i.reason === 'session_turn_end')))
  // Üç ayrı grup: acil (izin, soru, hata), durup sırayı verenler (acil değil, bitti de değil), bitti diyenler. Sıra bu.
  const sessUrgent = $derived(sessionItems.filter((i) => i.reason !== 'session_done' && i.reason !== 'session_turn_end'))
  const sessTurn = $derived(sessionItems.filter((i) => i.reason === 'session_turn_end'))
  const sessDone = $derived(sessionItems.filter((i) => i.reason === 'session_done'))
  const pendingItems = $derived(allItems.filter((i) => !isSessionItem(i) && (KNOWN_KINDS.includes(i.kind) || !IGNORED_KINDS.includes(i.kind))))
  const hero = $derived<AttentionItem | null>(pendingItems.find((i) => KNOWN_KINDS.includes(i.kind)) ?? null)
  const restPending = $derived(pendingItems.filter((i) => i !== hero))

  // O9: öncelikli kart da öteki kartlarla aynı biçimdedir (AttentionRow). Ekranda tek birincil düğme Başlat olur, kart düğmeleri ikincildir.

  // Bölüm 2: Açık İşlerim (bilgisayardaki canlı yardımcılar + Söz üzerinden çalışan işler). Düz liste: her satırda proje, yardımcı, ne yapıyor, ne kadar oldu.
  const split = $derived(pf.data ? liveSplit(pf.data, nowTs) : { groups: [], rows: [], old: [] })
  const warnings = $derived(profileWarnings(pf.data))
  const sozRunning = $derived([...jobsIn('running'), ...jobsIn('queued')])
  const liveCount = $derived(split.rows.filter((r) => r.state !== 'you').length + sozRunning.length) // bekleyenler ayrı bölümde: sayı bu listedeki satırlarla aynı (K15)
  // Sunucu "seni bekliyor" dediyse bu açık yardımcılar "Benden Beklenen" kutusunun en üstünde durur; aşağıdaki listede tekrar edilmez.
  // Aynı yardımcı /api/attention'da da varsa (externalSessionId) iki kez gösterilmez: sunucunun birleşik öğesi esastır.
  const waiting = $derived(split.rows.filter((r) => r.state === 'you' && !sessionItems.some((i) => i.externalSessionId != null && String(i.externalSessionId) === String(r.s.id))))
  const outsideCount = $derived(waiting.length)
  // Seni bekleyen en az bir öğe varsa Benden Beklenen bölümü ekranın EN ÜSTÜNDE durur (iş verme kutusunun ve tanıtımın üstünde). Yoksa tek ince satır olur.
  const urgentCount = $derived(waiting.length + sessUrgent.length + pendingItems.length)
  const anyItems = $derived(urgentCount > 0 || sessTurn.length > 0 || sessDone.length > 0)
  const lang = $derived(app.lang === 'en' ? 'en' : 'tr')
  // Bölüm sayısı ekrandaki kart sayısıyla aynı (K15): acil kartlar + sırayı sana verenler + bitti diyenler. Alt şerit rozeti, İşler çipi ve Sıra özeti aynı sayıyı profileStore.waitingBreakdown() ile verir (C4); "emin değilim" türü kart olmadığı için sayılmaz.
  const expectTotal = $derived(urgentCount + sessTurn.length + sessDone.length)
  const seenText = $derived(liveSeenText(pf.data, nowTs))
  const liveNote = $derived(pf.data?.liveNote?.trim() && pf.data?.liveSource !== 'agents-komutu' ? pf.data.liveNote.trim() : '')
  // Bekleyenler okunamadıysa (sunucu hata verdi ya da boş cevap geldi) "seni bekleyen yok" denmez: açık hata durumu çıkar.
  // Hangi yardımcının neden görünmediğini söyler (K-13); sunucunun cümlesi sade değilse genel söz.
  const liveNoteText = $derived.by(() => {
    // Sunucu zaten tam cümle söylüyor: önüne ikinci bir giriş eklenmez; birden çok not ayrı kısa cümleler olur.
    return notCumleleri((liveNote ? [liveNote] : warnings).filter((x) => sadeMi(x))) || t('liveSoftNote')
  })
  const attnFailed = $derived(!!at.error && !at.snap)
  const expectScanning = $derived(!pf.error && !attnFailed && (!pf.loaded || pf.scanning || !at.loaded))

  // Aynı projede çok yardımcı (ajan sürüsü) ekranı doldurmasın: proje başına tek özet satırı, tıklayınca açılır.
  const SWARM_MIN = 4
  const FOLD_MIN = 5
  type Block = { key: string; name: string; path: string | null; rows: LiveRow[]; total: number; working: number; idle: number; you: number; unknown: number; swarm: boolean }
  const blocks = $derived.by(() => {
    const m = new Map<string, Block>()
    for (const r of split.rows) {
      const key = (r.path ?? '') + '|' + r.projectName
      let b = m.get(key)
      if (!b) { b = { key, name: r.projectName, path: r.path, rows: [], total: 0, working: 0, idle: 0, you: 0, unknown: 0, swarm: false }; m.set(key, b) }
      b.total++
      if (r.state === 'working') b.working++
      else if (r.state === 'idle') b.idle++
      else if (r.state === 'you') b.you++
      else if (r.state === 'unknown') b.unknown++
      if (r.state !== 'you') b.rows.push(r)
    }
    const out = [...m.values()].filter((b) => b.rows.length > 0)
    for (const b of out) b.swarm = b.total >= SWARM_MIN
    return out
  })
  // Projesi belli olmayan oturumlar (ör. Antigravity klasörü kaydetmiyor) ana listeye karışmaz, ayrı ve dürüst bir bölümde durur.
  const projBlocks = $derived(blocks.filter((b) => b.path !== null))
  const noProjRows = $derived(blocks.filter((b) => b.path === null).flatMap((b) => b.rows))
  const noProjWhy = $derived.by(() => {
    const tools = [...new Set(noProjRows.map((r) => toolLabel(r.s.tool)))]
    return tools.length > 0 ? t('liveNoProjectWhy', { tools: tools.join(lang === 'en' ? ' and ' : ' ve ') }) : t('liveNoProjectWhyAny')
  })
  let noProjOpen = $state(false)
  const noProjFold = $derived(noProjRows.length > FOLD_MIN)

  // Açık işler: çalışanlar önde, boşta olanlar geri planda. Boşta olan 5'ten fazlaysa "Boşta olanlar (n)" altında katlanır.
  type Entry = { key: string; kind: 'job' | 'swarm' | 'row'; active: boolean; idleN: number; job?: (typeof sozRunning)[number]; block?: Block; row?: LiveRow }
  const entries = $derived.by(() => {
    const out: Entry[] = []
    for (const j of sozRunning) out.push({ key: 'j' + j.id, kind: 'job', job: j, active: true, idleN: 0 })
    for (const b of projBlocks) {
      if (b.swarm) out.push({ key: 's' + b.key, kind: 'swarm', block: b, active: b.working > 0, idleN: b.working > 0 ? 0 : b.rows.length })
      else for (const r of b.rows) out.push({ key: 'r' + r.s.tool + ':' + r.s.id, kind: 'row', row: r, active: r.state === 'working', idleN: r.state === 'working' ? 0 : 1 })
    }
    return [...out.filter((e) => e.active), ...out.filter((e) => !e.active)]
  })
  const idleTotal = $derived(entries.reduce((n, e) => n + e.idleN, 0))
  const foldIdle = $derived(idleTotal > FOLD_MIN)
  const shownEntries = $derived(foldIdle ? entries.filter((e) => e.active) : entries)
  const foldedEntries = $derived(foldIdle ? entries.filter((e) => !e.active) : [])
  let idleOpen = $state(false)
  const hasList = $derived(entries.length > 0 || noProjRows.length > 0)
  let openSwarms = $state<Record<string, boolean>>({})
  // Sayı ile açılınca görünen satırlar AYNI kaynaktan gelir (K-10): satır sayısı b.rows. Seni bekleyenler yukarıdaki kartlarda durur ve bu cümlede açıkça söylenir.
  const swarmText = (b: Block) => {
    const parts: string[] = []
    if (b.working > 0) parts.push(t('liveSwarmWork', { n: b.working }))
    if (b.idle > 0) parts.push(t('liveSwarmIdle', { n: b.idle }))
    if (b.unknown > 0) parts.push(t('liveSwarmUnknown', { n: b.unknown }))
    if (b.you > 0) parts.push(t('liveSwarmAbove', { n: b.you }))
    return t('liveSwarm', { n: b.rows.length }) + (parts.length ? ': ' + parts.join(', ') : '')
  }
  // Kapsülden ya da başka yerden gelen adres: #/suan?oturum=<kimlik> (isteğe bağlı &arac=claude). Satır görünür kılınır ve kısa süre vurgulanır.
  // <kimlik>: yardımcı oturumunun kimliği; Söz işi için is-<iş numarası>. Satır katlı bir yerdeyse o yer açılır. Bulunamazsa sade bir cümle söylenir.
  let hitDone = ''
  let hitNote = $state(false)
  let hitNoteTimer: ReturnType<typeof setTimeout> | undefined
  $effect(() => {
    const id = router.oturum
    const key = id + ':' + router.oturumTick
    const ready = pf.loaded && at.loaded && !pf.scanning
    void entries.length
    if (!id || !ready || hitDone === key) return
    const sel = (x: string) => {
      const v = CSS.escape(x)
      return id.startsWith('is-')
        ? `[data-running-job="${CSS.escape(id.slice(3))}"], [data-recent-job="${CSS.escape(id.slice(3))}"], [data-attention-job="${CSS.escape(id.slice(3))}"]`
        : `[data-live-session="${v}"], [data-wait-session="${v}"], [data-old-session="${v}"], [data-attention-session="${v}"]`
    }
    const find = () => document.querySelector<HTMLElement>(sel(id))
    let el = find()
    if (!el) {
      idleOpen = true; noProjOpen = true; oldOpen = true
      for (const b of blocks) openSwarms[b.key] = true
    }
    void tick().then(() => {
      el = find()
      hitDone = key
      if (!el) {
        // Kart yok: bildirimdeki iş artık beklemiyor. Kısa bir toast yerine ekranda durup kendiliğinden kalkan not.
        hitNote = true
        clearTimeout(hitNoteTimer)
        hitNoteTimer = setTimeout(() => (hitNote = false), 12000)
        return
      }
      hitNote = false
      el.setAttribute('data-hit', '1')
      el.scrollIntoView({ block: 'center', behavior: 'smooth' })
      setTimeout(() => el?.removeAttribute('data-hit'), 4500)
    })
  })

  // Elle "şimdi bak": otomatik yoklama beklemeden profili ve bekleyenleri yeniler.
  let looking = $state(false)
  let lookMsg = $state('')
  let lookTimer: ReturnType<typeof setTimeout> | undefined
  // "Şimdi bak" ve "Tekrar dene" görünür bir sonuç verir (C17): bakarken "Bakıyorum…", sonra "Yeniden baktım." ya da "Hâlâ göremiyorum."
  async function lookNow() {
    if (looking) return
    looking = true
    lookMsg = t('lookBusy')
    clearTimeout(lookTimer)
    try {
      await Promise.all([refreshProfile({ silent: true }), loadAttention()])
      refreshSurface()
      lookMsg = at.error || pf.error ? t('lookStill') : t('lookDone')
    } finally {
      looking = false
      lookTimer = setTimeout(() => (lookMsg = ''), 4000)
    }
  }
  let oldOpen = $state(false)
  let actionErr = $state('')

  // Kuyruktaki mesajın başı ve hangi klasör ya da pencere için olduğu (pencere açıksa yardımcının adı da yazılır).
  const queueHead = (x: string) => { const o = x.replace(/\s+/g, ' ').trim(); return o.length > 90 ? o.slice(0, 89) + '…' : o }
  const queueTarget = (q: { projectName?: string | null; projectId: number | null; sessionId: number | null }) => {
    const row = q.sessionId != null ? split.rows.find((r) => String(r.s.id) === String(q.sessionId)) ?? split.old.find((r) => String(r.s.id) === String(q.sessionId)) : undefined
    const proj = q.projectName?.trim() || row?.projectName || (q.projectId != null ? sf.projects.find((p) => p.id === q.projectId)?.name : '') || ''
    return { proj: proj || t('kqNoProject'), tool: row?.s.tool ?? '' }
  }
  const stateLabel = (st: LiveState) => t(st === 'working' ? 'liveWorking' : st === 'you' ? 'liveYou' : st === 'idle' ? 'liveIdle' : st === 'unknown' ? 'liveUnknown' : 'liveStopped')
  const whenShort = (ts: string | null | undefined) => (validTs(ts) ? agoShort(ts, nowTs) : '')
  // "Çalışıyor" zaten şimdi demektir: zaman yazılmaz. Diğerlerinde "Bekliyor · 3 dk önce".
  // Her satırda durum ve ne zamandır öyle olduğu görünür ("Çalışıyor · az önce", "Seni bekliyor · 3 dk önce").
  // Aynı oturum üstte kartta bir durumdaysa (izin bekliyor, soru sordu, bitti dedi) bu satır da aynı durumu söyler (C3).
  const stateText = (r: LiveRow) => {
    const w = whenShort(r.s.lastActivity)
    const lbl = attnRowState(r.s.id)?.text ?? stateLabel(r.state)
    return !w ? lbl : lbl + ' · ' + w
  }
  const rowWarm = (r: LiveRow) => r.state === 'you' || !!attnRowState(r.s.id)?.warm
  const rowTitle = (r: LiveRow) => [toolFullLabel(r.s.tool), r.hint || r.projectName, stateText(r)].filter(Boolean).join(' · ')
  const jobSince = (j: { givenAt?: string | null; createdAt: string }) => { const w = whenShort(j.givenAt ?? j.createdAt); return w ? t('liveStartedAgo', { w }) : '' }
  const rowName = (r: LiveRow) => sessionName(r.s, r.child)
  const canOpen = (r: LiveRow) => !!(r.s.canFocus || r.s.pid != null || r.s.hwnd != null || r.path || r.s.projectPath || r.s.cwd)

  async function openRow(r: LiveRow) {
    actionErr = ''
    try {
      const res = await focusLiveWindow({
        sessionId: r.s.id,
        tool: r.s.tool,
        pid: r.s.pid ?? null,
        hwnd: r.s.hwnd ?? null,
        path: r.s.projectPath ?? r.s.cwd ?? r.path,
        canFocus: r.s.canFocus,
      })
      toast(focusToastText(res, r.s.tool))
    } catch (e) {
      actionErr = friendlyActionError(e, 'window')
    }
  }
  async function openOldFolder(r: LiveRow) {
    actionErr = ''
    const p = r.s.projectPath ?? r.s.cwd ?? r.path
    if (!p) return
    try {
      await openProjectFolder(p)
      toast(t('suAnFolderOpened'))
    } catch (e) {
      actionErr = friendlyActionError(e, 'folder')
    }
  }
  function openSozJob(id: number) {
    select(id, { detail: true })
    go('is', String(id))
  }
  function retryLive() {
    pf.error = ''
    pf.autoScanTried = false
    void refreshProfile({ autoScan: true }).then((p) => {
      if (p && p.scannedAt === null) void autoScanInBackground()
    })
  }

  // O5: "Az önce verdiğin" satırı (gezinme.ts azOnceGorunur).
  const sonIs = $derived.by(() => {
    const s = sf.sonVerilen
    if (!s) return null
    const j = sf.jobs.find((x) => x.id === s.id)
    if (!j) return null
    const b = bucketOf(j)
    return azOnceGorunur({ verildi: s.at, simdi: nowTs, suruyor: b === 'running' || b === 'queued' }) ? j : null
  })
  // O8: özet satırı bekleyenler bölümüne kaydırır ve ilk karta odaklanır.
  async function bekleyenlereGit() {
    await tick()
    const sec = document.querySelector<HTMLElement>('[data-suan-expect]')
    sec?.scrollIntoView({ block: 'start', behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' })
    const ilk = sec?.querySelector<HTMLElement>('[data-wait-list] button')
    ilk?.focus({ preventScroll: true })
  }

  // Son biten işler (hangi projede ne bitti sorusuna 10 saniyede yanıt)
  const recentFinished = $derived(
    sf.jobs
      .filter((j) => ['passed', 'accepted', 'nochange', 'failed', 'unproven', 'nowork'].includes(bucketOf(j)))
      .sort((a, b) => parseTs(b.finishedAt ?? b.createdAt) - parseTs(a.finishedAt ?? a.createdAt))
      .slice(0, 4),
  )
</script>

{#snippet entryView(e: Entry)}
  {#if e.kind === 'job' && e.job}
    {@const j = e.job}
    {@const queued = bucketOf(j) === 'queued'}
    <li class="live-row" class:working={!queued} class:idle={queued} data-running-job={j.id}>
      <button type="button" class="lr-open" onclick={() => openSozJob(j.id)} title={t('liveRowHint')} aria-label={t('liveOpenAria', { p: j.projectName ?? '', n: j.title })}>
        <span class="dot" class:typing={!queued} aria-hidden="true"></span>
        <span class="lr-who"><span class="lr-proj" title={j.projectName ?? ''}>{j.projectName ?? ''}</span><span class="tool-badge" data-tool-badge={j.agent ?? 'claude'}>{agentName(j.agent)}</span></span>
        <span class="lr-name" title={j.title}>{j.title}</span>
        <span class="lr-state" class:typing={!queued}>{queued ? t('liveQueued') : t('liveWorking')}{#if jobSince(j)}{' · ' + jobSince(j)}{/if}</span>
      </button>
    </li>
  {:else if e.kind === 'swarm' && e.block}
    {@const b = e.block}
    {@const open = !!openSwarms[b.key]}
    <li class="live-row swarm" class:working={b.working > 0} class:idle={b.working === 0} data-swarm={b.name}>
      <button type="button" class="lr-open" aria-expanded={open} aria-label={t('liveSwarmAria', { p: b.name, n: b.rows.length })} onclick={() => (openSwarms[b.key] = !open)} data-swarm-toggle>
        <span class="dot" class:typing={b.working > 0} class:warm={b.you > 0} aria-hidden="true"></span>
        <span class="lr-who"><span class="lr-proj" title={b.name}>{b.name}</span></span>
        <span class="lr-name" data-swarm-text>{swarmText(b)}</span>
        <span class="lr-state"><span class="chev" class:open><Icon name="chevron" size={14} /></span></span>
      </button>
      {#if b.path}
        <button type="button" class="lr-give" onclick={() => giveJobToProject(b.path)} title={t('liveGiveHint')} aria-label={t('liveGiveAria', { p: b.name })} data-live-give>
          <Icon name="plus" size={16} />
        </button>
      {/if}
    </li>
    {#if open}
      {#each b.rows as r (r.s.tool + ':' + r.s.id)}{@render sessRow(r, true)}{/each}
    {/if}
  {:else if e.row}
    {@render sessRow(e.row, false)}
  {/if}
{/snippet}

{#snippet rowBody(o: { state: LiveState; proj: string; tool: string; name: string; stateText: string; child?: boolean })}
  <span class="dot" class:typing={o.state === 'working'} class:warm={o.state === 'you'} aria-hidden="true"></span>
  <span class="lr-who">{#if o.proj}<span class="lr-proj" title={o.proj}>{o.proj}</span>{/if}<span class="tool-badge" data-tool-badge={o.tool}>{toolLabel(o.tool)}</span></span>
  <span class="lr-name" title={o.name}>{o.name}</span>
  <span class="lr-state" class:typing={o.state === 'working'} class:warm={o.state === 'you'} title={o.state === 'unknown' ? t('liveUnknownHelp') : undefined}>{o.stateText}</span>
{/snippet}

{#snippet sessRow(r: LiveRow, sub: boolean)}
  {@const nm = rowName(r)}
  {@const nop = r.path === null}
  <li class="live-row" class:child={r.child} class:sub class:working={r.state === 'working'} class:idle={r.state !== 'working'} data-live-session={r.s.id} data-live-tool={r.s.tool} data-evidence={r.s.evidence ?? ''} data-state={r.state}>
    {#if canOpen(r)}
      <button type="button" class="lr-open" onclick={() => openRow(r)} title={rowTitle(r) + '. ' + t('liveRowHint')} aria-label={t('liveOpenAria', { p: r.projectName, n: nm })} data-live-focus>
        {@render rowBody({ state: rowWarm(r) ? 'you' : r.state, proj: nop ? '' : r.projectName, tool: r.s.tool, name: nm, stateText: stateText(r) })}
      </button>
    {:else}
      <div class="lr-open static">
        {@render rowBody({ state: rowWarm(r) ? 'you' : r.state, proj: nop ? '' : r.projectName, tool: r.s.tool, name: nm, stateText: stateText(r) })}
      </div>
    {/if}
    <OturumOzet tool={r.s.tool} extId={r.s.id} toggle />
    {#if r.path || r.s.projectPath || r.s.cwd}
      <button type="button" class="lr-give" onclick={() => giveJobToProject(r.s.projectPath ?? r.s.cwd ?? r.path, r.s.tool)} title={t('liveGiveHint')} aria-label={t('liveGiveAria', { p: r.projectName })} data-live-give>
        <Icon name="plus" size={16} />
      </button>
    {/if}
  </li>
{/snippet}

<!-- Benden Beklenen. Seni bekleyen varsa ekranın en üstünde ve tam; yoksa tek ince satır. -->
{#snippet expectSection()}
  <section class="sec expect" class:thin={!anyItems} aria-labelledby="sec-expect" data-suan-expect data-expect-first="0">
    {#if anyItems}
      <header class="sec-bar" title={t('suAnExpectLead')}>
        <h2 class="sec-title" id="sec-expect">{t('suAnExpectTitle')}</h2>
        {#if expectTotal > 0}
          <span class="badge-count" data-expect-count>{expectTotal}</span>
        {/if}
      </header>

      <ul class="wc-list" data-wait-list>
        {#each waiting as r (r.s.tool + ':' + r.s.id)}
          {@const nm = rowName(r)}
          {@const helperName = toolFullLabel(r.s.tool)}
          {@const wi = describeWaiting(r.s.waitingFor, helperName, lang)}
          {@const perm = wi.sentence !== null && wi.kind !== 'plain'}
          <WaitCard
            label={perm ? t('saPermission') : t('saWaiting')}
            icon={perm ? 'lock' : 'waiting'}
            project={r.projectName}
            helper={helperName}
            ago={whenShort(r.s.lastActivity)}
            sentence={wi.sentence ?? t('saWaitingSent', { h: helperName })}
            target={wi.target}
            quote={wi.quote ?? (wi.sentence === null ? (r.s.waitingFor?.trim() || null) : null)}
            danger={wi.danger}
            kind="ask"
            onopen={canOpen(r) ? () => void openRow(r) : undefined}
            openHint={t('saBodyGo')}
            data-wait-session={r.s.id}
            data-live-tool={r.s.tool}
          >
            <OturumOzet tool={r.s.tool} extId={r.s.id} waitKind={perm ? 'permission' : 'waiting'} />
            {#if canOpen(r)}
              <div class="wc-acts">
                <button type="button" class="btn btn-sm wait-go" onclick={() => openRow(r)} aria-label={t('waitGoAria', { p: r.projectName, n: nm })} data-wait-go>
                  <Icon name="terminal" size={14} />{t('waitGo')}
                </button>
              </div>
            {/if}
          </WaitCard>
        {/each}

        {#each sessUrgent as it (it.id)}<AttentionRow item={it} flat />{/each}

        {#if hero}<AttentionRow item={hero} flat />{/if}

        {#each restPending as it (it.id)}
          <AttentionRow item={it} flat />
        {/each}

        {#if sessTurn.length > 0}
          <li class="grp-head" data-turn-head><span class="grp-t">{t('saTurnGroup')}</span><span class="sec-n" data-turn-count>{sessTurn.length}</span><span class="grp-l">{t('saTurnGroupLead')}</span></li>
          {#each sessTurn as it (it.id)}<AttentionRow item={it} flat />{/each}
        {/if}

        {#if sessDone.length > 0}
          <li class="grp-head" data-done-head><span class="grp-t">{t('saDoneGroup')}</span><span class="sec-n">{sessDone.length}</span></li>
        {/if}
        {#each sessDone as it (it.id)}<AttentionRow item={it} flat />{/each}
      </ul>
    {:else}
      <div class="thin-line" class:failed={attnFailed} data-expect-thin title={attnFailed ? '' : expectScanning ? (pf.progressText || t('suAnExpectScanning')) : t('suAnExpectNoneHint')}>
        {#if attnFailed}
          <Icon name="alert" size={15} strokeWidth={2} />
          <h2 class="sec-title" id="sec-expect">{t('suAnExpectTitle')}</h2>
          <span class="err-inline" role="alert" data-expect-error>{t('dataError')}</span>
          <button type="button" class="btn btn-sm" onclick={lookNow} disabled={looking} data-expect-retry>{t('retry')}</button>
        {:else if expectScanning}
          <span class="spin" aria-hidden="true"></span>
          <h2 class="sec-title" id="sec-expect">{t('suAnExpectTitle')}</h2>
          <span class="muted" role="status" data-expect-scanning>{pf.progressText || t('suAnExpectScanning')}</span>
        {:else}
          <Icon name="check" size={15} strokeWidth={2} />
          <h2 class="sec-title" id="sec-expect">{t('suAnExpectTitle')}</h2>
          <span data-expect-empty>{t('suAnExpectNone')}<span class="sr-only"> {t('suAnExpectNoneHint')}</span></span>
        {/if}
      </div>
    {/if}
  </section>
{/snippet}

<div class="suan-wrap" data-suan>
  <h1 class="sr-only">{t('suAnTitle')}</h1>

  <div class="suan-scroll pagescroll">
    <!-- Her zaman en üstte: ürünün adı ve ne işe yaradığı (kapatılınca küçük "Söz nedir?" bağlantısı kalır).
         O8: seni bekleyen varken tanıtım yer kaplamaz (tek satır kalır), "Seni bekleyenler (N)" özeti iş kutusunun üstüne çıkar. -->
    <IntroCard kucuk={expectTotal > 0} />
    {#if expectTotal > 0}
      <div class="ozet-wrap">
        <button type="button" class="bekleyen-ozet" onclick={bekleyenlereGit} data-expect-summary>
          <Icon name="waiting" size={16} strokeWidth={2} /><span class="bo-t">{t('suAnExpectTitle')} ({expectTotal})</span><span class="bo-go">{t('expectSummaryGo')}</span>
        </button>
      </div>
    {/if}

    {#if linkLost}
      <div class="suan-body link-lost" role="alert" data-link-lost>
        <Icon name="alert" size={16} strokeWidth={2} /><span>{t('linkLost', { s: linkAge })}</span>
        <button type="button" class="btn btn-sm" onclick={lookNow} disabled={looking} data-link-retry>{t('retry')}</button>
      </div>
    {/if}

    <NewJobBar />

    <div class="suan-body" class:stale={linkLost}>
      {#if sonIs}
        {@const sb = bucketOf(sonIs)}
        <!-- O5: az önce verilen iş iş kutusunun hemen altında görünür: durumu canlıdır, "İzle" o işi açar. -->
        <div class="az-once" data-recent-given={sonIs.id}>
          <span class="ao-l">{t('recentGivenLabel')}</span>
          <span class="chip {bucketMeta[sb].cls}"><Icon name={bucketMeta[sb].icon} size={13} strokeWidth={2} />{t(chipLabelKey(sonIs))}</span>
          <span class="ao-t" title={sonIs.title}>{sonIs.title}</span>
          <button type="button" class="btn btn-sm" onclick={() => isiIzle(sonIs.id)} data-recent-given-open>{t('jobStartedWatch')}</button>
        </div>
      {/if}
      {#if sf.setup}
        <section class="setup-box" data-suan-setup>
          <SetupCard setup={sf.setup} />
        </section>
      {:else if sf.doneRaw !== null}
        <section class="setup-box" data-suan-done>
          <DonePanel raw={sf.doneRaw} />
        </section>
      {/if}

      <!-- Bekleyenler iş kutusunun ALTINDA, her zaman aynı yerde: kartlar sonradan gelince kutu yerinden oynamaz (K-06). -->
      {#if hitNote}<p class="hit-note" role="status" data-hit-note><Icon name="info" size={15} /><span><strong>{t('waitNoLongerWaiting')}</strong> {t('hitNoteLead')}</span></p>{/if}
      {@render expectSection()}
      <LostReplies />

      <!-- Yardımcıya henüz ulaşmamış mesajlar (telefondan ya da panelden bırakılan). Yoksa bölüm hiç görünmez. "Seni bekleyenler" sayısına girmez. -->
      {#if qp.items.length > 0}
        <section class="sec" aria-labelledby="sec-queue" data-suan-queue>
          <header class="sec-bar">
            <h2 class="sec-title" id="sec-queue">{t('kqTitle')}</h2>
            <span class="sec-n" data-queue-count>{qp.items.length}</span>
          </header>
          <p class="sec-lead" data-queue-lead>{t('kqLead')}</p>
          <ul class="lr-list" data-queue-list>
            {#each qp.items as q (q.id)}
              {@const tgt = queueTarget(q)}
              <li class="live-row idle" data-queue-item={q.id} data-queue-origin={q.origin}>
                <div class="lr-open static" title={q.text}>
                  <span class="dot" aria-hidden="true"></span>
                  <span class="lr-who"><span class="lr-proj" title={tgt.proj}>{tgt.proj}</span>{#if tgt.tool}<span class="tool-badge" data-tool-badge={tgt.tool}>{toolLabel(tgt.tool)}</span>{/if}</span>
                  <span class="lr-name">{queueHead(q.text)}</span>
                  <span class="lr-state">{q.origin === 'telefon' ? t('kqFromPhone') : t('kqFromPanel')}{#if whenShort(q.createdAt)}{' · ' + whenShort(q.createdAt)}{/if}</span>
                </div>
              </li>
            {/each}
          </ul>
        </section>
      {/if}

      <!-- Açık İşlerim. Düz liste: çalışanlar önde, boşta olanlar geri planda, satıra tıklamak ana eylem, "iş ver" küçük simge. -->
      <section class="sec" aria-labelledby="sec-live" data-suan-live>
        <header class="sec-bar">
          <h2 class="sec-title" id="sec-live">{t('suAnLiveTitle')}</h2>
          {#if liveCount > 0}<span class="sec-n" data-live-count>{liveCount}</span>{/if}
          {#if pf.scanning}
            <span class="small muted scan-note" role="status"><span class="spin" aria-hidden="true"></span>{t('pjScanning')}</span>
          {:else if seenText}
            <span class="small muted scan-note" data-live-seen>{seenText}</span>
          {/if}
          <button type="button" class="look-btn" onclick={lookNow} disabled={looking || pf.scanning} title={t('liveLookNowHint')} aria-label={t('liveLookNow')} data-live-refresh>
            {#if looking}<span class="spin" aria-hidden="true"></span>{:else}<Icon name="refresh" size={14} />{/if}{t('liveLookNow')}
          </button>
        </header>
        <p class="sec-lead" data-live-lead>{t('suAnLiveLead')}</p>

        {#if entries.length > 0}
          <ul class="lr-list" data-live-list>
            {#each shownEntries as e (e.key)}{@render entryView(e)}{/each}
            {#if foldedEntries.length > 0}
              <li class="fold-row" data-idle-fold>
                <button type="button" class="old-toggle" aria-expanded={idleOpen} onclick={() => (idleOpen = !idleOpen)} title={t('liveIdleFoldLead')} data-idle-toggle>
                  <span class="chev" class:open={idleOpen}><Icon name="chevron" size={14} /></span>{t('liveIdleFold', { n: idleTotal })}
                </button>
              </li>
              {#if idleOpen}
                {#each foldedEntries as e (e.key)}{@render entryView(e)}{/each}
              {/if}
            {/if}
          </ul>
        {/if}

        {#if noProjRows.length > 0}
          <div class="noproj" data-noproject>
            {#if noProjFold}
              <button type="button" class="old-toggle noproj-h" aria-expanded={noProjOpen} onclick={() => (noProjOpen = !noProjOpen)} data-noproject-toggle>
                <span class="chev" class:open={noProjOpen}><Icon name="chevron" size={14} /></span>{t('pjLiveNoProject')} ({noProjRows.length})
              </button>
            {:else}
              <h3 class="noproj-h" data-noproject-title>{t('pjLiveNoProject')} <span class="sec-n">{noProjRows.length}</span></h3>
            {/if}
            <p class="small muted" data-noproject-why>{noProjWhy}</p>
            {#if !noProjFold || noProjOpen}
              <ul class="lr-list" data-noproject-list>
                {#each noProjRows as r (r.s.tool + ':' + r.s.id)}{@render sessRow(r, false)}{/each}
              </ul>
            {/if}
          </div>
        {/if}

        {#if !hasList}
          {#if outsideCount > 0}
            <p class="calm-line muted" data-live-allwait>{t('liveAllWaiting')}</p>
          {:else if (!pf.loaded || pf.scanning) && !pf.error}
            <p class="calm-line muted" role="status" data-live-scanning><span class="spin" aria-hidden="true"></span><span>{t('suAnLiveScanning')}</span></p>
            <ul class="lr-list lr-skel" aria-hidden="true" data-live-skeleton>
              <li class="live-row"><span class="skl skl-a"></span></li>
              <li class="live-row"><span class="skl skl-b"></span></li>
              <li class="live-row"><span class="skl skl-c"></span></li>
            </ul>
          {:else if pf.error}
            <div class="calm-line err-box" role="alert" data-live-error>
              <Icon name="alert" size={16} />
              <span class="err-txt">{t('liveError')}</span>
              <button type="button" class="btn btn-sm" onclick={retryLive} data-live-retry>{t('retry')}</button>
            </div>
          {:else}
            <p class="calm-line muted" data-live-empty>{t('suAnLiveEmpty')}</p>
          {/if}
        {/if}

        {#if lookMsg}<p class="small muted look-msg" role="status" data-look-msg>{lookMsg}</p>{/if}
        {#if actionErr}<p class="small err-line" role="alert" data-action-error>{actionErr}</p>{/if}
        {#if liveNote || warnings.length > 0}
          <p class="small muted live-note" data-live-soft>{liveNoteText} <button type="button" class="linkbtn" onclick={lookNow} disabled={looking} data-live-soft-retry>{t('retry')}</button></p>
        {/if}

        {#if split.old.length > 0}
          <div class="old-wrap" data-live-old>
            <button type="button" class="old-toggle" aria-expanded={oldOpen} aria-controls="live-old-list" onclick={() => (oldOpen = !oldOpen)} data-old-toggle>
              <span class="chev" class:open={oldOpen}><Icon name="chevron" size={14} /></span>{t('liveOldToggle', { n: split.old.length })}
            </button>
            {#if oldOpen}
              <p class="small muted old-lead">{t('liveOldLead')}</p>
              <ul class="lr-list old" id="live-old-list">
                {#each split.old as r (r.s.tool + ':' + r.s.id)}
                  {@const nm = rowName(r)}
                  <li class="live-row idle" data-old-session={r.s.id} data-live-tool={r.s.tool}>
                    {#if r.path || r.s.projectPath || r.s.cwd}
                      <button type="button" class="lr-open" onclick={() => openOldFolder(r)} aria-label={t('liveOpenFolderAria', { p: r.projectName, n: nm })}>
                        {@render rowBody({ state: 'stopped', proj: r.path === null ? '' : r.projectName, tool: r.s.tool, name: nm, stateText: whenShort(r.s.lastActivity) })}
                      </button>
                    {:else}
                      <div class="lr-open static">
                        {@render rowBody({ state: 'stopped', proj: r.path === null ? '' : r.projectName, tool: r.s.tool, name: nm, stateText: whenShort(r.s.lastActivity) })}
                      </div>
                    {/if}
                  </li>
                {/each}
              </ul>
            {/if}
          </div>
        {/if}

      </section>

      <!-- Hangi projede ne bitti (son biten işler) -->
      {#if recentFinished.length === 0 && sf.jobsFailed}
        <section class="sec" aria-labelledby="sec-recent" data-suan-recent>
          <header class="sec-bar"><h2 class="sec-title" id="sec-recent">{t('suAnRecentTitle')}</h2></header>
          <div class="calm-line err-box" role="alert" data-recent-error>
            <Icon name="alert" size={16} /><span class="err-txt">{t('dataError')}</span>
            <button type="button" class="btn btn-sm" onclick={() => refreshSurface()} data-recent-retry>{t('retry')}</button>
          </div>
        </section>
      {:else if recentFinished.length > 0}
        <section class="sec" aria-labelledby="sec-recent" data-suan-recent>
          <header class="sec-bar">
            <h2 class="sec-title" id="sec-recent">{t('suAnRecentTitle')}</h2>
            <a class="small muted see-all" href="#/is">{t('suAnSeeAllJobs')}</a>
          </header>
          <p class="sec-lead" data-recent-lead>{t('suAnRecentLead')}</p>
          <ul class="recent-list">
            {#each recentFinished as j (j.id)}
              {@const b = bucketOf(j)}
              <li class="recent-row" data-recent-job={j.id}>
                <button type="button" class="recent-btn" onclick={() => { select(j.id, { detail: true }); go('is', String(j.id)) }}>
                  <span class="chip {bucketMeta[b].cls}"><Icon name={bucketMeta[b].icon} size={13} strokeWidth={2} />{t(chipLabelKey(j))}</span>
                  <span class="rc-title">{j.title}</span>
                  {#if j.projectName}<span class="rc-proj">{j.projectName}</span>{/if}
                  <time class="rc-time" datetime={j.finishedAt ?? j.createdAt}>{ago(j.finishedAt ?? j.createdAt)}</time>
                </button>
              </li>
            {/each}
          </ul>
        </section>
      {/if}
    </div>
  </div>
</div>

<dialog class="sheet" bind:this={dlg} aria-labelledby="suan-afd-t" onclose={() => { sf.addFolderOpen = false; cancelPendingStart() }} onclick={(e) => { if (e.target === dlg) dlg?.close() }}>
  <div class="afd">
    <h2 class="h2" id="suan-afd-t">{t('addFolderTitle')}</h2>
    {#if sf.addFolderOpen}<AddFolder autofocus />{/if}
    <p class="afd-x"><button type="button" class="btn btn-quiet btn-sm" onclick={() => dlg?.close()} data-afd-cancel>{t('cancel')}</button></p>
  </div>
</dialog>

<style>
  .suan-wrap { flex: 1; min-height: 0; display: flex; flex-direction: column; }
  .suan-scroll { flex: 1; min-height: 0; overflow-y: auto; }
  .ozet-wrap { padding: 8px var(--gutter) 0; }
  .bekleyen-ozet { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; width: 100%; min-height: 36px; padding: 6px 12px; border: 1px solid var(--waiting); border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); font: inherit; font-size: var(--fs-sm); text-align: left; cursor: pointer; }
  .bekleyen-ozet:hover { background: var(--hover); }
  .bekleyen-ozet :global(svg) { flex: none; color: var(--waiting); }
  .bo-t { font-weight: var(--fw-strong); }
  .bo-go { margin-left: auto; font-size: var(--fs-xs); color: var(--ink-2); text-decoration: underline; text-underline-offset: 2px; }
  .az-once { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; padding: 8px 12px; border: 1px solid var(--line); border-radius: var(--radius); background: var(--surface); font-size: var(--fs-sm); }
  .ao-l { font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-2); }
  .ao-t { flex: 1 1 4rem; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: var(--fw-medium); }
  .suan-body { width: 100%; max-width: 54rem; margin-inline: auto; padding: 14px var(--gutter) 36px; display: flex; flex-direction: column; gap: 22px; }
  .hit-note { display: flex; align-items: flex-start; gap: 8px; margin: 0; padding: 8px 12px; border-radius: var(--radius); background: var(--bg-sunken); color: var(--ink-2); font-size: var(--fs-sm); line-height: 1.45; }
  .hit-note :global(svg) { flex: none; margin-top: 3px; color: var(--ink-3); }
  .hit-note strong { color: var(--ink); font-weight: var(--fw-strong); }
  .setup-box { padding: 14px 16px; border: 1px solid var(--line-strong); border-radius: var(--radius-lg); background: var(--surface); }

  .sec { display: flex; flex-direction: column; gap: 8px; }
  .sec-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; min-height: 28px; }
  .sec-title { font-family: var(--display); font-size: var(--fs-sm); font-weight: var(--fw-strong); color: var(--ink-2); letter-spacing: 0.01em; }
  .badge-count { display: inline-flex; align-items: center; justify-content: center; min-width: 22px; height: 22px; padding: 0 7px; border-radius: var(--chip-r, 999px); background: var(--waiting-tint); color: var(--waiting); font-size: var(--fs-xs); font-weight: var(--fw-strong); font-variant-numeric: tabular-nums; }
  .scan-note { display: inline-flex; align-items: center; gap: 6px; margin-left: auto; }
  .look-btn { margin-left: auto; display: inline-flex; align-items: center; gap: 5px; min-height: 28px; padding: 0 8px; border: 0; border-radius: var(--radius); background: none; color: var(--ink-2); font: inherit; font-size: var(--fs-xs); cursor: pointer; }
  .scan-note + .look-btn { margin-left: 0; }
  .look-btn:hover:not([disabled]) { background: var(--bg-sunken); color: var(--ink); }
  .look-btn[disabled] { opacity: 0.6; cursor: default; }
  .see-all { margin-left: auto; display: inline-flex; align-items: center; min-height: 24px; }

  /* Boşken Benden Beklenen tek ince satırdır (başlık + açıklama + kart üç kat yer tutuyordu). */
  .sec.expect.thin { margin-bottom: -10px; }
  .thin-line { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; min-height: 28px; font-size: var(--fs-sm); color: var(--ink-2); }
  .thin-line :global(svg) { flex: none; color: var(--ok); }
  .thin-line .sec-title { margin: 0; }

  .grp-head { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; padding: 8px 14px 6px 18px; border-top: 1px solid var(--line-strong); background: var(--bg-sunken); list-style: none; }
  .grp-t { font-family: var(--display); font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-2); letter-spacing: 0.01em; }
  .grp-l { flex: 1 1 100%; font-size: var(--fs-xs); color: var(--ink-2); line-height: 1.4; }

  .calm-line { display: flex; align-items: center; gap: 8px; padding: 10px 12px; border: 1px solid var(--line); border-radius: var(--radius); background: var(--surface); color: var(--ink-2); font-size: var(--fs-sm); }
  .calm-line :global(svg) { flex: none; color: var(--ok); }

  .sec-lead { margin: -4px 0 2px; font-size: var(--fs-xs); line-height: 1.45; color: var(--ink-2); max-width: 64ch; }
  .sec-n { display: inline-flex; align-items: center; justify-content: center; min-width: 20px; height: 20px; padding: 0 6px; border-radius: var(--chip-r, 999px); background: var(--bg-sunken); color: var(--ink-2); font-size: var(--fs-xs); font-weight: var(--fw-strong); font-variant-numeric: tabular-nums; }
  .err-box { color: var(--ink); }
  .err-inline { font-weight: var(--fw-medium); color: var(--ink); }
  .thin-line.failed :global(svg) { color: var(--waiting); }
  .linkbtn { display: inline-flex; align-items: center; min-height: 28px; padding: 0 6px; border: 0; background: none; color: var(--ink); font: inherit; text-decoration: underline; text-underline-offset: 2px; cursor: pointer; }
  .linkbtn:hover:not([disabled]) { text-decoration-thickness: 2px; }
  :global([data-hit='1']) { background: var(--sel-bg, var(--select)) !important; outline: 2px solid var(--ink); outline-offset: -2px; transition: background-color var(--dur-1) var(--ease); }
  .err-box :global(svg) { color: var(--waiting); }
  .err-txt { flex: 1 1 10rem; min-width: 0; }

  /* Açık İşlerim: düz liste. Geniş: nokta | proje + yardımcı | ad | durum. Dar: ikinci satırda ad.
     Çalışan satır öne çıkar (sol çizgi, tonlu etiket); boşta olan geri planda kalır (soluk ad ve etiket). */
  .lr-list { display: flex; flex-direction: column; margin: 0; padding: 0; list-style: none; border: 1px solid var(--line); border-radius: var(--r-card, var(--radius-lg)); background: var(--surface); overflow: hidden; }
  .lr-list.old { background: var(--bg); }
  .live-row { display: flex; flex-wrap: wrap; align-items: stretch; min-width: 0; border-top: 1px solid var(--line); }
  .lr-list > .live-row:first-child { border-top: 0; }
  .live-row:hover { background: var(--hover); }
  .live-row.child .lr-open { padding-left: 26px; }
  .lr-open {
    flex: 1 1 0; min-width: 0; min-height: var(--row-s, 40px);
    display: grid; grid-template-columns: 8px minmax(8rem, 16rem) minmax(0, 1fr) auto; grid-template-areas: "dot who name state";
    align-items: center; column-gap: 10px; padding: 6px 4px 6px 12px;
    border: 0; background: none; color: inherit; font: inherit; text-align: left; cursor: pointer;
  }
  .lr-open.static { cursor: default; }
  .lr-open > .dot { grid-area: dot; }
  .lr-who { grid-area: who; min-width: 0; display: flex; align-items: center; gap: 8px; }
  .lr-proj { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--fs-sm); font-weight: var(--fw-strong); color: var(--ink); }
  .lr-name { grid-area: name; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--fs-sm); color: var(--ink-2); }
  .lr-state { grid-area: state; font-size: var(--fs-xs); color: var(--ink-2); white-space: nowrap; font-variant-numeric: tabular-nums; }
  .lr-state.typing { padding: 1px 8px; border-radius: var(--chip-r, 999px); background: var(--working-tint); color: var(--working); font-weight: var(--fw-strong); }
  .live-row.working { box-shadow: inset 3px 0 0 var(--working); }
  .live-row.working .lr-name { color: var(--ink); }
  .live-row.idle .lr-proj { color: var(--ink-2); font-weight: var(--fw-medium); }
  .live-row.idle .lr-name, .live-row.idle .lr-state { color: var(--ink-3); }
  .live-row.idle .tool-badge { background: transparent; color: var(--ink-3); }
  .lr-give { flex: none; align-self: center; display: inline-flex; align-items: center; justify-content: center; width: 32px; height: 32px; margin-right: 6px; border: 0; border-radius: var(--radius); background: none; color: var(--ink-3); cursor: pointer; }
  .lr-give:hover { background: var(--bg-sunken); color: var(--ink); }
  .live-row.sub { background: var(--bg); }
  .live-row.sub .lr-open { padding-left: 26px; }
  .live-row.swarm .lr-name { color: var(--ink); }
  .live-row.swarm.idle .lr-name { color: var(--ink-3); }
  .fold-row { display: flex; border-top: 1px solid var(--line); background: var(--bg); }
  .fold-row .old-toggle { flex: 1; min-height: 38px; padding: 0 12px; }
  .lr-skel { background: var(--bg); }
  .lr-skel .live-row { min-height: var(--row-s, 40px); align-items: center; padding: 0 12px; }
  .skl { display: block; height: 10px; border-radius: 5px; background: var(--bg-sunken); }
  .skl-a { width: 62%; } .skl-b { width: 48%; } .skl-c { width: 55%; }

  .noproj { display: flex; flex-direction: column; gap: 6px; margin-top: 4px; }
  .noproj-h { margin: 0; display: flex; align-items: center; gap: 6px; font-family: var(--display); font-size: var(--fs-sm); font-weight: var(--fw-strong); color: var(--ink-2); }

  .dot { flex: none; width: 8px; height: 8px; border-radius: 50%; border: 1.5px solid var(--line-strong); }
  .dot.warm { border-color: var(--waiting); background: var(--waiting); }
  .dot.typing { border-color: var(--working); background: var(--working); animation: suan-pulse 1.6s var(--ease) infinite; }
  @keyframes suan-pulse { 0%, 100% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--working) 45%, transparent); } 50% { box-shadow: 0 0 0 4px transparent; } }

  .live-note { margin: 0; padding: 0 2px; }
  .old-wrap { display: flex; flex-direction: column; gap: 6px; }
  .old-toggle { align-self: flex-start; display: inline-flex; align-items: center; gap: 6px; min-height: 28px; padding: 0 6px 0 2px; border: 0; background: none; color: var(--ink-2); font: inherit; font-size: var(--fs-xs); cursor: pointer; }
  .old-toggle:hover { color: var(--ink); }
  .chev { display: inline-flex; transition: transform var(--dur-1) var(--ease); }
  .chev.open { transform: rotate(90deg); }
  .old-lead { margin: 0; }

  .warn-list { display: flex; flex-direction: column; gap: 4px; margin-top: 4px; }
  .warn-line { display: flex; align-items: flex-start; gap: 6px; padding: 8px 10px; border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); }
  .warn-line :global(svg) { flex: none; margin-top: 2px; color: var(--waiting); }

  /* Son Bitenler */
  .recent-list { display: flex; flex-direction: column; border: 1px solid var(--line); border-radius: var(--r-card, var(--radius-lg)); background: var(--surface); overflow: hidden; }
  .recent-row + .recent-row { border-top: 1px solid var(--line); }
  .recent-btn { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; width: 100%; min-height: var(--row-s); padding: 6px 12px; border: 0; background: none; text-align: left; color: var(--ink); }
  .recent-btn:hover { background: var(--hover); }
  .rc-title { flex: 1 1 10rem; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: var(--fs-sm); font-weight: var(--fw-medium); }
  .rc-proj { font-size: var(--fs-xs); color: var(--ink-2); }
  .rc-time { font-size: var(--fs-xs); color: var(--ink-3); font-variant-numeric: tabular-nums; margin-left: auto; }

  .afd { padding: 18px 20px 20px; display: flex; flex-direction: column; gap: 12px; }
  .afd-x { margin: 0; display: flex; justify-content: flex-end; }
  .look-msg { margin: 0; padding: 0 2px; }
  .link-lost { flex-direction: row; flex-wrap: wrap; align-items: center; gap: 6px 10px; margin-block: 10px 0; padding-block: 10px; border: 1px solid var(--waiting); border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); font-size: var(--fs-sm); }
  .link-lost :global(svg) { flex: none; color: var(--waiting); }
  .link-lost span { flex: 1 1 14rem; min-width: 0; }
  .stale { opacity: 0.55; transition: opacity var(--dur-2) var(--ease); }

  @media (max-width: 540px) {
    .suan-body { padding: 10px 10px 28px; gap: 18px; }
    .lr-open { grid-template-columns: 8px minmax(0, 1fr) auto; grid-template-areas: "dot who state" ". name name"; row-gap: 1px; }
  }
  @media (prefers-reduced-motion: reduce) {
    .dot.typing { animation: none; }
    .chev { transition: none; }
  }
</style>
