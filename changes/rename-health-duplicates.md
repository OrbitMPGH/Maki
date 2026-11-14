---
type: fix
section: Library
---
Fixed Rename series leaving Health out of step with the files it moved. Renamed files were flagged as duplicates of their own old names, and when the series folder was renamed, files with no chapter linked kept their old path and showed as missing. Existing false duplicates clear on the next library scan from Health, and files already showing as missing can be fixed with Relink all on Health's Missing filter.
