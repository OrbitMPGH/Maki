import { useMemo, useRef, useState } from 'react'
import {
  Code,
  Divider,
  Group,
  Modal,
  ScrollArea,
  Select,
  Stack,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { useNamingTokens, type NamingToken } from '../api/hooks'

/**
 * The separator and case pickers rewrite the token text itself rather than setting anything
 * server-side: the formatter reads a token's own spelling, so "{Series.Title}" is what produces
 * "The.Series.Title". That keeps the whole feature in the format string, where an admin can see it.
 */
const SEPARATORS = [
  { value: ' ', label: 'Space ( )' },
  { value: '.', label: 'Period (.)' },
  { value: '_', label: 'Underscore (_)' },
  { value: '-', label: 'Dash (-)' },
]

const CASES = [
  { value: 'default', label: 'Default Case' },
  { value: 'lower', label: 'Lower Case' },
  { value: 'upper', label: 'Upper Case' },
]

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
          {token.example || <Text span c="dimmed" size="sm">(blank when unset)</Text>}
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
  const { data: tokens } = useNamingTokens()
  const [separator, setSeparator] = useState(' ')
  const [textCase, setTextCase] = useState('default')
  const input = useRef<HTMLInputElement>(null)

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
    <Modal opened={opened} onClose={onClose} title="Naming tokens" size="xl" scrollAreaComponent={ScrollArea.Autosize}>
      <Group justify="flex-end" gap="sm" mb="md">
        <Select
          data={SEPARATORS}
          value={separator}
          onChange={(value) => setSeparator(value ?? ' ')}
          allowDeselect={false}
          w={180}
          aria-label="Token separator"
        />
        <Select
          data={CASES}
          value={textCase}
          onChange={(value) => setTextCase(value ?? 'default')}
          allowDeselect={false}
          w={180}
          aria-label="Token case"
        />
      </Group>

      <Text size="sm" c="dimmed" mb="md">
        Click a token to insert it. A token with no value for a given series renders as nothing, and
        the surrounding spaces and empty brackets are cleaned up — so {'{Series TitleYear}'} on a
        series with no year is just its title.
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
      <Text size="sm" c="dimmed" mb={4}>
        Chapter number and volume also take zero-padding: <Code>{'{Chapter Number:000}'}</Code>{' '}
        renders 24 as 024.
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
