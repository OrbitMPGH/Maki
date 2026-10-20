import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, mergeConfig } from 'vitest/config'
import { storybookTest } from '@storybook/addon-vitest/vitest-plugin'
import { playwright } from '@vitest/browser-playwright'
import viteConfig from './vite.config'

const dirname = path.dirname(fileURLToPath(import.meta.url))

// vite.config.ts exports a function, which mergeConfig refuses, so it is resolved per env first.
export default defineConfig((env) =>
  mergeConfig(viteConfig(env), {
    // Lazily imported pages pull these in mid-run; Vite then optimizes them and reloads the test
    // ("Vite unexpectedly reloaded a test"), which on a cold cache (CI) fails whatever was running.
    optimizeDeps: {
      include: ['@mantine/charts', 'recharts', 'motion/react', 'qrcode', '@microsoft/signalr', 'idb', 'msw', 'msw/browser'],
    },
    test: {
      // Each worker is a Chromium instance loading the whole app; the default (up to 12 on a big
      // machine) ran past 12GB of browsers.
      maxWorkers: 4,
      projects: [
        {
          extends: true,
          plugins: [
            storybookTest({
              configDir: path.join(dirname, '.storybook'),
              storybookScript: 'npm run storybook -- --no-open',
            }),
          ],
          test: {
            name: 'storybook',
            // A fresh iframe per file reloads the whole app 202 times: 97% of a CI run, and each
            // closing iframe churns MSW's service worker clients. The preview resets state per story.
            isolate: false,
            browser: {
              enabled: true,
              // Paired with TestMotion in .storybook/preview.tsx: Mantine skips its transitions.
              provider: playwright({ contextOptions: { reducedMotion: 'reduce' } }),
              headless: true,
              // Vitest defaults to a 414px phone viewport, which would test every page's mobile
              // layout. Stories that want a phone set `parameters.viewport` themselves.
              viewport: { width: 1280, height: 800 },
              instances: [{ browser: 'chromium' }],
            },
          },
        },
      ],
    },
  }),
)
