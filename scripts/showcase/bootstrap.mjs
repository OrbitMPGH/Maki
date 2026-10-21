import { randomBytes, randomUUID } from 'node:crypto'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { MakiClient } from './client.mjs'
import { SERIES } from './series.mjs'
import { showcaseConfigDir } from './config.mjs'

const configDir = showcaseConfigDir()
const base = process.env.MAKI_URL ?? 'http://localhost:8990'
const credentialsPath = join(configDir, 'credentials.json')

const client = new MakiClient(base)
await client.get('/auth/me').catch(() => {})

let credentials = existsSync(credentialsPath) ? JSON.parse(readFileSync(credentialsPath, 'utf8')) : null
if (!credentials) {
  credentials = { username: 'Maki', password: randomBytes(18).toString('base64url') }
  await client.post('/auth/setup', { username: credentials.username, password: credentials.password })
  writeFileSync(credentialsPath, JSON.stringify(credentials, null, 2))
  console.log('Created the Maki account. Credentials are in credentials.json inside the config folder.')
} else {
  await client.post('/auth/login', { username: credentials.username, password: credentials.password })
  console.log('Signed in as the existing Maki account.')
}

if (typeof credentials.apiKey !== 'string') {
  const minted = await client.post('/account/apikeys', {
    name: 'screenshots',
    scope: 0,
    password: credentials.password,
  })
  credentials.apiKey = minted.secret
  writeFileSync(credentialsPath, JSON.stringify(credentials, null, 2))
  console.log('Minted an API key.')
}

const libraryPath = join(configDir, 'library')
let folders = await client.get('/rootfolder')
let folder = folders.find((f) => f.path === libraryPath)
if (!folder) {
  folder = await client.post('/rootfolder', { path: libraryPath })
  console.log(`Added root folder ${libraryPath}`)
}

const existing = await client.get('/series')
const have = new Set(existing.map((s) => String(s.mangaBakaId)))

let added = 0
for (const id of SERIES) {
  if (have.has(String(id))) continue
  try {
    await client.post('/series', {
      metadataProviderId: String(id),
      rootFolderId: folder.id,
      monitored: false,
      clientMutationId: randomUUID(),
    })
    added++
    console.log(`Added ${id}`)
  } catch (e) {
    console.warn(`Skipped ${id}: ${e.message}`)
  }
}
console.log(`Added ${added} series.`)
