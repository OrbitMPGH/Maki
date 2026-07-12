import { useEffect, useState } from 'react'
import { Button, Card, Group, PasswordInput, Text, TextInput, Title } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  useConnectionSettings,
  useSaveConnectionSettings,
  useTestConnectionSettings,
} from '../api/hooks'

interface Field {
  key: string
  label: string
  placeholder?: string
  secret?: boolean
}

/** Generic URL+credentials settings card with Test/Save, used for Prowlarr and qBittorrent. */
export function ConnectionSettingsCard({
  name,
  title,
  description,
  fields,
}: {
  name: 'prowlarr' | 'qbittorrent' | 'kavita'
  title: string
  description: string
  fields: Field[]
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

  const payload = () =>
    Object.fromEntries(fields.map((f) => [f.key, values[f.key] || null]))

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        {title}
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        {description}
      </Text>
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
              onSuccess: () => notifications.show({ message: `${title} is reachable`, color: 'green' }),
              onError: (err) => notifications.show({ message: String(err), color: 'red' }),
            })
          }
        >
          Test
        </Button>
        <Button
          loading={save.isPending}
          onClick={() =>
            save.mutate(payload(), {
              onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
              onError: (err) => notifications.show({ message: String(err), color: 'red' }),
            })
          }
        >
          Save
        </Button>
      </Group>
    </Card>
  )
}
