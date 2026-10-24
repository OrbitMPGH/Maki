# Design: more download clients and a second indexer (issue 119)

Issue [#119](https://github.com/OrbitMPGH/Maki/issues/119) asks for Deluge, Transmission and µTorrent, or for grabbing through the download clients Prowlarr already has. This note settles the approach and lays out the implementation. Facts about the current code come from a read of `ReleaseService`, `CompletedDownloadJob`, `TorrentImportService`, `QBittorrentClient`, `ProwlarrClient` and their callers; facts about the other clients and Jackett were checked against their source and RPC specs on 2026-10-15.

## Decision

Native drivers behind one `ITorrentClient` interface, one client active at a time, with Transmission and Deluge first. Prowlarr's own download clients are not used. A second interface, `IReleaseIndexer`, puts Jackett beside Prowlarr, again one backend active at a time.

### Why not grab through Prowlarr

Prowlarr can grab: `POST /api/v1/search` with `{guid, indexerId}` sends the release to whichever client it has for the protocol. Everything after that is missing for Maki's purposes.

- The response is the submitted release echoed back. No infohash, no client id. The release must still be in Prowlarr's 30 minute search cache or it is a 404.
- Prowlarr has no queue or progress API. History records `releaseGrabbed` events but leaves `downloadId` empty.
- `GET /api/v1/downloadclient` returns host, port and username, but every password or API key field is `"********"`. Maki cannot borrow the credentials to poll the client itself.

Maki's whole torrent path after the add is "poll the client for progress, then read `content_path` and hardlink from it". Prowlarr gives none of that, so the proxy route would only ever replace the add call, the one part that is already easy. It is dropped.

### Why one active client, not a list

Sonarr runs a list of clients with priorities. Maki's model is one queue, one category, one path map, and the issue asks to *choose* a client. A single `downloadclient.kind` setting keeps the settings card, the setup wizard, the health probe and the completion job unchanged in shape. The interface does not prevent a list later.

## What the client abstraction has to carry

The coupling map is short. Nothing outside `QBittorrentClient` reads `State` or `SavePath`. The callers use:

| Member | Used by | For |
|---|---|---|
| `AddAsync(magnet or url, category)` | `ReleaseService` | the grab; returns no hash today |
| `AddFileAsync(bytes, infoHash, category)` | `ReleaseService` | the grab; "already present" counts as success |
| `ListAsync(category)` | `CompletedDownloadJob` every 15 s, `TorrentImportService.ResolveContentPathAsync` | find Maki's torrents |
| `Hash` | both | match the queue row (`ReleaseInfo.TorrentHash`), case-insensitive |
| `Name`, `AddedOn` | `CompletedDownloadJob.ClaimTorrent` | the hashless fallback (time window plus title tokens) |
| `Progress`, `IsComplete` (`>= 1.0`) | `CompletedDownloadJob` | progress bar and the import trigger |
| `ContentPath` | both, through `PathRemapper` | what `ComicSourceScanner` reads and `FileLinker.Place` links from |
| `PingAsync` | `SettingsController` test, `HealthMonitor` | login check |

So the interface is:

```csharp
public enum TorrentClientKind { QBittorrent, Transmission, Deluge }

public sealed record TorrentClientSettings(
    TorrentClientKind Kind, string Url, string Username, string Password, string Label);

public sealed record ClientTorrent(
    string Hash, string Name, double Progress, bool IsComplete,
    string ContentPath, DateTimeOffset AddedAt, bool IsErrored, string? Message);

public enum TorrentClientFailure { Unreachable, LoginFailed, Rejected, LabelUnsupported }
public sealed class TorrentClientException(TorrentClientFailure failure, string message) : InvalidOperationException(message);

public interface ITorrentClient
{
    TorrentClientKind Kind { get; }
    bool UsesUsername { get; }           // Deluge's web UI has a password only
    string DefaultUrl { get; }           // placeholder for the card

    Task PingAsync(TorrentClientSettings s, CancellationToken ct);
    /// <returns>The infohash when the client reports one; null when it does not (qBittorrent URL add).</returns>
    Task<string?> AddAsync(TorrentClientSettings s, string magnetOrUrl, CancellationToken ct);
    Task<string> AddFileAsync(TorrentClientSettings s, byte[] torrent, string infoHash, CancellationToken ct);
    Task<IReadOnlyList<ClientTorrent>> ListAsync(TorrentClientSettings s, CancellationToken ct);
}
```

Three deliberate changes against today:

1. **`AddAsync` returns the hash when the client knows it.** Transmission's `torrent-add` answers with `hashString` (also on `torrent-duplicate`), Deluge's `add_torrent_*` returns the id. Only qBittorrent's URL add is blind. That shrinks the hashless `ClaimTorrent` path to one client's fallback. `ReleaseService.AddToQbittorrentAsync` becomes `AddToClientAsync` and prefers the returned hash over the one it computed.
2. **`ListAsync` is "Maki's torrents", not "torrents in category X".** Each driver decides how: qBittorrent filters server-side by category, Deluge by the Label plugin filter, Transmission and µTorrent list everything and filter on the label client-side (neither has a server-side label filter). A driver that cannot label (Deluge without the Label plugin) returns every torrent; hashed rows still match, and the hashless heuristic already has the time window and title tokens to avoid adopting strangers.
3. **`IsComplete` and `IsErrored` are the driver's call.** Today `Progress >= 1.0` triggers the import even for a qBittorrent torrent in `missingFiles` or `error` state, and the import then fails on the path check. Drivers compute completion the way their client means it (Transmission `isFinished` or `percentDone == 1` unless `error == 3`, a local error; Deluge `is_finished`, or progress 100 and state not Checking or Error; qBittorrent progress 1 and state not `error`/`missingFiles`/`checking*`). `IsErrored` lets the job hold a row with a visible reason instead of a silent "Downloading" forever; that is a small follow-on, not required for the drivers to land.

`ContentPath` stays on the record. qBittorrent reports it directly; Transmission is `downloadDir + name`; Deluge is `download_location + name`, or `move_completed_path + name` once finished with `move_completed` on; µTorrent is the root path at list index 26 with the name appended unless already there; rTorrent has `d.base_path`. Single-file torrents yield a file, multi-file a folder, which `ComicSourceScanner.Scan` already handles. `PathRemapper` stays one global pair and is applied exactly where it is now.

Hashes stay lowercase 40-hex. qBittorrent, Transmission 4 and Deluge (libtorrent 2) all list the v1 hash for hybrid torrents, and all compare case-insensitively on our side already.

### Drivers

**qBittorrent.** The existing class, renamed internally only as much as the interface needs. Keeps its own `HttpClient` with the cookie container, the Origin/Referer CSRF headers, the 4.x/5.x login detection and the "Fails" body sniff. `AddFileAsync`'s `HasTorrentAsync` stays.

**Transmission.** JSON over `POST {url}/transmission/rpc`, optional basic auth. The driver caches the `X-Transmission-Session-Id` and redoes a request once on 409. Add with `metainfo` (base64) or `filename`, `labels: ["maki"]`, `download-dir` left to the client. Labels on add need Transmission 4.0 (rpc-version 17); on 3.x the driver follows the add with `torrent-set labels`. Listing uses `torrent-get` with `hashString, name, percentDone, isFinished, status, error, errorString, downloadDir, labels, addedDate`. Transmission 4.1 switched the wire names to snake_case and JSON-RPC 2.0, but it answers a request without a JSON-RPC 2.0 envelope in the old names, so the driver speaks the legacy names on every version and reads `rpc-version` from `session-get` only to decide whether labels go on `torrent-add` (17 and up) or through `torrent-set`.

**Deluge.** JSON-RPC over `POST {url}/json`, `auth.login([password])`, `_session_id` cookie, re-login on error code 1. After login the driver checks `web.connected` and, if false, `web.get_hosts` then `web.connect(hostId)`, the same dance Sonarr does. Adds with `core.add_torrent_file(name, base64, {})` or `core.add_torrent_magnet(uri, {})`, both of which return the hash; a `None` from a duplicate is treated as present, mirroring the qBittorrent rule. Labels: `core.get_enabled_plugins`, enable `Label` if missing, `label.add("maki")` if absent, `label.set_torrent(hash, "maki")`. Deluge lowercases label ids and restricts them to `[a-z0-9_\-\.]`, so the settings card validates the label the same way for every kind. Listing uses `web.update_ui([...keys], {"label": "maki"})` rather than `core.get_torrents_status`, which Sonarr avoids because it returns file lists and times out on big instances. Keys: `hash, name, progress, state, is_finished, download_location, move_completed, move_completed_path, time_added, message`.

**rTorrent and µTorrent** are phase D if anyone asks. rTorrent is XML-RPC by hand (`load.raw_start` with `d.custom1.set=maki`, `d.multicall2` for listing, `d.base_path` as content path) and its URL shape differs per setup. µTorrent is a token handshake plus a positional JSON array; it is still in Sonarr's and Prowlarr's lists but its user base is Windows desktop and shrinking. Both fit the interface without changes.

### Settings and migration

New keys replace the `qbittorrent.*` six: `downloadclient.kind` (default `qbittorrent`), `.url`, `.username`, `.password`, `.label`, `.pathmapfrom`, `.pathmapto`. An EF migration renames the existing `AppConfig` rows in place (`UPDATE AppConfig SET Key = ... WHERE Key = 'qbittorrent.url'`, and so on), so nothing breaks for current installs and the old keys disappear in the same release. `download.usehardlinks` is untouched.

`ReleaseService.GetQbtConfigAsync` becomes `GetClientSettingsAsync` returning `TorrentClientSettings`; `GetQbtPathMapAsync` keeps its job under a neutral name. `TorrentClientRegistry` (singleton holding one instance per kind, DI-registered) resolves `Kind` to the driver; the three callers take the registry and the settings record instead of the concrete class.

### API and UI

- `GET/PUT settings/downloadclient` with `{kind, url, username, password, label, pathMapFrom, pathMapTo}`, `POST settings/downloadclient/test` taking the same body. `ConnectionName` gains `'downloadclient'` and loses `'qbittorrent'`; `ConnectionSettingsCard` is generic enough that the card is a kind `Select` above the existing fields. The username field hides when the driver's `UsesUsername` is false, and the URL placeholder comes from `DefaultUrl` (`http://localhost:8080`, `http://localhost:9091`, `http://localhost:8112`). The wizard gets the same kind select and starts sending `label`, which it drops today (so every wizard-configured install stores a null that reads back as "maki").
- `HealthMonitor` probes under the kind's display name instead of the literal "qBittorrent".
- `QBittorrentException` and `QBittorrentFailure` go; `ReleaseController` maps `TorrentClientFailure` to `error.release.clientLoginFailed` / `clientRejected` / `clientUnreachable`. Every server string naming qBittorrent (`error.release.qbittorrentNotConfigured`, `error.download.torrentMissing`, `torrentRemoved`, `error.torrentImport.notInQbittorrent`, the settings test failure, the grab-failed message) is reworded around "the download client" with a `{client}` argument carrying the kind's display name where the name helps. The frontend has about thirteen such strings across `DownloadCards`, `SetupWizard`, `QueuePause`, `ReleaseSearchModal`, `TorrentProposalCard`, `registry` and `queue.ts`; same treatment. Only `lingui extract` runs; translations are the owner's.

### Tests

- The two fakes that subclass the concrete class (`FakeQbt` in `CompletedDownloadJobTests`, `RecordingQbt` in `ReleaseServiceTests`) become one `FakeTorrentClient : ITorrentClient` in `TestHelpers`.
- `QBittorrentClientTests` stays. `TransmissionClientTests` and `DelugeClientTests` follow its pattern with recorded `HttpMessageHandler`s: the 409 handshake and the 3.x label fallback; the Deluge code-1 re-login, the disconnected-daemon reconnect, and the Label plugin being off.
- About fifteen test files construct `SettingsController` with `qbittorrent: null!` and `prowlarr: null!` by name; they get a mechanical rename in the same change.
- A live smoke against `linuxserver/transmission` and `linuxserver/deluge` containers is on the release checklist for the version that ships this, since recorded handlers cannot catch a wrong field name.

## Jackett

### Shape

Prowlarr has three direct callers (`ReleaseService`, `SettingsController`, `HealthMonitor`); everyone else sees `ReleaseDto`, with the release `Guid` string as identity everywhere (`TorrentProposal.ReleaseGuid` unique with `SeriesId`, `ReleaseInfo.Guid` on queue rows, `BlockedGuidsAsync`). `IndexerId` and `AgeMinutes` are never read outside the client. There are no hardcoded category ids.

```csharp
public enum IndexerBackend { Prowlarr, Jackett }

public sealed record IndexerSettings(IndexerBackend Backend, string Url, string ApiKey,
    IReadOnlyList<string>? IndexerIds, IReadOnlyList<int>? Categories);

public sealed record IndexerRelease(
    string Guid, string Title, long Size, string Indexer, int? Seeders, int? Leechers,
    string? DownloadUrl, string? MagnetUrl, string? InfoHash, string Protocol, string? InfoUrl);

public sealed record IndexerInfo(string Id, string Name, bool Enabled, string? Protocol,
    IReadOnlyList<(int Id, string Name)> Categories);

public interface IReleaseIndexer
{
    IndexerBackend Backend { get; }
    Task<IReadOnlyList<IndexerRelease>> SearchAsync(IndexerSettings s, string query, CancellationToken ct);
    Task<IReadOnlyList<IndexerInfo>> GetIndexersAsync(IndexerSettings s, CancellationToken ct);
    Task<bool> PingAsync(IndexerSettings s, CancellationToken ct);
    Task<TorrentLink> FetchTorrentAsync(string downloadUrl, CancellationToken ct);
}
```

Indexer ids become strings because Jackett's are (`nyaasi`, `animebytes`) where Prowlarr's are ints. The settings CSV already stores text; the Prowlarr adapter parses to int on the way out. `InfoHash` is new and optional: Jackett supplies it on most results, and when present the grab can skip fetching the `.torrent` just to hash it.

### Jackett specifics

- Search: `GET {url}/api/v2.0/indexers/all/results?apikey=&Query=&Category[]=&Tracker[]=`. Jackett's own JSON, PascalCase, `Results[]` (Title, Guid, Link, Details, Size, Seeders, Peers, InfoHash, MagnetUri, Tracker, TrackerId, TrackerType, Category[], PublishDate) and `Indexers[]` with per-indexer Status, Results and Error, so a failing indexer can be reported rather than silently empty. `Peers` appears to be leechers after Jackett subtracts seeders; the driver confirms this against a live instance before relying on it. `Tracker[]` is the indexer filter, verified in `ResultsController`.
- Indexer list: `GET .../indexers/all/results/torznab/api?apikey=&t=indexers&configured=true`, XML. The JSON `GET /api/v2.0/indexers?configured=true` has no API-key filter and wants the admin cookie, so it is avoided. Caps come per indexer from `t=caps` only when the card needs them; the torznab indexer list already carries each indexer's category set.
- Ping: `t=caps` on the `all` endpoint with the API key. `GET /api/v2.0/server/config` is cookie-authed, same caveat.
- Download: `Link` is Jackett's `/dl/{indexer}?jackett_apikey=&path=&file=` proxy. It serves `application/x-bittorrent`, or redirects to the magnet for magnet-only results. The existing `FetchTorrentAsync` already refuses to follow redirects and reads `Location: magnet:`, so it moves to a shared base verbatim.
- Categories: Torznab has no manga id. 7030 Books/Comics is the closest; many indexers file manga under 8000 Other or the indexer's own 100xxx custom ids. The default stays "no filter", as it is for Prowlarr today.
- Guid: a URL, often long. `TorrentProposal.ReleaseGuid` is already a string; nothing else to change. Switching backend changes every guid, which only means old Dismissed proposals stop blocking anything; acceptable, and noted in the card's help text.

### Settings and UI

`indexer.backend` (default `prowlarr`) chooses. The `prowlarr.*` keys stay as they are and `jackett.url`, `jackett.apikey`, `jackett.indexerids`, `jackett.categories` join them, because the two backends have different ids and a user switching back should find their Prowlarr selection intact. One "Indexer" card with a backend select at the top, the connection fields, and the existing indexer checklist and category MultiSelect bound to the active backend's options. Routes: `GET/PUT settings/indexer` (backend plus the active backend's connection), `GET/PUT settings/indexer/options`, `GET settings/indexer/indexers`, `POST settings/indexer/test`. The old `settings/prowlarr*` routes go in the same release; nothing outside the SPA calls them.

