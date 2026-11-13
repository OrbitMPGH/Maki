import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { Code, Group, MultiSelect, Select, Stack, Switch, Text, TextInput } from '@mantine/core'
import { notifications } from '../../lib/toast'
import { SettingsSection } from './SettingsSection'
import { useAuth } from '../../auth/AuthProvider'
import { useKavitaUser, useSetKavitaUser, useUsers } from '../../api/auth'
import {
  useConnectionSettings,
  useSaveScrobbleSettings,
  useImportListSettings,
  useSaveImportListSettings,
  type ImportListSettings,
  useKavitaLibraries,
  useScrobbleSettings,
  useScrobbleStatus,
  type ScrobbleConnection,
  type ScrobbleSettings,
} from '../../api/hooks'
import { MetaList } from '../../components/ui/MetaList'
import { relativeTime } from '../../components/ui/time'
import {
  SettingsRow,
  SettingsRows,
  SettingsRowsBlock,
  type RowStatus,
} from '../../components/settings/SettingsRow'
import { SettingsHelp } from '../../components/settings/SettingsHelp'
import { ImportListsSection } from '../../components/ImportListsSection'
import { TrackerSyncControls } from '../../components/TrackerSyncControls'
import { ConnectionSettingsCard, type SaveGroup } from '../../components/ConnectionSettingsCard'
import { savedToast } from './sharedRecord'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'

/** The record minus the Kavita library filter, which the Kavita card saves on its own. */
const withoutLibraries = (s: ScrobbleSettings) => JSON.stringify({ ...s, libraryIds: null })

/** The cards only show connection state; the live sync log polls faster on the Scrobble page. */
const SETTINGS_STATUS_POLL_MS = 30_000

