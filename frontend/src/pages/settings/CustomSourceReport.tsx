import { Stack, Text } from '@mantine/core'
import { Plural, useLingui } from '@lingui/react/macro'
import { ModalNote } from '../../components/ui/ModalShell'
import { IconCheck, IconLock, IconMinus, IconX } from '../../lib/icons'
import type { CustomSourceTestReport, CustomSourceTestStep } from '../../api/customSources'

function StepIcon({ status }: { status: CustomSourceTestStep['status'] }) {
  if (status === 'ok') return <IconCheck size={16} color="var(--ok)" />
  if (status === 'failed') return <IconX size={16} color="var(--danger)" />
  return <IconMinus size={16} color="var(--ink-4)" />
}

/** What the chapter list step read beyond its count: locked chapters and the groups credited. */
function ChapterExtras({ detail }: { detail: Record<string, unknown> }) {
  const { t } = useLingui()
  const locked = typeof detail.locked === 'number' ? detail.locked : 0
  const groups = Array.isArray(detail.groups) ? detail.groups.filter((g): g is string => typeof g === 'string') : []
  if (locked === 0 && groups.length === 0) return null
  const groupList = groups.join(', ')
  return (
    <div className="report-row-extra">
      {locked > 0 && (
        <span>
          <IconLock size={12} aria-hidden />{' '}
          <Plural value={locked} one="# chapter listed as locked" other="# chapters listed as locked" />
        </span>
      )}
      {groups.length > 0 && (
        <span>{t`Groups credited: ${groupList}`}</span>
      )}
    </div>
  )
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
    <li className="report-row">
      <StepIcon status={step.status} />
      <div style={{ minWidth: 0 }}>
        <div>
          <span className="report-row-name">{label}</span>
          {summary && <span className="report-row-summary">{summary}</span>}
        </div>
        {step.name === 'chapters' && step.detail && <ChapterExtras detail={step.detail} />}
        {step.error && <div className="report-row-error">{step.error}</div>}
      </div>
    </li>
  )
}

/** The test run's result for some of its steps, or every step when `only` is left out. */
export function CustomSourceReport({ report, only }: { report: CustomSourceTestReport; only?: CustomSourceTestStep['name'][] }) {
  const { t } = useLingui()
  const steps = only ? report.steps.filter((s) => only.includes(s.name)) : report.steps
  const passed = steps.every((s) => s.status !== 'failed')
  return (
    <Stack gap={4}>
      <ModalNote tone={passed ? 'ok' : 'warn'}>
        <Text span fw={600}>{passed ? t`Every check passed` : t`Some checks failed`}</Text>
      </ModalNote>
      <ul className="report-rows">{steps.map((step) => <StepRow key={step.name} step={step} />)}</ul>
    </Stack>
  )
}
