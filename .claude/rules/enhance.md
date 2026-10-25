---
paths:
  - "frontend/src/lib/enhance/**"
  - "frontend/src/pages/reader/EnhancedPage*"
  - "frontend/src/pages/reader/enhanceRuntime*"
  - "frontend/src/pages/reader/compareHold*"
  - "frontend/src/pages/reader/useServerEnhance*"
  - "frontend/src/pages/reader/enhanceMethods.ts"
  - "src/Maki.Api/Services/Upscaling/**"
  - "src/Maki.Core/Images/Upscaling/**"
  - "src/Maki.Api/Controllers/UpscalingController.cs"
  - "src/Maki.Api/Services/ReaderPageCache.cs"
  - "src/Maki.Api/Jobs/UpscalerIdleUnloadJob.cs"
  - "frontend/src/pages/settings/UpscalingSection*"
  - "frontend/src/api/upscaling.ts"
  - "distribution/upscale-models/**"
---

# Enhance: browser shaders and server upscaling

User-facing summary in `docs/features.md`. Two tiers share one reader setting: `a4k-*` methods run Anime4K in WebGL2 in the browser, `server:*` methods go through the ONNX queue and fall back to `a4k-m` until the server page is ready.

## Server output

- WebP cannot exceed 16383 px per side, and tall 2x webtoon slices do. `ImageCodec.EncodeUpscaled` switches to JPEG q90 past that. WebP also has no greyscale mode: a grey page is encoded from one band but decodes as three equal bands. JPEG writes a true one-band file.
- The cache root `cache/upscale/` is separate from `cache/reader/`. `HousekeepingJob.PruneReaderCacheDir` deletes names it does not recognise, so never put upscale files in the reader cache and never change that method.
- `UpscaleCache` keeps its own access-time index. Filesystem atime is useless (NTFS, `relatime`). A delete can fail on Windows while a file streams; the entry is kept and retried on the next write.

## Models and tiling

- waifu2x swin_unet outputs `2·in − 32` per axis. The adapter reflect-pads the multiple-of-4-rounded context tile by a further 8 px per side (`Waifu2xBorder`), so the output is exactly 2x the context tile.
- SPAN-S takes and returns float16. The engine reads tensor types from the model file, not the catalogue.
- Real-ESRGAN is a 4x model. Each tile's 4x output is Lanczos-halved to 2x before the context crop and stitch (`Plain4xThenHalve`), so a full 4x page never sits in memory. Per-tile halving differs from whole-page halving by at most a few levels near tile edges.
- Greyscale detection (`UpscalePixels.IsGreyscale`) is a fraction test, not a mean: a page is colour when more than 1 in 2000 sampled pixels has a channel spread over 16, or the mean spread passes 6. A red title on a grey page must stay red.
- `manifesturl` has no API. In dev it is a row in the `AppConfig` table of the config dir's SQLite database (key `library.upscaling.manifesturl`).
- Server tiling is 384 px with a 16 px context pad. Browser tiling is `min(MAX_TEXTURE_SIZE, 4096)/2` with a 16 px overlap.
- The browser hook sizing stays at 2x (`OUTPUT` is 2w x 2h) and `//!WHEN` is not parsed. The reader's Auto gate (display at 1.2x source) replaces it.
- `UpscaleModelStore.DeleteAsync` throws if the ORT session still holds the file. Call `OnnxPageUpscaler.ReleaseModel` first.
- `UpscaleOptions.Threads` is read per run. The session is rebuilt on the next idle run when it changes.
- `ImageWorkGate` is not reentrant. Wrap the decode and encode leaf calls only, never the whole run. Inference runs outside it.

## Queue and endpoints

- `ReaderController` is a primary-constructor class built by hand in several tests. New services go in through `[FromServices]` on the action, not the constructor.
- The reader POST replaces the user's pending window. Failed and skipped outcomes are remembered for 10 minutes so a bad page is not re-queued forever.
- `HostStartupTests` fails on any WRN/ERR at boot. The store and the cache must start silently with no models and no cache folder.

## Reader

- Crop and split keep measuring the original `<img>` (`pageShape.ts` caches by `currentSrc`). Regions are fractions and apply unchanged to the enhanced canvas.
- `pageUrl()` must not change shape. Offline saving, `useWarmChapter` and the service worker regexes depend on it. The enhanced URL (`.../page/{n}/enhanced`) is deliberately outside every service-worker list, so offline it gets the synthetic 503 and the reader shows Balanced.
- The engine's `enhance()` closes the source bitmap. A result produced with a `key` belongs to the engine's LRU: draw it with `drawImage`, never close or transfer it.

## Model delivery

- Release assets come from `distribution/upscale-models/prepare.py` and are uploaded by the owner with `distribution/publish-upscale-models.ps1`.
- In dev, point `library.upscaling.manifesturl` at a local `manifest.json` (a plain path or `file://`).
- Licences live in the root `THIRD_PARTY_NOTICES.md` and `distribution/upscale-models/licenses/`. SPAN-S is CC BY-NC 4.0, so it is non-commercial even though Maki's code is GPL-3.0.
