---
title: Access log and CrowdSec
description: Turn on Maki's request log, point CrowdSec at it, and see what the shipped rules ban.
---

Maki can write every HTTP request to its own file, in the combined format nginx and Apache use.
CrowdSec reads that file and bans addresses that guess passwords or scan the instance. The access
log is off by default. Turn it on for an instance that faces the internet, and read
[Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/) first.

## Set trusted proxies first

If Maki sits behind a reverse proxy, list the proxy under **Settings → System → Network → Trusted
proxies** (an IP or CIDR, for example `172.18.0.0/16`), then restart Maki. Until you do, every
access log line shows the proxy's address. The first ban would then block the proxy and lock out
every reader.

## Turn on the access log

The access log has no switch in the interface. It is set in `config.json` in the config folder
(`/config` in Docker, `./maki-config/config.json` in the compose example).

1. Open `config.json` and set `AccessLog` to `true`. Maki adds the key with `false` the first time
   it starts after an upgrade, so it is already in the file.
2. Restart Maki. The setting is read at startup.

```json
{
  "AccessLog": true
}
```

Lines go to `logs/access-YYYYMMDD.log` in the same `logs` folder as the normal log. In Docker that
is `/config/logs`. A new file starts each day and when a file reaches `LogFileSizeMb` (20 by
default). `RetainedLogFiles` (7 by default) sets how many files are kept.

Every request is written, including the app's own JavaScript and images, so one page load writes
several lines. The access log ignores `HttpRequestLogging`, and it does not add lines to the normal
log.

```
203.0.113.7 - - [30/Oct/2026:14:02:11 +0000] "POST /api/v1/auth/login HTTP/1.1" 401 0 "-" "Mozilla/5.0 ..."
203.0.113.7 - alice [30/Oct/2026:14:02:19 +0000] "GET /api/v1/reader/chapter/812/page/3 HTTP/1.1" 200 381204 "-" "Mozilla/5.0 ..."
```

Each line has the client address, the signed-in account (`-` when nobody is signed in, or
`opds-user-<id>` for an OPDS, Komga or KOReader token), the time, the request, the status, the
response size, the referrer and the user agent. The size is 0 for a compressed or streamed
response.

The log holds no credentials. The OPDS token in a feed path is written as `redacted`. So are the
values of the query parameters `code`, `state`, `session_state`, `token`, `access_token`, `id_token`,
`apikey`, `api_key` and `password`, in the request and in the referrer. That list is fixed, so a
credential sent in a parameter with another name would be written.

## Install the CrowdSec files

Maki ships an acquisition file, two parsers and a scenario file in
[`distribution/crowdsec`](https://github.com/OrbitMPGH/Maki/tree/main/distribution/crowdsec).

1. Copy them from that folder to the CrowdSec host. These paths are for a package install; the
   Docker image uses the same layout under `/etc/crowdsec`.

```sh
cp acquis.d/maki.yaml                   /etc/crowdsec/acquis.d/
cp parsers/s01-parse/maki-logs.yaml     /etc/crowdsec/parsers/s01-parse/
cp parsers/s02-enrich/maki-whitelist.yaml /etc/crowdsec/parsers/s02-enrich/
cp scenarios/maki-bf.yaml               /etc/crowdsec/scenarios/
```

2. Point `acquis.d/maki.yaml` at Maki's `logs` folder. The file reads:

```yaml
# Point this at Maki's log folder: {ConfigDir}/logs, which is /config/logs in the Docker image.
# Turn the access log on first: "AccessLog": true in config.json, then restart Maki.
filenames:
  - /path/to/maki/config/logs/access-*.log
labels:
  type: maki
```

If CrowdSec runs in its own container, mount the logs folder into it read-only.

3. Install the two collections and reload CrowdSec:

```sh
cscli collections install crowdsecurity/base-http-scenarios crowdsecurity/http-cve
systemctl reload crowdsec
```

These add CrowdSec's generic scanner, bad user agent, sensitive file and known-exploit scenarios.
They work on Maki's lines because the parser fills the same fields nginx's parser does.

4. Run a bouncer that acts on the bans, such as the firewall bouncer on the host or the one for your
   reverse proxy (Traefik, Caddy, nginx, SWAG, Nginx Proxy Manager). Without one, nothing is blocked.

Maki ships rules for CrowdSec only, so the rest of this page covers CrowdSec.

## What gets banned

`maki/maki-bf` counts refused attempts per address on:

- `POST` to `auth/login`, `auth/2fa` and `auth/setup`, and the single sign-on endpoints under `auth/oidc/`
- the password prompts in account settings: change password, enable, disable and recovery codes
  for two-factor, creating an API key, and rotating the OPDS token

A refusal is a 400, 401, 403 or 409 on a `POST` or `PUT`, or a 429. Six in quick succession is a
ban. `maki/maki-slow-bf` catches the same attempts spread out: sixteen, with the bucket draining one
every five minutes.

Maki already has its own limit on those endpoints: 10 attempts per address per minute. An eleventh
gets `429` with a `Retry-After` header. IPv6 clients are counted per /64, so rotating addresses inside
one block does not help. Maki's lockout keeps working alongside the rules.

## What is whitelisted

The parser drops these lines before any scenario sees them:

- requests from a signed-in account, an API key or a valid feed token, except under `auth/` and the
  password endpoints. Opening a chapter makes a request per page, which looks like crawling.
- answered image requests (covers, reader pages, thumbnails, previews, Komga pages), and any 401 on
  the API outside `auth/` and the password endpoints. A tab with an expired session keeps saving
  progress and gets 401s.

Requests from nobody in particular stay visible to every scenario, such as sign-in attempts and a
scanner probing `/wp-login.php`.

## Check it

```sh
cscli explain --file /path/to/maki/config/logs/access-20261030.log --type maki
cscli metrics show acquisition parsers
cscli decisions list
```

`cscli explain` shows each line going through the parser, the whitelist and the scenarios. To
unban yourself, run `cscli decisions delete --ip <address>`.
