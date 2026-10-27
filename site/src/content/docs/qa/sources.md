---
title: Sources and Cloudflare
description: FlareSolverr for challenged sites, mirror domains, Suwayomi and custom sources.
---

### A source returns a challenge page or fails. What now?

Some sites sit behind Cloudflare, which answers with a challenge page instead of the series. Sources
marked "requires FlareSolverr" in the [source list](/Maki/guides/sources/built-in-sources/#source-list)
need it, and some others use it only when a site challenges them. Run FlareSolverr, set its URL under
**Settings → Downloads → FlareSolverr**, and press **Test**. The
[Docker Compose guide](/Maki/installation/docker-compose/#optional-companions) has a compose file that
runs it next to Maki.

### A site changed its domain. How do I point Maki at the new one?

Some sources read their address from an environment variable, such as `MAKI_SOURCE_TCBSCANS_BASEURL`
for TCB Scans. The source list names the variable for each source that has one. Set it in the
container's environment and restart Maki, because environment variables are read at startup.

### Do I need to install extensions to get more sources?

No. Sources are built into Maki, so there is nothing to install. If you already run
[Suwayomi](https://github.com/Suwayomi/Suwayomi-Server), set its URL under
**Settings → Integrations → Suwayomi**, press **Test**, then turn on the extensions you want under
**Manage sources**. See [Built-in sources](/Maki/guides/sources/built-in-sources/).

### Can I add a site that Maki does not support?

Yes, as a custom source. Under **Manage sources**, choose **Add source → Set up a site**, paste a
series page and a chapter page, and Maki guesses how to read the site and tests the result before it
saves anything. Health checks saved custom sources every six hours. Sites that scramble their pages or
need signed API calls still need a built-in source. The full format is in
[Built-in sources](/Maki/guides/sources/built-in-sources/).
