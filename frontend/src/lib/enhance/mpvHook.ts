// Runs mpv user-shader files (//!HOOK ... vec4 hook()) in WebGL2. Ported from the shader lab.

export interface HookPass {
  hook: string
  binds: string[]
  save?: string
  width?: string
  height?: string
  code: string
}

export interface Size {
  w: number
  h: number
}

export interface Tex extends Size {
  tex: WebGLTexture
  fb: WebGLFramebuffer
}

export function parse(text: string): HookPass[] {
  const raw: { d: Record<string, string>; binds: string[]; code: string[] }[] = []
  let cur: (typeof raw)[number] | null = null
  let header = false
  for (const line of text.split(/\r?\n/)) {
    if (line.startsWith('//!')) {
      if (!cur || !header) {
        cur = { d: {}, binds: [], code: [] }
        raw.push(cur)
        header = true
      }
      const m = line.slice(3).match(/^(\S+)\s*(.*)$/)
      if (!m) continue
      if (m[1] === 'BIND') cur.binds.push(m[2].trim())
      else cur.d[m[1]] = m[2].trim()
    } else if (cur) {
      header = false
      cur.code.push(line)
    }
  }
  return raw
    .filter((p) => p.d.HOOK)
    .map((p) => ({
      hook: p.d.HOOK.split(/\s+/)[0],
      binds: p.binds,
      save: p.d.SAVE,
      width: p.d.WIDTH,
      height: p.d.HEIGHT,
      code: p.code.join('\n'),
    }))
}

// mpv size expressions are reverse Polish: `conv2d_last_tf.w 2 *` is twice that texture's width.
// `NAME.w`/`NAME.h` push a texture size, numbers push themselves, `!` negates, the rest are binary.
export function rpn(expr: string, size: (name: string) => Size): number {
  const st: number[] = []
  for (const t of expr.split(/\s+/).filter(Boolean)) {
    const m = t.match(/^([A-Za-z_0-9]+)\.(w|h)$/)
    if (m) {
      const s = size(m[1])
      st.push(m[2] === 'w' ? s.w : s.h)
    } else if (/^-?[0-9.]+(e-?[0-9]+)?$/.test(t)) st.push(parseFloat(t))
    else if (t === '!') st.push(st.pop() ? 0 : 1)
    else {
      const b = st.pop() ?? 0
      const a = st.pop() ?? 0
      const ops: Record<string, number> = { '+': a + b, '-': a - b, '*': a * b, '/': a / b, '>': +(a > b), '<': +(a < b), '=': +(a === b) }
      if (!(t in ops)) throw new Error(`rpn: unknown token ${t}`)
      st.push(ops[t])
    }
  }
  const r = st.pop()
  if (r === undefined || !Number.isFinite(r)) throw new Error(`rpn: bad expression ${expr}`)
  return r
}

export function fragFor(code: string, names: string[]): string {
  let s =
    '#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\nin vec2 v_pos;\nout vec4 fragColor_;\nuniform vec2 target_size;\nuniform vec2 input_size;\n'
  for (const n of names) {
    s += `uniform sampler2D ${n}_raw;\nuniform vec2 ${n}_size;\n#define ${n}_pos v_pos\n#define ${n}_pt (vec2(1.0)/${n}_size)\n#define ${n}_mul 1.0\n#define ${n}_tex(p) texture(${n}_raw, (p))\n#define ${n}_texOff(o) texture(${n}_raw, v_pos + (vec2(1.0)/${n}_size)*(o))\n`
    // textureGather emulation: mpv's gather returns the 2x2 footprint in the order
    // (0,1) (1,1) (1,0) (0,0) relative to the top-left texel, matching GLSL textureGather.
    s += `vec4 ${n}_gather(vec2 p, int c){ vec2 t = p*${n}_size - 0.5; ivec2 i = ivec2(floor(t)); ivec2 m = ivec2(${n}_size) - 1;\n return vec4(texelFetch(${n}_raw, clamp(i+ivec2(0,1), ivec2(0), m), 0)[c], texelFetch(${n}_raw, clamp(i+ivec2(1,1), ivec2(0), m), 0)[c], texelFetch(${n}_raw, clamp(i+ivec2(1,0), ivec2(0), m), 0)[c], texelFetch(${n}_raw, clamp(i, ivec2(0), m), 0)[c]); }\n`
  }
  return s + code + '\nvoid main(){ fragColor_ = hook(); }\n'
}

