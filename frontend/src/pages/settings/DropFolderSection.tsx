import { useState } from 'react'
import { Button, Group, SegmentedControl, Stack, Switch, Text, TextInput } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { Link } from 'react-router-dom'
import {
  useDropFolderSettings,
  useDropFolderStatus,
  useRunDropFolder,
  useSaveDropFolderSettings,
  type DropFolderSettings,
} from '../../api/dropFolder'
import { DrillPanes } from '../../components/ui/DrillPanes'
import { DrillBack, DrillField, DrillRow, useDrillPages } from '../../components/ui/DrillPanel'
import { FolderBrowserModal } from '../../components/settings/FolderBrowserModal'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'
import { formatDateTime } from '../../format'
import { IconClock, IconCopy, IconFolder, IconFolderSearch, IconRefresh } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { SettingsSection } from './SettingsSection'

type Page = 'main' | 'folder' | 'interval'

const PARENTS: Record<Page, Page> = { main: 'main', folder: 'main', interval: 'main' }

/**
 * A folder Maki polls for comics. Whatever names exactly one library series is imported into it;
 * the rest waits in the Manual import tab. Laid out as a drill panel: the first page shows every
 * setting's current value, the folder and the interval open a page of their own.
 */
export function DropFolderSection() {
  const { t } = useLingui()
  const { data: saved } = useDropFolderSettings()
  const { data: status } = useDropFolderStatus()
  const save = useSaveDropFolderSettings()
  const run = useRunDropFolder()
  const pages = useDrillPages<Page>('main', PARENTS)
  const [draft, setDraft] = useState<DropFolderSettings | null>(null)
  const [browsing, setBrowsing] = useState(false)

  const current: DropFolderSettings = draft ??
    saved ?? { enabled: false, path: null, intervalMinutes: 5, placement: 'copy' }
  const dirty = draft !== null && JSON.stringify(draft) !== JSON.stringify(saved)
  const patch = (p: Partial<DropFolderSettings>) => setDraft({ ...current, ...p })
  const minutes = current.intervalMinutes
  const pendingCount = status?.pending.length ?? 0

  return (
    <SettingsSection
      id="drop-folder"
      title={<Trans>Drop folder</Trans>}
      description={
        <Trans>
          Maki checks this folder for comics. Anything that names exactly one series in your library is imported into
          it; the rest waits in Manual import.
        </Trans>
      }
      dirty={dirty}
      saving={save.isPending}
      onSave={() =>
        save.mutate(current, {
          onSuccess: () => {
            setDraft(null)
            notifications.show({ message: now`Saved`, color: 'var(--ok)' })
          },
        })
      }
      onDiscard={() => setDraft(null)}
    >
      <Stack gap="sm">
        <Switch
          label={t`Import from a drop folder`}
          checked={current.enabled}
          onChange={(e) => patch({ enabled: e.currentTarget.checked })}
        />
        <div>
          <DrillPanes page={pages.page} depth={pages.depth} bleed={6}>
            {pages.page === 'main' && (
              <>
                <DrillRow
                  ref={pages.rowRef('folder')}
                  icon={IconFolder}
                  label={<Trans>Folder</Trans>}
                  value={current.path || t`Not set`}
                  onClick={() => pages.open('folder')}
                />
                <DrillRow
                  ref={pages.rowRef('interval')}
                  icon={IconClock}
                  label={<Trans>Check every</Trans>}
                  value={<Plural value={minutes} one="# minute" other="# minutes" />}
                  onClick={() => pages.open('interval')}
                />
                <DrillField
                  icon={IconCopy}
                  title={<Trans>Files</Trans>}
                  help={
                    current.placement === 'move' ? (
                      <Trans>
                        Matched comics move into the series folder. Archives that have to be converted are built there
                        and their originals stay.
                      </Trans>
                    ) : (
                      <Trans>
                        Matched comics are hardlinked or copied, as the download settings say. The originals stay in the
                        drop folder and are not imported again.
                      </Trans>
                    )
                  }
                >
                  <SegmentedControl
                    size="xs"
                    fullWidth
                    value={current.placement}
                    onChange={(v) => patch({ placement: v === 'move' ? 'move' : 'copy' })}
                    data={[
                      { value: 'copy', label: t`Copy` },
                      { value: 'move', label: t`Move` },
                    ]}
                  />
                </DrillField>
              </>
            )}
            {pages.page === 'folder' && (
              <>
                <DrillBack ref={pages.backRef} title={<Trans>Folder</Trans>} onBack={pages.back} />
                <DrillField
                  icon={IconFolder}
                  title={<Trans>Folder</Trans>}
                  help={<Trans>A folder on the server. It must not hold a root folder.</Trans>}
                >
                  <Group gap="xs" wrap="nowrap">
                    <TextInput
                      size="xs"
                      style={{ flex: 1 }}
                      aria-label={t`Folder`}
                      value={current.path ?? ''}
                      onChange={(e) => patch({ path: e.currentTarget.value })}
                    />
                    <Button
                      size="xs"
                      variant="default"
                      leftSection={<IconFolderSearch size={16} />}
                      onClick={() => setBrowsing(true)}
                    >
                      <Trans>Browse</Trans>
                    </Button>
                  </Group>
                </DrillField>
              </>
            )}
            {pages.page === 'interval' && (
              <>
                <DrillBack ref={pages.backRef} title={<Trans>Check every</Trans>} onBack={pages.back} />
                <DrillField
                  icon={IconClock}
                  title={<Trans>Check every (minutes)</Trans>}
                  help={<Trans>A file is only touched once its size stayed the same across two checks.</Trans>}
                >
                  <SettingsNumberInput
                    size="xs"
                    aria-label={t`Check every (minutes)`}
                    min={1}
                    max={1440}
                    value={minutes}
                    onChange={(v) => patch({ intervalMinutes: v })}
                  />
                </DrillField>
              </>
            )}
          </DrillPanes>
        </div>
        {saved?.enabled && (
          <Group justify="space-between" wrap="wrap" gap="sm">
            <Text size="sm" c={status?.lastError ? 'var(--danger)' : 'var(--ink-2)'}>
              {status?.lastError ? (
                <Trans>Maki couldn't read the drop folder: {status.lastError}</Trans>
              ) : status?.lastRunAt ? (
                <Trans>Last checked {formatDateTime(status.lastRunAt)}.</Trans>
              ) : (
                <Trans>Not checked yet.</Trans>
              )}{' '}
              {pendingCount > 0 && (
                <Link to="/import?mode=manual">
                  <Plural value={pendingCount} one="# item waits for review" other="# items wait for review" />
                </Link>
              )}
            </Text>
            <Button
              size="xs"
              variant="default"
              leftSection={<IconRefresh size={16} />}
              loading={run.isPending}
              disabled={dirty}
              onClick={() => run.mutate()}
            >
              <Trans>Check now</Trans>
            </Button>
          </Group>
        )}
      </Stack>
      <FolderBrowserModal
        opened={browsing}
        onClose={() => setBrowsing(false)}
        onSelect={(path) => patch({ path })}
        initialPath={current.path ?? undefined}
      />
    </SettingsSection>
  )
}
