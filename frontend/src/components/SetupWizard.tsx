import { useEffect, useState } from 'react'
import {
  ActionIcon,
  Alert,
  Anchor,
  Button,
  Card,
  Group,
  Modal,
  Radio,
  Stack,
  Stepper,
  Switch,
  Text,
  TextInput,
  Title,
  UnstyledButton,
} from '@mantine/core'
import { IconCheck, IconInfoCircle, IconTrash } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Link } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import { t } from '@lingui/core/macro'
import {
  useAddRootFolder,
  useCompleteSetup,
  useDeleteRootFolder,
  useDiscoverSettings,
  useFlareSolverrSettings,
  type FolderNamingMode,
  useLibrarySettings,
  useMetadataSettings,
  useMonitoringSettings,
  useRecommendationIndex,
  useRootFolders,
  useSaveDiscoverSettings,
  useSaveFlareSolverr,
  useSaveLibrarySettings,
  useSaveMetadataSettings,
  useSaveMonitoringSettings,
  useSetEmbeddingModel,
  useTestFlareSolverr,
} from '../api/hooks'
import { ConnectionSettingsCard } from './ConnectionSettingsCard'
import { ContentRatingCards } from './ContentRatingCards'
import { RecommendationModelCards } from './RecommendationModelCards'
import { useThemeChoice } from '../theme-context'
import { useLabel } from '../i18n-context'

function StepBody({ title, children }: { title: React.ReactNode; children: React.ReactNode }) {
  return (
    <Stack gap="md" mt="lg">
      <Title order={3}>{title}</Title>
      {children}
    </Stack>
  )
}

function LibraryStep() {
  const { t } = useLingui()
  const [newPath, setNewPath] = useState('')
  const { data: rootFolders } = useRootFolders()
  const addFolder = useAddRootFolder()
  const deleteFolder = useDeleteRootFolder()
  const { data: metadata } = useMetadataSettings()
  const saveMetadata = useSaveMetadataSettings()

  const add = () => {
    if (!newPath.trim()) return
    addFolder.mutate(newPath.trim(), { onSuccess: () => setNewPath('') })
  }

  return (
    <StepBody title={<Trans>Your library</Trans>}>
      <Text size="sm" c="var(--ink-3)">
        <Trans>
          Point Maki at the folder where your manga is stored (or should be). Kavita, if you use
          it, watches the same location. You can add more later in Settings.
        </Trans>
      </Text>
      <Stack gap="xs">
        {rootFolders?.map((f) => (
          <Group key={f.id} justify="space-between" wrap="nowrap">
            <Text size="sm">
              {f.path}
              {!f.accessible && (
                <Text span c="var(--danger)" size="xs" ml="xs">
                  <Trans>(inaccessible)</Trans>
                </Text>
              )}
            </Text>
            <ActionIcon
              variant="subtle"
              color="var(--danger)"
              onClick={() => deleteFolder.mutate(f.id)}
              aria-label={t`Delete root folder`}
            >
              <IconTrash size={16} />
            </ActionIcon>
          </Group>
        ))}
      </Stack>
      <Group>
        <TextInput
          placeholder={t`C:\\Manga or /library`}
          value={newPath}
          onChange={(e) => setNewPath(e.currentTarget.value)}
          style={{ flex: 1 }}
          onKeyDown={(e) => e.key === 'Enter' && add()}
        />
        <Button onClick={add} loading={addFolder.isPending}>
          <Trans>Add</Trans>
        </Button>
      </Group>
      <Switch
        mt="md"
        label={t`Use the local MangaBaka database (Highly Recommended)`}
        description={t`Keeps a ~3 GB metadata snapshot on disk so searches and imports are instant instead of rate-limited. Downloads in the background; the API is used until it's ready.`}
        checked={metadata?.useLocalDb ?? true}
        onChange={(e) => saveMetadata.mutate(e.currentTarget.checked)}
      />
    </StepBody>
  )
}

