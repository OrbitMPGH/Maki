import { i18n } from '@lingui/core'
import { ssoLinkErrorLabel } from '../../api/ssoErrors'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Badge, Button, Code, Group, Modal, PasswordInput, Stack, Text, TextInput, Tooltip } from '@mantine/core'
import { notifications } from '../../lib/toast'
import { IconCheck, IconCopy, IconPlus } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { EmptyState } from '../ui/EmptyState'
import { plural, t as now } from '@lingui/core/macro'
import {
  useApiKeys,
  useChangePassword,
  useConfirmOidcLink,
  useCreateApiKey,
  useDisableTwoFactor,
  useEnableTwoFactor,
  useRegenerateRecoveryCodes,
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

import { ModalFooter, ModalHeading, ModalNote } from '../ui/ModalShell'
import { SettingsRow, SettingsRows, SettingsRowsBlock } from './SettingsRow'
import { MintPasswordField, RecentSignInNotice, useMintConfirmation } from './MintConfirmation'

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
        <Trans>How you sign in, and the devices that stay signed in.</Trans>
      }
      panelProps={{ p: 0 }}
    >
      <div className="account-identity">
        <Text size="sm" c="var(--ink-3)">
          <Trans>Signed in as</Trans>
        </Text>
        <Text size="sm" fw={600} c="var(--ink-hi)">
          {me?.userName}
        </Text>
        {me?.isAdmin && (
          <Badge size="sm" variant="light">
            <Trans>Administrator</Trans>
          </Badge>
        )}
      </div>

      <SettingsRows>
        <PasswordRow />
        <TwoFactorRow />
        <SsoRow />
        <SessionsRow />
      </SettingsRows>
    </SettingsSection>
  )
}

export function ApiKeysSection() {
  const [creating, setCreating] = useState(false)

  return (
    <SettingsSection
      id="api-keys"
      title={<Trans>API keys</Trans>}
      description={
        <Trans>
          Sent in the <Code>X-Api-Key</Code> header. A key acts as you.
        </Trans>
      }
      actions={
        <Button
          size="xs"
          variant="subtle"
          leftSection={<IconPlus size={16} />}
          onClick={() => setCreating((open) => !open)}
        >
          <Trans>New key</Trans>
        </Button>
      }
      panelProps={{ p: 0 }}
    >
      <ApiKeysCard creating={creating} onCloseCreate={() => setCreating(false)} />
    </SettingsSection>
  )
}

function SsoRow() {
  const { t } = useLingui()
  const { me } = useAuth()
  const [sso, setSso] = useState<{ enabled: boolean; displayName: string } | null>(null)
  const [open, setOpen] = useState(false)
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
  // LoginPage's ssoError. It is a short code, mapped to a message here.
  const [searchParams, setSearchParams] = useSearchParams()
  const [linkResult] = useState(() => ({
    linked: searchParams.get('oidcLinked') === '1',
    error: searchParams.get('oidcLinkError'),
  }))

  // setSearchParams changes identity once the params are dropped, and StrictMode runs effects twice,
  // so the effect re-runs; the toast must still only show once.
  const linkResultShown = useRef(false)
  useEffect(() => {
    if (linkResultShown.current) return
    linkResultShown.current = true
    if (linkResult.linked) {
      notifications.show({ message: now`Single sign-on linked to your account`, color: 'var(--ok)' })
    } else if (linkResult.error) {
      notifications.show({ message: i18n._(ssoLinkErrorLabel(linkResult.error)), color: 'var(--danger)' })
    }
    // Dropped once shown so a reload does not repeat it.
    if (!linkResult.linked && linkResult.error === null) return
    setSearchParams(
      (current) => {
        const next = new URLSearchParams(current)
        next.delete('oidcLinked')
        next.delete('oidcLinkError')
        return next
      },
      { replace: true },
    )
  }, [linkResult, setSearchParams])

  if (!sso?.enabled) {
    return null
  }

  // Empty when no admin has typed a button label; the client supplies its own translated copy of
  // the English default rather than showing the server's raw literal.
  const displayName = sso.displayName || t`Single sign-on`
  const oidcUserName = me?.oidcUserName

  if (me?.oidcLinked) {
    return (
      <SettingsRow
        title={<Trans>Single sign-on</Trans>}
        status={{ tone: 'ok', label: t`Linked` }}
        summary={
          <Trans>
            Signed in as <Code>{oidcUserName}</Code> on {displayName}.
          </Trans>
        }
        aside={
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
        }
      />
    )
  }

  return (
    <SettingsRow
      title={<Trans>Single sign-on</Trans>}
      status={{ tone: 'neutral', label: t`Not linked` }}
      summary={
        <Trans>
          Not linked yet. Confirm your password, then sign in with {displayName} to link it to this account.
        </Trans>
      }
      opened={open}
      onToggle={() => setOpen((value) => !value)}
      lazy
    >
      <Stack
        component="form"
        gap="sm"
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
          maw={320}
        />
        <div className="settings-row-foot">
          <span />
          <Group gap="xs">
            <Button
              size="xs"
              variant="subtle"
              color="var(--neutral)"
              onClick={() => {
                setLinkPassword('')
                setOpen(false)
              }}
            >
              <Trans>Cancel</Trans>
            </Button>
            <Button type="submit" size="xs" loading={confirmLink.isPending}>
              <Trans>Link {displayName}</Trans>
            </Button>
          </Group>
        </div>
      </Stack>
    </SettingsRow>
  )
}

