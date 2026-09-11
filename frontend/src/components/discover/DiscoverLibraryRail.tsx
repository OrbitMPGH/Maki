import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Alert, Button, Paper, Select, Stack, Switch, Text, Title, Tooltip } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconArrowRight, IconCheck, IconEyeOff, IconFolder, IconPlus } from '@tabler/icons-react'
import {
  useAddSeries,
  useLibrarySettings,
  type MangaBakaDetail,
  type RecommendationItem,
} from '../../api/hooks'
import { useCreateSeriesRequest } from '../../api/requests'
import { useAuth } from '../../auth/AuthProvider'
import type { RootFolder } from '../../api/types'
import { RequestForm } from '../RequestForm'
import { INCOGNITO_OPTIONS, type IncognitoMode } from '../ui/incognito'

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
}: {
  item: RecommendationItem
  /** Undefined until the detail request lands; only the content rating is read from it. */
  detail: MangaBakaDetail | undefined
  /** Library series id if already owned, null/undefined otherwise. */
  inLibrarySeriesId: number | null | undefined
  rootFolders: RootFolder[] | undefined
  onClose: () => void
}) {
  const navigate = useNavigate()
  const { can } = useAuth()
  const addSeries = useAddSeries()
  const createRequest = useCreateSeriesRequest()
  const { data: librarySettings } = useLibrarySettings()

  // Without AddSeries the same panel asks an admin for the title instead of adding it. The server
  // enforces both halves independently; this only decides which form to draw.
  const canAdd = can('AddSeries')

  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [monitored, setMonitored] = useState(true)
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
  const [requested, setRequested] = useState(false)
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
  const title = detail?.title ?? item.title

  const goToLibrary = () => {
    if (seriesId != null) {
      onClose()
      navigate(`/series/${seriesId}`)
    }
  }

  const add = () => {
    if (!rootFolderId) return
    addSeries.mutate(
      {
        metadataProviderId: item.providerId,
        rootFolderId: Number(rootFolderId),
        monitored,
        monitorNewItems: monitored ? 'All' : 'None',
        incognito: incognito ?? 'Off',
      },
      {
        onSuccess: (series) => {
          // Deliberately stay put: adding used to jump straight to the series page, throwing away
          // the Discover filters the user had set up and making a second add a round trip. The
          // button becomes "Go to series" instead, so leaving is their choice.
          setAddedSeriesId(series.id)

          // The series was created either way, so this stays a success, but a failed folder has to
          // be said out loud, not just logged server-side. Source matching is no longer among the
          // warnings — it runs in the background now, and the series page reports on it.
          const warnings = series.warnings ?? []
          notifications.show({
            title: `Added ${title}`,
            message:
              warnings.length > 0
                ? warnings.join(' ')
                : 'Now in your library. Matching sources in the background.',
            color: warnings.length > 0 ? 'yellow' : 'green',
            autoClose: warnings.length > 0 ? false : undefined,
          })
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
          setRequested(true)
          notifications.show({
            title: `Requested ${title}`,
            message: 'An admin will see it on the Requests page.',
            color: 'green',
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
      style={{ alignSelf: 'end' }}
    >
      {seriesId != null ? (
        <>
          <Title order={3} fz={16}>
            In your library
          </Title>
          <Button
            mt="md"
            fullWidth
            color="teal"
            variant="light"
            rightSection={<IconArrowRight size={16} />}
            onClick={goToLibrary}
          >
            {addedSeriesId != null ? 'Go to series' : 'View in library'}
          </Button>
        </>
      ) : canAdd && !can('Admin') && (rootFolders?.length ?? 0) === 0 ? (
        // AddSeries lets someone create a series, but the root folder list is admin-only (it
        // discloses the host's directory layout), so a non-admin has nothing to point the add at.
        // Say so rather than leaving a dead Select and a disabled button.
        <>
          <Title order={3} fz={16}>
            Add to library
          </Title>
          <Alert color="yellow" variant="light" mt="md">
            You can add series, but only an admin can choose a root folder. Ask one to add this
            title, or to grant you admin.
          </Alert>
        </>
      ) : canAdd ? (
        // Every control here carries its own meaning, so none of them gets a field label: the path
        // reads as a path, the switch says Monitor, and the incognito select spells its own state.
        // This panel sits inside the band beside the title, and four labels' worth of height is
        // what pushed the band past the poster.
        <Stack gap={10}>
          {/* A root folder is an absolute host path, so it gets the panel's full width and still
              truncates; the dropdown is where the whole path is legible. */}
          <Select
            aria-label="Root folder"
            placeholder="Root folder"
            leftSection={<IconFolder size={15} />}
            data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
            value={rootFolderId}
            onChange={setRootFolderId}
            size="xs"
            comboboxProps={{ zIndex: 1001, width: 340, position: 'bottom-end' }}
          />
          {/* Pre-filled from the content-rating rules, so an explicit pick here is the exception
              rather than something to remember on every add. */}
          <Tooltip
            label="Keeps this series out of tracker pushes, and out of stats entirely on Full."
            withArrow
            zIndex={1001}
          >
            <Select
              aria-label="Incognito"
              leftSection={<IconEyeOff size={15} />}
              data={INCOGNITO_OPTIONS.map((o) => ({
                value: o.value,
                label: `Incognito: ${o.label.toLowerCase()}`,
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
          <Tooltip label="Fetch new chapters as they land" withArrow zIndex={1001}>
            <Switch
              label="Monitor"
              checked={monitored}
              onChange={(e) => setMonitored(e.currentTarget.checked)}
              labelPosition="left"
              size="sm"
              styles={{ body: { justifyContent: 'space-between' } }}
            />
          </Tooltip>
          <Button
            fullWidth
            mt={2}
            leftSection={<IconPlus size={16} />}
            onClick={add}
            loading={addSeries.isPending}
            disabled={!rootFolderId}
          >
            Add
          </Button>
        </Stack>
      ) : requested ? (
        <>
          <Title order={3} fz={16}>
            Requested
          </Title>
          <Alert color="green" variant="light" icon={<IconCheck size={16} />} mt="md">
            An admin decides where it lands and what gets downloaded.
          </Alert>
        </>
      ) : (
        <>
          <Title order={3} fz={16}>
            Ask for this
          </Title>
          <Text size="xs" c="var(--ink-4)" mt={6} style={{ lineHeight: 1.55 }}>
            You can't add series yourself. An admin decides where it lands and what gets
            downloaded.
          </Text>
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

      {addSeries.isError && (
        <Alert color="red" variant="light" mt="sm">
          {String(addSeries.error)}
        </Alert>
      )}
      {createRequest.isError && (
        <Alert color="red" variant="light" mt="sm">
          {String(createRequest.error)}
        </Alert>
      )}
    </Paper>
  )
}
