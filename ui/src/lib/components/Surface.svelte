<script lang="ts">
  import { onMount } from 'svelte'
  import { t } from '../i18n/index.svelte'
  import { isNarrow } from '../keys'
  import { go, router } from '../router.svelte'
  import { app } from '../store.svelte'
  import { cancelPendingStart, ensureJob, flatJobs, refreshSurface, select, selectedJob, sf } from '../surface.svelte'
  import { secimKarari } from '../isSecim'
  import { DAR_SORGU, darGorunum } from '../gezinme'
  import AddFolder from './AddFolder.svelte'
  import DonePanel from './DonePanel.svelte'
  import Icon from './Icon.svelte'
  import JobList from './JobList.svelte'
  import NewJobBar from './NewJobBar.svelte'
  import SetupCard from './SetupCard.svelte'
  import Stream from './Stream.svelte'

  let { hideBar = false }: { hideBar?: boolean } = $props()
  let dlg: HTMLDialogElement | undefined = $state()

  // Sayaç ve canlı olaylarla liste tazelenir.
  $effect(() => {
    void app.tick
    refreshSurface()
  })

  // Hangi iş açılır (isSecim.ts, sınanır): adresle ya da kısayolla istenen iş listede yoksa sunucudan getirilir, bulunamazsa "Bu işi bulamadım" denir.
  // İstenen işin yerine listedeki ilk iş ASLA gösterilmez; ilk iş yalnız hiçbir şey istenmemişken seçilir.
  const routeId = $derived(/^\d+$/.test(router.sub) && Number(router.sub) > 0 ? Number(router.sub) : null)
  $effect(() => {
    if (!sf.loaded) return
    const k = secimKarari({ jobIds: sf.jobs.map((j) => j.id), selId: sf.selId, routeId, pulling: sf.jobPulling, missing: sf.jobMissing })
    if (k === 'select-route' && routeId !== null) select(routeId, { detail: true })
    else if (k === 'fetch-route' && routeId !== null) { sf.selId = routeId; sf.view = 'detail'; void ensureJob(routeId) }
    else if (k === 'fetch-sel' && sf.selId !== null) void ensureJob(sf.selId)
    else if (k === 'first') { const f = flatJobs()[0]; if (f) select(f.id, { oto: true }); else sf.selId = null }
  })

  // Dar pencerede adres görünümü söyler (O1): #/is liste, #/is/<no> ayrıntı. Geri ve İleri bu yüzden liste ile ayrıntı arasında gider.
  // Pencere genişten dara geçince de (adres değişmeden) görünüm adrese uyar.
  let darMi = $state(isNarrow())
  $effect(() => {
    if (typeof matchMedia === 'undefined') return
    const mq = matchMedia(DAR_SORGU)
    const h = () => (darMi = mq.matches)
    mq.addEventListener('change', h)
    return () => mq.removeEventListener('change', h)
  })
  let sonAnahtar: string | null = null
  $effect(() => {
    const s = router.sub
    const anahtar = `${s}|${darMi}`
    if (anahtar === sonAnahtar) return
    sonAnahtar = anahtar
    if (darMi && !sf.setup && sf.doneRaw === null) sf.view = darGorunum(s)
  })
  const missingNow = $derived(sf.loaded && sf.jobMissing !== null && selectedJob() === null)

  // Onay kartı açılınca dar panelde ayrıntıya geç.
  $effect(() => {
    if (sf.setup || sf.doneRaw !== null) sf.view = 'detail'
  })

  let seenNew = sf.focusNew + router.yeniTick + app.focusNewJob
  $effect(() => {
    const n = sf.focusNew + router.yeniTick + app.focusNewJob
    if (n === seenNew) return
    seenNew = n
    if (isNarrow()) sf.view = 'list'
  })

  // Klasör seçme penceresi
  $effect(() => {
    if (sf.addFolderOpen && dlg && !dlg.open) dlg.showModal()
    if (!sf.addFolderOpen && dlg?.open) dlg.close()
  })

  onMount(() => {
    if (/^\d+$/.test(router.sub)) sf.view = 'detail'
    else sf.jobMissing = null
  })
