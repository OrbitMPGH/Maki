import { useEffect, useState } from 'react'
import {
  Alert,
  Button,
  Card,
  Code,
  Group,
  NumberInput,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  useOidcSettings,
  useSaveOidcSettings,
  useSaveSecuritySettings,
  useSecuritySettings,
  type OidcSettings,
  type SecuritySettings,
} from '../../api/auth'

/**
 * Instance security settings. Admin-only.
 *
 * Every value here configures something the host builds once at startup: the session cookie's Secure
 * flag, HSTS, the trusted-proxy list, the lockout thresholds. A change needs a restart. The card
 * says so rather than pretending otherwise: silently requiring a restart is how someone concludes the
 * setting does nothing.
 */
export function SecuritySection() {
  const { data } = useSecuritySettings()
  const save = useSaveSecuritySettings()
  const [draft, setDraft] = useState<SecuritySettings | null>(null)

  useEffect(() => {
    if (data) setDraft(data)
  }, [data])

  if (!draft) return null

  const dirty = data !== undefined && JSON.stringify(draft) !== JSON.stringify(data)

  return (
    <Card withBorder radius="md" padding="md" id="security">
      <Title order={4} mb="sm">
        Security
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Changes take effect after Maki restarts.
      </Text>

      <Stack gap="md">
        <Switch
          label="Require HTTPS"
          description="Redirects HTTP to HTTPS, sends HSTS, and marks the session cookie Secure. Turn this on once Maki is behind TLS, and not before, because a Secure cookie sent over plain HTTP never comes back and sign-in fails with nothing to show why."
          checked={draft.requireHttps}
          onChange={(e) => setDraft({ ...draft, requireHttps: e.currentTarget.checked })}
        />

        <TextInput
          label="Trusted proxies"
          description="Comma-separated IP addresses or CIDR networks, e.g. 172.18.0.0/16. Only these are believed when they set X-Forwarded-For. Leave empty if Maki is reached directly."
          placeholder="172.18.0.0/16, 10.0.0.5"
          value={draft.trustedProxies}
          onChange={(e) => setDraft({ ...draft, trustedProxies: e.currentTarget.value })}
        />

        {!draft.trustedProxies.trim() && (
          <Alert color="yellow" variant="light">
            With no trusted proxy configured, forwarded headers are ignored entirely, deliberately,
            since believing them from anyone would let a client claim any address and slip past both
            rate limiting and account lockout. Behind a reverse proxy that means every failed sign-in
            is attributed to the proxy: name it above so lockout and the audit log see the real client.
          </Alert>
        )}

        <Group grow align="flex-start">
          <NumberInput
            label="Failed sign-ins before lockout"
            description={<>Set to <Code>0</Code> to disable lockout.</>}
            min={0}
            max={100}
            value={draft.lockoutMaxAttempts}
            onChange={(v) => setDraft({ ...draft, lockoutMaxAttempts: Number(v) || 0 })}
          />
          <NumberInput
            label="Lockout duration (minutes)"
            min={1}
            max={1440}
            value={draft.lockoutMinutes}
            onChange={(v) => setDraft({ ...draft, lockoutMinutes: Number(v) || 1 })}
          />
          <NumberInput
            label="Session lifetime (days)"
            description="Sliding: activity extends it."
            min={1}
            max={365}
            value={draft.sessionDays}
            onChange={(v) => setDraft({ ...draft, sessionDays: Number(v) || 1 })}
          />
        </Group>

        <Group justify="flex-end">
          <Button
            loading={save.isPending}
            disabled={!dirty}
            onClick={() =>
              save.mutate(draft, {
                onSuccess: () =>
                  notifications.show({
                    message: 'Security settings saved. Restart Maki to apply them.',
                    color: 'green',
                  }),
                onError: (e) => notifications.show({ message: e.message, color: 'red' }),
              })
            }
          >
            Save
          </Button>
        </Group>
      </Stack>
    </Card>
  )
}

/**
 * Single sign-on. Admin-only, and a restart away from taking effect for the same reason the rest of
 * this file is: the OpenID Connect handler is built once and fetches the provider's discovery
 * document on first use.
 */
