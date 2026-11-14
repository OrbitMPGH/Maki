import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { msg } from '@lingui/core/macro'
import { useLabel } from '../i18n-context'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { Button, Stack, Tabs } from '@mantine/core'
import { PageHeader } from '../components/ui/PageHeader'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { useAuth } from '../auth/AuthProvider'
import {
  SETTINGS_ENTRY_ALIASES,
  SETTINGS_TAB_ALIASES,
  SETTINGS_TABS,
  settingsPath,
  type SettingsEntry,
} from './settings/registry'
import { NewSettingsContext, SettingsFocusContext, useNewSettingIds } from './settings/newSettings'
import { IconArrowRight } from '../lib/icons'
import { useVisibleSettingsEntries } from './settings/useVisibleSettingsEntries'
import { ApiKeysSection, SignInSection } from '../components/settings/AccountSection'
import { NotificationPrefsSection } from '../components/settings/NotificationPrefsSection'
import { PushNotificationsSection } from '../components/settings/PushNotificationsSection'
import { OfflineReadingSection } from '../components/settings/OfflineReadingSection'
import { NetworkSection, OidcSection, SecuritySection, SignInPageSection } from '../components/settings/SecuritySection'
import { UsersSection } from '../components/settings/UsersSection'
import { ReadingProfilesSection } from '../components/settings/ReadingProfilesSection'
import { ProgressSection } from '../components/settings/ProgressSection'
import { RatingPromptsSection } from '../components/settings/RatingPromptsSection'
import { ReadingStatusSection } from '../components/settings/ReadingStatusSection'
import { QualityFormatsSection, UpgradeProfilesSection } from '../components/settings/UpgradeProfilesSection'
import { useCompleteSetup } from '../api/hooks'
import { useReaderUsed } from '../api/reader'
import { UnsavedSettingsContext } from '../components/settings/SaveButton'
import { useLeaveGuard, type LeaveTarget } from '../lib/useLeaveGuard'
import { SettingsIndex, SettingsJump } from '../components/settings/SettingsIndex'
import { NotificationsSection } from '../components/NotificationsSection'
import {
  AppearanceSection,
  DiscoverSection,
  HomeSectionsSection,
  LanguageSection,
  SeriesPageSection,
  TitleLanguageSection,
} from './settings/PreferenceCards'
import { KavitaSyncSection, KoreaderSyncSection, OpdsSection } from './settings/ReadingCards'
import {
  LibraryFilesSection,
  CreditScanSection,
  MetadataSection,
  NameParsingSection,
  NamingSection,
  NewSeriesDefaultsSection,
  RecommendationIndexSection,
  RecycleBinSection,
  RootFoldersSection,
  RootFolderRulesSection,
  AutoTagRulesSection,
} from './settings/LibraryCards'
import { DropFolderSection } from './settings/DropFolderSection'
import { PageConversionSection } from './settings/PageConversionSection'
import { UpscalingSection } from './settings/UpscalingSection'
import {
  DownloadQueueSection,
  FlareSolverrCard,
  IndexerSection,
  DownloadClientCard,
  SmartDownloadSection,
  ReadFileCleanupSection,
  SourceLanguageSection,
  SourcePrioritySection,
  UpgradeScanSection,
  VolumeReleasesSection,
} from './settings/DownloadCards'
import { ImportListSettingsSection, KavitaCard, ScrobbleSection } from './settings/IntegrationCards'
import { SuwayomiCard } from './settings/SuwayomiCards'
import { BackupSection, HealthSettingsSection, ImageCacheSection, UpdatesSection } from './settings/SystemCards'
import { scrollBehavior } from '../lib/scrollBehavior'
import { ConfirmDialog } from '../components/ui/ConfirmDialog'

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
      'push-notifications': <PushNotificationsSection />,

      reader: <ReadingProfilesSection />,
      progress: <ProgressSection />,
      'rating-prompts': <RatingPromptsSection />,
      'reading-status': <ReadingStatusSection />,
      opds: <OpdsSection />,
      'koreader-sync': <KoreaderSyncSection />,
      'offline-reading': <OfflineReadingSection />,
      'kavita-sync': <KavitaSyncSection />,

      'root-folders': <RootFoldersSection />,
      'root-folder-rules': <RootFolderRulesSection />,
      naming: <NamingSection />,
      'name-parsing': <NameParsingSection />,
      'drop-folder': <DropFolderSection />,
      'library-files': <LibraryFilesSection />,
      'page-conversion': <PageConversionSection />,
      upscaling: <UpscalingSection />,
      'credit-scan': <CreditScanSection />,
      'recycle-bin': <RecycleBinSection />,
      'auto-tag-rules': <AutoTagRulesSection />,
      monitoring: <NewSeriesDefaultsSection />,
      metadata: <MetadataSection />,
      recommendations: <RecommendationIndexSection />,

      downloads: <DownloadQueueSection />,
      'smart-download': <SmartDownloadSection />,
      'read-cleanup': <ReadFileCleanupSection />,
      languages: <SourceLanguageSection />,
      sources: <SourcePrioritySection />,
      flaresolverr: <FlareSolverrCard />,
      indexer: <IndexerSection />,
      downloadclient: <DownloadClientCard />,
      profiles: <UpgradeProfilesSection />,
      formats: <QualityFormatsSection />,
      upgrades: <UpgradeScanSection />,
      'volume-releases': <VolumeReleasesSection />,

      kavita: <KavitaCard />,
      suwayomi: <SuwayomiCard />,
      scrobbling: <ScrobbleSection />,
      'import-lists': <ImportListSettingsSection />,
      notifications: <NotificationsSection />,

      users: <UsersSection />,
      security: <SecuritySection />,
      'signin-page': <SignInPageSection />,
      oidc: <OidcSection />,

      network: <NetworkSection />,
      backup: <BackupSection />,
      'image-cache': <ImageCacheSection />,
      health: <HealthSettingsSection />,
      updates: <UpdatesSection />,
    }),
    [i18n.locale],
  )
}

