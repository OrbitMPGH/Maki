import { Button, Divider, Group, Select, Stack, Text } from '@mantine/core'
import { IconPlayerPause, IconPlayerPlay, IconSettings } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { notifications } from '../../lib/toast'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { SettingsSection } from './SettingsSection'
import { useSettingsSummary } from '../../components/pageconversion/labels'
import { formatBytes, formatDateTime } from '../../format'
import {
  usePageConversion,
  usePausePageConversion,
  useSavePageConversionParallel,
  useStartPageConversion,
  type PageConversionStatus,
} from '../../api/pageConversion'

/** Converts stored pages to WebP or AVIF: the current choice, the pass over the existing library, and the way into the wizard that changes either. */
export function PageConversionSection() {
  const { data: status } = usePageConversion()
  const summarize = useSettingsSummary()

  return (
    <SettingsSection
      id="page-conversion"
      title={<Trans>Page conversion</Trans>}
      description={<Trans>Stores chapter pages in a smaller image format.</Trans>}
    >
      {status && (
        <Stack>
          <Group justify="space-between" wrap="wrap" gap="sm">
            <Stack gap={2}>
              <Text fw={500} size="sm">
                <Trans>Current setting</Trans>
              </Text>
              <Text size="sm">{summarize(status.settings, status.library.ruleCount)}</Text>
              {status.settings.format && status.settings.pngLossless && (
                <Text size="xs" c="var(--ink-3)">
                  <Trans>PNG pages are kept lossless.</Trans>
                </Text>
              )}
              {status.settings.format && status.library.overrideSeries > 0 && (
                <Text size="xs" c="var(--ink-3)">
                  <Plural
                    value={status.library.overrideSeries}
                    one="# series has its own setting in its menu."
                    other="# series have their own setting in their menus."
                  />
                </Text>
              )}
            </Stack>
            {status.settings.format ? (
              <Button
                component={Link}
                to="/settings/page-conversion?step=2"
                variant="default"
                size="xs"
                leftSection={<IconSettings size={14} />}
              >
                <Trans>Change settings</Trans>
              </Button>
            ) : (
              <Button component={Link} to="/settings/page-conversion" size="xs" leftSection={<IconSettings size={14} />}>
                <Trans>Set up conversion</Trans>
              </Button>
            )}
          </Group>

          <Divider />
          <LibraryPass status={status} />

          <Divider />
          <ParallelLimit parallel={status.parallel} />
        </Stack>
      )}
    </SettingsSection>
  )
}

function LibraryPass({ status }: { status: PageConversionStatus }) {
  const start = useStartPageConversion()
  const pause = usePausePageConversion()
  const { pass, library } = status
  const { pendingFiles, convertedFiles } = library
  const totals = pass.totals
  const off = status.settings.format === null
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

function ParallelLimit({ parallel }: { parallel: PageConversionStatus['parallel'] }) {
  const { t } = useLingui()
  const save = useSavePageConversionParallel()
  const auto = parallel.default
  const data = [
    { value: 'auto', label: t`Automatic (${auto})` },
    ...Array.from({ length: parallel.max }, (_, i) => ({ value: String(i + 1), label: String(i + 1) })),
  ]

  return (
    <Select
      styles={{ wrapper: { maxWidth: 200 } }}
      label={t`Pages converted at once`}
      description={t`Applies to the library pass and to new downloads. Higher finishes sooner but uses more of the server's CPU while it runs.`}
      data={data}
      value={parallel.limit === null ? 'auto' : String(parallel.limit)}
      onChange={(value) =>
        value &&
        save.mutate(value === 'auto' ? null : Number(value), {
          onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }),
        })
      }
      disabled={save.isPending}
      allowDeselect={false}
    />
  )
}

function PausedLine({ hold, pending }: { hold: PageConversionStatus['pass']['hold']; pending: number }) {
  if (hold === 'lowDisk') {
    return <Trans>Paused: converting the next file would leave less than 1 GB free on the library drive.</Trans>
  }
  if (hold === 'conversionOff') {
    return <Trans>Paused because page conversion was switched off.</Trans>
  }
  if (hold === 'settingsChanged') {
    return <Trans>Paused because the settings changed. Restart the pass to use them.</Trans>
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
