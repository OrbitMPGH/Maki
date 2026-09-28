import { useMemo, useRef, useState } from 'react'
import {
  Code,
  Divider,
  Group,
  Modal,
  Select,
  Stack,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useLingui as useLinguiReact } from '@lingui/react'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useNamingTokens, type NamingToken } from '../api/hooks'

/**
 * The separator and case pickers rewrite the token text itself rather than setting anything
 * server-side: the formatter reads a token's own spelling, so "{Series.Title}" is what produces
 * "The.Series.Title". That keeps the whole feature in the format string, where an admin can see it.
 *
 * Values are the wire spellings `respell` compares and rewrites, so they stay in English. Labels are
 * descriptors, not strings, because this table is built once when the module loads and a rendered
 * string would freeze at whatever language was active then; see `useSeparatorOptions`/`useCaseOptions`.
 */
const SEPARATORS: { value: string; label: MessageDescriptor }[] = [
  { value: ' ', label: msg`Space ( )` },
  { value: '.', label: msg`Period (.)` },
  { value: '_', label: msg`Underscore (_)` },
  { value: '-', label: msg`Dash (-)` },
]

const CASES: { value: string; label: MessageDescriptor }[] = [
  { value: 'default', label: msg`Default Case` },
  { value: 'lower', label: msg`Lower Case` },
  { value: 'upper', label: msg`Upper Case` },
]

function useSeparatorOptions() {
  const { _, i18n } = useLinguiReact()
  return useMemo(() => SEPARATORS.map((o) => ({ ...o, label: _(o.label) })), [_, i18n.locale])
}

function useCaseOptions() {
  const { _, i18n } = useLinguiReact()
  return useMemo(() => CASES.map((o) => ({ ...o, label: _(o.label) })), [_, i18n.locale])
}

function respell(token: string, separator: string, textCase: string) {
  const inner = token.slice(1, -1)
  const separated = separator === ' ' ? inner : inner.replace(/ /g, separator)
  const cased =
    textCase === 'lower'
      ? separated.toLowerCase()
      : textCase === 'upper'
        ? separated.toUpperCase()
        : separated
  return `{${cased}}`
}

function TokenRow({ token, spelling, onPick }: {
  token: NamingToken
  spelling: string
  onPick: (spelling: string) => void
}) {
  return (
    <UnstyledButton
      onClick={() => onPick(spelling)}
      title={token.description}
      style={{ display: 'block', width: '100%' }}
    >
      <Group
        gap={0}
        wrap="nowrap"
        style={{
          border: '1px solid var(--mantine-color-default-border)',
          borderRadius: 'var(--mantine-radius-sm)',
          overflow: 'hidden',
        }}
      >
        <Code
          style={{
            flex: '0 0 55%',
            padding: '8px 10px',
            background: 'var(--mantine-color-default-hover)',
            borderRadius: 0,
          }}
        >
          {spelling}
        </Code>
        <Text size="sm" px="sm" py={8} truncate style={{ flex: 1 }}>
          {token.example || (
            <Text span c="var(--ink-3)" size="sm">
              <Trans>(blank when unset)</Trans>
            </Text>
          )}
        </Text>
      </Group>
    </UnstyledButton>
  )
}

export function NamingTokenModal({
  opened,
  onClose,
  format,
  onChange,
}: {
  opened: boolean
  onClose: () => void
  /** The format being edited. Shown, and directly editable, inside the modal too — the field
   * outside is unusable while this is open, since it can't be seen or focused underneath it. */
  format: string
  onChange: (value: string) => void
}) {
  const { t } = useLingui()
  const { data: tokens } = useNamingTokens()
  const [separator, setSeparator] = useState(' ')
  const [textCase, setTextCase] = useState('default')
  const input = useRef<HTMLInputElement>(null)
  const separatorOptions = useSeparatorOptions()
  const caseOptions = useCaseOptions()
  // The literal spellings shown inline below; kept out of the translated sentences so the token
  // text itself is never mistaken for a placeholder or handed to a translator to rewrite.
  const yearExampleToken = '{Series TitleYear}'
  const paddingExampleToken = '{Chapter Number:000}'

  const insert = (token: string) => {
    const element = input.current
    const start = element?.selectionStart ?? format.length
    const end = element?.selectionEnd ?? format.length
    const next = format.slice(0, start) + token + format.slice(end)
    onChange(next)

    requestAnimationFrame(() => {
      element?.focus()
      element?.setSelectionRange(start + token.length, start + token.length)
    })
  }

  const categories = useMemo(() => {
    const groups = new Map<string, NamingToken[]>()
    for (const token of tokens ?? []) {
      groups.set(token.category, [...(groups.get(token.category) ?? []), token])
    }
    return [...groups.entries()]
  }, [tokens])

  return (
    <Modal opened={opened} onClose={onClose} title={t`Naming tokens`} size="xl">
      <Group justify="flex-end" gap="sm" mb="md">
        <Select
          data={separatorOptions}
          value={separator}
          onChange={(value) => setSeparator(value ?? ' ')}
          allowDeselect={false}
          w={180}
          aria-label={t`Token separator`}
        />
        <Select
          data={caseOptions}
          value={textCase}
          onChange={(value) => setTextCase(value ?? 'default')}
          allowDeselect={false}
          w={180}
          aria-label={t`Token case`}
        />
      </Group>

      <Text size="sm" c="var(--ink-3)" mb="md">
        <Trans>Click a token to insert it.</Trans>{' '}
        <Trans>
          A token with no value for a given series renders as nothing, and the surrounding spaces and
          empty brackets are cleaned up, so {yearExampleToken} on a series with no year is just its
          title.
        </Trans>
      </Text>

      <Stack gap="lg">
        {categories.map(([category, list]) => (
          <div key={category}>
            <Text fw={600} size="sm" mb={4}>
              {category}
            </Text>
            <Divider mb="xs" />
            <Stack gap={6}>
              {list.map((token) => (
                <TokenRow
                  key={token.token}
                  token={token}
                  spelling={respell(token.token, separator, textCase)}
                  onPick={insert}
                />
              ))}
            </Stack>
          </div>
        ))}
      </Stack>

      <Divider my="md" />
      <Text size="sm" c="var(--ink-3)" mb={4}>
        <Trans>
          Chapter number and volume also take zero-padding: <Code>{paddingExampleToken}</Code>{' '}
          renders 24 as 024.
        </Trans>
      </Text>
      <TextInput
        ref={input}
        value={format}
        spellCheck={false}
        onChange={(e) => onChange(e.currentTarget.value)}
      />
    </Modal>
  )
}
