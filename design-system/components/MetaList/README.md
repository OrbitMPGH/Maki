# MetaList

A row of short facts about one thing: "Ongoing | 2019 | Manhwa | 142 chapters", "Syncing now | Last 4 min ago | Next in 26 min". Items are split by a thin vertical rule at a third of the text colour, never by a centre dot or a comma. Series titles and alternate titles can contain commas, and a rule can't be mistaken for part of a title.

It takes its size and colour from where it sits and wraps onto a second line when the row is too narrow. Empty items (`null`, `false`, `''`) are dropped, so a caller can pass conditionals inline.

Use it for facts that are each readable on their own. When the pieces form a sentence, write the sentence ("Anime adaptation starts here. Click to expand."). A status line stays a list, so the part people look for (when the next sync runs) is findable without reading the rest.

With `inline` it renders spans instead of a list, for a parent that truncates or clamps to two lines (a filter summary, a one-line queue row).

**The consumer provides:** `items`, optional `inline`, `className` and `style`.
