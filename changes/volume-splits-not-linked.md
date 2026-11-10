---
type: fix
section: Downloads
---
Fixed volume downloads attaching to chapters they don't contain. When a source split a chapter into parts or listed specials (2.1, 2.2, 4.1), an imported volume also took those, so a 20-chapter series could show 60 readable chapters and opening a special served the whole volume from page one. Renaming such a volume could also give it a chapter name like `Ch.2.1-5`, which dropped its volume badge on the series page. A volume whose page names list its chapters now takes only those, and a rescan detaches the extra chapters from files already in the library; renaming the series' files afterwards restores the volume names.
