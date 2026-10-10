import { Button, Group, Stack, Text } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { IconTrash } from '@tabler/icons-react'

/**
 * Shown in a delete dialog after the server refused to use the recycle bin (another volume, or no
 * bin folder). The only way to delete then is for good, so it says so before offering it.
 */
export function PermanentDeleteFallback({ onConfirm, loading }: { onConfirm: () => void; loading?: boolean }) {
  return (
    <Stack gap="xs">
      <Text size="sm" c="var(--danger)">
        <Trans>
          These files cannot go to the recycle bin. You can delete them permanently instead, and they
          cannot be restored afterwards.
        </Trans>
      </Text>
      <Group justify="flex-end">
        <Button color="var(--danger-fill)" leftSection={<IconTrash size={16} />} loading={loading} onClick={onConfirm}>
          <Trans>Delete permanently</Trans>
        </Button>
      </Group>
    </Stack>
  )
}
