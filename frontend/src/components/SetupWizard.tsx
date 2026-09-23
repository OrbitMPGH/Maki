import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import {
  ActionIcon,
  Anchor,
  Button,
  Collapse,
  Group,
  Modal,
  Radio,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import {
  IconAlertTriangle,
  IconArrowLeft,
  IconArrowRight,
  IconCheck,
  IconChevronDown,
  IconFolder,
  IconFolderPlus,
  IconTrash,
} from '@tabler/icons-react'
import { useNavigate } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import {
  useAddRootFolder,
  useCompleteSetup,
  useConnectionSettings,
  useDeleteRootFolder,
  useDiscoverSettings,
  useDumpProgress,
  useLibrarySettings,
  useMetadataSettings,
  useMonitoringSettings,
  useRecommendationIndex,
  useRootFolders,
  useSaveDiscoverSettings,
  useSaveLibrarySettings,
  useSaveMetadataSettings,
  useSaveMonitoringSettings,
  useSaveSourceLanguages,
  useSetEmbeddingModel,
  useSetupStatus,
  useSourceLanguages,
  useUiSettings,
  type ConnectionName,
  type FolderNamingMode,
  type LibrarySettings,
} from '../api/hooks'
import { languageName } from '../api/titles'
import { formatBytes } from '../format'
import { useLabel, useLanguageChoice } from '../i18n-context'
import { useThemeChoice } from '../theme-context'
import { ConnectionForm, type ConnectionField } from './ConnectionSettingsCard'
import { ContentRatingCards } from './ContentRatingCards'
import { IconBrandMark } from './IconBrandMark'
import { DumpProgressBar } from './MetadataDumpProgress'
import { PriorityList } from './PriorityList'
import { RecommendationModelSwitch } from './RecommendationModelSwitch'
import { UnsavedSettingsContext } from './settings/SaveButton'
import { useApplyLanguage, useLanguageOptions } from './ui/language'

const STEP_IDS = ['welcome', 'library', 'discovery', 'downloads', 'connections', 'finish'] as const
type StepId = (typeof STEP_IDS)[number]

type Tone = 'ok' | 'warn' | undefined

/** One setting: what it is on the left, the control on the right, anything wide underneath. */
function SettingRow({
  label,
  description,
  control,
  children,
}: {
  label: ReactNode
  description?: ReactNode
  control?: ReactNode
  children?: ReactNode
}) {
  return (
    <section className="setup-row">
      <div className="setup-row-head">
        <div className="setup-row-text">
          <Text className="setup-row-label">{label}</Text>
          {description && <Text className="setup-row-description">{description}</Text>}
        </div>
        {control && <div className="setup-row-control">{control}</div>}
      </div>
      {children && <div className="setup-row-body">{children}</div>}
    </section>
  )
}

function WelcomeStep() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data: ui } = useUiSettings()
  const languageOptions = useLanguageOptions()
  const applyLanguage = useApplyLanguage()
  const { themeId, setThemeId, presets } = useThemeChoice()
  // Applying a language clears the query cache, so the stored value is briefly unknown. Hold the
  // pick locally so the select doesn't fall back to Automatic while it reloads.
  const [languageChoice, setLanguageChoice] = useState<string | null>(null)

  return (
    <>
      <SettingRow
        label={<Trans>Language</Trans>}
        description={<Trans>The language of Maki's interface, on every device you sign in on.</Trans>}
        control={
          <Select
            aria-label={t`Language`}
            data={languageOptions}
            value={languageChoice ?? ui?.language ?? ''}
            onChange={(value) => {
              if (value === null) return
              setLanguageChoice(value)
              applyLanguage?.(value)
            }}
            disabled={!applyLanguage}
            allowDeselect={false}
            w={280}
          />
        }
      />
      <SettingRow
        label={<Trans>Appearance</Trans>}
        description={<Trans>An accent colour, the light theme, or whatever your system uses. Remembered on this device.</Trans>}
      >
        <div className="setup-swatches" role="radiogroup" aria-label={t`Appearance`}>
          {presets.map((p) => {
            const active = p.id === themeId
            return (
              <UnstyledButton
                key={p.id}
                role="radio"
                aria-checked={active}
                className="setup-swatch"
                data-active={active || undefined}
                onClick={() => setThemeId(p.id)}
              >
                <span className="setup-swatch-dot" style={{ background: p.swatch }} />
                <span>{renderLabel(p.label)}</span>
              </UnstyledButton>
            )
          })}
        </div>
      </SettingRow>
    </>
  )
}

