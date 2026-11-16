import { useEffect, useReducer, useState } from 'react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { Button, Group, Input, PasswordInput, SegmentedControl, Stack, Text, TextInput } from '@mantine/core'
import { notifications } from '../../lib/toast'
import { SettingsSection } from './SettingsSection'
import { FieldAction } from '../../components/settings/FieldAction'
import { savedToast } from './sharedRecord'
import { ManageSourcesModal } from './ManageSourcesModal'
import { ConnectionTestLine } from '../../components/ConnectionSettingsCardStatus'
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
          <Input.Wrapper
            label={t`Sign-in`}
            description={t`Match Suwayomi's own auth mode setting.`}
            style={{ flex: '1 1 340px' }}
          >
            <SegmentedControl
              mt={4}
              aria-label={t`Sign-in`}
              value={authMode}
              onChange={(value) => setAuthMode(value as SuwayomiAuthMode)}
              data={[
                { value: 'none', label: t`None` },
                { value: 'basic', label: t`Basic auth` },
                { value: 'uilogin', label: t`UI login (v2.2 or later)` },
              ]}
            />
          </Input.Wrapper>
          <FieldAction>
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
          </FieldAction>
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
        {connected && <ConnectionTestLine testKey="suwayomi" mt={0} />}
        <Group justify="space-between" wrap="wrap">
          <Text size="sm" c="var(--ink-3)">
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
          <Button variant="default" disabled={!connected || dirty} onClick={() => setManaging(true)}>
            <Trans>Manage sources</Trans>
          </Button>
        </Group>
      </Stack>
      <ManageSourcesModal opened={managing} onClose={() => setManaging(false)} initialProvider="suwayomi" />
    </SettingsSection>
  )
}
