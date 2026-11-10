import { errorText } from '../api/errorText'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, Checkbox, Modal, ScrollArea, Skeleton, Stack, Text } from '@mantine/core'
import { notifications } from '../lib/toast'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { IconArrowRight } from '../lib/icons'
import { EmptyState } from './ui/EmptyState'
import { ModalFooter, ModalHeading, ModalNote } from './ui/ModalShell'
import { useQueryClient } from '@tanstack/react-query'
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
  const queryClient = useQueryClient()
  const start = useStartRatingImport()
  const { data, isFetching, isError: queryIsError, error: queryError } = useRatingImport(service, opened)
  const apply = useApplyRatingImport()
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const defaultedRef = useRef<string | null>(null)

  const retry = useCallback(() => {
    setSelected(new Set())
    defaultedRef.current = null
    queryClient.removeQueries({ queryKey: ['rating-import', service] })
    start.mutate(service)
    // start.mutate is stable; only re-run when the service changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [service, queryClient])

  // Kick off a fresh preview each time the modal opens, dropping any previous run's cached
  // items/selection so a reopen can't pre-select and enable Apply before the new preview lands.
  useEffect(() => {
    if (opened) {
      setSelected(new Set())
      defaultedRef.current = null
      queryClient.removeQueries({ queryKey: ['rating-import', service] })
      start.mutate(service)
    }
    // start.mutate is stable; only re-run on open/service change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, service, queryClient])

  const items = data?.items ?? []
  const requestFailed = start.isError || queryIsError
  const running = !requestFailed && (data?.running ?? true)
  const computedAt = data?.computedAt ?? null

  useEffect(() => {
    if (!running && items.length > 0 && computedAt !== defaultedRef.current) {
      defaultedRef.current = computedAt
      setSelected(new Set(items.map((i) => i.seriesId)))
    }
  }, [running, items, computedAt])

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
            color: 'var(--ok)',
          })
          onClose()
        },
      },
    )

  const errorMessage = start.error
    ? errorText(start.error)
    : queryError
      ? errorText(queryError)
      : requestFailed
        ? t`Couldn't reach ${label}.`
        : (data?.error ?? null)

  const body = useMemo(() => {
    if (errorMessage) {
      return (
        <Stack gap="xs" align="flex-start">
          <ModalNote>{errorMessage}</ModalNote>
          <Button size="xs" variant="default" onClick={retry}>
            <Trans>Retry</Trans>
          </Button>
        </Stack>
      )
    }
    if (running) {
      return (
        <Stack gap={0}>
          {[70, 52, 62, 44, 58].map((w) => (
            <div key={w} className="modal-row">
              <Skeleton h={14} w={14} />
              <Skeleton h={10} w={`${w}%`} />
            </div>
          ))}
          <Text size="sm" c="var(--ink-3)" mt="xs">
            <Trans>Reading your ratings from {label}…</Trans>
          </Text>
        </Stack>
      )
    }
    if (items.length === 0) {
      return (
        <EmptyState
          compact
          mood="pleased"
          title={t`Nothing to import`}
          description={<Trans>No scores on {label} differ from your local ratings.</Trans>}
        />
      )
    }
    return (
      <ScrollArea.Autosize mah="min(360px, 45dvh)">
        <div className="modal-rows">
          <div className="modal-row modal-row-head">
            <span style={{ width: 16 }} />
            <span className="modal-row-grow">
              <Trans>Series</Trans>
            </span>
            <span className="modal-score-old" style={{ textAlign: 'right' }}>
              <Trans>Local</Trans>
            </span>
            <span style={{ width: 14 }} />
            <span style={{ minWidth: 28 }}>{label}</span>
          </div>
          {items.map((i) => (
            <label key={i.seriesId} className="modal-row" style={{ cursor: 'pointer' }}>
              <Checkbox
                aria-label={i.title}
                size="xs"
                checked={selected.has(i.seriesId)}
                onChange={() => toggle(i.seriesId)}
              />
              <Text size="sm" lineClamp={1} className="modal-row-grow" c="var(--ink-hi)">
                {i.title}
              </Text>
              <span className="modal-score-old tnum">{i.localRating ? i.localRating : '-'}</span>
              <IconArrowRight size={14} aria-hidden style={{ color: 'var(--ink-4)' }} />
              <span className="modal-score-new tnum">{i.remoteScore}</span>
            </label>
          ))}
        </div>
      </ScrollArea.Autosize>
    )
  }, [errorMessage, retry, running, items, selected, label, t, i18n.locale])

  const showFooterSelection = !errorMessage && !running && items.length > 0

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size="lg"
      centered
      title={
        <ModalHeading
          title={t`Import ratings from ${label}`}
          description={<Trans>Scores on {label} that differ from your local ratings.</Trans>}
        />
      }
    >
      {body}
      <ModalFooter status={showFooterSelection ? t`${selectedCount} of ${totalCount} selected` : undefined}>
        {showFooterSelection && (
          <Checkbox
            size="xs"
            label={t`Select all`}
            checked={allChecked}
            indeterminate={selected.size > 0 && !allChecked}
            onChange={() =>
              setSelected(allChecked ? new Set() : new Set(items.map((i) => i.seriesId)))
            }
            mr="xs"
          />
        )}
        <Button variant="default" onClick={onClose}>
          <Trans>Cancel</Trans>
        </Button>
        <Button
          onClick={applyChosen}
          disabled={running || start.isPending || selected.size === 0}
          loading={apply.isPending || isFetching}
        >
          {selectedCount > 0 ? t`Apply ${selectedCount}` : t`Apply`}
        </Button>
      </ModalFooter>
    </Modal>
  )
}
