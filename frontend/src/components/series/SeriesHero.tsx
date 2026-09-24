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
    Skeleton,
    Stack,
    Text,
    Title,
    Tooltip
} from '@mantine/core'
import {IconAlertTriangle, IconArrowLeft, IconBook, IconDownload, IconX} from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { otherTitles, readableTitles } from '../../api/titles'
import type { SeriesDto } from '../../api/types'
import {
    contentRatingToken,
    contentRatingVisual,
    seriesProgressVisual,
    seriesStatusVisual,
    statusToken,
} from '../ui/status'
import {useReadTracking} from "../../api/reader.ts";
import {useChapters} from "../../api/hooks.ts";
import { msg } from '@lingui/core/macro'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import { useBackTarget } from '../../lib/navHistory'
import {HeroBackdrop} from './HeroBackdrop'
import { GENRE_LABELS, TYPE_LABELS } from '../CatalogueFilters'

/** Where the back link points for a series nobody navigated to: a bookmark, or a pasted link. */
const LIBRARY_FALLBACK = { to: '/library', label: msg`Library` }

/** How many alt titles fit under the heading before the line stops being readable. */
const MAX_HERO_ALT_TITLES = 4

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
    const { t, i18n } = useLingui()
    const readTracking = useReadTracking()
    const status = seriesStatusVisual(series.status)
    const contentRating = contentRatingVisual(series.contentRating)
    const ratingToken = contentRatingToken(series.contentRating)
    const author = series.authorStory ?? series.authorArt
    const { data: chapters } = useChapters(series.id)
    // Wherever you came from, not a fixed destination: reached from Discover with a panel full of
    // filters, this walks back to that panel with the filters still on it. The library is only the
    // fallback for a series opened cold, from a bookmark or a fresh tab.
    const back = useBackTarget(LIBRARY_FALLBACK)
    const label = useLabel()

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

        // Under a whole chapter short is not a gap: a source ending on 6.5 against a listed 7 is the
        // same run, and the warning would otherwise say "roughly 0 chapters" are missing.
        const highest = Math.max(...numbered)
        const missing = Math.floor(total - highest)
        if (missing < 1) return null

        return { highest, total, missing }
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

    // Hoisted out of the JSX below: Lingui names a placeholder after the expression only when that
    // expression is a plain identifier, so `progress.have` would extract as {0} and tell a translator
    // nothing about what goes in the slot. The defaults on the gap are never rendered, since every
    // use of them sits behind `sourceGap &&`; they are here to keep the destructure typed as numbers.
    const { have: haveCount, total: totalCount } = progress
    const readCount = series.readChapterCount ?? 0
    const { highest = 0, total: listed = 0, missing = 0 } = sourceGap ?? {}

    // One quiet line of facts rather than a row of coloured pills: none of these is a state anyone
    // acts on, so none of them earns a colour.
    // Through the same label maps as the Discover modal, so the type and genres read the same (and
    // in the reader's language) on a result and on the series it becomes.
    const facts = [
        series.type ? label(TYPE_LABELS[series.type] ?? series.type) : null,
        series.year ? String(series.year) : null,
        series.hasAnime ? (series.animeName ?? t`Anime adaptation`) : null,
        series.genres.slice(0, 5).map((g) => label(GENRE_LABELS[g] ?? g)).join(', ') || null,
    ].filter(Boolean)

    // The canonical title is in this line too when a language preference moved the heading off it —
    // otherwise picking "Japanese" makes the name everything else in Maki uses (the folder on disk,
    // the file names, search) disappear from the page entirely.
    const altTitles = otherTitles(
        readableTitles(series.altTitles, i18n.locale),
        series.originalTitle,
        series.displayTitle,
    ).concat(series.displayTitle === series.title ? [] : [{ title: series.title, language: null }])

    // Named rather than inlined into the <Plural>: Lingui names a placeholder after the expression
    // only when it is a plain identifier, so a subtraction would extract as {0}. Naming it
    // `overflow` also makes the message identical to the one TagBuckets already produces, which
    // means this reuses that translation in all ten languages instead of adding a new entry.
    const overflow = altTitles.length - MAX_HERO_ALT_TITLES

    return (
        <Box className="series-hero">
            <HeroBackdrop coverUrl={series.coverUrl} />

            <div className="series-hero-body">
                {/* Arrow inside the link, not beside it: the arrow is the part of this people aim at. */}
                <Text
                    component={Link}
                    to={back.to}
                    onClick={back.onClick}
                    className="series-hero-back"
                    mb="md"
                    size="sm"
                    fw={600}
                >
                    <IconArrowLeft size={16} stroke={1.9} />
                    {label(back.label)}
                </Text>

                <Group className={"series-hero-content"}>

                    <Group align="flex-start" gap={32} wrap="nowrap" className="series-hero-row">
                        {series.coverUrl && (
                            <img
                                className="series-hero-poster"
                                src={series.coverUrl}
                                alt={series.displayTitle}
                            />
                        )}

                        <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                            <Title order={1} className="series-hero-title" title={series.title}>
                                {series.displayTitle}
                            </Title>

                            {altTitles.length > 0 && (
                                // Capped: a well-covered series carries dozens of these (One Piece
                                // has 37), and the full list belongs in the Metadata card, not
                                // wrapped across four lines under the heading.
                                <Text size="sm" pt="xs" c="var(--ink-3)">
                                    {altTitles.slice(0, MAX_HERO_ALT_TITLES).map((t) => t.title).join(' · ')}
                                    {overflow > 0 && (
                                        <>
                                            {' · '}
                                            <Plural value={overflow} one="+# more" other="+# more" />
                                        </>
                                    )}
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
                        color: `var(--${statusToken(status.color)})`,
                        background: `var(--${statusToken(status.color)}-soft)`,
                    }}
                >
                  <status.Icon size={14} />
                    {label(status.label)}
                </span>

                                    {contentRating && (
                                        <Tooltip label={t`Content rating`} withArrow>
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
                        {label(contentRating.label)}
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
                                            <Tooltip label={t`Clear rating`} withArrow>
                                                <ActionIcon
                                                    size="sm"
                                                    variant="subtle"
                                                    color="gray"
                                                    onClick={() => onRate(null)}
                                                    aria-label={t`Clear rating`}
                                                >
                                                    <IconX size={14} />
                                                </ActionIcon>
                                            </Tooltip>
                                        </>
                                    ) : (
                                        <Text size="sm" c="var(--ink-4)">
                                            <Trans>Not rated</Trans>
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
                    <Paper withBorder radius="lg" p="lg" className="series-hero-glass-panel">
                        <Title order={3} fz={17}>
                            <Trans>Progress</Trans>
                        </Title>

                        {readTracking && series.readChapterCount != null && progress.have > 0 && (
                            <Box mt="md">
                                <Group gap={9} c="var(--ink-3)">
                                    <IconBook size={17} />
                                    <Text size="sm" fw={600} c="var(--ink)">
                                        <Trans>Reading</Trans>
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
                                        <Trans>
                                            {readCount} /{' '}
                                            <Plural value={haveCount} one="# chapter" other="# chapters" />
                                        </Trans>
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
                                    <Trans>Downloads</Trans>
                                </Text>
                            </Group>
                            <Progress
                                mt={12}
                                value={progress.pct}
                                // Never green while the sources are short of the full run: "all downloaded" and
                                // "you have the whole series" are different claims, and the green tick is exactly
                                // what makes someone unmonitor a series that's still missing its tail.
                                color={sourceGap ? 'var(--warn)' : progress.complete ? 'var(--ok)' : 'var(--info)'}
                                radius="xl"
                            />
                            <Group justify="space-between" mt={9}>
                                <Text size="sm" c="var(--ink-2)" className="tnum">
                                    {/* Two whole messages rather than one with a clause appended: a
                                        fragment glued onto a translated sentence lands in the wrong place
                                        in any language that does not order it the way English does. */}
                                    {progress.nothingWanted ? (
                                        <Trans>
                                            {haveCount} /{' '}
                                            <Plural value={totalCount} one="# chapter" other="# chapters" />{' '}
                                            listed, none wanted
                                        </Trans>
                                    ) : (
                                        <Trans>
                                            {haveCount} /{' '}
                                            <Plural value={totalCount} one="# chapter" other="# chapters" />
                                        </Trans>
                                    )}
                                </Text>
                                <Text size="sm" fw={600} c="var(--ink-2)" className="tnum">
                                    {Math.round(progress.pct)}%
                                </Text>
                            </Group>
                            {missingWanted > 0 && (
                                <Text size="xs" c="var(--ink-4)" mt={7} className="tnum">
                                    <Plural
                                        value={missingWanted}
                                        one="# wanted, not fetched"
                                        other="# wanted, not fetched"
                                    />
                                </Text>
                            )}
                        </Box>

                        {sourceGap && (
                            <Alert
                                mt="md"
                                color="var(--warn)"
                                variant="light"
                                radius="md"
                                icon={<IconAlertTriangle size={16} />}
                            >
                                <Text size="xs" c="var(--ink-3)" style={{ lineHeight: 1.55 }}>
                                    <Trans>
                                        Your sources only reach chapter{' '}
                                        <Text span fw={600} c="var(--ink)" className="tnum">
                                            {highest}
                                        </Text>
                                        , but MangaBaka lists{' '}
                                        <Text span fw={600} c="var(--ink)" className="tnum">
                                            {listed}
                                        </Text>
                                        . Roughly{' '}
                                        <Plural value={missing} one="# chapter" other="# chapters" /> can't be
                                        downloaded from the sources linked here. Link another source to close
                                        the gap.
                                    </Trans>
                                </Text>
                            </Alert>
                        )}
                    </Paper>
                </Group>
            </div>
        </Box>
    )
}

/** The band's own shape while the series loads, so the page lands in place instead of jumping from a spinner. */
export function SeriesHeroSkeleton() {
    return (
        <div aria-hidden>
        <Box className="series-hero" data-loading>
            <div className="series-hero-body">
                <Skeleton h={14} w={84} mb="md" />
                <Group className="series-hero-content">
                    <Group align="flex-start" gap={32} wrap="nowrap" className="series-hero-row">
                        <Skeleton className="series-hero-poster" radius={11} />
                        <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                            <Skeleton h={58} w="62%" />
                            <Skeleton h={14} w="38%" mt="md" />
                            <Skeleton h={18} w="24%" mt={12} />
                            <Group gap={8} mt={18}>
                                <Skeleton h={28} w={92} radius="md" />
                                <Skeleton h={28} w={76} radius="md" />
                                <Skeleton h={18} w={110} ml="xs" />
                            </Group>
                            <Skeleton h={12} w="32%" mt={14} />
                            <Group gap="xs" mt="lg">
                                <Skeleton h={42} w={168} radius="md" />
                                <Skeleton h={42} w={196} radius="md" />
                                <Skeleton h={42} w={132} radius="md" />
                            </Group>
                            <Group gap={28} mt="xl" pb={12}>
                                {[72, 86, 64, 58, 70].map((w) => (
                                    <Skeleton key={w} h={12} w={w} />
                                ))}
                            </Group>
                        </Stack>
                    </Group>
                    <Paper withBorder radius="lg" p="lg" className="series-hero-glass-panel">
                        <Skeleton h={16} w={80} />
                        <Skeleton h={12} w="46%" mt="lg" />
                        <Skeleton h={8} radius="xl" mt={14} />
                        <Skeleton h={12} w="30%" mt={12} />
                        <Skeleton h={12} w="52%" mt="xl" />
                        <Skeleton h={8} radius="xl" mt={14} />
                        <Skeleton h={12} w="30%" mt={12} />
                    </Paper>
                </Group>
            </div>
        </Box>
        <div className="series-body">
            <div className="series-split">
                <Paper withBorder radius="lg" p="lg">
                    <Skeleton h={16} w={96} mb="lg" />
                    {['96%', '88%', '92%', '80%', '90%', '54%'].map((w, i) => (
                        <Skeleton key={i} h={10} w={w} mb={12} />
                    ))}
                </Paper>
                <Paper withBorder radius="lg" p="lg">
                    <Skeleton h={16} w={84} mb="lg" />
                    {[0, 1, 2].map((i) => (
                        <Group key={i} justify="space-between" py="sm" wrap="nowrap">
                            <Skeleton h={10} w="22%" />
                            <Skeleton h={10} w="26%" />
                            <Skeleton h={10} w="8%" />
                            <Skeleton h={18} w={32} radius="xl" />
                        </Group>
                    ))}
                </Paper>
            </div>
        </div>
        </div>
    )
}
