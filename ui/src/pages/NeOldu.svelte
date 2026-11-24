<script lang="ts">
  import { getEvents } from '../lib/api5'
  import EmptyState from '../lib/components/EmptyState.svelte'
  import Skeleton from '../lib/components/Skeleton.svelte'
  import { t, type Key } from '../lib/i18n/index.svelte'
  import { go } from '../lib/router.svelte'
  import { app } from '../lib/store.svelte'
  import type { FeedGroup, FeedLine } from '../lib/types5'
  import { errText } from '../lib/util'

  // "Ne oldu": olay defterinin okunan hali. Zaman sırası (yeni üstte), gün başlıklarıyla; tür süzgeci; boşsa tek cümle.
  // Yalnız okunur: hiçbir düğme bir şeyi değiştirmez. Cümleler sunucudan sade gelir, arayüz kod adı göstermez.
  const GROUPS: FeedGroup[] = ['job', 'guard', 'setting', 'system']
  const PAGE = 40

  let group = $state<FeedGroup | ''>('')
  let items = $state<FeedLine[]>([])
  let next = $state<string | null>(null)
  let hasMore = $state(false)
  let loaded = $state(false)
  let busy = $state(false)
  let error = $state('')
  let seq = 0

  // keep: kendiliğinden yenileme. "Daha fazla" ile açılan satırlar kaybolmaz; yalnız yeni olaylar üste eklenir (A-SAB-11).
  async function load(more: boolean, keep = false) {
    const mine = ++seq
    busy = true
    try {
      const r = await getEvents({ limit: PAGE, before: more ? next : null, kind: group || null })
      if (mine !== seq) return
      if (keep && items.length > 0) {
        const have = new Set(items.map((x) => x.id))
        const fresh = r.items.filter((x) => !have.has(x.id))
        if (fresh.length > 0) items = [...fresh, ...items]
      } else {
        items = more ? [...items, ...r.items.filter((x) => !items.some((y) => y.id === x.id))] : r.items
        next = r.next
        hasMore = r.hasMore
      }
      error = ''
    } catch (e) {
      if (mine !== seq) return
      error = errText(e)
    } finally {
      if (mine === seq) {
        busy = false
        loaded = true
      }
    }
  }

  // Süzgeç değişince ya da veri yenilenince baştan okunur.
  let lastGroup: FeedGroup | '' | null = null
  $effect(() => {
    void app.tick
    const g = group
    const changed = g !== lastGroup
    lastGroup = g
    void load(false, !changed)
  })

  const key = (g: string) => `evGroup_${g}` as Key
  // Bilinmeyen olay türü ham anahtar göstermez (B-DIGER-09): "Başka bir olay", teknik ad yalnız Teknik ayrıntı altında.
  const known = (g: string) => (GROUPS as string[]).includes(g)
  const codeLike = (s: string) => /^[a-z0-9_]+([.:][a-z0-9_]+)+$/i.test(s.trim())
  const groupText = (g: string) => (known(g) ? t(key(g)) : t('evOtherGroup'))
  const lineText = (x: FeedLine) => (!x.sentence?.trim() || codeLike(x.sentence) ? t('evOtherLine') : x.sentence)
  const techOf = (x: FeedLine) => (!known(x.group) || codeLike(x.sentence ?? '') || !x.sentence?.trim() ? [x.group, x.kind, x.sentence].filter((v, i, a) => v && a.indexOf(v) === i).join(' · ') : '')
  const pad = (n: number) => String(n).padStart(2, '0')
  const dayId = (ts: string) => {
    const d = new Date(ts)
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
  }
  const hhmm = (ts: string) => {
    const d = new Date(ts)
    return Number.isNaN(d.getTime()) ? '' : `${pad(d.getHours())}:${pad(d.getMinutes())}`
  }
  function dayTitle(ts: string) {
    const d = new Date(ts)
    const today = new Date()
    const yest = new Date(today.getTime() - 86_400_000)
    if (dayId(ts) === dayId(today.toISOString())) return t('evToday')
    if (dayId(ts) === dayId(yest.toISOString())) return t('evYesterday')
    return new Intl.DateTimeFormat(app.lang === 'en' ? 'en-GB' : 'tr-TR', { day: 'numeric', month: 'long', year: d.getFullYear() === today.getFullYear() ? undefined : 'numeric' }).format(d)
  }
  const days = $derived.by(() => {
    const out: { id: string; title: string; rows: FeedLine[] }[] = []
    for (const x of items) {
      const id = dayId(x.at)
      const last = out[out.length - 1]
      if (last && last.id === id) last.rows.push(x)
      else out.push({ id, title: dayTitle(x.at), rows: [x] })
    }
    return out
  })
