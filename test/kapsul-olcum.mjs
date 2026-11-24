// Söz kapsülü ölçümü: Soz-Kapsul.exe --olc kipini çalıştırır, sonucu JSON dosyasına ve okunur tabloya yazar.
// Kullanım: node test\kapsul-olcum.mjs [exe yolu] [--hizli] [--cikti <json>] [--tema cam|murekkep|kagit]
// Gerçek fare ya da klavye olayı göndermez; sürükleme kapsülün kendi mantığına sentetik hedef vererek yapılır.
// DOTNET_ROOT yalnız debug exe için gerekir (yayın exe'si kendi çalışma zamanını taşır).
import { spawn } from 'node:child_process';
import { readFileSync, mkdirSync, existsSync, rmSync } from 'node:fs';
import { dirname, resolve } from 'node:path';

const args = process.argv.slice(2);
let exe = '_gecici/kapsul-yayin/Soz-Kapsul.exe';
let cikti = '_gecici/kapsul-olcum/sonuc.json';
let hizli = false;
let tema = null;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--hizli') hizli = true;
  else if (args[i] === '--cikti') cikti = args[++i];
  else if (args[i] === '--tema') tema = args[++i];
  else exe = args[i];
}
exe = resolve(exe); cikti = resolve(cikti);
if (!existsSync(exe)) { console.error('exe yok: ' + exe); process.exit(2); }
mkdirSync(dirname(cikti), { recursive: true });
if (existsSync(cikti)) rmSync(cikti);

const gunluk = cikti.replace(/\.json$/, '.log');
const kapsulArgs = ['--olc', cikti, '--gunluk', gunluk, '--durum', dirname(cikti) + '/durum', '--motor-yok', '--exit-after', hizli ? '70' : '160'];
if (hizli) kapsulArgs.push('--olc-hizli');
if (tema) kapsulArgs.push('--tema', tema);
const env = { ...process.env, DOTNET_ROOT: process.env.DOTNET_ROOT || 'D:\\tools\\dev\\dotnet10' };

console.log(`çalışıyor: ${exe} ${kapsulArgs.join(' ')}`);
const p = spawn(exe, kapsulArgs, { env, stdio: 'ignore' });
const kod = await new Promise((r) => p.on('exit', r));
if (!existsSync(cikti)) { console.error(`sonuç dosyası yok (çıkış kodu ${kod}); günlük: ${gunluk}`); process.exit(1); }

const s = JSON.parse(readFileSync(cikti, 'utf8'));
const sat = (a, b, c, d) => console.log(`${String(a).padEnd(46)}${String(b).padEnd(16)}${String(c).padEnd(22)}${d}`);
console.log('');
sat('Ölçüt', 'Ölçülen', 'Bütçe', 'Sonuç');
console.log('-'.repeat(92));
for (const o of s.ozet) sat(o.olcut, o.olculen, o.butce, o.gecti ? 'geçti' : 'TUTMADI');
console.log('');
console.log(`ekran ölçeği ${s.olcek}, ekran ${s.ekran_sayisi}, çekirdek ${s.islemci_cekirdek}, tema ${s.tema}, hızlı kip ${s.hizli_kip}`);
console.log(`boşta: ${s.bosta_sure_sn} sn, işlemci ${s.bosta_cpu_ms} ms (toplam makinede %${s.bosta_cpu_yuzde_toplam}), zamanlayıcı uyanması ${s.bosta_zamanlayici_uyanmasi}, iş parçacığı ${s.bosta_iplik_sayisi}`);
console.log(`sürükleme: ${s.surukle_kare_sayisi} kare, ${s.surukle_kare_sn} kare/sn, p50 ${s.surukle_kare_p50_ms} ms, p95 ${s.surukle_kare_p95_ms} ms, p99 ${s.surukle_kare_p99_ms} ms, en uzun ${s.surukle_kare_en_uzun_ms} ms, kare işi p95 ${s.surukle_kare_isi_p95_ms} ms`);
console.log(`yazı hatası: ${s.yazi_hatasi}`);
console.log(`tıkanma (başta): ${JSON.stringify(s.tikanma_basta)}`);
console.log(`tıkanma (ortada): ${JSON.stringify(s.tikanma_ortada)}`);
console.log(`ham sonuç: ${cikti}`);
const tutmayan = s.ozet.filter((o) => !o.gecti).length;
process.exit(tutmayan === 0 ? 0 : 3);
