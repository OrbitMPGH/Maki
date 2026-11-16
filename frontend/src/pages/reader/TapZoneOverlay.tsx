import { useLingui } from '@lingui/react/macro'
import { IconArrowDown, IconArrowLeft, IconArrowRight, IconArrowUp } from '../../lib/icons'
import type { ReaderPrefs } from './prefs'
import { tapZoneCells, type TapAction, type TapZoneCell } from './tapZones'

type TapPrefs = Pick<ReaderPrefs, 'tapLayout' | 'tapWidth' | 'tapInvert' | 'direction'>

/** The largest cell of each action carries its name; the rest only show the arrow. */
function namedCells(cells: TapZoneCell[]): Set<string> {
  const best = new Map<TapAction, TapZoneCell>()
  for (const cell of cells) {
    const held = best.get(cell.action)
    if (!held || cell.width * cell.height > held.width * held.height) best.set(cell.action, cell)
  }
  return new Set([...best.values()].map((cell) => cell.key))
}

/**
 * The tap zones drawn over the page itself while their settings are open, so the layout and zone
 * size show where a tap actually lands instead of on a thumbnail.
 */
export default function TapZoneOverlay({ prefs, vertical }: { prefs: TapPrefs; vertical: boolean }) {
  const { t } = useLingui()
  const cells = tapZoneCells(prefs, vertical)
  const named = namedCells(cells)
  const rtl = prefs.direction === 'rtl'
  const Forward = vertical ? IconArrowDown : rtl ? IconArrowLeft : IconArrowRight
  const Back = vertical ? IconArrowUp : rtl ? IconArrowRight : IconArrowLeft
  const names: Record<TapAction, string> = { next: t`Forward`, previous: t`Back`, menu: t`Show the bars` }

  return (
    <div className="reader-tap-overlay" aria-hidden>
      {cells.map((cell) => {
        const Arrow = cell.action === 'next' ? Forward : cell.action === 'previous' ? Back : null
        return (
          <div
            key={cell.key}
            className="reader-tap-zone"
            data-action={cell.action}
            style={{
              left: `${cell.x * 100}%`,
              top: `${cell.y * 100}%`,
              width: `${cell.width * 100}%`,
              height: `${cell.height * 100}%`,
            }}
          >
            {Arrow && <Arrow size={22} />}
            {named.has(cell.key) && <span className="reader-tap-zone-name">{names[cell.action]}</span>}
          </div>
        )
      })}
    </div>
  )
}
