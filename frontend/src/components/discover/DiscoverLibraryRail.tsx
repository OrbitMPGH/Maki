import { useEffect, useEffectEvent, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { randomUUID } from '../../lib/uuid'
import { useNavigate } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { Alert, Badge, Button, Group, SegmentedControl, Stack, Switch, Text, Title, Tooltip } from '@mantine/core'
import { notifications } from '../../lib/toast'
import { IconArrowRight, IconClock, IconEye, IconEyeOff, IconFolder, IconPlus, IconRanking } from '../../lib/icons'
import {
  useAddSeries,
  useLibrarySettings,
  type MangaBakaDetail,
  type RecommendationItem,
} from '../../api/hooks'
import { ApiError, archivedExistsSeriesId } from '../../api/client'
import { useUpgradeProfiles } from '../../api/upgrades'
import { useRootFolderRoute } from '../../api/rootFolderRules'
import { useCreateSeriesRequest, useSeriesRequests, type SeriesRequest } from '../../api/requests'
import { useApplyAnimeResumeAfterAdd } from '../../api/animeResume'
import { useAuth } from '../../auth/AuthProvider'
import type { RootFolder } from '../../api/types'
import { RequestForm } from '../RequestForm'
import { Trans, useLingui } from '@lingui/react/macro'
import { useIncognitoOptions, type IncognitoMode } from '../ui/incognito'
import { useLabel } from '../../i18n-context'
import { INCOGNITO_HELP, MONITOR_HELP, MONITOR_OPTIONS, MONITOR_SHORT } from '../series/SeriesActionsMenu'
import { DrillPanes } from '../ui/DrillPanes'
import { DrillBack, DrillChoices, DrillField, DrillRow, useDrillPages } from '../ui/DrillPanel'
import { formatBytes } from '../../format'
import { RestoreArchivedButton, useIsArchivedSeries } from './ArchivedRestore'

/** The add card's pages: Folder, Incognito and Automation open from the first; Quality from Automation. */
type AddPage = 'main' | 'folder' | 'incognito' | 'automation' | 'quality'
const ADD_PARENTS: Record<AddPage, AddPage> = {
  main: 'main',
  folder: 'main',
  incognito: 'main',
  automation: 'main',
  quality: 'automation',
}
const INSTANCE_DEFAULT = 'default'

/**
 * What this modal can actually do with the series it is showing: add it, ask an admin for it, or
 * take you to the copy you already have. One panel with three faces rather than three layouts, so
 * the rail keeps its footprint whichever one you get.
 *
 * Owns the add and request state outright. The modal mounts it keyed by provider id, so opening a
 * different card remounts it and a half-filled request can't survive into another series.
 */
export function DiscoverLibraryRail({
  item,
  detail,
  inLibrarySeriesId,
  rootFolders,
  onClose,
  addedFrom,
  barSlot,
  onDeepChange,
}: {
  item: RecommendationItem
  /** Undefined until the detail request lands; only the content rating is read from it. */
  detail: MangaBakaDetail | undefined
  /** Library series id if already owned, null/undefined otherwise. */
  inLibrarySeriesId: number | null | undefined
  rootFolders: RootFolder[] | undefined
  onClose: () => void
  addedFrom?: 'recommendation' | 'library'
  /** The modal's collapsed bar. When set, the panel's primary action is portalled into it too. */
  barSlot?: HTMLElement | null
  /** True while a page below the first is open, when the card hangs over the body below the band. */
  onDeepChange?: (deep: boolean) => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const navigate = useNavigate()
  const { can } = useAuth()
  const incognitoOptions = useIncognitoOptions()
  const queryClient = useQueryClient()
  const addSeries = useAddSeries()
  const addMutationId = useRef<string | null>(null)
  const createRequest = useCreateSeriesRequest()
  const applyAnimeResume = useApplyAnimeResumeAfterAdd()
  const { data: librarySettings } = useLibrarySettings()
  const { data: upgradeProfiles } = useUpgradeProfiles()

  // Without AddSeries the same panel asks an admin for the title instead of adding it. The server
  // enforces both halves independently; this only decides which form to draw.
  const canAdd = can('AddSeries')
  const { data: myRequests } = useSeriesRequests('all', !canAdd)

  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  /** Set once the reader picks a folder, so a route that answers late can't overwrite the choice. */
  const [rootFolderPinned, setRootFolderPinned] = useState(false)
  const { data: route } = useRootFolderRoute(
    canAdd && inLibrarySeriesId == null ? item.providerId : null,
    addedFrom ?? 'library',
  )
  const [upgradeProfileId, setUpgradeProfileId] = useState<string | null>(null)
  /**
   * Null until the reader picks a mode, and then nothing is sent, so the server applies the
   * instance's default exactly as it did when this was a plain switch.
   */
  const [monitorMode, setMonitorMode] = useState<string | null>(null)
  /** On by default: the modal only offers it when the reader finished the anime. */
  const [markAnimeWatched, setMarkAnimeWatched] = useState(true)
  /**
   * Null until the content-rating rules have had their say. Choosing a value from the Select pins
   * it, so a rule that resolves late (the detail request carries the rating) can't overwrite a
   * deliberate choice a fast reader already made.
   */
  const [incognito, setIncognito] = useState<IncognitoMode | null>(null)
  const [incognitoPinned, setIncognitoPinned] = useState(false)
  const [chapterStart, setChapterStart] = useState<number | ''>('')
  const [chapterEnd, setChapterEnd] = useState<number | ''>('')
  const [note, setNote] = useState('')
  /**
   * Series id from an add made here, so the button can flip to "Go to series" without navigating.
   * The ['series'] invalidation eventually feeds the same id back via inLibrarySeriesId; this
   * covers the gap until it refetches.
   */
  const [addedSeriesId, setAddedSeriesId] = useState<number | null>(null)
  /** The archived copy an add ran into; the series list refetches, then the panel offers Restore. */
  const [archivedSeriesId, setArchivedSeriesId] = useState<number | null>(null)

  useEffect(() => {
    if (rootFolders && rootFolders.length > 0 && !rootFolderId) {
      setRootFolderId(String(rootFolders[0].id))
    }
  }, [rootFolders, rootFolderId])

  // A root folder rule that matches this series preselects its folder; the reader can still change it.
  const routedFolderId = route?.rootFolderId
  useEffect(() => {
    if (!rootFolderPinned && routedFolderId != null && rootFolders?.some((f) => f.id === routedFolderId)) {
      setRootFolderId(String(routedFolderId))
    }
  }, [routedFolderId, rootFolders, rootFolderPinned])

  // Auto-fill from Settings → Library ("Incognito by content rating"). The rating only arrives with
  // the detail response, so this can't be an initial state value.
  const ratingRule = detail?.contentRating
    ? librarySettings?.incognitoByRating?.[detail.contentRating]
    : undefined
  useEffect(() => {
    if (!incognitoPinned) {
      setIncognito(ratingRule ?? 'Off')
    }
  }, [ratingRule, incognitoPinned])

  const seriesId = inLibrarySeriesId ?? addedSeriesId ?? archivedSeriesId
  const archived = useIsArchivedSeries(seriesId)
  const shownMonitorMode = monitorMode ?? librarySettings?.newSeriesMonitorMode ?? 'All'
  const myRequest = latestRequestFor(
    [...(myRequests ?? []), ...(createRequest.data ? [createRequest.data] : [])],
    item.providerId,
  )
  const resolutionNote = myRequest?.resolutionNote
  const title = detail?.title ?? item.title
  const animeResume = detail?.animeResume ?? null

  const goToLibrary = () => {
    if (seriesId != null) {
      onClose()
      navigate(`/series/${seriesId}`)
    }
  }

  const add = () => {
    if (!rootFolderId) return
    addMutationId.current ??= randomUUID()
    addSeries.mutate(
      {
        metadataProviderId: item.providerId,
        rootFolderId: Number(rootFolderId),
        monitored: monitorMode !== 'None',
        monitorNewItems: monitorMode ?? undefined,
        incognito: incognito ?? 'Off',
        addedFrom: addedFrom ?? 'library',
        clientMutationId: addMutationId.current,
        upgradeProfileId: upgradeProfileId ? Number(upgradeProfileId) : null,
      },
      {
        onSuccess: (series) => {
          // Deliberately stay put: adding used to jump straight to the series page, throwing away
          // the Discover filters the user had set up and making a second add a round trip. The
          // button becomes "Go to series" instead, so leaving is their choice.
          setAddedSeriesId(series.id)
          if (animeResume && markAnimeWatched) applyAnimeResume.mutate({ seriesId: series.id })

          // The series was created either way, so this stays a success, but a failed folder has to
          // be said out loud, not just logged server-side. Source matching is no longer among the
          // warnings — it runs in the background now, and the series page reports on it.
          const warnings = series.warnings ?? []
          notifications.show({
            title: t`Added ${title}`,
            message:
              warnings.length > 0
                ? warnings.join(' ')
                : t`Now in your library. Matching sources in the background.`,
            color: warnings.length > 0 ? 'var(--warn)' : 'var(--ok)',
            autoClose: warnings.length > 0 ? false : undefined,
          })
        },
        onError: (error) => {
          // 410 means this mutation id belongs to an add that committed and was then deleted. The
          // id is sticky so a retry cannot double-add; keeping it after a 410 would make every
          // later press fail the same way, so adding again becomes a genuinely new operation.
          if (error instanceof ApiError && error.status === 410) addMutationId.current = null
          const archivedId = archivedExistsSeriesId(error)
          if (archivedId != null) {
            setArchivedSeriesId(archivedId)
            void queryClient.invalidateQueries({ queryKey: ['series'] })
          }
        },
      },
    )
  }

  const request = () => {
    createRequest.mutate(
      {
        kind: 'NewSeries',
        metadataProviderId: item.providerId,
        chapterStart: chapterStart === '' ? null : chapterStart,
        chapterEnd: chapterEnd === '' ? null : chapterEnd,
        note: note.trim() || null,
      },
      {
        onSuccess: () => {
          notifications.show({
            title: t`Requested ${title}`,
            message: t`An admin will see it on the Requests page.`,
            color: 'var(--ok)',
          })
        },
      },
    )
  }

  const pages = useDrillPages<AddPage>('main', ADD_PARENTS)
  const panelRef = useRef<HTMLDivElement>(null)
  const deep = pages.depth > 0
  const pageBack = useEffectEvent(() => pages.back())
  const pageReset = useEffectEvent(() => pages.reset())

  // Layout, not passive: the modal has to know before the card starts growing, or it takes the first
  // frame of the new page's height for the band's.
  useLayoutEffect(() => {
    onDeepChange?.(deep)
  }, [deep, onDeepChange])
  useEffect(() => () => onDeepChange?.(false), [onDeepChange])

  // A deeper page hangs over the synopsis like a popover, so it leaves like one: Escape steps back a
  // page instead of closing the modal, and a press or focus anywhere else folds the card up.
  useEffect(() => {
    if (!deep) return
    const card = panelRef.current?.closest('.discover-action-card') ?? panelRef.current
    const outside = (e: Event) => {
      if (card && e.target instanceof Node && !card.contains(e.target)) pageReset()
    }
    const escape = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !e.defaultPrevented) pageBack()
    }
    document.addEventListener('pointerdown', outside, true)
    document.addEventListener('focusin', outside, true)
    window.addEventListener('keydown', escape)
    return () => {
      document.removeEventListener('pointerdown', outside, true)
      document.removeEventListener('focusin', outside, true)
      window.removeEventListener('keydown', escape)
    }
  }, [deep])
  const incognitoLabel = incognitoOptions.find((o) => o.value === (incognito ?? 'Off'))?.label
  const profileName = upgradeProfiles?.find((p) => String(p.id) === upgradeProfileId)?.name
  const monitorShort = renderLabel(MONITOR_SHORT[shownMonitorMode] ?? shownMonitorMode)
  const automationValue = profileName ? t`${monitorShort}, ${profileName}` : monitorShort

  // The bar repeats only a one-press action. A request needs its form, so it stays up in the card.
  const barAction =
    seriesId != null ? (
      <Button size="sm" variant="light" rightSection={<IconArrowRight size={16} />} onClick={goToLibrary}>
        {addedSeriesId != null ? <Trans>Go to series</Trans> : <Trans>View in library</Trans>}
      </Button>
    ) : canAdd && rootFolders !== undefined && rootFolders.length > 0 ? (
      <Button
        className="discover-bar-add"
        size="sm"
        leftSection={<IconPlus size={16} />}
        onClick={add}
        loading={addSeries.isPending}
        disabled={!rootFolderId}
      >
        <Trans>Add to library</Trans>
      </Button>
    ) : null

  return (
    <div className="discover-add-panel" ref={panelRef}>
      {barSlot && barAction && createPortal(barAction, barSlot)}
      {seriesId != null && archived ? (
        <>
          <Group gap="sm" justify="space-between" wrap="nowrap">
            <Title order={2} size="h3" fz="var(--type-subhead)">
              <Trans>In your library</Trans>
            </Title>
            <Badge color="gray" variant="light">
              <Trans context="series">Archived</Trans>
            </Badge>
          </Group>
          <Stack gap="xs" mt="md">
            <RestoreArchivedButton seriesId={seriesId} fullWidth />
            <Button
              fullWidth
              variant="default"
              rightSection={<IconArrowRight size={16} />}
              onClick={goToLibrary}
            >
              <Trans>View series</Trans>
            </Button>
          </Stack>
        </>
      ) : seriesId != null ? (
        <>
          <Title order={2} size="h3" fz="var(--type-subhead)">
            <Trans>In your library</Trans>
          </Title>
          <Button
            mt="md"
            fullWidth
            variant="light"
            rightSection={<IconArrowRight size={16} />}
            onClick={goToLibrary}
          >
            {addedSeriesId != null ? <Trans>Go to series</Trans> : <Trans>View in library</Trans>}
          </Button>
        </>
      ) : canAdd && !can('Admin') && rootFolders !== undefined && rootFolders.length === 0 ? (
        // AddSeries lets someone create a series, but only into a root folder they were granted
        // under Settings, Users. With no grant there is nothing to point the add at. Say so rather
        // than leaving a dead Select and a disabled button.
        <>
          <Title order={2} size="h3" fz="var(--type-subhead)">
            <Trans>Add to library</Trans>
          </Title>
          <Alert color="var(--warn)" variant="light" mt="md">
            <Trans>
              You can add series, but no root folder has been granted to you yet. Ask an admin to
              grant you one, or to add this title.
            </Trans>
          </Alert>
        </>
      ) : canAdd ? (
        // A drill panel like the series menu's: the settings people check on every add (where it
        // goes, whether it stays private) are rows on the first page, and the ones usually left
        // alone (monitoring, the quality profile) sit one level down under Automation.
        <Stack gap={0}>
          <div className="discover-add-pages">
            <DrillPanes page={pages.page} depth={pages.depth} bleed={6}>
              {pages.page === 'main' && (
                <>
                  <DrillRow
                    ref={pages.rowRef('folder')}
                    icon={IconFolder}
                    label={<Trans>Folder</Trans>}
                    value={rootFolders?.find((f) => String(f.id) === rootFolderId)?.path}
                    onClick={() => pages.open('folder')}
                  />
                  <DrillRow
                    ref={pages.rowRef('incognito')}
                    icon={IconEyeOff}
                    label={<Trans>Incognito</Trans>}
                    value={incognitoLabel}
                    onClick={() => pages.open('incognito')}
                  />
                  <DrillRow
                    ref={pages.rowRef('automation')}
                    icon={IconEye}
                    label={<Trans>Automation</Trans>}
                    value={automationValue}
                    onClick={() => pages.open('automation')}
                  />
                  {animeResume && (
                    <div className="discover-add-switch">
                      <AnimeWatchedSwitch
                        coveredTo={animeResume.coveredTo}
                        checked={markAnimeWatched}
                        onChange={setMarkAnimeWatched}
                      />
                    </div>
                  )}
                </>
              )}
              {pages.page === 'folder' && (
                <>
                  <DrillBack ref={pages.backRef} title={<Trans>Folder</Trans>} onBack={pages.back} />
                  <div className="drill-scroll">
                    <DrillChoices
                      label={t`Folder`}
                      value={rootFolderId ?? ''}
                      options={(rootFolders ?? []).map((f) => {
                        const free = formatBytes(f.freeSpace)
                        return {
                          value: String(f.id),
                          label: f.path,
                          hint: !f.accessible
                            ? t`Not reachable right now`
                            : f.freeSpace != null
                              ? t`${free} free`
                              : undefined,
                        }
                      })}
                      onPick={(value) => {
                        setRootFolderId(value)
                        setRootFolderPinned(true)
                        pages.back()
                      }}
                    />
                  </div>
                </>
              )}
              {pages.page === 'incognito' && (
                <>
                  <DrillBack ref={pages.backRef} title={<Trans>Incognito</Trans>} onBack={pages.back} />
                  <DrillChoices
                    label={t`Incognito`}
                    value={incognito ?? 'Off'}
                    options={incognitoOptions.map(({ value, label }) => ({
                      value,
                      label,
                      hint: INCOGNITO_HELP[value] && renderLabel(INCOGNITO_HELP[value]),
                    }))}
                    onPick={(value) => {
                      setIncognitoPinned(true)
                      setIncognito(value as IncognitoMode)
                      pages.back()
                    }}
                  />
                  {/* Said where the choice is made, so a pre-filled mode is not a mystery. */}
                  {!incognitoPinned && ratingRule && ratingRule !== 'Off' && (
                    <Text size="xs" c="var(--ink-3)" px={10} pt={4} pb={6}>
                      <Trans>Picked by your content rating rule in Settings.</Trans>
                    </Text>
                  )}
                </>
              )}
              {pages.page === 'automation' && (
                <>
                  <DrillBack ref={pages.backRef} title={<Trans>Automation</Trans>} onBack={pages.back} />
                  <DrillField
                    icon={IconEye}
                    title={<Trans>Monitor</Trans>}
                    help={MONITOR_HELP[shownMonitorMode] && renderLabel(MONITOR_HELP[shownMonitorMode])}
                  >
                    <SegmentedControl
                      aria-label={t`Monitor`}
                      fullWidth
                      size="xs"
                      value={shownMonitorMode}
                      onChange={setMonitorMode}
                      data={MONITOR_OPTIONS.map(({ value }) => ({ value, label: renderLabel(MONITOR_SHORT[value]) }))}
                    />
                  </DrillField>
                  <DrillRow
                    ref={pages.rowRef('quality')}
                    icon={IconRanking}
                    label={<Trans>Quality profile</Trans>}
                    value={profileName ?? t`Instance default`}
                    onClick={() => pages.open('quality')}
                  />
                </>
              )}
              {pages.page === 'quality' && (
                <>
                  <DrillBack ref={pages.backRef} title={<Trans>Quality profile</Trans>} onBack={pages.back} />
                  <div className="drill-scroll">
                    <DrillChoices
                      label={t`Quality profile`}
                      value={upgradeProfileId ?? INSTANCE_DEFAULT}
                      options={[
                        {
                          value: INSTANCE_DEFAULT,
                          label: t`Instance default`,
                          hint: t`Follows the default profile set in Settings.`,
                        },
                        ...(upgradeProfiles ?? []).map((p) => ({
                          value: String(p.id),
                          label: p.name,
                          hint: p.description ?? undefined,
                        })),
                      ]}
                      onPick={(value) => {
                        setUpgradeProfileId(value === INSTANCE_DEFAULT ? null : value)
                        pages.back()
                      }}
                    />
                  </div>
                </>
              )}
            </DrillPanes>
          </div>
          <Button
            fullWidth
            mt="sm"
            size="md"
            leftSection={<IconPlus size={18} />}
            onClick={add}
            loading={addSeries.isPending}
            disabled={!rootFolderId}
          >
            <Trans>Add to library</Trans>
          </Button>
        </Stack>
      ) : myRequest && (myRequest.status === 'Pending' || myRequest.status === 'Processing') ? (
        <>
          <Title order={2} size="h3" fz="var(--type-subhead)">
            <Trans>Requested</Trans>
          </Title>
          <Alert color="var(--warn)" variant="light" icon={<IconClock size={16} />} mt="md">
            <Trans>An admin decides where it lands and what gets downloaded.</Trans>
          </Alert>
        </>
      ) : (
        <>
          <Title order={2} size="h3" fz="var(--type-subhead)">
            <Trans>Ask for this</Trans>
          </Title>
          <Text size="xs" c="var(--ink-4)" mt={6} style={{ lineHeight: 1.55 }}>
            <Trans>
              You can't add series yourself. An admin decides where it lands and what gets
              downloaded.
            </Trans>
          </Text>
          {myRequest?.status === 'Rejected' && (
            <Alert color="var(--warn)" variant="light" mt="sm">
              {resolutionNote ? (
                <Trans>Your last request was rejected: {resolutionNote}</Trans>
              ) : (
                <Trans>Your last request was rejected.</Trans>
              )}
            </Alert>
          )}
          <RequestForm
            dense
            chapterStart={chapterStart}
            chapterEnd={chapterEnd}
            note={note}
            onChapterStart={setChapterStart}
            onChapterEnd={setChapterEnd}
            onNote={setNote}
            onSubmit={request}
            pending={createRequest.isPending}
          />
        </>
      )}
    </div>
  )
}

