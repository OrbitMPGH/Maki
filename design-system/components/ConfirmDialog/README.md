# ConfirmDialog

The one question before something that cannot be taken back: what is about to go, what follows from it, and a red button that names the action.

It is an ordinary utility modal (centred, `surface-raised`, `radius-lg`, a sectioned header over a `hairline`, a 3px blur over a 55% scrim) holding one sentence at `body` size and two buttons on the right: `default` Cancel, then the confirm button with `color="var(--danger-fill)"`. Every irreversible action in the app asks this same way.

**Copy:** the title is the action as a question with the object named ("Delete Night reading?", "Revoke Laptop?"). The body says what follows and ends with "This can't be undone." The confirm label repeats the verb with its object ("Delete profile", "Delete user", "Revoke"), never "OK" or "Yes".

Use `danger-fill`, not `danger`, for the filled button: white on `danger-fill` is 5.6:1 in every theme, while white on dark `danger` is only 3.4:1.

**The consumer provides:** `opened`, `onClose`, `title`, `children` (the consequence), `confirmLabel`, `onConfirm`, and `loading` while the request runs (the confirm button spins, the dialog stays open until it succeeds).
