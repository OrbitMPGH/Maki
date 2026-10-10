import { Alert, Badge, Card, Group, Loader, Modal, Stack, Text } from '@mantine/core'
import { IconAlertTriangle, IconArrowRight, IconInfoCircle } from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { errorText } from '../../api/errorText'
import {
  IMPORT_SKIP_REASONS,
  useImportPlan,
  type ImportPlan,
  type ImportPlanFile,
  type ImportRequestItem,
} from '../../api/libraryImport'
import { formatBytes } from '../../format'
import { useLabel } from '../../i18n-context'

/** What the import builds a CBZ from (`ComicSourceKind`). */
const KIND_LABELS: Record<string, MessageDescriptor> = {
  zip: msg`a zip`,
  repack: msg`a RAR, 7z or tar archive`,
  looseImages: msg`loose page images`,
}

/** What a file's name says it holds, before any chapter list exists to check it against. */
function ParsedLabel({ file }: { file: ImportPlanFile }) {
  const { number, volume, volumeEnd } = file
  if (number !== null) return <Trans>Ch.{number}</Trans>
  if (volume !== null && volumeEnd !== null && volumeEnd !== volume) {
    return (
      <Trans>
        Vol.{volume}-{volumeEnd}
      </Trans>
    )
  }
  if (volume !== null) return <Trans>Vol.{volume}</Trans>
  return null
}

function ExpectedLink({ file, deferred }: { file: ImportPlanFile; deferred: boolean }) {
  const label = useLabel()
  if (file.loneFile) {
    return <Trans>no number in the name, links if the series has exactly one chapter</Trans>
  }
  if (file.unlinked) {
    const reason = label(IMPORT_SKIP_REASONS[file.unlinked] ?? file.unlinked)
    return <Trans>stays unlinked: {reason}</Trans>
  }
  if (deferred || file.chapters === null) {
    return (
      <Trans>
        expected to link as <ParsedLabel file={file} />
      </Trans>
    )
  }
  const chapters = file.chapters.slice(0, 12).join(', ')
  const extra = file.chapters.length - 12
  return extra > 0 ? (
    <Trans>
      links Ch. {chapters} +{extra} more
    </Trans>
  ) : (
    <Trans>links Ch. {chapters}</Trans>
  )
}

function FileAction({ file }: { file: ImportPlanFile }) {
  const label = useLabel()
  const { aside, source } = file
  switch (file.action) {
    case 'build': {
      const kind = label(KIND_LABELS[file.kind] ?? file.kind)
      return source && source !== '.' ? (
        <Trans>CBZ built from {kind} ({source}), the original stays</Trans>
      ) : (
        <Trans>CBZ built from {kind}, the original stays</Trans>
      )
    }
    case 'rebuildInPlace':
      return <Trans>not really a CBZ: the original is renamed to {aside} and a CBZ is built under this name</Trans>
    case 'useExisting':
      return <Trans>a file already has this name, so that file is registered</Trans>
    default:
      return <Trans>registered as it is</Trans>
  }
}

