# SearchInput

The one search and filter-by-text box: a search icon on the left and, once there is text, a clear button on the right. It is a Mantine `TextInput` at size `sm`; only the big catalogue search and the command palette go larger.

The value is controlled, and `onClear` is required, since clearing is the caller's state. While the box is empty the right side can show `idleSection`, such as a shortcut hint. The clear button is labelled "Clear search". The input is `type="text"` with `enterKeyHint="search"` and autocomplete off, so a phone keyboard shows a Search key and browsers do not offer saved values. It forwards a ref and all other `TextInput` props.

**The consumer provides:** `value`, `onChange`, `onClear`, a `placeholder` or `aria-label` that says what is searched, and optionally `idleSection`.
