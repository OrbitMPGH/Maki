import { useState } from 'react'
import { i18n } from '@lingui/core'
import { Button, Checkbox, Modal, Radio, Stack, Text } from '@mantine/core'
import { notifications } from '../../lib/toast'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { useUndoSeriesConversion, type SeriesConversion } from '../../api/pageConversion'
import { formatBytes } from '../../format'
import { useCheckedWithLabel } from '../pageconversion/labels'
import { IconArrowBackUp } from '../../lib/icons'
import { ModalFooter, ModalHeading, ModalNote } from '../ui/ModalShell'

type Choice = 'restore' | 'again'

/**
 * Puts a series' converted files back from the originals the trash still holds, and can queue them
 * for another conversion under the current settings or switch the series to Never.
 */
export function UndoConversionModal({
  opened,
  onClose,
  seriesId,
  history,
}: {
  opened: boolean
  onClose: () => void
  seriesId: number
  history: SeriesConversion
}) {
  const { t } = useLingui()
  const label = useCheckedWithLabel()
  const undo = useUndoSeriesConversion()
  const [choice, setChoice] = useState<Choice>('restore')
  const [setNever, setSetNever] = useState(false)

  const again = choice === 'again'
  const size = formatBytes(history.restorableBytes)
  const settings = history.checkedWith.map(label)
  const list = typeof Intl.ListFormat === 'function' ? new Intl.ListFormat(i18n.locale || undefined).format(settings) : settings.join(', ')

  const close = () => {
    setChoice('restore')
    setSetNever(false)
    onClose()
  }

  const submit = () => {
    undo.mutate(
      { seriesId, setNever: setNever && !again, convertAgain: again },
      {
        onSuccess: (result) => {
          const restored = plural(result.restored, { one: 'Restored # file', other: 'Restored # files' })
          notifications.show({
            color: result.restored > 0 && result.skipped === 0 ? 'var(--ok)' : 'var(--warn)',
            title: result.restored > 0 ? restored : now`Nothing was restored`,
            message:
              result.reasons.length > 0 ? (
                <>
                  {result.reasons.map((reason, i) => (
                    <div key={i}>{reason}</div>
                  ))}
                </>
              ) : undefined,
          })
          close()
        },
      },
    )
  }

  return (
    <Modal
      opened={opened}
      onClose={close}
      centered
      title={
        <ModalHeading
          title={<Trans>Undo conversion</Trans>}
          description={<Trans>Put back the files as they were downloaded.</Trans>}
        />
      }
    >
      <Stack gap="md">
        <Text size="sm">
          <Plural
            value={history.restorableFiles}
            one={`# file has its original in the trash (${size}).`}
            other={`# files have their original in the trash (${size}).`}
          />
        </Text>
        <ModalNote tone="info">
          <Stack gap={2}>
            <span>
              <Trans>The others were converted on download or their original has been purged.</Trans>
            </span>
            {settings.length > 0 && (
              <span>
                <Trans>Converted with {list}.</Trans>
              </span>
            )}
          </Stack>
        </ModalNote>

        <Radio.Group
          value={choice}
          onChange={(value) => setChoice(value as Choice)}
          label={t`What to do with them`}
        >
          <div className="modal-choice-list">
            <Radio value="restore" label={t`Restore the originals`} />
            <Radio value="again" label={t`Restore and convert again with the current settings`} />
          </div>
        </Radio.Group>

        <Checkbox
          label={t`Also set this series to Never`}
          checked={setNever && !again}
          disabled={again}
          onChange={(e) => setSetNever(e.currentTarget.checked)}
        />
      </Stack>

      <ModalFooter
        status={
          <Plural
            value={history.convertedFiles}
            one={`${history.restorableFiles} of # file`}
            other={`${history.restorableFiles} of # files`}
          />
        }
      >
        <Button variant="default" onClick={close}>
          <Trans>Cancel</Trans>
        </Button>
        <Button leftSection={<IconArrowBackUp size={16} />} loading={undo.isPending} onClick={submit}>
          <Trans>Undo</Trans>
        </Button>
      </ModalFooter>
    </Modal>
  )
}
