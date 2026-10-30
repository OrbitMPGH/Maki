---
paths:
  - "frontend/src/lib/enhance/**"
  - "frontend/src/pages/reader/EnhancedPage*"
  - "frontend/src/pages/reader/ContinuousView.tsx"
  - "frontend/src/pages/reader/PagedView.tsx"
  - "frontend/src/pages/reader/enhanceRuntime*"
  - "frontend/src/pages/reader/compareHold*"
  - "frontend/src/pages/reader/useServerEnhance*"
  - "frontend/src/pages/reader/ServerEnhanceStatus*"
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

User-facing summary in `docs/features.md`. Two tiers share one reader setting: `a4k-*` methods run Anime4K in WebGL2 in the browser, `server:*` methods go through the ONNX queue and show `enhanceFallback` (an `a4k-*` preset, default `a4k-m`, or `off` for the plain page) until the server page is ready. With `off` the engine never loads and `PageEnhance` carries a null `preset` and `engine`.

## Server output

- WebP cannot exceed 16383 px per side, and tall 2x webtoon slices do. `ImageCodec.EncodeUpscaled` switches to JPEG q90 past that. WebP also has no greyscale mode: a grey page is encoded from one band but decodes as three equal bands. JPEG writes a true one-band file.
- The cache root `cache/upscale/` is separate from `cache/reader/`. `HousekeepingJob.PruneReaderCacheDir` deletes names it does not recognise, so never put upscale files in the reader cache and never change that method.
- `UpscaleCache` keeps its own access-time index. Filesystem atime is useless (NTFS, `relatime`). A delete can fail on Windows while a file streams; the entry is kept and retried on the next write.

## Models and tiling

- waifu2x swin_unet art was dropped before release: 19 MB and about 4x slower than SPAN-S (25 s a page on a 9950X3D, minutes on a NAS).
- SPAN-S takes and returns float16. The engine reads tensor types from the model file, not the catalogue.
- Real-ESRGAN is a 4x model. Each tile's 4x output is Lanczos-halved to 2x before the context crop and stitch (`Plain4xThenHalve`), so a full 4x page never sits in memory. Per-tile halving differs from whole-page halving by at most a few levels near tile edges.
- Greyscale detection (`UpscalePixels.IsGreyscale`) is a fraction test, not a mean: a page is colour when more than 1 in 2000 sampled pixels has a channel spread over 16, or the mean spread passes 6. A red title on a grey page must stay red.
- Run it on the decoded source, never on the model output. Real-ESRGAN tints flat screentone purple (SPAN-S barely), and a tinted output fails the test. Before encoding, `UpscalePixels.TakeColourFrom` keeps the model's luma and takes colour from the Lanczos-resized source, so grey areas stay grey on colour pages too.
- `UpscaleCache.Generation` (`g2`) is part of every cached name. Raise it, with the regex, whenever the pipeline's output changes: old names are then never served and housekeeping prunes them. Without it, tinted pages made by an older build keep being served.
- The browser's final resample takes luma from the shader chain and chroma from the source tile. Restore CNN tints screentone the same way; a grey page must come out grey.
- `manifesturl` has no API. In dev it is a row in the `AppConfig` table of the config dir's SQLite database (key `library.upscaling.manifesturl`).
- Server tiling is 384 px with a 16 px context pad. Browser tiling is `min(MAX_TEXTURE_SIZE, 4096)/2` with a 16 px overlap.
- The browser hook sizing stays at 2x (`OUTPUT` is 2w x 2h) and `//!WHEN` is not parsed. The reader's Auto gate (display at 1.2x source, or a soft page) replaces it.
- `UpscaleModelStore.DeleteAsync` throws if the ORT session still holds the file. Call `OnnxPageUpscaler.ReleaseModel` first.
- `UpscaleOptions.Threads` is read per run. The session is rebuilt on the next idle run when it changes.
- `ImageWorkGate` is not reentrant. Wrap the decode and encode leaf calls only, never the whole run. Inference runs outside it.