export function OidcSection() {
  const { data } = useOidcSettings()
  const save = useSaveOidcSettings()
  const [draft, setDraft] = useState<OidcSettings | null>(null)

  useEffect(() => {
    if (data) setDraft(data)
  }, [data])

  if (!draft) return null

  const dirty = data !== undefined && JSON.stringify(draft) !== JSON.stringify(data)
  const mapsPermissions = Boolean(draft.adminClaim.trim() || draft.permissionClaim.trim())

  return (
    <Card withBorder radius="md" padding="md" id="oidc">
      <Title order={4} mb="sm">
        Single sign-on
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Sign in through an OpenID Connect provider (Authelia, Keycloak, Authentik, Entra ID). Changes
        take effect after Maki restarts. Register{' '}
        <Code>{draft.redirectPath}</Code> as this client&apos;s redirect URI, on whatever host Maki is
        reached at.
      </Text>

      <Stack gap="md">
        <Switch
          label="Enable single sign-on"
          description="Adds a button to the login page. Local passwords keep working unless you restrict them below."
          checked={draft.enabled}
          onChange={(e) => setDraft({ ...draft, enabled: e.currentTarget.checked })}
        />

        <TextInput
          label="Issuer URL"
          description="The provider's issuer, without /.well-known/openid-configuration. Maki appends that itself."
          placeholder="https://auth.example.com"
          value={draft.authority}
          onChange={(e) => setDraft({ ...draft, authority: e.currentTarget.value })}
        />

        <Group grow align="flex-start">
          <TextInput
            label="Client ID"
            value={draft.clientId}
            onChange={(e) => setDraft({ ...draft, clientId: e.currentTarget.value })}
          />
          <TextInput
            label="Client secret"
            description="Leave empty for a public client. The exchange is protected by PKCE either way."
            value={draft.clientSecret}
            onChange={(e) => setDraft({ ...draft, clientSecret: e.currentTarget.value })}
          />
        </Group>

        <Group grow align="flex-start">
          <TextInput
            label="Scopes"
            description="openid is always requested."
            placeholder="profile email"
            value={draft.scopes}
            onChange={(e) => setDraft({ ...draft, scopes: e.currentTarget.value })}
          />
          <TextInput
            label="Button label"
            placeholder="Single sign-on"
            value={draft.displayName}
            onChange={(e) => setDraft({ ...draft, displayName: e.currentTarget.value })}
          />
        </Group>

        <Switch
          label="Require single sign-on"
          description="Refuses password sign-in for everyone except administrators, who keep it so a provider outage can never lock you out of your own library."
          checked={draft.oidcOnly}
          onChange={(e) => setDraft({ ...draft, oidcOnly: e.currentTarget.checked })}
        />

        {draft.breakGlassActive && (
          <Alert color="yellow" variant="light">
            <Code>MAKI_ALLOW_LOCAL_LOGIN</Code> is set in this instance&apos;s environment, so password
            sign-in is available to every account regardless of the switch above. Remove the variable
            and restart to enforce it again.
          </Alert>
        )}

        <Switch
          label="Create accounts on first sign-in"
          description="Off by default: with it on, anyone your provider will authenticate gets a Maki account. New accounts start with no library access until you grant a root folder."
          checked={draft.autoProvision}
          onChange={(e) => setDraft({ ...draft, autoProvision: e.currentTarget.checked })}
        />

        <TextInput
          label="Username claim"
          description="Used when creating an account. The durable link is always the provider's subject, so renaming a user upstream does not strand them here."
          placeholder="preferred_username"
          value={draft.usernameClaim}
          onChange={(e) => setDraft({ ...draft, usernameClaim: e.currentTarget.value })}
        />

        <Group grow align="flex-start">
          <TextInput
            label="Admin claim"
            description={<>Written <Code>claim=value</Code>, e.g. <Code>groups=maki-admins</Code>.</>}
            placeholder="groups=maki-admins"
            value={draft.adminClaim}
            onChange={(e) => setDraft({ ...draft, adminClaim: e.currentTarget.value })}
          />
          <TextInput
            label="Permission claim"
            description={<>Claim whose values name permissions, e.g. <Code>DownloadChapters</Code>. Values that match nothing are ignored.</>}
            placeholder="groups"
            value={draft.permissionClaim}
            onChange={(e) => setDraft({ ...draft, permissionClaim: e.currentTarget.value })}
          />
        </Group>

        {mapsPermissions && (
          <Alert color="blue" variant="light">
            With either claim set, your provider is the authority on permissions: they are recomputed
            on every sign-in, so changes made on the Users page are overwritten the next time that
            person signs in. Leave both empty to keep permissions here.
          </Alert>
        )}

        <Group justify="flex-end">
          <Button
            loading={save.isPending}
            disabled={!dirty}
            onClick={() =>
              save.mutate(draft, {
                onSuccess: () =>
                  notifications.show({
                    message: 'Single sign-on saved. Restart Maki to apply it.',
                    color: 'green',
                  }),
                onError: (e) => notifications.show({ message: e.message, color: 'red' }),
              })
            }
          >
            Save
          </Button>
        </Group>
      </Stack>
    </Card>
  )
}