const VS = `#version 300 es
const vec2 P[3] = vec2[3](vec2(-1.0,-1.0), vec2(3.0,-1.0), vec2(-1.0,3.0));
out vec2 v_pos;
void main(){ vec2 p = P[gl_VertexID]; v_pos = p*0.5+0.5; gl_Position = vec4(p,0.0,1.0); }`

const RESAMPLE = `#version 300 es
precision highp float;
out vec4 o;
uniform sampler2D A_raw;
uniform float dest_h;
uniform vec2 uv_scale;
uniform vec2 uv_offset;
void main(){
  vec2 p = vec2(gl_FragCoord.x, dest_h - gl_FragCoord.y);
  o = vec4(clamp(texture(A_raw, p * uv_scale + uv_offset).rgb, 0.0, 1.0), 1.0);
}`

interface Program {
  prog: WebGLProgram
  loc: Map<string, WebGLUniformLocation | null>
}

interface CompiledPass extends HookPass {
  names: string[]
  frag: string
}

export class HookRunner {
  private readonly gl: WebGL2RenderingContext
  private readonly programs = new Map<string, Program>()
  private readonly files = new Map<string, CompiledPass[]>()
  private readonly free = new Map<string, Tex[]>()
  private readonly used = new Set<Tex>()
  private readonly vao: WebGLVertexArrayObject

  constructor(gl: WebGL2RenderingContext) {
    this.gl = gl
    this.vao = gl.createVertexArray()
  }

  passes(text: string): CompiledPass[] {
    let p = this.files.get(text)
    if (!p) {
      p = parse(text).map((pass) => {
        const names = pass.binds.includes('HOOKED') ? [...pass.binds] : [...pass.binds, 'HOOKED']
        return { ...pass, names, frag: fragFor(pass.code, names) }
      })
      this.files.set(text, p)
    }
    return p
  }

  /** Compiles every pass up front so a compile error surfaces before any work is queued. */
  prepare(files: string[]) {
    for (const f of files) for (const pass of this.passes(f)) this.program(pass.frag)
  }

  private program(fs: string): Program {
    const hit = this.programs.get(fs)
    if (hit) return hit
    const gl = this.gl
    const mk = (type: number, src: string) => {
      const s = gl.createShader(type)
      if (!s) throw new Error('createShader failed')
      gl.shaderSource(s, src)
      gl.compileShader(s)
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(`compile: ${gl.getShaderInfoLog(s)}`)
      return s
    }
    const prog = gl.createProgram()
    const vs = mk(gl.VERTEX_SHADER, VS)
    const fsh = mk(gl.FRAGMENT_SHADER, fs)
    gl.attachShader(prog, vs)
    gl.attachShader(prog, fsh)
    gl.linkProgram(prog)
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(`link: ${gl.getProgramInfoLog(prog)}`)
    gl.deleteShader(vs)
    gl.deleteShader(fsh)
    const p = { prog, loc: new Map() }
    this.programs.set(fs, p)
    return p
  }

  private uniform(p: Program, name: string) {
    if (!p.loc.has(name)) p.loc.set(name, this.gl.getUniformLocation(p.prog, name))
    return p.loc.get(name) ?? null
  }

