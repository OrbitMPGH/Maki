#!/bin/bash
set -e

PUID=${PUID:-1000}
PGID=${PGID:-1000}

# linuxserver-style user mapping: run with the host user's uid/gid so files in
# /config and the library get the right owner. gosu takes numeric ids directly,
# so no passwd/group entries are needed (the base image may already use these ids).
mkdir -p /config
# Only walk the tree when the top level is not already ours: /config holds the page caches and the
# multi-GB dump, and a recursive chown over that on every start can take minutes on spinning disks.
if [ "$(stat -c %u:%g /config)" != "$PUID:$PGID" ]; then
    chown -R "$PUID:$PGID" /config
fi

export HOME=/config
exec gosu "$PUID:$PGID" dotnet /app/Maki.Api.dll "$@"
