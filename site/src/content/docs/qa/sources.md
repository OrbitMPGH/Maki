---
title: Sources and Cloudflare
description: FlareSolverr for challenged sites, mirror domains, Suwayomi and custom sources.
---

### A source returns a challenge page or fails. What now?

Cloudflare is serving a challenge page. Sources marked "requires FlareSolverr" in the [source list](/Maki/guides/sources/built-in-sources/#source-list) need it, and some others use it only when challenged. Run FlareSolverr, set its URL under **Settings → Downloads → FlareSolverr** and press **Test**. The [Docker Compose guide](/Maki/installation/docker-compose/#optional-companions) has a compose file for it.

### A site changed its domain. How do I point Maki at the new one?

Some sources read their address from an environment variable, such as `MAKI_SOURCE_TCBSCANS_BASEURL`. Set it and restart Maki. The full list is in [Environment variables](/Maki/reference/environment-variables/#source-domains) and the steps in [Mirror overrides](/Maki/guides/sources/mirror-overrides/).

### Do I need to install extensions to get more sources?

No, sources are built in. If you run [Suwayomi](https://github.com/Suwayomi/Suwayomi-Server), set its URL under **Settings → Integrations → Suwayomi**, press **Test**, then enable its extensions under **Manage sources**. See [Built-in sources](/Maki/guides/sources/built-in-sources/).

### Can I add a site that Maki does not support?

Yes, as a custom source. Under **Manage sources**, choose **Add source → Set up a site** and paste a series page and a chapter page. Maki guesses how to read the site and tests it before saving. Saved custom sources are health-checked every six hours. Sites that scramble pages or need signed API calls still need a built-in source. See [Custom sources](/Maki/guides/sources/custom-sources/).
