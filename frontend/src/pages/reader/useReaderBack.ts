import { useNavigate } from 'react-router-dom'
import { msg } from '@lingui/core/macro'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import { useBackTarget, type BackTarget } from '../../lib/navHistory'

export interface ReaderBack {
  /** Real href, so a middle click still opens the origin in a tab. */
  to: string
  onClick: BackTarget['onClick']
  /** Accessible name: "Back to series", or "Back to Home" when that is where you came from. */
  label: string
  /** Leaves the reader the way the back arrow does, for keys. */
  go: () => void
}

/**
 * Where the reader's back arrow and Escape lead: the page the reader was opened from, with the
 * series page as the fallback for a direct link, a reload or a new tab. The history stack skips
 * reader entries and chapter changes replace theirs, so reading on through chapters keeps the
 * origin.
 */
export function useReaderBack(seriesId: number | undefined): ReaderBack {
  const { t } = useLingui()
  const label = useLabel()
  const navigate = useNavigate()
  const seriesPath = seriesId != null ? `/series/${seriesId}` : '/library'
  const target = useBackTarget({ to: seriesPath, label: msg`Library` })
  const toSeries = target.to.split('?')[0] === seriesPath
  const name = toSeries ? t`Back to series` : t`Back to ${label(target.label)}`

  const go = () => {
    let popped = false
    target.onClick({
      preventDefault: () => {
        popped = true
      },
    })
    if (!popped) navigate(target.to)
  }

  return { to: target.to, onClick: target.onClick, label: name, go }
}
