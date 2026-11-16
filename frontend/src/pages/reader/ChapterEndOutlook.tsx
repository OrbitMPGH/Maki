import { plural } from '@lingui/core/macro'
import { useLingui } from '@lingui/react/macro'
import { confidenceLevel, useSeriesOutlook } from '../../api/releases'
import { CONFIDENCE_LABEL } from '../../components/releases/outlookText'
import { useLabel } from '../../i18n-context'
import { releaseSpan } from './endScreen'

function unitText(count: number, unit: 'days' | 'weeks'): string {
  return unit === 'days'
    ? plural(count, { one: '# day', other: '# days' })
    : plural(count, { one: '# week', other: '# weeks' })
}

/** One quiet line for the caught-up screen: a span and how sure the estimate is, never a date. */
export default function ChapterEndOutlook({ seriesId }: { seriesId: number }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data } = useSeriesOutlook(seriesId)
  const span = data ? releaseSpan(data, Date.now()) : null
  if (!data || !span) return null

  let text: string
  if (span.kind === 'soon') {
    text = t`The next chapter is likely any day now`
  } else if (span.kind === 'late') {
    text = t`The next chapter is later than usual`
  } else if (span.kind === 'within') {
    const within = unitText(span.to, span.unit)
    text = t`The next chapter usually comes within ${within}`
  } else {
    const { from, to } = span
    const upTo = unitText(to, span.unit)
    const range = from === to ? upTo : t`${from} to ${upTo}`
    text = t`The next chapter usually comes in ${range}`
  }
  const sure = span.kind !== 'soon' && span.kind !== 'late'
  return (
    <span className="reader-end-note reader-end-outlook">
      {text}
      {sure && <> · {renderLabel(CONFIDENCE_LABEL[confidenceLevel(data.confidence)])}</>}
    </span>
  )
}
