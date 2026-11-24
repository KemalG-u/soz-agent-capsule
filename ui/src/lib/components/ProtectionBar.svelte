<script lang="ts">
  import { hataMetni } from '../hata'
  import { tick } from 'svelte'
  import { applyUndo, getProtection, getUndoHistory, previewUndo, revertUndo } from '../api5'
  import { t } from '../i18n/index.svelte'
  import { app, toast } from '../store.svelte'
  import type { Protection, UndoRecord, UndoReply } from '../types5'
  import { ago } from '../util'
  import Icon from './Icon.svelte'
  import { geriAlGorunur, korumaNotuGorunur } from '../gezinme'

  // Koruma rozeti ve "Geri al": önce ÖNİZLEME (hangi dosya eski hâline döner, çakışma var mı), sonra onay. Geri almanın kendisi de geri alınır.
  // Söz bir kasa değil özen katmanıdır: geri alınamayanlar her zaman yazılıdır.
  // noOpen: "Geri al" düğmesi bu bileşenin dışında (Sıra bende satırı); burada ikinci bir düğme göstermeyiz. Önizleme kapanınca (Vazgeç, Esc, geri alındı) onclose çağrılır, odak o düğmeye döner.
  // Listede kişiye görünen sayı ile başlıktaki sayı aynıdır: ikisi de önizlemedeki satırlardan gelir; uzun liste "N tane daha" ile açılır, iç kaydırma kutusu yoktur (satır gizlenmesin).
  // degisiklikYok: iş klasörde değişiklik yapmadı (O6), "Geri al" ve yeşil not görünmez. calisiyor: iş sürüyor (D10), "Geri al" görünmez.
  let { jobId, autoPreview = 0, noOpen = false, onclose, degisiklikYok = false, calisiyor = false }: { jobId: number; autoPreview?: number; noOpen?: boolean; onclose?: () => void; degisiklikYok?: boolean; calisiyor?: boolean } = $props()

  let prot = $state<Protection | null>(null)
  let loadErr = $state('')
  let hist = $state<UndoRecord[]>([])
  let preview = $state<UndoReply | null>(null)
  let result = $state<UndoReply | null>(null)
  let busy = $state(false)
  let error = $state('')
  let showLimits = $state(false)
  let panel: HTMLElement | undefined = $state()
  const SHOW = 10 // ilk görünen satır
  const SHOW_MAX = 200 // açılınca en çok bu kadar satır çizilir
  let all = $state(false)
  let openBtn: HTMLButtonElement | undefined = $state()
  let okEl: HTMLElement | undefined = $state()

  async function load() {
    try {
      prot = await getProtection(jobId)
      loadErr = ''
    } catch (e) {
      prot = null
      loadErr = hataMetni(e)
    }
    try {
      hist = (await getUndoHistory(jobId)).undo
    } catch {
      hist = []
    }
  }

  // İş değişince önizleme ve sonuç sıfırlanır; canlı olay gelince yalnız koruma bilgisi tazelenir (açık önizleme kapanmaz).
  $effect(() => {
    void jobId
    preview = null
    result = null
    error = ''
  })
  $effect(() => {
    void jobId
    void app.jobTick
    void load()
  })

  // Dışarıdan ("Geri al" düğmesi başka yerden basıldı) önizleme isteği.
  let seen = 0
  $effect(() => {
    if (autoPreview > seen) {
      seen = autoPreview
      void openPreview()
    }
  })

  const lastApplied = $derived(hist.find((h) => h.status === 'applied') ?? null)
  const itemsShown = $derived(preview ? preview.items.slice(0, all ? SHOW_MAX : SHOW) : [])
  const hidden = $derived(preview ? Math.max(0, preview.items.length - itemsShown.length) : 0)
  const text = (c: string | { path?: string; text?: string; why?: string }): string => (typeof c === 'string' ? c : [c.path, c.text ?? c.why].filter(Boolean).join(': '))
  const hasConflicts = $derived((preview?.conflicts.length ?? 0) > 0)
  const undoVisible = $derived(!!prot && geriAlGorunur({ canUndo: prot.canUndo, calisiyor, degisiklikYok }))
  const noteVisible = $derived(!!prot && korumaNotuGorunur({ level: prot.level, degisiklikYok }))
  const chipText = $derived(!prot ? '' : prot.level === 'tam' ? t('protFull') : prot.level === 'sinirli' ? t('protLimited') : t('protNone'))

  async function openPreview() {
    if (busy) return
    busy = true
    error = ''
    result = null
    try {
      preview = await previewUndo(jobId)
      all = false
      await tick()
      panel?.focus()
    } catch (e) {
      error = hataMetni(e)
    } finally {
      busy = false
    }
  }

  // "N tane daha" ve "Daha az göster" düğmeleri birbirinin yerine geçer; basılan düğme kalkınca odak kaybolmasın, karşıdaki düğmeye geçsin.
  async function setAll(v: boolean) {
    all = v
    await tick()
    panel?.querySelector<HTMLElement>(v ? '[data-undo-less]' : '[data-undo-more]')?.focus()
  }

  function closePreview() {
    preview = null
    if (onclose) onclose()
    else void tick().then(() => openBtn?.focus())
  }

  async function apply(force = false) {
    if (!preview || busy) return
    busy = true
    error = ''
    try {
      result = await applyUndo(jobId, preview.undoUid, force)
      preview = null
      toast(result.summary)
      app.jobTick++
      await load()
      await tick()
      if (okEl) okEl.focus()
      else onclose?.()
    } catch (e) {
      error = hataMetni(e)
    } finally {
      busy = false
    }
  }

  async function revert(uid: string, force = false) {
    if (busy) return
    busy = true
    error = ''
    try {
      result = await revertUndo(uid, force)
      toast(result.summary)
      app.jobTick++
      await load()
      await tick()
      okEl?.focus()
    } catch (e) {
      error = hataMetni(e)
    } finally {
      busy = false
    }
  }
