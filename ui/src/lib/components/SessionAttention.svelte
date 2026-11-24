<script module lang="ts">
  import type { AttentionItem } from '../types5'
  /** Söz dışında, terminalde ya da başka bir pencerede açık yardımcıdan gelen öğe (sunucu reason alanını session_ ile başlatır). */
  export const isSessionItem = (i: Pick<AttentionItem, 'reason'>): boolean => (i.reason ?? '').startsWith('session_')
</script>

<script lang="ts">
  import { runAttentionAction } from '../api5'
  import { loadAttention } from '../attention.svelte'
  import { hata, sadeMi, type Hata } from '../hata'
  /** Sunucunun bildirim cümlesi yalnız sade Türkçeyse gösterilir; değilse arayüzün kendi cümlesi çıkar. */
  const sadeMesaj = (m: string | null | undefined): string => (m && sadeMi(m) ? m.trim() : '')
  import HataSatiri from './HataSatiri.svelte'
  import { t, type Key } from '../i18n/index.svelte'
  import { focusToastText } from '../profil'
  import { go } from '../router.svelte'
  import { app, toast } from '../store.svelte'
  import { refreshSurface, sf } from '../surface.svelte'
  import type { AttentionAction } from '../types5'
  import { describeWaiting } from '../waiting'
  import { ago } from '../util'
  import ChecksPanel from './ChecksPanel.svelte'
  import Icon, { type IconName } from './Icon.svelte'
  import OturumOzet from './OturumOzet.svelte'
  import WaitCard from './WaitCard.svelte'

  // flat: Şu An gibi ekranda zaten birincil düğme (Başlat) varken kart düğmeleri ikincil çizilir (O9).
  let { item, flat = false }: { item: AttentionItem; flat?: boolean } = $props()

  // Beş tür, beşi ayrı kısa ad, simge ve sade cümle. Tanınmayan tür çökmeden genel satır olur (sunucunun cümlesiyle).
  type Spec = { label: Key; icon: IconName; cls: string; sent: Key | null }
  const SPECS: Record<string, Spec> = {
    session_question: { label: 'saQuestion', icon: 'question', cls: 'chip-waiting', sent: 'saQuestionSent' },
    session_permission: { label: 'saPermission', icon: 'lock', cls: 'chip-waiting', sent: 'saPermissionSent' },
    session_input: { label: 'saInput', icon: 'question', cls: 'chip-waiting', sent: 'saInputSent' },
    session_waiting: { label: 'saWaiting', icon: 'waiting', cls: 'chip-waiting', sent: 'saWaitingSent' },
    session_failed: { label: 'saFailed', icon: 'alert', cls: 'chip-stalled', sent: null },
    // Bitti dedi ama Söz denemedi: yeşil güvence işareti yok, sakin gri (gerçekten denenip geçince iş sayfasında yeşil olur).
    session_done: { label: 'saDone', icon: 'check', cls: 'chip-neutral', sent: 'saDoneSent' },
    // Durdu ama bitti denmedi: yeşil tik yok, belirsiz (kesikli daire) görünümü. Söz bitip bitmediğini bilmez, sırayı kişiye verir.
    session_turn_end: { label: 'saTurnEnd', icon: 'unchecked', cls: 'chip-unproven', sent: 'saTurnEndSent' },
  }
  const spec = $derived<Spec>(SPECS[item.reason] ?? { label: 'attOtherKind', icon: 'info', cls: 'chip-neutral', sent: null })
  const helper = $derived(item.helper?.trim() || t('saSomeHelper'))
  // "hata yüzünden durdu: kullanım sınırı doldu" cümlesinden nedeni al; yoksa nedensiz söyle.
  const cause = $derived.by(() => {
    const m = /durdu\s*:\s*(.+?)\.?\s*$/.exec(item.sentence ?? '')
    return m ? m[1].trim() : ''
  })
  // Yardımcının ham metni (ör. "approve Edit: hesap.js") sade cümleye çevrilir; tanınmayan metin "yardımcının kendi sözü" olarak ayrı durur.
  const isQuestion = $derived(item.reason === 'session_question')
  const rawDetail = $derived(item.detail?.trim() || '')
  const info = $derived(rawDetail && !isQuestion ? describeWaiting(rawDetail, item.helper?.trim() || null, app.lang === 'en' ? 'en' : 'tr') : null)
  const sentence = $derived(
    item.reason === 'session_failed' ? (cause ? t('saFailedSentCause', { h: helper, c: cause }) : t('saFailedSent', { h: helper }))
    : info?.sentence ? info.sentence
    : spec.sent ? t(spec.sent, { h: helper })
    : item.sentence,
  )
  const quote = $derived(isQuestion ? (rawDetail || null) : info ? (info.quote ?? (info.sentence ? null : rawDetail)) : null)
  const quoteLabel = $derived(isQuestion ? t('saAskedLabel') : t('waitOwnWords'))
  const target = $derived(info?.target ?? null)
  const danger = $derived(!!info?.danger)
  const isDone = $derived(item.reason === 'session_done')
  // Söz bu işi deneyemiyorsa (klasörde kontrol yok ya da klasör kayıtlı değil) "Bu işi dene" düğmesi hiç çıkmaz; yerine Söz'ün notu ve "Nasıl denerim?" gelir.
  const cannotProve = $derived(isDone && item.canProve === false)
  const actions = $derived(cannotProve ? item.actions.filter((a) => a.id !== 'prove') : item.actions)
  const sozNote = $derived(item.sozNotu?.trim() || '')
  const known = (a: AttentionAction) => a.id === 'go' || a.id === 'prove'
  // Tek birincil düğme: bekleyen ve hata satırında "o pencereye git", biten satırda "bu işi dene" (denenemiyorsa "Nasıl denerim?").
  const primaryId = $derived(isDone ? (cannotProve ? 'how' : 'prove') : 'go')

  // "Nasıl denerim?": klasör Söz'de kayıtlıysa kontrol ekleme paneli kartın içinde açılır; kayıtlı değilse en yakın yer olan Projelerim ekranına götürür.
  const sameName = (a: string | null | undefined, b: string | null | undefined) => !!a && !!b && a.trim().replace(/[\/]+$/, '').toLowerCase() === b.trim().replace(/[\/]+$/, '').toLowerCase()
  const project = $derived(sf.projects.find((p) => sameName(p.path, item.projectPath)) ?? sf.projects.find((p) => !!item.projectName && p.name === item.projectName) ?? null)
  let howOpen = $state(false)
  function how() {
    if (project) howOpen = !howOpen
    else go('projelerim')
  }
  const label = (a: AttentionAction) => (a.id === 'go' ? t('waitGo') : a.id === 'prove' ? t('saProve') : a.label)
  const aIcon = (a: AttentionAction): IconName => (a.id === 'go' ? 'terminal' : a.id === 'prove' ? 'shield' : 'info')

  let busy = $state('')
  let error = $state<Hata | null>(null)
  let note = $state('')
  let infoOpen = $state(false)

  // İzin kartında ne için izin istendiği bilinmiyorsa uydurulmaz: açıkça söylenir (C8).
  const permUnknown = $derived(item.reason === 'session_permission' && !target && !(info?.sentence && info.kind !== 'permission' && info.kind !== 'plain'))
  const projectLine = $derived(item.projectPath?.trim() || '')
  const goAction = $derived(item.actions.find((a) => a.id === 'go') ?? null)
  // Kartın gövdesine tıklamak "O pencereye git" ile aynı işi yapar; o düğme yoksa elde olan bilgi kartın içinde açılır (C1).
  function openBody() {
    if (goAction) void run(goAction)
    else infoOpen = !infoOpen
  }

  async function run(a: AttentionAction) {
    if (busy) return
    busy = a.id
    error = null
    note = ''
    try {
      const res = (await runAttentionAction(a)) as { message?: string; tabSelected?: boolean; hint?: string | null; deliveryNote?: string | null } | null
      if (a.id === 'go') toast(focusToastText({ tabSelected: res?.tabSelected === true, hint: res?.hint },(item.helper ?? 'claude').split(/\s+/)[0]))
      else if (a.id === 'prove') toast(sadeMesaj(res?.message) || t('saProveStarted'))
      else toast(sadeMesaj(res?.message) || t('attActionDone', { a: a.label }))
      if (res?.deliveryNote?.trim()) note = res.deliveryNote.trim()
      await loadAttention()
      refreshSurface()
    } catch (e) {
      // Sunucunun sade cümlesi ("Bu projede neyle sınayacağımı bilmiyorum.") korunur; ham ya da teknik metin ekrana çıkmaz (lib/hata.ts).
      error = hata(e, 'pencere')
      if (error.refresh) { void loadAttention(); refreshSurface() }
    } finally {
      busy = ''
    }
  }
