import { existsSync, readdirSync } from 'node:fs'
import { join } from 'node:path'

export function findChrome() {
  if (process.env.CHROME_PATH) return process.env.CHROME_PATH

  const candidates = []
  const playwright = process.env.LOCALAPPDATA
    ? join(process.env.LOCALAPPDATA, 'ms-playwright')
    : join(process.env.HOME ?? '', '.cache', 'ms-playwright')
  if (existsSync(playwright)) {
    for (const dir of readdirSync(playwright).filter((d) => d.startsWith('chromium-'))) {
      candidates.push(
        join(playwright, dir, 'chrome-win64', 'chrome.exe'),
        join(playwright, dir, 'chrome-linux', 'chrome'),
      )
    }
  }
  candidates.push(
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  )

  const found = candidates.find((p) => existsSync(p))
  if (!found) throw new Error('No Chrome or Chromium found. Set CHROME_PATH to its executable.')
  return found
}
