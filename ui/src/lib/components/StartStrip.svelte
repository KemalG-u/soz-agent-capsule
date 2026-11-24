<script lang="ts">
  import { t } from '../i18n/index.svelte'
  import { toast } from '../store.svelte'
  import { chosenOption, currentProject, loadSummary, sf, startDraft } from '../surface.svelte'
  import Icon from './Icon.svelte'

  // İş verme ekranının altındaki sakin şerit: (1) seçilen ajan hazır mı, (2) aracın dürüst sınırı, (3) klasörde zaten ölçüt varsa tek satırlık özet ve "Değiştir".
  // Hepsi sunucunun cümleleridir (GET /api/agents/ready, GET /api/done/summary). Ajan hazır değilse Başlat engellenmez, yalnız uyarılır.
  const proj = $derived(currentProject())
  const chosen = $derived(chosenOption())
  const ready = $derived(chosen?.ready ?? null)
  const bad = $derived(!!ready && !ready.canStart)
  const sum = $derived(proj ? (sf.summary[proj.id] ?? null) : null)
  const compact = $derived(!!sum && !sum.needsFullCard)

  // Klasör değişince ya da ölçüt sayısı değişince özet yenilenir.
  $effect(() => {
    const id = proj?.id
    void (id !== undefined ? sf.checks[id]?.length : 0)
    if (id !== undefined) void loadSummary(id)
  })

  // Sunucunun cümlesi teknik olabilir ("kalıcı ölçüt" gibi): sayıdan sade cümle kurulur, sayı yoksa sunucunun cümlesi kalır.
  function summaryLine(s: { permanentCount?: number; line: string }): string {
    const n = s.permanentCount ?? 0
    if (n <= 0) return s.line
    return n === 1 ? t('stripChecks1') : t('stripChecksN', { n })
  }

  function changeDone() {
    if (!sf.draft.trim()) {
      sf.focusNew++
      toast(t('doneChangeFirst'))
      return
    }
    sf.forceFull = true
    void startDraft()
  }
</script>

{#if ready || compact || chosen?.limit || chosen?.stopNote}
  <div class="strip" data-strip>
    {#if ready}
      <p class="small sl" class:bad id="ready-says" data-ready data-ready-status={ready.status} data-can-start={ready.canStart ? '1' : '0'}>
        <Icon name={bad ? 'alert' : 'check'} size={14} strokeWidth={2} /><span>{ready.sentence}{#if ready.action && bad}{' '}<span class="act-line" data-ready-action>{ready.action}</span>{/if}</span>
      </p>
    {/if}
    {#if chosen?.limit}<p class="small sl muted" data-agent-limit><Icon name="info" size={14} /><span>{chosen.limit}</span></p>{/if}
    {#if chosen?.stopNote}<p class="small sl muted" data-agent-stop><Icon name="info" size={14} /><span>{chosen.stopNote}</span></p>{/if}
    {#if compact && sum}
      <p class="small sl" data-summary-line><Icon name="check" size={14} strokeWidth={2} /><span>{summaryLine(sum)} <button type="button" class="linkbtn" onclick={changeDone} data-summary-change>{t('stripChange')}</button></span></p>
    {/if}
  </div>
{/if}

<style>
  .strip { display: flex; flex-direction: column; gap: 2px; margin: 8px 4px 0; max-width: 72ch; }
  .sl { display: flex; align-items: flex-start; gap: 8px; color: var(--ink-2); }
  .sl :global(svg) { flex: none; margin-top: 2px; }
  .sl.bad { color: var(--ink); font-weight: var(--fw-medium); }
  .sl.bad :global(svg) { color: var(--waiting); }
  .act-line { font-weight: var(--fw-regular); color: var(--ink-2); }
  .linkbtn { display: inline-flex; align-items: center; min-height: 32px; padding: 0 6px; border: 0; background: none; color: var(--ink); font: inherit; text-decoration: underline; text-underline-offset: 2px; cursor: pointer; }
  @media (pointer: coarse) and (hover: none) { .linkbtn { min-height: 40px; } }
  .linkbtn:hover { text-decoration-thickness: 2px; }
</style>
