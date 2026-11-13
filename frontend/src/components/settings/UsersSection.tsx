import { type FormEvent, type ReactNode, useMemo, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Checkbox,
  Group,
  Modal,
  MultiSelect,
  PasswordInput,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  VisuallyHidden,
} from '@mantine/core'
import { notifications } from '../../lib/toast'
import {
  IconBan,
  IconDownload,
  IconLibrary,
  IconBook,
  IconPencil,
  IconPlus,
  IconShield,
  IconTrash,
} from '../../lib/icons'
import {
  useCreateUser,
  useDeleteUser,
  useResetUserTwoFactor,
  useSetUserMode,
  useUnlinkUserOidc,
  useUpdateUser,
  useUserMode,
  useLoginNames,
  useSetLoginNames,
  useUsers,
  type Permission,
  type SaveUserBody,
  type UserSummary,
} from '../../api/auth'
import { CONTENT_RATINGS, CONTENT_RATING_LABELS, useRootFolders } from '../../api/hooks'
import { useAuth } from '../../auth/AuthProvider'
import { useLabel } from '../../i18n-context'
import { useLingui } from '@lingui/react'
import { Trans, Plural, useLingui as useLinguiMacro } from '@lingui/react/macro'
import { msg, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { ModalFooter, ModalHeading, ModalNote } from '../ui/ModalShell'
import { contentRatingVisual } from '../ui/status'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import { TimeAgo } from '../ui/TimeAgo'

/**
 * Grantable permissions, in the order they read best. `Admin` is deliberately not in this list: it is
 * a separate switch, because it implies every other one and mixing it into the same checkbox group
 * makes that invisible.
 *
 * Descriptors, not strings: this table is built once when the module loads, so a rendered string here
 * would be stuck in whichever language was active at that moment. `useGrantablePermissions` renders
 * them at the call site.
 */
type PermissionGroup = 'library' | 'downloads' | 'sharing'

const GRANTABLE_DEFS: {
  value: Exclude<Permission, 'Admin'>
  group: PermissionGroup
  label: MessageDescriptor
  hint?: MessageDescriptor
}[] = [
  { value: 'AddSeries', group: 'library', label: msg`Add series` },
  { value: 'DeleteSeries', group: 'library', label: msg`Delete series` },
  { value: 'DownloadChapters', group: 'downloads', label: msg`Download chapters` },
  { value: 'ManageDownloadQueue', group: 'downloads', label: msg`Manage queue` },
  { value: 'ManageSources', group: 'downloads', label: msg`Manage sources` },
  {
    value: 'EditMetadata',
    group: 'library',
    label: msg`Edit metadata`,
    hint: msg`Refresh metadata, rewrite ComicInfo, change monitoring`,
  },
  { value: 'ManageTags', group: 'library', label: msg`Manage tags` },
  { value: 'ChangeContentRating', group: 'sharing', label: msg`Change own content rating` },
  { value: 'UseTrackers', group: 'sharing', label: msg`Use trackers` },
  { value: 'UseOpds', group: 'sharing', label: msg`Use OPDS` },
  { value: 'ImportLibrary', group: 'library', label: msg`Import library` },
  { value: 'ArchiveSeries', group: 'library', label: msg({ message: 'Archive series', context: 'series' }) },
]

function useGrantablePermissions() {
  const { _, i18n } = useLingui()
  return useMemo(
    () => GRANTABLE_DEFS.map((g) => ({ value: g.value, group: g.group, label: _(g.label), hint: g.hint && _(g.hint) })),
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
  ArchiveSeries: 1 << 12,
}

/**
 * Single-user mode hides Requests, the Stats reader picker and the leaderboard. Kept beside the user
 * list so whoever is about to add a second account sees that those exist, and that adding one turns
 * them back on.
 */
function UserModeSwitch() {
  const { t } = useLinguiMacro()
  const { data: mode } = useUserMode()
  const save = useSetUserMode()

  const checked = save.isPending && save.variables !== undefined ? save.variables : mode?.singleUser

  // The wrapper is there before the mode loads and stays the same element after, so a palette deep
  // link finds it on arrival and its flash class is not wiped when the switch fills in.
  return (
    <div id="setting-user-mode" className="settings-inline-target users-switch-row">
      {mode && (
        <Switch
          checked={checked}
          disabled={!mode.available || save.isPending}
          onChange={(e) => save.mutate(e.currentTarget.checked)}
          labelPosition="left"
          label={t`Single-user mode`}
          description={
            mode.available
              ? t`Hides the controls for sharing the instance.`
              : t`Multi-user mode is on because more than one account is enabled. Disable or delete the others to switch back.`
          }
        />
      )}
    </div>
  )
}

/**
 * Login names are hidden from everyone but admins and their owner, since one is half of a password
 * login. A household that knows each other can show them instead of "Another reader".
 */
function LoginNamesSwitch() {
  const { t } = useLinguiMacro()
  const { data: setting } = useLoginNames()
  const save = useSetLoginNames()

  const checked = save.isPending && save.variables !== undefined ? save.variables : setting?.visible

  return (
    <div id="setting-login-names" className="settings-inline-target users-switch-row">
      {setting && (
        <Switch
          checked={checked}
          disabled={save.isPending}
          onChange={(e) => save.mutate(e.currentTarget.checked)}
          labelPosition="left"
          label={t`Show login names to everyone`}
          description={t`Readers without a display name appear under their login name on shared notes, the leaderboard and edit history, instead of "Another reader". Anyone who can sign in then sees every username.`}
        />
      )}
    </div>
  )
}

export function UsersSection() {
  const { t } = useLinguiMacro()
  const renderLabel = useLabel()
  const { data: users } = useUsers()
  const { me, singleUser } = useAuth()
  const [editing, setEditing] = useState<UserSummary | 'new' | null>(null)
  const [deleting, setDeleting] = useState<UserSummary | null>(null)
  const remove = useDeleteUser()
  const deletingName = deleting ? deleting.displayName?.trim() || deleting.userName : ''

  const accountCount = users?.length

  return (
    <SettingsSection
      id="users"
      title={<Trans>Users</Trans>}
      actions={
        <>
          {accountCount !== undefined && (
            <span className="users-count">
              <Plural value={accountCount} one="# account" other="# accounts" />
            </span>
          )}
          <Button size="xs" leftSection={<IconPlus size={16} />} onClick={() => setEditing('new')}>
            <Trans>Add user</Trans>
          </Button>
        </>
      }
      panelProps={{ p: 0 }}
    >
      <UserModeSwitch />
      {!singleUser && <LoginNamesSwitch />}

      <Table.ScrollContainer minWidth={576}>
        <Table className="panel-table ops-table users-table">
          <Table.Thead>
            <Table.Tr>
              <Table.Th><Trans>User</Trans></Table.Th>
              <Table.Th><Trans>Permissions</Trans></Table.Th>
              <Table.Th><Trans>Rating</Trans></Table.Th>
              <Table.Th><Trans>Last sign-in</Trans></Table.Th>
              <Table.Th><VisuallyHidden><Trans>Actions</Trans></VisuallyHidden></Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {users?.map((user) => {
              const granted = user.permissionNames.length
              const name = user.displayName?.trim() || user.userName
              const rating = contentRatingVisual(user.maxContentRating)
              return (
                <Table.Tr key={user.id} data-muted={user.disabled || undefined}>
                  <Table.Td>
                    <Group gap={10} wrap="nowrap">
                      <span className="user-avatar" aria-hidden>
                        {name.charAt(0).toUpperCase()}
                      </span>
                      <Text fz="sm" fw={600} c="var(--ink-hi)">{name}</Text>
                      {user.id === me?.id && (
                        <Badge size="xs" variant="outline">
                          <Trans>you</Trans>
                        </Badge>
                      )}
                      {user.disabled && (
                        <span className="user-disabled">
                          <IconBan size={14} aria-hidden />
                          <Trans>disabled</Trans>
                        </span>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {user.isAdmin ? (
                      <Badge size="sm" variant="light" leftSection={<IconShield size={12} />}>
                        <Trans>Administrator</Trans>
                      </Badge>
                    ) : (
                      <Badge size="sm" variant="default">
                        <Plural value={granted} one="# permission" other="# permissions" />
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td>
                    {rating ? (
                      <Badge size="sm" variant="light" color={rating.color} leftSection={<rating.Icon size={12} />}>
                        {renderLabel(rating.label)}
                      </Badge>
                    ) : (
                      <Text fz="xs" c="var(--ink-3)">
                        {renderLabel(CONTENT_RATING_LABELS[user.maxContentRating] ?? user.maxContentRating)}
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td c="var(--ink-3)">
                    {user.lastLoginAt ? <TimeAgo value={user.lastLoginAt} /> : <Trans>never</Trans>}
                  </Table.Td>
                  <Table.Td ta="right">
                    <Group gap={2} justify="flex-end" wrap="nowrap">
                      <ActionIcon
                        variant="subtle"
                        color="var(--neutral)"
                        data-row-action
                        aria-label={t`Edit`}
                        onClick={() => setEditing(user)}
                      >
                        <IconPencil size={16} />
                      </ActionIcon>
                      {/* Hidden for your own row: the server refuses it anyway, so offering it would
                          only produce an error message. */}
                      {user.id !== me?.id && (
                        <ActionIcon
                          variant="subtle"
                          color="var(--danger)"
                          data-row-action="danger"
                          aria-label={t`Delete`}
                          onClick={() => setDeleting(user)}
                        >
                          <IconTrash size={16} />
                        </ActionIcon>
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

      <ConfirmDialog
        opened={deleting !== null}
        onClose={() => setDeleting(null)}
        title={<Trans>Delete {deletingName}?</Trans>}
        confirmLabel={<Trans>Delete user</Trans>}
        loading={remove.isPending}
        onConfirm={() =>
          deleting &&
          remove.mutate(deleting.id, {
            onSuccess: () => setDeleting(null),
          })
        }
      >
        <Trans>They will no longer be able to sign in. This can't be undone.</Trans>
      </ConfirmDialog>
    </SettingsSection>
  )
}

function UserModal({ target, onClose }: { target: UserSummary | 'new'; onClose: () => void }) {
  const { t } = useLinguiMacro()
  const renderLabel = useLabel()
  const grantable = useGrantablePermissions()
  const isNew = target === 'new'
  const { data: users } = useUsers()
  // Read from the list rather than the row that opened the modal, so a reset shows up here.
  const existing = target === 'new' ? null : (users?.find((u) => u.id === target.id) ?? target)
  const { me } = useAuth()
  const { data: rootFolders } = useRootFolders()
  const create = useCreateUser()
  const update = useUpdateUser()
  const resetTwoFactor = useResetUserTwoFactor()
  const unlinkOidc = useUnlinkUserOidc()
  const [confirming, setConfirming] = useState<'twoFactor' | 'sso' | null>(null)

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
      // An empty string clears the name on edit; leaving it out would keep the old one.
      displayName: isNew ? displayName.trim() || undefined : displayName.trim(),
      permissions: permissionsValue(),
      maxContentRating: rating,
      allRootFolders,
      // Left out while every folder is granted so unticking later restores the earlier selection.
      rootFolderIds: allRootFolders ? undefined : folderIds.map(Number),
      disabled,
    }
    if (password) body.password = password

    const onSuccess = () => {
      notifications.show({ message: isNew ? now`User created` : now`User updated`, color: 'var(--ok)' })
      onClose()
    }

    if (isNew) {
      create.mutate(body, { onSuccess })
    } else {
      update.mutate({ id: existing!.id, ...body }, { onSuccess })
    }
  }

  const editName = existing?.userName

  const grantedCount = granted.size
  const permissionGroups: { key: PermissionGroup; label: ReactNode; icon: ReactNode }[] = [
    { key: 'library', label: <Trans>Library</Trans>, icon: <IconLibrary size={14} /> },
    { key: 'downloads', label: <Trans>Downloads</Trans>, icon: <IconDownload size={14} /> },
    { key: 'sharing', label: <Trans>Reading and sharing</Trans>, icon: <IconBook size={14} /> },
  ]
  const cannotSubmit = !username.trim() || (isNew && password.length < 10)

  return (
    <Modal
      opened
      onClose={onClose}
      closeOnEscape={!confirming}
      closeOnClickOutside={!confirming}
      title={
        <ModalHeading
          title={isNew ? t`Add user` : t`Edit ${editName}`}
          description={
            isNew ? (
              <Trans>They sign in with these details and see only what you grant here.</Trans>
            ) : (
              <Trans>Change the sign-in, permissions or library access for this account.</Trans>
            )
          }
        />
      }
      size="lg"
    >
      <Stack
        component="form"
        gap="md"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          if (cannotSubmit) return
          submit()
        }}
      >
        <SimpleGrid cols={{ base: 1, xs: 2 }}>
          <TextInput
            label={t`Username`}
            autoComplete="off"
            required
            value={username}
            onChange={(e) => setUsername(e.currentTarget.value)}
          />
          <TextInput
            label={t`Display name`}
            value={displayName}
            onChange={(e) => setDisplayName(e.currentTarget.value)}
          />
        </SimpleGrid>
        <PasswordInput
          label={isNew ? t`Password` : t`New password`}
          description={isNew ? t`At least 10 characters` : t`Leave blank to keep the current one`}
          autoComplete="new-password"
          required={isNew}
          value={password}
          onChange={(e) => setPassword(e.currentTarget.value)}
        />

        <div className="user-form-group">
          <div className="user-form-title">
            <Trans>Admin</Trans>
          </div>
          <Switch
            label={t`Administrator`}
            description={t`Grants every permission and the Settings pages.`}
            checked={isAdmin}
            // An admin cannot demote themselves: the server refuses it, since the last admin standing
            // could otherwise lock the instance out of its own settings.
            disabled={editingSelf && existing?.isAdmin}
            onChange={(e) => setIsAdmin(e.currentTarget.checked)}
          />
          {editingSelf && existing?.isAdmin && (
            <ModalNote tone="info">
              <Trans>
                You cannot remove your own administrator permission. Promote another account first, then
                edit this one from there.
              </Trans>
            </ModalNote>
          )}
        </div>

        {!isAdmin && (
          <div className="user-form-group">
            <div className="user-form-title">
              <Trans>Permissions</Trans>
            </div>
            <SimpleGrid cols={{ base: 1, xs: 2 }} spacing="lg" verticalSpacing="md">
              {permissionGroups.map((group) => (
                <Stack key={group.key} gap={6} className="user-permission-group">
                  <div className="user-form-heading">
                    {group.icon}
                    {group.label}
                  </div>
                  {grantable
                    .filter((permission) => permission.group === group.key)
                    .map((permission) => (
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
              ))}
            </SimpleGrid>
          </div>
        )}

        <div className="user-form-group">
          <div className="user-form-title">
            <Trans>Library access</Trans>
          </div>
          <Select
            label={t`Maximum content rating`}
            data={CONTENT_RATINGS.map((r) => ({ value: r, label: renderLabel(CONTENT_RATING_LABELS[r]) }))}
            value={rating}
            onChange={(v) => setRating(v ?? 'safe')}
            allowDeselect={false}
          />
          <Switch
            label={t`Access all root folders`}
            description={t`Including root folders added later.`}
            checked={allRootFolders}
            onChange={(e) => setAllRootFolders(e.currentTarget.checked)}
          />
          {!allRootFolders && (
            <MultiSelect
              label={t`Root folders`}
              description={t`None selected means an empty library.`}
              data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
              value={folderIds}
              onChange={setFolderIds}
            />
          )}
        </div>

        {(!isNew || (existing && !editingSelf && (existing.twoFactorEnabled || existing.oidcLinked))) && (
          <div className="user-form-group">
            <div className="user-form-title">
              <Trans>Account</Trans>
            </div>
            {!isNew && (
              <Switch
                label={t`Disabled`}
                description={t`Blocks sign-in. Keeps the account and its history.`}
                checked={disabled}
                disabled={editingSelf}
                onChange={(e) => setDisabled(e.currentTarget.checked)}
              />
            )}
            {existing && !editingSelf && (existing.twoFactorEnabled || existing.oidcLinked) && (
              <Group gap="xs">
                {existing.twoFactorEnabled && (
                  <Button size="xs" variant="light" color="var(--danger)" onClick={() => setConfirming('twoFactor')}>
                    <Trans>Reset two-factor</Trans>
                  </Button>
                )}
                {existing.oidcLinked && (
                  <Button size="xs" variant="light" color="var(--danger)" onClick={() => setConfirming('sso')}>
                    <Trans>Remove single sign-on</Trans>
                  </Button>
                )}
              </Group>
            )}
          </div>
        )}

        <ModalFooter
          status={
            isAdmin ? (
              <Trans>Administrator, every permission</Trans>
            ) : (
              <Plural value={grantedCount} one="# permission" other="# permissions" />
            )
          }
        >
          <Button variant="default" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button type="submit" loading={busy} disabled={cannotSubmit}>
            {isNew ? <Trans>Create</Trans> : <Trans>Save</Trans>}
          </Button>
        </ModalFooter>
      </Stack>

      <ConfirmDialog
        opened={confirming === 'twoFactor'}
        onClose={() => setConfirming(null)}
        title={<Trans>Reset two-factor for {editName}?</Trans>}
        confirmLabel={<Trans>Reset two-factor</Trans>}
        loading={resetTwoFactor.isPending}
        onConfirm={() =>
          existing &&
          resetTwoFactor.mutate(existing.id, {
            onSuccess: () => {
              setConfirming(null)
              notifications.show({ message: now`Two-factor turned off for this account`, color: 'var(--ok)' })
            },
          })
        }
      >
        <Trans>
          Their second factor is turned off and the authenticator reset, so a password is all that
          protects the account until they set it up again.
        </Trans>
      </ConfirmDialog>

      <ConfirmDialog
        opened={confirming === 'sso'}
        onClose={() => setConfirming(null)}
        title={<Trans>Remove single sign-on for {editName}?</Trans>}
        confirmLabel={<Trans>Remove single sign-on</Trans>}
        loading={unlinkOidc.isPending}
        onConfirm={() =>
          existing &&
          unlinkOidc.mutate(existing.id, {
            onSuccess: () => {
              setConfirming(null)
              notifications.show({ message: now`Single sign-on removed from this account`, color: 'var(--ok)' })
            },
          })
        }
      >
        <Trans>They will have to link single sign-on again, or use a password, to sign in.</Trans>
      </ConfirmDialog>
    </Modal>
  )
}
