import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import babel from '@rolldown/plugin-babel'
import { lingui, linguiTransformerBabelPreset } from '@lingui/vite-plugin'

const __dirname = path.dirname(fileURLToPath(import.meta.url))

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  // `MAKI_API_URL` in `.env.<mode>` points the proxy at a backend other than the default 8990, so
  // a second instance (another config dir, another port) can be previewed beside the main one.
  const api = loadEnv(mode, __dirname, 'MAKI_').MAKI_API_URL || 'http://localhost:8990'
  return {
    plugins: [
      react(),
      // Turns an imported `.po` into runtime messages, so there is no `lingui compile` step and no
      // generated catalogs to keep in step with the source ones.
      lingui(),
      // The `t` / `Trans` / `plural` macros are a build-time transform, and `@vitejs/plugin-react` is
      // oxc-based from v6 with no Babel hook of its own, so the transform runs as its own pass. The
      // preset filters on the macro import, so files that never mention Lingui skip Babel entirely.
      babel({ presets: [linguiTransformerBabelPreset()] }),
    ],
    resolve: {
      alias: {
        // The catalogs live at the repo root (see lingui.config.js), one directory above this
        // package. A relative `../../locales/...` specifier resolves fine in dev and on some
        // platforms' production builds, but Rolldown's module resolution for a path that escapes
        // the package root was observed to fail only in the Linux/musl Docker build, not on the
        // Windows machine building the same commit — an alias resolves to an absolute path up
        // front and sidesteps that boundary check entirely.
        '@locales': path.resolve(__dirname, '../locales'),
      },
    },
    server: {
      // Honor an externally assigned port (e.g. the preview harness); default 5173.
      port: Number(process.env.PORT) || 5173,
      // The message catalogs live at the repo root so the API can embed the same files; without this
      // the dev server refuses to serve anything above `frontend/`.
      fs: { allow: ['..'] },
      proxy: {
        '/api': { target: api, changeOrigin: false },
        '/initialize.json': { target: api, changeOrigin: false },
        '/signalr': {
          target: api,
          ws: true,
          changeOrigin: false,
        },
      },
    },
  }
})