const NEW_TAB = 'new'
const NEW_TAB_LABEL = msg({ message: 'New', context: 'recently added setting' })

interface NewCard {
  /** The entry the card's link jumps to: the card itself, or its first new control. */
  jumpTo: SettingsEntry
  /** New controls that sit inside an editor or a row of the card, named under its title. */
  nested: SettingsEntry[]
}

/**
 * The cards the New tab shows. A new control inside an older card brings that card along, once,
 * and its link jumps to the control rather than the card's top.
 */
function newSettingCards(visible: SettingsEntry[], newIds: ReadonlySet<string>) {
  const cards = new Map<string, NewCard>()
  for (const entry of visible) {
    if (!newIds.has(entry.id)) continue
    const card = entry.within ? visible.find((e) => e.id === entry.within) : entry
    if (!card) continue
    const found = cards.get(card.id) ?? { jumpTo: entry, nested: [] }
    if (entry.nested) found.nested.push(entry)
    cards.set(card.id, found)
  }
  return cards
}

/**
 * A card on the New tab that is there only for new controls inside it: just those controls, and
 * the names of any that are inside an editor or a row rather than on the card itself.
 */
function NewSettingFocus({ nested, children }: { nested: SettingsEntry[]; children: ReactNode }) {
  const { i18n } = useLingui()
  const renderLabel = useLabel()
  const names = new Intl.ListFormat(i18n.locale, { type: 'conjunction' }).format(
    nested.map((e) => renderLabel(e.title)),
  )
  const note =
    nested.length > 0 ? (
      <Trans comment="On the New tab of Settings: new settings found inside this card's list or editor.">
        New in here: {names}.
      </Trans>
    ) : undefined
  return (
    <div className="new-setting-focus">
      <SettingsFocusContext value={{ note }}>{children}</SettingsFocusContext>
    </div>
  )
}

