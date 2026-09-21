import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import babel from '@rolldown/plugin-babel'
import { lingui, linguiTransformerBabelPreset } from '@lingui/vite-plugin'

const __dirname = path.dirname(fileURLToPath(import.meta.url))

// https://vite.dev/config/
export default defineConfig({
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
      '/api': { target: 'http://localhost:8990', changeOrigin: false },
      '/initialize.json': { target: 'http://localhost:8990', changeOrigin: false },
      '/signalr': {
        target: 'http://localhost:8990',
        ws: true,
        changeOrigin: false,
      },
    },
  },
})
