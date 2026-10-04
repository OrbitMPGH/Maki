// Sets the theme attributes before first paint. theme-context.tsx owns them after mount.
;(function () {
  var grounds = { tinted: '#0b0b0b', night: '#0b0d13', charcoal: '#0f0f0f', moss: '#0e100e' }
  var accents = ['indigo', 'rose', 'blush', 'emerald', 'amber']
  var background = 'tinted'
  var accent = 'indigo'
  try {
    background = localStorage.getItem('maki-background') || background
    accent = localStorage.getItem('maki-accent') || accent
  } catch (e) {}
  var light =
    background === 'light' || (background === 'system' && !matchMedia('(prefers-color-scheme: dark)').matches)
  var ground = Object.prototype.hasOwnProperty.call(grounds, background) ? background : 'tinted'
  var root = document.documentElement
  root.dataset.ground = ground
  root.dataset.accent = accents.indexOf(accent) < 0 ? 'indigo' : accent
  root.dataset.theme = light ? 'light' : 'dark'
  var meta = document.querySelector('meta[name="theme-color"]')
  if (meta) meta.setAttribute('content', light ? '#f3f4fa' : grounds[ground])
})()
