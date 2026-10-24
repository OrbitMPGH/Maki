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
  } catch (e) {}
  var light =
    background === 'light' || background === 'paper' || (background === 'system' && !matchMedia('(prefers-color-scheme: dark)').matches)
  var known = Object.prototype.hasOwnProperty.call(grounds, background)
  var ground = groundOf[background] || (known ? background : 'tinted')
  var root = document.documentElement
  root.dataset.ground = ground
  root.dataset.accent = accents.indexOf(accent) < 0 ? 'blush' : accent
  root.dataset.theme = light ? 'light' : 'dark'
  var meta = document.querySelector('meta[name="theme-color"]')
  if (meta) meta.setAttribute('content', background === 'paper' ? '#efe9dd' : light ? '#f3f4fa' : grounds[known ? background : 'tinted'])
})()
