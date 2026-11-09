---
type: improvement
section: Performance
---
Much lower memory use on small servers and NAS boxes. Installing the metadata database peaked around 4 GB and now stays under 400 MB. Page conversion of very large pages uses about half the memory, archive repacks during import use about a quarter, and memory is handed back after idle unloads.
