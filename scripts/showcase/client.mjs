export class MakiClient {
  constructor(base) {
    this.base = base.replace(/\/$/, '')
    this.cookies = new Map()
  }

  remember(res) {
    for (const line of res.headers.getSetCookie()) {
      const [pair] = line.split(';')
      const i = pair.indexOf('=')
      this.cookies.set(pair.slice(0, i).trim(), pair.slice(i + 1).trim())
    }
  }

  headers(extra = {}) {
    const headers = { 'Content-Type': 'application/json', Origin: this.base, ...extra }
    if (this.cookies.size > 0) {
      headers.Cookie = [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; ')
    }
    const xsrf = this.cookies.get('XSRF-TOKEN')
    if (xsrf) headers['X-XSRF-TOKEN'] = decodeURIComponent(xsrf)
    if (this.apiKey) headers['X-Api-Key'] = this.apiKey
    return headers
  }

  async request(method, path, body) {
    const res = await fetch(`${this.base}/api/v1${path}`, {
      method,
      headers: this.headers(),
      body: body === undefined ? undefined : JSON.stringify(body),
    })
    this.remember(res)
    const text = await res.text()
    const data = text ? JSON.parse(text) : null
    if (!res.ok) throw new Error(`${method} ${path} returned ${res.status}: ${text.slice(0, 300)}`)
    return data
  }

  get(path) {
    return this.request('GET', path)
  }

  post(path, body) {
    return this.request('POST', path, body ?? {})
  }

  put(path, body) {
    return this.request('PUT', path, body)
  }
}
