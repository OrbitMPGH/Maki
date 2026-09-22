import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Button,
  Center,
  Checkbox,
  Group,
  Loader,
  Modal,
  Rating,
  ScrollArea,
  Stack,
  Text,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import {
  useApplyRatingImport,
  useRatingImport,
  useStartRatingImport,
} from '../api/hooks'

/**
 * Preview-then-confirm import of a tracker's ratings into local series ratings. Opening the modal
 * kicks off the (background) preview; the user then picks which remote scores to apply.
 */
export function RatingImportModal({
  service,
  label,
  opened,
  onClose,
}: {
  service: string
  label: string
  opened: boolean
  onClose: () => void
}) {
  const { t, i18n } = useLingui()
  const start = useStartRatingImport()
  const { data, isFetching } = useRatingImport(service, opened)
  const apply = useApplyRatingImport()
  const [selected, setSelected] = useState<Set<number>>(new Set())

  // Kick off a fresh preview each time the modal opens.
  useEffect(() => {
    if (opened) {
      setSelected(new Set())
      start.mutate(service)
    }
    // start.mutate is stable; only re-run on open/service change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, service])

  const items = data?.items ?? []
  const running = data?.running ?? true

  // Default every previewed item to checked once the run finishes.
  useEffect(() => {
    if (!running && items.length > 0) {
      setSelected(new Set(items.map((i) => i.seriesId)))
    }
  }, [running, items])

  const allChecked = items.length > 0 && selected.size === items.length
  const selectedCount = selected.size
  const totalCount = items.length
  const toggle = (id: number) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const applyChosen = () =>
    apply.mutate(
      { service, seriesIds: [...selected] },
      {
        onSuccess: ({ applied }) => {
          notifications.show({
            message: plural(applied, { one: 'Imported # rating', other: 'Imported # ratings' }),
            color: 'green',
          })
          onClose()
        },
      },
    )

  const body = useMemo(() => {
    if (running) {
      return (
        <Center py={40}>
          <Stack align="center" gap="xs">
            <Loader />
            <Text size="sm" c="var(--ink-3)">
              <Trans>Reading your ratings from {label}…</Trans>
            </Text>
          </Stack>
        </Center>
      )
    }
    if (data?.error) {
      return (
        <Alert color="var(--danger)" variant="light">
          {data.error}
        </Alert>
      )
    }
    if (items.length === 0) {
      return (
        <Text size="sm" c="var(--ink-3)" py="md">
          <Trans>Nothing to import, no scores on {label} differ from your local ratings.</Trans>
        </Text>
      )
    }
    return (
      <Stack gap="xs">
        <Group justify="space-between">
          <Checkbox
            size="xs"
            label={t`${selectedCount} of ${totalCount} selected`}
            checked={allChecked}
            indeterminate={selected.size > 0 && !allChecked}
            onChange={() =>
              setSelected(allChecked ? new Set() : new Set(items.map((i) => i.seriesId)))
            }
          />
        </Group>
        <ScrollArea.Autosize mah={360}>
          <Stack gap={4}>
            {items.map((i) => (
              <Group key={i.seriesId} justify="space-between" wrap="nowrap" gap="sm">
                <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
                  <Checkbox
                    size="xs"
                    checked={selected.has(i.seriesId)}
                    onChange={() => toggle(i.seriesId)}
                  />
                  <Text size="sm" lineClamp={1}>
                    {i.title}
                  </Text>
                </Group>
                <Group gap={6} wrap="nowrap">
                  <Text size="xs" c="var(--ink-3)" className="tnum">
                    {i.localRating ? `${i.localRating}/10` : '-'} →
                  </Text>
                  <Rating size="xs" count={5} fractions={2} value={i.remoteScore / 2} readOnly />
                  <Text size="xs" c="var(--ink-3)" className="tnum" w={34} ta="right">
                    {i.remoteScore}/10
                  </Text>
                </Group>
              </Group>
            ))}
          </Stack>
        </ScrollArea.Autosize>
      </Stack>
    )
  }, [running, data?.error, items, selected, allChecked, label, selectedCount, totalCount, t, i18n.locale])

  return (
    <Modal opened={opened} onClose={onClose} title={t`Import ratings from ${label}`} size="lg" centered>
      {body}
      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          <Trans>Cancel</Trans>
        </Button>
        <Button
          onClick={applyChosen}
          disabled={running || selected.size === 0}
          loading={apply.isPending || isFetching}
        >
          {selectedCount > 0 ? t`Apply ${selectedCount}` : t`Apply`}
        </Button>
      </Group>
    </Modal>
  )
}
