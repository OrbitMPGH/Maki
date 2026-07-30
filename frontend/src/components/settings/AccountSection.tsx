import { useEffect, useState } from 'react'
import {
  Alert,
  Badge,
  Button,
  Card,
  Code,
  CopyButton,
  Divider,
  Group,
  Modal,
  PasswordInput,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconCheck, IconCopy } from '@tabler/icons-react'
import {
  useApiKeys,
  useChangePassword,
  useCreateApiKey,
  useDisableTwoFactor,
  useEnableTwoFactor,
  useRevokeApiKey,
  useRevokeSessions,
  useStartTwoFactorSetup,
  useTwoFactorStatus,
  type ApiKeyScope,
  type CreatedApiKey,
} from '../../api/auth'
import { useAuth } from '../../auth/AuthProvider'
import { getInitialize } from '../../api/client'

/**
 * Self-service account management: password, two-factor, API keys, sessions.
 *
 * Needs no permission — it only ever acts on the caller's own account, and the server takes the user
 * id from the session rather than from the request.
 */
export function AccountSection() {
  const { me } = useAuth()

  return (
    <Card withBorder radius="md" padding="md" id="account">
      <Title order={4} mb="sm">
        My account
      </Title>
      <Group gap="xs" mb="md">
        <Text size="sm" c="dimmed">
          Signed in as
        </Text>
        <Code>{me?.userName}</Code>
        {me?.isAdmin && (
          <Badge size="sm" variant="light">
            Administrator
          </Badge>
        )}
      </Group>

      <Stack gap="lg">
        <PasswordCard />
        <Divider />
        <TwoFactorCard />
        <Divider />
        <SsoCard />
        <Divider />
        <ApiKeysCard />
        <Divider />
        <SessionsCard />
      </Stack>
    </Card>
  )
}

function SsoCard() {
  const { me } = useAuth()
  const [sso, setSso] = useState<{ enabled: boolean; displayName: string } | null>(null)

  // Read once on mount, same as the login page: whether the provider is configured at all comes
  // from the anonymous /initialize.json, not from anything user-specific.
  useEffect(() => {
    void getInitialize().then((init) =>
      setSso({ enabled: init.oidc.enabled, displayName: init.oidc.displayName }),
    )
  }, [])

  // The redirect back from oidc/link-complete lands here as a top-level navigation, so the result
  // travels in the query string rather than a fetch response — read once, same pattern as
  // LoginPage's ssoError.
  const [linkResult] = useState(() => {
    const params = new URLSearchParams(window.location.search)
    return { linked: params.get('oidcLinked') === '1', error: params.get('oidcLinkError') }
  })

  useEffect(() => {
    if (linkResult.linked) {
      notifications.show({ message: 'Single sign-on linked to your account', color: 'green' })
    } else if (linkResult.error) {
      notifications.show({ message: linkResult.error, color: 'red' })
    }
  }, [linkResult])

  if (!sso?.enabled) {
    return null
  }

  return (
    <Stack gap="xs">
      <Text fw={600} size="sm">
        Single sign-on
      </Text>
      {me?.oidcLinked ? (
        <Group gap="xs">
          <Badge color="green" variant="light">
            Linked
          </Badge>
          <Text size="xs" c="dimmed">
            You can sign in with {sso.displayName}.
          </Text>
        </Group>
      ) : (
        <Group align="center">
          <Text size="xs" c="dimmed">
            Not linked yet — sign in with {sso.displayName} once to enable it for this account.
          </Text>
          <Button component="a" href="/api/v1/auth/oidc/link" size="xs" variant="default">
            Link {sso.displayName}
          </Button>
        </Group>
      )}
    </Stack>
  )
}

function PasswordCard() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const change = useChangePassword()

  return (
    <Stack gap="xs">
      <Text fw={600} size="sm">
        Password
      </Text>
      <Group align="flex-end" wrap="wrap">
        <PasswordInput
          label="Current"
          autoComplete="current-password"
          value={current}
          onChange={(e) => setCurrent(e.currentTarget.value)}
          w={200}
        />
        <PasswordInput
          label="New"
          description="At least 10 characters"
          autoComplete="new-password"
          value={next}
          onChange={(e) => setNext(e.currentTarget.value)}
          w={200}
        />
        <Button
          loading={change.isPending}
          disabled={!current || next.length < 10}
          onClick={() =>
            change.mutate(
              { currentPassword: current, newPassword: next },
              {
                onSuccess: () => {
                  setCurrent('')
                  setNext('')
                  notifications.show({
                    // Worth stating plainly: changing the password rotates the security stamp, which
                    // is what invalidates every other issued cookie.
                    message: 'Password changed. Other devices have been signed out.',
                    color: 'green',
                  })
                },
                onError: (e) => notifications.show({ message: e.message, color: 'red' }),
              },
            )
          }
        >
          Change
        </Button>
      </Group>
    </Stack>
  )
}

