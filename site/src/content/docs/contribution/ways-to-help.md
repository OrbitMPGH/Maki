---
title: Ways to help
description: How to report a bug, request a feature, ask a question or help with translations.
---

Most of the useful help on Maki is a clear report, a translation fix or a source request. This page covers the ways to get involved and what to include in each.

## Report a bug

Open a bug report from the [issues page](https://github.com/OrbitMPGH/Maki/issues/new/choose). Blank issues are turned off, so the bug template is the only way in. It asks for:

- **What happened.** What went wrong and what you expected instead. Rough notes are fine.
- **Maki version.** Shown on the Settings page. "Not sure" is fine too.
- **How it is installed.** Docker, bare install, built from source, or not sure.
- **Anything else.** Steps to reproduce, screenshots and logs.

Steps to reproduce matter most. Write down what you did in order, starting from a state that anyone can get to, such as a fresh library with one series.

Logs are in the `logs` folder of Maki's config directory: `/config/logs` in Docker, `%APPDATA%\Maki\logs` on Windows. Find the lines marked `[ERR]` or `[WRN]` around the time the problem happened, and paste those with a little context.

:::caution
Scrub API keys, passwords and tokens from logs and screenshots before you post them.
:::

Do not report security problems in a public issue. Use the [private security advisory form](https://github.com/OrbitMPGH/Maki/security/advisories/new) instead.

## Request a feature

Use the feature request template. The one field that matters is the description of what you want Maki to do. Say what you are doing today and why that gets in the way. A problem described well is more useful than a finished design.

To ask for a site to be added as a source, use the **New source request** template. Only the site URL is required. If you know the languages the site covers, whether it sits behind Cloudflare, or whether it has a public API, add that too.

## Ask a question

Use [GitHub Discussions](https://github.com/OrbitMPGH/Maki/discussions) for setup help, ideas and anything where you are not sure whether it is a bug. A question in Discussions is easier to answer later than one buried in an issue.

## Translate

Translation fixes are one of the easiest ways to help, and you do not need to read any code. See [Translating Maki](/Maki/contribution/translating/) for the Weblate project and for working directly on the PO files.

## Change the code

If you want to write code, start with [Development](/Maki/contribution/development/) to get a build running. Then read [Architecture tour](/Maki/contribution/architecture/) to find the right project, and [Pull request conventions](/Maki/contribution/pull-requests/) before you open a pull request.
