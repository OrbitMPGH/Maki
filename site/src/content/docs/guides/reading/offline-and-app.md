---
title: Install as an app and read offline
description: Add Maki to your home screen, save chapters and get push notifications.
---

Offline reading and push notifications need Maki served over **HTTPS** (or opened on `localhost`).
Browsers only allow service workers, offline storage and push on a secure connection, so on plain
`http://192.168.x.x:8990` these features are hidden. Put Maki behind a reverse proxy with a
certificate, as in [Exposing Maki to the internet](/Maki/installation/reverse-proxy-https/). "Add to
Home Screen" still works over plain HTTP, without the offline parts.

- **Saving.** Use **Save for offline** in a chapter row's menu, or **Save unread chapters for
  offline** on a series. Or turn on **Keep upcoming chapters** under **Settings → Reading → Offline
  reading** to keep your next unread chapters (up to 10) on the device. That page also shows
  storage use and everything saved. Saved chapters are stored per device and per account, and
  signing out keeps them. A saved copy is removed when its file is gone from the server or you lose
  access to it.
- **Browsing offline.** The app, covers, Home, Library and the series pages you have visited open
  without a connection. Library's **Saved on this device** chip lists what you can read, and a slim
  banner says when you're offline. An unreachable server never signs you out.
- **Progress.** Pages read offline are queued on the device and sent when Maki is reachable again.
  Reading time lands on the day you actually read.
- **iPhone and iPad.** Safari clears site data after seven days without use unless Maki is added to
  the home screen, and only delivers push to the installed app. Install it before saving chapters.
- **Push.** Turn it on per device under **Settings → Preferences → Push notifications** (**Send
  test** checks it). The Notifications table has a Push column next to Bell for picking events. New
  chapters and finished downloads push by default. Up to 20 devices per account.

