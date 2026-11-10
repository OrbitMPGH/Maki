---
type: fix
section: Settings
---
Fixed single sign-on accounts being unable to turn on the OPDS catalogue or regenerate its token, since the form waited for a password they don't have. Creating an API key or OPDS token from a single sign-on account now says up front when it needs a fresh sign-in, with a button that signs you in again and brings you back. An account whose password login is switched off by "single sign-on only" is no longer asked for that old password either.