function useLibraryPatch() {
  const { data: settings } = useLibrarySettings()
  const save = useSaveLibrarySettings()
  // writeCoverToFolder is left out unless it is the thing changing: an omitted field keeps the
  // stored value, and sending a default here once switched people's cover.jpg off.
  const patch = (changes: Partial<LibrarySettings>) =>
    save.mutate({
      writeComicInfo: settings?.writeComicInfo ?? true,
      folderNamingMode: settings?.folderNamingMode ?? 'rename',
      ...changes,
    })
  return { settings, patch }
}

function LibraryStep() {
  const { t } = useLingui()
  const [newPath, setNewPath] = useState('')
  const { data: rootFolders } = useRootFolders()
  const addFolder = useAddRootFolder()
  const deleteFolder = useDeleteRootFolder()
  const { settings, patch } = useLibraryPatch()

  const add = () => {
    const path = newPath.trim()
    if (!path) return
    addFolder.mutate(path, { onSuccess: () => setNewPath('') })
  }

  return (
    <>
      <SettingRow
        label={<Trans>Library folders</Trans>}
        description={
          <Trans>
            Where your manga is stored, or should be. Maki creates one folder per series inside it.
            If you use Kavita, point it at the same place.
          </Trans>
        }
      >
        <Stack gap="xs">
          {rootFolders?.map((f) => {
            const free = f.freeSpace != null ? formatBytes(f.freeSpace) : null
            return (
              <div key={f.id} className="setup-folder" data-broken={!f.accessible || undefined}>
                <IconFolder size={18} className="setup-folder-icon" />
                <div className="setup-folder-text">
                  <Text className="setup-folder-path">{f.path}</Text>
                  <Text className="setup-folder-meta">
                    {!f.accessible ? (
                      <Trans>Maki can't reach this folder</Trans>
                    ) : free ? (
                      <Trans>{free} free</Trans>
                    ) : null}
                  </Text>
                </div>
                <ActionIcon
                  variant="subtle"
                  color="var(--danger)"
                  onClick={() => deleteFolder.mutate(f.id)}
                  aria-label={t`Delete root folder`}
                >
                  <IconTrash size={16} />
                </ActionIcon>
              </div>
            )
          })}
          <Group gap="xs" wrap="nowrap">
            <TextInput
              aria-label={t`Folder path`}
              placeholder={t`C:\\Manga or /library`}
              leftSection={<IconFolderPlus size={16} />}
              value={newPath}
              onChange={(e) => setNewPath(e.currentTarget.value)}
              onKeyDown={(e) => e.key === 'Enter' && add()}
              style={{ flex: 1 }}
            />
            <Button onClick={add} loading={addFolder.isPending} disabled={!newPath.trim()}>
              <Trans>Add folder</Trans>
            </Button>
          </Group>
        </Stack>
      </SettingRow>

      <SettingRow
        label={<Trans>Existing folders on import</Trans>}
        description={<Trans>What happens to a series folder you already have when you import it into Maki.</Trans>}
      >
        <Radio.Group
          value={settings?.folderNamingMode ?? 'rename'}
          onChange={(value) => patch({ folderNamingMode: value as FolderNamingMode })}
        >
          <Stack gap="xs">
            <Radio.Card value="rename" className="setup-choice">
              <Group wrap="nowrap" align="flex-start" gap="sm">
                <Radio.Indicator />
                <div>
                  <Text className="setup-choice-title">
                    <Trans>Rename to the Maki standard</Trans>
                  </Text>
                  <Text className="setup-choice-description">
                    <Trans>Every series folder ends up named the same way.</Trans>
                  </Text>
                </div>
              </Group>
            </Radio.Card>
            <Radio.Card value="keep-new-standard" className="setup-choice">
              <Group wrap="nowrap" align="flex-start" gap="sm">
                <Radio.Indicator />
                <div>
                  <Text className="setup-choice-title">
                    <Trans>Keep the name, download into a new standard folder</Trans>
                  </Text>
                  <Text className="setup-choice-description">
                    <Trans>Your folder is left alone. New chapters go into a folder Maki names.</Trans>
                  </Text>
                </div>
              </Group>
            </Radio.Card>
            <Radio.Card value="keep-original" className="setup-choice">
              <Group wrap="nowrap" align="flex-start" gap="sm">
                <Radio.Indicator />
                <div>
                  <Text className="setup-choice-title">
                    <Trans>Keep the name, download into it too</Trans>
                  </Text>
                  <Text className="setup-choice-description">
                    <Trans>Nothing is renamed and everything stays in the folder you had.</Trans>
                  </Text>
                </div>
              </Group>
            </Radio.Card>
          </Stack>
        </Radio.Group>
      </SettingRow>

      <SettingRow
        label={<Trans>Write ComicInfo.xml into imported files</Trans>}
        description={
          <Trans>
            Helps Kavita and other readers group and name chapters. Off leaves torrent grabs and
            manual imports untouched. Maki's own downloads always get one.
          </Trans>
        }
        control={
          <Switch
            aria-label={t`Write ComicInfo.xml into imported files`}
            checked={settings?.writeComicInfo ?? true}
            onChange={(e) => patch({ writeComicInfo: e.currentTarget.checked })}
          />
        }
      />
      <SettingRow
        label={<Trans>Save a cover.jpg into each series folder</Trans>}
        description={<Trans>For readers like Komga and Kavita that pick up a poster from the folder.</Trans>}
        control={
          <Switch
            aria-label={t`Save a cover.jpg into each series folder`}
            checked={settings?.writeCoverToFolder ?? false}
            onChange={(e) => patch({ writeCoverToFolder: e.currentTarget.checked })}
          />
        }
      />
    </>
  )
}

