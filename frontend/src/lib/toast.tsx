import { notifications as mantine, type NotificationData } from '@mantine/notifications'
import { IconAlertCircle, IconAlertTriangle, IconCircleCheck, IconInfoCircle, type Icon } from '@tabler/icons-react'

const TONE_ICONS: Record<string, Icon> = {
  'var(--ok)': IconCircleCheck,
  'var(--danger)': IconAlertCircle,
  'var(--warn)': IconAlertTriangle,
}

/** A toast without an icon gets Mantine's coloured bar down its left edge, so every toast carries one. */
function withIcon(data: NotificationData): NotificationData {
  if (data.icon !== undefined || data.loading) return data
  const Tone = TONE_ICONS[data.color ?? ''] ?? IconInfoCircle
  return { ...data, icon: <Tone size={18} stroke={1.8} /> }
}

/** Drop-in for Mantine's `notifications`: import this one, never `@mantine/notifications` directly. */
export const notifications = {
  ...mantine,
  show: (data: NotificationData) => mantine.show(withIcon(data)),
  update: (data: NotificationData) => mantine.update(withIcon(data)),
}
