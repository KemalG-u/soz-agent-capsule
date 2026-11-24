<script lang="ts">
  import { tick, untrack } from 'svelte'
  import { t } from '../i18n/index.svelte'
  import { paneIn, rowIn } from '../motion'
  import { isNarrow } from '../keys'
  import { go } from '../router.svelte'
  import { app, toast } from '../store.svelte'
  import { kendiHata, sadeMi } from '../hata'
  import { ayarIlgili, sozKapandiMi, SOZ_KAPANDI_SATIRI_RE } from '../yarida'
  import HataSatiri from './HataSatiri.svelte'
  import { olumsuzSonuc } from '../gezinme'
  import { listeyeDon, vazgecTaslak, midRunFailed, answerOffer, answerOfferEdit, answerSuggested, bucketMeta, bucketOf, chipLabelKey, continueFinished, askStop, decide, decideChange, friendlyLine, guardTag, isFinished, isStopped, loadChecks, loadNightNote, offerEditSeen, offerEditViewable, pendingList, rejectAndSendBack, retryStart, selectedJob, sendReply, sf, stopCurrent, viewOfferEdit, visibleOffer, visibleSuggestions } from '../surface.svelte'
  import type { TimelineItem } from '../types'
  import { ago, copyText, inShell, parseTs, pasteToTerminal } from '../util'
  import ChecksPanel from './ChecksPanel.svelte'
  import ExplainBox from './ExplainBox.svelte'
  import HumanChecklist from './HumanChecklist.svelte'
  import ProtectionBar from './ProtectionBar.svelte'
  import Icon from './Icon.svelte'
  import ProofCard from './ProofCard.svelte'

  const job = $derived(selectedJob())
  const bucket = $derived(job ? bucketOf(job) : null)
  const canStop = $derived(!!job && (job.run?.status === 'running' || job.run?.status === 'waiting_approval' || (job.awaiting === 'approval' && !isFinished(job))))
  const needsReply = $derived(job?.awaiting === 'reply')
  const finished = $derived(!!job && isFinished(job))
  // Söz kapandığı için duran iş: kişi durdurmadı. Açıklama kartta söylenir, akışta aynı cümle tekrarlanmaz.
  const shutdownStop = $derived(!!job && isStopped(job) && sozKapandiMi(job.run?.lastLine))
  // Ajanın yazdığı deneme önerileri: iş bitince tek satırlık kart, [Evet] [Hayır].
  const suggestions = $derived(job ? visibleSuggestions(job) : [])
  $effect(() => {
    if (job && (job.suggestedChecks?.length ?? 0) > 0) void loadChecks(job.projectId)
  })
  // Gece işi dürüstlüğü: sunucunun cümlesi iş kartında bir kez görünür.
  $effect(() => {
    if (job?.night) void loadNightNote()
  })
  const offer = $derived(job ? visibleOffer(job) : null)
  let offerBusy = $state(false)
  async function answerO(yes: boolean) {
    if (!job || offerBusy) return
    offerBusy = true
    try { await answerOffer(job, yes) } finally { offerBusy = false }
  }
  // Değiştirilen onaylı testler: önce fark görülür, kabul ancak sonra mümkün. Varsayılan cevap Hayır.
  const editSeen = $derived(!!job && offerEditSeen(job))
  const editViewable = $derived(!!job && offerEditViewable(job))
  const editDiff = $derived(job ? (sf.offerDiff[job.id]?.files ?? []) : [])
  async function viewEdit() {
    if (!job || offerBusy) return
    offerBusy = true
    try { await viewOfferEdit(job) } finally { offerBusy = false }
  }
  async function answerEdit(yes: boolean) {
    if (!job || offerBusy) return
    offerBusy = true
    try { await answerOfferEdit(job, yes) } finally { offerBusy = false }
  }
  let sugBusy = $state(false)
  async function answer(s: (typeof suggestions)[number], yes: boolean) {
    if (!job || sugBusy) return
    sugBusy = true
    try { await answerSuggested(job, s, yes) } finally { sugBusy = false }
  }
  const shell = inShell()
  // Karar kartındaki "Geri al": başlıktaki koruma çubuğunun önizlemesini açar.
  let undoReq = $state(0)
  // "Reddet, ajana geri gönder": devam kutusuna kişinin düzenleyebileceği bir taslak konur.
  function sendBackDraft(p: NonNullable<TimelineItem['proof']>): string {
    if (p.reasonCode === 'agent_tests_fail') return t('sendBackDraftAgent')
    const names = (p.diffs ?? []).find((d) => d.kind === 'approved-fail')?.lines.map((l) => l.text).filter(Boolean) ?? []
    return names.length > 0 ? t('sendBackDraftNamed', { l: names.join(', ') }) : t('sendBackDraft')
  }

  // Akış zaman damgasına göre sıralanır: kanıt kartı, kendisinden önce gelen ilerleme satırlarının altında kalır.
  const rank = (x: TimelineItem) => (x.kind === 'user' ? -1 : x.kind === 'proof' ? 1 : 0)
  function ordered(list: TimelineItem[]): TimelineItem[] {
    return list
      .map((x, i) => ({ x, i, ts: parseTs(x.ts) }))
      .sort((a, b) => {
        const an = Number.isNaN(a.ts), bn = Number.isNaN(b.ts)
        if (an || bn) return a.i - b.i
        return a.ts - b.ts || rank(a.x) - rank(b.x) || a.i - b.i
      })
      .map((o) => o.x)
  }
  const items = $derived(ordered(job ? (sf.timelines[job.id] ?? []) : []))
  const lastProofId = $derived([...items].reverse().find((x) => x.kind === 'proof')?.id ?? null)
  const lastAgentId = $derived([...items].reverse().find((x) => x.kind === 'agent')?.id ?? null)
  const hasAgentLine = $derived(items.some((x) => x.kind === 'agent'))
  // "Yarıda kaldı" kartı: ham hata yalnız Teknik ayrıntı altında. Önce akıştaki son hata satırının ayrıntısı, yoksa sade olmayan başlatma hatası.
  const halfDetail = $derived.by(() => {
    const e = [...items].reverse().find((x) => x.kind === 'system' && x.state === 'error' && x.detail)
    return e?.detail ?? (job?.startError && !sadeMi(job.startError) ? job.startError : null)
  })
  const pend = $derived(job ? pendingList(job) : [])
  const answered = $derived(job ? (sf.answered[job.id] ?? 0) : 0)
  // Kabul edilmiş işin son kartında kabulün yolu yazılır; panelden gelmediyse sarı uyarı (denetim 4, F).
  const acceptInfo = $derived.by(() => {
    if (!job || !job.acceptedByUser) return null
    const via = job.acceptedVia ?? null
    if (!via) return null
    return { via, process: job.acceptedProcess ?? null, panel: via === 'arayüz' || via === 'kabuk penceresi' || via === 'tarayıcı' }
  })
  // Başlatılamadı kartı aynı sade cümleyi başlıkta söyler: akıştaki aynı cümleli hata satırı tekrar çizilmez (ham ayrıntısı kartta "Teknik ayrıntı" altında kalır).
  const blockedDup = $derived(!!job && bucket === 'blocked' && !midRunFailed(job, hasAgentLine) && !!job.startError && sadeMi(job.startError))
  const decisionProof = $derived(job?.awaiting === 'decision' && lastProofId !== null ? lastProofId : null)

  // Cevap alanı: yalnız anlamlı olduğu durumda var. Biten işte ölü kutu yerine "Bu işe devam et" düğmesi.
  const replyMode = $derived.by<'form' | 'continue' | 'button' | 'none'>(() => {
    if (!job || job.id < 0) return 'none'
    if (finished) return sf.continuing[job.id] ? 'continue' : 'button'
    if (job.awaiting === 'approval' || job.awaiting === 'decision' || bucket === 'blocked' || bucket === 'queued' || job.state === 'taslak') return 'none'
    return 'form'
  })

  let scroller: HTMLElement | undefined = $state()
  let replyEl: HTMLTextAreaElement | undefined = $state()
  let replyText = $state('')
  // Cevap taslağı işe bağlı saklanır (C-17): sayfa yenilenince ya da başka işe geçip dönünce yazılan kaybolmaz. Gönderilince silinir.
  const replyKey = (id: number) => `soz.reply.${id}`
  const loadReply = (id: number): string => { try { return localStorage.getItem(replyKey(id)) ?? '' } catch { return '' } }
  const storeReply = (id: number, v: string) => { try { if (v.trim()) localStorage.setItem(replyKey(id), v); else localStorage.removeItem(replyKey(id)) } catch { /* depolama kapalı: taslak yalnız bu oturumda yaşar */ } }
  let replyFor: number | null = null
  $effect(() => {
    const id = job && job.id > 0 ? job.id : null
    untrack(() => {
      if (id === replyFor) return
      replyFor = id
      replyText = id !== null ? loadReply(id) : ''
      void tick().then(autosize)
    })
  })
  $effect(() => {
    const v = replyText
    const id = replyFor
    if (id !== null) untrack(() => storeReply(id, v))
  })
  let open = $state<Record<string, boolean>>({})

  // Altta olan kullanıcı en sonda kalır: yeni satır ve büyüyen kart (kanıt çıktısı, görüntü) onu aşağı iter. İş değişince en sona kay.
  // "Altta mı" bilgisi içerik büyümeden ÖNCE, kaydırma olaylarında tutulur.
  let stuck = true
  let lastJob = -1
  function onScroll() {
    const el = scroller
    if (el) stuck = el.scrollHeight - el.scrollTop - el.clientHeight < 160
  }
  $effect(() => {
    const id = job?.id ?? -1
    void items.length
    const el = scroller
    if (!el) return
    if (id !== lastJob) stuck = true
    lastJob = id
    if (stuck) queueMicrotask(() => followEnd(el))
  })
  // Sona kay. Son kanıt kartı görünür alana sığıyorsa (ya da karar bekliyorsa) sona değil kartın başına kay: başlığı üstte kesilmesin, eylemler altta kalsın.
  function followEnd(el: HTMLElement) {
    const max = el.scrollHeight - el.clientHeight
    const cards = el.querySelectorAll<HTMLElement>('article.proof')
    const last = cards[cards.length - 1]
    if (!last || max <= 0) { el.scrollTo({ top: el.scrollHeight }); return }
    const top = last.getBoundingClientRect().top - el.getBoundingClientRect().top + el.scrollTop
    const wantHead = last.classList.contains('decision') || (last.getBoundingClientRect().height <= el.clientHeight && !last.closest('li')?.nextElementSibling)
    el.scrollTo({ top: wantHead ? Math.min(max, Math.max(0, top - 8)) : max })
  }
  $effect(() => {
    const el = scroller
    const list = el?.querySelector('ol.tl')
    if (!el || !list) return
    const ro = new ResizeObserver(() => {
      if (stuck) followEnd(el)
    })
    ro.observe(list)
    return () => ro.disconnect()
  })

  $effect(() => {
    const id = job?.id
    const p = id !== undefined ? sf.prefill[id] : undefined
    if (id === undefined || !p || replyEl === undefined) return
    untrack(() => {
      if (!replyText.trim()) replyText = p
      delete sf.prefill[id]
      void tick().then(autosize)
    })
  })

  // "r" ve liste açılışı: cevap kutusuna odak. Yalnız sayaç artınca çalışır (iş değişince odak çalınmaz).
  let seenFocus = sf.focusReply
  $effect(() => {
    const n = sf.focusReply
    if (n === seenFocus) return
    seenFocus = n
    untrack(() => void focusReply())
  })
  async function focusReply() {
    if (!job) return
    if (finished && !sf.continuing[job.id]) sf.continuing[job.id] = true
    await tick()
    replyEl?.focus()
  }
  // Cevap bekleyen işin ayrıntısı açılınca kutu hazır olsun (odak yalnız iş yeni seçilmişse).
  async function openContinue() {
    if (!job) return
    sf.continuing[job.id] = true
    await tick()
    replyEl?.focus()
  }

  async function send() {
    if (!job) return
    if (!replyText.trim()) {
      replyEl?.focus()
      return
    }
    const txt = replyText
    replyText = ''
    autosize()
    const ok = finished ? await continueFinished(job.id, txt) : await sendReply(job.id, txt)
    if (!ok) replyText = txt
    else delete sf.rejectBack[job.id]
  }

  function onReplyKey(e: KeyboardEvent) {
    if (e.isComposing) return
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      void send()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      e.stopPropagation()
      if (job && finished) sf.continuing[job.id] = false
      if (job) delete sf.rejectBack[job.id]
      replyEl?.blur()
      if (isNarrow()) listeyeDon()
      sf.focusList++
    }
  }

  function autosize() {
    const el = replyEl
    if (!el) return
    el.style.height = 'auto'
    el.style.height = Math.min(el.scrollHeight, 120) + 'px'
  }

  async function copyPrompt() {
    if (!job) return
    toast((await copyText(job.promptText)) ? t('copied') : t('copyFailed'))
  }

  // A3: kısa pencerede (700 px altı yükseklik, %150 ve %200 yakınlaştırma dahil) başlığın yalnız iş adı ve durum çipi sabit kalır,
  // geri alma, bilgi satırı ve notlar kayan alanın en üstüne iner. Uzun pencerede başlık eskisi gibidir.
  const KISA = '(max-height: 699px)'
  let kisa = $state(typeof matchMedia !== 'undefined' && matchMedia(KISA).matches)
  $effect(() => {
    if (typeof matchMedia === 'undefined') return
    const mq = matchMedia(KISA)
    const f = () => (kisa = mq.matches)
    f()
    mq.addEventListener('change', f)
    return () => mq.removeEventListener('change', f)
  })

  // O6, D10: geri alınacak değişiklik yokken ve iş sürerken "Geri al" görünmez.
  const degisiklikYok = $derived(bucket === 'nowork' || bucket === 'nochange')
  const calisiyor = $derived(bucket === 'running' || bucket === 'queued' || canStop)
  // O6: olumsuz sonuçta sıradaki adım adlandırılır: "Yardımcıya yeniden yaptır" cevap kutusunu hazır cümleyle doldurur, kişi gönderir.
  // "İş yapılmamış" sonucu işi kapatmaz (durum "verildi" kalır), bu yüzden "bitti mi" değil "yardımcı şu an çalışmıyor mu" bakılır.
  const yenidenYaptir = $derived(!!job && job.id > 0 && !canStop && !isStopped(job) && job.awaiting !== 'decision' && olumsuzSonuc(bucket) && !sf.continuing[job.id])
  async function yenidenYaptirAc() {
    if (!job) return
    sf.prefill[job.id] = bucket === 'nowork' ? t('redoDraftNoWork') : t('redoDraftFailed')
    sf.continuing[job.id] = true
    await tick()
    replyEl?.focus()
  }
  // A4: başlamamış (taslak) iş: birincil "Başlat", ikincil "Vazgeç" (sunucuda silme yok, iş iptal edilir).
  const taslak = $derived(!!job && job.id > 0 && job.state === 'taslak' && !sf.startMissing[job.id])

  const toSettings = () => go('ayarlar', 'claude')
  const toParallel = () => go('ayarlar', 'paralel')
  const placeholder = $derived(replyMode === 'continue' ? t('continuePlaceholder') : needsReply ? t('replyAnswer') : t('replyPlaceholder'))