function DiscoveryStep() {
  const { t } = useLingui()
  const { data: metadata } = useMetadataSettings()
  const saveMetadata = useSaveMetadataSettings()
  const { data: progress } = useDumpProgress()
  const { data: recIndex } = useRecommendationIndex()
  const setModel = useSetEmbeddingModel()
  const { data: discover } = useDiscoverSettings()
  const saveDiscover = useSaveDiscoverSettings()
  const downloading = Boolean(progress?.running && progress.phase !== 'checking')

  return (
    <>
      <SettingRow
        label={<Trans>Keep a local copy of MangaBaka</Trans>}
        description={
          <Trans>
            About 3 GB on disk, refreshed nightly. Search and imports become instant instead of
            rate-limited, and Discover needs it. Downloads in the background once setup is done.
          </Trans>
        }
        control={
          <Switch
            aria-label={t`Keep a local copy of MangaBaka`}
            checked={metadata?.useLocalDb ?? true}
            onChange={(e) => saveMetadata.mutate(e.currentTarget.checked)}
          />
        }
      >
        {downloading && progress && <DumpProgressBar progress={progress} />}
      </SettingRow>

      <SettingRow
        label={<Trans>Recommendations and search by description</Trans>}
        description={
          <Trans>
            A small local model lets Discover recommend by feel and lets you search for a plot
            rather than a title. Its index downloads prebuilt, so your machine skips the heavy work.
          </Trans>
        }
      >
        <RecommendationModelSwitch
          status={recIndex}
          busy={setModel.isPending}
          onSelect={(kind) => setModel.mutate(kind)}
        />
      </SettingRow>

      <SettingRow
        label={<Trans>Content rating</Trans>}
        description={<Trans>The most explicit rating shown in search, Discover and recommendations. Everything up to it is allowed.</Trans>}
      >
        <ContentRatingCards
          value={discover?.maxContentRating ?? 'erotica'}
          onChange={(rating) => saveDiscover.mutate(rating)}
        />
      </SettingRow>
    </>
  )
}