export function ScrobbleSection() {
  const { t } = useLingui()
  const { trackerStatus, trackerSummary } = useTrackerText()
  const { data } = useScrobbleSettings()
  const { data: status } = useScrobbleStatus(SETTINGS_STATUS_POLL_MS)
  const save = useSaveScrobbleSettings()
  const [form, setForm] = useState<ScrobbleSettings | null>(null)
  const [open, setOpen] = useState<string | null>(null)

  useEffect(() => {
    if (data && form === null) setForm(data)
  }, [data, form])

  // The app registrations and interval belong to the instance. The server returns them as null to
  // anyone else and drops them on save, so a non-admin never sees the inputs.
  const isAdmin = data?.isAdmin ?? false

  const conn = (service: string) => status?.connections.find((c) => c.service === service)

  const set = (patch: Partial<ScrobbleSettings>) =>
    setForm((f) => (f ? { ...f, ...patch } : f))
  const dirty = form !== null && data !== undefined && withoutLibraries(form) !== withoutLibraries(data)

  const origin = window.location.origin

  const row = (service: string, label: string, needsApp: boolean, fields: ReactNode) => {
    const connection = conn(service)
    return (
      <SettingsRow
        key={service}
        title={label}
        status={trackerStatus(connection, needsApp && isAdmin)}
        summary={trackerSummary(connection, needsApp && isAdmin)}
        opened={open === service}
        onToggle={() => setOpen((current) => (current === service ? null : service))}
      >
        {fields}
        <TrackerSyncControls service={service} label={label} connection={connection} />
      </SettingsRow>
    )
  }

  return (
    <SettingsSection
      id="scrobbling"
      title={<Trans>Trackers</Trans>}
      description={
        <Trans>Connect accounts on the Scrobble page.</Trans>
      }
      dirty={dirty}
      saving={save.isPending}
      onDiscard={() => setForm(data ?? null)}
      onSave={() => {
        if (!form) return
        const { libraryIds: _, ...rest } = form
        save.mutate(rest, {
          onSuccess: (saved) => {
            setForm(saved)
            savedToast()
          },
        })
      }}
      panelProps={{ p: 0 }}
    >
      <SettingsRows>
        {row(
          'anilist',
          'AniList',
          true,
          isAdmin && (
            <>
              <Text size="xs" c="var(--ink-3)">
                <Trans>
                  Create an API client at anilist.co/settings/developer with redirect URL{' '}
                  <Code style={{ overflowWrap: 'anywhere' }}>{origin}/api/v1/scrobble/oauth/anilist</Code>
                </Trans>
              </Text>
              <Group grow>
                <TextInput
                  label={t`Client ID`}
                  autoComplete="off"
                  value={form?.aniListClientId ?? ''}
                  onChange={(e) => set({ aniListClientId: e.currentTarget.value })}
                />
                <TextInput
                  label={t`Client secret`}
                  type="password"
                  autoComplete="new-password"
                  value={form?.aniListClientSecret ?? ''}
                  onChange={(e) => set({ aniListClientSecret: e.currentTarget.value })}
                />
              </Group>
            </>
          ),
        )}
        {row(
          'mal',
          'MyAnimeList',
          true,
          isAdmin && (
            <>
              <SettingsHelp>
                <Trans>
                  Create an API client at myanimelist.net/apiconfig (App Type: web) with redirect URL{' '}
                  <Code style={{ overflowWrap: 'anywhere' }}>{origin}/api/v1/scrobble/oauth/mal</Code>
                </Trans>
              </SettingsHelp>
              <Group grow>
                <TextInput
                  label={t`Client ID`}
                  autoComplete="off"
                  value={form?.malClientId ?? ''}
                  onChange={(e) => set({ malClientId: e.currentTarget.value })}
                />
                <TextInput
                  label={t`Client secret`}
                  type="password"
                  autoComplete="new-password"
                  value={form?.malClientSecret ?? ''}
                  onChange={(e) => set({ malClientSecret: e.currentTarget.value })}
                />
              </Group>
            </>
          ),
        )}
        {row(
          'mangabaka',
          'MangaBaka',
          false,
          <TextInput
            label={t`Personal Access Token`}
            description={t`From your MangaBaka settings. No OAuth needed.`}
            type="password"
            autoComplete="new-password"
            placeholder={form?.mangaBakaTokenSet ? t`Saved` : 'mb-...'}
            value={form?.mangaBakaToken ?? ''}
            onChange={(e) => set({ mangaBakaToken: e.currentTarget.value })}
          />,
        )}
        {row(
          'kitsu',
          'Kitsu',
          false,
          <Group grow>
            <TextInput
              label={t`Email`}
              autoComplete="off"
              value={form?.kitsuEmail ?? ''}
              onChange={(e) => set({ kitsuEmail: e.currentTarget.value })}
            />
            <TextInput
              label={t`Password`}
              type="password"
              autoComplete="new-password"
              placeholder={form?.kitsuPasswordSet ? t`Saved` : undefined}
              value={form?.kitsuPassword ?? ''}
              onChange={(e) => set({ kitsuPassword: e.currentTarget.value })}
            />
          </Group>,
        )}
        {row(
          'mangaupdates',
          'MangaUpdates',
          false,
          <Group grow>
            <TextInput
              label={t`Username`}
              autoComplete="off"
              value={form?.mangaUpdatesUsername ?? ''}
              onChange={(e) => set({ mangaUpdatesUsername: e.currentTarget.value })}
            />
            <TextInput
              label={t`Password`}
              type="password"
              autoComplete="new-password"
              placeholder={form?.mangaUpdatesPasswordSet ? t`Saved` : undefined}
              value={form?.mangaUpdatesPassword ?? ''}
              onChange={(e) => set({ mangaUpdatesPassword: e.currentTarget.value })}
            />
          </Group>,
        )}
      </SettingsRows>

      <SettingsRowsBlock>
        <Stack gap="sm">
          {isAdmin && (
            <SettingsNumberInput
              label={t`Sync interval (minutes)`}
              min={5}
              max={1440}
              value={form?.intervalMinutes ?? 30}
              onChange={(value) => set({ intervalMinutes: value })}
            />
          )}
          <Switch
            label={t`Add unread series as plan-to-read`}
            description={t`Entries already on your lists are never changed.`}
            checked={form?.planToRead ?? false}
            onChange={(e) => {
              const checked = e.currentTarget.checked
              set({ planToRead: checked })
            }}
          />
        </Stack>
      </SettingsRowsBlock>
    </SettingsSection>
  )
}

