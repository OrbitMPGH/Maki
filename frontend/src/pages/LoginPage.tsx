import { useEffect, useState } from 'react'
import {
  Anchor,
  Button,
  Center,
  Checkbox,
  Divider,
  PasswordInput,
  PinInput,
  Stack,
  Text,
  TextInput,
} from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { AuthError, AuthFrame } from '../components/auth/AuthFrame'
import { useLogin, useVerifyTwoFactor } from '../api/auth'
import { getInitialize } from '../api/client'

/**
 * Sign-in, outside the AppShell: there is no navigation to show before there is a session.
 *
 * Errors are whatever the server said, and the server says the same thing for every kind of failure
 * on purpose: distinguishing "no such user" from "wrong password" turns this form into an account
 * enumerator on an instance reachable from the internet.
 */
export function LoginPage() {
  const { t } = useLingui()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [rememberMachine, setRememberMachine] = useState(false)
  const [needsCode, setNeedsCode] = useState(false)
  const [sso, setSso] = useState<{ enabled: boolean; displayName: string; restricted: boolean }>({
    enabled: false,
    displayName: '',
    restricted: false,
  })

  // Whether the identity provider redirected back with a failure. Read once on mount: the server
  // puts it in the query string because the browser arrives here by a top-level navigation from
  // another origin, with no fetch waiting for a response body.
  const [ssoError] = useState(() => new URLSearchParams(window.location.search).get('ssoError'))

  // Shown only after the user asks for it when password login is provider-restricted: admins still
  // need the form, and everyone else needs to be told why it will not work for them.
  const [showPassword, setShowPassword] = useState(false)

  const login = useLogin()
  const verify = useVerifyTwoFactor()

  useEffect(() => {
    void getInitialize().then((init) =>
      setSso({
        enabled: init.oidc.enabled,
        displayName: init.oidc.displayName,
        restricted: init.oidc.localLoginRestricted,
      }),
    )
  }, [])

  const error = login.error ?? verify.error
  const busy = login.isPending || verify.isPending
  const passwordHidden = sso.enabled && sso.restricted && !showPassword
  const { displayName } = sso

  function submitPassword(event: React.FormEvent) {
    event.preventDefault()
    login.mutate(
      { username: username.trim(), password },
      {
        onSuccess: (result) => {
          if (result.requiresTwoFactor) {
            setNeedsCode(true)
            // Drop the password from state the moment it is no longer needed.
            setPassword('')
          }
          // On a full success AuthProvider's cached identity flips and the guard renders the app;
          // nothing to navigate here.
        },
      },
    )
  }

  function submitCode(event: React.FormEvent) {
    event.preventDefault()
    verify.mutate({ code, rememberMachine })
  }

  return (
    <AuthFrame
      title={needsCode ? t`Two-factor code` : t`Sign in`}
      subtitle={needsCode ? <Trans>Enter your authenticator code</Trans> : undefined}
    >
      {needsCode ? (
        <form onSubmit={submitCode}>
          <Stack>
            <Center>
              <PinInput
                length={6}
                type="number"
                inputMode="numeric"
                oneTimeCode
                autoFocus
                value={code}
                onChange={setCode}
              />
            </Center>
            <Checkbox
              label={t`Trust this device for 30 days`}
              checked={rememberMachine}
              onChange={(e) => setRememberMachine(e.currentTarget.checked)}
            />
            {error && <AuthError>{error.message}</AuthError>}
            <Button type="submit" color="brand" loading={busy} disabled={code.length < 6} fullWidth>
              <Trans>Verify</Trans>
            </Button>
            <Anchor
              fz="sm"
              ta="center"
              onClick={() => {
                setNeedsCode(false)
                setCode('')
                verify.reset()
              }}
            >
              <Trans>Start over</Trans>
            </Anchor>
          </Stack>
        </form>
      ) : (
        <Stack>
          {ssoError && <AuthError>{ssoError}</AuthError>}

          {sso.enabled && (
            <>
              {/* A link, not a fetch: the browser has to leave this origin entirely, and an
                  XHR to the challenge endpoint would only follow the redirect in the background
                  and land back here with nothing to show for it. */}
              <Button
                component="a"
                href={`/api/v1/auth/oidc/challenge?returnUrl=${encodeURIComponent('/')}`}
                variant="default"
                fullWidth
              >
                <Trans>Continue with {displayName}</Trans>
              </Button>
              {!passwordHidden && <Divider label="or" labelPosition="center" />}
            </>
          )}

          {passwordHidden ? (
            <Anchor fz="sm" ta="center" onClick={() => setShowPassword(true)}>
              <Trans>Sign in with a password</Trans>
            </Anchor>
          ) : (
            <form onSubmit={submitPassword}>
              <Stack>
                {sso.enabled && sso.restricted && (
                  <Text fz="xs" c="var(--ink-3)">
                    <Trans>Password sign-in is limited to administrators on this instance.</Trans>
                  </Text>
                )}
                <TextInput
                  label={t`Username`}
                  autoComplete="username"
                  autoFocus
                  required
                  value={username}
                  onChange={(e) => setUsername(e.currentTarget.value)}
                />
                <PasswordInput
                  label={t`Password`}
                  autoComplete="current-password"
                  required
                  value={password}
                  onChange={(e) => setPassword(e.currentTarget.value)}
                />
                {error && <AuthError>{error.message}</AuthError>}
                <Button type="submit" color="brand" loading={busy} fullWidth>
                  <Trans>Sign in</Trans>
                </Button>
              </Stack>
            </form>
          )}
        </Stack>
      )}
    </AuthFrame>
  )
}
