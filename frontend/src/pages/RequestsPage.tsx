import { useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Group,
  Image,
  Modal,
  NumberInput,
  SegmentedControl,
  Select,
  Stack,
  Text,
  Textarea,
  Tooltip,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  IconCheck,
  IconInbox,
  IconExternalLink,
  IconPencil,
  IconTrash,
  IconX,
} from '@tabler/icons-react'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { t as now, plural, msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useRootFolders } from '../api/hooks'
import {
  chapterRangeInline,
  chapterRangeLabel,
  useApproveSeriesRequest,
  useDeleteSeriesRequest,
  useEditSeriesRequest,
  useRejectSeriesRequest,
  useSeriesRequests,
  type RequestFilter,
  type SeriesRequest,
} from '../api/requests'
import { useAuth } from '../auth/AuthProvider'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { useLabel } from '../i18n-context'
import { formatDate } from '../format'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

const STATUS_COLOR: Record<SeriesRequest['status'], string> = {
  Pending: 'var(--warn)',
  Processing: 'var(--info)',
  Approved: 'var(--ok)',
  Rejected: 'var(--danger)',
}

/** Descriptors, not strings: this table is built once, when the module loads. */
const STATUS_LABEL: Record<SeriesRequest['status'], MessageDescriptor> = {
  Pending: msg`Pending`,
  Processing: msg`Approval in progress`,
  Approved: msg`Approved`,
  Rejected: msg`Rejected`,
}

