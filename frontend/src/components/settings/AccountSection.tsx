import { useEffect, useState } from 'react'
import {
  Alert,
  Badge,
  Button,
  Code,
  CopyButton,
  Divider,
  Group,
  Modal,
  PasswordInput,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconCheck, IconCopy } from '@tabler/icons-react'
import QRCode from 'qrcode'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
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
  type CreatedApiKey,
} from '../../api/auth'
import { useAuth } from '../../auth/AuthProvider'
import { getInitialize } from '../../api/client'
import { formatDateTime } from '../../format'
import { Panel } from '../ui/Panel'

/**
 * Self-service account management: password, two-factor, API keys, sessions.
 *
 * Needs no permission: it only ever acts on the caller's own account, and the server takes the user
 * id from the session rather than from the request.
 */
export function AccountSection() {
  const { me } = useAuth()

  return (
    <Panel id="account">
      <Title order={4} mb="sm">
        <Trans>My account</Trans>
      </Title>
      <Group gap="xs" mb="md">
        <Text size="sm" c="var(--ink-3)">
          <Trans>Signed in as</Trans>
        </Text>
        <Code>{me?.userName}</Code>
        {me?.isAdmin && (
          <Badge size="sm" variant="light">
            <Trans>Administrator</Trans>
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
    </Panel>
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
  // travels in the query string rather than a fetch response, read once, same pattern as
  // LoginPage's ssoError.
  const [linkResult] = useState(() => {
    const params = new URLSearchParams(window.location.search)
    return { linked: params.get('oidcLinked') === '1', error: params.get('oidcLinkError') }
  })

  useEffect(() => {
    if (linkResult.linked) {
      notifications.show({ message: now`Single sign-on linked to your account`, color: 'green' })
    } else if (linkResult.error) {
      notifications.show({ message: linkResult.error, color: 'red' })
    }
  }, [linkResult])

  if (!sso?.enabled) {
    return null
  }

  const { displayName } = sso
  const oidcUserName = me?.oidcUserName

  return (
    <Stack gap="xs">
      <Text fw={600} size="sm">
        <Trans>Single sign-on</Trans>
      </Text>
      {me?.oidcLinked ? (
        <Group gap="xs">
          <Badge color="var(--ok)" variant="light">
            <Trans>Linked</Trans>
          </Badge>
          <Text size="xs" c="var(--ink-3)">
            <Trans>
              Signed in as <Code>{oidcUserName}</Code> on {displayName}.
            </Trans>
          </Text>
        </Group>
      ) : (
        <Group align="center">
          <Text size="xs" c="var(--ink-3)">
            <Trans>Not linked yet. Sign in with {displayName} once to enable it for this account.</Trans>
          </Text>
          <Button component="a" href="/api/v1/auth/oidc/link" size="xs" variant="default">
            <Trans>Link {displayName}</Trans>
          </Button>
        </Group>
      )}
    </Stack>
  )
}

function PasswordCard() {
  const { t } = useLingui()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const change = useChangePassword()

  return (
    <Stack gap="xs">
      <Text fw={600} size="sm">
        <Trans>Password</Trans>
      </Text>
      <Group align="flex-end" wrap="wrap">
        <PasswordInput
          label={t`Current`}
          autoComplete="current-password"
          value={current}
          onChange={(e) => setCurrent(e.currentTarget.value)}
          w={200}
        />
        <PasswordInput
          label={t`New`}
          description={t`At least 10 characters`}
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
                    message: now`Password changed. Other devices have been signed out.`,
                    color: 'green',
                  })
                },
                onError: (e) => notifications.show({ message: e.message, color: 'red' }),
              },
            )
          }
        >
          <Trans>Change</Trans>
        </Button>
      </Group>
    </Stack>
  )
}

