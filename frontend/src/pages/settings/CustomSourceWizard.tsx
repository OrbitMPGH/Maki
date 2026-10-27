import { useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  Alert, Anchor, Button, Collapse, FileButton, Group, List, Modal, MultiSelect, NumberInput, Radio, Select, Stack,
  Stepper, Switch, Table, Text, TextInput, Textarea,
} from '@mantine/core'
import {
  IconAlertTriangle, IconCheck, IconChevronDown, IconChevronRight, IconFileImport, IconMinus, IconX,
} from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { errorText } from '../../api/errorText'
import { languageName } from '../../api/titles'
import { useSourceLanguages, type CustomSourceState } from '../../api/hooks'
import { CustomSourceUpdateDialog } from './CustomRepositoriesPanel'
import {
  compact, identityChangeOf, problemsOf, useProbeSearch, useProbeSite, useRevertCustomSource, useSaveCustomSource, useTestCustomSource,
  type CustomSourceDefinition, type CustomSourceTestReport, type CustomSourceTestStep, type IdentityReason,
  type ProbeSearchResult, type ProbeSiteResult, type Section,
} from '../../api/customSources'
import { COMMON_LANGUAGES } from './sourceLanguages'

export type WizardMode = 'setup' | 'import' | 'edit'

const STEPS = ['site', 'chapters', 'pages', 'search', 'details'] as const

/** Product names, never translated. */
const ENGINE_NAMES: Record<string, string> = {
  madara: 'Madara',
  mangathemesia: 'MangaThemesia',
  keyoapp: 'Keyoapp',
  heancms: 'HeanCMS',
}
const engineName = (engine: string) => ENGINE_NAMES[engine] ?? engine
const DETAILS = STEPS.indexOf('details')

function parseJson(text: string): { value: Section | null; invalid: boolean } {
  if (!text.trim()) return { value: null, invalid: false }
  try {
    const value: unknown = JSON.parse(text)
    return value && typeof value === 'object' && !Array.isArray(value)
      ? { value: value as Section, invalid: false }
      : { value: null, invalid: true }
  } catch {
    return { value: null, invalid: true }
  }
}

const pretty = (value: unknown) => JSON.stringify(compact(value ?? {}), null, 2)

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

/** The test run's result for some of its steps, or every step when `only` is left out. */
function Report({ report, only }: { report: CustomSourceTestReport; only?: CustomSourceTestStep['name'][] }) {
  const { t } = useLingui()
  const steps = only ? report.steps.filter((s) => only.includes(s.name)) : report.steps
  const passed = steps.every((s) => s.status !== 'failed')
  return (
    <Alert color={passed ? 'green' : 'yellow'} variant="light" title={passed ? t`Every check passed` : t`Some checks failed`}>
      <Stack gap={6}>{steps.map((step) => <StepRow key={step.name} step={step} />)}</Stack>
    </Alert>
  )
}

/** A section of the draft as JSON, applied when it parses. */
function Advanced({ value, onApply, label }: { value: unknown; onApply: (section: Section | null) => void; label: string }) {
  const { t } = useLingui()
  const [open, setOpen] = useState(false)
  const [text, setText] = useState(() => pretty(value))
  const external = pretty(value)
  useEffect(() => setText(external), [external])
  const { value: parsed, invalid } = parseJson(text)
  return (
    <Stack gap={6}>
      <Anchor component="button" type="button" size="sm" onClick={() => setOpen((o) => !o)} c="var(--ink-2)">
        <Group gap={4}>
          {open ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
          <Trans>Advanced</Trans>
        </Group>
      </Anchor>
      <Collapse expanded={open}>
        <Stack gap={6}>
          <Textarea
            value={text}
            onChange={(e) => setText(e.currentTarget.value)}
            autosize
            minRows={6}
            maxRows={18}
            spellCheck={false}
            aria-label={label}
            styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 12 } }}
            error={invalid ? t`This is not valid JSON.` : undefined}
          />
          <Group justify="flex-end">
            <Button size="xs" variant="default" disabled={invalid || text === external} onClick={() => onApply(parsed)}>
              <Trans>Apply</Trans>
            </Button>
          </Group>
        </Stack>
      </Collapse>
    </Stack>
  )
}

