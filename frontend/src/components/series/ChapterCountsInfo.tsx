import { ActionIcon, Popover, Stack, Text } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { IconInfoCircle } from '../../lib/icons'
import { formatDecimal } from '../../format'

export interface ChapterCounts {
    /** Every chapter row: the Chapters tab and "Chapters known". */
    listed: number
    /** The Downloads total: wanted or already obtained, or `listed` when nothing is wanted. */
    wanted: number
    nothingWanted: boolean
    notWanted: number
    /** Listed chapters with a decimal number or none. */
    offNumbering: number
    /** Chapters with a file row whose file the health scan could not find. */
    fileMissing: number
    /** Highest chapter number listed, or null when nothing is numbered. */
    highestListed: number | null
    totalChapters: number | null
    totalLocked: boolean
    /** Null when read tracking is off or nothing has been read. */
    readCount: number | null
    readUpTo: number | null
    nextChapter: string | null
}

/** Why the series page shows several chapter counts that disagree, one plain sentence each. */
export function ChapterCountsInfo({ counts }: { counts: ChapterCounts }) {
    const { t } = useLingui()
    const { listed, wanted, notWanted, offNumbering, fileMissing, totalChapters, readCount, nextChapter } = counts
    const highest = counts.highestListed != null ? formatDecimal(counts.highestListed) : null
    const readUpTo = counts.readUpTo != null ? formatDecimal(counts.readUpTo) : null
    const label = t`How these chapter counts add up`

    return (
        <Popover position="bottom-end" withArrow shadow="md" width={300} trapFocus returnFocus>
            <Popover.Target>
                <ActionIcon size="sm" variant="subtle" color="var(--neutral)" aria-label={label}>
                    <IconInfoCircle size={16} />
                </ActionIcon>
            </Popover.Target>
            <Popover.Dropdown>
                <Stack gap={8} className="tnum">
                    {listed > 0 && (
                        <Text size="sm">
                            <Trans>
                                <Plural value={listed} one="# chapter is listed" other="# chapters are listed" />, the
                                count on the Chapters tab and under Chapters known.
                            </Trans>
                        </Text>
                    )}
                    {offNumbering > 0 && (
                        <Text size="sm">
                            <Plural
                                value={offNumbering}
                                one="# of them has a decimal number or none, like an extra or a special."
                                other="# of them have a decimal number or none, like extras or specials."
                            />
                        </Text>
                    )}
                    {counts.nothingWanted ? (
                        <Text size="sm">
                            <Trans>Nothing is wanted, so Downloads counts against every listed chapter.</Trans>
                        </Text>
                    ) : notWanted > 0 && (
                        <Text size="sm">
                            <Trans>
                                Downloads counts the <Plural value={wanted} one="# chapter" other="# chapters" /> you
                                want or already have.{' '}
                                <Plural value={notWanted} one="# more is listed but not wanted." other="# more are listed but not wanted." />
                            </Trans>
                        </Text>
                    )}
                    {fileMissing > 0 && (
                        <Text size="sm">
                            <Plural
                                value={fileMissing}
                                one="# downloaded chapter is missing its file, so Downloads leaves it out."
                                other="# downloaded chapters are missing their files, so Downloads leaves them out."
                            />
                        </Text>
                    )}
                    {highest != null && (
                        <Text size="sm">
                            <Trans>Your sources go up to ch. {highest}.</Trans>
                        </Text>
                    )}
                    {totalChapters != null && (
                        <Text size="sm">
                            {counts.totalLocked ? (
                                <Plural
                                    value={totalChapters}
                                    one="You set the total to # chapter."
                                    other="You set the total to # chapters."
                                />
                            ) : (
                                <Plural value={totalChapters} one="MangaBaka lists # chapter." other="MangaBaka lists # chapters." />
                            )}
                        </Text>
                    )}
                    {readCount != null && (
                        <Text size="sm">
                            {readUpTo != null ? (
                                <Trans>
                                    You have finished <Plural value={readCount} one="# chapter" other="# chapters" />, up
                                    to ch. {readUpTo}.
                                </Trans>
                            ) : (
                                <Plural
                                    value={readCount}
                                    one="You have finished # chapter."
                                    other="You have finished # chapters."
                                />
                            )}
                            {nextChapter != null && (
                                <>
                                    {' '}
                                    <Trans>Next up is {nextChapter}.</Trans>
                                </>
                            )}
                        </Text>
                    )}
                </Stack>
            </Popover.Dropdown>
        </Popover>
    )
}
