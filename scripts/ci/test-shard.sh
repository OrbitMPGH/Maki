#!/usr/bin/env bash
# Runs one shard of a built test project in CI. The project's test classes are sorted and dealt
# round-robin into <total> shards, so a class (and its fixture) never straddles two of them, and the
# classes that run serially (ConfigDirCollection) end up spread across every shard.
#
# Usage: scripts/ci/test-shard.sh <project dir> <index, 1-based> <total> [extra dotnet test args]
# Expects a Release build already in place (it passes --no-build).
set -euo pipefail

project=$1
index=$2
total=$3
shift 3

# Discovery prints one display name per test, indented four spaces. Without a DisplayName override
# that is the namespace, class and method, plus the arguments for a theory case.
mapfile -t tests < <(dotnet test "$project" --no-build -c Release --list-tests | tr -d '\r' | sed -n 's/^    //p')
if [ "${#tests[@]}" -eq 0 ]; then
  echo "::error::No tests discovered in $project" >&2
  exit 1
fi

classes=$(printf '%s\n' "${tests[@]}" | sed 's/(.*//; s/\.[^.]*$//' | LC_ALL=C sort -u)
if bad=$(printf '%s\n' "$classes" | grep -vE '^Maki\.[A-Za-z0-9_.+]+$'); then
  echo "::error::Test names that do not parse as Namespace.Class.Method (a DisplayName override?), which this script cannot shard: $bad" >&2
  exit 1
fi

mine=$(printf '%s\n' "$classes" | awk -v i="$index" -v n="$total" 'NR % n == i % n')
if [ -z "$mine" ]; then
  echo "Shard $index/$total has no classes."
  exit 0
fi

# The trailing dot stops FooTests from also matching FooTestsSlow.
filter=$(printf '%s\n' "$mine" | sed 's/.*/FullyQualifiedName~&./' | paste -sd '|')

expected=$(printf '%s\n' "${tests[@]}" | sed 's/(.*//; s/\.[^.]*$//' | grep -cxFf <(printf '%s\n' "$mine"))
matched=$(dotnet test "$project" --no-build -c Release --list-tests --filter "$filter" | tr -d '\r' | grep -c '^    ' || true)
if [ "$matched" -ne "$expected" ]; then
  echo "::error::Shard $index/$total filter matches $matched tests, expected $expected. Some tests would run twice or not at all." >&2
  exit 1
fi
echo "Shard $index/$total: $(printf '%s\n' "$mine" | wc -l) classes, $expected tests"

dotnet test "$project" --no-build -c Release --filter "$filter" "$@"
