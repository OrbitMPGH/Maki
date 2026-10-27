---
title: Q&A
description: Short answers to common questions.
---

## Install

### Why does a dashboard show Maki using so much memory?

Most dashboards count the kernel's file cache as the container's memory. Set `mem_reservation`
instead of a hard limit. See [Memory](/Maki/installation/docker-compose/#memory).

### Can I use my existing Kavita library?

Yes. Maki writes plain CBZ with ComicInfo.xml. Point Kavita and Maki at the same folder, or add the
folder to Maki as a library folder.

### Do offline reading and push notifications work over plain HTTP?

No. They need HTTPS. See [Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/).

## Sources

### A source returns a challenge page or fails. What now?

Sources marked "requires FlareSolverr" in the [source list](/Maki/guides/sources/built-in-sources/#source-list)
sit behind Cloudflare. Run FlareSolverr and set its URL under **Settings → Downloads → FlareSolverr**.

### A site changed its domain.

Some sources have a mirror override set by an environment variable, such as
`MAKI_SOURCE_TCBSCANS_BASEURL`. They are listed in the source list.

## Reading

### Which apps can I read with?

Maki's own reader, KOReader, Panels, Chunky, Mihon (over OPDS or the Komga extension) and any
comic reader that opens CBZ. See [Reading over OPDS and Komga](/Maki/guides/reading/opds-apps/).

## Recommendations

### Discover is empty.

The local database has to finish downloading first, then Maki needs a few series you have
actually read. See [How recommendations work](/Maki/guides/recommendations/how-it-works/).

## Still stuck?

Open an issue on [GitHub](https://github.com/OrbitMPGH/Maki/issues).
