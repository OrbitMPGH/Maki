---
type: fix
section: Downloads
issue: 198
---
Fixed volume files attaching to chapters they don't contain. A volume took every chapter its source assigned to that volume number, so when the source's volume boundaries disagreed with the files (or listed split chapters and specials like 2.1 and 2.2), a volume picked up chapters from its neighbours and the next volume was left with nothing. A 20-chapter series could show 60 readable chapters, and opening a special served the whole volume from page one. Renaming such a volume could also give it a chapter name like `Ch.2.1-5`, which dropped its volume badge on the series page. A volume whose page names list its chapters now takes only those, and a rescan moves chapters already in the library onto the file that holds them; renaming the series' files afterwards restores the volume names.
