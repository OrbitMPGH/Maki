import { useState } from 'react'
import { Button, Divider, Group, Radio, Stack, Text } from '@mantine/core'
import { IconPlayerPause, IconPlayerPlay } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { SettingsSection } from './SettingsSection'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'
import { formatBytes, formatDateTime } from '../../format'
import {
  usePageConversion,
  usePausePageConversion,
  useSavePageConversion,
  useStartPageConversion,
  type PageConversionSettings,
  type PageConversionStatus,
} from '../../api/pageConversion'

const DEFAULT_QUALITY = { webp: 80, avif: 55 } as const

/** Converts stored pages to WebP or AVIF: the format for new downloads and the pass over the existing library. */
export function PageConversionSection() {
  const { t } = useLingui()
  const { data: status } = usePageConversion()
  const save = useSavePageConversion()
  const [draft, setDraft] = useState<PageConversionSettings | null>(null)

  const saved = status?.settings
  const current = draft ?? saved
  const dirty =
    draft !== null &&
    saved !== undefined &&
    (draft.format !== saved.format ||
      draft.webpQuality !== saved.webpQuality ||
      draft.avifQuality !== saved.avifQuality)
  const update = (changes: Partial<PageConversionSettings>) => {
    if (current) setDraft({ ...current, ...changes })
  }

  return (
    <SettingsSection
      id="page-conversion"
      title={<Trans>Page conversion</Trans>}
      description={
        <Trans>Stores chapter pages in a smaller image format.</Trans>
      }
      dirty={dirty}
      saving={save.isPending}
      onSave={() => {
        if (!draft) return
        save.mutate(draft, {
          onSuccess: () => {
            setDraft(null)
            notifications.show({ message: now`Saved`, color: 'var(--ok)' })
          },
        })
      }}
      onDiscard={() => setDraft(null)}
    >
      {current && status && (
        <Stack>
          <Radio.Group
            label={t`Format`}
            value={current.format}
            onChange={(format) => update({ format: format as PageConversionSettings['format'] })}
          >
            <Stack gap="sm" mt="xs">
              <Radio value="off" label={t`Off`} />
              <Radio
                value="webp"
                label={t`WebP`}
                description={t`Opens in every reader Maki works with.`}
              />
              <Radio
                value="avif"
                label={t`AVIF`}
                description={t`Smaller than WebP. KOReader and iOS before 16 can't open it.`}
              />
            </Stack>
          </Radio.Group>

          {current.format !== 'off' && <QualityInput settings={current} onChange={update} />}

          <Divider />
          <LibraryPass status={status} />
        </Stack>
      )}
    </SettingsSection>
  )
}

function QualityInput({
  settings,
  onChange,
}: {
  settings: PageConversionSettings
  onChange: (changes: Partial<PageConversionSettings>) => void
}) {
  const { t } = useLingui()
  if (settings.format === 'avif') {
    const fallback = DEFAULT_QUALITY.avif
    return (
      <SettingsNumberInput
        label={t`AVIF quality`}
        description={t`1 to 100, higher keeps more detail. Default ${fallback}.`}
        min={1}
        max={100}
        value={settings.avifQuality}
        onChange={(avifQuality) => onChange({ avifQuality })}
      />
    )
  }

  const fallback = DEFAULT_QUALITY.webp
  return (
    <SettingsNumberInput
      label={t`WebP quality`}
      description={t`1 to 100, higher keeps more detail. Default ${fallback}.`}
      min={1}
      max={100}
      value={settings.webpQuality}
      onChange={(webpQuality) => onChange({ webpQuality })}
    />
  )
}

function LibraryPass({ status }: { status: PageConversionStatus }) {
  const start = useStartPageConversion()
  const pause = usePausePageConversion()
  const { pass, library } = status
  const { pendingFiles, convertedFiles } = library
  const totals = pass.totals
  const off = status.settings.format === 'off'
  const saved = formatBytes(library.savedBytes)
  const trash = formatBytes(library.trashBytes)
  const days = library.trashDays

  return (
    <Stack gap="xs">
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Text fw={500} size="sm">
          <Trans>Existing library</Trans>
        </Text>
        {pass.state === 'running' ? (
          <Button
            variant="default"
            size="xs"
            leftSection={<IconPlayerPause size={14} />}
            loading={pause.isPending}
            onClick={() => pause.mutate(undefined)}
          >
            <Trans>Pause</Trans>
          </Button>
        ) : (
          <Button
            variant="default"
            size="xs"
            leftSection={<IconPlayerPlay size={14} />}
            disabled={off || pendingFiles === 0}
            loading={start.isPending}
            onClick={() => start.mutate(undefined)}
          >
            {pass.state === 'paused' ? <Trans>Resume</Trans> : <Trans>Convert library</Trans>}
          </Button>
        )}
      </Group>

      <Text size="sm" c="var(--ink-3)">
        {pass.state === 'running' ? (
          <Plural
            value={pendingFiles}
            one="Converting, # file left to check."
            other="Converting, # files left to check."
          />
        ) : pass.state === 'paused' ? (
          <PausedLine hold={pass.hold} pending={pendingFiles} />
        ) : pendingFiles === 0 ? (
          <Trans>Every file has been checked.</Trans>
        ) : off ? (
          <Plural
            value={pendingFiles}
            one="# file has not been checked. Choose a format to convert it."
            other="# files have not been checked. Choose a format to convert them."
          />
        ) : (
          <Plural
            value={pendingFiles}
            one="# file has not been checked yet."
            other="# files have not been checked yet."
          />
        )}
      </Text>

      {pass.currentFile && (
        <Text size="xs" c="var(--ink-3)" truncate="end" title={pass.currentFile}>
          {pass.currentFile}
        </Text>
      )}

      {totals && <PassTotals totals={totals} running={pass.state !== 'idle'} />}

      <Text size="sm">
        <Plural
          value={convertedFiles}
          one={`# file converted so far, ${saved} saved.`}
          other={`# files converted so far, ${saved} saved.`}
        />
      </Text>
      {library.trashFiles > 0 && (
        <Text size="sm" c="var(--ink-3)">
          <Plural
            value={days}
            one={`The originals (${trash}) wait in the upgrade trash for # day, then are deleted.`}
            other={`The originals (${trash}) wait in the upgrade trash for # days, then are deleted.`}
          />
        </Text>
      )}
    </Stack>
  )
}

function PausedLine({ hold, pending }: { hold: PageConversionStatus['pass']['hold']; pending: number }) {
  if (hold === 'lowDisk') {
    return <Trans>Paused: converting the next file would leave less than 1 GB free on the library drive.</Trans>
  }
  if (hold === 'conversionOff') {
    return <Trans>Paused because page conversion was switched off.</Trans>
  }
  return <Plural value={pending} one="Paused, # file left to check." other="Paused, # files left to check." />
}

function PassTotals({
  totals,
  running,
}: {
  totals: NonNullable<PageConversionStatus['pass']['totals']>
  running: boolean
}) {
  const saved = formatBytes(totals.savedBytes)
  const { converted, unchanged, hardlinked, failed } = totals
  const finished = totals.finishedAtUtc ? formatDateTime(totals.finishedAtUtc) : null
  return (
    <Text size="sm" c="var(--ink-3)">
      {running || !finished ? <Trans>This pass:</Trans> : <Trans>Last pass, finished {finished}:</Trans>}{' '}
      <Trans>
        {converted} converted ({saved} saved), {unchanged} left as they were, {hardlinked} hardlinked, {failed} passed
        over for now.
      </Trans>
    </Text>
  )
}
