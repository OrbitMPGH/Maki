import { AppShell, Group, NavLink, Title } from '@mantine/core'
import { Link, Route, Routes, useLocation } from 'react-router-dom'
import LibraryPage from './pages/LibraryPage'
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
        <Routes>
          <Route path="/" element={<LibraryPage />} />
          <Route path="/add" element={<AddSeriesPage />} />
          <Route path="/activity" element={<ActivityPage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </AppShell.Main>
    </AppShell>
  )
}

export default App
