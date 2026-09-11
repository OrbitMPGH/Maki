import { useRef, useState } from 'react'
import { ActionIcon, Text, TextInput } from '@mantine/core'
import { IconHelp } from '@tabler/icons-react'
import { NamingTokenModal } from './NamingTokenModal'

/**
 * A naming format field with Sonarr's "?" token picker beside it. The value is controlled by the
 * caller, which is what saves it — this only edits the string and inserts tokens at the caret.
 */
export function NamingFormatInput({
  label,
  description,
  value,
  example,
  error,
  onChange,
  onCommit,
}: {
  label: string
  description?: string
  value: string
  /** Rendered result from the server preview, shown under the field. */
  example?: string
  error?: string
  onChange: (value: string) => void
  /** Called when the field loses focus, so a save happens once rather than per keystroke. */
  onCommit: () => void
}) {
  const [pickerOpen, setPickerOpen] = useState(false)
  const input = useRef<HTMLInputElement>(null)

  return (
    <>
      <TextInput
        ref={input}
        label={label}
        description={description}
        value={value}
        error={error}
        spellCheck={false}
        onChange={(e) => onChange(e.currentTarget.value)}
        onBlur={onCommit}
        // Mantine leaves a rightSection non-interactive unless told otherwise, which makes the
        // button look clickable and do nothing.
        rightSectionPointerEvents="all"
        rightSection={
          <ActionIcon
            variant="light"
            aria-label={`${label} tokens`}
            onClick={() => setPickerOpen(true)}
          >
            <IconHelp size={16} />
          </ActionIcon>
        }
      />
      {example && !error && (
        <Text size="sm" c="dimmed" mt={4}>
          Example: {example}
        </Text>
      )}

      <NamingTokenModal
        opened={pickerOpen}
        // Closing the picker is what commits tokens inserted through it: the field was already
        // blurred when the button was clicked, so nothing else would fire onCommit afterwards.
        onClose={() => {
          setPickerOpen(false)
          onCommit()
        }}
        format={value}
        onChange={onChange}
      />
    </>
  )
}