function RecommendationsStep() {
  const { data: recIndex } = useRecommendationIndex()
  const setModel = useSetEmbeddingModel()

  return (
    <StepBody title={<Trans>Recommendations</Trans>}>
      <Text size="sm" c="var(--ink-3)">
        <Trans>
          Discover recommends by semantic "feel" and searches by description, using a local
          embedding model. Base is lighter (~240 MB of RAM); Large is more accurate but heavier
          (~500 MB) with a bigger one-time download; Off disables both. Either model is downloaded
          prebuilt, so your machine doesn't do the heavy work, and you can change this any time in
          Settings.
        </Trans>
      </Text>
      <RecommendationModelCards
        status={recIndex}
        busy={setModel.isPending}
        onSelect={(kind) => setModel.mutate(kind)}
      />
    </StepBody>
  )
}

function PreferencesStep() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data: monitoring } = useMonitoringSettings()
  const saveMonitoring = useSaveMonitoringSettings()
  const { data: library } = useLibrarySettings()
  const saveLibrary = useSaveLibrarySettings()
  const { data: discover } = useDiscoverSettings()
  const saveDiscover = useSaveDiscoverSettings()
  const { themeId, setThemeId, presets } = useThemeChoice()

  return (
    <StepBody title={<Trans>Preferences</Trans>}>
      <Card withBorder radius="md" padding="md">
        <Switch
          label={t`Don't want specials on new series`}
          description={t`Specials are decimal chapters (10.5 omake, x.1/x.2 splits). When on, they stay listed on newly added series but never download.`}
          checked={monitoring?.unmonitorSpecials ?? false}
          onChange={(e) => saveMonitoring.mutate(e.currentTarget.checked)}
          mb="md"
        />
        <Switch
          label={t`Write ComicInfo.xml into imported files`}
          description={t`Off leaves torrent and manually imported files untouched. Chapters Maki downloads itself always get a ComicInfo, since Maki builds those files.`}
          checked={library?.writeComicInfo ?? true}
          onChange={(e) =>
            saveLibrary.mutate({
              writeComicInfo: e.currentTarget.checked,
              folderNamingMode: library?.folderNamingMode ?? 'rename',
            })
          }
        />
      </Card>
      <Card withBorder radius="md" padding="md">
        <Text size="sm" fw={500} mb={4}>
          <Trans>Content rating</Trans>
        </Text>
        <Text size="sm" c="var(--ink-3)" mb="xs">
          <Trans>Highest rating shown in search, Discover and recommendations. Changeable later in Settings.</Trans>
        </Text>
        <ContentRatingCards
          value={discover?.maxContentRating ?? 'erotica'}
          onChange={(rating) => saveDiscover.mutate(rating)}
        />
      </Card>
      <Card withBorder radius="md" padding="md">
        <Text size="sm" fw={500} mb={4}>
          <Trans>Folder naming</Trans>
        </Text>
        <Text size="sm" c="var(--ink-3)" mb="xs">
          <Trans>
            Whether Maki renames an imported series' folder to its standard sanitized-title name,
            or leaves it as found. Changeable later in Settings.
          </Trans>
        </Text>
        <Radio.Group
          value={library?.folderNamingMode ?? 'rename'}
          onChange={(value) =>
            saveLibrary.mutate({
              writeComicInfo: library?.writeComicInfo ?? true,
              folderNamingMode: value as FolderNamingMode,
            })
          }
        >
          <Stack gap="xs" mt="xs">
            <Radio value="rename" label={t`Rename folder to Maki standard`} />
            <Radio
              value="keep-new-standard"
              label={t`Keep folder name, but put new downloads in a Maki standard folder`}
            />
            <Radio
              value="keep-original"
              label={t`Keep folder name, and put new downloads there too`}
            />
          </Stack>
        </Radio.Group>
      </Card>
      <Card withBorder radius="md" padding="md">
        <Text size="sm" fw={500} mb={4}>
          <Trans>Appearance</Trans>
        </Text>
        <Text size="sm" c="var(--ink-3)" mb="sm">
          <Trans>Pick an accent colour or the light theme. Applies instantly, remembered on this device.</Trans>
        </Text>
        <Group gap="sm">
          {presets.map((p) => {
            const active = p.id === themeId
            return (
              <UnstyledButton
                key={p.id}
                onClick={() => setThemeId(p.id)}
                aria-pressed={active}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 8,
                  padding: '8px 12px',
                  borderRadius: 10,
                  border: `1px solid ${active ? 'var(--brand)' : 'var(--border)'}`,
                  background: active ? 'var(--surface-hover)' : 'transparent',
                  boxShadow: active ? '0 0 0 1px var(--brand)' : undefined,
                }}
              >
                <span
                  style={{
                    width: 18,
                    height: 18,
                    borderRadius: '50%',
                    background: p.swatch,
                    border: '1px solid rgba(0,0,0,0.25)',
                    flexShrink: 0,
                  }}
                />
                <Text size="sm" fw={active ? 600 : 500}>
                  {renderLabel(p.label)}
                </Text>
                {active && <IconCheck size={14} style={{ color: 'var(--brand)' }} />}
              </UnstyledButton>
            )
          })}
        </Group>
      </Card>
    </StepBody>
  )
}