</script>

{#if job}
  {@const j = job}
  {#key j.id}
    <div class="pane" in:paneIn data-stream={j.id} data-kind={bucket}>
      {#snippet bilgi()}
        <span class="small muted">{j.projectName}</span>
        {#if guardTag(j)}<span class="small muted guardtag" title={t('guardTagHelp')} data-guard-tag><Icon name="shield" size={14} />{guardTag(j)}</span>{/if}
        {#if (j.attempts ?? 0) > 1}<span class="small muted">{t('attemptN', { n: j.attempts ?? 0 })}</span>{/if}
        {#if j.night}<span class="small muted" data-night-chip><Icon name="moon" size={14} />{t('nightChip')}</span>{/if}
        <time class="small muted" datetime={j.createdAt}>{ago(j.createdAt)}</time>
      {/snippet}
      {#snippet ek()}
        {#if j.night && sf.nightNote}<p class="small muted nightsays" data-night-note><Icon name="moon" size={14} />{sf.nightNote}</p>{/if}
        {#if j.state !== 'taslak' && j.state !== 'baslatilamadi' && j.id > 0 && !(j.state === 'iptal' && !j.givenAt)}<ProtectionBar jobId={j.id} autoPreview={undoReq} {degisiklikYok} {calisiyor} />{/if}
        {#if j.baseNote}
          <p class="basenote small" data-base-note><Icon name="info" size={14} />{j.baseNote || t('baseNoteDefault')}</p>
        {/if}
        {#if sf.startMissing[j.id] && j.state === 'taslak'}
          <div class="fallback">
            <p class="small muted">{t('fallbackHint')}</p>
            <div class="fb-act">
              <button class="btn btn-sm" onclick={copyPrompt}><Icon name="copy" size={14} />{t('copyPrompt')}</button>
              {#if shell}<button class="btn btn-sm" onclick={() => { pasteToTerminal(j.promptText); toast(t('pasteAsked')) }}><Icon name="terminal" size={14} />{t('pasteTerminal')}</button>{/if}
              <button class="btn btn-sm" onclick={() => retryStart(j.id)}><Icon name="refresh" size={14} />{t('retryStart')}</button>
            </div>
          </div>
        {/if}
      {/snippet}
      <header class="head" class:kisa data-head>
        <button class="back btn btn-quiet" onclick={listeyeDon} aria-label={t('backToList')}><Icon name="back" size={18} />{t('backToList')}</button>
        <h2 class="title" data-job-title title={j.rawText}>{j.title}</h2>
        <div class="meta">
          {#if bucket}<span class="chip {bucketMeta[bucket].cls}" data-chip={bucket}><Icon name={bucketMeta[bucket].icon} size={14} />{t(chipLabelKey(j))}</span>{/if}
          {#if !kisa}{@render bilgi()}{/if}
          {#if canStop}
            <button class="btn btn-sm stop" onclick={() => askStop(j.id)} disabled={!!sf.busy['stop:' + j.id]} data-stop><Icon name="stop" size={14} />{t('streamStop')} <kbd>s</kbd></button>
          {/if}
        </div>
        {#if sf.stopAsk === j.id && canStop}
          <div class="stopask" role="alertdialog" aria-label={t('stopAsk')} data-stop-ask>
            <p class="small">{t('stopAsk')}</p>
            <button class="btn btn-sm btn-primary" onclick={() => stopCurrent(j.id)} disabled={!!sf.busy['stop:' + j.id]} data-stop-yes>{t('stopAskYes')}</button>
            <button class="btn btn-sm" onclick={() => (sf.stopAsk = null)} data-stop-no>{t('stopAskNo')}</button>
          </div>
        {/if}
        {#if !kisa}{@render ek()}{/if}
      </header>

      <div class="scroll" bind:this={scroller} onscroll={onScroll} tabindex="-1" id="stream-scroll" data-scroll>
        {#if kisa}
          <div class="head-ic" data-head-in-scroll>
            <div class="meta">{@render bilgi()}</div>
            {@render ek()}
          </div>
        {/if}
        <ol class="tl" aria-label={t('tlLabel')}>
          {#each items as it (it.id)}
            {#if !(it.kind === 'approval' && it.state === 'pending') && !(shutdownStop && it.kind === 'system' && SOZ_KAPANDI_SATIRI_RE.test(it.text)) && !(blockedDup && it.kind === 'system' && it.state === 'error' && it.text.trim() === job?.startError?.trim())}
              <li class="it k-{it.kind}" in:rowIn>
                {#if it.kind === 'user'}
                  <p class="user voice">{it.text}</p>
                {:else if it.kind === 'agent'}
                  {@const ask = needsReply && it.id === lastAgentId}
                  <div class="agent" class:ask data-question={ask ? '' : undefined}>
                    <span class="gl"><Icon name="question" size={16} /></span>
                    <div class="aq">
                      {#if ask}<p class="small aqt">{t('askTitle')}</p>{/if}
                      <p>{it.text}</p>
                    </div>
                  </div>
                {:else if it.kind === 'progress'}
                  <div class="prog" class:active={it.state === 'active'}>
                    <span class="gl">{#if it.state === 'active'}<i class="spin" aria-hidden="true"></i>{:else}<Icon name="check" size={14} strokeWidth={2} />{/if}</span>
                    <span class="txt">{friendlyLine(it.text)}</span>
                    {#if it.detail}<button class="more small" onclick={() => (open[String(it.id)] = !open[String(it.id)])}>{open[String(it.id)] ? t('hideDetail') : t('showDetail')}</button>{/if}
                    <time class="small muted when" datetime={it.ts}>{ago(it.ts)}</time>
                  </div>
                  {#if it.detail && open[String(it.id)]}<pre class="calm det">{it.detail}</pre>{/if}
                {:else if it.kind === 'guard'}
                  <div class="guard">
                    <span class="gl"><Icon name="shield" size={16} /></span>
                    <span class="txt">{it.text}</span>
                    {#if it.detail}<button class="more small" onclick={() => (open[String(it.id)] = !open[String(it.id)])}>{open[String(it.id)] ? t('hideDetail') : t('showDetail')}</button>{/if}
                  </div>
                  {#if it.detail && open[String(it.id)]}<pre class="calm det">{it.detail}</pre>{/if}
                {:else if it.kind === 'approval'}
                  <div class="sys"><span class="gl"><Icon name={it.state === 'allowed' ? 'check' : 'x'} size={14} strokeWidth={2} /></span><span>{it.state === 'allowed' ? t('approvalYesDone') : t('approvalNoDone')}</span></div>
                {:else if it.kind === 'proof' && it.proof}
                  {#if decisionProof === it.id}
                    <ProofCard item={it} decision>
                      {#snippet actions()}
                        {@const pf = it.proof!}
                        {#if pf.reasonCode === 'approved_failed' || pf.reasonCode === 'agent_tests_fail'}
                          {@const ap = pf.reasonCode === 'approved_failed'}
                          <p class="small dnote">{ap ? t('decNoteApproved') : t('decNoteAgentTests')}</p>
                          <div class="decbtns" role="group" aria-label={t('decActionsLabel')} data-decision-actions>
                            <button class="btn btn-primary" onclick={() => decideChange(j.id, true)} disabled={!!sf.busy['change:' + j.id]} data-accept>{ap ? t('decAcceptApproved') : t('decAcceptAnyway')}</button>
                            <button class="btn" onclick={() => rejectAndSendBack(j.id, sendBackDraft(pf))} data-reject>{t('decRejectBack')}</button>
                            <button class="btn btn-quiet" onclick={() => undoReq++} data-decision-undo><Icon name="undo" size={14} />{t('decUndo')}</button>
                          </div>
                        {:else}
                          <p class="small dnote">{t('decisionNote')}</p>
                          <div class="decbtns" role="group" aria-label={t('decActionsLabel')} data-decision-actions>
                            <button class="btn btn-primary" onclick={() => decideChange(j.id, true)} data-accept>{t('decisionAccept')}</button>
                            <button class="btn" onclick={() => decideChange(j.id, false)} data-reject>{t('decisionReject')}</button>
                          </div>
                        {/if}
                      {/snippet}
                    </ProofCard>
                  {:else}
                    <ProofCard item={it} accepted={lastProofId === it.id ? acceptInfo : null} />
                  {/if}
                {:else}
                  <div class="sys"><span class="dot" aria-hidden="true"></span><span>{it.text}</span>{#if it.detail}<button class="more small" onclick={() => (open[String(it.id)] = !open[String(it.id)])} data-sys-more>{open[String(it.id)] ? t('hideDetail') : t('showDetail')}</button>{/if}</div>
                  {#if it.detail && open[String(it.id)]}<pre class="calm det">{it.detail}</pre>{/if}
                {/if}
              </li>
            {/if}
          {/each}
        </ol>
        {#if items.length === 0}
          <p class="muted small empty">{sf.tlLoading[j.id] ? t('streamLoading') : t('streamEmpty')}</p>
        {/if}

        <div class="state-cards">
          {#if taslak}
            <section class="card neutral" data-draft-card in:rowIn>
              <p class="ct"><Icon name="info" size={16} />{t('draftTitle')}</p>
              <p class="small muted">{t('draftBody')}</p>
              <div class="cact">
                <button class="btn btn-primary" onclick={() => retryStart(j.id)} disabled={!!sf.busy['cancel:' + j.id]} data-draft-start><Icon name="send" size={14} />{t('barStart')}</button>
                <button class="btn" onclick={() => vazgecTaslak(j.id)} disabled={!!sf.busy['cancel:' + j.id]} data-draft-cancel>{t('draftCancel')}</button>
              </div>
            </section>
          {/if}

          {#if yenidenYaptir}
            <section class="card neutral" data-redo-card in:rowIn>
              <p class="ct"><Icon name="refresh" size={16} />{bucket === 'nowork' ? t('redoTitleNoWork') : t('redoTitleFailed')}</p>
              <p class="small muted">{t('redoBody')}</p>
              <div class="cact">
                <button class="btn btn-primary" onclick={yenidenYaptirAc} data-redo><Icon name="refresh" size={14} />{t('redoBtn')}</button>
              </div>
            </section>
          {/if}

          {#if bucket === 'queued'}
            <section class="card neutral" data-queued in:rowIn>
              <p class="ct"><Icon name="clock" size={16} />{t('queuedTitle')}</p>
              <p class="small muted">{app.settings?.maxParallelRuns ? t('queuedBody', { n: app.settings.maxParallelRuns }) : t('queuedBodyAny')}</p>
              <div class="cact"><button class="btn btn-sm" onclick={toParallel} data-open-settings>{t('queuedChange')}</button></div>
            </section>
          {/if}

          {#if shutdownStop}
            <section class="card neutral" data-shutdown-stop in:rowIn>
              <p class="ct"><Icon name="info" size={16} />{t('shutdownTitle')}</p>
              <p class="small muted">{t('shutdownBody')}</p>
            </section>
          {/if}

          {#if bucket === 'blocked'}
            <section class="card neutral" data-blocked in:rowIn>
              {#if midRunFailed(j, hasAgentLine)}
                <p class="ct"><Icon name="info" size={16} />{t('rowHalf')}</p>
                <HataSatiri h={halfDetail ? { text: t('halfBody'), detail: halfDetail, status: -1, refresh: false, kind: 'diger' } : kendiHata(t('halfBody'))} data-half-error />
              {:else}
                <p class="ct"><Icon name="info" size={16} />{j.startError && sadeMi(j.startError) ? t('blockedTitle', { m: j.startError }) : t('blockedTitleAny')}</p>
                {#if j.startError && !sadeMi(j.startError)}<HataSatiri h={{ text: t('blockedRawNote'), detail: j.startError, status: -1, refresh: false, kind: 'diger' }} data-blocked-error />{:else if halfDetail}<div><button class="more small" onclick={() => (open['blk' + j.id] = !open['blk' + j.id])} data-blocked-more>{open['blk' + j.id] ? t('hideDetail') : t('showDetail')}</button>{#if open['blk' + j.id]}<pre class="calm" data-blocked-detail>{halfDetail}</pre>{/if}</div>{/if}
              {/if}
              <p class="small muted">{t('blockedBody')}</p>
              {#if midRunFailed(j, hasAgentLine)}
                <!-- Yarıda kalan işte doğal adım yeniden denemektir; Ayarlar yalnız hata ayarla ilgiliyse görünür (yardımcı bulunamadı, giriş yapılmamış gibi). -->
                <div class="cact">
                  <button class="btn btn-primary" onclick={() => retryStart(j.id)} data-half-retry><Icon name="refresh" size={14} />{t('retryStart')}</button>
                  {#if ayarIlgili(j.startError, halfDetail)}<button class="btn" onclick={toSettings} data-open-settings>{t('blockedOpenSettings')}</button>{/if}
                </div>
              {:else}
                <div class="cact">
                  <button class="btn btn-primary" onclick={toSettings} data-open-settings>{t('blockedOpenSettings')}</button>
                  <button class="btn" onclick={() => retryStart(j.id)}><Icon name="refresh" size={14} />{t('retryStart')}</button>
                </div>
              {/if}
            </section>
          {/if}

          {#if offer && offer.added > 0}
            <section class="card sug" aria-label={t('offerLabel')} data-test-offer in:rowIn>
              <p class="small sugs"><Icon name="info" size={16} /><span>{offer.addedSentence || offer.sentence}</span></p>
              {#if offer.edited === 0 && sf.offerError[j.id]}<p class="small err-line" role="alert" data-offer-error>{sf.offerError[j.id]}</p>{/if}
              <div class="cact">
                <button class="btn btn-primary btn-sm" onclick={() => answerO(true)} disabled={offerBusy} data-offer-yes>{t('offerYes')}</button>
                <button class="btn btn-sm" onclick={() => answerO(false)} disabled={offerBusy} data-offer-no>{t('offerNo')}</button>
              </div>
            </section>
          {/if}
          {#if offer && offer.edited > 0}
            <section class="card sug weakcard" aria-label={t('offerEditLabel')} data-test-offer-edit in:rowIn>
              <p class="small sugs"><Icon name="info" size={16} /><span>{offer.editedSentence || offer.sentence}</span></p>
              {#if editSeen}
                <div class="offerdiff" data-offer-diff>
                  <span class="small muted">{t('offerDiffTitle')}</span>
                  {#each editDiff as f, i (`${i}:${f.path}`)}
                    <p class="small dpath"><span class="mono">{f.path}</span>{#if f.sentence}<span class="muted">{' · '}{f.sentence}</span>{/if}</p>
                    {#if (f.lines ?? []).length > 0}
                      <pre class="calm dl" data-diff>{#each f.lines ?? [] as l, k (k)}<span class="ln op-{l.op === '+' ? 'add' : l.op === '-' ? 'del' : 'ctx'}"><span class="sr-only">{l.op === '+' ? t('pcDiffAdded') : l.op === '-' ? t('pcDiffRemoved') : ''}{' '}</span><span aria-hidden="true">{l.op === '+' || l.op === '-' ? l.op : ' '}{' '}</span>{l.text}</span>{/each}</pre>
                    {/if}
                  {/each}
                </div>
                {#if !editViewable}<p class="small sugnote" id="offer-edit-unviewable" data-offer-hint>{t('offerUnviewable')}</p>{/if}
              {:else}
                <p class="small sugnote" id="offer-edit-hint" data-offer-hint>{t('offerEditHint')}</p>
              {/if}
              {#if sf.offerError[j.id]}<p class="small err-line" role="alert" data-offer-error>{sf.offerError[j.id]}</p>{/if}
              <div class="cact">
                {#if !editSeen}
                  <button class="btn btn-primary btn-sm" onclick={viewEdit} disabled={offerBusy} data-offer-view>{t('offerSeeDiff')}</button>
                  <button class="btn btn-sm" onclick={() => answerEdit(false)} disabled={offerBusy} data-offer-edit-no>{t('offerEditNo')}</button>
                  <button class="btn btn-quiet btn-sm" disabled aria-describedby="offer-edit-hint" data-offer-edit-yes>{t('offerEditYes')}</button>
                {:else}
                  <button class="btn btn-primary btn-sm" onclick={() => answerEdit(false)} disabled={offerBusy} data-offer-edit-no>{t('offerEditNo')}</button>
                  <button class="btn btn-quiet btn-sm" onclick={() => answerEdit(true)} disabled={offerBusy || !editViewable} aria-describedby={!editViewable ? 'offer-edit-unviewable' : undefined} data-offer-edit-yes>{t('offerEditYes')}</button>
                {/if}
              </div>
            </section>
          {/if}

          {#each suggestions as s (s.checkId)}
            {@const weak = s.strength === 'weak' || (s.strength !== 'strong' && s.defaultAnswer === 'hayir')}
            <section class="card sug" class:weakcard={weak} class:rejectedcard={s.rejected} aria-label={t('sugTitle')} data-suggested={s.checkId} data-strength={s.strength ?? 'unknown'} in:rowIn>
              <p class="small sugs"><Icon name="info" size={16} /><span>{t('sugWrote', { c: s.cmd })}{#if !s.rejected}{' '}{t('sugAsk')}{/if}</span></p>
              {#if s.strengthNote}
                <p class="small sugnote" data-strength-note>{s.strengthNote}</p>
              {:else if !s.rejected}
                <p class="small muted">{t('sugNote')}</p>
              {/if}
              <div class="cact">
                {#if s.rejected}
                  <button class="btn btn-sm" onclick={() => answer(s, false)} disabled={sugBusy} data-suggest-ok>{t('sugOk')}</button>
                {:else if weak}
                  <button class="btn btn-primary btn-sm" onclick={() => answer(s, false)} disabled={sugBusy} data-suggest-no>{t('sugNo')}</button>
                  <button class="btn btn-quiet btn-sm" onclick={() => answer(s, true)} disabled={sugBusy} data-suggest-yes>{t('sugYes')}</button>
                {:else}
                  <button class="btn btn-primary btn-sm" onclick={() => answer(s, true)} disabled={sugBusy} data-suggest-yes>{t('sugYes')}</button>
                  <button class="btn btn-sm" onclick={() => answer(s, false)} disabled={sugBusy} data-suggest-no>{t('sugNo')}</button>
                {/if}
              </div>
            </section>
          {/each}

          {#if j.awaiting === 'decision' && decisionProof === null}
            <section class="card warn" data-decision in:rowIn>
              <p class="ct"><Icon name="alert" size={16} />{t('decisionTitle')}</p>
              <p class="small">{j.baseNote || t('reasonDefinition')}</p>
              <p class="small muted">{t('decisionNote')}</p>
              <div class="cact">
                <button class="btn btn-primary" onclick={() => decideChange(j.id, true)} data-accept>{t('decisionAccept')}</button>
                <button class="btn" onclick={() => decideChange(j.id, false)} data-reject>{t('decisionReject')}</button>
              </div>
            </section>
          {/if}

          {#if needsReply && !hasAgentLine && j.question}
            <section class="card warn" data-question in:rowIn>
              <p class="small aqt">{t('askTitle')}</p>
              <p>{j.question}</p>
            </section>
          {/if}

          {#if pend.length > 0}
            {@const a = pend[0]}
            {@const total = answered + pend.length}
            <section class="card warn appr" role="group" aria-label={t('approvalTitle')} data-approval data-approval-id={a.eventId} in:rowIn>
              {#if total > 1}<p class="cnt small" data-approval-counter>{t('approvalCounter', { total, n: answered + 1 })}</p>{/if}
              <p class="ct"><Icon name="alert" size={16} />{t('approvalAsk')}</p>
              <pre class="calm" data-approval-what>{a.summary}</pre>
              <div class="cact">
                <button class="btn btn-primary" onclick={() => decide(j.id, a.eventId, true)} disabled={!!sf.busy['decide:' + j.id + ':' + a.eventId]} data-approve="yes">{t('approvalYes')} <kbd>e</kbd></button>
                <button class="btn" onclick={() => decide(j.id, a.eventId, false)} disabled={!!sf.busy['decide:' + j.id + ':' + a.eventId]} data-approve="no">{t('approvalNo')} <kbd>h</kbd></button>
              </div>
              <p class="small muted">{pend.length > 1 ? t('approvalRestNote') : t('approvalStopNote')}</p>
            </section>
          {/if}
        </div>

        <HumanChecklist jobId={j.id} projectId={j.projectId} />
        {#if j.sessionId != null}<ExplainBox sessionId={j.sessionId} />{/if}

        <details class="extra" bind:open={sf.checksOpen}>
          <summary class="h2s">{t('checksSection')}</summary>
          <ChecksPanel projectId={j.projectId} />
        </details>
        {#if j.promptText}
          <details class="extra">
            <summary class="h2s">{t('promptSection')}</summary>
            <pre class="pre">{j.promptText}</pre>
          </details>
        {/if}
      </div>

      {#if replyMode === 'form' || replyMode === 'continue'}
        {#if replyMode === 'continue' && sf.rejectBack[j.id]}<p class="small rejnote" data-reject-note>{t('rejectNote')}</p>{/if}
        <form class="reply" class:needs={needsReply} onsubmit={(e) => { e.preventDefault(); void send() }}>
          <label class="sr-only" for="reply-box">{t('replyLabel')}</label>
          <textarea id="reply-box" rows="1" bind:this={replyEl} bind:value={replyText} oninput={autosize} onkeydown={onReplyKey}
            placeholder={placeholder} data-reply></textarea>
          <button class="btn btn-primary send" type="submit" aria-label={replyMode === 'continue' && sf.rejectBack[j.id] ? t('rejectSend') : replyMode === 'continue' ? t('continueSend') : t('replySend')}><Icon name="send" size={16} /><span class="sendtxt">{replyMode === 'continue' && sf.rejectBack[j.id] ? t('rejectSend') : replyMode === 'continue' ? t('continueSend') : t('replySend')}</span></button>
        </form>
      {:else if replyMode === 'button'}
        <div class="reply cont">
          <button class="btn" onclick={openContinue} data-continue title={t('continueHint')}><Icon name="refresh" size={14} />{t('continueBtn')} <kbd>r</kbd></button>
          <span class="small muted cont-hint">{t('continueHint')}</span>
        </div>
      {/if}
    </div>
  {/key}
{/if}

<style>
  .pane { display: flex; flex-direction: column; min-height: 0; height: 100%; }
  .head { flex: none; display: flex; flex-direction: column; gap: 8px; padding: 14px 20px 12px; border-bottom: 1px solid var(--line); background: var(--surface); }
  .head.kisa { gap: 4px; padding-block: 8px; }
  .head-ic { display: flex; flex-direction: column; gap: 8px; max-width: 46rem; margin-bottom: 14px; padding-bottom: 12px; border-bottom: 1px solid var(--line); }
  .back { display: none; align-self: flex-start; margin-left: -8px; }
  .title { font-size: var(--fs-md); font-weight: var(--fw-strong); line-height: 1.35; letter-spacing: -0.005em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .meta { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 12px; }
  .guardtag { display: inline-flex; align-items: center; gap: 4px; }
  .nightsays { display: flex; align-items: flex-start; gap: 8px; max-width: 64ch; }
  .nightsays :global(svg) { flex: none; margin-top: 2px; }
  .sugnote { margin: 0; }
  .weakcard .sugnote { color: var(--ink-2); }
  .rejectedcard { border-style: dashed; }
  .offerdiff { display: flex; flex-direction: column; gap: 6px; }
  .offerdiff p { margin: 0; overflow-wrap: anywhere; }
  .offerdiff .dl { max-height: calc(13 * 1.55em); }
  .offerdiff .ln { display: block; }
  .offerdiff .op-add { color: var(--ok); }
  .offerdiff .op-del { color: var(--stalled); }
  .sugs { display: flex; align-items: flex-start; gap: 8px; font-weight: var(--fw-medium); }
  .sugs :global(svg) { flex: none; margin-top: 2px; }
  .rejnote { margin: 0; padding: 8px 20px 0; color: var(--ink); font-weight: var(--fw-medium); background: var(--surface); }
  .stop { margin-left: auto; }
  .stopask { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; margin: 0; padding: 8px 12px; border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); }
  .stopask p { margin: 0; flex: 1 1 14rem; }
  .stop kbd, .cact kbd, .cont kbd { margin-left: 2px; }
  .cact .btn-primary kbd { background: transparent; border-color: currentColor; color: inherit; opacity: 0.8; }
  .basenote { display: flex; align-items: flex-start; gap: 8px; padding: 6px 10px; border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); }
  .basenote :global(svg) { flex: none; margin-top: 2px; color: var(--waiting); }
  .fallback { display: flex; flex-direction: column; gap: 6px; padding: 8px 12px; border-radius: var(--radius); background: var(--waiting-tint); }
  .fb-act { display: flex; flex-wrap: wrap; gap: 6px; }

  .scroll { flex: 1; min-height: 0; overflow-y: auto; padding: 18px 20px 24px; outline: none; background: var(--surface); }
  .tl { display: flex; flex-direction: column; gap: 10px; max-width: 46rem; }
  .it { min-width: 0; }
  .gl { flex: none; display: inline-flex; align-items: center; justify-content: center; width: 20px; height: 20px; color: var(--ink-3); }

  .user { padding: 2px 0 2px 14px; border-left: 2px solid var(--ink); max-width: 60ch; margin-top: 4px; }
  .agent { display: flex; gap: 10px; align-items: flex-start; max-width: 60ch; line-height: 1.5; }
  .agent .gl { color: var(--waiting); margin-top: 1px; }
  .agent p { font-size: var(--fs-sm); }
  .aqt { font-weight: var(--fw-strong); color: var(--waiting); }
  .agent.ask { padding: 12px 14px; border: 1px solid var(--waiting); border-left-width: 3px; border-radius: var(--radius-lg); background: var(--waiting-tint); }
  .agent.ask p:not(.aqt) { font-weight: var(--fw-medium); color: var(--ink); }

  .prog { display: flex; align-items: center; gap: 10px; min-height: 24px; color: var(--ink-2); }
  .prog .gl { color: var(--ok); }
  .prog.active { color: var(--ink); }
  .prog.active .gl { color: var(--working); }
  .prog .txt { flex: 0 1 auto; min-width: 0; }
  .when { margin-left: auto; opacity: 0; transition: opacity var(--dur-1) var(--ease); white-space: nowrap; }
  .it:hover .when, .it:focus-within .when { opacity: 1; }
  .more { border: 0; background: none; color: var(--ink-3); text-decoration: underline; text-underline-offset: 0.18em; padding: 0 4px; min-height: 32px; }
  .more:hover { color: var(--ink); }
  .calm { margin: 0; padding: 10px 12px; border-radius: var(--radius); background: var(--bg-sunken); font-family: var(--mono); font-size: var(--fs-xs); line-height: 1.55; white-space: pre-wrap; overflow-wrap: anywhere; max-height: calc(14 * 1.55em); overflow-y: auto; color: var(--ink-2); }
  .det { margin: 2px 0 4px 30px; }

  .guard { display: flex; align-items: center; gap: 10px; padding: 6px 10px; border-radius: var(--radius); background: var(--waiting-tint); color: var(--ink); }
  .guard .gl { color: var(--waiting); }

  .state-cards { display: flex; flex-direction: column; gap: 12px; max-width: 46rem; margin-top: 12px; }
  .state-cards:empty { display: none; }
  .card { display: flex; flex-direction: column; gap: 8px; padding: 14px 16px; border: 1px solid var(--line-strong); border-left-width: 3px; border-radius: var(--radius-lg); background: var(--surface); max-width: 40rem; }
  .card.warn { border-color: var(--waiting); box-shadow: var(--shadow); }
  .card.neutral { border-color: var(--line-strong); border-left-color: var(--idle); background: var(--bg); }
  .ct { display: flex; gap: 8px; align-items: flex-start; font-weight: var(--fw-medium); line-height: 1.4; }
  .ct :global(svg) { flex: none; margin-top: 2px; }
  .warn .ct :global(svg) { color: var(--waiting); }
  .neutral .ct :global(svg) { color: var(--ink-2); }
  .cact { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 2px; }
  .cnt { align-self: flex-start; padding: 2px 8px; border-radius: var(--radius-sm); background: var(--waiting-tint); color: var(--waiting); font-weight: var(--fw-strong); font-variant-numeric: tabular-nums; outline: 1px solid var(--waiting); outline-offset: -1px; }
  .dnote { flex: 1 1 100%; margin: 0; color: var(--ink-2); }
  .decbtns { flex: 1 1 100%; display: flex; flex-wrap: wrap; gap: 8px; }
  /* Dar ekranda karar düğmeleri alt alta dizilir ve kaydırılan alanın altına yapışık durur: kaydırma beklemeden görünür. */
  @media (max-width: 559px) {
    .dnote { padding: 10px 14px 4px; border-top: 1px solid var(--line); }
    .decbtns { position: sticky; bottom: -20px; z-index: 1; gap: 6px; padding: 8px 14px 10px; background: var(--surface); border-top: 1px solid var(--line); box-shadow: 0 -6px 10px -8px var(--ink-3); }
    .decbtns :global(.btn) { flex: 1 1 100%; }
  }

  .sys { display: flex; align-items: center; gap: 10px; color: var(--ink-3); font-size: var(--fs-xs); }
  .sys .more { flex: none; white-space: nowrap; }
  .sys .gl { width: 16px; height: 16px; }
  .dot { flex: none; width: 6px; height: 6px; margin: 0 7px; border-radius: 50%; background: var(--line-strong); }
  .empty { padding: 12px 0; }

  .extra { margin-top: 20px; max-width: 46rem; }
  .extra > summary { cursor: pointer; min-height: var(--ctl); display: flex; align-items: center; font-weight: var(--fw-medium); color: var(--ink-2); }

  .reply { flex: none; display: flex; align-items: flex-end; gap: 8px; padding: 10px 20px 12px; border-top: 1px solid var(--line); background: var(--surface); }
  .reply.cont { align-items: center; flex-wrap: wrap; }
  /* A3: kısa pencerede ve yakınlaştırmada açıklama satırı yer kaplamaz, düğmenin ipucunda kalır. */
  @media (max-height: 699px) { .cont-hint { display: none; } }
  .reply textarea { flex: 1; min-width: 0; resize: none; min-height: var(--ctl); max-height: 120px; padding: 6px 10px; border: 1px solid var(--line-strong); border-radius: var(--radius); background: var(--surface); line-height: 1.45; }
  .reply textarea:focus-visible { outline-offset: 1px; border-color: var(--focus); }
  .reply.needs textarea { border-color: var(--waiting); border-width: 2px; padding: 5px 9px; }
  .send { flex: none; }

  @media (max-width: 759px) {
    .back { display: inline-flex; }
    .head { padding: 8px 16px 10px; }
    .head.kisa { padding-block: 4px 8px; }
    .head.kisa .back { min-height: 32px; }
    .scroll { padding: 14px 16px 20px; }
    .reply { padding: 8px 16px 10px; }
    .sendtxt { display: none; }
    .when { display: none; }
    .cact .btn { flex: 1 1 100%; }
  }
</style>
