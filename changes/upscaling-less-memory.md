---
type: improvement
section: Reader
---
Server upscaling uses less memory. Colour pages are now saved while they are being upscaled instead of afterwards, which cut the peak by about 30 MB on ordinary manga pages and about 100 MB on webtoon slices around 4000 px tall in testing. Black-and-white pages, very tall slices and pages for browsers without JPEG XL no longer hold the whole upscaled page in Maki's own memory: on tall webtoon pages the peak dropped by about 80 MB and about 130 MB less stayed held after a run, slightly faster than before. The pages that come out are the same. Converting pages to JPEG XL also streams each page into the encoder, about 60 to 90 MB lower at its peak, with tall webtoon strips taking about 15% longer to convert. Splitting tall strips during page conversion no longer holds a second copy of each strip to decide where to cut.