function TwoFactorCard() {
  const { data: status } = useTwoFactorStatus()
  const start = useStartTwoFactorSetup()
  const enable = useEnableTwoFactor()
  const disable = useDisableTwoFactor()

  const [enrolling, setEnrolling] = useState<{ sharedKey: string; authenticatorUri: string } | null>(null)
  const [code, setCode] = useState('')
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null)
  const [disablePassword, setDisablePassword] = useState('')

  return (
    <Stack gap="xs">
      <Group justify="space-between">
        <div>
          <Text fw={600} size="sm">
            Two-factor authentication
          </Text>
          <Text size="xs" c="dimmed">
            The single biggest improvement if Maki is reachable from the internet.
          </Text>
        </div>
        {status?.enabled ? (
          <Badge color="green" variant="light">
            On
          </Badge>
        ) : (
          <Button
            size="xs"
            variant="default"
            loading={start.isPending}
            onClick={() =>
              start.mutate(undefined, {
                onSuccess: setEnrolling,
                onError: (e) => notifications.show({ message: e.message, color: 'red' }),
              })
            }
          >
            Set up
          </Button>
        )}
      </Group>

      {status?.enabled && (
        <Group align="flex-end">
          <PasswordInput
            label="Confirm your password to turn it off"
            value={disablePassword}
            onChange={(e) => setDisablePassword(e.currentTarget.value)}
            w={260}
          />
          <Button
            color="red"
            variant="light"
            loading={disable.isPending}
            disabled={!disablePassword}
            onClick={() =>
              disable.mutate(disablePassword, {
                onSuccess: () => {
                  setDisablePassword('')
                  notifications.show({ message: 'Two-factor authentication disabled', color: 'yellow' })
                },
                onError: (e) => notifications.show({ message: e.message, color: 'red' }),
              })
            }
          >
            Disable
          </Button>
        </Group>
      )}

      <Modal
        opened={enrolling !== null}
        onClose={() => {
          setEnrolling(null)
          setCode('')
        }}
        title="Set up two-factor authentication"
        centered
      >
        <Stack>
          <Text size="sm">
            Add this key to your authenticator app, then enter the code it shows. The key is only
            active once a code has verified — so a mistyped key cannot lock you out.
          </Text>
          <Group gap="xs">
            <Code>{enrolling?.sharedKey}</Code>
            <CopyButton value={enrolling?.sharedKey ?? ''}>
              {({ copied, copy }) => (
                <Button size="xs" variant="default" onClick={copy} leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />}>
                  {copied ? 'Copied' : 'Copy'}
                </Button>
              )}
            </CopyButton>
          </Group>
          <TextInput
            label="Code from your app"
            inputMode="numeric"
            value={code}
            onChange={(e) => setCode(e.currentTarget.value)}
          />
          <Button
            loading={enable.isPending}
            disabled={code.length < 6}
            onClick={() =>
              enable.mutate(code, {
                onSuccess: (result) => {
                  setEnrolling(null)
                  setCode('')
                  setRecoveryCodes(result.recoveryCodes)
                },
                onError: (e) => notifications.show({ message: e.message, color: 'red' }),
              })
            }
          >
            Verify and enable
          </Button>
        </Stack>
      </Modal>

      <Modal
        opened={recoveryCodes !== null}
        onClose={() => setRecoveryCodes(null)}
        title="Save your recovery codes"
        centered
      >
        <Stack>
          <Alert color="yellow" variant="light">
            These are shown once. They are stored hashed, so nobody — including you — can read them
            back. Keep them somewhere you can reach without your authenticator.
          </Alert>
          <Code block>{recoveryCodes?.join('\n')}</Code>
          <CopyButton value={recoveryCodes?.join('\n') ?? ''}>
            {({ copied, copy }) => (
              <Button variant="default" onClick={copy}>
                {copied ? 'Copied' : 'Copy codes'}
              </Button>
            )}
          </CopyButton>
        </Stack>
      </Modal>
    </Stack>
  )
}

