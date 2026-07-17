interface InitializeInfo {
  apiRoot: string
  apiKey: string
  version: string
}

let initialize: InitializeInfo | null = null

export async function getInitialize(): Promise<InitializeInfo> {
  if (!initialize) {
    const res = await fetch('/initialize.json')
    if (!res.ok) throw new Error('Failed to initialize')
    initialize = (await res.json()) as InitializeInfo
  }
  return initialize
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const init = await getInitialize()
  const res = await fetch(`${init.apiRoot}${path}`, {
    ...options,
    headers: {
      'X-Api-Key': init.apiKey,
      'Content-Type': 'application/json',
      ...options.headers,
    },
  })
  if (!res.ok) {
    const body = await res.text()
    throw new Error(`API ${res.status}: ${body || res.statusText}`)
  }
  // 204, and any 200 whose handler wrote no body, have nothing to parse.
  const body = await res.text()
  if (!body) return undefined as T
  return JSON.parse(body) as T
}