const backingOff = (connection: ScrobbleConnection) =>
  connection.backOffUntil != null && new Date(connection.backOffUntil).getTime() > Date.now()

function useTrackerText() {
  const { t } = useLingui()

  /**
   * One word for where a tracker stands. `needsApp` is set for an admin looking at a tracker that
   * signs in through an API client registered on the instance (AniList, MyAnimeList).
   */
  const trackerStatus = (connection: ScrobbleConnection | undefined, needsApp: boolean): RowStatus | null => {
    if (!connection) return null
    if (backingOff(connection)) return { tone: 'warn', label: t`Paused` }
    if (connection.connected) return { tone: 'ok', label: t`Connected` }
    if (!connection.configured) {
      return needsApp ? { tone: 'warn', label: t`Needs a client ID` } : { tone: 'neutral', label: t`Not set up` }
    }
    if (connection.savedDetails === false) return { tone: 'neutral', label: t`Not set up` }
    if (connection.savedDetails === true) return { tone: 'danger', label: t`Can't sign in` }
    return { tone: 'neutral', label: t`Not connected` }
  }

  const trackerSummary = (connection: ScrobbleConnection | undefined, needsApp: boolean): ReactNode => {
    if (!connection) return null
    if (backingOff(connection)) {
      const when = relativeTime(connection.backOffUntil!)
      return t`The site is refusing calls. Maki tries again ${when}.`
    }
    if (connection.connected) {
      const syncing = [
        connection.syncReading && t`Scrobbles reading`,
        connection.syncRatings && t`Syncs ratings`,
        connection.animeList && connection.animeSignals && t`Anime steers recommendations`,
      ]
      const name = connection.username
      return (
        <MetaList
          inline
          items={[name && t`Signed in as ${name}`, ...(syncing.some(Boolean) ? syncing : [t`Nothing syncs`])]}
        />
      )
    }
    if (!connection.configured) {
      return needsApp ? t`Nothing syncs until the API client is set up.` : t`An admin has to set this tracker up first.`
    }
    if (connection.savedDetails === false) return t`No sign-in details saved.`
    if (connection.savedDetails === true) return t`The saved details were refused. Check them and save again.`
    return t`Connect your account on the Scrobble page.`
  }

  return { trackerStatus, trackerSummary }
}

const splitLibraryIds = (ids: string | null | undefined) =>
  (ids ?? '').split(',').map((id) => id.trim()).filter(Boolean)

/**
 * The Kavita library filter for scrobbling, shown inside the Kavita card. Part of the scrobble
 * settings record, saved with the card's Save over the latest server copy. Stored as a comma-separated id list.
 * Ids Kavita no longer reports stay selectable, so a library that is briefly missing isn't dropped
 * from the filter by the next save.
 */