</script>

{#if prot && (noteVisible || noOpen || preview || result || lastApplied || error)}
  <div class="prot lv-{prot.level}" data-protection={prot.level}>
    {#if noteVisible}
      <!-- A3: geri alma notu ile "Geri al" tek satırda; uzun açıklama "Neler geri alınamaz?" altında. -->
      <div class="line">
        <span class="pair">
          <span class="chip {prot.level === 'tam' ? 'chip-ok' : prot.level === 'sinirli' ? 'chip-waiting' : 'chip-stalled'}" title={prot.level === 'yok' ? chipText : chipText + ' ' + prot.text} data-protection-chip>
            <Icon name="shield" size={14} /><span class="ctxt">{prot.level === 'tam' ? t('protFullShort') : chipText}</span>
          </span>
          {#if undoVisible && !preview && !noOpen}<button type="button" class="btn btn-sm undo" bind:this={openBtn} onclick={openPreview} disabled={busy} data-undo-open><Icon name="undo" size={14} />{t('undoOpen')}</button>{/if}
        </span>
        <button type="button" class="btn btn-quiet btn-sm" aria-expanded={showLimits} onclick={() => (showLimits = !showLimits)} data-limits-btn>{t('protLimitsBtn')}</button>
      </div>
      {#if prot.level === 'yok'}<p class="small ptxt">{prot.note ?? prot.cannotUndoWhy ?? prot.text}</p>{/if}
      {#if !prot.canUndo && prot.level !== 'yok' && prot.cannotUndoWhy}<p class="small muted">{prot.cannotUndoWhy}</p>{/if}
      {#if showLimits}
        <div class="small muted limits" data-limits>
          {#if prot.level !== 'yok'}<p>{prot.text}</p>{/if}
          {#if prot.level === 'sinirli' && prot.note}<p>{prot.note}</p>{/if}
          <p>{prot.limits}</p>
        </div>
      {/if}
    {/if}

    {#if preview}
      <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
      <section class="pv" bind:this={panel} tabindex="-1" aria-label={t('undoPreviewTitle')} data-undo-preview onkeydown={(e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closePreview() } }}>
        <p class="pt"><Icon name="undo" size={16} />{t('undoPreviewTitle')}</p>
        <p class="small" data-undo-count>{preview.items.length === 0 ? t('undoNothing') : t('undoCount', { n: preview.items.length })}</p>
        {#if preview.items.length > 0}
          <ul class="items" aria-label={t('undoListLabel')}>
            {#each itemsShown as it, i (i)}
              <li data-undo-item><span class="mono path">{it.path}</span><span class="small muted">{it.text}</span></li>
            {/each}
          </ul>
          {#if hidden > 0}
            <p class="small more"><button type="button" class="btn btn-quiet btn-sm" onclick={() => setAll(true)} data-undo-more>{t('undoShowMore', { n: hidden })}</button></p>
          {:else if all && preview.items.length > SHOW_MAX}
            <p class="small muted">{t('undoMore', { n: preview.items.length - SHOW_MAX })}</p>
          {:else if all && preview.items.length > SHOW}
            <p class="small more"><button type="button" class="btn btn-quiet btn-sm" onclick={() => setAll(false)} data-undo-less>{t('undoShowLess')}</button></p>
          {/if}
        {/if}
        {#if hasConflicts}
          <div class="warnbox small" role="alert" data-undo-conflicts>
            <Icon name="alert" size={14} />
            <div>
              <strong>{t('undoConflictTitle')}</strong>
              <ul>{#each preview.conflicts as c, i (i)}<li>{text(c)}</li>{/each}</ul>
            </div>
          </div>
        {/if}
        {#if preview.unrestorable.length > 0}
          <p class="small muted">{t('undoUnrestorable')}</p>
          <ul class="small muted">{#each preview.unrestorable as c, i (i)}<li>{text(c)}</li>{/each}</ul>
        {/if}
        {#each preview.notes as n (n)}<p class="small muted">{n}</p>{/each}
        <div class="acts">
          {#if hasConflicts}
            <button type="button" class="btn btn-sm" onclick={() => apply(true)} disabled={busy} data-undo-force>{t('undoForce')}</button>
          {:else}
            <button type="button" class="btn btn-primary btn-sm" onclick={() => apply(false)} disabled={busy || preview.items.length === 0} data-undo-apply>{t('undoApply')}</button>
          {/if}
          <button type="button" class="btn btn-quiet btn-sm" onclick={closePreview} data-undo-cancel>{t('cancel')} <kbd>Esc</kbd></button>
        </div>
      </section>
    {/if}

    {#if result && !preview}
      <p class="small okline" role="status" tabindex="-1" bind:this={okEl} data-undo-result><Icon name="check" size={14} strokeWidth={2.2} />{result.summary}</p>
    {/if}

    {#if lastApplied && !preview}
      <div class="line again" data-undo-done>
        <span class="small muted">{t('undoWasDone', { when: ago(lastApplied.finishedAt ?? lastApplied.startedAt) })}</span>
        <button type="button" class="btn btn-quiet btn-sm" onclick={() => revert(lastApplied.uid)} disabled={busy} data-undo-revert>{t('undoRevert')}</button>
      </div>
    {/if}

    {#if error}<p class="small err-line" role="alert" data-undo-error>{error}</p>{/if}
  </div>
{:else if loadErr && !/bulunamadı|yok/i.test(loadErr)}
  <p class="small muted">{t('protUnknown')}</p>
{/if}

<style>
  .prot { display: flex; flex-direction: column; gap: 6px; }
  .line { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; }
  .pair { display: flex; flex-wrap: nowrap; align-items: center; gap: 8px; min-width: 0; max-width: 100%; }
  .pair .chip { min-width: 0; overflow: hidden; }
  .ctxt { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .pair .undo { flex: none; }
  .ptxt { color: var(--ink-2); margin: 0; }
  .limits { display: flex; flex-direction: column; gap: 4px; max-width: 60ch; padding: 6px 10px; border-radius: var(--radius); background: var(--bg-sunken); }
  .limits p { margin: 0; }
  .pv { display: flex; flex-direction: column; gap: 8px; padding: 12px 14px; border: 1px solid var(--line-strong); border-left: 3px solid var(--working); border-radius: var(--radius-lg); background: var(--surface); max-width: 44rem; outline: none; }
  .pv:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
  .pt { display: flex; align-items: center; gap: 8px; font-weight: var(--fw-strong); }
  .items { display: flex; flex-direction: column; border: 1px solid var(--line); border-radius: var(--radius); }
  .more { margin: -2px 0 0; }
  .okline { outline: none; }
  .items li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 2px 12px; padding: 6px 10px; border-top: 1px solid var(--line); }
  .items li:first-child { border-top: 0; }
  .path { overflow-wrap: anywhere; }
  .acts { display: flex; flex-wrap: wrap; gap: 8px; }
  .warnbox { display: flex; gap: 8px; padding: 8px 12px; border-radius: var(--radius); background: var(--waiting-tint); border-left: 3px solid var(--waiting); }
  .warnbox :global(svg) { flex: none; margin-top: 2px; color: var(--waiting); }
  .okline { display: flex; align-items: center; gap: 6px; color: var(--ok); font-weight: var(--fw-medium); }
</style>
