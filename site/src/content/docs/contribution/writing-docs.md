---
title: Write docs
description: How the documentation site in site/ is built, where pages go, and how they reach the sidebar.
---

The docs site lives in `site/`. It is an [Astro](https://astro.build) project using [Starlight](https://starlight.astro.build), built in CI with Node 24.

## Run it locally

Use Node 24, then run these from `site/`:

```bash
cd site
npm ci
npm run dev
```

`npm run dev` serves with live reload. `npm run build` writes the static site to `site/dist`, and `npm run preview` serves that build.

## Where pages go

Pages live in `site/src/content/docs/`, one folder per section: `introduction/`, `features/`, `installation/`, `guides/`, `reference/`, `contribution/` and `qa/`. A page is a `.md` file with a `title` and `description` in its frontmatter:

```md
---
title: Ways to help
description: How to report a bug, request a feature, ask a question or help with translations.
---
```

Starlight asides such as `:::tip` and `:::caution` work in `.md` files.

Internal links are absolute, with the `/Maki/` base and a trailing slash, for example `/Maki/contribution/translating/`.

## Add a page to the sidebar

The sidebar is a hand-written `sidebar` array in `site/astro.config.mjs`. Entries are the path under `docs/` without an extension, such as `'contribution/development'`. An unlisted page stays out of the navigation, so add it in the same change.

## Style

Same rules as the rest of the repo: plain, direct second-person sentences, no filler such as "leverage", "seamless", "robust" or "delve", and no em dashes. See [Pull request conventions](/Maki/contribution/pull-requests/#writing-style).

## Deploy

[`.github/workflows/docs.yml`](https://github.com/OrbitMPGH/Maki/blob/main/.github/workflows/docs.yml) runs when `site/` or the workflow file changes. Pull requests run `npm ci` and `npm run build` in `site/`, so build errors show up there. A push to `main` builds again and deploys to GitHub Pages at `https://orbitmpgh.github.io/Maki/`. Each page has an edit link to GitHub.
