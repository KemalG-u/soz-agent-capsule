<script lang="ts">
  import { onMount } from 'svelte'
  import { PhoneError, phoneNow, phoneQueue, type PhoneAttention, type PhoneItem, type PhoneNow } from './phone-api'

  // Telefon sayfası: tek sütun. Yalnız Sıra bende + sabah özeti + mesaj kutusu. Metinler bu dosyada (yalnız Türkçe).
  let data = $state<PhoneNow | null>(null)
  let phase = $state<'loading' | 'ok' | 'unpaired' | 'offline' | 'error'>('loading')
  let errMsg = $state('')
  let serverFault = $state(false) // bilgisayara ulaşıldı ama Söz hata verdi (ulaşılamadı değil)
  let refreshing = $state(false)
  let target = $state('')
  let text = $state('')
  let sent = $state<{ id: number; text: string; to: string; ok: boolean | null }[]>([])
  let sendErr = $state('')
  let box: HTMLTextAreaElement | undefined = $state()

  async function load(manual = false) {
    if (manual) refreshing = true
    try {
      data = await phoneNow()
      phase = 'ok'
      serverFault = false
      errMsg = ''
      // Yazılmakta olan mesaj varken seçili sohbet listeden kalkarsa mesaj sessizce başka sohbete bağlanmaz (A-TEL-08): kutu uyarı verir, kişi yeniden seçer.
      const there = data.waiting.some((w) => String(w.sessionId) === target)
      if (!target || (!there && !text.trim())) target = data.waiting[0] ? String(data.waiting[0].sessionId) : ''
    } catch (e) {
      if (e instanceof PhoneError && (e.status === 401 || e.status === 403)) phase = 'unpaired'
      else if (e instanceof PhoneError && e.status === 0) phase = data ? 'ok' : 'offline'
      else {
        phase = data ? 'ok' : 'error'
        errMsg = e instanceof Error ? e.message : String(e) // ham metin yalnız "Teknik ayrıntı" altında gösterilir
        serverFault = true
      }
    } finally {
      refreshing = false
    }
  }

  onMount(() => {
    void load()
    const h = setInterval(() => !document.hidden && void load(), 15000)
    const v = () => !document.hidden && void load()
    document.addEventListener('visibilitychange', v)
    return () => {
      clearInterval(h)
      document.removeEventListener('visibilitychange', v)
    }
  })

  function ago(ts: string): string {
    const t0 = Date.parse(ts)
    if (!ts || !Number.isFinite(t0)) return '' // zaman boş ya da bozuk: hiçbir şey yazılmaz (A-TEL-06)
    const min = Math.max(0, Math.floor((Date.now() - t0) / 60000))
    if (min < 1) return 'az önce'
    if (min < 60) return `${min} dk önce`
    const h = Math.floor(min / 60)
    return h < 24 ? `${h} sa önce` : `${Math.floor(h / 24)} gün önce`
  }
  const TOOL_MAP: Record<string, string> = { claude: 'Claude Code', codex: 'Codex', antigravity: 'Antigravity', vscode: 'VS Code', cursor: 'Cursor', hermes: 'Hermes', opencode: 'OpenCode' }
  const tool = (x: string) => TOOL_MAP[(x ?? '').trim().toLowerCase()] ?? x
  const n = (v: unknown[] | number | undefined) => (Array.isArray(v) ? v.length : (v ?? 0))
  const waiting = $derived(data?.waiting ?? [])
  const m = $derived(data?.morning)
  // Sıra bende: sunucunun tek öncelik tablosu (priority). Eski sunucu items göndermezse oturum listesine düşülür.
  const items = $derived<PhoneAttention[]>(data?.items ?? [])
  const sentence = $derived(data?.sentence ?? (waiting.length === 0 ? 'Şu an seni bekleyen yok.' : `${waiting.length} yardımcı seni bekliyor.`))
  const kindLabel: Record<string, string> = { ask: 'soru', accept: 'kabul bekliyor', blocked: 'engel', done: 'bitti', uncertain: 'emin değilim' }
  const waitingFor = (sid: number | null) => (sid == null ? null : (waiting.find((w) => w.sessionId === sid) ?? null))
  const sel = $derived(waiting.find((w) => String(w.sessionId) === target) ?? null)

  function write(w: PhoneItem) {
    target = String(w.sessionId)
    box?.focus()
    box?.scrollIntoView({ block: 'center', behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' })
  }

  /** Gönderim hatasının gerçek nedeni, sade cümleyle (A-TEL-05). Ham sunucu metni ekrana çıkmaz. */
  function sendReason(er: unknown): string {
    const s = er instanceof PhoneError ? er.status : -1
    if (s === 401 || s === 403) return 'Eşleşme geçersiz. Bilgisayardan yeniden eşleştir.'
    if (s === 0) return 'Bilgisayara ulaşılamıyor. İkisi de aynı Wi-Fi ağında mı, Söz açık mı, bak.'
    if (s === 413 || (s === 400 && v4000())) return 'Mesaj çok uzun. Daha kısa yaz.'
    if (s === 400) return 'Mesaj kabul edilmedi. Boş olmadığından ve çok uzun olmadığından emin ol.'
    if (s === 404 || s === 409) return 'Bu sohbet artık beklemiyor. Listeyi yenile, sonra yeniden yaz.'
    if (s === 429) return 'Çok sık mesaj gönderdin. Bir dakika sonra yeniden dene.'
    if (s >= 500) return 'Söz bir hata verdi, mesaj gönderilemedi. Biraz sonra yeniden dene.'
    return 'Mesaj gönderilemedi. Biraz sonra yeniden dene.'
  }
  const v4000 = () => text.length > 4000

  async function send(e?: Event) {
    e?.preventDefault()
    const v = text.trim()
    if (!v || !sel) return
    sendErr = ''
    const row = { id: Date.now(), text: v, to: sel.projectName ?? 'sohbet', ok: null as boolean | null }
    sent = [row, ...sent] // önce çiz
    text = ''
    try {
      await phoneQueue({ sessionId: sel.sessionId, projectId: sel.projectId, text: v })
      row.ok = true
      sent = [...sent]
    } catch (er) {
      sent = sent.filter((x) => x.id !== row.id)
      text = v
      sendErr = sendReason(er)
    }
  }
</script>

<main class="ph">
  <header class="top">
    <span class="word">Söz</span>
    <button class="btn btn-quiet btn-sm" onclick={() => load(true)} disabled={refreshing} aria-label="Yenile">{refreshing ? 'Yenileniyor' : 'Yenile'}</button>
  </header>

  {#if phase === 'loading'}
    <p class="muted" role="status">Yükleniyor</p>
  {:else if phase === 'unpaired'}
    <section class="plain" role="alert">
      <h1 class="h1">Bu telefon henüz eşleşmedi.</h1>
      <p>Bilgisayarda Söz uygulamasını aç, <strong>Ayarlar</strong> sayfasında <strong>Gelişmiş</strong> kısmını aç, <strong>Telefon</strong> bölümünden QR’ı çıkar ve bu telefonun kamerasıyla oku.</p>
      <p class="small muted">Eşleşme yenilendiyse eski bağlantı çalışmaz, yeni QR gerekir.</p>
    </section>
  {:else if phase === 'offline' || phase === 'error'}
    <section class="plain" role="alert">
      {#if serverFault}
        <h1 class="h1">Söz bir sorun yaşadı.</h1>
        <p>Bilgisayara ulaşıldı ama Söz şu an cevap veremedi. Biraz sonra yeniden dene.</p>
      {:else}
        <h1 class="h1">Bilgisayara ulaşılamıyor.</h1>
        <p>Telefon ile bilgisayar aynı Wi-Fi ağında olmalı ve Söz çalışıyor olmalı.</p>
      {/if}
      {#if errMsg}<details class="small muted"><summary>Teknik ayrıntı</summary><p>{errMsg}</p></details>{/if}
      <button class="btn btn-primary" onclick={() => load(true)}>Yeniden dene</button>
    </section>
  {:else}
    <h1 class="h1">{sentence}</h1>

    {#if m}
      <dl class="mstats" aria-label="Sabah özeti">
        <div class="ok"><dd>{n(m.passed)}</dd><dt>iş geçti</dt></div>
        <div class="bad" class:zero={n(m.failed) === 0}><dd>{n(m.failed)}</dd><dt>olmadı</dt></div>
        <div class="warn" class:zero={m.unchecked === 0}><dd>{m.unchecked}</dd><dt>denenmedi</dt></div>
        <div class:zero={m.guardStops === 0}><dd>{m.guardStops}</dd><dt>koruma durdurdu</dt></div>
      </dl>
    {/if}

    <section aria-labelledby="sb">
      <h2 class="h2" id="sb">Sıra bende</h2>
      {#if items.length > 0}
        <ul class="spines" data-items>
          {#each items as it (it.id)}
            {@const w = waitingFor(it.sessionId)}
            <li class="spine v-waiting" data-priority={it.priority}>
              <div class="line"><strong class="proj">{it.projectName ?? it.title ?? 'Genel'}</strong><span class="tool">{kindLabel[it.kind] ?? it.kind}</span><span class="small muted">{ago(it.at)}</span></div>
              {#if it.sentence}<p class="said">{it.sentence}</p>{/if}
              {#if it.detail}<p class="small muted">{it.detail}</p>{/if}
              {#if w}<p><button class="btn btn-sm" onclick={() => write(w)}>Buna yaz</button></p>{:else if it.jobId != null}<p class="small muted">Karar için bilgisayarı aç.</p>{/if}
            </li>
          {/each}
        </ul>
      {:else if data?.items && waiting.length === 0}
        <p class="empty">Bekleyen ya da takılan bir şey yok.</p>
      {:else if waiting.length === 0}
        <p class="empty">Bekleyen ya da takılan sohbet yok.</p>
      {:else}
        <ul class="spines" data-waiting>
          {#each waiting as w (w.sessionId)}
            <li class="spine v-waiting">
              <div class="line"><strong class="proj">{w.projectName ?? 'Genel'}</strong><span class="tool">{tool(w.tool)}</span><span class="small muted">{ago(w.since)}</span></div>
              {#if w.asksQuestion}<p class="small"><span class="chip chip-working">soru soruyor</span></p>{/if}
              {#if w.lastAssistantText}<p class="said">{w.lastAssistantText}</p>{/if}
              <p><button class="btn btn-sm" onclick={() => write(w)}>Buna yaz</button></p>
            </li>
          {/each}
        </ul>
      {/if}
    </section>

    <section aria-labelledby="mk">
      <h2 class="h2" id="mk">Mesaj</h2>
      {#if waiting.length === 0}
        <p class="empty">Mesaj yazılacak sohbet çıkınca burada açılır.</p>
      {:else}
        <form class="form" onsubmit={send}>
          <div class="field">
            <label class="label" for="kime">Kime</label>
            <select id="kime" class="select" bind:value={target}>
              {#each waiting as w (w.sessionId)}<option value={String(w.sessionId)}>{w.projectName ?? 'Genel'} · {tool(w.tool)}</option>{/each}
            </select>
          </div>
          <div class="field">
            <label class="label" for="mesaj">Mesajın</label>
            <textarea id="mesaj" class="textarea" rows="3" bind:this={box} bind:value={text} placeholder="Cevabını ya da yönlendirmeni yaz"></textarea>
          </div>
          {#if target && !sel}<p class="small err-line" role="alert" data-target-lost>Yazdığın sohbet listeden kalktı. Mesajı göndermek için kime gideceğini yeniden seç.</p>{/if}
          <button class="btn btn-primary btn-lg" type="submit" disabled={!text.trim() || !sel}>Kuyruğa yaz</button>
          <p class="small muted">Mesaj kuyruğa girer, yardımcı bir sonraki duruşunda okur. Anında girmez.</p>
          {#if sendErr}<p class="small err-line" role="alert">{sendErr}</p>{/if}
        </form>
        {#if sent.length > 0}
          <ul class="sent" aria-label="Gönderilenler">
            {#each sent as s (s.id)}
              <li><span class="voice">{s.text}</span> <span class="small muted">→ {s.to} · kuyrukta</span></li>
            {/each}
          </ul>
        {/if}
      {/if}
    </section>
  {/if}
</main>

<style>
  .ph { max-width: 32rem; margin-inline: auto; padding: 14px 16px calc(40px + env(safe-area-inset-bottom)); }
  .top { display: flex; align-items: center; justify-content: space-between; margin-bottom: 18px; }
  .word { font-family: var(--sans); font-weight: var(--fw-strong); font-size: var(--fs-lg); }
  .h1 { font-size: var(--fs-xl); margin-bottom: 14px; }
  .plain { display: flex; flex-direction: column; gap: 12px; align-items: flex-start; }
  .mstats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 8px; margin: 0 0 26px; padding: 12px 0; border-block: 1px solid var(--line); }
  .mstats div { display: flex; flex-direction: column-reverse; }
  .mstats dd { margin: 0; font-family: var(--sans); font-weight: var(--fw-strong); font-size: var(--fs-xl); line-height: 1.05; font-variant-numeric: tabular-nums; }
  .mstats dt { font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-3); line-height: 1.2; }
  .ok dd { color: var(--ok); }
  .bad dd { color: var(--stalled); }
  .warn dd { color: var(--waiting); }
  .zero dd { color: var(--ink-3); }
  section { margin-bottom: 30px; }
  .h2 { margin-bottom: 8px; }
  .empty { padding: 14px 0; border-top: 1px solid var(--line); color: var(--ink-3); }
  .line { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 10px; }
  .proj { font-family: var(--sans); font-size: var(--fs-lg); }
  .tool { font-size: var(--fs-xs); font-weight: var(--fw-strong); color: var(--ink-2); border: 1px solid var(--line-strong); border-radius: 6px; padding: 0 7px; }
  .said { margin: 8px 0; line-height: 1.55; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 6; line-clamp: 6; overflow: hidden; }
  .form { display: flex; flex-direction: column; gap: 14px; }
  .sent { display: flex; flex-direction: column; gap: 8px; margin-top: 14px; }
</style>
