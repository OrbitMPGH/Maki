---
type: improvement
section: Performance
---
An idle Maki holds less memory. After a restart or an upgrade the container no longer keeps 40 to 90 MB of search and recommendation database pages on its memory figure, and the memory a visit to Discover or a search takes is handed back about a minute after you stop browsing instead of 10 to 17 minutes later. The first page after a quiet minute pays a one-off reload of 0.1 to 0.3 seconds. Startup and the first pages after it also use about a quarter less CPU.
