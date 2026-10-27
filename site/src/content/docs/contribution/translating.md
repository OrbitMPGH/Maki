---
title: Translating Maki
description: How the eleven languages are maintained and how to help.
---

The interface ships in English, German, French, Spanish, Brazilian Portuguese, Polish, Russian, Turkish, Japanese, Simplified Chinese and Korean. Pick one under **Settings → Preferences → Language**, or leave it on Automatic to follow the browser. The choice is per account. **Title language** is a separate setting.

English is the source language. **Everything else is machine-translated and marked as needing review**, so expect awkward phrasing. Untranslated strings fall back to English.

## Contribute corrections

- **Weblate (easiest):** sign in to [Maki on Hosted Weblate](https://hosted.weblate.org/engage/maki/), pick a language, and fix or approve strings. Weblate sends the changes back as a pull request. The translated percentage counts only human-approved strings, so it starts low.
- **In the repo:** the catalogs are gettext PO files under `locales/`, one directory per language.
- **Issue:** quote the string and what it should say.

[`scripts/i18n/glossary.md`](https://github.com/OrbitMPGH/Maki/blob/main/scripts/i18n/glossary.md) lists the terms that stay in English.
