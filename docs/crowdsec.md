# Access log and CrowdSec

Maki can write every request to its own file, in the same combined format nginx and Apache use.
[CrowdSec](https://www.crowdsec.net/) or fail2ban can then read it and ban addresses that try to
guess passwords or scan the instance. This is meant for an instance that faces the internet; read
[Exposing Maki to the internet](exposing-to-the-internet.md) first.

## Turn on the access log

Set `AccessLog` to `true` in `config.json` (in the config folder, `/config` in Docker) and restart
Maki:

```json
{
  "AccessLog": true
}
```

Lines go to `logs/access-YYYYMMDD.log` next to the normal log. They roll daily and at
`LogFileSizeMb`, and `RetainedLogFiles` decides how many are kept, the same as the main log.

```
203.0.113.7 - - [30/Oct/2026:14:02:11 +0000] "POST /api/v1/auth/login HTTP/1.1" 401 0 "-" "Mozilla/5.0 ..."
203.0.113.7 - alice [30/Oct/2026:14:02:19 +0000] "GET /api/v1/reader/chapter/812/page/3 HTTP/1.1" 200 381204 "-" "Mozilla/5.0 ..."
```

The fields are client address, the signed-in account (`-` when nobody is signed in,
`opds-user-<id>` for an OPDS, Komga or KOReader token), time, request, status, response size,
referrer and user agent. The size is 0 for a compressed or streamed response.

The log never contains a credential. The OPDS token in feed URLs is written as `redacted`, and so
are the values of query parameters like `code`, `state`, `token` and `apikey`.

**Set Trusted proxies first if Maki is behind a reverse proxy.** Without it every line carries the
proxy's address, and the first ban would lock everyone out. See step 2 of
[Exposing Maki to the internet](exposing-to-the-internet.md).

## CrowdSec

The files in [`distribution/crowdsec`](../distribution/crowdsec) are:

| File | What it does |
| --- | --- |
| `acquis.d/maki.yaml` | Tells CrowdSec where the log is |
| `parsers/s01-parse/maki-logs.yaml` | Reads the lines into the same fields CrowdSec's nginx parser produces |
| `parsers/s02-enrich/maki-whitelist.yaml` | Keeps normal reading out of the generic HTTP scenarios |
| `scenarios/maki-bf.yaml` | Bans brute force on sign-in, two-factor, setup and the password prompts |

### Install

On the CrowdSec host (paths are for a package install; the Docker image uses the same layout under
`/etc/crowdsec`):

```sh
cp acquis.d/maki.yaml                   /etc/crowdsec/acquis.d/
cp parsers/s01-parse/maki-logs.yaml     /etc/crowdsec/parsers/s01-parse/
cp parsers/s02-enrich/maki-whitelist.yaml /etc/crowdsec/parsers/s02-enrich/
cp scenarios/maki-bf.yaml               /etc/crowdsec/scenarios/

cscli collections install crowdsecurity/base-http-scenarios crowdsecurity/http-cve
systemctl reload crowdsec
```

Edit the path in `acquis.d/maki.yaml` so it points at Maki's `logs` folder. If CrowdSec runs in its
own container, mount that folder into it read-only.

The two collections are optional but worth having: they add CrowdSec's generic scanner, bad user
agent, sensitive file and known-exploit scenarios, which work on Maki's lines because the parser
fills the same fields nginx's does.

You also need a bouncer to act on the bans, for example the firewall bouncer on the host or the
one for your reverse proxy (Traefik, Caddy, nginx, SWAG, Nginx Proxy Manager).

### What gets banned

`maki/maki-bf` counts, per address, refused attempts on:

- `POST auth/login`, `auth/2fa` and `auth/setup`
- the single sign-on endpoints under `auth/oidc/`
- the signed-in prompts that ask for your password again: change password, two-factor enable,
  disable and recovery codes, creating an API key, and the OPDS token

A refusal is a 400, 401, 403 or 409 on a `POST` or `PUT`, or a 429 from Maki's own limit of ten
attempts a minute. Six in quick succession (the bucket drains one every 30 seconds) is a ban.
`maki/maki-slow-bf` catches the same thing spread out: sixteen, draining one every five minutes.

Maki's own rate limit and account lockout keep working either way. CrowdSec adds a ban that covers
the whole instance, and shares the address with the CrowdSec network if you enrolled.

### What is whitelisted

A whitelisted line is dropped before every scenario. Three kinds are:

- Requests made by a signed-in account, an API key or a valid feed token, except the sign-in and
  password endpoints above. Opening a chapter fetches one page and one thumbnail per page, which
  looks exactly like the crawling CrowdSec's generic scenarios are written to catch.
- Image requests that were answered: covers (`mediacover`, the cover proxy, Suwayomi thumbnails),
  reader pages and thumbnails, source previews, the sign-in mosaic, and Komga page and thumbnail
  requests. A series with no poster answers 404 for every tile, so 404 counts as answered here.
  A 401 on these does not: that is someone who is not signed in.
- A 401 anywhere outside the sign-in and password endpoints. A tab whose session expired keeps
  trying to save reading progress, and Komga clients only send their token after a 401, so
  CrowdSec's generic "too many 401s" scenario would otherwise ban real readers.

Requests from nobody in particular (the sign-in page, a wrong OPDS token, a scanner hitting
`/wp-login.php`) stay visible to every scenario.

### Check it

```sh
cscli explain --file /path/to/maki/config/logs/access-20261030.log --type maki
cscli metrics show acquisition parsers
cscli decisions list
```

`cscli explain` shows each line going through the parser, the whitelist and the scenarios. Lines
that read `maki/maki-whitelist` under `s02-enrich` were whitelisted.

To unban yourself: `cscli decisions delete --ip <address>`.
