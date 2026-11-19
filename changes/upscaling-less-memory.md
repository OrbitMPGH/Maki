---
type: improvement
section: Reader
---
Server upscaling uses less memory on colour pages. They are now saved while they are being upscaled instead of afterwards, which cut the peak by about 30 MB on ordinary manga pages and about 100 MB on webtoon slices around 4000 px tall in testing, at the same speed and with the same pages out. Taller slices are unchanged. Splitting tall strips during page conversion also no longer holds a second copy of each strip to decide where to cut.
