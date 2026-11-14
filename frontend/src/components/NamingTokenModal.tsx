import { useMemo, useRef, useState } from 'react'
import { Button, Code, Modal, Select, Text, TextInput } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { useLingui as useLinguiReact } from '@lingui/react'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useNamingTokens, type NamingToken } from '../api/hooks'
import { IconPlus } from '../lib/icons'
import { ModalFooter, ModalHeading, ModalNote } from './ui/ModalShell'
import { MODAL_SIZE } from './ui/modalSizes'

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
  const optionalExample = '<({Chapter Group})>'
  const openMarker = '<'
  const closeMarker = '>'

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

  const tokenCount = categories.reduce((n, [, list]) => n + list.length, 0)

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size={MODAL_SIZE.wide}
      title={
        <ModalHeading
          title={t`Naming tokens`}
          description={<Trans>Build the file name from these tokens. Click one to insert it.</Trans>}
        />
      }
    >
      <div className="token-modal-controls">
        <TextInput
          aria-label={t`Naming format`}
          label={t`Naming format`}
          ref={input}
          value={format}
          spellCheck={false}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
          style={{ flex: '2 1 240px' }}
          onChange={(e) => onChange(e.currentTarget.value)}
        />
        <Select
          label={t`Token case`}
          data={caseOptions}
          value={textCase}
          onChange={(value) => setTextCase(value ?? 'default')}
          allowDeselect={false}
          style={{ flex: '1 1 140px' }}
          aria-label={t`Token case`}
        />
        <Select
          label={t`Token separator`}
          data={separatorOptions}
          value={separator}
          onChange={(value) => setSeparator(value ?? ' ')}
          allowDeselect={false}
          style={{ flex: '1 1 140px' }}
          aria-label={t`Token separator`}
        />
      </div>

      <ModalNote tone="info">
        <Trans>
          A token with no value for a given series renders as nothing, and the surrounding spaces and
          empty brackets are cleaned up, so {yearExampleToken} on a series with no year is just its
          title.
        </Trans>{' '}
        <Trans>
          Chapter number and volume also take zero-padding: <Code>{paddingExampleToken}</Code>{' '}
          renders 24 as 024.
        </Trans>{' '}
        <Trans>
          Text between {openMarker} and {closeMarker} is kept only when every token inside has a value:{' '}
          <Code>{optionalExample}</Code> leaves out the brackets too when a chapter has no group.
        </Trans>
      </ModalNote>

      {categories.map(([category, list]) => (
        <table key={category} className="token-table" style={{ marginTop: 12 }}>
          <colgroup>
            <col className="token-col-token" />
            <col />
            <col className="token-col-example" />
          </colgroup>
          <thead>
            <tr>
              <th scope="col">
                {category}
              </th>
              <th scope="col">
                <Trans>Inserts</Trans>
              </th>
              <th scope="col">
                <Trans>Example</Trans>
              </th>
            </tr>
          </thead>
          <tbody>
            {list.map((token) => {
              const spelling = respell(token.token, separator, textCase)
              return (
                <tr key={token.token} onClick={() => insert(spelling)} style={{ cursor: 'pointer' }}>
                  <td>
                    <button type="button" className="token-pick" onClick={(e) => { e.stopPropagation(); insert(spelling) }}>
                      {spelling}
                      <IconPlus size={12} aria-hidden />
                    </button>
                  </td>
                  <td>
                    <Text component="span" size="sm" c="var(--ink-3)">
                      {token.description}
                    </Text>
                  </td>
                  <td>
                    <span className="token-example" data-blank={token.example ? undefined : 'true'}>
                      {token.example || <Trans>(blank when unset)</Trans>}
                    </span>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      ))}

      <ModalFooter status={<Plural value={tokenCount} one="# token" other="# tokens" />}>
        <Button onClick={onClose}>
          <Trans>Done</Trans>
        </Button>
      </ModalFooter>
    </Modal>
  )
}
