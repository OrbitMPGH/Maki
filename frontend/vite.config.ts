import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { brotliCompressSync, constants, gzipSync } from 'node:zlib'
import { defineConfig, loadEnv, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import babel from '@rolldown/plugin-babel'
import { lingui, linguiTransformerBabelPreset } from '@lingui/vite-plugin'

const __dirname = path.dirname(fileURLToPath(import.meta.url))

// Writes a .br and a .gz beside each text asset so the API serves them as they are instead of
// compressing every response; PrecompressedFileProvider on the API side picks them by Accept-Encoding.
const COMPRESSIBLE = /\.(?:js|css|html|svg|json|webmanifest|txt|xml)$/
const MIN_BYTES = 1024

function precompress(): Plugin {
  let outDir = ''
  const walk = (dir: string): string[] =>
    readdirSync(dir).flatMap((name) => {
      const full = path.join(dir, name)
      return statSync(full).isDirectory() ? walk(full) : [full]
    })
  return {
    name: 'maki:precompress',
    apply: 'build',
    configResolved(config) {
      outDir = path.resolve(config.root, config.build.outDir)
    },
    closeBundle() {
      for (const file of walk(outDir)) {
        if (!COMPRESSIBLE.test(file)) continue
        const source = readFileSync(file)
        if (source.length < MIN_BYTES) continue
        const br = brotliCompressSync(source, {
          params: {
            [constants.BROTLI_PARAM_QUALITY]: constants.BROTLI_MAX_QUALITY,
            [constants.BROTLI_PARAM_SIZE_HINT]: source.length,
          },
        })
        const gz = gzipSync(source, { level: constants.Z_BEST_COMPRESSION })
        if (br.length < source.length) writeFileSync(`${file}.br`, br)
        if (gz.length < source.length) writeFileSync(`${file}.gz`, gz)
      }
    },
  }
}

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  // `MAKI_API_URL` in `.env.<mode>` points the proxy at a backend other than the default 8990, so
  // a second instance (another config dir, another port) can be previewed beside the main one.
  const api = loadEnv(mode, __dirname, 'MAKI_').MAKI_API_URL || 'http://localhost:8990'
  return {
    plugins: [
      react(),
      precompress(),
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
        // Windows machine building the same commit, an alias resolves to an absolute path up
        // front and sidesteps that boundary check entirely.
        '@locales': path.resolve(__dirname, '../locales'),
      },
    },
    build: {
      rolldownOptions: {
        output: {
          advancedChunks: {
            groups: [
              // One chunk for the icon set instead of dozens of one-icon modules, each of which was
              // its own modulepreload request on a LAN served over HTTP/1.1.
              { name: 'icons', test: /node_modules[\\/]@tabler[\\/]icons-react/ },
            ],
          },
        },
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
