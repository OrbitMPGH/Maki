# ModalShell

The anatomy every utility modal shares: a title with one line saying what the dialog is for, the body, and a footer that holds the outcome and the actions. Three small pieces inside a Mantine `Modal`, which already brings the raised card, the sectioned header over a `hairline` and the scrolling body.

- **`ModalHeading`** goes in the Modal's `title`. The title sets in the display face; the line under it is `meta` size in `ink-3`, sentence case, one sentence ("Searches your indexers. Grab sends a release to your download client."). The line is kept out of the heading's accessible name, so the dialog is still announced by its title.
- **`ModalFooter`** is the body's last child. It pins to the foot while the body scrolls, ruled off by a `hairline` and spanning the full width. The left side is a status line in `ink-3` saying what the action will do or what came back ("14 files will be renamed", "12 results from 2 indexers"); the right side holds the actions, `default` Cancel or Close first and the one filled button last.
- **`ModalNote`** is one line that needs attention inside a dialog: a `warn` (or `info`) icon, then the words in `ink-2`. It replaces a filled alert block. A note can carry a second line in `ink-3` with a link to the setting that fixes it.

An empty dialog uses `EmptyState compact` with a `mood` (asking when it needs something from you, asleep when there is simply nothing yet), and a dialog that only has that to say sizes to its content instead of holding a wide table's width.

Used by Search releases, Manage tags, Rename files and Compare sources.

**The consumer provides:** `ModalHeading` `title` and `description`; `ModalFooter` `status` and the action buttons as children; `ModalNote` `tone` and its text.
