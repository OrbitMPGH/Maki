import { spawn, spawnSync } from 'node:child_process'
import { createWriteStream } from 'node:fs'
import { join } from 'node:path'

export async function isUp(base) {
  try {
    const res = await fetch(`${base}/api/v1/system/status`, { signal: AbortSignal.timeout(2000) })
    return res.status === 200 || res.status === 401
  } catch {
    return false
  }
}

export function build(repo) {
  console.log('Building Maki.Api (Debug, rebuilds the SPA when its sources changed)...')
  const r = spawnSync('dotnet', ['build', join(repo, 'src', 'Maki.Api'), '-c', 'Debug', '-v', 'q', '-nologo'], { stdio: 'inherit', shell: false })
  if (r.status !== 0) throw new Error('dotnet build failed')
}

export async function start({ repo, configDir, sharedDumpDir, base, logPath }) {
  const env = { ...process.env, MAKI_CONFIG_DIR: configDir }
  if (sharedDumpDir) env.MAKI_SHARED_DUMP_DIR = sharedDumpDir
  const log = createWriteStream(logPath)
  const child = spawn('dotnet', ['run', '--no-build', '--project', join(repo, 'src', 'Maki.Api')], { env, cwd: repo })
  child.stdout.pipe(log)
  child.stderr.pipe(log)
  let exited = false
  child.on('exit', () => (exited = true))

  const deadline = Date.now() + 180000
  while (!(await isUp(base))) {
    if (exited) throw new Error(`The backend exited during startup. See ${logPath}`)
    if (Date.now() > deadline) throw new Error(`The backend did not answer within three minutes. See ${logPath}`)
    await new Promise((r) => setTimeout(r, 1000))
  }
  // Startup jobs (health checks, recommendation warm-up) settle in the first seconds.
  await new Promise((r) => setTimeout(r, 5000))
  return {
    async stop() {
      if (exited) return
      if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' })
      else child.kill('SIGINT')
      const until = Date.now() + 20000
      while (!exited && Date.now() < until) await new Promise((r) => setTimeout(r, 250))
      log.end()
    },
  }
}
