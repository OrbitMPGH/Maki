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
  Paper,
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
import { useRootFolders } from '../api/hooks'
import {
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

const STATUS_COLOR: Record<SeriesRequest['status'], string> = {
  Pending: 'yellow',
  Approved: 'green',
  Rejected: 'red',
}

export default function RequestsPage() {
  const { can } = useAuth()
  const isAdmin = can('Admin')

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
          notifications.show({
            message: `Now ${chapterRangeLabel(result.chapterStart, result.chapterEnd).toLowerCase()}`,
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
                ? `Approved — queued ${result.queuedCount} chapter(s)`
                : 'Approved',
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
          notifications.show({ message: 'Request rejected', color: 'gray' })
        },
      },
    )
  }

  return (
    <>
      <PageHeader
        title="Requests"
        description={
          isAdmin
            ? 'What readers without add or download permissions have asked for. Approving adds the series and queues the chapters.'
            : 'Series and chapters you have asked an admin for.'
        }
      />

      <Group mb="lg">
        <SegmentedControl
          value={filter}
          onChange={(v) => setFilter(v as RequestFilter)}
          data={[
            { value: 'pending', label: 'Pending' },
            { value: 'resolved', label: 'Resolved' },
            { value: 'all', label: 'All' },
          ]}
        />
      </Group>

      {(approve.isError || reject.isError || remove.isError || edit.isError) && (
        <Alert color="red" variant="light" mb="md">
          {String(approve.error ?? reject.error ?? remove.error ?? edit.error)}
        </Alert>
      )}

      {!isPending && (requests?.length ?? 0) === 0 ? (
        <EmptyState
          icon={IconInbox}
          title={filter === 'pending' ? 'No pending requests' : 'Nothing here'}
          description={
            isAdmin
              ? 'Requests filed from the Request series page and from series pages show up here.'
              : 'Search for a series and request it — it will show up here once you do.'
          }
        />
      ) : (
        <Stack gap="xs">
          {requests?.map((r) => (
            <Paper key={r.id} withBorder radius="lg" p="sm">
              <Group wrap="nowrap" align="flex-start">
                <div
                  style={{
                    width: 48,
                    height: 72,
                    flexShrink: 0,
                    borderRadius: 8,
                    overflow: 'hidden',
                    background: 'var(--surface-2)',
                  }}
                >
                  {r.coverUrl && <Image src={r.coverUrl} w={48} h={72} fit="cover" alt="" />}
                </div>

                <div style={{ flex: 1, minWidth: 0 }}>
                  <Group gap="xs" wrap="nowrap">
                    <Text fw={650} lineClamp={1}>
                      {r.title}
                    </Text>
                    {r.year && (
                      <Text size="sm" c="dimmed" className="tnum">
                        {r.year}
                      </Text>
                    )}
                    <Badge size="sm" variant="light" color={STATUS_COLOR[r.status]}>
                      {r.status}
                    </Badge>
                    <Badge size="sm" variant="outline" color="gray">
                      {r.kind === 'NewSeries' ? 'New series' : 'Chapters'}
                    </Badge>
                  </Group>

                  <Group gap="xs" mt={4}>
                    <Text size="sm" c={r.editedAt ? undefined : 'dimmed'} fw={r.editedAt ? 600 : undefined}>
                      {chapterRangeLabel(r.chapterStart, r.chapterEnd)}
                    </Text>
                    <Text size="sm" c="dimmed">
                      ·
                    </Text>
                    <Text size="sm" c="dimmed">
                      {r.requestedBy}, {new Date(r.created).toLocaleDateString()}
                    </Text>
                  </Group>

                  {/* The admin's range is what will be queued, so it leads — but what was actually
                      asked for has to stay visible, or a trimmed request reads as the requester's
                      own. */}
                  {r.editedAt && (
                    <Text size="xs" c="dimmed" mt={2}>
                      Adjusted{r.editedBy ? ` by ${r.editedBy}` : ''} — asked for{' '}
                      {chapterRangeLabel(r.originalChapterStart, r.originalChapterEnd).toLowerCase()}
                    </Text>
                  )}

                  {r.note && (
                    <Text size="sm" mt={4} style={{ whiteSpace: 'pre-line' }}>
                      {r.note}
                    </Text>
                  )}

                  {r.status !== 'Pending' && (
                    <Text size="xs" c="dimmed" mt={4}>
                      {r.status === 'Approved' ? 'Approved' : 'Rejected'}
                      {r.resolvedBy ? ` by ${r.resolvedBy}` : ''}
                      {r.queuedCount != null && r.status === 'Approved'
                        ? ` — queued ${r.queuedCount} chapter(s)`
                        : ''}
                      {r.resolutionNote ? ` · ${r.resolutionNote}` : ''}
                    </Text>
                  )}
                </div>

                <Group gap="xs" wrap="nowrap">
                  {r.seriesId != null && (
                    <Tooltip label="Open series" withArrow>
                      <ActionIcon
                        component={Link}
                        to={`/series/${r.seriesId}`}
                        variant="subtle"
                        color="gray"
                        aria-label={`Open ${r.title}`}
                      >
                        <IconExternalLink size={17} />
                      </ActionIcon>
                    </Tooltip>
                  )}
                  {isAdmin && r.status === 'Pending' && (
                    <>
                      <Tooltip label="Change the chapter range" withArrow>
                        <ActionIcon
                          variant="subtle"
                          color="gray"
                          aria-label={`Edit request for ${r.title}`}
                          onClick={() => openEdit(r)}
                        >
                          <IconPencil size={17} />
                        </ActionIcon>
                      </Tooltip>
                      <Button
                        size="xs"
                        variant="light"
                        color="green"
                        leftSection={<IconCheck size={15} />}
                        onClick={() => openApprove(r)}
                      >
                        Approve
                      </Button>
                      <Button
                        size="xs"
                        variant="subtle"
                        color="red"
                        leftSection={<IconX size={15} />}
                        onClick={() => {
                          setRejecting(r)
                          setRejectNote('')
                        }}
                      >
                        Reject
                      </Button>
                    </>
                  )}
                  {(isAdmin || r.status === 'Pending') && (
                    <Tooltip label={isAdmin ? 'Delete request' : 'Cancel request'} withArrow>
                      <ActionIcon
                        variant="subtle"
                        color="red"
                        aria-label="Remove request"
                        onClick={() => remove.mutate(r.id)}
                        loading={remove.isPending && remove.variables === r.id}
                      >
                        <IconTrash size={17} />
                      </ActionIcon>
                    </Tooltip>
                  )}
                </Group>
              </Group>
            </Paper>
          ))}
        </Stack>
      )}

      <Modal opened={approving !== null} onClose={() => setApproving(null)} title="Approve request">
        <Stack gap="sm">
          <Text size="sm">
            {approving?.title} — {chapterRangeLabel(approving?.chapterStart ?? null, approving?.chapterEnd ?? null)}
          </Text>

          {needsRootFolder && (
            <Select
              label="Root folder"
              description="Where the series will live. The requester doesn't choose this."
              data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
              value={rootFolderId}
              onChange={setRootFolderId}
            />
          )}

          <Textarea
            label="Note (optional)"
            placeholder="Shown to whoever asked"
            value={approveNote}
            onChange={(e) => setApproveNote(e.currentTarget.value)}
            autosize
            minRows={2}
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setApproving(null)}>
              Cancel
            </Button>
            <Button
              color="green"
              onClick={submitApprove}
              loading={approve.isPending}
              disabled={needsRootFolder && !rootFolderId}
            >
              Approve and queue
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={editing !== null} onClose={() => setEditing(null)} title="Edit request">
        <Stack gap="sm">
          <Text size="sm">
            {editing?.title} — asked for{' '}
            {chapterRangeLabel(
              editing?.originalChapterStart ?? editing?.chapterStart ?? null,
              editing?.originalChapterEnd ?? editing?.chapterEnd ?? null,
            ).toLowerCase()}
          </Text>

          <Group gap="sm" align="flex-end" wrap="nowrap">
            <NumberInput
              label="From"
              placeholder="first"
              value={editStart}
              onChange={(v) => setEditStart(typeof v === 'number' ? v : '')}
              min={0}
              step={1}
              decimalScale={3}
              w={130}
            />
            <NumberInput
              label="To"
              placeholder="latest"
              value={editEnd}
              onChange={(v) => setEditEnd(typeof v === 'number' ? v : '')}
              min={0}
              step={1}
              decimalScale={3}
              w={130}
            />
          </Group>
          <Text size="xs" c="dimmed">
            Leave a field blank for no bound. Approving queues exactly this range.
          </Text>

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setEditing(null)}>
              Cancel
            </Button>
            <Button onClick={submitEdit} loading={edit.isPending}>
              Save range
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={rejecting !== null} onClose={() => setRejecting(null)} title="Reject request">
        <Stack gap="sm">
          <Text size="sm">{rejecting?.title}</Text>
          <Textarea
            label="Reason (optional)"
            placeholder="Shown to whoever asked"
            value={rejectNote}
            onChange={(e) => setRejectNote(e.currentTarget.value)}
            autosize
            minRows={2}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRejecting(null)}>
              Cancel
            </Button>
            <Button color="red" onClick={submitReject} loading={reject.isPending}>
              Reject
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  )
}
