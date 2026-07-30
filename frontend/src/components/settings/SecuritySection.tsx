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
import { useSaveSecuritySettings, useSecuritySettings, type SecuritySettings } from '../../api/auth'

/**
 * Instance security settings. Admin-only.
 *
 * Every value here configures something the host builds once at startup — the session cookie's Secure
 * flag, HSTS, the trusted-proxy list, the lockout thresholds — so a change needs a restart. The card
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
          description="Redirects HTTP to HTTPS, sends HSTS, and marks the session cookie Secure. Turn this on once Maki is behind TLS — and not before, because a Secure cookie sent over plain HTTP never comes back and sign-in fails with nothing to show why."
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
            With no trusted proxy configured, forwarded headers are ignored entirely — deliberately,
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
            description="Sliding — activity extends it."
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
