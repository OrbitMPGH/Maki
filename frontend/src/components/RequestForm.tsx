import { Button, Group, NumberInput, Stack, Text, Textarea } from '@mantine/core'
import { IconSend } from '@tabler/icons-react'

/**
 * The chapter range + note a request carries, and its submit button.
 *
 * Both bounds are optional and both blank means "everything", which is what almost every request
 * is. They are drawn as plain optional inputs rather than behind an "only some chapters" toggle so
 * that the common case costs no clicks and the uncommon one costs no discovery.
 *
 * Controlled from outside because the two callers keep the values for different reasons: the detail
 * modal resets them when a different card opens it, the series page when the modal closes.
 */
export function RequestForm({
  chapterStart,
  chapterEnd,
  note,
  onChapterStart,
  onChapterEnd,
  onNote,
  onSubmit,
  pending,
  label = 'Request series',
  dense = false,
}: {
  chapterStart: number | ''
  chapterEnd: number | ''
  note: string
  onChapterStart: (value: number | '') => void
  onChapterEnd: (value: number | '') => void
  onNote: (value: string) => void
  onSubmit: () => void
  pending: boolean
  label?: string
  /**
   * Fit a column rather than a page: the two bounds share whatever width there is and the button
   * spans it. The detail modal draws this in a ~215px rail, where two fixed 120px inputs and their
   * gap overflow it.
   */
  dense?: boolean
}) {
  return (
    <Stack gap="xs" mt="xs">
      <Text size="xs" fw={700} c="dimmed" tt="uppercase">
        Chapters - leave blank for all
      </Text>
      <Group gap="sm" align="flex-end" wrap="nowrap">
        <NumberInput
          label="From"
          placeholder="1"
          value={chapterStart}
          onChange={(v) => onChapterStart(typeof v === 'number' ? v : '')}
          min={0}
          // Chapter numbers are genuinely fractional (12.5 specials), so no step rounding.
          step={1}
          decimalScale={3}
          size="sm"
          w={dense ? undefined : 120}
          style={dense ? { flex: 1, minWidth: 0 } : undefined}
        />
        <NumberInput
          label="To"
          placeholder="latest"
          value={chapterEnd}
          onChange={(v) => onChapterEnd(typeof v === 'number' ? v : '')}
          min={0}
          step={1}
          decimalScale={3}
          size="sm"
          w={dense ? undefined : 120}
          style={dense ? { flex: 1, minWidth: 0 } : undefined}
        />
      </Group>
      <Textarea
        label="Note (optional)"
        placeholder="Anything the admin should know"
        value={note}
        onChange={(e) => onNote(e.currentTarget.value)}
        autosize
        minRows={2}
        maxRows={4}
      />
      <Group grow={dense}>
        <Button leftSection={<IconSend size={16} />} onClick={onSubmit} loading={pending}>
          {label}
        </Button>
      </Group>
    </Stack>
  )
}