function ApiKeysCard() {
  const { data: keys } = useApiKeys()
  const create = useCreateApiKey()
  const revoke = useRevokeApiKey()
  const { can } = useAuth()

  const [name, setName] = useState('')
  const [scope, setScope] = useState<ApiKeyScope>('Full')
  const [created, setCreated] = useState<CreatedApiKey | null>(null)

  const secretUrl =
    created?.key.scope === 'Opds' ? `/api/v1/opds/${created.secret}` : created?.secret

  return (
    <Stack gap="xs">
      <Text fw={600} size="sm">
        API keys
      </Text>
      <Text size="xs" c="dimmed">
        For scripts and third-party clients. A <Code>Full</Code> key acts as you through the{' '}
        <Code>X-Api-Key</Code> header. An <Code>OPDS</Code> key is only a feed URL — it cannot reach
        the management API, which is why the URL you paste into a reading app is safe to paste.
      </Text>

      <Group align="flex-end" wrap="wrap">
        <TextInput
          label="Name"
          placeholder="Phone reader"
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          w={200}
        />
        <Select
          label="Scope"
          data={[
            { value: 'Full', label: 'Full API' },
            { value: 'Opds', label: 'OPDS feed only', disabled: !can('UseOpds') },
          ]}
          value={scope}
          onChange={(v) => setScope((v as ApiKeyScope) ?? 'Full')}
          w={170}
          allowDeselect={false}
        />
        <Button
          loading={create.isPending}
          disabled={!name.trim()}
          onClick={() =>
            create.mutate(
              { name: name.trim(), scope },
              {
                onSuccess: (result) => {
                  setCreated(result)
                  setName('')
                },
                onError: (e) => notifications.show({ message: e.message, color: 'red' }),
              },
            )
          }
        >
          Create
        </Button>
      </Group>

      {keys && keys.length > 0 && (
        <Table striped withTableBorder mt="xs" fz="sm">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Name</Table.Th>
              <Table.Th>Scope</Table.Th>
              <Table.Th>Prefix</Table.Th>
              <Table.Th>Last used</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {keys.map((key) => (
              <Table.Tr key={key.id} opacity={key.revokedAt ? 0.5 : 1}>
                <Table.Td>{key.name}</Table.Td>
                <Table.Td>
                  <Badge size="xs" variant="light">
                    {key.scope}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Code>{key.prefix}…</Code>
                </Table.Td>
                <Table.Td c="dimmed">
                  {key.lastUsedAt ? new Date(key.lastUsedAt).toLocaleString() : 'never'}
                </Table.Td>
                <Table.Td ta="right">
                  {key.revokedAt ? (
                    <Text size="xs" c="dimmed">
                      revoked
                    </Text>
                  ) : (
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      color="red"
                      onClick={() => revoke.mutate(key.id)}
                    >
                      Revoke
                    </Button>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <Modal
        opened={created !== null}
        onClose={() => setCreated(null)}
        title={created?.key.scope === 'Opds' ? 'Your OPDS feed URL' : 'Your new API key'}
        centered
        size="lg"
      >
        <Stack>
          <Alert color="yellow" variant="light">
            Copy this now. Only its fingerprint is stored, so it cannot be shown again — if you lose
            it, revoke this one and create another.
          </Alert>
          <Code block style={{ wordBreak: 'break-all' }}>
            {secretUrl}
          </Code>
          <CopyButton value={secretUrl ?? ''}>
            {({ copied, copy }) => (
              <Button variant="default" onClick={copy}>
                {copied ? 'Copied' : 'Copy'}
              </Button>
            )}
          </CopyButton>
        </Stack>
      </Modal>
    </Stack>
  )
}

function SessionsCard() {
  const revoke = useRevokeSessions()

  return (
    <Group justify="space-between">
      <div>
        <Text fw={600} size="sm">
          Sessions
        </Text>
        <Text size="xs" c="dimmed">
          Signs out every other browser and device. This one stays signed in.
        </Text>
      </div>
      <Button
        variant="light"
        color="red"
        size="xs"
        loading={revoke.isPending}
        onClick={() =>
          revoke.mutate(undefined, {
            onSuccess: () =>
              notifications.show({ message: 'Other sessions signed out', color: 'green' }),
            onError: (e) => notifications.show({ message: e.message, color: 'red' }),
          })
        }
      >
        Sign out everywhere else
      </Button>
    </Group>
  )
}
