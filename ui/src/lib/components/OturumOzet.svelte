<script module lang="ts">
  import type { OturumOzet } from '../types5'
  // Aynı özet birkaç yerde çizilir (kart, canlı satır, Projelerim); yeniden çizimde boş yanıp sönmesin diye son bilinen cevap tutulur.
  const cache = new Map<string, OturumOzet>()
  const PENDING_MS = 3000 // "hazırlanıyor" cevabında yeniden sorma aralığı
  const MAX_PENDING = 4 // en çok bu kadar yeniden sor, sonra dürüstçe "ulaşamıyorum" de (sonsuz döngü yok)
  const REFRESH_MS = 30000 // açık özet bu sürede bir tazelenir (yalnız ekran görünürken)
</script>

<script lang="ts">
  import { untrack } from 'svelte'
  import { getOzetById, getOzetByTool } from '../api5'
  import { ApiError } from '../errors'
  import { t } from '../i18n/index.svelte'
  import { isVisible } from '../visible'
  import { kirp } from '../kirp'
  import Icon from './Icon.svelte'

  // Üç satırlık oturum özeti: "Ne yaptı", "Denedi mi", "Senden ne bekleniyor". Cümleleri sunucu kurar; burada süslenmez, kısaltılmaz.
  // Kimlik: Söz oturum numarası (sozId) ya da yardımcının kendi kimliği (tool + extId). İkisi de yoksa hiçbir şey çizilmez.
  // toggle: true ise küçük "Özet" düğmesi çizilir ve özet tıklayınca açılır (çalışan yardımcı satırları); false ise hemen görünür (bekleyen kartlar).
  // waitKind: kartın kendi bildiği bekleme nedeni (kanca ya da canlı pencere: izin, soru, bilgi, bekliyor). Kayıt dosyası olmayan oturumda özet "Bir şey beklemiyor" derse kartla çelişir;
  // o zaman üçüncü satırda kartın kendi sözü durur, çelişen cümle gösterilmez.
  let { sozId = null, tool = null, extId = null, toggle = false, waitKind = null }: { sozId?: number | null; tool?: string | null; extId?: string | null; toggle?: boolean; waitKind?: 'permission' | 'question' | 'input' | 'waiting' | null } = $props()

  const key = $derived(sozId != null ? `s:${sozId}` : tool && extId ? `t:${tool}:${extId}` : '')
  let open = $state(!untrack(() => toggle))
  let data = $state<OturumOzet | null>(null)
  let st = $state<'loading' | 'pending' | 'ok' | 'err' | 'gone'>('loading')

  let timer: ReturnType<typeof setTimeout> | undefined
  let tries = 0
  let alive = false

  function schedule(ms: number) {
    clearTimeout(timer)
    timer = setTimeout(() => {
      if (!alive) return
      if (!isVisible()) schedule(ms) // görünmezken istek yok
      else void load()
    }, ms)
  }

  async function load() {
    const k = key
    if (!k) return
    try {
      const r = await (sozId != null ? getOzetById(sozId) : getOzetByTool(tool ?? '', extId ?? ''))
      if (!alive || k !== key) return
      if (r && typeof r === 'object' && typeof r.neYapti === 'string') {
        data = r
        st = 'ok'
        tries = 0
        cache.set(k, r)
        schedule(REFRESH_MS)
      } else if (++tries <= MAX_PENDING) {
        // sunucu henüz hazırlamamış: kısa süre sonra yeniden sor
        if (!data) st = 'pending'
        schedule(PENDING_MS)
      } else if (!data) {
        st = 'err'
      }
    } catch (e) {
      if (!alive || k !== key) return
      // Oturumun kaydı yok (404): bu Söz'ün bozuk olduğu anlamına gelmez ve yeniden denemek işe yaramaz. Yalnız ağ ya da sunucu hatasında "ulaşamıyorum" denir.
      if (e instanceof ApiError && e.status === 404) { data = null; st = 'gone'; clearTimeout(timer); return }
      // ilk istek olmadıysa dürüstçe söyle; daha önce gösterilen özet varsa o kalır
      if (!data) st = 'err'
    }
  }

  function retry() {
    tries = 0
    st = 'loading'
    void load()
  }

  $effect(() => {
    const k = key
    if (!k || !open) return
    alive = true
    untrack(() => {
      const c = cache.get(k)
      data = c ?? null
      st = c ? 'ok' : 'loading'
      tries = 0
      void load()
    })
    return () => {
      alive = false
      clearTimeout(timer)
    }
  })

  // Denedi mi satırı uyarı görünümüne geçer: "bitti" demiş ama sonrasında hiçbir deneme yok, ya da son deneme hata vermiş.
  const warn = $derived(!!data && ((data.bittiDedi === true && data.bittiSonrasiDeneme === false) || data.sonDeneme?.sonuc === 'kaldi'))
  const waitText = $derived(waitKind === 'permission' ? t('saPermission') : waitKind === 'question' ? t('saQuestion') : waitKind === 'input' ? t('saInput') : waitKind === 'waiting' ? t('saWaiting') : '')
  const override = $derived(!!data && !!waitText && (data.bekleyen === 'yok' || data.bekleyen === undefined))
  // Düşük güven (K-01): üç satır yine görünür, altında tek sade satır "Bu özetten emin değilim: <neden>". Kayıt hiç okunamadıysa üç kez aynı cümle yerine tek cümle.
  const low = $derived(!!data && data.guven === 'dusuk')
  const norm = (s: string | null | undefined) => (s ?? '').trim()
  const unreadable = $derived(!!data && low && norm(data.neYapti) === norm(data.denediMi) && norm(data.denediMi) === norm(data.sendenBeklenen))
  const lowText = $derived(norm(data?.guvenNedeni) ? t('ozUnsure', { why: norm(data?.guvenNedeni) }) : t('ozUnsureNoWhy'))
  // Boş değer boş satır bırakmaz (K-15): "Ne yaptı" ve "Denedi mi" için sade söz; boş "Senden ne bekleniyor" satırı hiç çizilmez.
  const rows = $derived(
    data
      ? ([
          { id: 'did', k: t('ozWhat'), v: norm(data.neYapti) || t('ozNoDid'), warn: false },
          { id: 'tried', k: t('ozTried'), v: norm(data.denediMi) || t('ozNoTried'), warn },
          { id: 'expect', k: t('ozExpect'), v: override ? waitText + '.' : norm(data.sendenBeklenen), warn: false },
        ] as { id: string; k: string; v: string; warn: boolean }[]).filter((r) => r.v !== '')
      : [],
  )
  // Çok uzun özet kısaltılır, "Devamını göster" ile açılır (K-16).
  const CLIP = 240
  let wide = $state<Record<string, boolean>>({})
  const clipped = (v: string) => kirp(v, CLIP)
