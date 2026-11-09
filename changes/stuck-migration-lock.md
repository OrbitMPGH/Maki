---
type: fix
---
Fixed Maki hanging on every start after a start that was stopped or crashed while it upgraded the database. It now clears the lock that run left behind and finishes the upgrade.
