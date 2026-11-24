<script lang="ts">
  import { tick } from 'svelte'
  import { getProtection, runAttentionAction } from '../api5'
  import { at, loadAttention } from '../attention.svelte'
  import { t } from '../i18n/index.svelte'
  import { go } from '../router.svelte'
  import { confidenceLabel, endLabel, sourceLabel } from '../session'
  import { app, toast } from '../store.svelte'
  import { agentName, reasonOf, refreshSurface, sf } from '../surface.svelte'
  import { focusLiveWindow, focusToastText } from '../profil'
  import { pf } from '../profileStore.svelte'
  import type { AttentionAction, AttentionItem } from '../types5'
  import { ago, copyText, friendlyActionError } from '../util'
  import { hata, type Hata } from '../hata'
  import { loseReply, stillThere } from '../attention.svelte'
  import HataSatiri from './HataSatiri.svelte'
  import ExplainBox from './ExplainBox.svelte'
  import SessionAttention, { isSessionItem } from './SessionAttention.svelte'
  import Icon, { type IconName } from './Icon.svelte'
  import ProtectionBar from './ProtectionBar.svelte'

  // flat: ekranda zaten bir birincil düğme varken (Şu An) kartın düğmeleri ikincil çizilir, sıra yine önemliden başlar (O9).
  let { item, quiet = false, flat = false }: { item: AttentionItem; quiet?: boolean; flat?: boolean } = $props()

  // İşin son sonucu no_work ise (klasör hiç değişmedi) etiket uyarı rengiyle "İş yapılmamış görünüyor" der.
  const noWorkJob = $derived.by(() => { if (item.jobId == null || !['unproven', 'passed', 'no_change'].includes(item.reason)) return false; const j = sf.jobs.find((x) => x.id === item.jobId); return !!j && reasonOf(j) === 'no_work' })
  const label = $derived(
    noWorkJob ? t('grpNoWork')
    : item.reason === 'session_turn_end' ? t('saTurnEnd')
    : item.kind === 'ask' ? t('attAsk')
    : item.kind === 'accept' ? t('attAccept')
    : item.kind === 'blocked' && item.reason === 'proof_failed' ? t('attProofFailed')
    : item.kind === 'blocked' ? t('attBlocked')
    : item.kind === 'done' && item.reason === 'no_change' ? t('grpNoChange') // liste, Sabah ve iş sayfasıyla aynı sakin etiket
    : item.kind === 'done' ? t('attDoneLabel')
    : item.reason === 'unproven' ? t('grpUnproven') // iş listesinde, Sabah'ta ve burada aynı etiket: "Kanıtsız"
    : t('attUncertain'),
  )
  const icon = $derived<IconName>(noWorkJob ? 'alert' : item.kind === 'ask' ? 'question' : item.kind === 'accept' ? 'waiting' : item.kind === 'blocked' ? 'x' : item.kind === 'done' && item.reason === 'no_change' ? 'idle' : item.kind === 'done' ? 'check' : 'unchecked')
  const chipCls = $derived(noWorkJob ? 'chip-waiting' : item.kind === 'ask' || item.kind === 'accept' ? 'chip-waiting' : item.kind === 'blocked' ? 'chip-stalled' : item.kind === 'done' && item.reason === 'no_change' ? 'chip-neutral' : item.kind === 'done' ? 'chip-ok' : 'chip-unproven')

  // Terminalde açılan oturum Söz'ün başlattığı iş değildir: Söz ona cevap yazamaz, durduramaz, satırı kendi başına kapatamaz.
  // Düğme çizilmez, `sentence` yönlendirir; sağda soluk "Terminalde" etiketi durur, soru varsa kopyalanabilir.
  const terminal = $derived(item.jobId == null && item.sessionId != null && (item.reason?.startsWith('session_') ?? false))
  const asking = $derived(terminal && (item.reason === 'session_question' || item.kind === 'ask') && !!item.detail)
  async function copyQuestion() {
    toast((await copyText(item.detail ?? '')) ? t('copied') : t('copyFailed'))
  }
  async function openTerminalWin() {
    const sessInfo = item.sessionId != null ? (at.sessions[item.sessionId] ?? null) : null
    const projPath = pf.data?.projects?.find((p) => p.name === item.projectName)?.path
      ?? sf.projects.find((p) => p.name === item.projectName)?.path
      ?? null
    try {
      const r = await focusLiveWindow({ sessionId: item.sessionId, tool: sessInfo?.tool ?? null, path: projPath, canFocus: true })
      toast(focusToastText(r, sessInfo?.tool ?? null))
    } catch (e) {
      toast(friendlyActionError(e, 'window'))
    }
  }

  const sess = $derived(item.sessionId != null ? (at.sessions[item.sessionId] ?? null) : null)
  const facts = $derived.by(() => {
    if (!sess) return null
    const parts = [sourceLabel(sess.stateSource), endLabel(sess.endClass)].filter((x): x is string => !!x)
    return { parts, conf: confidenceLabel(sess.stateConfidence) }
  })

  // Yardımcı: Söz'ün işiyse işin yardımcısı, değilse sunucunun verdiği ad.
  const helperName = $derived(item.helper?.trim() || (item.jobId != null ? (() => { const j = sf.jobs.find((x) => x.id === item.jobId); return j?.agent ? agentName(j.agent) : '' })() : ''))

  let busy = $state(false)
  let error = $state<Hata | null>(null)
  let replying = $state(false)
  let replyText = $state('')
  let replyEl: HTMLTextAreaElement | undefined = $state()
  let undoOpen = $state(0)
  let showUndo = $state(false)
  let undoBtn: HTMLButtonElement | undefined = $state()
  let canUndo = $state(false)

  // "Geri al" yalnız klasörde geri alınacak bir şey varsa görünür (koruma bilgisini sunucu verir).
  // Başlamamış işte (start_failed) klasörde geri alınacak bir şey yok: "Geri al" ve "Devam et" hiç çıkmaz (C11).
  const notStarted = $derived(item.reason === 'start_failed')
  const undoKind = $derived(item.jobId != null && !notStarted && (item.kind === 'blocked' || item.kind === 'accept' || item.kind === 'done'))
  const sozNote = $derived(item.sozNotu?.trim() || '')
  $effect(() => {
    const id = item.jobId
    void app.jobTick
    if (id == null || !undoKind) {
      canUndo = false
      return
    }
    let live = true
    getProtection(id).then((p) => { if (live) canUndo = p.canUndo }, () => { if (live) canUndo = false })
    return () => { live = false }
  })

  // Yazılmış cevap varken kart kalkarsa metin kaybolmaz (K-07).
  $effect(() => () => { if (replyText.trim() && !stillThere(item.id)) loseReply(item, replyText) })

  // Kartın gövdesine tıklamak anlamlı bir şey açar: Söz işiyse işin sayfası, terminaldeki yardımcıysa onun penceresi. Başka yerde tıklanabilir görünmez.
  const bodyOpen = $derived<(() => void) | null>(item.jobId != null ? () => toJob() : terminal ? () => void openTerminalWin() : null)
  function onBody(e: MouseEvent) {
    if (!bodyOpen || (e.target as HTMLElement).closest('button, a, input, textarea, select, summary, details, form, label')) return
    bodyOpen()
  }

  async function run(a: AttentionAction, extra?: Record<string, unknown>) {
    if (busy) return
    busy = true
    error = null
    try {
      await runAttentionAction(a, extra)
      toast(t('attActionDone', { a: a.label }))
      replying = false
      replyText = ''
      await loadAttention()
      refreshSurface()
    } catch (e) {
      error = hata(e)
      // Kart başka yerden kalkmış ya da zaten yapılmış: listeyi yeniden oku, kart kendiliğinden kalksın.
      if (error.refresh) { void loadAttention(); refreshSurface() }
    } finally {
      busy = false
    }
  }

  async function act(a: AttentionAction) {
    // "Değişikliği gör": fark özetini iş sayfası gösterir ve orada "gördüm" kaydı yazılır; burada ham cevap gösterilmez.
    if (a.id === 'view-test-edit') {
      toJob()
      return
    }
    if (a.id === 'reply') {
      replying = !replying
      if (replying) {
        await tick()
        replyEl?.focus()
      }
      return
    }
    await run(a)
  }

  const reply = $derived(item.actions.find((a) => a.id === 'reply') ?? null)
  const toJob = () => {
    if (item.jobId != null) go('is', String(item.jobId))
  }

  // Onayladığın testler geçmiyor ya da ajanın yeni testleri geçmiyor: farkı görmeden tek dokunuşla kabul edilmesin, birincil düğme "Kanıtı gör".
  const proofDecision = $derived.by(() => {
    if (item.kind !== 'accept' || item.jobId == null) return false
    const j = sf.jobs.find((x) => x.id === item.jobId)
    const r = j ? reasonOf(j) : null
    return r === 'approved_failed' || r === 'agent_tests_fail'
  })

  // Her satırda TEK birincil düğme: durumun en doğru eylemi. Ötekiler ikincil, "Gördüm" sessiz. "Emin değilim" satırında birincil yoktur.
  //   soru -> Cevapla; izin isteği -> işi aç (izin kararı ikincil: ne istendiğini görmeden basılmasın);
  //   kabul bekleyen -> Kabul ediyorum; kanıtı kalan -> Kanıtı gör; başlatılamayan -> işi aç; biten -> Gördüm.
  type Btn = { key: string; label: string; role: 'primary' | 'secondary' | 'quiet'; onclick: () => void; action?: AttentionAction; expanded?: boolean }
  const buttons = $derived.by<Btn[]>(() => {
    const out: Btn[] = []
    const openJob: Btn | null = item.jobId == null ? null : { key: 'open', label: item.reason === 'proof_failed' || proofDecision ? t('attSeeProof') : t('attOpenJob'), role: 'secondary', onclick: toJob }
    for (const a of item.actions) {
      if (notStarted && a.id === 'continue') continue
      const role: Btn['role'] =
        a.id === 'reply' || (a.id === 'accept' && !proofDecision) ? 'primary'
        : a.id === 'seen' ? (item.kind === 'done' ? 'primary' : 'quiet')
        : 'secondary'
      out.push({ key: a.id, label: a.id === 'reply' ? t('attReplyBtn') : a.label, role, onclick: () => void act(a), action: a, expanded: a.id === 'reply' ? replying : undefined })
    }
    if (openJob) out.push(openJob)
    if (quiet) return out.map((b) => (b.role === 'primary' ? { ...b, role: 'secondary' as const } : b))
    if (!out.some((b) => b.role === 'primary') && openJob && (item.kind === 'blocked' || item.reason === 'proof_failed' || proofDecision || (item.kind === 'ask' && item.reason === 'approval'))) openJob.role = 'primary'
    const rank = { primary: 0, secondary: 1, quiet: 2 } as const
    return out.map((b, i) => ({ b, i })).sort((x, y) => rank[x.b.role] - rank[y.b.role] || x.i - y.i).map((x) => x.b)
  })

  function sendReply() {
    if (reply && replyText.trim()) void run(reply, { text: replyText.trim() })
  }
