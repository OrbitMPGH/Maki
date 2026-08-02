import { useState } from 'react'
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
import { useRootFolders } from '../../api/hooks'
import { useAuth } from '../../auth/AuthProvider'

/**
 * Grantable permissions, in the order they read best. `Admin` is deliberately not in this list — it is
 * a separate switch, because it implies every other one and mixing it into the same checkbox group
 * makes that invisible.
 */
const GRANTABLE: { value: Exclude<Permission, 'Admin'>; label: string; hint: string }[] = [
  { value: 'AddSeries', label: 'Add series', hint: 'Search sources and add new series to the library' },
  { value: 'DeleteSeries', label: 'Delete series', hint: 'Remove series, and optionally their files, from disk' },
  { value: 'DownloadChapters', label: 'Download chapters', hint: 'Queue downloads and grab torrent releases' },
  { value: 'ManageDownloadQueue', label: 'Manage queue', hint: 'Retry and cancel queued downloads' },
  { value: 'ManageSources', label: 'Manage sources', hint: 'Link and unlink per-series sources' },
  { value: 'EditMetadata', label: 'Edit metadata', hint: 'Refresh metadata, rewrite ComicInfo, change monitoring' },
  { value: 'ManageTags', label: 'Manage tags', hint: 'Create, rename and assign library tags' },
  { value: 'ChangeContentRating', label: 'Change own content rating', hint: 'Raise or lower their own maximum rating' },
  { value: 'UseTrackers', label: 'Use trackers', hint: 'Connect their own AniList, MAL or Kitsu account' },
  { value: 'UseOpds', label: 'Use OPDS', hint: 'Hold a feed token and read through an OPDS app' },
  { value: 'ImportLibrary', label: 'Import library', hint: 'Scan root folders and adopt existing series' },
]

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

const RATINGS = ['safe', 'suggestive', 'erotica', 'pornographic']

export function UsersSection() {
  const { data: users } = useUsers()
  const { me } = useAuth()
  const [editing, setEditing] = useState<UserSummary | 'new' | null>(null)
  const remove = useDeleteUser()

  return (
    <Card withBorder radius="md" padding="md" id="users">
      <Group justify="space-between" mb="sm">
        <Title order={4}>Users</Title>
        <Button size="xs" leftSection={<IconPlus size={14} />} onClick={() => setEditing('new')}>
          Add user
        </Button>
      </Group>
      <Text size="sm" c="dimmed" mb="md">
        Each account has its own login, permissions and content rating. Reading progress is shared
        across accounts for now — per-user history arrives with the next release.
      </Text>

      <Table striped withTableBorder fz="sm">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>User</Table.Th>
            <Table.Th>Permissions</Table.Th>
            <Table.Th>Rating</Table.Th>
            <Table.Th>Last sign-in</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {users?.map((user) => (
            <Table.Tr key={user.id} opacity={user.disabled ? 0.5 : 1}>
              <Table.Td>
                <Group gap={6}>
                  <Text fz="sm">{user.displayName?.trim() || user.userName}</Text>
                  {user.id === me?.id && (
                    <Badge size="xs" variant="outline">
                      you
                    </Badge>
                  )}
                  {user.disabled && (
                    <Badge size="xs" color="red" variant="light">
                      disabled
                    </Badge>
                  )}
                </Group>
              </Table.Td>
              <Table.Td>
                {user.isAdmin ? (
                  <Badge size="xs" variant="light">
                    Administrator
                  </Badge>
                ) : (
                  <Text fz="xs" c="dimmed">
                    {user.permissionNames.length} granted
                  </Text>
                )}
              </Table.Td>
              <Table.Td>
                <Text fz="xs" c="dimmed">
                  {user.maxContentRating}
                </Text>
              </Table.Td>
              <Table.Td c="dimmed">
                {user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleString() : 'never'}
              </Table.Td>
              <Table.Td ta="right">
                <Group gap={4} justify="flex-end">
                  <Button size="compact-xs" variant="subtle" onClick={() => setEditing(user)}>
                    Edit
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
                      Delete
                    </Button>
                  )}
                </Group>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>

      {editing && <UserModal target={editing} onClose={() => setEditing(null)} />}
    </Card>
  )
}

function UserModal({ target, onClose }: { target: UserSummary | 'new'; onClose: () => void }) {
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
      notifications.show({ message: isNew ? 'User created' : 'User updated', color: 'green' })
      onClose()
    }

    if (isNew) {
      create.mutate(body, { onSuccess, onError })
    } else {
      update.mutate({ id: existing!.id, ...body }, { onSuccess, onError })
    }
  }

  return (
    <Modal opened onClose={onClose} title={isNew ? 'Add user' : `Edit ${existing?.userName}`} centered size="lg">
      <Stack>
        <TextInput
          label="Username"
          required
          value={username}
          onChange={(e) => setUsername(e.currentTarget.value)}
        />
        <TextInput
          label="Display name"
          value={displayName}
          onChange={(e) => setDisplayName(e.currentTarget.value)}
        />
        <PasswordInput
          label={isNew ? 'Password' : 'New password'}
          description={isNew ? 'At least 10 characters' : 'Leave blank to keep the current one'}
          required={isNew}
          value={password}
          onChange={(e) => setPassword(e.currentTarget.value)}
        />

        <Switch
          label="Administrator"
          description="Full access, including settings, root folders, backups and user management."
          checked={isAdmin}
          // An admin cannot demote themselves — the server refuses it, since the last admin standing
          // could otherwise lock the instance out of its own settings.
          disabled={editingSelf && existing?.isAdmin}
          onChange={(e) => setIsAdmin(e.currentTarget.checked)}
        />
        {editingSelf && existing?.isAdmin && (
          <Alert variant="light" color="blue">
            You cannot remove your own administrator permission. Promote another account first, then
            edit this one from there.
          </Alert>
        )}

        {!isAdmin && (
          <Stack gap={6}>
            <Text fz="sm" fw={500}>
              Permissions
            </Text>
            {GRANTABLE.map((permission) => (
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
          label="Maximum content rating"
          description="Caps what Discover will show this account."
          data={RATINGS}
          value={rating}
          onChange={(v) => setRating(v ?? 'safe')}
          allowDeselect={false}
        />

        <Switch
          label="Access all libraries"
          description="Including root folders added later."
          checked={allRootFolders}
          onChange={(e) => setAllRootFolders(e.currentTarget.checked)}
        />
        {!allRootFolders && (
          <MultiSelect
            label="Libraries"
            description="With none selected this account sees an empty library — access is granted, never assumed."
            data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
            value={folderIds}
            onChange={setFolderIds}
          />
        )}

        {!isNew && (
          <Switch
            label="Disabled"
            description="Blocks sign-in and kills existing sessions. Keeps the account and its history."
            checked={disabled}
            disabled={editingSelf}
            onChange={(e) => setDisabled(e.currentTarget.checked)}
          />
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          <Button
            loading={busy}
            disabled={!username.trim() || (isNew && password.length < 10)}
            onClick={submit}
          >
            {isNew ? 'Create' : 'Save'}
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
