import { useState } from 'react'
import { Button, Checkbox, Group, SegmentedControl, Stack, Text, Textarea } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { MAX_NOTE_LENGTH, type NoteVisibility } from '../../api/notes'

export interface ComposerValues {
  body: string
  /** Null when the note is not pinned to a page. */
  pageIndex: number | null
  visibility: NoteVisibility
}

/**
 * A text box for a new note, an edit, or a reply. Replies carry no page and no audience of their own
 * (they take the thread's), so that variant is just the box.
 */
export default function NoteComposer({
  variant = 'note',
  initial,
  pinPage,
  singleUser = false,
  incognito = false,
  hasDisplayName = true,
  loginName = null,
  pending = false,
  disabled = false,
  error = null,
  submitLabel,
  onSubmit,
  onCancel,
}: {
  variant?: 'note' | 'reply'
  /** An edit starts from the note's own values; a new note starts private and empty. */
  initial?: ComposerValues
  /**
   * The page the "Pin to page" box refers to, zero based. Null hides the box (the series page, the
   * end screen, a note that has no page to keep).
   */
  pinPage?: number | null
  singleUser?: boolean
  /** The series hides your reading, so a shared note undoes that. */
  incognito?: boolean
  hasDisplayName?: boolean
  /** The login name others see instead of a display name, when the instance shows them. */
  loginName?: string | null
  pending?: boolean
  /** Offline, or anything else that makes saving impossible right now. */
  disabled?: boolean
  error?: string | null
  submitLabel?: string
  onSubmit: (values: ComposerValues) => void | Promise<void>
  onCancel?: () => void
}) {
  const { t } = useLingui()
  const [body, setBody] = useState(initial?.body ?? '')
  const [pin, setPin] = useState(initial ? initial.pageIndex !== null : pinPage != null)
  const [visibility, setVisibility] = useState<NoteVisibility>(initial?.visibility ?? 'private')

  const trimmed = body.trim()
  const canSubmit = trimmed.length > 0 && body.length <= MAX_NOTE_LENGTH && !pending && !disabled
  const note = variant === 'note'
  const target = initial?.pageIndex ?? pinPage ?? null
  const pageIndex = pin ? target : null
  const pageNumber = (target ?? 0) + 1
  const showPin = note && target !== null
  const showAudience = note && !singleUser

  const submit = async () => {
    if (!canSubmit) return
    try {
      await onSubmit({ body: trimmed, pageIndex, visibility })
      if (!initial) setBody('')
    } catch {
      // The caller shows the failure; the text stays so nothing typed is lost.
    }
  }

  return (
    <Stack gap="xs" className="note-composer" data-dirty={body !== (initial?.body ?? '') || undefined}>
      <Textarea
        aria-label={variant === 'reply' ? t`Write a reply` : t`Write a note`}
        placeholder={variant === 'reply' ? t`Write a reply` : t`Write a note. Wrap spoilers in ||double bars||.`}
        value={body}
        onChange={(event) => setBody(event.currentTarget.value)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
            event.preventDefault()
            void submit()
          }
        }}
        autosize
        minRows={variant === 'reply' ? 1 : 2}
        maxRows={8}
        maxLength={MAX_NOTE_LENGTH}
        disabled={disabled}
        error={error}
      />
      {body.length > MAX_NOTE_LENGTH * 0.9 && (
        <Text size="xs" c="var(--ink-3)" className="tnum" ta="end">
          {body.length} / {MAX_NOTE_LENGTH}
        </Text>
      )}
      {showPin && (
        <Checkbox
          size="xs"
          checked={pin}
          disabled={disabled}
          onChange={(event) => setPin(event.currentTarget.checked)}
          label={<Trans>Pin to page {pageNumber}</Trans>}
        />
      )}
      {showAudience && (
        <div>
          <SegmentedControl
            size="xs"
            fullWidth
            aria-label={t`Who can see this note`}
            value={visibility}
            disabled={disabled}
            onChange={(value) => setVisibility(value as NoteVisibility)}
            data={[
              { label: t`Private`, value: 'private' },
              { label: t`Shared`, value: 'shared' },
            ]}
          />
          <Text size="xs" c="var(--ink-3)" mt={4}>
            {visibility === 'private' ? (
              <Trans>Only you</Trans>
            ) : (
              <Trans>Everyone who can see this series</Trans>
            )}
          </Text>
          {visibility === 'shared' && incognito && (
            <Text size="xs" c="var(--warn)" mt={4}>
              <Trans>This series is incognito. A shared note tells everyone you read this chapter.</Trans>
            </Text>
          )}
          {visibility === 'shared' && !hasDisplayName && (
            <Text size="xs" c="var(--ink-3)" mt={4}>
              {loginName ? (
                <Trans>Others will see your login name, {loginName}.</Trans>
              ) : (
                <Trans>Others will see you as "Another reader" until an admin gives your account a display name.</Trans>
              )}
            </Text>
          )}
        </div>
      )}
      <Group gap="xs" justify="flex-end">
        {onCancel && (
          <Button variant="default" size="xs" onClick={onCancel} disabled={pending}>
            <Trans>Cancel</Trans>
          </Button>
        )}
        <Button size="xs" onClick={() => void submit()} disabled={!canSubmit} loading={pending}>
          {submitLabel ?? (variant === 'reply' ? t`Reply` : t`Add note`)}
        </Button>
      </Group>
    </Stack>
  )
}
