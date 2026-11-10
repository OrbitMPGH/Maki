import { useEffect, useRef } from 'react'
import { Button, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconCloudOff } from '../lib/icons'
import { AuthFrame } from '../components/auth/AuthFrame'
import { useConnectivity } from '../lib/connectivity'
import { useAuth } from './AuthProvider'

const RETRY_MS = 30_000

/**
 * The first load with the server unreachable and nothing saved on this device: there is no
 * identity to boot with, and a login form here could only fail. Tries again on its own while open.
 */
export function OfflineStartPage() {
  const { t } = useLingui()
  const { retry } = useAuth()
  const connectivity = useConnectivity()
  const retryRef = useRef(retry)
  useEffect(() => {
    retryRef.current = retry
  }, [retry])

  useEffect(() => {
    if (connectivity === 'online') retryRef.current()
    const timer = window.setInterval(() => retryRef.current(), RETRY_MS)
    return () => window.clearInterval(timer)
  }, [connectivity])

  return (
    <AuthFrame title={t`You're offline`} mood="asleep">
      <Stack>
        <Text size="sm" c="var(--ink-2)">
          <Trans>
            Maki can't reach the server, and nothing is saved on this device yet. Once the connection
            is back, sign in and save chapters for offline reading.
          </Trans>
        </Text>
        <div className="auth-retry-line">
          <IconCloudOff size={14} aria-hidden />
          <Trans>Checking again every 30 seconds</Trans>
        </div>
        <Button onClick={retry} fullWidth>
          <Trans>Try again</Trans>
        </Button>
      </Stack>
    </AuthFrame>
  )
}
