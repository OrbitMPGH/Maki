/** Values of the backend's `IncognitoMode` enum, as they travel over the wire. */
export type IncognitoMode = 'Off' | 'ScrobbleOnly' | 'Full'

/**
 * One label per mode, shared by the series page's switcher, the add-series form and the settings
 * rules, so the three places a user meets this setting describe it the same way.
 */
export const INCOGNITO_OPTIONS: { value: IncognitoMode; label: string }[] = [
  { value: 'Off', label: 'Off' },
  { value: 'ScrobbleOnly', label: 'No scrobble' },
  { value: 'Full', label: 'Full' },
]
