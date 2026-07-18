#!/bin/bash
set -e

PUID=${PUID:-1000}
PGID=${PGID:-1000}

# linuxserver-style user mapping: run with the host user's uid/gid so files in
# /config and the library get the right owner. gosu takes numeric ids directly,
# so no passwd/group entries are needed (the base image may already use these ids).
mkdir -p /config
chown -R "$PUID:$PGID" /config

export HOME=/config
exec gosu "$PUID:$PGID" dotnet /app/Maki.Api.dll "$@"