  private newTex(w: number, h: number, fmt: number): Tex {
    const gl = this.gl
    const tex = gl.createTexture()
    gl.bindTexture(gl.TEXTURE_2D, tex)
    gl.texStorage2D(gl.TEXTURE_2D, 1, fmt, w, h)
    setSampling(gl)
    const fb = gl.createFramebuffer()
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb)
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0)
    return { tex, fb, w, h }
  }

  /** A half-float render target from the pool; textures are reused across passes and jobs by size. */
  acquire(w: number, h: number): Tex {
    const key = `${w}x${h}`
    const t = this.free.get(key)?.pop() ?? this.newTex(w, h, this.gl.RGBA16F)
    this.used.add(t)
    return t
  }

  releaseTex(t: Tex) {
    const key = `${t.w}x${t.h}`
    const list = this.free.get(key) ?? []
    if (!list.includes(t)) list.push(t)
    this.free.set(key, list)
  }

  /** Frees pooled textures the last job did not touch, so memory follows the current page size. */
  trim() {
    for (const [key, list] of this.free) {
      const keep = list.filter((t) => this.used.has(t))
      for (const t of list) if (!this.used.has(t)) this.destroy(t)
      if (keep.length) this.free.set(key, keep)
      else this.free.delete(key)
    }
    this.used.clear()
  }

  destroy(t: Tex) {
    this.gl.deleteFramebuffer(t.fb)
    this.gl.deleteTexture(t.tex)
  }

  dispose() {
    for (const list of this.free.values()) for (const t of list) this.destroy(t)
    this.free.clear()
    this.used.clear()
    for (const p of this.programs.values()) this.gl.deleteProgram(p.prog)
    this.programs.clear()
    this.gl.deleteVertexArray(this.vao)
  }

  private draw(p: Program, binds: [string, Tex][], fb: WebGLFramebuffer, w: number, h: number, target: Size, input: Size) {
    const gl = this.gl
    gl.useProgram(p.prog)
    gl.bindVertexArray(this.vao)
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb)
    gl.viewport(0, 0, w, h)
    binds.forEach(([n, t], i) => {
      gl.activeTexture(gl.TEXTURE0 + i)
      gl.bindTexture(gl.TEXTURE_2D, t.tex)
      gl.uniform1i(this.uniform(p, `${n}_raw`), i)
      gl.uniform2f(this.uniform(p, `${n}_size`), t.w, t.h)
    })
    gl.uniform2f(this.uniform(p, 'target_size'), target.w, target.h)
    gl.uniform2f(this.uniform(p, 'input_size'), input.w, input.h)
    gl.drawArrays(gl.TRIANGLES, 0, 3)
  }

  /**
   * Runs every file in order against `src`; returns the final MAIN, which the caller releases.
   * `OUTPUT` in size expressions is `target`, held at 2x the source like the lab, never the display size.
   */
  runFiles(src: Tex, files: string[], target: Size): Tex {
    const reg = new Map<string, Tex>([
      ['MAIN', src],
      ['LUMA', src],
    ])
    const drop = (t: Tex | undefined) => {
      if (!t || t === src) return
      for (const v of reg.values()) if (v === t) return
      this.releaseTex(t)
    }
    for (const text of files) {
      for (const pass of this.passes(text)) {
        const hooked = reg.get(pass.hook)
        if (!hooked) throw new Error(`hook: no texture ${pass.hook}`)
        const size = (n: string): Size => {
          const s = n === 'OUTPUT' ? target : n === 'HOOKED' ? hooked : reg.get(n)
          if (!s) throw new Error(`size: no texture ${n}`)
          return s
        }
        const w = pass.width ? Math.round(rpn(pass.width, size)) : hooked.w
        const h = pass.height ? Math.round(rpn(pass.height, size)) : hooked.h
        const binds = pass.names.map((n): [string, Tex] => {
          const t = n === 'HOOKED' ? hooked : reg.get(n)
          if (!t) throw new Error(`bind: no texture ${n}`)
          return [n, t]
        })
        const out = this.acquire(w, h)
        const p = this.program(pass.frag)
        this.draw(p, binds, out.fb, w, h, target, src)
        const key = pass.save ?? pass.hook
        const prev = reg.get(key)
        reg.set(key, out)
        drop(prev)
      }
    }
    const main = reg.get('MAIN')
    if (!main) throw new Error('hook: no MAIN')
    for (const [k, v] of reg) {
      reg.delete(k)
      if (v !== main) drop(v)
    }
    return main
  }

  /**
   * Bilinear resample of `a` into the bound default framebuffer. Destination pixel (x, y), counted
   * from the top, samples `a` at `(x, y) * uvScale + uvOffset`. The caller sets the scissor.
   */
  resample(a: Tex, destW: number, destH: number, uvScale: [number, number], uvOffset: [number, number]) {
    const gl = this.gl
    const p = this.program(RESAMPLE)
    gl.useProgram(p.prog)
    gl.bindVertexArray(this.vao)
    gl.bindFramebuffer(gl.FRAMEBUFFER, null)
    gl.viewport(0, 0, destW, destH)
    gl.activeTexture(gl.TEXTURE0)
    gl.bindTexture(gl.TEXTURE_2D, a.tex)
    gl.uniform1i(this.uniform(p, 'A_raw'), 0)
    gl.uniform1f(this.uniform(p, 'dest_h'), destH)
    gl.uniform2f(this.uniform(p, 'uv_scale'), uvScale[0], uvScale[1])
    gl.uniform2f(this.uniform(p, 'uv_offset'), uvOffset[0], uvOffset[1])
    gl.drawArrays(gl.TRIANGLES, 0, 3)
  }

  prepareResample() {
    this.program(RESAMPLE)
  }
}

export function setSampling(gl: WebGL2RenderingContext) {
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE)
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE)
}
