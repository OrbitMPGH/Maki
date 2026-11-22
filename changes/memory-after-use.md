---
type: improvement
---
Maki gives memory back sooner after you use it. The search and recommendation files it loads for Discover no longer stay counted in memory after they are unloaded, which kept about 85 MB lower after a few visits in testing. Memory used by upscaling, credit page scans and page conversion is returned once they go idle, the credit page caches unload after five minutes, and the upscaling and credit page models unload after three and one minutes, so the first upscaled page after a longer pause can take a moment more. Your browser also frees the memory used by in-browser Enhance shortly after you leave the reader.
