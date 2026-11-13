import { useEffect, useState } from 'react'
import { Button, Modal, Stack } from '@mantine/core'
import { ModalFooter, ModalHeading } from './ui/ModalShell'
import { Trans, useLingui } from '@lingui/react/macro'
import { useAnnouncements, useSeenAppearanceAnnouncement } from '../api/hooks'
import { useThemeChoice } from '../theme-context'
import { AppearancePicker } from './AppearancePicker'

/**
 * Tells someone who was already using Maki that background and accent are separate choices now, and
 * that the default moved from Night to Tinted black, with the picker right there so they can go back.
 *
 * Shown once per account, gated by the server exactly like `LanguageAnnouncementModal`. Waits for
 * that one to be dismissed first so an account with both pending never gets two stacked dialogs.
 */
export default function AppearanceAnnouncementModal() {
  const { t } = useLingui()
  const { data: announcements } = useAnnouncements()
  const seen = useSeenAppearanceAnnouncement()
  const { background, setBackground } = useThemeChoice()

  // Latched for the same reason as the language modal: a language pick clears the query cache.
  const [open, setOpen] = useState(false)
  const [decided, setDecided] = useState(false)
  useEffect(() => {
    if (decided || !announcements || announcements.language) return
    setDecided(true)
    setOpen(announcements.appearance)
    // Every dark theme before this release sat on Night, so nobody chose it over Tinted black.
    // Show them the new default; Night is one click away and labelled as the old look.
    if (announcements.appearance && background === 'night') setBackground('tinted')
  }, [announcements, decided, background, setBackground])

  const close = () => {
    setOpen(false)
    seen.mutate()
  }

  return (
    <Modal
      opened={open}
      onClose={close}
      title={
        <ModalHeading
          title={t`More ways to make Maki yours`}
          description={
            <Trans>
              Background and accent colour are now picked separately, with new backgrounds to choose
              from. Tinted black is the new default. To keep the old look, pick Night. You can
              change this any time under Settings, Appearance.
            </Trans>
          }
        />
      }
      size="lg"
    >
      <Stack gap="lg">
        <AppearancePicker
          backgroundHints={{
            tinted: { label: t`New default` },
            night: { label: t`Previous default`, muted: true },
          }}
        />

        <ModalFooter>
          <Button onClick={close}>
            <Trans>Done</Trans>
          </Button>
        </ModalFooter>
      </Stack>
    </Modal>
  )
}