Strings: `not_eligible_no_prowlarr` becomes `not_eligible_no_indexer` on both sides (the frontend keeps the old code as an alias for a release). "Search releases (Prowlarr)", "Prowlarr isn't configured", "No indexers configured in Prowlarr" and friends, about twelve client strings and three server keys, reword around "indexer" or interpolate the backend name. The `prowlarr` named HTTP client (100 s timeout, `TransientRetryHandler`, no `PublicAddressGuard` because it is a LAN service) is registered a second time as `jackett` with the same options.

Generic Torznab as a third backend is cheap once Jackett is in (the `t=indexers`, `t=caps` and `t=search` XML paths are the same code), and it would cover NZBHydra2 and Prowlarr's own Torznab endpoints. Not in scope now.

## Phases

| Phase | Content | Size |
|---|---|---|
| A | `ITorrentClient`, records, registry; qBittorrent adapted; `ReleaseService`, `CompletedDownloadJob`, `TorrentImportService`, `SettingsController`, `HealthMonitor`, `ReleaseController` on the interface; settings key migration; card and wizard with kind select; strings generalised; fakes and the fifteen controller constructions updated | the bulk of the churn, no behaviour change |
| B | `TransmissionClient`, `DelugeClient`, their recorded-handler tests, release checklist smoke entry | ~250 and ~300 lines plus tests |
| C | `IReleaseIndexer`, Prowlarr adapter, `JackettClient`, `indexer.backend`, Indexer card, strings | ~200 lines plus the card |
| D (later) | rTorrent, µTorrent, generic Torznab; `IsErrored` surfacing in the activity list | on request |

A and B can be reviewed as one PR to `dev`; C is independent of both and can run in parallel in its own worktree, since it touches `ReleaseService.SearchAsync` and the Prowlarr call sites while A touches the grab and the job. Both land on `dev`. The `downloads.md` rule file gets its "Prowlarr → qBittorrent → CompletedDownloadJob" line rewritten and its `paths` widened to `src/Maki.Core/Download/*.cs` and `src/Maki.Core/Indexers/**`; README and PRODUCT.md drop the single-client wording.

## Open choices

Defaults taken in this note, each easy to change before phase A starts:

- Transmission and Deluge in phase B; rTorrent and µTorrent deferred.
- One active download client and one active indexer backend, not lists.
- Settings keys renamed by migration (`downloadclient.*`) rather than keeping `qbittorrent.*` under a misleading name.
- Jackett through its native JSON search rather than per-indexer Torznab.
