// Ortak temizlik (f1, yönetici notu 23 Kas): her koşu kendi açtığı sunucuyu ve tarayıcıyı KENDİ PID'iyle kapatır, kapandığını doğrular, PID dosyası yazar.
// Kurallar: yalnız bu sürecin kaydettiği PID'ler ve kayıtlı profil klasörünü kullanan Edge süreçleri kapatılır. Başka kimsenin sürecine dokunulmaz.
// Kayıt: surecKaydet(pid), portKaydet(port), profilKaydet(klasor). Kurulum: temizlikKur({ sure }) (insan.mjs asıl süreçte çağırır).
// Güvence katmanları: (1) normal çıkış ve hata, (2) kendi iç süre sınırı (SOZ_SURE sn, varsayılan 1500), (3) başlatan süreç ölürse (dış `timeout` vb.) kendiliğinden temizler ve çıkar.
import { execFileSync } from 'node:child_process'
import { mkdirSync, writeFileSync, existsSync } from 'node:fs'
import { basename } from 'node:path'

const pids = new Set()
const profiller = new Set()
const portlar = new Set()
const PID_KLASOR = process.env.SOZ_PID_KLASOR ?? 'E:/Sample Work/urun/_gecici/f1/pid'
const betik = basename(process.argv[1] ?? 'betik')
let kapandi = false
let rapor = null

const alive = (pid) => { try { process.kill(pid, 0); return true } catch { return false } }
const bekle = (ms) => { try { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms) } catch { /* yoksay */ } }
const norm = (s) => String(s).replaceAll('\\', '/').toLowerCase()

function pidDosyasi(ek = {}) {
  try {
    mkdirSync(PID_KLASOR, { recursive: true })
    writeFileSync(`${PID_KLASOR}/${betik}-${process.pid}.json`, JSON.stringify({ betik, node: process.pid, baslangic: new Date().toISOString(), pidler: [...pids], profiller: [...profiller], portlar: [...portlar], ...ek }, null, 2))
  } catch { /* PID dosyası yazılamazsa koşu durmaz */ }
}
export const surecKaydet = (pid) => { if (pid) { pids.add(pid); pidDosyasi() } }
export const portKaydet = (p) => { if (p) { portlar.add(p); pidDosyasi() } }
export const profilKaydet = (k) => { if (k) { profiller.add(norm(k)); pidDosyasi() } }

/** Kayıtlı profil klasörünü kullanan Edge süreçleri (ana süreç ve çocukları). */
function edgeSurecleri() {
  if (profiller.size === 0) return []
  try {
    const out = execFileSync('powershell', ['-NoProfile', '-Command', "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress"], { encoding: 'utf8', windowsHide: true, timeout: 20000 })
    if (!out.trim()) return []
    const j = JSON.parse(out)
    return (Array.isArray(j) ? j : [j]).filter((p) => p?.CommandLine && [...profiller].some((k) => norm(p.CommandLine).includes(k))).map((p) => p.ProcessId)
  } catch { return [] }
}

/** Kendi portlarında dinleyen süreçler (netstat ile). */
export function dinleyenler(liste = [...portlar]) {
  if (liste.length === 0) return []
  try {
    const out = execFileSync('netstat', ['-ano', '-p', 'TCP'], { encoding: 'utf8', windowsHide: true, timeout: 15000 })
    const bul = []
    for (const satir of out.split(/\r?\n/)) {
      const m = /^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)/i.exec(satir)
      if (m && liste.includes(Number(m[1]))) bul.push({ port: Number(m[1]), pid: Number(m[2]) })
    }
    return bul
  } catch { return [] }
}

/** Eşzamanlı: kayıtlı süreçleri ve profilli Edge'i kapatır, kapandığını doğrular, raporu yazar ve döndürür. Birden çok çağrı güvenlidir. */
export function kapatVeDogrula() {
  if (kapandi && rapor) return rapor
  kapandi = true
  const hedef = [...new Set([...pids, ...edgeSurecleri()])].filter((p) => p !== process.pid)
  for (const p of hedef) { try { process.kill(p) } catch { /* zaten yok */ } }
  const t0 = Date.now()
  while (Date.now() - t0 < 6000 && hedef.some(alive)) bekle(200)
  const kalan = hedef.filter(alive)
  // Edge çocukları geç kapanabilir: ikinci tarama
  const edgeKalan = edgeSurecleri().filter(alive)
  for (const p of edgeKalan) { try { process.kill(p) } catch { /* yoksay */ } }
  bekle(400)
  const dinleyen = dinleyenler()
  rapor = { kapatilan: hedef, kalan: [...new Set([...kalan, ...edgeKalan.filter(alive)])], dinleyen }
  pidDosyasi({ bitis: new Date().toISOString(), temizlik: rapor })
  console.log(`TEMİZLİK: kapatılan ${hedef.length} süreç (${hedef.join(', ') || '-'}); hâlâ açık ${rapor.kalan.length}${rapor.kalan.length ? ' (' + rapor.kalan.join(', ') + ')' : ''}; kendi portlarında dinleyen ${dinleyen.length}${dinleyen.length ? ' (' + dinleyen.map((d) => `${d.port}:${d.pid}`).join(', ') + ')' : ''}`)
  return rapor
}

let kuruldu = false
/** Asıl (insan eşdeğeri) süreçte bir kez çağrılır. sahip: bu süreci başlatan sürecin PID'i (ölürse kendiliğinden temizlenip çıkılır). */
export function temizlikKur({ sure = Number(process.env.SOZ_SURE ?? 1500), sahip = Number(process.env.SOZ_INSAN_SAHIP ?? 0) } = {}) {
  if (kuruldu) return
  kuruldu = true
  pidDosyasi()
  process.on('exit', () => { kapatVeDogrula() })
  for (const sig of ['SIGINT', 'SIGTERM', 'SIGBREAK']) process.on(sig, () => process.exit(130))
  // iç süre sınırı: bitmeyen koşu kendini kapatır (dışarıdan `timeout` ile sarmalamaya gerek yok)
  setTimeout(() => { console.log(`ZAMAN AŞIMI: ${sure} sn doldu, koşu kendini kapatıyor`); process.exitCode = 124; process.exit(124) }, sure * 1000).unref()
  // başlatan süreç öldüyse (dış kesme) yetim kalma
  if (sahip) setInterval(() => { if (!alive(sahip)) { console.log('Başlatan süreç kapandı, koşu temizlenip çıkıyor'); process.exit(125) } }, 2000).unref()
}
