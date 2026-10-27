---
title: Write docs
description: How the documentation site in site/ is built, where pages go, and how they reach the sidebar.
---

This page is for changing the documentation site you are reading. The site lives in `site/`. It is an [Astro](https://astro.build) project using [Starlight](https://starlight.astro.build), and CI builds it with Node 24.

## Run it locally

Use Node 24, then run these from `site/`:

```bash
cd site
npm ci
npm run dev
```

`npm run dev` starts a local server with live reload. `npm run build` writes the static site to `site/dist`, and `npm run preview` serves that build so you can check it.

## Where pages go

Pages live in `site/src/content/docs/`, one folder per section: `introduction/`, `features/`, `installation/`, `guides/`, `contribution/` and `qa/`. A page is a `.md` file, and it starts with frontmatter that has a `title` and a `description`:

```md
---
title: Ways to help
description: How to report a bug, request a feature, ask a question or help with translations.
---
```

Starlight asides work in `.md` files, such as `:::tip` and `:::caution`.

Internal links are absolute, with the `/Maki/` base and a trailing slash, for example `/Maki/contribution/translating/`. Follow the same form in every page.

## Add a page to the sidebar

The sidebar is written out by hand in the `sidebar` array of `site/astro.config.mjs`. Entries are the path under `docs/` with no extension, such as `'contribution/development'`. A page that is not listed there stays out of the navigation, so add it in the same change that creates it.

## Style

Use the same rules as the rest of the repo. Write plain, direct sentences in the second person, like a developer explaining something to a teammate. Leave out filler such as "leverage", "seamless", "robust" and "delve". Do not use em dashes.

## Deploy

[`.github/workflows/docs.yml`](https://github.com/OrbitMPGH/Maki/blob/main/.github/workflows/docs.yml) runs when `site/` or the workflow file changes. Every pull request runs `npm ci` and `npm run build` in `site/`, so a build error shows up on the pull request. A push to `main` builds the site again and deploys it to GitHub Pages at `https://orbitmpgh.github.io/Maki/`.

Each page has an edit link that opens it on GitHub.