function ChapterTable({ rows, count }: { rows: { number: number | null; text: string | null; date: string | null; link: string | null }[]; count: number }) {
  const { i18n } = useLingui()
  const shown = rows.slice(0, 8)
  const rest = count - shown.length
  return (
    <Stack gap={4}>
      <Table fz="xs" verticalSpacing={4} withRowBorders={false} striped>
        <Table.Thead>
          <Table.Tr>
            <Table.Th><Trans>Number</Trans></Table.Th>
            <Table.Th><Trans>Text</Trans></Table.Th>
            <Table.Th><Trans>Date</Trans></Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {shown.map((row, i) => (
            <Table.Tr key={`${row.link ?? ''}-${i}`}>
              <Table.Td c={row.number === null ? 'var(--danger)' : undefined}>
                {row.number === null ? <Trans>none</Trans> : i18n.number(row.number)}
              </Table.Td>
              <Table.Td style={{ maxWidth: 320, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={row.link ?? undefined}>
                {row.text}
              </Table.Td>
              <Table.Td>{row.date ? i18n.date(new Date(row.date), { dateStyle: 'medium' }) : ''}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {rest > 0 && <Text size="xs" c="var(--ink-3)"><Trans>and {rest} more</Trans></Text>}
    </Stack>
  )
}

/**
 * Sets up a site as a custom source in five steps, or opens an imported or stored definition on
 * the last one. Every step shows what the draft reads off the live site; nothing is saved until
 * Save, which writes through the server on its own (not Manage sources' Save changes), because a
 * definition has to be tested and the registry reloaded before anything can use it.
 */
export function CustomSourceWizard({ opened, onClose, mode, editingId, initial, startUrl, onSaved, onEditExisting, state = null }: {
  opened: boolean
  onClose: () => void
  mode: WizardMode
  /** The id being edited, which stays fixed. */
  editingId: string | null
  /** A stored or imported definition; the setup flow starts without one. */
  initial: CustomSourceDefinition | null
  /** A series address to start from, as the series page's "Set up this site" passes. */
  startUrl?: string | null
  onSaved: (sourceName: string, created: boolean) => void
  /** Opens an existing custom source instead, when the pasted site already has one. */
  onEditExisting?: (id: string) => void
  /** The edited source as Manage sources lists it: where it came from, whether an update waits. */
  state?: CustomSourceState | null
}) {
  const { t } = useLingui()
  const [step, setStep] = useState(0)
  const [draft, setDraft] = useState<CustomSourceDefinition | null>(null)
  const [seriesUrl, setSeriesUrl] = useState('')
  const [chapterUrl, setChapterUrl] = useState('')
  const [site, setSite] = useState<ProbeSiteResult | null>(null)
  const [choice, setChoice] = useState(0)
  const [query, setQuery] = useState('')
  const [found, setFound] = useState<ProbeSearchResult | null>(null)
  const [searchChoice, setSearchChoice] = useState<'found' | 'none'>('found')
  const [jsonMode, setJsonMode] = useState(false)
  const [jsonText, setJsonText] = useState('')
  const [testedKey, setTestedKey] = useState<string | null>(null)
  const probe = useProbeSite()
  const probeSearch = useProbeSearch()
  const test = useTestCustomSource()
  const save = useSaveCustomSource()
  const { data: languageSettings } = useSourceLanguages(opened)
  const revert = useRevertCustomSource()
  const [updateOpen, setUpdateOpen] = useState(false)
  const origin = mode === 'edit' ? state?.origin ?? null : null
  const repositoryName = origin ? (origin.repositoryName ?? origin.repository) : ''

  useEffect(() => {
    if (!opened) return
    const start = mode === 'setup' ? null : (initial ?? {})
    setDraft(start)
    setStep(mode === 'setup' ? 0 : DETAILS)
    setSeriesUrl(startUrl ?? '')
    setChapterUrl('')
    setSite(null)
    setChoice(0)
    setQuery(start?.sample?.query ?? '')
    setFound(null)
    setSearchChoice('found')
    setJsonMode(mode === 'import')
    setJsonText(start && Object.keys(start).length > 0 ? pretty(start) : '')
    setTestedKey(null)
    probe.reset()
    probeSearch.reset()
    test.reset()
    save.reset()
    revert.reset()
    setUpdateOpen(false)
    // Reset only when the wizard opens on new content, not on every mutation identity change.
  }, [opened, mode, initial, startUrl])

  // A stored definition is checked against the site straight away, so a broken one shows which part broke.
  useEffect(() => {
    if (!opened || mode !== 'edit' || !initial) return
    const stored = compact(initial)
    setTestedKey(JSON.stringify(stored))
    test.mutate(stored)
  }, [opened, mode, initial])

  const definition = useMemo(() => (draft ? compact(draft) : null), [draft])
  const key = definition ? JSON.stringify(definition) : null
  const report = test.data && testedKey === key ? test.data : null
  const engine = typeof definition?.engine === 'string' ? definition.engine : null
  const idChanged = editingId !== null && definition?.id !== editingId
  const problems = problemsOf(test.error) ?? problemsOf(save.error)
  const identity = identityChangeOf(save.error)
  const otherError = !problems && !identity ? (test.error ?? save.error) : null
  const canSave = definition !== null && !idChanged && (mode === 'edit' || report?.passed === true)

  function update(patch: Partial<CustomSourceDefinition>) {
    setDraft((current) => ({ ...(current ?? {}), ...patch }))
  }

  function finishedElsewhere() {
    if (editingId) onSaved(`custom-${editingId}`, false)
    onClose()
  }

  function runRevert(confirm: boolean) {
    if (!editingId) return
    revert.mutate({ id: editingId, confirm }, { onSuccess: finishedElsewhere })
  }

  function runTest() {
    if (!definition) return
    setTestedKey(key)
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

  function readSite() {
    probe.mutate({ seriesUrl: seriesUrl.trim(), chapterUrl: chapterUrl.trim() || null }, {
      onSuccess: (result) => {
        setSite(result)
        setDraft(result.draft)
        setChoice(0)
        setQuery(result.draft.sample?.query ?? '')
        setFound(null)
      },
    })
  }

  function chooseChapters(index: number) {
    setChoice(index)
    const guess = site?.chapters[index]
    if (guess) update({ chapters: guess.chapters })
  }

  function findSearch() {
    if (!definition) return
    update({ sample: { ...definition.sample, query } })
    probeSearch.mutate({
      baseUrl: definition.baseUrl ?? '',
      seriesTemplate: definition.series?.url ?? '',
      seriesUrl: definition.sample?.seriesUrl ?? '',
      query,
    }, {
      onSuccess: (result) => {
        setFound(result)
        const usable = result.search !== null || result.catalogue !== null
        setSearchChoice(usable ? 'found' : 'none')
        update({ search: result.search, catalogue: result.catalogue })
      },
    })
  }

  function chooseSearch(value: 'found' | 'none') {
    setSearchChoice(value)
    update(value === 'found' && found ? { search: found.search, catalogue: found.catalogue } : { search: null, catalogue: null })
  }

  function applyJson(text: string) {
    setJsonText(text)
    const { value } = parseJson(text)
    if (value) setDraft(value as CustomSourceDefinition)
  }

  const enabledLanguages = languageSettings ? languageSettings.order.filter((l) => !languageSettings.disabled.includes(l)) : null
  const languageOff = definition?.language && enabledLanguages && !enabledLanguages.some((l) => l.toLowerCase() === definition.language?.toLowerCase())
  const languageOptions = [...new Set([...COMMON_LANGUAGES, ...(definition?.language ? [definition.language] : [])])]
    .map((code) => ({ value: code, label: `${languageName(code) ?? code} (${code})` }))

  const reasonLabels: Record<IdentityReason, string> = {
    language: t`The language changes.`,
    host: t`The site's address changes.`,
    chapters: t`The sample series lists different chapter numbers or ids.`,
    imageHosts: t`Page images come from different hosts.`,
    unverified: t`Maki could not list the sample series under both versions to compare them.`,
  }

  const engineLabel = engine ? engineName(engine) : ''
  const presetNote = (
    <Text size="sm" c="var(--ink-3)">
      <Trans>The {engineLabel} preset reads this part of the site. Nothing to set up here.</Trans>
    </Text>
  )

  const siteEngine = site?.engine ? engineName(site.engine.engine) : ''
  const stepBody: Record<(typeof STEPS)[number], ReactNode> = {
    site: mode !== 'setup' ? (
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-3)">
          <Trans>The test runs against this series and chapter. Replace them if the site took them down.</Trans>
        </Text>
        <TextInput
          label={t`Sample series page`}
          value={definition?.sample?.seriesUrl ?? ''}
          onChange={(e) => update({ sample: { ...definition?.sample, seriesUrl: e.currentTarget.value } })}
        />
        <TextInput
          label={t`Sample chapter page`}
          value={definition?.sample?.chapterUrl ?? ''}
          onChange={(e) => update({ sample: { ...definition?.sample, chapterUrl: e.currentTarget.value } })}
        />
      </Stack>
    ) : (
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-3)">
          <Trans>Paste a series page from the site, and a chapter page of that series. Maki reads both and guesses where the chapters and pages are.</Trans>
        </Text>
        <TextInput
          label={t`Series page`}
          placeholder="https://example.com/series/some-title/"
          value={seriesUrl}
          onChange={(e) => setSeriesUrl(e.currentTarget.value)}
        />
        <TextInput
          label={t`Chapter page`}
          description={t`Optional, but the guess is far better with one.`}
          placeholder="https://example.com/some-title-chapter-1/"
          value={chapterUrl}
          onChange={(e) => setChapterUrl(e.currentTarget.value)}
        />
        <Group justify="flex-end">
          <Button onClick={readSite} loading={probe.isPending} disabled={!seriesUrl.trim()}>
            <Trans>Read the site</Trans>
          </Button>
        </Group>
        {probe.error && <Alert color="red" variant="light">{errorText(probe.error)}</Alert>}
        {site && (
          <Stack gap="xs">
            <Alert color="green" variant="light" icon={<IconCheck size={16} />}>
              {site.viaFlareSolverr
                ? <Trans>Read {site.title ?? site.seriesId} through FlareSolverr. The source will use it too.</Trans>
                : <Trans>Read {site.title ?? site.seriesId} directly.</Trans>}
            </Alert>
            {site.chapterError && (
              <Alert color="yellow" variant="light"><Trans>The chapter page did not load: {site.chapterError}</Trans></Alert>
            )}
            {site.hostTaken && (
              <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />}>
                <Stack gap={6}>
                  <Text size="sm"><Trans>{site.hostTaken.displayName} already reads this site.</Trans></Text>
                  {site.hostTaken.custom && onEditExisting && (
                    <Group>
                      <Button size="xs" variant="default" onClick={() => onEditExisting(site.hostTaken!.custom!)}>
                        <Trans>Edit {site.hostTaken.displayName}</Trans>
                      </Button>
                    </Group>
                  )}
                </Stack>
              </Alert>
            )}
            {site.engine && (
              <Alert color="blue" variant="light">
                <Trans>This site runs {siteEngine}, which Maki already knows how to read. The next steps are filled in.</Trans>
              </Alert>
            )}
          </Stack>
        )}
      </Stack>
    ),
    chapters: engine ? presetNote : (
      <Stack gap="sm">
        {site && site.chapters.length === 0 && (
          <Alert color="yellow" variant="light">
            <Trans>Maki could not find a chapter list on the series page. Describe it under Advanced.</Trans>
          </Alert>
        )}
        {site && site.chapters.length > 1 && (
          <Radio.Group value={String(choice)} onChange={(v) => chooseChapters(Number(v))} label={t`Which list holds this series' chapters?`}>
            <Stack gap={4} mt={4}>
              {site.chapters.map((guess, i) => (
                <Radio key={i} value={String(i)} label={t`${guess.count} links, starting with ${guess.rows[0]?.text ?? guess.rows[0]?.link ?? ''}`} />
              ))}
            </Stack>
          </Radio.Group>
        )}
        {site?.chapters[choice] && !report && (
          <ChapterTable rows={site.chapters[choice].rows} count={site.chapters[choice].count} />
        )}
        {report && <Report report={report} only={['series', 'chapters']} />}
        <Advanced
          label={t`Chapters section`}
          value={definition?.chapters}
          onApply={(section) => update({ chapters: section ?? {} })}
        />
        <Group justify="flex-end">
          <Button size="xs" variant="default" onClick={runTest} loading={test.isPending} disabled={!definition}>
            <Trans>Check against the site</Trans>
          </Button>
        </Group>
      </Stack>
    ),
    pages: engine ? presetNote : (
      <Stack gap="sm">
        {site && !site.pages && (
          <Alert color="yellow" variant="light">
            {chapterUrl.trim()
              ? <Trans>Maki could not find the page images on the chapter page. Describe them under Advanced.</Trans>
              : <Trans>Without a chapter page Maki can't guess where the images are. Go back and paste one, or describe them under Advanced.</Trans>}
          </Alert>
        )}
        {site?.pages && !report && (
          <Stack gap="xs">
            <Text size="sm"><Trans>{site.pages.count} pages on the sample chapter.</Trans></Text>
            <Group gap="xs">
              {site.pages.preview.map((url) => (
                <img
                  key={url}
                  src={url}
                  alt=""
                  referrerPolicy="no-referrer"
                  loading="lazy"
                  style={{ width: 72, height: 104, objectFit: 'cover', borderRadius: 4, background: 'var(--surface-2)' }}
                  onError={(e) => { e.currentTarget.style.visibility = 'hidden' }}
                />
              ))}
            </Group>
            {site.pages.image.ok
              ? <Text size="xs" c="var(--ok)"><Trans>Maki loaded the first page ({site.pages.image.format}).</Trans></Text>
              : <Alert color="red" variant="light"><Trans>The first page image did not load: {site.pages.image.error ?? t`not an image`}</Trans></Alert>}
          </Stack>
        )}
        {report && <Report report={report} only={['pages', 'image']} />}
        <Advanced label={t`Pages section`} value={definition?.pages} onApply={(section) => update({ pages: section ?? {} })} />
        <Group justify="flex-end">
          <Button size="xs" variant="default" onClick={runTest} loading={test.isPending} disabled={!definition}>
            <Trans>Check against the site</Trans>
          </Button>
        </Group>
      </Stack>
    ),
    search: engine ? presetNote : (
      <Stack gap="sm">
        <Group align="flex-end" gap="xs" wrap="nowrap">
          <TextInput
            style={{ flex: 1 }}
            label={t`Search for`}
            description={t`The sample series' title. Maki looks for a search that finds it.`}
            value={query}
            onChange={(e) => setQuery(e.currentTarget.value)}
          />
          <Button onClick={findSearch} loading={probeSearch.isPending} disabled={!query.trim() || !definition}>
            <Trans>Find search</Trans>
          </Button>
        </Group>
        {probeSearch.error && <Alert color="red" variant="light">{errorText(probeSearch.error)}</Alert>}
        {found && (
          <Radio.Group value={searchChoice} onChange={(v) => chooseSearch(v as 'found' | 'none')}>
            <Stack gap="xs">
              {(found.search || found.catalogue) && (
                <Radio
                  value="found"
                  label={found.search
                    ? t`Use the site's search (${found.results} results for this title)`
                    : t`Use the series list page (${found.results} series), matched by title`}
                  description={found.titles.slice(0, 3).join(', ')}
                />
              )}
              <Radio
                value="none"
                label={t`No search`}
                description={t`Maki won't find series on this site by itself. You'll link each series by pasting its address on the series page.`}
              />
            </Stack>
          </Radio.Group>
        )}
        {found && !found.search && !found.catalogue && (
          <Alert color="yellow" variant="light" title={t`No search found`}>
            <List size="xs" spacing={2}>
              {found.tried.map((a) => <List.Item key={a.url}>{a.url}{a.error ? `: ${a.error}` : ''}</List.Item>)}
            </List>
          </Alert>
        )}
        <Advanced
          label={t`Search section`}
          value={definition?.catalogue ? { catalogue: definition.catalogue } : { search: definition?.search ?? null }}
          onApply={(section) => update({ search: (section?.search as Section) ?? null, catalogue: (section?.catalogue as Section) ?? null })}
        />
      </Stack>
    ),
    details: (
      <Stack gap="sm">
        {origin?.updateAvailable && (
          <Alert color="blue" variant="light">
            <Group justify="space-between" wrap="nowrap">
              <Text size="sm"><Trans>Version {origin.latestVersion} is available from {repositoryName}.</Trans></Text>
              <Button size="xs" variant="default" onClick={() => setUpdateOpen(true)}><Trans>Review update</Trans></Button>
            </Group>
          </Alert>
        )}
        {origin && !state?.detached && (
          <Text size="xs" c="var(--ink-3)">
            <Trans>Installed from {repositoryName}, version {origin.version}. Saving changes here detaches it, and its updates stop applying.</Trans>
          </Text>
        )}
        {origin && state?.detached && (
          <Text size="xs" c="var(--ink-3)">
            <Trans>Edited locally, so updates from {repositoryName} no longer apply. Replace it from the repository panel to follow it again.</Trans>
          </Text>
        )}
        {mode === 'edit' && state?.canRevert && (
          <Group gap="xs">
            <Button size="xs" variant="default" loading={revert.isPending && !identityChangeOf(revert.error)} onClick={() => runRevert(false)}>
              <Trans>Revert to the previous version</Trans>
            </Button>
            {revert.error && !identityChangeOf(revert.error) && <Text size="xs" c="var(--danger)">{errorText(revert.error)}</Text>}
          </Group>
        )}
        {identityChangeOf(revert.error) && (
          <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} title={identityChangeOf(revert.error)!.error}>
            <List size="sm" spacing={2}>
              {identityChangeOf(revert.error)!.reasons.map((r) => <List.Item key={r}>{reasonLabels[r] ?? r}</List.Item>)}
            </List>
            <Group justify="flex-end" mt="sm">
              <Button size="xs" color="var(--warn)" loading={revert.isPending} onClick={() => runRevert(true)}>
                <Trans>Revert anyway</Trans>
              </Button>
            </Group>
          </Alert>
        )}
        {jsonMode ? (
          <Stack gap="xs">
            <Group justify="space-between">
              <Text size="sm" c="var(--ink-3)">
                <Trans>Paste a definition or open a .json file. A definition only describes where Maki reads the site; nothing in it runs.</Trans>
              </Text>
              <FileButton onChange={(file) => void file?.text().then(applyJson)} accept="application/json,.json">
                {(props) => (
                  <Button {...props} variant="default" size="xs" leftSection={<IconFileImport size={14} />}>
                    <Trans>Open file</Trans>
                  </Button>
                )}
              </FileButton>
            </Group>
            <Textarea
              value={jsonText}
              onChange={(e) => applyJson(e.currentTarget.value)}
              autosize
              minRows={14}
              maxRows={24}
              spellCheck={false}
              aria-label={t`Definition`}
              styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 12 } }}
              error={parseJson(jsonText).invalid ? t`This is not valid JSON.` : undefined}
            />
          </Stack>
        ) : (
          <Stack gap="xs">
            <Group grow align="flex-start">
              <TextInput label={t`Name`} value={definition?.name ?? ''} onChange={(e) => update({ name: e.currentTarget.value })} />
              <TextInput
                label={t`Id`}
                description={editingId ? t`Fixed once saved.` : t`Lowercase letters, digits and dashes.`}
                value={definition?.id ?? ''}
                disabled={editingId !== null}
                onChange={(e) => update({ id: e.currentTarget.value })}
              />
            </Group>
            <Group grow align="flex-start">
              <Select
                label={t`Language`}
                data={languageOptions}
                value={definition?.language ?? 'en'}
                onChange={(v) => v && update({ language: v })}
                searchable
                allowDeselect={false}
              />
              <Select
                label={t`Kind`}
                data={[
                  { value: 'scanlator', label: t`Scanlation group` },
                  { value: 'aggregator', label: t`Aggregator` },
                  { value: 'official', label: t`Official publisher` },
                ]}
                value={definition?.kind ?? 'scanlator'}
                onChange={(v) => v && update({ kind: v })}
                allowDeselect={false}
              />
            </Group>
            {languageOff && (
              <Text size="xs" c="var(--warn)">
                <Trans>{languageName(definition?.language) ?? definition?.language} is switched off under source languages, so auto-match won't pick this source until it is on.</Trans>
              </Text>
            )}
            <Group grow align="flex-start">
              <MultiSelect
                label={t`Content`}
                data={[
                  { value: 'manga', label: t`Manga` },
                  { value: 'manhwa', label: t`Manhwa` },
                  { value: 'manhua', label: t`Manhua` },
                  { value: 'webtoon', label: t`Webtoon` },
                  { value: 'doujinshi', label: t`Doujinshi` },
                ]}
                value={definition?.content ?? ['manga']}
                onChange={(v) => update({ content: v })}
              />
              <Select
                label={t`Rating`}
                data={[
                  { value: 'general', label: t`General` },
                  { value: 'mature', label: t`Mature` },
                  { value: 'adult', label: t`Adult` },
                ]}
                value={definition?.rating ?? 'general'}
                onChange={(v) => v && update({ rating: v })}
                allowDeselect={false}
              />
            </Group>
            <Group grow align="flex-end">
              <NumberInput
                label={t`Time between requests (ms)`}
                min={250}
                max={60000}
                step={250}
                value={definition?.minIntervalMs ?? 1000}
                onChange={(v) => typeof v === 'number' && update({ minIntervalMs: v })}
              />
              <Switch
                label={t`Needs FlareSolverr`}
                checked={definition?.flareSolverr ?? false}
                onChange={(e) => update({ flareSolverr: e.currentTarget.checked })}
                mb={8}
              />
            </Group>
          </Stack>
        )}
        <Group justify="flex-end">
          <Anchor
            component="button"
            type="button"
            size="xs"
            onClick={() => {
              if (!jsonMode) setJsonText(pretty(draft))
              setJsonMode((m) => !m)
            }}
          >
            {jsonMode ? <Trans>Edit as a form</Trans> : <Trans>Edit as JSON</Trans>}
          </Anchor>
        </Group>
        {idChanged && (
          <Alert color="red" variant="light">
            <Trans>The id of a saved source can't change. Import it as a new source instead.</Trans>
          </Alert>
        )}
        {problems && (
          <Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} title={t`The definition has problems`}>
            <List size="sm" spacing={2}>
              {problems.map((p, i) => <List.Item key={`${p.code}-${i}`}>{p.error}</List.Item>)}
            </List>
          </Alert>
        )}
        {otherError && <Alert color="red" variant="light">{errorText(otherError)}</Alert>}
        {report && <Report report={report} />}
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
      </Stack>
    ),
  }

  const stepLabels: Record<(typeof STEPS)[number], string> = {
    site: t`Site`,
    chapters: t`Chapters`,
    pages: t`Pages`,
    search: t`Search`,
    details: t`Details and test`,
  }
  // Without a draft there is nothing past the first step yet; an engine fills the middle three.
  const reachable = (index: number) => index === 0 || (definition !== null && (!engine || index === DETAILS || index === 0))
  const nextStep = () => setStep((s) => (engine && s === 0 ? DETAILS : Math.min(DETAILS, s + 1)))
  const previousStep = () => setStep((s) => (engine && s === DETAILS ? 0 : Math.max(0, s - 1)))

  const title = mode === 'edit' ? <Trans>Edit custom source</Trans> : mode === 'import' ? <Trans>Import a custom source</Trans> : <Trans>Set up a site</Trans>

  return (
    <Modal opened={opened} onClose={onClose} size={760} closeButtonProps={{ 'aria-label': t`Close` }} title={<Text fw={600}>{title}</Text>}>
      <Stack gap="md">
        <Stepper active={step} onStepClick={(i) => reachable(i) && setStep(i)} size="xs" allowNextStepsSelect={mode !== 'setup'}>
          {STEPS.map((name, i) => (
            <Stepper.Step key={name} label={stepLabels[name]} allowStepSelect={reachable(i)} />
          ))}
        </Stepper>
        {stepBody[STEPS[step]]}
        <Group justify="space-between">
          <Group gap="xs">
            {step > 0 && (
              <Button variant="default" onClick={previousStep}><Trans>Back</Trans></Button>
            )}
          </Group>
          <Group gap="xs">
            <Button variant="default" onClick={onClose}><Trans>Cancel</Trans></Button>
            {step < DETAILS ? (
              <Button onClick={nextStep} disabled={!definition || (mode === 'setup' && step === 0 && !site)}>
                <Trans>Next</Trans>
              </Button>
            ) : (
              <>
                <Button variant="default" onClick={runTest} disabled={!definition} loading={test.isPending}>
                  <Trans>Test</Trans>
                </Button>
                <Button onClick={() => runSave(false)} disabled={!canSave || identity !== null} loading={save.isPending && !identity}>
                  {mode === 'edit' && report && !report.passed ? <Trans>Save anyway</Trans> : <Trans>Save</Trans>}
                </Button>
              </>
            )}
          </Group>
        </Group>
        <CustomSourceUpdateDialog
          id={updateOpen ? editingId : null}
          opened={updateOpen}
          onClose={() => setUpdateOpen(false)}
          onApplied={finishedElsewhere}
        />
        {step === DETAILS && mode !== 'edit' && definition && !report?.passed && (
          <Text size="xs" c="var(--ink-4)" ta="right">
            <Trans>Saving a new source needs a test that passes.</Trans>
          </Text>
        )}
      </Stack>
    </Modal>
  )
}
