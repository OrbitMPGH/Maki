import { useEffect, useRef, useState } from 'react'
import { randomUUID } from '../../lib/uuid'
import { useNavigate } from 'react-router-dom'
import { Alert, Button, Paper, Select, Stack, Switch, Text, Title, Tooltip } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconArrowRight, IconBell, IconCheck, IconEyeOff, IconFolder, IconPlus } from '@tabler/icons-react'
import {
  useAddSeries,
  useLibrarySettings,
  type MangaBakaDetail,
  type RecommendationItem,
} from '../../api/hooks'
import { ApiError } from '../../api/client'
import { useUpgradeProfiles } from '../../api/upgrades'
import { useCreateSeriesRequest, useSeriesRequests, type SeriesRequest } from '../../api/requests'
import { useApplyAnimeResumeAfterAdd } from '../../api/animeResume'
import { useAuth } from '../../auth/AuthProvider'
import type { RootFolder } from '../../api/types'
import { RequestForm } from '../RequestForm'
import { Trans, useLingui } from '@lingui/react/macro'
import { useIncognitoOptions, type IncognitoMode } from '../ui/incognito'
import { useLabel } from '../../i18n-context'
import { MONITOR_OPTIONS } from '../series/SeriesActionsMenu'

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
}: {
  item: RecommendationItem
  /** Undefined until the detail request lands; only the content rating is read from it. */
  detail: MangaBakaDetail | undefined
  /** Library series id if already owned, null/undefined otherwise. */
  inLibrarySeriesId: number | null | undefined
  rootFolders: RootFolder[] | undefined
  onClose: () => void
  addedFrom?: 'recommendation' | 'library'
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const navigate = useNavigate()
  const { can } = useAuth()
  const incognitoOptions = useIncognitoOptions()
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

  useEffect(() => {
    if (rootFolders && rootFolders.length > 0 && !rootFolderId) {
      setRootFolderId(String(rootFolders[0].id))
    }
  }, [rootFolders, rootFolderId])

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

  const seriesId = inLibrarySeriesId ?? addedSeriesId
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

  return (
    <Paper
      withBorder
      radius="lg"
      p="md"
      className="series-hero-glass-panel"
    >
      {seriesId != null ? (
        <>
          <Title order={3} fz="var(--type-subhead)">
            <Trans>In your library</Trans>
          </Title>
          <Button
            mt="md"
            fullWidth
            color="var(--ok)"
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
          <Title order={3} fz="var(--type-subhead)">
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
        // Every control here carries its own meaning, so none of them gets a field label: the path
        // reads as a path, and the monitor and incognito selects spell their own state.
        // This panel sits inside the band beside the title, and four labels' worth of height is
        // what pushed the band past the poster.
        <Stack gap={10}>
          {/* A root folder is an absolute host path, so it gets the panel's full width and still
              truncates; the dropdown is where the whole path is legible. */}
          <Select
            aria-label={t`Root folder`}
            placeholder={t`Root folder`}
            leftSection={<IconFolder size={15} />}
            data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
            value={rootFolderId}
            onChange={setRootFolderId}
            size="xs"
            comboboxProps={{ zIndex: 1001, width: 340, position: 'bottom-end' }}
          />
          <Select
            aria-label={t`Quality profile`}
            placeholder={t`Instance default`}
            data={(upgradeProfiles ?? []).map((p) => ({ value: String(p.id), label: p.name }))}
            value={upgradeProfileId}
            onChange={setUpgradeProfileId}
            clearable
            size="xs"
            comboboxProps={{ zIndex: 1001, position: 'bottom-end' }}
          />
          {/* Pre-filled from the content-rating rules, so an explicit pick here is the exception
              rather than something to remember on every add. */}
          <Tooltip
            label={t`Keeps this series out of tracker pushes, and out of stats entirely on Full.`}
            withArrow
            zIndex={1001}
          >
            <Select
              aria-label={t`Incognito`}
              leftSection={<IconEyeOff size={15} />}
              data={incognitoOptions.map(({ value, label: mode }) => ({
                value,
                // Deliberately not lower-cased. German capitalises nouns and Turkish has two
                // different i's, so case-folding a translated label damages it. Named rather than
                // `o.label` so the placeholder extracts as {mode} instead of {0}.
                label: t`Incognito: ${mode}`,
              }))}
              value={incognito ?? 'Off'}
              onChange={(value) => {
                setIncognitoPinned(true)
                setIncognito((value as IncognitoMode | null) ?? 'Off')
              }}
              size="xs"
              comboboxProps={{ zIndex: 1001 }}
            />
          </Tooltip>
          <Tooltip label={t`Which new chapters get fetched as they land`} withArrow zIndex={1001}>
            <Select
              aria-label={t`Monitor`}
              leftSection={<IconBell size={15} />}
              data={MONITOR_OPTIONS.map(({ value, label }) => {
                const mode = renderLabel(label)
                return { value, label: t`Monitor: ${mode}` }
              })}
              value={shownMonitorMode}
              onChange={(value) => value && setMonitorMode(value)}
              allowDeselect={false}
              size="xs"
              comboboxProps={{ zIndex: 1001 }}
            />
          </Tooltip>
          {animeResume && (
            <AnimeWatchedSwitch
              coveredTo={animeResume.coveredTo}
              checked={markAnimeWatched}
              onChange={setMarkAnimeWatched}
            />
          )}
          <Button
            fullWidth
            mt={2}
            leftSection={<IconPlus size={16} />}
            onClick={add}
            loading={addSeries.isPending}
            disabled={!rootFolderId}
          >
            <Trans>Add</Trans>
          </Button>
        </Stack>
      ) : myRequest && myRequest.status !== 'Rejected' ? (
        <>
          <Title order={3} fz="var(--type-subhead)">
            {myRequest.status === 'Approved' ? <Trans>Approved</Trans> : <Trans>Requested</Trans>}
          </Title>
          <Alert color="var(--ok)" variant="light" icon={<IconCheck size={16} />} mt="md">
            {myRequest.status === 'Approved' ? (
              <Trans>An admin approved your request.</Trans>
            ) : (
              <Trans>An admin decides where it lands and what gets downloaded.</Trans>
            )}
          </Alert>
        </>
      ) : (
        <>
          <Title order={3} fz="var(--type-subhead)">
            <Trans>Ask for this</Trans>
          </Title>
          <Text size="xs" c="var(--ink-4)" mt={6} style={{ lineHeight: 1.55 }}>
            <Trans>
              You can't add series yourself. An admin decides where it lands and what gets
              downloaded.
            </Trans>
          </Text>
          {myRequest && (
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

    </Paper>
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
