import type { ReactNode } from 'react'
import { Button, Group, PasswordInput, Select, Stack, Text, TextInput } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import type { IndexerBackend } from '../../api/hooks'
import { SaveButton } from './SaveButton'
import { INDEXER_BACKENDS, useIndexerForm, type IndexerForm as IndexerFormState } from './useIndexerForm'

export function IndexerFieldRow({ form, children }: { form: IndexerFormState; children?: ReactNode }) {
  const { t } = useLingui()
  const placeholder = INDEXER_BACKENDS.find((b) => b.value === form.selected)?.placeholder
  return (
    <Stack gap="xs">
      <Group align="flex-end" wrap="wrap">
        <Select
          label={t`Indexer`}
          data={INDEXER_BACKENDS.map(({ value, label }) => ({ value, label }))}
          value={form.selected ?? null}
          allowDeselect={false}
          onChange={(v) => v && form.choose(v as IndexerBackend)}
          style={{ flex: 1, minWidth: 180 }}
        />
        <TextInput
          autoComplete="off"
          label={t`URL`}
          placeholder={placeholder}
          value={form.url}
          disabled={!form.loaded}
          onChange={(e) => form.setUrl(e.currentTarget.value)}
          style={{ flex: 1, minWidth: 180 }}
        />
        <PasswordInput
          autoComplete="new-password"
          label={t`API key`}
          value={form.apiKey}
          disabled={!form.loaded}
          onChange={(e) => form.setApiKey(e.currentTarget.value)}
          style={{ flex: 1, minWidth: 180 }}
        />
        <Button variant="default" loading={form.testing} disabled={!form.loaded} onClick={form.test}>
          <Trans>Test</Trans>
        </Button>
        {children}
      </Group>
      <Text size="sm" c="var(--ink-3)">
        <Trans>
          Only one indexer is used at a time. Switching makes release suggestions you dismissed
          earlier stop matching, because Prowlarr and Jackett number releases differently.
        </Trans>
      </Text>
    </Stack>
  )
}

/** The fields and Save on their own, for the setup guide. */
export function IndexerForm() {
  const form = useIndexerForm()
  return (
    <IndexerFieldRow form={form}>
      <SaveButton dirty={form.dirty} loading={form.saving} onClick={form.save} />
    </IndexerFieldRow>
  )
}
