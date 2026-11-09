---
type: fix
section: Enhance
---
Fixed Auto enhance in paged mode treating every page as stretched. It queued pages that didn't need it on the server, swapped in the upscale at the page turn, and could stay stuck on "loading" while the page was ready.