</script>

{#if key}
  {#if toggle}
    <button type="button" class="oz-tog" aria-expanded={open} onclick={() => (open = !open)} data-oz-toggle>
      <span class="chev" class:open aria-hidden="true"><Icon name="chevron" size={13} /></span>{open ? t('ozHide') : t('ozShow')}
    </button>
  {/if}
  {#if open}
    <div class="oz" class:in-row={toggle} role="group" aria-label={t('ozAria')} aria-busy={st === 'loading' || st === 'pending'} data-oz data-oz-state={st} data-oz-low={data?.guven === 'dusuk' ? '1' : '0'}>
      {#if st === 'ok' && data && unreadable}
        <p class="oz-v oz-one" data-oz-unreadable>{t('ozUnreadable')}</p>
        <p class="oz-low" data-oz-lowtext>{lowText}</p>
      {:else if st === 'ok' && data}
        {#each rows as r (r.id)}
          <div class="oz-row" class:warn={r.warn} data-oz-row={r.id} data-oz-warn={r.warn ? '1' : '0'} data-oz-own={r.id === 'expect' && override ? '1' : undefined}>
            <span class="oz-k">{#if r.warn}<Icon name="alert" size={13} strokeWidth={2} /><span class="sr">{t('ozWarn')}: </span>{/if}{r.k}</span>
            <span class="oz-v" class:long={r.v.length > CLIP && wide[r.id]}>{wide[r.id] || r.v.length <= CLIP ? r.v : clipped(r.v)}{#if r.v.length > CLIP}<button type="button" class="oz-more" aria-expanded={!!wide[r.id]} onclick={() => (wide[r.id] = !wide[r.id])} data-oz-more>{wide[r.id] ? t('ozLess') : t('ozMore')}</button>{/if}</span>
          </div>
        {/each}
        {#if low}<p class="oz-low" data-oz-lowtext>{lowText}</p>{/if}
      {:else if st === 'gone'}
        <p class="oz-msg" role="status" data-oz-gone>{t('ozGone')}</p>
      {:else if st === 'err'}
        <p class="oz-msg" role="alert" data-oz-fail>
          <span>{t('ozFail')}</span>
          <button type="button" class="oz-retry" onclick={retry} data-oz-retry>{t('retry')}</button>
        </p>
      {:else}
        <p class="oz-msg muted" role="status" data-oz-prep>{t('ozPrep')}</p>
      {/if}
    </div>
  {/if}
{/if}

<style>
  .oz { box-sizing: border-box; width: 100%; min-width: 0; display: flex; flex-direction: column; gap: 4px; margin: 2px 0 0; padding: 8px 12px; border-radius: var(--radius); background: var(--bg-sunken); font-size: var(--fs-sm); line-height: 1.45; }
  .oz.in-row { order: 99; width: auto; flex: 1 1 100%; margin: 0 8px 8px; }
  .oz-row { display: grid; grid-template-columns: 9.5rem minmax(0, 1fr); gap: 2px 12px; align-items: baseline; min-width: 0; }
  .oz-k { display: inline-flex; align-items: center; gap: 4px; font-size: var(--fs-xs); font-weight: var(--fw-medium); color: var(--ink-3); }
  .oz-v { color: var(--ink); overflow-wrap: anywhere; }
  .oz-row.warn { margin: 0 -6px; padding: 3px 6px; border-radius: var(--radius-sm); background: var(--waiting-tint); }
  .oz-row.warn .oz-k, .oz-row.warn .oz-v { color: var(--waiting); }
  .oz-row.warn .oz-v { font-weight: var(--fw-medium); }
  .oz-low { margin: 2px 0 0; padding: 3px 8px; border-radius: var(--radius-sm); background: var(--waiting-tint); color: var(--waiting); font-size: var(--fs-xs); font-weight: var(--fw-medium); }
  .oz-one { margin: 0; }
  .oz-v.long { display: block; max-height: 14rem; overflow-y: auto; }
  .oz-more { border: 0; background: none; padding: 0 4px; margin-left: 4px; color: var(--ink-2); font: inherit; font-size: var(--fs-xs); text-decoration: underline; cursor: pointer; }
  .oz-msg { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 0; color: var(--ink-2); }
  .oz-msg.muted { color: var(--ink-3); }
  .oz-retry { border: 0; background: none; padding: 2px 4px; color: var(--working); font: inherit; font-size: var(--fs-xs); text-decoration: underline; cursor: pointer; }
  .oz-tog { flex: none; display: inline-flex; align-items: center; gap: 4px; align-self: center; min-height: 28px; padding: 0 8px; border: 0; border-radius: var(--radius); background: none; color: var(--ink-3); font: inherit; font-size: var(--fs-xs); cursor: pointer; }
  .oz-tog:hover { background: var(--bg-sunken); color: var(--ink); }
  .chev { display: inline-flex; transform: rotate(-90deg); transition: transform 0.15s; }
  .chev.open { transform: none; }
  .sr { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
  @media (max-width: 520px) {
    .oz-row { grid-template-columns: minmax(0, 1fr); }
  }
  @media (prefers-reduced-motion: reduce) {
    .chev { transition: none; }
  }
</style>
