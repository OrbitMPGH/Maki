import {type ReactNode, useMemo} from 'react'
import {
    ActionIcon,
    Alert,
    Box,
    Divider,
    Group,
    Paper,
    Progress,
    Rating,
    Stack,
    Text,
    Title,
    Tooltip
} from '@mantine/core'
import {IconAlertTriangle, IconArrowLeft, IconBook, IconDownload, IconX} from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { SeriesDto } from '../../api/types'
import {contentRatingVisual, seriesProgressVisual, seriesStatusVisual} from '../ui/status'
import {useReadTracking} from "../../api/reader.ts";
import {useChapters} from "../../api/hooks.ts";

/**
 * status.tsx speaks in Mantine palette names; the tokens speak in meanings. One map, here, rather
 * than a class per colour, so a new slot cannot be added without also being named.
 */
const STATUS_TOKEN: Record<string, string> = {
    teal: 'ok',
    yellow: 'warn',
    blue: 'info',
    red: 'danger',
    violet: 'watched',
    gray: 'neutral',
}

/**
 * Content ratings follow a green, yellow-green, yellow, red progression. Keep Suggestive's
 * token separate from the purple used for watched state.
 */
const NSFW_TOKEN: Record<string, string> = {
    safe: 'ok',
    suggestive: 'suggestive',
    erotica: 'warn',
    pornographic: 'danger',
}

/**
 * The masthead of a series page: the art, the poster, the identity, and the row of actions and
 * tabs that sit under it.
 *
 * Deliberately presentational. `actions` and `tabs` arrive as nodes so every mutation, permission
 * check and modal stays in SeriesDetailPage, which is what keeps this file readable while the page
 * it serves is not.
 *
 * The backdrop is the series' own poster filled to the band and lightly blurred, with a corner
 * falloff and two scrims over it rather than one flat wash. The recipe, and the reason a flat wash
 * looks like a smudge, are in .claude/rules/design-system.md.
 */
