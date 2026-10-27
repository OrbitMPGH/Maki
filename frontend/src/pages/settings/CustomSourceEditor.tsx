import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, FileButton, Group, List, Modal, Stack, Text, Textarea } from '@mantine/core'
import { IconAlertTriangle, IconCheck, IconFileImport, IconMinus, IconX } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { errorText } from '../../api/errorText'
import {
  identityChangeOf, problemsOf, useSaveCustomSource, useTestCustomSource,
  type CustomSourceDefinition, type CustomSourceTestStep, type IdentityReason,
} from '../../api/customSources'

function parse(text: string): { definition: CustomSourceDefinition | null; invalid: boolean } {
  if (!text.trim()) return { definition: null, invalid: false }
  try {
    const value: unknown = JSON.parse(text)
    return value && typeof value === 'object' && !Array.isArray(value)
      ? { definition: value as CustomSourceDefinition, invalid: false }
      : { definition: null, invalid: true }
  } catch {
    return { definition: null, invalid: true }
  }
}

function StepIcon({ status }: { status: CustomSourceTestStep['status'] }) {
  if (status === 'ok') return <IconCheck size={14} color="var(--ok)" />
  if (status === 'failed') return <IconX size={14} color="var(--danger)" />
  return <IconMinus size={14} color="var(--ink-4)" />
}

function StepRow({ step }: { step: CustomSourceTestStep }) {
  const { t } = useLingui()
  const label = {
    search: t`Search`,
    series: t`Series page`,
    chapters: t`Chapter list`,
    pages: t`Chapter pages`,
    image: t`First page image`,
  }[step.name]
  const count = typeof step.detail?.count === 'number' ? step.detail.count : null
  const bytes = typeof step.detail?.bytes === 'number' ? step.detail.bytes : null
  const kb = bytes === null ? null : Math.round(bytes / 1024)
  let summary: string | null = null
  if (step.status === 'skipped') summary = t`Skipped`
  else if (step.name === 'search' && count !== null) summary = t`${count} results`
  else if (step.name === 'chapters' && count !== null) summary = t`${count} chapters`
  else if (step.name === 'pages' && count !== null) summary = t`${count} pages`
  else if (step.name === 'image' && kb !== null) summary = t`${kb} KB`
  else if (step.name === 'series' && typeof step.detail?.title === 'string') summary = step.detail.title
  return (
    <Group gap={8} wrap="nowrap" align="flex-start">
      <span style={{ paddingTop: 3 }}><StepIcon status={step.status} /></span>
      <div style={{ minWidth: 0 }}>
        <Text size="sm" fw={600}>
          {label}
          {summary && <Text span size="sm" c="var(--ink-3)" fw={400}>{'  '}{summary}</Text>}
        </Text>
        {step.error && <Text size="xs" c="var(--danger)">{step.error}</Text>}
      </div>
    </Group>
  )
}

/**
 * Pasted or opened JSON for one custom source, tested against the live site before it is saved.
 * Opens stacked over Manage sources and saves on its own: a definition has to be checked against
 * the site and the registry reloaded before anything can use it.
 */