function DownloadsStep() {
  const { t } = useLingui()
  const { data: languages } = useSourceLanguages()
  const saveLanguages = useSaveSourceLanguages()
  const { data: monitoring } = useMonitoringSettings()
  const saveMonitoring = useSaveMonitoringSettings()
  const [order, setOrder] = useState<string[] | null>(null)
  const [disabled, setDisabled] = useState<string[] | null>(null)
  const [noneEnabled, setNoneEnabled] = useState(false)

  useEffect(() => {
    if (languages && order === null) {
      setOrder(languages.order)
      setDisabled(languages.disabled)
    }
  }, [languages, order])

  return (
    <>
      <SettingRow
        label={<Trans>Languages to download</Trans>}
        description={
          <Trans>
            Most preferred first. Maki matches new series to sources that publish your top language
            and skips sources that publish none of these. Drag to reorder.
          </Trans>
        }
      >
        {order && disabled && (
          <PriorityList
            items={order}
            disabled={disabled}
            onChange={(nextOrder, nextDisabled) => {
              const allOff = nextOrder.every((c) => nextDisabled.includes(c))
              setNoneEnabled(allOff)
              if (allOff) return
              setOrder(nextOrder)
              setDisabled(nextDisabled)
              saveLanguages.mutate({
                order: nextOrder,
                disabled: nextDisabled,
                available: languages?.available ?? [],
              })
            }}
            renderLabel={(code) => languageName(code) ?? code}
            toggleLabel={(code) => {
              const name = languageName(code) ?? code
              return t`Enable ${name}`
            }}
          />
        )}
        {noneEnabled && (
          <Text size="sm" c="var(--danger)">
            <Trans>At least one language must stay enabled.</Trans>
          </Text>
        )}
      </SettingRow>

      <SettingRow
        label={<Trans>Skip specials on new series</Trans>}
        description={
          <Trans>
            Specials are decimal chapters like 10.5 or 12.1. When on, they stay listed on newly
            added series but never download.
          </Trans>
        }
        control={
          <Switch
            aria-label={t`Skip specials on new series`}
            checked={monitoring?.unmonitorSpecials ?? false}
            onChange={(e) => saveMonitoring.mutate(e.currentTarget.checked)}
          />
        }
      />
    </>
  )
}

interface ServiceSpec {
  name: ConnectionName
  title: string
  description: string
  fields: ConnectionField[]
}

function useServices(): ServiceSpec[] {
  const { t } = useLingui()
  return [
    {
      name: 'flaresolverr',
      title: 'FlareSolverr',
      description: t`Gets past Cloudflare for sources like MangaFire. Without it those sources are skipped.`,
      fields: [{ key: 'url', label: t`URL`, placeholder: 'http://localhost:8191' }],
    },
    {
      name: 'prowlarr',
      title: 'Prowlarr',
      description: t`Searches your indexers for manga releases to download as torrents.`,
      fields: [
        { key: 'url', label: t`URL`, placeholder: 'http://localhost:9696' },
        { key: 'apiKey', label: t`API key`, secret: true },
      ],
    },
    {
      name: 'qbittorrent',
      title: 'qBittorrent',
      description: t`Downloads what Prowlarr finds. Finished torrents import into the library on their own. Fill the path mapping only if qBittorrent sees paths Maki can't, as in Docker.`,
      fields: [
        { key: 'url', label: t`URL`, placeholder: 'http://localhost:8080' },
        { key: 'username', label: t`Username` },
        { key: 'password', label: t`Password`, secret: true },
        { key: 'pathMapFrom', label: t`Path mapping - qBittorrent side`, placeholder: t`/downloads (optional)` },
        { key: 'pathMapTo', label: t`Path mapping - Maki side`, placeholder: t`Z:\\downloads (optional)` },
      ],
    },
    {
      name: 'kavita',
      title: 'Kavita',
      description: t`Maki asks Kavita to scan a series after its files change and sends it posters, links and status. The API key is under User Settings, 3rd Party Clients. Fill the path mapping only if Kavita sees the library under another path.`,
      fields: [
        { key: 'url', label: t`URL`, placeholder: 'http://localhost:5000' },
        { key: 'apiKey', label: t`API key`, secret: true },
        { key: 'pathMapFrom', label: t`Path mapping - Maki side`, placeholder: t`C:\\Manga (optional)` },
        { key: 'pathMapTo', label: t`Path mapping - Kavita side`, placeholder: t`/manga (optional)` },
      ],
    },
  ]
}