function TwoFactorCard() {
  const { t } = useLingui()
  const { data: status } = useTwoFactorStatus()
  const start = useStartTwoFactorSetup()
  const enable = useEnableTwoFactor()
  const disable = useDisableTwoFactor()

  const [enrolling, setEnrolling] = useState<{ sharedKey: string; authenticatorUri: string } | null>(null)
  const [code, setCode] = useState('')
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null)
  const [disablePassword, setDisablePassword] = useState('')
  const [qrDataUrl, setQrDataUrl] = useState<string | null>(null)

  useEffect(() => {
    if (!enrolling) {
      setQrDataUrl(null)
      return
    }
    let cancelled = false
    QRCode.toDataURL(enrolling.authenticatorUri, { width: 200, margin: 1 })
      .then((url) => {
        if (!cancelled) setQrDataUrl(url)
      })
      .catch(() => {
        if (!cancelled) setQrDataUrl(null)
      })
    return () => {
      cancelled = true
    }
  }, [enrolling])

  return (
    <Stack gap="xs">
      <Group justify="space-between">
        <div>
          <Text fw={600} size="sm">
            <Trans>Two-factor authentication</Trans>
          </Text>
          <Text size="xs" c="var(--ink-3)">
            {status && !status.available ? (
              <Trans>This account has no password login for two-factor to protect.</Trans>
            ) : (
              <Trans>The single biggest improvement if Maki is reachable from the internet.</Trans>
            )}
          </Text>
        </div>
        {status?.enabled ? (
          <Badge color="var(--ok)" variant="light">
            <Trans>On</Trans>
          </Badge>
        ) : (
          status && status.available && (
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
              <Trans>Set up</Trans>
            </Button>
          )
        )}
      </Group>

      {status?.enabled && (
        <Group align="flex-end">
          <PasswordInput
            label={t`Confirm your password to turn it off`}
            value={disablePassword}
            onChange={(e) => setDisablePassword(e.currentTarget.value)}
            w={260}
          />
          <Button
            color="var(--danger)"
            variant="light"
            loading={disable.isPending}
            disabled={!disablePassword}
            onClick={() =>
              disable.mutate(disablePassword, {
                onSuccess: () => {
                  setDisablePassword('')
                  notifications.show({ message: now`Two-factor authentication disabled`, color: 'yellow' })
                },
                onError: (e) => notifications.show({ message: e.message, color: 'red' }),
              })
            }
          >
            <Trans>Disable</Trans>
          </Button>
        </Group>
      )}

      <Modal
        opened={enrolling !== null}
        onClose={() => {
          setEnrolling(null)
          setCode('')
        }}
        title={t`Set up two-factor authentication`}
        centered
      >
        <Stack>
          <Text size="sm">
            <Trans>
              Scan this with your authenticator app, or enter the key manually, then enter the code
              it shows. The key is only active once a code has verified, so a mistyped key cannot
              lock you out.
            </Trans>
          </Text>
          {qrDataUrl && (
            <Group justify="center">
              <img src={qrDataUrl} alt={t`Two-factor authenticator QR code`} width={200} height={200} />
            </Group>
          )}
          <Group gap="xs">
            <Code>{enrolling?.sharedKey}</Code>
            <CopyButton value={enrolling?.sharedKey ?? ''}>
              {({ copied, copy }) => (
                <Button size="xs" variant="default" onClick={copy} leftSection={copied ? <IconCheck size={14} /> : <IconCopy size={14} />}>
                  {copied ? <Trans>Copied</Trans> : <Trans>Copy</Trans>}
                </Button>
              )}
            </CopyButton>
          </Group>
          <TextInput
            label={t`Code from your app`}
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
            <Trans>Verify and enable</Trans>
          </Button>
        </Stack>
      </Modal>

      <Modal
        opened={recoveryCodes !== null}
        onClose={() => setRecoveryCodes(null)}
        title={t`Save your recovery codes`}
        centered
      >
        <Stack>
          <Alert color="var(--warn)" variant="light">
            <Trans>
              These are shown once. They are stored hashed, so nobody (including you) can read them
              back. Keep them somewhere you can reach without your authenticator.
            </Trans>
          </Alert>
          <Code block>{recoveryCodes?.join('\n')}</Code>
          <CopyButton value={recoveryCodes?.join('\n') ?? ''}>
            {({ copied, copy }) => (
              <Button variant="default" onClick={copy}>
                {copied ? <Trans>Copied</Trans> : <Trans>Copy codes</Trans>}
              </Button>
            )}
          </CopyButton>
        </Stack>
      </Modal>
    </Stack>
  )
}