function FlareSolverrCard() {
  const { data: settings } = useFlareSolverrSettings()
  const save = useSaveFlareSolverr()
  const test = useTestFlareSolverr()
  const [url, setUrl] = useState('')

  useEffect(() => {
    if (settings?.url) setUrl(settings.url)
  }, [settings?.url])

  return (
    <div>
      <Text size="sm" fw={600}>
        FlareSolverr
      </Text>
      <Text size="xs" c="var(--ink-3)" mb="xs">
        <Trans>
          Required for Cloudflare-protected sources like MangaFire. Point at a running instance
          (e.g. http://localhost:8191).
        </Trans>
      </Text>
      <Group align="flex-end">
        <TextInput
          placeholder="http://localhost:8191"
          value={url}
          onChange={(e) => setUrl(e.currentTarget.value)}
          style={{ flex: 1 }}
        />
        <Button
          variant="default"
          loading={test.isPending}
          onClick={() =>
            test.mutate(url || null, {
              onSuccess: () =>
                notifications.show({ message: t`FlareSolverr is reachable`, color: 'green' }),
            })
          }
        >
          <Trans>Test</Trans>
        </Button>
        <Button
          loading={save.isPending}
          onClick={() =>
            save.mutate(url || null, {
              onSuccess: () => notifications.show({ message: t`Saved`, color: 'green' }),
            })
          }
        >
          <Trans>Save</Trans>
        </Button>
      </Group>
    </div>
  )
}

function ConnectionsStep() {
  const { t } = useLingui()
  return (
    <StepBody title={<Trans>Connections</Trans>}>
      <Text size="sm" c="var(--ink-3)">
        <Trans>All optional: fill in only what you use. Everything here can be changed later in Settings.</Trans>
      </Text>
      <FlareSolverrCard />
      <ConnectionSettingsCard
        name="prowlarr"
        title="Prowlarr"
        description={t`Search manga releases on your indexers for torrent downloads.`}
        fields={[
          { key: 'url', label: t`URL`, placeholder: 'http://localhost:9696' },
          { key: 'apiKey', label: t`API key`, secret: true },
        ]}
      />
      <ConnectionSettingsCard
        name="qbittorrent"
        title="qBittorrent"
        description={t`Download client for grabbed releases. Completed torrents are imported automatically.`}
        fields={[
          { key: 'url', label: t`URL`, placeholder: 'http://localhost:8080' },
          { key: 'username', label: t`Username` },
          { key: 'password', label: t`Password`, secret: true },
        ]}
      />
      <ConnectionSettingsCard
        name="kavita"
        title="Kavita"
        description={t`Maki asks Kavita to scan the series folder after downloads and pushes posters, links and status. Get the API key under User Settings → 3rd Party Clients.`}
        fields={[
          { key: 'url', label: t`URL`, placeholder: 'http://localhost:5000' },
          { key: 'apiKey', label: t`API key`, secret: true },
        ]}
      />
    </StepBody>
  )
}

function ScrobbleStep() {
  return (
    <StepBody title={<Trans>Scrobbling</Trans>}>
      <Text size="sm" c="var(--ink-3)">
        <Trans>
          Maki can push your Kavita reading progress to AniList, MyAnimeList, Kitsu and MangaBaka.
          Each needs an OAuth app or token, so it's set up on the Settings page rather than here.
        </Trans>
      </Text>
      <Alert color="var(--info)" icon={<IconInfoCircle size={16} />} variant="light">
        <Trans>
          Finish setup, then open{' '}
          <Anchor component={Link} to="/settings">
            Settings → Scrobbling
          </Anchor>{' '}
          to connect your tracker accounts. Review and fix matches on the Scrobble page.
        </Trans>
      </Alert>
    </StepBody>
  )
}

const STEP_KEYS = ['welcome', 'library', 'preferences', 'recommendations', 'connections', 'scrobbling', 'done']

/**
 * First-run onboarding overlay. Rendered by App whenever setup.completed is false. Every step is
 * skippable and writes its settings immediately (same endpoints as the Settings page), so closing
 * early loses nothing. Finishing or skipping marks setup complete; the Settings "Run setup guide"
 * button flips it back to re-open this.
 */
export default function SetupWizard() {
  const { t } = useLingui()
  const [active, setActive] = useState(0)
  const complete = useCompleteSetup()
  const stepLabels = [
    t`Welcome`,
    t`Library`,
    t`Preferences`,
    t`Recommendations`,
    t`Connections`,
    t`Scrobbling`,
    t`Done`,
  ]

  const finish = () => complete.mutate(true)
  const next = () => setActive((s) => Math.min(s + 1, STEP_KEYS.length - 1))
  const back = () => setActive((s) => Math.max(s - 1, 0))
  const last = active === STEP_KEYS.length - 1

  return (
    <Modal
      opened
      onClose={finish}
      fullScreen
      withCloseButton={false}
      padding="xl"
      styles={{ body: { maxWidth: 720, margin: '0 auto' } }}
    >
      <Group justify="space-between" mb="md">
        <Text fw={800} fz="xl" style={{ letterSpacing: '-0.02em' }}>
          <Trans>Welcome to Maki</Trans>
        </Text>
        <Button variant="subtle" color="var(--neutral)" size="xs" onClick={finish} loading={complete.isPending}>
          <Trans>Skip setup</Trans>
        </Button>
      </Group>

      <Stepper active={active} onStepClick={setActive} size="sm" iconSize={28}>
        {STEP_KEYS.map((key, i) => (
          <Stepper.Step key={key} label={stepLabels[i]} />
        ))}
      </Stepper>

      {active === 0 && (
        <StepBody title={<Trans>Let's get you set up</Trans>}>
          <Text size="sm" c="var(--ink-3)">
            <Trans>
              A few quick choices to get Maki ready: where your library lives, how metadata and
              monitoring behave, and any download or reading tools you already run. Every step is
              optional and can be changed later in Settings. Your choices save as you go.
            </Trans>
          </Text>
        </StepBody>
      )}
      {active === 1 && <LibraryStep />}
      {active === 2 && <PreferencesStep />}
      {active === 3 && <RecommendationsStep />}
      {active === 4 && <ConnectionsStep />}
      {active === 5 && <ScrobbleStep />}
      {active === 6 && (
        <StepBody title={<Trans>All set</Trans>}>
          <Text size="sm" c="var(--ink-3)">
            <Trans>
              You're ready to go. Head to <b>Add Series</b> to start building your library, or
              open <b>Settings</b> any time to fine-tune connections and scrobbling. You can
              re-open this guide from Settings → General.
            </Trans>
          </Text>
        </StepBody>
      )}

      <Group justify="space-between" mt="xl">
        <Button variant="default" onClick={back} disabled={active === 0}>
          <Trans>Back</Trans>
        </Button>
        {last ? (
          <Button onClick={finish} loading={complete.isPending}>
            <Trans>Finish</Trans>
          </Button>
        ) : (
          <Button onClick={next}>
            <Trans>Next</Trans>
          </Button>
        )}
      </Group>
    </Modal>
  )
}
