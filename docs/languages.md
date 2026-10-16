# Languages

The interface ships in English, German, French, Spanish, Brazilian Portuguese, Polish, Russian,
Turkish, Japanese, Simplified Chinese, and Korean. Set yours under
**Settings → My account → Language**, or leave it on Automatic to follow the browser. The choice
is stored per account and follows you across devices. It is separate from **Title language**,
which controls how series titles are displayed.

English is the source language. **Everything else is machine-translated and marked as needing
review**, so expect awkward phrasing. Untranslated strings fall back to English.

Corrections are the easiest way to contribute. The simplest route is
[Maki on Hosted Weblate](https://hosted.weblate.org/engage/maki/): sign in, pick your language, and
fix or approve strings in the browser. Weblate sends the changes back here as a pull request. The
translated percentage only counts strings a person has approved, so it starts low even though every
string already has a machine translation.

If you would rather work in the repo, the catalogs are gettext PO files under `locales/`, one
directory per language. Or open an issue quoting the string and what it should say.
[`scripts/i18n/glossary.md`](../scripts/i18n/glossary.md) lists the terms that stay in English.