/** A connection counts as set up once it has a saved URL; every service here needs one. */
function useConnected(name: ConnectionName): boolean {
  const { data } = useConnectionSettings<Record<string, string | null>>(name)
  return Boolean(data?.url)
}

/** Titles of the services with a saved URL, in the order the Connections step lists them. */
function useConnectedTitles(): string[] {
  const services = useServices()
  const connected: Record<ConnectionName, boolean> = {
    flaresolverr: useConnected('flaresolverr'),
    prowlarr: useConnected('prowlarr'),
    qbittorrent: useConnected('qbittorrent'),
    kavita: useConnected('kavita'),
  }
  return services.filter((s) => connected[s.name]).map((s) => s.title)
}

function ServiceRow({ service }: { service: ServiceSpec }) {
  const [open, setOpen] = useState(false)
  const connected = useConnected(service.name)
  const bodyId = `setup-service-${service.name}`

  return (
    <div className="setup-service" data-open={open || undefined}>
      <UnstyledButton
        className="setup-service-head"
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={() => setOpen((o) => !o)}
      >
        <div className="setup-service-text">
          <Text className="setup-row-label">{service.title}</Text>
          <Text className="setup-row-description">{service.description}</Text>
        </div>
        <span className="setup-service-state" data-connected={connected || undefined}>
          {connected ? <Trans>Saved</Trans> : <Trans>Not set</Trans>}
        </span>
        <IconChevronDown size={16} className="setup-service-chevron" />
      </UnstyledButton>
      <Collapse expanded={open} id={bodyId}>
        <div className="setup-service-body">
          <ConnectionForm name={service.name} title={service.title} fields={service.fields} />
        </div>
      </Collapse>
    </div>
  )
}

function ConnectionsStep() {
  const services = useServices()
  return (
    <div className="setup-services">
      {services.map((s) => (
        <ServiceRow key={s.name} service={s} />
      ))}
    </div>
  )
}

function SummaryLine({ tone, children, action }: { tone: Tone; children: ReactNode; action?: ReactNode }) {
  return (
    <div className="setup-summary-line" data-tone={tone}>
      <span className="setup-summary-icon">
        {tone === 'warn' ? <IconAlertTriangle size={14} /> : <IconCheck size={14} />}
      </span>
      <Text className="setup-summary-text">{children}</Text>
      {action}
    </div>
  )
}

