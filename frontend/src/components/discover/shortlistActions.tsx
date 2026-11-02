import { Button, Group, Text } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import {
  OTHER_SOURCE,
  useIsShortlisted,
  useRemoveFromShortlist,
  useRestoreShortlistEntry,
  useSaveToShortlist,
  type ShortlistSource,
} from '../../api/shortlist'
import { notifications } from '../../lib/toast'

/** Removes an entry and offers Undo, which posts the entry the server returned back to `restore`. */
export function useRemoveWithUndo() {
  const { t } = useLingui()
  const remove = useRemoveFromShortlist()
  const restore = useRestoreShortlistEntry()
  return (id: number, title: string) => {
    // mutateAsync rather than call-level callbacks: the row that started this unmounts as soon as
    // the optimistic removal lands, and callbacks of an unmounted observer never run.
    remove
      .mutateAsync({ id })
      .then((removed) => {
        if (!removed) return
        const toast = notifications.show({
          autoClose: 8000,
          message: (
            <Group gap="xs" wrap="wrap">
              <Text size="sm">{t`Removed ${title} from your shortlist.`}</Text>
              <Button
                size="xs"
                variant="subtle"
                onClick={() => {
                  notifications.hide(toast)
                  restore.mutate(removed)
                }}
              >
                {t`Undo`}
              </Button>
            </Group>
          ),
        })
      })
      .catch(() => {})
  }
}

/**
 * Save or remove one title from where it is shown. `providerId` is the card's id; a merged entry
 * answers to it too, so the server finds the stored key itself.
 */
export function useShortlistToggle(providerId: string, title: string, source: ShortlistSource = OTHER_SOURCE) {
  const saved = useIsShortlisted(providerId)
  const save = useSaveToShortlist()
  const removeWithUndo = useRemoveWithUndo()
  const id = Number(providerId)
  const usable = Number.isSafeInteger(id) && id > 0
  return {
    saved,
    usable,
    toggle: () => {
      if (!usable) return
      if (saved) removeWithUndo(id, title)
      else save.mutate({ id, source })
    },
  }
}
