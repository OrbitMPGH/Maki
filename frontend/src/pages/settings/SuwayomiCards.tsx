import { useEffect, useReducer, useState } from 'react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { Button, Group, PasswordInput, Select, Stack, Text, TextInput } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { SettingsSection } from './SettingsSection'
import { savedToast } from './sharedRecord'
import { ManageSourcesModal } from './ManageSourcesModal'
import { useSources } from '../../api/hooks'
import {
  useSaveSuwayomiConnection, useSuwayomiConnection, useTestSuwayomiConnection,
  type SuwayomiAuthMode, type SuwayomiConnectionInput,
} from '../../api/suwayomi'

/**
 * The optional Suwayomi connection. Extensions are installed in Suwayomi itself; this card only
 * says where it is and how to sign in. Every source Suwayomi has shows up in Manage sources,
 * switched off, and is switched on there like any other.
 */
export function SuwayomiCard() {
  const { t } = useLingui()
  const { data: saved } = useSuwayomiConnection()
  const { data: sources } = useSources()
  const save = useSaveSuwayomiConnection()
  const test = useTestSuwayomiConnection()
  const [url, setUrl] = useState('')
  const [authMode, setAuthMode] = useState<SuwayomiAuthMode>('none')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [managing, setManaging] = useState(false)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useEffect(() => {
    if (saved) {
      setUrl(saved.url ?? '')
      setAuthMode(saved.authMode)
      setUsername(saved.username ?? '')
      setPassword('')
    }
  }, [saved, discarded])

  const dirty =
    saved !== undefined &&
    (url !== (saved.url ?? '') ||
      authMode !== saved.authMode ||
      username !== (saved.username ?? '') ||
      password !== '')

  const input = (): SuwayomiConnectionInput => ({
    url: url.trim() || null,
    authMode,
    username: authMode === 'none' ? null : username.trim() || null,
    password: password === '' ? null : password,
  })

  // The server forgets the stored password when the URL changes, so the hint must not promise otherwise.
  const keepsPassword = Boolean(saved?.passwordSet) && url.trim().replace(/\/+$/, '') === (saved?.url ?? '')
  const suwayomiSources = (sources ?? []).filter((s) => s.provider === 'suwayomi')
  const installed = suwayomiSources.length
  const inUse = suwayomiSources.filter((s) => s.enabled).length
  const connected = Boolean(saved?.url)

  return (
    <SettingsSection
      id="suwayomi"
      title="Suwayomi"
      description={t`Uses the sources of the extensions installed in Suwayomi.`}
      dirty={dirty}
      saving={save.isPending}
      onSave={() => save.mutate(input(), { onSuccess: savedToast })}
      onDiscard={discard}
    >
      <Stack gap="sm">
        <Group align="flex-end" wrap="wrap" className="settings-field-row">
          <TextInput
            autoComplete="off"
            label={t`URL`}
            description={t`Include the subpath if Suwayomi runs under one.`}
            placeholder="http://suwayomi:4567"
            value={url}
            onChange={(e) => setUrl(e.currentTarget.value)}
            style={{ flex: 2, minWidth: 220 }}
          />
          <Select
            label={t`Sign-in`}
            description={t`Match Suwayomi's own auth mode setting.`}
            value={authMode}
            onChange={(value) => setAuthMode((value as SuwayomiAuthMode | null) ?? 'none')}
            allowDeselect={false}
            data={[
              { value: 'none', label: t`None` },
              { value: 'basic', label: t`Basic auth` },
              { value: 'uilogin', label: t`UI login (v2.2 or later)` },
            ]}
            style={{ flex: 1, minWidth: 180 }}
          />
        </Group>
        {authMode !== 'none' && (
          <Group align="flex-end" wrap="wrap" className="settings-field-row">
            <TextInput
              autoComplete="off"
              label={t`Username`}
              value={username}
              onChange={(e) => setUsername(e.currentTarget.value)}
              style={{ flex: 1, minWidth: 180 }}
            />
            <PasswordInput
              autoComplete="new-password"
              label={t`Password`}
              placeholder={keepsPassword ? t`Saved. Leave empty to keep it.` : undefined}
              description={
                saved?.passwordSet && !keepsPassword && password === '' ? t`A new URL clears the saved password, so enter it again.` : undefined
              }
              value={password}
              onChange={(e) => setPassword(e.currentTarget.value)}
              style={{ flex: 1, minWidth: 180 }}
            />
          </Group>
        )}
        <Group justify="space-between" wrap="wrap">
          <Text size="sm" c="dimmed">
            {connected ? (
              <Plural
                value={installed}
                _0="No Suwayomi sources yet."
                one={`# Suwayomi source, ${inUse} switched on.`}
                other={`# Suwayomi sources, ${inUse} switched on.`}
              />
            ) : (
              <Trans>Not connected.</Trans>
            )}
          </Text>
          <Group gap="xs">
            <Button
              variant="default"
              loading={test.isPending}
              disabled={!url.trim()}
              onClick={() =>
                test.mutate(input(), {
                  onSuccess: ({ version, sourceCount }) =>
                    notifications.show({
                      color: 'var(--ok)',
                      message: plural(sourceCount, {
                        one: `Suwayomi ${version} answered with # source installed.`,
                        other: `Suwayomi ${version} answered with # sources installed.`,
                      }),
                    }),
                })
              }
            >
              <Trans>Test</Trans>
            </Button>
            <Button disabled={!connected || dirty} onClick={() => setManaging(true)}>
              <Trans>Manage sources</Trans>
            </Button>
          </Group>
        </Group>
      </Stack>
      <ManageSourcesModal opened={managing} onClose={() => setManaging(false)} initialProvider="suwayomi" />
    </SettingsSection>
  )
}