function PasswordRow() {
  const { t } = useLingui()
  const { data: status } = useTwoFactorStatus()
  const [open, setOpen] = useState(false)
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const change = useChangePassword()
  const blocked = !current
    ? t`Enter your current password first`
    : next.length < 10
      ? t`The new password needs at least 10 characters`
      : null

  const close = () => {
    setCurrent('')
    setNext('')
    setOpen(false)
  }

  return (
    <SettingsRow
      title={<Trans>Password</Trans>}
      status={
        status ? (status.hasPassword ? { tone: 'ok', label: t`Set` } : { tone: 'neutral', label: t`Not set up` }) : null
      }
      summary={<Trans>Changing it signs out your other devices.</Trans>}
      opened={open}
      onToggle={() => setOpen((value) => !value)}
      lazy
    >
      <Stack
        component="form"
        gap="sm"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          if (blocked) return
          change.mutate(
            { currentPassword: current, newPassword: next },
            {
              onSuccess: () => {
                close()
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
        <Group grow align="flex-start">
          <PasswordInput
            label={t`Current`}
            autoComplete="current-password"
            value={current}
            onChange={(e) => setCurrent(e.currentTarget.value)}
          />
          <PasswordInput
            label={t`New`}
            description={t`At least 10 characters`}
            autoComplete="new-password"
            value={next}
            onChange={(e) => setNext(e.currentTarget.value)}
          />
        </Group>
        <div className="settings-row-foot">
          <span />
          <Group gap="xs">
            <Button size="xs" variant="subtle" color="var(--neutral)" onClick={close}>
              <Trans>Cancel</Trans>
            </Button>
            {/* data-disabled rather than disabled: a disabled button gets no hover or focus, so the
                tooltip saying why could never show. The submit handler holds the guard. */}
            <Tooltip label={blocked} disabled={!blocked} events={{ hover: true, focus: true, touch: true }}>
              <Button
                type="submit"
                size="xs"
                loading={change.isPending}
                data-disabled={blocked !== null || undefined}
                aria-disabled={blocked !== null}
              >
                <Trans>Change</Trans>
              </Button>
            </Tooltip>
          </Group>
        </div>
      </Stack>
    </SettingsRow>
  )
}

function TwoFactorRow() {
  const { t } = useLingui()
  const { data: status } = useTwoFactorStatus()
  const start = useStartTwoFactorSetup()
  const enable = useEnableTwoFactor()
  const disable = useDisableTwoFactor()
  const regenerate = useRegenerateRecoveryCodes()

  const [open, setOpen] = useState(false)
  const [enrolling, setEnrolling] = useState<{ sharedKey: string; authenticatorUri: string } | null>(null)
  const [code, setCode] = useState('')
  const [enablePassword, setEnablePassword] = useState('')
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null)
  const [disablePassword, setDisablePassword] = useState('')
  const [regenPassword, setRegenPassword] = useState('')
  const [regenCode, setRegenCode] = useState('')
  const [qrDataUrl, setQrDataUrl] = useState<string | null>(null)
  const keyCopy = useCopyText()
  const codesCopy = useCopyText()

  useEffect(() => {
    if (!enrolling) {
      setQrDataUrl(null)
      return
    }
    let cancelled = false
    // Only two-factor setup draws a code, so the encoder stays out of every other Settings visit.
    import('qrcode')
      .then(({ default: QRCode }) => QRCode.toDataURL(enrolling.authenticatorUri, { width: 200, margin: 1 }))
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

  const cancelSetup = () => {
    setEnrolling(null)
    setCode('')
    setEnablePassword('')
    setOpen(false)
  }
  const canSetUp = !!status && status.available && !status.ssoDelegated
  const expandable = !!status && (status.enabled || canSetUp)

  const summary = status?.enabled ? (
    plural(status.recoveryCodesLeft, {
      one: '# recovery code left. A new set replaces the old codes.',
      other: '# recovery codes left. A new set replaces the old codes.',
    })
  ) : status?.ssoDelegated ? (
    <Trans>
      Sign-in relies on your identity provider. A code, once set up, protects password sign-in only.
    </Trans>
  ) : status && !status.available ? (
    <Trans>This account has no password login for two-factor to protect.</Trans>
  ) : (
    <Trans>The single biggest improvement if Maki is reachable from the internet.</Trans>
  )

  return (
    <>
      <SettingsRow
        title={<Trans>Two-factor authentication</Trans>}
        status={
          status ? (status.enabled ? { tone: 'ok', label: t`On` } : { tone: 'neutral', label: t`Not set up` }) : null
        }
        summary={summary}
        opened={open}
        onToggle={expandable ? () => setOpen((value) => !value) : undefined}
        lazy
      >
        {status?.enabled ? (
          <Stack gap="md">
            <Stack
              component="form"
              gap="xs"
              onSubmit={(e: FormEvent) => {
                e.preventDefault()
                if (!regenPassword || regenCode.length < 6) return
                regenerate.mutate({ code: regenCode, password: regenPassword }, {
                  onSuccess: (result) => {
                    setRegenPassword('')
                    setRegenCode('')
                    setRecoveryCodes(result.recoveryCodes)
                  },
                })
              }}
            >
              <Text size="sm" fw={600} c="var(--ink-hi)">
                <Trans>New recovery codes</Trans>
              </Text>
              <Group align="flex-end">
                <PasswordInput
                  label={t`Your password`}
                  autoComplete="current-password"
                  value={regenPassword}
                  onChange={(e) => setRegenPassword(e.currentTarget.value)}
                  w={220}
                />
                <TextInput
                  label={t`Code from your app`}
                  inputMode="numeric"
                  value={regenCode}
                  onChange={(e) => setRegenCode(e.currentTarget.value)}
                  w={170}
                />
                <Button
                  type="submit"
                  variant="default"
                  loading={regenerate.isPending}
                  disabled={!regenPassword || regenCode.length < 6}
                >
                  <Trans>New recovery codes</Trans>
                </Button>
              </Group>
            </Stack>

            <Stack
              component="form"
              gap="xs"
              className="account-turn-off"
              onSubmit={(e: FormEvent) => {
                e.preventDefault()
                if (!disablePassword) return
                disable.mutate(disablePassword, {
                  onSuccess: () => {
                    setDisablePassword('')
                    setOpen(false)
                    notifications.show({ message: now`Two-factor authentication disabled`, color: 'var(--warn)' })
                  },
                })
              }}
            >
              <Text size="sm" fw={600} c="var(--ink-hi)">
                <Trans>Turn it off</Trans>
              </Text>
              <Group align="flex-end">
                <PasswordInput
                  label={t`Confirm your password to turn it off`}
                  autoComplete="current-password"
                  value={disablePassword}
                  onChange={(e) => setDisablePassword(e.currentTarget.value)}
                  w={280}
                />
                <Button
                  type="submit"
                  variant="subtle"
                  color="var(--danger)"
                  loading={disable.isPending}
                  disabled={!disablePassword}
                >
                  <Trans>Disable</Trans>
                </Button>
              </Group>
            </Stack>
          </Stack>
        ) : enrolling ? (
          <Stack
            component="form"
            gap="sm"
            onSubmit={(e: FormEvent) => {
              e.preventDefault()
              if (code.length < 6 || !enablePassword) return
              enable.mutate({ code, password: enablePassword }, {
                onSuccess: (result) => {
                  setEnrolling(null)
                  setCode('')
                  setEnablePassword('')
                  setOpen(false)
                  setRecoveryCodes(result.recoveryCodes)
                },
              })
            }}
          >
            <div className="account-setup">
              {qrDataUrl ? (
                <img
                  className="account-setup-qr"
                  src={qrDataUrl}
                  alt={t`Two-factor authenticator QR code`}
                  width={160}
                  height={160}
                />
              ) : (
                <div className="account-setup-qr" aria-hidden />
              )}
              <Stack gap="sm">
                <Text size="sm">
                  <Trans>
                    Scan this with your authenticator app, or enter the key manually, then enter the code
                    it shows. The key is only active once a code has verified, so a mistyped key cannot
                    lock you out.
                  </Trans>
                </Text>
                <Group gap="xs">
                  <Code>{enrolling.sharedKey}</Code>
                  <Button
                    size="compact-xs"
                    variant="default"
                    onClick={() => void keyCopy.copy(enrolling.sharedKey)}
                    leftSection={keyCopy.copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
                  >
                    {keyCopy.copied ? <Trans>Copied</Trans> : <Trans>Copy</Trans>}
                  </Button>
                </Group>
                <Group grow align="flex-start">
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
                </Group>
              </Stack>
            </div>
            <div className="settings-row-foot">
              <Text size="xs" c="var(--ink-3)">
                <Trans>Recovery codes are shown once you verify.</Trans>
              </Text>
              <Group gap="xs">
                <Button size="xs" variant="subtle" color="var(--neutral)" onClick={cancelSetup}>
                  <Trans>Cancel</Trans>
                </Button>
                <Button type="submit" size="xs" loading={enable.isPending} disabled={code.length < 6 || !enablePassword}>
                  <Trans>Verify and enable</Trans>
                </Button>
              </Group>
            </div>
          </Stack>
        ) : (
          <div className="settings-row-foot">
            <span />
            <Group gap="xs">
              <Button size="xs" variant="subtle" color="var(--neutral)" onClick={() => setOpen(false)}>
                <Trans>Cancel</Trans>
              </Button>
              <Button
                size="xs"
                loading={start.isPending}
                onClick={() => start.mutate(undefined, { onSuccess: setEnrolling })}
              >
                <Trans>Set up</Trans>
              </Button>
            </Group>
          </div>
        )}
      </SettingsRow>

      <Modal
        opened={recoveryCodes !== null}
        onClose={() => setRecoveryCodes(null)}
        title={
          <ModalHeading
            title={<Trans>Save your recovery codes</Trans>}
            description={<Trans>Each code works once if you lose your authenticator.</Trans>}
          />
        }
        closeOnClickOutside={false}
        closeOnEscape={false}
      >
        <Stack gap="md">
          <ModalNote tone="warn">
            <Trans>
              These are shown once. They are stored hashed, so nobody (including you) can read them
              back. Keep them somewhere you can reach without your authenticator.
            </Trans>
          </ModalNote>
          <div className="account-codes">
            {recoveryCodes?.map((recoveryCode) => (
              <span key={recoveryCode}>{recoveryCode}</span>
            ))}
          </div>
        </Stack>
        <ModalFooter>
          <Button
            variant="default"
            onClick={() => void codesCopy.copy(recoveryCodes?.join('\n') ?? '')}
            leftSection={codesCopy.copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
          >
            {codesCopy.copied ? <Trans>Copied</Trans> : <Trans>Copy codes</Trans>}
          </Button>
          <Button onClick={() => setRecoveryCodes(null)}>
            <Trans>I have saved them</Trans>
          </Button>
        </ModalFooter>
      </Modal>
    </>
  )
}

function ApiKeysCard({ creating, onCloseCreate }: { creating: boolean; onCloseCreate: () => void }) {
  const { t } = useLingui()
  const { data: keys } = useApiKeys()
  const create = useCreateApiKey()
  const revoke = useRevokeApiKey()
  const mint = useMintConfirmation()

  const [name, setName] = useState('')
  const [keyPassword, setKeyPassword] = useState('')
  const [created, setCreated] = useState<CreatedApiKey | null>(null)
  const [revoking, setRevoking] = useState<ApiKey | null>(null)
  const secretCopy = useCopyText()
  const revokingName = revoking?.name ?? ''

  // The OPDS feed token is a key row too, but it is minted, shown and rotated on the OPDS card.
  const fullKeys = keys?.filter((key) => key.scope === 'Full')

  const closeCreate = () => {
    setName('')
    setKeyPassword('')
    onCloseCreate()
  }

  return (
    <>
      {creating && (
        <SettingsRowsBlock>
          <Stack
            component="form"
            gap="sm"
            onSubmit={(e: FormEvent) => {
              e.preventDefault()
              if (!name.trim() || mint.signInStale) return
              create.mutate(
                { name: name.trim(), password: keyPassword },
                {
                  onSuccess: (result) => {
                    setCreated(result)
                    closeCreate()
                  },
                },
              )
            }}
          >
            <Group grow align="flex-start">
              <TextInput
                label={t`Name`}
                placeholder={t`Phone reader`}
                value={name}
                onChange={(e) => setName(e.currentTarget.value)}
                data-autofocus
              />
              {mint.needsPassword && <MintPasswordField value={keyPassword} onChange={setKeyPassword} />}
            </Group>

            {mint.signInStale && <RecentSignInNotice canSignInAgain={mint.canSignInAgain} anchor="api-keys" />}

            <div className="settings-row-foot">
              <span />
              <Group gap="xs">
                <Button size="xs" variant="subtle" color="var(--neutral)" onClick={closeCreate}>
                  <Trans>Cancel</Trans>
                </Button>
                <Tooltip
                  label={t`Name the key first`}
                  disabled={!!name.trim()}
                  events={{ hover: true, focus: true, touch: true }}
                >
                  <Button
                    type="submit"
                    size="xs"
                    loading={create.isPending}
                    data-disabled={!name.trim() || mint.signInStale || undefined}
                    aria-disabled={!name.trim() || mint.signInStale}
                  >
                    <Trans>Create</Trans>
                  </Button>
                </Tooltip>
              </Group>
            </div>
          </Stack>
        </SettingsRowsBlock>
      )}

      {fullKeys && fullKeys.length > 0 ? (
        <SettingsRows>
          {fullKeys.map((key) => (
            <SettingsRow
              key={key.id}
              title={<span data-revoked={key.revokedAt ? '' : undefined}>{key.name}</span>}
              chips={<Code data-revoked={key.revokedAt ? '' : undefined}>{key.prefix}…</Code>}
              status={key.revokedAt ? { tone: 'neutral', label: t`Revoked` } : null}
              summary={
                <span data-revoked={key.revokedAt ? '' : undefined}>
                  {key.lastUsedAt ? (
                    <Trans>Last used {formatDateTime(key.lastUsedAt)}</Trans>
                  ) : (
                    <Trans>Last used never</Trans>
                  )}
                </span>
              }
              aside={
                key.revokedAt ? undefined : (
                  <Button
                    size="compact-xs"
                    variant="subtle"
                    color="var(--danger)"
                    onClick={() => setRevoking(key)}
                  >
                    <Trans>Revoke</Trans>
                  </Button>
                )
              }
            />
          ))}
        </SettingsRows>
      ) : (
        !creating && (
          <SettingsRowsBlock>
            <EmptyState
              compact
              title={t`No keys yet`}
              description={t`New key makes one for a script or an app.`}
            />
          </SettingsRowsBlock>
        )
      )}

      <Modal
        opened={created !== null}
        onClose={() => setCreated(null)}
        title={<ModalHeading title={<Trans>Your new API key</Trans>} description={created?.key.name} />}
        size="lg"
        closeOnClickOutside={false}
        closeOnEscape={false}
      >
        <Stack gap="md">
          <ModalNote tone="warn">
            <Trans>
              Copy this now. Only its fingerprint is stored, so it cannot be shown again: if you lose
              it, revoke this one and create another.
            </Trans>
          </ModalNote>
          <div className="account-secret">
            <Code>{created?.secret}</Code>
            <Button
              size="xs"
              variant="default"
              onClick={() => void secretCopy.copy(created?.secret ?? '')}
              leftSection={secretCopy.copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
            >
              {secretCopy.copied ? <Trans>Copied</Trans> : <Trans>Copy</Trans>}
            </Button>
          </div>
        </Stack>
        <ModalFooter
          status={
            <Trans>
              Sent as <Code>X-Api-Key</Code>
            </Trans>
          }
        >
          <Button onClick={() => setCreated(null)}>
            <Trans>I have copied it</Trans>
          </Button>
        </ModalFooter>
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
    </>
  )
}

function SessionsRow() {
  const revoke = useRevokeSessions()

  return (
    <SettingsRow
      title={<Trans>Sessions</Trans>}
      summary={<Trans>Signs out every other browser and device. This one stays signed in.</Trans>}
      aside={
        <Button
          variant="default"
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
      }
    />
  )
}
