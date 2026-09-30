import { Avatar, Badge, Menu, Text, UnstyledButton } from '@mantine/core'
import { IconLogout, IconSelector, IconSettings, IconShieldLock } from '@tabler/icons-react'
import { useNavigate } from 'react-router-dom'
import { Trans } from '@lingui/react/macro'
import { useLogout } from '../api/auth'
import { useProgressSummary } from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'

/** Two initials from the display name, or the username. */
function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return (parts[0][0] + parts[1][0]).toUpperCase()
}

/** The account row at the foot of the sidebar: who is signed in, their level, and the account menu. */
export function UserMenu({ onNavigate }: { onNavigate?: () => void }) {
  const { me } = useAuth()
  const navigate = useNavigate()
  const logout = useLogout()
  const { data: progress } = useProgressSummary(undefined, !!me)

  if (!me) return null

  const name = me.displayName?.trim() || me.userName
  const level = progress?.enabled ? progress.level : null
  const levelNumber = level?.level ?? 0

  return (
    <Menu position="top-start" width={220} offset={6} withinPortal>
      <Menu.Target>
        <UnstyledButton className="nav-user">
          <Avatar radius="xl" size={30} color="brand" variant="filled" className="nav-user-avatar">
            {initials(name)}
          </Avatar>
          <span className="nav-user-body">
            <span className="nav-user-name">{name}</span>
            {level && (
              <span className="nav-user-level tnum">
                <Trans>Level {levelNumber}</Trans>
                <span className="nav-user-xp" aria-hidden>
                  <span style={{ width: `${Math.round(level.progress * 100)}%` }} />
                </span>
              </span>
            )}
          </span>
          <IconSelector size={16} stroke={1.7} className="nav-user-chevron" />
        </UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>
          <Text fz="sm" fw={600} truncate>
            {name}
          </Text>
          {me.isAdmin && (
            <Badge size="xs" variant="light" mt={4} leftSection={<IconShieldLock size={10} />}>
              <Trans>Administrator</Trans>
            </Badge>
          )}
        </Menu.Label>
        <Menu.Divider />
        <Menu.Item
          leftSection={<IconSettings size={16} />}
          onClick={() => {
            onNavigate?.()
            navigate('/settings#account')
          }}
        >
          <Trans>My account</Trans>
        </Menu.Item>
        <Menu.Item
          color="var(--danger)"
          leftSection={<IconLogout size={16} />}
          // No navigation afterwards: clearing the cached identity re-renders AuthGate into the
          // login screen on its own, and the query cache is dropped so nothing of this user's
          // library is left behind for whoever signs in next.
          onClick={() => logout.mutate()}
        >
          <Trans>Sign out</Trans>
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  )
}
