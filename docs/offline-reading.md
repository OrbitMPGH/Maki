# Install as an app and read offline

Offline reading and push notifications need Maki served over **HTTPS** (or opened on
`localhost`). Browsers only allow service workers, offline storage and push on a secure
connection, so on plain `http://192.168.x.x:8990` these features are hidden. Put Maki behind a
reverse proxy with a certificate, as in [Exposing Maki to the internet](exposing-to-the-internet.md).
"Add to Home Screen" still works over plain HTTP, without the offline parts.

- **Saving.** Use **Save for offline** on a chapter or in a series' actions menu, or turn on
  **Keep upcoming chapters** under **Settings → Reading → Offline reading**. Saved chapters are
  stored per device and per account, and signing out keeps them for when you sign back in.
- **Progress.** Pages read offline are queued on the device and sent when Maki is reachable
  again. Reading time lands on the day you actually read.
- **iPhone and iPad.** Safari clears site data after seven days without use unless Maki is added
  to the home screen, and only delivers push to the installed app. Install it before saving
  chapters.
- **Push.** Turn it on per device under **Settings → Preferences → Push notifications** and pick
  which events to send.