function FinishStep({ goTo, finishTo }: { goTo: (step: StepId) => void; finishTo: (path: string) => void }) {
  const { data: rootFolders } = useRootFolders()
  const { data: metadata } = useMetadataSettings()
  const { data: recIndex } = useRecommendationIndex()
  const connectedNames = useConnectedTitles()
  const folderCount = rootFolders?.length ?? 0
  const connectedList = connectedNames.join(', ')

  return (
    <>
      <div className="setup-summary">
        {folderCount > 0 ? (
          <SummaryLine tone="ok">
            {plural(folderCount, { one: '# library folder', other: '# library folders' })}
          </SummaryLine>
        ) : (
          <SummaryLine
            tone="warn"
            action={
              <Anchor component="button" size="sm" onClick={() => goTo('library')}>
                <Trans>Add one</Trans>
              </Anchor>
            }
          >
            <Trans>No library folder yet. Series need one to download into.</Trans>
          </SummaryLine>
        )}
        <SummaryLine tone="ok">
          {metadata?.useLocalDb === false ? (
            <Trans>Metadata straight from the MangaBaka API</Trans>
          ) : metadata?.dumpPresent ? (
            <Trans>Local MangaBaka database ready</Trans>
          ) : (
            <Trans>Local MangaBaka database downloads once you finish</Trans>
          )}
        </SummaryLine>
        <SummaryLine tone="ok">
          {recIndex?.embeddingModel === 'off' ? (
            <Trans>Recommendations off</Trans>
          ) : (
            <Trans>Recommendations on</Trans>
          )}
        </SummaryLine>
        <SummaryLine tone="ok">
          {connectedNames.length > 0 ? (
            <Trans>Connected: {connectedList}</Trans>
          ) : (
            <Trans>No outside tools connected. That's fine, sources download on their own.</Trans>
          )}
        </SummaryLine>
      </div>

      <Text className="setup-next-heading">
        <Trans>Where to go next</Trans>
      </Text>
      <div className="setup-next">
        <UnstyledButton className="setup-next-card" onClick={() => finishTo('/add')}>
          <Text className="setup-row-label">
            <Trans>Add a series</Trans>
          </Text>
          <Text className="setup-row-description">
            <Trans>Search MangaBaka and start downloading.</Trans>
          </Text>
        </UnstyledButton>
        <UnstyledButton className="setup-next-card" onClick={() => finishTo('/import')}>
          <Text className="setup-row-label">
            <Trans>Import what you have</Trans>
          </Text>
          <Text className="setup-row-description">
            <Trans>Match the series already in your library folders.</Trans>
          </Text>
        </UnstyledButton>
        <UnstyledButton
          className="setup-next-card"
          onClick={() => finishTo('/settings?tab=integrations&s=scrobbling')}
        >
          <Text className="setup-row-label">
            <Trans>Connect your trackers</Trans>
          </Text>
          <Text className="setup-row-description">
            <Trans>Send reading progress to AniList, MyAnimeList, Kitsu or MangaBaka.</Trans>
          </Text>
        </UnstyledButton>
        <UnstyledButton className="setup-next-card" onClick={() => finishTo('/settings?tab=users&s=users')}>
          <Text className="setup-row-label">
            <Trans>Invite people</Trans>
          </Text>
          <Text className="setup-row-description">
            <Trans>Give others their own account, library access and reading history.</Trans>
          </Text>
        </UnstyledButton>
      </div>
    </>
  )
}

interface StepMeta {
  id: StepId
  label: string
  title: string
  lead: string
  hint?: string
  tone?: Tone
}

function useSteps(): StepMeta[] {
  const { t } = useLingui()
  const { locale, locales } = useLanguageChoice()
  const { data: rootFolders } = useRootFolders()
  const { data: metadata } = useMetadataSettings()
  const { data: languages } = useSourceLanguages()
  const connectedCount = useConnectedTitles().length
  const folderCount = rootFolders?.length ?? 0
  const topLanguages = (languages?.order ?? [])
    .filter((c) => !languages?.disabled.includes(c))
    .slice(0, 2)
    .map((c) => languageName(c) ?? c)
    .join(', ')

  return [
    {
      id: 'welcome',
      label: t`Welcome`,
      title: t`Welcome to Maki`,
      lead: t`A few choices to get your library running. Everything saves as you go, and all of it is in Settings afterwards.`,
      hint: locales.find((l) => l.code === locale)?.label,
    },
    {
      id: 'library',
      label: t`Library`,
      title: t`Where your manga lives`,
      lead: t`Pick the folders Maki reads from and downloads into, and how it treats the files it finds there.`,
      hint: rootFolders
        ? folderCount > 0
          ? plural(folderCount, { one: '# folder', other: '# folders' })
          : t`No folder yet`
        : undefined,
      tone: rootFolders && folderCount === 0 ? 'warn' : undefined,
    },
    {
      id: 'discovery',
      label: t`Discover`,
      title: t`Metadata and Discover`,
      lead: t`Series details come from MangaBaka. Choose how much of it Maki keeps at hand, and what it may show you.`,
      hint: metadata ? (metadata.useLocalDb ? t`Local database` : t`API only`) : undefined,
    },
    {
      id: 'downloads',
      label: t`Downloads`,
      title: t`What to download`,
      lead: t`Which languages Maki looks for, and which chapters it leaves alone.`,
      hint: topLanguages || undefined,
    },
    {
      id: 'connections',
      label: t`Connections`,
      title: t`Tools you already run`,
      lead: t`All optional. Maki downloads from its own sources without any of these. Open one to fill it in.`,
      hint:
        connectedCount > 0
          ? plural(connectedCount, { one: '# connected', other: '# connected' })
          : t`Optional`,
    },
    {
      id: 'finish',
      label: t`Finish`,
      title: t`You're set`,
      lead: t`Here is what Maki starts with. You can come back to this guide from the top of Settings.`,
    },
  ]
}

