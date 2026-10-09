import { useEffect, useState, type FormEvent } from 'react'
import {
  Alert,
  Badge,
  Button,
  Code,
  Divider,
  Group,
  Modal,
  PasswordInput,
  Stack,
  Table,
  Text,
  TextInput,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconCheck, IconCopy } from '@tabler/icons-react'
import QRCode from 'qrcode'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  useApiKeys,
  useChangePassword,
  useConfirmOidcLink,
  useCreateApiKey,
  useDisableTwoFactor,
  useEnableTwoFactor,
  useRevokeApiKey,
  useRevokeSessions,
  useStartTwoFactorSetup,
  useTwoFactorStatus,
  useUnlinkOidc,
  type ApiKey,
  type CreatedApiKey,
} from '../../api/auth'
import { useAuth } from '../../auth/AuthProvider'
import { getInitialize } from '../../api/client'
import { formatDateTime } from '../../format'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import { useCopyText } from '../ui/useCopyText'

/**
 * Self-service account management: password, two-factor, single sign-on, sessions.
 *
 * Needs no permission: it only ever acts on the caller's own account, and the server takes the user
 * id from the session rather than from the request. The same holds for API keys below.
 */
export function SignInSection() {
  const { me } = useAuth()

  return (
    <SettingsSection
      id="sign-in"
      title={<Trans>Sign-in</Trans>}
      description={
        <Trans>
          Your password, two-factor code and single sign-on link. Changing your password signs out
          your other devices.
        </Trans>
      }
    >
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
        <SessionsCard />
      </Stack>
    </SettingsSection>
  )
}

export function ApiKeysSection() {
  return (
    <SettingsSection
      id="api-keys"
      title={<Trans>API keys</Trans>}
      description={
        <Trans>
          Keys for scripts and third-party apps. A key acts as you, sent in the{' '}
          <Code>X-Api-Key</Code> header. Your OPDS feed has its own URL under Reading.
        </Trans>
      }
    >
      <ApiKeysCard />
    </SettingsSection>
  )
}

