// Sets the theme attributes before first paint. theme-context.tsx owns them after mount.
;(function () {
  var grounds = { tinted: '#0b0b0b', night: '#0b0d13', charcoal: '#0f0f0f', moss: '#0e100e', cover: '#0b0b0b', oled: '#000000' }
  // Background ids that name a different ground in theme.css.
  var groundOf = { oled: 'ink', paper: 'paper' }
  var accents = ['indigo', 'rose', 'blush', 'emerald', 'amber', 'vermilion', 'teal', 'plum']
  var background = 'tinted'
  var accent = 'blush'
  try {
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
