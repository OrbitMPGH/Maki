import { createHash } from 'node:crypto'
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import path from 'node:path'
import { build, type Plugin } from 'vite'

const SHELL_FILES = ['index.html', 'manifest.webmanifest', 'theme-init.js', 'favicon.svg']
const ASSET = /\.(?:js|css|woff2?|ttf|otf)$/
const CATALOG = /^catalog-.*\.js$/

function list(dir: string): string[] {
  if (!existsSync(dir)) return []
  return readdirSync(dir).filter((name) => statSync(path.join(dir, name)).isFile())
}

/**
 * Builds `sw.js` once the app bundle is on disk, so the precache list names the files this build
 * actually emitted. The nested build takes none of the parent's plugins: the worker has no React,
 * no Lingui and nothing to precompress until `precompress()` runs after it.
 */
export function serviceWorker(): Plugin {
  let root = ''
  let outDir = ''
  return {
    name: 'maki:service-worker',
    apply: 'build',
    configResolved(config) {
      root = config.root
      outDir = path.resolve(config.root, config.build.outDir)
    },
    async closeBundle() {
      const assets = list(path.join(outDir, 'assets'))
      const precache = [
        ...SHELL_FILES.filter((name) => existsSync(path.join(outDir, name))).map((name) => `/${name}`),
        ...list(path.join(outDir, 'icons'))
          .filter((name) => name.endsWith('.png'))
          .map((name) => `/icons/${name}`),
        ...assets.filter((name) => ASSET.test(name) && !CATALOG.test(name)).map((name) => `/assets/${name}`),
      ].sort()
      const catalogs = assets.filter((name) => CATALOG.test(name)).map((name) => `/assets/${name}`)

      // Hashed names change with their content; index.html and the public files do not, so their
      // bytes go into the id too.
      const hash = createHash('sha256')
      for (const url of precache) {
        hash.update(url)
        if (!url.startsWith('/assets/')) hash.update(readFileSync(path.join(outDir, url)))
      }
      const buildId = hash.digest('hex').slice(0, 12)

      await build({
        configFile: false,
        root,
        logLevel: 'warn',
        plugins: [],
        publicDir: false,
        define: {
          __PRECACHE__: JSON.stringify(precache),
          __CATALOGS__: JSON.stringify(catalogs),
          __BUILD_ID__: JSON.stringify(buildId),
        },
        build: {
          emptyOutDir: false,
          copyPublicDir: false,
          outDir,
          rolldownOptions: {
            input: path.resolve(root, 'src/sw/sw.ts'),
            output: { entryFileNames: 'sw.js', format: 'iife', codeSplitting: false },
          },
        },
      })
    },
  }
}