/** Where a card on the New tab normally lives, as a link that goes there. */
function NewSettingHome({ card, jumpTo }: { card: SettingsEntry; jumpTo: SettingsEntry }) {
  const renderLabel = useLabel()
  const home = SETTINGS_TABS.find((x) => x.key === card.tab)
  if (!home) return null
  const tabName = renderLabel(home.label)
  return (
    <Link to={settingsPath(jumpTo)} className="new-setting-home">
      <Trans comment="On the New tab of Settings: links to the tab where this card normally lives.">In {tabName}</Trans>
      <IconArrowRight size={14} aria-hidden />
    </Link>
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
  const newIds = useNewSettingIds(visible)
  const newCards = useMemo(() => newSettingCards(visible, newIds), [visible, newIds])
  const realTabs = useMemo(
    () => SETTINGS_TABS.filter((t) => visible.some((e) => e.tab === t.key)),
    [visible],
  )
  const tabs = useMemo(
    () => (newCards.size > 0 ? [{ key: NEW_TAB, label: NEW_TAB_LABEL }, ...realTabs] : realTabs),
    [newCards, realTabs],
  )
  const fallbackTab = realTabs[0]?.key ?? 'account'

  // The tab lives in the URL rather than in state so a deep link from the command palette lands on
  // the right one, and so the panel holding the target card is mounted by the time the scroll effect
  // below runs. A visible `s` decides the tab on its own, so a link with a stale `tab` still lands.
  const rawTarget = searchParams.get('s')
  const target = rawTarget ? (SETTINGS_ENTRY_ALIASES[rawTarget] ?? rawTarget) : null
  const targetTab = target ? visible.find((e) => e.id === target)?.tab : undefined
  const rawTab = searchParams.get('tab')
  const requested = targetTab ?? (rawTab ? (SETTINGS_TAB_ALIASES[rawTab] ?? rawTab) : null)
  const activeTab = tabs.some((t) => t.key === requested) ? requested! : fallbackTab

  const resolveTab = (search: string) => {
    const params = new URLSearchParams(search)
    const s = params.get('s')
    const sTarget = s ? (SETTINGS_ENTRY_ALIASES[s] ?? s) : null
    const fromTarget = sTarget ? visible.find((e) => e.id === sTarget)?.tab : undefined
    const tabParam = params.get('tab')
    const want = fromTarget ?? (tabParam ? (SETTINGS_TAB_ALIASES[tabParam] ?? tabParam) : null)
    return tabs.some((x) => x.key === want) ? want! : fallbackTab
  }
  const tabEntries = useMemo(
    () =>
      activeTab === NEW_TAB
        ? visible.filter((e) => newCards.has(e.id))
        : visible.filter((e) => e.tab === activeTab && !e.within),
    [visible, activeTab, newCards],
  )
  // The tab rail scrolls sideways on a phone; the open tab is brought into view, not left off the edge.
  const tabListRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const list = tabListRef.current
    const tab = list?.querySelector<HTMLElement>('[data-active]')
    if (!list || !tab || list.scrollWidth <= list.clientWidth) return
    const offset = tab.getBoundingClientRect().left - list.getBoundingClientRect().left
    list.scrollTo({ left: list.scrollLeft + offset - (list.clientWidth - tab.offsetWidth) / 2, behavior: scrollBehavior() })
  }, [activeTab])

  // Panels unmount on a tab change (`keepMounted={false}`), which used to drop half-typed edits
  // without a word. Cards report through SettingsSection; a switch away from unsaved edits asks first.
  const unsaved = useRef(new Set<string>())
  const [unsavedCount, setUnsavedCount] = useState(0)
  const reportUnsaved = useCallback((id: string, dirty: boolean) => {
    if (dirty) unsaved.current.add(id)
    else unsaved.current.delete(id)
    setUnsavedCount(unsaved.current.size)
  }, [])
  const [pendingLeave, setPendingLeave] = useState<(() => void) | null>(null)
  const switchTab = (value: string) => setSearchParams({ tab: value })

  // Any navigation that leaves the page or lands on another tab (sidebar, command palette, Back)
  // goes through the same prompt as the tab strip.
  useLeaveGuard(
    (next: LeaveTarget) => {
      if (unsaved.current.size === 0) return false
      if (next.pathname !== window.location.pathname) return true
      return resolveTab(next.search) !== activeTab
    },
    (proceed) => setPendingLeave(() => proceed),
  )

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

    // A control inside an editor or a row has no anchor of its own; its card does.
    const within = visible.find((e) => e.id === target)?.within
    const el = document.getElementById(`setting-${target}`) ?? (within ? document.getElementById(`setting-${within}`) : null)
    if (!el) return
    const show = () => el.scrollIntoView({ block: 'center', behavior: scrollBehavior() })
    // Cards above the target fill in as their queries resolve (the source table, the indexer list),
    // which pushes it down after the first scroll lands. Re-anchoring twice costs nothing and is
    // what makes a deep link arrive at the card rather than somewhere above it.
    show()
    window.setTimeout(show, 400)
    window.setTimeout(show, 1000)
    el.classList.add('settings-flash')
    window.setTimeout(() => el.classList.remove('settings-flash'), 2200)
  }, [target, awaitingTarget, activeTab, setSearchParams, visible])

  return (
    <SurfaceFrame pageStyle="operational" className="settings-surface">
      <PageHeader
        compact
        title={t`Settings`}
        actions={
          isAdmin && (
            <Button
              variant="default"
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
          if (value && value !== activeTab) switchTab(value)
        }}
        keepMounted={false}
      >
        <Tabs.List ref={tabListRef}>
          {tabs.map((tab) => (
            <Tabs.Tab key={tab.key} value={tab.key}>
              {renderLabel(tab.label)}
            </Tabs.Tab>
          ))}
        </Tabs.List>

        <UnsavedSettingsContext value={reportUnsaved}>
          <NewSettingsContext value={newIds}>
            {tabs.map((tab) => (
              <Tabs.Panel key={tab.key} value={tab.key}>
                <div className="settings-layout">
                  <SettingsJump entries={tabEntries} />
                  <Stack className="settings-content" gap="xl">
                    {tabEntries.map((entry) => (
                      <div key={entry.id} id={`setting-${entry.id}`} className="settings-entry">
                        {tab.key === NEW_TAB && <NewSettingHome card={entry} jumpTo={newCards.get(entry.id)?.jumpTo ?? entry} />}
                        {tab.key === NEW_TAB && !newIds.has(entry.id) ? (
                          <NewSettingFocus nested={newCards.get(entry.id)?.nested ?? []}>{sectionNodes[entry.id]}</NewSettingFocus>
                        ) : (
                          sectionNodes[entry.id]
                        )}
                      </div>
                    ))}
                  </Stack>
                  <SettingsIndex entries={tabEntries} />
                </div>
              </Tabs.Panel>
            ))}
          </NewSettingsContext>
        </UnsavedSettingsContext>
      </Tabs>

      <ConfirmDialog
        opened={pendingLeave !== null}
        onClose={() => setPendingLeave(null)}
        title={t`Discard unsaved changes?`}
        description={
          <Plural
            value={unsavedCount}
            one="A card on this page has changes that are not saved. Leaving drops them."
            other="# cards on this page have changes that are not saved. Leaving drops them."
          />
        }
        confirmLabel={<Trans>Discard changes</Trans>}
        cancelLabel={<Trans>Keep editing</Trans>}
        onConfirm={() => {
          const proceed = pendingLeave
          setPendingLeave(null)
          proceed?.()
        }}
      />
    </SurfaceFrame>
  )
}
