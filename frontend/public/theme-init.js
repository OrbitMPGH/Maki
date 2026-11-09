// Sets the theme attributes before first paint. theme-context.tsx owns them after mount.
;(function () {
  var grounds = { tinted: '#0b0b0b', night: '#0b0d13', charcoal: '#0f0f0f', moss: '#0e100e', cover: '#0b0b0b', oled: '#000000' }
  // Background ids that name a different ground in theme.css.
  var groundOf = { oled: 'ink', paper: 'paper' }
  var accents = ['indigo', 'rose', 'blush', 'emerald', 'amber', 'vermilion', 'teal', 'plum']
  // Same table as LEGACY_PRESETS in theme-context.tsx.
  var legacyPresets = {
    indigo: ['night', 'indigo'],
    rose: ['night', 'rose'],
    blush: ['night', 'blush'],
    emerald: ['night', 'emerald'],
    amber: ['night', 'amber'],
    light: ['light', 'indigo'],
    system: ['system', 'indigo']
  }
  var background = 'tinted'
  var accent = 'blush'
  try {
    var legacy = localStorage.getItem('maki-theme')
    var mapped = legacy !== null && Object.prototype.hasOwnProperty.call(legacyPresets, legacy) ? legacyPresets[legacy] : null
    if (mapped) {
      background = mapped[0]
      accent = mapped[1]
    }
    background = localStorage.getItem('maki-background') || background
    accent = localStorage.getItem('maki-accent') || accent
    // "Faster animations" (lib/motionPrefs.ts), which shortens every --dur-* token in theme.css.
    if (localStorage.getItem('maki-fast-motion') === 'on') document.documentElement.dataset.motion = 'fast'
  } catch (e) {}
  var light =
    background === 'light' || background === 'paper' || (background === 'system' && !matchMedia('(prefers-color-scheme: dark)').matches)
  var known = Object.prototype.hasOwnProperty.call(grounds, background)
  var ground = groundOf[background] || (known ? background : 'tinted')
  var root = document.documentElement
  root.dataset.ground = ground
  root.dataset.accent = accents.indexOf(accent) < 0 ? 'blush' : accent
  root.dataset.theme = light ? 'light' : 'dark'
  // Mantine sets this on mount; set now, native scrollbars and controls paint light on a dark ground
  // until then.
  root.setAttribute('data-mantine-color-scheme', light ? 'light' : 'dark')
  // Each accent's --brand in theme.css, which the tinted, cover and paper grounds mix into --app-bg.
  var brands = { indigo: '#6d7dff', blush: '#ee7fa4', rose: '#f52069', emerald: '#1bc97a', amber: '#f0ad14', vermilion: '#ef5634', teal: '#17ab9d', plum: '#b84cbc' }
  var lightBrands = { blush: '#c4476e', teal: '#0b766d' }
  var brand = (light && lightBrands[root.dataset.accent]) || brands[root.dataset.accent]
  // color-mix(in srgb, base share%, brand), so the status bar matches the page from the first frame.
  function mix(base, share) {
    var out = '#'
    for (var i = 1; i < 7; i += 2) {
      var v = Math.round(parseInt(base.substr(i, 2), 16) * share + parseInt(brand.substr(i, 2), 16) * (1 - share))
      out += (v < 16 ? '0' : '') + v.toString(16)
    }
    return out
  }
  var bar = grounds[known ? background : 'tinted']
  if (background === 'paper') bar = mix('#efe9dd', 0.96)
  else if (light) bar = '#f3f4fa'
  else if (ground === 'tinted') bar = mix('#0b0b0b', 0.96)
  else if (ground === 'cover') bar = mix('#0b0b0b', 0.93)
  var meta = document.querySelector('meta[name="theme-color"]')
  if (meta) meta.setAttribute('content', bar)
})()

// Starts the resolved language's catalogue downloading beside the main chunk instead of after it.
// The build lists each catalogue's file in a meta tag; the pick mirrors resolveInitialLocale in
// i18n.ts (stored choice, then the browser's list, then English).
;(function () {
  try {
    var meta = document.querySelector('meta[name="maki-catalogs"]')
    if (!meta) return
    var files = JSON.parse(meta.getAttribute('content') || '{}')
    var codes = Object.keys(files)
    var match = function (tag) {
      var wanted = typeof tag === 'string' ? tag.trim().toLowerCase() : ''
      if (!wanted) return null
      var primary = wanted.split('-')[0]
      var i
      for (i = 0; i < codes.length; i++) if (codes[i].toLowerCase() === wanted) return codes[i]
      for (i = 0; i < codes.length; i++) if (codes[i].split('-')[0].toLowerCase() === primary) return codes[i]
      return null
    }
    var code = null
    try {
      code = match(localStorage.getItem('maki-language'))
    } catch (e) {}
    var tags = navigator.languages || [navigator.language]
    for (var i = 0; !code && i < tags.length; i++) code = match(tags[i])
    var href = files[code || 'en']
    if (!href) return
    var link = document.createElement('link')
    link.rel = 'modulepreload'
    link.href = href
    document.head.appendChild(link)
  } catch (e) {}
})()
