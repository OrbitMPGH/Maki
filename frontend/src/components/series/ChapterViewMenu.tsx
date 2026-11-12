import { ActionIcon, Checkbox, Popover, Radio, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconColumns } from '../../lib/icons'
import { HIDABLE_CHAPTER_COLUMNS, type HidableChapterColumn } from './chapterTable'

type SortKey = 'number' | 'released'

type ChapterViewMenuProps = {
  sortKey: SortKey
  onSortKey: (key: SortKey) => void
  hidden: ReadonlySet<HidableChapterColumn>
  onToggleColumn: (column: HidableChapterColumn) => void
}

/** Sort key and column picker for the chapter table. Stays open while columns are ticked. */
export function ChapterViewMenu({ sortKey, onSortKey, hidden, onToggleColumn }: ChapterViewMenuProps) {
  const { t } = useLingui()
  const columnLabels: Record<HidableChapterColumn, string> = {
    title: t`Title`,
    released: t`Released`,
    source: t`Source`,
    status: t`Status`,
  }

  return (
      <Popover position="bottom-end" shadow="md" withinPortal trapFocus returnFocus>
        <Popover.Target>
          <ActionIcon size="input-xs" variant="default" aria-label={t`Sort and columns`}>
            <IconColumns size={16} />
          </ActionIcon>
        </Popover.Target>
        <Popover.Dropdown p="sm">
          <Stack gap="md">
            <Radio.Group
                label={<Trans>Sort by</Trans>}
                value={sortKey}
                onChange={(value) => onSortKey(value === 'released' ? 'released' : 'number')}
            >
              <Stack gap={6} mt={6}>
                <Radio size="xs" value="number" label={<Trans>Chapter number</Trans>} />
                <Radio size="xs" value="released" label={<Trans>Release date</Trans>} />
              </Stack>
            </Radio.Group>
            <div role="group" aria-labelledby="chapter-columns-label">
              <Text id="chapter-columns-label" size="sm" fw={500}><Trans>Columns</Trans></Text>
              <Stack gap={6} mt={6}>
                {HIDABLE_CHAPTER_COLUMNS.map((column) => (
                    <Checkbox
                        key={column}
                        size="xs"
                        label={columnLabels[column]}
                        checked={!hidden.has(column)}
                        onChange={() => onToggleColumn(column)}
                    />
                ))}
              </Stack>
            </div>
          </Stack>
        </Popover.Dropdown>
      </Popover>
  )
}