## Queue and endpoints

- `ReaderController` is a primary-constructor class built by hand in several tests. New services go in through `[FromServices]` on the action, not the constructor.
- The reader POST replaces the user's pending window. Failed and skipped outcomes are remembered for 10 minutes so a bad page is not re-queued forever.
- Near a chapter's end the reader POST also carries `nextChapterId`/`nextPages`: the window's overflow past the last page (paged: missing preload spreads; strip: `beyond` from `enhanceRange`). They fill the room left under the cap with the next chapter as their target, so that chapter's own first POST finds them still pending (same-chapter keys are kept) or ready. They are not in the response.
- `pageEnhanced` is pushed twice per page: `working` when the worker starts it, then the outcome. The push often beats the POST's own `queued` answer, so `useServerEnhance` never walks `working` back to `queued`. `ServerEnhanceStatus` reads those states plus `shown` (the upscale is decoded and drawn) for the reader pill.
- `HostStartupTests` fails on any WRN/ERR at boot. The store and the cache must start silently with no models and no cache folder.

## Reader

- Crop and split keep measuring the original `<img>` (`pageShape.ts` caches by `currentSrc`). Regions are fractions and apply unchanged to the enhanced canvas.
- `pageUrl()` must not change shape. Offline saving, `useWarmChapter` and the service worker regexes depend on it. The enhanced URL (`.../page/{n}/enhanced`) is deliberately outside every service-worker list, so offline it gets the synthetic 503 and the reader shows Balanced.
- `EnhancedPage` reports `onShowing` from a layout effect. The canvas hides in the same commit that holding H starts, so a passive effect paints one frame with neither the canvas nor the host's image.
- The strip's enhance range (`enhanceRange` in `ContinuousView`) is sized in displayed device pixels against `enhanceBudget()` (three quarters ahead, the rest behind, 32 pages at most), never a page count: webtoon chapters run from a handful of 15000 px slices to 100 short ones. The same range is the server's window in vertical mode, so `MAX_PAGES` in `useServerEnhance` and `UpscaleQueue.MaxPendingPerUser` move together.
- Auto also takes pages that measure soft (`lib/enhance/softness.ts`, Crété-Roffet blur on up to 24 native-resolution 160 px tiles, `SOFT_BLUR` 0.28). The threshold was calibrated on about 100 `.simulated` pages: screentone and clean line art 0.08 to 0.2, sub-800 px scans and soft colour webtoons 0.25 to 0.45, volumes up to 0.3. A measurement costs 10 to 35 ms, so `EnhancedPage` uses `knownSoft` during render and measures after paint; the paged preload measures pages ahead so a turn finds the answer cached. "Stretched" in `EnhanceScope` and `onStretched` now means "wanted by Auto", stretched or soft.
- `enhanceSkipTiers` (default `['volume']`, filled by `ReaderPrefsSpec.Sanitized` when unset) turns enhance off for a chapter whose file tier is listed. The manifest's `tier` drives it through `enhanceFor()` in `ReaderPage`; pass that result, never `prefs.enhance`, to the views and `serverModelFor`, or the server queue still fills for a skipped chapter.
- The engine's `enhance()` closes the source bitmap. A result produced with a `key` belongs to the engine's LRU: draw it with `drawImage`, never close or transfer it.

## Model delivery

- Release assets come from `distribution/upscale-models/prepare.py` and are uploaded by the owner with `distribution/publish-upscale-models.ps1`.
- In dev, point `library.upscaling.manifesturl` at a local `manifest.json` (a plain path or `file://`).
- Licences live in the root `THIRD_PARTY_NOTICES.md` and `distribution/upscale-models/licenses/`. SPAN-S is CC BY-NC 4.0, so it is non-commercial even though Maki's code is GPL-3.0.
