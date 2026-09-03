import { Avatar, Badge, Group, Menu, Text, UnstyledButton } from '@mantine/core'
import { IconChevronDown, IconLogout, IconSettings, IconShieldLock } from '@tabler/icons-react'
import { useNavigate } from 'react-router-dom'
import { useLogout } from '../api/auth'
import { useAuth } from '../auth/AuthProvider'

/** Two initials from the display name, or the username. */
function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return (parts[0][0] + parts[1][0]).toUpperCase()
}

/**
 * `full` is the sidebar-footer form: avatar, name and a chevron in a row. The bare avatar is kept
 * for anywhere the account has to fit into a toolbar.
 */
export function UserMenu({ full = false }: { full?: boolean }) {
  const { me } = useAuth()
  const navigate = useNavigate()
  const logout = useLogout()

  if (!me) return null

  const name = me.displayName?.trim() || me.userName

  return (
    <Menu position={full ? 'top-start' : 'bottom-end'} width={220} withinPortal>
      <Menu.Target>
        {full ? (
          <UnstyledButton aria-label="Account" className="user-row">
            <Group gap={10} wrap="nowrap">
              <Avatar radius="xl" size={30} color="gray">
                {initials(name)}
              </Avatar>
              <Text fz="sm" fw={600} c="var(--ink-2)" truncate style={{ flex: 1, minWidth: 0 }}>
                {name}
              </Text>
              <IconChevronDown size={15} stroke={1.8} color="var(--ink-4)" />
            </Group>
          </UnstyledButton>
        ) : (
          <UnstyledButton aria-label="Account">
            <Avatar radius="xl" size={30} color="gray">
              {initials(name)}
            </Avatar>
          </UnstyledButton>
        )}
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>
          <Text fz="sm" fw={600} truncate>
            {name}
          </Text>
          {me.isAdmin && (
            <Badge size="xs" variant="light" mt={4} leftSection={<IconShieldLock size={10} />}>
              Administrator
            </Badge>
          )}
        </Menu.Label>
        <Menu.Divider />
        <Menu.Item
          leftSection={<IconSettings size={16} />}
          onClick={() => navigate('/settings#account')}
        >
          My account
        </Menu.Item>
        <Menu.Item
          color="red"
          leftSection={<IconLogout size={16} />}
          // No navigation afterwards: clearing the cached identity re-renders AuthGate into the
          // login screen on its own, and the query cache is dropped so nothing of this user's
          // library is left behind for whoever signs in next.
          onClick={() => logout.mutate()}
        >
          Sign out
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  )
}
