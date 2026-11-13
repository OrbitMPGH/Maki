import { useEffect, useState } from 'react'
import { Button, Modal, Select, Stack, Text } from '@mantine/core'
import { ModalFooter, ModalHeading } from './ui/ModalShell'
import { Trans, useLingui } from '@lingui/react/macro'
import { useAnnouncements, useSeenLanguageAnnouncement, useUiSettings } from '../api/hooks'
import { useApplyLanguage, useLanguageOptions } from './ui/language'
import { MODAL_SIZE } from './ui/modalSizes'

/**
 * Tells someone who was already using Maki that it speaks other languages now, and lets them pick
 * one without leaving the page they are on.
 *
 * Shown once and never again: the server hands out the notice only to accounts that existed before
 * translations shipped, and the first close marks it seen for good. Nothing here decides that —
 * asking twice whether to show it is exactly the bug a client-side rule would introduce.
 */
export default function LanguageAnnouncementModal() {
  const { t } = useLingui()
  const { data: announcements } = useAnnouncements()
  const { data: ui } = useUiSettings()
  const seen = useSeenLanguageAnnouncement()
  const options = useLanguageOptions()
  const apply = useApplyLanguage()

  // Latched rather than read straight off the query, because applying a language clears the whole
  // query cache (see `useApplyLanguage`). Reading `announcements` directly would close the modal
  // out from under the person the moment they picked something.
  const [open, setOpen] = useState(false)
  const [decided, setDecided] = useState(false)
  useEffect(() => {
    if (decided || !announcements) return
    setDecided(true)
    setOpen(announcements.language)
  }, [announcements, decided])

  // Same reason: the settings query is gone for a moment after a pick, and the Select must not drop
  // back to Automatic while it reloads.
  const [choice, setChoice] = useState<string | null>(null)

  const close = () => {
    setOpen(false)
    seen.mutate()
  }

  const languageCount = options.length - 1

  return (
    <Modal
      opened={open}
      onClose={close}
      title={
        <ModalHeading
          title={t`Maki speaks your language`}
          description={
            <Trans>
              Maki's interface is now translated into {languageCount} languages. Pick yours here, or
              change it any time under Settings, Language.
            </Trans>
          }
        />
      }
      size={MODAL_SIZE.narrow}
    >
      <Stack gap="lg">
        <Select
          label={t`Language`}
          data={options}
          value={choice ?? ui?.language ?? ''}
          onChange={(value) => {
            if (value === null) return
            setChoice(value)
            apply?.(value)
          }}
          disabled={!apply}
          allowDeselect={false}
          comboboxProps={{ withinPortal: true }}
        />

        <Text size="xs" c="var(--ink-3)">
          <Trans>
            Translations other than English are machine-made and being corrected over time; anything
            still untranslated falls back to English.
          </Trans>
        </Text>

        <ModalFooter>
          <Button onClick={close}>
            <Trans>Done</Trans>
          </Button>
        </ModalFooter>
      </Stack>
    </Modal>
  )
}