function KavitaLibrariesField({
  value,
  onChange,
}: {
  value: string[] | null
  onChange: (ids: string[]) => void
}) {
  const { t } = useLingui()
  const { data } = useScrobbleSettings()
  const { data: kavitaConnection, isSuccess: kavitaConnectionLoaded } = useConnectionSettings<{
    url: string | null
    apiKey: string | null
  }>('kavita')
  const kavitaNotSetUp = kavitaConnectionLoaded && !(kavitaConnection?.url && kavitaConnection?.apiKey)
  const { data: kavitaLibraries, error: kavitaLibrariesError } = useKavitaLibraries(
    kavitaConnectionLoaded && !kavitaNotSetUp,
  )
  const selectedLibraries = value ?? splitLibraryIds(data?.libraryIds)
  const libraryOptions = [
    ...(kavitaLibraries ?? []).map((l) => ({ value: String(l.id), label: l.name ?? `#${l.id}` })),
    ...selectedLibraries
      .filter((id) => !(kavitaLibraries ?? []).some((l) => String(l.id) === id))
      .map((id) => ({ value: id, label: `#${id}` })),
  ]
  const kavitaLibrariesErrorMessage = kavitaLibrariesError?.message ?? null

  return (
    <MultiSelect
      mt="md"
      maw={420}
      label={t`Scrobble reading from these libraries`}
      description={kavitaNotSetUp ? t`Set up the Kavita connection above to pick libraries.` : undefined}
      placeholder={selectedLibraries.length === 0 ? t`All libraries` : undefined}
      data={libraryOptions}
      value={selectedLibraries}
      disabled={!data}
      onChange={onChange}
      error={
        kavitaLibrariesErrorMessage != null
          ? t`Could not load libraries from Kavita: ${kavitaLibrariesErrorMessage}`
          : undefined
      }
      clearable
    />
  )
}

export function KavitaCard() {
  const { t } = useLingui()
  const { data: scrobble } = useScrobbleSettings()
  const saveScrobble = useSaveScrobbleSettings()
  const { data: bound } = useKavitaUser()
  const saveOwner = useSetKavitaUser()
  const [owner, setOwner] = useState<string | null | undefined>(undefined)
  const [libraries, setLibraries] = useState<string[] | undefined>(undefined)

  const savedLibraries = splitLibraryIds(scrobble?.libraryIds)
  const ownerDirty = owner !== undefined && owner !== (bound?.userId != null ? String(bound.userId) : null)
  const librariesDirty = libraries !== undefined && libraries.join(',') !== savedLibraries.join(',')

  const extra: SaveGroup = {
    dirty: ownerDirty || librariesDirty,
    saving: saveOwner.isPending || saveScrobble.isPending,
    commit: async () => {
      if (ownerDirty) await saveOwner.mutateAsync(owner === null ? null : Number(owner))
      if (librariesDirty) {
        const ids = libraries ?? []
        await saveScrobble.mutateAsync({ libraryIds: ids.length > 0 ? ids.join(',') : null })
      }
      setOwner(undefined)
      setLibraries(undefined)
    },
    reset: () => {
      setOwner(undefined)
      setLibraries(undefined)
    },
  }

  return (
    <ConnectionSettingsCard
      name="kavita"
      extra={extra}
      title="Kavita"
      description={t`The API key is under User Settings, 3rd Party Clients in Kavita.`}
      fields={[
        { key: 'url', label: t`URL`, placeholder: 'http://localhost:5000' },
        { key: 'apiKey', label: t`API key`, secret: true },
        {
          key: 'pathMapFrom',
          label: t`Path as Maki sees it`,
          description: t`Only needed when Kavita sees the library under a different path, e.g. in Docker.`,
          placeholder: t`C:\\Manga (optional)`,
        },
        { key: 'pathMapTo', label: t`Path as Kavita sees it`, placeholder: t`/manga (optional)` },
      ]}
    >
      <KavitaOwnerField value={owner} onChange={setOwner} />
      <KavitaLibrariesField value={libraries ?? null} onChange={setLibraries} />
    </ConnectionSettingsCard>
  )
}

export function ImportListSettingsSection() {
  const { can } = useAuth()
  return can('Admin') ? <ImportListSettingsAdmin /> : <ImportListSettingsCard />
}

function ImportListSettingsAdmin() {
  const form = useImportListInstanceForm()
  return <ImportListSettingsCard form={form} />
}