</script>

<div class="page" data-neoldu>
  <nav class="subtabs" aria-label={t('navHistory')}>
    <a href="#/sabah">{t('tabHistorySummary')}</a>
    <a href="#/neoldu" aria-current="page">{t('tabHistoryEvents')}</a>
  </nav>

  <header class="page-head">
    <div>
      <h1 class="h1">{t('tabHistoryEvents')}</h1>
      <p class="lead">{t('evLead')}</p>
    </div>
  </header>

  <div class="filter" role="group" aria-label={t('evFilter')} data-events-filter>
    <button type="button" class="seg" class:on={group === ''} aria-pressed={group === ''} onclick={() => (group = '')} data-events-group="all">{t('evAll')}</button>
    {#each GROUPS as g (g)}
      <button type="button" class="seg" class:on={group === g} aria-pressed={group === g} onclick={() => (group = g)} data-events-group={g}>{t(key(g))}</button>
    {/each}
  </div>

  {#if error && !loaded}
    <p class="err-line" role="alert" data-events-error>{t('evLoadError', { m: error })}</p>
    <p class="retry-row"><button class="btn" onclick={() => load(false)}>{t('retry')}</button></p>
  {:else if !loaded}
    <Skeleton rows={5} />
  {:else if items.length === 0}
    <EmptyState id="no-events" text={group ? t('evEmptyFiltered') : t('evEmpty')} action={t('emptyGoJobs')} onaction={() => go('is')} />
  {:else}
    {#each days as d (d.id)}
      <section class="day" aria-label={d.title} data-events-day>
        <h2 class="h2s">{d.title}</h2>
        <ul class="rows">
          {#each d.rows as x (x.id)}
            <li class="r" data-event-row={x.group}>
              <time class="tm" datetime={x.at}>{hhmm(x.at)}</time>
              <div class="rt">
                <p class="sen">{lineText(x)}</p>
                <p class="small muted meta">
                  <span class="chip chip-neutral">{groupText(x.group)}</span>
                  {#if x.projectName}<span>{x.projectName}</span>{/if}
                  {#if x.jobId}<a class="openjob" href={`#/is/${x.jobId}`} onclick={(e) => { e.preventDefault(); go('is', String(x.jobId)) }}>{t('evOpenJob')}</a>{/if}
                </p>
                {#if techOf(x)}<details class="small muted tech" data-event-tech><summary>{t('showDetail')}</summary><p class="mono">{techOf(x)}</p></details>{/if}
              </div>
            </li>
          {/each}
        </ul>
      </section>
    {/each}
    {#if hasMore}
      <p class="more"><button class="btn" disabled={busy} onclick={() => load(true)} data-events-more>{t('evMore')}</button></p>
    {/if}
    {#if error}<p class="err-line" role="alert">{t('evLoadError', { m: error })}</p>{/if}
  {/if}
</div>

<style>
  .filter { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 14px; }
  .seg { min-height: var(--ctl); padding: 0 14px; border: 1px solid var(--line); border-radius: var(--seg-r, 999px); background: var(--surface); color: var(--ink-2); font-size: var(--fs-sm); font-weight: var(--fw-medium); transition: background-color var(--dur-1) var(--ease), border-color var(--dur-1) var(--ease); }
  .seg:hover { background: var(--hover); }
  .seg.on { border-color: var(--pri-bc, var(--primary)); background: var(--primary); color: var(--on-primary); }
  .day { margin-top: 26px; }
  .h2s { font-size: var(--fs-md); font-weight: var(--fw-strong); }
  .rows { display: flex; flex-direction: column; margin-top: 8px; border-bottom: 1px solid var(--line); }
  .r { display: flex; gap: 14px; padding: 11px 0; border-top: 1px solid var(--line); }
  .tm { flex: none; width: 3.4rem; padding-top: 1px; color: var(--ink-3); font-variant-numeric: tabular-nums; font-size: var(--fs-sm); }
  .rt { min-width: 0; display: flex; flex-direction: column; gap: 4px; }
  .sen { max-width: 64ch; color: var(--ink); overflow-wrap: anywhere; }
  .meta { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 10px; }
  .meta a { color: var(--ink-2); text-decoration: underline; text-underline-offset: 2px; }
  .more { margin-top: 16px; }
  .openjob { display: inline-flex; align-items: center; min-height: 24px; }
</style>
