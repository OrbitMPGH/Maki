---
title: Ways to help
description: How to report a bug, request a feature, ask a question or help with translations.
---

## Report a bug

Open a bug report from the [issues page](https://github.com/OrbitMPGH/Maki/issues/new/choose). Blank issues are off, so the bug template is the only way in. It asks for:

- **What happened** and what you expected. Rough notes are fine.
- **Maki version**, shown in the sidebar footer.
- **How it is installed:** Docker, Windows installer, Linux tarball, bare install, built from source, or not sure.
- **Anything else:** steps to reproduce, screenshots, logs.

Steps to reproduce matter most. List what you did in order, starting from a state anyone can reach, such as a fresh library with one series.

Logs are in `/config/logs` in Docker and `%LOCALAPPDATA%\Maki\logs` for the Windows app. Paste the `[ERR]` or `[WRN]` lines from around the time of the problem, with a little context.

:::caution
Scrub API keys, passwords and tokens from logs and screenshots before you post them.
:::

Do not report security problems in a public issue. Use the [private security advisory form](https://github.com/OrbitMPGH/Maki/security/advisories/new) instead.

## Request a feature

Use the feature request template. Describe what you do today and why it gets in the way. A well-described problem is more useful than a finished design.

For a new site, use the **New source request** template. Only the site URL is required. Add the languages, whether it sits behind Cloudflare, and whether it has a public API if you know.

## Ask a question

Use [GitHub Discussions](https://github.com/OrbitMPGH/Maki/discussions) for setup help, ideas and anything that might not be a bug.

## Translate

No code needed. See [Translating Maki](/Maki/contribution/translating/) for Weblate and the PO files.

## Change the code

Start with [Development](/Maki/contribution/development/) to get a build running, then the [Architecture tour](/Maki/contribution/architecture/), and [Pull request conventions](/Maki/contribution/pull-requests/) before you open a pull request.
