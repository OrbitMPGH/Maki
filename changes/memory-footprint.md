---
type: improvement
---
Maki uses less baseline memory on startup. Database lookups Maki has not needed for 20 minutes are let go instead of being kept for as long as Maki runs. Background page conversion, prepare-ahead upscaling and credit page scans pause while heavier work (such as browser scrapes, active reading upscales or catalogue builds) is running, and TopManhua chapter downloads now load pages in small batches instead of all at once.
