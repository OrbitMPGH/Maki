---
type: breaking
section: Recommendations
---
If semantic search and recommendations are on, Maki switches to a new text model on first start and downloads a new prebuilt index (about 42 MB, down from 109 MB). Search and recommendation rails that need the index are empty until it's in place. Progress shows under Settings > Library > Semantic search & recommendations. Search quality is a little better, and the index takes 65 MB on disk instead of 158 MB.