function FolderPlan({ plan }: { plan: ImportPlan }) {
  const label = useLabel()
  const { folderName, targetFolderName, seriesFolderName, seriesTitle } = plan
  const files = plan.files ?? []
  const skipped = plan.skipped ?? []
  const built = files.filter((f) => f.action === 'build' || f.action === 'rebuildInPlace').length

  if (plan.error) {
    return (
      <Alert color="var(--danger)" icon={<IconAlertTriangle size={16} />} title={folderName}>
        {plan.error}
      </Alert>
    )
  }

  return (
    <Card withBorder padding="sm" radius="md">
      <Stack gap={6}>
        <Group gap={6} wrap="wrap">
          <Text size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>
            {folderName}
          </Text>
          {plan.folderAction !== 'keep' && (
            <>
              <IconArrowRight size={14} />
              <Text size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>
                {targetFolderName}
              </Text>
            </>
          )}
          {plan.existingSeriesId !== null && (
            <Badge size="xs" variant="light">
              <Trans>In library</Trans>
            </Badge>
          )}
        </Group>
        <Text size="xs" c="var(--ink-3)">
          {plan.folderAction === 'rename' ? (
            <Trans>Imported as {seriesTitle}. The folder is renamed.</Trans>
          ) : plan.folderAction === 'merge' ? (
            <Trans>Imported into {seriesTitle}. The files move into its existing folder.</Trans>
          ) : (
            <Trans>Imported as {seriesTitle}. The folder keeps its name.</Trans>
          )}{' '}
          {seriesFolderName !== targetFolderName && (
            <Trans>New downloads go to {seriesFolderName}.</Trans>
          )}
        </Text>
        <Text size="xs" c="var(--ink-2)">
          <Plural value={files.length} one="Registers # file" other="Registers # files" />
          {built > 0 && (
            <>
              {', '}
              <Plural value={built} one="# of them a CBZ Maki builds" other="# of them CBZs Maki builds" />
            </>
          )}
        </Text>
        {plan.writesCover && (
          <Text size="xs" c={plan.replacesCover ? 'var(--warn)' : 'var(--ink-3)'}>
            {plan.replacesCover ? (
              <Trans>Writes the series cover over the cover.jpg already in the folder.</Trans>
            ) : (
              <Trans>Writes the series cover into the folder as cover.jpg.</Trans>
            )}
          </Text>
        )}
        <Stack gap={4}>
          {files.map((f) => {
            const { name, size } = f
            return (
              <div key={name}>
                <Group gap={6} wrap="nowrap" justify="space-between">
                  <Text size="xs" fw={600} lineClamp={1} title={name} style={{ overflowWrap: 'anywhere' }}>
                    {name}
                  </Text>
                  <Text size="xs" c="var(--ink-3)" className="tnum">
                    {formatBytes(size > 0 ? size : null)}
                  </Text>
                </Group>
                <Text size="xs" c="var(--ink-3)">
                  <FileAction file={f} />
                </Text>
                <Text size="xs" c={f.unlinked ? 'var(--warn)' : 'var(--ink-2)'}>
                  <ExpectedLink file={f} deferred={plan.linkDeferred} />
                </Text>
              </div>
            )
          })}
        </Stack>
        {skipped.length > 0 && (
          <Stack gap={2}>
            <Text size="xs" fw={600} c="var(--warn)">
              <Plural value={skipped.length} one="# file is left out" other="# files are left out" />
            </Text>
            {skipped.map((s) => {
              const { name } = s
              const reason = label(IMPORT_SKIP_REASONS[s.reason] ?? s.reason)
              return (
                <Text key={name} size="xs" c="var(--ink-3)" pl="sm">
                  <Trans>
                    {name}: {reason}
                  </Trans>
                </Text>
              )
            })}
          </Stack>
        )}
      </Stack>
    </Card>
  )
}

/**
 * A dry run of the import for one or more folders. Nothing is written: the server works out the
 * same folder decision and file plan the import would run and reports it.
 */
export function ImportPlanModal({
  rootFolderId,
  items,
  onClose,
}: {
  rootFolderId: number
  items: ImportRequestItem[]
  onClose: () => void
}) {
  const { t } = useLingui()
  const plan = useImportPlan(rootFolderId, items)
  const deferred = plan.data?.some((p) => !p.error && p.linkDeferred) ?? false

  return (
    <Modal opened onClose={onClose} title={t`Import preview`} size="xl">
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-2)">
          <Trans>Nothing has been changed. This is what importing would do.</Trans>
        </Text>
        {deferred && (
          <Alert color="var(--info)" icon={<IconInfoCircle size={16} />}>
            <Trans>
              Final linking happens after sources are found. Until then the chapters below are read
              off the file names, and a file whose chapter the sources do not list stays unlinked.
            </Trans>
          </Alert>
        )}
        {plan.isPending && (
          <Group justify="center" py="lg">
            <Loader size="sm" />
          </Group>
        )}
        {plan.isError && (
          <Alert color="var(--danger)" icon={<IconAlertTriangle size={16} />}>
            {errorText(plan.error)}
          </Alert>
        )}
        {plan.data?.map((p) => (
          <FolderPlan key={p.folderName} plan={p} />
        ))}
      </Stack>
    </Modal>
  )
}
