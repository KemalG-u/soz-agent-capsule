# Third-party components

Söz is licensed under Apache-2.0 (see `LICENSE`). It depends on the following packages, which keep their own licenses. Package versions are pinned in the project files and `ui/package-lock.json`.

.NET (NuGet): `Microsoft.Data.Sqlite`, `Microsoft.Web.WebView2`, `Velopack`, and for tests `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`.

UI (npm, build time): `svelte`, `vite`, `@sveltejs/vite-plugin-svelte`, `typescript`, `svelte-check`, `playwright-core`.

`src/Soz.Server/wwwroot` contains the pre-built UI bundle generated from `ui/` with the packages above (`npm run build`).

License texts are those of each upstream package; this file lists them and does not reproduce them.