function SsoCard() {
  const { t } = useLingui()
  const { me } = useAuth()
  const [sso, setSso] = useState<{ enabled: boolean; displayName: string } | null>(null)
  const [linkPassword, setLinkPassword] = useState('')
  const confirmLink = useConfirmOidcLink()
  const unlink = useUnlinkOidc()

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
      notifications.show({ message: now`Single sign-on linked to your account`, color: 'var(--ok)' })
    } else if (linkResult.error) {
      notifications.show({ message: linkResult.error, color: 'var(--danger)' })
    }
    // Dropped once shown so a reload does not repeat it.
    const params = new URLSearchParams(window.location.search)
    if (params.has('oidcLinked') || params.has('oidcLinkError')) {
      params.delete('oidcLinked')
      params.delete('oidcLinkError')
      const query = params.toString()
      window.history.replaceState(
        window.history.state,
        '',
        window.location.pathname + (query ? `?${query}` : '') + window.location.hash,
      )
    }
  }, [linkResult])

  if (!sso?.enabled) {
    return null
  }

  // Empty when no admin has typed a button label; the client supplies its own translated copy of
  // the English default rather than showing the server's raw literal.
  const displayName = sso.displayName || t`Single sign-on`
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
          <Button
            size="compact-xs"
            variant="subtle"
            color="var(--danger)"
            loading={unlink.isPending}
            onClick={() =>
              unlink.mutate(undefined, {
                onSuccess: () =>
                  notifications.show({ message: now`Single sign-on removed from your account`, color: 'var(--ok)' }),
              })
            }
          >
            <Trans>Remove</Trans>
          </Button>
        </Group>
      ) : (
        <Stack gap="xs">
          <Text size="xs" c="var(--ink-3)">
            <Trans>
              Not linked yet. Confirm your password, then sign in with {displayName} to link it to
              this account.
            </Trans>
          </Text>
          <Group
            component="form"
            align="flex-end"
            onSubmit={(e: FormEvent) => {
              e.preventDefault()
              confirmLink.mutate(linkPassword, {
                // The link is a top-level navigation to the provider; the confirmation it needs
                // was just set as a cookie.
                onSuccess: () => window.location.assign('/api/v1/auth/oidc/link'),
              })
            }}
          >
            <PasswordInput
              label={t`Confirm your password to link`}
              autoComplete="current-password"
              value={linkPassword}
              onChange={(e) => setLinkPassword(e.currentTarget.value)}
              w={260}
            />
            <Button type="submit" size="xs" variant="default" loading={confirmLink.isPending}>
              <Trans>Link {displayName}</Trans>
            </Button>
          </Group>
        </Stack>
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
      <Group
        component="form"
        align="flex-end"
        wrap="wrap"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          if (!current || next.length < 10) return
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
                  color: 'var(--ok)',
                })
              },
            },
          )
        }}
      >
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
        <Button type="submit" loading={change.isPending} disabled={!current || next.length < 10}>
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
  const [enablePassword, setEnablePassword] = useState('')
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null)
  const [disablePassword, setDisablePassword] = useState('')
  const [qrDataUrl, setQrDataUrl] = useState<string | null>(null)
  const keyCopy = useCopyText()
  const codesCopy = useCopyText()

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
            {status?.ssoDelegated && !status.enabled ? (
              <Trans>
                Sign-in relies on your identity provider. A code, once set up, protects password
                sign-in only.
              </Trans>
            ) : status && !status.available ? (
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
          status && status.available && !status.ssoDelegated && (
            <Button
              size="xs"
              variant="default"
              loading={start.isPending}
              onClick={() =>
                start.mutate(undefined, {
                  onSuccess: setEnrolling,
                })
              }
            >
              <Trans>Set up</Trans>
            </Button>
          )
        )}
      </Group>

      {status?.enabled && (
        <Group
          component="form"
          align="flex-end"
          onSubmit={(e: FormEvent) => {
            e.preventDefault()
            if (!disablePassword) return
            disable.mutate(disablePassword, {
              onSuccess: () => {
                setDisablePassword('')
                notifications.show({ message: now`Two-factor authentication disabled`, color: 'var(--warn)' })
              },
            })
          }}
        >
          <PasswordInput
            label={t`Confirm your password to turn it off`}
            autoComplete="current-password"
            value={disablePassword}
            onChange={(e) => setDisablePassword(e.currentTarget.value)}
            w={260}
          />
          <Button
            type="submit"
            color="var(--danger)"
            variant="light"
            loading={disable.isPending}
            disabled={!disablePassword}
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
          setEnablePassword('')
        }}
        title={t`Set up two-factor authentication`}
        centered
      >
        <Stack
          component="form"
          onSubmit={(e: FormEvent) => {
            e.preventDefault()
            if (code.length < 6 || !enablePassword) return
            enable.mutate({ code, password: enablePassword }, {
              onSuccess: (result) => {
                setEnrolling(null)
                setCode('')
                setEnablePassword('')
                setRecoveryCodes(result.recoveryCodes)
              },
            })
          }}
        >
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
            <Button
              size="xs"
              variant="default"
              onClick={() => void keyCopy.copy(enrolling?.sharedKey ?? '')}
              leftSection={keyCopy.copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
            >
              {keyCopy.copied ? <Trans>Copied</Trans> : <Trans>Copy</Trans>}
            </Button>
          </Group>
          <TextInput
            label={t`Code from your app`}
            inputMode="numeric"
            value={code}
            onChange={(e) => setCode(e.currentTarget.value)}
          />
          <PasswordInput
            label={t`Your password`}
            autoComplete="current-password"
            value={enablePassword}
            onChange={(e) => setEnablePassword(e.currentTarget.value)}
          />
          <Button type="submit" loading={enable.isPending} disabled={code.length < 6 || !enablePassword}>
            <Trans>Verify and enable</Trans>
          </Button>
        </Stack>
      </Modal>

      <Modal
        opened={recoveryCodes !== null}
        onClose={() => setRecoveryCodes(null)}
        title={t`Save your recovery codes`}
        centered
        closeOnClickOutside={false}
        closeOnEscape={false}
      >
        <Stack>
          <Alert color="var(--warn)" variant="light">
            <Trans>
              These are shown once. They are stored hashed, so nobody (including you) can read them
              back. Keep them somewhere you can reach without your authenticator.
            </Trans>
          </Alert>
          <Code block>{recoveryCodes?.join('\n')}</Code>
          <Button variant="default" onClick={() => void codesCopy.copy(recoveryCodes?.join('\n') ?? '')}>
            {codesCopy.copied ? <Trans>Copied</Trans> : <Trans>Copy codes</Trans>}
          </Button>
          <Button onClick={() => setRecoveryCodes(null)}>
            <Trans>I have saved them</Trans>
          </Button>
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
  const { data: status } = useTwoFactorStatus()
  const needsPassword = status?.hasPassword ?? true

  const [name, setName] = useState('')
  const [keyPassword, setKeyPassword] = useState('')
  const [created, setCreated] = useState<CreatedApiKey | null>(null)
  const [revoking, setRevoking] = useState<ApiKey | null>(null)
  const secretCopy = useCopyText()
  const revokingName = revoking?.name ?? ''

  // The OPDS feed token is a key row too, but it is minted, shown and rotated on the OPDS card.
  const fullKeys = keys?.filter((key) => key.scope === 'Full')

  return (
    <Stack gap="xs">
      <Group
        component="form"
        align="flex-end"
        wrap="wrap"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          if (!name.trim()) return
          create.mutate(
            { name: name.trim(), password: keyPassword },
            {
              onSuccess: (result) => {
                setCreated(result)
                setName('')
                setKeyPassword('')
              },
            },
          )
        }}
      >
        <TextInput
          label={t`Name`}
          placeholder={t`Phone reader`}
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          w={200}
        />
        {needsPassword && (
          <PasswordInput
            label={t`Your password`}
            autoComplete="current-password"
            value={keyPassword}
            onChange={(e) => setKeyPassword(e.currentTarget.value)}
            w={200}
          />
        )}
        <Button type="submit" loading={create.isPending} disabled={!name.trim()}>
          <Trans>Create</Trans>
        </Button>
      </Group>

      {fullKeys && fullKeys.length > 0 && (
        <Table className="panel-table ops-table" mt="xs">
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
                      onClick={() => setRevoking(key)}
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
        closeOnClickOutside={false}
        closeOnEscape={false}
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
          <Button variant="default" onClick={() => void secretCopy.copy(created?.secret ?? '')}>
            {secretCopy.copied ? <Trans>Copied</Trans> : <Trans>Copy</Trans>}
          </Button>
          <Button onClick={() => setCreated(null)}>
            <Trans>I have copied it</Trans>
          </Button>
        </Stack>
      </Modal>

      <ConfirmDialog
        opened={revoking !== null}
        onClose={() => setRevoking(null)}
        title={<Trans>Revoke {revokingName}?</Trans>}
        confirmLabel={<Trans>Revoke</Trans>}
        loading={revoke.isPending}
        onConfirm={() => revoking && revoke.mutate(revoking.id, { onSuccess: () => setRevoking(null) })}
      >
        <Trans>Anything still using this key stops working straight away. This can't be undone.</Trans>
      </ConfirmDialog>
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
              notifications.show({ message: now`Other sessions signed out`, color: 'var(--ok)' }),
          })
        }
      >
        <Trans>Sign out everywhere else</Trans>
      </Button>
    </Group>
  )
}
