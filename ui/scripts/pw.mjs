// Ortak Playwright başlatıcı: sistemdeki Edge, yalıtılmış geçici profil, başsız. Kullanıcının tarayıcısına dokunmaz.
import { chromium } from 'playwright-core'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { profilKaydet } from './temizlik.mjs'

export async function launch() {
  const profile = mkdtempSync(join(tmpdir(), 'soz-pw-'))
  profilKaydet(profile) // temizlik.mjs: bu profili kullanan Edge süreçleri çıkışta kapatılır
  const ctx = await chromium.launchPersistentContext(profile, {
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true,
    args: ['--no-first-run', '--no-default-browser-check'],
  })
  return { ctx, profile }
}
