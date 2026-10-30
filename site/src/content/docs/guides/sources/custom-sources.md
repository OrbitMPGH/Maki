---
title: Custom sources
description: Add a site Maki doesn't ship with, using a JSON definition you build in the wizard, import from a file, or install from a repository.
---

A custom source follows a site that isn't built in, without waiting for a Maki release. It is a JSON
definition that says where the site's search, chapter list and page images are, using URL templates
and extractors (CSS selectors, regular expressions, or a path into JSON the page embeds). Nothing in
a definition runs as code. Maki's own engine reads the site with it.

Only admins can add, edit, import or delete custom sources. A saved source behaves like a built-in
one and appears in **Manage sources** with a **Custom** tag. See
[Ranking and choosing sources](/Maki/guides/sources/choosing-sources/).

## Set up a site

1. Open **Settings → Downloads → Sources**, select **Manage sources**, then **Add source → Set up a site**.
2. Paste the address of a series page from the site, and the address of a chapter page from that series.
3. Maki reads both pages and guesses the chapter list, the page images and the search. Each guess shows
   what it read from the live site.
4. Check the guesses. If Maki couldn't find something, describe it under **Advanced**, or edit the
   definition as a form or as JSON.
5. Run the test. It runs against the sample series and chapter, and each check shows as passed or failed.
   A new source can only be saved once the test passes.

- Sites on the Madara, MangaThemesia, Keyoapp and HeanCMS themes are recognised, so the guessing is
  skipped.
- If the wizard finds no search, the source still works, but you link each series by pasting its
  address on the series page.
- If the site needs FlareSolverr, the test offers to read it through FlareSolverr, and the source
  then uses it too. See [FlareSolverr for Cloudflare](/Maki/guides/sources/flaresolverr/).

You can also start from a series page: paste the site's address into **Link a source** and choose
**Set up this site as a source**.

## Import a definition

Select **Add source → Import definition**, then paste the JSON or open a `.json` file. Maki rejects
unknown fields, so a typo can't silently do nothing.

A saved source can be exported from its row in Manage sources. The export leaves out repository
details, so you can import it on another install or put it in a repository. A source's id can't
change after saving, so import a changed definition as a new source.

## Repositories

A repository is a list of definitions published at an address. Select **Add source → Repositories**
and enter the **Repository address**.

- Open a repository to see its definitions. **Install** copies one into your sources, and the source
  then shows its installed version.
- A definition whose id is already taken, by a hand-made source or another repository, is refused. It
  is never overwritten.
- Removing a repository doesn't remove its sources. They keep working as hand-made sources, and the
  confirmation lists them.
- Maki checks each repository once a day. **Check now** runs the check straight away.

Plain `http://` repositories can be added and installed from. Only an `https://` repository can keep its
sources up to date on its own.

## Updates

When a repository publishes a newer version of an installed source, the row shows **Update
available**. **Review update** shows the changes field by field, then **Apply update**.

With **Keep up to date** on for the repository, Maki applies new versions itself, but only when all
of these hold:

- The file matches the checksum the repository lists for it.
- The test passes against the new version's sample.
- The update doesn't change the source's identity. That means the same language, the same site address,
  the same sample chapter numbers and ids, and the same image hosts.

An update that fails any of these waits as **Update available** with the reason shown, and the source
keeps running its current version.

Each automatic update is logged and raises an admin notification naming the source and both
versions. **Revert to the previous version** goes back one step.

Editing a definition installed from a repository marks it **Edited locally**, and its updates stop
applying so your fix isn't overwritten.

## Health checks

Every six hours Maki lists the sample series' chapters on each enabled custom source. If that
fails, it tries one other linked series before reporting a problem, so one taken-down sample doesn't
mark a working source as broken.

A failing source shows **Not working** with a note that the site's layout may have changed. Edit the
definition to fix it. Disabled sources aren't checked.

## Limits

- A definition can be at most 32 KB.
- A repository's index can be at most 256 KB.
- A fetched page can be at most 8 MB.
- A chapter list is capped at 5,000 chapters, and a list can span at most 50 pages.
- A chapter's page list is capped at 1,000 pages.
- A regular expression that runs longer than 250 ms counts as a failed extraction.
- A definition publishes one language.
- Sites that scramble their page images, need signed API calls, or need a POST search or a cookie-gated search
  can't be described yet. Those still need a built-in source.

The full design, including the schema, is in the
[custom sources plan](https://github.com/OrbitMPGH/Maki/blob/main/docs/custom-sources-plan.md).
