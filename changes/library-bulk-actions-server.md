---
type: improvement
section: Library
---
Refresh, Rescan files, Metadata, Search missing, Monitoring, Move and Rename in the Library's select mode now run on the server as one job instead of one request per series. A big selection starts in seconds, keeps going if you close the tab, and Stop still works. Moves and renames go one series at a time, and a series that fails is reported without stopping the rest.
