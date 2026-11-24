import type { ReactNode } from 'react'
import { plural } from '@lingui/core/macro'
import { useLingui } from '@lingui/react/macro'
import { IconStack } from '../../lib/icons'
import { MetaList } from '../ui/MetaList'

/**
 * Bolds the counts inside an already translated sentence. The message stays one plain plural
 * sentence, so every language keeps its own word order and plural forms; only the digits are found
 * again afterwards, in the locale's own number format.
 */
function emphasize(text: string, counts: { value: string; volume?: boolean }[]): ReactNode[] {
  const out: ReactNode[] = []
  let rest = text
  for (const { value, volume } of counts) {
    const at = rest.indexOf(value)
    if (at < 0) continue
    if (at > 0) out.push(rest.slice(0, at))
    out.push(
      <b key={out.length} data-volume={volume || undefined}>
        {value}
      </b>,
    )
    rest = rest.slice(at + value.length)
  }
  if (rest) out.push(rest)
  return out
}

/**
 * How long a series is, as figures under the Discover modal's pills. The series page leaves it out:
 * its Progress card already carries the counts that matter once the series is yours. A finished
 * single chapter reads as a one-shot rather than a big "1". `extra` follows behind a hairline that
 * drops when it wraps (the reading time).
 */
export function HeroLength({
  chapters,
  volumes,
  completed,
  extra,
}: {
  chapters: number | null
  volumes: number | null
  completed: boolean
  extra?: ReactNode
}) {
  const { t, i18n } = useLingui()
  const chapterCount = chapters != null ? { value: i18n.number(chapters) } : null
  // Equal counts can't be told apart in the text, so neither takes the volume hue.
  const volumeCount = volumes != null ? { value: i18n.number(volumes), volume: volumes !== chapters } : null

  let figure: ReactNode = null
  if (completed && chapters === 1 && (volumes == null || volumes === 1)) {
    figure = t`One-shot`
  } else if (chapters != null && volumes != null) {
    figure = emphasize(
      t`${plural(chapters, { one: '# chapter', other: '# chapters' })} in ${plural(volumes, { one: '# volume', other: '# volumes' })}`,
      [chapterCount!, volumeCount!],
    )
  } else if (chapters != null) {
    figure = emphasize(plural(chapters, { one: '# chapter', other: '# chapters' }), [chapterCount!])
  } else if (volumes != null) {
    figure = emphasize(plural(volumes, { one: '# volume', other: '# volumes' }), [volumeCount!])
  }

  if (figure == null) return null
  return (
    <div className="hero-length">
      <MetaList
        items={[
          <span key="figure" className="hero-length-figure">
            <IconStack size={16} />
            <span>{figure}</span>
          </span>,
          extra,
        ]}
      />
    </div>
  )
}
