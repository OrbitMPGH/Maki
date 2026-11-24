import { useState } from 'react'
import { Button, Group, Stack, Switch, Text, TextInput } from '@mantine/core'
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
import { SettingsDrill, SettingsDrillPage } from '../../components/settings/SettingsDrill'
import { DrillBack, DrillChoices, DrillRow, useDrillPages } from '../../components/ui/DrillPanel'
import { FolderBrowserModal } from '../../components/settings/FolderBrowserModal'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'
import { formatDateTime } from '../../format'
import { IconClock, IconCopy, IconFolder, IconFolderSearch, IconRefresh } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { SettingsSection } from './SettingsSection'

type Page = 'main' | 'folder' | 'interval' | 'files'

const PARENTS: Record<Page, Page> = { main: 'main', folder: 'main', interval: 'main', files: 'main' }

/**
 * A folder Maki polls for comics. Whatever names exactly one library series is imported into it;
 * the rest waits in the Manual import tab. Laid out as a drill panel: the first page shows every
 * setting's current value, and each opens a page of its own.
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
  const placements = [
    {
      value: 'copy',
      label: t`Copy`,
      hint: t`Matched comics are hardlinked or copied, as the download settings say. The originals stay in the drop folder and are not imported again.`,
    },
    {
      value: 'move',
      label: t`Move`,
      hint: t`Matched comics move into the series folder. Archives that have to be converted are built there and their originals stay.`,
    },
  ]

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
        <SettingsDrill page={pages.page} depth={pages.depth}>
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
              <DrillRow
                ref={pages.rowRef('files')}
                icon={IconCopy}
                label={<Trans>Files</Trans>}
                value={placements.find((p) => p.value === current.placement)?.label}
                onClick={() => pages.open('files')}
              />
            </>
          )}
          {pages.page === 'folder' && (
            <SettingsDrillPage>
              <DrillBack ref={pages.backRef} title={<Trans>Folder</Trans>} onBack={pages.back} />
              <Group gap="sm" wrap="nowrap" align="flex-end">
                <TextInput
                  style={{ flex: 1 }}
                  label={t`Folder`}
                  description={t`A folder on the server. It must not hold a root folder.`}
                  value={current.path ?? ''}
                  onChange={(e) => patch({ path: e.currentTarget.value })}
                />
                <Button variant="default" leftSection={<IconFolderSearch size={16} />} onClick={() => setBrowsing(true)}>
                  <Trans>Browse</Trans>
                </Button>
              </Group>
            </SettingsDrillPage>
          )}
          {pages.page === 'interval' && (
            <SettingsDrillPage>
              <DrillBack ref={pages.backRef} title={<Trans>Check every</Trans>} onBack={pages.back} />
              <SettingsNumberInput
                label={t`Check every (minutes)`}
                description={t`A file is only touched once its size stayed the same across two checks.`}
                min={1}
                max={1440}
                value={minutes}
                onChange={(v) => patch({ intervalMinutes: v })}
              />
            </SettingsDrillPage>
          )}
          {pages.page === 'files' && (
            <SettingsDrillPage>
              <DrillBack ref={pages.backRef} title={<Trans>Files</Trans>} onBack={pages.back} />
              <div className="settings-drill-choices">
                <DrillChoices
                  label={t`Files`}
                  value={current.placement}
                  options={placements}
                  onPick={(v) => patch({ placement: v === 'move' ? 'move' : 'copy' })}
                />
              </div>
            </SettingsDrillPage>
          )}
        </SettingsDrill>
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
