import { Notification, Portal } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import type { CSSProperties, ReactNode } from 'react'
import { Toaster, toast } from 'sonner'
import { IconAlertCircle, IconAlertTriangle, IconCircleCheck, IconInfoCircle, type Icon } from './icons'

export interface ToastData {
  /** Showing a toast with an id that is already up replaces it in place. */
  id?: string
  title?: ReactNode
  message: ReactNode
  /** A tone token such as `var(--ok)`; none takes the accent. */
  color?: string
  icon?: ReactNode
  loading?: boolean
  /** Milliseconds on screen, or false to stay until dismissed. */
  autoClose?: number | false
  withCloseButton?: boolean
  /** Runs however the toast goes: its close button, a swipe, or `hide`. Not on timing out. */
  onClose?: () => void
}

const TOAST_DURATION = 6000

// Sonner numbers toasts with no id itself, and `hide` takes the string `show` returned, which would
// never match a numeric id. Every toast gets a string one here instead.
let nextId = 0

const TONE_ICONS: Record<string, Icon> = {
  'var(--ok)': IconCircleCheck,
  'var(--danger)': IconAlertCircle,
  'var(--warn)': IconAlertTriangle,
}

/**
 * Sonner owns the behaviour (stacking, swipe to dismiss, pausing on hover and in a hidden tab) and
 * Mantine's Notification draws each toast, so a toast keeps Maki's look and theme.css styles it as
 * before. Every toast carries an icon in its tone: without one Mantine draws a coloured bar down
 * the left edge.
 */
function show(data: ToastData): string {
  const Tone = TONE_ICONS[data.color ?? ''] ?? IconInfoCircle
  const icon = data.loading ? undefined : (data.icon ?? <Tone size={18} />)
  const id = data.id ?? `toast-${++nextId}`
  toast.custom(
      (toastId) => (
          <Notification
              className="maki-toast"
              title={data.title}
              color={data.color}
              icon={icon}
              loading={data.loading}
              withCloseButton={data.withCloseButton ?? true}
              onClose={() => toast.dismiss(toastId)}
          >
            {data.message}
          </Notification>
      ),
      {
        id,
        duration: data.autoClose === false ? Infinity : (data.autoClose ?? TOAST_DURATION),
        onDismiss: data.onClose,
      },
  )
  return id
}

/** Import this, never `sonner` directly, so every toast goes through the same look. */
export const notifications = {
  show,
  /** Replaces the toast with `data.id`, or shows a new one when it has already gone. */
  update: show,
  hide: (id: string) => {
    toast.dismiss(id)
  },
  /** Dismisses every toast. */
  clean: () => {
    toast.dismiss()
  },
}

/**
 * Mounted once, in the app shell and Storybook's provider tree. Portalled to the body as Mantine's
 * toasts were: Sonner renders in place, and a transformed ancestor (a page transition) would turn
 * its fixed position into one relative to that ancestor.
 */
export function AppToaster() {
  const { t } = useLingui()
  return (
      <Portal>
        <Toaster
            position="bottom-right"
            duration={TOAST_DURATION}
            visibleToasts={4}
            gap={10}
            offset={16}
            mobileOffset={12}
            containerAriaLabel={t`Notifications`}
            className="maki-toaster"
            style={{ '--width': '440px' } as CSSProperties}
        />
      </Portal>
  )
}
