import { useEffect, useRef, useState } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { Code, Group, MultiSelect, Select, Stack, Switch, Text, TextInput } from '@mantine/core'
import { notifications } from '@mantine/notifications'
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
  type ScrobbleSettings,
} from '../../api/hooks'
import { SettingsHelp } from '../../components/settings/SettingsHelp'
import { ImportListsSection } from '../../components/ImportListsSection'
import { TrackerSyncControls } from '../../components/TrackerSyncControls'
import { ConnectionSettingsCard } from '../../components/ConnectionSettingsCard'
import { savedToast } from './sharedRecord'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'

/** The record minus the Kavita library filter, which the Kavita card saves on its own. */
const withoutLibraries = (s: ScrobbleSettings) => JSON.stringify({ ...s, libraryIds: null })

/** The cards only show connection state; the live sync log polls faster on the Scrobble page. */
const SETTINGS_STATUS_POLL_MS = 30_000

export function ScrobbleSection() {
  const { t } = useLingui()
  const { data } = useScrobbleSettings()
  const { data: status } = useScrobbleStatus(SETTINGS_STATUS_POLL_MS)
  const save = useSaveScrobbleSettings()
  const [form, setForm] = useState<ScrobbleSettings | null>(null)

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

  return (
    <SettingsSection
      id="scrobbling"
      title={<Trans>Trackers</Trans>}
      description={
        <Trans>
          Pushes reading progress and ratings to AniList, MyAnimeList, MangaBaka and Kitsu. Connect
          and disconnect accounts, and review matches, on the Scrobble page.
        </Trans>
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
    >
      <Stack gap="xs">
        <Text size="sm" fw={600}>
          AniList
        </Text>
        {isAdmin && (
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
        )}
        <TrackerSyncControls service="anilist" label="AniList" connection={conn('anilist')} />

        <Text size="sm" fw={600} mt="xs">
          MyAnimeList
        </Text>
        {isAdmin && (
          <>
            <SettingsHelp>
              <Trans>
                Create an API client at myanimelist.net/apiconfig (App Type: web) with redirect URL{' '}
                <Code style={{ overflowWrap: 'anywhere' }}>{origin}/api/v1/scrobble/oauth/mal</Code>. If
                connecting ends in <Code>invalid_client</Code>, re-copy the Client ID (not the secret) and
                check the App Type is set.
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
        )}
        <TrackerSyncControls service="mal" label="MyAnimeList" connection={conn('mal')} />

        <Text size="sm" fw={600} mt="xs">
          MangaBaka
        </Text>
        <TextInput
          label={t`Personal Access Token`}
          description={t`From your MangaBaka settings. No OAuth needed.`}
          type="password"
          autoComplete="new-password"
          placeholder={form?.mangaBakaTokenSet ? t`Saved` : 'mb-...'}
          value={form?.mangaBakaToken ?? ''}
          onChange={(e) => set({ mangaBakaToken: e.currentTarget.value })}
        />
        <TrackerSyncControls service="mangabaka" label="MangaBaka" connection={conn('mangabaka')} />

        <Text size="sm" fw={600} mt="xs">
          Kitsu
        </Text>
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
        </Group>
        <TrackerSyncControls service="kitsu" label="Kitsu" connection={conn('kitsu')} />

        {isAdmin && (
          <SettingsNumberInput
            mt="xs"
            w={220}
            label={t`Sync interval (minutes)`}
            description={t`How often progress and ratings are pushed, from both Kavita and the built-in reader.`}
            min={5}
            max={1440}
            value={form?.intervalMinutes ?? 30}
            onChange={(value) => set({ intervalMinutes: value })}
          />
        )}
        <Switch
          label={t`Add unread series as plan-to-read`}
          description={t`Series in your library you haven't started are added to your lists as plan to read. Entries already on your lists are never changed.`}
          checked={form?.planToRead ?? false}
          onChange={(e) => {
            const checked = e.currentTarget.checked
            set({ planToRead: checked })
          }}
        />
      </Stack>
    </SettingsSection>
  )
}

/**
 * The Kavita library filter for scrobbling, shown inside the Kavita card. Part of the scrobble
 * settings record, saved on change over the latest server copy. Stored as a comma-separated id list.
 * Ids Kavita no longer reports stay selectable, so a library that is briefly missing isn't dropped
 * from the filter by the next save.
 */
function KavitaLibrariesField() {
  const { t } = useLingui()
  const { data } = useScrobbleSettings()
  const save = useSaveScrobbleSettings()
  const { data: kavitaConnection, isSuccess: kavitaConnectionLoaded } = useConnectionSettings<{
    url: string | null
    apiKey: string | null
  }>('kavita')
  const kavitaNotSetUp = kavitaConnectionLoaded && !(kavitaConnection?.url && kavitaConnection?.apiKey)
  const { data: kavitaLibraries, error: kavitaLibrariesError } = useKavitaLibraries(
    kavitaConnectionLoaded && !kavitaNotSetUp,
  )
  const libraryIds = save.isPending && save.variables ? save.variables.libraryIds : (data?.libraryIds ?? null)
  const selectedLibraries = (libraryIds ?? '').split(',').map((id) => id.trim()).filter(Boolean)
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
      description={
        kavitaNotSetUp
          ? t`Set up the Kavita connection above to pick libraries.`
          : t`Leave empty to scrobble reading from every Kavita library. Reading in Maki's own reader is always scrobbled.`
      }
      placeholder={selectedLibraries.length === 0 ? t`All libraries` : undefined}
      data={libraryOptions}
      value={selectedLibraries}
      disabled={!data}
      onChange={(ids) =>
        save.mutate({ libraryIds: ids.length > 0 ? ids.join(',') : null }, { onSuccess: savedToast })
      }
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
  return (
    <ConnectionSettingsCard
      name="kavita"
      title="Kavita"
      description={t`Maki asks Kavita to scan a series after its files change, then pushes its poster, links and status. Covers you set in Kavita are kept. The API key is under User Settings, 3rd Party Clients in Kavita.`}
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
      <KavitaOwnerField />
      <KavitaLibrariesField />
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
        <Trans>
          Pulls your tracker lists on a schedule and adds matching series to the library, or files
          requests when you can't add series yourself. Connect trackers on the Scrobble page first.
          Needs the local MangaBaka database.
        </Trans>
      }
      dirty={form?.dirty}
      saving={form?.saving}
      onDiscard={form?.reset}
      onSave={form?.save}
    >
      {form && <ImportListInstanceControls form={form} />}
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
    <Stack gap="xs" mb="lg">
      <Switch
        label={t`Enable import lists for everyone`}
        checked={form?.enabled ?? true}
        disabled={data === undefined}
        onChange={(e) => set({ enabled: e.currentTarget.checked })}
      />
      <SettingsNumberInput
        label={t`Sync interval (minutes)`}
        description={t`How often every user's lists are checked.`}
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
function KavitaOwnerField() {
  const { t } = useLingui()
  const { data: bound } = useKavitaUser()
  const { data: users } = useUsers()
  const save = useSetKavitaUser()

  const options = (users ?? [])
    .filter((u) => !u.disabled && !u.pendingSetup)
    .map((u) => ({ value: String(u.id), label: u.displayName || u.userName }))

  return (
    <Select
      mt="md"
      maw={420}
      label={t`Attribute Kavita's reading to`}
      description={t`Unset means the lowest-numbered admin, which suits a single-user instance. Only this account can import from Kavita or push reads back.`}
      placeholder={t`Lowest-numbered admin`}
      clearable
      data={options}
      value={bound?.userId != null ? String(bound.userId) : null}
      onChange={(value) =>
        save.mutate(value === null ? null : Number(value), {
          onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }),
        })
      }
    />
  )
}