</script>

<WaitCard
  label={t(spec.label)}
  icon={spec.icon}
  chipCls={spec.cls}
  project={item.projectName ?? ''}
  helper={item.helper ?? ''}
  ago={ago(item.at)}
  {sentence}
  {target}
  {quote}
  {quoteLabel}
  {danger}
  done={isDone}
  onopen={openBody}
  openHint={goAction ? t('saBodyGo') : t('saBodyInfo')}
  kind={item.kind}
  data-attention={item.id}
  data-kind={item.kind}
  data-reason={item.reason}
  data-priority={item.priority}
  data-session-item
  data-attention-session={item.externalSessionId ?? (item.sessionId != null ? String(item.sessionId) : undefined)}
>
  {#if permUnknown}
    <p class="wc-note" data-sa-perm-unknown><Icon name="info" size={14} /><span>{t('saPermUnknown')}</span></p>
  {/if}
  {#if sozNote}
    <p class="wc-note" data-wc-soznotu><Icon name="info" size={14} /><span><span class="lbl">{t('saSozNote')}:</span> {sozNote}</span></p>
  {/if}
  {#if item.sessionId != null}
    <OturumOzet sozId={item.sessionId} waitKind={item.reason === 'session_permission' ? 'permission' : item.reason === 'session_question' ? 'question' : item.reason === 'session_input' ? 'input' : item.reason === 'session_waiting' ? 'waiting' : null} />
  {/if}
  {#if actions.length > 0 || cannotProve}
    <div class="wc-acts" role="group" aria-label={t('attActionsLabel')}>
      {#each actions as a (a.id)}
        <button type="button" class="btn btn-sm" class:btn-primary={!flat && a.id === primaryId} disabled={!!busy} onclick={() => void run(a)} data-action={a.id} data-known={known(a) ? '1' : '0'}>
          <Icon name={aIcon(a)} size={14} />{label(a)}
        </button>
      {/each}
      {#if cannotProve}
        <button type="button" class="btn btn-sm" class:btn-primary={!flat && primaryId === 'how'} aria-expanded={project ? howOpen : undefined} onclick={how} data-action="how" data-known="1">
          <Icon name="shield" size={14} />{t('saHowTry')}
        </button>
      {/if}
    </div>
  {/if}
  {#if howOpen && project}
    <div class="sa-how" data-sa-how>
      <p class="small muted">{t('saHowTryLead')}</p>
      <ChecksPanel projectId={project.id} onchange={() => { void loadAttention() }} />
    </div>
  {/if}
  {#if infoOpen}
    <dl class="sa-info" data-sa-info>
      <dt>{t('saInfoHelper')}</dt><dd>{helper}</dd>
      {#if projectLine}<dt>{t('saInfoFolder')}</dt><dd>{projectLine}</dd>{/if}
      {#if item.at}<dt>{t('saInfoWhen')}</dt><dd>{ago(item.at)}</dd>{/if}
      {#if rawDetail}<dt>{quoteLabel}</dt><dd>{rawDetail}</dd>{/if}
    </dl>
  {/if}
  <HataSatiri h={error} data-sa-error />
  {#if note}<p class="small muted" data-sa-note>{note}</p>{/if}
</WaitCard>

<style>
  /* Söz'ün kendi notu: yardımcının sözünden (alıntı kutusu) ayrı, düz satır ve kendi etiketiyle. */
  .wc-note { display: flex; align-items: flex-start; gap: 6px; margin: 0; max-width: 64ch; color: var(--ink-2); font-size: var(--fs-sm); line-height: 1.45; overflow-wrap: anywhere; }
  .wc-note :global(svg) { flex: none; margin-top: 3px; color: var(--ink-3); }
  .wc-note .lbl { font-weight: var(--fw-strong); color: var(--ink); }
  .sa-how { display: flex; flex-direction: column; gap: 8px; padding: 10px 12px; border: 1px solid var(--line); border-radius: var(--radius); background: var(--bg); max-width: 46rem; }
  .sa-how p { margin: 0; }
  .sa-info { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 2px 12px; margin: 0; padding: 8px 12px; border-radius: var(--radius); background: var(--bg-sunken); font-size: var(--fs-xs); color: var(--ink-2); }
  .sa-info dt { color: var(--ink-3); }
  .sa-info dd { margin: 0; overflow-wrap: anywhere; color: var(--ink); }
</style>
