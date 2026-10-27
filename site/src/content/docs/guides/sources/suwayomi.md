---
title: Suwayomi extensions
description: Use the sources of the extensions installed in Suwayomi, with its supported versions, sign-in modes and mixed-language sources.
---

[Suwayomi](https://github.com/Suwayomi/Suwayomi-Server) is a separate server that runs Mihon-style
extensions. If you already run it, Maki can use the sources of the extensions installed there. This
is optional. Without a Suwayomi URL set, nothing changes.

Install and update extensions in Suwayomi's own web UI. Maki doesn't install them. Maki reads search
results, chapter lists and pages through Suwayomi's API, so a source that works in Suwayomi usually
works in Maki too.

## Connect Maki to Suwayomi

1. Run Suwayomi. The [companion services](/Maki/installation/companions/) page has a compose service
   for it. Suwayomi listens on port `4567`.
2. In Maki, open **Settings → Integrations → Suwayomi**.
3. Enter the **URL** of Suwayomi, such as `http://suwayomi:4567` when it runs in the same compose file.
4. Set **Sign-in** to match Suwayomi's own auth mode setting. The options are described below.
5. If Suwayomi runs under a subpath, include it in the URL.
6. Select **Test**, then save.

Maki checks the server version during the test. Suwayomi v2.1 or later is supported. UI login needs
v2.2 or later, so an older server fails the test if you choose it.

Changing the URL clears the saved password, so enter it again.

## Sign-in modes

- **None**: Suwayomi has no login.
- **Basic auth**: Maki sends the username and password with each request.
- **UI login (v2.2 or later)**: Maki signs in with the username and password the way the Suwayomi
  web UI does, and renews the session itself.

If the sign-in fails, check that the password matches the one Suwayomi uses for that mode.

## Using the sources

Once connected, Suwayomi's installed sources appear in **Manage sources**, marked **Suwayomi**. They
are all switched off at first. Turn on the ones you want, then rank them like any other source. See
[Ranking and choosing sources](/Maki/guides/sources/choosing-sources/).

Maki refreshes this list when you save the connection and each time you open Manage sources. A source
that Suwayomi no longer lists is marked **No longer in Suwayomi**. Maki keeps it only while series are
still linked to it.

A series has to exist in Suwayomi before Maki can open it. Maki has no way to look up a manga that
Suwayomi has never seen, so search for the series once in Suwayomi's own UI first. Maki won't search
by title on its own in that case.

## Mixed-language sources

Some extensions publish several languages from one source. Suwayomi reports these as `all`, `multi` or
`other`, and it doesn't say which language each chapter is in. Maki asks you instead.

A mixed-language source stays off, whatever its switch says, until you choose a chapter language for it
in **Manage sources**. Every chapter from the source is then filed under that language, and the language
ends up in the file's metadata. Maki never assumes English for these sources.