export default function RequestsPage() {
  const { can } = useAuth()
  const isAdmin = can('Admin')
  const { t } = useLingui()
  const renderLabel = useLabel()

  const [filter, setFilter] = useState<RequestFilter>('pending')
  const { data: requests, isPending } = useSeriesRequests(filter)
  const { data: rootFolders } = useRootFolders()

  const approve = useApproveSeriesRequest()
  const reject = useRejectSeriesRequest()
  const remove = useDeleteSeriesRequest()
  const edit = useEditSeriesRequest()

  /** The request an admin is approving; the root folder is only asked for when one is needed. */
  const [approving, setApproving] = useState<SeriesRequest | null>(null)
  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [approveNote, setApproveNote] = useState('')

  const [rejecting, setRejecting] = useState<SeriesRequest | null>(null)
  const [rejectNote, setRejectNote] = useState('')

  const [editing, setEditing] = useState<SeriesRequest | null>(null)
  const [editStart, setEditStart] = useState<number | ''>('')
  const [editEnd, setEditEnd] = useState<number | ''>('')

  const openEdit = (request: SeriesRequest) => {
    setEditing(request)
    setEditStart(request.chapterStart ?? '')
    setEditEnd(request.chapterEnd ?? '')
  }

  const submitEdit = () => {
    if (!editing) return
    edit.mutate(
      {
        id: editing.id,
        chapterStart: editStart === '' ? null : editStart,
        chapterEnd: editEnd === '' ? null : editEnd,
      },
      {
        onSuccess: (result) => {
          setEditing(null)
          const range = chapterRangeInline(result.chapterStart, result.chapterEnd)
          notifications.show({
            message: now`Now ${range}`,
            color: 'green',
          })
        },
      },
    )
  }

  const openApprove = (request: SeriesRequest) => {
    setApproving(request)
    setApproveNote('')
    setRootFolderId(rootFolders && rootFolders.length > 0 ? String(rootFolders[0].id) : null)
  }

  // A new-series request has to land somewhere; a chapter request already has its series.
  const needsRootFolder = approving?.kind === 'NewSeries' && approving.seriesId == null

  const submitApprove = () => {
    if (!approving) return
    approve.mutate(
      {
        id: approving.id,
        rootFolderId: needsRootFolder && rootFolderId ? Number(rootFolderId) : null,
        note: approveNote.trim() || null,
      },
      {
        onSuccess: (result) => {
          setApproving(null)
          notifications.show({
            message:
              result.queuedCount && result.queuedCount > 0
                ? plural(result.queuedCount, {
                    one: 'Approved, queued # chapter',
                    other: 'Approved, queued # chapters',
                  })
                : now`Approved`,
            color: 'green',
          })
        },
      },
    )
  }

  const submitReject = () => {
    if (!rejecting) return
    reject.mutate(
      { id: rejecting.id, note: rejectNote.trim() || null },
      {
        onSuccess: () => {
          setRejecting(null)
          notifications.show({ message: now`Request rejected`, color: 'gray' })
        },
      },
    )
  }

  const editingTitle = editing?.title
  const editingAskedFor = chapterRangeInline(
    editing?.originalChapterStart ?? editing?.chapterStart ?? null,
    editing?.originalChapterEnd ?? editing?.chapterEnd ?? null,
  )

  return (
    <SurfaceFrame pageStyle="operational">
      <PageHeader
        compact
        title={t`Requests`}
        description={
          isAdmin
            ? t`What readers without add or download permissions have asked for. Approving adds the series and queues the chapters.`
            : t`Series and chapters you have asked an admin for.`
        }
      />

      <Group mb="lg">
        <SegmentedControl
          value={filter}
          onChange={(v) => setFilter(v as RequestFilter)}
          data={[
            { value: 'pending', label: t`Pending` },
            { value: 'resolved', label: t`Resolved` },
            { value: 'all', label: t`All` },
          ]}
        />
      </Group>

      {(approve.isError || reject.isError || remove.isError || edit.isError) && (
        <Alert color="var(--danger)" variant="light" mb="md">
          {String(approve.error ?? reject.error ?? remove.error ?? edit.error)}
        </Alert>
      )}

      {!isPending && (requests?.length ?? 0) === 0 ? (
        <EmptyState
          icon={IconInbox}
          title={filter === 'pending' ? t`No pending requests` : t`Nothing here`}
          description={
            isAdmin
              ? t`Requests filed from the Request series page and from series pages show up here.`
              : t`Search for a series and request it - it will show up here once you do.`
          }
        />
      ) : (
        <Stack gap="xs">
          {requests?.map((r) => {
            // Named locals, because a placeholder Lingui numbers `{0}` tells a translator nothing.
            const askedFor = chapterRangeInline(r.originalChapterStart, r.originalChapterEnd)
            const { title, editedBy, resolvedBy } = r
            return (
              <Panel key={r.id} p="sm">
                <div className="requests-row">
                  <div className="requests-row-cover">
                    {r.coverUrl && <Image src={r.coverUrl} w={48} h={72} fit="cover" alt="" />}
                  </div>

                  <div className="requests-row-main">
                    <Group gap="xs">
                      <Text fw={650} lineClamp={1}>
                        {r.title}
                      </Text>
                      {r.year && (
                        <Text size="sm" c="var(--ink-3)" className="tnum">
                          {r.year}
                        </Text>
                      )}
                      <Badge size="sm" variant="light" color={STATUS_COLOR[r.status]}>
                        {renderLabel(STATUS_LABEL[r.status])}
                      </Badge>
                      <Badge size="sm" variant="outline" color="var(--neutral)">
                        {r.kind === 'NewSeries' ? t`New series` : t`Chapters`}
                      </Badge>
                    </Group>

                    <Group gap="xs" mt={4}>
                      <Text size="sm" c={r.editedAt ? undefined : 'var(--ink-3)'} fw={r.editedAt ? 600 : undefined}>
                        {chapterRangeLabel(r.chapterStart, r.chapterEnd)}
                      </Text>
                      <Text size="sm" c="var(--ink-3)">
                        ·
                      </Text>
                      <Text size="sm" c="var(--ink-3)">
                        {r.requestedBy}, {formatDate(r.created)}
                      </Text>
                    </Group>

                    {/* The admin's range is what will be queued, so it leads, but what was actually
                        asked for has to stay visible, or a trimmed request reads as the requester's
                        own. */}
                    {r.editedAt && (
                      <Text size="xs" c="var(--ink-3)" mt={2}>
                        {editedBy ? (
                          <Trans>
                            Adjusted by {editedBy}, asked for {askedFor}
                          </Trans>
                        ) : (
                          <Trans>Adjusted, asked for {askedFor}</Trans>
                        )}
                      </Text>
                    )}

                    {r.note && (
                      <Text size="sm" mt={4} style={{ whiteSpace: 'pre-line' }}>
                        {r.note}
                      </Text>
                    )}

                    {r.status !== 'Pending' && (
                      <Text size="xs" c="var(--ink-3)" mt={4}>
                        {resolvedBy && r.status !== 'Processing' ? (
                          r.status === 'Approved' ? (
                            <Trans>Approved by {resolvedBy}</Trans>
                          ) : (
                            <Trans>Rejected by {resolvedBy}</Trans>
                          )
                        ) : (
                          renderLabel(STATUS_LABEL[r.status])
                        )}
                        {r.queuedCount != null && r.status === 'Approved' && (
                          <>
                            {', '}
                            <Plural value={r.queuedCount} one="queued # chapter" other="queued # chapters" />
                          </>
                        )}
                        {r.resolutionNote ? ` · ${r.resolutionNote}` : ''}
                      </Text>
                    )}
                  </div>

                  <div className="requests-row-actions">
                    {r.seriesId != null && (
                      <Tooltip label={t`Open series`} withArrow>
                        <ActionIcon
                          component={Link}
                          to={`/series/${r.seriesId}`}
                          variant="subtle"
                          color="var(--neutral)"
                          aria-label={t`Open ${title}`}
                        >
                          <IconExternalLink size={17} />
                        </ActionIcon>
                      </Tooltip>
                    )}
                    {isAdmin && r.status === 'Pending' && (
                      <>
                        <Tooltip label={t`Change the chapter range`} withArrow>
                          <ActionIcon
                            variant="subtle"
                            color="var(--neutral)"
                            aria-label={t`Edit request for ${title}`}
                            onClick={() => openEdit(r)}
                          >
                            <IconPencil size={17} />
                          </ActionIcon>
                        </Tooltip>
                        <Button
                          size="xs"
                          variant="light"
                          color="var(--ok)"
                          leftSection={<IconCheck size={15} />}
                          onClick={() => openApprove(r)}
                        >
                          <Trans>Approve</Trans>
                        </Button>
                        <Button
                          size="xs"
                          variant="subtle"
                          color="var(--danger)"
                          leftSection={<IconX size={15} />}
                          onClick={() => {
                            setRejecting(r)
                            setRejectNote('')
                          }}
                        >
                          <Trans>Reject</Trans>
                        </Button>
                      </>
                    )}
                    {((isAdmin && r.status !== 'Processing') || r.status === 'Pending') && (
                      <Tooltip label={isAdmin ? t`Delete request` : t`Cancel request`} withArrow>
                        <ActionIcon
                          variant="subtle"
                          color="var(--danger)"
                          aria-label={t`Remove request`}
                          onClick={() => remove.mutate(r.id)}
                          loading={remove.isPending && remove.variables === r.id}
                        >
                          <IconTrash size={17} />
                        </ActionIcon>
                      </Tooltip>
                    )}
                  </div>
                </div>
              </Panel>
            )
          })}
        </Stack>
      )}

      <Modal opened={approving !== null} onClose={() => setApproving(null)} title={t`Approve request`}>
        <Stack gap="sm">
          <Text size="sm">
            {approving?.title}: {chapterRangeLabel(approving?.chapterStart ?? null, approving?.chapterEnd ?? null)}
          </Text>

          {needsRootFolder && (
            <Select
              label={t`Root folder`}
              description={t`Where the series will live. The requester doesn't choose this.`}
              data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
              value={rootFolderId}
              onChange={setRootFolderId}
            />
          )}

          <Textarea
            label={t`Note (optional)`}
            placeholder={t`Shown to whoever asked`}
            value={approveNote}
            onChange={(e) => setApproveNote(e.currentTarget.value)}
            autosize
            minRows={2}
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setApproving(null)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              color="var(--ok)"
              onClick={submitApprove}
              loading={approve.isPending}
              disabled={needsRootFolder && !rootFolderId}
            >
              <Trans>Approve and queue</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={editing !== null} onClose={() => setEditing(null)} title={t`Edit request`}>
        <Stack gap="sm">
          <Text size="sm">
            <Trans>
              {editingTitle}, asked for {editingAskedFor}
            </Trans>
          </Text>

          <Group gap="sm" align="flex-end" className="requests-form-range">
            <NumberInput
              className="requests-form-field"
              label={t`From`}
              placeholder={t`first`}
              value={editStart}
              onChange={(v) => setEditStart(typeof v === 'number' ? v : '')}
              min={0}
              step={1}
              decimalScale={3}
            />
            <NumberInput
              className="requests-form-field"
              label={t`To`}
              placeholder={t`latest`}
              value={editEnd}
              onChange={(v) => setEditEnd(typeof v === 'number' ? v : '')}
              min={0}
              step={1}
              decimalScale={3}
            />
          </Group>
          <Text size="xs" c="var(--ink-3)">
            <Trans>Leave a field blank for no bound. Approving queues exactly this range.</Trans>
          </Text>

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setEditing(null)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button onClick={submitEdit} loading={edit.isPending}>
              <Trans>Save range</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={rejecting !== null} onClose={() => setRejecting(null)} title={t`Reject request`}>
        <Stack gap="sm">
          <Text size="sm">{rejecting?.title}</Text>
          <Textarea
            label={t`Reason (optional)`}
            placeholder={t`Shown to whoever asked`}
            value={rejectNote}
            onChange={(e) => setRejectNote(e.currentTarget.value)}
            autosize
            minRows={2}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRejecting(null)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button color="var(--danger)" onClick={submitReject} loading={reject.isPending}>
              <Trans>Reject</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </SurfaceFrame>
  )
}
