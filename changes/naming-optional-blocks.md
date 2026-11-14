---
type: feature
section: Library
issue: 197
---
Naming formats can mark a part as optional by wrapping it in `<` and `>`. The part is left out
entirely when any token inside it is empty, so `<({Chapter Group})>` adds the group in brackets
when there is one and nothing at all when there isn't.
