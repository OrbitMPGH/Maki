import { ActionIcon, Button, Checkbox, Group, Popover, Radio, Stack, Switch, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconColumns } from '../../lib/icons'
import { HIDABLE_CHAPTER_COLUMNS, type HidableChapterColumn } from './chapterTable'

type SortKey = 'number' | 'released'

type GroupingControl = {
  checked: boolean
  /** Why grouping can't apply right now, or null while it can. */
  blockedBy: 'release' | 'seasons' | 'none' | null
  onChange: (on: boolean) => void
  /** The table is grouped at this moment. */
  active: boolean
  onCollapseAll: () => void
  onExpandAll: () => void
}

type ChapterViewMenuProps = {
  sortKey: SortKey
  onSortKey: (key: SortKey) => void
  hidden: ReadonlySet<HidableChapterColumn>
  onToggleColumn: (column: HidableChapterColumn) => void
  grouping: GroupingControl
}

/** Sort key, volume grouping and column picker for the chapter table. Stays open while options are changed. */
export function ChapterViewMenu({ sortKey, onSortKey, hidden, onToggleColumn, grouping }: ChapterViewMenuProps) {
  const { t } = useLingui()
  const columnLabels: Record<HidableChapterColumn, string> = {
    title: t`Title`,
    released: t`Released`,
    source: t`Source`,
    status: t`Status`,
  }
  const { blockedBy } = grouping

  return (
      <Popover position="bottom-end" shadow="md" withinPortal trapFocus returnFocus>
        <Popover.Target>
          <ActionIcon size="input-xs" variant="default" aria-label={t`Sort and columns`}>
            <IconColumns size={16} />
          </ActionIcon>
        </Popover.Target>
        <Popover.Dropdown p="sm" maw={280}>
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
            <Stack gap={6}>
              <Switch
                  size="xs"
                  label={<Trans>Group by volume</Trans>}
                  checked={grouping.checked}
                  disabled={blockedBy !== null}
                  onChange={(event) => grouping.onChange(event.currentTarget.checked)}
              />
              {blockedBy === 'release' && (
                  <Text size="xs" c="var(--ink-3)">
                    <Trans>Volumes follow chapter order, so grouping is off while sorting by release date.</Trans>
                  </Text>
              )}
              {blockedBy === 'seasons' && (
                  <Text size="xs" c="var(--ink-3)">
                    <Trans>Grouping is off while anime seasons are marked in the table.</Trans>
                  </Text>
              )}
              {blockedBy === 'none' && (
                  <Text size="xs" c="var(--ink-3)">
                    <Trans>No chapter in this series has a volume number.</Trans>
                  </Text>
              )}
              {grouping.active && (
                  <Group gap="xs">
                    <Button size="compact-xs" variant="default" onClick={grouping.onCollapseAll}>
                      <Trans>Collapse all</Trans>
                    </Button>
                    <Button size="compact-xs" variant="default" onClick={grouping.onExpandAll}>
                      <Trans>Expand all</Trans>
                    </Button>
                  </Group>
              )}
            </Stack>
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
