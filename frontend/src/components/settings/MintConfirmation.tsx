import { useEffect, useState } from 'react'
import { Alert, Button, PasswordInput, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useMintProof } from '../../api/auth'

/**
 * What creating an API key or OPDS token asks for. Accounts that sign in with a password type it;
 * accounts that only use single sign-on have none, so the server wants a sign-in from the last ten
 * minutes instead. The deadline is measured from when the answer arrived, so the browser's clock
 * never has to agree with the server's.
 */
export function useMintConfirmation() {
  const { data, dataUpdatedAt } = useMintProof()
  const [now, setNow] = useState(() => Date.now())

  const deadline = data ? dataUpdatedAt + data.recentSignInSecondsLeft * 1000 : 0
  useEffect(() => {
    if (!data || data.proof !== 'RecentSignIn') return
    const wait = deadline - Date.now()
    if (wait <= 0) return
    const timer = window.setTimeout(() => setNow(Date.now()), wait + 250)
    return () => window.clearTimeout(timer)
  }, [data, deadline])

  const needsPassword = data?.proof === 'Password'
  const signInStale = data?.proof === 'RecentSignIn' && Math.max(now, dataUpdatedAt) >= deadline

  return {
    loaded: !!data,
    needsPassword,
    signInStale,
    canSignInAgain: data?.canSignInAgain ?? false,
    /** Whether the form has what the server will ask for. */
    ready: (password: string) => !!data && (needsPassword ? password.length > 0 : !signInStale),
  }
}

export function MintPasswordField({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const { t } = useLingui()
  return (
    <PasswordInput
      label={t`Your password`}
      autoComplete="current-password"
      value={value}
      onChange={(e) => onChange(e.currentTarget.value)}
    />
  )
}

/**
 * Shown to a single sign-on account whose last sign-in is too old to mint with. The round trip
 * returns to `anchor` on this page; the identity provider usually skips its own prompt when its
 * session is still live, so this is one click and a redirect.
 */
export function RecentSignInNotice({ canSignInAgain, anchor }: { canSignInAgain: boolean; anchor: string }) {
  const { pathname, search } = window.location
  const returnUrl = `${pathname}${search}#${anchor}`

  return (
    <Alert color="var(--warn)" variant="light">
      <Stack gap="xs" align="flex-start">
        <Text size="sm">
          {canSignInAgain ? (
            <Trans>You sign in with single sign-on, so this needs a sign-in from the last 10 minutes.</Trans>
          ) : (
            <Trans>
              This needs a sign-in from the last 10 minutes. Sign out and sign in again, then come back here.
            </Trans>
          )}
        </Text>
        {canSignInAgain && (
          <Button
            component="a"
            href={`/api/v1/auth/oidc/challenge?returnUrl=${encodeURIComponent(returnUrl)}`}
            size="compact-sm"
            variant="light"
          >
            <Trans>Sign in again</Trans>
          </Button>
        )}
      </Stack>
    </Alert>
  )
}
