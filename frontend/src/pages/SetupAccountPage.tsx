import { useState } from 'react'
import { Button, List, PasswordInput, Stack, Text, TextInput } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { AuthError, AuthFrame } from '../components/auth/AuthFrame'
import { useSetup } from '../api/auth'

/** Matches the server's Identity policy, so the failure is shown before a round trip. */
const MIN_PASSWORD_LENGTH = 10

/**
 * First-run account setup: claims the placeholder administrator the multi-user migration created.
 *
 * The same screen serves a brand-new install and an upgraded single-user one. For the upgrade it is
 * purely a login being attached to data that already exists: the placeholder is user 1 and every
 * per-user row already points at it, so nothing is migrated here and nothing can be lost.
 *
 * Distinct from `SetupWizard`, which configures the library (root folders, sources) *after* there is
 * an account to configure it with.
 */
export function SetupAccountPage() {
  const { t } = useLingui()
  const [username, setUsername] = useState('admin')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const setup = useSetup()

  const mismatch = confirm.length > 0 && password !== confirm
  const tooShort = password.length > 0 && password.length < MIN_PASSWORD_LENGTH
  const ready = username.trim().length > 0 && password.length >= MIN_PASSWORD_LENGTH && !mismatch

  function submit(event: React.FormEvent) {
    event.preventDefault()
    if (!ready) return
    setup.mutate({ username: username.trim(), password })
  }

  return (
    <AuthFrame
      title={t`Create your account`}
      subtitle={
        <Trans>
          Create the administrator account. Your library and reading history, if you have any, are
          already attached to it.
        </Trans>
      }
      footer={
        <div className="auth-note">
          <Text fz="sm" fw={600} mb={6}>
            <Trans>Before exposing Maki to the internet</Trans>
          </Text>
          <List fz="sm" c="var(--ink-3)" spacing={4}>
            <List.Item>
              <Trans>Put it behind HTTPS, then turn on Settings → Security → Require HTTPS.</Trans>
            </List.Item>
            <List.Item>
              <Trans>
                Name your reverse proxy under Trusted proxies, or rate limiting and the audit log will see
                the proxy instead of the client.
              </Trans>
            </List.Item>
            <List.Item>
              <Trans>Add two-factor authentication under Settings → My account.</Trans>
            </List.Item>
          </List>
        </div>
      }
    >
      <form onSubmit={submit}>
        <Stack>
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
            description={t`At least ${MIN_PASSWORD_LENGTH} characters. Length is what matters, no symbol requirements.`}
            autoComplete="new-password"
            required
            error={tooShort ? t`Use at least ${MIN_PASSWORD_LENGTH} characters` : null}
            value={password}
            onChange={(e) => setPassword(e.currentTarget.value)}
          />
          <PasswordInput
            label={t`Confirm password`}
            autoComplete="new-password"
            required
            error={mismatch ? t`Passwords do not match` : null}
            value={confirm}
            onChange={(e) => setConfirm(e.currentTarget.value)}
          />

          {setup.error && <AuthError>{setup.error.message}</AuthError>}

          <Button type="submit" color="brand" loading={setup.isPending} disabled={!ready} fullWidth>
            <Trans>Create account</Trans>
          </Button>
        </Stack>
      </form>
    </AuthFrame>
  )
}