</script>

{#if !sf.loaded}
  <div class="loading" role="status"><span class="skel" style="width: 40%"></span></div>
{:else}
  <h1 class="sr-only">{t('navJobs')}</h1>
  {#if !hideBar}
    <div class="newwrap" data-view={sf.view}><NewJobBar /></div>
  {/if}
  <div class="panes" data-view={sf.view} data-panes>
    <section class="listcol" aria-label={t('listLabel')}>
      <JobList />
    </section>
    <section class="streamcol" aria-live="off">
      {#if sf.setup}
        <div class="setupwrap"><SetupCard setup={sf.setup} /></div>
      {:else if sf.doneRaw !== null}
        <div class="setupwrap"><DonePanel raw={sf.doneRaw} /></div>
      {:else if selectedJob()}
        <Stream />
      {:else if missingNow}
        <div class="none" role="alert" data-job-missing>
          <Icon name="info" size={28} />
          <p>{t('jobNotFound')}</p>
          <p class="small muted">{t('jobNotFoundLead')}</p>
          <button class="btn" onclick={() => { sf.jobMissing = null; go('is') }} data-job-missing-back>{t('jobNotFoundBack')}</button>
        </div>
      {:else if sf.jobPulling !== null || routeId !== null}
        <div class="none" role="status" data-job-pulling><span class="spin" aria-hidden="true"></span><p class="small muted">{t('jobLoading')}</p></div>
      {:else}
        <div class="none">
          <Icon name="inbox" size={28} />
          <p class="muted">{t('streamNone')}</p>
          <p class="small muted" data-jobs-lead>{t('jobsLead')}</p>
          <button class="btn" onclick={() => sf.focusNew++}>{t('streamNoneBtn')} <kbd>n</kbd></button>
        </div>
      {/if}
    </section>
  </div>
{/if}

<dialog class="sheet" bind:this={dlg} aria-labelledby="afd-t" onclose={() => { sf.addFolderOpen = false; cancelPendingStart() }} onclick={(e) => { if (e.target === dlg) dlg?.close() }}>
  <div class="afd">
    <h2 class="h2" id="afd-t">{t('addFolderTitle')}</h2>
    {#if sf.addFolderOpen}<AddFolder autofocus />{/if}
    <p class="afd-x"><button type="button" class="btn btn-quiet btn-sm" onclick={() => dlg?.close()} data-afd-cancel>{t('cancel')}</button></p>
  </div>
</dialog>

<style>
  .loading { padding: 40px; }
  .panes { flex: 1; min-height: 0; display: grid; grid-template-columns: minmax(300px, 24rem) minmax(0, 1fr); }
  .listcol { display: flex; flex-direction: column; min-height: 0; border-right: 1px solid var(--line); background: var(--bg); }
  .streamcol { min-width: 0; min-height: 0; background: var(--surface); display: flex; flex-direction: column; }
  .setupwrap { padding: 24px 20px; overflow-y: auto; }
  .none { margin: auto; display: flex; flex-direction: column; align-items: center; gap: 10px; padding: 24px; color: var(--ink-3); text-align: center; }
  .afd { padding: 18px 20px 20px; display: flex; flex-direction: column; gap: 12px; }
  .afd-x { margin: 0; display: flex; justify-content: flex-end; }

  @media (max-width: 759px) {
    .newwrap[data-view='detail'] { display: none; }
    .panes { display: block; position: relative; overflow: hidden; }
    .listcol, .streamcol { position: absolute; inset: 0; border: 0; transition: transform var(--dur-2) var(--ease); }
    .streamcol { transform: translateX(100%); visibility: hidden; transition: transform var(--dur-2) var(--ease), visibility 0s linear var(--dur-2); }
    .panes[data-view='detail'] .streamcol { transform: none; visibility: visible; transition: transform var(--dur-2) var(--ease), visibility 0s; }
    .panes[data-view='detail'] .listcol { transform: translateX(-24%); visibility: hidden; transition: transform var(--dur-2) var(--ease), visibility 0s linear var(--dur-2); }
  }
</style>
