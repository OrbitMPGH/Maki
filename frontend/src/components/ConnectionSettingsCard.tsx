import { useEffect, useState } from 'react'
import { SettingsHelp } from './settings/SettingsHelp'
import type { ReactNode } from 'react'
import { Button, Group, PasswordInput, TextInput, Title } from '@mantine/core'
import { Panel } from './ui/Panel'
import { SaveButton } from './settings/SaveButton'
import { notifications } from '@mantine/notifications'
import { Trans } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  useConnectionSettings,
  useSaveConnectionSettings,
  useTestConnectionSettings,
  type ConnectionName,
} from '../api/hooks'

export interface ConnectionField {
  key: string
  label: string
  placeholder?: string
  secret?: boolean
}

/** Generic URL+credentials settings card with Test/Save, used for every external service connection. */
export function ConnectionSettingsCard({
  name,
  title,
  description,
  fields,
  children,
}: {
  name: ConnectionName
  title: string
  description: string
  fields: ConnectionField[]
  children?: ReactNode
}) {
  return (
    <Panel>
      <Title order={4} mb="sm">
        {title}
      </Title>
      <SettingsHelp mb="md">
        {description}
      </SettingsHelp>
      <ConnectionForm name={name} title={title} fields={fields} />
      {children}
    </Panel>
  )
}

/** The fields and Test/Save row on their own, for surfaces that frame a connection differently (the setup guide). */
export function ConnectionForm({
  name,
  title,
  fields,
}: {
  name: ConnectionName
  title: string
  fields: ConnectionField[]
}) {
  const { data: saved } = useConnectionSettings<Record<string, string | null>>(name)
  const save = useSaveConnectionSettings<Record<string, string | null>>(name)
  const test = useTestConnectionSettings<Record<string, string | null>>(name)
  const [values, setValues] = useState<Record<string, string>>({})

  useEffect(() => {
    if (saved) {
      const next: Record<string, string> = {}
      for (const f of fields) next[f.key] = saved[f.key] ?? ''
      setValues(next)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [saved])

  const dirty = saved !== undefined && fields.some((f) => (values[f.key] ?? '') !== (saved[f.key] ?? ''))

  const payload = () =>
    Object.fromEntries(fields.map((f) => [f.key, values[f.key] || null]))

  return (
    <Group align="flex-end" wrap="wrap">
      {fields.map((f) =>
        f.secret ? (
          <PasswordInput
            key={f.key}
            label={f.label}
            placeholder={f.placeholder}
            value={values[f.key] ?? ''}
            onChange={(e) => {
              const value = e.currentTarget.value
              setValues((v) => ({ ...v, [f.key]: value }))
            }}
            style={{ flex: 1, minWidth: 180 }}
          />
        ) : (
          <TextInput
            key={f.key}
            label={f.label}
            placeholder={f.placeholder}
            value={values[f.key] ?? ''}
            onChange={(e) => {
              const value = e.currentTarget.value
              setValues((v) => ({ ...v, [f.key]: value }))
            }}
            style={{ flex: 1, minWidth: 180 }}
          />
        ),
      )}
      <Button
        variant="default"
        loading={test.isPending}
        onClick={() =>
          test.mutate(payload(), {
            onSuccess: () => notifications.show({ message: now`${title} is reachable`, color: 'var(--ok)' }),
          })
        }
      >
        <Trans>Test</Trans>
      </Button>
      <SaveButton
        dirty={dirty}
        loading={save.isPending}
        onClick={() =>
          save.mutate(payload(), {
            onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }),
          })
        }
      />
    </Group>
  )
}
