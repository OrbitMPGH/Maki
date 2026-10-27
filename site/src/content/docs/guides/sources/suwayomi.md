---
title: Suwayomi extensions
description: Use the sources of the extensions installed in Suwayomi, with its supported versions, sign-in modes and mixed-language sources.
---

[Suwayomi](https://github.com/Suwayomi/Suwayomi-Server) is a separate server that runs Mihon-style
extensions. If you run it, Maki can use the sources of the extensions installed there. This is
optional, and nothing changes without a Suwayomi URL.

Install and update extensions in Suwayomi's own web UI. Maki reads search results, chapter lists and
pages through Suwayomi's API, so a source that works in Suwayomi usually works in Maki.

## Connect Maki to Suwayomi

1. Run Suwayomi. The [companion services](/Maki/installation/companions/) page has a compose service
   for it. Suwayomi listens on port `4567`.
2. In Maki, open **Settings → Integrations → Suwayomi**.
3. Enter the **URL**, such as `http://suwayomi:4567` in the same compose file. Include the subpath if
   Suwayomi runs under one.
4. Set **Sign-in** to match Suwayomi's own auth mode (see below).
5. Select **Test**, then save.

The test checks the server version. Suwayomi v2.1 or later is supported, and UI login needs v2.2 or
later, so an older server fails the test if you choose it. Changing the URL clears the saved
password, so enter it again.

## Sign-in modes

- **None**: Suwayomi has no login.
- **Basic auth**: Maki sends the username and password with each request.
- **UI login (v2.2 or later)**: Maki signs in the way the Suwayomi web UI does and renews the session
  itself.

## Using the sources

Suwayomi's installed sources appear in **Manage sources** marked **Suwayomi**, all switched off at
first. Turn on the ones you want and rank them like any other source. See
[Ranking and choosing sources](/Maki/guides/sources/choosing-sources/).

Maki refreshes the list when you save the connection and each time you open Manage sources. A source
Suwayomi no longer lists is marked **No longer in Suwayomi** and kept only while series are linked
to it.

A series has to exist in Suwayomi before Maki can open it. Maki can't look up a manga Suwayomi has
never seen, so search for the series once in Suwayomi's own UI first.

## Mixed-language sources

Some extensions publish several languages from one source. Suwayomi reports these as `all`, `multi`
or `other` and doesn't say which language each chapter is in, so Maki asks you.

A mixed-language source stays off, whatever its switch says, until you choose a chapter language for
it in **Manage sources**. Every chapter from it is then filed under that language, which also goes
into the file's metadata. Maki never assumes English.
