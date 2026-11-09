---
type: improvement
section: Enhance
---
Server upscaling is faster and wastes less work. Pages you've paged past are dropped from the queue, and a page still running is abandoned when you skip ahead or switch chapter. Pages are split into small tiles that run in parallel, and flat areas skip the model, about 1.4x faster on a 10-thread CPU with lower peak memory.
