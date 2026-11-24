---
type: improvement
section: Sources
---
MangaFire and TopManhua use less memory. Maki now drives their headless browser itself instead of through a separate Node.js helper process, which held about 100 MB while either browser was open. A TopManhua download holds about 140 MB less while it runs and in the minute after, and uses about a third less CPU. The Docker image is about 180 MB smaller, and the Windows and Linux downloads about 100 MB smaller unpacked. The browser itself is unchanged, and one that is already installed is reused. On a Linux install without Docker, the system libraries the browser needs are now installed with `sudo /opt/maki/install-browser-deps.sh`.