function GuideRail({
  steps,
  active,
  visited,
  onSelect,
  onSkip,
  skipping,
}: {
  steps: StepMeta[]
  active: number
  visited: Set<number>
  onSelect: (index: number) => void
  onSkip: () => void
  skipping: boolean
}) {
  const { t } = useLingui()
  return (
    <aside className="setup-rail">
      <div className="setup-rail-brand">
        <span className="brand-mark setup-brand-mark">
          <IconBrandMark />
        </span>
        <div>
          <Text className="setup-rail-wordmark">Maki</Text>
          <Text className="setup-rail-caption">
            <Trans>Setup</Trans>
          </Text>
        </div>
      </div>

      <nav aria-label={t`Setup steps`}>
        <ol className="setup-steps">
          {steps.map((step, i) => {
            const current = i === active
            const done = !current && visited.has(i)
            return (
              <li key={step.id}>
                <UnstyledButton
                  className="setup-step"
                  data-current={current || undefined}
                  data-done={done || undefined}
                  aria-current={current ? 'step' : undefined}
                  onClick={() => onSelect(i)}
                >
                  <span className="setup-step-index">{done ? <IconCheck size={13} stroke={2.6} /> : i + 1}</span>
                  <span className="setup-step-text">
                    <span className="setup-step-label">{step.label}</span>
                    {step.hint && (
                      <span className="setup-step-hint" data-tone={step.tone}>
                        {step.hint}
                      </span>
                    )}
                  </span>
                </UnstyledButton>
              </li>
            )
          })}
        </ol>
      </nav>

      <div className="setup-rail-foot">
        <Button variant="subtle" color="var(--neutral)" size="xs" onClick={onSkip} loading={skipping}>
          <Trans>Skip setup</Trans>
        </Button>
        <Text className="setup-rail-note">
          <Trans>Nothing here is final. Every option is also in Settings.</Trans>
        </Text>
      </div>
    </aside>
  )
}

/**
 * First-run guide, full screen over the app. Admin-only: every setting it touches is an instance
 * setting, and the server refuses them from anyone else. Each control saves on change through the
 * same endpoints as Settings, so leaving early loses nothing but a half-typed connection.
 *
 * The open state is latched rather than read off the setup query, because picking a language clears
 * the whole query cache. Reading the query directly would unmount the guide and drop the step.
 */
