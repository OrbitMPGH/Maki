import { useMemo, useState } from 'react'
import {
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  Group,
  Modal,
  MultiSelect,
  PasswordInput,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconPlus } from '@tabler/icons-react'
import {
  useCreateUser,
  useDeleteUser,
  useUpdateUser,
  useUsers,
  type Permission,
  type SaveUserBody,
  type UserSummary,
} from '../../api/auth'
import { CONTENT_RATINGS, CONTENT_RATING_LABELS, useRootFolders } from '../../api/hooks'
import { useAuth } from '../../auth/AuthProvider'
import { formatDateTime } from '../../format'
import { useLabel } from '../../i18n-context'
import { useLingui } from '@lingui/react'
import { Trans, Plural, useLingui as useLinguiMacro } from '@lingui/react/macro'
import { msg, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'

/**
 * Grantable permissions, in the order they read best. `Admin` is deliberately not in this list: it is
 * a separate switch, because it implies every other one and mixing it into the same checkbox group
 * makes that invisible.
 *
 * Descriptors, not strings: this table is built once when the module loads, so a rendered string here
 * would be stuck in whichever language was active at that moment. `useGrantablePermissions` renders
 * them at the call site.
 */
const GRANTABLE_DEFS: { value: Exclude<Permission, 'Admin'>; label: MessageDescriptor; hint: MessageDescriptor }[] = [
  { value: 'AddSeries', label: msg`Add series`, hint: msg`Search sources and add new series to the library` },
  { value: 'DeleteSeries', label: msg`Delete series`, hint: msg`Remove series, and optionally their files, from disk` },
  { value: 'DownloadChapters', label: msg`Download chapters`, hint: msg`Queue downloads and grab torrent releases` },
  { value: 'ManageDownloadQueue', label: msg`Manage queue`, hint: msg`Retry and cancel queued downloads` },
  { value: 'ManageSources', label: msg`Manage sources`, hint: msg`Link and unlink per-series sources` },
  { value: 'EditMetadata', label: msg`Edit metadata`, hint: msg`Refresh metadata, rewrite ComicInfo, change monitoring` },
  { value: 'ManageTags', label: msg`Manage tags`, hint: msg`Create, rename and assign library tags` },
  { value: 'ChangeContentRating', label: msg`Change own content rating`, hint: msg`Raise or lower their own maximum rating` },
  { value: 'UseTrackers', label: msg`Use trackers`, hint: msg`Connect their own AniList, MAL or Kitsu account` },
  { value: 'UseOpds', label: msg`Use OPDS`, hint: msg`Hold a feed token and read through an OPDS app` },
  { value: 'ImportLibrary', label: msg`Import library`, hint: msg`Scan root folders and adopt existing series` },
]

function useGrantablePermissions() {
  const { _, i18n } = useLingui()
  return useMemo(
    () => GRANTABLE_DEFS.map((g) => ({ value: g.value, label: _(g.label), hint: _(g.hint) })),
    [_, i18n.locale],
  )
}

/** Mirrors MakiPermission's bit positions. Values are persisted, so this order is part of the schema. */
const BIT: Record<Permission, number> = {
  Admin: 1 << 0,
  AddSeries: 1 << 1,
  DeleteSeries: 1 << 2,
  DownloadChapters: 1 << 3,
  ManageDownloadQueue: 1 << 4,
  ManageSources: 1 << 5,
  EditMetadata: 1 << 6,
  ManageTags: 1 << 7,
  ChangeContentRating: 1 << 8,
  UseTrackers: 1 << 9,
  UseOpds: 1 << 10,
  ImportLibrary: 1 << 11,
}

export function UsersSection() {
  const renderLabel = useLabel()
  const { data: users } = useUsers()
  const { me } = useAuth()
  const [editing, setEditing] = useState<UserSummary | 'new' | null>(null)
  const remove = useDeleteUser()

  return (
    <Card withBorder radius="md" padding="md" id="users">
      <Group justify="space-between" mb="sm">
        <Title order={4}>
          <Trans>Users</Trans>
        </Title>
        <Button size="xs" leftSection={<IconPlus size={14} />} onClick={() => setEditing('new')}>
          <Trans>Add user</Trans>
        </Button>
      </Group>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Each account has its own login, permissions and content rating. Reading progress is shared
          across accounts for now; per-user history arrives with the next release.
        </Trans>
      </Text>

      <Table.ScrollContainer minWidth={576}>
        <Table striped withTableBorder fz="sm">
          <Table.Thead>
            <Table.Tr>
              <Table.Th><Trans>User</Trans></Table.Th>
              <Table.Th><Trans>Permissions</Trans></Table.Th>
              <Table.Th><Trans>Rating</Trans></Table.Th>
              <Table.Th><Trans>Last sign-in</Trans></Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {users?.map((user) => {
              const granted = user.permissionNames.length
              return (
                <Table.Tr key={user.id} opacity={user.disabled ? 0.5 : 1}>
                  <Table.Td>
                    <Group gap={6}>
                      <Text fz="sm">{user.displayName?.trim() || user.userName}</Text>
                      {user.id === me?.id && (
                        <Badge size="xs" variant="outline">
                          <Trans>you</Trans>
                        </Badge>
                      )}
                      {user.disabled && (
                        <Badge size="xs" color="red" variant="light">
                          <Trans>disabled</Trans>
                        </Badge>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {user.isAdmin ? (
                      <Badge size="xs" variant="light">
                        <Trans>Administrator</Trans>
                      </Badge>
                    ) : (
                      <Text fz="xs" c="dimmed">
                        <Plural value={granted} one="# granted" other="# granted" />
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Text fz="xs" c="dimmed">
                      {renderLabel(CONTENT_RATING_LABELS[user.maxContentRating] ?? user.maxContentRating)}
                    </Text>
                  </Table.Td>
                  <Table.Td c="dimmed">
                    {user.lastLoginAt ? formatDateTime(user.lastLoginAt) : <Trans>never</Trans>}
                  </Table.Td>
                  <Table.Td ta="right">
                    <Group gap={4} justify="flex-end">
                      <Button size="compact-xs" variant="subtle" onClick={() => setEditing(user)}>
                        <Trans>Edit</Trans>
                      </Button>
                      {/* Hidden for your own row: the server refuses it anyway, so offering it would
                          only produce an error message. */}
                      {user.id !== me?.id && (
                        <Button
                          size="compact-xs"
                          variant="subtle"
                          color="red"
                          onClick={() =>
                            remove.mutate(user.id, {
                              onError: (e) => notifications.show({ message: e.message, color: 'red' }),
                            })
                          }
                        >
                          <Trans>Delete</Trans>
                        </Button>
                      )}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              )
            })}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>

      {editing && <UserModal target={editing} onClose={() => setEditing(null)} />}
    </Card>
  )
}

function UserModal({ target, onClose }: { target: UserSummary | 'new'; onClose: () => void }) {
  const { t } = useLinguiMacro()
  const renderLabel = useLabel()
  const grantable = useGrantablePermissions()
  const isNew = target === 'new'
  const existing = isNew ? null : target
  const { me } = useAuth()
  const { data: rootFolders } = useRootFolders()
  const create = useCreateUser()
  const update = useUpdateUser()

  const [username, setUsername] = useState(existing?.userName ?? '')
  const [displayName, setDisplayName] = useState(existing?.displayName ?? '')
  const [password, setPassword] = useState('')
  const [isAdmin, setIsAdmin] = useState(existing?.isAdmin ?? false)
  const [granted, setGranted] = useState<Set<string>>(
    new Set(existing?.permissionNames.filter((p) => p !== 'Admin') ?? ['UseOpds', 'UseTrackers']),
  )
  const [rating, setRating] = useState(existing?.maxContentRating ?? 'safe')
  const [allRootFolders, setAllRootFolders] = useState(existing?.allRootFolders ?? false)
  const [folderIds, setFolderIds] = useState<string[]>(
    existing?.rootFolderIds.map(String) ?? [],
  )
  const [disabled, setDisabled] = useState(existing?.disabled ?? false)

  const editingSelf = existing?.id === me?.id
  const busy = create.isPending || update.isPending

  function permissionsValue(): number {
    if (isAdmin) return BIT.Admin
    let value = 0
    for (const name of granted) value |= BIT[name as Permission] ?? 0
    return value
  }

  function submit() {
    const body: SaveUserBody = {
      username: username.trim(),
      displayName: displayName.trim() || undefined,
      permissions: permissionsValue(),
      maxContentRating: rating,
      allRootFolders,
      rootFolderIds: allRootFolders ? [] : folderIds.map(Number),
      disabled,
    }
    if (password) body.password = password

    const onError = (e: Error) => notifications.show({ message: e.message, color: 'red' })
    const onSuccess = () => {
      notifications.show({ message: isNew ? now`User created` : now`User updated`, color: 'green' })
      onClose()
    }

    if (isNew) {
      create.mutate(body, { onSuccess, onError })
    } else {
      update.mutate({ id: existing!.id, ...body }, { onSuccess, onError })
    }
  }

  const editName = existing?.userName

  return (
    <Modal
      opened
      onClose={onClose}
      title={isNew ? t`Add user` : t`Edit ${editName}`}
      centered
      size="lg"
    >
      <Stack>
        <TextInput
          label={t`Username`}
          required
          value={username}
          onChange={(e) => setUsername(e.currentTarget.value)}
        />
        <TextInput
          label={t`Display name`}
          value={displayName}
          onChange={(e) => setDisplayName(e.currentTarget.value)}
        />
        <PasswordInput
          label={isNew ? t`Password` : t`New password`}
          description={isNew ? t`At least 10 characters` : t`Leave blank to keep the current one`}
          required={isNew}
          value={password}
          onChange={(e) => setPassword(e.currentTarget.value)}
        />

        <Switch
          label={t`Administrator`}
          description={t`Full access, including settings, root folders, backups and user management.`}
          checked={isAdmin}
          // An admin cannot demote themselves: the server refuses it, since the last admin standing
          // could otherwise lock the instance out of its own settings.
          disabled={editingSelf && existing?.isAdmin}
          onChange={(e) => setIsAdmin(e.currentTarget.checked)}
        />
        {editingSelf && existing?.isAdmin && (
          <Alert variant="light" color="blue">
            <Trans>
              You cannot remove your own administrator permission. Promote another account first, then
              edit this one from there.
            </Trans>
          </Alert>
        )}

        {!isAdmin && (
          <Stack gap={6}>
            <Text fz="sm" fw={500}>
              <Trans>Permissions</Trans>
            </Text>
            {grantable.map((permission) => (
              <Checkbox
                key={permission.value}
                label={permission.label}
                description={permission.hint}
                checked={granted.has(permission.value)}
                onChange={(e) => {
                  const next = new Set(granted)
                  if (e.currentTarget.checked) next.add(permission.value)
                  else next.delete(permission.value)
                  setGranted(next)
                }}
              />
            ))}
          </Stack>
        )}

        <Select
          label={t`Maximum content rating`}
          description={t`Caps what Discover will show this account.`}
          data={CONTENT_RATINGS.map((r) => ({ value: r, label: renderLabel(CONTENT_RATING_LABELS[r]) }))}
          value={rating}
          onChange={(v) => setRating(v ?? 'safe')}
          allowDeselect={false}
        />

        <Switch
          label={t`Access all libraries`}
          description={t`Including root folders added later.`}
          checked={allRootFolders}
          onChange={(e) => setAllRootFolders(e.currentTarget.checked)}
        />
        {!allRootFolders && (
          <MultiSelect
            label={t`Libraries`}
            description={t`With none selected this account sees an empty library: access is granted, never assumed.`}
            data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
            value={folderIds}
            onChange={setFolderIds}
          />
        )}

        {!isNew && (
          <Switch
            label={t`Disabled`}
            description={t`Blocks sign-in and kills existing sessions. Keeps the account and its history.`}
            checked={disabled}
            disabled={editingSelf}
            onChange={(e) => setDisabled(e.currentTarget.checked)}
          />
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            loading={busy}
            disabled={!username.trim() || (isNew && password.length < 10)}
            onClick={submit}
          >
            {isNew ? <Trans>Create</Trans> : <Trans>Save</Trans>}
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
