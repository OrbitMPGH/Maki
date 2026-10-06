import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useLabel } from '../i18n-context'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { Button, Group, Modal, Stack, Tabs, Text } from '@mantine/core'
import { PageHeader } from '../components/ui/PageHeader'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { useAuth } from '../auth/AuthProvider'
import { SETTINGS_ENTRY_ALIASES, SETTINGS_TAB_ALIASES, SETTINGS_TABS } from './settings/registry'
import { useVisibleSettingsEntries } from './settings/useVisibleSettingsEntries'
import { ApiKeysSection, SignInSection } from '../components/settings/AccountSection'
import { NotificationPrefsSection } from '../components/settings/NotificationPrefsSection'
import { NetworkSection, OidcSection, SecuritySection } from '../components/settings/SecuritySection'
import { UsersSection } from '../components/settings/UsersSection'
import { ReadingProfilesSection } from '../components/settings/ReadingProfilesSection'
import { ProgressSection } from '../components/settings/ProgressSection'
import { QualityFormatsSection, UpgradeProfilesSection } from '../components/settings/UpgradeProfilesSection'
import { useCompleteSetup } from '../api/hooks'
import { useReaderUsed } from '../api/reader'
import { UnsavedSettingsContext } from '../components/settings/SaveButton'
import { SettingsIndex } from '../components/settings/SettingsIndex'
import { NotificationsSection } from '../components/NotificationsSection'
import {
  AppearanceSection,
  DiscoverSection,
  HomeSectionsSection,
  LanguageSection,
  SeriesPageSection,
  TitleLanguageSection,
} from './settings/PreferenceCards'
import { KavitaSyncSection, OpdsSection } from './settings/ReadingCards'
import {
  LibraryFilesSection,
  MetadataSection,
  NamingSection,
  NewSeriesDefaultsSection,
  RecommendationIndexSection,
  RootFoldersSection,
} from './settings/LibraryCards'
import {
  DownloadQueueSection,
  FlareSolverrCard,
  ProwlarrSection,
  QbittorrentCard,
  SmartDownloadSection,
  ReadFileCleanupSection,
  SourceLanguageSection,
  SourcePrioritySection,
  UpgradeScanSection,
  VolumeReleasesSection,
} from './settings/DownloadCards'
import { ImportListSettingsSection, KavitaCard, ScrobbleSection } from './settings/IntegrationCards'
import { BackupSection, ImageCacheSection, UpdatesSection } from './settings/SystemCards'

/**
 * Every card, keyed by its registry id. The registry decides order, tab and who may see it; this
 * only says how each id is built, so adding a setting is one entry there plus one line here.
 */
function useSectionNodes(): Record<string, ReactNode> {
  const { i18n } = useLingui()

  // Memoized so the elements keep their identity between renders. Without it every section subtree
  // re-renders whenever anything on this page changes. Keyed on the locale so a language switch
  // rebuilds them.
  return useMemo<Record<string, ReactNode>>(
    () => ({
      'sign-in': <SignInSection />,
      'api-keys': <ApiKeysSection />,

      appearance: <AppearanceSection />,
      language: <LanguageSection />,
      'title-language': <TitleLanguageSection />,
      'discover-rating': <DiscoverSection />,
      'home-screen': <HomeSectionsSection />,
      'series-page': <SeriesPageSection />,
      'notification-prefs': <NotificationPrefsSection />,

      reader: <ReadingProfilesSection />,
      progress: <ProgressSection />,
      opds: <OpdsSection />,
      'kavita-sync': <KavitaSyncSection />,

      'root-folders': <RootFoldersSection />,
      naming: <NamingSection />,
      'library-files': <LibraryFilesSection />,
      monitoring: <NewSeriesDefaultsSection />,
      metadata: <MetadataSection />,
      recommendations: <RecommendationIndexSection />,

      downloads: <DownloadQueueSection />,
      'smart-download': <SmartDownloadSection />,
      'read-cleanup': <ReadFileCleanupSection />,
      languages: <SourceLanguageSection />,
      sources: <SourcePrioritySection />,
      flaresolverr: <FlareSolverrCard />,
      prowlarr: <ProwlarrSection />,
      qbittorrent: <QbittorrentCard />,
      profiles: <UpgradeProfilesSection />,
      formats: <QualityFormatsSection />,
      upgrades: <UpgradeScanSection />,
      'volume-releases': <VolumeReleasesSection />,

      kavita: <KavitaCard />,
      scrobbling: <ScrobbleSection />,
      'import-lists': <ImportListSettingsSection />,
      notifications: <NotificationsSection />,

      users: <UsersSection />,
      security: <SecuritySection />,
      oidc: <OidcSection />,

      network: <NetworkSection />,
      backup: <BackupSection />,
      'image-cache': <ImageCacheSection />,
      updates: <UpdatesSection />,
    }),
    [i18n.locale],
  )
}

