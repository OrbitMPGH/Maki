# ModalShell

A Mantine `Modal` provides the raised card, sectioned header and scrolling body. Use `MODAL_SIZE.narrow` (420) for short prompts, `default` (560) for forms, `wide` (820) for pickers and tables, and `xwide` (1080) for wider workspaces.

- **`ModalHeading`** goes in the Modal's `title`. The title sets in the display face; the line under it is `meta` size in `ink-3`, sentence case, one sentence ("Searches your indexers. Grab sends a release to your download client."). The line is kept out of the heading's accessible name, so the dialog is still announced by its title.
- **`ModalFooter`** is the body's last child. It pins to the foot while the body scrolls, ruled off by a `hairline` and spanning the full width. The left side is a status line in `ink-3` saying what the action will do or what came back ("14 files will be renamed", "12 results from 2 indexers"); the right side holds the actions, `default` Cancel or Close first and the one filled button last.
- **`ModalNote`** is the shared `Note`, with a tone icon and a short line in `ink-2`. It supports the same tones and optional action as `Note`.

An empty dialog uses `EmptyState compact` with a `mood` (asking when it needs something from you, asleep when there is simply nothing yet), and a dialog that only has that to say sizes to its content instead of holding a wide table's width.

Used by Search releases, Manage tags, Rename files and Compare sources.

Pass a title to `ModalHeading` and add its optional description only if it explains a condition or consequence. Give `ModalFooter` the action buttons and a status when it helps explain the outcome.
