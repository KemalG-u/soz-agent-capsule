<script lang="ts">
  import type { Snippet } from 'svelte'
  import { t } from '../i18n/index.svelte'
  import Icon, { type IconName } from './Icon.svelte'

  // Şu An > Benden Beklenen: bekleyen her öğe bu TEK biçimle çizilir.
  // Sıra: tür rozeti, proje, yardımcı, ne kadar oldu, tek sade cümle, (komut/yol küçük satır), (yardımcının kendi sözü), eylem.
  // Silme gibi geri alınamayabilecek işte `danger`: kırmızı çerçeve ve "Dikkat" notu.
  let {
    label, icon, chipCls = 'chip-waiting', project = '', helper = '', ago = '', sentence, target = null, quote = null, quoteLabel = '', danger = false, done = false, kind = 'ask', onopen = undefined, openHint = '', children, ...rest
  }: {
    label: string
    icon: IconName
    chipCls?: string
    project?: string
    helper?: string
    ago?: string
    sentence: string
    target?: string | null
    quote?: string | null
    quoteLabel?: string
    danger?: boolean
    done?: boolean
    kind?: string
    /** Kartın gövdesine (düğme ve bağlantı dışı yere) tıklanınca yapılacak iş: ölü tıklama kalmasın (C1). Klavye karşılığı kartın kendi düğmesidir. */
    onopen?: (() => void) | undefined
    openHint?: string
    children?: Snippet
    [k: string]: unknown
  } = $props()
</script>

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
<li class="wc k-{kind}" class:danger class:done class:clickable={!!onopen} title={onopen ? openHint || undefined : undefined} data-wait-card data-danger={danger ? '1' : '0'} onclick={onopen ? (e) => { if (!(e.target as HTMLElement).closest('button, a, input, textarea, select, summary, details, form, label')) onopen() } : undefined} {...rest}>
  <div class="wc-top">
    <span class="chip {danger ? 'chip-stalled' : chipCls}" data-wc-label><Icon name={icon} size={14} strokeWidth={2} />{label}</span>
    {#if project}<span class="wc-proj" data-wc-project>{project}</span>{/if}
    {#if helper}<span class="tool-badge" data-wc-helper>{helper}</span>{/if}
    {#if ago}<time class="wc-time" data-wc-ago>{ago}</time>{/if}
  </div>
  <p class="wc-sent" data-wc-sentence>{sentence}</p>
  {#if danger}<p class="wc-danger" data-wc-danger><Icon name="alert" size={15} strokeWidth={2} />{t('waitDanger')}</p>{/if}
  {#if target}<p class="wc-target" data-wc-target><code>{target}</code></p>{/if}
  {#if quote}<p class="wc-quote" data-wc-quote><span class="lbl">{quoteLabel || t('waitOwnWords')}:</span> “{quote}”</p>{/if}
  {@render children?.()}
</li>

