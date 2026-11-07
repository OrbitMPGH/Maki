---
type: improvement
section: Performance
---
Semantic search and recommendations need far less memory, roughly 140 MB instead of 240 MB, and come back almost instantly after being unloaded while idle. Set `MAKI_SNAPSHOT_MMAP=0` if your filesystem has trouble with memory-mapped files.
