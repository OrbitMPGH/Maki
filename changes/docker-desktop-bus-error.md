---
type: fix
---
Fixed Maki crashing with a bus error on Docker Desktop when /config is a bind-mounted folder. A fresh install could die on its first start, and once a stopped or crashed container had left a database file behind, every later start failed the same way.
