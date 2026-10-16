import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const configDir = process.env.MAKI_CONFIG_DIR ?? join(root, '.screenshots')
const base = process.env.MAKI_URL ?? 'http://localhost:8990'
const { apiKey } = JSON.parse(readFileSync(join(configDir, 'credentials.json'), 'utf8'))
const headers = { 'Content-Type': 'application/json', 'X-Api-Key': apiKey }

const res = await fetch(`${base}/api/v1/settings/ui`, { headers })
const ui = await res.json()
const glance = ui.homeLayout.sections.find((s) => s.key === 'glance')
if (glance) glance.enabled = false

const saved = await fetch(`${base}/api/v1/settings/ui`, { method: 'PUT', headers, body: JSON.stringify(ui) })
console.log(saved.ok ? 'Home: stats at a glance is off.' : `Could not save the layout (${saved.status}).`)