export default function SetupWizard() {
  const { t } = useLingui()
  const navigate = useNavigate()
  const { data: setup } = useSetupStatus()
  const complete = useCompleteSetup()
  const steps = useSteps()
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const [visited, setVisited] = useState<Set<number>>(() => new Set([0]))
  const titleRef = useRef<HTMLHeadingElement>(null)
  const stageRef = useRef<HTMLDivElement>(null)

  // Dirty connection forms, reported by their Save buttons, so leaving a step with typed-in
  // credentials asks once instead of dropping them.
  const unsaved = useRef(new Set<string>())
  const [unsavedCount, setUnsavedCount] = useState(0)
  const [confirmLeave, setConfirmLeave] = useState(false)
  const reportUnsaved = useCallback((id: string, dirty: boolean) => {
    if (dirty) unsaved.current.add(id)
    else unsaved.current.delete(id)
    setUnsavedCount(unsaved.current.size)
  }, [])

  useEffect(() => {
    if (!setup) return
    if (setup.completed) {
      setOpen(false)
    } else if (!open) {
      setOpen(true)
      setActive(0)
      setVisited(new Set([0]))
    }
  }, [setup, open])

  useEffect(() => {
    if (unsavedCount === 0) setConfirmLeave(false)
  }, [unsavedCount])

  const last = STEP_IDS.length - 1

  const go = (index: number) => {
    const next = Math.max(0, Math.min(index, last))
    if (next === active) return
    setActive(next)
    setVisited((v) => new Set(v).add(next))
    setConfirmLeave(false)
    stageRef.current?.scrollTo({ top: 0 })
    requestAnimationFrame(() => titleRef.current?.focus({ preventScroll: true }))
  }

  const advance = () => {
    if (unsavedCount > 0 && !confirmLeave) {
      setConfirmLeave(true)
      return
    }
    go(active + 1)
  }

  const finish = (then?: string) =>
    complete.mutate(true, {
      onSuccess: () => {
        if (then) navigate(then)
      },
    })

  if (!open) return null

  const step = steps[active]
  const stepNumber = active + 1
  const stepTotal = steps.length

  return (
    <Modal
      opened
      onClose={() => {}}
      fullScreen
      withCloseButton={false}
      closeOnEscape={false}
      padding={0}
      transitionProps={{ duration: 0 }}
      classNames={{ content: 'setup-modal', body: 'setup-modal-body' }}
      aria-label={t`Setup guide`}
    >
      <UnsavedSettingsContext value={reportUnsaved}>
        <div className="setup-frame">
          <div className="setup-art" aria-hidden />
          <GuideRail
            steps={steps}
            active={active}
            visited={visited}
            onSelect={go}
            onSkip={() => finish()}
            skipping={complete.isPending}
          />

          <main className="setup-main">
            <div className="setup-mobile-bar">
              <Text className="setup-mobile-step">
                <Trans>
                  Step {stepNumber} of {stepTotal}
                </Trans>
              </Text>
              <Button variant="subtle" color="var(--neutral)" size="compact-xs" onClick={() => finish()}>
                <Trans>Skip setup</Trans>
              </Button>
              <div className="setup-mobile-progress" aria-hidden>
                {steps.map((s, i) => (
                  <span key={s.id} data-filled={i <= active || undefined} />
                ))}
              </div>
            </div>

            <div className="setup-stage" ref={stageRef}>
              <div className="setup-stage-inner" key={step.id}>
                <Text className="setup-eyebrow">
                  <Trans>
                    Step {stepNumber} of {stepTotal}
                  </Trans>
                </Text>
                <h2 className="setup-title" ref={titleRef} tabIndex={-1}>
                  {step.title}
                </h2>
                <Text className="setup-lead">{step.lead}</Text>

                <div className="setup-content">
                  {step.id === 'welcome' && <WelcomeStep />}
                  {step.id === 'library' && <LibraryStep />}
                  {step.id === 'discovery' && <DiscoveryStep />}
                  {step.id === 'downloads' && <DownloadsStep />}
                  {step.id === 'connections' && <ConnectionsStep />}
                  {step.id === 'finish' && <FinishStep goTo={(id) => go(STEP_IDS.indexOf(id))} finishTo={finish} />}
                </div>
              </div>
            </div>

            <footer className="setup-footer">
              <div className="setup-footer-inner">
                <Button
                  variant="default"
                  leftSection={<IconArrowLeft size={16} />}
                  onClick={() => go(active - 1)}
                  style={{ visibility: active === 0 ? 'hidden' : undefined }}
                >
                  <Trans>Back</Trans>
                </Button>
                {confirmLeave && (
                  <Text className="setup-footer-warning" role="status">
                    <Trans>A connection has unsaved changes.</Trans>
                  </Text>
                )}
                {active === last ? (
                  <Button onClick={() => finish()} loading={complete.isPending}>
                    <Trans>Finish setup</Trans>
                  </Button>
                ) : (
                  <Button rightSection={<IconArrowRight size={16} />} onClick={advance}>
                    {confirmLeave ? <Trans>Continue without saving</Trans> : <Trans>Continue</Trans>}
                  </Button>
                )}
              </div>
            </footer>
          </main>
        </div>
      </UnsavedSettingsContext>
    </Modal>
  )
}