function ApiKeysCard() {
  const { t } = useLingui()
  const { data: keys } = useApiKeys()
  const create = useCreateApiKey()
  const revoke = useRevokeApiKey()

  const [name, setName] = useState('')
  const [created, setCreated] = useState<CreatedApiKey | null>(null)

  // The OPDS feed token is a key row too, but it is minted, shown and rotated on the OPDS card.
  const fullKeys = keys?.filter((key) => key.scope === 'Full')

  return (
    <Stack gap="xs">
      <Text fw={600} size="sm">
        <Trans>API keys</Trans>
      </Text>
      <Text size="xs" c="var(--ink-3)">
        <Trans>
          For scripts and third-party clients. A key acts as you through the{' '}
          <Code>X-Api-Key</Code> header. Your OPDS feed URL is managed on the OPDS card.
        </Trans>
      </Text>

      <Group align="flex-end" wrap="wrap">
        <TextInput
          label={t`Name`}
          placeholder={t`Phone reader`}
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          w={200}
        />
        <Button
          loading={create.isPending}
          disabled={!name.trim()}
          onClick={() =>
            create.mutate(
              { name: name.trim() },
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
          <Trans>Create</Trans>
        </Button>
      </Group>

      {fullKeys && fullKeys.length > 0 && (
        <Table striped withTableBorder mt="xs" fz="sm">
          <Table.Thead>
            <Table.Tr>
              <Table.Th><Trans>Name</Trans></Table.Th>
              <Table.Th><Trans>Prefix</Trans></Table.Th>
              <Table.Th><Trans>Last used</Trans></Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {fullKeys.map((key) => (
              <Table.Tr key={key.id} opacity={key.revokedAt ? 0.5 : 1}>
                <Table.Td>{key.name}</Table.Td>
                <Table.Td>
                  <Code>{key.prefix}…</Code>
                </Table.Td>
                <Table.Td c="var(--ink-3)">
                  {key.lastUsedAt ? formatDateTime(key.lastUsedAt) : <Trans>never</Trans>}
                </Table.Td>
                <Table.Td ta="right">
                  {key.revokedAt ? (
                    <Text size="xs" c="var(--ink-3)">
                      <Trans>revoked</Trans>
                    </Text>
                  ) : (
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      color="var(--danger)"
                      onClick={() => revoke.mutate(key.id)}
                    >
                      <Trans>Revoke</Trans>
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
        title={t`Your new API key`}
        centered
        size="lg"
      >
        <Stack>
          <Alert color="var(--warn)" variant="light">
            <Trans>
              Copy this now. Only its fingerprint is stored, so it cannot be shown again: if you lose
              it, revoke this one and create another.
            </Trans>
          </Alert>
          <Code block style={{ wordBreak: 'break-all' }}>
            {created?.secret}
          </Code>
          <CopyButton value={created?.secret ?? ''}>
            {({ copied, copy }) => (
              <Button variant="default" onClick={copy}>
                {copied ? <Trans>Copied</Trans> : <Trans>Copy</Trans>}
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
          <Trans>Sessions</Trans>
        </Text>
        <Text size="xs" c="var(--ink-3)">
          <Trans>Signs out every other browser and device. This one stays signed in.</Trans>
        </Text>
      </div>
      <Button
        variant="light"
        color="var(--danger)"
        size="xs"
        loading={revoke.isPending}
        onClick={() =>
          revoke.mutate(undefined, {
            onSuccess: () =>
              notifications.show({ message: now`Other sessions signed out`, color: 'green' }),
            onError: (e) => notifications.show({ message: e.message, color: 'red' }),
          })
        }
      >
        <Trans>Sign out everywhere else</Trans>
      </Button>
    </Group>
  )
}