export default function SettingsPage() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { me } = useAuth()
  const isAdmin = me?.isAdmin ?? false
  const [searchParams, setSearchParams] = useSearchParams()
  const sectionNodes = useSectionNodes()
  const completeSetup = useCompleteSetup()
  const readerUsedPending = useReaderUsed().isPending
  const visible = useVisibleSettingsEntries()
  const tabs = useMemo(
    () => SETTINGS_TABS.filter((t) => visible.some((e) => e.tab === t.key)),
    [visible],
  )

  // The tab lives in the URL rather than in state so a deep link from the command palette lands on
  // the right one, and so the panel holding the target card is mounted by the time the scroll effect
  // below runs. A visible `s` decides the tab on its own, so a link with a stale `tab` still lands.
  const rawTarget = searchParams.get('s')
  const target = rawTarget ? (SETTINGS_ENTRY_ALIASES[rawTarget] ?? rawTarget) : null
  const targetTab = target ? visible.find((e) => e.id === target)?.tab : undefined
  const rawTab = searchParams.get('tab')
  const requested = targetTab ?? (rawTab ? (SETTINGS_TAB_ALIASES[rawTab] ?? rawTab) : null)
  const activeTab = tabs.some((t) => t.key === requested) ? requested! : (tabs[0]?.key ?? 'account')
  const tabEntries = useMemo(() => visible.filter((e) => e.tab === activeTab), [visible, activeTab])

  // Panels unmount on a tab change (`keepMounted={false}`), which used to drop half-typed edits
  // without a word. Cards report through SettingsSection; a switch away from unsaved edits asks first.
  const unsaved = useRef(new Set<string>())
  const [unsavedCount, setUnsavedCount] = useState(0)
  const reportUnsaved = useCallback((id: string, dirty: boolean) => {
    if (dirty) unsaved.current.add(id)
    else unsaved.current.delete(id)
    setUnsavedCount(unsaved.current.size)
  }, [])
  const [pendingTab, setPendingTab] = useState<string | null>(null)
  const switchTab = (value: string) => setSearchParams({ tab: value })

  useEffect(() => {
    if (unsavedCount === 0) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [unsavedCount])

  // Kavita read sync stays hidden until the reader-used answer arrives, so a cold deep link to it
  // waits rather than being consumed while it still resolves to no tab.
  const awaitingTarget = target === 'kavita-sync' && readerUsedPending

  useEffect(() => {
    if (!target || awaitingTarget) return
    // Consumed immediately, so picking the same entry twice in a row flashes it twice. This also
    // re-runs the effect with no target, which is why nothing below is torn down on cleanup: the
    // scroll and the flash have to outlive the render that clears the parameter.
    setSearchParams(
      (current) => {
        const next = new URLSearchParams(current)
        next.delete('s')
        // Pins the tab the target resolved to, which may differ from the link's own `tab`.
        next.set('tab', activeTab)
        return next
      },
      { replace: true },
    )

    const el = document.getElementById(`setting-${target}`)
    if (!el) return
    const show = () => el.scrollIntoView({ block: 'center', behavior: 'smooth' })
    // Cards above the target fill in as their queries resolve (the source table, the indexer list),
    // which pushes it down after the first scroll lands. Re-anchoring twice costs nothing and is
    // what makes a deep link arrive at the card rather than somewhere above it.
    show()
    window.setTimeout(show, 400)
    window.setTimeout(show, 1000)
    el.classList.add('settings-flash')
    window.setTimeout(() => el.classList.remove('settings-flash'), 2200)
  }, [target, awaitingTarget, activeTab, setSearchParams])

  return (
    <SurfaceFrame pageStyle="operational" className="settings-surface">
      <PageHeader
        compact
        title={t`Settings`}
        description={
          isAdmin
            ? t`Your account and preferences, then everything about this Maki instance.`
            : t`Your account and how Maki looks.`
        }
        actions={
          isAdmin && (
            <Button
              variant="default"
              size="xs"
              loading={completeSetup.isPending}
              onClick={() => completeSetup.mutate(false)}
            >
              <Trans>Run setup guide</Trans>
            </Button>
          )
        }
      />
      <Tabs
        value={activeTab}
        variant="unstyled"
        classNames={{ list: 'series-tabs page-tabs', tab: 'series-tab' }}
        onChange={(value) => {
          if (!value || value === activeTab) return
          if (unsaved.current.size > 0) setPendingTab(value)
          else switchTab(value)
        }}
        keepMounted={false}
      >
        <Tabs.List>
          {tabs.map((tab) => (
            <Tabs.Tab key={tab.key} value={tab.key}>
              {renderLabel(tab.label)}
            </Tabs.Tab>
          ))}
        </Tabs.List>

        <UnsavedSettingsContext value={reportUnsaved}>
          {tabs.map((tab) => (
            <Tabs.Panel key={tab.key} value={tab.key}>
              <div className="settings-layout">
                <Stack className="settings-content" gap="xl">
                  <Text size="sm" c="var(--ink-3)">
                    {renderLabel(tab.description)}
                  </Text>
                  {tabEntries.map((entry) => (
                    <div key={entry.id} id={`setting-${entry.id}`} style={{ scrollMarginTop: 80 }}>
                      {sectionNodes[entry.id]}
                    </div>
                  ))}
                </Stack>
                <SettingsIndex entries={tabEntries} />
              </div>
            </Tabs.Panel>
          ))}
        </UnsavedSettingsContext>
      </Tabs>

      <Modal
        opened={pendingTab !== null}
        onClose={() => setPendingTab(null)}
        title={t`Discard unsaved changes?`}
        size="sm"
      >
        <Stack gap="md">
          <Text size="sm">
            <Plural
              value={unsavedCount}
              one="A card on this tab has changes that are not saved. Leaving the tab drops them."
              other="# cards on this tab have changes that are not saved. Leaving the tab drops them."
            />
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setPendingTab(null)}>
              <Trans>Keep editing</Trans>
            </Button>
            <Button
              color="var(--danger-fill)"
              onClick={() => {
                if (pendingTab) switchTab(pendingTab)
                setPendingTab(null)
              }}
            >
              <Trans>Discard changes</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </SurfaceFrame>
  )
}
