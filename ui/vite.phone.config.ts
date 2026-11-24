import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'
declare const process: { env: Record<string, string | undefined> }

// Telefon sayfası (t/index.html → /t/). Yerel ağ sunucusu YALNIZ /t/* yolunu sunar; bu yüzden betikler ve stiller
// de /t/assets/ altına yazılır (assetsDir 't/assets', base '/'). wwwroot boşaltılmaz: ana derlemenin çıktısı yerinde kalır.
export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: process.env.SOZ_OUT ?? '../src/Soz.Server/wwwroot',
    emptyOutDir: false,
    assetsDir: 't/assets',
    rollupOptions: {
      input: { t: 't/index.html' },
    },
  },
})
