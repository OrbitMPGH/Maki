import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const defaultDir = resolve(root, '.screenshots')
const MARKER = '.showcase'

const norm = (p) => resolve(p).replace(/[\/]+$/, '').toLowerCase()

const realConfigDirs = [
  '/config',
  process.env.APPDATA && join(process.env.APPDATA, 'Maki'),
  process.env.HOME && join(process.env.HOME, '.config', 'Maki'),
].filter(Boolean).map(norm)

export function showcaseConfigDir() {
  const configDir = resolve(process.env.MAKI_CONFIG_DIR ?? defaultDir)
  if (realConfigDirs.includes(norm(configDir))) {
    throw new Error(`Refusing to touch ${configDir}: that is a real Maki config folder, not a showcase one.`)
  }

  const marker = join(configDir, MARKER)
  if (!existsSync(marker)) {
    if (norm(configDir) !== norm(defaultDir)) {
      throw new Error(
        `${configDir} is not marked as a showcase config. These scripts overwrite its history and files. ` +
          `If it really is a throwaway demo folder, create an empty ${MARKER} file in it and run again.`,
      )
    }
    mkdirSync(configDir, { recursive: true })
    writeFileSync(marker, 'Throwaway config for scripts/showcase. Safe to wipe.\n')
  }
  return configDir
}