export function CustomSourceEditor({ opened, onClose, editingId, initialText, onSaved }: {
  opened: boolean
  onClose: () => void
  /** The id being edited, which stays fixed; null to add a new source. */
  editingId: string | null
  initialText: string
  onSaved: (sourceName: string, created: boolean) => void
}) {
  const { t } = useLingui()
  const [text, setText] = useState(initialText)
  const [testedText, setTestedText] = useState<string | null>(null)
  const test = useTestCustomSource()
  const save = useSaveCustomSource()

  useEffect(() => {
    if (!opened) return
    setText(initialText)
    setTestedText(null)
    test.reset()
    save.reset()
    // Reset only when the editor opens on new content, not on every mutation identity change.
  }, [opened, initialText])

  const { definition, invalid } = useMemo(() => parse(text), [text])
  const idChanged = editingId !== null && definition !== null && definition.id !== editingId
  const report = test.data && testedText === text ? test.data : null
  const problems = problemsOf(test.error) ?? problemsOf(save.error)
  const identity = identityChangeOf(save.error)
  const otherError = !problems && !identity ? (test.error ?? save.error) : null
  // A new source has to pass against the site first; an edit can save regardless, so a source
  // can be renamed or partly fixed while its site is down.
  const canSave = definition !== null && !idChanged && (editingId !== null || report?.passed === true)

  const reasonLabels: Record<IdentityReason, string> = {
    language: t`The language changes.`,
    host: t`The site's address changes.`,
    chapters: t`The sample series lists different chapter numbers or ids.`,
    imageHosts: t`Page images come from different hosts.`,
    unverified: t`Maki could not list the sample series under both versions to compare them.`,
  }

  function runTest() {
    if (!definition) return
    setTestedText(text)
    save.reset()
    test.mutate(definition)
  }

  function runSave(confirm: boolean) {
    if (!definition) return
    save.mutate({ definition, confirm }, {
      onSuccess: (result) => {
        onSaved(result.sourceName, result.created)
        onClose()
      },
    })
  }

  async function openFile(file: File | null) {
    if (!file) return
    setText(await file.text())
  }

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size={720}
      closeButtonProps={{ 'aria-label': t`Close` }}
      title={<Text fw={600}>{editingId ? <Trans>Edit custom source</Trans> : <Trans>Import a custom source</Trans>}</Text>}
    >
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            Paste a definition or open a .json file. Test it against the site, then save. A definition
            only describes where Maki reads the site; nothing in it runs.
          </Trans>
        </Text>
        <Group justify="flex-end">
          <FileButton onChange={(file) => void openFile(file)} accept="application/json,.json">
            {(props) => (
              <Button {...props} variant="default" size="xs" leftSection={<IconFileImport size={14} />}>
                <Trans>Open file</Trans>
              </Button>
            )}
          </FileButton>
        </Group>
        <Textarea
          value={text}
          onChange={(e) => setText(e.currentTarget.value)}
          autosize
          minRows={14}
          maxRows={24}
          spellCheck={false}
          aria-label={t`Definition`}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 12 } }}
          error={
            invalid ? t`This is not valid JSON.`
              : idChanged ? t`The id of a saved source can't change. Import it as a new source instead.`
                : undefined
          }
        />

        {problems && (
          <Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} title={t`The definition has problems`}>
            <List size="sm" spacing={2}>
              {problems.map((p, i) => <List.Item key={`${p.code}-${i}`}>{p.error}</List.Item>)}
            </List>
          </Alert>
        )}
        {otherError && <Alert color="red" variant="light">{errorText(otherError)}</Alert>}

        {report && (
          <Alert
            color={report.passed ? 'green' : 'yellow'}
            variant="light"
            title={report.passed ? t`Every check passed` : t`Some checks failed`}
          >
            <Stack gap={6}>
              {report.steps.map((step) => <StepRow key={step.name} step={step} />)}
            </Stack>
          </Alert>
        )}

        {identity && (
          <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} title={identity.error}>
            <List size="sm" spacing={2}>
              {identity.reasons.map((r) => <List.Item key={r}>{reasonLabels[r] ?? r}</List.Item>)}
            </List>
            <Group justify="flex-end" mt="sm">
              <Button size="xs" color="var(--warn)" loading={save.isPending} onClick={() => runSave(true)}>
                <Trans>Save anyway</Trans>
              </Button>
            </Group>
          </Alert>
        )}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button variant="default" onClick={runTest} disabled={!definition} loading={test.isPending}>
            <Trans>Test</Trans>
          </Button>
          <Button onClick={() => runSave(false)} disabled={!canSave || identity !== null} loading={save.isPending && !identity}>
            <Trans>Save</Trans>
          </Button>
        </Group>
        {!editingId && definition && !report?.passed && (
          <Text size="xs" c="var(--ink-4)" ta="right">
            <Trans>Saving a new source needs a test that passes.</Trans>
          </Text>
        )}
      </Stack>
    </Modal>
  )
}
