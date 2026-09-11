/** Values of the backend's `SeriesNotificationMode` enum, as they travel over the wire. */
export type SeriesNotificationMode = 'Default' | 'All' | 'Reading' | 'Muted'

/**
 * One label per mode, shared by the series page's switcher and the library's bulk action, so both
 * places a user meets this setting describe it the same way.
 *
 * `Default` defers to the global choice on the notification settings card, which is why its label
 * names neither outcome — what it resolves to is a setting away.
 */
export const SERIES_NOTIFICATION_OPTIONS: { value: SeriesNotificationMode; label: string }[] = [
  { value: 'Default', label: 'Default' },
  { value: 'All', label: 'Every chapter' },
  { value: 'Reading', label: 'While reading' },
  { value: 'Muted', label: 'Muted' },
]

/** Longer copy for the bulk modal, where there is room to say what each one actually does. */
export const SERIES_NOTIFICATION_HELP: Record<SeriesNotificationMode, string> = {
  Default: 'Follows the default on your notification settings.',
  All: 'Tells you about every new chapter, whatever your default is.',
  Reading: "Only while you're partway through the series and haven't marked it finished.",
  Muted: 'Nothing from this series. Admins still get download failures.',
}

/**
 * The two the *global* default may be. `Default` would point at itself, and a global `Muted` is
 * what switching the per-type "New chapters available" toggle off already does.
 */
export const SERIES_DEFAULT_OPTIONS: { value: SeriesNotificationMode; label: string }[] = [
  { value: 'All', label: 'Every series' },
  { value: 'Reading', label: "Series I'm reading" },
]
