import { useEffect, useReducer, useState } from 'react'
import type { ReactNode } from 'react'
import { Button, Group, PasswordInput, TextInput } from '@mantine/core'
import { SettingsSection } from '../pages/settings/SettingsSection'
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

export interface SaveGroup {
  dirty: boolean
  saving: boolean
  /** Saves without a toast of its own; rejects on failure (the global mutation handler reports it). */
  commit: () => Promise<unknown>
  reset: () => void
}

const savedToast = () => notifications.show({ message: now`Saved`, color: 'var(--ok)' })

/** Generic URL+credentials settings card with Test/Save, used for every external service connection. */
export function ConnectionSettingsCard({
  name,
  title,
  description,
  fields,
  extra,
  children,
}: {
  name: ConnectionName
  title: string
  description: string
  fields: ConnectionField[]
  /** A second set of fields in the same card that saves through its own endpoint. */
  extra?: SaveGroup
  children?: ReactNode
}) {
  const form = useConnectionForm(name, title, fields)
  const extraDirty = extra?.dirty ?? false

  return (
    <SettingsSection
      id={name}
      title={title}
      description={description}
      dirty={form.dirty || extraDirty}
      saving={form.saving || (extra?.saving ?? false)}
      onSave={() => {
        const jobs = [form.dirty && form.commit(), extraDirty && extra?.commit()].filter((j) => j instanceof Promise)
        Promise.all(jobs).then(savedToast, () => {})
      }}
      onDiscard={() => {
        form.reset()
        extra?.reset()
      }}
    >
      <ConnectionFields form={form} fields={fields} />
      {children}
    </SettingsSection>
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
  const form = useConnectionForm(name, title, fields)
  return (
    <ConnectionFields form={form} fields={fields}>
      <SaveButton dirty={form.dirty} loading={form.saving} onClick={form.save} />
    </ConnectionFields>
  )
}

function useConnectionForm(name: ConnectionName, title: string, fields: ConnectionField[]) {
  const { data: saved } = useConnectionSettings<Record<string, string | null>>(name)
  const save = useSaveConnectionSettings<Record<string, string | null>>(name)
  const test = useTestConnectionSettings<Record<string, string | null>>(name)
  const [values, setValues] = useState<Record<string, string>>({})
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useEffect(() => {
    if (saved) {
      const next: Record<string, string> = {}
      for (const f of fields) next[f.key] = saved[f.key] ?? ''
      setValues(next)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [saved, discarded])

  const dirty = saved !== undefined && fields.some((f) => (values[f.key] ?? '') !== (saved[f.key] ?? ''))

  const payload = () =>
    Object.fromEntries(fields.map((f) => [f.key, values[f.key] || null]))

  return {
    values,
    setValue: (key: string, value: string) => setValues((v) => ({ ...v, [key]: value })),
    dirty,
    saving: save.isPending,
    save: () => save.mutate(payload(), { onSuccess: savedToast }),
    commit: () => save.mutateAsync(payload()),
    reset: discard,
    testing: test.isPending,
    test: () =>
      test.mutate(payload(), {
        onSuccess: () => notifications.show({ message: now`${title} is reachable`, color: 'var(--ok)' }),
      }),
  }
}

function ConnectionFields({
  form,
  fields,
  children,
}: {
  form: ReturnType<typeof useConnectionForm>
  fields: ConnectionField[]
  children?: ReactNode
}) {
  return (
    <Group align="flex-end" wrap="wrap">
      {fields.map((f) =>
        f.secret ? (
          <PasswordInput
            key={f.key}
            label={f.label}
            placeholder={f.placeholder}
            value={form.values[f.key] ?? ''}
            onChange={(e) => form.setValue(f.key, e.currentTarget.value)}
            style={{ flex: 1, minWidth: 180 }}
          />
        ) : (
          <TextInput
            key={f.key}
            label={f.label}
            placeholder={f.placeholder}
            value={form.values[f.key] ?? ''}
            onChange={(e) => form.setValue(f.key, e.currentTarget.value)}
            style={{ flex: 1, minWidth: 180 }}
          />
        ),
      )}
      <Button variant="default" loading={form.testing} onClick={form.test}>
        <Trans>Test</Trans>
      </Button>
      {children}
    </Group>
  )
}