export function SeriesHero({
                               series,
                               onRate,
                               actions,
                               tabs,
                           }: {
    series: SeriesDto
    onRate: (value: number | null) => void
    actions: ReactNode
    tabs: ReactNode
}) {
    const readTracking = useReadTracking()
    const status = seriesStatusVisual(series.status)
    const contentRating = contentRatingVisual(series.contentRating)
    const ratingToken = series.contentRating ? NSFW_TOKEN[series.contentRating] : undefined
    const author = series.authorStory ?? series.authorArt
    const { data: chapters } = useChapters(series.id)

    /**
     * How far the linked sources fall short of the chapter count MangaBaka reports.
     *
     * Without this a series reads "41 / 41" once every chapter the sources carry is downloaded,
     * which looks finished, so it's easy to unmonitor a series that's actually missing its tail.
     * The gap is deliberately kept out of the progress fraction: those chapters can't be fetched
     * from the linked sources, so counting them would just make the bar unreachable instead.
     *
     * Compared by highest chapter NUMBER, never the row count: sources list specials and one-shots
     * MangaBaka doesn't count, so a count reads "ahead" (365 rows against a reported 119) on a
     * series that is really three chapters short.
     */
    const sourceGap = useMemo(() => {
        const total = series?.totalChapters
        const numbered = (chapters ?? []).map((c) => c.number).filter((n): n is number => n !== null)
        if (!total || numbered.length === 0) return null

        const highest = Math.max(...numbered)
        if (highest >= total) return null

        return { highest, total, missing: Math.floor(total - highest) }
    }, [series, chapters])

    // What "Download all wanted" would actually queue, so the button can say so rather than making
    // the user open the Chapters tab to find out.
    const missingWanted = useMemo(
        () => (chapters ?? []).filter((c) => c.wanted && !c.hasFile).length,
        [chapters],
    )

    const progress = useMemo(
        () =>
            seriesProgressVisual(
                series ?? { wantedChapterCount: 0, knownChapterCount: 0, chapterFileCount: 0, readChapterCount: null },
                readTracking,
            ),
        [series, readTracking],
    )

    // One quiet line of facts rather than a row of coloured pills: none of these is a state anyone
    // acts on, so none of them earns a colour.
    const facts = [
        series.type,
        series.year ? String(series.year) : null,
        series.hasAnime ? (series.animeName ?? 'Anime adaptation') : null,
        series.genres.slice(0, 5).join(', ') || null,
    ].filter(Boolean)

    const altTitles = [series.originalTitle, ...series.altTitles].filter(
        (t): t is string => !!t && t !== series.title,
    )

    return (
        <Box className="series-hero">
            {series.coverUrl && (
                <div
                    className="series-hero-art"
                    style={{ backgroundImage: `url(${series.coverUrl})` }}
                    aria-hidden
                />
            )}
            <div className="series-hero-falloff" aria-hidden />
            <div className="series-hero-scrim-x" aria-hidden />
            <div className="series-hero-scrim-y" aria-hidden />

            <div className="series-hero-body">
                {/* Arrow inside the link, not beside it: the arrow is the part of this people aim at. */}
                <Text
                    component={Link}
                    to="/library"
                    className="series-hero-back"
                    mb="md"
                    size="sm"
                    fw={600}
                >
                    <IconArrowLeft size={16} stroke={1.9} />
                    Library
                </Text>

                <Group className={"series-hero-content"}>

                    <Group align="flex-start" gap={32} wrap="nowrap" className="series-hero-row">
                        {series.coverUrl && (
                            <img
                                className="series-hero-poster"
                                src={series.coverUrl}
                                alt={series.title}
                            />
                        )}

                        <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                            <Title order={1} className="series-hero-title">
                                {series.title}
                            </Title>

                            {altTitles.length > 0 && (
                                <Text size="sm" pt="xs" c="var(--ink-3)">
                                    {altTitles.join(' · ')}
                                </Text>
                            )}

                            {author && (
                                <Text size="lg" fw={500} c="var(--ink-3)" mt={7}>
                                    {author}
                                </Text>
                            )}

                            <Group gap="md" mt={15} wrap="wrap">
                                {/* Status and rating are one cluster at a tighter gap, so they read as two facts
                  about the same thing rather than as two separate items in the row. */}
                                <Group gap={8} wrap="nowrap">
                <span
                    className="series-hero-status"
                    style={{
                        color: `var(--${STATUS_TOKEN[status.color] ?? 'neutral'})`,
                        background: `var(--${STATUS_TOKEN[status.color] ?? 'neutral'}-soft)`,
                    }}
                >
                  <status.Icon size={14} />
                    {status.label}
                </span>

                                    {contentRating && (
                                        <Tooltip label="Content rating" withArrow>
                    <span
                        className="series-hero-status"
                        data-quiet={ratingToken ? undefined : true}
                        style={
                            ratingToken
                                ? {
                                    color: `var(--${ratingToken})`,
                                    background: `var(--${ratingToken}-soft)`,
                                }
                                : undefined
                        }
                    >
                      <contentRating.Icon size={14} />
                        {contentRating.label}
                    </span>
                                        </Tooltip>
                                    )}
                                </Group>

                                <Group gap={8} wrap="nowrap">
                                    <Rating
                                        count={5}
                                        fractions={2}
                                        value={series.rating ? series.rating / 2 : 0}
                                        onChange={(v) => onRate(Math.round(v * 2) || null)}
                                    />
                                    {series.rating ? (
                                        <>
                                            <Text size="sm" fw={600} c="var(--ink-2)" className="tnum">
                                                {series.rating}/10
                                            </Text>
                                            <Tooltip label="Clear rating" withArrow>
                                                <ActionIcon
                                                    size="sm"
                                                    variant="subtle"
                                                    color="gray"
                                                    onClick={() => onRate(null)}
                                                    aria-label="Clear rating"
                                                >
                                                    <IconX size={14} />
                                                </ActionIcon>
                                            </Tooltip>
                                        </>
                                    ) : (
                                        <Text size="sm" c="var(--ink-4)">
                                            Not rated
                                        </Text>
                                    )}
                                </Group>
                            </Group>

                            {facts.length > 0 && (
                                <Text size="sm" c="var(--ink-4)" mt={9}>
                                    {facts.join(' · ')}
                                </Text>
                            )}

                            <Group gap="xs" mt="lg" wrap="wrap">
                                {actions}
                            </Group>

                            <Box mt="xl">{tabs}</Box>
                        </Stack>
                    </Group>
                    <Paper withBorder radius="lg" p="lg">
                        <Title order={3} fz={17}>
                            Progress
                        </Title>

                        {readTracking && series.readChapterCount != null && progress.have > 0 && (
                            <Box mt="md">
                                <Group gap={9} c="var(--ink-3)">
                                    <IconBook size={17} />
                                    <Text size="sm" fw={600} c="var(--ink)">
                                        Reading
                                    </Text>
                                </Group>
                                <Progress
                                    mt={12}
                                    value={Math.min(100, (series.readChapterCount / progress.have) * 100)}
                                    color="brand"
                                    radius="xl"
                                />
                                <Group justify="space-between" mt={9}>
                                    <Text size="sm" c="var(--ink-2)" className="tnum">
                                        {series.readChapterCount} / {progress.have} chapters
                                    </Text>
                                    <Text size="sm" fw={600} c="var(--ink-2)" className="tnum">
                                        {Math.round((series.readChapterCount / progress.have) * 100)}%
                                    </Text>
                                </Group>
                                <Divider my="md" color="var(--hairline)" />
                            </Box>
                        )}

                        <Box mt="md">
                            <Group gap={9} c="var(--ink-3)">
                                <IconDownload size={17} />
                                <Text size="sm" fw={600} c="var(--ink)">
                                    Downloads
                                </Text>
                            </Group>
                            <Progress
                                mt={12}
                                value={progress.pct}
                                // Never green while the sources are short of the full run: "all downloaded" and
                                // "you have the whole series" are different claims, and the green tick is exactly
                                // what makes someone unmonitor a series that's still missing its tail.
                                color={sourceGap ? 'yellow' : progress.complete ? 'teal' : 'blue'}
                                radius="xl"
                            />
                            <Group justify="space-between" mt={9}>
                                <Text size="sm" c="var(--ink-2)" className="tnum">
                                    {progress.have} / {progress.total} chapters
                                    {progress.nothingWanted && ' listed, none wanted'}
                                </Text>
                                <Text size="sm" fw={600} c="var(--ink-2)" className="tnum">
                                    {Math.round(progress.pct)}%
                                </Text>
                            </Group>
                            {missingWanted > 0 && (
                                <Text size="xs" c="var(--ink-4)" mt={7} className="tnum">
                                    {missingWanted} wanted, not fetched
                                </Text>
                            )}
                        </Box>

                        {sourceGap && (
                            <Alert
                                mt="md"
                                color="yellow"
                                variant="light"
                                radius="md"
                                icon={<IconAlertTriangle size={16} />}
                            >
                                <Text size="xs" c="var(--ink-3)" style={{ lineHeight: 1.55 }}>
                                    Your sources only reach chapter{' '}
                                    <Text span fw={600} c="var(--ink)" className="tnum">
                                        {sourceGap.highest}
                                    </Text>
                                    , but MangaBaka lists{' '}
                                    <Text span fw={600} c="var(--ink)" className="tnum">
                                        {sourceGap.total}
                                    </Text>
                                    . Roughly {sourceGap.missing} chapter{sourceGap.missing === 1 ? '' : 's'} can&apos;t
                                    be downloaded from the sources linked here. Link another source to close the gap.
                                </Text>
                            </Alert>
                        )}
                    </Paper>
                </Group>
            </div>
        </Box>
    )
}
