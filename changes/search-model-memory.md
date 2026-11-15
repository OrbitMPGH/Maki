---
type: improvement
section: Performance
---
Searching by meaning uses about 100 MB less memory while the search model is loaded, with the same results. The first search after the update prepares a memory-mapped copy of the model in the background, which takes about 110 MB of disk in the config folder. A search by meaning after a pause is now answered by meaning straight away instead of by title first. Set `MAKI_EMBED_MMAP=0` if your filesystem has trouble with memory-mapped files.
