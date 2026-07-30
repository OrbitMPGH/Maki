import { useEffect, useState } from 'react'
import {
  Alert,
  Anchor,
  Button,
  Card,
  Center,
  Checkbox,
  Divider,
  PasswordInput,
  PinInput,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { IconBrandMark } from '../components/IconBrandMark'
import { useLogin, useVerifyTwoFactor } from '../api/auth'
import { getInitialize } from '../api/client'

/**
 * Sign-in, outside the AppShell — there is no navigation to show before there is a session.
 *
 * Errors are whatever the server said, and the server says the same thing for every kind of failure
 * on purpose: distinguishing "no such user" from "wrong password" turns this form into an account
 * enumerator on an instance reachable from the internet.
 */
export function LoginPage() {
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

  // Shown only after the user asks for it when password login is provider-restricted — admins still
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
    <Center mih="100vh" p="md">
      <Stack w="100%" maw={380} gap="lg">
        <Stack gap={4} align="center">
          <span className="brand-mark" style={{ transform: 'scale(1.4)' }}>
            <IconBrandMark />
          </span>
          <Title order={2} mt="sm">
            Maki
          </Title>
          <Text c="dimmed" fz="sm">
            {needsCode ? 'Enter your authenticator code' : 'Sign in to continue'}
          </Text>
        </Stack>

        <Card withBorder radius="md" p="lg">
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
                  label="Trust this device for 30 days"
                  checked={rememberMachine}
                  onChange={(e) => setRememberMachine(e.currentTarget.checked)}
                />
                {error && (
                  <Alert color="red" variant="light">
                    {error.message}
                  </Alert>
                )}
                <Button type="submit" loading={busy} disabled={code.length < 6} fullWidth>
                  Verify
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
                  Start over
                </Anchor>
              </Stack>
            </form>
          ) : (
            <Stack>
              {ssoError && (
                <Alert color="red" variant="light">
                  {ssoError}
                </Alert>
              )}

              {sso.enabled && (
                <>
                  {/* A link, not a fetch: the browser has to leave this origin entirely, and an
                      XHR to the challenge endpoint would only follow the redirect in the background
                      and land back here with nothing to show for it. */}
                  <Button
                    component="a"
                    href={`/api/v1/auth/oidc/challenge?returnUrl=${encodeURIComponent('/')}`}
                    variant="light"
                    fullWidth
                  >
                    Continue with {sso.displayName}
                  </Button>
                  {!passwordHidden && <Divider label="or" labelPosition="center" />}
                </>
              )}

              {passwordHidden ? (
                <Anchor fz="sm" ta="center" onClick={() => setShowPassword(true)}>
                  Sign in with a password
                </Anchor>
              ) : (
                <form onSubmit={submitPassword}>
                  <Stack>
                    {sso.enabled && sso.restricted && (
                      <Text fz="xs" c="dimmed">
                        Password sign-in is limited to administrators on this instance.
                      </Text>
                    )}
                    <TextInput
                      label="Username"
                      autoComplete="username"
                      autoFocus
                      required
                      value={username}
                      onChange={(e) => setUsername(e.currentTarget.value)}
                    />
                    <PasswordInput
                      label="Password"
                      autoComplete="current-password"
                      required
                      value={password}
                      onChange={(e) => setPassword(e.currentTarget.value)}
                    />
                    {error && (
                      <Alert color="red" variant="light">
                        {error.message}
                      </Alert>
                    )}
                    <Button type="submit" loading={busy} fullWidth>
                      Sign in
                    </Button>
                  </Stack>
                </form>
              )}
            </Stack>
          )}
        </Card>
      </Stack>
    </Center>
  )
}
