import { Link } from 'react-router-dom'
import { Button, Text } from '@mantine/core'
import { useElementSize, useMediaQuery } from '@mantine/hooks'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { IconBook, IconCircleCheck, IconClockPause } from '../../../lib/icons'
import type { TasteShelfPick } from '../../../api/hooks'
import { Panel } from '../../../components/ui/Panel'
import { formatNumber } from '../../../format'

/** The library card's poster, scrim and title, as the theme stage draws its own series. */
function ShelfCover({ pick }: { pick: TasteShelfPick }) {
  return (
    <Link to={`/series/${pick.seriesId}`} className="cover-card">
      <div className="cover-poster">
        {pick.coverUrl ? (
          <img src={pick.coverUrl} alt="" loading="lazy" decoding="async" />
        ) : (
          <div className="cover-placeholder" aria-hidden>
            {pick.title}
          </div>
        )}
        <div className="cover-scrim" />
        <div className="cover-meta">
          <span className="cover-title">{pick.title}</span>
        </div>
      </div>
    </Link>
  )
}

/** How close a pick sits to what the reader reads. A bar, because the number is not a percentage. */
function FitBar({ fit }: { fit: number }) {
  const { t } = useLingui()
  return (
    <span className="taste-fit" role="img" aria-label={t`How well it fits what you read`}>
      <b style={{ width: `${Math.max(6, Math.round(fit * 100))}%` }} />
    </span>
  )
}

function Reason({ pick }: { pick: TasteShelfPick }) {
  const { reason, reasonCount } = pick
  if (reason) {
    return plural(reasonCount, {
      one: `${reason}, like # series you've read.`,
      other: `${reason}, like # series you've read.`,
    })
  }
  if (pick.fromRecommendation) return <Trans>You added it from a recommendation.</Trans>
  return <Trans>Close to what you read.</Trans>
}

function State({ pick }: { pick: TasteShelfPick }) {
  if (pick.onHold) {
    const at = pick.resumeAt === null ? null : formatNumber(pick.resumeAt)
    return (
      <span className="taste-shelf-state" data-tone="waiting">
        <IconClockPause size={14} aria-hidden />
        {at === null ? <Trans>On hold</Trans> : <Trans>On hold at ch. {at}</Trans>}
      </span>
    )
  }
  if (!pick.ready) return null
  return (
    <span className="taste-shelf-state" data-tone="ready">
      <IconCircleCheck size={14} aria-hidden />
      <Trans>Ready to read</Trans>
    </span>
  )
}

/** Narrowest a pick beside the lead may get before the row holds one fewer. */
const PICK_MIN = 150
const PICK_GAP = 16

/**
 * The unread and paused series the reader already owns that sit closest to what they read: one to
 * lead with and a start button, and as many beside it as the row holds, each with the reason and
 * where it stands.
 */

export function TasteShelf({ picks }: { picks: TasteShelfPick[] }) {
  const [lead, ...rest] = picks
  const { ref, width } = useElementSize()
  // A phone scrolls the row sideways, so it keeps every pick; wider screens fill one row exactly.
  const phone = useMediaQuery('(max-width: 48em)', false, { getInitialValueInEffect: false })
  const columns = width > 0 ? Math.max(1, Math.floor((width + PICK_GAP) / (PICK_MIN + PICK_GAP))) : 5
  const shown = phone ? rest : rest.slice(0, columns)
  if (!lead) return null
  const startTo = lead.nextChapterId ? `/read/${lead.nextChapterId}` : `/series/${lead.seriesId}`

  return (
    <Panel className="taste-shelf">
      <div className="taste-shelf-lead">
        <div className="taste-shelf-lead-cover">
          <ShelfCover pick={lead} />
        </div>
        <div className="taste-shelf-lead-text">
          <h3 className="taste-shelf-lead-title">{lead.title}</h3>
          <FitBar fit={lead.fit} />
          <Text className="taste-shelf-reason">
            <Reason pick={lead} />
          </Text>
          <State pick={lead} />
          <Button component={Link} to={startTo} leftSection={<IconBook size={16} />} mt="xs">
            {lead.onHold ? <Trans>Pick it back up</Trans> : <Trans>Start reading</Trans>}
          </Button>
        </div>
      </div>
      {rest.length > 0 && (
        <div
          ref={ref}
          className="taste-shelf-rest"
          style={phone ? undefined : { gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` }}
        >
          {shown.map((pick) => (
            <div key={pick.seriesId} className="taste-shelf-pick">
              <ShelfCover pick={pick} />
              <FitBar fit={pick.fit} />
              <Text className="taste-shelf-reason" size="sm">
                <Reason pick={pick} />
              </Text>
              <State pick={pick} />
            </div>
          ))}
        </div>
      )}
    </Panel>
  )
}
