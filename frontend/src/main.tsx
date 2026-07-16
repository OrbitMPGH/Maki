import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Notifications } from '@mantine/notifications'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import '@mantine/core/styles.css'
import '@mantine/notifications/styles.css'
import './theme.css'
import { AppThemeProvider } from './theme-context'
import App from './App.tsx'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      refetchOnWindowFocus: false,
    },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppThemeProvider>
      <Notifications autoClose={6000} />
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </QueryClientProvider>
    </AppThemeProvider>
  </StrictMode>,
)
