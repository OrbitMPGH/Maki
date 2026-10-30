# Backup and restore

**Settings → Backup & Restore** takes zip snapshots of the database plus `config.json`. Together
these are everything that cannot be cheaply regenerated. The MangaBaka dump, embeddings, covers,
and cache are excluded so backups stay small. Backups are stored under `{ConfigDir}/backups`, and
a setting controls how many are kept.

Maki also backs up automatically **before any upgrade applies a database migration**. Migrations
are forward-only, so this is your recovery path if an upgrade goes wrong.

> **Backups contain the secrets from your settings (API keys, passwords) in plain text.** Treat a
> downloaded backup like a password.

**Restoring** replaces the database and settings, then restarts Maki. Under Docker
(`restart: unless-stopped`), the Windows app or systemd it comes back on its own; a process started
by hand, such as `dotnet run` or `./Maki.Api` from the Linux tarball, exits and must be started again
the same way. You can restore a backup from another machine, but not one taken by a newer Maki
version.