function ImportListSettingsCard({ form }: { form?: ReturnType<typeof useImportListInstanceForm> }) {
  return (
    <SettingsSection
      id="import-lists"
      title={<Trans>Import lists</Trans>}
      description={
        <Trans>Adds series from your tracker lists. Connect trackers on the Scrobble page first.</Trans>
      }
      dirty={form?.dirty}
      saving={form?.saving}
      onDiscard={form?.reset}
      onSave={form?.save}
      panelProps={{ p: 0 }}
    >
      {form && (
        <SettingsRowsBlock>
          <ImportListInstanceControls form={form} />
        </SettingsRowsBlock>
      )}
      <ImportListsSection />
    </SettingsSection>
  )
}

function useImportListInstanceForm() {
  const { data } = useImportListSettings()
  const save = useSaveImportListSettings()
  const [form, setForm] = useState<ImportListSettings | null>(null)
  // Tracks whether the user has touched the form since the last seed/save, so a background
  // refetch can rebase onto newer server values without clobbering an in-progress edit.
  const editedRef = useRef(false)

  useEffect(() => {
    if (data && !editedRef.current) setForm(data)
  }, [data])

  const set = (patch: Partial<ImportListSettings>) => {
    editedRef.current = true
    setForm((f) => (f ? { ...f, ...patch } : f))
  }
  const dirty = form !== null && data !== undefined && JSON.stringify(form) !== JSON.stringify(data)

  return {
    data,
    form,
    set,
    dirty,
    saving: save.isPending,
    reset: () => {
      editedRef.current = false
      if (data) setForm(data)
    },
    save: () =>
      form &&
      save.mutate(form, {
        onSuccess: () => {
          editedRef.current = false
          notifications.show({ message: now`Saved`, color: 'var(--ok)' })
        },
      }),
  }
}

function ImportListInstanceControls({ form: { data, form, set } }: { form: ReturnType<typeof useImportListInstanceForm> }) {
  const { t } = useLingui()

  return (
    <Stack gap="xs">
      <Switch
        label={t`Enable import lists for everyone`}
        checked={form?.enabled ?? true}
        disabled={data === undefined}
        onChange={(e) => set({ enabled: e.currentTarget.checked })}
      />
      <SettingsNumberInput
        label={t`Sync interval (minutes)`}
        min={15}
        max={1440}
        value={form?.intervalMinutes ?? 15}
        disabled={data === undefined}
        onChange={(value) => set({ intervalMinutes: value })}
      />
    </Stack>
  )
}


/**
 * Which Maki account Kavita's reading belongs to, shown inside the Kavita card. Instance-wide on purpose: Kavita is one server
 * reached with one API key, so everything it reports is a single person's reading and there is no way
 * to tell two Kavita users apart from here. Naming the owner is what keeps the adopt/merge/zero-delta
 * chain intact: the recurring pass, the read-status import, the per-chapter sync and the push-back
 * all act as the same user, so a chapter read in Maki and re-reported by Kavita counts once.
 */
function KavitaOwnerField({
  value,
  onChange,
}: {
  value: string | null | undefined
  onChange: (userId: string | null) => void
}) {
  const { t } = useLingui()
  const { me, singleUser } = useAuth()
  const { data: bound } = useKavitaUser()
  const { data: users } = useUsers()

  // With one account there is nobody else to attribute the reading to. Still shown when it points
  // at some other, now disabled, account, since that is a setting worth being able to clear.
  if (singleUser && (bound?.userId == null || bound.userId === me?.id)) return null

  const options = (users ?? [])
    .filter((u) => !u.disabled && !u.pendingSetup)
    .map((u) => ({ value: String(u.id), label: u.displayName || u.userName }))

  return (
    <Select
      mt="md"
      maw={420}
      label={t`Attribute Kavita's reading to`}
      description={t`Only this account can import from Kavita or push reads back.`}
      placeholder={t`Lowest-numbered admin`}
      clearable
      data={options}
      value={value !== undefined ? value : bound?.userId != null ? String(bound.userId) : null}
      onChange={onChange}
    />
  )
}

