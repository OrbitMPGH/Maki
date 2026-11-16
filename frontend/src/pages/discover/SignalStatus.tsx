import type { Icon } from '../../lib/icons'

export interface SignalStatusInfo {
  icon: Icon
  label: string
  /** The word's colour: a status hue only while the title is in that state, neutral ink otherwise. */
  color: string
  /** The glyph's colour when it differs from the word's, as for the rating star. */
  iconColor?: string
}

/** One taste signal as an icon and a coloured word, in place of a pill. */
export function SignalStatus({ icon: StatusIcon, label, color, iconColor }: SignalStatusInfo) {
  return (
    <span className="signal-status" style={{ color }}>
      <StatusIcon size={16} aria-hidden style={iconColor ? { color: iconColor } : undefined} />
      {label}
    </span>
  )
}
