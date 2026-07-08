import { Alert, AppShell, Group, NavLink, Stack, Title } from '@mantine/core'
import { Link, Route, Routes, useLocation } from 'react-router-dom'
import { useHealth } from './api/hooks'
import { useLiveEvents } from './api/signalr'
import LibraryPage from './pages/LibraryPage'
import SeriesDetailPage from './pages/SeriesDetailPage'
import AddSeriesPage from './pages/AddSeriesPage'
import ActivityPage from './pages/ActivityPage'
import SettingsPage from './pages/SettingsPage'

const navItems = [
  { label: 'Library', path: '/' },
  { label: 'Add Series', path: '/add' },
  { label: 'Activity', path: '/activity' },
  { label: 'Settings', path: '/settings' },
]

function App() {
  const location = useLocation()
  useLiveEvents()
  const { data: health } = useHealth()

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 200, breakpoint: 'sm' }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md">
          <Title order={3} c="indigo.4">
            Mangarr
          </Title>
        </Group>
      </AppShell.Header>
      <AppShell.Navbar p="xs">
        {navItems.map((item) => (
          <NavLink
            key={item.path}
            component={Link}
            to={item.path}
            label={item.label}
            active={
              item.path === '/'
                ? location.pathname === '/'
                : location.pathname.startsWith(item.path)
            }
          />
        ))}
      </AppShell.Navbar>
      <AppShell.Main>
        {health && health.length > 0 && (
          <Stack gap="xs" mb="md">
            {health.map((issue, i) => (
              <Alert
                key={i}
                color={issue.severity === 'error' ? 'red' : 'yellow'}
                variant="light"
                p="xs"
              >
                {issue.message}
              </Alert>
            ))}
          </Stack>
        )}
        <Routes>
          <Route path="/" element={<LibraryPage />} />
          <Route path="/series/:id" element={<SeriesDetailPage />} />
          <Route path="/add" element={<AddSeriesPage />} />
          <Route path="/activity" element={<ActivityPage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </AppShell.Main>
    </AppShell>
  )
}

export default App
