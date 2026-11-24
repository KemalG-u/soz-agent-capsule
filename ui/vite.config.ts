import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'
declare const process: { env: Record<string, string | undefined> }

// Ana arayüz (index.html). Telefon sayfası ayrı derlenir: vite.phone.config.ts ("npm run build" ikisini sırayla çalıştırır).
// Bu derleme wwwroot'u boşaltır; telefon derlemesi ondan sonra çalışmalıdır.
export default defineConfig({
  plugins: [svelte()],
  build: {
    // SOZ_OUT: yayın klasörüne dokunmadan sınama derlemesi için (varsayılan: sunucunun wwwroot klasörü).
    outDir: process.env.SOZ_OUT ?? '../src/Soz.Server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:49300',
    },
  },
})