</script>

{#if isSessionItem(item) && !quiet}
  <SessionAttention {item} {flat} />
{:else}
<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
<li class="arow wc k-{item.kind}" class:quiet class:clickable={!!bodyOpen} title={bodyOpen ? (item.jobId != null ? t('waitCardOpenJob') : t('saBodyGo')) : undefined} onclick={onBody} data-attention={item.id} data-attention-job={item.jobId ?? undefined} data-attention-session={item.externalSessionId ?? (item.sessionId != null ? String(item.sessionId) : undefined)} data-kind={item.kind} data-reason={item.reason} data-priority={item.priority}>
  <div class="top wc-top">
    <span class="chip {chipCls}"><Icon name={icon} size={14} strokeWidth={2} />{label}</span>
    {#if item.isNew}<span class="chip chip-working" data-new>{t('attNew')}</span>{/if}
    {#if item.projectName}<span class="wc-proj">{item.projectName}</span>{/if}
    {#if helperName}<span class="tool-badge" data-wc-helper>{helperName}</span>{/if}
    {#if terminal}<span class="small muted tag" data-terminal-tag title={t('attTerminalHelp')}><Icon name="terminal" size={14} />{t('attTerminal')}</span>{/if}
    <time class="wc-time" datetime={item.at}>{ago(item.at)}</time>
  </div>
  <p class="sent wc-sent">{item.sentence}</p>
  {#if asking}
    <div class="qcopy" data-terminal-question>
      <blockquote class="voice det">{item.detail}</blockquote>
      <div class="qacts">
        <button type="button" class="btn btn-sm" class:btn-primary={!flat} onclick={openTerminalWin} data-focus-live><Icon name="terminal" size={14} />{t('suAnOpenWin')}</button>
        <button type="button" class="btn btn-quiet btn-sm" onclick={copyQuestion} data-copy-question><Icon name="copy" size={14} />{t('attCopyQuestion')}</button>
      </div>
    </div>
  {:else if item.detail}<p class="small det">{item.detail}</p>{/if}
  {#if sozNote}
    <p class="small det soz-note" data-wc-soznotu><Icon name="info" size={14} /><span><span class="lbl">{t('saSozNote')}:</span> {sozNote}</span></p>
  {/if}
  {#if quiet && facts}
    <p class="small muted facts" data-session-facts>
      {#if facts.parts.length}<span>{facts.parts.join(' · ')}</span>{/if}
      {#if facts.conf}<span class="conf" class:soft={facts.conf.soft} data-confidence={sess?.stateConfidence}>{facts.conf.text}</span>{/if}
    </p>
  {/if}
  {#if terminal && !asking}
    <div class="qacts" role="group" aria-label={t('attActionsLabel')}>
      <button type="button" class="btn btn-sm" onclick={openTerminalWin} data-focus-live><Icon name="terminal" size={14} />{t('suAnOpenWin')}</button>
    </div>
  {/if}

  {#if buttons.length === 0 && !canUndo && item.kind !== 'uncertain' && !terminal}<p class="small muted" data-no-buttons>{t('attNoButtons')}</p>{/if}
  {#if buttons.length > 0 || canUndo}
  <div class="acts" role="group" aria-label={t('attActionsLabel')}>
    {#each buttons as bt (bt.key)}
      <button type="button" class="btn btn-sm" class:btn-primary={bt.role === 'primary' && !flat} class:btn-quiet={bt.role === 'quiet'} onclick={bt.onclick} disabled={busy && !!bt.action} aria-expanded={bt.expanded} data-action={bt.action?.id} data-open-job={bt.key === 'open' ? '' : undefined} data-role={bt.role}>{bt.label}</button>
    {/each}
    {#if canUndo}
      <span class="grow" aria-hidden="true"></span>
      <button type="button" class="btn btn-quiet btn-sm undo-btn" bind:this={undoBtn} aria-expanded={showUndo} onclick={() => { showUndo = true; undoOpen++ }} data-undo-from-attention><Icon name="undo" size={14} />{t('undoOpen')}</button>
    {/if}
  </div>
  {/if}

  {#if replying && reply}
    <form class="reply" onsubmit={(e) => { e.preventDefault(); sendReply() }}>
      <label class="sr-only" for="ar-{item.id}">{t('replyLabel')}</label>
      <textarea id="ar-{item.id}" class="textarea" rows="2" bind:this={replyEl} bind:value={replyText} placeholder={t('replyAnswer')}
        onkeydown={(e) => {
          if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); sendReply() }
          else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); replying = false }
        }}></textarea>
      <button type="submit" class="btn btn-sm" class:btn-primary={!flat} disabled={busy || !replyText.trim()}>{t('replySend')}</button>
    </form>
  {/if}

  {#if showUndo && item.jobId != null}<div class="undo"><ProtectionBar jobId={item.jobId} autoPreview={undoOpen} noOpen onclose={() => { showUndo = false; void tick().then(() => undoBtn?.focus()) }} /></div>{/if}
  {#if item.sessionId != null}<ExplainBox sessionId={item.sessionId} />{/if}
  <HataSatiri h={error} data-attention-error />
</li>
{/if}

<style>
  .arow { --c: var(--idle); }
  .k-ask, .k-accept { --c: var(--waiting); }
  .k-blocked { --c: var(--stalled); }
  .k-done { --c: var(--ok); }
  .k-uncertain { --c: var(--idle); }
  .k-uncertain::before { background: repeating-linear-gradient(to bottom, var(--c) 0 6px, transparent 6px 10px); }
  .quiet .sent { font-weight: var(--fw-regular); color: var(--ink-2); }
  .top .tag { display: inline-flex; align-items: center; gap: 4px; margin-left: auto; padding: 0 8px; border-radius: var(--radius-sm); background: var(--bg-sunken); cursor: default; }
  .top .tag + :global(time) { margin-left: 0; }
  .qcopy { display: flex; flex-direction: column; align-items: flex-start; gap: 8px; }
  .qacts { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
  .qcopy blockquote { margin: 0; padding: 2px 0 2px 12px; border-left: 2px solid var(--line-strong); }
  .det { color: var(--ink-2); max-width: 64ch; }
  .soz-note { display: flex; align-items: flex-start; gap: 6px; margin: 0; overflow-wrap: anywhere; }
  .soz-note :global(svg) { flex: none; margin-top: 3px; color: var(--ink-3); }
  .soz-note .lbl { font-weight: var(--fw-strong); color: var(--ink); }
  .facts { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; }
  .conf { padding: 0 6px; border-radius: var(--radius-sm); background: var(--bg-sunken); cursor: default; }
  .conf.soft { opacity: 0.8; border-style: dashed; }
  .acts { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-top: 2px; }
  .grow { flex: 1 1 0; min-width: 8px; }
  /* "Geri al" yıkıcı bir eylem: ayrı durur, sessizdir; üzerine gelince uyarı rengine döner. */
  .undo-btn { color: var(--ink-2); }
  .undo-btn:hover { color: var(--stalled); background: var(--stalled-tint); }
  .reply { display: flex; gap: 8px; align-items: flex-end; max-width: 44rem; }
  .reply textarea { flex: 1; min-width: 0; resize: vertical; }
  .undo { max-width: 46rem; }
  @media (max-width: 559px) {
    .acts :global(.btn) { flex: 1 1 auto; }
    .acts :global(.btn-primary) { flex: 1 1 100%; }
    .grow { display: none; }
    .acts :global(.undo-btn) { flex: 1 1 100%; justify-content: flex-start; border-top: 1px solid var(--line); border-radius: 0; margin-top: 4px; }
  }
</style>