/**
 * The caller's newest new-series request for a title. One still awaiting an answer outranks a newer
 * rejected one, since that is the row that blocks filing another.
 */
function latestRequestFor(requests: SeriesRequest[], providerId: string): SeriesRequest | undefined {
  const mine = requests.filter((r) => r.kind === 'NewSeries' && r.metadataProviderId === providerId)
  const newest = (rows: SeriesRequest[]) =>
    rows.reduce<SeriesRequest | undefined>((best, r) => (best && best.id > r.id ? best : r), undefined)
  return newest(mine.filter((r) => r.status === 'Pending' || r.status === 'Processing')) ?? newest(mine)
}

function AnimeWatchedSwitch({
  coveredTo,
  checked,
  onChange,
}: {
  coveredTo: number
  checked: boolean
  onChange: (checked: boolean) => void
}) {
  const { t } = useLingui()
  return (
    <Tooltip
      label={t`Ticks them off as soon as the chapter list arrives. Watched chapters don't count toward your reading stats.`}
      withArrow
      multiline
      w={260}
      zIndex={1001}
    >
      <Switch
        label={t`Mark ch. 1 to ${coveredTo} watched`}
        checked={checked}
        onChange={(e) => onChange(e.currentTarget.checked)}
        labelPosition="left"
        size="sm"
        styles={{ body: { justifyContent: 'space-between' } }}
      />
    </Tooltip>
  )
}
